"""Exercise the actual ConfigMap sources using only the Python standard library."""
import concurrent.futures
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parents[1]


def source(manifest, filename):
    lines = (ROOT / manifest).read_text().splitlines()
    start = lines.index(f"  {filename}: |") + 1
    code = []
    for line in lines[start:]:
        if line and not line.startswith("    "):
            break
        code.append(line[4:])
    return "\n".join(code) + "\n"


def module(manifest, filename):
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / filename
        path.write_text(source(manifest, filename))
        spec = importlib.util.spec_from_file_location(path.stem, path)
        loaded = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(loaded)
        return loaded


ledger = module("06-core-ledger.yaml", "ledger.py")
report = module("08-month-end-report.yaml", "report.py")
load = module("ledger-load-test.yaml", "load.py")


class LedgerTests(unittest.TestCase):
    def setUp(self):
        self.ledger = ledger.Ledger(1, queue_timeout=0.15)
        self.server = ledger.Server(("127.0.0.1", 0), self.ledger, "test-key")
        self.thread = threading.Thread(target=self.server.serve_forever)
        self.thread.start()
        self.url = f"http://127.0.0.1:{self.server.server_port}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.ledger.stop()

    def request(self, path, body=None, headers=None):
        request = Request(self.url + path, data=json.dumps(body).encode() if body is not None else None,
                          headers=headers if headers is not None else
                          {"X-Client-Id": "customer-portal", "X-Ledger-Key": "test-key"})
        try:
            response = urlopen(request, timeout=5)
        except HTTPError as error:
            response = error
        with response:
            return response.status, response.headers, json.load(response)

    def test_auth_validation_and_outcome(self):
        for headers in ({}, {"X-Client-Id": "portal"}, {"X-Ledger-Key": "test-key"}):
            status, received, _ = self.request("/v1/postings", {}, headers)
            self.assertEqual(status, 401)
            self.assertIn("X-Ledger-Queue-Ms", received)
            self.assertIn("X-Ledger-Batch-Active", received)
        self.assertEqual(self.request("/v1/postings", [1])[0], 400)
        self.assertEqual(self.request("/v1/postings", {"amount": True})[0], 400)
        self.assertEqual(self.request("/v1/statements?period=2026-10")[0], 400)
        self.assertEqual(self.request("/missing")[0], 404)
        with patch.object(ledger.random, "random", return_value=0):
            status, headers, body = self.request("/v1/postings", {
                "amount": 42.50, "currency": "EUR", "channel": "web", "reference": "test"})
        self.assertEqual(status, 200)
        self.assertEqual(body["outcome"], "rejected")
        self.assertTrue(body["postingId"])
        self.assertLess(float(headers["X-Ledger-Queue-Ms"]), 100)

    def test_probe_bypasses_busy_worker_and_timeout_cleans_queue(self):
        with concurrent.futures.ThreadPoolExecutor() as executor:
            statement = executor.submit(self.ledger.submit, "month-end-report", "statements",
                                        {"period": "2026-10", "segment": "1"})
            deadline = time.monotonic() + 1
            while not self.ledger.stats()["busyWorkers"] and time.monotonic() < deadline:
                time.sleep(0.005)
            for path in ("/readyz", "/healthz"):
                started = time.monotonic()
                status, headers, _ = self.request(path, headers={})
                self.assertEqual(status, 200)
                self.assertEqual(headers["X-Ledger-Batch-Active"], "1")
                self.assertLess(time.monotonic() - started, 0.3)
            status, body, waited = self.ledger.submit("portal", "postings", {})
            self.assertEqual((status, body), (503, {"error": "queue_timeout"}))
            self.assertGreaterEqual(waited, 140)
            self.assertEqual(self.ledger.stats()["queueDepth"], 0)
            self.assertEqual(self.ledger.stats()["busyShareByClient"], {"month-end-report": 1.0})
            self.assertEqual(statement.result()[0], 200)
        self.assertEqual(self.ledger.stats()["batchActive"], 0)
        self.assertEqual(self.ledger.stats()["busyWorkers"], 0)

    def test_fifo_dispatch(self):
        self.ledger.stop()
        self.ledger = ledger.Ledger(1, queue_timeout=2)
        self.server.ledger = self.ledger
        order = []
        original_sleep = ledger.time.sleep

        def service(seconds):
            order.append(threading.current_thread().name)
            original_sleep(0.05)

        with patch.object(ledger.time, "sleep", side_effect=service):
            works = []
            # Hold the condition so all arrivals are enqueued before dispatch.
            with self.ledger.condition:
                for sequence in range(6):
                    work = ledger.Work(str(sequence), "postings", {})
                    works.append(work)
                    self.ledger.queue.append(work)
                self.ledger.condition.notify()
            for work in works:
                self.assertTrue(work.done.wait(2))
            self.assertEqual([client for _, _, client in self.ledger.history],
                             [str(index) for index in range(6)])
        self.assertEqual(len(order), 6)

    def test_stats_window_arithmetic(self):
        self.ledger.stop()
        with self.ledger.condition:
            now = time.monotonic()
            self.ledger.history.extend([(now - 50, now - 30, "expired"),
                                        (now - 20, now - 5, "batch"), (now - 5, now, "portal")])
            self.ledger.waits.extend([(now - 30, 9999), (now, 20), (now, 30)])
        stats = self.ledger.stats()
        self.assertEqual(stats["p95QueueMs"], 30)
        self.assertAlmostEqual(stats["busyShareByClient"]["batch"], 2 / 3, places=3)
        self.assertNotIn("expired", stats["busyShareByClient"])

    def test_unsupported_method_also_has_observation_headers(self):
        request = Request(self.url + "/v1/postings", method="PUT")
        with self.assertRaises(HTTPError) as raised:
            urlopen(request, timeout=1)
        with raised.exception as response:
            self.assertEqual(response.status, 501)
            self.assertIn("X-Ledger-Queue-Ms", response.headers)
            self.assertIn("X-Ledger-Batch-Active", response.headers)

    def test_expired_statement_is_removed_from_batch_count(self):
        self.ledger.stop()
        self.ledger = ledger.Ledger(1, queue_timeout=0.01)
        work = ledger.Work("month-end-report", "statements", {})
        work.queued -= 1
        with self.ledger.condition:
            self.ledger.batch_active = 1
            self.ledger.queue.append(work)
            self.ledger.condition.notify()
        self.assertTrue(work.done.wait(1))
        self.assertEqual(work.result, (503, {"error": "queue_timeout"}))
        stats = self.ledger.stats()
        self.assertEqual(stats["batchActive"], 0)
        self.assertGreater(stats["p95QueueMs"], 1000)


class ReportTests(unittest.TestCase):
    def test_metadata_distinguishes_real_schedule_and_manual(self):
        cron = {"spec": {"schedule": "5 10 * * *", "timeZone": "Europe/Brussels"},
                "metadata": {"annotations": {"kubernetes.io/change-cause": "test change"}}}
        job = {"metadata": {"ownerReferences": [{"name": "month-end-report",
                                                 "kind": "CronJob", "controller": True}],
                            "annotations": {"batch.kubernetes.io/cronjob-scheduled-timestamp": "2026-11-05T09:05:00Z"}}}
        with patch.object(report, "kube_get", side_effect=[job, cron]):
            evidence = report.metadata("job-1", "month-end-report")
        self.assertEqual(evidence["trigger"], "schedule")
        self.assertEqual(evidence["schedule"], "5 10 * * *")
        self.assertEqual(evidence["scheduledAt"], "2026-11-05T09:05:00Z")
        job = {"metadata": {"annotations": {"cronjob.kubernetes.io/instantiate": "manual"}}}
        with patch.object(report, "kube_get", side_effect=[job, cron]):
            self.assertEqual(report.metadata("job-2", "month-end-report")["trigger"], "manual")
        with patch.object(report, "kube_get", return_value={"metadata": {}}):
            with self.assertRaises(ValueError):
                report.metadata("unknown", "month-end-report")
        with patch.object(report, "kube_get", side_effect=PermissionError("denied")):
            with self.assertRaises(PermissionError):
                report.metadata("denied", "month-end-report")

    def test_ramp_and_graceful_stop(self):
        instance = report.Report("http://unused", "key", "month-end-report", "2026-10",
                                 max_workers=4, ramp_seconds=0.05, duration=0.6)
        starts = []

        def worker():
            starts.append(time.monotonic())
            instance.stopped.wait()

        with patch.object(instance, "worker", side_effect=worker), patch.object(report, "log"):
            self.assertEqual(instance.run(), 0)
        self.assertEqual(len(starts), 4)
        self.assertTrue(all(not thread.is_alive() for thread in instance.threads))
        self.assertGreater(starts[-1] - starts[0], 0.1)

    def test_auth_failure_stops_report_explicitly(self):
        instance = report.Report("http://unused", "key", "month-end-report", "2026-10")
        with patch.object(report, "urlopen", side_effect=HTTPError("url", 401, "no", {}, None)), \
                patch.object(report, "log"):
            instance.worker()
        self.assertTrue(instance.failed.is_set())
        self.assertEqual(instance.progress()["errors"], 1)


class LoadTests(unittest.TestCase):
    def test_summary_does_not_count_business_rejection_as_failure(self):
        summary = load.summarize([
            {"queueMs": 4000, "latencyMs": 4050, "failed": False, "outcome": "accepted"},
            {"queueMs": 10, "latencyMs": 40, "failed": False, "outcome": "rejected"},
            {"queueMs": None, "latencyMs": 12000, "failed": True, "outcome": None}])
        self.assertEqual(summary["failed"], 1)
        self.assertEqual(summary["rejected"], 1)
        self.assertEqual(summary["queueMeasured"], 2)
        self.assertEqual(summary["queueOver3sFraction"], 1 / 3)
        self.assertEqual(summary["p95QueueMs"], 4000)
        self.assertIsNone(load.summarize([])["p95QueueMs"])

    def test_constant_arrivals_overlap_slow_requests(self):
        instance = load.Load("http://unused", "key", rate=600, duration=0.5)
        arrivals = []

        def posting(sequence, elapsed):
            arrivals.append(elapsed)
            time.sleep(0.3)
            instance.slots.release()

        with patch.object(instance, "posting", side_effect=posting), patch.object(load, "log"):
            self.assertEqual(instance.run(), 0)
        self.assertEqual(instance.dispatched, 5)
        self.assertEqual(instance.dropped, 0)
        for index, elapsed in enumerate(arrivals):
            self.assertAlmostEqual(elapsed, index * 0.1, delta=0.08)

    def test_queue_timeout_is_measured_but_not_aborted(self):
        instance = load.Load("http://unused", "key", duration=1)
        self.assertTrue(instance.slots.acquire())
        response = HTTPError("url", 503, "queue full", {"X-Ledger-Queue-Ms": "8001.5"},
                             io.BytesIO(b'{"error":"queue_timeout"}'))
        with patch.object(load, "urlopen", side_effect=response), patch.object(load, "log"):
            instance.posting(0, 0)
        record = instance.records[0]
        self.assertEqual(record["status"], 503)
        self.assertEqual(record["queueMs"], 8001.5)
        self.assertTrue(record["failed"])
        self.assertFalse(instance.stopped.is_set())


if __name__ == "__main__":
    unittest.main()

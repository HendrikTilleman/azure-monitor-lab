"""Local contention experiment; not a substitute for the AKS acceptance checks."""
import argparse
import json
import multiprocessing
import threading
import time
from urllib.request import urlopen

from test_ledger_workloads import ledger, load, report


def report_process(url, workers, ramp, stopped):
    instance = report.Report(url, "local-test-key", "month-end-report", "2026-10",
                             max_workers=workers, ramp_seconds=ramp, duration=600)
    thread = threading.Thread(target=instance.run)
    thread.start()
    stopped.wait()
    instance.stopped.set()
    thread.join()


def experiment(workers, baseline, contention, recovery, ramp):
    processes = []
    servers = []
    loads = []
    load_threads = []
    stop_report = multiprocessing.Event()
    started = time.monotonic()
    try:
        for _ in range(2):
            pool = ledger.Ledger()
            server = ledger.Server(("127.0.0.1", 0), pool, "local-test-key")
            server_thread = threading.Thread(target=server.serve_forever)
            server_thread.start()
            servers.append((server, server_thread))
            url = f"http://127.0.0.1:{server.server_port}"
            instance = load.Load(url, "local-test-key", rate=45,
                                 duration=baseline + contention + recovery)
            loads.append(instance)
            thread = threading.Thread(target=instance.run)
            load_threads.append(thread)
            thread.start()
        time.sleep(baseline)
        report_started = time.monotonic() - started
        for server, _ in servers:
            url = f"http://127.0.0.1:{server.server_port}"
            for _ in range(2):
                process = multiprocessing.Process(target=report_process,
                                                  args=(url, workers, ramp, stop_report))
                processes.append(process)
                process.start()
        probes_ok = True
        until = time.monotonic() + contention
        last_stats = None
        while time.monotonic() < until:
            for server, _ in servers:
                before = time.monotonic()
                with urlopen(f"http://127.0.0.1:{server.server_port}/readyz", timeout=1) as response:
                    probes_ok = probes_ok and response.status == 200 and time.monotonic() - before < 0.3
            last_stats = [server.ledger.stats() for server, _ in servers]
            time.sleep(2)
        stop_report.set()
        report_stopped = time.monotonic() - started
        for process in processes:
            process.join(timeout=20)
            if process.is_alive() or process.exitcode != 0:
                raise RuntimeError("Report did not terminate cleanly.")
        for thread in load_threads:
            thread.join()
        records = [record for instance in loads for record in instance.records]
        # Per-loader start offsets are less than one second.
        windows = {
            "baseline": [r for r in records if r["dispatchElapsed"] < baseline - 1],
            "incidentLast30s": [r for r in records
                               if report_started + contention - 30 <= r["dispatchElapsed"]
                               < report_started + contention],
            "recoveredLast30s": [r for r in records if r["dispatchElapsed"] >= report_stopped + recovery - 31],
        }
        result = {"workersPerPod": workers, "rampSeconds": ramp, "reportStarted": report_started,
                  "reportStopped": report_stopped, "probesResponsive": probes_ok,
                  "reportStats": last_stats,
                  "arrivalsDropped": sum(instance.dropped for instance in loads),
                  "windows": {name: load.summarize(values) for name, values in windows.items()}}
        print("BENCHMARK " + json.dumps(result), flush=True)
        return result
    finally:
        stop_report.set()
        for instance in loads:
            instance.stopped.set()
        for process in processes:
            process.join(timeout=20)
            if process.is_alive():
                process.terminate()
                process.join()
        for thread in load_threads:
            thread.join()
        for server, thread in servers:
            server.shutdown()
            server.server_close()
            thread.join()
            server.ledger.stop()


if __name__ == "__main__":
    multiprocessing.set_start_method("spawn")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--baseline", type=float, default=30)
    parser.add_argument("--contention", type=float, default=240)
    parser.add_argument("--recovery", type=float, default=60)
    parser.add_argument("--ramp", type=float, default=45)
    args = parser.parse_args()
    experiment(args.workers, args.baseline, args.contention, args.recovery, args.ramp)

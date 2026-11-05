# SRE Mission Control

The scenario is specified in [DESIGN.md](../DESIGN.md). **Implemented so far:
work items 1-3: the ledger, report, portal API, business pulse, synthetic
customers and orders SLO metric alert.** Mission operations, agent configuration
and the screen are later work items. The portal is disabled by default; the
opt-in manifests do not change existing lab deployment, traffic or Lab Operations
runner behavior.

## Workload contracts

All three programs are embedded in ConfigMaps, use only Python's standard
library and run on public `python:3.11-slim`; there is no image build.

- `06-core-ledger.yaml`: two pods, each with four FIFO workers. Postings take
  25-40 ms; statement extracts hold a worker for 2-3 seconds, mostly sleeping.
  Queued work times out after eight seconds with HTTP 503 `queue_timeout`.
  About 1% of successful postings have the normal business outcome `rejected`;
  this is not an HTTP failure. There are no artificial HTTP failures.
- Business requests require `X-Ledger-Key` and `X-Client-Id`. `/healthz` and
  `/readyz` deliberately allow unauthenticated Kubernetes probes and bypass the
  business pool. Every API response has queue-wait and batch-active headers.
  JSON request logs and 15-second stats expose actual contention, including
  each client's fraction of occupied worker time in the trailing 15 seconds.
  Empty-window percentiles are `null`, not invented zeros.
- `08-month-end-report.yaml`: a nightly `Europe/Brussels` CronJob at `0 2 * * *`.
  Its Job has **both parallelism and completions set to four**, ensuring four
  concurrent pods. Each pod starts one worker and adds one every 45 seconds,
  performs statement reads followed by about one second of real CPU aggregation,
  and normally exits after 25 minutes. SIGTERM stops further reads and drains
  in-flight calls. An HTTP/auth or metadata failure is logged, not disguised as
  successful evidence.
- The report's namespace-scoped service account can only `get` batch Jobs and
  CronJobs in `demo`. The pod obtains its Job name from its label, then reads
  the Job and CronJob through the authenticated Kubernetes API. A manual Job's
  `cronjob.kubernetes.io/instantiate` annotation identifies manual invocation;
  CronJob controller ownership identifies scheduled invocation (including on
  clusters predating the scheduled-timestamp annotation). The startup JSON
  records the actual observed schedule, time zone, trigger and optional
  scheduled timestamp. No write permission or invented fallback is used.
- `09-month-end-report-business-hours.yaml` is **a strategic-merge patch, not
  an apply-able resource**. It changes only schedule and change-cause.
- `ledger-load-test.yaml`: a temporary Job, default 30 minutes at 90 postings
  per minute. Constant arrivals overlap up to 32 requests; slow responses do
  not reduce the offered rate. It keeps running through HTTP/network failures,
  reports each real outcome and 30-second windows, and emits an end summary.
  A local scheduler-capacity drop is explicitly logged and invalidates the run.
  The 12-second transport timeout allows the eight-second ledger queue timeout
  to be observed; it is deliberately not the future portal's three-second timeout.
  `queueOver3sFraction` measures actual queue waits, including HTTP 503 responses;
  unmeasured waits never count as exceeding three seconds.

### Measured tuning versus the design

The nominal four report workers per pod (16 clients) did not meet the
three-second contention target in the local experiment. The default is therefore
**six per pod (24 clients)**, retaining the 45-second ramp and all ledger timings
and limits. This is the one intentional load-parameter deviation from sections
9.2 and Appendix B. It applies equally to nightly, manual and business-hours
runs; injection still changes only the schedule and annotation.

Local experiments exercise the embedded code in the specified Python image,
with two ledger instances and four separate report processes. Posting arrivals
are evenly divided between the two instances, unlike AKS Service connection
distribution. They do not prove AKS readiness, restarts, scheduling, network
policy or CPU alert behavior. The owner checks below remain necessary.

| Local run | Baseline p95 latency | Postings queued >3 s (final 30 s) | Recovery p95 queue wait |
|---|---:|---:|---:|
| 4 workers/pod, accelerated 5 s ramp | 41.8 ms | 4.5% | 1.22 ms |
| 6 workers/pod, normal 45 s ramp, four-minute incident | 43.6 ms | 81.8% | 0.25 ms |

Both experiments had zero dropped posting arrivals and responsive probes.
These are local measurements, not guaranteed rates or AKS verification.

## Owner setup (AKS)

Run only against the deployed lab's AKS context, namespace `demo`. These commands
make Kubernetes changes; they are not run automatically by CI. Use Kubernetes
1.27 or newer for stable CronJob `timeZone` support. If the deployed cluster
cannot support it, use a supported cluster before proceeding; do not silently
drop the time zone.

Set the existing lab's resource names; do not substitute another subscription:

```bash
export RESOURCE_GROUP=rg-azure-monitor-lab
export WEBAPP_NAME='<existing lab web app>'
export MISSION_TMP="$(mktemp -d)"
kubectl config current-context
kubectl get namespace demo
export WEBAPP_OUTBOUND_IPS="$(az webapp show -g "$RESOURCE_GROUP" -n "$WEBAPP_NAME" \
  --query outboundIpAddresses -o tsv)"
export LEDGER_DNS_LABEL="contoso-ledger-$(python3 -c 'import secrets; print(secrets.token_hex(4))')"
export LEDGER_KEY="$(python3 -c 'import secrets; print(secrets.token_urlsafe(32))')"
kubectl create secret generic mission-ledger -n demo \
  --from-literal=key="$LEDGER_KEY" --dry-run=client -o yaml | kubectl apply -f -
unset LEDGER_KEY
python3 - <<'PY'
import ipaddress, os, pathlib, re
label = os.environ["LEDGER_DNS_LABEL"]
assert re.fullmatch(r"[a-z][a-z0-9-]{0,61}[a-z0-9]", label), "Invalid DNS label"
addresses = [ipaddress.ip_address(s.strip()) for s in os.environ["WEBAPP_OUTBOUND_IPS"].split(",")]
assert addresses, "No web app outbound IPs"
text = pathlib.Path("workloads/k8s/06-core-ledger.yaml").read_text()
text = text.replace("__LEDGER_DNS_LABEL__", label)
text = text.replace('    - "__WEBAPP_OUTBOUND_CIDR__"',
                    "\n".join(f'    - "{ip}/{ip.max_prefixlen}"' for ip in addresses))
pathlib.Path(os.environ["MISSION_TMP"], "ledger.yaml").write_text(text)
PY
kubectl apply --dry-run=server -f "$MISSION_TMP/ledger.yaml"
kubectl apply -f "$MISSION_TMP/ledger.yaml"
kubectl rollout status deployment/core-ledger -n demo --timeout=180s
kubectl apply -f workloads/k8s/08-month-end-report.yaml
kubectl get service core-ledger-public -n demo -o wide
```

The public Service never has an unrestricted fallback: its unrendered CIDR is
invalid. Include every outbound IP as its own `/32` (or `/128`), never
`0.0.0.0/0`. The helper and report use the ClusterIP, not the public endpoint.
HTTP with a shared key is the specified lab-only network model; production
would require private networking/TLS. Later Setup will manage rendering and keys.

## Owner to verify: work item 1 "Done when"

Keep the load test running throughout the incident/recovery checks. In a POSIX
shell with Python 3, run the following from the repository root.

### Thirty-minute baseline: p95 below 300 ms, zero restarts

Ensure no active report Job is running; inspect and suspend any rehearsal Job
first. Do not baseline across the nightly 02:00 schedule.

```bash
kubectl get jobs,pods -n demo
kubectl apply -f workloads/k8s/ledger-load-test.yaml
kubectl wait --for=condition=complete job/ledger-load-test -n demo --timeout=32m
kubectl logs job/ledger-load-test -n demo > "$MISSION_TMP/baseline.jsonl"
python3 - <<'PY'
import json, os, pathlib
rows = [json.loads(s) for s in pathlib.Path(os.environ["MISSION_TMP"], "baseline.jsonl").read_text().splitlines()]
end = next(r for r in rows if r["event"] == "end")
assert end["completed"] == 2700 and end["dropped"] == 0, end
assert end["failed"] == 0 and end["p95LatencyMs"] < 300, end
print(end)
PY
kubectl get pods -n demo -l app=core-ledger \
  -o custom-columns=NAME:.metadata.name,READY:.status.containerStatuses[*].ready,RESTARTS:.status.containerStatuses[*].restartCount
```

Both ledger pods must show Ready `true`, restarts `0`. Check this after each
incident too; do not interpret a replacement pod's fresh counter as proof of
zero restarts (also compare pod names/UIDs).

### Manual report: at least half the postings wait over 3 s within 4 minutes

The helper's TTL is ten minutes, so read its logs promptly. If it has already
been collected, skip the delete of that completed helper Job.

```bash
kubectl delete job ledger-load-test -n demo --ignore-not-found
kubectl apply -f workloads/k8s/ledger-load-test.yaml
export MANUAL_JOB="month-end-report-manual-$(date +%s)"
kubectl create job "$MANUAL_JOB" -n demo --from=cronjob/month-end-report
kubectl wait --for=jsonpath='{.status.active}'=4 job/"$MANUAL_JOB" -n demo --timeout=90s
sleep 240
kubectl logs -n demo -l batch.kubernetes.io/job-name="$MANUAL_JOB" \
  --max-log-requests=4 --tail=-1 > "$MISSION_TMP/manual-report.jsonl"
sleep 12
kubectl logs job/ledger-load-test -n demo > "$MISSION_TMP/incident.jsonl"
python3 - <<'PY'
import datetime as dt, json, os, pathlib
root = pathlib.Path(os.environ["MISSION_TMP"])
reports = [json.loads(s) for s in (root / "manual-report.jsonl").read_text().splitlines()]
starts = [r for r in reports if r["event"] == "start"]
assert len(starts) == 4 and all(r["trigger"] == "manual" for r in starts), starts
start = min(dt.datetime.fromisoformat(r["time"]) for r in starts)
rows = [json.loads(s) for s in (root / "incident.jsonl").read_text().splitlines()]
# time is completion time; subtract measured latency to recover arrival time.
samples = [r for r in rows if r["event"] == "posting"
           and 210 <= (dt.datetime.fromisoformat(r["time"]) - start).total_seconds() - r["latencyMs"]/1000 < 240]
assert len(samples) >= 35, len(samples)
fraction = sum(r["queueMs"] is not None and r["queueMs"] > 3000 for r in samples) / len(samples)
assert fraction >= 0.5, fraction
assert not any(r["event"] == "arrival_dropped" for r in rows)
print({"samples": len(samples), "queueOver3sFraction": fraction})
PY
kubectl get pods -n demo -l app=core-ledger -o wide
kubectl get pods -n demo -l app=core-ledger \
  -o custom-columns=NAME:.metadata.name,READY:.status.containerStatuses[*].ready,RESTARTS:.status.containerStatuses[*].restartCount
kubectl logs -n demo -l app=core-ledger --tail=-1 --since=5m \
  | grep '"event": "stats"'
```

The stats must show busy workers, queue depth and measured
`busyShareByClient` containing `month-end-report` and `customer-portal`.
Record the last-30-second fraction at four minutes, not an average of the
initial low-load ramp.

### Suspend the CronJob AND Job: wait below 100 ms within one minute

```bash
export PAUSED_AT="$(python3 -c 'from datetime import datetime, timezone; print(datetime.now(timezone.utc).isoformat())')"
kubectl patch cronjob month-end-report -n demo --type merge -p '{"spec":{"suspend":true}}'
kubectl patch job "$MANUAL_JOB" -n demo --type merge -p '{"spec":{"suspend":true}}'
sleep 60
kubectl logs job/ledger-load-test -n demo > "$MISSION_TMP/recovery.jsonl"
python3 - <<'PY'
import datetime as dt, json, os, pathlib
paused = dt.datetime.fromisoformat(os.environ["PAUSED_AT"])
rows = [json.loads(s) for s in pathlib.Path(os.environ["MISSION_TMP"], "recovery.jsonl").read_text().splitlines()]
samples = [r for r in rows if r["event"] == "posting"
           and 30 <= (dt.datetime.fromisoformat(r["time"]) - paused).total_seconds() < 60]
assert len(samples) >= 35, len(samples)
assert all(r["queueMs"] is not None and r["queueMs"] < 100 and not r["failed"] for r in samples), samples
print({"samples": len(samples), "maxQueueMs": max(r["queueMs"] for r in samples)})
PY
```

Suspending the CronJob alone does **not** stop active Jobs. Suspending a Job
terminates its pods; the ledger finishes/drains their existing work normally.

### Patch 09: a scheduled Job at the intended minute, `trigger: schedule`

Restore the CronJob first, leaving the old manual Job suspended:

```bash
kubectl apply -f workloads/k8s/08-month-end-report.yaml
export REPORT_TIME_ZONE="$(kubectl get cronjob month-end-report -n demo -o jsonpath='{.spec.timeZone}')"
python3 - <<'PY'
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo
import json, os, pathlib
now = datetime.now(timezone.utc)
target = now.replace(second=0, microsecond=0) + timedelta(minutes=1)
if (target - now).total_seconds() < 20:
    target += timedelta(minutes=1)
local = target.astimezone(ZoneInfo(os.environ["REPORT_TIME_ZONE"]))
text = pathlib.Path("workloads/k8s/09-month-end-report-business-hours.yaml").read_text()
pathlib.Path(os.environ["MISSION_TMP"], "business-hours.yaml").write_text(
    text.replace("{{SCHEDULE}}", f"{local.minute} {local.hour} * * *"))
pathlib.Path(os.environ["MISSION_TMP"], "scheduled-target.json").write_text(
    json.dumps({"targetUtc": target.isoformat(), "targetLocal": local.isoformat()}))
print("Intended minute:", target.isoformat(), local.isoformat())
PY
kubectl patch cronjob month-end-report -n demo --type strategic \
  --patch-file "$MISSION_TMP/business-hours.yaml"
sleep 100
kubectl get jobs -n demo -o json > "$MISSION_TMP/jobs.json"
export SCHEDULED_JOB="$(python3 - <<'PY'
import datetime as dt, json, os, pathlib
root = pathlib.Path(os.environ["MISSION_TMP"])
target = dt.datetime.fromisoformat(json.loads((root / "scheduled-target.json").read_text())["targetUtc"])
jobs = [j for j in json.loads((root / "jobs.json").read_text())["items"]
        if any(o["kind"] == "CronJob" and o["name"] == "month-end-report"
               for o in j["metadata"].get("ownerReferences", []))
        and dt.datetime.fromisoformat(j["metadata"]["creationTimestamp"]) >= target]
assert len(jobs) == 1, [j["metadata"]["name"] for j in jobs]
job = jobs[0]
created = dt.datetime.fromisoformat(job["metadata"]["creationTimestamp"])
assert 0 <= (created - target).total_seconds() < 60, job["metadata"]
print(job["metadata"]["name"])
PY
)"
test -n "$SCHEDULED_JOB"
kubectl logs -n demo -l batch.kubernetes.io/job-name="$SCHEDULED_JOB" \
  --max-log-requests=4 --tail=-1 > "$MISSION_TMP/scheduled-report.jsonl"
python3 - <<'PY'
import json, os, pathlib
rows = [json.loads(s) for s in pathlib.Path(os.environ["MISSION_TMP"], "scheduled-report.jsonl").read_text().splitlines()]
starts = [r for r in rows if r["event"] == "start"]
assert len(starts) == 4 and all(r["trigger"] == "schedule" for r in starts), starts
print(starts)
PY
kubectl patch cronjob month-end-report -n demo --type merge -p '{"spec":{"suspend":true}}'
kubectl patch job "$SCHEDULED_JOB" -n demo --type merge -p '{"spec":{"suspend":true}}'
kubectl apply -f workloads/k8s/08-month-end-report.yaml
kubectl get cronjob month-end-report -n demo \
  -o jsonpath='{.spec.schedule}{"\n"}{.spec.timeZone}{"\n"}{.spec.suspend}{"\n"}{.metadata.annotations.kubernetes\.io/change-cause}{"\n"}'
```

The final values must be `0 2 * * *`, `Europe/Brussels`, `false` and
`Baseline: nightly at 02:00`. Suspended rehearsal Jobs remain suspended; applying
08 does not resume or delete them. Remove only those named rehearsal Jobs when
finished, and stop the helper with `kubectl delete job ledger-load-test -n demo`.
Never delete Azure resources as part of this verification.

## Local checks

```bash
docker run --rm -v "$PWD/workloads/k8s:/workloads:ro" python:3.11-slim \
  python -B -m unittest discover -s /workloads/tests -v
docker run --rm -v "$PWD/workloads/k8s:/workloads:ro" python:3.11-slim \
  python -B /workloads/tests/benchmark_ledger.py --workers 6
```

The benchmark uses two local HTTP servers, four independent report processes
and 90 total postings/minute, and prints `BENCHMARK` with baseline, final
30-second incident and recovery summaries. Its default baseline is 30 seconds,
not the owner's required 30 minutes; use `--baseline 1800` for that duration.
`--workers 4 --ramp 5` can reproduce the accelerated nominal-concurrency
comparison. Do not label local measurements as AKS results.

## Portal API (work item 2)

The existing Control Center hosts two customer-key-protected endpoints outside
the operator allowlist. They need no operator session, same-origin header or
sign-in cookie. Existing protected operations remain protected.

| Request | Behavior |
|---|---|
| `GET /api/portal/catalog` | Six in-memory Contoso products, EUR prices; records a browse, never calls the ledger. |
| `POST /api/portal/orders` | Optional JSON `sku` and `quantity`; defaults to `CT-001`, quantity 1. Quantity must be 1-100. Missing/empty JSON body uses defaults. Returns order ID, confirmed/rejected outcome, amount, currency and measured ledger queue time. |

Both require one `X-Lab-Customer-Key` matching configuration (otherwise 401),
share a dedicated 1,200 requests/minute fixed-window limiter with no queue
(429 with `Retry-After`), and accept channel `web` (default), `mobile` or
`partner` through `X-Amlab-Channel`. Invalid JSON/SKU/quantity/channel returns
400 and is not recorded as an order attempt. Other media types return 415.
Bodies are limited to 4 KiB. Disabled mode returns 404; an authenticated order
with missing/invalid ledger configuration returns 503, not a fictional ledger
result. Catalog browsing still works when the ledger is unavailable.

The ledger client uses only the configured HTTP(S) origin (no userinfo, path,
query or fragment), sends `X-Client-Id: customer-portal` and the ledger key,
and disables redirects. A single **three-second total budget** covers connect,
body reads, and at most one connection-error retry after 150 ms. Timeouts, HTTP
errors and invalid responses never retry. Response bodies are limited to 16 KiB.
Failure responses contain `error: order_failed` and a `ledger_` reason:
`ledger_timeout` or `ledger_queue_timeout` (504), connection refused/reset/error,
HTTP status, transport error or invalid response (502). Caller cancellation is
recorded separately as `ledger_request_cancelled`, not a ledger timeout.
Normal business rejections are HTTP 200 and count as successful attempts.

### Pulse and telemetry

`BusinessPulse` is a thread-safe singleton, retaining one-second buckets in a
fixed 30-minute ring. It records completed attempts, confirmed/rejected/failed
outcomes, failure reasons, timings, measured queue waits, confirmed-only revenue,
browses and last-order time. `Snapshot()` defaults to the trailing 60 one-second
buckets; `Snapshot(TimeSpan.FromMinutes(5))` provides the acceptance window.
Both rates use `(confirmed + rejected) / attempts`; empty percentages and
percentiles are `null`. Percentiles are nearest-rank over actual samples.
The ten-second series has measured buckets only, never interpolated points.
This pulse assumes **one App Service instance**, is reset by a process restart,
and is not yet a public endpoint; protected Mission snapshots arrive in work
item 6.

The ledger client observes `X-Ledger-Batch-Active` on every received response,
including non-2xx responses and headers received before a body timeout.
`BeginSession(startedAt)` resets batch marks for the later coordinator; older
in-flight requests cannot seed the new session. Positive counts establish the
first batch observation. After that, a measured zero starts a candidate recovery
window; a subsequent zero at least 30 seconds later confirms its start time.
Positive/unknown headers or a gap over five seconds reset the candidate, so
silence and missing values do not prove recovery. Snapshot reads alone never
advance a recovery mark.

The existing Application Insights SDK records requests. The client adds a
correlated `core-ledger` dependency per attempt. Each completed valid order emits
`PortalOrder` with outcome, failure reason, channel and amount bucket, plus real
duration/queue measurements. It also includes `pulseSuccessRate5m` and
`pulseAttempts5m` for the owner comparison before the snapshot API exists.
`portal.order.failed` records exactly one raw 0/1 metric sample per order;
its average is the failure ratio. `portal.order.duration` records milliseconds.
Only portal requests, `core-ledger` dependencies and `PortalOrder` events bypass
SDK sampling, to preserve this measured evidence; unrelated telemetry keeps its
existing policy. No customer/ledger keys or order payloads are added to telemetry.

### Owner setup and verification for work item 2

Redeploy the web app with this code first. Use the existing lab resource names
and AKS context; the following changes are owner-run, not performed by CI.
Keep the public ledger source ranges restricted as in work item 1.

```bash
export RESOURCE_GROUP=rg-azure-monitor-lab
export WEBAPP_NAME='<existing lab web app>'
export MISSION_TMP="$(mktemp -d)"
export PORTAL_URL="https://$(az webapp show -g "$RESOURCE_GROUP" -n "$WEBAPP_NAME" \
  --query defaultHostName -o tsv)"
export LEDGER_URL="http://$(kubectl get service core-ledger-public -n demo \
  -o jsonpath='{.status.loadBalancer.ingress[0].ip}')"
export CUSTOMER_KEY="$(python3 -c 'import secrets; print(secrets.token_urlsafe(32))')"
export LEDGER_KEY="$(kubectl get secret mission-ledger -n demo -o jsonpath='{.data.key}' | base64 --decode)"
az webapp config appsettings set -g "$RESOURCE_GROUP" -n "$WEBAPP_NAME" --settings \
  LabConsole__Mission__Enabled=true \
  LabConsole__Mission__LedgerBaseUrl="$LEDGER_URL" \
  LabConsole__Mission__LedgerKey="$LEDGER_KEY" \
  LabConsole__Mission__CustomerKey="$CUSTOMER_KEY" --output none
unset LEDGER_KEY
kubectl create secret generic mission-portal -n demo \
  --from-literal=customer-key="$CUSTOMER_KEY" --dry-run=client -o yaml | kubectl apply -f -
```

**App Service Easy Auth executes before Program.cs.** If it requires sign-in
globally, exclude **only** `/api/portal/*`; do not turn off authentication or
exclude the operator APIs. The following preserves all other auth settings:

```bash
export WEBAPP_ID="$(az webapp show -g "$RESOURCE_GROUP" -n "$WEBAPP_NAME" --query id -o tsv)"
az rest --method get \
  --url "https://management.azure.com$WEBAPP_ID/config/authsettingsV2?api-version=2024-11-01" \
  > "$MISSION_TMP/auth-before.json"
python3 - <<'PY'
import json, os, pathlib
root = pathlib.Path(os.environ["MISSION_TMP"])
auth = json.loads((root / "auth-before.json").read_text())["properties"]
validation = auth.setdefault("globalValidation", {})
paths = validation.setdefault("excludedPaths", [])
if "/api/portal/*" not in paths:
    paths.append("/api/portal/*")
(root / "auth-portal.json").write_text(json.dumps({"properties": auth}))
PY
az rest --method put \
  --url "https://management.azure.com$WEBAPP_ID/config/authsettingsV2?api-version=2024-11-01" \
  --body @"$MISSION_TMP/auth-portal.json" --output none
test "$(curl -s -o /dev/null -w '%{http_code}' "$PORTAL_URL/api/portal/catalog")" = 401
test "$(curl -s -o /dev/null -w '%{http_code}' -X POST \
  -H 'Content-Type: application/json' -d '{}' "$PORTAL_URL/api/portal/orders")" = 401
curl --fail-with-body -s -H "X-Lab-Customer-Key: $CUSTOMER_KEY" "$PORTAL_URL/api/portal/catalog"
curl --fail-with-body -s -H "X-Lab-Customer-Key: $CUSTOMER_KEY" \
  -H 'Content-Type: application/json' -d '{}' "$PORTAL_URL/api/portal/orders"
```

Manifest 07 uses pinned public `grafana/k6:1.4.0`. It runs constant-arrival
scenarios (90 orders/minute and 240 browses/minute) for 100 minutes, choosing
random SKU/quantity and a 60/30/10 web/mobile/partner mix. Check failures never
abort the workload; there are no thresholds. It never follows sign-in redirects.
Its Job deadline is 110 minutes and completion TTL is ten minutes. Render a
unique Job name and public portal origin; rates/duration are environment values
that later mission operations will fill. The customer key is Secret-backed,
not interpolated into the manifest.

```bash
export CUSTOMERS_JOB="mission-customers-$(date +%s)"
python3 - <<'PY'
import os, pathlib, re
from urllib.parse import urlsplit
name, url = os.environ["CUSTOMERS_JOB"], os.environ["PORTAL_URL"].rstrip("/")
assert re.fullmatch(r"[a-z0-9][a-z0-9-]{0,61}[a-z0-9]", name)
parsed = urlsplit(url)
assert parsed.scheme == "https" and parsed.hostname and not parsed.username
assert not parsed.query and not parsed.fragment and parsed.path in ("", "/")
text = pathlib.Path("workloads/k8s/07-mission-customers.yaml").read_text()
text = text.replace("__MISSION_CUSTOMERS_JOB__", name).replace("__PORTAL_URL__", url)
pathlib.Path(os.environ["MISSION_TMP"], "customers.yaml").write_text(text)
PY
kubectl apply --dry-run=server -f "$MISSION_TMP/customers.yaml"
kubectl apply -f "$MISSION_TMP/customers.yaml"
kubectl get pods -n demo -l job-name="$CUSTOMERS_JOB"
sleep 480
```

In the existing lab Application Insights **Logs** (not the general Log Analytics
workspace), run this query after ingestion catches up. Using an event's own
timestamp as the window end avoids clock-window mismatch; the two-minute
ingestion buffer leaves a complete five-minute window after the warm-up:

```kusto
let latest = customEvents
| where timestamp between (ago(30m) .. ago(2m)) and name == "PortalOrder"
| top 1 by timestamp desc
| project End=timestamp, Role=cloud_RoleInstance,
          PulseRate=todouble(customMeasurements.pulseSuccessRate5m),
          PulseAttempts=tolong(customMeasurements.pulseAttempts5m);
let endTime = toscalar(latest | project End);
let instance = toscalar(latest | project Role);
// The pulse uses whole one-second buckets, inclusive of the current bucket.
let startTime = bin(endTime, 1s) - 299s;
customEvents
| where name == "PortalOrder" and cloud_RoleInstance == instance
| where timestamp between (startTime .. endTime)
| summarize Attempts=sum(itemCount),
            Successful=sumif(itemCount, tostring(customDimensions.outcome) in ("confirmed", "rejected"))
| extend InsightsSuccessRate=todouble(Successful)/Attempts,
         PulseSuccessRate=toscalar(latest | project PulseRate),
         PulseAttempts=toscalar(latest | project PulseAttempts)
| extend DifferencePercentagePoints=100*abs(InsightsSuccessRate-PulseSuccessRate)
```

Require at least five minutes of traffic, nonzero attempts, and difference at
most **2 percentage points** (also compare attempt counts). Use one App Service
instance, rerun after ingestion settles, and investigate missing telemetry
rather than manufacturing agreement. Pulse timestamps use completion buckets;
events use completion timestamps, not request start timestamps. Minor ordering
differences between concurrent completions may cause small boundary differences.

Verify every required telemetry surface in the same Application Insights Logs:

```kusto
requests
| where timestamp > ago(15m) and url contains "/api/portal/orders"
| summarize Orders=count(), Failed=countif(success == false), P95Ms=percentile(duration, 95)
```

```kusto
dependencies
| where timestamp > ago(15m) and name == "core-ledger"
| project timestamp, operation_Id, success, resultCode, duration, target
```

```kusto
customEvents
| where timestamp > ago(15m) and name == "PortalOrder"
| project timestamp, customDimensions, customMeasurements
```

```kusto
customMetrics
| where timestamp > ago(15m) and name == "portal.order.failed"
| summarize FailureRate=sum(valueSum)/sum(valueCount), Samples=sum(valueCount) by bin(timestamp, 1m)
```

During a manual report run (work item 1), order requests must produce genuine
504s with `ledger_timeout` and failed dependencies, while catalog remains 200.
Suspend both the CronJob and its active Job afterward using the work-item-1
commands. Verify recovery through the same sources. Stop only the named
customers Job when finished:

```bash
kubectl delete job "$CUSTOMERS_JOB" -n demo
unset CUSTOMER_KEY
```

No Azure resources are deleted. The opt-in app settings and Easy Auth exclusion
remain configured; disable the portal with `LabConsole__Mission__Enabled=false`
if it is not being used. Full Baseline/End orchestration and the protected
Mission snapshot endpoint remain later work items.

### Local portal checks

```bash
dotnet test workloads/webapp.Tests/AmlabHello.Tests.csproj
node --test workloads/k8s/tests/mission-customers.test.cjs
```

In addition to deterministic handler/clock tests, a local Kestrel smoke run
against the actual embedded Python ledger verified a real k6 baseline,
unauthenticated 401s, a 413 for a body over 4 KiB, and report-driven contention.
All eight concurrent incident orders timed out at 3.01-3.03 seconds while the
catalog completed in 2.3 ms; a post-report order measured 0.18 ms queue wait.
The report ramp was accelerated for this smoke check. These are local results,
not the five-minute Azure telemetry comparison or an AKS acceptance claim.

## Orders SLO alert (work item 3)

`infra/modules/mission-alerts.bicep` is an opt-in resource-group module targeting
the **existing lab Application Insights component** and notifying the **existing
lab action group**. It does not modify the main Bicep/Terraform deployment.
Automatic deployment by `mission-control.ps1 -Action Setup` belongs to work
item 4; until then the owner can deploy it explicitly as shown below, or use
the manually triggered GitHub Actions workflow.

### Deploy from GitHub Actions (service principal)

`.github/workflows/deploy-mission-alerts.yml` runs on a GitHub-hosted runner,
not the interactive cloud terminal. It deploys **only the alert module** in
incremental mode; it does not deploy the web app, Kubernetes workloads, or the
rest of the lab. It never runs on push or pull request. Only `master` is allowed.
The first run defaults to **preview only**.

An owner with Azure role-assignment permission and GitHub repository
administration access must configure the following once. Credentials stay in
GitHub; they are not read by or copied to the interactive cloud terminal.

1. In GitHub, open **Settings > Environments > New environment**, name it
   `azure-lab`, restrict deployment branches to `master`, and add a required
   reviewer where your plan supports it. Configure protection before running
   the workflow; do not rely on an automatically created, unprotected environment.
2. Use a dedicated **Microsoft Entra app registration / service principal**.
   Store its client secret **value** (not the secret's ID or credential JSON)
   in the repository Actions secret **`azure_lab`**. Do not paste it in chat,
   commit it, or put it in a variable. Rotate it before expiry.
3. On the **lab resource group's IAM**, grant that service principal **Monitoring
   Contributor** scoped to that resource group only. This built-in role grants
   monitoring-resource management and ARM deployment operations; do not grant
   subscription-wide Contributor or Owner. A stricter custom role can further
   restrict writes to metric alerts and resource-group deployments if required.
   If the identity already has subscription-wide Contributor, it can deploy,
   but an owner should narrow that assignment after granting the lab-scoped role.
4. In **Settings > Secrets and variables > Actions > Variables**, add the
   three required repository variables below. Environment variables in
   `azure-lab` may also be used and take precedence over repository variables.

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | App registration's application/client ID, not its object/principal ID |
| `AZURE_TENANT_ID` | Lab directory/tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Existing lab subscription ID |
| `AZURE_RESOURCE_GROUP` | Optional; defaults to `rg-azure-monitor-lab` |
| `APPINSIGHTS_NAME` | Optional; defaults to `appi-amlab` |
| `ACTION_GROUP_NAME` | Optional; defaults to `ag-amlab-email` |

The defaults match `lab.config.json.example` and `infra/main.bicep` with
`namePrefix=amlab`. Override them if the deployed lab uses other names. The
workflow constructs both resource IDs within the configured subscription and
resource group and fails if either does not exist; it does not search for or
select another lab. The secret is supplied only to the login step, never
printed, and Azure's cached credentials are cleared at the end of the job.

After merging the workflow into `master`, go to **Actions > Deploy mission
alerts > Run workflow**, select `master`, and leave **Apply the alert** unchecked.
Approve the environment request, if configured, and inspect the validation and
what-if output. Expect changes only to `alert-orders-slo-burn`. Then run again
with **Apply the alert** checked to deploy and read back its configuration.
Runs are serialized; a newer request does not cancel an in-progress deployment.

A green deployment proves the ARM settings were applied, **not** that telemetry
arrived, a notification was delivered, or the alert fired/resolved on time.
Complete the owner checks below. If login fails, check the client/tenant IDs
and secret value/expiry; if authorization fails, check the resource-group role
assignment and allow for propagation. Passwordless GitHub OIDC is a future
hardening option; the current workflow explicitly uses the owner-provided
service-principal secret.

The enabled metric rule `alert-orders-slo-burn` has severity 1, evaluates every
minute and fires when the five-minute **Average** of `portal.order.failed` is
**strictly greater than 0.05**. Auto-mitigation is enabled. Both its name and
description contain `slo-burn`, independent of the screen's configurable nouns.
The custom metric namespace is `azure.applicationinsights`; metric validation
is skipped because the metric does not exist until valid portal orders run.
This skips deployment-time validation, **not** the need to verify actual metric
ingestion and namespace in Azure.

The portal emits a raw 0/1 sample for every completed valid order. A business
rejection contributes 0, not 1. Average, rather than Total, is required to
measure the failure ratio. No dimensions split the rule into channel-specific
alerts. Missing traffic is not healthy evidence and is not an SLO measurement.

### Owner to verify: deploy, fire and recover

Run against the existing lab resource group, with work item 2's portal/customer
configuration and traffic already running. Do not select an unrelated component
or action group. The module requires their resource IDs, not instrumentation
keys. These commands are **not executed by CI**.

```bash
export RESOURCE_GROUP=rg-azure-monitor-lab
export APPINSIGHTS_NAME='<existing lab Application Insights resource name>'
export ACTION_GROUP_NAME='<existing lab action group name>'
export APPINSIGHTS_ID="$(az monitor app-insights component show \
  -g "$RESOURCE_GROUP" --app "$APPINSIGHTS_NAME" --query id -o tsv)"
export ACTION_GROUP_ID="$(az monitor action-group show \
  -g "$RESOURCE_GROUP" -n "$ACTION_GROUP_NAME" --query id -o tsv)"
az deployment group validate -g "$RESOURCE_GROUP" \
  --template-file infra/modules/mission-alerts.bicep \
  --parameters appInsightsId="$APPINSIGHTS_ID" actionGroupId="$ACTION_GROUP_ID"
az deployment group create -g "$RESOURCE_GROUP" -n mission-alerts \
  --template-file infra/modules/mission-alerts.bicep \
  --parameters appInsightsId="$APPINSIGHTS_ID" actionGroupId="$ACTION_GROUP_ID"
az monitor metrics alert show -g "$RESOURCE_GROUP" -n alert-orders-slo-burn \
  --query '{name:name,description:description,severity:severity,enabled:enabled,scopes:scopes,evaluationFrequency:evaluationFrequency,windowSize:windowSize,autoMitigate:autoMitigate,criteria:criteria,actions:actions}'
az monitor metrics list --resource "$APPINSIGHTS_ID" \
  --namespace azure.applicationinsights --metric portal.order.failed \
  --aggregation Average --interval PT1M --offset 30m
```

Check the rule properties against the contract above, including the action
group, `skipMetricValidation: true` and `GreaterThan`. Verify the metric contains
actual baseline samples, not an empty series. If the custom metric cannot be
used by metric alerts in this deployment, use the explicit fallback below;
do not silently substitute a platform 5xx count.

With the same customers Job still running, start a manual report and save the
pods' measured start times. Poll actual Alerts Management instances:

```bash
export ALERT_RULE=alert-orders-slo-burn
export SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
export REPORT_JOB="month-end-report-slo-check-$(date +%s)"
kubectl create job "$REPORT_JOB" -n demo --from=cronjob/month-end-report
kubectl wait --for=jsonpath='{.status.active}'=4 job/"$REPORT_JOB" -n demo --timeout=90s
kubectl get pods -n demo -l job-name="$REPORT_JOB" \
  -o custom-columns=NAME:.metadata.name,STARTED:.status.startTime
export ALERTS_URL="https://management.azure.com/subscriptions/$SUBSCRIPTION_ID/providers/Microsoft.AlertsManagement/alerts?api-version=2019-03-01&targetResource=$APPINSIGHTS_ID"
for attempt in $(seq 1 11); do
  date -u '+%Y-%m-%dT%H:%M:%SZ'
  az rest --method get --url "$ALERTS_URL" \
    --query "value[?properties.essentials.alertRule=='$ALERT_RULE'].{id:id,condition:properties.essentials.monitorCondition,fired:properties.essentials.startDateTime,changed:properties.essentials.lastModifiedDateTime}" \
    --output json
  sleep 30
done
```

**Acceptance:** a new instance for this run fires within five minutes of the
report pods starting. Compare `startDateTime` with the actual pod start time,
not the time the query was run. Existing resolved instances do not count.
Also inspect the rule's alert instances in Azure Portal, filtering to the lab
resource group and this rule. Follow pagination (`nextLink`) if returned.
Verify the lab action group receives the alert. Neither this module nor the
check configures the SRE Agent or its response plans.

Restore is not implemented until work item 4. For this incremental check,
perform its relevant reversible action manually, then restore the nightly spec:

```bash
kubectl patch cronjob month-end-report -n demo --type merge -p '{"spec":{"suspend":true}}'
kubectl patch job "$REPORT_JOB" -n demo --type merge -p '{"spec":{"suspend":true}}'
kubectl apply -f workloads/k8s/08-month-end-report.yaml
for attempt in $(seq 1 24); do
  date -u '+%Y-%m-%dT%H:%M:%SZ'
  az rest --method get --url "$ALERTS_URL" \
    --query "value[?properties.essentials.alertRule=='$ALERT_RULE'].{id:id,condition:properties.essentials.monitorCondition,fired:properties.essentials.startDateTime,changed:properties.essentials.lastModifiedDateTime}" \
    --output json
  sleep 30
done
```

Require the **same instance** to change from `Fired` to `Resolved` automatically
while customers keep posting successfully. User-managed alert state
(`New`/`Acknowledged`/`Closed`) is not the monitor condition. Resolution takes
the rolling five-minute window plus Azure's stateful-alert recovery evaluations;
it is not instantaneous on suspend. Measure both firing and resolution during
rehearsal. This local build cannot establish Azure ingestion, notification or
evaluation latency.

### Log-search fallback (explicit owner choice, not auto-selected)

If custom-metric alerting is unsupported for the deployed emission path, first
confirm `PortalOrder` metrics/events and requests are arriving (work item 2).
In **Application Insights Logs**, verify this query on the real order requests:

```kusto
requests
| where timestamp > ago(5m)
| where name == "POST /api/portal/orders"
| where toint(resultCode) in (200, 502, 504)
| summarize Attempts=sum(itemCount),
            Failed=sumif(itemCount, success == false)
| where Attempts > 0
| project FailureRate=todouble(Failed)/Attempts
```

Only valid completed orders (200/502/504) belong to this SLO denominator; exclude
401/400/413/429/configuration failures, as the business pulse does. Normal
rejections remain successful 200s. The `Attempts > 0` guard produces **no row**
for no traffic, rather than a made-up healthy value. `itemCount` preserves
weighting if ingestion sampling is configured outside the SDK.

In Azure Monitor, create a **stateful custom log search alert** scoped to the
same lab Application Insights component, in a supported regional location:

| Setting | Value |
|---|---|
| Name and description | `alert-orders-slo-burn-log`; description includes `slo-burn` and explicitly says log-search fallback |
| Query | The request failure-ratio query above |
| Measurement | Numeric column `FailureRate`, aggregation **Average**, no dimensions |
| Condition | **Greater than 0.05** |
| Lookback / evaluation | **5 minutes / 1 minute** |
| Failing periods | **1 of 1** |
| Severity / state | **1**, enabled, **automatically resolve alerts** (`autoMitigate: true`) |
| Actions | The existing lab action group |

Do not use a result-row Count threshold of 0.05: the numeric failure ratio,
not the number of query rows, is the signal. Once the fallback is deployed and
verified, disable the metric rule to avoid duplicate pages:

```bash
az monitor metrics alert update -g "$RESOURCE_GROUP" -n alert-orders-slo-burn --enabled false
```

Record the selected rule type, fallback reason, resource ID, query and measured
fire/resolve timings in the run's notes and in this guide for the deployment.
Repeat the fire/recover check with `ALERT_RULE=alert-orders-slo-burn-log`.
Log ingestion adds latency; do **not** claim the metric
rule's timing target passed if the fallback is slower. There is no automatic
switch and no claim that Azure custom-metric support was verified here.

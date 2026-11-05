namespace AmlabHello.MissionControl;

public sealed record PulsePoint(DateTimeOffset Time, int Attempts, double? SuccessRate, double? P95Ms);
public sealed record BatchObservations(DateTimeOffset SessionStartedAt, DateTimeOffset? BatchObservedAt,
    DateTimeOffset? FixObservedAt, DateTimeOffset? LastMeasuredAt);
public sealed record PulseSnapshot(DateTimeOffset Now, int WindowSeconds, int Attempts, int Confirmed, int Rejected,
    int Failed, double? SuccessRate, double? FailureRate, double OrdersPerMinute, decimal SalesPerMinute,
    double BrowsesPerMinute, double? P50Ms, double? P95Ms, double? P95LedgerQueueMs,
    IReadOnlyDictionary<string, int> FailureReasons, IReadOnlyList<PulsePoint> Series,
    DateTimeOffset? LastOrderAt, BatchObservations Batch);

public sealed class BusinessPulse(TimeProvider clock)
{
    private const int RetentionSeconds = 1800;
    private readonly object gate = new();
    private readonly Bucket?[] buckets = new Bucket[RetentionSeconds];
    private DateTimeOffset sessionStarted = clock.GetUtcNow();
    private DateTimeOffset? batchObserved;
    private DateTimeOffset? fixObserved;
    private DateTimeOffset? zeroSince;
    private DateTimeOffset? lastBatchMeasurement;
    private DateTimeOffset? lastOrder;

    private sealed class Bucket(long second)
    {
        public long Second { get; } = second;
        public int Confirmed;
        public int Rejected;
        public int Failed;
        public int Browses;
        public decimal Revenue;
        public List<double> Latencies { get; } = [];
        public List<double> QueueWaits { get; } = [];
        public Dictionary<string, int> Reasons { get; } = new(StringComparer.Ordinal);
        public int Attempts => Confirmed + Rejected + Failed;
    }

    private Bucket Current()
    {
        var second = clock.GetUtcNow().ToUnixTimeSeconds();
        var index = (int)(second % RetentionSeconds);
        if (buckets[index]?.Second != second) buckets[index] = new Bucket(second);
        return buckets[index]!;
    }

    public void RecordBrowse()
    {
        lock (gate) Current().Browses++;
    }

    public void RecordOrder(string outcome, string? reason, double durationMs, double? queueMs, decimal amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durationMs);
        if (!double.IsFinite(durationMs) || queueMs is { } wait && (!double.IsFinite(wait) || wait < 0))
            throw new ArgumentException("Only measured finite, nonnegative timings are accepted.");
        if (outcome is not ("confirmed" or "rejected" or "failed"))
            throw new ArgumentException("Unknown order outcome.", nameof(outcome));
        if (outcome == "failed" && string.IsNullOrEmpty(reason))
            throw new ArgumentException("A failure reason is required.", nameof(reason));
        lock (gate)
        {
            var bucket = Current();
            switch (outcome)
            {
                case "confirmed": bucket.Confirmed++; bucket.Revenue += amount; break;
                case "rejected": bucket.Rejected++; break;
                case "failed":
                    bucket.Failed++;
                    bucket.Reasons[reason!] = bucket.Reasons.GetValueOrDefault(reason!) + 1;
                    break;
            }
            bucket.Latencies.Add(durationMs);
            if (queueMs is { } measured) bucket.QueueWaits.Add(measured);
            lastOrder = clock.GetUtcNow();
        }
    }

    public void BeginSession(DateTimeOffset startedAt)
    {
        lock (gate)
        {
            sessionStarted = startedAt;
            batchObserved = fixObserved = zeroSince = lastBatchMeasurement = null;
        }
    }

    public void ObserveBatch(int? active, DateTimeOffset requestStartedAt)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (requestStartedAt < sessionStarted || now < sessionStarted) return;
            if (active is null or < 0)
            {
                zeroSince = null;
                lastBatchMeasurement = null;
                return;
            }
            if (active > 0)
            {
                batchObserved ??= now;
                zeroSince = null;
            }
            else if (batchObserved is not null && fixObserved is null)
            {
                // Silence is not recovery evidence. Require ongoing measured responses.
                if (lastBatchMeasurement is null || now - lastBatchMeasurement > TimeSpan.FromSeconds(5))
                    zeroSince = now;
                zeroSince ??= now;
                if (now - zeroSince >= TimeSpan.FromSeconds(30)) fixObserved = zeroSince;
            }
            lastBatchMeasurement = now;
        }
    }

    public PulseSnapshot Snapshot(TimeSpan? window = null)
    {
        var seconds = (int)(window ?? TimeSpan.FromSeconds(60)).TotalSeconds;
        if (seconds is < 1 or > RetentionSeconds) throw new ArgumentOutOfRangeException(nameof(window));
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var second = now.ToUnixTimeSeconds();
            var retained = buckets.OfType<Bucket>()
                .Where(b => b.Second > second - RetentionSeconds && b.Second <= second).ToArray();
            var selected = retained.Where(b => b.Second > second - seconds).ToArray();
            var attempts = selected.Sum(b => b.Attempts);
            var confirmed = selected.Sum(b => b.Confirmed);
            var rejected = selected.Sum(b => b.Rejected);
            var failed = selected.Sum(b => b.Failed);
            var reasons = selected.SelectMany(b => b.Reasons)
                .GroupBy(pair => pair.Key).ToDictionary(g => g.Key, g => g.Sum(pair => pair.Value));
            var latencies = selected.SelectMany(b => b.Latencies).Order().ToArray();
            var waits = selected.SelectMany(b => b.QueueWaits).Order().ToArray();
            var series = retained.GroupBy(b => b.Second / 10 * 10).OrderBy(g => g.Key)
                .Select(g =>
                {
                    var count = g.Sum(b => b.Attempts);
                    return new PulsePoint(DateTimeOffset.FromUnixTimeSeconds(g.Key), count,
                        count == 0 ? null : (double)g.Sum(b => b.Confirmed + b.Rejected) / count,
                        Percentile(g.SelectMany(b => b.Latencies).Order().ToArray(), 0.95));
                }).ToArray();
            return new(now, seconds, attempts, confirmed, rejected, failed,
                attempts == 0 ? null : (double)(confirmed + rejected) / attempts,
                attempts == 0 ? null : (double)failed / attempts,
                attempts * 60d / seconds, selected.Sum(b => b.Revenue) * 60 / seconds,
                selected.Sum(b => b.Browses) * 60d / seconds,
                Percentile(latencies, 0.5), Percentile(latencies, 0.95), Percentile(waits, 0.95),
                reasons, series, lastOrder,
                new(sessionStarted, batchObserved, fixObserved, lastBatchMeasurement));
        }
    }

    private static double? Percentile(double[] sorted, double fraction) => sorted.Length == 0
        ? null : sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
}

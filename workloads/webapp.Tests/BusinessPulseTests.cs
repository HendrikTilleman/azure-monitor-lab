using AmlabHello.MissionControl;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AmlabHello.Tests;

public sealed class BusinessPulseTests
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ArithmeticPercentilesRevenueAndFailureReasonsUseRealOutcomes()
    {
        var pulse = new BusinessPulse(clock);
        for (var index = 1; index <= 20; index++)
        {
            pulse.RecordOrder(index == 20 ? "failed" : index == 19 ? "rejected" : "confirmed",
                index == 20 ? "ledger_timeout" : null, index, index == 20 ? null : index, 10m);
        }
        pulse.RecordBrowse();
        pulse.RecordBrowse();
        var result = pulse.Snapshot();
        Assert.Equal(20, result.Attempts);
        Assert.Equal(18, result.Confirmed);
        Assert.Equal(1, result.Rejected);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0.95, result.SuccessRate);
        Assert.Equal(0.05, result.FailureRate);
        Assert.Equal(20, result.OrdersPerMinute);
        Assert.Equal(180m, result.SalesPerMinute);
        Assert.Equal(2, result.BrowsesPerMinute);
        Assert.Equal(10, result.P50Ms);
        Assert.Equal(19, result.P95Ms);
        Assert.Equal(19, result.P95LedgerQueueMs);
        Assert.Equal(1, result.FailureReasons["ledger_timeout"]);
        Assert.Equal(clock.GetUtcNow(), result.LastOrderAt);
        Assert.Equal(0.95, Assert.Single(result.Series).SuccessRate);
        var fiveMinutes = pulse.Snapshot(TimeSpan.FromMinutes(5));
        Assert.Equal(20, fiveMinutes.Attempts);
        Assert.Equal(4, fiveMinutes.OrdersPerMinute);
    }

    [Fact]
    public void WindowsAndRetentionExpireWithoutInventingSamples()
    {
        var pulse = new BusinessPulse(clock);
        Assert.Null(pulse.Snapshot().SuccessRate);
        pulse.RecordOrder("failed", "ledger_http_500", 3000, null, 10);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, pulse.Snapshot().Attempts);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, pulse.Snapshot().Attempts);
        Assert.Null(pulse.Snapshot().P95Ms);
        Assert.Null(pulse.Snapshot().P95LedgerQueueMs);
        Assert.Equal(1, pulse.Snapshot(TimeSpan.FromMinutes(5)).Attempts);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Empty(pulse.Snapshot().Series);
        pulse.RecordBrowse();
        var result = pulse.Snapshot();
        Assert.Equal(1, result.BrowsesPerMinute);
        Assert.Null(Assert.Single(result.Series).SuccessRate);
        Assert.Null(Assert.Single(result.Series).P95Ms);
    }

    [Fact]
    public void TenSecondSeriesGroupsWithoutSmoothingOrFillingGaps()
    {
        var pulse = new BusinessPulse(clock);
        pulse.RecordOrder("confirmed", null, 10, 0, 50);
        clock.Advance(TimeSpan.FromSeconds(5));
        pulse.RecordOrder("failed", "ledger_timeout", 3000, null, 50);
        clock.Advance(TimeSpan.FromSeconds(25));
        pulse.RecordOrder("rejected", null, 15, 1, 50);
        var series = pulse.Snapshot().Series;
        Assert.Equal(2, series.Count);
        Assert.Equal(2, series[0].Attempts);
        Assert.Equal(0.5, series[0].SuccessRate);
        Assert.Equal(3000, series[0].P95Ms);
        Assert.Equal(1, series[1].SuccessRate);
        Assert.Equal(TimeSpan.FromSeconds(30), series[1].Time - series[0].Time);
    }

    [Fact]
    public void ConcurrentUpdatesDoNotLoseOrders()
    {
        var pulse = new BusinessPulse(clock);
        Parallel.For(0, 2000, _ => pulse.RecordOrder("confirmed", null, 10, 1, 2));
        Assert.Equal(2000, pulse.Snapshot().Attempts);
        Assert.Equal(4000m, pulse.Snapshot().SalesPerMinute);
    }

    [Fact]
    public void BatchFixRequiresThirtySecondsOfContinuingMeasuredZeroResponses()
    {
        var pulse = new BusinessPulse(clock);
        var started = clock.GetUtcNow();
        pulse.BeginSession(started);
        pulse.ObserveBatch(0, started);
        Assert.Null(pulse.Snapshot().Batch.BatchObservedAt);
        pulse.ObserveBatch(3, started);
        var batchAt = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        var firstZero = clock.GetUtcNow();
        pulse.ObserveBatch(0, started);
        for (var index = 0; index < 14; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            pulse.ObserveBatch(0, started);
        }
        Assert.Null(pulse.Snapshot().Batch.FixObservedAt);
        clock.Advance(TimeSpan.FromSeconds(2));
        pulse.ObserveBatch(0, started);
        Assert.Equal(firstZero, pulse.Snapshot().Batch.FixObservedAt);
        Assert.Equal(batchAt, pulse.Snapshot().Batch.BatchObservedAt);
    }

    [Fact]
    public void MissingHeadersSilenceAndOldInFlightResponsesCannotProveRecovery()
    {
        var pulse = new BusinessPulse(clock);
        var started = clock.GetUtcNow();
        pulse.ObserveBatch(1, started);
        pulse.ObserveBatch(0, started);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(pulse.Snapshot().Batch.FixObservedAt);
        pulse.ObserveBatch(0, started);
        Assert.Null(pulse.Snapshot().Batch.FixObservedAt);
        pulse.ObserveBatch(null, started);
        clock.Advance(TimeSpan.FromSeconds(31));
        pulse.ObserveBatch(0, started);
        Assert.Null(pulse.Snapshot().Batch.FixObservedAt);
        pulse.BeginSession(clock.GetUtcNow());
        pulse.ObserveBatch(10, started);
        Assert.Null(pulse.Snapshot().Batch.BatchObservedAt);
        Assert.Null(pulse.Snapshot().Batch.FixObservedAt);
    }

    [Fact]
    public void PositiveBatchObservationResetsTheZeroWindow()
    {
        var pulse = new BusinessPulse(clock);
        var started = clock.GetUtcNow();
        pulse.ObserveBatch(1, started);
        for (var index = 0; index < 29; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            pulse.ObserveBatch(0, started);
        }
        pulse.ObserveBatch(1, started);
        clock.Advance(TimeSpan.FromSeconds(1));
        pulse.ObserveBatch(0, started);
        Assert.Null(pulse.Snapshot().Batch.FixObservedAt);
    }
}

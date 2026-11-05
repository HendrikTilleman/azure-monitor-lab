using AmlabHello.MissionControl;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AmlabHello.Tests;

public sealed class PortalTelemetryTests
{
    [Fact]
    public async Task FiveMinutePulseAgreesWithEmittedOrderEventsAndFailureMetric()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        using var harness = new PortalHarness(clock);
        for (var index = 0; index < 360; index++)
        {
            harness.Handler.Respond = (_, _) => Task.FromResult(index % 10 == 0
                ? LedgerStubHandler.Response(System.Net.HttpStatusCode.ServiceUnavailable,
                    """{"error":"queue_timeout"}""", "8000", "6")
                : LedgerStubHandler.Response(body: index % 20 == 1
                    ? """{"postingId":"rejected","outcome":"rejected"}"""
                    : """{"postingId":"confirmed","outcome":"accepted"}"""));
            await harness.Service.OrderAsync(new(), "web", default);
            if (index < 359) clock.Advance(TimeSpan.FromSeconds(1));
        }
        var pulse = harness.Pulse.Snapshot(TimeSpan.FromMinutes(5));
        var events = harness.Channel.Items.OfType<EventTelemetry>().Where(e => e.Name == "PortalOrder").TakeLast(300).ToArray();
        var eventRate = (double)events.Count(e => e.Properties["outcome"] is "confirmed" or "rejected") / events.Length;
        var last = events[^1];
        Assert.Equal(300, pulse.Attempts);
        Assert.Equal(0.9, pulse.SuccessRate);
        Assert.Equal(eventRate, pulse.SuccessRate);
        Assert.Equal(pulse.SuccessRate, last.Metrics["pulseSuccessRate5m"]);
        Assert.Equal(300, last.Metrics["pulseAttempts5m"]);
        var metrics = harness.Channel.Items.OfType<MetricTelemetry>()
            .Where(m => m.Name == "portal.order.failed").TakeLast(300).ToArray();
        Assert.Equal(0.1, metrics.Average(m => m.Sum), precision: 6);
    }

    [Fact]
    public void PortalEvidenceIsNotSampledWhileUnrelatedLabTrafficKeepsItsSamplingPolicy()
    {
        var channel = new PortalRecordingChannel();
        using var configuration = new TelemetryConfiguration { TelemetryChannel = channel };
        configuration.TelemetryInitializers.Add(new PortalTelemetryInitializer());
        configuration.TelemetryProcessorChainBuilder.Use(next =>
            new SamplingTelemetryProcessor(next) { SamplingPercentage = 0.000001 }).Build();
        var telemetry = new TelemetryClient(configuration);
        for (var index = 0; index < 100; index++)
        {
            telemetry.TrackEvent("PortalOrder");
            telemetry.TrackDependency(new DependencyTelemetry { Name = "core-ledger" });
            telemetry.TrackRequest(new RequestTelemetry { Url = new Uri("https://portal.example/api/portal/orders") });
        }
        Assert.Equal(300, channel.Items.Count);
        Assert.All(channel.Items, item => Assert.Equal(100, ((ISupportSampling)item).SamplingPercentage));
        var unrelated = new EventTelemetry("CheckoutCompleted");
        new PortalTelemetryInitializer().Initialize(unrelated);
        Assert.Null(((ISupportSampling)unrelated).SamplingPercentage);
    }
}

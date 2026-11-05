using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AmlabHello.MissionControl;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmlabHello.Tests;

internal sealed class PortalRecordingChannel : ITelemetryChannel
{
    public ConcurrentQueue<ITelemetry> Items { get; } = new();
    public bool? DeveloperMode { get; set; }
    public string EndpointAddress { get; set; } = "";
    public void Send(ITelemetry item) => Items.Enqueue(item);
    public void Flush() { }
    public void Dispose() { }
}

internal sealed class LedgerStubHandler : HttpMessageHandler
{
    public int Calls;
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(Response());

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        return Respond(request, token);
    }

    public static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK,
        string body = """{"postingId":"posting-1","outcome":"accepted"}""", string? queue = "12.5", string? batch = "0")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (queue is not null) response.Headers.Add("X-Ledger-Queue-Ms", queue);
        if (batch is not null) response.Headers.Add("X-Ledger-Batch-Active", batch);
        return response;
    }
}

internal sealed class LedgerStubFactory(LedgerStubHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
}

internal sealed class PortalHarness : IDisposable
{
    public LedgerStubHandler Handler { get; } = new();
    public PortalRecordingChannel Channel { get; } = new();
    public BusinessPulse Pulse { get; }
    public TelemetryConfiguration Configuration { get; }
    public TelemetryClient Telemetry { get; }
    public CoreLedgerClient Client { get; }
    public PortalService Service { get; }

    public static Dictionary<string, string?> Settings { get; } = new()
    {
        ["LabConsole:Mission:Enabled"] = "true",
        ["LabConsole:Mission:CustomerKey"] = "customer-test-key",
        ["LabConsole:Mission:LedgerKey"] = "ledger-test-key",
        ["LabConsole:Mission:LedgerBaseUrl"] = "http://ledger.example"
    };

    public PortalHarness(TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var options = new MissionPortalOptions(new ConfigurationBuilder().AddInMemoryCollection(Settings).Build());
        Pulse = new(clock);
        Configuration = new TelemetryConfiguration { TelemetryChannel = Channel };
        Telemetry = new(Configuration);
        Client = new(new LedgerStubFactory(Handler), options, Pulse, Telemetry,
            NullLogger<CoreLedgerClient>.Instance, clock);
        Service = new(Client, Pulse, Telemetry, NullLogger<PortalService>.Instance, clock);
    }

    public void Dispose()
    {
        Configuration.Dispose();
        Handler.Dispose();
    }
}

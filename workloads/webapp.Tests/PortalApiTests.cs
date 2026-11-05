using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AmlabHello.MissionControl;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AmlabHello.Tests;

public sealed class PortalApiTests
{
    private sealed class Factory(bool enabled = true, bool ledgerConfigured = true) : WebApplicationFactory<Program>
    {
        public LedgerStubHandler Ledger { get; } = new();
        public PortalRecordingChannel Channel { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var settings = new Dictionary<string, string?>(PortalHarness.Settings)
            {
                ["LabConsole:Mission:Enabled"] = enabled.ToString(),
                ["LabConsole:Mission:LedgerKey"] = ledgerConfigured ? "ledger-test-key" : "",
                ["ApplicationInsights:ConnectionString"] =
                    "InstrumentationKey=00000000-0000-0000-0000-000000000001"
            };
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("core-ledger").ConfigurePrimaryHttpMessageHandler(() => Ledger);
                services.RemoveAll<ITelemetryChannel>();
                services.AddSingleton<ITelemetryChannel>(Channel);
                services.Configure<ApplicationInsightsServiceOptions>(options => options.EnableAdaptiveSampling = false);
            });
        }

        public HttpClient Customer(bool authenticated = true)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://portal.example"),
                AllowAutoRedirect = false
            });
            if (authenticated) client.DefaultRequestHeaders.Add("X-Lab-Customer-Key", "customer-test-key");
            return client;
        }
    }

    [Fact]
    public async Task CustomerKeyAllowsBothEndpointsWithoutOperatorIdentityOrPostHeader()
    {
        using var factory = new Factory();
        using var customer = factory.Customer();
        using var catalog = await customer.GetAsync("/api/portal/catalog");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        var products = await catalog.Content.ReadFromJsonAsync<PortalProduct[]>();
        Assert.Equal(6, products!.Length);
        Assert.All(products, p => Assert.Equal("EUR", p.Currency));
        Assert.Equal(0, factory.Ledger.Calls);
        using var order = await customer.PostAsJsonAsync("/api/portal/orders", new { });
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        var body = await order.Content.ReadFromJsonAsync<PortalOrderResponse>();
        Assert.Equal("confirmed", body!.Outcome);
        Assert.Equal(12.50m, body.Amount);
        Assert.Equal(12.5, body.LedgerQueueMs);
        // The new customer key must not grant access to existing protected operations.
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.GetAsync("/api/operations/catalog")).StatusCode);
        var snapshot = factory.Services.GetRequiredService<BusinessPulse>().Snapshot();
        Assert.Equal(1, snapshot.Attempts);
        Assert.Equal(1, snapshot.BrowsesPerMinute);
        var telemetry = Assert.Single(factory.Channel.Items.OfType<EventTelemetry>(), e => e.Name == "PortalOrder");
        Assert.Equal("confirmed", telemetry.Properties["outcome"]);
        Assert.Equal(1, telemetry.Metrics["pulseSuccessRate5m"]);
        Assert.Equal(12.5, telemetry.Metrics["ledgerQueueMs"]);
        Assert.Contains(factory.Channel.Items.OfType<MetricTelemetry>(),
            metric => metric.Name == "portal.order.failed" && metric.Sum == 0);
        Assert.Contains(factory.Channel.Items.OfType<DependencyTelemetry>(),
            dependency => dependency.Name == "core-ledger" && dependency.Success == true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task MissingOrWrongKeyIs401ForCatalogAndOrders(string? key)
    {
        using var factory = new Factory();
        using var customer = factory.Customer(false);
        if (key is not null) customer.DefaultRequestHeaders.Add("X-Lab-Customer-Key", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.GetAsync("/api/portal/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.PostAsJsonAsync("/api/portal/orders", new { })).StatusCode);
        Assert.Equal(0, factory.Ledger.Calls);
        Assert.Equal(0, factory.Services.GetRequiredService<BusinessPulse>().Snapshot().Attempts);
    }

    [Fact]
    public async Task DuplicateKeysAreNotAccepted()
    {
        using var factory = new Factory();
        using var customer = factory.Customer();
        customer.DefaultRequestHeaders.Add("X-Lab-Customer-Key", "another");
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.GetAsync("/api/portal/catalog")).StatusCode);
    }

    [Fact]
    public async Task DisabledPortalAndMissingLedgerFailExplicitly()
    {
        using (var factory = new Factory(enabled: false))
        using (var customer = factory.Customer())
            Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync("/api/portal/catalog")).StatusCode);
        using (var factory = new Factory(ledgerConfigured: false))
        using (var customer = factory.Customer())
        {
            Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/portal/catalog")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                (await customer.PostAsJsonAsync("/api/portal/orders", new { })).StatusCode);
        }
    }

    [Fact]
    public async Task RejectionIsSuccessfulAndTimeoutIs504WithFailedTelemetry()
    {
        using var factory = new Factory();
        using var customer = factory.Customer();
        factory.Ledger.Respond = (_, _) => Task.FromResult(
            LedgerStubHandler.Response(body: """{"postingId":"posting-rejected","outcome":"rejected"}"""));
        using var rejected = await customer.PostAsJsonAsync("/api/portal/orders", new { sku = "CT-003", quantity = 2 });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("rejected", (await rejected.Content.ReadFromJsonAsync<PortalOrderResponse>())!.Outcome);
        factory.Ledger.Respond = (_, _) => Task.FromResult(
            LedgerStubHandler.Response(HttpStatusCode.ServiceUnavailable, """{"error":"queue_timeout"}""", "8000", "6"));
        using var failure = await customer.PostAsJsonAsync("/api/portal/orders", new { });
        Assert.Equal(HttpStatusCode.GatewayTimeout, failure.StatusCode);
        using var body = JsonDocument.Parse(await failure.Content.ReadAsStringAsync());
        Assert.Equal("order_failed", body.RootElement.GetProperty("error").GetString());
        Assert.Equal("ledger_queue_timeout", body.RootElement.GetProperty("reason").GetString());
        var pulse = factory.Services.GetRequiredService<BusinessPulse>().Snapshot();
        Assert.Equal(2, pulse.Attempts);
        Assert.Equal(0.5, pulse.SuccessRate);
        Assert.Equal(0, pulse.SalesPerMinute);
        Assert.NotNull(pulse.Batch.BatchObservedAt);
        Assert.Contains(factory.Channel.Items.OfType<MetricTelemetry>(),
            metric => metric.Name == "portal.order.failed" && metric.Sum == 1);
        Assert.Contains(factory.Channel.Items.OfType<EventTelemetry>(),
            item => item.Name == "PortalOrder" && item.Properties["reason"] == "ledger_queue_timeout");
        Assert.Equal(2, factory.Ledger.Calls);
    }

    [Theory]
    [InlineData("""{"sku":"unknown"}""")]
    [InlineData("""{"quantity":0}""")]
    [InlineData("""{"quantity":101}""")]
    [InlineData("""{"quantity":"many"}""")]
    [InlineData("not json")]
    public async Task InvalidOrderDoesNotBecomeARealAttempt(string body)
    {
        using var factory = new Factory();
        using var customer = factory.Customer();
        using var response = await customer.PostAsync("/api/portal/orders", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Ledger.Calls);
        Assert.Equal(0, factory.Services.GetRequiredService<BusinessPulse>().Snapshot().Attempts);
    }

    [Fact]
    public async Task EmptyBodyUsesDefaultsAndChannelValidationIsBounded()
    {
        using var factory = new Factory();
        using var customer = factory.Customer();
        using var empty = await customer.PostAsync("/api/portal/orders", null);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        customer.DefaultRequestHeaders.Add("X-Amlab-Channel", "not-a-channel");
        using var badChannel = await customer.PostAsJsonAsync("/api/portal/orders", new { });
        Assert.Equal(HttpStatusCode.BadRequest, badChannel.StatusCode);
        Assert.Equal(1, factory.Ledger.Calls);
    }

    [Fact]
    public async Task DedicatedLimiterRejectsExcessCustomersWithoutLimitingHealth()
    {
        using var factory = new Factory();
        using var customer = factory.Customer();
        for (var index = 0; index < 1200; index++)
        {
            using var response = await customer.GetAsync("/api/portal/catalog");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var excess = await customer.GetAsync("/api/portal/catalog");
        Assert.Equal(HttpStatusCode.TooManyRequests, excess.StatusCode);
        Assert.True(excess.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/healthz")).StatusCode);
        Assert.Equal(0, factory.Ledger.Calls);
    }
}

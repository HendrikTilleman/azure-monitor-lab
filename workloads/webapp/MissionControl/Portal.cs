using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;

namespace AmlabHello.MissionControl;

public sealed class MissionPortalOptions(IConfiguration configuration)
{
    public bool Enabled { get; } = configuration.GetValue<bool>("LabConsole:Mission:Enabled");
    public string CustomerKey { get; } = configuration["LabConsole:Mission:CustomerKey"] ?? "";
    public string LedgerKey { get; } = configuration["LabConsole:Mission:LedgerKey"] ?? "";
    public Uri? LedgerBaseUrl { get; } = SafeUrl(configuration["LabConsole:Mission:LedgerBaseUrl"]);
    public bool LedgerConfigured => LedgerBaseUrl is not null && LedgerKey.Length > 0;

    private static Uri? SafeUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;

    public bool Allows(string supplied) => CustomerKey.Length > 0
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(CustomerKey)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
}

public sealed record PortalProduct(string Sku, string Name, decimal Price, string Currency = "EUR");
public sealed record PortalOrderRequest(string? Sku = null, int? Quantity = null);
public sealed record PortalOrderResponse(string OrderId, string Outcome, decimal Amount, string Currency,
    double? LedgerQueueMs);

public sealed class PortalService(CoreLedgerClient ledger, BusinessPulse pulse, TelemetryClient telemetry,
    ILogger<PortalService> logger, TimeProvider clock)
{
    public static IReadOnlyList<PortalProduct> Catalog { get; } = Array.AsReadOnly<PortalProduct>(
    [
        new("CT-001", "Contoso Notebook", 12.50m),
        new("CT-002", "Contoso Desk Lamp", 39.00m),
        new("CT-003", "Contoso Backpack", 59.00m),
        new("CT-004", "Contoso Water Bottle", 19.50m),
        new("CT-005", "Contoso Headphones", 89.00m),
        new("CT-006", "Contoso Keyboard", 65.00m)
    ]);

    public IResult Browse()
    {
        pulse.RecordBrowse();
        return Results.Json(Catalog);
    }

    public async Task<IResult> OrderAsync(PortalOrderRequest? request, string channel, CancellationToken cancellationToken)
    {
        var product = request?.Sku is null ? Catalog[0] : Catalog.FirstOrDefault(p => p.Sku == request.Sku);
        var quantity = request?.Quantity ?? 1;
        if (product is null || quantity is < 1 or > 100)
            return Results.BadRequest(new { error = "Choose a catalog SKU and a quantity between 1 and 100." });
        if (channel is not ("web" or "mobile" or "partner"))
            return Results.BadRequest(new { error = "Channel must be web, mobile or partner." });

        var started = clock.GetTimestamp();
        var amount = product.Price * quantity;
        var result = await ledger.PostAsync(amount, channel, cancellationToken);
        var duration = clock.GetElapsedTime(started).TotalMilliseconds;
        var outcome = result.Reason is not null ? "failed" : result.Outcome == "rejected" ? "rejected" : "confirmed";
        pulse.RecordOrder(outcome, result.Reason, duration, result.QueueMs, amount);
        var fiveMinutes = pulse.Snapshot(TimeSpan.FromMinutes(5));
        var properties = new Dictionary<string, string>
        {
            ["outcome"] = outcome, ["reason"] = result.Reason ?? "", ["channel"] = channel,
            ["amountBucket"] = amount < 50 ? "under_50" : amount < 200 ? "50_to_199" : "200_plus"
        };
        var metrics = new Dictionary<string, double> { ["durationMs"] = duration };
        if (result.QueueMs is { } wait) metrics["ledgerQueueMs"] = wait;
        if (fiveMinutes.SuccessRate is { } rate) metrics["pulseSuccessRate5m"] = rate;
        metrics["pulseAttempts5m"] = fiveMinutes.Attempts;
        telemetry.TrackEvent("PortalOrder", properties, metrics);
        // TrackMetric preserves each 0/1 sample for the alert's Average aggregation.
        telemetry.TrackMetric("portal.order.failed", outcome == "failed" ? 1 : 0);
        telemetry.TrackMetric("portal.order.duration", duration);
        if (result.Reason is not null)
        {
            logger.LogWarning("Portal order failed: {Reason}", result.Reason);
            return Results.Json(new { error = "order_failed", reason = result.Reason },
                statusCode: result.StatusCode);
        }
        return Results.Json(new PortalOrderResponse(result.PostingId!, outcome, amount, "EUR", result.QueueMs));
    }
}

public static class PortalEndpoints
{
    public static IServiceCollection AddMissionPortal(this IServiceCollection services)
    {
        services.AddSingleton<MissionPortalOptions>();
        services.AddSingleton<ITelemetryInitializer, PortalTelemetryInitializer>();
        services.AddSingleton<BusinessPulse>();
        services.AddTransient<CoreLedgerClient>();
        services.AddTransient<PortalService>();
        services.AddHttpClient("core-ledger", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = 16384;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Retry policy is owned by CoreLedgerClient, not HttpClient's pooled-connection retry.
            PooledConnectionLifetime = TimeSpan.Zero
        });
        return services;
    }

    public static async Task AuthorizeAsync(HttpContext context, RequestDelegate next)
    {
        var options = context.RequestServices.GetRequiredService<MissionPortalOptions>();
        if (!options.Enabled)
        {
            await Results.NotFound(new { error = "Portal is not enabled." }).ExecuteAsync(context);
            return;
        }
        var values = context.Request.Headers["X-Lab-Customer-Key"];
        if (values.Count != 1 || !options.Allows(values.ToString()))
        {
            await Results.Json(new { error = "Customer key required." }, statusCode: 401).ExecuteAsync(context);
            return;
        }
        if (!options.LedgerConfigured && context.Request.Path.StartsWithSegments("/api/portal/orders"))
        {
            await Results.Json(new { error = "Portal ledger is not configured." }, statusCode: 503).ExecuteAsync(context);
            return;
        }
        var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 4096;
        await next(context);
    }

    public static void MapPortalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/portal").RequireRateLimiting("portal-customers");
        group.MapGet("/catalog", (PortalService service) => service.Browse());
        group.MapPost("/orders", PlaceOrderAsync);
    }

    private static async Task<IResult> PlaceOrderAsync(HttpContext context, PortalService service,
        CancellationToken cancellationToken)
    {
        PortalOrderRequest? request = null;
        var hasBody = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody
            ?? (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"));
        if (hasBody)
        {
            if (!context.Request.HasJsonContentType())
                return Results.Json(new { error = "Expected an application/json order." }, statusCode: 415);
            try
            {
                request = await context.Request.ReadFromJsonAsync<PortalOrderRequest>(cancellationToken);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid order JSON." });
            }
            catch (BadHttpRequestException error)
            {
                return Results.Json(new { error = "Invalid order body." }, statusCode: error.StatusCode);
            }
        }
        var channel = context.Request.Headers["X-Amlab-Channel"];
        if (channel.Count > 1)
            return Results.BadRequest(new { error = "Supply only one channel." });
        return await service.OrderAsync(request, channel.FirstOrDefault() ?? "web", cancellationToken);
    }
}

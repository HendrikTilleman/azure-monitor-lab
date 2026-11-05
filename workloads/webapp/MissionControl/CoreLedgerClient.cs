using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;

namespace AmlabHello.MissionControl;

public sealed record LedgerPosting(string? PostingId, string? Outcome, string? Reason, int StatusCode,
    double? QueueMs = null);

public sealed class CoreLedgerClient(IHttpClientFactory factory, MissionPortalOptions options,
    BusinessPulse pulse, TelemetryClient telemetry, ILogger<CoreLedgerClient> logger, TimeProvider clock)
{
    private sealed record PostingBody(string? PostingId, string? Outcome);
    public static readonly TimeSpan TimeoutBudget = TimeSpan.FromSeconds(3);

    public async Task<LedgerPosting> PostAsync(decimal amount, string channel, CancellationToken cancellationToken)
    {
        var startedAt = clock.GetUtcNow();
        if (!options.LedgerConfigured)
            return new(null, null, "ledger_not_configured", 502);
        using var timeout = new CancellationTokenSource(TimeoutBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var client = factory.CreateClient("core-ledger");
        var reference = Guid.NewGuid().ToString();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var operation = telemetry.StartOperation<DependencyTelemetry>("core-ledger");
            operation.Telemetry.Type = "HTTP";
            operation.Telemetry.Target = options.LedgerBaseUrl!.Authority;
            operation.Telemetry.Data = "/v1/postings";
            operation.Telemetry.Success = false;
            operation.Telemetry.Properties["attempt"] = (attempt + 1).ToString(CultureInfo.InvariantCulture);
            double? queueMs = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    new Uri(options.LedgerBaseUrl.GetLeftPart(UriPartial.Authority) + "/v1/postings"))
                {
                    // The standard-library ledger requires Content-Length, not chunked request bodies.
                    Content = new StringContent(JsonSerializer.Serialize(new { amount, currency = "EUR", channel, reference }),
                        Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Client-Id", "customer-portal");
                request.Headers.Add("X-Ledger-Key", options.LedgerKey);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                operation.Telemetry.ResultCode = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                if (double.TryParse(Header(response, "X-Ledger-Queue-Ms"),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var wait)
                    && double.IsFinite(wait) && wait >= 0)
                    queueMs = wait;
                int? batch = null;
                if (int.TryParse(Header(response, "X-Ledger-Batch-Active"),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                    && count >= 0)
                    batch = count;
                pulse.ObserveBatch(batch, startedAt);
                if (queueMs is null || batch is null)
                    logger.LogWarning("Ledger response has missing or invalid observation headers");
                if (!response.IsSuccessStatusCode)
                {
                    var reason = $"ledger_http_{(int)response.StatusCode}";
                    if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        using var body = JsonDocument.Parse(await ReadBodyAsync(response.Content, linked.Token));
                        if (body.RootElement.ValueKind == JsonValueKind.Object
                            && body.RootElement.TryGetProperty("error", out var error)
                            && error.ValueKind == JsonValueKind.String && error.GetString() == "queue_timeout")
                            reason = "ledger_queue_timeout";
                    }
                    return new(null, null, reason, reason == "ledger_queue_timeout" ? 504 : 502, queueMs);
                }
                // Buffer under the same deadline; never let a slow response body escape the timeout.
                var posting = JsonSerializer.Deserialize<PostingBody>(await ReadBodyAsync(response.Content, linked.Token),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (posting is null || string.IsNullOrWhiteSpace(posting.PostingId)
                    || posting.Outcome is not ("accepted" or "rejected"))
                    throw new JsonException("Invalid posting response.");
                operation.Telemetry.Success = true;
                return new(posting.PostingId, posting.Outcome, null, 200, queueMs);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                var reason = cancellationToken.IsCancellationRequested ? "ledger_request_cancelled" : "ledger_timeout";
                operation.Telemetry.ResultCode = reason;
                return new(null, null, reason, 504, queueMs);
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                var reason = ConnectionReason(error);
                operation.Telemetry.ResultCode = reason;
                logger.LogWarning("Ledger transport failed: {Reason}", reason);
                if (attempt == 0 && IsConnectionError(error))
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(150), clock, linked.Token);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        return new(null, null, cancellationToken.IsCancellationRequested
                            ? "ledger_request_cancelled" : "ledger_timeout", 504, queueMs);
                    }
                    continue;
                }
                return new(null, null, reason, 502, queueMs);
            }
            catch (JsonException error)
            {
                logger.LogWarning("Ledger response failed validation: {ErrorType}", error.GetType().Name);
                operation.Telemetry.ResultCode = "ledger_invalid_response";
                return new(null, null, "ledger_invalid_response", 502, queueMs);
            }
        }
        throw new UnreachableException();
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken token)
    {
        using var stream = await content.ReadAsStreamAsync(token);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (body.Length + read > 16384) throw new JsonException("Ledger response exceeds 16 KiB.");
            body.Write(buffer, 0, read);
        }
        return body.ToArray();
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        var items = values.Take(2).ToArray();
        return items.Length == 1 ? items[0] : null;
    }

    private static bool IsConnectionError(Exception error) =>
        error is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError }
        || SocketFailure(error) is not null;

    private static SocketException? SocketFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SocketException socket) return socket;
        return null;
    }

    private static string ConnectionReason(Exception error)
    {
        if (SocketFailure(error) is { } socket)
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "ledger_connection_refused",
                SocketError.ConnectionReset or SocketError.ConnectionAborted => "ledger_connection_reset",
                _ => "ledger_connection_error"
            };
        return IsConnectionError(error) ? "ledger_connection_error" : "ledger_transport_error";
    }
}

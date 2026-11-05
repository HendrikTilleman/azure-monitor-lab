using System.Net;
using System.Net.Sockets;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AmlabHello.Tests;

public sealed class CoreLedgerClientTests
{
    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task PostingUsesOnlyConfiguredHostAndRequiredHeaders()
    {
        using var harness = new PortalHarness();
        harness.Handler.Respond = async (request, token) =>
        {
            Assert.Equal("http://ledger.example/v1/postings", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("customer-portal", Assert.Single(request.Headers.GetValues("X-Client-Id")));
            Assert.Equal("ledger-test-key", Assert.Single(request.Headers.GetValues("X-Ledger-Key")));
            Assert.True(request.Content!.Headers.ContentLength > 0);
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.Contains("\"amount\":42.50", body);
            Assert.Contains("\"channel\":\"mobile\"", body);
            Assert.DoesNotContain("customer-test-key", body);
            return LedgerStubHandler.Response(batch: "2");
        };
        var result = await harness.Client.PostAsync(42.50m, "mobile", default);
        Assert.Null(result.Reason);
        Assert.Equal(12.5, result.QueueMs);
        Assert.NotNull(harness.Pulse.Snapshot().Batch.BatchObservedAt);
        var dependency = Assert.Single(harness.Channel.Items.OfType<DependencyTelemetry>());
        Assert.Equal("core-ledger", dependency.Name);
        Assert.True(dependency.Success);
        Assert.DoesNotContain("ledger-test-key", dependency.Data);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, "ledger_connection_refused")]
    [InlineData(SocketError.ConnectionReset, "ledger_connection_reset")]
    public async Task ConnectionFailuresRetryOnceAndKeepSpecificReason(SocketError error, string reason)
    {
        using var harness = new PortalHarness();
        harness.Handler.Respond = (_, _) => throw new HttpRequestException("connection failed",
            new SocketException((int)error));
        var result = await harness.Client.PostAsync(1, "web", default);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal(2, harness.Handler.Calls);
        Assert.Equal(2, harness.Channel.Items.OfType<DependencyTelemetry>().Count());
    }

    [Fact]
    public async Task ConnectionRetryCanRecoverWithoutCountingAnOrderFailure()
    {
        using var harness = new PortalHarness();
        harness.Handler.Respond = (_, _) => harness.Handler.Calls == 1
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "connection reset")
            : Task.FromResult(LedgerStubHandler.Response());
        var result = await harness.Client.PostAsync(1, "web", default);
        Assert.Null(result.Reason);
        Assert.Equal(2, harness.Handler.Calls);
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.ResponseEnded)]
    public async Task NonConnectionTransportFailuresDoNotRetry(HttpRequestError error)
    {
        using var harness = new PortalHarness();
        harness.Handler.Respond = (_, _) => throw new HttpRequestException(error, "transport failure");
        var result = await harness.Client.PostAsync(1, "web", default);
        Assert.Equal("ledger_transport_error", result.Reason);
        Assert.Equal(1, harness.Handler.Calls);
    }

    [Fact]
    public async Task NestedSocketErrorsKeepTheirSpecificConnectionReason()
    {
        using var harness = new PortalHarness();
        harness.Handler.Respond = (_, _) => throw new HttpRequestException("send failed",
            new IOException("read failed", new SocketException((int)SocketError.ConnectionReset)));
        var result = await harness.Client.PostAsync(1, "web", default);
        Assert.Equal("ledger_connection_reset", result.Reason);
        Assert.Equal(2, harness.Handler.Calls);
    }

    [Theory]
    [InlineData(500, """{"error":"server_error"}""", "ledger_http_500", 502)]
    [InlineData(503, """{"error":"queue_timeout"}""", "ledger_queue_timeout", 504)]
    [InlineData(503, """{"error":"shutting_down"}""", "ledger_http_503", 502)]
    [InlineData(401, "unauthorized", "ledger_http_401", 502)]
    [InlineData(302, "", "ledger_http_302", 502)]
    public async Task HttpFailuresNeverRetryAndObserveHeaders(int status, string body, string reason, int portalStatus)
    {
        using var harness = new PortalHarness();
        harness.Handler.Respond = (_, _) => Task.FromResult(
            LedgerStubHandler.Response((HttpStatusCode)status, body, batch: "4"));
        var result = await harness.Client.PostAsync(1, "web", default);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(portalStatus, result.StatusCode);
        Assert.Equal(1, harness.Handler.Calls);
        Assert.NotNull(harness.Pulse.Snapshot().Batch.BatchObservedAt);
    }

    [Fact]
    public async Task ThreeSecondDeadlineNeverRetriesATimeout()
    {
        var clock = new FakeTimeProvider();
        using var harness = new PortalHarness(clock);
        harness.Handler.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return LedgerStubHandler.Response();
        };
        var pending = harness.Client.PostAsync(1, "web", default);
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(3));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ledger_timeout", result.Reason);
        Assert.Equal(504, result.StatusCode);
        Assert.Equal(1, harness.Handler.Calls);
    }

    [Fact]
    public async Task DeadlineIncludesTheResponseBodyAndStillObservesBatchHeaders()
    {
        var clock = new FakeTimeProvider();
        using var harness = new PortalHarness(clock);
        harness.Handler.Respond = (_, _) =>
        {
            var response = LedgerStubHandler.Response(batch: "2");
            response.Content = new StreamContent(new BlockingReadStream());
            return Task.FromResult(response);
        };
        var pending = harness.Client.PostAsync(1, "web", default);
        Assert.False(pending.IsCompleted);
        Assert.NotNull(harness.Pulse.Snapshot().Batch.BatchObservedAt);
        clock.Advance(TimeSpan.FromSeconds(3));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ledger_timeout", result.Reason);
        Assert.Equal(1, harness.Handler.Calls);
        Assert.Equal(12.5, result.QueueMs);
    }

    [Fact]
    public async Task RetrySharesTheOriginalThreeSecondBudget()
    {
        var clock = new FakeTimeProvider();
        using var harness = new PortalHarness(clock);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Respond = async (_, token) =>
        {
            if (harness.Handler.Calls == 1)
                throw new HttpRequestException(HttpRequestError.ConnectionError, "connection reset");
            secondAttempt.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return LedgerStubHandler.Response();
        };
        var pending = harness.Client.PostAsync(1, "web", default);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMilliseconds(2850));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ledger_timeout", result.Reason);
        Assert.Equal(2, harness.Handler.Calls);
    }

    [Fact]
    public async Task OversizedResponsesAreRejectedWithoutARetry()
    {
        using var harness = new PortalHarness();
        harness.Handler.Respond = (_, _) => Task.FromResult(LedgerStubHandler.Response(body: new string('x', 17000)));
        var result = await harness.Client.PostAsync(1, "web", default);
        Assert.Equal("ledger_invalid_response", result.Reason);
        Assert.Equal(1, harness.Handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationIsNotRetriedOrReportedAsLedgerTimeout()
    {
        using var harness = new PortalHarness();
        using var cancelled = new CancellationTokenSource();
        harness.Handler.Respond = async (_, token) =>
        {
            cancelled.Cancel();
            await Task.Delay(1, token);
            return LedgerStubHandler.Response();
        };
        var result = await harness.Client.PostAsync(1, "web", cancelled.Token);
        Assert.Equal("ledger_request_cancelled", result.Reason);
        Assert.Equal(1, harness.Handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"postingId":"p","outcome":"unknown"}""")]
    public async Task MalformedSuccessIsExplicitAndNeverRetried(string body)
    {
        using var harness = new PortalHarness();
        harness.Handler.Respond = (_, _) => Task.FromResult(LedgerStubHandler.Response(body: body));
        var result = await harness.Client.PostAsync(1, "web", default);
        Assert.Equal("ledger_invalid_response", result.Reason);
        Assert.Equal(1, harness.Handler.Calls);
    }

    [Fact]
    public async Task MissingHeadersStayUnmeasuredRatherThanBecomingZeros()
    {
        using var harness = new PortalHarness();
        harness.Handler.Respond = (_, _) => Task.FromResult(LedgerStubHandler.Response(queue: null, batch: null));
        var result = await harness.Client.PostAsync(1, "web", default);
        Assert.Null(result.QueueMs);
        Assert.Null(harness.Pulse.Snapshot().Batch.LastMeasuredAt);
    }
}

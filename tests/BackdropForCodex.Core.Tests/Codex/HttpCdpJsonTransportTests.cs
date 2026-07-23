using System.Net;
using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class HttpCdpJsonTransportTests
{
    [Fact]
    public async Task GetStringAsync_RejectsNonLoopbackBeforeSending()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        using var transport = new HttpCdpJsonTransport(client);

        await Assert.ThrowsAsync<ArgumentException>(() => transport
            .GetStringAsync(new Uri("http://192.0.2.1:9222/json/version"))
            .AsTask());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetStringAsync_RejectsRedirectedResponse()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://example.com/json/version"),
            Content = new StringContent("{}"),
        }));
        using var client = new HttpClient(handler);
        using var transport = new HttpCdpJsonTransport(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => transport
            .GetStringAsync(new Uri("http://127.0.0.1:9222/json/version"))
            .AsTask());
    }

    [Fact]
    public async Task GetStringAsync_EnforcesResponseLimit()
    {
        var handler = new StubHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new NonSeekableReadStream(new byte[65])),
            }));
        using var client = new HttpClient(handler);
        using var transport = new HttpCdpJsonTransport(
            client,
            requestTimeout: TimeSpan.FromSeconds(1),
            maxResponseBytes: 64);

        await Assert.ThrowsAsync<CdpResponseTooLargeException>(() => transport
            .GetStringAsync(new Uri("http://127.0.0.1:9222/json/list"))
            .AsTask());
    }

    [Fact]
    public async Task GetStringAsync_EnforcesIndependentTimeout()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var transport = new HttpCdpJsonTransport(
            client,
            requestTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport
            .GetStringAsync(new Uri("http://127.0.0.1:9222/json/version"))
            .AsTask());
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return sendAsync(request, cancellationToken);
        }
    }

    private sealed class NonSeekableReadStream(byte[] buffer)
        : MemoryStream(buffer, writable: false)
    {
        public override bool CanSeek => false;
    }
}

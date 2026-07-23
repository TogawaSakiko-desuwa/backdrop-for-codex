using System.Net;
using System.Net.Http.Headers;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class LoopbackMediaServerTests
{
    [Fact]
    public async Task StartAsyncUsesA256BitOpaqueTokenAndPathFreeEndpointDto()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = await CreatePngAsync(directoryPath, "private-wallpaper.png");
            await using var server = new LoopbackMediaServer();

            var endpoint = await server.StartAsync(mediaPath);
            var token = endpoint.Uri.AbsolutePath.Trim('/');
            var decodedToken = Convert.FromBase64String(
                token.Replace('-', '+').Replace('_', '/') + "=");

            Assert.Equal(IPAddress.Loopback.ToString(), endpoint.Uri.Host);
            Assert.Equal(32, decodedToken.Length);
            Assert.Equal(43, token.Length);
            Assert.DoesNotContain(mediaPath, endpoint.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                endpoint.GetType().GetProperties(),
                property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
            Assert.True(server.IsRunning);
            Assert.Equal(endpoint, server.CurrentEndpoint);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task GetHeadAndRangeServeOnlyTheCurrentFile()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = await CreatePngAsync(directoryPath, "wallpaper.png");
            var expectedBytes = await File.ReadAllBytesAsync(mediaPath);
            await using var server = new LoopbackMediaServer();
            var endpoint = await server.StartAsync(mediaPath);
            using var client = CreateHttpClient();

            using var getResponse = await client.GetAsync(endpoint.Uri);
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            Assert.Equal("image/png", getResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expectedBytes, await getResponse.Content.ReadAsByteArrayAsync());

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, endpoint.Uri);
            using var headResponse = await client.SendAsync(headRequest);
            Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            Assert.Equal(expectedBytes.LongLength, headResponse.Content.Headers.ContentLength);
            Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, endpoint.Uri);
            rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
            using var rangeResponse = await client.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal("bytes", rangeResponse.Content.Headers.ContentRange?.Unit);
            Assert.Equal(2, rangeResponse.Content.Headers.ContentRange?.From);
            Assert.Equal(5, rangeResponse.Content.Headers.ContentRange?.To);
            Assert.Equal(expectedBytes.LongLength, rangeResponse.Content.Headers.ContentRange?.Length);
            Assert.Equal(expectedBytes[2..6], await rangeResponse.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task ServerRejectsDirectoryQueryAndUnsupportedMethods()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = await CreatePngAsync(directoryPath, "wallpaper.png");
            await using var server = new LoopbackMediaServer();
            var endpoint = await server.StartAsync(mediaPath);
            using var client = CreateHttpClient();

            var origin = endpoint.Uri.GetLeftPart(UriPartial.Authority);
            using var rootResponse = await client.GetAsync(new Uri($"{origin}/"));
            using var childResponse = await client.GetAsync(new Uri($"{endpoint.Uri}/child"));
            using var queryResponse = await client.GetAsync(new Uri($"{endpoint.Uri}?path={Uri.EscapeDataString(mediaPath)}"));
            using var postResponse = await client.PostAsync(endpoint.Uri, content: null);

            Assert.Equal(HttpStatusCode.NotFound, rootResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, childResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, queryResponse.StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, postResponse.StatusCode);
            Assert.Contains(HttpMethod.Get.Method, postResponse.Content.Headers.Allow);
            Assert.Contains(HttpMethod.Head.Method, postResponse.Content.Headers.Allow);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task RestartReplacesTheSingleMappingAndRotatesTheToken()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var firstPath = await CreatePngAsync(directoryPath, "first.png");
            var secondPath = await CreatePngAsync(directoryPath, "second.png", trailingByte: 0x42);
            await using var server = new LoopbackMediaServer();
            using var client = CreateHttpClient();
            var firstEndpoint = await server.StartAsync(firstPath);

            var secondEndpoint = await server.StartAsync(secondPath);

            Assert.NotEqual(firstEndpoint.Uri.AbsolutePath, secondEndpoint.Uri.AbsolutePath);
            Assert.Equal(secondEndpoint, server.CurrentEndpoint);
            Assert.Equal(await File.ReadAllBytesAsync(secondPath), await client.GetByteArrayAsync(secondEndpoint.Uri));

            try
            {
                using var oldResponse = await client.GetAsync(firstEndpoint.Uri);
                Assert.False(oldResponse.IsSuccessStatusCode);
            }
            catch (HttpRequestException)
            {
                // A new ephemeral port was selected and the previous listener is gone.
            }
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task StopAsyncRemovesTheListenerAndIsIdempotent()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = await CreatePngAsync(directoryPath, "wallpaper.png");
            await using var server = new LoopbackMediaServer();
            var endpoint = await server.StartAsync(mediaPath);
            using var client = CreateHttpClient();

            await server.StopAsync();
            await server.StopAsync();

            Assert.False(server.IsRunning);
            Assert.Null(server.CurrentEndpoint);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(endpoint.Uri));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task RunningServerKeepsValidatedSourceReadOnlyUntilStopped()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = await CreatePngAsync(directoryPath, "wallpaper.png");
            await using var server = new LoopbackMediaServer();
            await server.StartAsync(mediaPath);

            Assert.Throws<IOException>(() =>
            {
                using var writer = new FileStream(
                    mediaPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite);
            });

            await server.StopAsync();
            await using var writable = new FileStream(
                mediaPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            Assert.True(writable.CanWrite);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    private static HttpClient CreateHttpClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static async Task<string> CreatePngAsync(
        string directoryPath,
        string fileName,
        byte? trailingByte = null)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (trailingByte is not null)
        {
            bytes.Add(trailingByte.Value);
        }

        var mediaPath = Path.Combine(directoryPath, fileName);
        await File.WriteAllBytesAsync(mediaPath, bytes.ToArray());
        return mediaPath;
    }

    private static string CreateTemporaryDirectory()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "BackdropForCodex.Core.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static void DeleteTemporaryDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}

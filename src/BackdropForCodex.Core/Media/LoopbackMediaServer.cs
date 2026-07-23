using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackdropForCodex.Core.Media;

/// <summary>
/// The public, path-free description of a running loopback media endpoint.
/// </summary>
public sealed record LoopbackMediaEndpoint(
    Uri Uri,
    MediaFormat Format,
    MediaKind Kind,
    string ContentType,
    long ContentLength);

public interface ILoopbackMediaServer : IAsyncDisposable
{
    bool IsRunning { get; }

    LoopbackMediaEndpoint? CurrentEndpoint { get; }

    Task<LoopbackMediaEndpoint> StartAsync(
        string mediaPath,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Hosts exactly one validated file at one unguessable URL on the IPv4 loopback interface.
/// </summary>
public sealed class LoopbackMediaServer : ILoopbackMediaServer
{
    private const int TokenSizeInBytes = 32;

    private readonly IMediaFileInspector _inspector;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApplication? _application;
    private FileStream? _mediaLease;
    private LoopbackMediaEndpoint? _currentEndpoint;
    private int _disposeState;

    public LoopbackMediaServer(IMediaFileInspector? inspector = null)
    {
        _inspector = inspector ?? new MediaFileInspector();
    }

    public bool IsRunning => CurrentEndpoint is not null;

    public LoopbackMediaEndpoint? CurrentEndpoint => Volatile.Read(ref _currentEndpoint);

    public async Task<LoopbackMediaEndpoint> StartAsync(
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        FileStream? mediaLease = null;
        try
        {
            mediaLease = OpenMediaLease(mediaPath);
            var metadata = await _inspector
                .InspectAsync(mediaPath, cancellationToken)
                .ConfigureAwait(false);
            if (mediaLease.Length != metadata.ContentLength)
            {
                throw new MediaValidationException(
                    "The media file changed while it was being validated.");
            }

            var lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(mediaPath), TimeSpan.Zero);
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);

                var token = CreateToken();
                var resourcePath = $"/{token}";
                var application = BuildApplication(resourcePath, mediaPath, metadata, lastModified);

                try
                {
                    await application.StartAsync(cancellationToken).ConfigureAwait(false);
                    var endpoint = CreateEndpoint(application, resourcePath, metadata);
                    _application = application;
                    _mediaLease = mediaLease;
                    mediaLease = null;
                    Volatile.Write(ref _currentEndpoint, endpoint);
                    return endpoint;
                }
                catch (OperationCanceledException)
                {
                    await DisposeApplicationAsync(application).ConfigureAwait(false);
                    throw;
                }
                catch (Exception exception)
                {
                    await DisposeApplicationAsync(application).ConfigureAwait(false);
                    throw new LoopbackMediaServerException(
                        "The loopback media server could not be started.",
                        exception);
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            mediaLease?.Dispose();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static WebApplication BuildApplication(
        string resourcePath,
        string mediaPath,
        MediaFileMetadata metadata,
        DateTimeOffset lastModified)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(LoopbackMediaServer).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
        });

        // Framework request logging includes URL paths. This private server deliberately has no
        // providers; a caller can log path-free lifecycle events around the abstraction instead.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        application.Run(async context =>
        {
            if (!string.Equals(context.Request.Path.Value, resourcePath, StringComparison.Ordinal) ||
                context.Request.QueryString.HasValue)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.Headers.Allow = $"{HttpMethods.Get}, {HttpMethods.Head}";
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            if (!File.Exists(mediaPath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";

            var result = Results.File(
                mediaPath,
                metadata.ContentType,
                lastModified: lastModified,
                enableRangeProcessing: true);
            await result.ExecuteAsync(context).ConfigureAwait(false);
        });

        return application;
    }

    private static LoopbackMediaEndpoint CreateEndpoint(
        WebApplication application,
        string resourcePath,
        MediaFileMetadata metadata)
    {
        var server = application.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault();

        if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out var baseUri))
        {
            throw new LoopbackMediaServerException("The loopback media server did not publish an address.");
        }

        if (!IPAddress.TryParse(baseUri.Host, out var boundAddress) || !IPAddress.IsLoopback(boundAddress))
        {
            throw new LoopbackMediaServerException("The media server did not bind to a loopback address.");
        }

        var endpointUri = new UriBuilder(baseUri)
        {
            Path = resourcePath,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;

        return new LoopbackMediaEndpoint(
            endpointUri,
            metadata.Format,
            metadata.Kind,
            metadata.ContentType,
            metadata.ContentLength);
    }

    private static string CreateToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(TokenSizeInBytes);
        return Convert.ToBase64String(tokenBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static FileStream OpenMediaLease(string mediaPath)
    {
        try
        {
            // Keeping this read-only handle open prevents ordinary writes, renames, and deletes
            // from changing the path after its signature has been validated.
            return new FileStream(
                mediaPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException exception)
        {
            throw new MediaValidationException("The media file was not found.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new MediaValidationException("The media file was not found.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MediaValidationException("The media file could not be opened.", exception);
        }
        catch (IOException exception)
        {
            throw new MediaValidationException("The media file could not be read.", exception);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var application = _application;
        var mediaLease = _mediaLease;
        _application = null;
        _mediaLease = null;
        Volatile.Write(ref _currentEndpoint, null);

        if (application is null)
        {
            mediaLease?.Dispose();
            return;
        }

        try
        {
            await application.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                mediaLease?.Dispose();
            }
        }
    }

    private static async Task DisposeApplicationAsync(WebApplication application)
    {
        try
        {
            await application.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Startup may have failed before a hosted server existed. Disposal is still required.
        }

        await application.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
}

public sealed class LoopbackMediaServerException : InvalidOperationException
{
    public LoopbackMediaServerException(string message)
        : base(message)
    {
    }

    public LoopbackMediaServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

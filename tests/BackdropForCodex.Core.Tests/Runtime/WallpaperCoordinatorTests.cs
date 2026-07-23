using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Runtime;

public sealed class WallpaperCoordinatorTests
{
    [Fact]
    public async Task StartOrUpdateAsync_ActivatesOnlyAfterValidationAndAppliesVerifiedEndpoint()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();

        var saved = await coordinator.StartOrUpdateAsync(fixture.ValidSettings);

        Assert.True(coordinator.IsActive);
        Assert.Equal(MediaKind.Image, saved.MediaKind);
        Assert.Equal(CodexCompatibilityCatalog.SupportedPackageVersion, fixture.Package.Descriptor.Version);
        Assert.Equal(1, fixture.Activation.CallCount);
        Assert.Equal(WallpaperCoordinator.RemoteDebuggingArguments, fixture.Activation.Arguments);
        Assert.Equal(1, fixture.MediaServer.StartCount);
        Assert.Equal(1, fixture.Injection.ApplyCount);
        Assert.Equal(fixture.Endpoint, fixture.Injection.LastEndpoint);
        Assert.Equal(fixture.MediaServer.Endpoint.Uri, fixture.Injection.LastOptions?.Source);
        Assert.Equal(fixture.ValidSettings.MediaPath, fixture.Injection.LastOptions?.LocalMediaPath);
        Assert.Equal(
            fixture.MediaServer.Endpoint.ContentLength,
            fixture.Injection.LastOptions?.ExpectedContentLength);
        Assert.Equal(1, fixture.SettingsStore.SaveCount);
        Assert.Equal(WallpaperRuntimePhase.Active, coordinator.Status.Phase);
    }

    [Fact]
    public async Task StartOrUpdateAsync_RefusesCodexThatWasAlreadyRunning()
    {
        var fixture = new CoordinatorFixture();
        fixture.ProcessSource.Processes = [fixture.ReviewedProcess];
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<CodexAlreadyRunningException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

        Assert.Equal(0, fixture.Activation.CallCount);
        Assert.Equal(0, fixture.MediaServer.StartCount);
        Assert.Equal(0, fixture.Injection.ApplyCount);
        Assert.Equal(WallpaperRuntimePhase.Faulted, coordinator.Status.Phase);
    }

    [Fact]
    public async Task StartOrUpdateAsync_RequiresExplicitRiskAcknowledgement()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<CdpRiskNotAcceptedException>(
            () => coordinator.StartOrUpdateAsync(
                fixture.ValidSettings with { AcceptedCdpRisk = false }));

        Assert.Equal(0, fixture.Inspector.CallCount);
        Assert.Equal(0, fixture.Activation.CallCount);
    }

    [Fact]
    public async Task StartOrUpdateAsync_CleansMediaWhenInjectionFails()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.ApplyException = new WallpaperInjectionException("test failure");
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<WallpaperInjectionException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

        Assert.Equal(1, fixture.MediaServer.StopCount);
        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public async Task DisableAsync_RemovesInjectionBeforeStoppingMedia()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);

        await coordinator.DisableAsync();

        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Equal(1, fixture.MediaServer.StopCount);
        Assert.False(coordinator.IsActive);
        Assert.Equal(WallpaperRuntimePhase.Idle, coordinator.Status.Phase);
    }

    [Fact]
    public async Task StartOrUpdateAsync_RefusesReplacementForPreviouslyOwnedProcess()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
        await coordinator.DisableAsync();
        fixture.ProcessSource.Processes =
        [
            fixture.ReviewedProcess with
            {
                ProcessId = 84,
                StartTimeUtc = fixture.ReviewedProcess.StartTimeUtc.AddMinutes(1),
            },
        ];

        await Assert.ThrowsAsync<CodexAlreadyRunningException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

        Assert.Equal(1, fixture.Activation.CallCount);
        Assert.Equal(1, fixture.MediaServer.StartCount);
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public async Task StartOrUpdateAsync_DoesNotAttachEndpointOwnedByDifferentProcess()
    {
        var fixture = new CoordinatorFixture();
        var foreignEndpoint = new VerifiedCdpEndpoint(
            fixture.Endpoint.Candidate with { ProcessId = 84 },
            fixture.Endpoint.Browser,
            fixture.Endpoint.BrowserWebSocketUri,
            fixture.Endpoint.Targets);
        fixture.Discovery.Results.Clear();
        fixture.Discovery.Results.Enqueue(new CdpDiscoveryResult([foreignEndpoint], []));
        await using var coordinator = fixture.CreateCoordinator(
            new WallpaperCoordinatorOptions
            {
                DiscoveryTimeout = TimeSpan.FromMilliseconds(25),
                DiscoveryInterval = TimeSpan.FromMilliseconds(1),
            });

        await Assert.ThrowsAsync<CdpEndpointTimeoutException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

        Assert.Equal(0, fixture.Injection.ApplyCount);
        Assert.Equal(1, fixture.Activation.CallCount);
    }

    [Fact]
    public async Task SetPausedAsync_CommitsStateOnlyAfterPageConfirmsIt()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
        fixture.Injection.PauseException = new WallpaperInjectionException("pause failed");

        await Assert.ThrowsAsync<WallpaperInjectionException>(
            () => coordinator.SetPausedAsync(true));

        Assert.False(coordinator.IsPaused);
        Assert.Equal(WallpaperRuntimePhase.Faulted, coordinator.Status.Phase);
    }

    [Fact]
    public async Task StartOrUpdateAsync_NewImageGenerationDoesNotInheritVideoPause()
    {
        var fixture = new CoordinatorFixture();
        fixture.Inspector.Format = MediaFormat.WebM;
        fixture.MediaServer.Endpoint = new LoopbackMediaEndpoint(
            new Uri("http://127.0.0.1:50100/private-video-token"),
            MediaFormat.WebM,
            MediaKind.Video,
            "video/webm",
            128);
        await using var coordinator = fixture.CreateCoordinator();
        var videoSettings = fixture.ValidSettings with
        {
            MediaPath = "C:\\Wallpapers\\wallpaper.webm",
            MediaKind = MediaKind.Video,
        };
        await coordinator.StartOrUpdateAsync(videoSettings);
        await coordinator.SetPausedAsync(true);
        fixture.ProcessSource.Processes = [fixture.ReviewedProcess];
        fixture.Inspector.Format = MediaFormat.Png;
        fixture.MediaServer.Endpoint = new LoopbackMediaEndpoint(
            new Uri("http://127.0.0.1:50100/private-image-token"),
            MediaFormat.Png,
            MediaKind.Image,
            "image/png",
            128);

        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);

        Assert.False(coordinator.IsPaused);
        Assert.Equal(WallpaperRuntimePhase.Active, coordinator.Status.Phase);
        Assert.Equal(2, fixture.Injection.ApplyCount);
        Assert.Equal(1, fixture.Injection.SetPausedCount);
    }

    [Fact]
    public async Task SaveSettingsAsync_PersistsRiskRevocationWithoutLaunchingCodex()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var revoked = fixture.ValidSettings with { AcceptedCdpRisk = false };

        var saved = await coordinator.SaveSettingsAsync(revoked);

        Assert.False(saved.AcceptedCdpRisk);
        Assert.False(fixture.SettingsStore.Settings.AcceptedCdpRisk);
        Assert.Equal(0, fixture.Activation.CallCount);
    }

    [Fact]
    public async Task DisposeAsync_AttemptsEveryResourceAfterCleanupFailures()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.StopException = new InvalidOperationException("injection stop failed");
        fixture.Injection.DisposeException = new InvalidOperationException("injection dispose failed");
        fixture.MediaServer.StopException = new InvalidOperationException("media stop failed");
        fixture.MediaServer.DisposeException = new InvalidOperationException("media dispose failed");
        fixture.SettingsStore.DisposeException = new InvalidOperationException("settings dispose failed");
        var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<AggregateException>(() => coordinator.DisposeAsync().AsTask());

        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Equal(1, fixture.Injection.DisposeCount);
        Assert.Equal(1, fixture.MediaServer.StopCount);
        Assert.Equal(1, fixture.MediaServer.DisposeCount);
        Assert.Equal(1, fixture.SettingsStore.DisposeCount);
        Assert.Equal(WallpaperRuntimePhase.Disposed, coordinator.Status.Phase);
    }

    [Fact]
    public async Task InjectionHealthFault_StopsMediaAndTransitionsRuntimeToFaulted()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
        var faulted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.Phase == WallpaperRuntimePhase.Faulted)
            {
                faulted.TrySetResult();
            }
        };

        fixture.Injection.RaiseHealthFault();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.IsActive);
        Assert.False(fixture.MediaServer.IsRunning);
        Assert.Equal(1, fixture.MediaServer.StopCount);
        Assert.Equal(WallpaperRuntimePhase.Faulted, coordinator.Status.Phase);
    }

    private sealed class CoordinatorFixture
    {
        private readonly CodexCompatibilityProfile _profile;

        public CoordinatorFixture()
        {
            var descriptor = new CodexPackageDescriptor(
                CodexCompatibilityCatalog.OfficialPackageName,
                CodexCompatibilityCatalog.OfficialPackageFamilyName,
                CodexCompatibilityCatalog.SupportedPackageVersion,
                CodexPackageArchitecture.X64,
                CodexCompatibilityCatalog.OfficialApplicationId);
            Package = new InstalledCodexPackage(descriptor, "package", "C:\\Codex", "app/ChatGPT.exe");
            _profile = CodexCompatibilityCatalog.Evaluate(
                descriptor,
                new CodexRuntimeDescriptor(
                    IsWindows: true,
                    new Version(10, 0, 22631, 0),
                    CodexPackageArchitecture.X64)).Profile!;
            ReviewedProcess = new CodexProcessSnapshot(
                42,
                "ChatGPT.exe",
                CodexCompatibilityCatalog.OfficialPackageFamilyName,
                CodexCompatibilityCatalog.SupportedPackageFullName,
                new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                WindowsCodexProcessSnapshotSource.CurrentSessionId,
                null);
            Endpoint = new VerifiedCdpEndpoint(
                new CdpEndpointCandidate(
                    ReviewedProcess.ProcessId,
                    ReviewedProcess.ExecutableName,
                    ReviewedProcess.PackageFamilyName,
                    ReviewedProcess.PackageFullName,
                    ReviewedProcess.StartTimeUtc,
                    ReviewedProcess.SessionId,
                    new Uri("http://127.0.0.1:49152/")),
                new CdpBrowserVersion(
                    "Chrome/140.0.0.0",
                    "1.3",
                    null,
                    null,
                    "ws://127.0.0.1:49152/devtools/browser/test"),
                new Uri("ws://127.0.0.1:49152/devtools/browser/test"),
                [new ClassifiedCdpTarget(
                    new CdpTargetDescriptor(
                        "page",
                        "page",
                        "Codex",
                        "app://codex/index.html",
                        "ws://127.0.0.1:49152/devtools/page/page"),
                    CdpTargetClassification.CodexPage)]);
            Discovery.Results.Enqueue(new CdpDiscoveryResult([Endpoint], []));
        }

        public InstalledCodexPackage Package { get; }

        public CodexProcessSnapshot ReviewedProcess { get; }

        public VerifiedCdpEndpoint Endpoint { get; }

        public FakeProcessSource ProcessSource { get; } = new();

        public FakeActivationManager Activation { get; } = new();

        public FakeDiscovery Discovery { get; } = new();

        public FakeMediaInspector Inspector { get; } = new();

        public FakeMediaServer MediaServer { get; } = new();

        public FakeInjectionSession Injection { get; } = new();

        public FakeSettingsStore SettingsStore { get; } = new();

        public SettingsV1 ValidSettings { get; } = SettingsV1.CreateDefault() with
        {
            MediaPath = "C:\\Wallpapers\\wallpaper.png",
            MediaKind = MediaKind.Image,
            AcceptedCdpRisk = true,
        };

        public WallpaperCoordinator CreateCoordinator(WallpaperCoordinatorOptions? options = null) => new(
            new FakePackageLocator(Package),
            ProcessSource,
            Activation,
            Discovery,
            Inspector,
            MediaServer,
            Injection,
            SettingsStore,
            options ?? new WallpaperCoordinatorOptions
            {
                DiscoveryTimeout = TimeSpan.FromSeconds(1),
                DiscoveryInterval = TimeSpan.FromMilliseconds(1),
            });

        private sealed class FakePackageLocator(InstalledCodexPackage package)
            : IInstalledCodexPackageLocator
        {
            public InstalledCodexPackage Locate() => package;
        }
    }

    private sealed class FakeProcessSource : ICodexProcessSnapshotSource
    {
        public IReadOnlyList<CodexProcessSnapshot> Processes { get; set; } = [];

        public ValueTask<IReadOnlyList<CodexProcessSnapshot>> GetProcessesAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Processes);
    }

    private sealed class FakeActivationManager : IApplicationActivationManager
    {
        public int CallCount { get; private set; }

        public string? Arguments { get; private set; }

        public ApplicationActivationResult Activate(
            CodexCompatibilityProfile profile,
            string? arguments = null,
            ApplicationActivationOptions options = ApplicationActivationOptions.NoErrorUi)
        {
            CallCount++;
            Arguments = arguments;
            return new ApplicationActivationResult(42);
        }
    }

    private sealed class FakeDiscovery : ICdpEndpointDiscoveryService
    {
        public Queue<CdpDiscoveryResult> Results { get; } = new();

        public ValueTask<CdpDiscoveryResult> DiscoverAsync(
            CodexCompatibilityProfile profile,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                Results.Count == 0 ? new CdpDiscoveryResult([], []) : Results.Dequeue());
    }

    private sealed class FakeMediaInspector : IMediaFileInspector
    {
        public int CallCount { get; private set; }

        public MediaFormat Format { get; set; } = MediaFormat.Png;

        public Task<MediaFileMetadata> InspectAsync(
            string mediaPath,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(MediaFileInspector.CreateMetadata(Format, 128));
        }
    }

    private sealed class FakeMediaServer : ILoopbackMediaServer
    {
        public LoopbackMediaEndpoint Endpoint { get; set; } = new(
            new Uri("http://127.0.0.1:50100/private-token"),
            MediaFormat.Png,
            MediaKind.Image,
            "image/png",
            128);

        public bool IsRunning { get; private set; }

        public LoopbackMediaEndpoint? CurrentEndpoint => IsRunning ? Endpoint : null;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Exception? StartException { get; set; }

        public Exception? StopException { get; set; }

        public Exception? DisposeException { get; set; }

        public Task<LoopbackMediaEndpoint> StartAsync(
            string mediaPath,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (StartException is not null)
            {
                return Task.FromException<LoopbackMediaEndpoint>(StartException);
            }

            IsRunning = true;
            return Task.FromResult(Endpoint);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            IsRunning = false;
            return StopException is null ? Task.CompletedTask : Task.FromException(StopException);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsRunning = false;
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class FakeInjectionSession :
        IWallpaperInjectionSession,
        IWallpaperInjectionHealthSource
    {
        public event EventHandler<WallpaperInjectionHealthFaultedEventArgs>? HealthFaulted;

        public bool IsActive { get; private set; }

        public long Generation => LastOptions?.Generation ?? 0;

        public int ApplyCount { get; private set; }

        public int StopCount { get; private set; }

        public int SetPausedCount { get; private set; }

        public Exception? ApplyException { get; set; }

        public Exception? PauseException { get; set; }

        public Exception? StopException { get; set; }

        public Exception? DisposeException { get; set; }

        public int DisposeCount { get; private set; }

        public VerifiedCdpEndpoint? LastEndpoint { get; private set; }

        public WallpaperInjectionOptions? LastOptions { get; private set; }

        public Task ApplyAsync(
            VerifiedCdpEndpoint endpoint,
            WallpaperInjectionOptions options,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            if (ApplyException is not null)
            {
                return Task.FromException(ApplyException);
            }

            LastEndpoint = endpoint;
            LastOptions = options;
            IsActive = true;
            return Task.CompletedTask;
        }

        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
        {
            SetPausedCount++;
            return PauseException is null ? Task.CompletedTask : Task.FromException(PauseException);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            IsActive = false;
            return StopException is null ? Task.CompletedTask : Task.FromException(StopException);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsActive = false;
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }

        public void RaiseHealthFault()
        {
            var generation = Generation;
            if (generation == 0)
            {
                throw new InvalidOperationException("Apply a wallpaper before raising a health fault.");
            }

            HealthFaulted?.Invoke(
                this,
                new WallpaperInjectionHealthFaultedEventArgs(generation, "test health fault"));
        }
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public int SaveCount { get; private set; }

        public SettingsV1 Settings { get; private set; } = SettingsV1.CreateDefault();

        public int DisposeCount { get; private set; }

        public Exception? DisposeException { get; set; }

        public Task<SettingsV1> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings);

        public Task SaveAsync(SettingsV1 settings, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Settings = settings;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisposeCount++;
            if (DisposeException is not null)
            {
                throw DisposeException;
            }
        }
    }
}

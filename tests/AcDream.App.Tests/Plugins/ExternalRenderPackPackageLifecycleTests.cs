using System.Runtime.CompilerServices;
using System.Text.Json;
using AcDream.App.Configuration;
using AcDream.App.Plugins;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Packs;
using AcDream.App.Settings;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Platform;
using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.Plugins;

public sealed class ExternalRenderPackPackageLifecycleTests
{
    private const string PackageId = "acdream.test.external-render-pack-package";
    private const string PackId = "acdream.test.external-render-pack";
    private static readonly RenderPackActivationExtent Extent = new(1280, 720, 1);

    [Fact]
    public void LiveProductionCatalogWithdrawsAtFrameBoundaryAndAcceptsCorrectedReregistration()
    {
        using var temporary = new TemporaryDirectory();
        RunLiveProductionCatalogLifecycle(temporary);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunLiveProductionCatalogLifecycle(TemporaryDirectory temporary)
    {
        ApplicationPathSet paths = Paths(temporary.Path);
        _ = InstallPackage(paths.PluginsDirectory, new Version(1, 0, 0));
        string settingsPath = Path.Combine(paths.ConfigDirectory, "settings.json");
        Directory.CreateDirectory(paths.ConfigDirectory);
        var selected = new RenderPackSelectionSettings(PackId, "1.0.0", "low");
        new JsonRuntimeSettingsStorage(settingsPath).SaveDisplay(
            DisplaySettings.Default with { RenderPack = selected });

        using var registry = new BufferedRenderPackRegistry();
        var source = new RenderPackCatalogSource(
            registry,
            RenderPackHostCapabilities.Conformance);
        using var device = new RecordingGpuDevice();
        var factory = new CountingFactory(device);
        using var controller = new RenderPackController(
            source.Snapshot,
            factory,
            preparationScheduler: InlineRenderPackPreparationScheduler.Instance,
            catalogSource: source);
        var settings = new RuntimeSettingsController(
            new JsonRuntimeSettingsStorage(settingsPath),
            log: static _ => { });
        using var binding = new RenderPackSelectionBinding(settings, controller);

        GraphicalPluginSession first = StartSession(
            paths,
            Path.Combine(temporary.Path, "live-v1-status.jsonl"),
            registry);
        Assert.Equal(
            RenderPackActivationState.Active,
            binding.ApplyAtFrameBoundary(Extent).State);
        WeakReference firstAssets = CaptureAssetWeakReference(registry);
        WeakReference firstContext = Assert.Single(first.CaptureLoadContextWeakReferences());

        // No settings write/request accompanies uninstall. The registry event
        // alone must retire the active production runtime at the next frame.
        first.Dispose();
        RenderPackActivationSnapshot withdrawn = binding.ApplyAtFrameBoundary(Extent);
        Assert.Equal(RenderPackActivationState.FailedToRetail, withdrawn.State);
        Assert.Contains("was withdrawn", withdrawn.Reason, StringComparison.Ordinal);
        Assert.True(settings.Display.RenderPack.IsRetail);
        Assert.Null(controller.ActiveRuntime);
        Assert.Empty(registry.Snapshot());
        Collect(firstAssets);
        Collect(firstContext);

        GraphicalPluginSession corrected = StartSession(
            paths,
            Path.Combine(temporary.Path, "live-corrected-status.jsonl"),
            registry);
        long correctedRegistration = CaptureRegistrationId(registry);
        Assert.True(correctedRegistration > 1);
        settings.SaveDisplay(settings.Display with { RenderPack = selected });
        RenderPackActivationSnapshot recovered = binding.ApplyAtFrameBoundary(Extent);
        Assert.Equal(RenderPackActivationState.Active, recovered.State);
        Assert.Equal(selected, recovered.Selection);
        Assert.Equal(2, factory.BuildCount);

        WeakReference correctedAssets = CaptureAssetWeakReference(registry);
        WeakReference correctedContext = Assert.Single(
            corrected.CaptureLoadContextWeakReferences());
        corrected.Dispose();
        Assert.Equal(
            RenderPackActivationState.FailedToRetail,
            binding.ApplyAtFrameBoundary(Extent).State);
        binding.Dispose();
        controller.Dispose();
        Collect(correctedAssets);
        Collect(correctedContext);
        AssertZeroGpuPackResources(device);
    }

    [Fact]
    public void PackageSelectionUpdateWithdrawalAndPersistenceConvergeWithoutPackResources()
    {
        using var temporary = new TemporaryDirectory();
        ApplicationPathSet paths = Paths(temporary.Path);
        string packageDirectory = InstallPackage(paths.PluginsDirectory, new Version(1, 0, 0));
        string settingsPath = Path.Combine(paths.ConfigDirectory, "settings.json");
        Directory.CreateDirectory(paths.ConfigDirectory);
        var persistedV1 = new RenderPackSelectionSettings(PackId, "1.0.0", "low");
        new JsonRuntimeSettingsStorage(settingsPath).SaveDisplay(
            DisplaySettings.Default with { RenderPack = persistedV1 });

        using var registry = new BufferedRenderPackRegistry();
        using var device = new RecordingGpuDevice();
        LifetimeReferences v1 = RunV1Lifecycle(
            paths,
            temporary.Path,
            settingsPath,
            persistedV1,
            registry,
            device);
        Collect(v1.Assets);
        Collect(v1.Context);

        WritePackageVersion(packageDirectory, new Version(2, 0, 0));
        WriteManifest(packageDirectory, new Version(2, 0, 0));
        new JsonRuntimeSettingsStorage(settingsPath).SaveDisplay(
            DisplaySettings.Default with { RenderPack = persistedV1 });

        LifetimeReferences v2 = RunV2Lifecycle(
            paths,
            temporary.Path,
            settingsPath,
            persistedV1,
            registry,
            device);
        Collect(v2.Assets);
        Collect(v2.Context);

        Assert.Empty(registry.Snapshot());
        AssertZeroGpuPackResources(device);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LifetimeReferences RunV1Lifecycle(
        ApplicationPathSet paths,
        string root,
        string settingsPath,
        RenderPackSelectionSettings persistedV1,
        BufferedRenderPackRegistry registry,
        RecordingGpuDevice device)
    {
        var factory = new CountingFactory(device);
        using var controller = Controller(registry, factory);
        var settings = new RuntimeSettingsController(
            new JsonRuntimeSettingsStorage(settingsPath),
            log: static _ => { });
        using var binding = new RenderPackSelectionBinding(settings, controller);
        GraphicalPluginSession session = StartSession(
            paths,
            Path.Combine(root, "v1-status.jsonl"),
            registry);
        Assert.Equal(1, session.LoadedCount);
        AssertRegisteredVersion(registry, new Version(1, 0, 0));
        WeakReference assets = CaptureAssetWeakReference(registry);

        RenderPackActivationSnapshot active = binding.ApplyAtFrameBoundary(Extent);
        Assert.Equal(RenderPackActivationState.Active, active.State);
        Assert.Equal(persistedV1, active.Selection);
        Assert.IsAssignableFrom<IDefaultWorldPathRenderPackRuntime>(controller.ActiveRuntime);
        Assert.Equal(1, factory.BuildCount);
        AssertZeroGpuPackResources(device);

        WeakReference context = Assert.Single(session.CaptureLoadContextWeakReferences());
        session.Dispose();
        Assert.Empty(registry.Snapshot());
        settings.SaveDisplay(settings.Display with
        {
            RenderPack = RenderPackSelectionSettings.Retail,
        });
        Assert.Equal(
            RenderPackActivationState.Retail,
            binding.ApplyAtFrameBoundary(Extent).State);
        Assert.Null(controller.ActiveRuntime);

        settings.SaveDisplay(settings.Display with { RenderPack = persistedV1 });
        RenderPackActivationSnapshot removed = binding.ApplyAtFrameBoundary(Extent);
        Assert.Equal(RenderPackActivationState.FailedToRetail, removed.State);
        Assert.Contains("is not installed", removed.Reason, StringComparison.Ordinal);
        Assert.True(settings.Display.RenderPack.IsRetail);
        Assert.True(
            new JsonRuntimeSettingsStorage(settingsPath).LoadDisplay().RenderPack.IsRetail);
        Assert.Equal(1, factory.BuildCount);

        controller.Request(persistedV1);
        RenderPackActivationSnapshot latched = controller.ApplyAtFrameBoundary(Extent);
        Assert.Equal(RenderPackActivationState.FailedToRetail, latched.State);
        Assert.Contains(
            "failed for the current registration",
            latched.Reason,
            StringComparison.Ordinal);
        Assert.Equal(1, factory.BuildCount);
        return new LifetimeReferences(context, assets);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LifetimeReferences RunV2Lifecycle(
        ApplicationPathSet paths,
        string root,
        string settingsPath,
        RenderPackSelectionSettings persistedV1,
        BufferedRenderPackRegistry registry,
        RecordingGpuDevice device)
    {
        var factory = new CountingFactory(device);
        using var controller = Controller(registry, factory);
        GraphicalPluginSession session = StartSession(
            paths,
            Path.Combine(root, "v2-status.jsonl"),
            registry);
        Assert.Equal(1, session.LoadedCount);
        AssertRegisteredVersion(registry, new Version(2, 0, 0));
        WeakReference assets = CaptureAssetWeakReference(registry);
        var settings = new RuntimeSettingsController(
            new JsonRuntimeSettingsStorage(settingsPath),
            log: static _ => { });
        using var binding = new RenderPackSelectionBinding(settings, controller);

        RenderPackActivationSnapshot stale = binding.ApplyAtFrameBoundary(Extent);
        Assert.Equal(RenderPackActivationState.FailedToRetail, stale.State);
        Assert.Equal(
            "Render pack 'acdream.test.external-render-pack' version 1.0.0 was selected, "
                + "but version 2.0.0 is installed.",
            stale.Reason);
        Assert.True(settings.Display.RenderPack.IsRetail);
        Assert.Equal(0, factory.BuildCount);

        controller.Request(persistedV1);
        RenderPackActivationSnapshot noRetry = controller.ApplyAtFrameBoundary(Extent);
        Assert.Equal(RenderPackActivationState.FailedToRetail, noRetry.State);
        Assert.Contains("will not be retried", noRetry.Reason, StringComparison.Ordinal);
        Assert.Equal(0, factory.BuildCount);

        var selectedV2 = new RenderPackSelectionSettings(PackId, "2.0.0", "high");
        settings.SaveDisplay(settings.Display with { RenderPack = selectedV2 });
        RenderPackActivationSnapshot updated = binding.ApplyAtFrameBoundary(Extent);
        Assert.Equal(RenderPackActivationState.Active, updated.State);
        Assert.Equal(selectedV2, updated.Selection);
        Assert.Equal("high", controller.ActiveRuntime!.Preset.Id);
        Assert.Equal(1, factory.BuildCount);
        Assert.Equal(
            selectedV2,
            new JsonRuntimeSettingsStorage(settingsPath).LoadDisplay().RenderPack);
        AssertZeroGpuPackResources(device);

        WeakReference context = Assert.Single(session.CaptureLoadContextWeakReferences());
        session.Dispose();
        Assert.Empty(registry.Snapshot());
        settings.SaveDisplay(settings.Display with
        {
            RenderPack = RenderPackSelectionSettings.Retail,
        });
        Assert.Equal(
            RenderPackActivationState.Retail,
            binding.ApplyAtFrameBoundary(Extent).State);
        Assert.Null(controller.ActiveRuntime);
        return new LifetimeReferences(context, assets);
    }

    [Fact]
    public void MalformedThenFailingPackageCanBeCorrectedWithoutLeakingRegistrationOrAlc()
    {
        using var temporary = new TemporaryDirectory();
        ApplicationPathSet paths = Paths(temporary.Path);
        string packageDirectory = InstallPackage(paths.PluginsDirectory, new Version(1, 0, 0));
        string manifestPath = Path.Combine(packageDirectory, "plugin.json");
        File.WriteAllText(manifestPath, "{ this is not a plugin manifest }");
        using var registry = new BufferedRenderPackRegistry();

        GraphicalPluginSession malformed = StartSession(
            paths,
            Path.Combine(temporary.Path, "malformed-status.jsonl"),
            registry);
        Assert.Equal(0, malformed.LoadedCount);
        Assert.Empty(registry.Snapshot());
        Assert.Empty(malformed.CaptureLoadContextWeakReferences());
        JsonElement malformedFailure = Assert.Single(ReadStatuses(
            Path.Combine(temporary.Path, "malformed-status.jsonl")),
            value => value.GetProperty("e").GetString() == "pluginFailed");
        Assert.Contains(
            "invalid start of a property name",
            malformedFailure.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        malformed.Dispose();

        WriteManifest(packageDirectory, new Version(1, 0, 0));
        string failureMarker = Path.Combine(
            packageDirectory,
            "throw-after-render-pack-register");
        File.WriteAllText(failureMarker, string.Empty);
        GraphicalPluginSession failing = StartSession(
            paths,
            Path.Combine(temporary.Path, "failing-status.jsonl"),
            registry);
        Assert.Equal(0, failing.LoadedCount);
        Assert.Empty(registry.Snapshot());
        WeakReference failedContext = Assert.Single(
            failing.CaptureLoadContextWeakReferences());
        Assert.Contains(
            "failed after publishing its descriptor",
            Assert.Single(ReadStatuses(
                    Path.Combine(temporary.Path, "failing-status.jsonl")),
                value => value.GetProperty("e").GetString() == "pluginFailed")
                .GetProperty("error").GetString(),
            StringComparison.Ordinal);
        failing.Dispose();
        Collect(failedContext);

        File.Delete(failureMarker);
        string zeroMarker = Path.Combine(packageDirectory, "register-no-render-packs");
        File.WriteAllText(zeroMarker, string.Empty);
        GraphicalPluginSession zeroRegistration = StartSession(
            paths,
            Path.Combine(temporary.Path, "zero-registration-status.jsonl"),
            registry);
        Assert.Equal(0, zeroRegistration.LoadedCount);
        Assert.Empty(registry.Snapshot());
        WeakReference zeroContext = Assert.Single(
            zeroRegistration.CaptureLoadContextWeakReferences());
        Assert.Contains(
            "registered no packs",
            Assert.Single(ReadStatuses(
                    Path.Combine(temporary.Path, "zero-registration-status.jsonl")),
                value => value.GetProperty("e").GetString() == "pluginFailed")
                .GetProperty("error").GetString(),
            StringComparison.Ordinal);
        zeroRegistration.Dispose();
        Collect(zeroContext);

        File.Delete(zeroMarker);
        GraphicalPluginSession corrected = StartSession(
            paths,
            Path.Combine(temporary.Path, "corrected-status.jsonl"),
            registry);
        Assert.Equal(1, corrected.LoadedCount);
        AssertCompatibleRegistration(registry);
        WeakReference correctedAssets = CaptureAssetWeakReference(registry);

        WeakReference correctedContext = Assert.Single(
            corrected.CaptureLoadContextWeakReferences());
        corrected.Dispose();
        Assert.Empty(registry.Snapshot());
        Collect(correctedAssets);
        Collect(correctedContext);
    }

    [Theory]
    [InlineData(
        "AcDream.Plugin.Tests.Fixtures.InvalidRenderPackMultiple",
        "must contain exactly one IRenderPackPlugin implementation")]
    [InlineData(
        "AcDream.Plugin.Tests.Fixtures.InvalidRenderPackInternal",
        "must be public")]
    public void InvalidRenderPackEntrypointShapeRollsBackCollectiblePackage(
        string fixtureName,
        string expectedFailure)
    {
        using var temporary = new TemporaryDirectory();
        ApplicationPathSet paths = Paths(temporary.Path);
        string packageId = "acdream.test.invalid-render-pack";
        string source = FixtureAssemblyPath(fixtureName);
        string packageDirectory = Path.Combine(paths.PluginsDirectory, packageId);
        Directory.CreateDirectory(packageDirectory);
        File.Copy(source, Path.Combine(packageDirectory, Path.GetFileName(source)));
        File.WriteAllText(
            Path.Combine(packageDirectory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = packageId,
                displayName = "Invalid render-pack entry fixture",
                version = "1.0.0",
                entryDll = Path.GetFileName(source),
                apiVersion = 1,
                kinds = new[] { "RenderPack" },
            }));
        using var registry = new BufferedRenderPackRegistry();
        string statusPath = Path.Combine(temporary.Path, fixtureName + ".jsonl");

        GraphicalPluginSession session = StartSession(
            paths,
            statusPath,
            registry,
            packageId);

        Assert.Equal(0, session.LoadedCount);
        Assert.Empty(registry.Snapshot());
        Assert.Contains(
            expectedFailure,
            Assert.Single(ReadStatuses(statusPath),
                    value => value.GetProperty("e").GetString() == "pluginFailed")
                .GetProperty("error").GetString(),
            StringComparison.Ordinal);
        WeakReference context = Assert.Single(session.CaptureLoadContextWeakReferences());
        session.Dispose();
        Collect(context);
    }

    private static RenderPackController Controller(
        BufferedRenderPackRegistry registry,
        IRenderPackRuntimeFactory factory)
    {
        var source = new RenderPackCatalogSource(
            registry,
            RenderPackHostCapabilities.Conformance);
        return new RenderPackController(
            source.Snapshot,
            factory,
            preparationScheduler: InlineRenderPackPreparationScheduler.Instance,
            catalogSource: source);
    }

    private static GraphicalPluginSession StartSession(
        ApplicationPathSet paths,
        string statusPath,
        BufferedRenderPackRegistry registry,
        string packageId = PackageId)
    {
        var host = new AppPluginHost(
            new NullLogger(),
            new WorldGameState(),
            new WorldEvents(),
            new SelectionState(),
            new BufferedUiRegistry(),
            NoOpAutomationSurface.Instance);
        var session = GraphicalPluginSession.Create(
            paths,
            [packageId],
            "external-render-pack-test",
            host,
            new SessionStatusWriter(statusPath),
            registry);
        session.Start();
        return session;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertRegisteredVersion(
        BufferedRenderPackRegistry registry,
        Version expected)
    {
        BufferedRenderPackRegistration registration = Assert.Single(registry.Snapshot());
        Assert.Equal(PackId, registration.Descriptor.Id);
        Assert.Equal(expected, registration.Descriptor.PackVersion);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureAssetWeakReference(
        BufferedRenderPackRegistry registry) =>
        new(Assert.Single(registry.Snapshot()).Assets);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long CaptureRegistrationId(BufferedRenderPackRegistry registry) =>
        Assert.Single(registry.Snapshot()).RegistrationId;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCompatibleRegistration(BufferedRenderPackRegistry registry)
    {
        BufferedRenderPackRegistration registration = Assert.Single(registry.Snapshot());
        Assert.Equal(PackId, registration.Descriptor.Id);
        Assert.True(RenderPackCatalog.Build(
            registry.Snapshot(),
            RenderPackHostCapabilities.Conformance).TryGet(
                PackId,
                out RenderPackCatalogEntry catalogEntry));
        Assert.True(catalogEntry.IsCompatible, catalogEntry.IncompatibilityReason);
    }

    private static ApplicationPathSet Paths(string root) => new(
        Path.Combine(root, "config"),
        Path.Combine(root, "data"),
        Path.Combine(root, "cache"),
        LegacyConfigDirectory: null);

    private static string InstallPackage(string root, Version version)
    {
        string source = FixtureAssemblyPath();
        string packageDirectory = Path.Combine(root, PackageId);
        Directory.CreateDirectory(packageDirectory);
        File.Copy(source, Path.Combine(packageDirectory, Path.GetFileName(source)));
        WritePackageVersion(packageDirectory, version);
        WriteManifest(packageDirectory, version);
        return packageDirectory;
    }

    private static void WritePackageVersion(string directory, Version version) =>
        File.WriteAllText(
            Path.Combine(directory, "render-pack-version.txt"),
            version.ToString());

    private static void WriteManifest(string directory, Version version) =>
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = PackageId,
                displayName = "External render-pack package fixture",
                version = version.ToString(),
                entryDll = Path.GetFileName(FixtureAssemblyPath()),
                apiVersion = 1,
                kinds = new[] { "RenderPack" },
            }));

    private static string FixtureAssemblyPath()
    {
        const string projectName = "AcDream.Plugin.Tests.Fixtures.HostPlugin";
        string colocated = Path.Combine(AppContext.BaseDirectory, projectName + ".dll");
        if (File.Exists(colocated))
            return colocated;

        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!.Name;
        return Path.Combine(
            FindRepoRoot(),
            "tests",
            projectName,
            "bin",
            configuration,
            "net10.0",
            projectName + ".dll");
    }

    private static string FixtureAssemblyPath(string projectName)
    {
        string colocated = Path.Combine(AppContext.BaseDirectory, projectName + ".dll");
        if (File.Exists(colocated))
            return colocated;

        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!.Name;
        return Path.Combine(
            FindRepoRoot(),
            "tests",
            projectName,
            "bin",
            configuration,
            "net10.0",
            projectName + ".dll");
    }

    private static JsonElement[] ReadStatuses(string path) =>
        File.ReadAllLines(path)
            .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static void AssertZeroGpuPackResources(RecordingGpuDevice device)
    {
        Assert.Empty(device.CreatedBuffers);
        Assert.Empty(device.CreatedPipelines);
        Assert.Empty(device.CreatedTextures);
        Assert.Empty(device.CreatedRenderTargets);
        Assert.Empty(device.CreatedDirectionalDepthTargets);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect(WeakReference context)
    {
        for (int attempt = 0; attempt < 12 && context.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(context.IsAlive);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string FindRepoRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("ACDREAM_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)
            && File.Exists(Path.Combine(configured, "AcDream.slnx")))
        {
            return Path.GetFullPath(configured);
        }
        foreach (string start in new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class CountingFactory(IGpuDevice device) : IRenderPackRuntimeFactory
    {
        private readonly AtmosphericRenderPackRuntimeFactory _inner = new(device);

        internal int BuildCount { get; private set; }

        public IRenderPackRuntime Build(
            RenderPackDescriptor descriptor,
            ValidatedRenderPackShaderAssets assets,
            RenderQualityPreset preset,
            IReadOnlyDictionary<string, string> userSettingOverrides)
        {
            BuildCount++;
            return _inner.Build(descriptor, assets, preset, userSettingOverrides);
        }
    }

    private sealed class NullLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private readonly record struct LifetimeReferences(
        WeakReference Context,
        WeakReference Assets);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-external-pack-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            for (int attempt = 0; Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (Exception error)
                    when (error is IOException or UnauthorizedAccessException
                        && attempt < 249)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Thread.Sleep(20);
                }
            }
        }
    }
}

using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class GameWindowLifetimeTests
{
    [Fact]
    public void CleanCompletionReleasesNativeWindowLastAndTerminalCallsAreInert()
    {
        var calls = new List<string>();
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("owners",
            [
                new("owner", () => calls.Add("owner")),
            ]));
        var lifetime = new GameWindowLifetime(() => transaction);
        var native = new RecordingDisposable("native", calls);
        lifetime.PublishNativeWindow(native);

        GameWindowLifetimeReport closing = lifetime.TryComplete();
        Assert.Equal(GameWindowLifetimeStatus.Complete, closing.Status);
        Assert.Equal(["owner"], calls);

        GameWindowLifetimeReport disposed = lifetime.CompleteAndReleaseNativeWindow();
        GameWindowLifetimeReport repeated = lifetime.CompleteAndReleaseNativeWindow();

        Assert.Same(closing, disposed);
        Assert.Same(disposed, repeated);
        Assert.Equal(["owner", "native"], calls);
        Assert.Equal(1, native.DisposeCalls);
        Assert.True(lifetime.HasShutdownRoots);
        Assert.False(lifetime.RetainsShutdownGraph);
    }

    [Fact]
    public void ArmedRenderLoopDefersNativeReleaseInsteadOfDisposing()
    {
        var calls = new List<string>();
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("owners",
            [
                new("owner", () => calls.Add("owner")),
            ]));
        var lifetime = new GameWindowLifetime(() => transaction);
        var native = new RecordingDisposable("native", calls);
        bool closeRequested = false;
        lifetime.PublishNativeWindow(
            native,
            isRenderLoopArmed: () => true,
            requestClose: () => closeRequested = true);

        GameWindowLifetimeReport report = lifetime.CompleteAndReleaseNativeWindow();

        Assert.Equal(GameWindowLifetimeStatus.CompleteWithDeferredNativeRelease, report.Status);
        Assert.Equal("native window", report.BlockedStage);
        Assert.Null(report.Error);
        Assert.Empty(report.CleanupFailures);
        Assert.Equal(["owner"], calls);
        Assert.Equal(0, native.DisposeCalls);
        Assert.True(closeRequested);
        Assert.True(lifetime.RetainsShutdownGraph);
        Assert.True(report.IsTerminal);

        GameWindowLifetimeReport repeated = lifetime.CompleteAndReleaseNativeWindow();
        Assert.Same(report, repeated);
        Assert.Equal(0, native.DisposeCalls);
    }

    [Fact]
    public void UnarmedRenderLoopStillDisposesNativeWindowNormally()
    {
        var calls = new List<string>();
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("owners",
            [
                new("owner", () => calls.Add("owner")),
            ]));
        var lifetime = new GameWindowLifetime(() => transaction);
        var native = new RecordingDisposable("native", calls);
        bool closeRequested = false;
        lifetime.PublishNativeWindow(
            native,
            isRenderLoopArmed: () => false,
            requestClose: () => closeRequested = true);

        GameWindowLifetimeReport report = lifetime.CompleteAndReleaseNativeWindow();

        Assert.Equal(GameWindowLifetimeStatus.Complete, report.Status);
        Assert.Null(report.BlockedStage);
        Assert.Equal(["owner", "native"], calls);
        Assert.Equal(1, native.DisposeCalls);
        Assert.False(closeRequested);
        Assert.False(lifetime.RetainsShutdownGraph);
    }

    [Fact]
    public void HardBarrierFailureRetriesWithoutReplayThenCompletes()
    {
        int stableCalls = 0;
        int pendingCalls = 0;
        bool allowPending = false;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("session barrier",
            [
                new("stable", () => stableCalls++),
                new("pending", () =>
                {
                    pendingCalls++;
                    if (!allowPending)
                        throw new InvalidOperationException("pending");
                }),
            ]));
        var lifetime = new GameWindowLifetime(() => transaction);

        GameWindowLifetimeReport first = lifetime.TryComplete();
        Assert.Equal(GameWindowLifetimeStatus.RetryableIncomplete, first.Status);
        Assert.Equal("session barrier", first.BlockedStage);
        int failedAttempts = pendingCalls;
        allowPending = true;

        GameWindowLifetimeReport second = lifetime.CompleteAndReleaseNativeWindow();

        Assert.Equal(GameWindowLifetimeStatus.Complete, second.Status);
        Assert.Equal(1, stableCalls);
        Assert.Equal(failedAttempts + 1, pendingCalls);
    }

    [Fact]
    public void PersistentSoftFailureCompletesAndRetainsStructuredReport()
    {
        int dependentCalls = 0;
        var physicalFailure = new InvalidOperationException("remove failed");
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("physical ingress cleanup",
            [
                new(
                    "mouse",
                    () => throw physicalFailure,
                    ResourceShutdownOperationPolicy.ReportAndContinue),
            ]),
            new ResourceShutdownStage("dependents",
            [
                new("dependent", () => dependentCalls++),
            ]));
        var lifetime = new GameWindowLifetime(() => transaction);

        GameWindowLifetimeReport report = lifetime.CompleteAndReleaseNativeWindow();

        Assert.Equal(GameWindowLifetimeStatus.CompleteWithCleanupFailures, report.Status);
        Assert.Equal(1, dependentCalls);
        ResourceShutdownCleanupFailure cleanup = Assert.Single(report.CleanupFailures);
        Assert.Equal("physical ingress cleanup", cleanup.Stage);
        Assert.Equal("mouse", cleanup.Operation);
        Assert.Same(physicalFailure, cleanup.Error);
        Assert.Same(report, lifetime.CompleteAndReleaseNativeWindow());
        Assert.False(lifetime.RetainsShutdownGraph);
    }

    [Fact]
    public void PersistentHardFailureUsesNativeFallbackAndBecomesTerminal()
    {
        var calls = new List<string>();
        int attempts = 0;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("GPU barrier",
            [
                new("drain", () =>
                {
                    attempts++;
                    throw new InvalidOperationException("driver wedged");
                }),
            ]),
            new ResourceShutdownStage("must stay protected",
            [
                new("dependent", () => calls.Add("dependent")),
            ]));
        var lifetime = new GameWindowLifetime(() => transaction);
        var native = new RecordingDisposable("native", calls);
        lifetime.PublishNativeWindow(native);

        GameWindowLifetimeReport report = lifetime.CompleteAndReleaseNativeWindow();
        int terminalAttempts = attempts;

        Assert.Equal(GameWindowLifetimeStatus.AbandonedIncomplete, report.Status);
        Assert.Equal("GPU barrier", report.BlockedStage);
        Assert.NotNull(report.Error);
        Assert.Equal(["native"], calls);
        Assert.Same(report, lifetime.TryComplete());
        Assert.Same(report, lifetime.CompleteAndReleaseNativeWindow());
        Assert.Equal(terminalAttempts, attempts);
        Assert.Equal(1, native.DisposeCalls);
        Assert.True(lifetime.RetainsShutdownGraph);
    }

    [Fact]
    public void NativeReleaseFailureIsTerminalAndNeverRetried()
    {
        var transaction = new ResourceShutdownTransaction();
        var lifetime = new GameWindowLifetime(() => transaction);
        var native = new ThrowingDisposable();
        lifetime.PublishNativeWindow(native);

        GameWindowLifetimeReport report = lifetime.CompleteAndReleaseNativeWindow();

        Assert.Equal(GameWindowLifetimeStatus.AbandonedIncomplete, report.Status);
        Assert.Equal("native window", report.BlockedStage);
        Assert.Equal(1, native.DisposeCalls);
        Assert.Same(report, lifetime.CompleteAndReleaseNativeWindow());
        Assert.Equal(1, native.DisposeCalls);
        Assert.True(lifetime.RetainsShutdownGraph);
    }

    [Fact]
    public void ReentrantCompletionDoesNotAbandonOrReplayOuterProgress()
    {
        GameWindowLifetime? lifetime = null;
        GameWindowLifetimeReport? nested = null;
        int calls = 0;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("owner",
            [
                new("reentrant", () =>
                {
                    calls++;
                    nested = lifetime!.CompleteAndReleaseNativeWindow();
                }),
            ]));
        lifetime = new GameWindowLifetime(() => transaction);

        GameWindowLifetimeReport outer = lifetime.TryComplete();

        Assert.NotNull(nested);
        Assert.Equal(GameWindowLifetimeStatus.Active, nested.Status);
        Assert.Equal(GameWindowLifetimeStatus.Complete, outer.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ConstructedNeverRunCompletesWithoutNativeWindow()
    {
        var lifetime = new GameWindowLifetime(
            static () => new ResourceShutdownTransaction());

        GameWindowLifetimeReport report = lifetime.CompleteAndReleaseNativeWindow();

        Assert.Equal(GameWindowLifetimeStatus.Complete, report.Status);
        Assert.Same(report, lifetime.CompleteAndReleaseNativeWindow());
        Assert.False(lifetime.RetainsShutdownGraph);
    }

    private sealed class RecordingDisposable(string name, List<string> calls)
        : IDisposable
    {
        public int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
            calls.Add(name);
        }
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
            throw new InvalidOperationException("native failed");
        }
    }
}

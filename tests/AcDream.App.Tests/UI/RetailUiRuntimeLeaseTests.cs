using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class RetailUiRuntimeLeaseTests
{
    [Fact]
    public void ConstructorFailureLeavesHostOwnedUntilLifetimeDisposal()
    {
        var lease = new RetailUiRuntimeLease();
        var host = AcquireHost(lease);

        Assert.Throws<InvalidOperationException>(() =>
            lease.MountCore<FakeRuntime>(
                () => throw new InvalidOperationException("constructor failed"),
                static runtime => runtime.Initialize(),
                static runtime => runtime.Dispose(),
                static runtime => runtime.IsDisposalComplete));

        Assert.True(lease.RetainsResources);
        Assert.Equal(0, host.DisposeCalls);

        lease.Dispose();
        lease.Dispose();

        Assert.True(lease.IsDisposalComplete);
        Assert.False(lease.RetainsResources);
        Assert.Equal(1, host.DisposeCalls);
    }

    [Fact]
    public void InitializeFailureCleansRuntimeAndHostExactlyOnce()
    {
        var lease = new RetailUiRuntimeLease();
        var host = AcquireHost(lease);
        var runtime = new FakeRuntime(host) { InitializeFailure = true };

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            Mount(lease, runtime));

        Assert.Equal("initialize failed", failure.Message);
        Assert.True(lease.IsDisposalComplete);
        Assert.False(lease.RetainsResources);
        Assert.Equal(1, runtime.DisposeCalls);
        Assert.Equal(1, host.DisposeCalls);
    }

    [Fact]
    public void TransientInitializeCleanupFailureRetainsOwnershipForLifetimeRetry()
    {
        var lease = new RetailUiRuntimeLease();
        var host = AcquireHost(lease);
        var runtime = new FakeRuntime(host)
        {
            InitializeFailure = true,
            RemainingDisposeFailures = 1,
        };

        AggregateException failure = Assert.Throws<AggregateException>(() =>
            Mount(lease, runtime));

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.True(lease.RetainsResources);
        Assert.False(lease.IsDisposalComplete);
        Assert.Equal(1, runtime.DisposeCalls);
        Assert.Equal(0, host.DisposeCalls);

        lease.Dispose();

        Assert.True(lease.IsDisposalComplete);
        Assert.False(lease.RetainsResources);
        Assert.Equal(2, runtime.DisposeCalls);
        Assert.Equal(1, host.DisposeCalls);
    }

    [Fact]
    public void PersistentCleanupFailureCanBeExplicitlyAbandonedAndBecomesInert()
    {
        var lease = new RetailUiRuntimeLease();
        var host = AcquireHost(lease);
        var runtime = new FakeRuntime(host)
        {
            InitializeFailure = true,
            RemainingDisposeFailures = int.MaxValue,
        };

        Assert.Throws<AggregateException>(() => Mount(lease, runtime));
        Assert.Throws<InvalidOperationException>(lease.Dispose);
        Assert.Throws<InvalidOperationException>(() =>
            new RetailUiRuntimeLease().AbandonAfterTerminalFailure());

        lease.AbandonAfterTerminalFailure();
        lease.Dispose();
        lease.QuiesceInput();
        lease.DeactivateInput();

        Assert.True(lease.IsAbandoned);
        Assert.False(lease.IsDisposalComplete);
        Assert.True(lease.RetainsResources);
        Assert.Equal(2, runtime.DisposeCalls);
        Assert.Equal(0, host.DisposeCalls);
    }

    [Fact]
    public void QuiesceAndDeactivateAreSeparateIdempotentOperations()
    {
        var lease = new RetailUiRuntimeLease();
        var host = AcquireHost(lease);

        lease.QuiesceInput();
        lease.QuiesceInput();
        lease.DeactivateInput();
        lease.DeactivateInput();

        Assert.Equal(1, host.QuiesceCalls);
        Assert.Equal(1, host.DeactivateCalls);
        Assert.Equal(0, host.DisposeCalls);

        lease.Dispose();

        Assert.Equal(1, host.DisposeCalls);
    }

    [Fact]
    public void HostAndRuntimeMayOnlyBePublishedOnce()
    {
        var lease = new RetailUiRuntimeLease();
        var host = AcquireHost(lease);
        var runtime = new FakeRuntime(host);

        Assert.Throws<InvalidOperationException>(() => AcquireHost(lease));
        Assert.Same(runtime, Mount(lease, runtime));
        Assert.Throws<InvalidOperationException>(() =>
            Mount(lease, new FakeRuntime(host)));

        lease.Dispose();
    }

    [Fact]
    public void ReentrantHostAndRuntimeFactoriesCannotOverwriteOuterOwnership()
    {
        var lease = new RetailUiRuntimeLease();
        var host = new FakeHost();
        int nestedHosts = 0;
        FakeHost acquired = lease.AcquireHostCore(
            () =>
            {
                Assert.Throws<InvalidOperationException>(() =>
                    lease.AcquireHostCore(
                        () =>
                        {
                            nestedHosts++;
                            return new FakeHost();
                        },
                        static value => value.QuiesceInput(),
                        static value => value.DeactivateInput(),
                        static value => value.Dispose(),
                        static value => value.IsDisposalComplete));
                return host;
            },
            static value => value.QuiesceInput(),
            static value => value.DeactivateInput(),
            static value => value.Dispose(),
            static value => value.IsDisposalComplete);
        var runtime = new FakeRuntime(host);
        int nestedRuntimes = 0;

        FakeRuntime mounted = lease.MountCore(
            () =>
            {
                Assert.Throws<InvalidOperationException>(() =>
                    lease.MountCore(
                        () =>
                        {
                            nestedRuntimes++;
                            return new FakeRuntime(host);
                        },
                        static value => value.Initialize(),
                        static value => value.Dispose(),
                        static value => value.IsDisposalComplete));
                return runtime;
            },
            static value => value.Initialize(),
            static value => value.Dispose(),
            static value => value.IsDisposalComplete);

        Assert.Same(host, acquired);
        Assert.Same(runtime, mounted);
        Assert.Equal(0, nestedHosts);
        Assert.Equal(0, nestedRuntimes);
        lease.Dispose();
    }

    [Fact]
    public void ReentrantDisposeIsRejectedWithoutDoubleDisposingRuntimeOrHost()
    {
        var lease = new RetailUiRuntimeLease();
        var host = AcquireHost(lease);
        var runtime = new FakeRuntime(host)
        {
            OnDispose = () =>
                Assert.Throws<InvalidOperationException>(lease.Dispose),
        };
        Mount(lease, runtime);

        lease.Dispose();

        Assert.Equal(1, runtime.DisposeCalls);
        Assert.Equal(1, host.DisposeCalls);
        Assert.True(lease.IsDisposalComplete);
    }

    [Fact]
    public void HostFactoryCannotPrematurelyCompleteInputCutoffOperations()
    {
        var lease = new RetailUiRuntimeLease();
        var host = new FakeHost();

        lease.AcquireHostCore(
            () =>
            {
                Assert.Throws<InvalidOperationException>(lease.QuiesceInput);
                Assert.Throws<InvalidOperationException>(lease.DeactivateInput);
                return host;
            },
            static value => value.QuiesceInput(),
            static value => value.DeactivateInput(),
            static value => value.Dispose(),
            static value => value.IsDisposalComplete);

        lease.QuiesceInput();
        lease.DeactivateInput();

        Assert.Equal(1, host.QuiesceCalls);
        Assert.Equal(1, host.DeactivateCalls);
        lease.Dispose();
    }

    [Fact]
    public void InputCutoffRejectsReentrantMutationAndRetriesFailedCallback()
    {
        var lease = new RetailUiRuntimeLease();
        var host = new FakeHost();
        int quiesceAttempts = 0;
        int deactivateAttempts = 0;
        lease.AcquireHostCore(
            () => host,
            _ =>
            {
                quiesceAttempts++;
                Assert.Throws<InvalidOperationException>(() =>
                    lease.MountCore(
                        () => new FakeRuntime(host),
                        static value => value.Initialize(),
                        static value => value.Dispose(),
                        static value => value.IsDisposalComplete));
                if (quiesceAttempts == 1)
                    throw new InvalidOperationException("quiesce failed");
                host.QuiesceInput();
            },
            _ =>
            {
                deactivateAttempts++;
                Assert.Throws<InvalidOperationException>(lease.Dispose);
                if (deactivateAttempts == 1)
                    throw new InvalidOperationException("deactivate failed");
                host.DeactivateInput();
            },
            static value => value.Dispose(),
            static value => value.IsDisposalComplete);

        Assert.Throws<InvalidOperationException>(lease.QuiesceInput);
        lease.QuiesceInput();
        Assert.Throws<InvalidOperationException>(lease.DeactivateInput);
        lease.DeactivateInput();

        Assert.Equal(2, quiesceAttempts);
        Assert.Equal(2, deactivateAttempts);
        Assert.Equal(1, host.QuiesceCalls);
        Assert.Equal(1, host.DeactivateCalls);
        lease.Dispose();
    }

    private static FakeHost AcquireHost(RetailUiRuntimeLease lease)
    {
        var host = new FakeHost();
        return lease.AcquireHostCore(
            () => host,
            static value => value.QuiesceInput(),
            static value => value.DeactivateInput(),
            static value => value.Dispose(),
            static value => value.IsDisposalComplete);
    }

    private static FakeRuntime Mount(
        RetailUiRuntimeLease lease,
        FakeRuntime runtime) => lease.MountCore(
            () => runtime,
            static value => value.Initialize(),
            static value => value.Dispose(),
            static value => value.IsDisposalComplete);

    private sealed class FakeHost : IDisposable
    {
        public int QuiesceCalls { get; private set; }
        public int DeactivateCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool IsDisposalComplete { get; private set; }

        public void QuiesceInput() => QuiesceCalls++;

        public void DeactivateInput() => DeactivateCalls++;

        public void Dispose()
        {
            if (IsDisposalComplete)
                return;

            DisposeCalls++;
            IsDisposalComplete = true;
        }
    }

    private sealed class FakeRuntime(FakeHost host) : IDisposable
    {
        public bool InitializeFailure { get; init; }
        public int RemainingDisposeFailures { get; set; }
        public int DisposeCalls { get; private set; }
        public bool IsDisposalComplete { get; private set; }
        public Action? OnDispose { get; init; }

        public void Initialize()
        {
            if (InitializeFailure)
                throw new InvalidOperationException("initialize failed");
        }

        public void Dispose()
        {
            if (IsDisposalComplete)
                return;

            DisposeCalls++;
            OnDispose?.Invoke();
            if (RemainingDisposeFailures-- > 0)
                throw new InvalidOperationException("dispose failed");

            host.Dispose();
            IsDisposalComplete = true;
        }
    }
}

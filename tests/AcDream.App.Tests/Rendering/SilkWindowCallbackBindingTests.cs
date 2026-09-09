using AcDream.App.Diagnostics;
using AcDream.App.Rendering;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace AcDream.App.Tests.Rendering;

public sealed class SilkWindowCallbackBindingTests
{
    [Fact]
    public void AttachAndDisposeUseExactForwardAndReverseOrder()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface();
        var gate = new HostQuiescenceGate();

        using SilkWindowCallbackBinding binding = CreateAttachedBinding(
            surface,
            Targets(gate),
            pacing,
            gate);

        Assert.Equal(
        [
            "add load",
            "add update",
            "add render main",
            "add render pacing",
            "add closing",
            "add focus",
            "add move",
            "add state",
            "add framebuffer",
        ], surface.Operations);

        surface.Operations.Clear();
        binding.Dispose();

        Assert.Equal(
        [
            "remove framebuffer",
            "remove state",
            "remove move",
            "remove focus",
            "remove closing",
            "remove render pacing",
            "remove render main",
            "remove update",
            "remove load",
        ], surface.Operations);
        Assert.True(binding.IsDetached);
        Assert.False(gate.IsAccepting);
    }

    [Fact]
    public void MainRenderIsRegisteredBeforePacingRender()
    {
        var order = new List<string>();
        var clock = new OrderingClock();
        using var profiler = new FrameProfiler();
        using var pacing = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler,
            new FramePacingController(clock, new OrderingWaiter(clock, order)));
        pacing.InitializeStartup(requestedVSync: false);
        var surface = new FakeWindowCallbackSurface();
        var gate = new HostQuiescenceGate();
        WindowCallbackTargets targets = Targets(
            gate,
            render: _ => order.Add("main render"));
        using SilkWindowCallbackBinding binding = CreateAttachedBinding(
            surface,
            targets,
            pacing,
            gate);

        Assert.Equal(2, surface.RenderCallbacks.Count);

        surface.RaiseRender(1d / 60d);

        Assert.Equal(["main render", "pacing render"], order);
    }

    public static IEnumerable<object[]> AttachFailureCases =>
        Enumerable.Range(1, 9).SelectMany(attempt => new[]
        {
            new object[] { attempt, false },
            new object[] { attempt, true },
        });

    [Theory]
    [MemberData(nameof(AttachFailureCases))]
    public void FailureAfterNthAttachRollsBackPossiblyAcquiredPrefix(
        int failedAttempt,
        bool failAfterSideEffect)
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface
        {
            FailAddAttempt = failedAttempt,
            FailAddAfterSideEffect = failAfterSideEffect,
        };
        var gate = new HostQuiescenceGate();
        SilkWindowCallbackBinding binding = SilkWindowCallbackBinding.Create(
            surface,
            Targets(gate),
            pacing,
            gate);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(binding.Attach);

        Assert.Contains("rolled back", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, surface.SubscriptionCount);
        Assert.False(gate.IsAccepting);

        string[] added = surface.Operations
            .Where(operation => operation.StartsWith("add ", StringComparison.Ordinal))
            .Take(failedAttempt)
            .ToArray();
        string[] removed = surface.Operations
            .Where(operation => operation.StartsWith("remove ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            added
                .Reverse()
                .Select(operation => operation.Replace("add ", "remove ", StringComparison.Ordinal)),
            removed);
        Assert.True(binding.IsDetached);

        int operationsAfterRollback = surface.Operations.Count;
        binding.Dispose();
        Assert.Equal(operationsAfterRollback, surface.Operations.Count);
    }

    [Fact]
    public void RollbackFailureIsReportedAndRemainingCallbackIsLogicallySilent()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface
        {
            FailAddAttempt = 4,
        };
        surface.PersistentRemoveFailures.Add("update");
        var gate = new HostQuiescenceGate();
        int updateCalls = 0;
        SilkWindowCallbackBinding binding = SilkWindowCallbackBinding.Create(
            surface,
            Targets(gate, update: _ => updateCalls++),
            pacing,
            gate);

        AggregateException error = Assert.Throws<AggregateException>(binding.Attach);

        Assert.Contains(
            error.InnerExceptions,
            exception => exception.Message.Contains("registration failed", StringComparison.Ordinal));
        Assert.Contains(
            error.InnerExceptions,
            exception => exception.Message.Contains("index 1", StringComparison.Ordinal));
        Assert.False(gate.IsAccepting);
        Assert.Equal(1, surface.SubscriptionCount);

        surface.RaiseUpdate(0.1d);

        Assert.Equal(0, updateCalls);

        surface.PersistentRemoveFailures.Clear();
        binding.Dispose();

        Assert.True(binding.IsDetached);
        Assert.Equal(0, surface.SubscriptionCount);
    }

    [Fact]
    public void PersistentPhysicalDetachFailureIsRetriableWithoutReplayingSuccesses()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface();
        surface.PersistentRemoveFailures.Add("focus");
        var gate = new HostQuiescenceGate();
        int updateCalls = 0;
        SilkWindowCallbackBinding binding = CreateAttachedBinding(
            surface,
            Targets(gate, update: _ => updateCalls++),
            pacing,
            gate);

        Assert.Throws<AggregateException>(binding.Dispose);
        Assert.False(binding.IsDetached);
        Assert.False(gate.IsAccepting);
        int successfulRemovalCount = surface.Operations.Count(operation =>
            operation.StartsWith("remove ", StringComparison.Ordinal)
            && operation != "remove focus");
        Assert.Equal(8, successfulRemovalCount);

        surface.RaiseUpdate(0.1d);
        Assert.Equal(0, updateCalls);

        surface.PersistentRemoveFailures.Clear();
        binding.Dispose();

        Assert.True(binding.IsDetached);
        Assert.Equal(0, surface.SubscriptionCount);
        Assert.Equal(8, surface.Operations.Count(operation =>
            operation.StartsWith("remove ", StringComparison.Ordinal)
            && operation != "remove focus"));
    }

    [Fact]
    public void RemoveThenTransientThrowIsRetriedToPhysicalConvergence()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface();
        surface.RemoveAfterSideEffectFailures.Add("focus");
        var gate = new HostQuiescenceGate();
        SilkWindowCallbackBinding binding = CreateAttachedBinding(
            surface,
            Targets(gate),
            pacing,
            gate);

        binding.Dispose();

        Assert.True(binding.IsDetached);
        Assert.Equal(0, surface.SubscriptionCount);
        Assert.Equal(2, surface.Operations.Count(operation => operation == "remove focus"));
    }

    [Fact]
    public void DisposeIsRepeatedAndReentrantSafe()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface();
        var gate = new HostQuiescenceGate();
        SilkWindowCallbackBinding? binding = null;
        surface.Removing = _ => binding!.Dispose();
        binding = CreateAttachedBinding(surface, Targets(gate), pacing, gate);

        binding.Dispose();
        int operationsAfterFirstDispose = surface.Operations.Count;
        binding.Dispose();

        Assert.True(binding.IsDetached);
        Assert.Equal(operationsAfterFirstDispose, surface.Operations.Count);
        Assert.Equal(0, surface.SubscriptionCount);
    }

    [Fact]
    public async Task ConcurrentDisposeWaitsForPhysicalDetachToComplete()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface
        {
            BlockRemoveName = "framebuffer",
        };
        var gate = new HostQuiescenceGate();
        SilkWindowCallbackBinding binding = CreateAttachedBinding(
            surface,
            Targets(gate),
            pacing,
            gate);

        Task<Exception> first = Task.Run(() => Record.Exception(binding.Dispose));
        Assert.True(surface.RemoveEntered.Wait(TimeSpan.FromSeconds(5)));
        using var secondEntered = new ManualResetEventSlim(false);
        Exception? secondError = null;
        var secondThread = new Thread(() =>
        {
            secondEntered.Set();
            secondError = Record.Exception(binding.Dispose);
        })
        {
            IsBackground = true,
            Name = "Silk callback binding concurrent dispose contract",
        };
        secondThread.Start();
        bool secondStartedInTime = secondEntered.Wait(TimeSpan.FromSeconds(5));
        bool secondBlockedOnDetach = secondStartedInTime && SpinWait.SpinUntil(
            () => (secondThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5));

        surface.ContinueRemove.Set();

        Assert.Null(await first);
        bool secondJoined = secondThread.Join(TimeSpan.FromSeconds(5));
        Assert.True(secondStartedInTime, "the second dispose thread did not start");
        Assert.True(secondBlockedOnDetach, "the second dispose never waited for physical detach");
        Assert.True(secondJoined, "the second dispose did not finish after detach completed");
        Assert.Null(secondError);
        Assert.True(binding.IsDetached);
    }

    [Fact]
    public async Task ConcurrentDisposeCannotHidePhysicalDetachFailure()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface
        {
            BlockRemoveName = "framebuffer",
        };
        surface.PersistentRemoveFailures.Add("framebuffer");
        var gate = new HostQuiescenceGate();
        SilkWindowCallbackBinding binding = CreateAttachedBinding(
            surface,
            Targets(gate),
            pacing,
            gate);

        Task<Exception> first = Task.Run(() => Record.Exception(binding.Dispose));
        Assert.True(surface.RemoveEntered.Wait(TimeSpan.FromSeconds(5)));
        using var secondEntered = new ManualResetEventSlim(false);
        Exception? secondError = null;
        var secondThread = new Thread(() =>
        {
            secondEntered.Set();
            secondError = Record.Exception(binding.Dispose);
        })
        {
            IsBackground = true,
            Name = "Silk callback binding detach failure contract",
        };
        secondThread.Start();
        bool secondStartedInTime = secondEntered.Wait(TimeSpan.FromSeconds(5));
        bool secondBlockedOnDetach = secondStartedInTime && SpinWait.SpinUntil(
            () => (secondThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5));

        surface.ContinueRemove.Set();

        Assert.IsType<AggregateException>(await first);
        bool secondJoined = secondThread.Join(TimeSpan.FromSeconds(5));
        Assert.True(secondStartedInTime, "the second dispose thread did not start");
        Assert.True(secondBlockedOnDetach, "the second dispose never waited for physical detach");
        Assert.True(secondJoined, "the second dispose did not finish after detach failed");
        Assert.IsType<AggregateException>(secondError);
        Assert.False(binding.IsDetached);

        surface.PersistentRemoveFailures.Clear();
        binding.Dispose();
        Assert.True(binding.IsDetached);
    }

    [Fact]
    public async Task DisposeDuringBlockedAttachForcesRollbackBeforeDetachCompletes()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface
        {
            BlockAddName = "load",
        };
        var gate = new HostQuiescenceGate();
        SilkWindowCallbackBinding binding = SilkWindowCallbackBinding.Create(
            surface,
            Targets(gate),
            pacing,
            gate);

        // Both operations intentionally block; dedicated workers keep their
        // ordering independent of thread-pool capacity during parallel tests.
        Task<Exception> attach = Task.Factory.StartNew(
            () => Record.Exception(binding.Attach),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Task<Exception>? dispose = null;
        try
        {
            Assert.True(surface.AddEntered.Wait(TimeSpan.FromSeconds(5)));
            dispose = Task.Factory.StartNew(
                () => Record.Exception(binding.Dispose),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.True(SpinWait.SpinUntil(
                () => !gate.IsAccepting,
                TimeSpan.FromSeconds(5)));
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            surface.ContinueAdd.Set();
        }

        Assert.IsType<InvalidOperationException>(await attach);
        Assert.Null(await dispose!);
        Assert.True(binding.IsDetached);
        Assert.Equal(0, surface.SubscriptionCount);
    }

    [Fact]
    public async Task ConcurrentDoubleAttachAllowsOnlyTheLifecycleOwner()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface
        {
            BlockAddName = "load",
        };
        var gate = new HostQuiescenceGate();
        SilkWindowCallbackBinding binding = SilkWindowCallbackBinding.Create(
            surface,
            Targets(gate),
            pacing,
            gate);

        Task<Exception> first = Task.Run(() => Record.Exception(binding.Attach));
        Assert.True(surface.AddEntered.Wait(TimeSpan.FromSeconds(5)));
        using var secondStarted = new ManualResetEventSlim(false);
        Task<Exception> second = Task.Run(() =>
        {
            secondStarted.Set();
            return Record.Exception(binding.Attach);
        });
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
        Exception secondError = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(secondError);

        surface.ContinueAdd.Set();

        Assert.Null(await first);
        binding.Dispose();
        Assert.True(binding.IsDetached);
    }

    [Fact]
    public void AddAccessorCanRequestDisposeReentrantlyWithoutDeadlock()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface();
        var gate = new HostQuiescenceGate();
        SilkWindowCallbackBinding? binding = null;
        surface.Adding = _ => binding!.Dispose();
        binding = SilkWindowCallbackBinding.Create(
            surface,
            Targets(gate),
            pacing,
            gate);

        Assert.Throws<InvalidOperationException>(binding.Attach);
        Assert.False(binding.IsDisposalComplete);
        binding.Dispose();

        Assert.True(binding.IsDetached);
        Assert.True(binding.IsDisposalComplete);
        Assert.Equal(0, surface.SubscriptionCount);
    }

    [Fact]
    public void ReentrantAttachCleanupRemainsPublishedUntilRetryCompletes()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface();
        surface.PersistentRemoveFailures.Add("load");
        var gate = new HostQuiescenceGate();
        SilkWindowCallbackBinding? slot = SilkWindowCallbackBinding.Create(
            surface,
            Targets(gate),
            pacing,
            gate);
        ResourceShutdownTransaction? shutdown = null;
        shutdown = new ResourceShutdownTransaction(
            new ResourceShutdownStage("input",
            [
                new("window callbacks", () =>
                {
                    SilkWindowCallbackBinding? current = slot;
                    if (current is null)
                        return;

                    current.Dispose();
                    if (!current.IsDisposalComplete)
                        throw new NativeWindowCallbackCleanupDeferredException();
                    slot = null;
                }),
            ]));
        surface.Adding = _ => shutdown.CompleteOrThrow();

        Assert.Throws<AggregateException>(slot.Attach);
        Assert.NotNull(slot);
        Assert.False(slot.IsDisposalComplete);

        surface.PersistentRemoveFailures.Clear();
        shutdown.CompleteOrThrow();

        Assert.Null(slot);
        Assert.True(shutdown.IsComplete);
        Assert.Equal(0, surface.SubscriptionCount);
    }

    [Fact]
    public async Task ConcurrentCallbackDisposeAndLaterAddFailureCannotDeadlock()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface();
        var gate = new HostQuiescenceGate();
        using var loadEntered = new ManualResetEventSlim(false);
        SilkWindowCallbackBinding? binding = null;
        Task<Exception>? callback = null;
        var targets = new WindowCallbackTargets(
            load: () =>
            {
                loadEntered.Set();
                binding!.Dispose();
            },
            update: static _ => { },
            render: static _ => { },
            closing: gate.StopAccepting,
            focusChanged: static _ => { },
            framebufferResize: static _ => { });
        binding = SilkWindowCallbackBinding.Create(surface, targets, pacing, gate);
        surface.Adding = name =>
        {
            if (name != "update")
                return;

            callback = Task.Run(() => Record.Exception(surface.RaiseLoad));
            Assert.True(loadEntered.Wait(TimeSpan.FromSeconds(5)));
            throw new InvalidOperationException("synthetic later add failure");
        };

        Task<Exception> attach = Task.Run(() => Record.Exception(binding.Attach));
        Exception attachError = await attach.WaitAsync(TimeSpan.FromSeconds(5));
        Exception callbackError = await callback!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<InvalidOperationException>(attachError);
        Assert.IsType<NativeWindowCallbackCleanupDeferredException>(callbackError);
        Assert.False(binding.IsDisposalComplete);

        binding.Dispose();
        Assert.True(binding.IsDisposalComplete);
        Assert.Equal(0, surface.SubscriptionCount);
    }

    [Fact]
    public void CallbackRaisedDuringDetachAndAfterDetachIsSilent()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface();
        var gate = new HostQuiescenceGate();
        int updateCalls = 0;
        SilkWindowCallbackBinding binding = CreateAttachedBinding(
            surface,
            Targets(gate, update: _ => updateCalls++),
            pacing,
            gate);
        surface.Removing = _ => surface.RaiseUpdate(0.1d);

        binding.Dispose();
        surface.RaiseUpdate(0.1d);

        Assert.Equal(0, updateCalls);
    }

    [Fact]
    public void ClosingCanQuiesceEveryLaterNativeCallback()
    {
        using var profiler = new FrameProfiler();
        using var pacing = CreatePacing(profiler);
        var surface = new FakeWindowCallbackSurface();
        var gate = new HostQuiescenceGate();
        int closingCalls = 0;
        int updateCalls = 0;
        WindowCallbackTargets targets = Targets(
            gate,
            update: _ => updateCalls++,
            closing: () =>
            {
                closingCalls++;
                gate.StopAccepting();
            });
        using SilkWindowCallbackBinding binding = CreateAttachedBinding(
            surface,
            targets,
            pacing,
            gate);

        surface.RaiseClosing();
        surface.RaiseClosing();
        surface.RaiseUpdate(0.1d);

        Assert.Equal(1, closingCalls);
        Assert.Equal(0, updateCalls);
        Assert.False(gate.IsAccepting);
    }

    [Fact]
    public void ClosedGateSilencesEveryNativeWindowEdge()
    {
        var pacingCalls = new List<string>();
        var clock = new OrderingClock();
        using var profiler = new FrameProfiler();
        using var pacing = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler,
            new FramePacingController(clock, new OrderingWaiter(clock, pacingCalls)));
        pacing.InitializeStartup(requestedVSync: false);
        var pacingSurface = new CountingPacingSurface();
        pacing.BindSurface(pacingSurface);
        var surface = new FakeWindowCallbackSurface();
        var gate = new HostQuiescenceGate();
        int targetCalls = 0;
        var targets = new WindowCallbackTargets(
            () => targetCalls++,
            _ => targetCalls++,
            _ => targetCalls++,
            () => targetCalls++,
            _ => targetCalls++,
            _ => targetCalls++);
        using SilkWindowCallbackBinding binding = CreateAttachedBinding(
            surface,
            targets,
            pacing,
            gate);
        gate.StopAccepting();

        surface.RaiseLoad();
        surface.RaiseUpdate(0.1d);
        surface.RaiseRender(0.1d);
        surface.RaiseClosing();
        surface.RaiseFocusChanged(false);
        surface.RaiseMove(default);
        surface.RaiseStateChanged(default);
        surface.RaiseFramebufferResize(new Vector2D<int>(1280, 720));

        Assert.Equal(0, targetCalls);
        Assert.Empty(pacingCalls);
        Assert.Equal(0, pacingSurface.RefreshReadCount);
    }

    private static DisplayFramePacingController CreatePacing(FrameProfiler profiler)
    {
        var pacing = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler);
        pacing.InitializeStartup(requestedVSync: true);
        return pacing;
    }

    private static SilkWindowCallbackBinding CreateAttachedBinding(
        IWindowCallbackSurface surface,
        WindowCallbackTargets targets,
        DisplayFramePacingController pacing,
        HostQuiescenceGate gate)
    {
        SilkWindowCallbackBinding binding = SilkWindowCallbackBinding.Create(
            surface,
            targets,
            pacing,
            gate);
        binding.Attach();
        return binding;
    }

    private static WindowCallbackTargets Targets(
        HostQuiescenceGate gate,
        Action<double>? update = null,
        Action<double>? render = null,
        Action? closing = null) => new(
            load: static () => { },
            update: update ?? (static _ => { }),
            render: render ?? (static _ => { }),
            closing: closing ?? gate.StopAccepting,
            focusChanged: static _ => { },
            framebufferResize: static _ => { });

    private sealed class FakeWindowCallbackSurface : IWindowCallbackSurface
    {
        private readonly List<Action> _load = [];
        private readonly List<Action<double>> _update = [];
        private readonly List<Action<double>> _render = [];
        private readonly List<Action> _closing = [];
        private readonly List<Action<bool>> _focus = [];
        private readonly List<Action<Vector2D<int>>> _move = [];
        private readonly List<Action<WindowState>> _state = [];
        private readonly List<Action<Vector2D<int>>> _framebuffer = [];
        private readonly Dictionary<Action<double>, string> _renderNames =
            new(ReferenceEqualityComparer.Instance);
        private int _addAttempts;
        private int _addBlockUsed;
        private int _removeBlockUsed;

        public List<string> Operations { get; } = [];
        public HashSet<string> PersistentRemoveFailures { get; } = [];
        public HashSet<string> RemoveAfterSideEffectFailures { get; } = [];
        public int? FailAddAttempt { get; init; }
        public bool FailAddAfterSideEffect { get; init; }
        public string? BlockAddName { get; init; }
        public string? BlockRemoveName { get; init; }
        public ManualResetEventSlim AddEntered { get; } = new(false);
        public ManualResetEventSlim ContinueAdd { get; } = new(false);
        public ManualResetEventSlim RemoveEntered { get; } = new(false);
        public ManualResetEventSlim ContinueRemove { get; } = new(false);
        public Action<string>? Adding { get; set; }
        public Action<string>? Removing { get; set; }
        public IReadOnlyList<Action<double>> RenderCallbacks => _render;

        public int SubscriptionCount =>
            _load.Count + _update.Count + _render.Count + _closing.Count
            + _focus.Count + _move.Count + _state.Count + _framebuffer.Count;

        public void AddLoad(Action callback) => Add("load", _load, callback);
        public void RemoveLoad(Action callback) => Remove("load", _load, callback);
        public void AddUpdate(Action<double> callback) => Add("update", _update, callback);
        public void RemoveUpdate(Action<double> callback) => Remove("update", _update, callback);
        public void AddRender(Action<double> callback)
        {
            string name = _render.Count == 0 ? "render main" : "render pacing";
            _renderNames.Add(callback, name);
            Add(name, _render, callback);
        }
        public void RemoveRender(Action<double> callback)
        {
            string name = _renderNames[callback];
            Remove(name, _render, callback);
            _renderNames.Remove(callback);
        }
        public void AddClosing(Action callback) => Add("closing", _closing, callback);
        public void RemoveClosing(Action callback) => Remove("closing", _closing, callback);
        public void AddFocusChanged(Action<bool> callback) => Add("focus", _focus, callback);
        public void RemoveFocusChanged(Action<bool> callback) => Remove("focus", _focus, callback);
        public void AddMove(Action<Vector2D<int>> callback) => Add("move", _move, callback);
        public void RemoveMove(Action<Vector2D<int>> callback) => Remove("move", _move, callback);
        public void AddStateChanged(Action<WindowState> callback) => Add("state", _state, callback);
        public void RemoveStateChanged(Action<WindowState> callback) => Remove("state", _state, callback);
        public void AddFramebufferResize(Action<Vector2D<int>> callback) =>
            Add("framebuffer", _framebuffer, callback);
        public void RemoveFramebufferResize(Action<Vector2D<int>> callback) =>
            Remove("framebuffer", _framebuffer, callback);

        public void RaiseLoad() => InvokeSnapshot(_load);
        public void RaiseUpdate(double delta) => InvokeSnapshot(_update, delta);
        public void RaiseRender(double delta) => InvokeSnapshot(_render, delta);
        public void RaiseClosing() => InvokeSnapshot(_closing);
        public void RaiseFocusChanged(bool focused) => InvokeSnapshot(_focus, focused);
        public void RaiseMove(Vector2D<int> position) => InvokeSnapshot(_move, position);
        public void RaiseStateChanged(WindowState state) => InvokeSnapshot(_state, state);
        public void RaiseFramebufferResize(Vector2D<int> size) =>
            InvokeSnapshot(_framebuffer, size);

        private void Add<T>(string name, List<T> callbacks, T callback)
        {
            Operations.Add($"add {name}");
            _addAttempts++;
            if (name == BlockAddName
                && Interlocked.Exchange(ref _addBlockUsed, 1) == 0)
            {
                AddEntered.Set();
                if (!ContinueAdd.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Synthetic add block timed out.");
            }
            bool fail = FailAddAttempt == _addAttempts;
            if (fail && !FailAddAfterSideEffect)
                throw new InvalidOperationException($"synthetic add failure: {name}");
            callbacks.Add(callback);
            Adding?.Invoke(name);
            if (fail)
                throw new InvalidOperationException($"synthetic add-after-side-effect failure: {name}");
        }

        private void Remove<T>(string name, List<T> callbacks, T callback)
        {
            Operations.Add($"remove {name}");
            Removing?.Invoke(name);
            if (name == BlockRemoveName
                && Interlocked.Exchange(ref _removeBlockUsed, 1) == 0)
            {
                RemoveEntered.Set();
                if (!ContinueRemove.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Synthetic remove block timed out.");
            }
            if (PersistentRemoveFailures.Contains(name))
                throw new InvalidOperationException($"synthetic remove failure: {name}");
            callbacks.Remove(callback);
            if (RemoveAfterSideEffectFailures.Remove(name))
            {
                throw new InvalidOperationException(
                    $"synthetic remove-after-side-effect failure: {name}");
            }
        }

        private static void InvokeSnapshot(List<Action> callbacks)
        {
            foreach (Action callback in callbacks.ToArray())
                callback();
        }

        private static void InvokeSnapshot<T>(List<Action<T>> callbacks, T value)
        {
            foreach (Action<T> callback in callbacks.ToArray())
                callback(value);
        }
    }

    private sealed class OrderingClock : IFramePacingClock
    {
        public long Frequency => 1_000;
        public long Timestamp { get; set; }
        public long GetTimestamp() => Timestamp;
    }

    private sealed class OrderingWaiter(
        OrderingClock clock,
        List<string> order) : IFramePacingWaiter
    {
        public void Wait(long durationTicks, long clockFrequency)
        {
            order.Add("pacing render");
            clock.Timestamp += durationTicks;
        }
    }

    private sealed class CountingPacingSurface : IDisplayFramePacingSurface
    {
        public bool VSync { get; set; }
        public int RefreshReadCount { get; private set; }

        public bool TryGetActiveMonitorRefreshHz(out int refreshHz)
        {
            RefreshReadCount++;
            refreshHz = 144;
            return true;
        }
    }
}

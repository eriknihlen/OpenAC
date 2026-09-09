using System.Reflection;
using AcDream.App.Input;
using AcDream.App.Rendering;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

public sealed class QuiescentInputContextTests
{
    [Fact]
    public void EveryFrontendAddFailureRetainsTheCompletePossiblyAttachedPrefix()
    {
        for (int failAdd = 1; failAdd <= 10; failAdd++)
        {
            var fixture = new Fixture { FailAdd = failAdd };
            var context = fixture.CreateContext();

            Assert.Throws<InvalidOperationException>(() => SubscribeAll(context, () => { }));
            context.Dispose();

            Assert.Equal(failAdd, fixture.AddCalls);
            Assert.Equal(failAdd, fixture.RemoveCalls);
            Assert.Equal(0, fixture.LiveEdges);
            Assert.True(context.IsDisposalComplete);
            Assert.False(fixture.InnerDisposed);
        }
    }

    [Fact]
    public void EveryFrontendRemoveFailureRetriesOnlyThePendingEdge()
    {
        for (int failRemoveEdge = 1; failRemoveEdge <= 10; failRemoveEdge++)
        {
            var fixture = new Fixture();
            var context = fixture.CreateContext();
            SubscribeAll(context, () => { });
            context.Activate();
            fixture.FailRemoveEdgeOnce = failRemoveEdge;

            context.Dispose();
            int[] completedCounts = fixture.RemoveCounts.ToArray();
            context.Dispose();

            for (int edge = 1; edge <= 10; edge++)
            {
                Assert.Equal(
                    edge == failRemoveEdge ? 2 : 1,
                    completedCounts[edge]);
                Assert.Equal(completedCounts[edge], fixture.RemoveCounts[edge]);
            }
            Assert.Equal(0, fixture.LiveEdges);
            Assert.True(context.IsDisposalComplete);
            Assert.False(fixture.InnerDisposed);
        }
    }

    [Fact]
    public void DeactivateSilencesKeyboardMouseAndConnectionRelaysBeforePhysicalDetach()
    {
        var fixture = new Fixture();
        var context = fixture.CreateContext();
        int calls = 0;
        SubscribeAll(context, () => calls++);
        context.Activate();

        fixture.RaiseAll();
        Assert.Equal(10, calls);

        context.Deactivate();
        fixture.RaiseAll();
        context.Dispose();

        Assert.Equal(10, calls);
        Assert.Equal(0, fixture.LiveEdges);
        Assert.True(context.IsDisposalComplete);
    }

    [Fact]
    public void EventRaisedFromAddAccessorCannotEnterPartialFrontend()
    {
        var fixture = new Fixture { RaiseKeyboardDownDuringAdd = true };
        var context = fixture.CreateContext();
        int calls = 0;

        context.Keyboards[0].KeyDown += (_, _, _) => calls++;
        Assert.Equal(0, calls);
        context.Activate();
        fixture.Keyboard.RaiseDown();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void DisposeBeforeFrontendConstructionMakesContextAndDeviceViewsTerminal()
    {
        var fixture = new Fixture();
        var context = fixture.CreateContext();
        IKeyboard keyboard = context.Keyboards[0];
        IMouse mouse = context.Mice[0];
        context.Dispose();

        Assert.Throws<ObjectDisposedException>(context.Activate);
        Assert.Throws<ObjectDisposedException>(
            () => keyboard.KeyDown += static (_, _, _) => { });
        Assert.Throws<ObjectDisposedException>(
            () => mouse.MouseDown += static (_, _) => { });
        Assert.Throws<ObjectDisposedException>(
            () => context.ConnectionChanged += static (_, _) => { });

        context.Dispose();
        Assert.Equal(0, fixture.LiveEdges);
        Assert.True(context.IsDisposalComplete);
    }

    private static void SubscribeAll(QuiescentInputContext context, Action called)
    {
        IKeyboard keyboard = context.Keyboards[0];
        IMouse mouse = context.Mice[0];
        keyboard.KeyDown += (_, _, _) => called();
        keyboard.KeyUp += (_, _, _) => called();
        keyboard.KeyChar += (_, _) => called();
        mouse.MouseDown += (_, _) => called();
        mouse.MouseUp += (_, _) => called();
        mouse.Click += (_, _, _) => called();
        mouse.DoubleClick += (_, _, _) => called();
        mouse.MouseMove += (_, _) => called();
        mouse.Scroll += (_, _) => called();
        context.ConnectionChanged += (_, _) => called();
    }

    private sealed class Fixture
    {
        private readonly FaultPlan _faults = new();
        private Context? _inner;
        private IMouse? _mouse;
        private MouseProxy? _mouseProxy;

        public Keyboard Keyboard { get; private set; } = null!;
        public int FailAdd { set => _faults.FailAdd = value; }
        public int FailRemoveEdgeOnce { set => _faults.FailRemoveEdgeOnce = value; }
        public bool RaiseKeyboardDownDuringAdd { get; set; }
        public int AddCalls => _faults.AddCalls;
        public int RemoveCalls => _faults.RemoveCalls;
        public int[] RemoveCounts => _faults.RemoveCounts;
        public int LiveEdges => _faults.LiveEdges;
        public bool InnerDisposed => _inner?.Disposed == true;

        public QuiescentInputContext CreateContext()
        {
            Keyboard = new Keyboard(_faults)
            {
                RaiseDuringAdd = RaiseKeyboardDownDuringAdd,
            };
            _mouse = DispatchProxy.Create<IMouse, MouseProxy>();
            _mouseProxy = (MouseProxy)(object)_mouse;
            _mouseProxy.Initialize(_faults, _mouse);
            _inner = new Context(_faults, Keyboard, _mouse);
            return new QuiescentInputContext(_inner, new HostQuiescenceGate());
        }

        public void RaiseAll()
        {
            Keyboard.RaiseAll();
            _mouseProxy!.RaiseAll();
            _inner!.RaiseConnection(Keyboard);
        }
    }

    public sealed class FaultPlan
    {
        public int FailAdd { get; set; }
        public int FailRemoveEdgeOnce { get; set; }
        public int AddCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int LiveEdges { get; private set; }
        public int[] RemoveCounts { get; } = new int[11];

        public void Add(int edge, Action sideEffect)
        {
            AddCalls++;
            sideEffect();
            LiveEdges++;
            if (AddCalls == FailAdd)
                throw new InvalidOperationException($"add {edge}");
        }

        public void Remove(int edge, Action sideEffect)
        {
            RemoveCalls++;
            RemoveCounts[edge]++;
            if (FailRemoveEdgeOnce == edge)
            {
                FailRemoveEdgeOnce = 0;
                throw new InvalidOperationException($"remove {edge}");
            }

            sideEffect();
            LiveEdges--;
        }
    }

    private sealed class Context(
        FaultPlan faults,
        IKeyboard keyboard,
        IMouse mouse) : IInputContext
    {
        private Action<IInputDevice, bool>? _connectionChanged;
        public bool Disposed { get; private set; }
        public nint Handle => 1;
        public IReadOnlyList<IGamepad> Gamepads { get; } = [];
        public IReadOnlyList<IJoystick> Joysticks { get; } = [];
        public IReadOnlyList<IKeyboard> Keyboards { get; } = [keyboard];
        public IReadOnlyList<IMouse> Mice { get; } = [mouse];
        public IReadOnlyList<IInputDevice> OtherDevices { get; } = [];

        public event Action<IInputDevice, bool>? ConnectionChanged
        {
            add => faults.Add(10, () => _connectionChanged += value);
            remove => faults.Remove(10, () => _connectionChanged -= value);
        }

        public void Dispose() => Disposed = true;
        public void RaiseConnection(IInputDevice device) =>
            _connectionChanged?.Invoke(device, true);
    }

    private sealed class Keyboard(FaultPlan faults) : IKeyboard
    {
        private Action<IKeyboard, Key, int>? _down;
        private Action<IKeyboard, Key, int>? _up;
        private Action<IKeyboard, char>? _char;
        public bool RaiseDuringAdd { get; init; }
        public string Name => "keyboard";
        public int Index => 0;
        public bool IsConnected => true;
        public IReadOnlyList<Key> SupportedKeys { get; } = [];
        public string ClipboardText { get; set; } = string.Empty;

        public event Action<IKeyboard, Key, int>? KeyDown
        {
            add => faults.Add(1, () =>
            {
                _down += value;
                if (RaiseDuringAdd)
                    _down?.Invoke(this, Key.A, 0);
            });
            remove => faults.Remove(1, () => _down -= value);
        }
        public event Action<IKeyboard, Key, int>? KeyUp
        {
            add => faults.Add(2, () => _up += value);
            remove => faults.Remove(2, () => _up -= value);
        }
        public event Action<IKeyboard, char>? KeyChar
        {
            add => faults.Add(3, () => _char += value);
            remove => faults.Remove(3, () => _char -= value);
        }

        public bool IsKeyPressed(Key key) => false;
        public bool IsScancodePressed(int scancode) => false;
        public void BeginInput() { }
        public void EndInput() { }
        public void RaiseDown() => _down?.Invoke(this, Key.A, 0);
        public void RaiseAll()
        {
            _down?.Invoke(this, Key.A, 0);
            _up?.Invoke(this, Key.A, 0);
            _char?.Invoke(this, 'a');
        }
    }

    public class MouseProxy : DispatchProxy
    {
        private readonly Dictionary<string, Delegate?> _events = new();
        private FaultPlan _faults = null!;
        private IMouse _self = null!;

        public void Initialize(FaultPlan faults, IMouse self)
        {
            _faults = faults;
            _self = self;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal))
            {
                string name = targetMethod.Name[4..];
                int edge = Edge(name);
                _faults.Add(edge, () =>
                    _events[name] = Delegate.Combine(_events.GetValueOrDefault(name), (Delegate)args[0]!));
                return null;
            }
            if (targetMethod.Name.StartsWith("remove_", StringComparison.Ordinal))
            {
                string name = targetMethod.Name[7..];
                int edge = Edge(name);
                _faults.Remove(edge, () =>
                    _events[name] = Delegate.Remove(_events.GetValueOrDefault(name), (Delegate)args[0]!));
                return null;
            }

            if (targetMethod.Name == nameof(IMouse.IsButtonPressed))
                return false;
            Type returnType = targetMethod.ReturnType;
            if (returnType == typeof(void))
                return null;
            if (returnType == typeof(string))
                return string.Empty;
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }

        public void RaiseAll()
        {
            Get<Action<IMouse, MouseButton>>(nameof(IMouse.MouseDown))?.Invoke(_self, MouseButton.Left);
            Get<Action<IMouse, MouseButton>>(nameof(IMouse.MouseUp))?.Invoke(_self, MouseButton.Left);
            Get<Action<IMouse, MouseButton, System.Numerics.Vector2>>(nameof(IMouse.Click))
                ?.Invoke(_self, MouseButton.Left, System.Numerics.Vector2.One);
            Get<Action<IMouse, MouseButton, System.Numerics.Vector2>>(nameof(IMouse.DoubleClick))
                ?.Invoke(_self, MouseButton.Left, System.Numerics.Vector2.One);
            Get<Action<IMouse, System.Numerics.Vector2>>(nameof(IMouse.MouseMove))
                ?.Invoke(_self, System.Numerics.Vector2.One);
            Get<Action<IMouse, ScrollWheel>>(nameof(IMouse.Scroll))
                ?.Invoke(_self, new ScrollWheel(1, 1));
        }

        private T? Get<T>(string name) where T : Delegate =>
            _events.GetValueOrDefault(name) as T;

        private static int Edge(string name) => name switch
        {
            nameof(IMouse.MouseDown) => 4,
            nameof(IMouse.MouseUp) => 5,
            nameof(IMouse.Click) => 6,
            nameof(IMouse.DoubleClick) => 7,
            nameof(IMouse.MouseMove) => 8,
            nameof(IMouse.Scroll) => 9,
            _ => throw new InvalidOperationException($"Unexpected mouse event {name}."),
        };
    }
}

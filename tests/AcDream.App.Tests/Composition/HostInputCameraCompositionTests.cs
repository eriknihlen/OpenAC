using System.Numerics;
using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Architecture;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace AcDream.App.Tests.Composition;

public sealed class HostInputCameraCompositionTests
{
    [Fact]
    public void ProductionPhasePreservesTheCompleteHostInputCameraOrder()
    {
        using var fixture = new Fixture();

        HostInputCameraResult result = fixture.Phase().Compose(fixture.Platform);

        Assert.Equal(
            Enum.GetValues<HostInputCameraCompositionPoint>(),
            fixture.Points);
        Assert.Same(fixture.Publication.GpuFrames, result.GpuFrameFlights);
        Assert.Same(fixture.Publication.GpuDevice, result.GpuDevice);
        Assert.Same(fixture.Publication.Keyboard, result.KeyboardSource);
        Assert.Same(fixture.Publication.Mouse, result.MouseSource);
        Assert.Same(fixture.Publication.Dispatcher, result.InputDispatcher);
        Assert.Same(fixture.Publication.Camera, result.CameraController);
        Assert.Same(fixture.Publication.Pointer, result.CameraPointerInput);
        Assert.Equal(1280f / 720f, fixture.ViewportAspect.Aspect, precision: 5);
        Assert.Equal((1280, 720), fixture.Factory.Viewport.Size);
    }

    [Fact]
    public void DiagnosticOrbitOverridesReachOnlyTheInitialOrbitCamera()
    {
        using var fixture = new Fixture();

        HostInputCameraResult result = fixture
            .Phase(180f, -135f, 7.5f)
            .Compose(fixture.Platform);

        Assert.Equal(180f, result.CameraController.Orbit.Distance);
        Assert.Equal(-135f * MathF.PI / 180f, result.CameraController.Orbit.Yaw);
        Assert.Equal(7.5f * MathF.PI / 180f, result.CameraController.Orbit.Pitch);
    }

    [Fact]
    public void VulkanFactoryConvertsDiagnosticOrbitAnglesFromDegrees()
    {
        var factory = new VulkanHostInputCameraCompositionFactory();

        CameraController camera = factory.CreateCameraController(200f, 180f, -12f);

        Assert.Equal(200f, camera.Orbit.Distance);
        Assert.Equal(MathF.PI, camera.Orbit.Yaw, precision: 6);
        Assert.Equal(-12f * MathF.PI / 180f, camera.Orbit.Pitch, precision: 6);
    }

    [Theory]
    [MemberData(nameof(FaultPointValues))]
    public void FailureAtEveryProductionBoundaryStopsTheExactSuffix(
        int faultPointValue)
    {
        var faultPoint = (HostInputCameraCompositionPoint)faultPointValue;
        using var fixture = new Fixture(faultPoint);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Phase().Compose(fixture.Platform));

        HostInputCameraCompositionPoint[] expected =
            Enum.GetValues<HostInputCameraCompositionPoint>()
                .TakeWhile(point => point <= faultPoint)
                .ToArray();
        Assert.Equal(expected, fixture.Points);
        fixture.Publication.AssertPublishedThrough(faultPoint);
    }

    public static TheoryData<int> FaultPointValues()
    {
        var data = new TheoryData<int>();
        foreach (HostInputCameraCompositionPoint point in
                 Enum.GetValues<HostInputCameraCompositionPoint>())
        {
            data.Add((int)point);
        }
        return data;
    }

    [Fact]
    public void GameWindowUsesTheExactPlatformPreludeAndPhaseOneType()
    {
        IReadOnlyList<CompiledCall> declaredCalls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));

        Assert.Single(
            declaredCalls,
            call => call.Target.DeclaringType
                    == typeof(GameWindowPlatformAcquisition)
                && call.Target.Name == nameof(GameWindowPlatformAcquisition.Acquire));
        Assert.Single(
            declaredCalls,
            call => call.Target.DeclaringType
                    == typeof(HostInputCameraCompositionPhase)
                && call.Target.IsConstructor);

        MethodInfo onLoad = typeof(GameWindow).GetMethod(
            "OnLoad",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(GameWindow).FullName, "OnLoad");
        IReadOnlyList<CompiledCall> loadCalls = CompiledCallGraph.Read(onLoad);
        Assert.Contains(
            loadCalls,
            call => call.Target.DeclaringType == typeof(GameWindow)
                && call.Target.Name == "AcquirePlatform");
        Assert.Contains(
            loadCalls,
            call => call.Target.DeclaringType == typeof(GameWindowCompositionPipeline)
                && call.Target.Name == nameof(GameWindowCompositionPipeline.Run));
        Assert.DoesNotContain(loadCalls, call => call.Target.Name == "GetApi");
        Assert.DoesNotContain(loadCalls, call => call.Target.Name == "CreateInput");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HostInputCameraCompositionPoint? _failure;

        public Fixture(HostInputCameraCompositionPoint? failure = null)
        {
            _failure = failure;
            IKeyboard keyboard = DispatchProxy.Create<IKeyboard, NullDeviceProxy>();
            IMouse mouse = DispatchProxy.Create<IMouse, NullDeviceProxy>();
            Input = new InputContext(keyboard, mouse);
            Platform = new GameWindowPlatformResult<GameWindowGraphics, IInputContext>(TestGameWindowGraphics.Instance, Input);
            ViewportAspect = new ViewportAspectState();
            Framebuffer = new FramebufferResizeController(ViewportAspect);
            Capture = new CaptureSource();
            MovementState = new RuntimeLocalPlayerMovementState();
            Movement = new DispatcherMovementInputSource(MovementState, Capture);
            CameraInput = new DispatcherCameraInputSource();
            PlayerMode = new LocalPlayerModeState();
            Chase = new ChaseCameraInputState();
            Pointer = new PointerPositionState();
            Factory = new Factory();
            Publication = new Publication();
        }

        public List<HostInputCameraCompositionPoint> Points { get; } = [];
        public InputContext Input { get; }
        public GameWindowPlatformResult<GameWindowGraphics, IInputContext> Platform { get; }
        public ViewportAspectState ViewportAspect { get; }
        public FramebufferResizeController Framebuffer { get; }
        public CaptureSource Capture { get; }
        public RuntimeLocalPlayerMovementState MovementState { get; }
        public DispatcherMovementInputSource Movement { get; }
        public DispatcherCameraInputSource CameraInput { get; }
        public LocalPlayerModeState PlayerMode { get; }
        public ChaseCameraInputState Chase { get; }
        public PointerPositionState Pointer { get; }
        public Factory Factory { get; }
        public Publication Publication { get; }

        public HostInputCameraCompositionPhase Phase(
            float? initialOrbitDistanceMeters = null,
            float? initialOrbitYawDegrees = null,
            float? initialOrbitPitchDegrees = null) => new(
            new HostInputCameraDependencies(
                Framebuffer,
                new Vector2D<int>(1280, 720),
                new HostQuiescenceGate(),
                Capture,
                KeyBindings.RetailDefaults(),
                Movement,
                CameraInput,
                PlayerMode,
                Chase,
                Pointer,
                new DiagnosticLog(),
                initialOrbitDistanceMeters,
                initialOrbitYawDegrees,
                initialOrbitPitchDegrees),
            Publication,
            Factory,
            point =>
            {
                Points.Add(point);
                if (_failure == point)
                    throw new InvalidOperationException($"fault at {point}");
            });

        public void Dispose()
        {
            Publication.Dispose();
            MovementState.Dispose();
        }
    }

    private sealed class Publication :
        IGameWindowHostInputCameraPublication,
        IDisposable
    {
        public GpuFrameFlightController? GpuFrames { get; private set; }
        public IGpuDevice? GpuDevice { get; private set; }
        public GpuDeviceFrameLifetime? GpuFrameLifetime { get; private set; }
        public SilkKeyboardSource? Keyboard { get; private set; }
        public SilkMouseSource? Mouse { get; private set; }
        public IMouseLookCursor? Cursor { get; private set; }
        public InputDispatcher? Dispatcher { get; private set; }
        public CameraController? Camera { get; private set; }
        public CameraPointerInputController? Pointer { get; private set; }

        public void PublishGpuFrameFlights(GpuFrameFlightController? value)
        {
            if (value is not null)
                GpuFrames = PublishOnce(GpuFrames, value);
        }

        public void PublishGpuDevice(IGpuDevice value) =>
            GpuDevice = PublishOnce(GpuDevice, value);

        public void PublishGpuFrameLifetime(GpuDeviceFrameLifetime value) =>
            GpuFrameLifetime = PublishOnce(GpuFrameLifetime, value);

        public void PublishKeyboardSource(SilkKeyboardSource value) =>
            Keyboard = PublishOnce(Keyboard, value);

        public void PublishMouseSource(SilkMouseSource value) =>
            Mouse = PublishOnce(Mouse, value);

        public void PublishMouseLookCursor(IMouseLookCursor value) =>
            Cursor = PublishOnce(Cursor, value);

        public void PublishInputDispatcher(InputDispatcher value) =>
            Dispatcher = PublishOnce(Dispatcher, value);

        public void PublishCameraController(CameraController value) =>
            Camera = PublishOnce(Camera, value);

        public void PublishCameraPointerInput(CameraPointerInputController value) =>
            Pointer = PublishOnce(Pointer, value);

        public void AssertPublishedThrough(HostInputCameraCompositionPoint point)
        {
            Assert.Equal(
                point >= HostInputCameraCompositionPoint.GpuFrameFlightsPublished,
                GpuFrames is not null);
            Assert.Equal(
                point >= HostInputCameraCompositionPoint.GpuDevicePublished,
                GpuDevice is not null);
            Assert.Equal(
                point >= HostInputCameraCompositionPoint.KeyboardPublished,
                Keyboard is not null);
            Assert.Equal(
                point >= HostInputCameraCompositionPoint.MousePublished,
                Mouse is not null);
            Assert.Equal(
                point >= HostInputCameraCompositionPoint.MouseLookCursorPublished,
                Cursor is not null);
            Assert.Equal(
                point >= HostInputCameraCompositionPoint.DispatcherPublished,
                Dispatcher is not null);
            Assert.Equal(
                point >= HostInputCameraCompositionPoint.CameraPublished,
                Camera is not null);
            Assert.Equal(
                point >= HostInputCameraCompositionPoint.CameraPointerPublished,
                Pointer is not null);
        }

        public void Dispose()
        {
            Pointer?.Dispose();
            Pointer = null;
            Dispatcher?.Dispose();
            Dispatcher = null;
            Mouse?.Dispose();
            Mouse = null;
            Keyboard?.Dispose();
            Keyboard = null;
            GpuDevice?.Dispose();
            GpuDevice = null;
            GpuFrames?.Dispose();
            GpuFrames = null;
        }

        private static T PublishOnce<T>(T? current, T value)
            where T : class
        {
            if (current is not null)
                throw new InvalidOperationException("duplicate publication");
            return value;
        }
    }

    private sealed class Factory : IHostInputCameraCompositionFactory
    {
        public ViewportTarget Viewport { get; } = new();
        private readonly KeyboardSurface _keyboard = new();
        private readonly MouseSurface _mouse = new();
        private readonly RawPointerSurface _rawPointer = new();

        public IFramebufferViewportTarget CreateViewportTarget(
            GameWindowGraphics graphics) => Viewport;

        public GpuFrameFlightController? CreateGpuFrameFlights(
            GameWindowGraphics graphics) =>
            new(new FenceApi());

        public IGpuDevice CreateGpuDevice(
            GameWindowGraphics graphics,
            GpuFrameFlightController? frameFlights) =>
            new RecordingGpuDevice();

        public IGpuResourceRetirementQueue CreateRetirement(
            GameWindowGraphics graphics,
            GpuFrameFlightController? frameFlights,
            IGpuDevice device) => frameFlights!;

        public IRenderFrameSlotSource CreateFrameSlots(
            GameWindowGraphics graphics,
            GpuFrameFlightController? frameFlights,
            IGpuDevice device) => frameFlights!;

        public SilkKeyboardSource CreateKeyboardSource(
            IKeyboard keyboard,
            HostQuiescenceGate quiescence) =>
            SilkKeyboardSource.CreateDetached(_keyboard, quiescence);

        public SilkMouseSource CreateMouseSource(
            IMouse mouse,
            IInputCaptureSource capture,
            IKeyboardSource? keyboard,
            HostQuiescenceGate quiescence) =>
            SilkMouseSource.CreateDetached(_mouse, capture, keyboard, quiescence);

        public IMouseLookCursor CreateMouseLookCursor(IMouse mouse) => new Cursor();

        public InputDispatcher CreateInputDispatcher(
            IKeyboardSource keyboard,
            IMouseSource mouse,
            KeyBindings bindings) =>
            InputDispatcher.CreateDetached(keyboard, mouse, bindings);

        public CameraController CreateCameraController(
            float? initialOrbitDistanceMeters,
            float? initialOrbitYawDegrees,
            float? initialOrbitPitchDegrees)
        {
            var orbit = new OrbitCamera();
            if (initialOrbitDistanceMeters is { } distance)
                orbit.Distance = distance;
            if (initialOrbitYawDegrees is { } yaw)
                orbit.Yaw = yaw * (MathF.PI / 180f);
            if (initialOrbitPitchDegrees is { } pitch)
                orbit.Pitch = pitch * (MathF.PI / 180f);
            return new CameraController(orbit, new FlyCamera());
        }

        public IFramebufferCameraTarget CreateCameraTarget(CameraController camera) =>
            new CameraTarget(camera);

        public CameraPointerInputController CreateCameraPointerInput(
            IReadOnlyList<IMouse> mice,
            HostQuiescenceGate quiescence,
            IInputCaptureSource capture,
            LocalPlayerModeState playerMode,
            CameraController camera,
            ChaseCameraInputState chase,
            IMouseSource mouse,
            PointerPositionState pointer) =>
            new(
                [_rawPointer],
                new CursorModeTarget(),
                quiescence,
                capture,
                playerMode,
                camera,
                chase,
                mouse,
                pointer,
                new Clock());
    }

    private sealed class InputContext(IKeyboard keyboard, IMouse mouse)
        : IInputContext
    {
        public nint Handle => 1;
        public IReadOnlyList<IGamepad> Gamepads { get; } = [];
        public IReadOnlyList<IJoystick> Joysticks { get; } = [];
        public IReadOnlyList<IKeyboard> Keyboards { get; } = [keyboard];
        public IReadOnlyList<IMouse> Mice { get; } = [mouse];
        public IReadOnlyList<IInputDevice> OtherDevices { get; } = [];
        public event Action<IInputDevice, bool>? ConnectionChanged
        {
            add { }
            remove { }
        }
        public void Dispose() { }
    }

    public class NullDeviceProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            Type returnType = targetMethod?.ReturnType ?? typeof(void);
            if (returnType == typeof(void))
                return null;
            if (returnType == typeof(string))
                return string.Empty;
            return returnType.IsValueType
                ? Activator.CreateInstance(returnType)
                : null;
        }
    }

    private sealed class KeyboardSurface : IKeyboardEventSurface
    {
        public void AddKeyDown(Action<Key> callback) { }
        public void RemoveKeyDown(Action<Key> callback) { }
        public void AddKeyUp(Action<Key> callback) { }
        public void RemoveKeyUp(Action<Key> callback) { }
        public bool IsKeyPressed(Key key) => false;
    }

    private sealed class MouseSurface : IMouseEventSurface
    {
        public void AddMouseDown(Action<MouseButton> callback) { }
        public void RemoveMouseDown(Action<MouseButton> callback) { }
        public void AddMouseUp(Action<MouseButton> callback) { }
        public void RemoveMouseUp(Action<MouseButton> callback) { }
        public void AddMouseMove(Action<Vector2> callback) { }
        public void RemoveMouseMove(Action<Vector2> callback) { }
        public void AddScroll(Action<float> callback) { }
        public void RemoveScroll(Action<float> callback) { }
        public bool IsButtonPressed(MouseButton button) => false;
    }

    private sealed class RawPointerSurface : IRawPointerSurface
    {
        public void AddMouseMove(Action<Vector2> callback) { }
        public void RemoveMouseMove(Action<Vector2> callback) { }
    }

    private sealed class CursorModeTarget : IPointerCursorModeTarget
    {
        public CursorMode CursorMode { get; set; }
    }

    private sealed class Cursor : IMouseLookCursor
    {
        public bool HasSavedMode { get; private set; }
        public void Hide() => HasSavedMode = true;
        public void Restore() => HasSavedMode = false;
    }

    private sealed class Clock : IInputMonotonicClock
    {
        public float NowSeconds => 0;
    }

    private sealed class CaptureSource : IInputCaptureSource
    {
        public bool WantCaptureMouse => false;
        public bool WantCaptureKeyboard => false;
        public bool DevToolsWantCaptureKeyboard => false;
    }

    private sealed class ViewportTarget : IFramebufferViewportTarget
    {
        public (int Width, int Height) Size { get; private set; }
        public void ResizeViewport(int width, int height) => Size = (width, height);
    }

    private sealed class CameraTarget(CameraController camera)
        : IFramebufferCameraTarget
    {
        public void SetAspect(float aspect) => camera.SetAspect(aspect);
    }

    private sealed class FenceApi : IGpuFenceApi
    {
        public nint Insert() => 1;
        public GpuFenceWaitResult Wait(
            nint fence,
            bool flushCommands,
            ulong timeoutNanoseconds) => GpuFenceWaitResult.Signaled;
        public void Delete(nint fence) { }
    }

    private sealed class DiagnosticLog : IRenderFrameDiagnosticLog
    {
        public void WriteLine(string message) { }
    }

}

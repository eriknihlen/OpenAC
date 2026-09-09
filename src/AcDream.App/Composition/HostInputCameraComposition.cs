using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace AcDream.App.Composition;

internal interface IGameWindowHostInputCameraPublication
{
    void PublishGpuFrameFlights(GpuFrameFlightController? value);
    void PublishGpuDevice(IGpuDevice value);
    void PublishGpuFrameLifetime(GpuDeviceFrameLifetime value);
    void PublishKeyboardSource(SilkKeyboardSource value);
    void PublishMouseSource(SilkMouseSource value);
    void PublishMouseLookCursor(IMouseLookCursor value);
    void PublishInputDispatcher(InputDispatcher value);
    void PublishCameraController(CameraController value);
    void PublishCameraPointerInput(CameraPointerInputController value);
}

internal sealed record HostInputCameraResult(
    GpuFrameFlightController? GpuFrameFlights,
    IGpuResourceRetirementQueue Retirement,
    IRenderFrameSlotSource FrameSlots,
    IGpuDevice GpuDevice,
    GpuDeviceFrameLifetime GpuFrameLifetime,
    SilkKeyboardSource? KeyboardSource,
    SilkMouseSource? MouseSource,
    IMouseLookCursor? MouseLookCursor,
    InputDispatcher? InputDispatcher,
    CameraController CameraController,
    CameraPointerInputController? CameraPointerInput);

internal sealed record HostInputCameraDependencies(
    FramebufferResizeController FramebufferResize,
    Vector2D<int> InitialFramebufferSize,
    HostQuiescenceGate HostQuiescence,
    IInputCaptureSource InputCapture,
    KeyBindings KeyBindings,
    DispatcherMovementInputSource MovementInput,
    DispatcherCameraInputSource CameraInput,
    LocalPlayerModeState LocalPlayerMode,
    ChaseCameraInputState ChaseCameraInput,
    PointerPositionState PointerPosition,
    IRenderFrameDiagnosticLog RenderDiagnosticLog,
    float? InitialOrbitDistanceMeters = null,
    float? InitialOrbitYawDegrees = null,
    float? InitialOrbitPitchDegrees = null);

internal interface IHostInputCameraCompositionFactory
{
    IFramebufferViewportTarget CreateViewportTarget(GameWindowGraphics graphics);

    /// <summary>The GL fence ring, or null when the backend's RHI device owns its flights.</summary>
    GpuFrameFlightController? CreateGpuFrameFlights(GameWindowGraphics graphics);

    IGpuDevice CreateGpuDevice(
        GameWindowGraphics graphics,
        GpuFrameFlightController? frameFlights);

    /// <summary>Where per-frame resource release is queued. GL's ring, or the RHI device's own.</summary>
    IGpuResourceRetirementQueue CreateRetirement(
        GameWindowGraphics graphics,
        GpuFrameFlightController? frameFlights,
        IGpuDevice device);

    /// <summary>The ring slot renderers index their per-flight buffers by.</summary>
    IRenderFrameSlotSource CreateFrameSlots(
        GameWindowGraphics graphics,
        GpuFrameFlightController? frameFlights,
        IGpuDevice device);

    SilkKeyboardSource CreateKeyboardSource(
        IKeyboard keyboard,
        HostQuiescenceGate quiescence);
    SilkMouseSource CreateMouseSource(
        IMouse mouse,
        IInputCaptureSource capture,
        IKeyboardSource? keyboard,
        HostQuiescenceGate quiescence);
    IMouseLookCursor CreateMouseLookCursor(IMouse mouse);
    InputDispatcher CreateInputDispatcher(
        IKeyboardSource keyboard,
        IMouseSource mouse,
        KeyBindings bindings);
    CameraController CreateCameraController(
        float? initialOrbitDistanceMeters,
        float? initialOrbitYawDegrees,
        float? initialOrbitPitchDegrees);
    IFramebufferCameraTarget CreateCameraTarget(CameraController camera);
    CameraPointerInputController CreateCameraPointerInput(
        IReadOnlyList<IMouse> mice,
        HostQuiescenceGate quiescence,
        IInputCaptureSource capture,
        LocalPlayerModeState playerMode,
        CameraController camera,
        ChaseCameraInputState chase,
        IMouseSource mouse,
        PointerPositionState pointer);
}

internal enum HostInputCameraCompositionPoint
{
    ViewportBound,
    GpuFrameFlightsPublished,
    GpuDevicePublished,
    KeyboardPublished,
    KeyboardAttached,
    MousePublished,
    MouseAttached,
    MouseLookCursorPublished,
    DispatcherPublished,
    DispatcherAttached,
    MovementInputBound,
    CameraInputBound,
    CameraPublished,
    CameraTargetBound,
    InitialFramebufferApplied,
    CameraPointerPublished,
    CameraPointerAttached,
}

internal sealed class HostInputCameraCompositionPhase :
    IHostInputCameraCompositionPhase<
        GameWindowPlatformResult<GameWindowGraphics, IInputContext>,
        HostInputCameraResult>
{
    private readonly HostInputCameraDependencies _dependencies;
    private readonly IGameWindowHostInputCameraPublication _publication;
    private readonly IHostInputCameraCompositionFactory? _injectedFactory;
    private readonly Action<HostInputCameraCompositionPoint>? _faultInjection;
    private IHostInputCameraCompositionFactory _factory =
        new VulkanHostInputCameraCompositionFactory();

    public HostInputCameraCompositionPhase(
        HostInputCameraDependencies dependencies,
        IGameWindowHostInputCameraPublication publication,
        IHostInputCameraCompositionFactory? factory = null,
        Action<HostInputCameraCompositionPoint>? faultInjection = null)
    {
        _dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies));
        _publication = publication
            ?? throw new ArgumentNullException(nameof(publication));
        _injectedFactory = factory;
        _faultInjection = faultInjection;
    }

    private static IHostInputCameraCompositionFactory DefaultFactoryFor(
        GameWindowGraphics graphics) =>
        new VulkanHostInputCameraCompositionFactory();

    public HostInputCameraResult Compose(
        GameWindowPlatformResult<GameWindowGraphics, IInputContext> platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var scope = new CompositionAcquisitionScope();
        try
        {
            HostInputCameraResult result = ComposeCore(platform, scope);
            scope.Complete();
            return result;
        }
        catch (Exception failure)
        {
            scope.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    private HostInputCameraResult ComposeCore(
        GameWindowPlatformResult<GameWindowGraphics, IInputContext> platform,
        CompositionAcquisitionScope scope)
    {
        GameWindowGraphics graphics = platform.Graphics;
        IInputContext input = platform.Input;
        _factory = _injectedFactory ?? DefaultFactoryFor(graphics);

        _dependencies.FramebufferResize.BindViewport(
            _factory.CreateViewportTarget(graphics));
        Fault(HostInputCameraCompositionPoint.ViewportBound);

        // Null on a backend whose RHI device owns its own frame flights
        // (Vulkan's timeline semaphore). The publication still runs so the
        // teardown ledger records the same slot either way.
        GpuFrameFlightController? gpuFrames = scope.AcquireOptional(
            "GPU frame flights",
            () => _factory.CreateGpuFrameFlights(graphics),
            static value => value.Dispose()).Publish(
                _publication.PublishGpuFrameFlights);
        Fault(HostInputCameraCompositionPoint.GpuFrameFlightsPublished);

        IGpuDevice gpuDevice = scope.Acquire(
            "GPU device (RHI)",
            () => _factory.CreateGpuDevice(graphics, gpuFrames),
            static value => value.Dispose()).Publish(
                _publication.PublishGpuDevice);
        Fault(HostInputCameraCompositionPoint.GpuDevicePublished);
        IGpuResourceRetirementQueue retirement =
            _factory.CreateRetirement(graphics, gpuFrames, gpuDevice);
        IRenderFrameSlotSource frameSlots =
            _factory.CreateFrameSlots(graphics, gpuFrames, gpuDevice);

        var gpuFrameLifetime = new GpuDeviceFrameLifetime(gpuDevice);
        _publication.PublishGpuFrameLifetime(gpuFrameLifetime);

        IKeyboard? firstKeyboard = input.Keyboards.FirstOrDefault();
        IMouse? firstMouse = input.Mice.FirstOrDefault();
        SilkKeyboardSource? keyboard = null;
        SilkMouseSource? mouse = null;
        IMouseLookCursor? cursor = null;
        InputDispatcher? dispatcher = null;
        CameraPointerInputController? pointer = null;

        if (firstKeyboard is not null)
        {
            keyboard = scope.Acquire(
                "keyboard source",
                () => _factory.CreateKeyboardSource(
                    firstKeyboard,
                    _dependencies.HostQuiescence),
                static value => value.Dispose()).Publish(
                    _publication.PublishKeyboardSource);
            Fault(HostInputCameraCompositionPoint.KeyboardPublished);
            keyboard.Attach();
            Fault(HostInputCameraCompositionPoint.KeyboardAttached);
        }

        if (firstMouse is not null)
        {
            mouse = scope.Acquire(
                "mouse source",
                () => _factory.CreateMouseSource(
                    firstMouse,
                    _dependencies.InputCapture,
                    keyboard,
                    _dependencies.HostQuiescence),
                static value => value.Dispose()).Publish(
                    _publication.PublishMouseSource);
            Fault(HostInputCameraCompositionPoint.MousePublished);
            mouse.Attach();
            Fault(HostInputCameraCompositionPoint.MouseAttached);

            cursor = _factory.CreateMouseLookCursor(firstMouse);
            _publication.PublishMouseLookCursor(cursor);
            Fault(HostInputCameraCompositionPoint.MouseLookCursorPublished);
        }

        if (keyboard is not null && mouse is not null)
        {
            dispatcher = scope.Acquire(
                "input dispatcher",
                () => _factory.CreateInputDispatcher(
                    keyboard,
                    mouse,
                    _dependencies.KeyBindings),
                static value => value.Dispose()).Publish(
                    _publication.PublishInputDispatcher);
            Fault(HostInputCameraCompositionPoint.DispatcherPublished);
            dispatcher.Attach();
            Fault(HostInputCameraCompositionPoint.DispatcherAttached);

            _dependencies.MovementInput.Bind(dispatcher);
            Fault(HostInputCameraCompositionPoint.MovementInputBound);
            _dependencies.CameraInput.Bind(dispatcher);
            Fault(HostInputCameraCompositionPoint.CameraInputBound);
        }

        CameraController camera = _factory.CreateCameraController(
            _dependencies.InitialOrbitDistanceMeters,
            _dependencies.InitialOrbitYawDegrees,
            _dependencies.InitialOrbitPitchDegrees);
        _publication.PublishCameraController(camera);
        Fault(HostInputCameraCompositionPoint.CameraPublished);
        _dependencies.FramebufferResize.BindCamera(
            _factory.CreateCameraTarget(camera));
        Fault(HostInputCameraCompositionPoint.CameraTargetBound);
        _dependencies.FramebufferResize.Resize(
            _dependencies.InitialFramebufferSize);
        Fault(HostInputCameraCompositionPoint.InitialFramebufferApplied);

        if (mouse is not null && firstMouse is not null)
        {
            pointer = scope.Acquire(
                "camera pointer input",
                () => _factory.CreateCameraPointerInput(
                    input.Mice,
                    _dependencies.HostQuiescence,
                    _dependencies.InputCapture,
                    _dependencies.LocalPlayerMode,
                    camera,
                    _dependencies.ChaseCameraInput,
                    mouse,
                    _dependencies.PointerPosition),
                static value => value.Dispose()).Publish(
                    _publication.PublishCameraPointerInput);
            Fault(HostInputCameraCompositionPoint.CameraPointerPublished);
            pointer.AttachRaw();
            Fault(HostInputCameraCompositionPoint.CameraPointerAttached);
        }

        return new HostInputCameraResult(
            gpuFrames,
            retirement,
            frameSlots,
            gpuDevice,
            gpuFrameLifetime,
            keyboard,
            mouse,
            cursor,
            dispatcher,
            camera,
            pointer);
    }

    private void Fault(HostInputCameraCompositionPoint point) =>
        _faultInjection?.Invoke(point);
}

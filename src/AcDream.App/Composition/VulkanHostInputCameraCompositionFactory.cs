using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Composition;

internal sealed class VulkanHostInputCameraCompositionFactory
    : IHostInputCameraCompositionFactory
{
    public IFramebufferViewportTarget CreateViewportTarget(
        GameWindowGraphics graphics) =>
        new SwapchainRecreateViewportTarget(RequireContext(graphics).RequestRecreate);

    public GpuFrameFlightController? CreateGpuFrameFlights(
        GameWindowGraphics graphics) => null;

    public IGpuDevice CreateGpuDevice(
        GameWindowGraphics graphics,
        GpuFrameFlightController? frameFlights) =>
        RequireContext(graphics).Device;

    public IGpuResourceRetirementQueue CreateRetirement(
        GameWindowGraphics graphics,
        GpuFrameFlightController? frameFlights,
        IGpuDevice device) => device.Retirement;

    public IRenderFrameSlotSource CreateFrameSlots(
        GameWindowGraphics graphics,
        GpuFrameFlightController? frameFlights,
        IGpuDevice device) =>
        new VulkanRenderFrameSlotSource(RequireContext(graphics).Device);

    public SilkKeyboardSource CreateKeyboardSource(
        IKeyboard keyboard,
        HostQuiescenceGate quiescence) =>
        SilkKeyboardSource.CreateDetached(keyboard, quiescence);

    public SilkMouseSource CreateMouseSource(
        IMouse mouse,
        IInputCaptureSource capture,
        IKeyboardSource? keyboard,
        HostQuiescenceGate quiescence) =>
        SilkMouseSource.CreateDetached(mouse, capture, keyboard, quiescence);

    public IMouseLookCursor CreateMouseLookCursor(IMouse mouse) =>
        new SilkMouseLookCursor(mouse);

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
            orbit.Yaw = DegreesToRadians(yaw);
        if (initialOrbitPitchDegrees is { } pitch)
            orbit.Pitch = DegreesToRadians(pitch);
        return new CameraController(orbit, new FlyCamera());
    }

    private static float DegreesToRadians(float degrees) =>
        degrees * (MathF.PI / 180f);

    public IFramebufferCameraTarget CreateCameraTarget(CameraController camera) =>
        new CameraFramebufferTarget(camera);

    public CameraPointerInputController CreateCameraPointerInput(
        IReadOnlyList<IMouse> mice,
        HostQuiescenceGate quiescence,
        IInputCaptureSource capture,
        LocalPlayerModeState playerMode,
        CameraController camera,
        ChaseCameraInputState chase,
        IMouseSource mouse,
        PointerPositionState pointer) =>
        CameraPointerInputController.Create(
            mice,
            quiescence,
            capture,
            playerMode,
            camera,
            chase,
            mouse,
            pointer,
            new EnvironmentInputMonotonicClock());

    private static VulkanGraphicsContext RequireContext(
        GameWindowGraphics graphics) =>
        graphics.Vulkan
        ?? throw new InvalidOperationException(
            "The Vulkan host factory was composed against a backend with no " +
            "Vulkan context.");

    internal sealed class SwapchainRecreateViewportTarget(Action requestRecreate)
        : IFramebufferViewportTarget
    {
        private readonly Action _requestRecreate = requestRecreate
            ?? throw new ArgumentNullException(nameof(requestRecreate));

        public void ResizeViewport(int width, int height) => _requestRecreate();
    }

    private sealed class VulkanRenderFrameSlotSource(VulkanGpuDevice device)
        : IRenderFrameSlotSource
    {
        public int CurrentSlot => device.Flights.CurrentSlot;
    }
}

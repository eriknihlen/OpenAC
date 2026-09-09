using AcDream.App.Input;
using Silk.NET.Maths;

namespace AcDream.App.Rendering;

internal interface IFramebufferViewportTarget
{
    void ResizeViewport(int width, int height);
}


internal interface IFramebufferCameraTarget
{
    void SetAspect(float aspect);
}

internal sealed class CameraFramebufferTarget(CameraController camera)
    : IFramebufferCameraTarget
{
    private readonly CameraController _camera = camera
        ?? throw new ArgumentNullException(nameof(camera));

    public void SetAspect(float aspect) => _camera.SetAspect(aspect);
}

internal interface IFramebufferDevToolsTarget
{
    void ResetLayout(int width, int height);
}

/// <summary>Expected-owner lease for the optional renderer resize edge.</summary>
internal sealed class FramebufferDevToolsBinding : IDisposable
{
    private readonly FramebufferResizeController _owner;
    private readonly IFramebufferDevToolsTarget _target;
    private bool _disposed;

    public FramebufferDevToolsBinding(
        FramebufferResizeController owner,
        IFramebufferDevToolsTarget target)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _owner.BindDevTools(_target);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _owner.UnbindDevTools(_target);
        _disposed = true;
    }
}

internal sealed class FramebufferResizeController
{
    private readonly ViewportAspectState _viewportAspect;
    private IFramebufferViewportTarget? _viewport;
    private IFramebufferCameraTarget? _camera;
    private IFramebufferDevToolsTarget? _devTools;

    public FramebufferResizeController(ViewportAspectState viewportAspect) =>
        _viewportAspect = viewportAspect
            ?? throw new ArgumentNullException(nameof(viewportAspect));

    public void BindViewport(IFramebufferViewportTarget viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        BindOnce(ref _viewport, viewport, "viewport");
    }

    public void BindCamera(IFramebufferCameraTarget camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        BindOnce(ref _camera, camera, "camera");
    }

    public void BindDevTools(IFramebufferDevToolsTarget devTools)
    {
        ArgumentNullException.ThrowIfNull(devTools);
        BindOnce(ref _devTools, devTools, "developer tools");
    }

    public void UnbindDevTools(IFramebufferDevToolsTarget devTools)
    {
        ArgumentNullException.ThrowIfNull(devTools);
        if (ReferenceEquals(_devTools, devTools))
            _devTools = null;
    }

    public void Resize(Vector2D<int> newSize) => Resize(newSize.X, newSize.Y);

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        Console.WriteLine($"window: framebuffer resize event {width}x{height}");

        _viewport?.ResizeViewport(width, height);
        _viewportAspect.Update(width, height);
        _camera?.SetAspect(width / (float)height);
        _devTools?.ResetLayout(width, height);
    }

    private static void BindOnce<T>(ref T? slot, T value, string name)
        where T : class
    {
        if (slot is not null && !ReferenceEquals(slot, value))
            throw new InvalidOperationException(
                $"The framebuffer {name} target is already bound.");
        slot = value;
    }
}

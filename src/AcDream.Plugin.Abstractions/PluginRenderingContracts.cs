#pragma warning disable CS1591
namespace AcDream.Plugin.Abstractions;

/// <summary>Stable metadata used to create a plugin-owned overlay.</summary>
public sealed record PluginHudDescriptor(
    string Id,
    string Title,
    PluginHudBounds DefaultBounds,
    bool Visible = true,
    bool Movable = true,
    bool Resizable = true,
    bool ClickThrough = false,
    int Layer = 0);

public readonly record struct PluginHudBounds(double X, double Y, double Width, double Height);
public readonly record struct PluginPoint(double X, double Y);
public readonly record struct PluginRect(double X, double Y, double Width, double Height);
public readonly record struct PluginColor(byte R, byte G, byte B, byte A = 255);
public readonly record struct PluginTextStyle(string FontFamily, float Size, PluginColor Color, bool Bold = false);
public readonly record struct PluginHudInput(PluginHudInputKind Kind, PluginPoint Position, PluginMouseButton Button = PluginMouseButton.None, bool Shift = false, bool Control = false, bool Alt = false);
public enum PluginHudInputKind { PointerDown, PointerUp, Click, PointerMove }
public enum PluginMouseButton { None, Left, Middle, Right }

/// <summary>Immutable host-managed texture metadata.</summary>
public interface IPluginTexture : IDisposable
{
    string ResourceId { get; }
    int Width { get; }
    int Height { get; }
}

/// <summary>Commands emitted during one host-owned HUD frame.</summary>
public interface IPluginRenderSurface
{
    void BeginFrame();
    void DrawTexture(IPluginTexture texture, PluginRect destination, PluginColor color);
    void DrawLine(PluginPoint from, PluginPoint to, PluginColor color, float thickness);
    void DrawText(string text, PluginPoint position, PluginTextStyle style);
    void EndFrame();
}

/// <summary>A plugin-owned transparent overlay. Disposal unregisters all callbacks.</summary>
public interface IPluginHudRegistration : IDisposable
{
    bool IsVisible { get; set; }
    PluginHudBounds Bounds { get; set; }
    event Action<PluginHudInput>? Input;
    IPluginRenderSurface Surface { get; }
}

/// <summary>Host renderer services. Plugins never receive graphics-device types.</summary>
public interface IPluginRenderRegistry
{
    IPluginHudRegistration AddHud(PluginHudDescriptor descriptor);
    IPluginTexture? LoadTexture(string resourceId);
}

/// <summary>Headless renderer implementation.</summary>
public sealed class NoOpPluginRenderRegistry : IPluginRenderRegistry
{
    public static NoOpPluginRenderRegistry Instance { get; } = new();
    private NoOpPluginRenderRegistry() { }
    public IPluginHudRegistration AddHud(PluginHudDescriptor descriptor) => new NoOpHud(descriptor.DefaultBounds);
    public IPluginTexture? LoadTexture(string resourceId) => null;
    private sealed class NoOpHud(PluginHudBounds bounds) : IPluginHudRegistration
    {
        public bool IsVisible { get; set; }
        public PluginHudBounds Bounds { get; set; } = bounds;
        public event Action<PluginHudInput>? Input { add { } remove { } }
        public IPluginRenderSurface Surface { get; } = new NoOpSurface();
        public void Dispose() { }
    }
    private sealed class NoOpSurface : IPluginRenderSurface
    {
        public void BeginFrame() { }
        public void DrawTexture(IPluginTexture texture, PluginRect destination, PluginColor color) { }
        public void DrawLine(PluginPoint from, PluginPoint to, PluginColor color, float thickness) { }
        public void DrawText(string text, PluginPoint position, PluginTextStyle style) { }
        public void EndFrame() { }
    }
}

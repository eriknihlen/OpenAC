using System.Numerics;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.UI;

public sealed class UiViewport : UiElement
{
    public override bool ConsumesDatChildren => true;

    public System.Action? Clicked { get; set; }
    public System.Action<int, int>? ClickedAt { get; set; }

    public override bool HandlesClick => Clicked is not null || ClickedAt is not null;

    public override bool OnEvent(in UiEvent e)
    {
        if (Clicked is null && ClickedAt is null) return base.OnEvent(e);
        switch (e.Type)
        {
            case UiEventType.MouseDown: return true;
            case UiEventType.Click:
                Clicked?.Invoke();
                ClickedAt?.Invoke(e.Data1, e.Data2);
                return true;
        }
        return base.OnEvent(e);
    }

    /// <summary>Renderer that produces the off-screen texture. Set by GameWindow wiring (later task).</summary>
    public IUiViewportRenderer? Renderer { get; set; }

    internal GpuTextureSlot TextureSlot { get; set; } = GpuTextureSlot.Unassigned;

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (!Visible || !TextureSlot.IsAssigned) return;
        uint textureHandle = AcDream.App.Rendering.TextRenderer.ResolveExternalTextureSlot(TextureSlot);
        if (textureHandle == 0) return;
        bool bottomUp = Renderer?.TextureIsBottomUp ?? true;
        float v0 = bottomUp ? 1f : 0f;
        float v1 = bottomUp ? 0f : 1f;
        ctx.DrawSprite(textureHandle, 0f, 0f, Width, Height, 0f, v0, 1f, v1, Vector4.One);
    }
}

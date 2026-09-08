using System.Numerics;
using AcDream.App.Rendering;

namespace AcDream.App.UI;

public sealed class UiTextureElement : UiElement
{
    public UiTextureElement() => ClickThrough = true;

    public uint Texture { get; set; }
    public Vector4 Tint { get; set; } = Vector4.One;

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (Texture != 0u)
            ctx.DrawSprite(Texture, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Tint);
    }
}

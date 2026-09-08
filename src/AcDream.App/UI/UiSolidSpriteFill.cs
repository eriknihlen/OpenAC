using System;
using System.Numerics;

namespace AcDream.App.UI;

public sealed class UiSolidSpriteFill : UiElement
{
    public uint SpriteId { get; init; }
    public Func<uint, (uint tex, int w, int h)>? SpriteResolve { get; set; }

    public UiSolidSpriteFill()
    {
        // A backing field must never intercept pointer events meant for
        // whatever's stacked in front of/behind it (the buttons draw ON
        // TOP via their own later ReadOrder; nothing should route to this
        // leaf at all).
        ClickThrough = true;
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        var resolve = SpriteResolve;
        if (resolve is null || SpriteId == 0u) return;
        var (tex, tw, th) = resolve(SpriteId);
        if (tex == 0 || tw == 0 || th == 0) return;
        ctx.PushAlphaAbsolute(1f);
        try
        {
            ctx.DrawSprite(tex, 0f, 0f, Width, Height, 0f, 0f, Width / tw, Height / th, Vector4.One);
        }
        finally { ctx.PopAlpha(); }
    }
}

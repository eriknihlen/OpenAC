using System.Numerics;

namespace AcDream.App.UI;

public sealed class UiMarkupIcon : UiElement
{
    /// <summary>
    /// Resolves to (GL texture, native width, native height) each draw.
    /// <c>tex == 0</c> (or a non-positive extent) draws nothing — never
    /// throws, matching every other markup binding's "unresolvable at
    /// runtime is silent" rule (only a malformed literal throws, at Build).
    /// </summary>
    public Func<(uint tex, int w, int h)> IconSource { get; set; } =
        static () => (0u, 0, 0);

    public UiMarkupIcon() => ClickThrough = true;

    protected override void OnDraw(UiRenderContext ctx)
    {
        (uint tex, int w, int h) = IconSource();
        if (tex == 0u || w <= 0 || h <= 0 || Width <= 0f || Height <= 0f)
            return;

        float scale = MathF.Min(Width / w, Height / h);
        float drawWidth = w * scale;
        float drawHeight = h * scale;
        float x = (Width - drawWidth) * 0.5f;
        float y = (Height - drawHeight) * 0.5f;
        ctx.DrawSprite(tex, x, y, drawWidth, drawHeight, 0f, 0f, 1f, 1f, Vector4.One);
    }
}

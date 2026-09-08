using System.Numerics;

namespace AcDream.App.UI;

internal static class UiCheckLamp
{
    /// <summary>The lamp glyph's fixed on-screen size in px (both axes).</summary>
    public const float LampSize = 11f;

    public static readonly Vector4 CheckedOuter = new(0.36f, 0.58f, 0.12f, 1f);
    public static readonly Vector4 CheckedInner = new(0.52f, 1f, 0.08f, 1f);
    public static readonly Vector4 UncheckedOuter = new(0.26f, 0.22f, 0.13f, 1f);
    public static readonly Vector4 UncheckedInner = new(0.38f, 0.34f, 0.23f, 1f);

    public static void Draw(UiRenderContext ctx, float x, float y, bool isChecked)
    {
        Vector4 outer = isChecked ? CheckedOuter : UncheckedOuter;
        Vector4 inner = isChecked ? CheckedInner : UncheckedInner;
        ctx.DrawFill(x + 3f, y, 5f, 1f, outer);
        ctx.DrawFill(x + 1f, y + 1f, 9f, 2f, outer);
        ctx.DrawFill(x, y + 3f, 11f, 5f, outer);
        ctx.DrawFill(x + 1f, y + 8f, 9f, 2f, outer);
        ctx.DrawFill(x + 3f, y + 10f, 5f, 1f, outer);
        ctx.DrawFill(x + 3f, y + 3f, 5f, 5f, inner);
    }
}

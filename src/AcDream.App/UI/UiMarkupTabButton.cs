using System.Numerics;

namespace AcDream.App.UI;

public sealed class UiMarkupTabButton : UiSimpleButton
{
    private static readonly Vector4 ActiveText =
        new(0.94f, 0.76f, 0.18f, 1f);
    private static readonly Vector4 NormalText =
        new(0.78f, 0.76f, 0.67f, 1f);
    private static readonly Vector4 DisabledText =
        new(0.34f, 0.33f, 0.29f, 1f);
    private static readonly Vector4 Underline =
        new(0.77f, 0.59f, 0.12f, 1f);

    public Func<bool>? SelectedSource { get; set; }

    public bool IsSelected => SelectedSource?.Invoke() ?? false;

    public UiMarkupTabButton()
    {
        BackgroundColor = Vector4.Zero;
        BorderColor = Vector4.Zero;
        BorderThickness = 0f;
        Outline = true;
    }

    protected override void OnTick(double deltaSeconds)
    {
        base.OnTick(deltaSeconds);
        TextColor = !Enabled
            ? DisabledText
            : IsSelected ? ActiveText : NormalText;
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        base.OnDraw(ctx);
        if (IsSelected)
            ctx.DrawFill(2f, Height - 2f, MathF.Max(0f, Width - 4f), 1f, Underline);
    }
}

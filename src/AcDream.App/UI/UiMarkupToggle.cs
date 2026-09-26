using System.Numerics;

namespace AcDream.App.UI;

public sealed class UiMarkupToggle : UiElement
{
    public PluginUiPalette? ThemePalette { get; set; }

    public string Text { get; set; } = string.Empty;
    public Func<string?>? TextSource { get; set; }
    public Func<bool>? CheckedSource { get; set; }
    public UiDatFont? DatFont { get; set; }
    public Vector4 TextColor { get; set; } =
        new(0.86f, 0.84f, 0.74f, 1f);

    /// <summary>Re-read every frame in place of <see cref="TextColor"/>.</summary>
    public Func<Vector4>? TextColorSource { get; set; }
    public Action? Toggle { get; set; }

    public bool IsChecked => CheckedSource?.Invoke() ?? false;

    public override bool HandlesClick => true;

    private readonly UiKeyboardActivation _keyboardActivation = new();

    public override bool OnEvent(in UiEvent e)
    {
        if (_keyboardActivation.HandleEvent(in e, TabStop, Enabled, Toggle))
            return true;
        if (e.Type != UiEventType.Click || !Enabled)
            return false;
        Toggle?.Invoke();
        return true;
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (_keyboardActivation.Focused)
            ctx.DrawRectOutline(0f, 0f, Width, Height,
                new Vector4(1f, 0.82f, 0.25f, 1f), 1f);
        float checkY = MathF.Max(1f, (Height - UiCheckLamp.LampSize) * 0.5f);
        if (ThemePalette is { } p) p.DrawCheck(ctx, 1f, checkY, IsChecked);
        else UiCheckLamp.Draw(ctx, 1f, checkY, IsChecked);

        string caption = TextSource?.Invoke() ?? Text;
        Vector4 textColor = TextColorSource?.Invoke() ?? TextColor;
        Vector4 color = Enabled
            ? textColor
            : new Vector4(textColor.X, textColor.Y, textColor.Z, 0.42f);
        float y = DatFont is { } font
            ? (Height - font.LineHeight) * 0.5f
            : 1f;
        if (DatFont is { } dat)
            ctx.DrawStringDat(dat, caption, 17f, y, color, outline: ThemePalette is null);
        else
            ctx.DrawString(caption, 17f, y, color);
    }
}

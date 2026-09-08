using System.Numerics;

namespace AcDream.App.UI;

public sealed class UiMarkupToggle : UiElement
{
    public string Text { get; set; } = string.Empty;
    public Func<string?>? TextSource { get; set; }
    public Func<bool>? CheckedSource { get; set; }
    public UiDatFont? DatFont { get; set; }
    public Vector4 TextColor { get; set; } =
        new(0.86f, 0.84f, 0.74f, 1f);
    public Action? Toggle { get; set; }

    public bool IsChecked => CheckedSource?.Invoke() ?? false;

    public override bool HandlesClick => true;

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type != UiEventType.Click || !Enabled)
            return false;
        Toggle?.Invoke();
        return true;
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        UiCheckLamp.Draw(ctx, 1f, MathF.Max(1f, (Height - UiCheckLamp.LampSize) * 0.5f), IsChecked);

        string caption = TextSource?.Invoke() ?? Text;
        Vector4 color = Enabled
            ? TextColor
            : new Vector4(TextColor.X, TextColor.Y, TextColor.Z, 0.42f);
        float y = DatFont is { } font
            ? (Height - font.LineHeight) * 0.5f
            : 1f;
        if (DatFont is { } dat)
            ctx.DrawStringDat(dat, caption, 17f, y, color, outline: true);
        else
            ctx.DrawString(caption, 17f, y, color);
    }
}

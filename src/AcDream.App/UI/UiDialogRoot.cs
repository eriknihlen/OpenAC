using System.Numerics;

namespace AcDream.App.UI;

public sealed class UiDialogRoot : UiPanel
{
    public Action? Cancel { get; set; }

    public UiDialogRoot()
    {
        BackgroundColor = Vector4.Zero;
        BorderColor = Vector4.Zero;
        ClickThrough = false;
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type == UiEventType.KeyDown
            && e.Data0 == (int)Silk.NET.Input.Key.Escape)
        {
            Cancel?.Invoke();
        }

        return true;
    }
}

using System;

namespace AcDream.App.UI.Layout;

public sealed class UiVitalsRoot : UiDatElement
{
    public const uint GmVitalsClassId = 0x10000009u;
    public const uint GmFloatyVitalsClassId = 0x1000004Du;
    public const uint GmFloatySideVitalsClassId = 0x10000056u;

    public UiVitalsRoot(ElementInfo info, Func<uint, (uint tex, int w, int h)> resolve)
        : base(info, resolve)
    {
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type is UiEventType.MouseDown or UiEventType.RightDown
            && !PressConsumedByChrome(e.Target))
        {
            TrySetRetailState(
                ActiveRetailStateId == RetailUiStateIds.HideDetail
                    ? RetailUiStateIds.ShowDetail
                    : RetailUiStateIds.HideDetail);
        }
        return base.OnEvent(in e);
    }

    private bool PressConsumedByChrome(UiElement? target)
    {
        for (UiElement? w = target; w is not null && w != this; w = w.Parent)
            if (w.WindowMoveHandle || w is UiResizeGrip)
                return true;
        return false;
    }
}

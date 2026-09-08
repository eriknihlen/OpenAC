namespace AcDream.App.UI;

public sealed class UiOptionToggleSlider : UiElement, IUiChildrenAttachedListener
{
    public UiButton? Toggle { get; private set; }

    public UiScrollbar? Slider { get; private set; }

    public void OnChildrenAttached()
    {
        foreach (UiElement child in Children)
        {
            if (Toggle is null && child is UiButton button)
                Toggle = button;
            else if (Slider is null && child is UiScrollbar scrollbar)
                Slider = scrollbar;
        }
    }
}

using System;

namespace AcDream.App.UI;

public interface IRetainedPanelController : IDisposable
{
    void OnShown() { }

    void OnHidden() { }

    void OnDescendantFocusChanged(UiElement? focusedDescendant) { }
}

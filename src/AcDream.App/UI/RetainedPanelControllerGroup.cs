using System;

namespace AcDream.App.UI;

public sealed class RetainedPanelControllerGroup : IRetainedPanelController
{
    private readonly IRetainedPanelController[] _controllers;
    private bool _disposed;

    public RetainedPanelControllerGroup(params IRetainedPanelController[] controllers)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        _controllers = (IRetainedPanelController[])controllers.Clone();
        for (int i = 0; i < _controllers.Length; i++)
            ArgumentNullException.ThrowIfNull(_controllers[i]);
    }

    public void OnShown()
    {
        if (_disposed) return;
        for (int i = 0; i < _controllers.Length; i++)
            _controllers[i].OnShown();
    }

    public void OnHidden()
    {
        if (_disposed) return;
        for (int i = _controllers.Length - 1; i >= 0; i--)
            _controllers[i].OnHidden();
    }

    public void OnDescendantFocusChanged(UiElement? focusedDescendant)
    {
        if (_disposed) return;
        for (int i = 0; i < _controllers.Length; i++)
            _controllers[i].OnDescendantFocusChanged(focusedDescendant);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = _controllers.Length - 1; i >= 0; i--)
            _controllers[i].Dispose();
    }
}

using System;
using System.Collections.Generic;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.UI;

public sealed class RetailWindowOpacityController : IDisposable
{
    private static readonly HashSet<string> ChatWindowNames = new(StringComparer.Ordinal)
    {
        WindowNames.Chat,
        WindowNames.ChatWindow1,
        WindowNames.ChatWindow2,
        WindowNames.ChatWindow3,
        WindowNames.ChatWindow4,
    };

    private readonly RetailWindowManager _manager;
    private readonly HashSet<RetailWindowHandle> _focused = new();
    private bool _disposed;

    public RetailWindowOpacityController(
        RetailWindowManager manager,
        float defaultOpacity,
        float activeOpacity)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));

        (DefaultOpacity, ActiveOpacity) = ChatOpacityLink.SetActive(
            System.Math.Clamp(defaultOpacity, 0f, 1f),
            System.Math.Clamp(activeOpacity, 0f, 1f));

        _manager.WindowRegistered += OnWindowRegistered;
        _manager.WindowUnregistered += OnWindowUnregistered;
        foreach (RetailWindowHandle handle in _manager.Windows)
            if (ChatWindowNames.Contains(handle.Name))
                Attach(handle);
    }

    public float DefaultOpacity { get; private set; }

    public float ActiveOpacity { get; private set; }

    public void SetDefaultOpacity(float value)
    {
        if (_disposed) return;
        (DefaultOpacity, ActiveOpacity) = ChatOpacityLink.SetDefault(ActiveOpacity, value);
        ReapplyAll();
    }

    public void SetActiveOpacity(float value)
    {
        if (_disposed) return;
        (DefaultOpacity, ActiveOpacity) = ChatOpacityLink.SetActive(DefaultOpacity, value);
        ReapplyAll();
    }

    public void SetOpacity(float defaultOpacity, float activeOpacity)
    {
        if (_disposed) return;
        (DefaultOpacity, ActiveOpacity) = ChatOpacityLink.SetDefault(ActiveOpacity, defaultOpacity);
        (DefaultOpacity, ActiveOpacity) = ChatOpacityLink.SetActive(DefaultOpacity, activeOpacity);
        ReapplyAll();
    }

    private void OnWindowRegistered(RetailWindowHandle handle)
    {
        if (ChatWindowNames.Contains(handle.Name))
            Attach(handle);
    }

    private void OnWindowUnregistered(RetailWindowHandle handle)
    {
        handle.DescendantFocusChanged -= OnDescendantFocusChanged;
        _focused.Remove(handle);
    }

    private void Attach(RetailWindowHandle handle)
    {
        handle.DescendantFocusChanged += OnDescendantFocusChanged;
        Apply(handle, hasFocus: false);
    }

    private void OnDescendantFocusChanged(RetailWindowHandle handle, UiElement? focusedDescendant)
    {
        bool hasFocus = focusedDescendant is not null;
        if (hasFocus)
            _focused.Add(handle);
        else
            _focused.Remove(handle);
        Apply(handle, hasFocus);
    }

    private void Apply(RetailWindowHandle handle, bool hasFocus)
        => handle.SetOpacity(hasFocus ? ActiveOpacity : DefaultOpacity);

    private void ReapplyAll()
    {
        foreach (RetailWindowHandle handle in _manager.Windows)
            if (ChatWindowNames.Contains(handle.Name))
                Apply(handle, _focused.Contains(handle));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _manager.WindowRegistered -= OnWindowRegistered;
        _manager.WindowUnregistered -= OnWindowUnregistered;
        foreach (RetailWindowHandle handle in _manager.Windows)
            if (ChatWindowNames.Contains(handle.Name))
                handle.DescendantFocusChanged -= OnDescendantFocusChanged;
        _focused.Clear();
    }
}

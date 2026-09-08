using System;
using System.Collections.Generic;

namespace AcDream.App.UI;

public sealed class RetailWindowLockPresentationController : IDisposable
{
    private static readonly (uint LockedStart, uint LiveStart)[] AuthoredChromeBlocks =
    [
        (0x10000633u, 0x1000063Bu),
        (0x10000643u, 0x1000064Bu),
        (0x10000653u, 0x1000065Bu),
        (0x10000663u, 0x1000066Bu),
        (0x10000673u, 0x1000067Bu),
        (0x10000683u, 0x1000068Bu),
        (0x10000693u, 0x1000069Bu),
        (0x100006A5u, 0x100006ADu),
    ];

    private const uint SmartBoxLiveChromeStart = 0x100006CAu;

    private readonly RetailWindowManager _manager;
    private readonly Dictionary<RetailWindowHandle, WindowPresentation> _windows = new();
    private bool _disposed;

    public RetailWindowLockPresentationController(RetailWindowManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _manager.WindowRegistering += OnWindowRegistered;
        _manager.WindowUnregistered += OnWindowUnregistered;

        foreach (RetailWindowHandle handle in _manager.Windows)
            Attach(handle);
    }

    private void OnWindowRegistered(RetailWindowHandle handle) => Attach(handle);

    private void OnWindowUnregistered(RetailWindowHandle handle) => Detach(handle);

    private void Attach(RetailWindowHandle handle)
    {
        if (_windows.ContainsKey(handle))
            return;

        var presentation = WindowPresentation.Capture(handle.OuterFrame);
        _windows.Add(handle, presentation);
        handle.LockChanged += OnLockChanged;
        presentation.Apply(_manager.IsLocked);
    }

    private void Detach(RetailWindowHandle handle)
    {
        handle.LockChanged -= OnLockChanged;
        _windows.Remove(handle);
    }

    private void OnLockChanged(RetailWindowHandle handle, bool locked)
    {
        if (_windows.TryGetValue(handle, out WindowPresentation? presentation))
            presentation.Apply(locked);
    }

    private static bool IsAuthoredLockedChrome(uint id)
    {
        foreach ((uint start, _) in AuthoredChromeBlocks)
            if (id >= start && id < start + 8u)
                return true;
        return false;
    }

    private static bool IsAuthoredLiveChrome(uint id)
    {
        foreach ((_, uint start) in AuthoredChromeBlocks)
            if (id >= start && id < start + 8u)
                return true;
        return id >= SmartBoxLiveChromeStart && id < SmartBoxLiveChromeStart + 8u;
    }

    private sealed class WindowPresentation
    {
        private readonly List<UiElement> _authoredLockedChrome = new();
        private readonly List<(UiElement Element, bool VisibleWhenUnlocked)> _liveChrome = new();
        private readonly List<(UiNineSlicePanel Panel, bool VisibleWhenUnlocked)> _nineSlices = new();

        public static WindowPresentation Capture(UiElement outerFrame)
        {
            var presentation = new WindowPresentation();
            presentation.CaptureElement(outerFrame);
            return presentation;
        }

        private void CaptureElement(UiElement element)
        {
            if (element is UiNineSlicePanel nineSlice)
                _nineSlices.Add((nineSlice, nineSlice.DrawResizeAffordances));

            if (IsAuthoredLockedChrome(element.DatElementId))
            {
                _authoredLockedChrome.Add(element);
            }
            else if (IsAuthoredLiveChrome(element.DatElementId) || element is UiResizeGrip)
            {
                _liveChrome.Add((element, element.Visible));
            }

            foreach (UiElement child in element.Children)
                CaptureElement(child);
        }

        public void Apply(bool locked)
        {
            foreach (UiElement element in _authoredLockedChrome)
                element.Visible = locked;

            foreach ((UiElement element, bool visibleWhenUnlocked) in _liveChrome)
                element.Visible = !locked && visibleWhenUnlocked;

            foreach ((UiNineSlicePanel panel, bool visibleWhenUnlocked) in _nineSlices)
                panel.DrawResizeAffordances = !locked && visibleWhenUnlocked;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _manager.WindowRegistering -= OnWindowRegistered;
        _manager.WindowUnregistered -= OnWindowUnregistered;
        foreach (RetailWindowHandle handle in new List<RetailWindowHandle>(_windows.Keys))
            Detach(handle);
    }
}

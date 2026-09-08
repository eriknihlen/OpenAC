using System;

namespace AcDream.App.UI.Layout;

public sealed class VitalsSideBySideController
{
    private readonly UiRoot _root;
    private readonly Func<bool> _sideBySideVitals;
    private readonly string _stackedWindow;
    private readonly string _sideWindow;
    private bool? _applied;

    public VitalsSideBySideController(
        UiRoot root,
        Func<bool> sideBySideVitals,
        string stackedWindow,
        string sideWindow)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _sideBySideVitals = sideBySideVitals
            ?? throw new ArgumentNullException(nameof(sideBySideVitals));
        _stackedWindow = stackedWindow;
        _sideWindow = sideWindow;
    }

    /// <summary>The last applied bit (null before the first apply). Exposed for tests.</summary>
    public bool? Applied => _applied;

    public void Tick()
    {
        bool sideBySide = _sideBySideVitals();
        if (_applied == sideBySide) return;
        _applied = sideBySide;

        if (sideBySide)
        {
            _root.HideWindow(_stackedWindow);
            _root.ShowWindow(_sideWindow);
        }
        else
        {
            _root.ShowWindow(_stackedWindow);
            _root.HideWindow(_sideWindow);
        }
    }
}

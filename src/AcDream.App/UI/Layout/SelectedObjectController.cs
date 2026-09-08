using System;
using System.Numerics;
using AcDream.App.UI;
using AcDream.Core.Items;
using AcDream.Core.Selection;

namespace AcDream.App.UI.Layout;

public sealed class SelectedObjectController : IRetainedPanelController
{
    public const uint ContainerId   = 0x1000019E;
    public const uint NameId        = 0x1000019F;
    /// <summary>Selected-object overlay element id (states: ObjectSelected / StackedItemSelected).</summary>
    public const uint OverlayId     = 0x100001A0;
    public const uint HealthMeterId = 0x100001A1;
    public const uint ManaMeterId = 0x100001A2;
    public const uint StackSizeEntryId = 0x100001A3;
    public const uint StackSizeSliderId = 0x100001A4;

    private const double FlashSeconds = 0.25;

    private const int NameZOrderOnTop = 1_000_000;

    /// <summary>Z-order for the selection-flash overlay — above the health meter (so the green
    /// flash isn't hidden by the bar) but below the name (so the name stays readable).</summary>
    private const int OverlayZOrder = NameZOrderOnTop - 1;

    private const float NameBandHeight = 15f;

    // ── Found elements (any may be null for partial/test layouts) ───────────
    private readonly UiElement?     _name;
    private readonly UiDatElement?  _overlay;
    private readonly UiMeter?       _healthMeter;
    private readonly UiMeter?       _manaMeter;
    private readonly UiField?       _stackSizeEntry;
    private readonly UiScrollbar?   _stackSizeSlider;

    private readonly Func<uint, bool>    _isHealthTarget;
    private readonly Func<uint, bool>    _isOwnedByPlayer;
    private readonly Func<uint, string?> _resolveName;
    private readonly Func<uint, float>   _healthPercent;
    private readonly Func<uint, bool>    _hasHealth;
    private readonly Func<uint, uint>    _stackSize;
    private readonly Action<uint>        _sendQueryHealth;
    private readonly Func<uint, float>   _manaPercent;
    private readonly Action<uint>        _sendQueryItemMana;
    private readonly StackSplitQuantityState _splitQuantity;
    private readonly SelectionState _selection;
    private readonly Func<uint, bool> _isVendorSplitExempt;
    private readonly Func<uint, bool> _isCoinstack;
    private readonly Func<int> _coinTotal;
    private readonly Action<Action<uint, float>> _unsubscribeHealthChanged;
    private readonly Action<Action<uint, float, bool>> _unsubscribeItemManaChanged;
    private readonly Action<Action<ClientObject>> _unsubscribeObjectUpdated;

    private uint?   _current;
    private string? _currentName;
    private double  _flashRemaining;   // > 0 while the selection overlay is flashing
    private bool _changingSplitFromSlider;
    private bool _disposed;

    private static readonly Vector4 NameColor = new(1f, 1f, 1f, 1f);

    private SelectedObjectController(
        ImportedLayout layout,
        SelectionState selection,
        Action<Action<uint, float>>  subscribeHealthChanged,
        Action<Action<uint, float>>  unsubscribeHealthChanged,
        Action<Action<uint, float, bool>> subscribeItemManaChanged,
        Action<Action<uint, float, bool>> unsubscribeItemManaChanged,
        Func<uint, bool>    isHealthTarget,
        Func<uint, bool>    isOwnedByPlayer,
        Func<uint, string?> name,
        Func<uint, float>   healthPercent,
        Func<uint, bool>    hasHealth,
        Func<uint, uint>    stackSize,
        Action<uint>        sendQueryHealth,
        Func<uint, float>   manaPercent,
        Action<uint>        sendQueryItemMana,
        UiDatFont?          datFont,
        StackSplitQuantityState splitQuantity,
        Action<Action<ClientObject>> subscribeObjectUpdated,
        Action<Action<ClientObject>> unsubscribeObjectUpdated,
        Func<uint, bool>    isVendorSplitExempt,
        Func<uint, bool>?   isCoinstack,
        Func<int>?          coinTotal)
    {
        _isHealthTarget  = isHealthTarget;
        _isOwnedByPlayer = isOwnedByPlayer;
        _resolveName     = name;
        _healthPercent   = healthPercent;
        _hasHealth       = hasHealth;
        _stackSize       = stackSize;
        _sendQueryHealth = sendQueryHealth;
        _manaPercent = manaPercent;
        _sendQueryItemMana = sendQueryItemMana;
        _splitQuantity = splitQuantity ?? throw new ArgumentNullException(nameof(splitQuantity));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _isVendorSplitExempt = isVendorSplitExempt
            ?? throw new ArgumentNullException(nameof(isVendorSplitExempt));
        _isCoinstack = isCoinstack ?? (_ => false);
        _coinTotal = coinTotal ?? (() => 0);
        _unsubscribeHealthChanged = unsubscribeHealthChanged;
        _unsubscribeItemManaChanged = unsubscribeItemManaChanged;
        _unsubscribeObjectUpdated = unsubscribeObjectUpdated;

        // Find elements — silently skip absent ones (partial/test layouts).
        _name        = layout.FindElement(NameId);
        _overlay     = layout.FindElement(OverlayId)     as UiDatElement;
        _healthMeter = layout.FindElement(HealthMeterId) as UiMeter;
        _manaMeter   = layout.FindElement(ManaMeterId)   as UiMeter;
        _stackSizeEntry = layout.FindElement(StackSizeEntryId) as UiField;
        _stackSizeSlider = layout.FindElement(StackSizeSliderId) as UiScrollbar;

        // The selection-flash overlay must draw OVER the health meter (which spans the whole
        // strip) — otherwise the meter hides the green flash whenever a bar is visible (i.e.
        // for players/monsters). Float it just below the name so the name stays readable.
        if (_overlay is not null) _overlay.ZOrder = OverlayZOrder;

        if (_healthMeter is not null)
        {
            _healthMeter.Visible = false;
            _healthMeter.Fill = () => _current is uint g ? _healthPercent(g) : (float?)0f;
        }
        if (_manaMeter is not null)
        {
            _manaMeter.Visible = false;
            _manaMeter.Fill = () => _current is uint g ? _manaPercent(g) : (float?)0f;
        }

        if (_stackSizeEntry is not null)
        {
            _stackSizeEntry.Visible = false;
            _stackSizeEntry.OneLine = true;
            _stackSizeEntry.RightAligned = true;
            _stackSizeEntry.Selectable = true;
            _stackSizeEntry.ClearOnSubmit = false;
            _stackSizeEntry.RecordHistory = false;
            _stackSizeEntry.CharacterFilter = static c => c is >= '0' and <= '9';
            _stackSizeEntry.SelectAllOnFocus = true;
            _stackSizeEntry.OnSubmit = CommitStackEntry;
            _stackSizeEntry.OnFocusLost = CommitStackEntry;
        }
        if (_stackSizeSlider is not null)
        {
            _stackSizeSlider.Visible = false;
            _stackSizeSlider.Horizontal = true;
            _stackSizeSlider.SetScalarPosition(_splitQuantity.Ratio);
            _stackSizeSlider.ScalarChanged = OnStackSliderChanged;
        }

        if (_name is not null)
        {
            _name.ZOrder = NameZOrderOnTop;
            float nameWidth = _name.Width;
            var wrapFont = datFont;
            Func<int, UiText.Line[]> lineFor = index =>
            {
                var n = _currentName;
                if (string.IsNullOrEmpty(n))
                    return Array.Empty<UiText.Line>();
                (string first, string second) = WrapNameTwoLines(n, nameWidth, wrapFont);
                string text = index == 0 ? first : second;
                return text.Length == 0
                    ? Array.Empty<UiText.Line>()
                    : new[] { new UiText.Line(text, NameColor) };
            };
            for (int lineIndex = 0; lineIndex < 2; lineIndex++)
            {
                int captured = lineIndex;
                var label = new UiText
                {
                    Left = 0f,
                    Top = captured * NameBandHeight,
                    Width = _name.Width,
                    Height = NameBandHeight,
                    Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right,
                    Centered            = true,
                    OneLine             = true,
                    DatFont             = datFont,
                    ClickThrough        = true,
                    AcceptsFocus        = false,
                    IsEditControl       = false,
                    CapturesPointerDrag = false,
                    LinesProvider       = () => lineFor(captured),
                };
                _name.AddChild(label);
            }
        }

        // Register the handlers LAST so the initial state is fully set up first.
        _selection.Changed += OnSelectionTransition;
        _splitQuantity.Changed += OnSplitQuantityChanged;
        subscribeHealthChanged(OnHealthChanged);
        subscribeItemManaChanged(OnItemManaChanged);
        subscribeObjectUpdated(OnObjectUpdated);
        if (_selection.SelectedObjectId is { } initial)
            ApplySelection(initial);
    }

    public static SelectedObjectController Bind(
        ImportedLayout layout,
        SelectionState selection,
        Action<Action<uint, float>>  subscribeHealthChanged,
        Action<Action<uint, float>>  unsubscribeHealthChanged,
        Action<Action<uint, float, bool>> subscribeItemManaChanged,
        Action<Action<uint, float, bool>> unsubscribeItemManaChanged,
        Func<uint, bool>    isHealthTarget,
        Func<uint, bool>    isOwnedByPlayer,
        Func<uint, string?> name,
        Func<uint, float>   healthPercent,
        Func<uint, bool>    hasHealth,
        Func<uint, uint>    stackSize,
        Action<uint>        sendQueryHealth,
        Func<uint, float>   manaPercent,
        Action<uint>        sendQueryItemMana,
        UiDatFont?          datFont,
        StackSplitQuantityState splitQuantity,
        Action<Action<ClientObject>> subscribeObjectUpdated,
        Action<Action<ClientObject>> unsubscribeObjectUpdated,
        Func<uint, bool>    isVendorSplitExempt,
        Func<uint, bool>?   isCoinstack = null,
        Func<int>?          coinTotal = null)
        => new SelectedObjectController(
            layout, selection,
            subscribeHealthChanged, unsubscribeHealthChanged,
            subscribeItemManaChanged, unsubscribeItemManaChanged,
            isHealthTarget, isOwnedByPlayer, name, healthPercent, hasHealth, stackSize,
            sendQueryHealth, manaPercent, sendQueryItemMana, datFont,
            splitQuantity, subscribeObjectUpdated, unsubscribeObjectUpdated,
            isVendorSplitExempt, isCoinstack, coinTotal);

    private void ApplySelection(uint? guid)
    {
        bool selectionChanged = _current != guid;

        if (selectionChanged)
        {
            if (_healthMeter?.Visible == true)
                _sendQueryHealth(0);
            if (_manaMeter?.Visible == true)
                _sendQueryItemMana(0);
        }

        if (selectionChanged)
        {
            if (_healthMeter is not null) _healthMeter.Visible = false;
            if (_manaMeter is not null) _manaMeter.Visible = false;
        }
        if (_stackSizeEntry is not null) _stackSizeEntry.Visible = false;
        if (_stackSizeSlider is not null) _stackSizeSlider.Visible = false;
        _splitQuantity.Reset(1u);
        _currentName    = null;
        _current        = guid;

        if (guid is null)
        {
            SetOverlayState(UiStateInfo.DirectStateId);
            _flashRemaining = 0;
            return;
        }

        uint g = guid.Value;

        uint stackSize = _stackSize(g);
        string? objectName = _resolveName(g);
        _currentName = _isCoinstack(g) && _isOwnedByPlayer(g)
            ? $"{stackSize} {objectName} (of {_coinTotal()})"
            : stackSize > 1u && !string.IsNullOrEmpty(objectName)
                ? $"{stackSize} {objectName}"
                : objectName;

        SetOverlayState(stackSize > 1u
            ? RetailUiStateIds.StackedItemSelected
            : RetailUiStateIds.ObjectSelected);
        _flashRemaining = FlashSeconds;

        if (stackSize > 1u)
        {
            bool vendorSplitExempt = _isVendorSplitExempt(g);
            uint seed = vendorSplitExempt ? 1u : stackSize;
            _splitQuantity.Reset(stackSize, initialValue: seed);
            if (_stackSizeEntry is not null) _stackSizeEntry.Visible = true;
            if (_stackSizeSlider is not null) _stackSizeSlider.Visible = true;
        }

        if (stackSize <= 1u && _isHealthTarget(g))
        {
            if (selectionChanged)
                _sendQueryHealth(g);
            if (_hasHealth(g) && _healthMeter is not null)
                _healthMeter.Visible = true;
        }
        else if (stackSize <= 1u && _isOwnedByPlayer(g))
        {
            if (selectionChanged)
                _sendQueryItemMana(g);
        }
    }

    public void OnHealthChanged(uint guid, float percent)
    {
        if (_current is uint c && c == guid && _isHealthTarget(guid) && _healthMeter is not null)
            _healthMeter.Visible = true;
    }

    /// <summary>Per-frame tick: reverts the selection overlay after the brief flash window.</summary>
    public void Tick(double deltaSeconds)
    {
        if (_flashRemaining <= 0) return;
        _flashRemaining -= deltaSeconds;
        if (_flashRemaining <= 0)
            SetOverlayState(UiStateInfo.DirectStateId);   // flash done → overlay back to blank
    }

    private void SetOverlayState(uint state)
    {
        _overlay?.TrySetRetailState(state);
    }

    public void OnItemManaChanged(uint guid, float percent, bool valid)
    {
        if (_current != guid)
            return;

        if (!valid)
        {
            _sendQueryItemMana(0);
            return;
        }

        if (_manaMeter is not null)
            _manaMeter.Visible = true;
    }

    internal static (string First, string Second) WrapNameTwoLines(
        string name,
        float width,
        UiDatFont? font)
    {
        if (font is null || font.MeasureWidth(name) <= width)
            return (name, string.Empty);

        int breakAt = -1;
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != ' ')
                continue;
            if (font.MeasureWidth(name[..i]) <= width)
                breakAt = i;
            else
                break;
        }

        if (breakAt <= 0)
            return (name, string.Empty);
        return (name[..breakAt], name[(breakAt + 1)..].TrimStart());
    }

    private void CommitStackEntry(string text)
        => _splitQuantity.SetFromText(text);

    private void OnSplitQuantityChanged()
    {
        _stackSizeEntry?.SetText(_splitQuantity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!_changingSplitFromSlider)
            _stackSizeSlider?.SetScalarPosition(_splitQuantity.Ratio);
    }

    private void OnStackSliderChanged(float position)
    {
        _changingSplitFromSlider = true;
        try
        {
            _splitQuantity.SetFromSliderRatio(position);
        }
        finally
        {
            _changingSplitFromSlider = false;
        }
    }

    public bool FocusSplitStackEntry(uint objectId)
    {
        if (_current != objectId
            || _stackSize(objectId) <= 1u
            || _stackSizeEntry is null
            || !_stackSizeEntry.Visible)
        {
            return false;
        }

        _stackSizeEntry.FindRoot()?.SetKeyboardFocus(_stackSizeEntry);
        _stackSizeEntry.SelectAllText();
        return true;
    }

    private void OnObjectUpdated(ClientObject updated)
    {
        if (_current == updated.ObjectId && _stackSize(updated.ObjectId) != _splitQuantity.Maximum)
            ApplySelection(updated.ObjectId);
    }

    private void OnSelectionTransition(SelectionTransition transition)
    {
        _ = transition;
        ApplySelection(_selection.SelectedObjectId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selection.Changed -= OnSelectionTransition;
        _splitQuantity.Changed -= OnSplitQuantityChanged;
        _unsubscribeHealthChanged(OnHealthChanged);
        _unsubscribeItemManaChanged(OnItemManaChanged);
        _unsubscribeObjectUpdated(OnObjectUpdated);
    }
}

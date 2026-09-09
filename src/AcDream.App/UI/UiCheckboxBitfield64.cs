using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.UI.Layout;

namespace AcDream.App.UI;

public sealed class UiCheckboxBitfield64 : UiPanel
{
    public const uint TemplateCheckboxElementId = 0x10000219u;

    public readonly record struct Row(
        ulong LowMask, ulong HighMask, string Label, string? Tooltip,
        UiElement RowRoot, UiButton Toggle);

    private readonly List<Row> _rows = new();
    private float _contentHeight;

    public IReadOnlyList<Row> Rows => _rows;

    public ulong CurrentLow { get; private set; }

    public ulong CurrentHigh { get; private set; }

    private ulong _defaultLow, _defaultHigh;

    public IReadOnlyList<UiTemplateListEntry> Templates { get; }

    public uint CheckedLedSprite { get; }

    public uint UncheckedLedSprite { get; }

    public Func<uint, uint, UiElement?>? TemplateResolver { get; set; }

    public UiDatFont? LabelFont { get; set; }

    // SpriteResolve (forwarded to each row's built subtree) is inherited from UiPanel —
    // same resolver shape, no need to redeclare it.

    public Action<ulong, ulong>? ValueChanged { get; set; }

    public UiCheckboxBitfield64(
        IReadOnlyList<UiTemplateListEntry> templates,
        uint checkedLedSprite = 0u,
        uint uncheckedLedSprite = 0u)
    {
        Templates = templates;
        CheckedLedSprite = checkedLedSprite;
        UncheckedLedSprite = uncheckedLedSprite;
        BackgroundColor = Vector4.Zero;
        BorderColor = Vector4.Zero;
    }

    public void SetDefaultValue(ulong low, ulong high)
    {
        _defaultLow = low;
        _defaultHigh = high;
        if (_rows.Count == 0)
        {
            CurrentLow = low;
            CurrentHigh = high;
        }
    }

    public void RestoreDefaultValue()
    {
        CurrentLow = _defaultLow;
        CurrentHigh = _defaultHigh;
        RefreshRowVisuals();
        ValueChanged?.Invoke(CurrentLow, CurrentHigh);
    }

    public void SetCurrentValue(ulong low, ulong high)
    {
        CurrentLow = low;
        CurrentHigh = high;
        RefreshRowVisuals();
    }

    public UiButton? AddChild(ulong lowMask, ulong highMask, string label, string? tooltip = null)
    {
        if (Templates.Count == 0)
        {
            Console.WriteLine("[UI] UiCheckboxBitfield64.AddChild: no authored row template (property 0x64 empty) — cannot build a row.");
            return null;
        }
        Func<uint, uint, UiElement?>? resolver = TemplateResolver;
        if (resolver is null)
        {
            Console.WriteLine("[UI] UiCheckboxBitfield64.AddChild: TemplateResolver not wired yet — cannot build a row.");
            return null;
        }

        UiTemplateListEntry entry = Templates[0];
        UiElement? row = resolver(entry.TemplateLayoutId, entry.TemplateElementId);
        if (row is null)
        {
            Console.WriteLine($"[UI] UiCheckboxBitfield64.AddChild: resolver returned null for template 0x{entry.TemplateLayoutId:X8}/0x{entry.TemplateElementId:X8}.");
            return null;
        }

        UiButton? checkbox = FindCheckboxRecursive(row);
        if (checkbox is null)
        {
            Console.WriteLine($"[UI] UiCheckboxBitfield64.AddChild: resolved row template did not contain checkbox 0x{TemplateCheckboxElementId:X8} — row will not respond to clicks.");
            return null;
        }

        checkbox.Label = label;
        checkbox.TooltipText = tooltip;
        checkbox.OnClick = () => ToggleRow(lowMask, highMask, checkbox);

        row.Left = 0f;
        row.Top = _contentHeight;
        _contentHeight += row.Height;
        base.AddChild(row);

        Height = _contentHeight;

        var newRow = new Row(lowMask, highMask, label, tooltip, row, checkbox);
        _rows.Add(newRow);
        ApplyRowVisuals(newRow);
        return checkbox;
    }

    private static UiButton? FindCheckboxRecursive(UiElement node)
    {
        if (node.DatElementId == TemplateCheckboxElementId && node is UiButton button)
            return button;
        foreach (UiElement child in node.Children)
        {
            UiButton? found = FindCheckboxRecursive(child);
            if (found is not null) return found;
        }
        return null;
    }

    private bool IsAnySet(ulong lowMask, ulong highMask)
        => (CurrentLow & lowMask) != 0 || (CurrentHigh & highMask) != 0;

    private bool IsAllSet(ulong lowMask, ulong highMask)
        => (CurrentLow & lowMask) == lowMask && (CurrentHigh & highMask) == highMask;

    private void ToggleRow(ulong lowMask, ulong highMask, UiButton toggle)
    {
        bool turnOn = !IsAnySet(lowMask, highMask);
        if (turnOn)
        {
            CurrentLow |= lowMask;
            CurrentHigh |= highMask;
        }
        else
        {
            CurrentLow &= ~lowMask;
            CurrentHigh &= ~highMask;
        }
        ApplyRowVisualsForMask(lowMask, highMask, toggle);
        ValueChanged?.Invoke(CurrentLow, CurrentHigh);
    }

    private void RefreshRowVisuals()
    {
        foreach (Row row in _rows)
            ApplyRowVisuals(row);
    }

    private void ApplyRowVisuals(Row row) => ApplyRowVisualsForMask(row.LowMask, row.HighMask, row.Toggle);

    private void ApplyRowVisualsForMask(ulong lowMask, ulong highMask, UiButton toggle)
    {
        bool anySet = IsAnySet(lowMask, highMask);
        toggle.Selected = anySet;
        if (!anySet)
        {
            toggle.FaceFileOverride = null;
            return;
        }
        uint overrideSprite = IsAllSet(lowMask, highMask) ? CheckedLedSprite : UncheckedLedSprite;
        toggle.FaceFileOverride = overrideSprite != 0u ? overrideSprite : null;
    }
}

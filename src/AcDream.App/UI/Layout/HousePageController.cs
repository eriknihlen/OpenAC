using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

public sealed class HousePageController
{
    public const uint TextBoxId = 0x100001E6u;

    public sealed record Bindings(
        Func<IReadOnlyList<string>> Lines,
        Action? OnShown = null,
        Func<uint, uint, UiElement?>? TemplateResolver = null,
        Func<IReadOnlyList<HousePanelLine>>? PanelLines = null);

    private readonly UiTemplateListBox _listBox;
    private readonly Bindings _bindings;
    private IReadOnlyList<HousePanelLine> _lastLines = Array.Empty<HousePanelLine>();

    private HousePageController(UiTemplateListBox listBox, Bindings bindings)
    {
        _listBox = listBox;
        _bindings = bindings;
    }

    public static HousePageController? Bind(UiElement page, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(bindings);

        if (UiElement.FindDescendant(page, TextBoxId) is not UiTemplateListBox listBox)
        {
            Console.WriteLine(
                $"[UI] House tab: ListBox 0x{TextBoxId:X8} not found or not a template list box.");
            return null;
        }

        listBox.TemplateResolver = bindings.TemplateResolver;
        var controller = new HousePageController(listBox, bindings);
        controller.Refresh(controller.CurrentLines());
        return controller;
    }

    public void Tick()
    {
        IReadOnlyList<HousePanelLine> lines = CurrentLines();
        if (lines.SequenceEqual(_lastLines)) return;
        Refresh(lines);
    }

    public void OnShown() => _bindings.OnShown?.Invoke();

    private IReadOnlyList<HousePanelLine> CurrentLines()
    {
        if (_bindings.PanelLines is { } styled)
            return styled();

        IReadOnlyList<string> plain = _bindings.Lines();
        if (plain.Count == 0)
            return Array.Empty<HousePanelLine>();

        var projected = new HousePanelLine[plain.Count];
        for (int i = 0; i < plain.Count; i++)
            projected[i] = new HousePanelLine(plain[i], HousePanelTextColor.Normal);
        return projected;
    }

    private void Refresh(IReadOnlyList<HousePanelLine> lines)
    {
        _lastLines = lines;
        _listBox.Flush();
        foreach (HousePanelLine line in lines)
        {
            UiElement? row = _listBox.AddItemFromTemplateList(0);
            if (row is UiText text)
            {
                int colorIndex = (int)line.Color;
                Vector4 color = colorIndex >= 0 && colorIndex < text.FontColorPalette.Count
                    ? text.FontColorPalette[colorIndex]
                    : text.DefaultColor;
                text.LinesProvider = () => [new UiText.Line(line.Text, color)];
            }
        }
    }
}

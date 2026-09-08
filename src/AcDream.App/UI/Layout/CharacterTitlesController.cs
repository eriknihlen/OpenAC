using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Runtime;

namespace AcDream.App.UI.Layout;

public sealed class CharacterTitlesController : IDisposable
{
    public const uint CurrentDisplayTitleTextId = 0x1000052Fu;
    public const uint TitleListBoxId = 0x10000532u;
    public const uint SetDisplayButtonId = 0x10000535u;

    private const uint RowTextId = 0x10000537u;

    private const string UnknownTitleText = "Unknown";

    private readonly record struct Row(UiElement Root, uint TitleId);

    private readonly RuntimeCharacterTitleState _titles;
    private readonly Func<uint, string?> _resolveTitle;
    private readonly Func<uint, RuntimeCommandResult> _sendSetTitle;
    private readonly UiTemplateListBox _listBox;
    private readonly UiText? _displayText;
    private readonly UiButton? _setDisplayButton;
    private readonly List<Row> _rows = new();
    private uint? _selectedTitleId;
    private bool _disposed;

    private CharacterTitlesController(
        RuntimeCharacterTitleState titles,
        Func<uint, string?> resolveTitle,
        Func<uint, RuntimeCommandResult> sendSetTitle,
        UiTemplateListBox listBox,
        UiText? displayText,
        UiButton? setDisplayButton)
    {
        _titles = titles;
        _resolveTitle = resolveTitle;
        _sendSetTitle = sendSetTitle;
        _listBox = listBox;
        _displayText = displayText;
        _setDisplayButton = setDisplayButton;
    }

    public static CharacterTitlesController? Bind(
        UiElement layoutRoot,
        RuntimeCharacterTitleState titles,
        Func<uint, string?> resolveTitle,
        Func<uint, uint, UiElement?> templateResolver,
        Func<uint, RuntimeCommandResult> sendSetTitle)
    {
        ArgumentNullException.ThrowIfNull(layoutRoot);
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(resolveTitle);
        ArgumentNullException.ThrowIfNull(templateResolver);
        ArgumentNullException.ThrowIfNull(sendSetTitle);

        if (UiElement.FindDescendant(layoutRoot, TitleListBoxId) is not UiTemplateListBox listBox)
        {
            Console.WriteLine(
                $"[D.2b] CharacterTitlesController: ListBox 0x{TitleListBoxId:X8} not " +
                "found — the Titles page will not populate.");
            return null;
        }
        listBox.TemplateResolver = templateResolver;
        listBox.LineHeight = 24;

        uint scrollbarElementId = listBox.ScrollbarElementId;
        UiElement? scrollbarElement = scrollbarElementId == 0
            ? null
            : UiElement.FindDescendant(layoutRoot, scrollbarElementId);
        if (scrollbarElement is UiScrollbar scrollbar)
            scrollbar.Model = listBox.Scroll;
        else
            Console.WriteLine(
                $"[D.2b] CharacterTitlesController: scrollbar 0x{scrollbarElementId:X8} " +
                "not found — the Titles list will not scroll.");

        UiText? displayText =
            UiElement.FindDescendant(layoutRoot, CurrentDisplayTitleTextId) as UiText;
        UiButton? setDisplayButton =
            UiElement.FindDescendant(layoutRoot, SetDisplayButtonId) as UiButton;

        var controller = new CharacterTitlesController(
            titles, resolveTitle, sendSetTitle, listBox, displayText, setDisplayButton);
        controller.WireButton();

        titles.TableReplaced += controller.OnTableReplaced;
        titles.TitleAdded += controller.OnTitleAdded;
        titles.DisplayTitleChanged += controller.OnDisplayTitleChanged;

        controller.RebuildRows();
        controller.RefreshDisplayText();
        controller.RefreshButtonGhost();
        return controller;
    }

    private void WireButton()
    {
        if (_setDisplayButton is null) return;
        _setDisplayButton.OnClick = () =>
        {
            if (_selectedTitleId is not uint id || id == _titles.DisplayTitleId)
                return;
            _sendSetTitle(id);
        };
    }

    private void OnTableReplaced()
    {
        ClearSelection();
        RebuildRows();
        RefreshDisplayText();
        RefreshButtonGhost();
    }

    private void OnTitleAdded(uint titleId)
    {
        RebuildRows();
        RefreshButtonGhost();
    }

    private void OnDisplayTitleChanged(uint titleId)
    {
        ClearSelection();
        ApplyRowHighlights();
        RefreshDisplayText();
        RefreshButtonGhost();
    }

    private void ClearSelection() => _selectedTitleId = null;

    private void RebuildRows()
    {
        _listBox.FlushPreservingScroll();
        _rows.Clear();

        var candidates = new List<(uint Id, string Text)>();
        foreach (uint id in _titles.EarnedTitleIds)
        {
            if (id == 0) continue;
            string? text = _resolveTitle(id);
            if (text is null) continue;
            candidates.Add((id, text));
        }
        List<(uint Id, string Text)> sorted = candidates
            .OrderBy(static c => c.Text, StringComparer.Ordinal)
            .ThenBy(static c => c.Id)
            .ToList();

        foreach ((uint id, string text) in sorted)
        {
            UiElement? row = _listBox.AddItemFromTemplateList(0);
            if (row is null) continue;

            if (row is UiDatElement datRow)
            {
                datRow.ClickThrough = false;
                uint capturedId = id;
                datRow.OnClick = () => SelectRow(capturedId);
            }

            if (UiElement.FindDescendant(row, RowTextId) is UiText rowText)
            {
                UiText.Line[] lines = [new UiText.Line(text, rowText.DefaultColor)];
                rowText.LinesProvider = () => lines;
            }

            _rows.Add(new Row(row, id));
        }

        if (_selectedTitleId is uint selected && !_rows.Exists(r => r.TitleId == selected))
            _selectedTitleId = null;

        ApplyRowHighlights();
    }

    private void SelectRow(uint titleId)
    {
        if (_disposed) return;
        _selectedTitleId = titleId;
        ApplyRowHighlights();
        RefreshButtonGhost();
    }

    private void ApplyRowHighlights()
    {
        foreach (Row row in _rows)
        {
            if (row.Root is IUiDatStateful stateful)
            {
                stateful.TrySetRetailState(
                    row.TitleId == _selectedTitleId
                        ? UiButtonStateMachine.Highlight
                        : UiButtonStateMachine.Normal);
            }
        }
    }

    private void RefreshDisplayText()
    {
        if (_displayText is null) return;
        string text = _resolveTitle(_titles.DisplayTitleId) ?? UnknownTitleText;
        UiText.Line[] lines = [new UiText.Line(text, _displayText.DefaultColor)];
        _displayText.LinesProvider = () => lines;
    }

    private void RefreshButtonGhost()
    {
        if (_setDisplayButton is null) return;
        bool shouldGhost = _selectedTitleId is not uint id || id == _titles.DisplayTitleId;
        _setDisplayButton.TrySetRetailState(
            shouldGhost ? UiButtonStateMachine.Ghosted : UiButtonStateMachine.Normal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _titles.TableReplaced -= OnTableReplaced;
        _titles.TitleAdded -= OnTitleAdded;
        _titles.DisplayTitleChanged -= OnDisplayTitleChanged;
    }
}

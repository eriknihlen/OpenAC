using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using AcDream.Core.Journal;
using AcDream.Core.Ui;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

public sealed class JournalPageListController
{
    /// <summary>The layout the row template lives in — authored <c>0x63</c>.</summary>
    public const uint RowTemplateLayoutId = 0x21000067u;

    /// <summary>The row template element — authored <c>0x62</c>.</summary>
    public const uint RowTemplateElementId = 0x10000589u;

    private const uint ListId = 0x10000583u;
    private const uint DeleteButtonId = 0x10000585u;
    private const uint SearchFieldId = 0x10000587u;
    private const uint ResetButtonId = 0x10000588u;

    private const uint RowNumberId = 0x1000058Au;
    private const uint RowTitleId = 0x1000058Bu;
    private const uint RowTimerId = 0x1000058Cu;
    private const uint RowLabelId = 0x1000058Du;

    private static readonly Vector4 SelectedNameColor = Vector4.One;

    public sealed record Bindings(
        IRuntimeJournalView Journal,
        RuntimeJournalState Commands,
        Action<int> OpenPage,
        Func<uint, uint, UiElement?> TemplateResolver,
        Func<DateTime>? Now = null);

    private readonly Bindings _bindings;
    private readonly UiTemplateListBox? _list;
    private readonly UiField? _search;
    private readonly List<int> _rowPages = [];
    private readonly List<(int Page, UiText? Title, Vector4 Unselected)> _rows = [];

    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromSeconds(1d);

    private long _renderedRevision = -1;
    private string _renderedSearch = string.Empty;
    private int _selectedPage;
    private int _lastClickPage;
    private DateTime _lastClickAt;

    public JournalPageListController(UiElement page, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(page);
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));

        _list = UiElement.FindDescendant(page, ListId) as UiTemplateListBox;
        if (_list is not null)
            _list.TemplateResolver = bindings.TemplateResolver;

        _search = UiElement.FindDescendant(page, SearchFieldId) as UiField;

        if (UiElement.FindDescendant(page, DeleteButtonId) is UiButton delete)
            delete.OnClick = DeleteSelected;
        if (UiElement.FindDescendant(page, ResetButtonId) is UiButton reset)
        {
            reset.OnClick = () =>
            {
                _search?.SetText(string.Empty);
                Refresh();
            };
        }

        Refresh();
    }

    /// <summary>The 1-based page the list has selected, or 0.</summary>
    public int SelectedPage => _selectedPage;

    public IReadOnlyList<int> RowPages => _rowPages;

    public static bool PageContainsString(JournalPage page, string search)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (string.IsNullOrEmpty(search))
            return true;

        return page.Label.Contains(search, StringComparison.Ordinal)
            || page.Title.Contains(search, StringComparison.Ordinal)
            || page.Notes.Contains(search, StringComparison.Ordinal);
    }

    public void Tick()
    {
        string search = _search?.Text ?? string.Empty;
        if (_bindings.Journal.Snapshot.Revision != _renderedRevision
            || !string.Equals(search, _renderedSearch, StringComparison.Ordinal))
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        RuntimeJournalSnapshot snapshot = _bindings.Journal.Snapshot;
        _renderedRevision = snapshot.Revision;
        _renderedSearch = _search?.Text ?? string.Empty;

        IReadOnlyList<JournalPage> pages = _bindings.Journal.Pages;

        _rowPages.Clear();
        _rows.Clear();
        _list?.FlushPreservingScroll();

        for (int i = 0; i < pages.Count; i++)
        {
            JournalPage page = pages[i];
            if (!PageContainsString(page, _renderedSearch))
                continue;

            int pageNumber = i + 1;
            _rowPages.Add(pageNumber);
            if (_list is null)
                continue;

            UiElement? row = _list.AddItemFromTemplateList(0);
            if (row is null)
                continue;

            SetText(UiElement.FindDescendant(row, RowNumberId) as UiText,
                pageNumber.ToString(CultureInfo.InvariantCulture));
            var title = UiElement.FindDescendant(row, RowTitleId) as UiText;
            SetText(title, page.Title);
            SetText(UiElement.FindDescendant(row, RowTimerId) as UiText,
                page.IsTimerRunning
                    ? RetailDurationText.Format(page.RunningTimerSeconds)
                    : page.HasTimer
                        ? RetailDurationText.Format(page.TimerDuration.TotalSeconds)
                        : string.Empty);
            SetText(UiElement.FindDescendant(row, RowLabelId) as UiText, page.Label);

            _rows.Add((pageNumber, title, title?.DefaultColor ?? Vector4.One));

            int captured = pageNumber;
            if (row is UiDatElement clickable)
            {
                clickable.ClickThrough = false;
                clickable.OnClick = () => Click(captured);
            }
        }

        if (_selectedPage != 0 && !_rowPages.Contains(_selectedPage))
            _selectedPage = 0;

        ApplySelectionHighlight();
    }

    public void Click(int pageNumber)
    {
        DateTime now = _bindings.Now?.Invoke() ?? DateTime.UtcNow;

        if (pageNumber == _lastClickPage && now - _lastClickAt <= DoubleClickWindow)
        {
            _lastClickPage = 0;
            _lastClickAt = default;
            Open(pageNumber);
            return;
        }

        _lastClickPage = pageNumber;
        _lastClickAt = now;
        Select(pageNumber);
    }

    public void Select(int pageNumber)
    {
        _selectedPage = pageNumber;
        ApplySelectionHighlight();
    }

    public void Open(int pageNumber)
    {
        Select(pageNumber);
        _bindings.OpenPage(pageNumber);
    }

    private void DeleteSelected()
    {
        if (_selectedPage == 0)
            return;

        _bindings.Commands.DeletePage(_selectedPage);
        _selectedPage = 0;
        Refresh();
    }

    private void ApplySelectionHighlight()
    {
        foreach ((int pageNumber, UiText? title, Vector4 unselected) in _rows)
        {
            if (title is not null)
                title.DefaultColor = pageNumber == _selectedPage ? SelectedNameColor : unselected;
        }
    }

    private static void SetText(UiText? text, string value)
    {
        if (text is null) return;
        text.LinesProvider = () => [new UiText.Line(value, text.DefaultColor)];
    }
}

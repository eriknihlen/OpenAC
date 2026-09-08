using System;

namespace AcDream.App.UI.Layout;

public sealed class JournalPanelController : IRetainedPanelController
{
    public const uint HostLayoutId = 0x2100006Eu;

    public const uint SlotElementId = 0x10000559u;

    public const uint ContractsPageId = 0x100005D4u;

    public const uint NotesPageId = 0x10000563u;

    public const uint PageListPageId = 0x10000564u;

    private const uint CloseButtonId = 0x10000562u;

    private readonly UiTabPanel _tabPanel;
    private readonly JournalContractsPageController? _contracts;
    private readonly JournalNotesPageController? _notes;
    private JournalPageListController? _pageList;
    private readonly Action<uint, uint> _onActivePageChanged;
    private bool _disposed;

    /// <summary>Root element of the imported panel — the tab host itself.</summary>
    public UiElement Root => _tabPanel;

    public UiTabPanel TabPanel => _tabPanel;

    public JournalContractsPageController? Contracts => _contracts;

    public JournalNotesPageController? Notes => _notes;

    public JournalPageListController? PageList => _pageList;

    private readonly Action _saveJournal;

    private JournalPanelController(
        UiTabPanel tabPanel,
        JournalContractsPageController? contracts,
        JournalNotesPageController? notes,
        JournalPageListController? pageList,
        Action saveJournal)
    {
        _tabPanel = tabPanel;
        _contracts = contracts;
        _notes = notes;
        _pageList = pageList;
        _saveJournal = saveJournal;

        _onActivePageChanged = (previous, _) =>
        {
            if (previous == NotesPageId)
            {
                _notes?.OnHidden();
                _saveJournal();
            }
        };
        _tabPanel.ActivePageChanged += _onActivePageChanged;
    }

    public sealed record Callbacks(
        Action Toggle,
        JournalContractsPageController.Bindings Contracts,
        JournalNotesPageController.Bindings Notes,
        Action SaveJournal,
        Func<Action<int>, JournalPageListController.Bindings> PageList);

    public static JournalPanelController? Bind(ImportedLayout layout, Callbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(callbacks);

        if (layout.Root is not UiTabPanel tabPanel)
        {
            Console.WriteLine(
                "[D.2b] JournalPanelController.Bind: root did not build as UiTabPanel "
                + $"(actual type {layout.Root.GetType().Name}) — journal panel will not open.");
            return null;
        }

        if (layout.FindElement(CloseButtonId) is UiButton close)
            close.OnClick = callbacks.Toggle;

        JournalContractsPageController? contracts = null;
        if (layout.FindElement(ContractsPageId) is { } contractsPage)
            contracts = new JournalContractsPageController(contractsPage, callbacks.Contracts);
        else
            Console.WriteLine("[D.2b] JournalPanelController: contracts page not found.");

        JournalNotesPageController? notes = null;
        if (layout.FindElement(NotesPageId) is { } notesPage)
            notes = new JournalNotesPageController(notesPage, callbacks.Notes);
        else
            Console.WriteLine("[D.2b] JournalPanelController: notes page not found.");

        JournalPageListController? pageList = null;
        var built = new JournalPanelController(
            tabPanel, contracts, notes, pageList: null, callbacks.SaveJournal);
        if (layout.FindElement(PageListPageId) is { } listPage)
        {
            // The index opens a page on the NOTES tab, so it needs the panel
            // that owns the tab switch — hence the deferred binding.
            pageList = new JournalPageListController(
                listPage,
                callbacks.PageList(pageNumber =>
                {
                    notes?.CommitText();
                    callbacks.Notes.Commands.GotoPage(pageNumber);
                    built.ShowNotes();
                }));
        }
        else
        {
            Console.WriteLine("[D.2b] JournalPanelController: page list not found.");
        }

        built.AttachPageList(pageList);
        return built;
    }

    public void ActivateTabs() => _tabPanel.ActivateTabBehavior();

    public void ShowContracts() => _tabPanel.SwitchTo(ContractsPageId);

    /// <summary>Switches to the notes tab — what opening a page from the index does.</summary>
    public void ShowNotes() => _tabPanel.SwitchTo(NotesPageId);

    /// <summary>Switches to the authored journal index tab.</summary>
    public void ShowPageList() => _tabPanel.SwitchTo(PageListPageId);

    private void AttachPageList(JournalPageListController? pageList)
        => _pageList = pageList;

    public void Tick()
    {
        if (_disposed) return;
        _contracts?.Tick();
        _notes?.Tick();
        _pageList?.Tick();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tabPanel.ActivePageChanged -= _onActivePageChanged;

        _notes?.OnHidden();
        _saveJournal();
    }
}

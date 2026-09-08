using System.Numerics;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.UI.Layout;

internal sealed class CharacterManagementUiController : IDisposable
{
    internal const uint RootEnum = 0x10000005u;
    internal const uint RootElementId = 0x1000039Au;
    internal const uint WorldTextElementId = 0x1000039Bu;
    internal const uint ListElementId = 0x1000039Du;
    internal const uint CreateElementId = 0x100003A0u;
    internal const uint EnterElementId = 0x100003A2u;
    internal const uint DeleteElementId = 0x1000039Fu;
    internal const uint RestoreElementId = 0x1000039Eu;
    internal const uint CreditsElementId = 0x100003A3u;
    internal const uint ExitElementId = 0x100003A4u;

    internal sealed record DialogStrings(
        Func<string, string> DeleteConfirmation,
        string DeleteResponse,
        string PleaseWait,
        string EnteringWorld,
        string ConfirmExit);

    private readonly UiRoot _host;
    private readonly ImportedLayout _layout;
    private readonly UiText _worldText;
    private readonly UiTemplateListBox _list;
    private readonly UiButton _create;
    private readonly UiButton _enter;
    private readonly UiButton _delete;
    private readonly UiButton _restore;
    private readonly UiButton _credits;
    private readonly UiButton _exit;
    private readonly RetailDialogFactory _dialogs;
    private readonly CharacterSelectionRuntimeBindings _bindings;
    private readonly DialogStrings _strings;
    private readonly List<UiButton> _rows = [];
    private readonly Dictionary<UiButton, uint> _rowIds = [];

    private Vector2 _authoredCanvas;
    private RuntimeGenerationToken _lastGeneration;
    private long _lastRevision = long.MinValue;
    private string _lastWorldName = string.Empty;
    private uint _deleteDialogContext;
    private uint _operationWaitContext;
    private uint _enterWaitContext;
    private uint _errorDialogContext;
    private uint _confirmExitDialogContext;
    private bool _active;
    private bool _presentationSuppressed;
    private bool _restoreCommandInFlight;
    private bool _suppressDialogCallbacks;
    private bool _disposed;

    private CharacterManagementUiController(
        UiRoot host,
        ImportedLayout layout,
        UiText worldText,
        UiTemplateListBox list,
        UiButton create,
        UiButton enter,
        UiButton delete,
        UiButton restore,
        UiButton credits,
        UiButton exit,
        RetailDialogFactory dialogs,
        CharacterSelectionRuntimeBindings bindings,
        DialogStrings strings,
        Action? openCredits)
    {
        _host = host;
        _layout = layout;
        _worldText = worldText;
        _list = list;
        _create = create;
        _enter = enter;
        _delete = delete;
        _restore = restore;
        _credits = credits;
        _exit = exit;
        _dialogs = dialogs;
        _bindings = bindings;
        _strings = strings;

        Root.Left = 0f;
        Root.Top = 0f;
        Root.ClickThrough = false;
        Root.Visible = false;

        _authoredCanvas = new Vector2(
            Root.Width > 0f ? Root.Width : 800f,
            Root.Height > 0f ? Root.Height : 600f);

        _create.Visible = true;
        _create.Enabled = false;
        _create.OnClick = RequestCreate;
        _enter.OnClick = EnterSelected;
        _delete.OnClick = RequestDelete;
        _restore.OnClick = RestoreSelected;

        _credits.Visible = true;
        _credits.Enabled = openCredits is not null;
        _credits.OnClick = openCredits;
        _exit.OnClick = RequestExit;

        _worldText.LinesProvider =
            () => [new UiText.Line(_lastWorldName, _worldText.DefaultColor)];
    }

    internal UiElement Root => _layout.Root;
    internal IReadOnlyList<UiButton> Rows => _rows;
    internal uint DeleteDialogContext => _deleteDialogContext;
    internal uint OperationWaitContext => _operationWaitContext;
    internal uint EnterWaitContext => _enterWaitContext;
    internal uint ErrorDialogContext => _errorDialogContext;
    internal uint ConfirmExitDialogContext => _confirmExitDialogContext;

    internal void ResetSession()
    {
        if (_disposed)
            return;
        _presentationSuppressed = false;
        Deactivate();
        _lastRevision = long.MinValue;
    }

    internal static CharacterManagementUiController? Bind(
        UiRoot host,
        ImportedLayout layout,
        Func<uint, uint, UiElement?> templateResolver,
        RetailDialogFactory dialogs,
        CharacterSelectionRuntimeBindings bindings,
        DialogStrings strings,
        Action? openCredits = null)
    {
        CharacterManagementUiController? controller = CreateDetached(
            host,
            layout,
            templateResolver,
            dialogs,
            bindings,
            strings,
            openCredits);
        if (controller is null)
            return null;

        try
        {
            controller.AttachAndTick();
            return controller;
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    internal static CharacterManagementUiController? CreateDetached(
        UiRoot host,
        ImportedLayout layout,
        Func<uint, uint, UiElement?> templateResolver,
        RetailDialogFactory dialogs,
        CharacterSelectionRuntimeBindings bindings,
        DialogStrings strings,
        Action? openCredits = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(templateResolver);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(strings);

        if (ContainsViewport(layout.Root))
        {
            Console.WriteLine(
                "[UI] character management: refusing an unapproved model-preview viewport.");
            return null;
        }

        if (layout.Root.DatElementId != RootElementId
            || layout.FindElement(WorldTextElementId) is not UiText worldText
            || layout.FindElement(ListElementId) is not UiTemplateListBox list
            || layout.FindElement(CreateElementId) is not UiButton create
            || layout.FindElement(EnterElementId) is not UiButton enter
            || layout.FindElement(DeleteElementId) is not UiButton delete
            || layout.FindElement(RestoreElementId) is not UiButton restore
            || layout.FindElement(CreditsElementId) is not UiButton credits
            || layout.FindElement(ExitElementId) is not UiButton exit)
        {
            Console.WriteLine(
                "[UI] character management: the authored root/list/button contract is incomplete.");
            return null;
        }

        list.TemplateResolver = templateResolver;
        try
        {
            return new CharacterManagementUiController(
                host,
                layout,
                worldText,
                list,
                create,
                enter,
                delete,
                restore,
                credits,
                exit,
                dialogs,
                bindings,
                strings,
                openCredits);
        }
        catch
        {
            list.TemplateResolver = null;
            create.OnClick = null;
            enter.OnClick = null;
            delete.OnClick = null;
            restore.OnClick = null;
            credits.OnClick = null;
            exit.OnClick = null;
            throw;
        }
    }

    internal void AttachAndTick()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Root.Parent is null)
            _host.AddChild(Root);
        Tick();
    }

    private static bool ContainsViewport(UiElement element)
    {
        if (element is UiViewport)
            return true;
        foreach (UiElement child in element.Children)
            if (ContainsViewport(child))
                return true;
        return false;
    }

    internal void Tick()
    {
        if (_disposed)
            return;

        if (_presentationSuppressed)
        {
            Deactivate();
            _lastRevision = long.MinValue;
            return;
        }

        IRuntimeCharacterSelectionView? view = _bindings.View();
        RuntimeCharacterSelectionSnapshot snapshot = view?.Snapshot ?? default;
        if (view is null || !snapshot.IsActive)
        {
            Deactivate();
            _lastGeneration = snapshot.Generation;
            _lastRevision = snapshot.Revision;
            return;
        }

        if (!_active)
        {
            _active = true;
            Root.Visible = true;
            _host.DeclareFixedCanvas(this, _authoredCanvas);
            _host.BringToFront(Root);
        }

        _lastWorldName = snapshot.WorldName;

        if (_lastGeneration != snapshot.Generation
            || _lastRevision != snapshot.Revision)
        {
            if (TryCaptureRoster(view, snapshot, out RuntimeCharacterSelectionEntry[] roster))
            {
                bool rowsReady;
                if (RowsMatchRoster(roster, snapshot.SlotCount))
                {
                    ApplyHighlight(snapshot.HighlightedCharacterId);
                    rowsReady = true;
                }
                else
                {
                    rowsReady = RebuildRows(
                        roster,
                        snapshot.SlotCount,
                        snapshot.HighlightedCharacterId);
                }

                if (rowsReady)
                {
                    _lastGeneration = snapshot.Generation;
                    _lastRevision = snapshot.Revision;
                }
            }
            else
            {
                _lastRevision = long.MinValue;
                snapshot = view.Snapshot;
                if (!snapshot.IsActive)
                {
                    Deactivate();
                    _lastGeneration = snapshot.Generation;
                    _lastRevision = snapshot.Revision;
                    return;
                }
            }
        }
        else
        {
            ApplyHighlight(snapshot.HighlightedCharacterId);
        }

        ApplyButtons(snapshot.Buttons);
        ReconcileDialogs(view, snapshot);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            CloseAllDialogs(suppressCallbacks: true);
        }
        finally
        {
            _host.RevokeFixedCanvas(this);
            _enter.OnClick = null;
            _delete.OnClick = null;
            _restore.OnClick = null;
            _credits.OnClick = null;
            _exit.OnClick = null;
            foreach (UiButton row in _rows)
            {
                row.OnClick = null;
                row.OnDoubleClick = null;
            }
            _rows.Clear();
            _rowIds.Clear();
            _list.Flush();
            _list.TemplateResolver = null;
            _host.RemoveChild(Root);
        }
    }

    internal void SetPresentationSuppressed(bool suppressed)
    {
        if (_disposed || _presentationSuppressed == suppressed)
            return;
        _presentationSuppressed = suppressed;
        _lastRevision = long.MinValue;
        Tick();
    }

    private static bool TryCaptureRoster(
        IRuntimeCharacterSelectionView view,
        RuntimeCharacterSelectionSnapshot expected,
        out RuntimeCharacterSelectionEntry[] roster)
    {
        roster = new RuntimeCharacterSelectionEntry[expected.RosterCount];
        for (int i = 0; i < roster.Length; i++)
        {
            if (!view.TryGetAt(i, out roster[i]))
                return false;
        }

        RuntimeCharacterSelectionSnapshot after = view.Snapshot;
        return after.Generation == expected.Generation
            && after.Revision == expected.Revision
            && after.RosterCount == expected.RosterCount;
    }

    private bool RowsMatchRoster(
        IReadOnlyList<RuntimeCharacterSelectionEntry> roster,
        int allowedSlotCount)
    {
        if (_rows.Count != roster.Count)
            return false;

        int rowHeight = ComputeRowHeight(
            _list.Height,
            roster.Count,
            allowedSlotCount);
        for (int i = 0; i < roster.Count; i++)
        {
            UiButton row = _rows[i];
            RuntimeCharacterSelectionEntry character = roster[i];
            if (!_rowIds.TryGetValue(row, out uint characterId)
                || characterId != character.CharacterId
                || !string.Equals(row.Label, character.Name, StringComparison.Ordinal)
                || (int)row.Height != rowHeight
                || row.LabelColor != (character.IsPendingDelete
                    ? new Vector4(1f, 0f, 0f, 1f)
                    : Vector4.One))
            {
                return false;
            }
        }

        return true;
    }

    private bool RebuildRows(
        IReadOnlyList<RuntimeCharacterSelectionEntry> roster,
        int allowedSlotCount,
        uint highlightedCharacterId)
    {
        foreach (UiButton row in _rows)
        {
            row.OnClick = null;
            row.OnDoubleClick = null;
        }
        _rows.Clear();
        _rowIds.Clear();
        _list.Flush();

        int rowHeight = ComputeRowHeight(
            _list.Height,
            roster.Count,
            allowedSlotCount);
        _list.LineHeight = rowHeight;
        bool complete = _list.Templates.Count > 0
            && _list.TemplateResolver is not null;
        foreach (RuntimeCharacterSelectionEntry character in roster)
        {
            if (!complete)
                break;

            UiTemplateListEntry template = _list.Templates[0];
            if (_list.TemplateResolver!(
                    template.TemplateLayoutId,
                    template.TemplateElementId) is not UiButton row)
            {
                complete = false;
                break;
            }

            row.Height = rowHeight;
            _list.AddPrebuiltRow(row);
            uint characterId = character.CharacterId;
            row.Label = character.Name;
            row.LabelColor = character.IsPendingDelete
                ? new Vector4(1f, 0f, 0f, 1f)
                : Vector4.One;
            row.Enabled = true;
            row.SuppressSelfToggle = true;
            row.Selected = characterId == highlightedCharacterId;
            row.OnClick = () => Highlight(characterId);
            row.OnDoubleClick = EnterSelected;
            _rows.Add(row);
            _rowIds.Add(row, characterId);
        }

        if (complete)
            return true;

        foreach (UiButton row in _rows)
        {
            row.OnClick = null;
            row.OnDoubleClick = null;
        }
        _rows.Clear();
        _rowIds.Clear();
        _list.Flush();
        _lastRevision = long.MinValue;
        return false;
    }

    internal static int ComputeRowHeight(
        float listHeight,
        int rosterCount,
        int allowedSlotCount)
    {
        int height = (int)MathF.Truncate(listHeight);
        int denominator = Math.Max(rosterCount, allowedSlotCount);
        if (denominator <= 0)
            return height / 10;
        return Math.Max(height / denominator, height / 10);
    }

    private void ApplyHighlight(uint highlightedCharacterId)
    {
        foreach (UiButton row in _rows)
            row.Selected = _rowIds.TryGetValue(row, out uint characterId)
                && characterId == highlightedCharacterId;
    }

    private void ApplyButtons(RuntimeCharacterSelectionButtons buttons)
    {
        _create.Visible = true;
        _create.Enabled = buttons.CanCreate;
        _enter.Enabled = buttons.CanEnter;
        _delete.Visible = buttons.DeleteVisible;
        _delete.Enabled = buttons.CanDelete;
        _restore.Visible = buttons.RestoreVisible;
        _restore.Enabled = buttons.CanRestore;
    }

    private void Highlight(uint characterId)
    {
        if (_disposed)
            return;
        _bindings.Highlight(characterId);
        InvalidateAndTick();
    }

    private void RequestCreate()
    {
        if (_disposed)
            return;
        _bindings.RequestCreate?.Invoke();
    }

    private void EnterSelected()
    {
        if (_disposed)
            return;

        EnsureEnterWait();
        RuntimeCommandResult result = _bindings.Enter();
        Console.WriteLine(result.Accepted
            ? "[UI] character enter accepted"
            : $"[UI] character enter rejected status={result.Status}");
        if (!result.Accepted)
            CloseContext(ref _enterWaitContext, suppressCallback: true);
        InvalidateAndTick();
    }

    private void RequestDelete()
    {
        if (_disposed)
            return;
        _bindings.RequestDelete();
        InvalidateAndTick();
    }

    private void RestoreSelected()
    {
        if (_disposed)
            return;

        EnsureOperationWait();
        RuntimeCommandResult result = default;
        Exception? failure = null;
        _restoreCommandInFlight = true;
        try
        {
            result = _bindings.Restore();
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            _restoreCommandInFlight = false;
        }

        if (failure is not null)
        {
            Console.WriteLine(
                $"[UI] character restore command failed: {failure.Message}");
            CloseContext(ref _operationWaitContext, suppressCallback: true);
            InvalidateAndTick();
            return;
        }

        if (!result.Accepted)
            CloseContext(ref _operationWaitContext, suppressCallback: true);
        InvalidateAndTick();
    }

    private void RequestExit()
    {
        if (_disposed)
            return;

        if (_confirmExitDialogContext != 0u)
            return;

        _confirmExitDialogContext = _dialogs.MakeConfirmation(
            _strings.ConfirmExit,
            data =>
            {
                _confirmExitDialogContext = 0u;
                if (_disposed || _suppressDialogCallbacks)
                    return;

                if (data.GetBoolean(RetailDialogProperty.ConfirmationResult))
                    _bindings.RequestExit();
            });
    }

    private void ReconcileDialogs(
        IRuntimeCharacterSelectionView view,
        RuntimeCharacterSelectionSnapshot snapshot)
    {
        if (snapshot.Error is { } error)
        {
            CloseContext(ref _deleteDialogContext, suppressCallback: true);
            CloseContext(ref _operationWaitContext, suppressCallback: true);
            CloseContext(ref _enterWaitContext, suppressCallback: true);
            EnsureError(error.Message);
            return;
        }

        CloseContext(ref _errorDialogContext, suppressCallback: true);
        if (snapshot.Lifecycle == RuntimeCharacterSelectionLifecycle.EnteringWorld)
        {
            CloseContext(ref _deleteDialogContext, suppressCallback: true);
            CloseContext(ref _operationWaitContext, suppressCallback: true);
            EnsureEnterWait();
            return;
        }

        CloseContext(ref _enterWaitContext, suppressCallback: true);
        if (snapshot.PendingDeleteCharacterId != 0u
            && view.TryGet(snapshot.PendingDeleteCharacterId, out RuntimeCharacterSelectionEntry pending))
        {
            EnsureDeleteConfirmation(pending.Name);
        }
        else
        {
            CloseContext(ref _deleteDialogContext, suppressCallback: true);
        }

        if (_restoreCommandInFlight
            || snapshot.Operation is RuntimeCharacterSelectionOperation.DeleteRequested
            or RuntimeCharacterSelectionOperation.DeleteAcknowledged
            or RuntimeCharacterSelectionOperation.RestoreRequested)
        {
            EnsureOperationWait();
        }
        else
        {
            CloseContext(ref _operationWaitContext, suppressCallback: true);
        }
    }

    private void EnsureDeleteConfirmation(string characterName)
    {
        if (_deleteDialogContext != 0u)
            return;

        _deleteDialogContext = _dialogs.MakeConfirmationTextInput(
            _strings.DeleteConfirmation(characterName),
            data =>
            {
                _deleteDialogContext = 0u;
                if (_disposed || _suppressDialogCallbacks)
                    return;

                string response = data.GetString(
                    RetailDialogProperty.TextInputResult) ?? string.Empty;
                if (string.Equals(
                    response,
                    _strings.DeleteResponse,
                    StringComparison.OrdinalIgnoreCase))
                {
                    _bindings.ConfirmDelete();
                }
                else
                {
                    _bindings.Cancel();
                }
                InvalidateAndTick();
            });
    }

    private void EnsureOperationWait()
    {
        if (_operationWaitContext == 0u)
            _operationWaitContext = _dialogs.MakeWait(_strings.PleaseWait);
    }

    private void EnsureEnterWait()
    {
        if (_enterWaitContext == 0u)
            _enterWaitContext = _dialogs.MakeWait(_strings.EnteringWorld);
    }

    private void EnsureError(string message)
    {
        if (_errorDialogContext != 0u)
            return;
        _errorDialogContext = _dialogs.MakeMessage(
            message,
            _ =>
            {
                _errorDialogContext = 0u;
                if (_disposed || _suppressDialogCallbacks)
                    return;
                _bindings.Cancel();
                InvalidateAndTick();
            });
    }

    private void InvalidateAndTick()
    {
        _lastRevision = long.MinValue;
        Tick();
    }

    private void Deactivate()
    {
        if (_active)
        {
            _active = false;
            Root.Visible = false;
            _host.RevokeFixedCanvas(this);
        }
        foreach (UiButton row in _rows)
        {
            row.OnClick = null;
            row.OnDoubleClick = null;
        }
        _rows.Clear();
        _rowIds.Clear();
        _list.Flush();
        CloseAllDialogs(suppressCallbacks: true);
    }

    private void CloseAllDialogs(bool suppressCallbacks)
    {
        bool previous = _suppressDialogCallbacks;
        _suppressDialogCallbacks |= suppressCallbacks;
        try
        {
            CloseContext(ref _deleteDialogContext, suppressCallback: false);
            CloseContext(ref _operationWaitContext, suppressCallback: false);
            CloseContext(ref _enterWaitContext, suppressCallback: false);
            CloseContext(ref _errorDialogContext, suppressCallback: false);
            CloseContext(ref _confirmExitDialogContext, suppressCallback: false);
        }
        finally
        {
            _suppressDialogCallbacks = previous;
        }
    }

    private void CloseContext(ref uint context, bool suppressCallback)
    {
        uint closing = context;
        if (closing == 0u)
            return;
        context = 0u;

        bool previous = _suppressDialogCallbacks;
        _suppressDialogCallbacks |= suppressCallback;
        try
        {
            _dialogs.CloseDialog(closing);
        }
        finally
        {
            _suppressDialogCallbacks = previous;
        }
    }
}

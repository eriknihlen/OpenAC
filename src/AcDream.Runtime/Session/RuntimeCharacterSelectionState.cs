using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Session;

public enum RuntimeCharacterSelectionLifecycle
{
    Inactive,
    Connecting,
    AwaitingSelection,
    EnteringWorld,
    InWorld,
}

public enum RuntimeCharacterSelectionOperation
{
    None,
    DeleteRequested,
    DeleteAcknowledged,
    RestoreRequested,
    RestoreSucceeded,
    RestoreRejected,
}

public enum RuntimeCharacterSelectionDeltaKind
{
    Reset,
    RosterChanged,
    HighlightChanged,
    DeleteConfirmationOpened,
    DeleteConfirmationCancelled,
    DeleteRequested,
    DeleteAcknowledged,
    RestoreRequested,
    RestoreCompleted,
    RestoreCorrelationExpired,
    ErrorChanged,
    EnteringWorld,
    EnteredWorld,
    WorldNameChanged,
}

public readonly record struct RuntimeCharacterSelectionEntry(
    int ActiveIndex,
    uint CharacterId,
    string Name,
    uint SecondsGreyedOut)
{
    public bool IsPendingDelete => SecondsGreyedOut != 0u;

    public bool CanEnter => CharacterId != 0u && !IsPendingDelete;
}

public readonly record struct RuntimeCharacterSelectionButtons(
    bool CanEnter,
    bool CanDelete,
    bool CanRestore,
    bool DeleteVisible,
    bool RestoreVisible,
    bool CanCreate = false)
{
    public static RuntimeCharacterSelectionButtons None { get; } =
        new(false, false, false, true, false);
}

public readonly record struct RuntimeCharacterSelectionError(
    uint RawCode,
    CharacterError.Code Code,
    string Message);

public readonly record struct RuntimeCharacterSelectionSnapshot(
    RuntimeGenerationToken Generation,
    RuntimeCharacterSelectionLifecycle Lifecycle,
    long Revision,
    string AccountName,
    int SlotCount,
    int RosterCount,
    string WorldName,
    uint HighlightedCharacterId,
    int HighlightedDisplayIndex,
    uint PendingDeleteCharacterId,
    uint LastRestoreRequestedCharacterId,
    RuntimeCharacterSelectionOperation Operation,
    RuntimeCharacterSelectionError? Error,
    RuntimeCharacterSelectionButtons Buttons)
{
    public bool IsActive =>
        Lifecycle is RuntimeCharacterSelectionLifecycle.AwaitingSelection
            or RuntimeCharacterSelectionLifecycle.EnteringWorld;
}

public readonly record struct RuntimeCharacterSelectionDelta(
    RuntimeGenerationToken Generation,
    ulong Sequence,
    long Revision,
    RuntimeCharacterSelectionDeltaKind Kind,
    uint CharacterId = 0u,
    uint ErrorCode = 0u);

public interface IRuntimeCharacterSelectionVisitor
{
    void Visit(in RuntimeCharacterSelectionEntry character);
}

public interface IRuntimeCharacterSelectionObserver
{
    void OnCharacterSelectionChanged(
        in RuntimeCharacterSelectionDelta delta);
}

public interface IRuntimeCharacterSelectionEventSource
{
    IDisposable Subscribe(IRuntimeCharacterSelectionObserver observer);
}

public interface IRuntimeCharacterSelectionView
    : IRuntimeCharacterSelectionEventSource
{
    RuntimeCharacterSelectionSnapshot Snapshot { get; }

    bool TryGetAt(
        int displayIndex,
        out RuntimeCharacterSelectionEntry character);

    bool TryGet(
        uint characterId,
        out RuntimeCharacterSelectionEntry character);

    void Visit(IRuntimeCharacterSelectionVisitor visitor);
}

public interface IRuntimeCharacterSelectionCommands
{
    RuntimeCommandResult Highlight(
        RuntimeGenerationToken expectedGeneration,
        uint characterId);

    RuntimeCommandResult Enter(
        RuntimeGenerationToken expectedGeneration);

    RuntimeCommandResult RequestDelete(
        RuntimeGenerationToken expectedGeneration);

    RuntimeCommandResult ConfirmDelete(
        RuntimeGenerationToken expectedGeneration);

    RuntimeCommandResult Restore(
        RuntimeGenerationToken expectedGeneration);

    RuntimeCommandResult Cancel(
        RuntimeGenerationToken expectedGeneration);
}

public sealed class RuntimeCharacterSelectionState : IDisposable
{
    internal static readonly TimeSpan RestoreCorrelationTimeout =
        TimeSpan.FromSeconds(5);

    private sealed class ViewProjection(RuntimeCharacterSelectionState owner)
        : IRuntimeCharacterSelectionView
    {
        public RuntimeCharacterSelectionSnapshot Snapshot => owner.Snapshot;

        public bool TryGetAt(
            int displayIndex,
            out RuntimeCharacterSelectionEntry character) =>
            owner.TryGetAt(displayIndex, out character);

        public bool TryGet(
            uint characterId,
            out RuntimeCharacterSelectionEntry character) =>
            owner.TryGet(characterId, out character);

        public void Visit(IRuntimeCharacterSelectionVisitor visitor) =>
            owner.Visit(visitor);

        public IDisposable Subscribe(
            IRuntimeCharacterSelectionObserver observer) =>
            owner._events.Subscribe(observer);
    }

    private readonly object _gate = new();
    private readonly RuntimeCharacterSelectionEventStream _events = new();
    private readonly ViewProjection _view;
    private readonly TimeProvider _timeProvider;
    private RuntimeCharacterSelectionEntry[] _entries = [];
    private RuntimeGenerationToken _generation;
    private RuntimeCharacterSelectionLifecycle _lifecycle;
    private long _revision;
    private string _accountName = string.Empty;
    private int _slotCount;
    private string _worldName = string.Empty;
    private uint _highlightedCharacterId;
    private uint _pendingDeleteCharacterId;
    private uint _lastRestoreRequestedCharacterId;
    private bool _restoreResponseArmed;
    private long _restoreCorrelationStartedTimestamp;
    private bool _flagOnlyRestoreResponseAmbiguous;
    private RuntimeCharacterSelectionOperation _operation;
    private RuntimeCharacterSelectionError? _error;
    private bool _disposed;

    public RuntimeCharacterSelectionState(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _view = new ViewProjection(this);
    }

    public IRuntimeCharacterSelectionView View => _view;

    public RuntimeCharacterSelectionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                int selectedIndex = FindDisplayIndex(_highlightedCharacterId);
                RuntimeCharacterSelectionButtons buttons =
                    BuildButtons(selectedIndex);
                return new RuntimeCharacterSelectionSnapshot(
                    _generation,
                    _lifecycle,
                    _revision,
                    _accountName,
                    _slotCount,
                    _entries.Length,
                    _worldName,
                    _highlightedCharacterId,
                    selectedIndex,
                    _pendingDeleteCharacterId,
                    _lastRestoreRequestedCharacterId,
                    _operation,
                    _error,
                    buttons);
            }
        }
    }

    internal void Begin(RuntimeGenerationToken generation)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _generation = generation;
            _lifecycle = RuntimeCharacterSelectionLifecycle.Connecting;
            ClearSessionState();
            _revision++;
        }
        Publish(RuntimeCharacterSelectionDeltaKind.Reset);
    }

    internal void ApplyRoster(LiveSessionRosterReport roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        uint selected;
        lock (_gate)
        {
            ThrowIfDisposed();
            uint previous = _highlightedCharacterId;
            var wireEntries = new RuntimeCharacterSelectionEntry[
                roster.Entries.Count];
            uint fallback = 0u;
            bool foundAvailableFallback = false;
            for (int i = 0; i < wireEntries.Length; i++)
            {
                LiveSessionRosterEntry source = roster.Entries[i];
                wireEntries[i] = new RuntimeCharacterSelectionEntry(
                    i,
                    source.Id,
                    source.Name,
                    source.SecondsGreyedOut);
                if (fallback == 0u && source.Id != 0u)
                    fallback = source.Id;
                if (!foundAvailableFallback
                    && source.Id != 0u
                    && source.SecondsGreyedOut == 0u)
                {
                    fallback = source.Id;
                    foundAvailableFallback = true;
                }
            }

            Array.Sort(
                wireEntries,
                static (left, right) =>
                    string.CompareOrdinal(left.Name, right.Name));
            _entries = StablePartitionGreyedToTail(wireEntries);
            _accountName = roster.AccountName;
            _slotCount = roster.SlotCount;
            _lifecycle = RuntimeCharacterSelectionLifecycle.AwaitingSelection;
            _pendingDeleteCharacterId = 0u;
            _lastRestoreRequestedCharacterId = 0u;
            _restoreResponseArmed = false;
            _restoreCorrelationStartedTimestamp = 0;
            _flagOnlyRestoreResponseAmbiguous = false;
            _operation = RuntimeCharacterSelectionOperation.None;
            _error = null;
            _highlightedCharacterId = Contains(previous)
                ? previous
                : fallback;
            selected = _highlightedCharacterId;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.RosterChanged,
            selected);
    }

    internal void AppendCreatedCharacter(uint characterId, string name, int wireIndex)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            ThrowIfDisposed();
            var appended = new RuntimeCharacterSelectionEntry[_entries.Length + 1];
            Array.Copy(_entries, appended, _entries.Length);
            appended[^1] = new RuntimeCharacterSelectionEntry(
                wireIndex,
                characterId,
                name,
                SecondsGreyedOut: 0u);

            Array.Sort(
                appended,
                static (left, right) =>
                    string.CompareOrdinal(left.Name, right.Name));
            _entries = StablePartitionGreyedToTail(appended);
            _revision++;
        }
        Publish(RuntimeCharacterSelectionDeltaKind.RosterChanged, characterId);
    }

    internal void ApplyWorldName(string worldName)
    {
        ArgumentNullException.ThrowIfNull(worldName);
        lock (_gate)
        {
            if (_disposed || _worldName == worldName)
                return;
            _worldName = worldName;
            _revision++;
        }
        Publish(RuntimeCharacterSelectionDeltaKind.WorldNameChanged);
    }

    internal bool TryHighlight(uint characterId)
    {
        lock (_gate)
        {
            if (_disposed
                || _lifecycle != RuntimeCharacterSelectionLifecycle.AwaitingSelection
                || HasDeleteModalOrRequest()
                || !Contains(characterId))
            {
                return false;
            }
            if (_highlightedCharacterId == characterId)
                return true;

            _highlightedCharacterId = characterId;
            _pendingDeleteCharacterId = 0u;
            _error = null;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.HighlightChanged,
            characterId);
        return true;
    }

    internal bool TryRequestDelete(out uint characterId)
    {
        lock (_gate)
        {
            int index = FindDisplayIndex(_highlightedCharacterId);
            if (_disposed
                || _lifecycle != RuntimeCharacterSelectionLifecycle.AwaitingSelection
                || HasDeleteModalOrRequest()
                || index < 0
                || !_entries[index].CanEnter)
            {
                characterId = 0u;
                return false;
            }
            characterId = _entries[index].CharacterId;
            _pendingDeleteCharacterId = characterId;
            _error = null;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.DeleteConfirmationOpened,
            characterId);
        return true;
    }

    internal bool TryTakeDeleteConfirmation(
        out RuntimeCharacterSelectionEntry character,
        out string accountName)
    {
        lock (_gate)
        {
            int index = FindDisplayIndex(_pendingDeleteCharacterId);
            if (_disposed
                || _lifecycle != RuntimeCharacterSelectionLifecycle.AwaitingSelection
                || _pendingDeleteCharacterId == 0u
                || _operation is RuntimeCharacterSelectionOperation.DeleteRequested
                    or RuntimeCharacterSelectionOperation.DeleteAcknowledged
                || index < 0
                || !_entries[index].CanEnter)
            {
                character = default;
                accountName = string.Empty;
                return false;
            }

            character = _entries[index];
            accountName = _accountName;
            _pendingDeleteCharacterId = 0u;
            _operation = RuntimeCharacterSelectionOperation.DeleteRequested;
            _error = null;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.DeleteRequested,
            character.CharacterId);
        return true;
    }

    internal bool TryBeginRestore(
        out RuntimeCharacterSelectionEntry character)
    {
        lock (_gate)
        {
            int index = FindDisplayIndex(_highlightedCharacterId);
            if (_disposed
                || _lifecycle != RuntimeCharacterSelectionLifecycle.AwaitingSelection
                || HasDeleteModalOrRequest()
                || _restoreResponseArmed
                || index < 0
                || !_entries[index].IsPendingDelete)
            {
                character = default;
                return false;
            }

            character = _entries[index];
            _pendingDeleteCharacterId = 0u;
            _lastRestoreRequestedCharacterId = character.CharacterId;
            _restoreResponseArmed = true;
            _restoreCorrelationStartedTimestamp =
                _timeProvider.GetTimestamp();
            _operation = RuntimeCharacterSelectionOperation.RestoreRequested;
            _error = null;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.RestoreRequested,
            character.CharacterId);
        return true;
    }

    internal bool SweepRestoreCorrelation()
    {
        uint characterId;
        lock (_gate)
        {
            if (_disposed
                || !_restoreResponseArmed
                || _timeProvider.GetElapsedTime(
                    _restoreCorrelationStartedTimestamp)
                    < RestoreCorrelationTimeout)
            {
                return false;
            }

            characterId = _lastRestoreRequestedCharacterId;
            _restoreResponseArmed = false;
            _restoreCorrelationStartedTimestamp = 0;
            _flagOnlyRestoreResponseAmbiguous = true;
            if (!HasConfirmedDelete())
                _operation = RuntimeCharacterSelectionOperation.None;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.RestoreCorrelationExpired,
            characterId);
        return true;
    }

    internal bool Cancel()
    {
        RuntimeCharacterSelectionDeltaKind kind;
        uint characterId;
        lock (_gate)
        {
            if (_disposed
                || _lifecycle != RuntimeCharacterSelectionLifecycle.AwaitingSelection)
            {
                return false;
            }

            if (_pendingDeleteCharacterId != 0u)
            {
                characterId = _pendingDeleteCharacterId;
                _pendingDeleteCharacterId = 0u;
                kind = RuntimeCharacterSelectionDeltaKind.DeleteConfirmationCancelled;
            }
            else if (_error is not null)
            {
                characterId = 0u;
                _error = null;
                kind = RuntimeCharacterSelectionDeltaKind.ErrorChanged;
            }
            else
            {
                return false;
            }
            _revision++;
        }
        Publish(kind, characterId);
        return true;
    }

    internal void ApplyDeleteAcknowledged()
    {
        uint characterId;
        lock (_gate)
        {
            if (_disposed
                || _lifecycle != RuntimeCharacterSelectionLifecycle.AwaitingSelection
                || _operation != RuntimeCharacterSelectionOperation.DeleteRequested)
            {
                return;
            }
            _operation = RuntimeCharacterSelectionOperation.DeleteAcknowledged;
            characterId = _highlightedCharacterId;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.DeleteAcknowledged,
            characterId);
    }

    internal void ApplyRestore(CharacterRestore.Parsed response)
    {
        uint characterId;
        uint errorCode = 0u;
        bool deleteInFlight;
        lock (_gate)
        {
            if (_disposed
                || _lifecycle != RuntimeCharacterSelectionLifecycle.AwaitingSelection
                || !_restoreResponseArmed)
                return;
            if (response.Guid is null
                && _flagOnlyRestoreResponseAmbiguous)
            {
                return;
            }
            if (response.Guid is { } responseGuid
                && responseGuid != _lastRestoreRequestedCharacterId)
            {
                return;
            }
            _restoreResponseArmed = false;
            _restoreCorrelationStartedTimestamp = 0;
            characterId = response.Guid
                ?? _lastRestoreRequestedCharacterId;
            deleteInFlight = HasConfirmedDelete();

            if (response.IsOk
                && response.Guid is { } guid
                && response.SecondsGreyedOut is { } seconds)
            {
                int index = FindDisplayIndex(guid);
                if (index >= 0)
                {
                    RuntimeCharacterSelectionEntry current = _entries[index];
                    _entries[index] = current with
                    {
                        Name = response.Name ?? current.Name,
                        SecondsGreyedOut = seconds,
                    };
                    Array.Sort(
                        _entries,
                        static (left, right) =>
                            string.CompareOrdinal(left.Name, right.Name));
                    _entries = StablePartitionGreyedToTail(_entries);
                }
                if (!deleteInFlight)
                {
                    _operation =
                        RuntimeCharacterSelectionOperation.RestoreSucceeded;
                }
                _error = null;
            }
            else
            {
                errorCode = response.VerificationFlag;
                if (!deleteInFlight)
                {
                    _operation =
                        RuntimeCharacterSelectionOperation.RestoreRejected;
                }
                _error = new RuntimeCharacterSelectionError(
                    response.VerificationFlag,
                    CharacterError.Code.Undefined,
                    $"The character could not be restored (verification 0x{response.VerificationFlag:X8}).");
            }
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.RestoreCompleted,
            characterId,
            errorCode);
    }

    internal void ApplyError(CharacterError.Parsed error)
    {
        if (error.AsCode == CharacterError.Code.NumErrors)
            return;

        string message = MapError(error.RawErrorCode, error.AsCode);
        lock (_gate)
        {
            if (_disposed
                || _lifecycle is not (
                    RuntimeCharacterSelectionLifecycle.AwaitingSelection
                    or RuntimeCharacterSelectionLifecycle.EnteringWorld))
                return;
            _pendingDeleteCharacterId = 0u;
            if (_restoreResponseArmed)
                _flagOnlyRestoreResponseAmbiguous = true;
            _restoreResponseArmed = false;
            _restoreCorrelationStartedTimestamp = 0;
            _operation = RuntimeCharacterSelectionOperation.None;
            _error = new RuntimeCharacterSelectionError(
                error.RawErrorCode,
                error.AsCode,
                message);
            if (_lifecycle == RuntimeCharacterSelectionLifecycle.EnteringWorld)
                _lifecycle = RuntimeCharacterSelectionLifecycle.AwaitingSelection;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.ErrorChanged,
            errorCode: error.RawErrorCode);
    }

    internal bool BeginEnter(out RuntimeCharacterSelectionEntry character)
    {
        lock (_gate)
        {
            int index = FindDisplayIndex(_highlightedCharacterId);
            if (_disposed
                || _lifecycle != RuntimeCharacterSelectionLifecycle.AwaitingSelection
                || HasDeleteModalOrRequest()
                || index < 0
                || !_entries[index].CanEnter)
            {
                character = default;
                return false;
            }
            character = _entries[index];
            _pendingDeleteCharacterId = 0u;
            _lastRestoreRequestedCharacterId = 0u;
            _restoreResponseArmed = false;
            _restoreCorrelationStartedTimestamp = 0;
            _flagOnlyRestoreResponseAmbiguous = false;
            _operation = RuntimeCharacterSelectionOperation.None;
            _error = null;
            _lifecycle = RuntimeCharacterSelectionLifecycle.EnteringWorld;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.EnteringWorld,
            character.CharacterId);
        return true;
    }

    internal void CompleteEnter(uint characterId)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _lifecycle = RuntimeCharacterSelectionLifecycle.InWorld;
            _highlightedCharacterId = characterId;
            _operation = RuntimeCharacterSelectionOperation.None;
            _error = null;
            _revision++;
        }
        Publish(
            RuntimeCharacterSelectionDeltaKind.EnteredWorld,
            characterId);
    }

    internal void ReturnToSelection()
    {
        lock (_gate)
        {
            if (_disposed
                || _lifecycle != RuntimeCharacterSelectionLifecycle.EnteringWorld)
            {
                return;
            }
            _lifecycle = RuntimeCharacterSelectionLifecycle.AwaitingSelection;
            _revision++;
        }
    }

    internal void Reset(RuntimeGenerationToken generation)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _generation = generation;
            _lifecycle = RuntimeCharacterSelectionLifecycle.Inactive;
            ClearSessionState();
            _revision++;
        }
        Publish(RuntimeCharacterSelectionDeltaKind.Reset);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifecycle = RuntimeCharacterSelectionLifecycle.Inactive;
            ClearSessionState();
            _revision++;
        }
        _events.Dispose();
    }

    private bool TryGetAt(
        int displayIndex,
        out RuntimeCharacterSelectionEntry character)
    {
        lock (_gate)
        {
            if ((uint)displayIndex >= (uint)_entries.Length)
            {
                character = default;
                return false;
            }
            character = _entries[displayIndex];
            return true;
        }
    }

    private bool TryGet(
        uint characterId,
        out RuntimeCharacterSelectionEntry character)
    {
        lock (_gate)
        {
            int index = FindDisplayIndex(characterId);
            if (index < 0)
            {
                character = default;
                return false;
            }
            character = _entries[index];
            return true;
        }
    }

    private void Visit(IRuntimeCharacterSelectionVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        lock (_gate)
        {
            foreach (RuntimeCharacterSelectionEntry character in _entries)
                visitor.Visit(in character);
        }
    }

    private RuntimeCharacterSelectionButtons BuildButtons(int selectedIndex)
    {
        bool canCreate = _entries.Length < _slotCount;

        if (_operation is RuntimeCharacterSelectionOperation.DeleteRequested
            or RuntimeCharacterSelectionOperation.DeleteAcknowledged)
        {
            return RuntimeCharacterSelectionButtons.None with { CanCreate = canCreate };
        }
        if (selectedIndex < 0)
            return RuntimeCharacterSelectionButtons.None with { CanCreate = canCreate };

        RuntimeCharacterSelectionEntry selected = _entries[selectedIndex];
        if (selected.IsPendingDelete)
        {
            return new RuntimeCharacterSelectionButtons(
                CanEnter: false,
                CanDelete: false,
                CanRestore: !_restoreResponseArmed,
                DeleteVisible: false,
                RestoreVisible: true,
                CanCreate: canCreate);
        }

        return new RuntimeCharacterSelectionButtons(
            CanEnter: selected.CanEnter,
            CanDelete: selected.CanEnter,
            CanRestore: false,
            DeleteVisible: true,
            RestoreVisible: false,
            CanCreate: canCreate);
    }

    private int FindDisplayIndex(uint characterId)
    {
        if (characterId == 0u)
            return -1;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].CharacterId == characterId)
                return i;
        }
        return -1;
    }

    private bool Contains(uint characterId) =>
        FindDisplayIndex(characterId) >= 0;

    private bool HasDeleteModalOrRequest() =>
        _pendingDeleteCharacterId != 0u
        || HasConfirmedDelete();

    private bool HasConfirmedDelete() =>
        _operation is RuntimeCharacterSelectionOperation.DeleteRequested
            or RuntimeCharacterSelectionOperation.DeleteAcknowledged;

    private static RuntimeCharacterSelectionEntry[] StablePartitionGreyedToTail(
        RuntimeCharacterSelectionEntry[] sorted)
    {
        if (sorted.Length < 2)
            return sorted;
        var result = new RuntimeCharacterSelectionEntry[sorted.Length];
        int position = 0;
        foreach (RuntimeCharacterSelectionEntry entry in sorted)
        {
            if (!entry.IsPendingDelete)
                result[position++] = entry;
        }
        foreach (RuntimeCharacterSelectionEntry entry in sorted)
        {
            if (entry.IsPendingDelete)
                result[position++] = entry;
        }
        return result;
    }

    private static string MapError(
        uint rawCode,
        CharacterError.Code code) =>
        code switch
        {
            CharacterError.Code.Logon =>
                "Another account is already logged on from this client.",
            CharacterError.Code.LoggedOn =>
                "This account is already logged on.",
            CharacterError.Code.AccountLogon =>
                "The server could not access the account. Please try again shortly.",
            CharacterError.Code.ServerCrash or CharacterError.Code.AccountInUse =>
                "The server disconnected. Please try again shortly.",
            CharacterError.Code.Logoff =>
                "The server could not log off the character.",
            CharacterError.Code.Delete =>
                "The server could not delete the character.",
            CharacterError.Code.NoPremade =>
                "No premade character is available.",
            CharacterError.Code.AccountInvalid =>
                "The account name is not valid.",
            CharacterError.Code.AccountDoesntExist =>
                "The account does not exist.",
            CharacterError.Code.EnterGameGeneric =>
                "The character could not enter the world.",
            CharacterError.Code.EnterGameStressAccount =>
                "A stress-test character cannot enter the world.",
            CharacterError.Code.EnterGameCharacterInWorld =>
                "One of this account's characters is still in the world. Please try again shortly.",
            CharacterError.Code.EnterGamePlayerAccountMissing =>
                "The server could not find the player account. Please try again later.",
            CharacterError.Code.EnterGameCharacterNotOwned =>
                "This account does not own the selected character.",
            CharacterError.Code.EnterGameCharacterInWorldServer =>
                "One of this account's characters is already in the world.",
            CharacterError.Code.EnterGameOldCharacter =>
                "The selected character must be updated before entering the world.",
            CharacterError.Code.EnterGameCorruptCharacter =>
                "The selected character's data is corrupt.",
            CharacterError.Code.EnterGameStartServerDown =>
                "The selected character's starting server is unavailable.",
            CharacterError.Code.EnterGameCouldntPlaceCharacter =>
                "The selected character could not be placed in the world. Please try again shortly.",
            CharacterError.Code.LogonServerFull =>
                "The server is currently full. Please try again later.",
            CharacterError.Code.CharacterIsBooted =>
                "The selected character is temporarily unavailable.",
            CharacterError.Code.EnterGameCharacterLocked =>
                "A save of the selected character is still in progress. Please try again later.",
            CharacterError.Code.SubscriptionExpired =>
                "The account subscription has expired.",
            _ => $"Character selection failed (error 0x{rawCode:X8}).",
        };

    private void ClearSessionState()
    {
        _entries = [];
        _accountName = string.Empty;
        _slotCount = 0;
        _worldName = string.Empty;
        _highlightedCharacterId = 0u;
        _pendingDeleteCharacterId = 0u;
        _lastRestoreRequestedCharacterId = 0u;
        _restoreResponseArmed = false;
        _restoreCorrelationStartedTimestamp = 0;
        _flagOnlyRestoreResponseAmbiguous = false;
        _operation = RuntimeCharacterSelectionOperation.None;
        _error = null;
    }

    private void Publish(
        RuntimeCharacterSelectionDeltaKind kind,
        uint characterId = 0u,
        uint errorCode = 0u)
    {
        RuntimeGenerationToken generation;
        long revision;
        lock (_gate)
        {
            if (_disposed)
                return;
            generation = _generation;
            revision = _revision;
        }
        _events.Publish(generation, revision, kind, characterId, errorCode);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class RuntimeCharacterSelectionEventStream : IDisposable
{
    private readonly object _gate = new();
    private readonly List<RuntimeCharacterSelectionDelta> _pending = [];
    private IRuntimeCharacterSelectionObserver[] _observers = [];
    private ulong _sequence;
    private bool _dispatching;
    private bool _disposed;

    public IDisposable Subscribe(IRuntimeCharacterSelectionObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Array.IndexOf(_observers, observer) >= 0)
            {
                throw new InvalidOperationException(
                    "The character-selection observer is already subscribed.");
            }
            var replacement = new IRuntimeCharacterSelectionObserver[
                _observers.Length + 1];
            Array.Copy(_observers, replacement, _observers.Length);
            replacement[^1] = observer;
            Volatile.Write(ref _observers, replacement);
        }
        return new Subscription(this, observer);
    }

    public void Publish(
        RuntimeGenerationToken generation,
        long revision,
        RuntimeCharacterSelectionDeltaKind kind,
        uint characterId,
        uint errorCode)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending.Add(new RuntimeCharacterSelectionDelta(
                generation,
                unchecked(++_sequence),
                revision,
                kind,
                characterId,
                errorCode));
            if (_dispatching)
                return;
            _dispatching = true;
        }

        int index = 0;
        while (true)
        {
            RuntimeCharacterSelectionDelta delta;
            lock (_gate)
            {
                if (index >= _pending.Count)
                {
                    _pending.Clear();
                    _dispatching = false;
                    return;
                }
                delta = _pending[index++];
            }

            foreach (IRuntimeCharacterSelectionObserver observer
                     in Volatile.Read(ref _observers))
            {
                try
                {
                    observer.OnCharacterSelectionChanged(in delta);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        $"runtime: character-selection observer failed: {error.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending.Clear();
            _dispatching = false;
            Volatile.Write(ref _observers, []);
        }
    }

    private void Unsubscribe(IRuntimeCharacterSelectionObserver observer)
    {
        lock (_gate)
        {
            int index = Array.IndexOf(_observers, observer);
            if (index < 0)
                return;
            var replacement = new IRuntimeCharacterSelectionObserver[
                _observers.Length - 1];
            if (index > 0)
                Array.Copy(_observers, 0, replacement, 0, index);
            if (index < _observers.Length - 1)
            {
                Array.Copy(
                    _observers,
                    index + 1,
                    replacement,
                    index,
                    _observers.Length - index - 1);
            }
            Volatile.Write(ref _observers, replacement);
        }
    }

    private sealed class Subscription(
        RuntimeCharacterSelectionEventStream owner,
        IRuntimeCharacterSelectionObserver observer)
        : IDisposable
    {
        private RuntimeCharacterSelectionEventStream? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(observer);
    }
}

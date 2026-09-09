using AcDream.Core.Chat;
using AcDream.Core.Social;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeCommunicationEvent(
    ulong Sequence,
    RuntimeChatEntry Entry);

public interface IRuntimeCommunicationObserver
{
    void OnChat(in RuntimeCommunicationEvent delta);
}

public interface IRuntimeCommunicationEventSource
{
    IDisposable Subscribe(IRuntimeCommunicationObserver observer);
}

public readonly record struct RuntimeCommunicationOwnershipSnapshot(
    bool IsDisposed,
    bool CommandTargetsDisposed,
    int StreamSubscriberCount,
    int PendingDispatchCount,
    bool IsDispatching,
    int FriendCount,
    int SquelchAccountCount,
    int SquelchCharacterCount,
    int SquelchGlobalTypeCount,
    int NegotiatedRoomCount,
    bool HasReplyTarget,
    bool HasRetellTarget,
    long DispatchFailureCount)
{
    public bool IsConverged =>
        IsDisposed
        && CommandTargetsDisposed
        && StreamSubscriberCount == 0
        && PendingDispatchCount == 0
        && !IsDispatching
        && FriendCount == 0
        && SquelchAccountCount == 0
        && SquelchCharacterCount == 0
        && SquelchGlobalTypeCount == 0
        && NegotiatedRoomCount == 0
        && !HasReplyTarget
        && !HasRetellTarget;
}

public sealed class RuntimeCommunicationState : IDisposable
{
    private readonly RuntimeCommunicationEventStream _events;
    private bool _disposed;

    public RuntimeCommunicationState(int maximumChatEntries = 500)
    {
        Chat = new ChatLog(maximumChatEntries);
        SpewBox = new SpewBoxState();
        CommandTargets = new ChatCommandTargetState(Chat);
        _events = new RuntimeCommunicationEventStream(Chat);
        TurbineChat = new TurbineChatState();
        Friends = new FriendsState();
        Squelch = new SquelchState();
        ChatWindows = new ChatWindowState();
        View = new CommunicationView(Chat);
        SocialView = new CommunicationSocialView(
            TurbineChat,
            Friends,
            Squelch);
    }

    public ChatLog Chat { get; }

    public ChatWindowState ChatWindows { get; }

    public SpewBoxState SpewBox { get; }

    public Func<bool>? DisplayTimestampsSource
    {
        get => Chat.DisplayTimestampsSource;
        set => Chat.DisplayTimestampsSource = value;
    }

    public ChatCommandTargetState CommandTargets { get; }
    public TurbineChatState TurbineChat { get; }
    public FriendsState Friends { get; }
    public SquelchState Squelch { get; }
    public IRuntimeChatView View { get; }
    public IRuntimeSocialView SocialView { get; }
    public IRuntimeCommunicationEventSource Events => _events;

    public bool IsDisposed => _disposed;
    public ulong LastSequence => _events.LastSequence;
    public int SubscriberCount => _events.SubscriberCount;
    public int PendingDispatchCount => _events.PendingDispatchCount;
    public bool IsDispatching => _events.IsDispatching;
    public long DispatchFailureCount => _events.DispatchFailureCount;
    public Exception? LastDispatchFailure => _events.LastDispatchFailure;

    public RuntimeCommunicationOwnershipSnapshot CaptureOwnership()
    {
        SquelchDatabase squelch = Squelch.Snapshot();
        return new RuntimeCommunicationOwnershipSnapshot(
            _disposed,
            CommandTargets.IsDisposed,
            SubscriberCount,
            PendingDispatchCount,
            IsDispatching,
            Friends.Count,
            squelch.Accounts.Count,
            squelch.Characters.Count,
            squelch.Global.MessageTypes.Count,
            CountRooms(TurbineChat),
            CommandTargets.LastIncomingTellSender is not null,
            CommandTargets.LastOutgoingTellTarget is not null,
            DispatchFailureCount);
    }

    public void ResetCommandTargets() => CommandTargets.ResetSession();
    public void ResetChatIdentity() => Chat.ResetSessionIdentity();
    public void ResetNegotiatedChannels() => TurbineChat.Reset();
    public void ResetFriends() => Friends.Clear();
    public void ResetSquelch() => Squelch.Clear();

    public void ResetSpewBox() => SpewBox.Reset();

    public void AddText(string text, RetailLogTextType type, uint windowId = 0)
    {
        ArgumentNullException.ThrowIfNull(text);

        text = text.Trim();

        if (type == RetailLogTextType.ClientLocal)
        {
            SpewBox.Enqueue(text);
            return;
        }

        Chat.OnSystemMessage(text, (uint)type);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _events.Dispose();
        CommandTargets.ResetSession();
        CommandTargets.Dispose();
        TurbineChat.Reset();
        Friends.Clear();
        Squelch.Clear();
        Chat.ResetSessionIdentity();
        SpewBox.Reset();
        ChatWindows.ResetToDefaults();
    }

    private sealed class CommunicationView(ChatLog chat) : IRuntimeChatView
    {
        public long Revision => chat.Revision;
        public int Count => chat.Count;
    }

    private sealed class CommunicationSocialView(
        TurbineChatState turbineChat,
        FriendsState friends,
        SquelchState squelch)
        : IRuntimeSocialView
    {
        public RuntimeSocialSnapshot Snapshot
        {
            get
            {
                SquelchDatabase database = squelch.Snapshot();
                return new RuntimeSocialSnapshot(
                    friends.Revision,
                    friends.Count,
                    squelch.Revision,
                    database.Accounts.Count,
                    database.Characters.Count,
                    database.Global.MessageTypes.Count,
                    CountRooms(turbineChat));
            }
        }

        public bool TryGetFriend(
            uint characterId,
            out RuntimeFriendSnapshot friend)
        {
            if (!friends.TryGet(characterId, out FriendEntry? current)
                || current is null)
            {
                friend = default;
                return false;
            }

            friend = new RuntimeFriendSnapshot(
                current.Id,
                current.Name,
                current.Online,
                current.AppearOffline);
            return true;
        }

    }

    private static int CountRooms(TurbineChatState state)
    {
        int count = 0;
        if (state.AllegianceRoom != 0u) count++;
        if (state.GeneralRoom != 0u) count++;
        if (state.TradeRoom != 0u) count++;
        if (state.LfgRoom != 0u) count++;
        if (state.RoleplayRoom != 0u) count++;
        if (state.SocietyRoom != 0u) count++;
        if (state.OlthoiRoom != 0u) count++;
        return count;
    }
}

internal sealed class RuntimeCommunicationEventStream
    : IRuntimeCommunicationEventSource,
      IDisposable
{
    private readonly ChatLog _chat;
    private readonly object _gate = new();
    private readonly List<RuntimeCommunicationEvent> _pendingDispatch = [];
    private IRuntimeCommunicationObserver[] _observers = [];
    private long _sequence;
    private long _dispatchFailureCount;
    private bool _dispatching;
    private bool _disposed;

    public RuntimeCommunicationEventStream(ChatLog chat)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _chat.EntryAppended += OnEntryAppended;
    }

    public int SubscriberCount => Volatile.Read(ref _observers).Length;
    public ulong LastSequence
    {
        get
        {
            lock (_gate)
                return unchecked((ulong)_sequence);
        }
    }
    public int PendingDispatchCount
    {
        get
        {
            lock (_gate)
                return _pendingDispatch.Count;
        }
    }
    public bool IsDispatching
    {
        get
        {
            lock (_gate)
                return _dispatching;
        }
    }
    public long DispatchFailureCount => Interlocked.Read(ref _dispatchFailureCount);
    public Exception? LastDispatchFailure { get; private set; }

    public IDisposable Subscribe(IRuntimeCommunicationObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IRuntimeCommunicationObserver[] current = _observers;
            if (Array.IndexOf(current, observer) >= 0)
                throw new InvalidOperationException(
                    "The communication observer is already subscribed.");

            var replacement =
                new IRuntimeCommunicationObserver[current.Length + 1];
            Array.Copy(current, replacement, current.Length);
            replacement[^1] = observer;
            Volatile.Write(ref _observers, replacement);
        }
        return new Subscription(this, observer);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _chat.EntryAppended -= OnEntryAppended;
            _pendingDispatch.Clear();
            _dispatching = false;
            Volatile.Write(ref _observers, []);
        }
    }

    private void OnEntryAppended(ChatEntry entry)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            ulong sequence = unchecked((ulong)++_sequence);
            _pendingDispatch.Add(new RuntimeCommunicationEvent(
                sequence,
                new RuntimeChatEntry(
                    _chat.Revision,
                    entry.SenderGuid,
                    (int)entry.Kind,
                    entry.Sender,
                    entry.Text,
                    entry.ChannelName)));
            if (_dispatching)
                return;
            _dispatching = true;
        }

        int index = 0;
        while (true)
        {
            RuntimeCommunicationEvent pending;
            lock (_gate)
            {
                if (index >= _pendingDispatch.Count)
                {
                    _pendingDispatch.Clear();
                    _dispatching = false;
                    return;
                }
                pending = _pendingDispatch[index++];
            }
            Dispatch(in pending);
        }
    }

    private void Dispatch(in RuntimeCommunicationEvent delta)
    {
        IRuntimeCommunicationObserver[] observers =
            Volatile.Read(ref _observers);
        foreach (IRuntimeCommunicationObserver observer in observers)
        {
            try
            {
                observer.OnChat(in delta);
            }
            catch (Exception error)
            {
                Interlocked.Increment(ref _dispatchFailureCount);
                LastDispatchFailure = error;
            }
        }
    }

    private void Unsubscribe(IRuntimeCommunicationObserver observer)
    {
        lock (_gate)
        {
            IRuntimeCommunicationObserver[] current = _observers;
            int index = Array.IndexOf(current, observer);
            if (index < 0)
                return;
            if (current.Length == 1)
            {
                Volatile.Write(ref _observers, []);
                return;
            }

            var replacement =
                new IRuntimeCommunicationObserver[current.Length - 1];
            if (index > 0)
                Array.Copy(current, 0, replacement, 0, index);
            if (index < current.Length - 1)
            {
                Array.Copy(
                    current,
                    index + 1,
                    replacement,
                    index,
                    current.Length - index - 1);
            }
            Volatile.Write(ref _observers, replacement);
        }
    }

    private sealed class Subscription(
        RuntimeCommunicationEventStream owner,
        IRuntimeCommunicationObserver observer)
        : IDisposable
    {
        private RuntimeCommunicationEventStream? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(observer);
    }
}

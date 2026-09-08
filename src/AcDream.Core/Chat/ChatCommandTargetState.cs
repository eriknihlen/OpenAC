namespace AcDream.Core.Chat;

public sealed class ChatCommandTargetState : IDisposable
{
    private readonly ChatLog _chat;
    private readonly object _gate = new();
    private string? _lastIncomingTellSender;
    private string? _lastOutgoingTellTarget;
    private string? _lastMonarchSender;
    private string? _lastPatronSender;
    private bool _disposed;

    public ChatCommandTargetState(ChatLog chat)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _chat.EntryAppended += OnEntryAppended;
    }

    public string? LastIncomingTellSender
    {
        get
        {
            lock (_gate)
                return _lastIncomingTellSender;
        }
    }

    public string? LastOutgoingTellTarget
    {
        get
        {
            lock (_gate)
                return _lastOutgoingTellTarget;
        }
    }

    public string? LastMonarchSender
    {
        get
        {
            lock (_gate)
                return _lastMonarchSender;
        }
    }

    public string? LastPatronSender
    {
        get
        {
            lock (_gate)
                return _lastPatronSender;
        }
    }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
                return _disposed;
        }
    }

    public void ResetSession()
    {
        lock (_gate)
        {
            _lastIncomingTellSender = null;
            _lastOutgoingTellTarget = null;
            _lastMonarchSender = null;
            _lastPatronSender = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _chat.EntryAppended -= OnEntryAppended;
        }
    }

    private void OnEntryAppended(ChatEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Sender))
            return;

        lock (_gate)
        {
            if (_disposed)
                return;
            if (entry.Kind == ChatKind.Tell)
            {
                if (entry.SenderGuid != 0u)
                    _lastIncomingTellSender = entry.Sender;
                else
                    _lastOutgoingTellTarget = entry.Sender;
                return;
            }

            if (entry.Kind != ChatKind.Channel)
                return;
            if (entry.ChannelId == 0x00004000u)
                _lastMonarchSender = entry.Sender;
            else if (entry.ChannelId == 0x00002000u)
                _lastPatronSender = entry.Sender;
        }
    }
}

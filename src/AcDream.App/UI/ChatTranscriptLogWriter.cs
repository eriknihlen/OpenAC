using System;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.UI;

public sealed class ChatTranscriptLogWriter
{
    private readonly ChatSessionLog _log;
    private ChatLog? _source;

    public ChatTranscriptLogWriter(ChatSessionLog log)
        => _log = log ?? throw new ArgumentNullException(nameof(log));

    public void Attach(ChatLog source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Detach();
        _source = source;
        source.EntryAppended += Write;
    }

    public void Detach()
    {
        if (_source is null)
            return;

        _source.EntryAppended -= Write;
        _source = null;
    }

    private void Write(ChatEntry entry)
    {
        ChatLog? source = _source;
        if (source is null)
            return;

        bool stamped = source.DisplayTimestampsSource?.Invoke() == true;

        _log.Write(
            stamped ? ChatLog.FormatTimestampPrefix(entry.Received) : null,
            ChatVM.FormatEntry(entry));
    }
}

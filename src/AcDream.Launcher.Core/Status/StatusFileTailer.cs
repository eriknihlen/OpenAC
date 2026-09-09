using System.Text;

namespace AcDream.Launcher.Core.Status;

public interface IStatusEventSource
{
    IReadOnlyList<StatusEvent> ReadNewEvents();
}

public interface IStatusEventSourceFactory
{
    IStatusEventSource Create(string path);
}

public sealed class StatusFileTailerFactory : IStatusEventSourceFactory
{
    public IStatusEventSource Create(string path) => new StatusFileTailer(path);
}

public sealed class StatusFileTailer : IStatusEventSource
{
    private readonly string _path;
    private long _position;

    public StatusFileTailer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public IReadOnlyList<StatusEvent> ReadNewEvents()
    {
        try
        {
            return ReadNewEventsCore();
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }

    private IReadOnlyList<StatusEvent> ReadNewEventsCore()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < _position)
        {
            _position = 0;
        }

        if (stream.Length == _position)
        {
            return [];
        }

        stream.Seek(_position, SeekOrigin.Begin);
        int unreadByteCount = checked((int)(stream.Length - _position));
        byte[] buffer = new byte[unreadByteCount];
        int totalRead = 0;
        while (totalRead < unreadByteCount)
        {
            int read = stream.Read(buffer, totalRead, unreadByteCount - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var events = new List<StatusEvent>();
        int lineStart = 0;

        int consumedThroughIndex = 0;

        for (int i = 0; i < totalRead; i++)
        {
            if (buffer[i] != (byte)'\n')
            {
                continue;
            }

            int lineEnd = i;
            if (lineEnd > lineStart && buffer[lineEnd - 1] == (byte)'\r')
            {
                lineEnd--;
            }

            if (lineEnd > lineStart)
            {
                string rawLine = Encoding.UTF8.GetString(buffer, lineStart, lineEnd - lineStart);
                events.Add(StatusEventParser.Parse(rawLine));
            }

            lineStart = i + 1;
            consumedThroughIndex = lineStart;
        }

        _position += consumedThroughIndex;

        return events;
    }
}

using System.Text;

namespace AcDream.Launcher.Core.Launching;

public sealed class BoundedProcessOutputCapture : IDisposable
{
    public const long DefaultMaxBytes = 2 * 1024 * 1024;

    private static readonly byte[] Newline = "\n"u8.ToArray();

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private bool _directoryEnsured;
    private long _written;
    private bool _capped;
    private bool _latchedOff;
    private bool _disposed;

    public BoundedProcessOutputCapture(string path, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes),
                "The bounded capture size must be positive.");
        }

        _path = Path.GetFullPath(path);
        _maxBytes = maxBytes;
    }

    public bool IsDone
    {
        get
        {
            lock (_gate)
            {
                return _capped || _latchedOff || _disposed;
            }
        }
    }

    public void AppendLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        byte[] textBytes = Encoding.UTF8.GetBytes(line);
        var buffer = new byte[textBytes.Length + Newline.Length];
        textBytes.CopyTo(buffer, 0);
        Newline.CopyTo(buffer, textBytes.Length);

        lock (_gate)
        {
            AppendLocked(buffer);
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            AppendLocked(data);
        }
    }

    private void AppendLocked(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || _disposed || _latchedOff || _capped)
        {
            return;
        }

        try
        {
            long remaining = _maxBytes - _written;
            if (remaining <= 0)
            {
                CapLocked();
                return;
            }

            int toWrite = data.Length > remaining
                ? checked((int)remaining)
                : data.Length;
            WriteChunkLocked(data[..toWrite]);
            _written += toWrite;

            if (toWrite < data.Length)
            {
                CapLocked();
            }
        }
        catch (Exception error) when (IsRecoverableIoFailure(error))
        {
            _latchedOff = true;
        }
    }

    private void WriteChunkLocked(ReadOnlySpan<byte> chunk)
    {
        EnsureDirectoryLocked();
        using FileStream stream = new(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        stream.Write(chunk);
        stream.Flush();
    }

    private void EnsureDirectoryLocked()
    {
        if (_directoryEnsured)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _directoryEnsured = true;
    }

    private void CapLocked()
    {
        if (_capped)
        {
            return;
        }

        _capped = true;
        try
        {
            byte[] marker = Encoding.UTF8.GetBytes(
                $"\n[OpenAC launcher] client.err.log truncated at {_maxBytes} bytes\n");
            WriteChunkLocked(marker);
        }
        catch (Exception error) when (IsRecoverableIoFailure(error))
        {
        }
    }

    private static bool IsRecoverableIoFailure(Exception error) =>
        error is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException
            or DirectoryNotFoundException;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }
}

using System;
using System.IO;
using System.Text;

namespace AcDream.Core.Chat;

public sealed class ChatSessionLog : IDisposable
{
    private readonly string _baseDirectory;
    private StreamWriter? _writer;

    public ChatSessionLog(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _baseDirectory = baseDirectory;
    }

    /// <summary>The name the player asked for, or null when nothing is open.</summary>
    public string? CurrentName { get; private set; }

    public bool IsOpen => _writer is not null;

    public static string EnsureExtension(string name)
        => Path.GetExtension(name).Length == 0 ? name + ".txt" : name;

    public bool Open(string name, out string resolvedName)
    {
        resolvedName = string.Empty;
        Close();

        if (string.IsNullOrWhiteSpace(name))
            return false;

        resolvedName = EnsureExtension(name.Trim());

        try
        {
            string path = Path.IsPathRooted(resolvedName)
                ? resolvedName
                : Path.Combine(_baseDirectory, resolvedName);

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
            CurrentName = resolvedName;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            _writer = null;
            CurrentName = null;
            return false;
        }
    }

    public bool Close()
    {
        if (_writer is null)
            return false;

        try
        {
            _writer.Dispose();
        }
        catch (IOException)
        {
        }

        _writer = null;
        CurrentName = null;
        return true;
    }

    public void Write(string? timestampPrefix, string? text)
    {
        StreamWriter? writer = _writer;
        if (writer is null)
            return;

        try
        {
            writer.Write(timestampPrefix);
            writer.Write(text);
            writer.Write('\n');
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => Close();
}

public readonly record struct ChatLogResult(
    bool Opened,
    bool Closed,
    string Name,
    string? ClosedName);

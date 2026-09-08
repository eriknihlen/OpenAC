using System.Text.Json;

namespace AcDream.Runtime.Session;

public sealed class SessionStatusWriter
{
    private const int VocabularyVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string? _path;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private bool _directoryEnsured;
    private bool _latchedOff;
    private bool _connected;
    private bool _exited;

    public SessionStatusWriter(string? path, TimeProvider? timeProvider = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsEnabled => _path is not null && !_latchedOff;

    public void Started(string sessionId) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "started",
            t = Now(),
            sessionId,
        });

    public void Connected(string sessionId)
    {
        if (!IsEnabled)
            return;

        lock (_gate)
        {
            if (_latchedOff || _exited)
                return;

            if (_connected)
            {
                if (!TryWriteLocked(new
                    {
                        v = VocabularyVersion,
                        e = "disconnected",
                        t = Now(),
                        sessionId,
                        reason = "reconnect",
                    }))
                {
                    return;
                }
                _connected = false;
            }

            if (TryWriteLocked(new
                {
                    v = VocabularyVersion,
                    e = "connected",
                    t = Now(),
                    sessionId,
                }))
            {
                _connected = true;
            }
        }
    }

    public void CharacterList(string sessionId, LiveSessionRosterReport roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (!IsEnabled)
            return;

        Write(new
        {
            v = VocabularyVersion,
            e = "characterList",
            t = Now(),
            sessionId,
            accountName = roster.AccountName,
            slotCount = roster.SlotCount,
            characters = roster.Entries
                .Select(static entry => new
                {
                    id = entry.Id,
                    name = entry.Name,
                    secondsGreyedOut = entry.SecondsGreyedOut,
                })
                .ToArray(),
        });
    }

    public void EnteredWorld(string sessionId, uint characterId, string characterName) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "enteredWorld",
            t = Now(),
            sessionId,
            characterId,
            characterName,
        });

    public void CharacterCreated(string sessionId, uint guid, string name) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "characterCreated",
            t = Now(),
            sessionId,
            guid,
            name,
        });

    public void CreationFailed(string sessionId, uint code, string reason, string name) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "creationFailed",
            t = Now(),
            sessionId,
            code,
            reason,
            name,
        });

    public void PluginLoaded(string sessionId, string plugin) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "pluginLoaded",
            t = Now(),
            sessionId,
            plugin,
        });

    public void PluginFailed(string sessionId, string plugin, string error) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "pluginFailed",
            t = Now(),
            sessionId,
            plugin,
            error,
        });

    public void LoginCommandFailed(
        string sessionId,
        int commandIndex,
        string command,
        string error) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "loginCommandFailed",
            t = Now(),
            sessionId,
            commandIndex,
            command,
            error,
        });

    public void Disconnected(string sessionId, string reason)
    {
        if (!IsEnabled)
            return;

        lock (_gate)
        {
            if (_latchedOff || _exited)
                return;

            if (TryWriteLocked(new
                {
                    v = VocabularyVersion,
                    e = "disconnected",
                    t = Now(),
                    sessionId,
                    reason,
                }))
            {
                _connected = false;
            }
        }
    }

    public void Exited(string sessionId, int code, string reason)
    {
        if (!IsEnabled)
            return;

        lock (_gate)
        {
            if (_latchedOff || _exited)
                return;

            if (_connected)
            {
                if (!TryWriteLocked(new
                    {
                        v = VocabularyVersion,
                        e = "disconnected",
                        t = Now(),
                        sessionId,
                        reason = "process-exit",
                    }))
                {
                    return;
                }
                _connected = false;
            }

            if (TryWriteLocked(new
                {
                    v = VocabularyVersion,
                    e = "exited",
                    t = Now(),
                    sessionId,
                    code,
                    reason,
                }))
            {
                _exited = true;
            }
        }
    }

    private string Now() =>
        _timeProvider.GetUtcNow().ToString(
            "O",
            System.Globalization.CultureInfo.InvariantCulture);

    private void Write<T>(T value)
    {
        if (_path is null || _latchedOff)
            return;

        lock (_gate)
        {
            if (_latchedOff || _exited)
                return;

            _ = TryWriteLocked(value);
        }
    }

    private bool TryWriteLocked<T>(T value)
    {
        string path = _path!;
        try
        {
            EnsureDirectory(path);
            string line = JsonSerializer.Serialize(value, JsonOptions);
            using FileStream stream = new(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(line);
            writer.Flush();
            return true;
        }
        catch (Exception error) when (IsRecoverableIoFailure(error))
        {
            LatchOff(path, error);
            return false;
        }
    }

    private void EnsureDirectory(string path)
    {
        if (_directoryEnsured)
            return;

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _directoryEnsured = true;
    }

    private void LatchOff(string path, Exception error)
    {
        _latchedOff = true;
        try
        {
            Console.Error.WriteLine(
                $"[status-writer] disabling status stream at '{path}' after a "
                + $"write failure ({error.GetType().Name}: {error.Message}); no "
                + "further events for this session will be written.");
        }
        catch (Exception diagnosticError)
            when (IsRecoverableIoFailure(diagnosticError)
                || diagnosticError is ObjectDisposedException
                or InvalidOperationException)
        {
        }
    }

    private static bool IsRecoverableIoFailure(Exception error) =>
        error is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException
            or DirectoryNotFoundException;
}

using System.Text;
using System.Text.Json;

namespace AcDream.Launcher.Core.Installation;

public sealed class BakeProgressJsonlParser
{
    public const int CurrentVersion = 1;

    private readonly StringBuilder _pending = new();

    public IReadOnlyList<BakeProgressEvent> Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _pending.Append(chunk);
        return Drain(completeFinalLine: false);
    }

    public IReadOnlyList<BakeProgressEvent> Complete() =>
        Drain(completeFinalLine: true);

    public static BakeProgressEvent ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return new BakeHumanOutputEvent(string.Empty);
        }

        if (trimmed[0] != '{')
        {
            return new BakeHumanOutputEvent(line.TrimEnd('\r'));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetInt32(root, "v", out int version)
                || !TryGetString(root, "e", out string? eventName))
            {
                return Malformed(line, "JSON progress requires integer 'v' and string 'e'.");
            }

            if (version != CurrentVersion)
            {
                return new FutureBakeProgressEvent(version, eventName!, line);
            }

            return eventName switch
            {
                "started" => ParseStarted(root, version, line),
                "progress" => ParseProgress(root, version, line),
                "completed" => ParseCompleted(root, version, line),
                "error" => ParseError(root, version, line),
                _ => new UnknownBakeProgressEvent(version, eventName!, line),
            };
        }
        catch (JsonException ex)
        {
            return Malformed(line, ex.Message);
        }
    }

    private IReadOnlyList<BakeProgressEvent> Drain(bool completeFinalLine)
    {
        var events = new List<BakeProgressEvent>();
        while (true)
        {
            int newline = IndexOfNewline(_pending);
            if (newline < 0)
            {
                break;
            }

            string line = _pending.ToString(0, newline);
            _pending.Remove(0, newline + 1);
            events.Add(ParseLine(line));
        }

        if (completeFinalLine && _pending.Length > 0)
        {
            string line = _pending.ToString();
            _pending.Clear();
            events.Add(ParseLine(line));
        }

        return events;
    }

    private static int IndexOfNewline(StringBuilder value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static BakeProgressEvent ParseStarted(
        JsonElement root,
        int version,
        string raw)
    {
        if (!TryGetUInt32(root, "bakeToolVersion", out uint bakeToolVersion)
            || bakeToolVersion == 0)
        {
            return Malformed(raw, "started requires a positive bakeToolVersion.");
        }

        _ = TryGetString(root, "outputPath", out string? outputPath);
        return new BakeStartedEvent(version, bakeToolVersion, outputPath);
    }

    private static BakeProgressEvent ParseProgress(
        JsonElement root,
        int version,
        string raw)
    {
        if (!TryGetString(root, "phase", out string? phase)
            || !TryGetInt64(root, "completed", out long completed)
            || !TryGetInt64(root, "total", out long total)
            || !TryGetInt32(root, "failures", out int failures)
            || !TryGetDouble(root, "elapsedSeconds", out double elapsedSeconds)
            || !TryGetDouble(root, "etaSeconds", out double etaSeconds)
            || completed < 0
            || total < 0
            || completed > total
            || failures < 0
            || elapsedSeconds < 0
            || etaSeconds < 0)
        {
            return Malformed(raw, "progress payload has missing or invalid fields.");
        }

        return new BakeWorkProgressEvent(
            version,
            phase!,
            completed,
            total,
            failures,
            elapsedSeconds,
            etaSeconds);
    }

    private static BakeProgressEvent ParseCompleted(
        JsonElement root,
        int version,
        string raw)
    {
        if (!TryGetUInt32(root, "bakeToolVersion", out uint bakeToolVersion)
            || !TryGetInt64(root, "outputBytes", out long outputBytes)
            || !TryGetInt32(root, "failures", out int failures)
            || bakeToolVersion == 0
            || outputBytes <= 0
            || failures < 0)
        {
            return Malformed(raw, "completed payload has missing or invalid fields.");
        }

        return new BakeCompletedEvent(
            version,
            bakeToolVersion,
            outputBytes,
            failures);
    }

    private static BakeProgressEvent ParseError(
        JsonElement root,
        int version,
        string raw) =>
        TryGetString(root, "message", out string? message)
        && !string.IsNullOrWhiteSpace(message)
            ? new BakeErrorEvent(version, message)
            : Malformed(raw, "error requires a non-empty message.");

    private static MalformedBakeProgressEvent Malformed(string raw, string reason) =>
        new(raw, reason);

    private static bool TryGetString(
        JsonElement root,
        string name,
        out string? value)
    {
        value = null;
        return root.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && (value = element.GetString()) is not null;
    }

    private static bool TryGetInt32(JsonElement root, string name, out int value)
    {
        value = default;
        return root.TryGetProperty(name, out JsonElement element)
            && element.TryGetInt32(out value);
    }

    private static bool TryGetUInt32(JsonElement root, string name, out uint value)
    {
        value = default;
        return root.TryGetProperty(name, out JsonElement element)
            && element.TryGetUInt32(out value);
    }

    private static bool TryGetInt64(JsonElement root, string name, out long value)
    {
        value = default;
        return root.TryGetProperty(name, out JsonElement element)
            && element.TryGetInt64(out value);
    }

    private static bool TryGetDouble(JsonElement root, string name, out double value)
    {
        value = default;
        return root.TryGetProperty(name, out JsonElement element)
            && element.TryGetDouble(out value)
            && double.IsFinite(value);
    }
}

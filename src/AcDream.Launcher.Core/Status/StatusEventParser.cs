using System.Text.Json;

namespace AcDream.Launcher.Core.Status;

public static class StatusEventParser
{
    public static StatusEvent Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return UnknownEvent(line ?? string.Empty);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return UnknownEvent(line);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return MalformedEvent(
                    v: 0,
                    e: string.Empty,
                    t: default,
                    sessionId: string.Empty,
                    "status event root is not a JSON object.");
            }

            if (!TryGetEventName(root, out string e, out string eventNameError))
            {
                return MalformedEvent(
                    GetInt32OrDefault(root, "v"),
                    e,
                    GetDateTimeOffsetOrDefault(root, "t"),
                    GetStringOrDefault(root, "sessionId"),
                    eventNameError);
            }

            if (!IsKnownEventName(e))
            {
                return new UnknownStatusEvent
                {
                    V = GetInt32OrDefault(root, "v"),
                    E = e,
                    T = GetDateTimeOffsetOrDefault(root, "t"),
                    SessionId = GetStringOrDefault(root, "sessionId"),
                    RawJson = line,
                };
            }

            try
            {
                int v = RequireVersionOne(root);
                DateTimeOffset t = RequireUtcTimestamp(root, "t");
                string sessionId = RequireNonEmptyString(root, "sessionId");

                return e switch
                {
                    "started" =>
                        new StartedStatusEvent { V = v, E = e, T = t, SessionId = sessionId },
                    "connected" =>
                        new ConnectedStatusEvent { V = v, E = e, T = t, SessionId = sessionId },
                    "characterList" =>
                        ParseCharacterList(root, v, e, t, sessionId),
                    "enteredWorld" =>
                        ParseEnteredWorld(root, v, e, t, sessionId),
                    "pluginLoaded" =>
                        ParsePluginLoaded(root, v, e, t, sessionId),
                    "pluginFailed" =>
                        ParsePluginFailed(root, v, e, t, sessionId),
                    "loginCommandFailed" =>
                        ParseLoginCommandFailed(root, v, e, t, sessionId),
                    "characterCreated" =>
                        ParseCharacterCreated(root, v, e, t, sessionId),
                    "creationFailed" =>
                        ParseCreationFailed(root, v, e, t, sessionId),
                    "disconnected" =>
                        ParseDisconnected(root, v, e, t, sessionId),
                    "exited" =>
                        ParseExited(root, v, e, t, sessionId),
                    _ => throw new InvalidOperationException("known event dispatch is incomplete."),
                };
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                return MalformedEvent(
                    GetInt32OrDefault(root, "v"),
                    e,
                    GetDateTimeOffsetOrDefault(root, "t"),
                    GetStringOrDefault(root, "sessionId"),
                    ex.Message);
            }
        }
    }

    private static bool IsKnownEventName(string eventName) =>
        eventName is
            "started" or
            "connected" or
            "characterList" or
            "enteredWorld" or
            "pluginLoaded" or
            "pluginFailed" or
            "loginCommandFailed" or
            "characterCreated" or
            "creationFailed" or
            "disconnected" or
            "exited";

    private static bool TryGetEventName(
        JsonElement root,
        out string eventName,
        out string error)
    {
        if (!root.TryGetProperty("e", out JsonElement element))
        {
            eventName = string.Empty;
            error = "status event is missing 'e'.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            eventName = string.Empty;
            error = "status event field 'e' is not a string.";
            return false;
        }

        eventName = element.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(eventName))
        {
            error = "status event field 'e' is empty.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static UnknownStatusEvent UnknownEvent(string rawLine) =>
        new()
        {
            V = 0,
            E = string.Empty,
            T = default,
            SessionId = string.Empty,
            RawJson = rawLine,
        };

    private static MalformedStatusEvent MalformedEvent(
        int v,
        string e,
        DateTimeOffset t,
        string sessionId,
        string error) =>
        new()
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            Error = error,
        };

    private static StatusEvent ParseCharacterList(
        JsonElement root,
        int v,
        string e,
        DateTimeOffset t,
        string sessionId)
    {
        string accountName = RequireString(root, "accountName");
        int slotCount = RequireInt32(root, "slotCount");
        JsonElement charactersElement = RequireProperty(root, "characters");

        var characters = new List<StatusCharacterEntry>();
        foreach (JsonElement item in charactersElement.EnumerateArray())
        {
            uint id = RequireUInt32(item, "id");
            string name = RequireString(item, "name");
            uint secondsGreyedOut = RequireUInt32(item, "secondsGreyedOut");
            characters.Add(new StatusCharacterEntry(id, name, secondsGreyedOut));
        }

        return new CharacterListStatusEvent
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            AccountName = accountName,
            SlotCount = slotCount,
            Characters = characters,
        };
    }

    private static StatusEvent ParseEnteredWorld(
        JsonElement root,
        int v,
        string e,
        DateTimeOffset t,
        string sessionId) =>
        new EnteredWorldStatusEvent
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            CharacterId = RequireUInt32(root, "characterId"),
            CharacterName = RequireString(root, "characterName"),
        };

    private static StatusEvent ParseCharacterCreated(
        JsonElement root,
        int v,
        string e,
        DateTimeOffset t,
        string sessionId) =>
        new CharacterCreatedStatusEvent
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            Guid = RequireUInt32(root, "guid"),
            Name = RequireString(root, "name"),
        };

    private static StatusEvent ParseCreationFailed(
        JsonElement root,
        int v,
        string e,
        DateTimeOffset t,
        string sessionId) =>
        new CreationFailedStatusEvent
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            Code = RequireUInt32(root, "code"),
            Reason = RequireString(root, "reason"),
            Name = RequireString(root, "name"),
        };

    private static StatusEvent ParsePluginLoaded(
        JsonElement root,
        int v,
        string e,
        DateTimeOffset t,
        string sessionId) =>
        new PluginLoadedStatusEvent
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            Plugin = RequireString(root, "plugin"),
        };

    private static StatusEvent ParsePluginFailed(
        JsonElement root,
        int v,
        string e,
        DateTimeOffset t,
        string sessionId) =>
        new PluginFailedStatusEvent
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            Plugin = RequireString(root, "plugin"),
            Error = RequireString(root, "error"),
        };

    private static StatusEvent ParseDisconnected(
        JsonElement root,
        int v,
        string e,
        DateTimeOffset t,
        string sessionId) =>
        new DisconnectedStatusEvent
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            Reason = RequireString(root, "reason"),
        };

    private static StatusEvent ParseLoginCommandFailed(
        JsonElement root,
        int v,
        string e,
        DateTimeOffset t,
        string sessionId)
    {
        int commandIndex = RequireInt32(root, "commandIndex");
        if (commandIndex < 0)
        {
            throw new FormatException(
                "status event field 'commandIndex' is negative.");
        }

        return new LoginCommandFailedStatusEvent
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            CommandIndex = commandIndex,
            Command = RequireString(root, "command"),
            Error = RequireString(root, "error"),
        };
    }

    private static StatusEvent ParseExited(
        JsonElement root,
        int v,
        string e,
        DateTimeOffset t,
        string sessionId) =>
        new ExitedStatusEvent
        {
            V = v,
            E = e,
            T = t,
            SessionId = sessionId,
            Code = RequireInt32(root, "code"),
            Reason = RequireString(root, "reason"),
        };

    private static int GetInt32OrDefault(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out int value)
            ? value
            : 0;

    private static string GetStringOrDefault(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset GetDateTimeOffsetOrDefault(
        JsonElement root,
        string name) =>
        root.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && element.TryGetDateTimeOffset(out DateTimeOffset value)
            && value.Offset == TimeSpan.Zero
            ? value
            : default;

    private static JsonElement RequireProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element)
            ? element
            : throw new FormatException($"status event is missing '{name}'.");

    private static string RequireString(JsonElement root, string name)
    {
        JsonElement element = RequireProperty(root, name);
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : throw new FormatException($"status event field '{name}' is not a string.");
    }

    private static int RequireInt32(JsonElement root, string name)
    {
        JsonElement element = RequireProperty(root, name);
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value)
            ? value
            : throw new FormatException($"status event field '{name}' is not an integer.");
    }

    private static int RequireVersionOne(JsonElement root)
    {
        int version = RequireInt32(root, "v");
        return version == 1
            ? version
            : throw new FormatException(
                $"status event version is {version}; expected 1.");
    }

    private static string RequireNonEmptyString(JsonElement root, string name)
    {
        string value = RequireString(root, name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FormatException($"status event field '{name}' is empty.");
    }

    private static DateTimeOffset RequireUtcTimestamp(JsonElement root, string name)
    {
        JsonElement element = RequireProperty(root, name);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new FormatException(
                $"status event field '{name}' is not an ISO-8601 UTC string.");
        }

        string text = element.GetString() ?? string.Empty;
        bool hasExplicitUtcOffset = text.EndsWith('Z')
            || text.EndsWith("+00:00", StringComparison.Ordinal);
        if (!hasExplicitUtcOffset
            || !element.TryGetDateTimeOffset(out DateTimeOffset value)
            || value.Offset != TimeSpan.Zero)
        {
            throw new FormatException(
                $"status event field '{name}' is not an ISO-8601 UTC timestamp.");
        }

        return value;
    }

    private static uint RequireUInt32(JsonElement root, string name)
    {
        JsonElement element = RequireProperty(root, name);
        return element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out uint value)
            ? value
            : throw new FormatException(
                $"status event field '{name}' is not an unsigned integer.");
    }
}

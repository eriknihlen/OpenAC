using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.DrakBot.Profiles;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// The profile as a flat form for the phone: every scalar leaf of the
/// profile's JSON under a dotted key (<c>vitals.healBelow</c>), lists of
/// names joined for reading. A change comes back as one key and one value,
/// is patched into the profile's JSON, and only takes when the whole
/// profile still parses - so an unknown key, a wrong type or an enum name
/// the profile does not know is refused rather than half-applied. The
/// monster and loot rules are lists of objects and are not in the form.
/// </summary>
internal static class RemoteSettingsBridge
{
    public const string Schema = "acdream.drakbot.settings/1";

    /// <summary>Keys nobody should set from a phone: the profile's identity and the dashboard's window state.</summary>
    private static readonly string[] HiddenPrefixes = ["name", "dashboard."];

    public static byte[] Build(BotProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var flat = new JsonObject();
        if (JsonNode.Parse(profile.ToJson()) is JsonObject root)
            Flatten(root, string.Empty, flat);
        var document = new JsonObject
        {
            ["schema"] = Schema,
            ["profile"] = profile.Name,
            ["values"] = flat,
        };
        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    private static void Flatten(JsonObject node, string prefix, JsonObject into)
    {
        foreach ((string name, JsonNode? child) in node)
        {
            string key = prefix.Length == 0 ? name : prefix + "." + name;
            if (IsHidden(key))
                continue;
            switch (child)
            {
                case JsonObject nested:
                    Flatten(nested, key, into);
                    break;
                case JsonArray array:
                    if (array.All(static element => element is JsonValue))
                        into[key] = string.Join(", ", array.Select(static element => element!.ToString()));
                    break;
                case JsonValue value:
                    into[key] = value.DeepClone();
                    break;
            }
        }
    }

    private static bool IsHidden(string key)
    {
        foreach (string prefix in HiddenPrefixes)
        {
            if (prefix.EndsWith('.') ? key.StartsWith(prefix, StringComparison.Ordinal) : key == prefix)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Patches one setting into a copy of the profile. The value must be
    /// the kind the profile already holds there: a bool for a bool, a
    /// number for a number (rounded when the profile holds an integer), a
    /// string for a string or an enum. Returns the new profile, or null
    /// with the reason.
    /// </summary>
    public static BotProfile? TryApply(BotProfile profile, string key, JsonElement value, out string error)
    {
        ArgumentNullException.ThrowIfNull(profile);
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(key) || IsHidden(key))
        {
            error = $"'{key}' is not a setting";
            return null;
        }
        if (JsonNode.Parse(profile.ToJson()) is not JsonObject root)
        {
            error = "the profile could not be read";
            return null;
        }
        string[] path = key.Split('.');
        JsonObject parent = root;
        for (int index = 0; index < path.Length - 1; index++)
        {
            if (parent[path[index]] is not JsonObject next)
            {
                error = $"'{key}' is not a setting";
                return null;
            }
            parent = next;
        }
        string leaf = path[^1];
        if (parent[leaf] is not JsonValue existing)
        {
            error = $"'{key}' is not a setting";
            return null;
        }

        JsonNode? replacement;
        switch (existing.GetValueKind())
        {
            case JsonValueKind.True or JsonValueKind.False:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = $"'{key}' takes on or off";
                    return null;
                }
                replacement = JsonValue.Create(value.GetBoolean());
                break;
            case JsonValueKind.Number:
                if (value.ValueKind != JsonValueKind.Number)
                {
                    error = $"'{key}' takes a number";
                    return null;
                }
                bool integral = !existing.ToJsonString().Contains('.', StringComparison.Ordinal)
                    && !existing.ToJsonString().Contains('E', StringComparison.OrdinalIgnoreCase);
                double number = value.GetDouble();
                if (!double.IsFinite(number))
                {
                    error = $"'{key}' takes a finite number";
                    return null;
                }
                replacement = integral
                    ? JsonValue.Create((long)Math.Round(number))
                    : JsonValue.Create(number);
                break;
            case JsonValueKind.String:
                if (value.ValueKind != JsonValueKind.String)
                {
                    error = $"'{key}' takes a name";
                    return null;
                }
                replacement = JsonValue.Create(value.GetString());
                break;
            default:
                error = $"'{key}' cannot be set from here";
                return null;
        }
        parent[leaf] = replacement;

        try
        {
            BotProfile patched = BotProfile.FromJson(root.ToJsonString());
            // The name is the file the profile is saved under; a patch never renames.
            return patched with { Name = profile.Name };
        }
        catch (JsonException failure)
        {
            error = $"'{key}' refused: {Shorten(failure.Message)}";
            return null;
        }
    }

    private static string Shorten(string message)
    {
        int cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }

    /// <summary>Formats a number the way the profile's JSON does, for logs.</summary>
    internal static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.String => value.GetString() ?? string.Empty,
        _ => value.ToString(),
    };
}

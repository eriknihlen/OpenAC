using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AcDream.UI.Abstractions.Input;

public sealed class RetailUnmappedKeyBindings
{
    private readonly Dictionary<(uint InputMapId, uint ActionId), List<KeyChord>> _bindings = new();

    public IReadOnlyList<KeyChord> Get(uint inputMapId, uint actionId) =>
        _bindings.TryGetValue((inputMapId, actionId), out List<KeyChord>? list)
            ? list
            : Array.Empty<KeyChord>();

    public void Set(uint inputMapId, uint actionId, IReadOnlyList<KeyChord> chords)
    {
        if (chords.Count == 0)
            _bindings.Remove((inputMapId, actionId));
        else
            _bindings[(inputMapId, actionId)] = new List<KeyChord>(chords);
    }

    public static RetailUnmappedKeyBindings LoadOrEmpty(string path)
    {
        var result = new RetailUnmappedKeyBindings();
        if (!File.Exists(path)) return result;
        try
        {
            using FileStream stream = File.OpenRead(path);
            JsonDocument doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("rows", out JsonElement rows)
                || rows.ValueKind != JsonValueKind.Array)
                return result;

            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("inputMap", out JsonElement mapEl)
                    || !row.TryGetProperty("action", out JsonElement actionEl)
                    || !row.TryGetProperty("chords", out JsonElement chordsEl)
                    || chordsEl.ValueKind != JsonValueKind.Array)
                    continue;

                uint inputMapId = Convert.ToUInt32(mapEl.GetString(), 16);
                uint actionId = Convert.ToUInt32(actionEl.GetString(), 16);
                var chords = new List<KeyChord>();
                foreach (JsonElement c in chordsEl.EnumerateArray())
                {
                    if (!c.TryGetProperty("key", out JsonElement keyEl)) continue;
                    string? keyName = keyEl.GetString();
                    if (keyName is null || !Enum.TryParse(keyName, out Silk.NET.Input.Key key)) continue;
                    var mods = ModifierMask.None;
                    if (c.TryGetProperty("mod", out JsonElement modEl)
                        && modEl.ValueKind == JsonValueKind.String
                        && modEl.GetString() is { } modStr)
                    {
                        foreach (string part in modStr.Split('|', StringSplitOptions.RemoveEmptyEntries))
                            if (Enum.TryParse(part, out ModifierMask m)) mods |= m;
                    }
                    byte device = 0;
                    if (c.TryGetProperty("device", out JsonElement devEl) && devEl.ValueKind == JsonValueKind.Number)
                        device = (byte)devEl.GetInt32();
                    chords.Add(new KeyChord(key, mods, device));
                }
                result._bindings[(inputMapId, actionId)] = chords;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"unmapped keybinds: failed to load {path}: {ex.Message}");
        }
        return result;
    }

    public void SaveToFile(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var rows = new List<object>();
        foreach (((uint inputMapId, uint actionId), List<KeyChord> chords) in _bindings)
        {
            var chordList = new List<object>();
            foreach (KeyChord chord in chords)
            {
                var entry = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["key"] = chord.Key.ToString(),
                };
                if (chord.Modifiers != ModifierMask.None) entry["mod"] = chord.Modifiers.ToString();
                if (chord.Device != 0) entry["device"] = (int)chord.Device;
                chordList.Add(entry);
            }
            rows.Add(new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["inputMap"] = $"0x{inputMapId:X}",
                ["action"] = $"0x{actionId:X}",
                ["chords"] = chordList,
            });
        }

        var root = new Dictionary<string, object> { ["version"] = 1, ["rows"] = rows };
        File.WriteAllText(path, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }
}

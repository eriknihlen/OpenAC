using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace AcDream.App.UI;

public sealed class ControlsIni
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections;

    private ControlsIni(Dictionary<string, Dictionary<string, string>> s) => _sections = s;

    /// <summary>Load from disk; returns an empty sheet if the file is absent.</summary>
    public static ControlsIni Load(string path)
        => System.IO.File.Exists(path)
            ? Parse(System.IO.File.ReadAllText(path))
            : new ControlsIni(new());

    public static ControlsIni Parse(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(System.StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                cur = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
                sections[name] = cur;
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || cur is null) continue;
            cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return new ControlsIni(sections);
    }

    public string? Get(string section, string key)
        => _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    public bool TryColor(string section, string key, out Vector4 color)
    {
        color = default;
        var v = Get(section, key);
        if (v is null || v.Length != 9 || v[0] != '#') return false;
        if (!uint.TryParse(v.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
            return false;
        float a = ((argb >> 24) & 0xFF) / 255f;
        float r = ((argb >> 16) & 0xFF) / 255f;
        float g = ((argb >> 8) & 0xFF) / 255f;
        float b = (argb & 0xFF) / 255f;
        color = new Vector4(r, g, b, a);
        return true;
    }
}

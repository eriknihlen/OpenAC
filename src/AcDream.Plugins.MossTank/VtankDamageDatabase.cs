using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal static class VtankDamageDatabase
{
    private static readonly MonsterDamageType[] Fallback =
    [
        MonsterDamageType.Pierce,
        MonsterDamageType.Bludgeon,
        MonsterDamageType.Slash,
        MonsterDamageType.Acid,
        MonsterDamageType.Electric,
        MonsterDamageType.Cold,
        MonsterDamageType.Fire,
    ];

    private static readonly Dictionary<string, MonsterDamageType[]> Overrides =
        ParseNames(OverrideData);
    private static readonly Dictionary<int, MonsterDamageType[]> Species =
        ParseSpecies(SpeciesData);

    public static IReadOnlyList<MonsterDamageType> Preferences(
        in PluginCombatTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.Name)
            && Overrides.TryGetValue(target.Name, out MonsterDamageType[]? exact))
        {
            return exact;
        }

        if (target.SpeciesId != 0
            && Species.TryGetValue(target.SpeciesId, out MonsterDamageType[]? species))
        {
            return species;
        }
        return Fallback;
    }

    public static int PreferenceIndex(
        in PluginCombatTarget target,
        MonsterDamageType damage)
    {
        IReadOnlyList<MonsterDamageType> preferences = Preferences(target);
        for (int i = 0; i < preferences.Count; i++)
        {
            if (preferences[i] == damage)
                return i;
        }

        for (int i = 0; i < Fallback.Length; i++)
        {
            if (Fallback[i] == damage)
                return preferences.Count + i;
        }
        return int.MaxValue;
    }

    private static Dictionary<string, MonsterDamageType[]> ParseNames(
        string data)
    {
        var result = new Dictionary<string, MonsterDamageType[]>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ReadOnlySpan<char> line in data.AsSpan().EnumerateLines())
        {
            int separator = line.IndexOf('|');
            if (separator <= 0)
                continue;
            result[line[..separator].ToString()] = ParseElements(
                line[(separator + 1)..]);
        }
        return result;
    }

    private static Dictionary<int, MonsterDamageType[]> ParseSpecies(
        string data)
    {
        var result = new Dictionary<int, MonsterDamageType[]>();
        foreach (ReadOnlySpan<char> line in data.AsSpan().EnumerateLines())
        {
            int separator = line.IndexOf('|');
            if (separator <= 0
                || !int.TryParse(line[..separator], out int species))
            {
                continue;
            }
            result[species] = ParseElements(line[(separator + 1)..]);
        }
        return result;
    }

    private static MonsterDamageType[] ParseElements(ReadOnlySpan<char> text)
    {
        var result = new List<MonsterDamageType>(7);
        foreach (Range range in text.Split(';'))
        {
            if (!int.TryParse(text[range], out int raw))
                continue;
            MonsterDamageType mapped = raw switch
            {
                0 => MonsterDamageType.Pierce,
                1 => MonsterDamageType.Bludgeon,
                2 => MonsterDamageType.Slash,
                3 => MonsterDamageType.Acid,
                4 => MonsterDamageType.Electric,
                5 => MonsterDamageType.Cold,
                6 => MonsterDamageType.Fire,
                _ => MonsterDamageType.None,
            };
            if (mapped != MonsterDamageType.None && !result.Contains(mapped))
                result.Add(mapped);
        }
        return [.. result];
    }

    private const string OverrideData = """
Magma Golem|5;1;0;2
Mist Golem|5;4;3;6;2;0;1
Nubilous Golem|4;5;3;2;0;1
Plasma Golem|4;5;3;2;0;1
Vapor Golem|5;4;15;4;3;6;2;0;1
Damaged Glacial Golem|6;1;0;2
Fractured Glacial Golem|6;1;0;2
Tanada Nanjou Shou-jen|3;4;6;5
Disgraced Nanjou Shou-jen|3;4;6;5
Magma Golem Exarch|5;1;0;2
Pillar of Fire|5;2;0
Infused Blood Golem|5;3;4;1;0;6;2
Infused Empyrean Blood Golem|5;3;4;2;6;0;1
Sapphire Golem|0;3;1;5;6;4;2
High Priestess Xik Minru|1;2;0
Contained Rift|2;1;0
Ebon Rift|2;1;0
Fallen Rift|2;1;0
Narrow Rift|2;1;0
Quiddity Rift|2;1;0
Shallow Rift|2;1;0
Tenebrous Rift|2;1;0
Umbral Rift|2;1;0
Unstable Rift|2;1;0
Aqueous Golem|4;6;5;3;2;1;0
Wave Golem|4;6;5;3;2;1;0
Unstable Magma Golem|5;1;0;2
Behemoth of Tenkarrdun|5;1;0;2
Small Magma Golem|5;1;0;2
Atlan's Crafting Golem|5;1;0;2
Bur Lizk|5;4;0;2
Dust Golem|5;4;6;3;0;1;2
Ancient Magma Golem|5;1;0;2
Frozen Ice Golem|6;1;0;2
Frozen Glacial Golem|6;1;0;2
Forge Golem|5;4;3;1;0;2
Frozen Gearknight|6
Diaphanous Nephol Golem|5;4;3;6;2;0;1
Tenuous Nephol Golem|5;4;3;6;2;0;1
Turbid Nephol Golem|5;4;3;6;2;0;1
Wall of Ice|6;1;0;2;3;4
Scold|5;1;0;2
Scold Chunk|5;1;0;2
Scold Lump|5;1;0;2
Freezing Mist Golem|5;4;3;6;2;0;1
Frost Golem|6;4;5;3
Elite Guardian|5;6
Enraged Ancient Soul|6;3;1;4;5;2;0
Mudmouth|6;4;3;5;1;0;2
Fiery Defender|5;2;4;1;0;3
Follower of Deewain|4;3;1;0;5;2;6
Chilled Defender|4;3;6;1;2;0;5
Charged Defender|5;3;4;2;1;0
Iron Golem Samurai|3;4;5;6;2;1;0
Clay Golem Samurai|1;5;6;4;3;2;0
Bronze Golem Samurai|4;3;5;6;2;1;0
Spectral Nanjou Shou-jen|1;6;2;3;4;5;0
Spectral Samurai|5;4;3;2;1;6;0
Spectral Claw Master|1;6;2;3;4;5;0
""";

    private const string SpeciesData = """
0|2
1|1;0;2;5;6;4;3
2|4;6;5;2;0;1;3
3|6;1;2;3;5;0;4
4|6;1;4;3;2;5;0
5|4;3;2;0;1;5;6
6|2;0;5;3;1;4;6
7|0;2;1;6;3;5;4
8|6;0;1;5;3;2;4
9|1;2;0;3;6;5;4
10|1;4;2;0;5;3;6
11|2
12|2
13|1;3;0;5;6;4;2
14|6;3;2;1;4;0;5
15|2;0;1
16|5;2;4;3;0;1;6
17|2;0;3;1;5;4;6
18|2
19|6;0;1;2;3;5;4
20|2;0;1;3;4;5
21|0;4;6;3
22|6;2;1;4;0;3;5
23|6;0;1;3;2;5;4
24|6;3;2
25|2
26|5;2;0;6
27|0;2;1
28|5;3;0
29|4;5;2
30|1;2;0
31|6;0;1;2;3;4;5
32|5;1;3
33|2;0;1
34|2;0;1
35|1;0;2
36|2;6;0
37|2
38|5;2;0
39|6;2;1
40|2
41|2
42|3;2;0
43|2
44|2;0;1
45|2;5;3
46|6;0;2;1;3;4;5
47|1;0;2
48|5;2;0;3;1;6;4
49|6;2;0;1
50|2;6;1
51|2;1;0
52|6;2;1;0
53|5;1;2;4;6;3;0
54|5;2;0;1
55|1;5;4;2;6;3;0
56|5;1;3
57|2;0;5;3;1;4;6
58|2;0;5;3;1;4;6
59|5;2;0;3;1;6;4
60|4;2;0
61|6;2;0
62|2;0;1
63|3;4;6;5;1;2;0
64|2
65|2
66|2
67|2
68|2
69|2
70|4;3;0;2;1
71|1;5;4;0;2;6;3
72|2
73|2
74|2
75|5;0;2;6;1;4;3
76|2
77|6;2;0;1;3;4;5
78|4;6;5;2;3;0;1
79|2;0;6;5;4;3;1
80|6;2;1;0
81|1;0;6;2;3;4;5
82|2;1;0
83|4;2;0;1
84|1;2;0
85|2
86|2;0;1
87|2
88|1;0;2
89|0;1;2
90|2
91|2
92|1;0;2
93|2
94|2
95|6;2;1;0
96|2
-1|1;0;2;3;4;5;6
97|6
98|2;0;1
99|3;4;1;0;6;5;2
100|6;3;2;0;1;5;4
101|3;1;6;0;2;4;5
""";
}

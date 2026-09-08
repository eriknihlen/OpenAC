using System.Collections.Frozen;

namespace AcDream.Runtime.Chat;

public static class RetailChannelTagTable
{
    private static readonly FrozenDictionary<string, uint> ByTag =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["abuse"] = 0x00000001u,
            ["ad"] = 0x00000002u,
            ["admin"] = 0x00000002u,
            ["au"] = 0x00000004u,
            ["audit"] = 0x00000004u,
            ["av"] = 0x00000008u,
            ["av1"] = 0x00000008u,
            ["advocate"] = 0x00000008u,
            ["advocate1"] = 0x00000008u,
            ["av2"] = 0x00000010u,
            ["advocate2"] = 0x00000010u,
            ["av3"] = 0x00000020u,
            ["advocate3"] = 0x00000020u,
            ["sent"] = 0x00000200u,
            ["sentinel"] = 0x00000200u,
            ["celestialhand"] = 0x08000000u,
            ["celhan"] = 0x08000000u,
            ["eldrytchweb"] = 0x10000000u,
            ["eldweb"] = 0x10000000u,
            ["radiantblood"] = 0x20000000u,
            ["radblo"] = 0x20000000u,
            ["ol"] = 0x40000000u,
            ["olthoi"] = 0x40000000u,

            ["fellowship"] = 0x00000800u,
            ["fellow"] = 0x00000800u,
            ["fellows"] = 0x00000800u,
            ["f"] = 0x00000800u,
            ["group"] = 0x00000800u,
            ["g"] = 0x00000800u,
            ["party"] = 0x00000800u,
            ["vassals"] = 0x00001000u,
            ["vassal"] = 0x00001000u,
            ["v"] = 0x00001000u,
            ["patron"] = 0x00002000u,
            ["p"] = 0x00002000u,
            ["monarch"] = 0x00004000u,
            ["m"] = 0x00004000u,
            ["covassals"] = 0x01000000u,
            ["covassal"] = 0x01000000u,
            ["co-vassals"] = 0x01000000u,
            ["c"] = 0x01000000u,
            ["a"] = 0x02000000u,
            ["ab"] = 0x02000000u,
            ["allegiance"] = 0x02000000u,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolve(string tag, out uint channelId) =>
        ByTag.TryGetValue(tag, out channelId);

    private static readonly FrozenSet<string> RegisteredVerbTags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fellowship", "fellow", "fellows", "f", "group", "g", "party",
            "vassals", "vassal", "v",
            "patron", "p",
            "monarch", "m",
            "covassals", "covassal", "co-vassals", "c",
            "a", "ab", "allegiance",
            "olthoi",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsUnregisteredFallbackTag(string tag) =>
        ByTag.ContainsKey(tag) && !RegisteredVerbTags.Contains(tag);
}

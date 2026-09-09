namespace AcDream.Plugins.MossTank;

internal static class BuffElementProfile
{
    internal enum Element
    {
        Pierce = 0,
        Bludgeon = 1,
        Slash = 2,
        Acid = 3,
        Lightning = 4,
        Cold = 5,
        Fire = 6,
        Physical = 99,
    }

    public static List<Element> Banes(int mode, string? customLetters) =>
        Parse(BaneProfileLetters(mode, customLetters));

    private static string BaneProfileLetters(int mode, string? customLetters) =>
        mode switch
        {
            1 => customLetters ?? string.Empty,
            2 => "All",
            3 => "None",
            4 => "B",
            5 => "BPS",
            6 => "BPSA",
            7 => "BPSAC",
            8 => "ALFC",
            _ => "???",
        };

    public static List<Element> Parse(string? letters)
    {
        var result = new List<Element>(7);
        if (letters is null)
            return result;
        if (string.Equals(letters, "All", StringComparison.Ordinal))
        {
            result.Add(Element.Acid);
            result.Add(Element.Bludgeon);
            result.Add(Element.Cold);
            result.Add(Element.Fire);
            result.Add(Element.Lightning);
            result.Add(Element.Pierce);
            result.Add(Element.Slash);
            return result;
        }
        if (string.Equals(letters, "None", StringComparison.Ordinal))
            return result;

        foreach (char letter in letters)
        {
            Element? element = letter switch
            {
                'A' => Element.Acid,
                'L' => Element.Lightning,
                'F' => Element.Fire,
                'C' => Element.Cold,
                'B' => Element.Bludgeon,
                'P' => Element.Pierce,
                'S' => Element.Slash,
                _ => null,
            };
            if (element is { } value && !result.Contains(value))
                result.Add(value);
        }
        return result;
    }

    public static string BaneSpellName(Element element) => element switch
    {
        Element.Acid => "Acid Bane I",
        Element.Bludgeon => "Bludgeon Bane I",
        Element.Cold => "Frost Bane I",
        Element.Fire => "Flame Bane I",
        Element.Lightning => "Lightning Bane I",
        Element.Physical => "Impenetrability I",
        Element.Pierce => "Piercing Bane I",
        Element.Slash => "Blade Bane I",
        _ => string.Empty,
    };
}

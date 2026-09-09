using System.Text.RegularExpressions;

namespace AcDream.Plugins.MossTank;

internal enum CombatResultTextClass
{
    None,

    Fail,

    PermanentFail,

    Success,

    Kill,
}

internal static class CombatResultText
{
    private const RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary><c>d3.cs:46-47</c> — <c>l.g.a</c>, plain fail/resist.</summary>
    private static readonly Regex[] FailPatterns =
    [
        new("^Your spell fizzled.$", Options),
        new("^(?<targetname>.*) resists your spell$", Options),
    ];

    /// <summary><c>d3.cs:48-52</c> — <c>l.g.b</c>, permanent fail.</summary>
    private static readonly Regex[] PermanentFailPatterns =
    [
        new("^Target is out of range$", Options),
        new(
            "^You fail to affect (?<targetname>.*) because you are not a player killer!$",
            Options),
        new(
            "^You fail to affect (?<targetname>.*) because .* is not a player killer!$",
            Options),
        new(
            "^You fail to affect (?<targetname>.*) because beneficial spells do not affect .*!$",
            Options),
        new("^(?<targetname>.*) is an invalid target.$", Options),
    ];

    /// <summary><c>d3.cs:53-65</c> — <c>l.g.c</c>, success.</summary>
    private static readonly Regex[] SuccessPatterns =
    [
        new("^You cast (?<spellname>.*) on (?<targetname>.*), refreshing .*$", Options),
        new("^You cast (?<spellname>.*) on (?<targetname>.*), surpassing .*$", Options),
        new(
            "^You cast (?<spellname>.*) on (?<targetname>.*), but it is surpassed by .*$",
            Options),
        new("^You cast (?<spellname>.*) on (?<targetname>.*)$", Options),
        new("^You .* due to casting (?<spellname>.*) on (?<targetname>.*)$", Options),
        new("^With (?<spellname>.*) you .* from (?<targetname>.*)\\.$", Options),
        new("^With (?<spellname>.*) you .* to (?<targetname>.*)\\.$", Options),
        new(
            "^(Critical hit! )?(Sneak Attack! )?You .* .* for .* points with (?<spellname>.*)\\.$",
            Options),
        new("^You cast (?<spellname>.*) and restore .* points of your .*\\.$", Options),
        new(
            "^You cast (?<spellname>.*) on yourself and lose .* points of .* and also gain .* points of .*$",
            Options),
        new("^You have been teleported.$", Options),
        new("^You cast (?<spellname>.*) on (?<targetname>.*) and dispel: .*\\.$", Options),
        new(
            "^You cast (?<spellname>.*) on (?<targetname>.*), but the dispel fails.$",
            Options),
    ];

    /// <summary><c>d3.cs:66-101</c> — <c>l.g.d</c>, kill.</summary>
    private static readonly Regex[] KillPatterns =
    [
        new("^You knock (?<targetname>.*) into next Morningthaw!$", Options),
        new("^You obliterate (?<targetname>.*)!$", Options),
        new("^(?<targetname>.*) is utterly destroyed by your attack!$", Options),
        new("^(?<targetname>.*) catches your attack, with dire consequences!$", Options),
        new(
            "^The deadly force of your attack is so strong that (?<targetname>.*)'s ancestors feel it!$",
            Options),
        new("^You smite (?<targetname>.*) mightily!$", Options),
        new(
            "^You slay (?<targetname>.*) viciously enough to impart death several times over!$",
            Options),
        new("^You killed (?<targetname>.*)!$", Options),
        new("^(?<targetname>.*) is torn to ribbons by your assault!$", Options),
        new("^You cleave (?<targetname>.*) in twain!$", Options),
        new("^Your killing blow nearly turns (?<targetname>.*) inside-out!$", Options),
        new("^You split (?<targetname>.*) apart!$", Options),
        new(
            "^The thunder of crushing (?<targetname>.*) is followed by the deafening silence of death!$",
            Options),
        new("^You beat (?<targetname>.*) to a lifeless pulp!$", Options),
        new("^(?<targetname>.*) is shattered by your assault!$", Options),
        new(
            "^You flatten (?<targetname>.*)'s body with the force of your assault!$",
            Options),
        new("^(?<targetname>.*) is fatally punctured!$", Options),
        new("^(?<targetname>.*)'s perforated corpse falls before you!$", Options),
        new("^You run (?<targetname>.*) through!$", Options),
        new(
            "^(?<targetname>.*)'s death is preceded by a sharp, stabbing pain!$",
            Options),
        new("^You bring (?<targetname>.*) to a fiery end!$", Options),
        new("^(?<targetname>.*) is incinerated by your assault!$", Options),
        new("^(?<targetname>.*) is reduced to cinders!$", Options),
        new("^(?<targetname>.*)'s seared corpse smolders before you!$", Options),
        new(
            "^Your lightning coruscates over (?<targetname>.*)'s mortal remains!$",
            Options),
        new("^Blistered by lightning, (?<targetname>.*) falls!$", Options),
        new("^Electricity tears (?<targetname>.*) apart!$", Options),
        new("^Your assault sends (?<targetname>.*) to an icy death!$", Options),
        new("^Your attack stops (?<targetname>.*) cold!$", Options),
        new("^(?<targetname>.*) suffers a frozen fate!$", Options),
        new("^(?<targetname>.*)'s last strength dissolves before you!$", Options),
        new("^(?<targetname>.*) is liquified by your attack!$", Options),
        new("^You reduce (?<targetname>.*) to a sizzling, oozing mass!$", Options),
        new("^You reduce (?<targetname>.*) to a drained, twisted corpse!$", Options),
        new("^(?<targetname>.*) is dessicated by your attack!$", Options),
        new("^(?<targetname>.*)'s last strength withers before you!$", Options),
    ];

    public static CombatResultTextClass Classify(
        string text,
        out string spellName,
        out string targetName)
    {
        spellName = string.Empty;
        targetName = string.Empty;
        if (string.IsNullOrEmpty(text))
            return CombatResultTextClass.None;

        if (TryMatch(KillPatterns, text, ref spellName, ref targetName))
            return CombatResultTextClass.Kill;
        if (TryMatch(PermanentFailPatterns, text, ref spellName, ref targetName))
            return CombatResultTextClass.PermanentFail;
        if (TryMatch(FailPatterns, text, ref spellName, ref targetName))
            return CombatResultTextClass.Fail;
        if (TryMatch(SuccessPatterns, text, ref spellName, ref targetName))
            return CombatResultTextClass.Success;
        return CombatResultTextClass.None;
    }

    private static bool TryMatch(
        Regex[] patterns,
        string text,
        ref string spellName,
        ref string targetName)
    {
        foreach (Regex pattern in patterns)
        {
            Match match = pattern.Match(text);
            if (!match.Success)
                continue;
            Group spell = match.Groups["spellname"];
            Group target = match.Groups["targetname"];
            spellName = spell.Success ? spell.Value : string.Empty;
            targetName = target.Success ? target.Value : string.Empty;
            return true;
        }
        return false;
    }
}

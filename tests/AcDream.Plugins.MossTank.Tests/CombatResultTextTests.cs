namespace AcDream.Plugins.MossTank.Tests;

public sealed class CombatResultTextTests
{
    [Theory]
    // d3.cs:46-47 — l.g.a, plain fail.
    [InlineData("Your spell fizzled.", CombatResultTextClass.Fail, "")]
    [InlineData("Drudge Slinker resists your spell", CombatResultTextClass.Fail, "Drudge Slinker")]
    // d3.cs:48-52 — l.g.b, permanent fail: the ONLY thing that force-trips the
    // blacklist (gj.cs:412 -> fp.a).
    [InlineData("Target is out of range", CombatResultTextClass.PermanentFail, "")]
    [InlineData(
        "Drudge Slinker is an invalid target.",
        CombatResultTextClass.PermanentFail,
        "Drudge Slinker")]
    [InlineData(
        "You fail to affect Drudge Slinker because you are not a player killer!",
        CombatResultTextClass.PermanentFail,
        "Drudge Slinker")]
    // d3.cs:53-65 — l.g.c, success.
    [InlineData(
        "You cast Frost Bolt VII on Drudge Slinker",
        CombatResultTextClass.Success,
        "Drudge Slinker")]
    [InlineData(
        "You cast Imperil Other VII on Drudge Slinker, refreshing Imperil Other VI",
        CombatResultTextClass.Success,
        "Drudge Slinker")]
    // d3.cs:66-101 — l.g.d, kill.
    [InlineData("You killed Drudge Slinker!", CombatResultTextClass.Kill, "Drudge Slinker")]
    [InlineData("You obliterate Drudge Slinker!", CombatResultTextClass.Kill, "Drudge Slinker")]
    [InlineData(
        "Drudge Slinker's perforated corpse falls before you!",
        CombatResultTextClass.Kill,
        "Drudge Slinker")]
    [InlineData(
        "You knock Drudge Slinker into next Morningthaw!",
        CombatResultTextClass.Kill,
        "Drudge Slinker")]
    // Anything else.
    [InlineData("You say, \"Frost Bolt VII\"", CombatResultTextClass.None, "")]
    [InlineData("Drudge Slinker hits you for 12 points of slashing damage!",
        CombatResultTextClass.None, "")]
    internal void ClassifiesRetailsOwnResultLines(
        string text,
        CombatResultTextClass expected,
        string expectedTarget)
    {
        Assert.Equal(
            expected,
            CombatResultText.Classify(text, out _, out string target));
        Assert.Equal(expectedTarget, target);
    }

    [Fact]
    public void SuccessLineCapturesTheSpellName()
    {
        Assert.Equal(
            CombatResultTextClass.Success,
            CombatResultText.Classify(
                "You cast Frost Bolt VII on Drudge Slinker",
                out string spell,
                out string target));
        Assert.Equal("Frost Bolt VII", spell);
        Assert.Equal("Drudge Slinker", target);
    }

    [Fact]
    public void EveryRetailKillVerbIsCovered()
    {
        string[] lines =
        [
            "You knock X into next Morningthaw!",
            "You obliterate X!",
            "X is utterly destroyed by your attack!",
            "X catches your attack, with dire consequences!",
            "The deadly force of your attack is so strong that X's ancestors feel it!",
            "You smite X mightily!",
            "You slay X viciously enough to impart death several times over!",
            "You killed X!",
            "X is torn to ribbons by your assault!",
            "You cleave X in twain!",
            "Your killing blow nearly turns X inside-out!",
            "You split X apart!",
            "The thunder of crushing X is followed by the deafening silence of death!",
            "You beat X to a lifeless pulp!",
            "X is shattered by your assault!",
            "You flatten X's body with the force of your assault!",
            "X is fatally punctured!",
            "X's perforated corpse falls before you!",
            "You run X through!",
            "X's death is preceded by a sharp, stabbing pain!",
            "You bring X to a fiery end!",
            "X is incinerated by your assault!",
            "X is reduced to cinders!",
            "X's seared corpse smolders before you!",
            "Your lightning coruscates over X's mortal remains!",
            "Blistered by lightning, X falls!",
            "Electricity tears X apart!",
            "Your assault sends X to an icy death!",
            "Your attack stops X cold!",
            "X suffers a frozen fate!",
            "X's last strength dissolves before you!",
            "X is liquified by your attack!",
            "You reduce X to a sizzling, oozing mass!",
            "You reduce X to a drained, twisted corpse!",
            "X is dessicated by your attack!",
            "X's last strength withers before you!",
        ];

        Assert.Equal(36, lines.Length);
        foreach (string line in lines)
        {
            Assert.Equal(
                CombatResultTextClass.Kill,
                CombatResultText.Classify(line, out _, out _));
        }
    }
}

using System.Linq;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using Xunit;

namespace AcDream.Core.Tests.Chat;

public sealed class CombatChatTranslatorTests
{
    private static (ChatLog, CombatState, CombatChatTranslator) Setup()
    {
        var chat = new ChatLog();
        var combat = new CombatState();
        var t = new CombatChatTranslator(combat, chat);
        return (chat, combat, t);
    }


    public static TheoryData<uint, string[]> VerbTable() => new()
    {
        { 0x1u, new[] { "scratch", "scratches", "cut", "cuts", "slash", "slashes", "mangle", "mangles" } },
        { 0x2u, new[] { "nick", "nicks", "stab", "stabs", "impale", "impales", "gore", "gores" } },
        { 0x4u, new[] { "graze", "grazes", "bash", "bashes", "smash", "smashes", "crush", "crushes" } },
        { 0x8u, new[] { "numb", "numbs", "chill", "chills", "frost", "frosts", "freeze", "freezes" } },
        { 0x10u, new[] { "singe", "singes", "scorch", "scorches", "burn", "burns", "incinerate", "incinerates" } },
        { 0x20u, new[] { "blister", "blisters", "sear", "sears", "corrode", "corrodes", "dissolve", "dissolves" } },
        { 0x40u, new[] { "spark", "sparks", "shock", "shocks", "jolt", "jolts", "blast", "blasts" } },
        { 0x80u, new[] { "drain", "drains", "exhaust", "exhausts", "siphon", "siphons", "deplete", "depletes" } },
        { 0x400u, new[] { "scar", "scars", "twist", "twists", "wither", "withers", "eradicate", "eradicates" } },
        { 0x100u, new[] { "hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits" } },
        { 0x200u, new[] { "hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits" } },
        { 0x10000000u, new[] { "hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits" } },
        { 0x1u | 0x2u, new[] { "hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits" } },
        { 0x0u, new[] { "hit", "hits", "hit", "hits", "hit", "hits", "hit", "hits" } },
    };

    [Theory]
    [MemberData(nameof(VerbTable))]
    public void InqCombatHitAdjectives_MatchesRetailVerbTablePerTypeAndBand(
        uint damageType, string[] expected)
    {
        // Well inside each band, so this pins the VERBS; the boundaries get
        // their own test below.
        double[] samples = { 0.05, 0.20, 0.40, 0.90 };
        for (int band = 0; band < 4; band++)
        {
            CombatHitAdjectives.Inq(
                damageType, samples[band], out string verb, out string third);
            Assert.Equal(expected[band * 2], verb);
            Assert.Equal(expected[(band * 2) + 1], third);
        }
    }

    [Theory]
    [InlineData(0.10, "grazes")]      // == 0.10 stays in the light band
    [InlineData(0.100001, "bashes")]
    [InlineData(0.25, "bashes")]      // == 0.25 stays in the medium band
    [InlineData(0.250001, "smashes")]
    [InlineData(0.50, "smashes")]     // == 0.50 stays in the heavy band
    [InlineData(0.500001, "crushes")]
    public void InqCombatHitAdjectives_BoundaryValuesTakeTheWeakerVerb(
        double percent, string expectedThirdPerson)
    {
        CombatHitAdjectives.Inq(0x4u /*BLUDGEON*/, percent, out _, out string third);
        Assert.Equal(expectedThirdPerson, third);
    }

    [Theory]
    [InlineData(0.10, "nick")]
    [InlineData(0.100001, "stab")]
    [InlineData(0.25, "stab")]
    [InlineData(0.250001, "impale")]
    [InlineData(0.50, "impale")]
    [InlineData(0.500001, "gore")]
    public void InqCombatHitAdjectives_PlusSLadderBoundariesAlsoTakeTheWeakerVerb(
        double percent, string expectedFirstPerson)
    {
        CombatHitAdjectives.Inq(0x2u /*PIERCE*/, percent, out string verb, out string third);
        Assert.Equal(expectedFirstPerson, verb);
        Assert.Equal(expectedFirstPerson + "s", third);
    }

    [Fact]
    public void InqCombatHitAdjectives_LowestBandIsInclusiveOfZero()
    {
        CombatHitAdjectives.Inq(0x4u, 0.0, out string verb, out _);
        Assert.Equal("graze", verb);
    }

    [Fact]
    public void InqCombatHitAdjectives_NegativePercent_LeavesVerbsEmpty()
    {
        bool typed = CombatHitAdjectives.Inq(
            0x4u, -0.01, out string verb, out string third);

        Assert.False(typed);
        Assert.Equal("", verb);
        Assert.Equal("", third);
    }


    [Theory]
    [InlineData(0x1u, "Slashing")]
    [InlineData(0x2u, "Piercing")]
    [InlineData(0x4u, "Bludgeoning")]
    [InlineData(0x8u, "Cold")]
    [InlineData(0x10u, "Fire")]
    [InlineData(0x20u, "Acid")]
    [InlineData(0x40u, "Electrical")]
    [InlineData(0x400u, "Nether")]
    [InlineData(0x10000000u, "Prismatic")]
    public void DamageTypeToString_MatchesRetailLiterals(uint bit, string expected)
        => Assert.Equal(expected, DamageTypeText.Describe(bit));

    [Theory]
    [InlineData(0x80u)]
    [InlineData(0x100u)]
    [InlineData(0x200u)]
    [InlineData(0x0u)]
    public void DamageTypeToString_HealthStaminaManaContributeNothing(uint bits)
        => Assert.Equal("", DamageTypeText.Describe(bits));

    [Fact]
    public void DamageTypeToString_MultipleBitsJoinWithSlashInRetailBitOrder()
    {
        Assert.Equal("Slashing/Piercing", DamageTypeText.Describe(0x1u | 0x2u));
        // Bit order is fixed by the walk, not by argument order.
        Assert.Equal("Cold/Fire/Nether", DamageTypeText.Describe(0x400u | 0x8u | 0x10u));
        // The unmapped health bit drops out of the join entirely.
        Assert.Equal("Slashing", DamageTypeText.Describe(0x1u | 0x80u));
    }


    [Theory]
    [InlineData(-1, "undefined")]
    [InlineData(0, "head")]
    [InlineData(1, "chest")]
    [InlineData(2, "abdomen")]
    [InlineData(3, "upper arm")]   // LowerCaseRemoveUnderscores: '_' -> ' '
    [InlineData(8, "foot")]
    [InlineData(9, "horn")]
    [InlineData(24, "upper tentacle")]
    [InlineData(27, "num")]
    [InlineData(11, "unknown")]
    [InlineData(14, "unknown")]
    [InlineData(99, "unknown")]
    public void BodyPartToString_MatchesRetailTableAndLowerCasing(
        int bodyPart, string expected)
        => Assert.Equal(expected, BodyPartText.DescribeForDisplay(bodyPart));

    // ── The four notification lines ────────────────────────────────────────

    [Fact]
    public void DamageDealt_FormatsRetailAttackerTemplate_AsInfo()
    {
        var (chat, combat, _) = Setup();
        // 0.54 > 0.50 -> the severe SLASH verb, first person: "mangle".
        combat.OnAttackerNotification(
            defenderName: "Mosswart Defiler", damageType: 0x01u,
            damage: 12u, damagePercent: 0.54);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(ChatKind.Combat, entry.Kind);
        Assert.Equal(CombatLineKind.Info, entry.CombatKind);
        Assert.Equal(
            "You mangle Mosswart Defiler for 12 points of slashing damage!",
            entry.Text);
        Assert.Equal(0x16u, entry.LogTextType);
    }

    [Fact]
    public void DamageDealt_SinglePointOfDamage_UsesSingularPoint()
    {
        var (chat, combat, _) = Setup();
        combat.OnAttackerNotification("Drudge Skulker", 0x04u, 1u, 0.02);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(
            "You graze Drudge Skulker for 1 point of bludgeoning damage!",
            entry.Text);
    }

    [Fact]
    public void DamageDealt_HealthDamage_OmitsTheTypeWordEntirely()
    {
        var (chat, combat, _) = Setup();
        combat.OnAttackerNotification("Banderling Raider", 0x80u, 5u, 0.30);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal("You siphon Banderling Raider for 5 points of damage!", entry.Text);
    }

    [Fact]
    public void DamageDealt_CriticalAndAllConditions_UsesRetailPrefixOrderAndSpacing()
    {
        var (chat, combat, _) = Setup();
        combat.OnAttackerNotification(
            defenderName: "Olthoi Soldier", damageType: 0x02u, damage: 99u,
            damagePercent: 0.20, critical: 1u,
            attackConditions: 0x1ul | 0x2ul | 0x4ul);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(
            "Critical hit!  Sneak Attack! Recklessness! "
            + "You stab Olthoi Soldier for 99 points of piercing damage!"
            + " Your target's Critical Protection augmentation allows them "
            + "to avoid your critical hit!",
            entry.Text);
    }

    [Fact]
    public void DamageDealt_OverpowerCondition_AddsNoText()
    {
        var (chat, combat, _) = Setup();
        combat.OnAttackerNotification(
            "Tusker", 0x04u, 8u, 0.05, critical: 0u, attackConditions: 0x8ul);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal("You graze Tusker for 8 points of bludgeoning damage!", entry.Text);
    }

    [Fact]
    public void DamageTaken_FormatsRetailDefenderTemplate_AsWarning()
    {
        var (chat, combat, _) = Setup();
        combat.OnVictimNotification(
            attackerName: "Mosswart Stalker", attackerGuid: 0xA1u,
            damageType: 0x10u, damage: 7u,
            hitQuadrant: 1u, critical: 0u, attackType: 0u,
            damagePercent: 0.12);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(ChatKind.Combat, entry.Kind);
        Assert.Equal(CombatLineKind.Warning, entry.CombatKind);
        // THIRD person verb, " your {part}", "point{s} of {type }damage!".
        Assert.Equal(
            "Mosswart Stalker scorches your chest for 7 points of fire damage!",
            entry.Text);
        Assert.Equal(0x15u, entry.LogTextType);
    }

    [Fact]
    public void DamageTaken_CriticalAndConditions_UseTheDefendersOwnLiterals()
    {
        var (chat, combat, _) = Setup();
        combat.OnVictimNotification(
            attackerName: "Olthoi Soldier", attackerGuid: 0xB2u,
            damageType: 0x02u /*PIERCE*/, damage: 99u,
            hitQuadrant: 0u /*head*/, critical: 1u, attackType: 0u,
            damagePercent: 0.20, attackConditions: 0x1ul | 0x2ul | 0x4ul);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(
            "Critical hit! Sneak Attack! Reckless! "
            + "Olthoi Soldier stabs your head for 99 points of piercing damage!"
            + " Your Critical Protection augmentation allows you "
            + "to avoid a critical hit!",
            entry.Text);
    }

    [Fact]
    public void DefenderLine_EmptyBodyPartText_UsesTheYouBranch()
    {
        Assert.Equal(
            "Drudge Slinker cuts you for 4 points of slashing damage!",
            CombatNotificationText.DefenderLineForBodyPartText(
                attackerName: "Drudge Slinker", damageType: 0x1u,
                percent: 0.20, damage: 4u, bodyPartText: "",
                critical: false, attackConditions: 0ul));
    }

    [Fact]
    public void MissedOutgoing_FormatsRetailEvasionAttackerTemplate_AsInfo()
    {
        var (chat, combat, _) = Setup();
        combat.OnEvasionAttackerNotification("Mosswart Sniper");

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(ChatKind.Combat, entry.Kind);
        Assert.Equal(CombatLineKind.Info, entry.CombatKind);
        Assert.Equal("Mosswart Sniper evaded your attack.", entry.Text);
        Assert.Equal(0x16u, entry.LogTextType);
    }

    [Fact]
    public void EvadedIncoming_FormatsRetailEvasionDefenderTemplate_AsInfo()
    {
        var (chat, combat, _) = Setup();
        combat.OnEvasionDefenderNotification("Drudge Slinker");

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(CombatLineKind.Info, entry.CombatKind);
        Assert.Equal("You evaded Drudge Slinker!", entry.Text);
        Assert.Equal(0x15u, entry.LogTextType);
    }


    [Fact]
    public void NoCombatLineEverContainsAPercentage()
    {
        var (chat, combat, _) = Setup();
        combat.OnAttackerNotification("A", 0x01u, 12u, 0.54, 1u, 0x7ul);
        combat.OnVictimNotification("B", 0u, 0x10u, 7u, 3u, 1u, 0u, 0.235, 0x7ul);
        combat.OnEvasionAttackerNotification("C");
        combat.OnEvasionDefenderNotification("D");

        string[] lines = chat.Snapshot().Select(x => x.Text).ToArray();
        Assert.Equal(4, lines.Length);
        foreach (string line in lines)
        {
            Assert.DoesNotContain("%", line);
            Assert.DoesNotContain("(", line);
        }
    }


    [Fact]
    public void AttackDone_NonZeroControlStatus_EmitsNothing()
    {
        var (chat, combat, _) = Setup();
        combat.OnAttackDone(attackSequence: 7, weenieError: 0x0036u);

        Assert.Empty(chat.Snapshot());
    }

    [Fact]
    public void AttackDone_ZeroError_EmitsNothing()
    {
        var (chat, combat, _) = Setup();
        combat.OnAttackDone(attackSequence: 7, weenieError: 0u);

        Assert.Empty(chat.Snapshot());
    }

    [Fact]
    public void KillLanded_SynthesizesNoClientSideText()
    {
        var (chat, combat, _) = Setup();
        combat.OnKillerNotification(victimName: "Phyntos Wasp", victimGuid: 0xCAFEu);

        Assert.Empty(chat.Snapshot());
    }

    [Fact]
    public void Dispose_UnsubscribesFromAllEvents()
    {
        var (chat, combat, t) = Setup();
        t.Dispose();

        combat.OnAttackerNotification("X", 0x01u, 1u, 1.0);
        combat.OnVictimNotification("X", 0u, 0x01u, 1u, 0u, 0u, 0u);
        combat.OnEvasionAttackerNotification("X");
        combat.OnEvasionDefenderNotification("X");
        combat.OnAttackDone(0u, 0xFFu);
        combat.OnKillerNotification("X", 0u);

        Assert.Empty(chat.Snapshot());
    }
}

using AcDream.Core.Combat;
using Xunit;

namespace AcDream.Core.Tests.Combat;

public sealed class CombatStateTests
{
    [Fact]
    public void OnUpdateHealth_CachesPercent_AndFiresEvent()
    {
        var state = new CombatState();
        uint seenGuid = 0;
        float seenPct = 0;
        state.HealthChanged += (g, p) => { seenGuid = g; seenPct = p; };

        state.OnUpdateHealth(0xBEEF, 0.33f);

        Assert.Equal(0xBEEFu, seenGuid);
        Assert.Equal(0.33f, seenPct, 4);
        Assert.Equal(0.33f, state.GetHealthPercent(0xBEEF), 4);
    }

    [Fact]
    public void GetHealthPercent_Unknown_ReturnsFullHealth()
    {
        var state = new CombatState();
        Assert.Equal(1f, state.GetHealthPercent(0xDEAD));
    }

    [Fact]
    public void CombatMode_UsesRetailAceBitValues()
    {
        Assert.Equal(1, (int)CombatMode.NonCombat);
        Assert.Equal(2, (int)CombatMode.Melee);
        Assert.Equal(4, (int)CombatMode.Missile);
        Assert.Equal(8, (int)CombatMode.Magic);
    }

    [Fact]
    public void AttackType_UsesNamedRetailBitValues()
    {
        Assert.Equal(0x0001u, (uint)AttackType.Punch);
        Assert.Equal(0x0002u, (uint)AttackType.Thrust);
        Assert.Equal(0x0004u, (uint)AttackType.Slash);
        Assert.Equal(0x0008u, (uint)AttackType.Kick);
        Assert.Equal(0x0010u, (uint)AttackType.OffhandPunch);
        Assert.Equal(0x79E0u, (uint)AttackType.MultiStrike);
    }

    [Fact]
    public void SetCombatMode_TracksCurrentMode_AndFiresEvent()
    {
        var state = new CombatState();
        CombatMode? seen = null;
        state.CombatModeChanged += mode => seen = mode;

        state.SetCombatMode(CombatMode.Missile);

        Assert.Equal(CombatMode.Missile, state.CurrentMode);
        Assert.Equal(CombatMode.Missile, seen);
    }

    [Fact]
    public void OnCombatCommenceAttack_FiresAttackCommenced()
    {
        var state = new CombatState();
        bool seen = false;
        state.AttackCommenced += () => seen = true;

        state.OnCombatCommenceAttack();

        Assert.True(seen);
    }

    [Fact]
    public void OnVictimNotification_FiresDamageTaken()
    {
        var state = new CombatState();
        CombatState.DamageIncoming seen = default;
        state.DamageTaken += d => seen = d;

        state.OnVictimNotification("Drudge", 0xAA, 1, 42, 3, 1, 8);

        Assert.Equal("Drudge", seen.AttackerName);
        Assert.Equal(42u, seen.Damage);
        Assert.True(seen.Critical);
    }

    [Fact]
    public void OnAttackerNotification_FiresDamageDealt()
    {
        var state = new CombatState();
        CombatState.DamageDealt seen = default;
        state.DamageDealtAccepted += d => seen = d;

        state.OnAttackerNotification("Drudge", 1, 30, 0.15f);

        Assert.Equal("Drudge", seen.DefenderName);
        Assert.Equal(30u, seen.Damage);
        Assert.Equal(0.15f, seen.DamagePercent, 4);
    }

    [Fact]
    public void OnEvasionNotification_FiresCorrectEvent()
    {
        var state = new CombatState();
        string? evaded = null, missed = null;
        state.EvadedIncoming += a => evaded = a;
        state.MissedOutgoing += d => missed = d;

        state.OnEvasionDefenderNotification("Rat");     // you evaded rat
        state.OnEvasionAttackerNotification("Tusker");  // tusker evaded you

        Assert.Equal("Rat", evaded);
        Assert.Equal("Tusker", missed);
    }

    [Fact]
    public void OnAttackDone_FiresAttackDone()
    {
        var state = new CombatState();
        uint seenSeq = 0, seenErr = 999;
        state.AttackDone += (s, e) => { seenSeq = s; seenErr = e; };

        state.OnAttackDone(5, 0);
        Assert.Equal(5u, seenSeq);
        Assert.Equal(0u, seenErr);
    }

    [Fact]
    public void Clear_ResetsCharacterSessionToPeaceMode()
    {
        var state = new CombatState();
        state.OnUpdateHealth(1, 0.5f);
        state.OnUpdateHealth(2, 0.8f);
        state.SetCombatMode(CombatMode.Melee);

        state.Clear();

        Assert.Equal(1f, state.GetHealthPercent(1));    // back to default
        Assert.Equal(0, state.TrackedTargetCount);
        Assert.Equal(CombatMode.NonCombat, state.CurrentMode);
    }

    [Fact]
    public void Clear_RetryRepublishesAndOneObserverCannotStarveAnother()
    {
        var state = new CombatState();
        state.SetCombatMode(CombatMode.Melee);
        bool fail = true;
        int delivered = 0;
        state.CombatModeChanged += _ =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("transient");
            }
        };
        state.CombatModeChanged += mode =>
        {
            Assert.Equal(CombatMode.NonCombat, mode);
            delivered++;
        };

        Assert.Throws<AggregateException>(state.Clear);
        Assert.Equal(1, delivered);
        state.Clear();
        Assert.Equal(2, delivered);
    }
}

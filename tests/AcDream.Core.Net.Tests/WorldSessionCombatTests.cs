using System.Net;
using AcDream.Core.Combat;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionCombatTests
{
    private static WorldSession NewSession()
    {
        var ep = new IPEndPoint(IPAddress.Loopback, 65000);
        return new WorldSession(ep);
    }

    [Fact]
    public void SendChangeCombatMode_UsesSequenceAndRetailModeValue()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendChangeCombatMode(CombatMode.Magic);

        Assert.NotNull(captured);
        Assert.Equal(CharacterActions.BuildChangeCombatMode(
            1,
            CharacterActions.CombatMode.Magic), captured);
    }

    [Fact]
    public void SendRaiseAttribute_UsesRetailBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendRaiseAttribute(5u, 110u);

        Assert.NotNull(captured);
        Assert.Equal(CharacterActions.BuildRaiseAttribute(1, 5u, 110u), captured);
    }

    [Fact]
    public void SendRaiseVital_UsesRetailBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendRaiseVital(1u, 90u);

        Assert.NotNull(captured);
        Assert.Equal(CharacterActions.BuildRaiseVital(1, 1u, 90u), captured);
    }

    [Fact]
    public void SendRaiseSkill_UsesRetailBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendRaiseSkill(34u, 111_000_000u);

        Assert.NotNull(captured);
        Assert.Equal(CharacterActions.BuildRaiseSkill(1, 34u, 111_000_000u), captured);
    }

    [Fact]
    public void SendTrainSkill_UsesRetailBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendTrainSkill(21u, 6u);

        Assert.NotNull(captured);
        Assert.Equal(CharacterActions.BuildTrainSkill(1, 21u, 6u), captured);
    }

    [Fact]
    public void SendMeleeAttack_UsesRetailMeleeBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendMeleeAttack(0x50000002u, AttackHeight.High, 0.75f);

        Assert.NotNull(captured);
        Assert.Equal(AttackTargetRequest.BuildMelee(
            1,
            0x50000002u,
            (uint)AttackHeight.High,
            0.75f), captured);
    }

    [Fact]
    public void SendMissileAttack_UsesRetailMissileBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendMissileAttack(0x50000003u, AttackHeight.Low, 0.5f);

        Assert.NotNull(captured);
        Assert.Equal(AttackTargetRequest.BuildMissile(
            1,
            0x50000003u,
            (uint)AttackHeight.Low,
            0.5f), captured);
    }

    [Fact]
    public void SendCancelAttack_UsesRetailCancelBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendCancelAttack();

        Assert.NotNull(captured);
        Assert.Equal(AttackTargetRequest.BuildCancel(1), captured);
    }

    [Fact]
    public void SendQueryHealth_UsesRetailQueryHealthBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendQueryHealth(0x50000007u);

        Assert.NotNull(captured);
        Assert.Equal(SocialActions.BuildQueryHealth(1, 0x50000007u), captured);
    }

    [Fact]
    public void SendQueryItemMana_UsesRetailQueryItemManaBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendQueryItemMana(0x50000A01u);

        Assert.NotNull(captured);
        Assert.Equal(SocialActions.BuildQueryItemMana(1, 0x50000A01u), captured);
    }

    [Fact]
    public void SendUseWithTarget_UsesRetailBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendUseWithTarget(0x50000A01u, 0x50000001u);

        Assert.NotNull(captured);
        Assert.Equal(
            InteractRequests.BuildUseWithTarget(1, 0x50000A01u, 0x50000001u),
            captured);
    }

    [Fact]
    public void SendAppraise_UsesRetailBuilder()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendAppraise(0x5000000Au);

        Assert.Equal(AppraiseRequest.Build(1u, 0x5000000Au), captured);
    }
}

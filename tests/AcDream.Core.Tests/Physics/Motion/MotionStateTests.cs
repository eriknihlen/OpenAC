using System.Linq;
using AcDream.Core.Physics.Motion;

namespace AcDream.Core.Tests.Physics.Motion;

public class MotionStateTests
{
    [Fact]
    public void Defaults_MatchRetailCtor()
    {
        var ms = new MotionState();
        Assert.Equal(0u, ms.Style);
        Assert.Equal(0u, ms.Substate);
        Assert.Equal(1f, ms.SubstateMod);
        Assert.Empty(ms.Modifiers);
        Assert.Empty(ms.Actions);
    }

    [Fact]
    public void Modifiers_ArePushFrontStack()
    {
        var ms = new MotionState();
        ms.AddModifierNoCheck(0x0Du, 1.0f);
        ms.AddModifierNoCheck(0x0Fu, 1.5f);

        Assert.Equal(new uint[] { 0x0Fu, 0x0Du }, ms.Modifiers.Select(m => m.Motion).ToArray());
    }

    [Fact]
    public void Actions_AreTailAppendFifo()
    {
        var ms = new MotionState();
        ms.AddAction(0x62u, 1.0f);
        ms.AddAction(0x63u, 1.25f);

        Assert.Equal(new uint[] { 0x62u, 0x63u }, ms.Actions.Select(a => a.Motion).ToArray());
    }

    [Fact]
    public void AddModifier_RejectsDuplicate()
    {
        var ms = new MotionState();
        Assert.True(ms.AddModifier(0x0Du, 1.0f));
        Assert.False(ms.AddModifier(0x0Du, 2.0f));
        Assert.Single(ms.Modifiers);
        Assert.Equal(1.0f, ms.Modifiers.First().SpeedMod); // original untouched
    }

    [Fact]
    public void AddModifier_RefusesCurrentSubstate()
    {
        var ms = new MotionState { Substate = 0x45000005u };
        Assert.False(ms.AddModifier(0x45000005u, 1.0f));
        Assert.Empty(ms.Modifiers);
    }

    [Fact]
    public void AddModifierNoCheck_SkipsBothGuards()
    {
        var ms = new MotionState { Substate = 0x45000005u };
        ms.AddModifierNoCheck(0x45000005u, 1.0f);
        ms.AddModifierNoCheck(0x45000005u, 2.0f); // duplicate allowed too
        Assert.Equal(2, ms.Modifiers.Count());
    }

    [Fact]
    public void RemoveModifier_ByNodeIdentity()
    {
        var ms = new MotionState();
        ms.AddModifierNoCheck(0x0Du, 1.0f);
        ms.AddModifierNoCheck(0x0Fu, 1.5f);
        var target = ms.Modifiers.First(m => m.Motion == 0x0Du);

        ms.RemoveModifier(target);

        Assert.Single(ms.Modifiers);
        Assert.Equal(0x0Fu, ms.Modifiers.First().Motion);
    }

    [Fact]
    public void RemoveActionHead_PopsFifo_ReturnsMotion_ZeroWhenEmpty()
    {
        var ms = new MotionState();
        ms.AddAction(0x62u, 1f);
        ms.AddAction(0x63u, 1f);

        Assert.Equal(0x62u, ms.RemoveActionHead());
        Assert.Equal(0x63u, ms.RemoveActionHead());
        Assert.Equal(0u, ms.RemoveActionHead());
        Assert.Empty(ms.Actions);

        ms.AddAction(0x64u, 1f);
        Assert.Equal(new uint[] { 0x64u }, ms.Actions.Select(a => a.Motion).ToArray());
    }

    [Fact]
    public void ClearModifiers_And_ClearActions_AreIndependentChains()
    {
        var ms = new MotionState();
        ms.AddModifierNoCheck(0x0Du, 1f);
        ms.AddAction(0x62u, 1f);

        ms.ClearModifiers();
        Assert.Empty(ms.Modifiers);
        Assert.Single(ms.Actions);

        ms.ClearActions();
        Assert.Empty(ms.Actions);
    }

    [Fact]
    public void DeepCopy_ClonesChains_NoSharedState()
    {
        var ms = new MotionState { Style = 0x8000003Du, Substate = 0x44000007u, SubstateMod = 2.85f };
        ms.AddModifierNoCheck(0x0Du, 1.5f);
        ms.AddAction(0x62u, 1f);

        var snap = new MotionState(ms);

        ms.ClearModifiers();
        ms.RemoveActionHead();
        ms.Substate = 0x41000003u;

        Assert.Equal(0x8000003Du, snap.Style);
        Assert.Equal(0x44000007u, snap.Substate);
        Assert.Equal(2.85f, snap.SubstateMod);
        Assert.Single(snap.Modifiers);
        Assert.Equal(0x0Du, snap.Modifiers.First().Motion);
        Assert.Single(snap.Actions);
    }
}

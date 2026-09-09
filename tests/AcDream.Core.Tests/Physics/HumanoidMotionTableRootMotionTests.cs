using AcDream.Content.Vfx;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public sealed class HumanoidMotionTableRootMotionTests
{
    private const uint HumanoidMotionTable = 0x09000001u;
    private const uint HumanoidSetup = 0x02000001u;
    private const uint NonCombatStyle = 0x8000003Du;
    private const uint WalkForward = 0x40000005u;
    private const uint RunForward = 0x40000007u;
    private const uint Ready = 0x41000003u;
    private const uint InterpretedWalkForward = 0x45000005u;
    private const uint TurnRight = 0x6500000Du;

    [Theory]
    [InlineData(WalkForward)]
    [InlineData(RunForward)]
    public void RetailLocomotionCycle_ExposesItsLiteralMotionDataVelocity(uint motion)
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        MotionTable table = Assert.IsType<MotionTable>(dats.Get<MotionTable>(HumanoidMotionTable));
        int key = unchecked((int)((NonCombatStyle << 16) | (motion & 0x00FF_FFFFu)));
        MotionData cycle = Assert.IsType<MotionData>(table.Cycles[key]);

        Assert.Equal(System.Numerics.Vector3.Zero, cycle.Velocity);
    }

    [Fact]
    public void RetailWalkSequence_ProducesAuthoredRootDisplacement()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var boundedDats = new DatCollectionAdapter(dats);
        Setup setup = Assert.IsType<Setup>(dats.Get<Setup>(HumanoidSetup));
        MotionTable table = Assert.IsType<MotionTable>(dats.Get<MotionTable>(HumanoidMotionTable));
        var sequencer = new AnimationSequencer(
            setup,
            table,
            new RetailAnimationLoader(boundedDats));

        sequencer.SetCycle(NonCombatStyle, Ready);
        sequencer.SetCycle(NonCombatStyle, InterpretedWalkForward);

        float distance = 0f;
        for (int i = 0; i < 120; i++)
        {
            var root = new Frame
            {
                Origin = System.Numerics.Vector3.Zero,
                Orientation = System.Numerics.Quaternion.Identity,
            };
            sequencer.Advance(1f / 60f, root);
            distance += root.Origin.Length();
        }

        Assert.True(
            distance > 1f,
            $"Installed retail WalkForward should author root travel; got {distance:F3} m over 2 s.");
    }

    [Fact]
    public void RetailTurnRightModifier_ExposesLiteralSequenceOmega()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        MotionTable table = Assert.IsType<MotionTable>(dats.Get<MotionTable>(HumanoidMotionTable));
        int styledKey = unchecked((int)((NonCombatStyle << 16) | (TurnRight & 0x00FF_FFFFu)));
        int unstyledKey = unchecked((int)(TurnRight & 0x00FF_FFFFu));
        bool found = table.Modifiers.TryGetValue(styledKey, out MotionData? modifier)
            || table.Modifiers.TryGetValue(unstyledKey, out modifier);

        Assert.True(
            found,
            $"Retail Humanoid TurnRight modifier was absent at styled key 0x{styledKey:X8} "
            + $"and fallback key 0x{unstyledKey:X8}.");
        Assert.NotNull(modifier);

        Assert.True(
            modifier!.Flags.HasFlag(DatReaderWriter.Enums.MotionDataFlags.HasOmega),
            $"Retail Humanoid TurnRight must mark its literal omega present; "
            + $"flags={modifier.Flags}.");
        Assert.Equal(System.Numerics.Vector3.Zero, modifier.Omega with { Z = 0f });
        Assert.Equal(-1.5f, modifier.Omega.Z, 5);
    }
}

using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

internal static class ProjectileVfxPacketFixtures
{
    internal static readonly byte[] DirectF754 =
    [
        0x54, 0xF7, 0x00, 0x00,
        0x42, 0x00, 0x00, 0x50,
        0x99, 0x00, 0x00, 0x33,
    ];

    internal static readonly byte[] TypedF755UnknownNaN =
    [
        0x55, 0xF7, 0x00, 0x00,
        0x42, 0x00, 0x00, 0x50,
        0xA5, 0xA5, 0xA5, 0xA5,
        0x34, 0x12, 0xC0, 0x7F,
    ];

    internal static readonly byte[] GoldenCreateObjectProjectilePhysicsFields = BuildGoldenCreateObject();
    internal static readonly byte[] CreateObjectMovementPresentEmpty = BuildBranchFixture(
        CreateObject.PhysicsDescriptionFlag.Movement, 0x50000051u);
    internal static readonly byte[] CreateObjectAnimationFramePresentZero = BuildBranchFixture(
        CreateObject.PhysicsDescriptionFlag.AnimationFrame, 0x50000052u);
    internal static readonly byte[] CreateObjectPeTablePresentZero = BuildBranchFixture(
        CreateObject.PhysicsDescriptionFlag.PeTable, 0x50000053u);
    internal static readonly byte[] CreateObjectPeTableAbsent = BuildBranchFixture(
        CreateObject.PhysicsDescriptionFlag.None, 0x50000054u);

    private static byte[] BuildGoldenCreateObject()
    {
        var bytes = new List<byte>();
        uint flags =
            (uint)(CreateObject.PhysicsDescriptionFlag.CSetup
                | CreateObject.PhysicsDescriptionFlag.MTable
                | CreateObject.PhysicsDescriptionFlag.Velocity
                | CreateObject.PhysicsDescriptionFlag.Acceleration
                | CreateObject.PhysicsDescriptionFlag.Omega
                | CreateObject.PhysicsDescriptionFlag.Parent
                | CreateObject.PhysicsDescriptionFlag.Children
                | CreateObject.PhysicsDescriptionFlag.ObjScale
                | CreateObject.PhysicsDescriptionFlag.Friction
                | CreateObject.PhysicsDescriptionFlag.Elasticity
                | CreateObject.PhysicsDescriptionFlag.STable
                | CreateObject.PhysicsDescriptionFlag.PeTable
                | CreateObject.PhysicsDescriptionFlag.DefaultScript
                | CreateObject.PhysicsDescriptionFlag.DefaultScriptIntensity
                | CreateObject.PhysicsDescriptionFlag.Position
                | CreateObject.PhysicsDescriptionFlag.Translucency);

        WriteU32(CreateObject.Opcode);
        WriteU32(0x50000042u);
        bytes.AddRange([0x11, 0, 0, 0]); // empty ModelData
        WriteU32(flags);
        WriteU32(0x00000748u); // Missile | ReportCollisions | AlignPath | PathClipped | Gravity

        WriteU32(0x7F010123u);
        WriteF32(12.5f); WriteF32(25.0f); WriteF32(3.75f);
        WriteF32(1.0f); WriteF32(0.0f); WriteF32(0.0f); WriteF32(0.0f); // WXYZ
        WriteU32(0x09000001u);
        WriteU32(0x20000014u);
        WriteU32(0x34000005u);
        WriteU32(0x0200040Du);
        WriteU32(0x50000010u); WriteU32(3u);
        WriteU32(1u); WriteU32(0x50000011u); WriteU32(4u);
        WriteF32(1.25f);
        WriteF32(0.125f);
        WriteF32(0.05f);
        WriteF32(0.25f);
        WriteF32(10f); WriteF32(20f); WriteF32(30f);
        WriteF32(1f); WriteF32(2f); WriteF32(3f);
        WriteF32(0.1f); WriteF32(0.2f); WriteF32(0.3f);
        WriteU32(0xA5A5A5A5u);
        WriteU32(0x7FC01234u); // NaN intensity, preserving payload bits
        for (ushort i = 0; i < 9; i++) WriteU16((ushort)(0xFFF8u + i));
        Align4();

        WriteU32(0u); // no optional WeenieHeader fields
        WriteString16L("Golden Projectile");
        WritePackedDword(7277u);
        WritePackedDword(0u);
        WriteU32(0x00000100u); // ammunition ItemType
        WriteU32(0u);
        Align4();
        return bytes.ToArray();

        void WriteU16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            bytes.AddRange(buffer.ToArray());
        }

        void WriteU32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            bytes.AddRange(buffer.ToArray());
        }

        void WriteF32(float value) => WriteU32(BitConverter.SingleToUInt32Bits(value));

        void WritePackedDword(uint value)
        {
            if (value <= 0x7FFFu) WriteU16((ushort)value);
            else
            {
                WriteU16((ushort)(((value >> 16) & 0x7FFFu) | 0x8000u));
                WriteU16((ushort)value);
            }
        }

        void WriteString16L(string value)
        {
            byte[] encoded = System.Text.Encoding.GetEncoding(1252).GetBytes(value);
            WriteU16(checked((ushort)encoded.Length));
            bytes.AddRange(encoded);
            Align4();
        }

        void Align4()
        {
            while ((bytes.Count & 3) != 0) bytes.Add(0);
        }
    }

    private static byte[] BuildBranchFixture(CreateObject.PhysicsDescriptionFlag flags, uint guid)
    {
        var bytes = new List<byte>();
        WriteU32(CreateObject.Opcode);
        WriteU32(guid);
        bytes.AddRange([0x11, 0, 0, 0]); // empty ModelData
        WriteU32((uint)flags);
        WriteU32(0); // explicit zero PhysicsState

        if ((flags & CreateObject.PhysicsDescriptionFlag.Movement) != 0)
            WriteU32(0); // present Movement with an empty byte blob; no autonomous dword
        else if ((flags & CreateObject.PhysicsDescriptionFlag.AnimationFrame) != 0)
            WriteU32(0); // present placement/frame with value zero

        if ((flags & CreateObject.PhysicsDescriptionFlag.PeTable) != 0)
            WriteU32(0); // present PhysicsScriptTable DID with value zero

        for (ushort i = 0; i < 9; i++) WriteU16((ushort)(0x1100 + i));
        Align4();
        WriteU32(0); // no optional WeenieHeader fields
        WriteString16L("Physics Branch Fixture");
        WritePackedDword(7277);
        WritePackedDword(0);
        WriteU32(0x00000100u);
        WriteU32(0);
        Align4();
        return bytes.ToArray();

        void WriteU16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            bytes.AddRange(buffer.ToArray());
        }

        void WriteU32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            bytes.AddRange(buffer.ToArray());
        }

        void WritePackedDword(uint value)
        {
            if (value <= 0x7FFFu) WriteU16((ushort)value);
            else
            {
                WriteU16((ushort)(((value >> 16) & 0x7FFFu) | 0x8000u));
                WriteU16((ushort)value);
            }
        }

        void WriteString16L(string value)
        {
            byte[] encoded = System.Text.Encoding.GetEncoding(1252).GetBytes(value);
            WriteU16(checked((ushort)encoded.Length));
            bytes.AddRange(encoded);
            Align4();
        }

        void Align4()
        {
            while ((bytes.Count & 3) != 0) bytes.Add(0);
        }
    }
}

public sealed class ProjectileVfxPacketFixtureTests
{
    [Fact]
    public void DirectF754_PreservesExactTwelveByteWireShape()
    {
        ReadOnlySpan<byte> packet = ProjectileVfxPacketFixtures.DirectF754;

        Assert.Equal(12, packet.Length);
        Assert.Equal(0xF754u, BinaryPrimitives.ReadUInt32LittleEndian(packet));
        Assert.Equal(0x50000042u, BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]));
        Assert.Equal(0x33000099u, BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]));

        var parsed = PlayPhysicsScript.TryParse(packet);
        Assert.Equal(new PlayPhysicsScript(0x50000042u, 0x33000099u), parsed);
        Assert.Null(PlayPhysicsScript.TryParse(packet[..^1]));
        Assert.Null(PlayPhysicsScript.TryParse([.. packet.ToArray(), 0]));
    }

    [Fact]
    public void TypedF755_PreservesUnknownTypeAndNaNPayloadBits()
    {
        ReadOnlySpan<byte> packet = ProjectileVfxPacketFixtures.TypedF755UnknownNaN;

        Assert.Equal(16, packet.Length);
        Assert.Equal(0xF755u, BinaryPrimitives.ReadUInt32LittleEndian(packet));
        Assert.Equal(0x50000042u, BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]));
        Assert.Equal(0xA5A5A5A5u, BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]));
        Assert.Equal(0x7FC01234u, BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]));
        Assert.True(float.IsNaN(BinaryPrimitives.ReadSingleLittleEndian(packet[12..])));

        var parsed = PlayPhysicsScriptType.TryParse(packet);
        Assert.NotNull(parsed);
        Assert.Equal(0x50000042u, parsed.Value.Guid);
        Assert.Equal(0xA5A5A5A5u, parsed.Value.RawScriptType);
        Assert.Equal(0x7FC01234u, BitConverter.SingleToUInt32Bits(parsed.Value.Intensity));
        Assert.Null(PlayPhysicsScriptType.TryParse(packet[..^1]));
        Assert.Null(PlayPhysicsScriptType.TryParse([.. packet.ToArray(), 0]));
    }

    [Theory]
    [InlineData(0x7F800000u)]
    [InlineData(0xFF800000u)]
    public void TypedF755_PreservesInfiniteIntensityBits(uint intensityBits)
    {
        byte[] packet = ProjectileVfxPacketFixtures.TypedF755UnknownNaN.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), intensityBits);

        var parsed = PlayPhysicsScriptType.TryParse(packet);

        Assert.NotNull(parsed);
        Assert.Equal(intensityBits, BitConverter.SingleToUInt32Bits(parsed.Value.Intensity));
    }

    [Fact]
    public void GoldenCreateObject_ExercisesBroadProjectilePhysicsFieldsWithoutLosingAlignment()
    {
        byte[] packet = ProjectileVfxPacketFixtures.GoldenCreateObjectProjectilePhysicsFields;

        var parsed = CreateObject.TryParse(packet);

        Assert.NotNull(parsed);
        Assert.Equal(0x50000042u, parsed.Value.Guid);
        Assert.Equal("Golden Projectile", parsed.Value.Name);
        Assert.Equal(0x0200040Du, parsed.Value.SetupTableId);
        Assert.Equal(0x09000001u, parsed.Value.MotionTableId);
        Assert.Equal(1.25f, parsed.Value.ObjScale);
        Assert.Equal(0.125f, parsed.Value.Friction);
        Assert.Equal(0.05f, parsed.Value.Elasticity);
        Assert.Equal(0x50000010u, parsed.Value.ParentGuid);
        Assert.Equal((ushort)0xFFF8, parsed.Value.PositionSequence);
        Assert.Equal((ushort)0xFFF9, parsed.Value.MovementSequence);
        Assert.Equal((ushort)0x0000, parsed.Value.InstanceSequence);

        var physics = Assert.IsType<PhysicsSpawnData>(parsed.Value.Physics);
        Assert.Equal(0x00000748u, physics.RawState);
        Assert.True(physics.State.HasFlag(AcDream.Core.Physics.PhysicsStateFlags.Missile));
        Assert.Equal(0x20000014u, physics.SoundTableId);
        Assert.Equal(0x34000005u, physics.PhysicsScriptTableId);
        Assert.Equal(new PhysicsAttachment(0x50000010u, 3u), physics.Parent);
        Assert.Equal([new PhysicsAttachment(0x50000011u, 4u)],
            physics.Children!.Value.ToArray());
        Assert.Equal(0.25f, physics.Translucency);
        Assert.Equal(new System.Numerics.Vector3(10f, 20f, 30f), physics.Velocity);
        Assert.Equal(new System.Numerics.Vector3(1f, 2f, 3f), physics.Acceleration);
        Assert.Equal(new System.Numerics.Vector3(0.1f, 0.2f, 0.3f), physics.AngularVelocity);
        Assert.Equal(0xA5A5A5A5u, physics.DefaultScriptType);
        Assert.Equal(0x7FC01234u,
            BitConverter.SingleToUInt32Bits(physics.DefaultScriptIntensity!.Value));
        Assert.Equal(new PhysicsTimestamps(
            0xFFF8, 0xFFF9, 0xFFFA, 0xFFFB, 0xFFFC,
            0xFFFD, 0xFFFE, 0xFFFF, 0x0000), physics.Timestamps);
    }

    [Fact]
    public void MutuallyExclusiveMovementAndAnimationFrameFixturesRemainAligned()
    {
        var movement = CreateObject.TryParse(ProjectileVfxPacketFixtures.CreateObjectMovementPresentEmpty);
        var animation = CreateObject.TryParse(ProjectileVfxPacketFixtures.CreateObjectAnimationFramePresentZero);

        Assert.NotNull(movement);
        Assert.NotNull(animation);
        Assert.Equal(0x50000051u, movement.Value.Guid);
        Assert.Equal(0x50000052u, animation.Value.Guid);
        Assert.Null(movement.Value.PlacementId);
        Assert.Equal(0u, animation.Value.PlacementId);
        Assert.Equal((ushort)0x1108, movement.Value.InstanceSequence);
        Assert.Equal((ushort)0x1108, animation.Value.InstanceSequence);
        Assert.NotNull(movement.Value.Physics!.Value.Movement);
        Assert.True(movement.Value.Physics.Value.Movement!.Value.RawData.IsEmpty);
        Assert.Null(movement.Value.Physics.Value.Movement!.Value.IsAutonomous);
        Assert.Equal(0u, animation.Value.Physics!.Value.AnimationFrame);
    }

    [Fact]
    public void PeTableFixturesPinAbsentVersusPresentZeroWireShapes()
    {
        ReadOnlySpan<byte> present = ProjectileVfxPacketFixtures.CreateObjectPeTablePresentZero;
        ReadOnlySpan<byte> absent = ProjectileVfxPacketFixtures.CreateObjectPeTableAbsent;

        Assert.Equal((uint)CreateObject.PhysicsDescriptionFlag.PeTable,
            BinaryPrimitives.ReadUInt32LittleEndian(present[12..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(present[20..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(absent[12..]));
        Assert.Equal(present.Length, absent.Length + sizeof(uint));
        var parsedPresent = CreateObject.TryParse(present);
        var parsedAbsent = CreateObject.TryParse(absent);
        Assert.NotNull(parsedPresent);
        Assert.NotNull(parsedAbsent);
        Assert.Equal(0u, parsedPresent.Value.Physics!.Value.PhysicsScriptTableId);
        Assert.Null(parsedAbsent.Value.Physics!.Value.PhysicsScriptTableId);
    }

    [Fact]
    public void GoldenCreateObject_RejectsEveryTruncationInsidePhysicsDesc()
    {
        byte[] packet = ProjectileVfxPacketFixtures.GoldenCreateObjectProjectilePhysicsFields;

        for (int length = 20; length < 168; length++)
            Assert.Null(CreateObject.TryParse(packet.AsSpan(0, length)));

        Assert.NotNull(CreateObject.TryParse(packet.AsSpan(0, 168)));
    }
}

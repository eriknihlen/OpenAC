using System.Buffers.Binary;

namespace AcDream.Content.Tests.Vfx;

internal static class ProjectileVfxDatFixtures
{
    internal static readonly byte[] PhysicsScriptCreateBlockingThenAnimationDone = BuildPhysicsScript();
    internal static readonly byte[] AnimationCreateBlockingThenAnimationDone = BuildAnimation();
    internal static readonly byte[] OrdinaryPhysicsScript = BuildOrdinaryPhysicsScript();
    internal static readonly byte[] OrdinaryAnimation = BuildOrdinaryAnimation();

    private static byte[] BuildPhysicsScript()
    {
        var bytes = new byte[80];
        int pos = 0;

        WriteU32(0x3300F001u); // synthetic PhysicsScript DID
        WriteU32(2);           // two PhysicsScriptData entries

        WriteF64(0.0);
        WriteU32(0x1Au);
        WriteU32(0);           // AnimationHookDir.Both
        WriteU32(0x3200F001u); // emitter DID
        WriteU32(0xFFFFFFFFu); // root attachment, distinct from part zero
        WriteF32(1.0f); WriteF32(2.0f); WriteF32(3.0f); // offset origin
        WriteF32(0.5f); WriteF32(0.5f); WriteF32(-0.5f); WriteF32(-0.5f); // WXYZ quaternion
        WriteU32(7);           // nonzero logical emitter ID

        WriteF64(1.25);
        WriteU32(0x04u);
        WriteU32(1);           // AnimationHookDir.Forward

        if (pos != bytes.Length) throw new InvalidOperationException($"Fixture length mismatch: {pos}");
        return bytes;

        void WriteU32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pos), value);
            pos += 4;
        }

        void WriteF32(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(pos), value);
            pos += 4;
        }

        void WriteF64(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(pos), value);
            pos += 8;
        }
    }

    private static byte[] BuildAnimation()
    {
        // Animation header: DID, flags, NumParts=0, NumFrames=1. The sole
        // AnimationFrame then has hookCount=2 and the same two hook records as
        // the PhysicsScript fixture. No position or part frames are present.
        var bytes = new byte[76];
        int pos = 0;

        WriteU32(0x0300F001u); // synthetic Animation DID
        WriteU32(0);           // no PosFrames
        WriteU32(0);           // NumParts
        WriteU32(1);           // NumFrames
        WriteU32(2);

        WriteU32(0x1Au);
        WriteU32(0);           // AnimationHookDir.Both
        WriteU32(0x3200F002u); // emitter DID
        WriteU32(0);           // part zero, distinct from the PES root fixture
        WriteF32(-1.0f); WriteF32(4.0f); WriteF32(0.25f); // offset origin
        WriteF32(1.0f); WriteF32(0.0f); WriteF32(0.0f); WriteF32(0.0f); // WXYZ identity
        WriteU32(9);           // nonzero logical emitter ID

        WriteU32(0x04u);
        WriteU32(0xFFFFFFFFu); // AnimationHookDir.Backward

        if (pos != bytes.Length) throw new InvalidOperationException($"Fixture length mismatch: {pos}");
        return bytes;

        void WriteU32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pos), value);
            pos += 4;
        }

        void WriteF32(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(pos), value);
            pos += 4;
        }
    }

    private static byte[] BuildOrdinaryPhysicsScript()
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x3300F002u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1u);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(8), 2.5);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x04u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1u);
        return bytes;
    }

    private static byte[] BuildOrdinaryAnimation()
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x0300F002u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 0x04u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 1u);
        return bytes;
    }
}

public sealed class ProjectileVfxDatFixtureTests
{
    [Fact]
    public void BlockingThenOrdinaryHook_PinsInheritedPayloadAndNextCursor()
    {
        ReadOnlySpan<byte> bytes = ProjectileVfxDatFixtures.PhysicsScriptCreateBlockingThenAnimationDone;

        Assert.Equal(80, bytes.Length);
        Assert.Equal(0x3300F001u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]));
        Assert.Equal(0x1Au, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
        Assert.Equal(0x3200F001u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..]));
        Assert.Equal(1.25, BinaryPrimitives.ReadDoubleLittleEndian(bytes[64..]));
        Assert.Equal(0x04u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[72..]));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[76..]));
    }

    [Fact]
    public void AnimationContainer_BlockingThenOrdinaryHookPinsNextCursor()
    {
        ReadOnlySpan<byte> bytes = ProjectileVfxDatFixtures.AnimationCreateBlockingThenAnimationDone;

        Assert.Equal(76, bytes.Length);
        Assert.Equal(0x0300F001u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
        Assert.Equal(0x1Au, BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]));
        Assert.Equal(0x3200F002u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[64..]));
        Assert.Equal(0x04u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[68..]));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(bytes[72..]));
    }
}

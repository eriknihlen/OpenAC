using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class UpdatePositionTests
{
    [Fact]
    public void RejectsWrongOpcode()
    {
        var body = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xCAFEBABE);
        Assert.Null(UpdatePosition.TryParse(body));
    }

    [Fact]
    public void RejectsTruncated()
    {
        Assert.Null(UpdatePosition.TryParse(Array.Empty<byte>()));
        Assert.Null(UpdatePosition.TryParse(new byte[3]));
    }

    [Fact]
    public void ParsesAllFourRotationComponentsPresent()
    {
        // Full rotation, no velocity, no placement.
        var body = BuildBody(
            guid: 0x12345678u,
            flags: 0,
            cellId: 0xA9B4001Au,
            px: 10f, py: 20f, pz: 30f,
            rw: 1f, rx: 0f, ry: 0f, rz: 0f);

        var result = UpdatePosition.TryParse(body);
        Assert.NotNull(result);
        Assert.Equal(0x12345678u, result!.Value.Guid);
        Assert.Equal(0xA9B4001Au, result.Value.Position.LandblockId);
        Assert.Equal(10f, result.Value.Position.PositionX);
        Assert.Equal(20f, result.Value.Position.PositionY);
        Assert.Equal(30f, result.Value.Position.PositionZ);
        Assert.Equal(1f, result.Value.Position.RotationW);
        Assert.Null(result.Value.Velocity);
        Assert.Null(result.Value.PlacementId);
        Assert.Equal((ushort)0x1001, result.Value.InstanceSequence);
        Assert.Equal((ushort)0x2002, result.Value.PositionSequence);
        Assert.Equal((ushort)0x3003, result.Value.TeleportSequence);
        Assert.Equal((ushort)0x4004, result.Value.ForcePositionSequence);
    }

    [Fact]
    public void ParsesWithMissingRotationComponents()
    {
        uint flags = 0x10 | 0x20 | 0x40;
        var body = BuildBodyPartial(
            guid: 0xDEADBEEF,
            flags: flags,
            cellId: 0x01BB0001,
            px: 1f, py: 2f, pz: 3f,
            rotationComponents: new float[] { 0.707f });

        var result = UpdatePosition.TryParse(body);
        Assert.NotNull(result);
        Assert.Equal(0.707f, result!.Value.Position.RotationW, precision: 4);
        Assert.Equal(0f, result.Value.Position.RotationX);
        Assert.Equal(0f, result.Value.Position.RotationY);
        Assert.Equal(0f, result.Value.Position.RotationZ);
    }

    [Fact]
    public void ParsesHasVelocityFlag()
    {
        uint flags = 0x01;
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write(UpdatePosition.Opcode);
        bw.Write(0xAABBCCDDu);
        bw.Write(flags);
        bw.Write(0x01020003u);
        bw.Write(5f); bw.Write(6f); bw.Write(7f);
        bw.Write(1f); bw.Write(0f); bw.Write(0f); bw.Write(0f);
        bw.Write(100f); bw.Write(200f); bw.Write(300f);  // velocity
        WriteSequences(bw);

        var result = UpdatePosition.TryParse(ms.ToArray());
        Assert.NotNull(result);
        Assert.NotNull(result!.Value.Velocity);
        Assert.Equal(100f, result.Value.Velocity!.Value.X);
        Assert.Equal(200f, result.Value.Velocity.Value.Y);
        Assert.Equal(300f, result.Value.Velocity.Value.Z);
    }

    [Fact]
    public void ParsesHasPlacementIdFlag()
    {
        // HasPlacementID(0x02) — placement id u32 follows rotation (no velocity).
        uint flags = 0x02;
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write(UpdatePosition.Opcode);
        bw.Write(0x11111111u);
        bw.Write(flags);
        bw.Write(0x00000001u);
        bw.Write(0f); bw.Write(0f); bw.Write(0f);
        bw.Write(1f); bw.Write(0f); bw.Write(0f); bw.Write(0f);
        bw.Write((uint)0x65);  // Placement.Resting
        WriteSequences(bw);

        var result = UpdatePosition.TryParse(ms.ToArray());
        Assert.NotNull(result);
        Assert.Equal((uint)0x65, result!.Value.PlacementId);
    }

    [Fact]
    public void ParsesBothVelocityAndPlacement()
    {
        uint flags = 0x01 | 0x02;
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write(UpdatePosition.Opcode);
        bw.Write(0x22222222u);
        bw.Write(flags);
        bw.Write(0x04050607u);
        bw.Write(10f); bw.Write(20f); bw.Write(30f);
        bw.Write(1f); bw.Write(0f); bw.Write(0f); bw.Write(0f);
        bw.Write(1f); bw.Write(2f); bw.Write(3f);
        bw.Write((uint)42u);
        WriteSequences(bw);

        var result = UpdatePosition.TryParse(ms.ToArray());
        Assert.NotNull(result);
        Assert.NotNull(result!.Value.Velocity);
        Assert.Equal(42u, result.Value.PlacementId);
    }

    // ---- helpers ----

    private static byte[] BuildBody(
        uint guid, uint flags, uint cellId,
        float px, float py, float pz,
        float rw, float rx, float ry, float rz)
    {
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write(UpdatePosition.Opcode);
        bw.Write(guid);
        bw.Write(flags);
        bw.Write(cellId);
        bw.Write(px); bw.Write(py); bw.Write(pz);
        bw.Write(rw); bw.Write(rx); bw.Write(ry); bw.Write(rz);
        WriteSequences(bw);
        return ms.ToArray();
    }

    private static byte[] BuildBodyPartial(
        uint guid, uint flags, uint cellId,
        float px, float py, float pz,
        float[] rotationComponents)
    {
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write(UpdatePosition.Opcode);
        bw.Write(guid);
        bw.Write(flags);
        bw.Write(cellId);
        bw.Write(px); bw.Write(py); bw.Write(pz);
        foreach (var c in rotationComponents) bw.Write(c);
        WriteSequences(bw);
        return ms.ToArray();
    }

    private static void WriteSequences(System.IO.BinaryWriter writer)
    {
        writer.Write((ushort)0x1001); // instance
        writer.Write((ushort)0x2002); // position
        writer.Write((ushort)0x3003); // teleport
        writer.Write((ushort)0x4004); // force-position
    }
}

using System.Buffers.Binary;
using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class PositionPackTests
{
    [Fact]
    public void MoveToState_PositionBlock_OrderIsCellIdThenOriginThenQuaternion()
    {
        var body = MoveToState.Build(
            gameActionSequence: 1,
            rawMotionState: RawMotionState.Default,
            cellId: 0xA9B40001u,
            position: new Vector3(1.5f, 2.5f, 3.5f),
            rotation: new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
            instanceSequence: 0,
            serverControlSequence: 0,
            teleportSequence: 0,
            forcePositionSequence: 0);

        // No motion state -> flags(4) only before the Position block at offset 16.
        int off = 12 + 4;
        uint cellId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(off));
        float x = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(off + 4));
        float y = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(off + 8));
        float z = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(off + 12));
        float qw = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(off + 16));
        float qx = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(off + 20));
        float qy = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(off + 24));
        float qz = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(off + 28));

        Assert.Equal(0xA9B40001u, cellId);
        Assert.Equal(1.5f, x);
        Assert.Equal(2.5f, y);
        Assert.Equal(3.5f, z);
        Assert.Equal(0.9f, qw);
        Assert.Equal(0.1f, qx);
        Assert.Equal(0.2f, qy);
        Assert.Equal(0.3f, qz);
    }
}

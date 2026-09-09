using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ShadowObjectRegistryCellRenderRevisionTests
{
    private const uint Block = 0xA9B40000;
    private const uint Cell = Block | 1;

    private static ShadowShape[] Parts(uint mesh = 0x01000001) =>
        [ShadowShape.Bsp(mesh, Vector3.Zero, Quaternion.Identity, 1f,
            ShadowPartGeometry.Create(new FlatCollisionSphere(Vector3.Zero, 1f), null))];

    private static void Register(ShadowObjectRegistry registry, uint owner, float x = 12f)
    {
        var parts = Parts();
        registry.RegisterMultiPart(owner, new Vector3(x, 12f, 50f), Quaternion.Identity,
            parts, 0, EntityCollisionFlags.None, 0, 0, Block,
            seedCellId: Block | (uint)((int)(x / 24f) * 8 + 1), partArray: parts);
    }

    [Fact]
    public void StableReadsAndUnrelatedOwnerMotionPreserveRevision()
    {
        var registry = new ShadowObjectRegistry();
        Assert.Equal(0UL, registry.GetCellRenderRevision(Cell));
        Register(registry, 1);
        Register(registry, 2, 84f);
        ulong revision = registry.GetCellRenderRevision(Cell);
        Assert.NotEqual(0UL, revision);
        registry.UpdatePosition(2, new Vector3(85f, 12f, 50f), Quaternion.Identity, 0, 0, Block);
        Assert.Equal(revision, registry.GetCellRenderRevision(Cell));
        registry.UpdatePosition(1, new Vector3(13f, 12f, 50f), Quaternion.Identity, 0, 0, Block);
        Assert.True(registry.GetCellRenderRevision(Cell) > revision);
    }

    [Fact]
    public void RemovingOneOfSeveralOwnersInvalidatesRemainingBucket()
    {
        var registry = new ShadowObjectRegistry();
        Register(registry, 1);
        Register(registry, 2);
        ulong revision = registry.GetCellRenderRevision(Cell);
        registry.Deregister(1);
        Assert.NotEmpty(registry.GetRetailPartEntriesInCell(Cell));
        Assert.True(registry.GetCellRenderRevision(Cell) > revision);
    }

    [Fact]
    public void EmptyRecreatedAndResetCellsNeverReusePopulatedRevision()
    {
        var registry = new ShadowObjectRegistry();
        Register(registry, 1);
        ulong first = registry.GetCellRenderRevision(Cell);
        registry.Deregister(1);
        Assert.Equal(0UL, registry.GetCellRenderRevision(Cell));
        Register(registry, 1);
        ulong second = registry.GetCellRenderRevision(Cell);
        Assert.True(second > first);
        registry.Clear();
        Assert.Equal(0UL, registry.GetCellRenderRevision(Cell));
        Register(registry, 1);
        Assert.True(registry.GetCellRenderRevision(Cell) > second);
    }

    [Fact]
    public void AttachedPartPublicationAndRemovalInvalidateParentCell()
    {
        var registry = new ShadowObjectRegistry();
        Register(registry, 1);
        ulong before = registry.GetCellRenderRevision(Cell);
        Assert.True(registry.AttachChild(2, 1, Parts(0x01000002)));
        ulong attached = registry.GetCellRenderRevision(Cell);
        Assert.True(attached > before);
        Assert.True(registry.DetachChild(2));
        Assert.True(registry.GetCellRenderRevision(Cell) > attached);
    }

    [Fact]
    public void ReplacingPartPayloadInvalidatesWithoutChangingMembership()
    {
        var registry = new ShadowObjectRegistry();
        Register(registry, 1);
        ulong before = registry.GetCellRenderRevision(Cell);
        var parts = Parts(0x01000002);
        registry.ReplaceMultiPartPayload(1, new Vector3(12f, 12f, 50f), Quaternion.Identity,
            parts, 0, EntityCollisionFlags.None, 0, 0, Block, seedCellId: Cell,
            partArray: parts);
        Assert.True(registry.GetCellRenderRevision(Cell) > before);
        Assert.All(registry.GetRetailPartEntriesInCell(Cell), entry => Assert.Equal(0x01000002u, entry.GfxObjId));
    }
    [Fact]
    public void PreparingPositionLeavesStampStableAndCommitInvalidatesBothCells()
    {
        var registry = new ShadowObjectRegistry();
        Register(registry, 1);
        Register(registry, 2);
        Register(registry, 3, 36f);
        uint destination = Block | 9;
        ulong oldCell = registry.GetCellRenderRevision(Cell);
        ulong newCell = registry.GetCellRenderRevision(destination);
        Assert.True(registry.TryPrepareSetPosition(1, new Vector3(36f, 12f, 50f),
            Quaternion.Identity, destination, 0, 0, PhysicsShadowCommitAction.Replace,
            crossCellIds: [destination], provenShapeless: false, suspendOwner: false,
            out var prepared));
        Assert.Equal(oldCell, registry.GetCellRenderRevision(Cell));
        Assert.Equal(newCell, registry.GetCellRenderRevision(destination));
        Assert.True(registry.TryApplySetPosition(prepared!, out _));
        Assert.True(registry.GetCellRenderRevision(Cell) > oldCell);
        Assert.True(registry.GetCellRenderRevision(destination) > newCell);
    }

    [Fact]
    public void RetiringOwnerInvalidatesSurvivingCellAndBlockRemovalEmptiesIt()
    {
        var registry = new ShadowObjectRegistry();
        Register(registry, 1);
        Register(registry, 2);
        ulong before = registry.GetCellRenderRevision(Cell);
        registry.RetireOwnerFromLandblock(1, Block);
        Assert.True(registry.GetCellRenderRevision(Cell) > before);
        registry.RemoveLandblock(Block);
        Assert.Equal(0UL, registry.GetCellRenderRevision(Cell));
        Register(registry, 1);
        Assert.True(registry.GetCellRenderRevision(Cell) > before);
    }}

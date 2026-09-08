using System.Numerics;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class AttachmentUpdateOrderTests
{
    [Fact]
    public void ReinsertedParentStillUpdatesBeforeGrandchild()
    {
        const uint parent = 10u;
        const uint grandchild = 20u;
        // Dictionary order models B being removed/re-added while C remained.
        uint[] insertionOrder = [grandchild, parent];
        var parents = new Dictionary<uint, uint?>
        {
            [grandchild] = parent,
            [parent] = null,
        };
        var calls = new List<uint>();

        var children = insertionOrder.ToDictionary(id => id, id => parents[id]);
        new AttachmentUpdateOrder<uint, uint?>().ForEachParentFirst(
            children,
            static parentId => parentId,
            id =>
            {
                calls.Add(id);
                return true;
            });

        Assert.Equal([parent, grandchild], calls);
    }

    [Fact]
    public void FailedParentSuppressesAndReportsDescendantsAfterTraversal()
    {
        const uint parent = 10u;
        const uint grandchild = 20u;
        var children = new Dictionary<uint, uint?>
        {
            [grandchild] = parent,
            [parent] = null,
        };
        var calls = new List<uint>();

        IReadOnlyList<uint> failed = new AttachmentUpdateOrder<uint, uint?>().ForEachParentFirst(
            children,
            static parentId => parentId,
            id =>
            {
                calls.Add(id);
                return id != parent;
            });

        Assert.Equal([parent], calls);
        Assert.Equal([parent, grandchild], failed);
        Assert.Equal(2, children.Count);
    }

    [Fact]
    public void NestedDrawableUsesImmediateParentsComposedWorldRoot()
    {
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        Matrix4x4 parentWorld = Matrix4x4.CreateFromQuaternion(turn)
            * Matrix4x4.CreateTranslation(100f, 0f, 0f);
        var child = new AcDream.Core.World.WorldEntity
        {
            Id = 3u,
            SourceGfxObjOrSetupId = 0x02000003u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs =
            [
                new AcDream.Core.World.MeshRef(
                    0x01000003u,
                    Matrix4x4.CreateTranslation(2f, 0f, 0f)),
            ],
        };

        Assert.True(EquippedChildRenderController.ApplyParentWorldPose(child, parentWorld));
        Matrix4x4 installedRoot = Matrix4x4.CreateFromQuaternion(child.Rotation)
            * Matrix4x4.CreateTranslation(child.Position);
        Vector3 renderedOrigin = Vector3.Transform(
            Vector3.Zero,
            child.MeshRefs[0].PartTransform * installedRoot);

        Assert.Equal(new Vector3(100f, 2f, 0f), renderedOrigin, new Vector3ToleranceComparer());
    }

    [Fact]
    public void NestedDrawable_InheritsAncestorSuppressionWithoutMutatingOwnState()
    {
        var root = Entity(1u);
        var child = Entity(2u);
        var grandchild = Entity(3u);
        root.IsDrawVisible = false;

        EquippedChildRenderController.ApplyParentDrawVisibility(child, root);
        EquippedChildRenderController.ApplyParentDrawVisibility(grandchild, child);

        Assert.True(child.IsDrawVisible);
        Assert.False(child.IsAncestorDrawVisible);
        Assert.True(grandchild.IsDrawVisible);
        Assert.False(grandchild.IsAncestorDrawVisible);

        root.IsDrawVisible = true;
        EquippedChildRenderController.ApplyParentDrawVisibility(child, root);
        EquippedChildRenderController.ApplyParentDrawVisibility(grandchild, child);
        Assert.True(child.IsAncestorDrawVisible);
        Assert.True(grandchild.IsAncestorDrawVisible);
    }

    private static AcDream.Core.World.WorldEntity Entity(uint id) => new()
    {
        Id = id,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<AcDream.Core.World.MeshRef>(),
    };

    [Fact]
    public void AncestorTeardownCollectsEntireAttachmentSubtreePostOrder()
    {
        var children = new Dictionary<uint, uint?>
        {
            [20u] = 10u,
            [30u] = 20u,
        };
        var order = new AttachmentUpdateOrder<uint, uint?>().CollectSubtreePostOrder(
            children,
            10u,
            static parentId => parentId);

        Assert.Equal([30u, 20u, 10u], order);
    }

    [Fact]
    public void PoseRecoveryRealizesCompleteDescendantChainParentFirst()
    {
        var waitingByParent = new Dictionary<uint, IReadOnlyList<uint>>
        {
            [10u] = [20u],
            [20u] = [30u],
        };
        var realized = new List<uint>();

        new AttachmentUpdateOrder<uint, uint?>().RealizeDescendants(
            10u,
            parent => waitingByParent.TryGetValue(parent, out var waiting)
                ? waiting
                : Array.Empty<uint>(),
            child =>
            {
                realized.Add(child);
                return true;
            });

        Assert.Equal([20u, 30u], realized);
    }

    [Fact]
    public void PoseRecoveryDoesNotTraverseThroughUnrealizedIntermediateParent()
    {
        var waitingByParent = new Dictionary<uint, IReadOnlyList<uint>>
        {
            [10u] = [20u],
            [20u] = [30u],
        };
        var attempted = new List<uint>();

        new AttachmentUpdateOrder<uint, uint?>().RealizeDescendants(
            10u,
            parent => waitingByParent.TryGetValue(parent, out var waiting)
                ? waiting
                : Array.Empty<uint>(),
            child =>
            {
                attempted.Add(child);
                return false;
            });

        Assert.Equal([20u], attempted);
    }

    [Fact]
    public void DrawableRootRejectsVisualScaleInRigidPoseChannel()
    {
        var child = new AcDream.Core.World.WorldEntity
        {
            Id = 4u,
            SourceGfxObjOrSetupId = 0x02000004u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<AcDream.Core.World.MeshRef>(),
        };

        Assert.False(EquippedChildRenderController.ApplyParentWorldPose(
            child,
            Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(10, 0, 0)));
    }

    private sealed class Vector3ToleranceComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => Vector3.DistanceSquared(x, y) < 1e-8f;
        public int GetHashCode(Vector3 obj) => obj.GetHashCode();
    }
}

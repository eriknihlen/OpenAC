using System.Collections.Generic;
using System.Numerics;
using System.Text;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class Ts4Path6ConformanceTests
{
    private const uint Cell = 0xA9B40001u;
    private const float Radius = BSPStepUpFixtures.SphereRadius;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimarySteepHit_GraphAndFlat_SetCollideWithExactState(
        bool seedSlidingNormal)
    {
        var fixture = Normalize(BSPStepUpFixtures.SlopedUnwalkable());
        FlatPhysicsBsp flat = FlatCollisionAssetBuilder.FlattenPhysicsBsp(
            fixture.Root,
            fixture.Resolved);
        Vector3 currentBody = new(0.5f, 0f, 1.1f);
        Vector3 targetBody = new(0.5f, 0f, 0.9f);
        Vector3 targetCenter = targetBody + new Vector3(0f, 0f, Radius);
        Vector3 expectedNormal = fixture.Resolved[
            BSPStepUpFixtures.SlopedUnwalkable_SlopeId].Plane.Normal;
        Vector3 seededSliding = Vector3.UnitY;

        SiteOutcome graph = Run(
            fixture.Root,
            fixture.Resolved,
            flat: null,
            currentBody,
            targetBody,
            new Sphere { Origin = targetCenter, Radius = Radius },
            head: null,
            seedSlidingNormal,
            seededSliding);
        SiteOutcome prepared = Run(
            root: null,
            fixture.Resolved,
            flat,
            currentBody,
            targetBody,
            new Sphere { Origin = targetCenter, Radius = Radius },
            head: null,
            seedSlidingNormal,
            seededSliding);

        Assert.Equal(graph.Bits, prepared.Bits);
        Assert.Equal(TransitionState.Adjusted, graph.State);
        Assert.True(graph.Collide);
        AssertVectorBits(targetBody, graph.CheckPos);
        Assert.Equal(Cell, graph.CheckCellId);
        AssertVectorBits(targetBody, graph.BackupCheckPos);
        Assert.Equal(Cell, graph.BackupCheckCellId);
        AssertVectorBits(expectedNormal, graph.StepUpNormal);
        AssertFloatBits(1f, graph.WalkInterp);
        AssertFloatBits(PhysicsGlobals.LandingZ, graph.WalkableAllowance);
        Assert.False(graph.CollisionNormalValid);
        Assert.Equal(seedSlidingNormal, graph.SlidingNormalValid);
        AssertVectorBits(
            seedSlidingNormal ? seededSliding : Vector3.Zero,
            graph.SlidingNormal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecondaryOnlyHit_GraphAndFlat_HardStopsWithoutSetCollide(
        bool seedSlidingNormal)
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildRaisedWall();
        FlatPhysicsBsp flat = FlatCollisionAssetBuilder.FlattenPhysicsBsp(
            root,
            resolved);
        Vector3 currentBody = new(0.1f, 0f, 0f);
        Vector3 targetBody = new(0.35f, 0f, 0f);
        var foot = new Sphere
        {
            Origin = targetBody + new Vector3(0f, 0f, Radius),
            Radius = Radius,
        };
        var head = new Sphere
        {
            Origin = targetBody + new Vector3(0f, 0f, 0.8f),
            Radius = Radius,
        };
        Vector3 seededSliding = Vector3.UnitY;

        SiteOutcome graph = Run(
            root,
            resolved,
            flat: null,
            currentBody,
            targetBody,
            foot,
            head,
            seedSlidingNormal,
            seededSliding);
        SiteOutcome prepared = Run(
            root: null,
            resolved,
            flat,
            currentBody,
            targetBody,
            foot,
            head,
            seedSlidingNormal,
            seededSliding);

        Assert.Equal(graph.Bits, prepared.Bits);
        Assert.Equal(TransitionState.Collided, graph.State);
        Assert.False(graph.Collide);
        AssertVectorBits(targetBody, graph.CheckPos);
        Assert.Equal(Cell, graph.CheckCellId);
        AssertVectorBits(new Vector3(91f, 92f, 93f), graph.BackupCheckPos);
        Assert.Equal(0xA9B40077u, graph.BackupCheckCellId);
        AssertVectorBits(Vector3.Zero, graph.StepUpNormal);
        AssertFloatBits(0.625f, graph.WalkInterp);
        AssertFloatBits(0.8125f, graph.WalkableAllowance);
        Assert.True(graph.CollisionNormalValid);
        AssertFloatBits(-1f, graph.CollisionNormal.X);
        Assert.Equal(0f, graph.CollisionNormal.Y);
        Assert.Equal(0f, graph.CollisionNormal.Z);
        Assert.Equal(seedSlidingNormal, graph.SlidingNormalValid);
        AssertVectorBits(
            seedSlidingNormal ? seededSliding : Vector3.Zero,
            graph.SlidingNormal);
    }

    private static SiteOutcome Run(
        PhysicsBSPNode? root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        FlatPhysicsBsp? flat,
        Vector3 currentBody,
        Vector3 targetBody,
        Sphere foot,
        Sphere? head,
        bool seedSlidingNormal,
        Vector3 seededSliding)
    {
        var transition = new Transition();
        transition.SpherePath.InitPath(
            currentBody,
            targetBody,
            Cell,
            Radius,
            sphereHeight: head is null ? 0f : 1f);
        transition.SpherePath.SetCheckPos(targetBody, Cell);
        transition.SpherePath.BackupCheckPos = new Vector3(91f, 92f, 93f);
        transition.SpherePath.BackupCheckCellId = 0xA9B40077u;
        transition.SpherePath.WalkInterp = 0.625f;
        transition.SpherePath.WalkableAllowance = 0.8125f;
        if (seedSlidingNormal)
            transition.CollisionInfo.SetSlidingNormal(seededSliding);

        TransitionState state = flat is null
            ? BSPQuery.FindCollisions(
                root,
                resolved,
                transition,
                foot,
                head,
                currentBody,
                Vector3.UnitZ,
                1f)
            : FlatBspQuery.FindCollisions(
                flat,
                transition,
                foot,
                head,
                currentBody,
                Vector3.UnitZ,
                1f);

        SpherePath path = transition.SpherePath;
        CollisionInfo collision = transition.CollisionInfo;
        return new SiteOutcome(
            state,
            path.Collide,
            path.CheckPos,
            path.CheckCellId,
            path.BackupCheckPos,
            path.BackupCheckCellId,
            path.StepUpNormal,
            path.WalkInterp,
            path.WalkableAllowance,
            collision.CollisionNormalValid,
            collision.CollisionNormal,
            collision.SlidingNormalValid,
            collision.SlidingNormal,
            Signature(state, path, collision));
    }

    private static (
        PhysicsBSPNode Root,
        Dictionary<ushort, ResolvedPolygon> Resolved) BuildRaisedWall()
    {
        Vector3[] vertices =
        [
            new(0.5f, -1f, 0.55f),
            new(0.5f, -1f, 2.5f),
            new(0.5f,  1f, 2.5f),
            new(0.5f,  1f, 0.55f),
        ];
        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere
            {
                Origin = new Vector3(0.5f, 0f, 1.5f),
                Radius = 4f,
            },
        };
        root.Polygons.Add(1);
        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [1] = new ResolvedPolygon
            {
                Id = 1,
                Vertices = vertices,
                Plane = new Plane(-Vector3.UnitX, 0.5f),
                NumPoints = vertices.Length,
                SidesType = CullMode.None,
            },
        };
        return (root, resolved);
    }

    private static (
        PhysicsBSPNode Root,
        Dictionary<ushort, ResolvedPolygon> Resolved) Normalize(
            (PhysicsBSPNode Root, Dictionary<ushort, ResolvedPolygon> Resolved) fixture)
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>(fixture.Resolved.Count);
        foreach ((ushort id, ResolvedPolygon polygon) in fixture.Resolved)
        {
            resolved.Add(id, new ResolvedPolygon
            {
                Id = id,
                Vertices = polygon.Vertices,
                Plane = polygon.Plane,
                NumPoints = polygon.NumPoints,
                SidesType = polygon.SidesType,
            });
        }

        return (fixture.Root, resolved);
    }

    private static string Signature(
        TransitionState state,
        SpherePath path,
        CollisionInfo collision)
    {
        var bits = new StringBuilder(256);
        bits.Append((int)state).Append('|').Append(path.Collide ? 1 : 0).Append('|');
        Append(bits, path.CheckPos);
        bits.Append(path.CheckCellId.ToString("X8")).Append('|');
        Append(bits, path.BackupCheckPos);
        bits.Append(path.BackupCheckCellId.ToString("X8")).Append('|');
        Append(bits, path.StepUpNormal);
        Append(bits, path.WalkInterp);
        Append(bits, path.WalkableAllowance);
        bits.Append(collision.CollisionNormalValid ? 1 : 0).Append('|');
        Append(bits, collision.CollisionNormal);
        bits.Append(collision.SlidingNormalValid ? 1 : 0).Append('|');
        Append(bits, collision.SlidingNormal);
        return bits.ToString();
    }

    private static void Append(StringBuilder target, float value) =>
        target.Append(BitConverter.SingleToUInt32Bits(value).ToString("X8")).Append('|');

    private static void Append(StringBuilder target, Vector3 value)
    {
        Append(target, value.X);
        Append(target, value.Y);
        Append(target, value.Z);
    }

    private static void AssertFloatBits(float expected, float actual) =>
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(expected),
            BitConverter.SingleToUInt32Bits(actual));

    private static void AssertVectorBits(Vector3 expected, Vector3 actual)
    {
        AssertFloatBits(expected.X, actual.X);
        AssertFloatBits(expected.Y, actual.Y);
        AssertFloatBits(expected.Z, actual.Z);
    }

    private sealed record SiteOutcome(
        TransitionState State,
        bool Collide,
        Vector3 CheckPos,
        uint CheckCellId,
        Vector3 BackupCheckPos,
        uint BackupCheckCellId,
        Vector3 StepUpNormal,
        float WalkInterp,
        float WalkableAllowance,
        bool CollisionNormalValid,
        Vector3 CollisionNormal,
        bool SlidingNormalValid,
        Vector3 SlidingNormal,
        string Bits);
}

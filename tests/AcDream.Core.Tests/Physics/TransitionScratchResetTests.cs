using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Threading;
using AcDream.Core.Physics;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics;

/// <summary>
/// Structural proof for the retained transition lifetime introduced by Slice
/// I1. Reflection deliberately makes a newly added state field fail this test
/// until its poison/reset representation is defined.
/// </summary>
public sealed class TransitionScratchResetTests
{
    [Fact]
    public void ResetForReuse_PoisonsEveryStoredMemberAndMatchesFreshState()
    {
        var transition = new Transition();
        PoisonStoredState(transition);

        ObjectInfo objectInfo = transition.ObjectInfo;
        SpherePath path = transition.SpherePath;
        CollisionInfo collision = transition.CollisionInfo;
        Sphere[] localSpheres = path.LocalSphere;
        Sphere[] globalSpheres = path.GlobalSphere;
        Sphere[] currentSpheres = path.GlobalCurrCenter;
        Sphere[] allSpheres =
        [
            .. localSpheres,
            .. globalSpheres,
            .. currentSpheres,
        ];
        Vector3[] retainedWalkable = Assert.IsType<Vector3[]>(
            path.RetainedWalkableVertexStorage);
        Vector3[] retainedLastWalkable = Assert.IsType<Vector3[]>(
            path.RetainedLastWalkableVertexStorage);
        CellArray retainedCells = path.CellCandidates;
        CellOrderScratchArena retainedOrder = path.OrderedCellScratch;
        List<uint>[] retainedOrderRecords =
            retainedOrder.RetainedRecords.ToArray();
        List<uint> retainedCollisions = collision.CollideObjectGuids;

        transition.ResetForReuse();

        Assert.Same(objectInfo, transition.ObjectInfo);
        Assert.Same(path, transition.SpherePath);
        Assert.Same(collision, transition.CollisionInfo);
        Assert.Same(localSpheres, path.LocalSphere);
        Assert.Same(globalSpheres, path.GlobalSphere);
        Assert.Same(currentSpheres, path.GlobalCurrCenter);
        Assert.Same(retainedWalkable, path.RetainedWalkableVertexStorage);
        Assert.Same(retainedLastWalkable, path.RetainedLastWalkableVertexStorage);
        Assert.Same(retainedCells, path.CellCandidates);
        Assert.Same(retainedOrder, path.OrderedCellScratch);
        Assert.Equal(0, retainedOrder.ActiveDepth);
        Assert.Equal(retainedOrderRecords.Length, retainedOrder.RetainedRecordCount);
        for (int i = 0; i < retainedOrderRecords.Length; i++)
        {
            Assert.Same(
                retainedOrderRecords[i],
                retainedOrder.RetainedRecords[i]);
            Assert.Empty(retainedOrderRecords[i]);
        }
        Assert.Same(retainedCollisions, collision.CollideObjectGuids);

        Sphere[] resetSpheres =
        [
            .. path.LocalSphere,
            .. path.GlobalSphere,
            .. path.GlobalCurrCenter,
        ];
        for (int i = 0; i < allSpheres.Length; i++)
            Assert.Same(allSpheres[i], resetSpheres[i]);

        Assert.All(retainedWalkable, value => AssertBitwise(Vector3.Zero, value));
        Assert.All(retainedLastWalkable, value => AssertBitwise(Vector3.Zero, value));
        AssertFreshState(new Transition(), transition);
    }

    [Fact]
    public void WalkableStorage_ReusesOnlyAnExactLogicalLength()
    {
        var path = new SpherePath();
        var first = new[]
        {
            new Vector3(1f, 2f, 3f),
            new Vector3(4f, 5f, 6f),
            new Vector3(7f, 8f, 9f),
        };
        var second = new[]
        {
            new Vector3(-1f, -2f, -3f),
            new Vector3(-4f, -5f, -6f),
            new Vector3(-7f, -8f, -9f),
        };

        path.SetWalkable(new Plane(Vector3.UnitZ, -3f), first, Vector3.UnitZ);
        Vector3[] firstStorage = Assert.IsType<Vector3[]>(path.WalkableVertices);
        Vector3[] firstLastStorage = Assert.IsType<Vector3[]>(path.LastWalkableVertices);

        path.ResetForReuse();
        path.SetWalkable(new Plane(Vector3.UnitZ, 3f), second, Vector3.UnitZ);

        Assert.Same(firstStorage, path.WalkableVertices);
        Assert.Same(firstLastStorage, path.LastWalkableVertices);
        AssertVectorsBitwise(second, Assert.IsType<Vector3[]>(path.WalkableVertices));
        AssertVectorsBitwise(second, Assert.IsType<Vector3[]>(path.LastWalkableVertices));

        Vector3[] fourVertices =
        [
            .. second,
            new Vector3(10f, 11f, 12f),
        ];
        path.SetWalkable(new Plane(Vector3.UnitZ, 0f), fourVertices, Vector3.UnitZ);

        Assert.NotSame(firstStorage, path.WalkableVertices);
        Assert.NotSame(firstLastStorage, path.LastWalkableVertices);
        Assert.Equal(4, path.WalkableVertices!.Length);
        Assert.Equal(4, path.LastWalkableVertices!.Length);
        AssertVectorsBitwise(fourVertices, path.WalkableVertices);
        AssertVectorsBitwise(fourVertices, path.LastWalkableVertices);
    }

    [Fact]
    public void PhysicsBodyWalkablePublication_RetainsOnlyAnExactLength()
    {
        var body = new PhysicsBody();
        Vector3[] triangle =
        [
            new(1f, 2f, 3f),
            new(4f, 5f, 6f),
            new(7f, 8f, 9f),
        ];

        body.SetWalkableVerticesExact(triangle);
        Vector3[] first = Assert.IsType<Vector3[]>(body.WalkableVertices);
        body.WalkableVertices = null;
        body.SetWalkableVerticesExact(triangle);
        Assert.Same(first, body.WalkableVertices);

        body.SetWalkableVerticesExact(
        [
            .. triangle,
            new Vector3(10f, 11f, 12f),
        ]);
        Assert.NotSame(first, body.WalkableVertices);
        Assert.Equal(4, body.WalkableVertices!.Length);
    }

    [Fact]
    public void Arena_IsTenDeepDistinctAndReusesRecordsInLifoOrder()
    {
        var arena = new TransitionScratchArena();
        var leased = new Transition[TransitionScratchArena.Capacity];

        for (int i = 0; i < leased.Length; i++)
        {
            leased[i] = arena.Rent();
            Assert.Equal(i + 1, arena.ActiveDepth);
            Assert.DoesNotContain(
                leased[i],
                leased.AsSpan(0, i).ToArray());
        }

        Assert.Throws<InvalidOperationException>(() => arena.Rent());

        for (int i = leased.Length - 1; i >= 0; i--)
        {
            arena.Return(leased[i]);
            Assert.Equal(i, arena.ActiveDepth);
        }

        Transition reused = arena.Rent();
        Assert.Same(leased[0], reused);
        reused.ObjectInfo.State = ObjectInfoState.IsPlayer;
        arena.Return(reused);

        reused = arena.Rent();
        Assert.Equal(ObjectInfoState.None, reused.ObjectInfo.State);
        arena.Return(reused);
    }

    [Fact]
    public void Arena_RejectsOutOfOrderAndCrossThreadUse()
    {
        var arena = new TransitionScratchArena();
        Transition outer = arena.Rent();
        Transition inner = arena.Rent();

        Assert.Throws<InvalidOperationException>(() => arena.Return(outer));
        arena.Return(inner);

        Exception? crossThreadError = null;
        var thread = new Thread(() =>
        {
            try
            {
                arena.Rent();
            }
            catch (Exception error)
            {
                crossThreadError = error;
            }
        });
        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(crossThreadError);
        Assert.Equal(1, arena.ActiveDepth);
        arena.Return(outer);
        Assert.Equal(0, arena.ActiveDepth);
    }

    private static void PoisonStoredState(object instance)
    {
        foreach (FieldInfo field in instance.GetType().GetFields(
                     BindingFlags.Instance
                     | BindingFlags.Public
                     | BindingFlags.NonPublic
                     | BindingFlags.DeclaredOnly))
        {
            object? current = field.GetValue(instance);
            if (field.IsInitOnly)
            {
                switch (current)
                {
                    case ObjectInfo objectInfo:
                        PoisonStoredState(objectInfo);
                        break;
                    case SpherePath path:
                        PoisonStoredState(path);
                        break;
                    case CollisionInfo collision:
                        PoisonStoredState(collision);
                        break;
                    case Sphere[] spheres:
                        foreach (Sphere sphere in spheres)
                        {
                            sphere.Origin = new Vector3(11f, 12f, 13f);
                            sphere.Radius = 14f;
                        }
                        break;
                    case CellArray cells:
                        cells.Add(0xA9B40001u);
                        break;
                    case CellOrderScratchArena orderScratch:
                        orderScratch.Rent().Add(0xA9B40001u);
                        orderScratch.Rent().Add(0xA9B40100u);
                        break;
                    case List<uint> values:
                        values.Add(0xDEADBEEFu);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"No retained-state poison rule for "
                            + $"{instance.GetType().Name}.{field.Name} "
                            + $"({field.FieldType.Name}).");
                }

                continue;
            }

            field.SetValue(instance, PoisonValue(field.FieldType));
        }
    }

    private static object PoisonValue(Type type)
    {
        if (type == typeof(bool))
            return true;
        if (type == typeof(int))
            return 37;
        if (type == typeof(uint))
            return 0xC0FFEEu;
        if (type == typeof(float))
            return 17.25f;
        if (type == typeof(Vector3))
            return new Vector3(1.25f, -2.5f, 3.75f);
        if (type == typeof(Quaternion))
            return new Quaternion(1f, 2f, 3f, 4f);
        if (type == typeof(Plane))
            return new Plane(new Vector3(5f, 6f, 7f), 8f);
        if (type == typeof(Vector3[]))
        {
            return new[]
            {
                new Vector3(1f, 2f, 3f),
                new Vector3(4f, 5f, 6f),
                new Vector3(7f, 8f, 9f),
            };
        }
        if (type == typeof(Vector3?))
            return (Vector3?)new Vector3(9f, 8f, 7f);
        if (type == typeof(uint?))
            return (uint?)0xBADF00Du;
        if (type.IsEnum)
            return Enum.ToObject(type, uint.MaxValue);

        throw new InvalidOperationException(
            $"No poison value for stored type {type.FullName}.");
    }

    private static void AssertFreshState(object expected, object actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());

        foreach (FieldInfo field in expected.GetType().GetFields(
                     BindingFlags.Instance
                     | BindingFlags.Public
                     | BindingFlags.NonPublic
                     | BindingFlags.DeclaredOnly))
        {
            object? expectedValue = field.GetValue(expected);
            object? actualValue = field.GetValue(actual);

            if (field.Name is "_walkableVertexStorage"
                or "_lastWalkableVertexStorage")
            {
                Vector3[] retained = Assert.IsType<Vector3[]>(actualValue);
                Assert.All(retained, value => AssertBitwise(Vector3.Zero, value));
                continue;
            }

            switch (expectedValue)
            {
                case ObjectInfo expectedObject:
                    AssertFreshState(expectedObject, Assert.IsType<ObjectInfo>(actualValue));
                    break;
                case SpherePath expectedPath:
                    AssertFreshState(expectedPath, Assert.IsType<SpherePath>(actualValue));
                    break;
                case CollisionInfo expectedCollision:
                    AssertFreshState(
                        expectedCollision,
                        Assert.IsType<CollisionInfo>(actualValue));
                    break;
                case Sphere[] expectedSpheres:
                    {
                        Sphere[] actualSpheres = Assert.IsType<Sphere[]>(actualValue);
                        Assert.Equal(expectedSpheres.Length, actualSpheres.Length);
                        for (int i = 0; i < expectedSpheres.Length; i++)
                        {
                            AssertBitwise(expectedSpheres[i].Origin, actualSpheres[i].Origin);
                            AssertBitwise(expectedSpheres[i].Radius, actualSpheres[i].Radius);
                        }
                        break;
                    }
                case CellArray expectedCells:
                    Assert.Equal(expectedCells.OrderedIds, Assert.IsType<CellArray>(actualValue).OrderedIds);
                    break;
                case CellOrderScratchArena:
                    {
                        CellOrderScratchArena actualScratch =
                            Assert.IsType<CellOrderScratchArena>(actualValue);
                        Assert.Equal(0, actualScratch.ActiveDepth);
                        Assert.All(actualScratch.RetainedRecords, Assert.Empty);
                        break;
                    }
                case List<uint> expectedValues:
                    Assert.Equal(expectedValues, Assert.IsType<List<uint>>(actualValue));
                    break;
                case Vector3 expectedVector:
                    AssertBitwise(expectedVector, Assert.IsType<Vector3>(actualValue));
                    break;
                case Quaternion expectedRotation:
                    AssertBitwise(expectedRotation, Assert.IsType<Quaternion>(actualValue));
                    break;
                case Plane expectedPlane:
                    AssertBitwise(expectedPlane, Assert.IsType<Plane>(actualValue));
                    break;
                case null:
                    Assert.Null(actualValue);
                    break;
                default:
                    Assert.Equal(expectedValue, actualValue);
                    break;
            }
        }
    }

    private static void AssertVectorsBitwise(
        IReadOnlyList<Vector3> expected,
        IReadOnlyList<Vector3> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
            AssertBitwise(expected[i], actual[i]);
    }

    private static void AssertBitwise(Vector3 expected, Vector3 actual)
    {
        AssertBitwise(expected.X, actual.X);
        AssertBitwise(expected.Y, actual.Y);
        AssertBitwise(expected.Z, actual.Z);
    }

    private static void AssertBitwise(Quaternion expected, Quaternion actual)
    {
        AssertBitwise(expected.X, actual.X);
        AssertBitwise(expected.Y, actual.Y);
        AssertBitwise(expected.Z, actual.Z);
        AssertBitwise(expected.W, actual.W);
    }

    private static void AssertBitwise(Plane expected, Plane actual)
    {
        AssertBitwise(expected.Normal, actual.Normal);
        AssertBitwise(expected.D, actual.D);
    }

    private static void AssertBitwise(float expected, float actual)
        => Assert.Equal(
            BitConverter.SingleToInt32Bits(expected),
            BitConverter.SingleToInt32Bits(actual));
}

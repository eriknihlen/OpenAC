using System.Reflection;
using System.Numerics;
using System.Collections.Immutable;
using AcDream.App.Streaming;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DatReaderWriter.Enums;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Streaming;

public sealed class LandblockBuildFactoryTests
{
    private const uint LandblockId = 0xA9B4FFFFu;
    private const uint AdjacentLandblockId = 0xA9B5FFFFu;
    private static readonly LandblockBuildOrigin HoltburgOrigin = new(0xA9, 0xB4);

    [Fact]
    public void BuildFar_ReturnsOnlyHeightmapAndCapturedOrigin()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        var heightmap = new LandBlock { Id = LandblockId };
        proxy.Add(LandblockId, heightmap);
        var factory = Factory(dat, new object());

        LandblockBuild? result = factory.Build(Request(LandblockStreamJobKind.LoadFar));

        Assert.NotNull(result);
        Assert.Same(heightmap, result.Landblock.Heightmap);
        Assert.Empty(result.Landblock.Entities);
        Assert.Same(PhysicsDatBundle.Empty, result.Landblock.PhysicsDats);
        Assert.Null(result.EnvCells);
        Assert.Null(result.Collisions);
        Assert.Equal(HoltburgOrigin, result.Origin);
        Assert.Equal([(typeof(LandBlock), LandblockId)], proxy.Reads);
    }

    [Fact]
    public void Build_RejectsUnspecifiedOriginBeforeReadingDat()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        var factory = Factory(dat, new object());

        Assert.Throws<ArgumentException>(() => factory.Build(
            new LandblockBuildRequest(
                LandblockId,
                LandblockStreamJobKind.LoadFar,
                Generation: 1,
                Origin: default)));
        Assert.Empty(proxy.Reads);
    }

    [Fact]
    public void Build_MissingHeightmapReturnsNullWithoutPartialPayload()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        var factory = Factory(dat, new object());

        LandblockBuild? result = factory.Build(Request(LandblockStreamJobKind.LoadFar));

        Assert.Null(result);
        Assert.Equal([(typeof(LandBlock), LandblockId)], proxy.Reads);
    }

    [Fact]
    public void BuildNear_MissingLandblockInfoReturnsCompleteEmptyNearPayload()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        proxy.Add(LandblockId, new LandBlock { Id = LandblockId });
        var factory = Factory(dat, new object());

        LandblockBuild? result = factory.Build(Request(LandblockStreamJobKind.LoadNear));

        Assert.NotNull(result);
        Assert.Empty(result.Landblock.Entities);
        var envCells = Assert.IsType<AcDream.App.Rendering.Wb.EnvCellLandblockBuild>(
            result.EnvCells);
        Assert.Empty(envCells.VisibilityCells);
        Assert.Empty(envCells.Shells);
        var physics = Assert.IsType<PhysicsDatBundle>(result.Landblock.PhysicsDats);
        Assert.Null(physics.Info);
        Assert.Empty(physics.EnvCells);
        Assert.NotNull(result.Collisions);
        Assert.Empty(result.Collisions.GfxObjs);
        Assert.Empty(result.Collisions.Setups);
        Assert.Empty(result.Collisions.CellStructures);
        Assert.Empty(result.Collisions.EnvCells);
    }

    [Fact]
    public void BuildNear_PopulatesWalkZSlabUnconditionallyAndWalkBuildingsFromLandBlockInfo()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        AddNearFixture(proxy, LandblockId, environmentId: 1);
        var heights = new byte[81];
        Array.Fill(heights, (byte)10);
        heights[0] = 200;   // one outlier byte -> a distinct maxByte from the rest.
        proxy.Add(LandblockId, new LandBlock { Id = LandblockId, Height = heights });
        proxy.Add(
            (LandblockId & 0xFFFF0000u) | 0xFFFEu,
            new LandBlockInfo
            {
                NumCells = 1,
                Buildings = new List<BuildingInfo>
                {
                    new BuildingInfo
                    {
                        ModelId = 0x01234567u,
                        Frame = new Frame
                        {
                            Origin = new Vector3(12f, 12f, 0f),
                            Orientation = Quaternion.Identity,
                        },
                        Portals = new List<BuildingPortal>(),
                    },
                },
            });
        var heightTable = new float[256];
        heightTable[10] = 5f;
        heightTable[200] = 40f;
        var factory = new LandblockBuildFactory(
            dat, TestPreparedCollisionSource.Instance, new object(), heightTable);

        LandblockBuild? result = factory.Build(Request(LandblockStreamJobKind.LoadNear));

        Assert.NotNull(result);
        var envCells = Assert.IsType<AcDream.App.Rendering.Wb.EnvCellLandblockBuild>(
            result.EnvCells);
        Assert.Equal(240f, envCells.WalkMaxZ);   // heightTable[200] + 200
        Assert.Equal(4f, envCells.WalkMinZ);     // heightTable[10] - 1
        Assert.Equal(new LandblockTerrainBounds(240f, 4f), result.TerrainBounds);
        AcDream.App.Rendering.Walk.WalkBuildingFactory.Entry buildingEntry =
            Assert.Single(envCells.WalkBuildings);
        Assert.Equal((LandblockId & 0xFFFF0000u) | 1u, buildingEntry.Building.PositionCellId);
    }

    [Fact]
    public void WalkBuildingFactory_SetupModelUsesExactlyRestingPartZeroAndDefaultScale()
    {
        const uint setupId = 0x0200_0100u;
        const uint partZeroId = 0x0100_0101u;
        var dat = CreateDat(out RecordingDatProxy proxy);
        var resting = new AnimationFrame(1);
        resting.Frames.Add(new Frame
        {
            Origin = new Vector3(1f, 2f, 3f),
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f),
        });
        var setup = new Setup();
        setup.Parts.Add(partZeroId);
        setup.DefaultScale.Add(new Vector3(2f, 3f, 4f));
        setup.PlacementFrames[Placement.Resting] = resting;
        proxy.Add(setupId, setup);
        proxy.Add(partZeroId, new GfxObj
        {
            Id = partZeroId,
            SortCenter = new Vector3(4f, 5f, 6f),
        });
        var info = new BuildingInfo
        {
            ModelId = setupId,
            Frame = new Frame
            {
                Origin = new Vector3(12f, 12f, 0f),
                Orientation = Quaternion.Identity,
            },
            Portals = [],
        };

        WalkBuildingFactory.Entry entry = Assert.Single(
            WalkBuildingFactory.Build(dat, LandblockId, [info], Vector3.Zero));

        Assert.Equal(partZeroId, entry.Building.GfxObjId);
        Assert.Equal(new Vector3(4f, 5f, 6f), entry.Building.SortCenter);
        Assert.Equal(4f, entry.Building.PartZeroScaleZ);
        Matrix4x4 expected = Matrix4x4.CreateScale(2f, 3f, 4f)
            * Matrix4x4.CreateFromQuaternion(resting.Frames[0].Orientation)
            * Matrix4x4.CreateTranslation(1f, 2f, 3f);
        Assert.Equal(expected, entry.Building.PartZeroTransform);
        Assert.Equal(expected * entry.WorldTransform, entry.PartZeroWorldTransform);
    }

    [Fact]
    public void BuildFar_CarriesNonFlatAuthoredBoundsWithoutNearDataOrExtraReads()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        var heights = Enumerable.Repeat((byte)10, 81).ToArray();
        heights[80] = 200;
        proxy.Add(LandblockId, new LandBlock { Id = LandblockId, Height = heights });
        var heightTable = new float[256];
        heightTable[10] = 5.25f;
        heightTable[200] = 40.5f;
        var source = new TestPreparedCollisionSource(PreparedAssetReadStatus.Missing);
        var factory = new LandblockBuildFactory(dat, source, new object(), heightTable);

        LandblockBuild? result = factory.Build(Request(LandblockStreamJobKind.LoadFar));

        Assert.NotNull(result);
        Assert.Null(result.EnvCells);
        Assert.Equal(new LandblockTerrainBounds(240.5f, 4.25f), result.TerrainBounds);
        Assert.Empty(result.Landblock.Entities);
        Assert.Same(PhysicsDatBundle.Empty, result.Landblock.PhysicsDats);
        Assert.Null(result.Collisions);
        Assert.Equal(0, source.Reads);
        Assert.Equal([(typeof(LandBlock), LandblockId)], proxy.Reads);
    }

    [Fact]
    public void NearPreparedFaultRejectsWholeGenerationWhileFarNeverReadsCollision()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        AddNearFixture(proxy, LandblockId, environmentId: 1);
        var source = new TestPreparedCollisionSource(
            PreparedAssetReadStatus.Missing);
        var factory = new LandblockBuildFactory(
            dat,
            source,
            new object(),
            new float[256]);

        LandblockBuild? far = factory.Build(
            Request(LandblockStreamJobKind.LoadFar));
        Assert.NotNull(far);
        Assert.Null(far.Collisions);
        Assert.Equal(0, source.Reads);

        InvalidDataException failure =
            Assert.Throws<InvalidDataException>(() =>
                factory.Build(Request(LandblockStreamJobKind.LoadNear)));
        Assert.Contains(
            "complete near-tier generation",
            failure.Message,
            StringComparison.Ordinal);
        Assert.True(source.Reads > 0);
    }

    [Fact]
    public void BuildNear_MissingEnvCellSkipsOnlyThatCellWithoutPartialVisibilityEntry()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        AddNearFixture(proxy, LandblockId, environmentId: 1, cellCount: 2);
        uint missingCell = (LandblockId & 0xFFFF0000u) | 0x0101u;
        proxy.Remove<EnvCell>(missingCell);
        var factory = Factory(dat, new object());

        LandblockBuild? result = factory.Build(Request(LandblockStreamJobKind.LoadNear));

        Assert.NotNull(result);
        var envCells = Assert.IsType<AcDream.App.Rendering.Wb.EnvCellLandblockBuild>(
            result.EnvCells);
        Assert.Equal(
            [(LandblockId & 0xFFFF0000u) | 0x0100u],
            envCells.VisibilityCells.Select(cell => cell.CellId));
        var physics = Assert.IsType<PhysicsDatBundle>(result.Landblock.PhysicsDats);
        Assert.Single(physics.EnvCells);
        Assert.DoesNotContain(missingCell, physics.EnvCells.Keys);
        Assert.Single(result.Collisions!.CellStructures);
        Assert.Single(result.Collisions.EnvCells);
        Assert.DoesNotContain(missingCell, result.Collisions.EnvCells.Keys);
    }

    [Fact]
    public async Task Build_SerializesCompleteNearTransactionsOnSharedGate()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        AddNearFixture(proxy, LandblockId, environmentId: 1);
        AddNearFixture(proxy, AdjacentLandblockId, environmentId: 2);
        object gate = new();
        var factory = Factory(dat, gate);

        Assert.NotNull(factory.Build(Request(
            LandblockId,
            LandblockStreamJobKind.LoadNear,
            generation: 1,
            HoltburgOrigin)));
        (Type Type, uint Id)[] firstSequence = proxy.Reads.ToArray();
        proxy.ClearReads();
        Assert.NotNull(factory.Build(Request(
            AdjacentLandblockId,
            LandblockStreamJobKind.LoadNear,
            generation: 1,
            new LandblockBuildOrigin(0xA9, 0xB5))));
        (Type Type, uint Id)[] secondSequence = proxy.Reads.ToArray();
        proxy.ClearReads();
        proxy.ReadDelay = TimeSpan.FromMilliseconds(5);

        Task<LandblockBuild?> first = Task.Run(() =>
            factory.Build(Request(
                LandblockId,
                LandblockStreamJobKind.LoadNear,
                generation: 2,
                HoltburgOrigin)));
        Task<LandblockBuild?> second = Task.Run(() =>
            factory.Build(Request(
                AdjacentLandblockId,
                LandblockStreamJobKind.LoadNear,
                generation: 3,
                new LandblockBuildOrigin(0xA9, 0xB5))));
        LandblockBuild?[] results = await Task.WhenAll(first, second);

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, proxy.MaxConcurrentReads);
        (Type Type, uint Id)[] combined = proxy.Reads.ToArray();
        Assert.True(
            combined.SequenceEqual(firstSequence.Concat(secondSequence))
            || combined.SequenceEqual(secondSequence.Concat(firstSequence)),
            "Near-build DAT reads interleaved instead of remaining one serialized transaction.");
    }

    [Fact]
    public void Build_UsesTheSuppliedSharedReaderGate()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        proxy.Add(LandblockId, new LandBlock { Id = LandblockId });
        object gate = new();
        var factory = Factory(dat, gate);
        using var started = new ManualResetEventSlim();
        LandblockBuild? result = null;
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            started.Set();
            try
            {
                result = factory.Build(Request(LandblockStreamJobKind.LoadFar));
            }
            catch (Exception error)
            {
                workerError = error;
            }
        })
        {
            IsBackground = true,
            Name = "LandblockBuildFactory shared-gate contract",
        };

        Monitor.Enter(gate);
        bool startedInTime;
        bool blockedOnGate;
        bool readWhileBlocked;
        try
        {
            worker.Start();
            startedInTime = started.Wait(TimeSpan.FromSeconds(5));
            blockedOnGate = startedInTime && SpinWait.SpinUntil(
                () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5));
            readWhileBlocked = proxy.ReadObserved.IsSet;
        }
        finally
        {
            Monitor.Exit(gate);
        }

        bool joined = worker.Join(TimeSpan.FromSeconds(10));
        Assert.True(startedInTime, "the dedicated build thread did not start");
        Assert.True(blockedOnGate, "the build thread never blocked on the supplied gate");
        Assert.False(readWhileBlocked, "the DAT read bypassed the supplied gate");
        Assert.True(joined, "the build thread did not finish after the gate was released");
        Assert.Null(workerError);
        Assert.NotNull(result);
        Assert.True(proxy.ReadObserved.IsSet);
    }

    [Theory]
    [InlineData(LandblockStreamJobKind.LoadNear)]
    [InlineData(LandblockStreamJobKind.PromoteToNear)]
    public void Build_NearAndPromoteIncludeEveryValidZeroSubmeshVisibilityCell(
        LandblockStreamJobKind kind)
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        AddNearFixture(proxy, LandblockId, environmentId: 1, cellCount: 2);
        var factory = Factory(dat, new object());

        LandblockBuild? result = factory.Build(Request(kind));

        Assert.NotNull(result);
        var envCells = Assert.IsType<AcDream.App.Rendering.Wb.EnvCellLandblockBuild>(
            result.EnvCells);
        var physics = Assert.IsType<PhysicsDatBundle>(result.Landblock.PhysicsDats);
        Assert.Equal(
            [(LandblockId & 0xFFFF0000u) | 0x0100u, (LandblockId & 0xFFFF0000u) | 0x0101u],
            envCells.VisibilityCells.Select(cell => cell.CellId));
        Assert.Empty(envCells.Shells);
        Assert.NotNull(physics.Info);
        Assert.Equal(2, physics.EnvCells.Count);
        Assert.Empty(physics.Environments);
        Assert.Equal(2, result.Collisions!.CellStructures.Count);
        Assert.Equal(2, result.Collisions.EnvCells.Count);
    }

    [Fact]
    public void ConstructorSnapshotsTheRetailHeightTable()
    {
        var dat = CreateDat(out _);
        var heights = Enumerable.Range(0, 256).Select(value => (float)value).ToArray();
        var factory = new LandblockBuildFactory(
            dat,
            TestPreparedCollisionSource.Instance,
            new object(),
            heights);
        heights[42] = -1f;

        var snapshot = Assert.IsType<float[]>(typeof(LandblockBuildFactory)
            .GetField("_heightTable", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(factory));
        Assert.NotSame(heights, snapshot);
        Assert.Equal(42f, snapshot[42]);
    }

    [Fact]
    public void ConstructorRejectsIncompleteRetailHeightTable()
    {
        var dat = CreateDat(out _);

        Assert.Throws<ArgumentException>(() => new LandblockBuildFactory(
            dat,
            TestPreparedCollisionSource.Instance,
            new object(),
            new float[255]));
    }

    [Fact]
    public void Build_ExceptionReleasesSharedGateForTheNextTransaction()
    {
        var dat = CreateDat(out RecordingDatProxy proxy);
        proxy.Add(LandblockId, new LandBlock { Id = LandblockId });
        proxy.ExceptionToThrow = new InvalidDataException("fixture read failure");
        var factory = Factory(dat, new object());

        Assert.Throws<InvalidDataException>(() =>
            factory.Build(Request(LandblockStreamJobKind.LoadFar)));
        proxy.ExceptionToThrow = null;

        Assert.NotNull(factory.Build(Request(LandblockStreamJobKind.LoadFar)));
    }

    [Fact]
    public void FactorySurface_HasNoRendererWorldStateOrLiveOriginDependency()
    {
        Type type = typeof(LandblockBuildFactory);
        Type[] dependencyTypes = type
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .Concat(type.GetConstructors().SelectMany(ctor =>
                ctor.GetParameters().Select(parameter => parameter.ParameterType)))
            .ToArray();

        Assert.DoesNotContain(dependencyTypes, dependency =>
            dependency.FullName?.Contains("GameWindow", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(dependencyTypes, dependency =>
            dependency.FullName?.Contains("GpuWorldState", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(dependencyTypes, dependency =>
            dependency.FullName?.Contains("LiveWorldOrigin", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(dependencyTypes, dependency =>
            dependency.Namespace?.StartsWith("Silk.NET", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(typeof(PhysicsDataCache), dependencyTypes);
    }

    [Fact]
    [Trait("Lane", "PreparedPackage")]
    public void InstalledDatNearBuild_IsDeterministicAndClosesEntityPhysicsPayload()
    {
        string? datDirectory = ResolveDatDirectory();
        if (datDirectory is null)
            throw new InvalidOperationException(
                "Lane=PreparedPackage requires installed retail DATs; see docs/release-gate.md.");

        using var dat = new BoundedTestDatCollection(datDirectory);
        var bounded = (IDatReaderWriter)dat;
        Region? region = bounded.Get<Region>(0x13000000u);
        float[]? heights = region?.LandDefs.LandHeightTable;
        Assert.NotNull(heights);
        Assert.True(heights.Length >= 256);
        string? pakPath = ResolvePreparedPackagePath(datDirectory);
        if (pakPath is null)
            throw new InvalidOperationException(
                "Lane=PreparedPackage requires a validated acdream.pak; see docs/release-gate.md.");
        using var prepared = new PakPreparedAssetSource(pakPath, bounded);
        var factory = new LandblockBuildFactory(
            bounded,
            prepared,
            new object(),
            heights);
        LandblockBuildRequest request = Request(LandblockStreamJobKind.LoadNear, generation: 4);

        LandblockBuild? first = factory.Build(request);
        LandblockBuild? second = factory.Build(request);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(HoltburgOrigin, first.Origin);
        Assert.NotNull(first.EnvCells);
        Assert.NotNull(first.Collisions);
        Assert.NotEmpty(first.Landblock.Entities);
        AssertEntityBuildsEqual(first.Landblock.Entities, second.Landblock.Entities);
        Assert.Equal(
            first.EnvCells.VisibilityCells.Select(cell => cell.CellId),
            second.EnvCells!.VisibilityCells.Select(cell => cell.CellId));
        Assert.Equal(
            first.EnvCells.Shells.Select(shell => shell.GeometryId),
            second.EnvCells.Shells.Select(shell => shell.GeometryId));

        LandBlockInfo? info = bounded.Get<LandBlockInfo>(
            (LandblockId & 0xFFFF0000u) | 0xFFFEu);
        Assert.NotNull(info);
        var expectedVisibilityCells = new List<uint>();
        uint firstCellId = (LandblockId & 0xFFFF0000u) | 0x0100u;
        for (uint offset = 0; offset < info.NumCells; offset++)
        {
            uint cellId = firstCellId + offset;
            EnvCell? cell = bounded.Get<EnvCell>(cellId);
            if (cell is null || cell.EnvironmentId == 0)
                continue;
            DatReaderWriter.DBObjs.Environment? environment =
                bounded.Get<DatReaderWriter.DBObjs.Environment>(
                    0x0D000000u | cell.EnvironmentId);
            if (environment?.Cells.ContainsKey(cell.CellStructure) == true)
                expectedVisibilityCells.Add(cellId);
        }
        Assert.Equal(
            expectedVisibilityCells,
            first.EnvCells.VisibilityCells.Select(cell => cell.CellId));

        PhysicsDatBundle physics = Assert.IsType<PhysicsDatBundle>(
            first.Landblock.PhysicsDats);
        Assert.NotNull(physics.Info);
        Assert.Equal(physics.EnvCells.Keys.Order(), first.Collisions.EnvCells.Keys.Order());
        Assert.Equal(
            physics.EnvCells.Keys.Order(),
            first.Collisions.CellStructures.Keys.Order());
        Assert.Equal(physics.Setups.Keys.Order(), first.Collisions.Setups.Keys.Order());
        Assert.Empty(physics.Environments);
        Assert.Empty(physics.GfxObjs);
        uint[] expectedGfxObjIds = first.Landblock.Entities
            .SelectMany(static entity => entity.MeshRefs)
            .Select(static mesh => mesh.GfxObjId)
            .Distinct()
            .Order()
            .ToArray();
        Assert.Equal(
            expectedGfxObjIds,
            first.Collisions.GfxObjs.Keys.Order());
        foreach (WorldEntity entity in first.Landblock.Entities)
        {
            if ((entity.SourceGfxObjOrSetupId & 0xFF000000u) == 0x02000000u)
                Assert.Contains(entity.SourceGfxObjOrSetupId, physics.Setups.Keys);
            foreach (MeshRef mesh in entity.MeshRefs)
                Assert.Contains(mesh.GfxObjId, first.Collisions.GfxObjs.Keys);
        }
    }

    private static LandblockBuildFactory Factory(IDatReaderWriter dat, object gate) =>
        new(
            dat,
            TestPreparedCollisionSource.Instance,
            gate,
            new float[256]);

    private static LandblockBuildRequest Request(
        LandblockStreamJobKind kind,
        ulong generation = 1) =>
        new(LandblockId, kind, generation, HoltburgOrigin);

    private static LandblockBuildRequest Request(
        uint landblockId,
        LandblockStreamJobKind kind,
        ulong generation,
        LandblockBuildOrigin origin) =>
        new(landblockId, kind, generation, origin);

    private static void AddNearFixture(
        RecordingDatProxy proxy,
        uint landblockId,
        ushort environmentId,
        uint cellCount = 1)
    {
        proxy.Add(landblockId, new LandBlock { Id = landblockId });
        proxy.Add(
            (landblockId & 0xFFFF0000u) | 0xFFFEu,
            new LandBlockInfo { NumCells = cellCount });

        var environment = new DatReaderWriter.DBObjs.Environment
        {
            Id = 0x0D000000u | environmentId,
        };
        for (uint offset = 0; offset < cellCount; offset++)
        {
            ushort structure = checked((ushort)(offset + 1));
            uint cellId = (landblockId & 0xFFFF0000u) | (0x0100u + offset);
            proxy.Add(cellId, new EnvCell
            {
                Id = cellId,
                EnvironmentId = environmentId,
                CellStructure = structure,
                Position = new Frame
                {
                    Orientation = Quaternion.Identity,
                },
            });
            environment.Cells[structure] = EmptyCellStruct();
        }
        proxy.Add(environment.Id, environment);
    }

    private static CellStruct EmptyCellStruct() => new()
    {
        VertexArray = new VertexArray
        {
            Vertices = new Dictionary<ushort, SWVertex>(),
        },
    };

    private static IDatReaderWriter CreateDat(out RecordingDatProxy proxy)
    {
        IDatReaderWriter dat = DispatchProxy.Create<IDatReaderWriter, RecordingDatProxy>();
        proxy = (RecordingDatProxy)(object)dat;
        return dat;
    }

    private static void AssertEntityBuildsEqual(
        IReadOnlyList<WorldEntity> expected,
        IReadOnlyList<WorldEntity> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            WorldEntity left = expected[i];
            WorldEntity right = actual[i];
            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.SourceGfxObjOrSetupId, right.SourceGfxObjOrSetupId);
            Assert.Equal(left.Position, right.Position);
            Assert.Equal(left.Rotation, right.Rotation);
            Assert.Equal(left.ParentCellId, right.ParentCellId);
            Assert.Equal(left.EffectCellId, right.EffectCellId);
            Assert.Equal(left.MeshRefs.Count, right.MeshRefs.Count);
            for (int part = 0; part < left.MeshRefs.Count; part++)
                Assert.Equal(left.MeshRefs[part], right.MeshRefs[part]);
        }
    }

    private static string? ResolveDatDirectory()
    {
        string? configured = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured;
        const string installed = @"C:\Turbine\Asheron's Call";
        if (Directory.Exists(installed))
            return installed;
        string documents = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return Directory.Exists(documents) ? documents : null;
    }

    private static string? ResolvePreparedPackagePath(string datDirectory)
    {
        string? configured =
            System.Environment.GetEnvironmentVariable("ACDREAM_PAK_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        string besideDats = Path.Combine(datDirectory, "acdream.pak");
        if (File.Exists(besideDats))
            return besideDats;

        string documents = Path.Combine(
            System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call",
            "acdream.pak");
        return File.Exists(documents) ? documents : null;
    }

    private class RecordingDatProxy : DispatchProxy
    {
        private readonly Dictionary<(Type Type, uint Id), object> _objects = new();
        private readonly object _gate = new();
        private int _activeReads;

        internal List<(Type Type, uint Id)> Reads { get; } = new();
        internal int MaxConcurrentReads { get; private set; }
        internal TimeSpan ReadDelay { get; set; }
        internal Exception? ExceptionToThrow { get; set; }
        internal ManualResetEventSlim ReadObserved { get; } = new();

        internal void Add<T>(uint id, T value) where T : class =>
            _objects[(typeof(T), id)] = value;

        internal void Remove<T>(uint id) where T : class =>
            _objects.Remove((typeof(T), id));

        internal void ClearReads()
        {
            lock (_gate)
            {
                Reads.Clear();
                MaxConcurrentReads = 0;
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "Get" && args is [uint id])
            {
                Type type = targetMethod.ReturnType;
                int active = Interlocked.Increment(ref _activeReads);
                ReadObserved.Set();
                lock (_gate)
                {
                    Reads.Add((type, id));
                    MaxConcurrentReads = Math.Max(MaxConcurrentReads, active);
                }
                try
                {
                    if (ReadDelay > TimeSpan.Zero)
                        Thread.Sleep(ReadDelay);
                    if (ExceptionToThrow is { } error)
                        throw error;
                    return _objects.TryGetValue((type, id), out object? value)
                        ? value
                        : null;
                }
                finally
                {
                    Interlocked.Decrement(ref _activeReads);
                }
            }

            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType.IsValueType == true)
                return Activator.CreateInstance(targetMethod.ReturnType);
            return null;
        }
    }

    private sealed class TestPreparedCollisionSource :
        IPreparedCollisionSource
    {
        private static readonly FlatPhysicsBsp EmptyPhysics = new(
            -1,
            ImmutableArray<FlatPhysicsBspNode>.Empty,
            ImmutableArray<int>.Empty,
            FlatPolygonTable.Empty);
        private static readonly FlatCellStructureCollisionAsset EmptyCell =
            new(
                EmptyPhysics,
                new FlatCellContainmentBsp(
                    -1,
                    ImmutableArray<FlatCellBspNode>.Empty),
                FlatPolygonTable.Empty);
        private static readonly FlatEnvCellTopology EmptyTopology = new(
            ImmutableArray<FlatEnvCellPortal>.Empty,
            ImmutableArray<uint>.Empty,
            seenOutside: false);

        private readonly PreparedAssetReadStatus _status;

        internal TestPreparedCollisionSource(
            PreparedAssetReadStatus status =
                PreparedAssetReadStatus.Loaded)
        {
            _status = status;
        }

        internal static TestPreparedCollisionSource Instance { get; } = new();
        internal int Reads { get; private set; }

        public PreparedAssetPresence ProbeCollision(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            Result(
                new FlatGfxObjCollisionAsset(
                    EmptyPhysics,
                    null,
                    null));

        public PreparedCollisionReadResult<FlatSetupCollision>
            ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            Result(
                new FlatSetupCollision(
                    ImmutableArray<FlatCollisionCylinder>.Empty,
                    ImmutableArray<FlatCollisionSphere>.Empty,
                    0f,
                    0f,
                    0f,
                    0f));

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            Result(EmptyCell);

        public PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            Result(EmptyTopology);

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }

        private PreparedCollisionReadResult<T> Result<T>(T value)
            where T : class
        {
            Reads++;
            return _status switch
            {
                PreparedAssetReadStatus.Loaded =>
                    PreparedCollisionReadResult<T>.Loaded(value),
                PreparedAssetReadStatus.Missing =>
                    PreparedCollisionReadResult<T>.Missing,
                _ => PreparedCollisionReadResult<T>.Corrupt,
            };
        }
    }
}

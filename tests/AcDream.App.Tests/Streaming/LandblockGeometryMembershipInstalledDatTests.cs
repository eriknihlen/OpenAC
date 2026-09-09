using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Meshing;
using AcDream.Core.Physics;
using AcDream.Core.World;
using Chorizite.Core.Render.Enums;
using DatReaderWriter.DBObjs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatGfxObj = DatReaderWriter.DBObjs.GfxObj;
using DatSetup = DatReaderWriter.DBObjs.Setup;

namespace AcDream.App.Tests.Streaming;

[Trait("Lane", "InstalledDat")]
public sealed partial class LandblockGeometryMembershipInstalledDatTests
{
    private const uint LandblockId = 0xF418FFFFu;
    private const uint ShellCellId = 0xF4180104u;
    private const uint RampSetupId = 0x020009A2u;
    private const string ExpectedCombinedHash = "251A13A58133D79062B38A7145250E5B1FE018AF8E28E1D29A092C4B6C63ABF0";

    private static readonly (string Name, string Sha256)[] AcceptedDatFiles =
    [
        ("client_portal.dat", "DC6E500BA22E6B186DB7171E3F3345238B6444C85D798ADC85E550973B8D12E4"),
        ("client_cell_1.dat", "6DB0ABF00FBCEED62C3F1EE842EE7C1F423D732BED77A5B7C102EE89A52AB99E"),
        ("client_highres.dat", "503E0828D14F2F9CCBC31431E1055AC188464BF4B499DE37F4C3D5B2D9F3E727"),
        ("client_local_English.dat", "E85C820280C88FAC7DF6C8043F5E24596E9C8774193AF4123D756546F78FB2BB"),
    ];

    private readonly ITestOutputHelper _output;

    public LandblockGeometryMembershipInstalledDatTests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public void CathedralFactoryGeometryAndRetailMembership_AreCompleteAndDeterministic()
    {
        string datDirectory = RequireExplicitInstalledDatDirectory();
        VerifyInstalledFileIdentity(datDirectory);

        Capture first = CaptureProduct(datDirectory);
        Capture second = CaptureProduct(datDirectory);

        _output.WriteLine($"geometry-sha256={first.GeometryHash}");
        _output.WriteLine($"membership-sha256={first.MembershipHash}");
        _output.WriteLine($"combined-sha256={first.CombinedHash}");
        _output.WriteLine($"counts={first.Counts}");

        Assert.Equal(first.GeometryHash, second.GeometryHash);
        Assert.Equal(first.MembershipHash, second.MembershipHash);
        Assert.Equal(first.CombinedHash, second.CombinedHash);
        Assert.Equal(first.Counts, second.Counts);
        Assert.Equal(first.GeometryBytes, second.GeometryBytes);
        Assert.Equal(first.MembershipBytes, second.MembershipBytes);
        Assert.Equal(ExpectedCombinedHash, first.CombinedHash);
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void CathedralRampBuildingBridge_ReportsFiniteRetailBoxPredicates()
    {
        string datDirectory = RequireExplicitInstalledDatDirectory();
        VerifyInstalledFileIdentity(datDirectory);

        _ = WithPublishedProduct(
            datDirectory,
            (build, _, cache, engine, _) =>
            {
                ReportRampBuildingBridge(build, cache, engine);
                return true;
            });
    }

    private void VerifyInstalledFileIdentity(string datDirectory)
    {
        foreach ((string name, string accepted) in AcceptedDatFiles)
        {
            string path = Path.Combine(datDirectory, name);
            Assert.True(File.Exists(path), $"Installed DAT is missing: '{path}'.");
            using FileStream stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            _output.WriteLine($"dat-sha256 {name}={actual}");
            Assert.Equal(accepted, actual);
        }
    }

    private static string RequireExplicitInstalledDatDirectory()
    {
        Assert.Equal(
            "1",
            System.Environment.GetEnvironmentVariable("ACDREAM_RUN_INSTALLED_DAT_TESTS"));
        string? directory = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        Assert.False(
            string.IsNullOrWhiteSpace(directory),
            "C1a requires an explicit ACDREAM_DAT_DIR for its installed-DAT identity gate.");
        Assert.True(Directory.Exists(directory), $"Installed DAT directory is missing: '{directory}'.");
        return directory!;
    }

    private static Capture CaptureProduct(string datDirectory) =>
        WithPublishedProduct(
            datDirectory,
            static (build, envBuild, _, engine, meshSource) =>
            {
                ProductFacts facts = AssertSemanticFacts(build, envBuild, engine);
                (byte[] geometry, GeometryCounts geometryCounts) =
                    SerializeGeometry(build, envBuild, meshSource);
                (byte[] membership, MembershipCounts membershipCounts) =
                    SerializeMembership(build, envBuild, engine, facts);
                byte[] combined = SerializeCombined(geometry, membership);

                return new Capture(
                    geometry,
                    membership,
                    Hash(geometry),
                    Hash(membership),
                    Hash(combined),
                    new ProductCounts(geometryCounts, membershipCounts));
            });

    private static T WithPublishedProduct<T>(
        string datDirectory,
        Func<LandblockBuild, EnvCellLandblockBuild, PhysicsDataCache,
            PhysicsEngine, IPreparedAssetSource, T> consume)
    {
        using var dat = new BoundedTestDatCollection(datDirectory);
        var bounded = (IDatReaderWriter)dat;
        Region region = Assert.IsType<Region>(bounded.Get<Region>(0x13000000u));
        float[] heights = region.LandDefs.LandHeightTable;
        Assert.True(heights.Length >= 256);

        using var collisionSource = new InstalledDatPreparedCollisionAdapter(bounded);
        using var meshSource = new DatPreparedAssetSource(bounded, NullLogger.Instance);
        var factory = new LandblockBuildFactory(
            bounded,
            collisionSource,
            new object(),
            heights);
        var request = new LandblockBuildRequest(
            LandblockId,
            LandblockStreamJobKind.LoadNear,
            Generation: 1,
            new LandblockBuildOrigin(0xF4, 0x18));

        LandblockBuild build = Assert.IsType<LandblockBuild>(factory.Build(request));
        EnvCellLandblockBuild envBuild = Assert.IsType<EnvCellLandblockBuild>(build.EnvCells);
        LandblockCollisionBuild collisions = Assert.IsType<LandblockCollisionBuild>(build.Collisions);
        Assert.Equal(Vector3.Zero, PublicationOrigin(build.Origin));

        var cache = PhysicsDataCache.CreateProduction();
        var engine = new PhysicsEngine { DataCache = cache };
        Vector3 origin = PublicationOrigin(build.Origin);
        TerrainSurface terrain = LandblockPhysicsContentBuilder.BuildTerrainSurface(
            build.Landblock,
            heights);
        var cellSurfaces = new List<CellSurface>();
        var portalPlanes = new List<PortalPlane>();
        LandblockPhysicsContentBuilder.PublishPreparedCells(
            cache,
            build.Landblock,
            collisions,
            origin,
            cellSurfaces,
            portalPlanes);
        LandblockPhysicsContentBuilder.CacheBuildings(
            cache,
            build.Landblock,
            terrain,
            origin);
        LandblockPhysicsContentBuilder.CachePreparedObjects(cache, collisions);
        engine.AddLandblock(
            build.LandblockId,
            terrain,
            cellSurfaces,
            portalPlanes,
            origin.X,
            origin.Y);
        _ = LandblockPhysicsContentBuilder.PublishStaticCollision(
            engine,
            cache,
            build.Landblock,
            collisions,
            origin);

        return consume(build, envBuild, cache, engine, meshSource);
    }

    private void ReportRampBuildingBridge(
        LandblockBuild build,
        PhysicsDataCache cache,
        PhysicsEngine engine)
    {
        const uint outdoorCellId = 0xF4180009u;
        const uint destinationCellId = 0xF4180114u;
        const float epsilon = 0.000199999995f;

        WorldEntity ramp = Assert.Single(
            build.Landblock.Entities,
            entity => !entity.IsBuildingShell
                && entity.SourceGfxObjOrSetupId == RampSetupId);
        IReadOnlyList<ShadowShape> retainedParts =
            GetRetainedPartArray(engine.ShadowObjects, ramp.Id);
        Assert.Equal(ramp.MeshRefs.Count, retainedParts.Count);

        IReadOnlyList<ShadowPartBox> partBoxes = InvokePrivateStatic<
            IReadOnlyList<ShadowPartBox>>(
                typeof(ShadowObjectRegistry),
                "BuildFloodPartBoxes",
                ramp.Position,
                ramp.Rotation,
                retainedParts);
        IReadOnlyList<DatReaderWriter.Types.Sphere> partSpheres =
            InvokePrivateStatic<IReadOnlyList<DatReaderWriter.Types.Sphere>>(
                typeof(ShadowObjectRegistry),
                "BuildBspPartSpheres",
                ramp.Position,
                ramp.Rotation,
                retainedParts);
        Assert.Equal(retainedParts.Count, partBoxes.Count);
        Assert.Equal(retainedParts.Count, partSpheres.Count);

        BuildingPhysics building = Assert.IsType<BuildingPhysics>(
            cache.GetBuilding(outdoorCellId));
        CellPhysics destination = Assert.IsType<CellPhysics>(
            cache.GetCellStruct(destinationCellId));
        (int BuildingPortalIndex, BldPortalInfo BuildingPortal)[] bridges =
            building.Portals
                .Select((portal, index) => (index, portal))
                .Where(candidate => candidate.portal.OtherCellId == destinationCellId)
                .ToArray();
        Assert.NotEmpty(bridges);

        foreach ((int BuildingPortalIndex, BldPortalInfo BuildingPortal) bridge in bridges)
        {
        Assert.True(bridge.BuildingPortal.OtherPortalId >= 0);
        int reciprocalIndex = bridge.BuildingPortal.OtherPortalId;
        Assert.InRange(reciprocalIndex, 0, destination.Portals.Count - 1);
        PortalInfo reciprocal = destination.Portals[reciprocalIndex];
        Plane portalPlane = ResolvePortalPlane(
            destination,
            reciprocalIndex,
            reciprocal);
        int rawPortalSide = reciprocal.PortalSide ? 1 : 0;

        _output.WriteLine(
            $"bridge owner=0x{ramp.Id:X8} setup=0x{RampSetupId:X8} " +
            $"from=0x{outdoorCellId:X8} buildingPortal={bridge.BuildingPortalIndex} " +
            $"to=0x{destinationCellId:X8} reciprocal={reciprocalIndex} " +
            $"flags=0x{bridge.BuildingPortal.Flags:X4} reciprocalOther=0x{reciprocal.OtherCellId:X4} " +
            $"reciprocalFlags=0x{reciprocal.Flags:X4} side={rawPortalSide}");
        _output.WriteLine($"destination-world={FormatMatrix(destination.WorldTransform)}");
        _output.WriteLine($"destination-inverse={FormatMatrix(destination.InverseWorldTransform)}");
        _output.WriteLine(
            $"portal-plane n={FormatVector(portalPlane.Normal)} d={FormatFloat(portalPlane.D)}");
        _output.WriteLine(
            "shared-primitives box-transform=ShadowPartBox.RefitToLocal " +
            "box-containment=FlatBspQuery.BoxIntersectsCellBsp " +
            "sphere-containment=FlatBspQuery.SphereIntersectsCellBsp");

        FlatCellContainmentBsp containment = Assert.IsType<FlatCellContainmentBsp>(
            destination.FlatContainmentBsp);
        bool anyRetailBoxAdmit = false;
        bool anyCurrentSphereAdmit = false;
        for (int partIndex = 0; partIndex < retainedParts.Count; partIndex++)
        {
            ShadowShape shape = retainedParts[partIndex];
            ShadowPartBox box = partBoxes[partIndex];
            DatReaderWriter.Types.Sphere sphere = partSpheres[partIndex];
            Vector3 localSphereCenter = Vector3.Transform(
                sphere.Origin,
                destination.InverseWorldTransform);
            double spherePlaneDistance = DotWide(portalPlane, localSphereCenter);
            float paddedRadius = sphere.Radius + epsilon;
            bool cheapRejectPass = rawPortalSide == 1
                ? spherePlaneDistance <= paddedRadius
                : spherePlaneDistance >= -paddedRadius;
            double cheapTieMargin = rawPortalSide == 1
                ? Math.Abs(spherePlaneDistance - paddedRadius)
                : Math.Abs(spherePlaneDistance + paddedRadius);

            box.RefitToLocal(
                destination.InverseWorldTransform,
                out Vector3 localBoxMin,
                out Vector3 localBoxMax);
            RetailBoxClassification classification = ClassifyRetailBox(
                portalPlane,
                localBoxMin,
                localBoxMax,
                epsilon);
            bool planeAdmit =
                classification.Side == RetailPlaneSide.Crossing
                || (int)classification.Side == rawPortalSide;
            bool boxContainment = FlatBspQuery.BoxIntersectsCellBsp(
                containment,
                localBoxMin,
                localBoxMax);
            bool sphereContainment = FlatBspQuery.SphereIntersectsCellBsp(
                containment,
                localSphereCenter,
                sphere.Radius);
            bool retailBoxAdmit = cheapRejectPass && planeAdmit && boxContainment;
            anyRetailBoxAdmit |= retailBoxAdmit;
            anyCurrentSphereAdmit |= sphereContainment;

            Assert.True(double.IsFinite(spherePlaneDistance));
            Assert.True(double.IsFinite(cheapTieMargin));
            Assert.NotEqual(0d, cheapTieMargin);
            Assert.True(double.IsFinite(classification.EpsilonTieMargin));
            Assert.NotEqual(0d, classification.EpsilonTieMargin);

            _output.WriteLine(
                $"buildingPortal={bridge.BuildingPortalIndex} part={partIndex} gfx=0x{shape.GfxObjId:X8} " +
                $"sphereWorld={FormatVector(sphere.Origin)} radius={FormatFloat(sphere.Radius)} " +
                $"sphereLocal={FormatVector(localSphereCenter)} " +
                $"distance={spherePlaneDistance:R} padded={FormatFloat(paddedRadius)} " +
                $"cheap={cheapRejectPass} cheapTieMargin={cheapTieMargin:R}");
            _output.WriteLine(
                $"buildingPortal={bridge.BuildingPortalIndex} part={partIndex} " +
                $"boxLocal={FormatVector(box.LocalMin)}..{FormatVector(box.LocalMax)} " +
                $"boxWorldPosition={FormatVector(box.WorldPosition)} " +
                $"boxWorldRotation={FormatQuaternion(box.WorldRotation)} " +
                $"destBox={FormatVector(localBoxMin)}..{FormatVector(localBoxMax)} " +
                $"cornerSides={string.Join(',', classification.CornerSides)} " +
                $"boxSide={classification.Side} cornerTieMargin={classification.EpsilonTieMargin:R} " +
                $"planeAdmit={planeAdmit} boxContain={boxContainment} " +
                $"retailBoxAdmit={retailBoxAdmit} currentSphereContain={sphereContainment}");
        }

        _output.WriteLine(
            $"portal-summary buildingPortal={bridge.BuildingPortalIndex} " +
            $"retailBoxAny={anyRetailBoxAdmit} " +
            $"currentSphereAny={anyCurrentSphereAdmit}");
        }

        _output.WriteLine($"bridge-summary portals={bridges.Length}");

        Assert.True(engine.ShadowObjects.TryGetRetailCellArray(ramp.Id, out var actualCells));
        _output.WriteLine(
            $"current-retained-cellarray={string.Join(',', actualCells.Select(id => $"0x{id:X8}"))}");
        foreach (uint sourceCellId in actualCells
                     .TakeWhile(id => id != destinationCellId)
                     .Where(id => (id & 0xFFFFu) >= 0x0100u))
        {
            CellPhysics sourceCell = Assert.IsType<CellPhysics>(
                cache.GetCellStruct(sourceCellId));
            var candidates = new CellArray();
            CellTransit.FindTransitCellsBox(
                cache,
                sourceCell,
                sourceCellId,
                partBoxes,
                partSpheres,
                candidates,
                out bool exitOutside);
            _output.WriteLine(
                $"normal-box-source=0x{sourceCellId:X8} " +
                $"adds0114={candidates.Contains(destinationCellId)} " +
                $"exitOutside={exitOutside} " +
                $"candidates={string.Join(',', candidates.OrderedIds.Select(id => $"0x{id:X8}"))}");
        }
    }

    private static IReadOnlyList<ShadowShape> GetRetainedPartArray(
        ShadowObjectRegistry registry,
        uint entityId)
    {
        PropertyInfo property = Assert.IsAssignableFrom<PropertyInfo>(
            typeof(ShadowObjectRegistry).GetProperty(
                "_entityRetailPartArrays",
                BindingFlags.Instance | BindingFlags.NonPublic));
        var arrays = Assert.IsType<Dictionary<uint, IReadOnlyList<ShadowShape>>>(
            property.GetValue(registry));
        return Assert.IsAssignableFrom<IReadOnlyList<ShadowShape>>(arrays[entityId]);
    }

    private static T InvokePrivateStatic<T>(
        Type owner,
        string methodName,
        params object?[] arguments)
    {
        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(owner.GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic));
        return Assert.IsAssignableFrom<T>(method.Invoke(null, arguments));
    }

    private static Plane ResolvePortalPlane(
        CellPhysics cell,
        int portalIndex,
        PortalInfo portal)
    {
        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(CellTransit).GetMethod(
                "TryGetPortalPlane",
                BindingFlags.Static | BindingFlags.NonPublic));
        object?[] arguments = [cell, portalIndex, portal, default(Plane)];
        Assert.True(Assert.IsType<bool>(method.Invoke(null, arguments)));
        return Assert.IsType<Plane>(arguments[3]);
    }

    private static RetailBoxClassification ClassifyRetailBox(
        Plane plane,
        Vector3 min,
        Vector3 max,
        float epsilon)
    {
        Vector3[] corners =
        [
            new(min.X, min.Y, min.Z),
            new(max.X, max.Y, max.Z),
            new(min.X, min.Y, max.Z),
            new(min.X, max.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(max.X, min.Y, max.Z),
            new(min.X, max.Y, max.Z),
            new(max.X, max.Y, min.Z),
        ];
        var sides = new RetailPlaneSide[corners.Length];
        double epsilonTieMargin = double.MaxValue;
        for (int index = 0; index < corners.Length; index++)
        {
            double distance = DotWide(plane, corners[index]);
            Assert.True(double.IsFinite(distance));
            epsilonTieMargin = Math.Min(
                epsilonTieMargin,
                Math.Min(
                    Math.Abs(distance - epsilon),
                    Math.Abs(distance + epsilon)));
            sides[index] = distance > epsilon
                ? RetailPlaneSide.Positive
                : distance < -epsilon
                    ? RetailPlaneSide.Negative
                    : RetailPlaneSide.InPlane;
        }

        RetailPlaneSide result = sides[0] == RetailPlaneSide.InPlane
            ? RetailPlaneSide.Crossing
            : sides.Skip(1).Any(side => side != sides[0])
                ? RetailPlaneSide.Crossing
                : sides[0];
        return new RetailBoxClassification(result, sides, epsilonTieMargin);
    }

    private static double DotWide(Plane plane, Vector3 point) =>
        (double)plane.Normal.X * point.X
        + (double)plane.Normal.Y * point.Y
        + (double)plane.Normal.Z * point.Z
        + plane.D;

    private static string FormatFloat(float value) =>
        $"{value:R}/0x{BitConverter.SingleToUInt32Bits(value):X8}";

    private static string FormatVector(Vector3 value) =>
        $"({FormatFloat(value.X)},{FormatFloat(value.Y)},{FormatFloat(value.Z)})";

    private static string FormatQuaternion(Quaternion value) =>
        $"({FormatFloat(value.X)},{FormatFloat(value.Y)},{FormatFloat(value.Z)},{FormatFloat(value.W)})";

    private static string FormatMatrix(Matrix4x4 value) =>
        string.Join(',', new[]
        {
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44,
        }.Select(FormatFloat));

    private static ProductFacts AssertSemanticFacts(
        LandblockBuild build,
        EnvCellLandblockBuild envBuild,
        PhysicsEngine engine)
    {
        EnvCellShellPlacement shell = Assert.Single(
            envBuild.Shells,
            candidate => candidate.CellId == ShellCellId);
        Assert.NotEqual(0ul, shell.GeometryId);

        WorldEntity ramp = Assert.Single(
            build.Landblock.Entities,
            entity => !entity.IsBuildingShell
                && entity.SourceGfxObjOrSetupId == RampSetupId);
        Assert.Equal(7, ramp.MeshRefs.Count);
        Assert.True(engine.ShadowObjects.HasLogicalOwner(ramp.Id));
        Assert.True(engine.ShadowObjects.TryGetRetailCellArray(ramp.Id, out var rampCells));
        Assert.Equal<uint>([0xF4180112u, 0xF4180113u, 0xF4180009u], rampCells);
        Assert.Equal(
            RetailCellArrayRoute.BoundingBox,
            engine.ShadowObjects.GetRetailCellArrayRoute(ramp.Id));

        var rampEntries = new List<RetailPartEntry>();
        foreach (uint cellId in rampCells)
        {
            RetailPartEntry[] entries = engine.ShadowObjects
                .GetRetailPartEntriesInCell(cellId)
                .Where(entry => entry.EntityId == ramp.Id)
                .ToArray();
            Assert.Equal(7, entries.Length);
            Assert.Equal(Enumerable.Range(0, 7), entries.Select(entry => entry.PartIndex));
            Assert.All(entries, entry =>
            {
                Assert.Equal(cellId, entry.CellId);
                Assert.True(entry.ClipPlanesRequired);
            });
            rampEntries.AddRange(entries);
        }
        Assert.Equal(21, rampEntries.Count);

        HashSet<uint> ownerIds = build.Landblock.Entities
            .Select(entity => entity.Id)
            .ToHashSet();
        var cellDomain = new HashSet<uint>(
            envBuild.VisibilityCells.Select(cell => cell.CellId));
        foreach (WorldEntity entity in build.Landblock.Entities)
        {
            if (engine.ShadowObjects.TryGetRetailCellArray(entity.Id, out var cells))
                cellDomain.UnionWith(cells);
        }

        uint crossOwnerCell = 0u;
        foreach (uint cellId in cellDomain.Order())
        {
            IReadOnlyList<RetailPartEntry> entries =
                engine.ShadowObjects.GetRetailPartEntriesInCell(cellId);
            foreach (RetailPartEntry entry in entries)
            {
                Assert.Contains(entry.EntityId, ownerIds);
                Assert.True(
                    engine.ShadowObjects.TryGetRetailCellArray(entry.EntityId, out var ownerCells));
                Assert.Contains(cellId, ownerCells);
            }
            if (entries.Select(entry => entry.EntityId).Distinct().Skip(1).Any())
                crossOwnerCell = cellId;
        }
        Assert.NotEqual(0u, crossOwnerCell);

        Assert.Contains(build.Landblock.Entities, entity =>
            engine.ShadowObjects.TryGetRetailCellArray(entity.Id, out var cells)
            && cells.Count > 1);
        Assert.Contains(build.Landblock.Entities, entity =>
            engine.ShadowObjects.GetRetailCellArrayRoute(entity.Id)
                == RetailCellArrayRoute.BoundingBox);

        return new ProductFacts(ramp.Id, shell.GeometryId, crossOwnerCell);
    }

    private static (byte[] Bytes, GeometryCounts Counts) SerializeGeometry(
        LandblockBuild build,
        EnvCellLandblockBuild envBuild,
        IPreparedAssetSource meshSource)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteVersion(writer, "ACDREAM-C1A-GEOMETRY-V1");
        writer.Write(build.LandblockId);
        WriteFloat(writer, envBuild.WalkMaxZ);
        WriteFloat(writer, envBuild.WalkMinZ);

        int vertexCount = 0;
        int subsetCount = 0;
        int indexCount = 0;
        int setupPartCount = 0;

        writer.Write(envBuild.VisibilityCells.Length);
        foreach (LoadedCell cell in envBuild.VisibilityCells)
            WriteVisibilityCell(writer, cell);

        writer.Write(envBuild.Shells.Length);
        foreach (EnvCellShellPlacement shell in envBuild.Shells)
            WriteShellPlacement(writer, shell);

        writer.Write(build.Landblock.Entities.Count);
        foreach (WorldEntity entity in build.Landblock.Entities)
            WritePublishedEntity(writer, entity);

        writer.Write(envBuild.Shells.Length);
        foreach (EnvCellShellPlacement shell in envBuild.Shells)
        {
            writer.Write(shell.CellId);
            writer.Write(shell.GeometryId);
            PreparedAssetReadResult read = meshSource.Read(
                PreparedAssetRequest.EnvCellGeometry(
                    shell.CellId,
                    shell.GeometryId,
                    shell.EnvironmentId,
                    shell.CellStructure,
                    shell.Surfaces));
            ObjectMeshData mesh = RequireMesh(read, "EnvCell", shell.CellId);
            if (shell.CellId == ShellCellId)
            {
                Assert.NotEmpty(mesh.Vertices);
                var shellBatches = ObjectMeshManager.OrderedUploadBatches(mesh);
                Assert.NotEmpty(shellBatches);
                Assert.True(shellBatches.Sum(subset => subset.Batch.Indices.Count) > 0);
            }
            WriteMeshProduct(
                writer,
                mesh,
                cellShell: true,
                ref vertexCount,
                ref subsetCount,
                ref indexCount,
                ref setupPartCount);
        }

        uint[] setupIds = build.Landblock.Entities
            .Select(entity => entity.SourceGfxObjOrSetupId)
            .Where(id => (id & 0xFF000000u) == 0x02000000u)
            .Distinct()
            .ToArray();
        writer.Write(setupIds.Length);
        foreach (uint setupId in setupIds)
        {
            writer.Write(setupId);
            ObjectMeshData mesh = RequireMesh(
                meshSource.Read(PreparedAssetRequest.Setup(setupId)),
                "Setup",
                setupId);
            WriteMeshProduct(
                writer,
                mesh,
                cellShell: false,
                ref vertexCount,
                ref subsetCount,
                ref indexCount,
                ref setupPartCount);
        }

        uint[] gfxObjIds = build.Landblock.Entities
            .SelectMany(entity => entity.MeshRefs)
            .Select(mesh => mesh.GfxObjId)
            .Distinct()
            .ToArray();
        writer.Write(gfxObjIds.Length);
        foreach (uint gfxObjId in gfxObjIds)
        {
            writer.Write(gfxObjId);
            ObjectMeshData mesh = RequireMesh(
                meshSource.Read(PreparedAssetRequest.GfxObj(gfxObjId)),
                "GfxObj",
                gfxObjId);
            WriteMeshProduct(
                writer,
                mesh,
                cellShell: false,
                ref vertexCount,
                ref subsetCount,
                ref indexCount,
                ref setupPartCount);
        }

        return (
            stream.ToArray(),
            new GeometryCounts(
                envBuild.VisibilityCells.Length,
                envBuild.Shells.Length,
                build.Landblock.Entities.Count,
                setupIds.Length,
                gfxObjIds.Length,
                vertexCount,
                subsetCount,
                indexCount,
                setupPartCount));
    }

    private static (byte[] Bytes, MembershipCounts Counts) SerializeMembership(
        LandblockBuild build,
        EnvCellLandblockBuild envBuild,
        PhysicsEngine engine,
        ProductFacts facts)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteVersion(writer, "ACDREAM-C1A-MEMBERSHIP-V1");
        writer.Write(build.LandblockId);
        writer.Write(facts.RampEntityId);
        writer.Write(facts.ShellGeometryId);
        writer.Write(facts.CrossOwnerCellId);

        int logicalOwnerCount = 0;
        int retainedArrayCount = 0;
        int retainedCellCount = 0;
        var cellDomain = new HashSet<uint>(
            envBuild.VisibilityCells.Select(cell => cell.CellId));

        writer.Write(build.Landblock.Entities.Count);
        foreach (WorldEntity entity in build.Landblock.Entities)
        {
            writer.Write(entity.Id);
            bool hasLogicalOwner = engine.ShadowObjects.HasLogicalOwner(entity.Id);
            writer.Write(hasLogicalOwner);
            if (hasLogicalOwner)
                logicalOwnerCount++;

            bool hasRetailCellArray = engine.ShadowObjects.TryGetRetailCellArray(
                entity.Id,
                out IReadOnlyList<uint> cells);
            writer.Write(hasRetailCellArray);
            writer.Write((int)engine.ShadowObjects.GetRetailCellArrayRoute(entity.Id));
            writer.Write(cells.Count);
            foreach (uint cellId in cells)
            {
                writer.Write(cellId);
                cellDomain.Add(cellId);
            }
            if (hasRetailCellArray)
            {
                retainedArrayCount++;
                retainedCellCount += cells.Count;
            }
        }

        uint[] orderedCellDomain = cellDomain.Order().ToArray();
        int entryCount = 0;
        writer.Write(orderedCellDomain.Length);
        foreach (uint cellId in orderedCellDomain)
        {
            writer.Write(cellId);
            IReadOnlyList<RetailPartEntry> entries =
                engine.ShadowObjects.GetRetailPartEntriesInCell(cellId);
            writer.Write(entries.Count);
            foreach (RetailPartEntry entry in entries)
            {
                writer.Write(entry.EntityId);
                writer.Write(entry.PartIndex);
                writer.Write(entry.GfxObjId);
                writer.Write(entry.CellId);
                writer.Write(entry.ClipPlanesRequired);
                entryCount++;
            }
        }

        return (
            stream.ToArray(),
            new MembershipCounts(
                build.Landblock.Entities.Count,
                logicalOwnerCount,
                retainedArrayCount,
                retainedCellCount,
                orderedCellDomain.Length,
                entryCount));
    }

    private static byte[] SerializeCombined(byte[] geometry, byte[] membership)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteVersion(writer, "ACDREAM-C1A-COMBINED-V1");
        writer.Write(geometry.Length);
        writer.Write(geometry);
        writer.Write(membership.Length);
        writer.Write(membership);
        return stream.ToArray();
    }

    private static void WriteVisibilityCell(BinaryWriter writer, LoadedCell cell)
    {
        writer.Write(cell.CellId);
        WriteVector3(writer, cell.WorldPosition);
        WriteMatrix(writer, cell.WorldTransform);
        WriteMatrix(writer, cell.InverseWorldTransform);
        WriteVector3(writer, cell.LocalBoundsMin);
        WriteVector3(writer, cell.LocalBoundsMax);
        WriteNullableUInt32(writer, cell.BuildingId);

        writer.Write(cell.Portals.Count);
        foreach (CellPortalInfo portal in cell.Portals)
        {
            writer.Write(portal.OtherCellId);
            writer.Write(portal.PolygonId);
            writer.Write(portal.Flags);
            writer.Write(portal.OtherPortalId);
        }
        writer.Write(cell.ClipPlanes.Count);
        foreach (PortalClipPlane plane in cell.ClipPlanes)
        {
            WriteVector3(writer, plane.Normal);
            WriteFloat(writer, plane.D);
            writer.Write(plane.InsideSide);
        }
        writer.Write(cell.PortalPolygons.Count);
        foreach (Vector3[] polygon in cell.PortalPolygons)
        {
            writer.Write(polygon.Length);
            foreach (Vector3 vertex in polygon)
                WriteVector3(writer, vertex);
        }
        writer.Write(cell.VisibleCells.Count);
        foreach (uint visibleCell in cell.VisibleCells)
            writer.Write(visibleCell);
        writer.Write(cell.SeenOutside);
        writer.Write(cell.IsOutdoorNode);

        writer.Write(cell.Walk is not null);
        if (cell.Walk is not { } walk)
            return;
        writer.Write(walk.CellId);
        WriteMatrix(writer, walk.WorldTransform);
        WriteMatrix(writer, walk.InverseWorldTransform);
        writer.Write(walk.Portals.Length);
        foreach (WalkCellPortal portal in walk.Portals)
        {
            writer.Write(portal.OtherCellId);
            writer.Write(portal.PolygonIndex);
            writer.Write(portal.PortalSide);
            writer.Write(portal.OtherPortalId);
            writer.Write(portal.ExactMatch);
        }
        writer.Write(walk.PortalPolygons.Length);
        foreach (WalkPolygon polygon in walk.PortalPolygons)
        {
            writer.Write(polygon.Vertices.Length);
            foreach (Vector3 vertex in polygon.Vertices)
                WriteVector3(writer, vertex);
            WriteVector3(writer, polygon.Plane.Normal);
            WriteFloat(writer, polygon.Plane.D);
        }
        writer.Write(walk.StabList.Length);
        foreach (uint cellId in walk.StabList)
            writer.Write(cellId);
    }

    private static void WriteShellPlacement(BinaryWriter writer, EnvCellShellPlacement shell)
    {
        writer.Write(shell.CellId);
        writer.Write(shell.GeometryId);
        writer.Write(shell.EnvironmentId);
        writer.Write(shell.CellStructure);
        writer.Write(shell.Surfaces.Length);
        foreach (ushort surface in shell.Surfaces)
            writer.Write(surface);
        WriteVector3(writer, shell.WorldPosition);
        WriteQuaternion(writer, shell.Rotation);
        WriteMatrix(writer, shell.Transform);
        WriteVector3(writer, shell.LocalBounds.Min);
        WriteVector3(writer, shell.LocalBounds.Max);
        WriteVector3(writer, shell.WorldBounds.Min);
        WriteVector3(writer, shell.WorldBounds.Max);
    }

    private static void WritePublishedEntity(BinaryWriter writer, WorldEntity entity)
    {
        writer.Write(entity.Id);
        writer.Write(entity.SourceGfxObjOrSetupId);
        WriteVector3(writer, entity.Position);
        WriteQuaternion(writer, entity.Rotation);
        WriteFloat(writer, entity.Scale);
        WriteNullableUInt32(writer, entity.ParentCellId);
        WriteNullableUInt32(writer, entity.EffectCellId);
        writer.Write(entity.IsBuildingShell);
        WriteNullableUInt32(writer, entity.BuildingShellAnchorCellId);
        writer.Write(entity.MeshRefs.Count);
        foreach (MeshRef meshRef in entity.MeshRefs)
        {
            writer.Write(meshRef.GfxObjId);
            WriteMatrix(writer, meshRef.PartTransform);
        }
    }

    private static void WriteMeshProduct(
        BinaryWriter writer,
        ObjectMeshData mesh,
        bool cellShell,
        ref int vertexCount,
        ref int subsetCount,
        ref int indexCount,
        ref int setupPartCount)
    {
        writer.Write(mesh.ObjectId);
        writer.Write(mesh.IsSetup);
        writer.Write(mesh.Vertices.Length);
        foreach (VertexPositionNormalTexture vertex in mesh.Vertices)
        {
            WriteVector3(writer, vertex.Position);
            WriteVector3(writer, vertex.Normal);
            WriteVector2(writer, vertex.UV);
        }
        vertexCount += mesh.Vertices.Length;

        writer.Write(mesh.Batches.Count);
        foreach (MeshBatchData batch in mesh.Batches)
        {
            writer.Write(batch.Indices.Length);
            foreach (ushort index in batch.Indices)
                writer.Write(index);
            writer.Write(batch.TextureFormat.Width);
            writer.Write(batch.TextureFormat.Height);
            writer.Write((int)batch.TextureFormat.Format);
            WriteTextureKey(writer, batch.TextureKey);
            writer.Write(batch.TextureIndex);
            WriteNullableEnum(writer, batch.UploadPixelFormat);
            WriteNullableEnum(writer, batch.UploadPixelType);
            writer.Write((int)batch.CullMode);
            indexCount += batch.Indices.Length;
        }

        List<((int Width, int Height, TextureFormat Format) Format, TextureBatchData Batch)> subsets =
            cellShell
                ? ObjectMeshManager.OrderedUploadBatches(mesh)
                : mesh.TextureBatches
                    .OrderBy(pair => pair.Key.Width)
                    .ThenBy(pair => pair.Key.Height)
                    .ThenBy(pair => (int)pair.Key.Format)
                    .SelectMany(pair => pair.Value.Select(batch => (pair.Key, batch)))
                    .ToList();
        writer.Write(subsets.Count);
        foreach (var (format, batch) in subsets)
        {
            writer.Write(format.Width);
            writer.Write(format.Height);
            writer.Write((int)format.Format);
            WriteTextureKey(writer, batch.Key);
            WriteNullableEnum(writer, batch.UploadPixelFormat);
            WriteNullableEnum(writer, batch.UploadPixelType);
            writer.Write((int)batch.CullMode);
            writer.Write((int)batch.Translucency);
            writer.Write(batch.IsTransparent);
            writer.Write(batch.IsAdditive);
            writer.Write(batch.HasWrappingUVs);
            WriteFloat(writer, batch.SurfaceOpacity);
            writer.Write(batch.MaterialState.ToPackedByte());
            writer.Write(batch.SourceSurfaceIndex);
            writer.Write(batch.RetailSurfaceMask);
            writer.Write(batch.RawSurfaceType);
            writer.Write(batch.IsCellShell);
            writer.Write(batch.Indices.Count);
            foreach (ushort index in batch.Indices)
                writer.Write(index);
            subsetCount++;
            indexCount += batch.Indices.Count;
        }

        writer.Write(mesh.SetupParts.Count);
        foreach ((ulong gfxObjId, Matrix4x4 transform) in mesh.SetupParts)
        {
            writer.Write(gfxObjId);
            WriteMatrix(writer, transform);
        }
        setupPartCount += mesh.SetupParts.Count;

        WriteVector3(writer, mesh.BoundingBox.Min);
        WriteVector3(writer, mesh.BoundingBox.Max);
        WriteVector3(writer, mesh.SortCenter);
        writer.Write(mesh.DIDDegrade);
        writer.Write(mesh.SelectionSphere is not null);
        if (mesh.SelectionSphere is { } sphere)
        {
            WriteVector3(writer, sphere.Origin);
            WriteFloat(writer, sphere.Radius);
        }
        writer.Write(mesh.EdgeLines.Length);
        foreach (Vector3 edgeVertex in mesh.EdgeLines)
            WriteVector3(writer, edgeVertex);
    }

    private static ObjectMeshData RequireMesh(
        PreparedAssetReadResult result,
        string kind,
        uint sourceId)
    {
        Assert.Equal(PreparedAssetReadStatus.Loaded, result.Status);
        return Assert.IsType<ObjectMeshData>(result.Data);
    }

    private static Vector3 PublicationOrigin(LandblockBuildOrigin origin) => new(
        (((int)((LandblockId >> 24) & 0xFFu)) - origin.CenterX) * 192f,
        (((int)((LandblockId >> 16) & 0xFFu)) - origin.CenterY) * 192f,
        0f);

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteVersion(BinaryWriter writer, string value)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteNullableUInt32(BinaryWriter writer, uint? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(value.Value);
    }

    private static void WriteTextureKey(BinaryWriter writer, TextureKey key)
    {
        writer.Write(key.SurfaceId);
        writer.Write(key.PaletteId);
        writer.Write((int)key.Stippling);
        writer.Write(key.IsSolid);
    }

    private static void WriteNullableEnum<T>(BinaryWriter writer, T? value)
        where T : struct, Enum
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(Convert.ToInt32(value.Value));
    }

    private static void WriteVector2(BinaryWriter writer, Vector2 value)
    {
        WriteFloat(writer, value.X);
        WriteFloat(writer, value.Y);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        WriteFloat(writer, value.X);
        WriteFloat(writer, value.Y);
        WriteFloat(writer, value.Z);
    }

    private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
    {
        WriteFloat(writer, value.X);
        WriteFloat(writer, value.Y);
        WriteFloat(writer, value.Z);
        WriteFloat(writer, value.W);
    }

    private static void WriteMatrix(BinaryWriter writer, Matrix4x4 value)
    {
        WriteFloat(writer, value.M11);
        WriteFloat(writer, value.M12);
        WriteFloat(writer, value.M13);
        WriteFloat(writer, value.M14);
        WriteFloat(writer, value.M21);
        WriteFloat(writer, value.M22);
        WriteFloat(writer, value.M23);
        WriteFloat(writer, value.M24);
        WriteFloat(writer, value.M31);
        WriteFloat(writer, value.M32);
        WriteFloat(writer, value.M33);
        WriteFloat(writer, value.M34);
        WriteFloat(writer, value.M41);
        WriteFloat(writer, value.M42);
        WriteFloat(writer, value.M43);
        WriteFloat(writer, value.M44);
    }

    private static void WriteFloat(BinaryWriter writer, float value) =>
        writer.Write(BitConverter.SingleToInt32Bits(value));

    private readonly record struct Capture(
        byte[] GeometryBytes,
        byte[] MembershipBytes,
        string GeometryHash,
        string MembershipHash,
        string CombinedHash,
        ProductCounts Counts);

    private readonly record struct ProductFacts(
        uint RampEntityId,
        ulong ShellGeometryId,
        uint CrossOwnerCellId);

    private readonly record struct GeometryCounts(
        int VisibilityCells,
        int Shells,
        int PublishedEntities,
        int SetupMeshes,
        int GfxObjMeshes,
        int Vertices,
        int Subsets,
        int Indices,
        int SetupParts);

    private readonly record struct MembershipCounts(
        int PublishedEntities,
        int LogicalOwners,
        int RetainedCellArrays,
        int RetainedCellArrayCells,
        int CellDomain,
        int PartEntries);

    private readonly record struct ProductCounts(
        GeometryCounts Geometry,
        MembershipCounts Membership);

    private enum RetailPlaneSide
    {
        Positive = 0,
        Negative = 1,
        InPlane = 2,
        Crossing = 3,
    }

    private readonly record struct RetailBoxClassification(
        RetailPlaneSide Side,
        IReadOnlyList<RetailPlaneSide> CornerSides,
        double EpsilonTieMargin);

    private sealed class InstalledDatPreparedCollisionAdapter : IPreparedCollisionSource
    {
        private readonly IDatReaderWriter _dats;
        private long _probes;
        private long _reads;
        private long _loaded;
        private long _missing;

        public InstalledDatPreparedCollisionAdapter(IDatReaderWriter dats) =>
            _dats = dats;

        public PreparedCollisionSourceStats CollisionStats => new(
            _probes,
            _reads,
            _loaded,
            _missing,
            Corrupt: 0);

        public PreparedAssetPresence ProbeCollision(PakAssetType type, uint sourceFileId)
        {
            _probes++;
            bool available = type switch
            {
                PakAssetType.GfxObjCollision => _dats.Get<DatGfxObj>(sourceFileId) is not null,
                PakAssetType.SetupCollision => _dats.Get<DatSetup>(sourceFileId) is not null,
                PakAssetType.CellStructureCollision =>
                    TryResolveCell(sourceFileId, out _, out _),
                PakAssetType.EnvCellTopology =>
                    TryResolveCell(sourceFileId, out _, out _),
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
            return available
                ? PreparedAssetPresence.Available
                : PreparedAssetPresence.Missing;
        }

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            DatGfxObj? value = _dats.Get<DatGfxObj>(sourceFileId);
            if (value is null)
                return Missing<FlatGfxObjCollisionAsset>();
            return Loaded(FlatCollisionAssetBuilder.FlattenGfxObj(value));
        }

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            DatSetup? value = _dats.Get<DatSetup>(sourceFileId);
            if (value is null)
                return Missing<FlatSetupCollision>();
            return Loaded(FlatCollisionAssetBuilder.FlattenSetup(value));
        }

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            if (!TryResolveCell(sourceFileId, out _, out var structure))
                return Missing<FlatCellStructureCollisionAsset>();
            return Loaded(FlatCollisionAssetBuilder.FlattenCellStructure(structure));
        }

        public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            if (!TryResolveCell(sourceFileId, out DatEnvCell? cell, out var structure))
                return Missing<FlatEnvCellTopology>();
            FlatCellStructureCollisionAsset flat =
                FlatCollisionAssetBuilder.FlattenCellStructure(structure);
            return Loaded(FlatCollisionAssetBuilder.FlattenEnvCellTopology(
                sourceFileId,
                cell!,
                flat.PortalPolygons));
        }

        private bool TryResolveCell(
            uint sourceFileId,
            out DatEnvCell? cell,
            out DatReaderWriter.Types.CellStruct structure)
        {
            cell = _dats.Get<DatEnvCell>(sourceFileId);
            if (cell is not null
                && _dats.Get<DatEnvironment>(0x0D000000u | cell.EnvironmentId) is { } environment
                && environment.Cells.TryGetValue(cell.CellStructure, out var found)
                && found is not null)
            {
                structure = found;
                return true;
            }
            structure = null!;
            return false;
        }

        private PreparedCollisionReadResult<T> Loaded<T>(T value)
            where T : class
        {
            _loaded++;
            return PreparedCollisionReadResult<T>.Loaded(value);
        }

        private PreparedCollisionReadResult<T> Missing<T>()
            where T : class
        {
            _missing++;
            return PreparedCollisionReadResult<T>.Missing;
        }

        public void Dispose()
        {
        }
    }
}

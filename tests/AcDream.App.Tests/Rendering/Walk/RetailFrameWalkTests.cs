using System.Numerics;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class RetailFrameWalkTests
{
    private sealed class Recorder : IWalkEventSink
    {
        public readonly List<WalkEvent> Events = new();

        public readonly List<string> Combined = new();
        public int BuildingAlphaTurns;
        public readonly List<WalkBuildingSelection> ShellSelections = new();

        public void OnBuildingTurn(WalkBuilding building) => BuildingAlphaTurns++;

        public void OnBuildingShellTurn(
            WalkBuilding building,
            WalkBuildingSelection selection) => ShellSelections.Add(selection);

        public void Emit(in WalkEvent walkEvent)
        {
            Events.Add(walkEvent);
            Combined.Add(walkEvent.Kind switch
            {
                WalkEventKind.Landscape => "LS",
                WalkEventKind.Building => $"BLD:{walkEvent.CellId:x8}",
                WalkEventKind.DrawInside => $"DI:{walkEvent.CellId:x8}",
                WalkEventKind.DrawCells =>
                    $"DC:ov={walkEvent.OutsideViewCount}:{string.Join(',', walkEvent.Cells.Select(c => c.ToString("x8")))}",
                _ => "?",
            });
        }

        public void OnLandCellTurn(uint landblockId, int sideCellCount, int cellIndex)
            => Combined.Add(
                $"LC:{(landblockId & 0xFFFF0000u) | (uint)(cellIndex + 1):x8}");

        public void OnLandscapeCellTurn(uint cellId) => Combined.Add($"SC:{cellId:x8}");

        public void OnSortCellExit(uint landblockId, int sideCellCount, int cellIndex)
            => Combined.Add(
                $"SCX:{(landblockId & 0xFFFF0000u) | (uint)(cellIndex + 1):x8}");

        public string Signature()
            => string.Join("|", Events.Select(e => e.Kind switch
            {
                WalkEventKind.Landscape => "LS",
                WalkEventKind.Building => $"BLD:{e.CellId:x8}",
                WalkEventKind.DrawInside => $"DI:{e.CellId:x8}",
                WalkEventKind.DrawCells =>
                    $"DC:ov={e.OutsideViewCount}:{string.Join(',', e.Cells.Select(c => c.ToString("x8")))}",
                _ => "?",
            }));
    }

    private sealed class Caster : IWalkRayCaster
    {
        public Vector3 RayThrough(float screenX, float screenY)
            => new(screenX, screenY, 100f);
    }

    private class TestContext : IWalkFrameContext, IRetailFrameWalkContext
    {
        public readonly Dictionary<uint, WalkCell> Cells = new();
        private readonly Matrix4x4 _viewProj;

        public TestContext()
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(
                Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1f, 0.1f, 1000f);
            _viewProj = view * proj;
        }

        public Vector3 ViewpointIn(WalkCell cell) => Vector3.Zero;
        public Matrix4x4 ObjectToClip(WalkCell cell) => _viewProj;
        public WalkCell? GetVisible(uint cellId) => Cells.GetValueOrDefault(cellId);
        public IWalkRayCaster Rays { get; } = new Caster();
        public Vector3 WorldViewpoint => Vector3.Zero;
        public float ViewportWidth => 640f;
        public float ViewportHeight => 480f;

        public Vector3 ViewpointInBuilding(WalkBuilding building) => Vector3.Zero;
        public virtual float ViewerDistanceTo(WalkBuilding building) => 0f;
        public bool BuildingDegradesDisabled { get; set; }
        public bool KeepDistantBuildings { get; set; }
        public IWalkFrameContext CellContext => this;
        public WalkPlane CyPlane { get; set; } = new(new Vector3(0, 0, 1), 0f);
        public void SetActiveView(WalkPortalView views, int index) { }

        public int ClipBuildingPolygon(
            WalkBuilding building, WalkPolygon polygon, int side, Span<WalkScreenPoint> output)
            => 0;
    }

    private static WalkPolygon Quad(float z, bool facingViewer = true) => new()
    {
        Vertices =
        [
            new Vector3(-0.5f, -0.5f, z), new Vector3(0.5f, -0.5f, z),
            new Vector3(0.5f, 0.5f, z), new Vector3(-0.5f, 0.5f, z),
        ],
        Plane = new WalkPlane(new Vector3(0, 0, facingViewer ? 1f : -1f), facingViewer ? -z : z),
    };

    private static WalkLandscape Landscape3x3()
    {
        var landscape = new WalkLandscape
        {
            MidWidth = 3,
            Blocks = new WalkLandBlock?[9],
            ViewerBlockX = 1,
            ViewerBlockY = 1,
            ViewerCellX = 0,
            ViewerCellY = 0,
        };
        for (int i = 0; i < 9; i++)
        {
            landscape.Blocks[i] = new WalkLandBlock { MaxZ = 10f, MinZ = 0f };
            landscape.Blocks[i]!.EnsureCellArrays();
        }
        return landscape;
    }

    [Fact]
    public void Outdoor_frame_emits_landscape_then_buildings_far_to_near()
    {
        var ctx = new TestContext();
        WalkLandscape landscape = Landscape3x3();
        var ringBuilding = new WalkBuilding { PositionCellId = 0xAAAA0001 };
        var farBuilding = new WalkBuilding { PositionCellId = 0xBBBB0002 };
        var nearBuilding = new WalkBuilding { PositionCellId = 0xCCCC0003 };
        landscape.Blocks[0]!.CellBuildings[0] = ringBuilding;              // block (0,0)
        WalkLandBlock viewerBlock = landscape.Blocks[1 * 3 + 1]!;
        viewerBlock.CellBuildings[7 * 8 + 7] = farBuilding;
        viewerBlock.CellBuildings[0] = nearBuilding;
        var walk = new RetailFrameWalk();
        var recorder = new Recorder();

        // ViewCount == 0 exercises the CY-only visibility arm deterministically.
        walk.DrawLandscape(landscape, new WalkPortalView(), ctx, recorder);

        Assert.Equal(
            "LS|BLD:aaaa0001|BLD:bbbb0002|BLD:cccc0003",
            recorder.Signature());
    }

    [Fact]
    public void Degraded_building_still_emits_its_entry_event()
    {
        var ctx = new TestContext();
        var walk = new RetailFrameWalk();
        var recorder = new Recorder();
        var building = new WalkBuilding { PositionCellId = 0xF518002E, GfxObjId = 0 };

        walk.DrawBuilding(building, new WalkPortalView(), ctx, recorder);

        Assert.Equal("BLD:f518002e", recorder.Signature());
        Assert.Equal(0, recorder.BuildingAlphaTurns);
        Assert.Empty(recorder.ShellSelections);
    }

    [Fact]
    public void NullBspSelectedGfxStillRunsAlphaAndShellWithoutPortalWalk()
    {
        var building = new WalkBuilding
        {
            PositionCellId = 0xF518002Eu,
            GfxObjId = 0x01000001u,
            DrawingBsp = null,
        };
        var recorder = new Recorder();

        new RetailFrameWalk().DrawBuilding(
            building, new WalkPortalView(), new TestContext(), recorder);

        Assert.Equal("BLD:f518002e", recorder.Signature());
        Assert.Equal(1, recorder.BuildingAlphaTurns);
        WalkBuildingSelection selected = Assert.Single(recorder.ShellSelections);
        Assert.Equal(0x01000001u, selected.GfxObjId);
        Assert.Null(selected.DrawingBsp);
    }

    [Fact]
    public void SelectionUsesStrictThresholdAndFinalZeroIsCompleteBodyFailure()
    {
        var firstBsp = new WalkBspNode();
        var building = new WalkBuilding
        {
            GfxObjId = 0x0100FFFFu,
            DegradeLevels =
            [
                new(0x01000001u, 1u, 10f, 20f, 30f, firstBsp),
                new(0u, 1u, 30f, 40f, 50f, null),
            ],
        };

        Assert.Equal(0x01000001u, building.Select(29.999f, 0f, 1f).GfxObjId);
        WalkBuildingSelection equality = building.Select(30f, 0f, 1f);
        Assert.Equal(0u, equality.GfxObjId);
        Assert.Equal(1, equality.Level);

        var recorder = new Recorder();
        var context = new DistanceContext(80f);
        new RetailFrameWalk().DrawBuilding(
            building, new WalkPortalView(), context, recorder);
        Assert.Equal("BLD:00000000", recorder.Signature());
        Assert.Equal(0, recorder.BuildingAlphaTurns);
        Assert.Empty(recorder.ShellSelections);
    }

    [Fact]
    public void SelectionComparesStoredFloatEffectiveDistanceToWideThreshold()
    {
        const float multiplier = 0.0020020019728690386f;
        const float effective = 24.04804801940918f;
        Assert.Equal(0x41C06267u, BitConverter.SingleToUInt32Bits(effective));
        var building = new WalkBuilding
        {
            DegradeLevels =
            [
                new(0x01000001u, 1u, 0f, 24f, 48f, null),
                new(0x01000002u, 1u, 48f, 64f, 80f, null),
            ],
        };

        WalkBuildingSelection selected = building.Select(
            effective, degradeDistance: 0f, degradeMultiplier: multiplier);

        Assert.Equal(0, selected.Level);
        Assert.Equal(0x01000001u, selected.GfxObjId);
    }

    [Fact]
    public void DirectAndLadderSelectionsCarryExactIdBspLevelAndMode()
    {
        var directBsp = new WalkBspNode();
        var direct = new WalkBuilding
        {
            GfxObjId = 0x01000010u,
            DrawingBsp = directBsp,
        };
        Assert.Equal(
            new WalkBuildingSelection(0x01000010u, directBsp, 0, 1u),
            direct.Select(float.NaN, 50f, float.NaN));

        var nearBsp = new WalkBspNode();
        var middleBsp = new WalkBspNode();
        var ladder = new WalkBuilding
        {
            GfxObjId = 0x0100FFFFu,
            DegradeLevels =
            [
                new(0x01000011u, 3u, 10f, 20f, 30f, nearBsp),
                new(0x01000012u, 5u, 30f, 40f, 50f, middleBsp),
                new(0u, 7u, 50f, 60f, 70f, null),
            ],
        };

        Assert.Equal(
            new WalkBuildingSelection(0x01000011u, nearBsp, 0, 3u),
            ladder.Select(29f, 5f, 0.5f)); // effective 24 < positive threshold 25
        Assert.Equal(
            new WalkBuildingSelection(0x01000011u, nearBsp, 0, 3u),
            ladder.Select(-19f, 5f, -0.5f)); // effective 14 < negative threshold 15
        Assert.Equal(
            new WalkBuildingSelection(0x01000012u, middleBsp, 1, 5u),
            ladder.Select(30f, 5f, 0.5f)); // equality advances
        Assert.Equal(2, ladder.Select(float.PositiveInfinity, 5f, 0f).Level);
        Assert.Equal(2, ladder.Select(float.NegativeInfinity, 5f, 0f).Level);
        Assert.Equal(2, ladder.Select(float.NaN, 5f, 0f).Level);
    }

    [Fact]
    public void DisableAndForcedLevelBranchesPreserveRetailPrecedenceAndClamp()
    {
        var firstBsp = new WalkBspNode();
        var building = new WalkBuilding
        {
            DegradeLevels =
            [
                new(0x01000021u, 11u, 1f, 2f, 3f, firstBsp),
                new(0x01000022u, 12u, 3f, 4f, 5f, null),
                new(0u, 13u, 5f, 6f, 7f, null),
            ],
        };

        Assert.Equal(
            new WalkBuildingSelection(0x01000021u, firstBsp, 0, 11u),
            building.Select(float.PositiveInfinity, 50f, 1f,
                degradesDisabled: true, forcedLevel: 2));
        Assert.Equal(1, building.Select(0f, 50f, 0f, forcedLevel: 1).Level);
        WalkBuildingSelection clamped = building.Select(0f, 50f, 0f, forcedLevel: 99);
        Assert.Equal(2, clamped.Level);
        Assert.Equal(13u, clamped.Mode);
        Assert.Equal(0u, clamped.GfxObjId);
    }

    [Fact]
    public void KeepDistantBuildingsFallsBackToTheNearestLevelThatNamesAMesh()
    {
        var nearBsp = new WalkBspNode();
        var middleBsp = new WalkBspNode();
        var building = new WalkBuilding
        {
            DegradeLevels =
            [
                new(0x01000031u, 3u, 1f, 2f, 3f, nearBsp),
                new(0x01000032u, 5u, 3f, 4f, 5f, middleBsp),
                new(0u, 7u, 5f, 6f, 7f, null),
                new(0u, 9u, 7f, 8f, 9f, null),
            ],
        };

        // Far enough that the ladder ends on its last entry, which names no
        // mesh: the rule walks back to the nearest entry that does.
        Assert.Equal(
            new WalkBuildingSelection(0x01000032u, middleBsp, 1, 5u),
            building.Select(
                float.PositiveInfinity, 0f, 0f, keepDistantBuildings: true));

        // Off, the ladder is honoured exactly as authored.
        WalkBuildingSelection asAuthored =
            building.Select(float.PositiveInfinity, 0f, 0f);
        Assert.Equal(0u, asAuthored.GfxObjId);
        Assert.Equal(3, asAuthored.Level);
        Assert.Equal(9u, asAuthored.Mode);
        Assert.Null(asAuthored.DrawingBsp);
    }

    [Fact]
    public void KeepDistantBuildingsLeavesALadderWithoutABlankEntryAlone()
    {
        var nearBsp = new WalkBspNode();
        var farBsp = new WalkBspNode();
        var building = new WalkBuilding
        {
            DegradeLevels =
            [
                new(0x01000041u, 3u, 1f, 2f, 3f, nearBsp),
                new(0x01000042u, 5u, 3f, 4f, 5f, farBsp),
            ],
        };

        var expectedNear = new WalkBuildingSelection(0x01000041u, nearBsp, 0, 3u);
        var expectedFar = new WalkBuildingSelection(0x01000042u, farBsp, 1, 5u);

        Assert.Equal(expectedNear, building.Select(0f, 0f, 0f));
        Assert.Equal(expectedNear, building.Select(0f, 0f, 0f, keepDistantBuildings: true));
        Assert.Equal(expectedFar, building.Select(float.PositiveInfinity, 0f, 0f));
        Assert.Equal(
            expectedFar,
            building.Select(float.PositiveInfinity, 0f, 0f, keepDistantBuildings: true));
    }

    [Fact]
    public void KeepDistantBuildingsDrawsNothingWhenNoLevelNamesAMesh()
    {
        var building = new WalkBuilding
        {
            DegradeLevels =
            [
                new(0u, 3u, 1f, 2f, 3f, null),
                new(0u, 5u, 3f, 4f, 5f, null),
            ],
        };

        WalkBuildingSelection selection = building.Select(
            float.PositiveInfinity, 0f, 0f, keepDistantBuildings: true);
        Assert.Equal(0u, selection.GfxObjId);
        Assert.Equal(1, selection.Level);
    }

    [Fact]
    public void KeepDistantBuildingsReachesSelectionThroughTheWalkContext()
    {
        var nearBsp = new WalkBspNode();
        var building = new WalkBuilding
        {
            DegradeLevels =
            [
                new(0x01000051u, 3u, 1f, 2f, 3f, nearBsp),
                new(0u, 7u, 5f, 6f, 7f, null),
            ],
        };
        var walk = new RetailFrameWalk();
        var context = new DistanceContext(450f);

        var authored = new Recorder();
        walk.DrawBuilding(building, new WalkPortalView(), context, authored);
        Assert.Empty(authored.ShellSelections);

        context.KeepDistantBuildings = true;
        var kept = new Recorder();
        walk.DrawBuilding(building, new WalkPortalView(), context, kept);
        Assert.Equal(0x01000051u, Assert.Single(kept.ShellSelections).GfxObjId);

        // The overhead view still turns the whole ladder off: level 0 either way.
        context.BuildingDegradesDisabled = true;
        var overhead = new Recorder();
        walk.DrawBuilding(building, new WalkPortalView(), context, overhead);
        Assert.Equal(0x01000051u, Assert.Single(overhead.ShellSelections).GfxObjId);

        context.KeepDistantBuildings = false;
        context.BuildingDegradesDisabled = false;
        var restored = new Recorder();
        walk.DrawBuilding(building, new WalkPortalView(), context, restored);
        Assert.Empty(restored.ShellSelections);
    }

    private sealed class DistanceContext(float distance) : TestContext
    {
        public override float ViewerDistanceTo(WalkBuilding building) => distance;
    }

    [Fact]
    public void OverheadBuildingSelectionSurvivesTheFinalEmptyLevelAndRestoresDistanceSelection()
    {
        var building = new WalkBuilding
        {
            GfxObjId = 0x01000001u,
            DegradeLevels =
            [
                new(0x01000001u, 1u, 24f, 48f, 96f, null),
                new(0x01000002u, 1u, 192f, 392f, 392f, null),
                new(0u, 1u, float.MaxValue, float.MaxValue, float.MaxValue, null),
            ],
        };
        var walk = new RetailFrameWalk();
        var context = new DistanceContext(450f);
        var normal = new Recorder();
        walk.DrawBuilding(building, new WalkPortalView(), context, normal);
        Assert.Empty(normal.ShellSelections);

        context.BuildingDegradesDisabled = true;
        var overhead = new Recorder();
        walk.DrawBuilding(building, new WalkPortalView(), context, overhead);
        Assert.Equal(0x01000001u, Assert.Single(overhead.ShellSelections).GfxObjId);

        context.BuildingDegradesDisabled = false;
        var restored = new Recorder();
        walk.DrawBuilding(building, new WalkPortalView(), context, restored);
        Assert.Empty(restored.ShellSelections);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CoarseTerrainDrawsAllBuildingShellsWithinObjectRangeAndUpdatesOnRecenter(int ring)
    {
        const uint viewerCell = 0xA9B40001u;
        uint landblock = 0xA9000000u | ((uint)(0xB4 + ring) << 16);
        var entries = new[] { 1u, 2u, 64u }.Select((cell, index) =>
            new WalkBuildingFactory.Entry(
                new WalkBuilding { PositionCellId = landblock | cell, GfxObjId = 0x01000001u + (uint)index },
                Matrix4x4.Identity, Matrix4x4.Identity)).ToArray();
        var assembler = new WalkLandscapeAssembler();
        assembler.PublishLandblock(landblock, 10f, 0f, entries);
        assembler.SetViewer(viewerCell, Vector3.Zero);
        var walk = new RetailFrameWalk { ObjectRingLimit = ring };

        AssertShells(3);
        walk.ObjectRingLimit = ring - 1;
        AssertShells(0);
        walk.ObjectRingLimit = ring;

        assembler.SetViewer(landblock | 1u, Vector3.Zero);
        AssertShells(3);
        assembler.SetViewer(viewerCell, Vector3.Zero);
        AssertShells(3);
        assembler.ClearBuildings(landblock);
        AssertShells(0);

        void AssertShells(int count)
        {
            var recorder = new Recorder();
            walk.DrawLandscape(assembler.Landscape, new WalkPortalView(), new TestContext(), recorder);
            Assert.Equal(count, recorder.ShellSelections.Count);
            Assert.Equal(count, recorder.ShellSelections.Select(s => s.GfxObjId).Distinct().Count());
        }
    }


    [Fact]
    public void DrawLandscape_InterleavesLandCellTurnsWithBuildingAndObjectTurns_FarToNear()
    {
        var ctx = new TestContext
        {
            CyPlane = new WalkPlane(new Vector3(1, 0, 0), -30f),
        };
        var landscape = new WalkLandscape
        {
            MidWidth = 2,
            Blocks = new WalkLandBlock?[4],
            ViewerBlockX = 0,
            ViewerBlockY = 0,
            ViewerCellX = 0,
            ViewerCellY = 0,
        };

        var nearBlock = new WalkLandBlock
        {
            LandblockId = 0x11110000u, SideCellCount = 8, MaxZ = 10f, MinZ = 0f,
        };
        nearBlock.EnsureCellArrays();
        var building = new WalkBuilding { PositionCellId = 0x11110019u, GfxObjId = 0 };
        nearBlock.CellBuildings[3 * 8 + 0] = building;

        var farBlock = new WalkLandBlock
        {
            LandblockId = 0x22220000u, SideCellCount = 1, MaxZ = 10f, MinZ = 0f,
        };
        farBlock.EnsureCellArrays();

        landscape.Blocks[0] = nearBlock;
        landscape.Blocks[3] = farBlock;

        var walk = new RetailFrameWalk();
        var recorder = new Recorder();

        walk.DrawLandscape(landscape, new WalkPortalView(), ctx, recorder);

        Assert.Equal("LS", recorder.Combined[0]);

        const uint farCellId = 0x22220001u;
        int farLcIndex = recorder.Combined.IndexOf($"LC:{farCellId:x8}");
        int farScIndex = recorder.Combined.IndexOf($"SC:{farCellId:x8}");
        Assert.True(farLcIndex >= 0 && farScIndex > farLcIndex, "far block: LC before SC");

        var outsideIds = new List<uint>();
        var insideIds = new List<uint>();
        for (int cx = 0; cx < 8; cx++)
        for (int cy = 0; cy < 8; cy++)
        {
            uint cellId = 0x11110000u | (uint)(cx * 8 + cy + 1);
            (cx == 0 ? outsideIds : insideIds).Add(cellId);
        }

        int nearFirstIndex = insideIds.Concat(outsideIds)
            .Select(id => recorder.Combined.IndexOf($"SC:{id:x8}"))
            .Where(i => i >= 0)
            .Min();
        Assert.True(farScIndex < nearFirstIndex, "far block must draw before the near block");

        foreach (uint cellId in outsideIds)
        {
            Assert.Contains($"SC:{cellId:x8}", recorder.Combined);
            Assert.DoesNotContain($"LC:{cellId:x8}", recorder.Combined);
        }

        Assert.Equal(56, insideIds.Count);
        foreach (uint cellId in insideIds)
        {
            int lc = recorder.Combined.IndexOf($"LC:{cellId:x8}");
            int sc = recorder.Combined.IndexOf($"SC:{cellId:x8}");
            Assert.True(lc >= 0, $"cell 0x{cellId:x8} must have an LC turn");
            Assert.True(sc > lc, $"cell 0x{cellId:x8}: LC must precede SC");
        }

        const uint buildingCellId = 0x11110019u;
        int bldIndex = recorder.Combined.IndexOf($"BLD:{buildingCellId:x8}");
        int buildingLc = recorder.Combined.IndexOf($"LC:{buildingCellId:x8}");
        int buildingSc = recorder.Combined.IndexOf($"SC:{buildingCellId:x8}");
        Assert.True(bldIndex >= 0, "the building's own cell must emit BLD");
        Assert.True(buildingLc < bldIndex && bldIndex < buildingSc, "LC, then BLD, then SC");
    }

    [Fact]
    public void DrawLandscape_EmitsSortCellExitAfterEachCellsObjectTurnAndBeforeTheNextCellsLandTurn()
    {
        var ctx = new TestContext
        {
            CyPlane = new WalkPlane(new Vector3(1, 0, 0), -30f),
        };
        var landscape = new WalkLandscape
        {
            MidWidth = 2,
            Blocks = new WalkLandBlock?[4],
            ViewerBlockX = 0,
            ViewerBlockY = 0,
            ViewerCellX = 0,
            ViewerCellY = 0,
        };

        var nearBlock = new WalkLandBlock
        {
            LandblockId = 0x11110000u, SideCellCount = 8, MaxZ = 10f, MinZ = 0f,
        };
        nearBlock.EnsureCellArrays();
        var farBlock = new WalkLandBlock
        {
            LandblockId = 0x22220000u, SideCellCount = 1, MaxZ = 10f, MinZ = 0f,
        };
        farBlock.EnsureCellArrays();
        landscape.Blocks[0] = nearBlock;
        landscape.Blocks[3] = farBlock;

        var walk = new RetailFrameWalk();
        var recorder = new Recorder();
        walk.DrawLandscape(landscape, new WalkPortalView(), ctx, recorder);

        const uint farCellId = 0x22220001u;
        int farSc = recorder.Combined.IndexOf($"SC:{farCellId:x8}");
        int farScx = recorder.Combined.IndexOf($"SCX:{farCellId:x8}");
        Assert.True(farSc >= 0 && farScx == farSc + 1, "SCX must immediately follow its own cell's SC");

        int firstNearLc = -1;
        for (int i = 0; i < recorder.Combined.Count; i++)
        {
            if (recorder.Combined[i].StartsWith("LC:11110", StringComparison.Ordinal))
            {
                firstNearLc = i;
                break;
            }
        }
        Assert.True(firstNearLc > farScx, "the far cell's SCX must precede the near block's first LC");

        for (int cx = 0; cx < 8; cx++)
        for (int cy = 0; cy < 8; cy++)
        {
            uint cellId = 0x11110000u | (uint)(cx * 8 + cy + 1);
            int sc = recorder.Combined.IndexOf($"SC:{cellId:x8}");
            int scx = recorder.Combined.IndexOf($"SCX:{cellId:x8}");
            Assert.True(sc >= 0, $"cell 0x{cellId:x8} must have an SC turn");
            Assert.Equal(sc + 1, scx);
        }

        Assert.Equal(65, recorder.Combined.Count(e => e.StartsWith("SCX:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Interior_frame_without_exit_views_skips_the_landscape()
    {
        var ctx = new TestContext();
        var cell = new WalkCell
        {
            CellId = 0xA9B40178,
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0xA9B40179, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        ctx.Cells[cell.CellId] = cell;
        var walk = new RetailFrameWalk();
        var recorder = new Recorder();

        walk.WalkFrame(cell.CellId, cell, Landscape3x3(), ctx, recorder);

        Assert.Equal("DI:a9b40178|DC:ov=0:a9b40178", recorder.Signature());
    }

    [Fact]
    public void Interior_frame_with_an_exit_view_draws_the_landscape_through_it()
    {
        var ctx = new TestContext();
        var cell = new WalkCell
        {
            CellId = 0xA9B40150,
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0xFFFFFFFF, PolygonIndex = 0, PortalSide = 0, OtherPortalId = -1,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        ctx.Cells[cell.CellId] = cell;
        var walk = new RetailFrameWalk();
        var recorder = new Recorder();
        var landscape = new WalkLandscape
        {
            MidWidth = 1,
            Blocks = new WalkLandBlock?[1],
            ViewerBlockX = 0,
            ViewerBlockY = 0,
        };

        walk.WalkFrame(cell.CellId, cell, landscape, ctx, recorder);

        Assert.Equal("DI:a9b40150|DC:ov=1:a9b40150|LS", recorder.Signature());
    }

    [Fact]
    public void RetailFrameWalk_WiresTheOutdoorPViewWithDrawLandscapeFalse_AndTheInteriorPViewTrue()
    {
        var walk = new RetailFrameWalk();

        Assert.False(walk.OutdoorPView.DrawLandscape);
        Assert.True(walk.InteriorPView.DrawLandscape);
    }

    [Fact]
    public void Outdoor_camera_cell_roots_the_landscape_walk()
    {
        var ctx = new TestContext();
        var walk = new RetailFrameWalk();
        var recorder = new Recorder();

        // Low word < 0x100 = an outdoor landcell id.
        walk.WalkFrame(0xA9B40015, null, Landscape3x3(), ctx, recorder);

        Assert.StartsWith("LS", recorder.Signature());
    }

    [Fact]
    public void View_state_unwinds_completely_after_an_interior_frame()
    {
        var ctx = new TestContext();
        var stabCell = new WalkCell { CellId = 0xA9B40151 };
        var cell = new WalkCell
        {
            CellId = 0xA9B40150,
            StabList = [0xA9B40151u],
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0xA9B40151, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        stabCell.Portals = [new WalkCellPortal
        {
            OtherCellId = 0xA9B40150, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0,
        }];
        stabCell.PortalPolygons = [Quad(-2f)];
        ctx.Cells[cell.CellId] = cell;
        ctx.Cells[stabCell.CellId] = stabCell;
        var walk = new RetailFrameWalk();

        walk.WalkFrame(cell.CellId, cell, Landscape3x3(), ctx, new Recorder());

        Assert.Equal(0, cell.NumView);
        Assert.Equal(0, stabCell.NumView);
    }
}

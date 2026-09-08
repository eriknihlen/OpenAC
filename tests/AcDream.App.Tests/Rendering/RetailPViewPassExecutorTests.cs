using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using AcDream.App.Composition;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Sky;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Architecture;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailPViewPassExecutorTests
{
    [Fact]
    public void Extracted_contracts_retain_no_window_callbacks_or_visibility_owner()
    {
        Assert.DoesNotContain(
            typeof(RetailPViewFrameInput).GetProperties(),
            property => typeof(Delegate).IsAssignableFrom(property.PropertyType));

        FieldInfo[] fields = typeof(RetailPViewPassExecutor).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameWindow));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(CellVisibility));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(RetailPViewFrameInput));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(RetailPViewFrameResult));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(ClipFrameAssembly));
        Assert.DoesNotContain(
            fields,
            field => typeof(Delegate).IsAssignableFrom(field.FieldType));
    }

    [Fact]
    public void Concrete_executor_accumulates_walk_terrain_batch_timing()
    {
        MethodInfo landscape = typeof(RetailPViewPassExecutor).GetMethod(
            "DrawWalkLandCellBatch",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> landscapeCalls = CompiledCallGraph.Read(landscape);
        int terrainDraw = RequiredCallIndex(
            landscapeCalls,
            typeof(TerrainModernRenderer),
            nameof(TerrainModernRenderer.DrawLandCells));
        int accumulate = RequiredCallIndex(
            landscapeCalls,
            typeof(TerrainDrawDiagnosticsController),
            nameof(TerrainDrawDiagnosticsController.AccumulateWalkBatch));

        Assert.True(terrainDraw < accumulate);
        Assert.DoesNotContain(
            landscapeCalls,
            call => call.Target.DeclaringType == typeof(TerrainDrawDiagnosticsController)
                && call.Target.Name == nameof(TerrainDrawDiagnosticsController.Begin));
        Assert.DoesNotContain(
            landscapeCalls,
            call => call.Target.DeclaringType == typeof(TerrainDrawDiagnosticsController)
                && call.Target.Name == nameof(TerrainDrawDiagnosticsController.Complete));
    }

    [Fact]
    public void Concrete_executor_pushes_the_walk_terrain_frame_sample_at_replay_end()
    {
        MethodInfo drawWalkDrivenStatics = typeof(RetailPViewRenderer).GetMethod(
            "DrawWalkDrivenStatics",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(drawWalkDrivenStatics);
        int replay = RequiredCallIndex(
            calls,
            typeof(AcDream.App.Rendering.Walk.WalkFrameDriver),
            nameof(AcDream.App.Rendering.Walk.WalkFrameDriver.Replay));
        int completeWalkFrame = RequiredCallIndex(
            calls,
            typeof(RetailPViewPassExecutor),
            nameof(RetailPViewPassExecutor.CompleteWalkTerrainFrame));

        Assert.True(replay < completeWalkFrame);
    }

    [Fact]
    public void Frame_composition_constructs_one_walk_executor()
    {
        MethodInfo compose = typeof(FrameRootCompositionPhase).GetMethod(
            "ComposeCore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(compose);

        int executor = RequiredCallIndex(
            calls,
            typeof(RetailPViewPassExecutor),
            ".ctor");
        int renderer = RequiredCallIndex(
            calls,
            typeof(WorldScenePViewRenderer),
            ".ctor");

        Assert.True(executor < renderer);
        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(RetailPViewPassExecutor)
                && call.Target.Name == ".ctor");
        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(WorldScenePViewRenderer)
                && call.Target.Name == ".ctor");
    }

    [Fact]
    public void DrawWeatherOnce_DrawsTheWeatherMeshAndParticlesButNeverPrints()
    {
        MethodInfo method = typeof(RetailPViewPassExecutor).GetMethod(
            nameof(RetailPViewPassExecutor.DrawWeatherOnce),
            BindingFlags.Instance | BindingFlags.Public)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);

        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(SkyRenderer)
                && call.Target.Name == nameof(SkyRenderer.RenderWeather));
        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(ParticleRenderer)
                && call.Target.Name == nameof(ParticleRenderer.Draw));
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(WalkTranscriptDump));
    }

    [Fact]
    public void SubmitOrDrawTransparentCellShell_ScansRetainedBatchesBeforeProductionDispatch()
    {
        MethodInfo method = typeof(RetailPViewPassExecutor).GetMethod(
            "SubmitOrDrawTransparentCellShell",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);

        int detailProbe = RequiredCallIndex(
            calls, typeof(EnvCellRenderer), "get_TransparentDetailEnabled");
        int scan = RequiredCallIndex(
            calls, typeof(EnvCellRenderer), nameof(EnvCellRenderer.GetTransparentRoutes));
        int dispatch = RequiredCallIndex(
            calls, typeof(RetailPViewPassExecutor), nameof(RetailPViewPassExecutor.DispatchTransparentCellShell));

        Assert.True(detailProbe < scan);
        Assert.True(scan < dispatch);
    }

    [Fact]
    public void DrawLandscapeDynamicsPhase_CallsDrawWeatherOnceExactlyOnce()
    {
        MethodInfo method = typeof(RetailPViewRenderer).GetMethod(
            "DrawLandscapeDynamicsPhase",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);

        Assert.Single(
            calls,
            call => call.Target.DeclaringType == typeof(RetailPViewPassExecutor)
                && call.Target.Name == nameof(RetailPViewPassExecutor.DrawWeatherOnce));
    }

    [Fact]
    public void DrawLandscapeDynamicsPhase_DrawWeatherOnceCallSiteHasNoEnclosingBackwardBranch()
    {
        MethodInfo method = typeof(RetailPViewRenderer).GetMethod(
            "DrawLandscapeDynamicsPhase",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int callIndex = RequiredCallIndex(
            calls,
            typeof(RetailPViewPassExecutor),
            nameof(RetailPViewPassExecutor.DrawWeatherOnce));
        int callOffset = calls[callIndex].Offset;

        IReadOnlyList<CompiledBranch> branches = CompiledCallGraph.ReadBranches(method);
        Assert.DoesNotContain(
            branches,
            branch => branch.TargetOffset < branch.Offset
                && branch.TargetOffset <= callOffset
                && callOffset < branch.Offset);
    }

    [Fact]
    public void DrawLandscapeDynamicsPhase_GatesDrawWeatherOnceOnWalkDriverWeatherTurnFired()
    {
        MethodInfo method = typeof(RetailPViewRenderer).GetMethod(
            "DrawLandscapeDynamicsPhase",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int callIndex = RequiredCallIndex(
            calls,
            typeof(RetailPViewPassExecutor),
            nameof(RetailPViewPassExecutor.DrawWeatherOnce));
        Assert.True(callIndex > 0, "Expected a call before DrawWeatherOnce — the gate condition.");

        CompiledCall condition = calls[callIndex - 1];
        Assert.Equal(typeof(AcDream.App.Rendering.Walk.WalkFrameDriver), condition.Target.DeclaringType);
        Assert.Equal("get_WeatherTurnFired", condition.Target.Name);

        int conditionOffset = condition.Offset;
        int drawOffset = calls[callIndex].Offset;
        IReadOnlyList<CompiledBranch> branches = CompiledCallGraph.ReadBranches(method);
        Assert.Single(
            branches,
            branch => branch.Offset > conditionOffset
                && branch.Offset < drawOffset
                && (branch.OpCode == OpCodes.Brfalse || branch.OpCode == OpCodes.Brfalse_S)
                && branch.TargetOffset > drawOffset);
    }

    [Fact]
    public void DrawLandscapeDynamicsPhase_ExactlyOneBranchGuardsDrawWeatherOnce()
    {
        MethodInfo method = typeof(RetailPViewRenderer).GetMethod(
            "DrawLandscapeDynamicsPhase",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int particlesIndex = RequiredCallIndex(
            calls,
            typeof(RetailPViewPassExecutor),
            nameof(RetailPViewPassExecutor.DrawUnattachedSceneParticles));
        int drawIndex = RequiredCallIndex(
            calls,
            typeof(RetailPViewPassExecutor),
            nameof(RetailPViewPassExecutor.DrawWeatherOnce));
        int particlesOffset = calls[particlesIndex].Offset;
        int drawOffset = calls[drawIndex].Offset;

        IReadOnlyList<CompiledBranch> branches = CompiledCallGraph.ReadBranches(method);
        CompiledBranch onlyGuard = Assert.Single(
            branches,
            branch => branch.Offset > particlesOffset && branch.Offset < drawOffset);

        Assert.True(
            onlyGuard.OpCode == OpCodes.Brfalse || onlyGuard.OpCode == OpCodes.Brfalse_S,
            $"Expected the sole branch between DrawUnattachedSceneParticles and "
            + $"DrawWeatherOnce to be a brfalse, was {onlyGuard.OpCode}.");
        Assert.True(
            onlyGuard.TargetOffset > drawOffset,
            "Expected the guard branch to skip forward past DrawWeatherOnce.");
    }

    [Fact]
    public void DrawWalkSky_RenderSkyCallSiteHasNoEnclosingBackwardBranch()
    {
        MethodInfo method = typeof(RetailPViewPassExecutor).GetMethod(
            "DrawWalkSky",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int callIndex = RequiredCallIndex(
            calls,
            typeof(SkyRenderer),
            nameof(SkyRenderer.RenderSky));
        int callOffset = calls[callIndex].Offset;

        IReadOnlyList<CompiledBranch> branches = CompiledCallGraph.ReadBranches(method);
        Assert.DoesNotContain(
            branches,
            branch => branch.TargetOffset < branch.Offset
                && branch.TargetOffset <= callOffset
                && callOffset < branch.Offset);
    }

    [Theory]
    [InlineData(true, true, 0xF4180003u, true)]
    [InlineData(true, true, 0xA9B40100u, false)]   // player indoors (local id >= 0x100) -> no draw
    [InlineData(false, true, 0xF4180003u, false)]  // RenderSky off -> no draw
    [InlineData(true, false, 0xF4180003u, false)]  // RenderWeather off -> no draw
    public void ShouldDrawWeatherOnce_MatchesRetailIsPlayerOutsideGate(
        bool renderSky, bool renderWeather, uint playerCellId, bool expected)
    {
        Assert.Equal(
            expected,
            RetailPViewPassExecutor.ShouldDrawWeatherOnce(renderSky, renderWeather, playerCellId));
    }

    [Fact]
    public void DrawPortalDepthWrite_RejectsDegenerateLocalPolygons_BeforeTransformOrSubmission()
    {
        MethodInfo method = typeof(RetailPViewPassExecutor).GetMethod(
            "DrawPortalDepthWrite",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);

        int guardIndex = RequiredCallIndex(
            calls,
            typeof(AcDream.App.Rendering.Walk.WalkVisibilityMath),
            nameof(AcDream.App.Rendering.Walk.WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard));
        int transformIndex = RequiredCallIndex(calls, typeof(Vector3), nameof(Vector3.Transform));
        int drawIndex = RequiredCallIndex(
            calls, typeof(PortalDepthMaskRenderer), nameof(PortalDepthMaskRenderer.DrawDepthFan));

        Assert.True(
            guardIndex < transformIndex,
            "The boundary guard must run BEFORE the world-transform loop "
            + "(retail's reject -> transform -> clip -> count order).");
        Assert.True(
            guardIndex < drawIndex,
            "The boundary guard must run BEFORE the fan is submitted "
            + "(no draw, no `submitted` increment on a hit).");

        int guardOffset = calls[guardIndex].Offset;
        int transformOffset = calls[transformIndex].Offset;
        IReadOnlyList<CompiledBranch> branches = CompiledCallGraph.ReadBranches(method);
        Assert.Contains(
            branches,
            branch => branch.Offset > guardOffset
                && branch.Offset < transformOffset
                && (branch.OpCode == OpCodes.Brtrue || branch.OpCode == OpCodes.Brtrue_S
                    || branch.OpCode == OpCodes.Brfalse || branch.OpCode == OpCodes.Brfalse_S));
    }

    [Fact]
    public void DrawExitPortalMask_CountsAnUnclippableTwoVertexPolygon_ButDrawsNothing()
    {
        var cell = new LoadedCell
        {
            CellId = 0xA9B40105u,
            WorldTransform = Matrix4x4.Identity,
        };
        cell.Portals.Add(new CellPortalInfo(OtherCellId: 0xFFFF, PolygonId: 0, Flags: 0, OtherPortalId: 0));
        cell.PortalPolygons.Add(
        [
            new Vector3(1f, 1f, 0f),
            new Vector3(2f, 1f, 0f),
        ]);

        using var device = new RecordingGpuDevice();
        var frames = new GpuDeviceFrameLifetime(device);
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        using var portalDepthMask = new PortalDepthMaskRenderer(device, frames, scope);
        var executor = (RetailPViewPassExecutor)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(RetailPViewPassExecutor));
        typeof(RetailPViewPassExecutor)
            .GetField("_portalDepthMask", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(executor, portalDepthMask);
        var root = new LoadedCell { CellId = 0xF4180003u, IsOutdoorNode = false };
        RetailPViewFrameInput frame = new RetailPViewFrameInput().Reset(
            rootCell: root,
            nearbyBuildingCells: null,
            viewerEyePos: Vector3.Zero,
            viewProjection: Matrix4x4.Identity,
            cells: new SingleCellSource(cell),
            camera: null!,
            cameraWorldPosition: Vector3.Zero,
            frustum: null,
            playerLandblockId: null,
            animatedEntityIds: null,
            renderCenterLbX: 0,
            renderCenterLbY: 0,
            renderRadius: 0,
            landblockEntries: Array.Empty<(uint, Vector3, Vector3,
                IReadOnlyList<AcDream.Core.World.WorldEntity>,
                IReadOnlyDictionary<uint, AcDream.Core.World.WorldEntity>?)>(),
            renderSky: false,
            renderWeather: false,
            dayFraction: 0f,
            activeDayGroup: null,
            skyKeyframe: default,
            environOverrideActive: false,
            viewerCellId: 0,
            playerCellId: 0,
            playerViewPosition: Vector3.Zero,
            cameraView: Matrix4x4.Identity,
            cameraCellResolution: default);

        int drawsBefore = device.Calls.OfType<GpuRecordedDraw>().Count();
        int submitted = executor.DrawExitPortalMask(frame, cell.CellId, ReadOnlySpan<Vector4>.Empty);
        int drawsAfter = device.Calls.OfType<GpuRecordedDraw>().Count();

        Assert.Equal(1, submitted);
        Assert.Equal(drawsBefore, drawsAfter);
    }

    private sealed class SingleCellSource(LoadedCell cell) : IRetailPViewCellSource
    {
        public LoadedCell? Find(uint cellId) => cellId == cell.CellId ? cell : null;
    }

    private static int RequiredCallIndex(
        IReadOnlyList<CompiledCall> calls,
        Type declaringType,
        string methodName)
    {
        int index = CompiledCallGraph.IndexOf(calls, declaringType, methodName);
        Assert.True(
            index >= 0,
            $"Expected call to {declaringType.Name}.{methodName}.");
        return index;
    }
}

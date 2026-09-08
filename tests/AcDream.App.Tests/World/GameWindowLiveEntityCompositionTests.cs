using System.Collections;
using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Tests.Architecture;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Physics;

namespace AcDream.App.Tests.World;

public sealed class GameWindowLiveEntityCompositionTests
{
    private const BindingFlags PrivateImplementation =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData("RegisterLiveEntityCollision")]
    [InlineData("MotionTableDefaultPose")]
    [InlineData("LeaveWorldLiveEntityRuntimeComponents")]
    [InlineData("WithdrawLiveEntityWorldProjection")]
    [InlineData("RebindAnimatedEntityForAppearance")]
    [InlineData("OnLiveEntitySpawned")]
    [InlineData("OnLiveEntitySpawnedLocked")]
    [InlineData("RehydrateServerEntitiesForLandblock")]
    [InlineData("TryInitializeLiveCenter")]
    [InlineData("CompleteLiveEntityReady")]
    [InlineData("OnLiveAppearanceUpdated")]
    [InlineData("OnLiveEntityPickedUp")]
    [InlineData("OnLiveParentUpdated")]
    [InlineData("TryAcceptParentForRender")]
    [InlineData("OnLiveEntityDeleted")]
    [InlineData("OnLiveEntityPruned")]
    [InlineData("TearDownLiveEntityRuntimeComponents")]
    [InlineData("CreateLiveEntityRuntimeTeardownPlan")]
    [InlineData("RouteSameGenerationCreateObject")]
    [InlineData("OnLiveMotionUpdated")]
    [InlineData("OnLivePositionUpdated")]
    [InlineData("OnLiveVectorUpdated")]
    [InlineData("OnLiveStateUpdated")]
    [InlineData("EnsureRemoteMotionBindings")]
    [InlineData("ResolvePhysicsHost")]
    [InlineData("GetSetupCylinder")]
    [InlineData("RouteServerMoveTo")]
    [InlineData("StickToObjectFromWire")]
    [InlineData("ClearTargetForHiddenEntity")]
    [InlineData("ApplyServerControlledVelocityCycle")]
    [InlineData("RunRemoteTeleportHook")]
    [InlineData("SendImmediateLocalPositionEvent")]
    [InlineData("DispatchRemoteInboundMotion")]
    [InlineData("CreateRemoteMotion")]
    [InlineData("WillAdvanceRemoteMotion")]
    [InlineData("AdvanceLiveObjectRuntime")]
    [InlineData("AdvanceLiveObjectRuntimeCore")]
    [InlineData("ReconcileLiveObjectSpatialPresentation")]
    [InlineData("CaptureAnimationHooks")]
    public void GameWindow_DoesNotReacquireExtractedLiveEntityBodies(string methodName)
    {
        Assert.Null(typeof(GameWindow).GetMethod(methodName, PrivateImplementation));
    }

    [Fact]
    public void GameWindow_DoesNotOwnAnAppearanceUpdateStateType()
    {
        Assert.Null(typeof(GameWindow).GetNestedType(
            "AppearanceUpdateState",
            BindingFlags.NonPublic));
    }

    [Fact]
    public void RetainedButWithdrawnWorldEntity_RequiresPositionProjectionRecovery()
    {
        LiveEntityRecord record =
            LiveEntityTestFixture.CreateExactProjectionRecord(
                new WorldSession.EntitySpawn(
                    Guid: 0x7000_00D0u,
                    Position: null,
                    SetupTableId: null,
                    AnimPartChanges: [],
                    TextureChanges: [],
                    SubPalettes: [],
                    BasePaletteId: null,
                    ObjScale: null,
                    Name: null,
                    ItemType: null,
                    MotionState: null,
                    MotionTableId: null,
                    InstanceSequence: 1));
        record.WorldEntity = new WorldEntity
        {
            Id = record.LocalEntityId!.Value,
            ServerGuid = record.ServerGuid,
            SourceGfxObjOrSetupId = 0x0200_0001u,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            MeshRefs = [],
        };
        Assert.NotNull(record.WorldEntity);
        Assert.False(record.IsSpatiallyProjected);

        Assert.True(
            LiveEntityNetworkUpdateController
                .RequiresSpatialProjectionRecovery(record));
    }

    [Theory]
    [InlineData(typeof(LiveEntityCollisionBuilder))]
    [InlineData(typeof(LiveEntityDefaultPoseResolver))]
    [InlineData(typeof(LiveEntityProjectionWithdrawalController))]
    [InlineData(typeof(LocalPlayerShadowState))]
    [InlineData(typeof(LiveEntityAppearanceBinding))]
    [InlineData(typeof(LiveEntityHydrationController))]
    [InlineData(typeof(DatLiveEntityProjectionMaterializer))]
    [InlineData(typeof(LiveEntityRuntimeTeardownController))]
    [InlineData(typeof(LiveEntityNetworkUpdateController))]
    [InlineData(typeof(LiveEntityMotionRuntimeController))]
    [InlineData(typeof(LiveEntityInboundAuthorityGate))]
    [InlineData(typeof(DeferredLiveEntityMotionRuntimeBindings))]
    [InlineData(typeof(AcDream.App.Update.LiveObjectFrameController))]
    [InlineData(typeof(AcDream.App.Update.LiveEffectFrameController))]
    [InlineData(typeof(AcDream.App.Update.LiveSpatialPresentationReconciler))]
    [InlineData(typeof(AcDream.App.Streaming.StreamingFrameController))]
    [InlineData(typeof(LiveEntityAnimationPresenter))]
    [InlineData(typeof(LiveAnimationPresentationContext))]
    [InlineData(typeof(StaticLiveRootCommitter))]
    [InlineData(typeof(RetailLiveFrameCoordinator))]
    public void ExtractedHelpers_DoNotOwnGuidIndexesOrBackendState(Type helperType)
    {
        foreach (FieldInfo field in helperType.GetFields(
                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic))
        {
            bool isExactSynchronousProjectionContext =
                helperType == typeof(LiveEntityHydrationController)
                && field.Name == "_projectionOperations"
                && field.FieldType.IsGenericType
                && field.FieldType.GetGenericArguments()[0]
                    == typeof(AcDream.Runtime.Entities.RuntimeEntityRecord);
            if (!isExactSynchronousProjectionContext)
            {
                Assert.False(
                    typeof(IDictionary).IsAssignableFrom(field.FieldType),
                    $"{helperType.Name}.{field.Name} must resolve identity through LiveEntityRuntime.");
            }
            string typeName = field.FieldType.FullName ?? field.FieldType.Name;
            Assert.DoesNotContain("Silk.NET", typeName, StringComparison.Ordinal);
            Assert.DoesNotContain("OpenGL", typeName, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(typeof(GameWindow), field.FieldType);
        }
    }

    [Fact]
    public void ExtractedUpdateOwners_DoNotRetainAnonymousCallbacks()
    {
        Type[] owners =
        [
            typeof(AcDream.App.Update.LiveObjectFrameController),
            typeof(AcDream.App.Update.LiveEffectFrameController),
            typeof(AcDream.App.Update.LiveSpatialPresentationReconciler),
            typeof(AcDream.App.Streaming.StreamingFrameController),
            typeof(LiveEntityAnimationScheduler),
            typeof(LiveEntityAnimationPresenter),
            typeof(LiveAnimationPresentationContext),
            typeof(StaticLiveRootCommitter),
            typeof(RetailLiveFrameCoordinator),
        ];

        foreach (Type owner in owners)
        {
            Assert.DoesNotContain(
                owner.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        }

        Assert.False(typeof(ILiveAnimationPresentationContext).IsAssignableFrom(
            typeof(GameWindow)));
    }

    [Fact]
    public void RuntimeViewsAndProjectileController_RetainTypedSourcesNotWindowClosures()
    {
        FieldInfo animationRuntime = Assert.Single(
            typeof(LiveEntityAnimationRuntimeView<LiveEntityAnimationState>)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_runtime");
        Assert.Equal(typeof(ILiveEntityRuntimeSource), animationRuntime.FieldType);

        FieldInfo[] projectileFields = typeof(ProjectileController).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(
            typeof(IProjectileSetupResolver),
            Assert.Single(projectileFields, field => field.Name == "_setupResolver").FieldType);
        Assert.Equal(
            typeof(IEntityRootPosePublisher),
            Assert.Single(projectileFields, field => field.Name == "_rootPoses").FieldType);
        Assert.Equal(
            typeof(LiveWorldOriginState),
            Assert.Single(projectileFields, field => field.Name == "_origin").FieldType);
        Assert.Equal(
            typeof(RuntimeProjectilePhysicsUpdater),
            Assert.Single(
                projectileFields,
                field => field.Name == "_runtimeUpdater").FieldType);
        Assert.DoesNotContain(
            projectileFields,
            field => field.FieldType == typeof(PhysicsEngine)
                || field.FieldType == typeof(ProjectilePhysicsStepper)
                || field.FieldType.IsGenericType
                    && field.FieldType.GetGenericTypeDefinition()
                        == typeof(Dictionary<,>));
        Assert.DoesNotContain(
            projectileFields,
            field => typeof(Delegate).IsAssignableFrom(field.FieldType)
                     && !field.Name.Contains("DiagnosticSink", StringComparison.Ordinal));

        FieldInfo[] windowFields = typeof(GameWindow).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(
            typeof(LiveEntityAnimationRuntimeView<LiveEntityAnimationState>),
            Assert.Single(windowFields, field => field.Name == "_animatedEntities").FieldType);
        Assert.DoesNotContain(
            windowFields,
            field => field.FieldType.Name.Contains(
                "LiveEntityRemoteMotionRuntimeView",
                StringComparison.Ordinal));

        MethodInfo compose = typeof(LivePresentationCompositionPhase).GetMethod(
            "ComposeCore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(compose);
        int resolver = RequiredCallIndex(
            calls,
            typeof(DatProjectileSetupResolver),
            ".ctor");
        int controller = RequiredCallIndex(
            calls,
            typeof(ProjectileController),
            ".ctor");
        Assert.True(resolver < controller);
    }

    [Fact]
    public void SessionReset_CallsPlayerModeNetworkAndWorldOriginOwners()
    {
        MethodInfo resetPlayer = typeof(LiveSessionRuntimeFactory).GetMethod(
            "ResetPlayerPresentation",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> playerCalls = CompiledCallGraph.Read(resetPlayer);
        Assert.True(RequiredCallIndex(
            playerCalls,
            typeof(PlayerModeController),
            nameof(PlayerModeController.ResetSession)) >= 0);

        MethodInfo resetMode = typeof(PlayerModeController).GetMethod(
            nameof(PlayerModeController.ResetSession))!;
        IReadOnlyList<CompiledCall> modeCalls = CompiledCallGraph.Read(resetMode);
        Assert.True(RequiredCallIndex(
            modeCalls,
            typeof(LocalPlayerModeState),
            nameof(LocalPlayerModeState.ResetSession)) >= 0);

        MethodInfo resetIdentity = typeof(LiveSessionRuntimeFactory).GetMethod(
            "ResetIdentityPresentation",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> identityCalls = CompiledCallGraph.Read(resetIdentity);
        int network = RequiredCallIndex(
            identityCalls,
            typeof(LiveEntityNetworkUpdateController),
            nameof(LiveEntityNetworkUpdateController.ResetSessionState));
        int origin = RequiredCallIndex(
            identityCalls,
            typeof(LiveWorldOriginState),
            nameof(LiveWorldOriginState.Reset));
        Assert.True(network < origin);
    }

    private static int RequiredCallIndex(
        IReadOnlyList<CompiledCall> calls,
        Type declaringType,
        string methodName)
    {
        int index = CompiledCallGraph.IndexOf(calls, declaringType, methodName);
        Assert.True(index >= 0, $"Missing compiled call: {declaringType.Name}.{methodName}");
        return index;
    }
}

using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Tests.Support;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests;

public sealed class NoWindowGameRuntimeHostTests
{
    [Fact]
    public void RootHostRunsPopulatedGenerationWithoutPresentationAssembly()
    {
        var host = new NoWindowGameRuntimeHost();

        RuntimeSessionStartResult started = host.Start();
        Assert.Equal(RuntimeSessionStartStatus.Connected, started.Status);
        Assert.Equal(0x50000001u, host.Runtime.PlayerIdentity.ServerGuid);
        Assert.True(host.Deliver(runtime =>
        {
            runtime.EntityObjects.RegisterEntity(
                Spawn(0x70000001u, 1));
            runtime.EntityObjects.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = 0x80000001u,
                Name = "fixture",
                ContainerId = runtime.PlayerIdentity.ServerGuid,
            });
            runtime.CommunicationOwner.Chat.OnSystemMessage(
                "fixture",
                1u);
        }));
        host.Advance(1d / 30d);
        RuntimeStateCheckpoint populated =
            host.Runtime.CaptureCheckpoint();
        Assert.Equal(1, populated.EntityCount);
        Assert.Equal(1, populated.InventoryObjectCount);
        Assert.Equal(1, populated.ChatCount);

        RuntimeTeardownAcknowledgement stopped = host.Stop();

        Assert.True(stopped.IsComplete);
        Assert.Equal(0u, host.Runtime.PlayerIdentity.ServerGuid);
        Assert.Equal(0, host.Runtime.Entities.Count);
        Assert.Equal(0, host.Runtime.Inventory.ObjectCount);
        Assert.False(host.Deliver(_ =>
            throw new InvalidOperationException("old route leaked")));
        Assert.False(host.Execute(_ =>
            throw new InvalidOperationException("old command leaked")));
        Assert.Equal(1, host.ProjectionRetirementCount);
        Assert.Equal(2, host.ProjectionDrainCount);
        Assert.Equal(2, host.ProjectionCompletionCount);

        string[] loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(static assembly =>
                assembly.GetName().Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(loaded, static name =>
            name.StartsWith(
                "AcDream.App",
                StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(
                "AcDream.UI.",
                StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(
                "Silk.NET",
                StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(
                "OpenAL",
                StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(
                "Arch",
                StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(
                "ImGui",
                StringComparison.OrdinalIgnoreCase));

        host.Dispose();
        Assert.True(host.Runtime.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void ReconnectReusesRootButNotPriorGuidIncarnationOrRoutes()
    {
        const uint guid = 0x70000001u;
        var host = new NoWindowGameRuntimeHost();
        RuntimeSessionStartResult first = host.Start();
        RuntimeGenerationToken firstGeneration = first.Generation;
        Assert.True(host.Deliver(runtime =>
            runtime.EntityObjects.RegisterEntity(Spawn(guid, 1))));
        Assert.True(host.Runtime.EntityObjects.Entities.TryGetActive(
            guid,
            out RuntimeEntityRecord firstRecord));

        RuntimeSessionStartResult second = host.Reconnect();
        RuntimeGenerationToken secondGeneration = second.Generation;

        Assert.Equal(RuntimeSessionStartStatus.Connected, second.Status);
        Assert.NotEqual(firstGeneration, secondGeneration);
        Assert.False(host.Runtime.EntityObjects.Entities.TryGetActive(
            guid,
            out _));
        Assert.True(host.Deliver(runtime =>
            runtime.EntityObjects.RegisterEntity(Spawn(guid, 2))));
        Assert.True(host.Runtime.EntityObjects.Entities.TryGetActive(
            guid,
            out RuntimeEntityRecord secondRecord));
        Assert.NotSame(firstRecord, secondRecord);
        Assert.Equal((ushort)2, secondRecord.Incarnation);
        Assert.Equal(0, host.Runtime.EntityObjects.Entities
            .PendingTeardownCount);
        Assert.Contains(
            $"events-:{firstGeneration.Value}",
            host.LifecycleTrace);
        Assert.Contains(
            $"commands-:{firstGeneration.Value}",
            host.LifecycleTrace);

        host.Dispose();
        Assert.True(host.Runtime.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void DeterministicLifecycleUsesOneRootAcrossGameplayPortalAndReconnect()
    {
        const uint localGuid = 0x50000001u;
        const uint reusedGuid = 0x70000001u;
        const uint usableGuid = 0x70000002u;
        const uint containerGuid = 0x80000001u;
        const uint itemGuid = 0x80000002u;
        const string passwordMarker = "credential-must-not-appear";
        var host = new NoWindowGameRuntimeHost(
            password: passwordMarker);

        RuntimeStateCheckpoint empty = host.CaptureCheckpoint();
        Assert.Equal(RuntimeLifecycleState.Constructed, empty.Lifecycle);
        Assert.Equal(0, empty.EntityCount);
        Assert.Equal(0, empty.InventoryObjectCount);
        Assert.Equal(0, empty.ChatCount);

        RuntimeSessionStartResult first = host.Start();
        RuntimeGenerationToken firstGeneration = first.Generation;
        Assert.Equal(RuntimeSessionStartStatus.Connected, first.Status);
        Assert.True(host.Deliver(firstGeneration, runtime =>
        {
            runtime.EntityObjects.RegisterEntity(Spawn(localGuid, 1));
            runtime.EntityObjects.RegisterEntity(Spawn(reusedGuid, 1));
            runtime.EntityObjects.RegisterEntity(Spawn(usableGuid, 1));
            runtime.EntityObjects.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = containerGuid,
                Name = "fixture pack",
                ContainerId = localGuid,
            });
            runtime.EntityObjects.Objects.AddContainer(new Container
            {
                ObjectId = containerGuid,
                Capacity = 24,
            });
            runtime.EntityObjects.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = itemGuid,
                Name = "fixture item",
                ContainerId = localGuid,
                StackSize = 3,
                StackSizeMax = 10,
            });
            Assert.True(runtime.EntityObjects.Objects.ApplyServerMove(
                itemGuid,
                containerGuid,
                newWielderId: 0u,
                newSlot: 0));
            runtime.CommunicationOwner.Chat.OnSystemMessage(
                "fixture system",
                1u);
            runtime.CommunicationOwner.Chat.OnLocalSpeech(
                "Runtime",
                "fixture speech",
                localGuid,
                isRanged: false,
                logTextType: 0x02u);
            runtime.CharacterOwner.LocalPlayer.OnVitalUpdate(
                vitalId: 1u,
                ranks: 100u,
                start: 100u,
                xp: 50u,
                current: 95u);
            runtime.EnvironmentOwner.Initialize(
                new RuntimeWorldEnvironmentDefinition(
                    originOffsetTicks: 0d,
                    sourceTickSize: 1d,
                    lightTickSize: 1d,
                    dayGroups: null));
            runtime.EnvironmentOwner.SynchronizeFromServer(1234d);
        }));

        Assert.True(host.Move());
        Assert.True(host.Use(usableGuid));
        Assert.True(host.Attack());
        Assert.True(host.CastFixtureSpell());
        Assert.True(host.AdvanceProjectile());
        Assert.True(host.CompletePortal());
        host.Advance(1d / 30d);

        RuntimeStateCheckpoint populated = host.CaptureCheckpoint();
        Assert.Equal(RuntimeLifecycleState.InWorld, populated.Lifecycle);
        Assert.Equal(4, populated.EntityCount);
        Assert.Equal(2, populated.InventoryObjectCount);
        Assert.Equal(1, populated.InventoryContainerCount);
        Assert.Equal(2, populated.ChatCount);
        Assert.Equal(1, populated.Character.LearnedSpellCount);
        Assert.True(populated.Movement.AutoRunActive);
        Assert.True(populated.Portal.Completed);
        Assert.True(populated.Portal.WorldViewportObserved);
        Assert.Equal(0, populated.TransitOwnership.HostProjectionCount);

        Assert.Equal(
            [
                "movement:run",
                $"use:{usableGuid:X8}",
                "attack:prepare",
                "attack:Medium:0.5",
                "cast:stop",
                "cast:1",
                "cast:busy",
                "projectile:ack",
            ],
            host.GameplayTrace);
        RuntimeTraceEntry[] firstTrace = host.Trace.Entries
            .Where(entry =>
                entry.Stamp.Generation == firstGeneration)
            .ToArray();
        Assert.Contains(
            firstTrace,
            static entry => entry.Kind == RuntimeTraceKind.Lifecycle);
        Assert.Contains(
            firstTrace,
            static entry => entry.Kind == RuntimeTraceKind.Inventory
                && entry.Code == (int)RuntimeInventoryChange.Moved);
        Assert.Contains(
            firstTrace,
            static entry => entry.Kind == RuntimeTraceKind.Movement);
        Assert.Contains(
            firstTrace,
            static entry => entry.Kind == RuntimeTraceKind.Command
                && entry.Code >> 16
                    == (int)RuntimeCommandDomain.Magic);
        Assert.Equal(
            5,
            firstTrace.Count(static entry =>
                entry.Kind == RuntimeTraceKind.Portal));
        AssertStrictlyOrdered(firstTrace);

        Assert.True(host.Runtime.EntityObjects.Entities.TryGetActive(
            reusedGuid,
            out RuntimeEntityRecord firstRecord));
        RuntimeSessionStartResult second = host.Reconnect();
        RuntimeGenerationToken secondGeneration = second.Generation;
        Assert.Equal(RuntimeSessionStartStatus.Connected, second.Status);
        Assert.NotEqual(firstGeneration, secondGeneration);
        Assert.False(host.Deliver(
            firstGeneration,
            _ => throw new InvalidOperationException(
                "retired inbound route ran")));
        Assert.False(host.Execute(
            firstGeneration,
            _ => throw new InvalidOperationException(
                "retired command route ran")));

        RuntimeStateCheckpoint reset = host.Runtime.CaptureCheckpoint();
        Assert.Equal(0, reset.EntityCount);
        Assert.Equal(0, reset.InventoryObjectCount);
        Assert.Equal(2, reset.ChatCount);
        Assert.Equal(0, reset.Character.LearnedSpellCount);
        Assert.Equal(0u, reset.Actions.SelectedObjectId);
        Assert.Equal(RuntimePortalSnapshot.Idle, reset.Portal);
        Assert.False(reset.Movement.AutoRunActive);
        Assert.True(reset.EnvironmentOwnership.IsInitialized);

        Assert.True(host.Deliver(secondGeneration, runtime =>
            runtime.EntityObjects.RegisterEntity(
                Spawn(reusedGuid, 2))));
        Assert.True(host.Runtime.EntityObjects.Entities.TryGetActive(
            reusedGuid,
            out RuntimeEntityRecord secondRecord));
        Assert.NotSame(firstRecord, secondRecord);
        Assert.Equal((ushort)2, secondRecord.Incarnation);
        RuntimeStateCheckpoint secondCheckpoint =
            host.CaptureCheckpoint();
        Assert.Equal(1, secondCheckpoint.EntityCount);
        Assert.Equal(0, secondCheckpoint.InventoryObjectCount);
        Assert.Equal(2, secondCheckpoint.ChatCount);

        Assert.True(host.Stop().IsComplete);
        AssertStrictlyOrdered(
            host.Trace.Entries
                .Where(entry =>
                    entry.Stamp.Generation == secondGeneration)
                .ToArray());
        Assert.DoesNotContain(
            host.LifecycleTrace.Concat(host.GameplayTrace),
            line => line.Contains(
                passwordMarker,
                StringComparison.Ordinal));

        host.Dispose();
        Assert.True(host.Runtime.CaptureOwnership().IsConverged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingPortalCancellationRetiresHostAcknowledgementsBeforeReconnect(
        bool destinationReady)
    {
        var host = new NoWindowGameRuntimeHost();
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        RuntimeWorldTransitState transit = host.Runtime.TransitOwner;
        const uint destinationCell = 0x8A020164u;
        const ushort sequence = 7;

        Assert.True(transit.TryQueueTeleportStart(sequence));
        Assert.True(transit.ActivateQueuedTeleport());
        Assert.True(transit.OfferTeleportDestination(
            new RuntimeTeleportDestination(
                host.Runtime.PlayerIdentity.ServerGuid,
                InstanceSequence: 1,
                PositionSequence: 1,
                TeleportSequence: sequence,
                ForcePositionSequence: 1,
                Position: new Position(
                    destinationCell,
                    Vector3.Zero,
                    Quaternion.Identity)),
            teleportTimestampAdvanced: true));
        Assert.True(transit.TryBeginPortalReveal(
            sequence,
            destinationCell,
            out long revealGeneration));
        Assert.True(transit.TryRegisterHostProjection(
            revealGeneration,
            destinationCell,
            out RuntimeWorldHostProjectionToken projection));
        Assert.True(transit.AcknowledgeHostProjection(
            new RuntimeWorldHostAcknowledgement(
                projection,
                RuntimeWorldHostAcknowledgementStage
                    .ProjectionRegistered)));
        if (destinationReady)
        {
            Assert.True(transit.AcknowledgeDestinationReadiness(
                new RuntimeDestinationReadiness(
                    revealGeneration,
                    destinationCell,
                    IsIndoor: true,
                    IsUnhydratable: false,
                    RequiredRenderRadius: 0,
                    IsRenderNeighborhoodReady: true,
                    AreCompositeTexturesReady: true,
                    IsCollisionReady: true)));
        }

        Assert.True(transit.Cancel(revealGeneration));
        DrainPendingPortal(transit, projection);
        Assert.Equal(0, transit.Ownership.HostProjectionCount);

        RuntimeSessionStartResult reconnected = host.Reconnect();

        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            reconnected.Status);
        Assert.Equal(RuntimePortalSnapshot.Idle, transit.Snapshot);
        host.Dispose();
        Assert.True(host.Runtime.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void ConcurrentRootsKeepCredentialsClockStateAndFailuresIsolated()
    {
        const string firstPassword = "first-password-marker";
        const string secondPassword = "second-password-marker";
        const uint sharedGuid = 0x70000055u;
        var first = new NoWindowGameRuntimeHost(
            user: "first-user",
            password: firstPassword,
            characterId: 0x50000011u,
            characterName: "First");
        var second = new NoWindowGameRuntimeHost(
            user: "second-user",
            password: secondPassword,
            characterId: 0x50000022u,
            characterName: "Second");

        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            first.Start().Status);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            second.Start().Status);
        Assert.True(first.Deliver(runtime =>
        {
            runtime.EntityObjects.RegisterEntity(Spawn(sharedGuid, 1));
            runtime.EntityObjects.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = 0x80000011u,
                Name = "first item",
                ContainerId = runtime.PlayerIdentity.ServerGuid,
            });
            runtime.CommunicationOwner.Chat.OnSystemMessage(
                "first chat",
                1u);
        }));
        Assert.True(second.Deliver(runtime =>
        {
            runtime.EntityObjects.RegisterEntity(Spawn(sharedGuid, 1));
            runtime.EntityObjects.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = 0x80000022u,
                Name = "second item",
                ContainerId = runtime.PlayerIdentity.ServerGuid,
            });
            runtime.CommunicationOwner.Chat.OnSystemMessage(
                "second chat",
                1u);
        }));
        Assert.True(first.CastFixtureSpell());
        Assert.True(second.CastFixtureSpell());
        Assert.True(first.AdvanceProjectile(0x70000011u));
        Assert.True(second.AdvanceProjectile(0x70000022u));
        Assert.True(first.CompletePortal(0x8A020164u, sequence: 1));
        Assert.True(second.CompletePortal(0x11340021u, sequence: 2));
        first.Advance(0.1d);
        first.Advance(0.2d);
        second.Advance(0.4d);

        Assert.True(first.Runtime.EntityObjects.Entities.TryGetActive(
            sharedGuid,
            out RuntimeEntityRecord firstRecord));
        Assert.True(second.Runtime.EntityObjects.Entities.TryGetActive(
            sharedGuid,
            out RuntimeEntityRecord secondRecord));
        Assert.NotSame(firstRecord, secondRecord);
        Assert.Null(first.Runtime.EntityObjects.Objects.Get(0x80000022u));
        Assert.Null(second.Runtime.EntityObjects.Objects.Get(0x80000011u));
        Assert.Equal(
            "first chat",
            Assert.Single(
                first.Runtime.CommunicationOwner.Chat.Snapshot()).Text);
        Assert.Equal(
            "second chat",
            Assert.Single(
                second.Runtime.CommunicationOwner.Chat.Snapshot()).Text);
        Assert.Equal(2UL, first.Runtime.Clock.FrameNumber);
        Assert.Equal(1UL, second.Runtime.Clock.FrameNumber);
        Assert.NotEqual(
            first.Runtime.Clock.SimulationTimeSeconds,
            second.Runtime.Clock.SimulationTimeSeconds);
        Assert.Equal(
            0x8A020164u,
            first.Runtime.TransitOwner.Snapshot.DestinationCell);
        Assert.Equal(
            0x11340021u,
            second.Runtime.TransitOwner.Snapshot.DestinationCell);
        Assert.DoesNotContain(
            first.LifecycleTrace.Concat(first.GameplayTrace),
            line => line.Contains(
                firstPassword,
                StringComparison.Ordinal)
                || line.Contains(
                    secondPassword,
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            second.LifecycleTrace.Concat(second.GameplayTrace),
            line => line.Contains(
                firstPassword,
                StringComparison.Ordinal)
                || line.Contains(
                    secondPassword,
                    StringComparison.Ordinal));

        first.Dispose();
        second.Dispose();
        Assert.True(first.Runtime.CaptureOwnership().IsConverged);
        Assert.True(second.Runtime.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void ThrowingRootObserverIsContainedAndDoesNotBlockTeardown()
    {
        var host = new NoWindowGameRuntimeHost();
        using IDisposable failure =
            host.Runtime.Subscribe(new ThrowingObserver());
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        Assert.True(host.Deliver(runtime =>
        {
            runtime.EntityObjects.RegisterEntity(
                Spawn(0x70000077u, 1));
            runtime.CommunicationOwner.Chat.OnSystemMessage(
                "observer fixture",
                1u);
        }));
        Assert.True(host.Move());
        Assert.True(host.CompletePortal());
        Assert.True(
            host.Runtime.CaptureOwnership()
                .Events.DispatchFailureCount >= 4);

        Assert.True(host.Stop().IsComplete);
        failure.Dispose();
        host.Dispose();
        Assert.True(host.Runtime.CaptureOwnership().IsConverged);
    }

    private static void AssertStrictlyOrdered(
        IReadOnlyList<RuntimeTraceEntry> entries)
    {
        for (int index = 1; index < entries.Count; index++)
        {
            Assert.True(
                entries[index].Stamp.Sequence
                    > entries[index - 1].Stamp.Sequence,
                $"trace sequence {entries[index].Stamp.Sequence} "
                + $"did not follow {entries[index - 1].Stamp.Sequence}");
        }
    }

    private static void DrainPendingPortal(
        RuntimeWorldTransitState transit,
        RuntimeWorldHostProjectionToken projection)
    {
        while (transit.TryGetHostProjection(
            projection,
            out RuntimeWorldHostProjectionSnapshot pending))
        {
            RuntimeWorldHostAcknowledgementStage stages =
                pending.PendingAcknowledgements;
            RuntimeWorldHostAcknowledgementStage stage =
                (stages & RuntimeWorldHostAcknowledgementStage
                    .SimulationReleaseProjected) != 0
                    ? RuntimeWorldHostAcknowledgementStage
                        .SimulationReleaseProjected
                    : (stages & RuntimeWorldHostAcknowledgementStage
                        .DestinationReservationReleased) != 0
                        ? RuntimeWorldHostAcknowledgementStage
                            .DestinationReservationReleased
                        : RuntimeWorldHostAcknowledgementStage
                            .TerminalProjected;
            Assert.NotEqual(
                RuntimeWorldHostAcknowledgementStage.None,
                stages);
            Assert.True(transit.AcknowledgeHostProjection(
                new RuntimeWorldHostAcknowledgement(
                    projection,
                    stage)));
        }
    }

    private sealed class ThrowingObserver : IRuntimeEventObserver
    {
        public void OnLifecycle(in RuntimeLifecycleDelta delta) =>
            throw new InvalidOperationException("observer");
        public void OnCommand(in RuntimeCommandDelta delta) =>
            throw new InvalidOperationException("observer");
        public void OnEntity(in RuntimeEntityDelta delta) =>
            throw new InvalidOperationException("observer");
        public void OnInventory(in RuntimeInventoryDelta delta) =>
            throw new InvalidOperationException("observer");
        public void OnChat(in RuntimeChatDelta delta) =>
            throw new InvalidOperationException("observer");
        public void OnMovement(in RuntimeMovementDelta delta) =>
            throw new InvalidOperationException("observer");
        public void OnPortal(in RuntimePortalDelta delta) =>
            throw new InvalidOperationException("observer");
        public void OnCombat(in RuntimeCombatDelta delta) =>
            throw new InvalidOperationException("observer");
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort incarnation)
    {
        var position = new CreateObject.ServerPosition(
            0x01010001u,
            10f,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: incarnation);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            guid.ToString("X8"),
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}

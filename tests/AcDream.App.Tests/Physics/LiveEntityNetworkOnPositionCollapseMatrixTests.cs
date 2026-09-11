using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityNetworkOnPositionCollapseMatrixTests
{
    private const uint SourceLandblock = 0xB1000000u;
    private const uint SourceCell = SourceLandblock | 0x0001u;
    private const uint DestinationLandblock = 0xB2000000u;
    private const uint DestinationCell = DestinationLandblock | 0x0001u;
    private static readonly Vector3 DestinationWorldOffset = new(192f, 0f, 0f);

    private const uint PlayerGuid = 0x50007001u;

    private const uint CreatureGuid = 0x80007001u;

    private const float SpawnHeight = 7f;
    private const float FootSphereCenterLift = 0.48f;

    private static bool IsPlayer(uint guid) => guid == PlayerGuid;


    [Theory]
    [InlineData(PlayerGuid)]
    [InlineData(CreatureGuid)]
    public void WithdrawnProjection_AcceptedPositionRestoresBucketAndWireCell(
        uint guid)
    {
        using var fixture = new Fixture(guid, decliningMaterializer: true);
        Assert.True(fixture.Runtime.TryGetRecord(guid, out LiveEntityRecord record));
        Assert.True(record.IsSpatiallyProjected);
        Assert.Equal(SourceCell, record.FullCellId);

        // The withdrawal: render bucket gone, logical record + WorldEntity
        // retained. This is what makes RequiresSpatialProjectionRecovery true
        // on the next accepted Position.
        Assert.True(fixture.Runtime.WithdrawLiveEntityProjection(guid));
        Assert.False(record.IsSpatiallyProjected);
        Assert.NotNull(record.WorldEntity);
        Assert.Equal(SourceCell, record.FullCellId);

        const uint WireCell = SourceLandblock | 0x0002u;
        fixture.Controller.OnPosition(fixture.Update(
            new Vector3(13f, 15f, SpawnHeight),
            WireCell,
            teleportSequence: 1,
            guid: guid));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            guid, out RuntimeEntityRecord canonical));
        Assert.Equal(
            WireCell,
            canonical.Snapshot.Position!.Value.LandblockId);
        Assert.True(fixture.MaterializerDeclined);

        Assert.True(record.IsSpatiallyProjected);
        Assert.Equal(WireCell, record.FullCellId);
        Assert.Equal(WireCell, canonical.FullCellId);
        Assert.Equal(WireCell, fixture.Entity.ParentCellId);
    }


    [Theory]
    [InlineData(PlayerGuid)]
    [InlineData(CreatureGuid)]
    public void TeleportCommit_BothGuids_ArmsOnceAndNeverInstallsVelocity(uint guid)
    {
        using var fixture = new Fixture(guid);
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);
        EntityPhysicsHost host = fixture.InstallHost();
        Assert.Null(host.PositionManager.Constraint);
        var destination = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            destination, DestinationCell, teleportSequence: 5, guid: guid));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            guid, out RuntimeEntityRecord canonical));
        Assert.NotNull(canonical.PhysicsBody);
        PhysicsBody body = canonical.PhysicsBody!;
        Vector3 resolved = destination + DestinationWorldOffset
            + new Vector3(0f, 0f, FootSphereCenterLift);
        Assert.Equal(resolved, body.Position);
        Assert.Equal(body.Position, fixture.Entity.Position);
        Assert.Equal(DestinationCell, fixture.Entity.ParentCellId);
        Assert.Equal(DestinationCell, canonical.FullCellId);

        // The leash armed exactly once (D4: teleport arms on every outcome).
        Assert.NotNull(host.PositionManager.Constraint);

        Assert.False(fixture.Remote.HasServerVelocity);
        Assert.Equal(Vector3.Zero, fixture.Remote.ServerVelocity);

        // The shadow published at the resolved position.
        ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(body.Position, shadowEntry.Position);

        fixture.DrainPlacementFifo();
    }

    // ── Scenario 2: landing packet (the preserved rows 2a/2b asymmetry) ─

    [Fact]
    public void LandingPacket_PlayerGuid_QueueClearedAndShadowPublished()
    {
        using var fixture = new Fixture(PlayerGuid);
        EntityPhysicsHost host = fixture.InstallHost();
        Assert.Null(host.PositionManager.Constraint);
        fixture.Remote.Body.TransientState = TransientStateFlags.Active;
        fixture.Remote.Interp.Enqueue(
            new Vector3(1f, 1f, SpawnHeight),
            Quaternion.Identity,
            isMovingTo: false,
            currentBodyPosition: new Vector3(50f, 50f, SpawnHeight),
            currentBodyOrientation: Quaternion.Identity);
        Assert.True(fixture.Remote.Interp.IsActive);
        Vector3 spawnShadowPos = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id).Position;
        var landingPos = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            landingPos, SourceCell, teleportSequence: 1,
            guid: PlayerGuid, isGrounded: true));

        // Body snapped to the landing pose; entity synced from the resolved
        // body; armed exactly once.
        Assert.Equal(landingPos, fixture.Remote.Body.Position);
        Assert.Equal(landingPos, fixture.Entity.Position);
        Assert.Equal(SourceCell, fixture.Entity.ParentCellId);
        Assert.NotNull(host.PositionManager.Constraint);

        Assert.False(fixture.Remote.Interp.IsActive);

        ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(landingPos, shadowEntry.Position);
        Assert.NotEqual(spawnShadowPos, shadowEntry.Position);

        Assert.Equal(SourceCell, fixture.Remote.CellId);
    }

    [Fact]
    public void LandingPacket_CreatureGuid_ShadowPublishedQueueNotCleared()
    {
        using var fixture = new Fixture(CreatureGuid);
        EntityPhysicsHost host = fixture.InstallHost();
        Assert.Null(host.PositionManager.Constraint);
        fixture.Remote.Body.TransientState = TransientStateFlags.Active;
        fixture.Remote.Interp.Enqueue(
            new Vector3(1f, 1f, SpawnHeight),
            Quaternion.Identity,
            isMovingTo: false,
            currentBodyPosition: new Vector3(50f, 50f, SpawnHeight),
            currentBodyOrientation: Quaternion.Identity);
        Assert.True(fixture.Remote.Interp.IsActive);
        var landingPos = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            landingPos, SourceCell, teleportSequence: 1,
            guid: CreatureGuid, isGrounded: true));

        Assert.Equal(landingPos, fixture.Remote.Body.Position);
        Assert.Equal(landingPos, fixture.Entity.Position);
        Assert.NotNull(host.PositionManager.Constraint);

        Assert.True(fixture.Remote.Interp.IsActive);

        ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(landingPos, shadowEntry.Position);
    }


    [Theory]
    [InlineData(PlayerGuid)]
    [InlineData(CreatureGuid)]
    public void WireAirborneNullClassified_BothGuids_WritesOnlyPlacementBookkeeping(
        uint guid)
    {
        using var fixture = new Fixture(guid, nullClassification: true);
        EntityPhysicsHost host = fixture.InstallHost();
        Assert.Null(host.PositionManager.Constraint);
        Vector3 spawnBodyPose = fixture.Remote.Body.Position;
        var wirePos = new Vector3(50f, 50f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            wirePos, SourceCell, teleportSequence: 1,
            guid: guid, isGrounded: false));

        // No body write.
        Assert.Equal(spawnBodyPose, fixture.Remote.Body.Position);
        // No shadow republish.
        ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(spawnBodyPose, shadowEntry.Position);
        Assert.Null(host.PositionManager.Constraint);

        // Positive half: the placement bookkeeping writes did happen.
        Assert.Equal(SourceCell, fixture.Remote.CellId);
        Assert.Equal(wirePos, fixture.Remote.LastServerPos);
        Assert.NotEqual(0d, fixture.Remote.LastServerPosTime);
    }

    // ── Scenario 4: airborne no-op (NoPositionOperation, non-null route) ─

    [Theory]
    [InlineData(PlayerGuid)]
    [InlineData(CreatureGuid)]
    public void AirborneNoOperation_BothGuids_WritesOnlyPlacementBookkeepingNoArm(
        uint guid)
    {
        using var fixture = new Fixture(guid);
        EntityPhysicsHost host = fixture.InstallHost();
        Assert.Null(host.PositionManager.Constraint);
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        Vector3 spawnBodyPose = fixture.Remote.Body.Position;
        var wirePos = new Vector3(50f, 50f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            wirePos, SourceCell, teleportSequence: 1,
            guid: guid, isGrounded: false));

        Assert.Equal(spawnBodyPose, fixture.Remote.Body.Position);
        ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(spawnBodyPose, shadowEntry.Position);
        Assert.Null(host.PositionManager.Constraint);

        Assert.Equal(SourceCell, fixture.Remote.CellId);
        Assert.Equal(wirePos, fixture.Remote.LastServerPos);
        Assert.NotEqual(0d, fixture.Remote.LastServerPosTime);
    }

    // ── Scenario 5: near interpolate ─────────────────────────────────────

    [Theory]
    [InlineData(PlayerGuid)]
    [InlineData(CreatureGuid)]
    public void NearInterpolate_BothGuids_EnqueuesAndArmsOnce(uint guid)
    {
        using var fixture = new Fixture(guid);
        EntityPhysicsHost host = fixture.InstallHost();
        Assert.Null(host.PositionManager.Constraint);
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        fixture.Remote.Body.Position = new Vector3(2f, 2f, SpawnHeight);
        fixture.Remote.LastServerPos = fixture.Remote.Body.Position;
        fixture.Remote.LastServerPosTime =
            (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - 0.15;
        var target = new Vector3(4f, 3f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            target, SourceCell, teleportSequence: 1,
            guid: guid, isGrounded: true));

        Assert.Equal(new Vector3(2f, 2f, SpawnHeight), fixture.Remote.Body.Position);
        Assert.True(fixture.Remote.Interp.IsActive);
        Assert.NotNull(host.PositionManager.Constraint);
        Assert.Equal(SourceCell, fixture.Remote.CellId);
        // The render entity tracks the (unchanged) body, not the wire pose —
        // route 4a's generic-write suppression plus the unified tail's
        // resync from the resolved body.
        Assert.Equal(fixture.Remote.Body.Position, fixture.Entity.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearInterpolate_DifferentWireCellPreservesCommittedBodyCell(bool firstUpdate)
    {
        using var fixture = new Fixture(CreatureGuid);
        EntityPhysicsHost host = fixture.InstallHost();
        const uint committedCell = SourceLandblock | 0x01E4u;
        const uint wireCell = SourceLandblock | 0x01E5u;
        var committedPose = new Vector3(2f, 2f, SpawnHeight);
        var target = new Vector3(4f, 3f, SpawnHeight);
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        fixture.Remote.Body.Position = committedPose;
        fixture.Remote.CellId = committedCell;
        Assert.True(fixture.Runtime.RebucketLiveEntity(CreatureGuid, committedCell));
        fixture.Remote.LastServerPos = committedPose;
        fixture.Remote.LastServerPosTime = firstUpdate ? 0d
            : (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - 0.15;

        fixture.Controller.OnPosition(fixture.Update(
            target, wireCell, teleportSequence: 1,
            guid: CreatureGuid, isGrounded: true));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            CreatureGuid, out RuntimeEntityRecord canonical));
        Assert.Equal(wireCell, canonical.Snapshot.Position!.Value.LandblockId);
        Assert.Equal(firstUpdate ? target : committedPose, fixture.Remote.Body.Position);
        Assert.Equal(!firstUpdate, fixture.Remote.Interp.IsActive);
        uint expectedCell = firstUpdate ? wireCell : committedCell;
        Assert.Equal(expectedCell, fixture.Remote.CellId);
        Assert.Equal(expectedCell, canonical.FullCellId);
        Assert.Equal(expectedCell, fixture.Entity.ParentCellId);
        Assert.Equal(expectedCell, host.Position.ObjCellId);
        Assert.Equal(fixture.Remote.Body.Position, fixture.Entity.Position);
    }
    // ── Scenario 6: far snap ─────────────────────────────────────────────

    [Theory]
    [InlineData(PlayerGuid)]
    [InlineData(CreatureGuid)]
    public void FarSnap_BothGuids_PlacesAndArmsOnEveryOutcome(uint guid)
    {
        using var fixture = new Fixture(guid);
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);
        EntityPhysicsHost host = fixture.InstallHost();
        Assert.Null(host.PositionManager.Constraint);
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        Vector3 spawnPose = fixture.Entity.Position;
        var destination = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            destination, DestinationCell, teleportSequence: 1,
            guid: guid, isGrounded: true));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            guid, out RuntimeEntityRecord canonical));
        Assert.NotNull(canonical.PhysicsBody);
        PhysicsBody body = canonical.PhysicsBody!;
        Assert.NotEqual(spawnPose, body.Position);
        Assert.Equal(body.Position, fixture.Entity.Position);
        Assert.Equal(DestinationCell, fixture.Entity.ParentCellId);
        Assert.Equal(DestinationCell, canonical.FullCellId);
        Assert.NotNull(host.PositionManager.Constraint);

        ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(body.Position, shadowEntry.Position);

        fixture.DrainPlacementFifo();
    }

    // ── Scenario 7: a sticky lease does not gate the position arm ──
    // A melee creature keeps its stick on the player for the whole
    // engagement; the server corrections it receives meanwhile must still
    // route (the per-tick stick adjustment then overwrites the frame).

    [Fact]
    public void StickyArmed_CreatureGuid_NearWirePositionStillRoutesAndEnqueues()
    {
        using var fixture = new Fixture(CreatureGuid);
        EntityPhysicsHost host = fixture.ArmSticky(stickTargetGuid: 0x70009999u);
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        fixture.Remote.Body.Position = new Vector3(2f, 2f, SpawnHeight);
        fixture.Remote.LastServerPos = fixture.Remote.Body.Position;
        fixture.Remote.LastServerPosTime =
            (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - 0.15;
        var target = new Vector3(4f, 3f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            target, SourceCell, teleportSequence: 1,
            guid: CreatureGuid, isGrounded: true));

        Assert.True(fixture.Remote.Interp.IsActive);
        Assert.NotNull(host.PositionManager.Constraint);
        // Sticky itself is untouched by this non-teleport path (only the
        // teleport hook unsticks).
        Assert.NotEqual(0u, host.PositionManager.GetStickyObjectId());
    }

    [Fact]
    public void StickyArmed_CreatureGuid_FarWirePositionSnapsTheBody()
    {
        using var fixture = new Fixture(CreatureGuid);
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);
        EntityPhysicsHost host = fixture.ArmSticky(stickTargetGuid: 0x70009999u);
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        Vector3 spawnPose = fixture.Entity.Position;
        var destination = new Vector3(12f, 14f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            destination, DestinationCell, teleportSequence: 1,
            guid: CreatureGuid, isGrounded: true));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            CreatureGuid, out RuntimeEntityRecord canonical));
        PhysicsBody body = canonical.PhysicsBody!;
        Assert.NotEqual(spawnPose, body.Position);
        Assert.Equal(body.Position, fixture.Entity.Position);
        Assert.Equal(DestinationCell, fixture.Entity.ParentCellId);
        Assert.NotNull(host.PositionManager.Constraint);
        Assert.NotEqual(0u, host.PositionManager.GetStickyObjectId());

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void StickySuppressed_PlayerGuid_GateNeverAppliesRoutingRunsAnyway()
    {
        using var fixture = new Fixture(PlayerGuid);
        EntityPhysicsHost host = fixture.ArmSticky(stickTargetGuid: 0x70009999u);
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        fixture.Remote.Body.Position = new Vector3(2f, 2f, SpawnHeight);
        fixture.Remote.LastServerPos = fixture.Remote.Body.Position;
        fixture.Remote.LastServerPosTime =
            (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - 0.15;
        var target = new Vector3(4f, 3f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            target, SourceCell, teleportSequence: 1,
            guid: PlayerGuid, isGrounded: true));

        Assert.True(fixture.Remote.Interp.IsActive);
        Assert.NotNull(host.PositionManager.Constraint);
        // Sticky itself is untouched by this non-teleport path (only the
        // teleport hook unsticks).
        Assert.NotEqual(0u, host.PositionManager.GetStickyObjectId());
    }


    [Fact]
    public void VelocityCycle_CreatureGuid_PlansACycleFromWireVelocity()
    {
        using var fixture = new Fixture(CreatureGuid, withAnimation: true);
        EntityPhysicsHost host = fixture.InstallHost();
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        Assert.Equal(
            AcDream.Core.Physics.MotionCommand.Ready,
            fixture.Animated!.Sequencer!.CurrentMotion);
        var target = new Vector3(12f, 14f, SpawnHeight);
        var wireVelocity = new Vector3(0.7f, 0f, 0f);

        fixture.Controller.OnPosition(fixture.Update(
            target, SourceCell, teleportSequence: 1,
            guid: CreatureGuid, isGrounded: true, velocity: wireVelocity));

        Assert.True(fixture.Remote.HasServerVelocity);
        Assert.Equal(wireVelocity, fixture.Remote.ServerVelocity);
        Assert.NotEqual(
            AcDream.Core.Physics.MotionCommand.Ready,
            fixture.Animated.Sequencer.CurrentMotion);
        _ = host;
    }

    [Fact]
    public void VelocityCycle_PlayerGuid_SequencerUntouchedByWireVelocity()
    {
        using var fixture = new Fixture(PlayerGuid, withAnimation: true);
        EntityPhysicsHost host = fixture.InstallHost();
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        Assert.Equal(
            AcDream.Core.Physics.MotionCommand.Ready,
            fixture.Animated!.Sequencer!.CurrentMotion);
        var target = new Vector3(12f, 14f, SpawnHeight);
        var wireVelocity = new Vector3(0.7f, 0f, 0f);

        fixture.Controller.OnPosition(fixture.Update(
            target, SourceCell, teleportSequence: 1,
            guid: PlayerGuid, isGrounded: true, velocity: wireVelocity));

        // Row 6: the synth-velocity write is still write-only for players —
        // it happens (the unified NPC formula runs for every guid), but
        // nothing production reads it here either.
        Assert.True(fixture.Remote.HasServerVelocity);
        Assert.Equal(
            AcDream.Core.Physics.MotionCommand.Ready,
            fixture.Animated.Sequencer.CurrentMotion);
        _ = host;
    }

    [Fact]
    public void PositionPackVelocity_DoesNotOverwriteAuthoritativeBodyVelocity()
    {
        using var fixture = new Fixture(CreatureGuid);
        Vector3 authoritativeVectorUpdate = new(4f, 5f, 6f);
        fixture.Remote.Body.Velocity = authoritativeVectorUpdate;

        fixture.Controller.OnPosition(fixture.Update(
            new Vector3(12f, 14f, SpawnHeight + 2f),
            SourceCell,
            teleportSequence: 1,
            guid: CreatureGuid,
            isGrounded: false,
            velocity: new Vector3(0.7f, 0.2f, -0.1f)));

        Assert.Equal(authoritativeVectorUpdate, fixture.Remote.Body.Velocity);
    }



    private const uint MissileGuid = 0x80007101u;
    private static readonly Vector3 MissileAirborneDestination =
        new(12f, 14f, SpawnHeight + 10f);

    [Fact]
    public void MissileTeleportCommit_PlacesBodyNoRemoteMotionParentCellIdAgreesWithBody()
    {
        using var fixture = new Fixture(MissileGuid, isMissile: true);
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);

        fixture.Controller.OnPosition(fixture.Update(
            MissileAirborneDestination, DestinationCell, teleportSequence: 5,
            guid: MissileGuid, isGrounded: true));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonical));
        Assert.Null(canonical.RemoteMotion);
        Assert.NotNull(canonical.Projectile);
        PhysicsBody body = canonical.PhysicsBody!;
        Vector3 resolved = MissileAirborneDestination + DestinationWorldOffset;
        Assert.Equal(resolved, body.Position);
        Assert.Equal(body.Position, fixture.Entity.Position);
        Assert.Equal(body.CellPosition.ObjCellId, fixture.Entity.ParentCellId);
        Assert.Equal(DestinationCell, fixture.Entity.ParentCellId);
        ShadowEntry shadowEntry = Assert.Single(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == fixture.Entity.Id);
        Assert.Equal(body.Position, shadowEntry.Position);

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void MissileFarCommit_PlacesBodyNoRemoteMotionParentCellIdAgreesWithBody()
    {
        using var fixture = new Fixture(MissileGuid, isMissile: true);
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);

        fixture.Controller.OnPosition(fixture.Update(
            MissileAirborneDestination, DestinationCell, teleportSequence: 1,
            guid: MissileGuid, isGrounded: true));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonical));
        Assert.Null(canonical.RemoteMotion);
        PhysicsBody body = canonical.PhysicsBody!;
        Vector3 resolved = MissileAirborneDestination + DestinationWorldOffset;
        Assert.Equal(resolved, body.Position);
        Assert.Equal(body.Position, fixture.Entity.Position);
        Assert.Equal(body.CellPosition.ObjCellId, fixture.Entity.ParentCellId);
        Assert.Equal(DestinationCell, fixture.Entity.ParentCellId);

        fixture.DrainPlacementFifo();
    }

    private const uint IndoorSourceCell = SourceLandblock | 0x0100u;

    [Fact]
    public void MissileFarRefused_StorePathStillMovesEntityToDestinationParentCellIdAgreesWithCommittedCell()
    {
        using var fixture = new Fixture(MissileGuid, isMissile: true);
        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonicalBeforeStage));
        PhysicsBody stagedBody = canonicalBeforeStage.PhysicsBody!;
        stagedBody.SnapToCell(
            IndoorSourceCell, stagedBody.Position, stagedBody.Position);
        Assert.Equal(IndoorSourceCell, stagedBody.CellPosition.ObjCellId);
        fixture.PublishDestinationCollision();

        fixture.Controller.OnPosition(fixture.Update(
            MissileAirborneDestination, DestinationCell, teleportSequence: 1,
            guid: MissileGuid, isGrounded: true));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonical));
        Assert.Null(canonical.RemoteMotion);
        Assert.NotNull(canonical.Projectile);
        PhysicsBody body = canonical.PhysicsBody!;
        Vector3 resolved = MissileAirborneDestination + DestinationWorldOffset;
        Assert.Equal(resolved, body.Position);
        Assert.True(body.InWorld);
        Assert.Equal(IndoorSourceCell, body.CellPosition.ObjCellId);
        Assert.Equal(body.Position, fixture.Entity.Position);
        Assert.Equal(canonical.FullCellId, fixture.Entity.ParentCellId);
        Assert.NotEqual(
            body.CellPosition.ObjCellId,
            fixture.Entity.ParentCellId);
        Assert.Equal(SourceCell, canonical.FullCellId);
        Assert.NotEqual(DestinationCell, fixture.Entity.ParentCellId);

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void MissileNear_NoOp_BodyUnchangedNoRemoteMotionNoWirePoseWrite()
    {
        using var fixture = new Fixture(MissileGuid, isMissile: true);
        Vector3 spawnPose = fixture.Entity.Position;
        var target = new Vector3(4f, 3f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            target, SourceCell, teleportSequence: 1,
            guid: MissileGuid, isGrounded: true));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonical));
        Assert.Null(canonical.RemoteMotion);
        PhysicsBody body = canonical.PhysicsBody!;
        Assert.Equal(spawnPose, body.Position);
        // A1's regression, asserted directly: no early wire-pose write —
        // the render entity was never moved to `target`.
        Assert.Equal(spawnPose, fixture.Entity.Position);
        Assert.NotEqual(target, fixture.Entity.Position);
    }

    [Fact]
    public void MissileAirborne_NoOp_BodyUnchangedNoRemoteMotion()
    {
        using var fixture = new Fixture(MissileGuid, isMissile: true);
        Vector3 spawnPose = fixture.Entity.Position;
        var wirePos = new Vector3(50f, 50f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            wirePos, SourceCell, teleportSequence: 1,
            guid: MissileGuid, isGrounded: false));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonical));
        Assert.Null(canonical.RemoteMotion);
        PhysicsBody body = canonical.PhysicsBody!;
        Assert.Equal(spawnPose, body.Position);
        Assert.Equal(spawnPose, fixture.Entity.Position);
    }

    [Fact]
    public void MissileNullClassification_Swallowed_NoRemoteMotionNoWriteNoPredictionChange()
    {
        using var fixture = new Fixture(
            MissileGuid, nullClassification: true, isMissile: true);
        Vector3 spawnPose = fixture.Entity.Position;
        ulong predictionBefore = fixture.Projectile!.PredictionAuthorityVersion;
        var wirePos = new Vector3(50f, 50f, SpawnHeight);

        fixture.Controller.OnPosition(fixture.Update(
            wirePos, SourceCell, teleportSequence: 1,
            guid: MissileGuid, isGrounded: true));

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonical));
        Assert.Null(canonical.RemoteMotion);
        PhysicsBody body = canonical.PhysicsBody!;
        Assert.Equal(spawnPose, body.Position);
        Assert.Equal(spawnPose, fixture.Entity.Position);
        Assert.Equal(
            predictionBefore, fixture.Projectile.PredictionAuthorityVersion);
    }

    [Fact]
    public void MissileUnbound_FallsThroughToRemoteTail_TracksInsteadOfFreezing()
    {
        using var fixture = new Fixture(MissileGuid, isMissile: false);
        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonical));
        fixture.Lifetime.Entities.SetFinalPhysicsState(
            canonical,
            canonical.FinalPhysicsState | PhysicsStateFlags.Missile);
        Assert.Null(canonical.Projectile);
        EntityPhysicsHost host = fixture.InstallHost();
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);
        Vector3 spawnPose = fixture.Entity.Position;

        fixture.Controller.OnPosition(fixture.Update(
            MissileAirborneDestination, DestinationCell, teleportSequence: 1,
            guid: MissileGuid, isGrounded: true));

        // Placed via the ordinary remote far-snap arm — not frozen, and
        // armed exactly like FarSnap_BothGuids above.
        Assert.NotEqual(spawnPose, fixture.Remote.Body.Position);
        Assert.Equal(fixture.Remote.Body.Position, fixture.Entity.Position);
        Assert.Equal(DestinationCell, fixture.Entity.ParentCellId);
        Assert.NotNull(host.PositionManager.Constraint);

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void MissileAdoptedBody_TeleportCommit_UnConstrainsAndClearsInterpQueue()
    {
        using var fixture = new Fixture(MissileGuid, isMissile: false);
        EntityPhysicsHost host = fixture.InstallHost();
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        // Arm the leash and populate the queue directly — the pre-teleport
        // "live remote" state the adopted-body scenario requires.
        host.PositionManager.ConstrainTo(
            new AcDream.Core.Physics.Position(
                SourceCell, fixture.Remote.Body.Position, Quaternion.Identity),
            startDistance: 1f,
            maxDistance: 5f);
        Assert.NotNull(host.PositionManager.Constraint);
        fixture.Remote.Interp.Enqueue(
            fixture.Remote.Body.Position + Vector3.UnitX,
            heading: 0f,
            isMovingTo: false,
            currentBodyPosition: fixture.Remote.Body.Position);
        Assert.True(fixture.Remote.Interp.IsActive);

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonical));
        fixture.Lifetime.Entities.SetFinalPhysicsState(
            canonical,
            canonical.FinalPhysicsState | PhysicsStateFlags.Missile);
        fixture.Lifetime.Physics.BindProjectile(
            canonical,
            canonical.PhysicsBody!,
            new ProjectileCollisionSphere(Vector3.Zero, 0.1f, 1f));
        Assert.NotNull(canonical.Projectile);
        Assert.NotNull(canonical.RemoteMotion);

        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);

        fixture.Controller.OnPosition(fixture.Update(
            MissileAirborneDestination, DestinationCell, teleportSequence: 5,
            guid: MissileGuid, isGrounded: true));

        Assert.NotNull(host.PositionManager.Constraint);
        Assert.False(host.PositionManager.Constraint!.IsConstrained);
        Assert.False(fixture.Remote.Interp.IsActive);
        Assert.Equal(
            MissileAirborneDestination + DestinationWorldOffset,
            canonical.PhysicsBody!.Position);

        fixture.DrainPlacementFifo();
    }

    [Fact]
    public void MissileAdoptedBody_FarCommit_ClearsInterpQueueButLeavesConstraintArmed()
    {
        using var fixture = new Fixture(MissileGuid, isMissile: false);
        EntityPhysicsHost host = fixture.InstallHost();
        fixture.Remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        host.PositionManager.ConstrainTo(
            new AcDream.Core.Physics.Position(
                SourceCell, fixture.Remote.Body.Position, Quaternion.Identity),
            startDistance: 1f,
            maxDistance: 5f);
        Assert.NotNull(host.PositionManager.Constraint);
        Assert.True(host.PositionManager.Constraint!.IsConstrained);
        fixture.Remote.Interp.Enqueue(
            fixture.Remote.Body.Position + Vector3.UnitX,
            heading: 0f,
            isMovingTo: false,
            currentBodyPosition: fixture.Remote.Body.Position);
        Assert.True(fixture.Remote.Interp.IsActive);

        Assert.True(fixture.Lifetime.Entities.TryGetActive(
            MissileGuid, out RuntimeEntityRecord canonical));
        fixture.Lifetime.Entities.SetFinalPhysicsState(
            canonical,
            canonical.FinalPhysicsState | PhysicsStateFlags.Missile);
        fixture.Lifetime.Physics.BindProjectile(
            canonical,
            canonical.PhysicsBody!,
            new ProjectileCollisionSphere(Vector3.Zero, 0.1f, 1f));
        Assert.NotNull(canonical.Projectile);
        Assert.NotNull(canonical.RemoteMotion);

        fixture.PublishDestinationCollision();
        fixture.ServiceWindow.Allow(DestinationLandblock);

        fixture.Controller.OnPosition(fixture.Update(
            MissileAirborneDestination, DestinationCell, teleportSequence: 1,
            guid: MissileGuid, isGrounded: true));

        Assert.False(fixture.Remote.Interp.IsActive);
        // UnConstrain did NOT run — the far branch is one action, not six.
        // The leash is still armed.
        Assert.NotNull(host.PositionManager.Constraint);
        Assert.True(host.PositionManager.Constraint!.IsConstrained);
        Assert.Equal(
            MissileAirborneDestination + DestinationWorldOffset,
            canonical.PhysicsBody!.Position);

        fixture.DrainPlacementFifo();
    }

    private sealed class Fixture : IDisposable
    {
        internal RuntimeEntityObjectLifetime Lifetime { get; }
        internal LiveEntityRuntime Runtime { get; }
        internal LiveEntityNetworkUpdateController Controller { get; }
        internal RemoteServiceWindow ServiceWindow { get; } = new();
        internal ShadowObjectRegistry Shadows { get; }
        internal WorldEntity Entity { get; }
        internal RemoteMotion Remote { get; private set; } = null!;
        internal LiveEntityAnimationState? Animated { get; private set; }
        internal RuntimeProjectile? Projectile { get; private set; }
        private readonly GpuWorldState _spatial;
        private readonly uint _guid;
        private readonly bool _nullClassification;

        internal bool MaterializerDeclined => _decliningMaterializer?.Declined
            ?? false;

        private readonly DecliningMaterializer? _decliningMaterializer;

        internal Fixture(
            uint guid,
            bool nullClassification = false,
            bool withAnimation = false,
            bool isMissile = false,
            bool decliningMaterializer = false)
        {
            _guid = guid;
            _nullClassification = nullClassification;
            _decliningMaterializer =
                decliningMaterializer ? new DecliningMaterializer() : null;
            var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
            engine.AddLandblock(
                SourceLandblock,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            Lifetime = new RuntimeEntityObjectLifetime(engine);
            Lifetime.BindEventContext(
                static () => new RuntimeGenerationToken(1UL),
                static () => 1UL);
            Shadows = engine.ShadowObjects;

            var spatial = new GpuWorldState();
            spatial.AddLandblock(new LoadedLandblock(
                CanonicalLandblock(SourceLandblock),
                new DatReaderWriter.DBObjs.LandBlock(),
                Array.Empty<WorldEntity>()));
            _spatial = spatial;
            Runtime = new LiveEntityRuntime(
                spatial,
                new NoopResources(),
                NullLiveEntityRuntimeComponentLifecycle.Instance,
                Lifetime);

            var wirePosition = new CreateObject.ServerPosition(
                SourceCell, 10f, 10f, SpawnHeight, 1f, 0f, 0f, 0f);
            var timestamps = new PhysicsTimestamps(
                Position: 1,
                Movement: 1,
                State: 1,
                Vector: 1,
                Teleport: 1,
                ServerControlledMove: 1,
                ForcePosition: 1,
                ObjDesc: 1,
                Instance: 1);
            PhysicsStateFlags baseState = PhysicsStateFlags.ReportCollisions
                | (isMissile ? PhysicsStateFlags.Missile : PhysicsStateFlags.None);
            var physics = new PhysicsSpawnData(
                RawState: (uint)baseState,
                Position: wirePosition,
                Movement: null,
                AnimationFrame: null,
                SetupTableId: 0x02000001u,
                MotionTableId: 0x09000001u,
                SoundTableId: null,
                PhysicsScriptTableId: null,
                Parent: null,
                Children: null,
                Scale: 1f,
                Friction: null,
                Elasticity: null,
                Translucency: null,
                Velocity: null,
                Acceleration: null,
                AngularVelocity: null,
                DefaultScriptType: null,
                DefaultScriptIntensity: null,
                Timestamps: timestamps);
            var spawn = new WorldSession.EntitySpawn(
                _guid,
                wirePosition,
                0x02000001u,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                null,
                null,
                "onposition-collapse-fixture",
                null,
                null,
                0x09000001u,
                PhysicsState: (uint)baseState,
                InstanceSequence: 1,
                PositionSequence: 1,
                MovementSequence: 1,
                ServerControlSequence: 1,
                Physics: physics);
            LiveEntityRecord record =
                Runtime.RegisterAndMaterializeProjection(spawn);
            Entity = record.WorldEntity
                ?? throw new InvalidOperationException(
                    "fixture failed to materialize the remote entity");
            Assert.True(Runtime.RebucketLiveEntity(_guid, SourceCell));

            if (isMissile)
            {
                var body = new PhysicsBody
                {
                    Position = Entity.Position,
                    Orientation = Entity.Rotation,
                    LastUpdateTime = 1d,
                    State = baseState,
                    TransientState = TransientStateFlags.Active,
                };
                body.SnapToCell(SourceCell, Entity.Position, Entity.Position);
                RuntimeEntityRecord canonical = record.Canonical!;
                Lifetime.Entities.SetPhysicsBody(canonical, body);
                canonical.ObjectClock.Activate();
                Lifetime.Physics.AcknowledgeSpatialProjection(canonical, spatial: true);
                Projectile = (RuntimeProjectile)Lifetime.Physics.BindProjectile(
                    canonical, body, new ProjectileCollisionSphere(Vector3.Zero, 0.1f, 1f));
                Shadows.Register(
                    Entity.Id,
                    0x02000001u,
                    Entity.Position,
                    Entity.Rotation,
                    radius: 0.1f,
                    worldOffsetX: 0f,
                    worldOffsetY: 0f,
                    landblockId: SourceLandblock,
                    collisionType: ShadowCollisionType.Sphere,
                    state: (uint)baseState,
                    seedCellId: SourceCell,
                    isStatic: false);
            }
            else
            {
                var remote = new RemoteMotion();
                remote.Body.SnapToCell(SourceCell, Entity.Position, Entity.Position);
                remote.CellId = SourceCell;
                Runtime.SetRemoteMotionRuntime(_guid, remote);
                Remote = remote;
                Shadows.Register(
                    Entity.Id,
                    0x02000001u,
                    Entity.Position,
                    Entity.Rotation,
                    radius: 0.48f,
                    worldOffsetX: 0f,
                    worldOffsetY: 0f,
                    landblockId: SourceLandblock,
                    collisionType: ShadowCollisionType.Cylinder,
                    cylHeight: 1.835f,
                    seedCellId: SourceCell,
                    isStatic: false);
            }

            var origin = new LiveWorldOriginState();
            origin.SetPlaceholder(
                (int)((SourceLandblock >> 24) & 0xFFu),
                (int)((SourceLandblock >> 16) & 0xFFu));

            LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animatedEntities;
            if (withAnimation)
            {
                var slot = new LiveEntityRuntimeSlot();
                slot.Bind(Runtime);
                animatedEntities =
                    new LiveEntityAnimationRuntimeView<LiveEntityAnimationState>(slot);
                const uint animStyle = 0x8000003Du;
                const uint readyAnimId = 0x03000101u;
                const uint walkAnimId = 0x03000102u;
                var mt = new DatReaderWriter.DBObjs.MotionTable
                {
                    DefaultStyle = (DRWMotionCommand)animStyle,
                };
                mt.StyleDefaults[(DRWMotionCommand)animStyle] =
                    (DRWMotionCommand)AcDream.Core.Physics.MotionCommand.Ready;
                mt.Cycles[(int)((animStyle << 16)
                        | (AcDream.Core.Physics.MotionCommand.Ready & 0xFFFFFFu))] =
                    MakeMotionData(readyAnimId);
                mt.Cycles[(int)((animStyle << 16)
                        | (AcDream.Core.Physics.MotionCommand.WalkForward & 0xFFFFFFu))] =
                    MakeMotionData(walkAnimId);
                var loader = new FakeAnimationLoader();
                loader.Register(readyAnimId, MakeTwoFrameAnim());
                loader.Register(walkAnimId, MakeTwoFrameAnim());
                var setup = new DatReaderWriter.DBObjs.Setup();
                setup.Parts.Add(0x01000000u);
                setup.DefaultScale.Add(Vector3.One);
                var sequencer = new AcDream.Core.Physics.AnimationSequencer(
                    setup, mt, loader);
                sequencer.InitializeState();
                Animated = new LiveEntityAnimationState
                {
                    Entity = Entity,
                    Setup = setup,
                    Animation = new DatReaderWriter.DBObjs.Animation(),
                    LowFrame = 0,
                    HighFrame = 0,
                    Framerate = 0f,
                    Scale = 1f,
                    PartTemplate = Array.Empty<LiveAnimationPartTemplate>(),
                    PartAvailability = Array.Empty<bool>(),
                    Sequencer = sequencer,
                };
                animatedEntities[Entity.Id] = Animated;
            }
            else
            {
                animatedEntities =
                    new LiveEntityAnimationRuntimeView<LiveEntityAnimationState>(
                        new LiveEntityRuntimeSlot());
            }

            var remotePlacementDrive = new RuntimeRemotePlacementDriveController(
                Lifetime,
                new GameRuntimeClock(),
                new NoopCollisionSource(),
                ServiceWindow);
            var acceptedPositionDrive = new RuntimeAcceptedPositionDriveController(
                Lifetime,
                new GameRuntimeClock(),
                new NoopCollisionSource(),
                new LocalPlayerOutboundController(static (_, _, _, _, _, _) => { }),
                static () => new RuntimeGenerationToken(1UL),
                static () => 0x50000099u,
                static () => null,
                static () => false,
                static () => null);
            var identity = new NoopIdentitySource();
            var deletion = new LiveEntityDeletionController(
                Runtime,
                Lifetime,
                new NoopTeardownCoordinator(),
                identity);
            var hydration = new LiveEntityHydrationController(
                Runtime,
                Lifetime,
                new object(),
                (ILiveEntityProjectionMaterializer?)_decliningMaterializer
                    ?? new NoopMaterializer(),
                new NoopRelationships(),
                new NoopReadyPublisher(),
                new AlwaysKnownOrigin(),
                new NoopNetworkSink(),
                new NoopTimestampPublisher(),
                identity,
                deletion);
            var entityEffects = new EntityEffectController(
                Runtime,
                new AcDream.Core.Vfx.PhysicsScriptRunner(
                    static _ => null,
                    new AcDream.Core.Physics.AnimationHookRouter(),
                    randomUnit: static () => 0.5),
                new AcDream.Core.Vfx.PhysicsScriptTableResolver(static _ => null),
                new EntityEffectPoseRegistry());

            Controller = new LiveEntityNetworkUpdateController(
                Runtime,
                Lifetime.Objects,
                hydration,
                entityEffects,
                new LiveEntityPresentationController(
                    Runtime,
                    Shadows,
                    (_, _, _) => true,
                    new LiveEntityPartArrayEnterWorldPort(_ => { })),
                new LiveEntityLightController(
                    Runtime,
                    new EntityEffectPoseRegistry(),
                    new AcDream.Core.Lighting.LightingHookSink(
                        new AcDream.Core.Lighting.LightManager(),
                        new EntityEffectPoseRegistry()),
                    static _ => null),
                new EquippedChildRenderController(
                    new NoopDatReaderWriter(),
                    new object(),
                    Lifetime.Objects,
                    Runtime,
                    new EntityEffectPoseRegistry(),
                    static _ => false,
                    static (_, _, _) =>
                        new ExactProjectionWithdrawalOutcome(
                            ExactProjectionWithdrawalDisposition.Superseded,
                            null),
                    Shadows,
                    new PhysicsDataCache(),
                    static (_, _) => { }),
                new ProjectileController(Runtime),
                animatedEntities,
                new RemoteMovementObservationTracker(),
                new RemotePhysicsUpdater(
                    Lifetime.Physics,
                    static (_, _) => (0.48f, 1.835f),
                    static (_, _) => (
                        System.Collections.Immutable
                            .ImmutableArray<FlatCollisionSphere>.Empty,
                        1f, 0.4f, 0.4f),
                    static (_, _, _, _) => { }),
                new RemoteInboundMotionDispatcher(
                    static (_, _, _) => false,
                    static (_, _) => { }),
                new LiveEntityMotionRuntimeController(
                    Runtime,
                    new PhysicsDataCache(),
                    static () => null,
                    new AcDream.Core.Selection.SelectionState(),
                    origin),
                engine,
                new NoopDatReaderWriter(),
                new NoopAnimationLoader(),
                combatTargetController: null,
                origin,
                new NoopTeleportSink(),
                _nullClassification
                    ? new NoopLocalPlayerControllerSource()
                    : new StubLocalPlayerControllerSource(),
                new LocalPlayerOutboundController(static (_, _, _, _, _, _) => { }),
                new NoopPhysicsHostSource(),
                identity,
                new FixedScriptTime(),
                new NoopSessionSource(),
                publishTimestamps: static (_, _) => { },
                new NoopMovementTruthSink(),
                acceptedPositionDrive,
                remotePlacementDrive,
                worldDropProjection: null);
        }

        internal void PublishDestinationCollision()
        {
            var heights = new byte[81];
            Array.Fill(heights, (byte)SpawnHeight);
            var heightTable = new float[256];
            for (int index = 0; index < heightTable.Length; index++)
                heightTable[index] = index;
            Lifetime.Physics.ObserveLocalWorldFrame(
                SourceCell, teleportAdvanced: false);
            Lifetime.Physics.SetPosition.BeginCollisionGeneration(
                DestinationLandblock, 1UL);
            Lifetime.Physics.Engine.AddLandblock(
                DestinationLandblock,
                new TerrainSurface(heights, heightTable),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: DestinationWorldOffset.X,
                worldOffsetY: DestinationWorldOffset.Y);
            Lifetime.Physics.SetPosition.CommitCollisionGeneration(
                DestinationLandblock, 1UL, ready: true);

            uint destinationCanonical = CanonicalLandblock(DestinationLandblock);
            if (!_spatial.IsLoaded(destinationCanonical))
            {
                _spatial.AddLandblock(new LoadedLandblock(
                    destinationCanonical,
                    new DatReaderWriter.DBObjs.LandBlock(),
                    Array.Empty<WorldEntity>()));
            }
        }

        private static uint CanonicalLandblock(uint landblockId) =>
            (landblockId & 0xFFFF0000u) | 0xFFFFu;

        internal WorldSession.EntityPositionUpdate Update(
            Vector3 destination,
            uint cellId,
            ushort teleportSequence,
            uint guid,
            bool isGrounded = true,
            Vector3? velocity = null) => new(
            guid,
            new CreateObject.ServerPosition(
                cellId,
                destination.X,
                destination.Y,
                destination.Z,
                1f, 0f, 0f, 0f),
            Velocity: velocity,
            PlacementId: null,
            IsGrounded: isGrounded,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: teleportSequence,
            ForcePositionSequence: 0);

        private static DatReaderWriter.Types.MotionData MakeMotionData(uint animId)
        {
            var md = new DatReaderWriter.Types.MotionData();
            md.Anims.Add(new DatReaderWriter.Types.AnimData
            {
                AnimId = (QualifiedDataId<DatReaderWriter.DBObjs.Animation>)animId,
                LowFrame = 0,
                HighFrame = -1,
                Framerate = 30f,
            });
            return md;
        }

        private static DatReaderWriter.DBObjs.Animation MakeTwoFrameAnim()
        {
            var anim = new DatReaderWriter.DBObjs.Animation();
            var pf0 = new DatReaderWriter.Types.AnimationFrame(1u);
            var pf1 = new DatReaderWriter.Types.AnimationFrame(1u);
            pf0.Frames.Add(new DatReaderWriter.Types.Frame
            {
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
            });
            pf1.Frames.Add(new DatReaderWriter.Types.Frame
            {
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
            });
            anim.PartFrames.Add(pf0);
            anim.PartFrames.Add(pf1);
            return anim;
        }

        private sealed class FakeAnimationLoader : IAnimationLoader
        {
            private readonly Dictionary<uint, DatReaderWriter.DBObjs.Animation> _anims = new();

            internal void Register(uint id, DatReaderWriter.DBObjs.Animation anim) =>
                _anims[id] = anim;

            public DatReaderWriter.DBObjs.Animation? LoadAnimation(uint id) =>
                _anims.TryGetValue(id, out var a) ? a : null;
        }

        internal EntityPhysicsHost ArmSticky(uint stickTargetGuid)
        {
            EntityPhysicsHost host = InstallHost();
            host.PositionManager.StickTo(stickTargetGuid, radius: 1f, height: 1f);
            Assert.NotEqual(0u, host.PositionManager.GetStickyObjectId());
            return host;
        }

        internal EntityPhysicsHost InstallHost()
        {
            Assert.True(Runtime.TryGetRecord(
                _guid, out LiveEntityRecord liveRecord));
            var host = new EntityPhysicsHost(
                _guid,
                getPosition: () => new AcDream.Core.Physics.Position(
                    Remote.CellId, Remote.Body.Position, Remote.Body.Orientation),
                getVelocity: () => Remote.Body.Velocity,
                getRadius: () => 0.48f,
                inContact: () => Remote.Body.InContact,
                minterpMaxSpeed: () => null,
                curTime: () => 0d,
                physicsTimerTime: () => 0d,
                getObjectA: _ => null,
                handleUpdateTarget: _ => { },
                interruptCurrentMovement: () => { });
            Runtime.InstallPhysicsHost(liveRecord, host);
            Remote.MarkFullPhysicsHostBound();
            return host;
        }

        internal void DrainPlacementFifo()
        {
            while (Lifetime.Physics.SetPosition.TryPeekProjection(
                    out RuntimePlacementProjectionSnapshot head))
            {
                if (!Lifetime.Physics.SetPosition.AcknowledgeProjection(head.Token))
                    break;
            }
        }

        public void Dispose() => Lifetime.Dispose();

        internal sealed class RemoteServiceWindow : IRuntimeRemotePlacementServiceWindow
        {
            private readonly HashSet<uint> _within = [];

            internal void Allow(uint landblockId) =>
                _within.Add((landblockId & 0xFFFF0000u) | 0xFFFFu);

            public bool IsWithinServiceWindow(uint landblockId) =>
                _within.Contains((landblockId & 0xFFFF0000u) | 0xFFFFu);
        }

        private sealed class NoopResources : ILiveEntityResourceLifecycle
        {
            public void Register(WorldEntity entity) { }
            public void Unregister(WorldEntity entity) { }
        }

        private sealed class NoopCollisionSource : IPreparedCollisionSource
        {
            public PreparedAssetPresence ProbeCollision(
                PakAssetType type, uint sourceFileId) =>
                PreparedAssetPresence.Available;

            public PreparedCollisionReadResult<FlatSetupCollision>
                ReadSetupCollision(
                    uint sourceFileId,
                    CancellationToken cancellationToken = default) =>
                PreparedCollisionReadResult<FlatSetupCollision>.Loaded(
                    new FlatSetupCollision(
                        System.Collections.Immutable
                            .ImmutableArray<FlatCollisionCylinder>.Empty,
                        [new FlatCollisionSphere(Vector3.Zero, 0.48f)],
                        height: 0f,
                        radius: 0f,
                        stepUpHeight: 0.4f,
                        stepDownHeight: 0.4f));

            public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
                ReadGfxObjCollision(
                    uint sourceFileId,
                    CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
                ReadCellStructureCollision(
                    uint sourceFileId,
                    CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public PreparedCollisionReadResult<FlatEnvCellTopology>
                ReadEnvCellTopology(
                    uint sourceFileId,
                    CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public PreparedCollisionSourceStats CollisionStats => default;

            public void Dispose() { }
        }

        private sealed class NoopIdentitySource : ILocalPlayerIdentitySource
        {
            public uint ServerGuid => 0x50000099u;
        }

        private sealed class NoopTeardownCoordinator
            : ILiveEntityTeardownCoordinator
        {
            public void TearDown(LiveEntityRecord record) { }
            public void ForgetUnknownOwner(uint serverGuid) { }
        }

        private sealed class DecliningMaterializer
            : ILiveEntityProjectionMaterializer
        {
            internal bool Declined { get; private set; }

            public bool TryMaterialize(
                RuntimeEntityRecord expectedCanonical,
                WorldSession.EntitySpawn canonicalSpawn,
                LiveProjectionPurpose purpose,
                ulong expectedCreateIntegrationVersion,
                AcDream.App.Rendering.LiveEntityAppearanceUpdateState?
                    appearanceUpdate = null)
            {
                Declined = true;
                return false;
            }

            public void ResetSessionState() => Declined = false;
        }

        private sealed class NoopMaterializer : ILiveEntityProjectionMaterializer
        {
            public bool TryMaterialize(
                RuntimeEntityRecord expectedCanonical,
                WorldSession.EntitySpawn canonicalSpawn,
                LiveProjectionPurpose purpose,
                ulong expectedCreateIntegrationVersion,
                AcDream.App.Rendering.LiveEntityAppearanceUpdateState?
                    appearanceUpdate = null) =>
                throw new InvalidOperationException(
                    "The fixture pre-materializes the remote entity; " +
                    "TryMaterialize should never be reached for an " +
                    "already-projected accepted Position.");

            public void ResetSessionState() { }
        }

        private sealed class NoopRelationships : ILiveEntityRelationshipProjection
        {
            public void OnSpawn(WorldSession.EntitySpawn spawn) { }
            public void OnParent(ParentEvent.Parsed update) { }
            public void OnCreateParentAccepted(CreateParentUpdate update) { }

            public AcDream.App.Rendering.ChildUnparentDisposition
                OnChildBecameUnparented(uint childGuid) =>
                AcDream.App.Rendering.ChildUnparentDisposition.NotAttached;

            public bool TryApplyAttachedAppearance(
                LiveEntityRecord record, ulong objDescAuthorityVersion) => false;
        }

        private sealed class NoopReadyPublisher : ILiveEntityReadyPublisher
        {
            public bool Publish(LiveEntityReadyCandidate candidate) => true;
        }

        private sealed class AlwaysKnownOrigin : ILiveEntityWorldOriginCoordinator
        {
            public bool IsKnown => true;

            public LiveEntityOriginInitialization TryInitialize(
                WorldSession.EntitySpawn spawn) => new(true, []);
        }

        private sealed class NoopNetworkSink : ILiveEntityNetworkUpdateSink
        {
            public void ApplySameGeneration(SameGenerationCreateObjectEvents events) { }
        }

        private sealed class NoopTimestampPublisher
            : IAcceptedLocalPhysicsTimestampPublisher
        {
            public void Publish(uint serverGuid, AcceptedPhysicsTimestamps timestamps) { }
        }

        private sealed class NoopDatReaderWriter : IDatReaderWriter
        {
            private readonly StubDatabase _portal = new();
            private readonly StubDatabase _highRes = new();
            private readonly StubDatabase _language = new();
            private readonly StubDatabase _cell = new();

            public string SourceDirectory => string.Empty;
            public IDatDatabase Portal => _portal;
            public IDatDatabase Cell => _cell;
            public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
                new(new Dictionary<uint, IDatDatabase>());
            public IDatDatabase HighRes => _highRes;
            public IDatDatabase Language => _language;
            public IDatDatabase Local => _language;
            public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
                new(new Dictionary<uint, uint>());
            public int PortalIteration => 0;
            public int CellIteration => 0;
            public int HighResIteration => 0;
            public int LanguageIteration => 0;

            public bool TryGetFileBytes(
                uint regionId,
                uint fileId,
                ref byte[] bytes,
                out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }

            public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
                Array.Empty<uint>();

            public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
                Array.Empty<IDatReaderWriter.IdResolution>();

            public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
                throw new NotSupportedException();

            public bool TrySave<T>(
                uint regionId,
                T obj,
                int iteration = 0) where T : IDBObj =>
                throw new NotSupportedException();

            [return: MaybeNull]
            public T Get<T>(uint fileId) where T : IDBObj => default;

            public bool TryGet<T>(
                uint fileId,
                [MaybeNullWhen(false)] out T value) where T : IDBObj
            {
                value = default;
                return false;
            }

            public void Dispose() { }
        }

        private sealed class StubDatabase : IDatDatabase
        {
            public DatDatabase Db => throw new NotSupportedException();
            public int Iteration => 0;

            public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
                Array.Empty<uint>();

            public bool TryGet<T>(
                uint fileId,
                [MaybeNullWhen(false)] out T value) where T : IDBObj
            {
                value = default;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                [MaybeNullWhen(false)] out byte[] value)
            {
                value = null;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                ref byte[] bytes,
                out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }

            public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
                throw new NotSupportedException();

            public void Dispose() { }
        }

        private sealed class NoopAnimationLoader : IAnimationLoader
        {
            public Animation? LoadAnimation(uint id) => null;
        }

        private sealed class NoopTeleportSink : ILocalPlayerTeleportNetworkSink
        {
            public void OnTeleportStarted(uint sequence) { }

            public void OfferDestination(
                RuntimeTeleportDestination destination,
                bool teleportTimestampAdvanced)
            { }

            public void OnLocalPlayerFirstEntryCompleted() { }

            public void ArmLoginTunnel() { }

            public void RequestLogout() { }

            public void ResetSession() { }

            public void ResetGenerationPresentation() { }
        }

        private sealed class StubLocalPlayerControllerSource
            : IRuntimeLocalPlayerControllerSource
        {
            public PlayerMovementController? Controller { get; } =
                new PlayerMovementController(new PhysicsEngine());
        }

        private sealed class NoopLocalPlayerControllerSource
            : IRuntimeLocalPlayerControllerSource
        {
            public PlayerMovementController? Controller => null;
        }

        private sealed class NoopPhysicsHostSource : ILocalPlayerPhysicsHostSource
        {
            public EntityPhysicsHost? Host => null;
        }

        private sealed class FixedScriptTime : IPhysicsScriptTimeSource
        {
            public double CurrentScriptTime => 1_700_000_000d;
        }

        private sealed class NoopSessionSource : ILiveWorldSessionSource
        {
            public WorldSession? CurrentSession => null;
        }

        private sealed class NoopMovementTruthSink : IMovementTruthDiagnosticSink
        {
            public void OnOutbound(
                string kind,
                uint sequence,
                MovementResult result,
                Vector3 wirePosition,
                uint wireCellId,
                byte contactByte)
            { }

            public void OnServerEcho(
                WorldSession.EntityPositionUpdate update,
                Vector3 serverWorldPosition)
            { }

            public void ResetSession() { }
        }
    }
}

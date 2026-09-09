using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeSetPositionMoverPreparationTests
{
    private const uint Cell = 0xA9B40021u;
    private const uint SetupId = 0x02000001u;

    [Fact]
    public void PreparedMoverPreservesCompleteAuthoredRetailInput()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        float half = MathF.Sqrt(0.5f);
        var acceptedPosition = new CreateObject.ServerPosition(
            Cell,
            12.5f,
            23.5f,
            34.5f,
            half,
            0f,
            half,
            0f);
        RuntimeEntityRecord record = CreateRecord(
            lifetime,
            objScale: 3f,
            physicsScale: 2f,
            objectDescriptionFlags: 0x8u | 0x20u | 0x200000u
                | 0x2000000u | 0x100000u | 0x400000u,
            position: acceptedPosition);
        const PhysicsStateFlags state = PhysicsStateFlags.Gravity
            | PhysicsStateFlags.EdgeSlide
            | PhysicsStateFlags.PathClipped;
        lifetime.Entities.SetFinalPhysicsState(record, state);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);
        ImmutableArray<FlatCollisionSphere> spheres =
        [
            new(new Vector3(1f, 2f, 3f), 0.1f),
            new(new Vector3(4f, 5f, 6f), 0.2f),
            new(new Vector3(7f, 8f, 9f), 0.3f),
        ];
        FlatSetupCollision setup = Setup(spheres, 0.25f, -0.5f);
        var portal = default(RuntimePortalPlacementAuthority);
        var input = new RuntimeSetPositionMoverPreparation(
            RuntimeSetPositionMoverSetup.Resolved(SetupId, setup),
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            GameTime: 42.25d,
            PhysicsPlacementClass.Corpse,
            PhysicsSetPositionFlags.Line | PhysicsSetPositionFlags.Scatter,
            Line: new Vector3(3f, 4f, 5f),
            ScatterRadiusX: 6f,
            ScatterRadiusY: 7f,
            ScatterAttempts: 8u,
            ShadowWorldOffsetX: 9f,
            ShadowWorldOffsetY: 10f,
            Portal: portal);

        RuntimeSetPositionMoverPreparationStatus status = lifetime.Physics
            .SetPosition.PrepareMover(token, input, out var command);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared, status);
        Assert.Equal(spheres, command.Physics.Spheres);
        Assert.Equal(2f, command.Physics.Scale);
        Assert.Equal(0.5f, command.Physics.StepUpHeight);
        Assert.Equal(-1f, command.Physics.StepDownHeight);
        Assert.Equal(Cell, command.Physics.CellId);
        Assert.Equal(new Vector3(12.5f, 23.5f, 34.5f),
            command.Physics.CellLocalPosition);
        Assert.Equal(new Vector3(
            12.5f + 9f,
            23.5f + 10f,
            34.5f), command.Physics.Position);
        Assert.Equal(new Quaternion(0f, half, 0f, half),
            command.Physics.Orientation);
        Assert.Equal(state, command.Physics.MoverPhysicsState);
        Assert.Equal(
            ObjectInfoState.IsPlayer
            | ObjectInfoState.IsPK
            | ObjectInfoState.IsPKLite
            | ObjectInfoState.IsImpenetrable
            | ObjectInfoState.CanBypassMoveRestrictions,
            command.Physics.MoverFlags);
        Assert.False(command.Physics.MoverFlags.HasFlag(
            ObjectInfoState.EdgeSlide));
        Assert.False(command.Physics.MoverFlags.HasFlag(
            ObjectInfoState.PathClipped));
        Assert.False(command.Physics.MoverFlags.HasFlag(
            ObjectInfoState.FreeRotate));
        Assert.False(command.Physics.MoverFlags.HasFlag(
            ObjectInfoState.Contact));
        Assert.False(command.Physics.MoverFlags.HasFlag(
            ObjectInfoState.OnWalkable));
        Assert.Equal(record.Key!.Value.LocalEntityId,
            command.Physics.MovingEntityId);
        Assert.Equal(PhysicsPlacementClass.Corpse,
            command.Physics.PlacementClass);
        Assert.Equal(input.Flags, command.Physics.Flags);
        Assert.Equal(input.Line, command.Physics.Line);
        Assert.Equal(6f, command.Physics.ScatterRadiusX);
        Assert.Equal(7f, command.Physics.ScatterRadiusY);
        Assert.Equal(8u, command.Physics.ScatterAttempts);
        Assert.Equal(42.25d, command.GameTime);
        Assert.Equal(9f, command.ShadowWorldOffsetX);
        Assert.Equal(10f, command.ShadowWorldOffsetY);
        Assert.Equal(portal, command.Portal);
        Assert.Equal(record.VelocityAuthorityVersion,
            command.ExpectedVelocityAuthorityVersion);
    }

    [Fact]
    public void LivePkStatusUpdate_ReachesMoverFlagsOnNextPlacement()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(
            lifetime,
            objectDescriptionFlags: 0x8u); // BF_PLAYER only, no PK/PKLite yet

        lifetime.Objects.AddOrUpdate(new AcDream.Core.Items.ClientObject
        {
            ObjectId = record.ServerGuid,
            PublicWeenieBitfield = 0x8u,
        });
        lifetime.Objects.UpdateIntProperty(
            record.ServerGuid,
            AcDream.Core.Items.ClientObjectTable.PlayerKillerStatusPropertyId,
            value: AcDream.Core.Items.PlayerKillerStatusBitfield.PkLite);

        RuntimeEntityPlacementToken token = Begin(lifetime, record);
        ImmutableArray<FlatCollisionSphere> spheres =
            [new(new Vector3(1f, 2f, 3f), 0.1f)];
        FlatSetupCollision setup = Setup(spheres, 0f, 0f);
        RuntimeSetPositionMoverPreparation input =
            Input(RuntimeSetPositionMoverSetup.Resolved(SetupId, setup));

        RuntimeSetPositionMoverPreparationStatus status = lifetime.Physics
            .SetPosition.PrepareMover(token, input, out var command);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared, status);
        Assert.True(command.Physics.MoverFlags.HasFlag(ObjectInfoState.IsPKLite));
    }

    [Fact]
    public void LocalPortalKindAndAuthorityArePreservedWithoutInference()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime);
        var portal = new RuntimePortalPlacementAuthority(
            Present: true,
            RevealGeneration: 17,
            TeleportSequence: 3,
            new RuntimeWorldHostProjectionToken(17, Cell));
        RuntimeEntityPlacementToken token = lifetime.Physics.SetPosition
            .BeginAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.LocalAuthoritative,
                portal);
        var input = Input(ResolvedEmptySetup()) with
        {
            Kind = RuntimeSetPositionOperationKind.LocalAuthoritative,
            Portal = portal,
        };

        RuntimeSetPositionMoverPreparationStatus status = lifetime.Physics
            .SetPosition.PrepareMover(token, input, out var command);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared, status);
        Assert.Equal(RuntimeSetPositionOperationKind.LocalAuthoritative,
            command.Kind);
        Assert.Equal(portal, command.Portal);
    }

    [Fact]
    public void UnavailableSetupRetriesWithoutInventingDummyWhileAbsentAndEmptyResolve()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime, setupTableId: null);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);

        RuntimeSetPositionMoverPreparationStatus unavailable = lifetime.Physics
            .SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.Unavailable),
                out RuntimeSetPositionCommand unavailableCommand);

        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.RetrySetupUnavailable,
            unavailable);
        Assert.Equal(default, unavailableCommand);
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .AwaitingSetPositionPreparationCount);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.ResolvedAbsent),
                out RuntimeSetPositionCommand absent));
        Assert.Empty(absent.Physics.Spheres);

        record = CreateRecord(lifetime, guid: 0x70002002u);
        token = Begin(lifetime, record);
        FlatSetupCollision authoredEmpty = Setup(
            ImmutableArray<FlatCollisionSphere>.Empty,
            0f,
            0f);
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.Resolved(
                    SetupId,
                    authoredEmpty)),
                out RuntimeSetPositionCommand empty));
        Assert.Empty(empty.Physics.Spheres);
    }

    [Fact]
    public void KnownSetupMustResolveTheExactCanonicalDid()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);

        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.RetrySetupUnavailable,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.Unavailable),
                out _));
        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.InvalidData,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.ResolvedAbsent),
                out _));
        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.InvalidData,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.Resolved(
                    SetupId + 1u,
                    Setup(ImmutableArray<FlatCollisionSphere>.Empty, 0f, 0f))),
                out _));

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(ResolvedEmptySetup()),
                out _));
    }

    [Fact]
    public void AbsentSetupRejectsInventedCollisionPayload()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(
            lifetime,
            setupTableId: null);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);

        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.InvalidData,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.Resolved(
                    SetupId,
                    Setup(ImmutableArray<FlatCollisionSphere>.Empty, 0f, 0f))),
                out _));
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.ResolvedAbsent),
                out _));
    }

    [Fact]
    public void AuthoredEmptySetupStillSuppliesScaledStepHeights()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(
            lifetime,
            objScale: 2f);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);
        FlatSetupCollision authoredEmpty = Setup(
            ImmutableArray<FlatCollisionSphere>.Empty,
            0.25f,
            -0.5f);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.Resolved(
                    SetupId,
                    authoredEmpty)),
                out RuntimeSetPositionCommand command));
        Assert.Empty(command.Physics.Spheres);
        Assert.Equal(0.5f, command.Physics.StepUpHeight);
        Assert.Equal(-1f, command.Physics.StepDownHeight);
    }

    [Fact]
    public void AuthoredTokenRejectsManualUnsealedSubmission()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime);
        AttachBody(lifetime, record);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);
        RuntimeSetPositionCommand invented = new(
            new PhysicsSetPositionRequest(
                new Vector3(1f, 2f, 3f),
                Quaternion.Identity,
                Cell,
                new Vector3(1f, 2f, 3f),
                ImmutableArray<FlatCollisionSphere>.Empty,
                1f,
                0f,
                0f,
                record.FinalPhysicsState,
                ObjectInfoState.None,
                record.Key!.Value.LocalEntityId),
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            1d,
            record.VelocityAuthorityVersion);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(token, invented);

        Assert.Equal(RuntimeSetPositionStatus.Rejected, outcome.Status);
        Assert.Equal(0, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .AwaitingSetPositionPreparationCount);
    }

    [Fact]
    public void RepreparationReplacesTheExactSealedCommand()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime);
        AttachBody(lifetime, record);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(ResolvedEmptySetup()) with { GameTime = 1d },
                out RuntimeSetPositionCommand first));
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(ResolvedEmptySetup()) with { GameTime = 2d },
                out RuntimeSetPositionCommand second));
        Assert.NotEqual(first, second);

        RuntimeSetPositionOutcome stale = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(token, first);
        Assert.Equal(RuntimeSetPositionStatus.Rejected, stale.Status);

        RuntimeSetPositionOutcome current = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(token, second);
        Assert.NotEqual(RuntimeSetPositionStatus.Rejected, current.Status);
    }

    [Theory]
    [InlineData(null, null, 1f)]
    [InlineData(0f, null, 0f)]
    [InlineData(-2f, null, -2f)]
    [InlineData(3f, 0f, 0f)]
    [InlineData(3f, -4f, -4f)]
    public void ScalePreservesAbsentZeroNegativeAndPhysicsPrecedence(
        float? objScale,
        float? physicsScale,
        float expected)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(
            lifetime,
            objScale,
            physicsScale);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);

        RuntimeSetPositionMoverPreparationStatus status = lifetime.Physics
            .SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.Resolved(SetupId, Setup(
                    [new FlatCollisionSphere(Vector3.Zero, 0.5f)],
                    0.25f,
                    -0.5f))),
                out RuntimeSetPositionCommand command);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared, status);
        Assert.Equal(expected, command.Physics.Scale);
        Assert.Equal(0.25f * expected, command.Physics.StepUpHeight);
        Assert.Equal(-0.5f * expected, command.Physics.StepDownHeight);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteScaleRejectsWhenAuthoredSphereConsumesIt(float scale)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime, scale, null);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);

        RuntimeSetPositionMoverPreparationStatus status = lifetime.Physics
            .SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.Resolved(
                    SetupId,
                    Setup(
                        [new FlatCollisionSphere(Vector3.Zero, 0.5f)],
                        0f,
                        0f))),
                out RuntimeSetPositionCommand command);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.InvalidData, status);
        Assert.Equal(default, command);
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .AwaitingSetPositionPreparationCount);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteScaleIsNotConsumedByResolvedAbsentDummy(float scale)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(
            lifetime,
            objScale: scale,
            setupTableId: null);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);

        RuntimeSetPositionMoverPreparationStatus status = lifetime.Physics
            .SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.ResolvedAbsent),
                out RuntimeSetPositionCommand command);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared, status);
        Assert.Equal(scale, command.Physics.Scale);
        Assert.Equal(0f, command.Physics.StepUpHeight);
        Assert.Equal(0f, command.Physics.StepDownHeight);
        Assert.Empty(command.Physics.Spheres);
    }

    [Fact]
    public void PreparationDoesNotMutateCanonicalOrPresentationState()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime);
        var body = new PhysicsBody
        {
            Position = new Vector3(1f, 2f, 3f),
            Orientation = Quaternion.Identity,
            State = PhysicsStateFlags.Gravity,
            InWorld = true,
        };
        body.SnapToCell(0xA8B40001u, body.Position, body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);
        lifetime.Entities.SetFullCell(record, 0xA8B40001u, 0xA8B4FFFFu);
        Vector3 priorPosition = body.Position;
        uint priorCell = record.FullCellId;
        ulong priorSpatial = record.SpatialAuthorityVersion;
        RuntimeEntityPlacementToken token = Begin(lifetime, record);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(ResolvedEmptySetup()),
                out _));

        Assert.Same(body, record.PhysicsBody);
        Assert.Equal(priorPosition, body.Position);
        Assert.Equal(priorCell, record.FullCellId);
        Assert.Equal(priorSpatial, record.SpatialAuthorityVersion);
        Assert.True(body.InWorld);
        Assert.Equal(0, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .AwaitingSetPositionPreparationCount);
        Assert.False(lifetime.Physics.SetPosition
            .TryGetPreparedMoverSphereCount(record, out _));
    }

    [Theory]
    [InlineData(AuthorityReplacement.Position)]
    [InlineData(AuthorityReplacement.Velocity)]
    [InlineData(AuthorityReplacement.Vector)]
    [InlineData(AuthorityReplacement.State)]
    [InlineData(AuthorityReplacement.ObjectDescription)]
    [InlineData(AuthorityReplacement.Create)]
    public void AnyAuthorityReplacementRejectsPreparedSubmissionWithoutMutation(
        AuthorityReplacement replacement)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime);
        var body = new PhysicsBody
        {
            Position = new Vector3(1f, 2f, 3f),
            Orientation = Quaternion.Identity,
            State = record.FinalPhysicsState,
            InWorld = true,
        };
        body.SnapToCell(0xA8B40001u, body.Position, body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);
        lifetime.Entities.SetFullCell(record, 0xA8B40001u, 0xA8B4FFFFu);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);
        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared,
            lifetime.Physics.SetPosition.PrepareMover(
                token,
                Input(ResolvedEmptySetup()),
                out RuntimeSetPositionCommand command));
        Vector3 priorPosition = body.Position;
        uint priorCell = record.FullCellId;

        ReplaceAuthority(lifetime, record, replacement);
        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(token, command);

        Assert.Equal(RuntimeSetPositionStatus.Rejected, outcome.Status);
        Assert.Equal(priorPosition, body.Position);
        Assert.Equal(priorCell, record.FullCellId);
        Assert.Equal(0, lifetime.Physics.SetPosition.PendingProjectionCount);
        Assert.False(lifetime.Physics.SetPosition
            .TryGetPreparedMoverSphereCount(record, out _));

        RuntimeEntityPlacementToken replacementToken = Begin(lifetime, record);
        Assert.True(replacementToken.IsValid);
        Assert.NotEqual(token, replacementToken);
    }

    [Fact]
    public void ThirdAuthoredSphereIsPreservedButNotConsumedByCoreValidation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);
        FlatSetupCollision setup = Setup(
        [
            new FlatCollisionSphere(Vector3.Zero, 0.1f),
            new FlatCollisionSphere(Vector3.UnitZ, 0.2f),
            new FlatCollisionSphere(
                new Vector3(float.NaN, 0f, 0f),
                0.3f),
        ], 0f, 0f);

        RuntimeSetPositionMoverPreparationStatus status = lifetime.Physics
            .SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.Resolved(SetupId, setup)),
                out _);

        Assert.Equal(RuntimeSetPositionMoverPreparationStatus.Prepared, status);
    }

    [Fact]
    public void SessionReplacementInvalidatesExactTokenAndClearsPreparationAuthority()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = CreateRecord(lifetime);
        RuntimeEntityPlacementToken token = Begin(lifetime, record);

        _ = lifetime.BeginSessionClear();
        RuntimeSetPositionMoverPreparationStatus status = lifetime.Physics
            .SetPosition.PrepareMover(
                token,
                Input(RuntimeSetPositionMoverSetup.ResolvedAbsent),
                out RuntimeSetPositionCommand command);

        Assert.Equal(
            RuntimeSetPositionMoverPreparationStatus.RejectedAuthority,
            status);
        Assert.Equal(default, command);
        RuntimeSetPositionOwnershipSnapshot ownership = lifetime.Physics
            .SetPosition.CaptureOwnership();
        Assert.Equal(0, ownership.MoverPreparationAuthorityCount);
        Assert.Equal(0, ownership.ActiveOperationCount);
    }

    private static RuntimeEntityPlacementToken Begin(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord record) => lifetime.Physics.SetPosition
        .BeginAuthoredPlacement(
            record,
            record.PositionAuthorityVersion,
            RuntimeSetPositionOperationKind.RemoteAuthoritative);

    private static RuntimeSetPositionMoverPreparation Input(
        RuntimeSetPositionMoverSetup setup) => new(
            setup,
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            GameTime: 1d,
            PhysicsPlacementClass.Ordinary,
            PhysicsSetPositionFlags.Placement);

    private static RuntimeSetPositionMoverSetup ResolvedEmptySetup() =>
        RuntimeSetPositionMoverSetup.Resolved(
            SetupId,
            Setup(ImmutableArray<FlatCollisionSphere>.Empty, 0f, 0f));

    private static FlatSetupCollision Setup(
        ImmutableArray<FlatCollisionSphere> spheres,
        float stepUp,
        float stepDown) => new(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            spheres,
            height: 0f,
            radius: 0f,
            stepUp,
            stepDown);

    private static RuntimeEntityRecord CreateRecord(
        RuntimeEntityObjectLifetime lifetime,
        float? objScale = null,
        float? physicsScale = null,
        uint? objectDescriptionFlags = null,
        uint guid = 0x70002001u,
        uint? setupTableId = SetupId,
        CreateObject.ServerPosition? position = null)
    {
        CreateObject.ServerPosition acceptedPosition = position ?? new(
            Cell,
            1f,
            2f,
            3f,
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
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.Gravity,
            Position: acceptedPosition,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: setupTableId,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: physicsScale,
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
            Guid: guid,
            Position: acceptedPosition,
            SetupTableId: setupTableId,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: objScale,
            Name: "mover-preparation-fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: 0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.Gravity,
            ObjectDescriptionFlags: objectDescriptionFlags,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
        return lifetime.RegisterEntity(spawn).Canonical!;
    }

    private static void AttachBody(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord record)
    {
        var body = new PhysicsBody
        {
            Position = new Vector3(1f, 2f, 3f),
            Orientation = Quaternion.Identity,
            State = record.FinalPhysicsState,
            InWorld = true,
        };
        body.SnapToCell(Cell, body.Position, body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);
        lifetime.Entities.SetFullCell(record, Cell, Cell & 0xFFFF0000u);
    }

    private static void ReplaceAuthority(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord record,
        AuthorityReplacement replacement)
    {
        switch (replacement)
        {
            case AuthorityReplacement.Position:
                lifetime.Entities.AdvancePositionAuthority(record);
                break;
            case AuthorityReplacement.Velocity:
                lifetime.Entities.AdvanceMovementAuthority(record);
                break;
            case AuthorityReplacement.Vector:
                lifetime.Entities.AdvanceVectorAuthority(record);
                break;
            case AuthorityReplacement.State:
                _ = lifetime.Entities.ApplyRawPhysicsState(
                    record,
                    (uint)PhysicsStateFlags.Frozen);
                break;
            case AuthorityReplacement.ObjectDescription:
                lifetime.Entities.AdvanceObjDescAuthority(record);
                break;
            case AuthorityReplacement.Create:
                lifetime.Entities.AdvanceCreateAuthority(record);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(replacement));
        }
    }

    public enum AuthorityReplacement
    {
        Position,
        Velocity,
        Vector,
        State,
        ObjectDescription,
        Create,
    }
}

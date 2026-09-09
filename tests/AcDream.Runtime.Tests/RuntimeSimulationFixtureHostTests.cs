using System.Globalization;
using System.Numerics;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests;

public sealed class RuntimeSimulationFixtureHostTests
{
    [Fact]
    public void RuntimeOnlyHostDrivesCompleteGameplaySimulationWithoutPresentation()
    {
        var host = new RuntimeOnlySimulationFixtureHost();

        host.Move();
        host.Use(0x70000011u);
        host.Attack();
        host.Cast();
        host.AdvanceProjectile(failProjection: false);

        Assert.Equal(
            [
                "movement:run",
                "use:70000011",
                "attack:prepare",
                "attack:Medium:0.5",
                "cast:stop",
                "cast:1",
                "cast:busy",
                "projectile:ack",
            ],
            host.Trace);

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
                "ImGui",
                StringComparison.OrdinalIgnoreCase));

        host.Dispose();
        Assert.True(host.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void ProjectionAcknowledgementFailureStillAllowsTerminalConvergence()
    {
        var host = new RuntimeOnlySimulationFixtureHost();

        Assert.Throws<InvalidOperationException>(() =>
            host.AdvanceProjectile(failProjection: true));

        host.Dispose();

        RuntimeSimulationOwnershipSnapshot ownership =
            host.CaptureOwnership();
        Assert.True(ownership.IsConverged);
        Assert.Equal(0, ownership.Physics.SpatialProjectileCount);
        Assert.Equal(0, ownership.EntityObjects.ActiveEntityCount);
    }

    private sealed class RuntimeOnlySimulationFixtureHost :
        IRuntimeInteractionTransport,
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations,
        IDisposable
    {
        private const uint ProjectileGuid = 0x70000021u;
        private double _now = 10d;
        private uint _sequence;
        private bool _disposed;

        public RuntimeOnlySimulationFixtureHost()
        {
            Runtime = new GameRuntime(new GameRuntimeDependencies(
                this,
                this,
                this,
                this,
                CombatTime: () => _now));
            Character.Spellbook.InstallMetadata(SpellMetadata());
            Character.Spellbook.OnSpellLearned(1u);
        }

        public GameRuntime Runtime { get; }
        public RuntimeEntityObjectLifetime EntityObjects =>
            Runtime.EntityObjects;
        public RuntimeInventoryState Inventory =>
            Runtime.InventoryOwner;
        public RuntimeCharacterState Character =>
            Runtime.CharacterOwner;
        public RuntimeCommunicationState Communication =>
            Runtime.CommunicationOwner;
        public RuntimeActionState Actions => Runtime.ActionOwner;
        public RuntimeLocalPlayerMovementState Movement =>
            Runtime.MovementOwner;
        public List<string> Trace { get; } = [];

        public void Move()
        {
            Assert.True(Movement.Execute(
                RuntimeMovementCommand.ToggleRunLock));
            Assert.True(Movement.AutoRunActive);
            Trace.Add("movement:run");
        }

        public void Use(uint serverGuid)
        {
            ItemUseRequestReservation reservation =
                Actions.Transactions.BeginUseRequestReservation();
            RuntimeInteractionDispatchResult result =
                Actions.Transactions.TryDispatchUse(
                    serverGuid,
                    ownedByPlayer: false,
                    useable: true,
                    reservation,
                    this,
                    out _);
            Assert.Equal(
                RuntimeInteractionDispatchResult.Dispatched,
                result);
            Actions.Transactions.CompleteUse(0u);
        }

        public void Attack()
        {
            Actions.Combat.SetCombatMode(CombatMode.Melee);
            Actions.CombatAttack.SetDesiredPower(0.5f);
            Actions.CombatAttack.PressAttack(AttackHeight.Medium);
            _now += 0.5d;
            Actions.CombatAttack.ReleaseAttack();
        }

        public void Cast() =>
            Assert.Equal(
                CastRequestResult.Sent,
                Actions.SpellCast.Cast(1u));

        public void AdvanceProjectile(bool failProjection)
        {
            RuntimeEntityRecord record =
                EntityObjects.RegisterEntity(
                    Spawn(ProjectileGuid, 1)).Canonical!;
            PhysicsBody body =
                EntityObjects.Physics.GetOrCreatePhysicsBody(
                    record,
                    static canonical =>
                    {
                        var created = new PhysicsBody
                        {
                            Position = new Vector3(10f, 20f, 5f),
                            Orientation = Quaternion.Identity,
                            InWorld = true,
                            TransientState =
                                TransientStateFlags.Active,
                        };
                        created.SnapToCell(
                            canonical.FullCellId,
                            created.Position,
                            created.Position);
                        return created;
                    });
            _ = EntityObjects.Physics.BindProjectile(
                record,
                body,
                new ProjectileCollisionSphere(
                    Vector3.Zero,
                    0.25f));
            EntityObjects.Physics.AcknowledgeSpatialProjection(
                record,
                spatial: true);

            var updater =
                new RuntimeProjectilePhysicsUpdater(
                    EntityObjects.Physics);
            Assert.True(updater.TryBegin(
                record,
                quantum: 0.05f,
                record.ObjectClockEpoch,
                externalOwnerValid: null,
                out RuntimeProjectilePhysicsCommit commit));
            _ = updater.Complete(
                commit,
                liveCenterX: 1,
                liveCenterY: 1,
                _ =>
                {
                    if (failProjection)
                    {
                        throw new InvalidOperationException(
                            "projection acknowledgement");
                    }
                    Trace.Add("projectile:ack");
                    return true;
                });
        }

        public RuntimeSimulationOwnershipSnapshot CaptureOwnership() =>
            Runtime.CaptureOwnership().Simulation;

        public bool IsInWorld => true;
        public uint LocalPlayerId => 0x50000001u;
        public bool CanSend => true;
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => true;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            sequence = ++_sequence;
            Trace.Add($"use:{serverGuid:X8}");
            return true;
        }

        public bool TrySendPickup(
            uint itemGuid,
            uint destinationContainerId,
            int placement,
            out uint sequence)
        {
            sequence = ++_sequence;
            return true;
        }

        public bool CanStartAttack() => true;
        public void PrepareAttackRequest() =>
            Trace.Add("attack:prepare");

        public bool SendAttack(AttackHeight height, float power)
        {
            Trace.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"attack:{height}:{power:0.0}"));
            return true;
        }

        public void SendCancelAttack() { }
        public uint? SelectClosestTarget() => null;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public bool HasRequiredComponents(uint spellId) => true;

        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => true;

        public void StopCompletely() =>
            Trace.Add("cast:stop");

        public void SendUntargeted(uint spellId) =>
            Trace.Add($"cast:{spellId}");

        public void SendTargeted(uint targetId, uint spellId) =>
            Trace.Add($"cast:{targetId}:{spellId}");

        public void DisplayMessage(string message) =>
            Trace.Add($"message:{message}");

        public void IncrementBusy()
        {
            Inventory.Transactions.IncrementBusyCount();
            Trace.Add("cast:busy");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Runtime.Dispose();
        }

        private static SpellTable SpellMetadata()
        {
            const string header =
                "Spell ID,Name,Flags [Hex],IsUntargetted,TargetMask [Hex]";
            const string row = "1,Runtime Fixture,0x0,true,0x0";
            return SpellTable.LoadFromReader(
                new System.IO.StringReader($"{header}\n{row}"));
        }

        private static WorldSession.EntitySpawn Spawn(
            uint guid,
            ushort instance)
        {
            var position = new CreateObject.ServerPosition(
                0x01010001u,
                10f,
                20f,
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
                Instance: instance);
            var physics = new PhysicsSpawnData(
                RawState: (uint)(
                    PhysicsStateFlags.ReportCollisions
                    | PhysicsStateFlags.Missile),
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
                Velocity: new Vector3(10f, 0f, 0f),
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
                "runtime projectile",
                null,
                null,
                null,
                PhysicsState: physics.RawState,
                InstanceSequence: instance,
                MovementSequence: 1,
                ServerControlSequence: 1,
                PositionSequence: 1,
                Physics: physics);
        }
    }
}

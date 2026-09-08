using System.Collections.Immutable;
using System.Reflection;
using AcDream.Content;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Headless.Configuration;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

public sealed class HeadlessCollisionNeighborhoodServiceWindowTests
{
    [Fact]
    public void ServiceWindowIsResidencyNotGeometry_UnpublishedLandblockIsRefusedDespiteGeometricMembership()
    {
        var factory = new FixtureContentFactory();
        using var owner = new HeadlessProcessContentOwner(
            ContentDescriptor(),
            _ => { },
            factory);
        using HeadlessProcessContentOwner.HeadlessProcessContentLease lease =
            owner.AcquireLease("fixture");
        var operations = new FixtureGameplayOperations();
        using var runtime = new GameRuntime(new GameRuntimeDependencies(
            operations, operations, operations, operations));
        var neighborhood = new HeadlessCollisionNeighborhood(runtime, lease);

        const uint cell = 0xA9B40001u;
        Assert.True(
            ((IHeadlessCollisionNeighborhood)neighborhood)
                .IsWithinServiceWindow(cell));
        Assert.False(
            ((IRuntimeRemotePlacementServiceWindow)neighborhood)
                .IsWithinServiceWindow(cell));
    }

    [Fact]
    public void ServiceWindowCoversAPublishedNeighborLandblockNotOnlyTheExactCenter()
    {
        var factory = new FixtureContentFactory();
        using var owner = new HeadlessProcessContentOwner(
            ContentDescriptor(),
            _ => { },
            factory);
        using HeadlessProcessContentOwner.HeadlessProcessContentLease lease =
            owner.AcquireLease("fixture");
        var operations = new FixtureGameplayOperations();
        using var runtime = new GameRuntime(new GameRuntimeDependencies(
            operations, operations, operations, operations));
        var neighborhood = new HeadlessCollisionNeighborhood(runtime, lease);

        const uint neighborLandblock = 0xA9B5FFFFu;
        const uint neighborCell = 0xA9B50001u;
        runtime.EntityObjects.Physics.Engine.AddLandblock(
            neighborLandblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        SeedResident(neighborhood, neighborLandblock);

        Assert.True(
            ((IRuntimeRemotePlacementServiceWindow)neighborhood)
                .IsWithinServiceWindow(neighborCell));
    }

    private static void SeedResident(
        HeadlessCollisionNeighborhood neighborhood,
        uint landblockId)
    {
        FieldInfo field = typeof(HeadlessCollisionNeighborhood).GetField(
            "_resident",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(
                nameof(HeadlessCollisionNeighborhood), "_resident");
        var resident = (HashSet<uint>)field.GetValue(neighborhood)!;
        resident.Add(landblockId);
    }

    private static HeadlessContentDescriptor ContentDescriptor() => new()
    {
        DatDirectory = "fixture-dats",
        PreparedAssetPath = "fixture.pak",
    };

    private sealed class FixtureContentFactory
        : IHeadlessProcessContentFactory
    {
        internal FixtureContentFactory()
        {
            DatsResource =
                DispatchProxy.Create<IDatReaderWriter, TestResourceProxy>();
            PreparedResource =
                DispatchProxy.Create<ITestPreparedSource, TestResourceProxy>();
        }

        internal IDatReaderWriter DatsResource { get; }
        internal ITestPreparedSource PreparedResource { get; }

        public HeadlessOpenedProcessContent Open(
            HeadlessContentDescriptor descriptor,
            Action<string> diagnostic) =>
            new(
                DatsResource,
                PreparedResource,
                MagicCatalog.Empty,
                ImmutableArray.CreateRange(new float[256]));
    }

    private sealed class FixtureGameplayOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest()
        {
        }

        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack()
        {
        }

        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest()
        {
        }

        public void SendChangeCombatMode(CombatMode mode)
        {
        }

        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;

        public bool IsTargetCompatible(
            uint targetId, SpellMetadata spell, bool showMessage) => false;

        public void StopCompletely()
        {
        }

        public void SendUntargeted(uint spellId)
        {
        }

        public void SendTargeted(uint targetId, uint spellId)
        {
        }

        public void DisplayMessage(string message)
        {
        }

        public void IncrementBusy()
        {
        }
    }
}

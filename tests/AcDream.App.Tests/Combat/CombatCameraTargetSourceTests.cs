using System.Numerics;
using AcDream.App.Combat;
using AcDream.App.Interaction;
using AcDream.Core.Combat;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Combat;

public sealed class CombatCameraTargetSourceTests
{
    [Fact]
    public void GetTrackedTargetPoint_RequiresOptionTargetedModeAndSelection()
    {
        const uint target = 0x70000001u;
        Vector3 point = new(10f, 20f, 30f);
        var options = new RuntimeCharacterOptionsState();
        options.SetOptionBit((uint)CharacterOptionId.ViewCombatTarget, false);
        var settings = new CharacterOptionCombatSettingsSource(options);
        var combat = new CombatState();
        var selection = new SelectionState();
        var world = new TargetQuery(point);
        var source = new CombatCameraTargetSource(
            settings,
            combat,
            selection,
            world);

        Assert.Null(source.GetTrackedTargetPoint());

        options.SetOptionBit((uint)CharacterOptionId.ViewCombatTarget, true);
        combat.SetCombatMode(CombatMode.Melee);
        selection.Select(target, SelectionChangeSource.World);

        Assert.Equal(point, source.GetTrackedTargetPoint());
        Assert.Equal(target, world.LastGuid);

        combat.SetCombatMode(CombatMode.Magic);
        Assert.Null(source.GetTrackedTargetPoint());
    }

    private sealed class TargetQuery(Vector3 point) : IWorldSelectionQuery
    {
        public uint LastGuid { get; private set; }
        public Vector3? GetCombatCameraTargetPoint(uint serverGuid)
        {
            LastGuid = serverGuid;
            return point;
        }

        public uint? PickAtCursor(bool includeSelf) => null;
        public uint? PickAt(float mouseX, float mouseY, bool includeSelf) => null;
        public void BeginLightingPulse(uint serverGuid) { }
        public bool TryCaptureIdentity(uint serverGuid, out uint localEntityId)
        {
            localEntityId = 0;
            return false;
        }
        public bool IsCurrent(uint serverGuid, uint localEntityId) => false;
        public string Describe(uint serverGuid) => string.Empty;
        public bool IsCreature(uint serverGuid) => false;
        public bool IsHostileMonster(uint serverGuid) => false;
        public bool IsAttackableTarget(uint serverGuid) => false;
        public ClosestCombatTarget? FindClosestHostileMonster() => null;
        public bool IsUseable(uint serverGuid) => false;
        public bool IsPickupable(uint serverGuid) => false;
        public bool IsStuckInWorld(uint serverGuid) => false;
        public bool IsWieldedByPlayer(uint serverGuid) => false;
        public bool IsWieldedPositionState(uint serverGuid) => false;
        public bool TryGetApproach(uint serverGuid, out InteractionApproach approach)
        {
            approach = default;
            return false;
        }
    }
}

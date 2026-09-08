using System.Net;
using AcDream.App.Net;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.Social;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Net;

public sealed class LiveMovementStatsApplierTests
{
    private const uint PlayerGuid = 0x5000000Au;

    private sealed class Harness : IDisposable
    {
        public WorldSession Session { get; }
        public LiveSessionEventRouter Router { get; }
        public ClientObjectTable Objects { get; } = new();
        public RuntimeCharacterState Character { get; } = new();
        public RuntimeLocalPlayerMovementState Movement { get; } = new();
        public LiveMovementStatsApplier Applier { get; }
        public List<string> Log { get; } = [];

        public Harness()
        {
            Session = new WorldSession(new IPEndPoint(IPAddress.Loopback, 9));
            Applier = new LiveMovementStatsApplier(
                Movement,
                Character.MovementSkills,
                Log.Add);
            Router = new LiveSessionEventRouter(
                Session,
                new LiveEntitySessionSink(
                    _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { },
                    _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { },
                    _ => { }),
                new LiveEnvironmentSessionSink(_ => { }, _ => { }),
                new LiveInventorySessionBindings(
                    Objects,
                    PlayerGuid: () => PlayerGuid,
                    OnShortcuts: null,
                    OnUseDone: null,
                    ItemMana: new ItemManaState(),
                    ExternalContainers: new ExternalContainerState()),
                new LiveCharacterSessionBindings(
                    new CombatState(),
                    Character,
                    ResolveSkillFormulaBonus: null,
                    OnSkillsUpdated: (_, _) => Applier.Apply("skills"),
                    OnConfirmationRequest: null,
                    OnConfirmationDone: null,
                    ClientTime: () => 0d,
                    OnMovementStatsUpdated: () => Applier.Apply("stats")),
                new LiveSocialSessionBindings(
                    new ChatLog(),
                    new TurbineChatState(),
                    new FriendsState(),
                    new SquelchState()));
            Router.Attach();
        }

        public void IngestPlayerRow(uint? pwdBitfield = null) =>
            Objects.Ingest(new WeenieData(
                Guid: PlayerGuid, Name: "+Acdream", Type: ItemType.Creature,
                WeenieClassId: 1u, IconId: 0, IconOverlayId: 0,
                IconUnderlayId: 0, Effects: 0,
                Value: null, StackSize: null, StackSizeMax: null, Burden: null,
                ContainerId: null, WielderId: null, ValidLocations: null,
                CurrentWieldedLocation: null, Priority: null,
                ItemsCapacity: null, ContainersCapacity: null,
                Structure: null, MaxStructure: null, Workmanship: null,
                PublicWeenieBitfield: pwdBitfield));

        public void Dispose()
        {
            Router.Dispose();
            Session.Dispose();
            Movement.Dispose();
            Character.Dispose();
        }
    }

    private static PlayerMovementController NewDormantRuntimeController()
    {
        PlayerMovementController controller =
            PlayerMovementController.CreatePublicationCandidate(
                new PhysicsEngine(),
                PlayerMovementConstructionOptions.Fallback);
        controller.SealPublicationCandidate();
        controller.CommitRuntimeOwnership(new RetailObjectQuantumClock());
        return controller;
    }

    [Fact]
    public void PostTeardownIngestRecomputeReportsTypedDropInsteadOfCrashing()
    {
        using var harness = new Harness();
        harness.Character.MovementSkills.Update(runSkill: 240, jumpSkill: 180);

        PlayerMovementController controller = NewDormantRuntimeController();
        harness.Movement.Controller = controller;
        controller.ActivateRuntimePublication();
        controller.RetireRuntimePublication();
        Assert.Throws<InvalidOperationException>(
            () => controller.SetCharacterSkills(1, 1));

        harness.IngestPlayerRow();

        Assert.Contains(
            harness.Log,
            line => line.StartsWith(
                "player: dropped displaced movement stats",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            harness.Log,
            line => line.StartsWith(
                "player: applied server movement",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DormantWindowIngestRecomputeAppliesToTheControllerThatGoesLive()
    {
        using var harness = new Harness();
        harness.Character.MovementSkills.Update(runSkill: 240, jumpSkill: 180);
        harness.Character.MovementSkills.UpdateStamina(37);

        PlayerMovementController controller = NewDormantRuntimeController();
        harness.Movement.Controller = controller;
        Assert.True(controller.IsRuntimeOwnedDormant);

        // BF_PLAYER (0x8) + BF_PLAYER_KILLER (0x20) on the player's own row
        // exercises the full stat set through the recompute.
        harness.IngestPlayerRow(pwdBitfield: 0x28u);

        Assert.Contains(
            harness.Log,
            line => line.StartsWith(
                "player: applied server movement stats",
                StringComparison.Ordinal));

        controller.ActivateRuntimePublication();
        IWeenieObject weenie = controller.Motion.WeenieObj!;
        Assert.True(weenie.InqRunRate(out float runRate));
        Assert.True(runRate > 0f);
        Assert.Equal(
            ObjectInfoState.IsPK,
            controller.OwnPvpFlags & ObjectInfoState.IsPK);
    }

    [Fact]
    public void AbsentControllerAndIncompleteSnapshotStaySilent()
    {
        using var harness = new Harness();

        harness.IngestPlayerRow();
        Assert.DoesNotContain(
            harness.Log,
            line => line.StartsWith("player:", StringComparison.Ordinal));

        harness.Character.MovementSkills.Update(runSkill: 240, jumpSkill: 180);
        Assert.Equal(
            RuntimeMovementStatsApplication.DroppedNoController,
            harness.Applier.Apply("stats"));
        Assert.DoesNotContain(
            harness.Log,
            line => line.StartsWith("player:", StringComparison.Ordinal));
    }
}

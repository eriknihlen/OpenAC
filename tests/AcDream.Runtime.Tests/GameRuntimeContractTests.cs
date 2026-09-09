using System.Reflection;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests;

public sealed class GameRuntimeContractTests
{
    [Fact]
    public void InstanceClock_NormalizesHostTimeAndCanFreezeSimulation()
    {
        var first = new GameRuntimeClock();
        var second = new GameRuntimeClock();

        RuntimeFrameTime frame1 = first.Advance(0.25);
        RuntimeFrameTime frozen = first.Advance(0.5, advanceSimulationTime: false);
        RuntimeFrameTime invalid = first.Advance(double.NaN);

        Assert.Equal(1UL, frame1.FrameNumber);
        Assert.Equal(0.25, frame1.DeltaSeconds);
        Assert.Equal(0.25, frame1.SimulationTimeSeconds);
        Assert.Equal(2UL, frozen.FrameNumber);
        Assert.Equal(0.5, frozen.DeltaSeconds);
        Assert.Equal(0.25, frozen.SimulationTimeSeconds);
        Assert.Equal(3UL, invalid.FrameNumber);
        Assert.Equal(0.0, invalid.DeltaSeconds);
        Assert.Equal(0.25, invalid.SimulationTimeSeconds);
        Assert.Equal(0UL, second.FrameNumber);
        Assert.Equal(0.0, second.SimulationTimeSeconds);
    }

    [Theory]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    public void InstanceClock_InvalidDeltaNormalizesToZero(double input)
    {
        Assert.Equal(0.0, GameRuntimeClock.NormalizeDeltaSeconds(input));
    }

    [Fact]
    public void GenerationAndTeardownAcknowledgementAreExact()
    {
        RuntimeGenerationToken first = RuntimeGenerationToken.Initial.Next();
        var complete = new RuntimeTeardownAcknowledgement(
            first,
            first.Next(),
            RuntimeCommandStatus.Accepted,
            RuntimeTeardownStage.Complete);
        var stale = complete with
        {
            Status = RuntimeCommandStatus.StaleGeneration,
        };
        var partial = complete with
        {
            CompletedStages =
                RuntimeTeardownStage.CommandsInert
                | RuntimeTeardownStage.InboundDetached,
        };

        Assert.Equal(1UL, first.Value);
        Assert.True(complete.IsComplete);
        Assert.False(stale.IsComplete);
        Assert.False(partial.IsComplete);
    }

    [Fact]
    public void EventSequencersAreMonotonicAndInstanceScoped()
    {
        var first = new RuntimeEventSequencer();
        var second = new RuntimeEventSequencer();
        var generation = new RuntimeGenerationToken(7);

        RuntimeEventStamp one = first.Next(generation, frameNumber: 11);
        RuntimeEventStamp two = first.Next(generation, frameNumber: 12);
        RuntimeEventStamp independent = second.Next(generation, frameNumber: 99);

        Assert.Equal(1UL, one.Sequence);
        Assert.Equal(2UL, two.Sequence);
        Assert.Equal(1UL, independent.Sequence);
        Assert.Equal(12UL, two.FrameNumber);
        Assert.Equal(99UL, independent.FrameNumber);
    }

    [Fact]
    public void EventSequencer_RestartsAtOneForEachSessionGeneration()
    {
        var sequencer = new RuntimeEventSequencer();

        RuntimeEventStamp first = sequencer.Next(new(4), 10);
        RuntimeEventStamp second = sequencer.Next(new(4), 11);
        RuntimeEventStamp replacement = sequencer.Next(new(5), 12);

        Assert.Equal(1UL, first.Sequence);
        Assert.Equal(2UL, second.Sequence);
        Assert.Equal(1UL, replacement.Sequence);
        Assert.Equal(new RuntimeGenerationToken(5), replacement.Generation);
    }

    [Fact]
    public void TraceRecorderNormalizesTypedDeltasWithoutRetainingOwners()
    {
        var recorder = new RuntimeTraceRecorder();
        var stamp = new RuntimeEventStamp(
            new RuntimeGenerationToken(3),
            Sequence: 1,
            FrameNumber: 8);
        var command = new RuntimeCommandDelta(
            stamp,
            RuntimeCommandDomain.Selection,
            (int)RuntimeSelectionCommand.SelectClosestHostile,
            RuntimeCommandStatus.Accepted,
            PrimaryObjectId: 0x50000001u);
        var chat = new RuntimeChatDelta(
            stamp with { Sequence = 2 },
            new RuntimeChatEntry(
                Revision: 4,
                SenderGuid: 0x50000002u,
                Kind: 2,
                Sender: "Grey",
                Text: "hello",
                ChannelName: "General"));

        recorder.OnCommand(in command);
        recorder.OnChat(in chat);

        Assert.Collection(
            recorder.Entries,
            entry =>
            {
                Assert.Equal(RuntimeTraceKind.Command, entry.Kind);
                Assert.Equal(0x50000001u, entry.PrimaryObjectId);
                Assert.Equal((long)RuntimeCommandStatus.Accepted, entry.Value);
            },
            entry =>
            {
                Assert.Equal(RuntimeTraceKind.Chat, entry.Kind);
                Assert.Equal(4L, entry.Value);
                Assert.Equal("General|Grey|hello", entry.Text);
            });
    }

    [Fact]
    public void TraceRecorderIncludesGameplayStateInCheckpoint()
    {
        var recorder = new RuntimeTraceRecorder();
        var stamp = new RuntimeEventStamp(new(3), 1, 8);
        var checkpoint = new RuntimeStateCheckpoint(
            new RuntimeGenerationToken(3),
            RuntimeLifecycleState.InWorld,
            FrameNumber: 8,
            EntityCount: 2,
            MaterializedEntityCount: 2,
            InventoryObjectCount: 1,
            InventoryContainerCount: 1,
            new RuntimeInventoryStateSnapshot(
                0u, 0u, 0, true, null, 2, 4, 1, 3),
            new RuntimeCharacterSnapshot(
                CharacterRevision: 5,
                SpellbookRevision: 6,
                default,
                default,
                LearnedSpellCount: 7,
                ActiveEnchantmentCount: 0,
                DesiredComponentCount: 0,
                SkillCount: 8,
                SpellbookFilters: 0x3FFFu),
            new RuntimeSocialSnapshot(9, 10, 11, 0, 0, 0, 0),
            ChatRevision: 12,
            ChatCount: 13,
            new RuntimeActionSnapshot(
                SelectionRevision: 14,
                SelectedObjectId: 0x50000001u,
                PreviousObjectId: 0u,
                PreviousValidObjectId: 0u,
                CombatRevision: 15,
                CombatMode: AcDream.Core.Combat.CombatMode.Missile,
                TrackedTargetHealthCount: 1,
                InteractionRevision: 16,
                InteractionMode: InteractionModeKind.Use,
                InteractionSourceObjectId: 0u,
                InteractionTransactions: default),
            default,
            default,
            new RuntimeWorldEnvironmentOwnershipSnapshot(
                IsInitialized: true,
                DayGroupDefinitionCount: 4,
                ActiveDayGroupCount: 1),
            default,
            new RuntimeWorldTransitOwnershipSnapshot(
                BufferedTeleportDestinationCount: 0,
                PendingTeleportStartCount: 0,
                ActiveTeleportCount: 1,
                AcceptedTeleportDestinationCount: 0,
                ActiveRevealCount: 1,
                PendingDestinationReadinessCount: 1,
                HostProjectionCount: 1,
                PendingHostAcknowledgementCount: 2),
            new(),
            new());

        recorder.AddCheckpoint(stamp, checkpoint);

        RuntimeTraceEntry entry = Assert.Single(recorder.Entries);
        Assert.Contains("inventory-state=2:4:1:3", entry.Text);
        Assert.Contains("character=5:6:7:8:0:0", entry.Text);
        Assert.Contains("social=9:10:11", entry.Text);
        Assert.Contains("environment-owner=True:4:1", entry.Text);
        Assert.Contains("transit-owner=0:0:1:0:1:1:1:2", entry.Text);
        Assert.Contains(
            "actions=14:50000001:15:4:1:16:1:00000000:0:00000000:" +
            "00000000:00000000:00000000:0:0:0:0:00000000:00000000:" +
            "False:False:00000000:0:00000000:00000000",
            entry.Text);
    }

    [Fact]
    public void GameplayOwnersHaveNoStaticMutableSessionState()
    {
        Type[] owners =
        [
            typeof(RuntimeCommunicationState),
            typeof(RuntimeInventoryState),
            typeof(RuntimeCharacterState),
            typeof(RuntimeCharacterOptionsState),
            typeof(RuntimeMovementSkillState),
            typeof(RuntimeActionState),
            typeof(InteractionState),
            typeof(RuntimeCombatAttackState),
            typeof(RuntimeCombatTargetState),
            typeof(RuntimeCombatModeState),
            typeof(RuntimeSpellCastState),
            typeof(RuntimeFellowshipState),
            typeof(RuntimeAllegianceState),
        ];

        foreach (Type owner in owners)
        {
            FieldInfo[] mutable = owner
                .GetFields(
                    BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic)
                .Where(static field => !field.IsLiteral)
                .ToArray();
            Assert.Empty(mutable);
        }
    }
}

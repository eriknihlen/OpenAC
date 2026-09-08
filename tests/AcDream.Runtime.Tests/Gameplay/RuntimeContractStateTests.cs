using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeContractStateTests
{
    private static readonly DateTime Arrival = new(2026, 8, 21, 13, 0, 0, DateTimeKind.Utc);

    private static ContractTracker Tracker(
        uint contractId,
        ContractStage stage = ContractStage.InProgress,
        double whenRepeats = 0)
        => new(1u, contractId, stage, 0, whenRepeats, Arrival);

    private static ContractTrackerUpdate Update(
        uint contractId,
        ContractStage stage = ContractStage.InProgress,
        bool delete = false,
        bool setAsDisplay = false)
        => new(Tracker(contractId, stage), delete, setAsDisplay);

    [Fact]
    public void AnUpdateAddsAContractAndASecondUpdateReplacesIt()
    {
        using var state = new RuntimeContractState();

        state.ApplyUpdate(Update(0x10u, ContractStage.Available));
        state.ApplyUpdate(Update(0x10u, ContractStage.InProgress));

        Assert.True(state.View.TryGetContract(0x10u, out ContractTracker tracker));
        Assert.Equal(ContractStage.InProgress, tracker.Stage);
        Assert.Equal(1, state.View.Snapshot.ContractCount);
    }

    [Fact]
    public void TheDeleteFlagRemovesTheContractItNames()
    {
        using var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x10u));

        state.ApplyUpdate(Update(0x10u, delete: true));

        Assert.False(state.View.TryGetContract(0x10u, out _));
        Assert.Equal(0, state.View.Snapshot.ContractCount);
    }

    [Fact]
    public void DeletingTheDisplayContractClearsTheDisplaySelection()
    {
        using var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x10u, setAsDisplay: true));
        Assert.Equal(0x10u, state.View.Snapshot.DisplayContractId);

        state.ApplyUpdate(Update(0x10u, delete: true));

        Assert.Equal(0u, state.View.Snapshot.DisplayContractId);
    }

    [Fact]
    public void ATableReplacesEverythingRatherThanMergingIntoIt()
    {
        using var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x10u));
        state.ApplyUpdate(Update(0x20u));

        state.ApplyTable(new Dictionary<uint, ContractTracker>
        {
            [0x30u] = Tracker(0x30u),
        });

        Assert.False(state.View.TryGetContract(0x10u, out _));
        Assert.True(state.View.TryGetContract(0x30u, out _));
        Assert.Equal(1, state.View.Snapshot.ContractCount);
    }

    [Fact]
    public void AnEmptyTableClearsTheTracker()
    {
        using var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x10u, setAsDisplay: true));

        state.ApplyTable(new Dictionary<uint, ContractTracker>());

        Assert.Equal(0, state.View.Snapshot.ContractCount);
        Assert.Equal(0u, state.View.Snapshot.DisplayContractId);
    }

    [Fact]
    public void ATableThatDropsTheDisplayContractClearsTheSelection()
    {
        using var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x10u, setAsDisplay: true));

        state.ApplyTable(new Dictionary<uint, ContractTracker> { [0x20u] = Tracker(0x20u) });

        Assert.Equal(0u, state.View.Snapshot.DisplayContractId);
    }

    [Fact]
    public void ATableThatKeepsTheDisplayContractKeepsTheSelection()
    {
        // A routine full refresh must not deselect what the player is looking
        // at.
        using var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x10u, setAsDisplay: true));

        state.ApplyTable(new Dictionary<uint, ContractTracker> { [0x10u] = Tracker(0x10u) });

        Assert.Equal(0x10u, state.View.Snapshot.DisplayContractId);
    }

    [Fact]
    public void ContractsComeBackInAStableOrder()
    {
        using var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x30u));
        state.ApplyUpdate(Update(0x10u));
        state.ApplyUpdate(Update(0x20u));

        IReadOnlyList<ContractTracker> contracts = state.View.GetContracts();

        Assert.Equal(
            new uint[] { 0x10u, 0x20u, 0x30u },
            contracts.Select(c => c.ContractId).ToArray());
    }

    [Fact]
    public void EveryMutationAdvancesTheRevision()
    {
        using var state = new RuntimeContractState();
        long start = state.View.Snapshot.Revision;

        state.ApplyUpdate(Update(0x10u));
        long afterAdd = state.View.Snapshot.Revision;
        state.ApplyTable(new Dictionary<uint, ContractTracker>());
        long afterTable = state.View.Snapshot.Revision;

        Assert.True(afterAdd > start);
        Assert.True(afterTable > afterAdd);
    }

    [Fact]
    public void DeletingSomethingAbsentDoesNotAdvanceTheRevision()
    {
        // A no-op that bumps would redraw the panel on every stray message.
        using var state = new RuntimeContractState();
        long start = state.View.Snapshot.Revision;

        state.ApplyUpdate(Update(0x99u, delete: true));

        Assert.Equal(start, state.View.Snapshot.Revision);
    }

    [Fact]
    public void ResetSessionClearsBecauseQuestStateIsPurelyServerSide()
    {
        using var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x10u, setAsDisplay: true));

        state.ResetSession();

        Assert.Equal(0, state.View.Snapshot.ContractCount);
        Assert.Equal(0u, state.View.Snapshot.DisplayContractId);
    }

    [Fact]
    public void ResetSessionIsSafeToRepeatBecauseTheResetTransactionRetries()
    {
        using var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x10u));

        state.ResetSession();
        state.ResetSession();

        Assert.Equal(0, state.View.Snapshot.ContractCount);
    }

    [Fact]
    public void OwnershipConvergesOnlyAfterDisposal()
    {
        var state = new RuntimeContractState();
        state.ApplyUpdate(Update(0x10u, setAsDisplay: true));
        Assert.False(state.CaptureOwnership().IsConverged);

        state.Dispose();

        RuntimeContractOwnershipSnapshot ownership = state.CaptureOwnership();
        Assert.True(ownership.IsConverged);
        Assert.Equal(0, ownership.ContractCount);
        Assert.Equal(0u, ownership.DisplayContractId);
    }

    [Fact]
    public void MutationsAfterDisposalAreIgnoredRatherThanThrowing()
    {
        // Teardown is terminal and a late inbound packet must not resurrect
        // state or take the process down.
        var state = new RuntimeContractState();
        state.Dispose();

        state.ApplyUpdate(Update(0x10u));
        state.ApplyTable(new Dictionary<uint, ContractTracker> { [0x20u] = Tracker(0x20u) });

        Assert.True(state.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void DisposalIsIdempotent()
    {
        var state = new RuntimeContractState();
        state.Dispose();
        state.Dispose();
        Assert.True(state.CaptureOwnership().IsConverged);
    }
}

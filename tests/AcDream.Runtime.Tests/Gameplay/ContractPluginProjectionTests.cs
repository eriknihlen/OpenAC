using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Net.Messages;
using AcDream.Core.Quests;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class ContractPluginProjectionTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static void Track(
        RuntimeContractState state,
        uint contractId,
        uint stage,
        bool setAsDisplay = false)
        => state.ApplyUpdate(new ContractTrackerUpdate(
            new ContractTracker(1u, contractId, (ContractStage)stage, 0d, 0d, Now),
            Delete: false,
            SetAsDisplay: setAsDisplay));

    private static ContractCatalog Catalog(uint id, string name, string progressFormat = "")
        => new(new Dictionary<uint, ContractEntry>
        {
            [id] = ContractEntry.Unknown with
            {
                ContractId = id,
                ContractName = name,
                Description = "Do the thing.",
                DescriptionProgress = progressFormat,
            },
        });

    [Fact]
    public void AnEmptyTrackerProjectsToNothing()
    {
        using var state = new RuntimeContractState();

        Assert.Empty(ContractPluginProjection.Project(state.View, ContractCatalog.Empty, Now));
    }

    [Fact]
    public void TheProjectionCarriesTheAuthoredTextAndTheRetailStatus()
    {
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 9u);          // ProgressCounter + 5

        ContractSnapshot snapshot = Assert.Single(ContractPluginProjection.Project(
            state.View, Catalog(0x10u, "Tusker Hunt", "%d/20 Tuskers"), Now));

        Assert.Equal(0x10u, snapshot.ContractId);
        Assert.Equal(9u, snapshot.Stage);
        Assert.Equal(5u, snapshot.Progress);
        Assert.Equal("Tusker Hunt", snapshot.Name);
        Assert.Equal("Do the thing.", snapshot.Description);
        Assert.Equal("5/20 Tuskers", snapshot.Status);
    }

    [Fact]
    public void TheDisplayContractIsFlagged()
    {
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 2u);
        Track(state, 0x20u, stage: 2u, setAsDisplay: true);

        IReadOnlyList<ContractSnapshot> projected = ContractPluginProjection.Project(
            state.View, ContractCatalog.Empty, Now);

        Assert.False(projected.Single(c => c.ContractId == 0x10u).IsDisplayed);
        Assert.True(projected.Single(c => c.ContractId == 0x20u).IsDisplayed);
    }

    [Fact]
    public void WithNoCatalogTheNumbersStillProject()
    {
        // A headless bot has no dat access. Losing the text is expected;
        // losing the QUEST would mean a bot silently unable to see what it is
        // on, which is the failure this rules out.
        using var state = new RuntimeContractState();
        Track(state, 0x10u, stage: 6u);

        ContractSnapshot snapshot = Assert.Single(
            ContractPluginProjection.Project(state.View, catalog: null, Now));

        Assert.Equal(0x10u, snapshot.ContractId);
        Assert.Equal(6u, snapshot.Stage);
        Assert.Equal(2u, snapshot.Progress);
        Assert.Equal(string.Empty, snapshot.Name);
        Assert.Equal(string.Empty, snapshot.Status);
    }

    [Fact]
    public void AContractTheCatalogDoesNotKnowStillProjects()
    {
        using var state = new RuntimeContractState();
        Track(state, 0xDEADu, stage: 2u);

        ContractSnapshot snapshot = Assert.Single(ContractPluginProjection.Project(
            state.View, Catalog(0x10u, "Something Else"), Now));

        Assert.Equal(0xDEADu, snapshot.ContractId);
        Assert.Equal(string.Empty, snapshot.Name);
        Assert.Equal("In Progress", snapshot.Status);
    }

    [Fact]
    public void TheProjectionOrderMatchesTheTrackersOwn()
    {
        using var state = new RuntimeContractState();
        Track(state, 0x30u, stage: 2u);
        Track(state, 0x10u, stage: 2u);
        Track(state, 0x20u, stage: 2u);

        IReadOnlyList<ContractSnapshot> projected = ContractPluginProjection.Project(
            state.View, ContractCatalog.Empty, Now);

        Assert.Equal(
            new uint[] { 0x10u, 0x20u, 0x30u },
            projected.Select(c => c.ContractId).ToArray());
    }
}

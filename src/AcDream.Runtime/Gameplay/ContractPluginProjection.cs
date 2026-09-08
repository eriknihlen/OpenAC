using System;
using System.Collections.Generic;
using AcDream.Core.Net.Messages;
using AcDream.Core.Quests;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Gameplay;

public static class ContractPluginProjection
{
    public static IReadOnlyList<ContractSnapshot> Project(
        IRuntimeContractView contracts,
        ContractCatalog? catalog,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(contracts);

        IReadOnlyList<ContractTracker> tracked = contracts.GetContracts();
        if (tracked.Count == 0)
            return [];

        uint displayed = contracts.Snapshot.DisplayContractId;
        var result = new ContractSnapshot[tracked.Count];
        for (int i = 0; i < tracked.Count; i++)
        {
            ContractTracker tracker = tracked[i];
            ContractEntry? entry = catalog?.Lookup(tracker.ContractId);

            result[i] = new ContractSnapshot(
                tracker.ContractId,
                (uint)tracker.Stage,
                tracker.Progress,
                tracker.ContractId == displayed,
                entry?.ContractName ?? string.Empty,
                entry?.Description ?? string.Empty,
                entry is null
                    ? string.Empty
                    : ContractProgressText.Build(
                        (uint)tracker.Stage,
                        tracker.TimeWhenRepeats,
                        tracker.ReceivedAt,
                        entry,
                        now));
        }

        return result;
    }
}

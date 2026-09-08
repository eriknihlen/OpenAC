using System.Collections.Generic;

namespace AcDream.Core.Quests;

public sealed record ContractEntry(
    uint Version,
    uint ContractId,
    string ContractName,
    string Description,
    string DescriptionProgress,
    string NameNpcStart,
    string NameNpcEnd,
    string QuestflagStamped,
    string QuestflagStarted,
    string QuestflagFinished,
    string QuestflagProgress,
    string QuestflagTimer,
    string QuestflagRepeatTime,
    uint LocationNpcStartCell = 0u,
    uint LocationNpcEndCell = 0u,
    /// <summary>Landcell of the quest area — the "Quest Location" row.</summary>
    uint LocationQuestAreaCell = 0u)
{
    public static readonly ContractEntry Unknown = new(
        0u, 0u,
        string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty);
}

public sealed class ContractCatalog(IReadOnlyDictionary<uint, ContractEntry> contracts)
{
    public static readonly ContractCatalog Empty =
        new(new Dictionary<uint, ContractEntry>());

    public IReadOnlyDictionary<uint, ContractEntry> Contracts { get; } = contracts;

    public int Count => Contracts.Count;

    public ContractEntry Lookup(uint contractId) =>
        Contracts.TryGetValue(contractId, out ContractEntry? entry)
            ? entry
            : ContractEntry.Unknown;
}

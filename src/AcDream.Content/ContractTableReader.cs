using System.Collections.Frozen;
using System.Collections.Generic;
using AcDream.Core.Quests;
using DatContractTable = DatReaderWriter.DBObjs.ContractTable;

namespace AcDream.Content;

public static class ContractTableReader
{
    public const uint ContractTableDid = 0x0E00001Du;

    public static ContractCatalog Load(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);

        DatContractTable? table = dats.Get<DatContractTable>(ContractTableDid);
        if (table is null || table.Contracts.Count == 0)
            return ContractCatalog.Empty;

        var projected = new Dictionary<uint, ContractEntry>(table.Contracts.Count);
        foreach ((uint key, DatReaderWriter.Types.Contract contract) in table.Contracts)
        {
            projected[key] = new ContractEntry(
                contract.Version,
                contract.ContractId,
                contract.ContractName ?? string.Empty,
                contract.Description ?? string.Empty,
                contract.DescriptionProgress ?? string.Empty,
                contract.NameNPCStart ?? string.Empty,
                contract.NameNPCEnd ?? string.Empty,
                contract.QuestflagStamped ?? string.Empty,
                contract.QuestflagStarted ?? string.Empty,
                contract.QuestflagFinished ?? string.Empty,
                contract.QuestflagProgress ?? string.Empty,
                contract.QuestflagTimer ?? string.Empty,
                contract.QuestflagRepeatTime ?? string.Empty,
                contract.LocationNPCStart?.CellId ?? 0u,
                contract.LocationNPCEnd?.CellId ?? 0u,
                contract.LocationQuestArea?.CellId ?? 0u);
        }

        return new ContractCatalog(projected.ToFrozenDictionary());
    }
}

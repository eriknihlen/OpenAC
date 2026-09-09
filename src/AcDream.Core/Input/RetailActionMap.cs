using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Input;

public enum RetailActionClass : uint
{
    None = 0,
    Movement = 1,
    Camera = 2,
    Ui = 3,
    Combat = 4,
    Emote = 5,
    // 6 is genuinely absent from the shipped 2013 DAT — not a gap in this table.
    CharacterSettings = 7,
}

public static class RetailActionMapIds
{
    public const uint ActionMapId = 0x26000000u;

    public const uint GameplayMasterMapId = 0x14000000u;

    public const uint SystemMasterMapId = 0x14000002u;
}

public readonly record struct RetailKeyChord(uint Scan, uint Device, uint Modifier, uint Activation);

public sealed record RetailActionMapRow(
    uint InputMapId,
    uint ActionId,
    RetailActionClass ActionClass,
    uint LabelHash,
    uint TooltipHash,
    IReadOnlyList<RetailKeyChord> DefaultBindings);

public sealed record RetailActionMapSnapshot(
    IReadOnlyList<RetailActionMapRow> Rows,
    IReadOnlyDictionary<uint, IReadOnlySet<uint>>? ConflictingInputMaps = null)
{
    public bool InputMapsConflict(uint leftInputMapId, uint rightInputMapId) =>
        leftInputMapId == rightInputMapId
        || (ConflictingInputMaps?.TryGetValue(
                leftInputMapId,
                out IReadOnlySet<uint>? conflicts) == true
            && conflicts.Contains(rightInputMapId));
}

public static class RetailInputMapHeaders
{
    public const uint StringTableId = 0x23000005u;

    public static readonly IReadOnlyDictionary<uint, string> NameByInputMapId =
        new Dictionary<uint, string>
        {
            [0x00000004u] = "ID_InputMap_MovementCommands",
            [0x00000005u] = "ID_InputMap_CameraControls",
            [0x00000006u] = "ID_InputMap_CameraAlternateControls",
            [0x00000009u] = "ID_InputMap_DialogBoxes",
            [0x0000000Bu] = "ID_InputMap_DebugConsole",
            [0x0000000Cu] = "ID_InputMap_ProfilerUI",
            [0x0000000Du] = "ID_InputMap_UIDebugger",
            [0x0000000Eu] = "ID_InputMap_DebugCommands",
            [0x10000002u] = "ID_InputMap_Combat",
            [0x10000003u] = "ID_InputMap_MeleeCombat",
            [0x10000004u] = "ID_InputMap_MissileCombat",
            [0x10000005u] = "ID_InputMap_MagicCombat",
            [0x10000006u] = "ID_InputMap_Emotes",
            [0x10000007u] = "ID_InputMap_ItemSelectionCommands",
            [0x10000008u] = "ID_InputMap_CharacterOptionCommands",
            [0x10000009u] = "ID_InputMap_UICommands",
            [0x1000000Au] = "ID_InputMap_ChatCommands",
            [0x1000000Cu] = "ID_InputMap_QuickslotCommands",
            [0x1000000Du] = "ID_InputMap_ToggleChatEntry",
        };
}

public static class RetailActionMapReader
{
    public static RetailActionMapSnapshot? Read(IDatObjectSource dats)
    {
        ArgumentNullException.ThrowIfNull(dats);

        ActionMap? actionMap = dats.Get<ActionMap>(RetailActionMapIds.ActionMapId);
        if (actionMap is null)
            return null;

        MasterInputMap? gameplayMap = dats.Get<MasterInputMap>(RetailActionMapIds.GameplayMasterMapId);
        MasterInputMap? systemMap = dats.Get<MasterInputMap>(RetailActionMapIds.SystemMasterMapId);

        var rows = new List<RetailActionMapRow>();
        foreach (var inputMapEntry in actionMap.InputMaps)
        {
            uint inputMapId = inputMapEntry.Key;
            foreach (var actionEntry in inputMapEntry.Value)
            {
                uint actionId = actionEntry.Key;
                var value = actionEntry.Value;
                var userBinding = value.UserBinding;
                uint classId = userBinding?.ActionClass ?? 0u;
                if (classId == 0u)
                    continue;

                var defaults = new List<RetailKeyChord>();
                CollectDefaults(gameplayMap, inputMapId, actionId, defaults);
                CollectDefaults(systemMap, inputMapId, actionId, defaults);

                rows.Add(new RetailActionMapRow(
                    inputMapId,
                    actionId,
                    (RetailActionClass)classId,
                    userBinding!.ActionName,
                    userBinding.ActionDescription,
                    defaults));
            }
        }

        var conflictingInputMaps = new Dictionary<uint, IReadOnlySet<uint>>();
        foreach (var entry in actionMap.ConflictingMaps)
        {
            InputsConflictsValue value = entry.Value;
            uint inputMapId = value.InputMap != 0u ? value.InputMap : entry.Key;
            conflictingInputMaps[inputMapId] =
                new HashSet<uint>(value.ConflictingInputMaps);
        }

        return new RetailActionMapSnapshot(rows, conflictingInputMaps);
    }

    private static void CollectDefaults(
        MasterInputMap? map, uint inputMapId, uint actionId, List<RetailKeyChord> into)
    {
        if (map is null) return;
        if (!map.InputMaps.TryGetValue(inputMapId, out var cInputMap)) return;
        foreach (var control in cInputMap.Mappings)
        {
            if (control.Unknown != actionId) continue;
            uint scan = (control.Key.Key >> 16) & 0xFFFFu;
            uint device = control.Key.Key & 0xFFFFu;
            into.Add(new RetailKeyChord(scan, device, control.Key.Modifier, control.Activation));
        }
    }
}

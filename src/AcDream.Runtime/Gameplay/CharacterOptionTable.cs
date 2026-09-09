using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Gameplay;

public readonly record struct CharacterOptionTableEntry(
    CharacterOptionId Id,
    bool IsOptions1,
    uint Mask,
    bool IsAutoSave,
    bool ClientDefault);

public static class CharacterOptionTable
{
    private static readonly Dictionary<uint, CharacterOptionTableEntry> Entries = Build();

    public static bool TryGet(uint optionId, out CharacterOptionTableEntry entry) =>
        Entries.TryGetValue(optionId, out entry);

    public static bool TryGet(CharacterOptionId optionId, out CharacterOptionTableEntry entry) =>
        TryGet((uint)optionId, out entry);

    public static IReadOnlyList<CharacterOptionTableEntry> All { get; } =
        [.. Entries.Values.OrderBy(static e => (uint)e.Id)];

    private static Dictionary<uint, CharacterOptionTableEntry> Build()
    {
        var table = new Dictionary<uint, CharacterOptionTableEntry>(53);

        void Add(
            CharacterOptionId id,
            bool isOptions1,
            uint mask,
            bool autoSave,
            bool clientDefault) =>
            table.Add(
                (uint)id,
                new CharacterOptionTableEntry(id, isOptions1, mask, autoSave, clientDefault));

        Add(CharacterOptionId.AutoRepeatAttack, true, 0x00000002u, true, true);
        Add(CharacterOptionId.IgnoreAllegianceRequests, true, 0x00000004u, true, false);
        Add(CharacterOptionId.IgnoreFellowshipRequests, true, 0x00000008u, true, true);
        Add(CharacterOptionId.IgnoreTradeRequests, true, 0x00020000u, false, false);
        Add(CharacterOptionId.DisableMostWeatherEffects, true, 0x00010000u, false, false);
        Add(CharacterOptionId.PersistentAtDay, false, 0x00000001u, false, false);
        Add(CharacterOptionId.AllowGive, true, 0x00000040u, false, true);
        Add(CharacterOptionId.ViewCombatTarget, true, 0x00000080u, false, false);
        Add(CharacterOptionId.ShowTooltips, true, 0x00000100u, false, true);
        Add(CharacterOptionId.UseDeception, true, 0x00000200u, false, false);
        Add(CharacterOptionId.ToggleRun, true, 0x00000400u, false, true);
        Add(CharacterOptionId.StayInChatMode, true, 0x00000800u, false, false);
        Add(CharacterOptionId.AdvancedCombatUI, true, 0x00001000u, false, false);
        Add(CharacterOptionId.AutoTarget, true, 0x00002000u, false, true);
        Add(CharacterOptionId.VividTargetingIndicator, true, 0x00008000u, false, true);
        Add(CharacterOptionId.FellowshipShareXP, true, 0x00040000u, true, true);
        Add(CharacterOptionId.AcceptLootPermits, true, 0x00080000u, true, false);
        Add(CharacterOptionId.FellowshipShareLoot, true, 0x00100000u, true, false);
        Add(CharacterOptionId.FellowshipAutoAcceptRequests, true, 0x20000000u, true, false);
        Add(CharacterOptionId.SideBySideVitals, true, 0x00200000u, false, false);
        Add(CharacterOptionId.CoordinatesOnRadar, true, 0x00400000u, false, true);
        Add(CharacterOptionId.SpellDuration, true, 0x00800000u, false, true);
        Add(CharacterOptionId.DisableHouseRestrictionEffects, true, 0x02000000u, false, false);
        Add(CharacterOptionId.DragItemOnPlayerOpensSecureTrade, true, 0x04000000u, false, false);
        Add(CharacterOptionId.DisplayAllegianceLogonNotifications, true, 0x08000000u, false, false);
        Add(CharacterOptionId.UseChargeAttack, true, 0x10000000u, true, true);
        Add(CharacterOptionId.UseCraftSuccessDialog, true, 0x80000000u, false, false);
        Add(CharacterOptionId.ListenToAllegianceChat, true, 0x40000000u, true, true);
        Add(CharacterOptionId.DisplayDateOfBirth, false, 0x00000002u, false, false);
        Add(CharacterOptionId.DisplayAge, false, 0x00000020u, false, false);
        Add(CharacterOptionId.DisplayChessRank, false, 0x00000004u, false, false);
        Add(CharacterOptionId.DisplayFishingSkill, false, 0x00000008u, false, false);
        Add(CharacterOptionId.DisplayNumberDeaths, false, 0x00000010u, false, false);
        Add(CharacterOptionId.DisplayTimeStamps, false, 0x00000040u, false, false);
        Add(CharacterOptionId.SalvageMultiple, false, 0x00000080u, false, false);
        Add(CharacterOptionId.ListenToGeneralChat, false, 0x00000100u, true, true);
        Add(CharacterOptionId.ListenToTradeChat, false, 0x00000200u, true, true);
        Add(CharacterOptionId.ListenToLFGChat, false, 0x00000400u, true, true);
        Add(CharacterOptionId.ListenToRoleplayChat, false, 0x00000800u, true, false);
        Add(CharacterOptionId.AppearOffline, false, 0x00001000u, true, false);
        Add(CharacterOptionId.DisplayNumberCharacterTitles, false, 0x00002000u, false, false);
        Add(CharacterOptionId.MainPackPreferred, false, 0x00004000u, false, false);
        Add(CharacterOptionId.LeadMissileTargets, false, 0x00008000u, true, true);
        Add(CharacterOptionId.UseFastMissiles, false, 0x00010000u, true, false);
        Add(CharacterOptionId.FilterLanguage, false, 0x00020000u, false, false);
        Add(CharacterOptionId.ConfirmVolatileRareUse, false, 0x00040000u, false, false);
        Add(CharacterOptionId.ListenToSocietyChat, false, 0x00080000u, true, false);
        Add(CharacterOptionId.ShowHelm, false, 0x00100000u, true, false);
        Add(CharacterOptionId.DisableDistanceFog, false, 0x00200000u, false, false);
        Add(CharacterOptionId.UseMouseTurning, false, 0x00400000u, true, false);
        Add(CharacterOptionId.ShowCloak, false, 0x00800000u, true, false);
        Add(CharacterOptionId.LockUI, false, 0x01000000u, true, false);
        Add(CharacterOptionId.HearPkDeathMessages, false, 0x02000000u, false, false);

        return table;
    }
}

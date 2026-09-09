using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class CharacterOptionTableTests
{
    private static readonly CharacterOptionId[] AutoSaveIds =
    [
        CharacterOptionId.AutoRepeatAttack,
        CharacterOptionId.IgnoreAllegianceRequests,
        CharacterOptionId.IgnoreFellowshipRequests,
        CharacterOptionId.FellowshipShareXP,
        CharacterOptionId.AcceptLootPermits,
        CharacterOptionId.FellowshipShareLoot,
        CharacterOptionId.FellowshipAutoAcceptRequests,
        CharacterOptionId.UseChargeAttack,
        CharacterOptionId.ListenToAllegianceChat,
        CharacterOptionId.ListenToGeneralChat,
        CharacterOptionId.ListenToTradeChat,
        CharacterOptionId.ListenToLFGChat,
        CharacterOptionId.ListenToRoleplayChat,
        CharacterOptionId.AppearOffline,
        CharacterOptionId.LeadMissileTargets,
        CharacterOptionId.UseFastMissiles,
        CharacterOptionId.ListenToSocietyChat,
        CharacterOptionId.ShowHelm,
        CharacterOptionId.UseMouseTurning,
        CharacterOptionId.ShowCloak,
        CharacterOptionId.LockUI,
    ];

    private static readonly CharacterOptionId[] ClientDefaultOnIds =
    [
        CharacterOptionId.AutoRepeatAttack,
        CharacterOptionId.IgnoreFellowshipRequests,
        CharacterOptionId.AllowGive,
        CharacterOptionId.ShowTooltips,
        CharacterOptionId.ToggleRun,
        CharacterOptionId.AutoTarget,
        CharacterOptionId.VividTargetingIndicator,
        CharacterOptionId.FellowshipShareXP,
        CharacterOptionId.CoordinatesOnRadar,
        CharacterOptionId.SpellDuration,
        CharacterOptionId.UseChargeAttack,
        CharacterOptionId.ListenToAllegianceChat,
        CharacterOptionId.ListenToGeneralChat,
        CharacterOptionId.ListenToTradeChat,
        CharacterOptionId.ListenToLFGChat,
        CharacterOptionId.LeadMissileTargets,
    ];

    [Fact]
    public void All_HasExactly53Entries_Ids0x00Through0x34Contiguous()
    {
        CharacterOptionTableEntry[] all = [.. CharacterOptionTable.All];

        Assert.Equal(53, all.Length);
        for (uint id = 0x00; id <= 0x34; id++)
        {
            Assert.True(
                CharacterOptionTable.TryGet(id, out CharacterOptionTableEntry entry),
                $"id 0x{id:X2} missing from CharacterOptionTable");
            Assert.Equal(id, (uint)entry.Id);
        }
    }

    [Theory]
    [MemberData(nameof(AllModeledIds))]
    public void IsAutoSave_MatchesByteVerifiedSplit(CharacterOptionId id)
    {
        bool expected = Array.IndexOf(AutoSaveIds, id) >= 0;

        Assert.True(CharacterOptionTable.TryGet(id, out CharacterOptionTableEntry entry));
        Assert.Equal(expected, entry.IsAutoSave);
    }

    [Theory]
    [MemberData(nameof(AllModeledIds))]
    public void ClientDefault_MatchesByteVerifiedSplit(CharacterOptionId id)
    {
        bool expected = Array.IndexOf(ClientDefaultOnIds, id) >= 0;

        Assert.True(CharacterOptionTable.TryGet(id, out CharacterOptionTableEntry entry));
        Assert.Equal(expected, entry.ClientDefault);
    }

    [Fact]
    public void AutoSaveIds_CountIs21()
    {
        Assert.Equal(21, AutoSaveIds.Length);
        Assert.Equal(21, CharacterOptionTable.All.Count(static e => e.IsAutoSave));
    }

    [Fact]
    public void ClientDefaultOnIds_CountIs16()
    {
        Assert.Equal(16, ClientDefaultOnIds.Length);
        Assert.Equal(16, CharacterOptionTable.All.Count(static e => e.ClientDefault));
    }

    [Fact]
    public void ReconstructedClientDefaultWords_MatchIndependentlyConfirmedConstants()
    {
        uint options1 = 0u;
        uint options2 = 0u;
        foreach (CharacterOptionTableEntry entry in CharacterOptionTable.All)
        {
            if (!entry.ClientDefault) continue;
            if (entry.IsOptions1) options1 |= entry.Mask;
            else options2 |= entry.Mask;
        }

        Assert.Equal(0x50C4A54Au, options1);
        Assert.Equal(0x00008700u, options2);
    }

    [Theory]
    [InlineData(0x35u)]
    [InlineData(0x36u)]
    [InlineData(0xFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    public void TryGet_RejectsUnknownAndReservedIds(uint optionId)
    {
        Assert.False(CharacterOptionTable.TryGet(optionId, out _));
    }

    [Fact]
    public void SpotCheck_WordAndMaskAgainstVerbatimAcclientEnums()
    {
        Assert.True(CharacterOptionTable.TryGet(
            CharacterOptionId.AutoRepeatAttack, out CharacterOptionTableEntry autoRepeat));
        Assert.True(autoRepeat.IsOptions1);
        Assert.Equal(0x00000002u, autoRepeat.Mask);

        Assert.True(CharacterOptionTable.TryGet(
            CharacterOptionId.PersistentAtDay, out CharacterOptionTableEntry persistentAtDay));
        Assert.False(persistentAtDay.IsOptions1);
        Assert.Equal(0x00000001u, persistentAtDay.Mask);

        Assert.True(CharacterOptionTable.TryGet(
            CharacterOptionId.ListenToAllegianceChat, out CharacterOptionTableEntry allegiance));
        Assert.True(allegiance.IsOptions1);
        Assert.Equal(0x40000000u, allegiance.Mask);

        Assert.True(CharacterOptionTable.TryGet(
            CharacterOptionId.HearPkDeathMessages, out CharacterOptionTableEntry pkDeath));
        Assert.False(pkDeath.IsOptions1);
        Assert.Equal(0x02000000u, pkDeath.Mask);
        Assert.False(pkDeath.IsAutoSave);
    }

    public static IEnumerable<object[]> AllModeledIds()
    {
        for (uint id = 0x00; id <= 0x34; id++)
            yield return [(CharacterOptionId)id];
    }

    [Theory]
    [InlineData(CharacterOptionId.AutoRepeatAttack, true, 0x00000002u)]
    [InlineData(CharacterOptionId.IgnoreAllegianceRequests, true, 0x00000004u)]
    [InlineData(CharacterOptionId.IgnoreFellowshipRequests, true, 0x00000008u)]
    [InlineData(CharacterOptionId.IgnoreTradeRequests, true, 0x00020000u)]
    [InlineData(CharacterOptionId.DisableMostWeatherEffects, true, 0x00010000u)]
    [InlineData(CharacterOptionId.PersistentAtDay, false, 0x00000001u)]
    [InlineData(CharacterOptionId.AllowGive, true, 0x00000040u)]
    [InlineData(CharacterOptionId.ViewCombatTarget, true, 0x00000080u)]
    [InlineData(CharacterOptionId.ShowTooltips, true, 0x00000100u)]
    [InlineData(CharacterOptionId.UseDeception, true, 0x00000200u)]
    [InlineData(CharacterOptionId.ToggleRun, true, 0x00000400u)]
    [InlineData(CharacterOptionId.StayInChatMode, true, 0x00000800u)]
    [InlineData(CharacterOptionId.AdvancedCombatUI, true, 0x00001000u)]
    [InlineData(CharacterOptionId.AutoTarget, true, 0x00002000u)]
    [InlineData(CharacterOptionId.VividTargetingIndicator, true, 0x00008000u)]
    [InlineData(CharacterOptionId.FellowshipShareXP, true, 0x00040000u)]
    [InlineData(CharacterOptionId.AcceptLootPermits, true, 0x00080000u)]
    [InlineData(CharacterOptionId.FellowshipShareLoot, true, 0x00100000u)]
    [InlineData(CharacterOptionId.FellowshipAutoAcceptRequests, true, 0x20000000u)]
    [InlineData(CharacterOptionId.SideBySideVitals, true, 0x00200000u)]
    [InlineData(CharacterOptionId.CoordinatesOnRadar, true, 0x00400000u)]
    [InlineData(CharacterOptionId.SpellDuration, true, 0x00800000u)]
    [InlineData(CharacterOptionId.DisableHouseRestrictionEffects, true, 0x02000000u)]
    [InlineData(CharacterOptionId.DragItemOnPlayerOpensSecureTrade, true, 0x04000000u)]
    [InlineData(CharacterOptionId.DisplayAllegianceLogonNotifications, true, 0x08000000u)]
    [InlineData(CharacterOptionId.UseChargeAttack, true, 0x10000000u)]
    [InlineData(CharacterOptionId.UseCraftSuccessDialog, true, 0x80000000u)]
    [InlineData(CharacterOptionId.ListenToAllegianceChat, true, 0x40000000u)]
    [InlineData(CharacterOptionId.DisplayDateOfBirth, false, 0x00000002u)]
    [InlineData(CharacterOptionId.DisplayAge, false, 0x00000020u)]
    [InlineData(CharacterOptionId.DisplayChessRank, false, 0x00000004u)]
    [InlineData(CharacterOptionId.DisplayFishingSkill, false, 0x00000008u)]
    [InlineData(CharacterOptionId.DisplayNumberDeaths, false, 0x00000010u)]
    [InlineData(CharacterOptionId.DisplayTimeStamps, false, 0x00000040u)]
    [InlineData(CharacterOptionId.SalvageMultiple, false, 0x00000080u)]
    [InlineData(CharacterOptionId.ListenToGeneralChat, false, 0x00000100u)]
    [InlineData(CharacterOptionId.ListenToTradeChat, false, 0x00000200u)]
    [InlineData(CharacterOptionId.ListenToLFGChat, false, 0x00000400u)]
    [InlineData(CharacterOptionId.ListenToRoleplayChat, false, 0x00000800u)]
    [InlineData(CharacterOptionId.AppearOffline, false, 0x00001000u)]
    [InlineData(CharacterOptionId.DisplayNumberCharacterTitles, false, 0x00002000u)]
    [InlineData(CharacterOptionId.MainPackPreferred, false, 0x00004000u)]
    [InlineData(CharacterOptionId.LeadMissileTargets, false, 0x00008000u)]
    [InlineData(CharacterOptionId.UseFastMissiles, false, 0x00010000u)]
    [InlineData(CharacterOptionId.FilterLanguage, false, 0x00020000u)]
    [InlineData(CharacterOptionId.ConfirmVolatileRareUse, false, 0x00040000u)]
    [InlineData(CharacterOptionId.ListenToSocietyChat, false, 0x00080000u)]
    [InlineData(CharacterOptionId.ShowHelm, false, 0x00100000u)]
    [InlineData(CharacterOptionId.DisableDistanceFog, false, 0x00200000u)]
    [InlineData(CharacterOptionId.UseMouseTurning, false, 0x00400000u)]
    [InlineData(CharacterOptionId.ShowCloak, false, 0x00800000u)]
    [InlineData(CharacterOptionId.LockUI, false, 0x01000000u)]
    [InlineData(CharacterOptionId.HearPkDeathMessages, false, 0x02000000u)]
    public void WordAndMask_MatchesIndependentTranscriptionOfVerbatimAcclientEnums(
        CharacterOptionId id, bool expectedIsOptions1, uint expectedMask)
    {
        Assert.True(CharacterOptionTable.TryGet(id, out CharacterOptionTableEntry entry));
        Assert.Equal(expectedIsOptions1, entry.IsOptions1);
        Assert.Equal(expectedMask, entry.Mask);
    }

    [Fact]
    public void WordAndMask_AreAllPairwiseDistinct()
    {
        var pairs = CharacterOptionTable.All
            .Select(static e => (e.IsOptions1, e.Mask))
            .ToList();
        Assert.Equal(53, pairs.Distinct().Count());
    }

    [Theory]
    [InlineData(CharacterOptionId.AllowGive, true, (uint)PlayerDescriptionParser.CharacterOptions1.AllowGive)]
    [InlineData(CharacterOptionId.ListenToAllegianceChat, true, (uint)PlayerDescriptionParser.CharacterOptions1.HearAllegianceChat)]
    [InlineData(CharacterOptionId.DragItemOnPlayerOpensSecureTrade, true, (uint)PlayerDescriptionParser.CharacterOptions1.DragItemOnPlayerOpensSecureTrade)]
    [InlineData(CharacterOptionId.ListenToGeneralChat, false, (uint)PlayerDescriptionParser.CharacterOptions2.HearGeneralChat)]
    [InlineData(CharacterOptionId.ListenToTradeChat, false, (uint)PlayerDescriptionParser.CharacterOptions2.HearTradeChat)]
    [InlineData(CharacterOptionId.ListenToLFGChat, false, (uint)PlayerDescriptionParser.CharacterOptions2.HearLFGChat)]
    [InlineData(CharacterOptionId.ListenToRoleplayChat, false, (uint)PlayerDescriptionParser.CharacterOptions2.HearRoleplayChat)]
    [InlineData(CharacterOptionId.ListenToSocietyChat, false, (uint)PlayerDescriptionParser.CharacterOptions2.HearSocietyChat)]
    public void CharacterOptionTable_AgreesWithPlayerDescriptionParserEnums(
        CharacterOptionId id, bool expectedIsOptions1, uint expectedMask)
    {
        Assert.True(CharacterOptionTable.TryGet(id, out CharacterOptionTableEntry entry));
        Assert.Equal(expectedIsOptions1, entry.IsOptions1);
        Assert.Equal(expectedMask, entry.Mask);
    }
}

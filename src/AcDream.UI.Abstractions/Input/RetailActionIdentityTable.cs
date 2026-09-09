using System.Collections.Generic;
using System.Linq;

namespace AcDream.UI.Abstractions.Input;

public static class RetailActionIdentityTable
{
    public static readonly IReadOnlyDictionary<(uint InputMapId, uint ActionId), InputAction> Map =
        BuildTable();

    public static bool TryResolve(uint inputMapId, uint actionId, out InputAction action) =>
        Map.TryGetValue((inputMapId, actionId), out action);

    public static readonly IReadOnlyDictionary<InputAction, (uint InputMapId, uint ActionId)> ReverseMap =
        Map.ToDictionary(static pair => pair.Value, static pair => pair.Key);

    public static bool TryGetRetailIdentity(
        InputAction action,
        out (uint InputMapId, uint ActionId) identity) =>
        ReverseMap.TryGetValue(action, out identity);

    public static InputScope ScopeForInputMap(uint inputMapId) => inputMapId switch
    {
        0x00000006u => InputScope.Camera,
        0x10000003u => InputScope.MeleeCombat,
        0x10000004u => InputScope.MissileCombat,
        0x10000005u => InputScope.MagicCombat,
        _ => InputScope.Game,
    };

    public static ActivationType ActivationFor(uint inputMapId, uint actionId)
    {
        if (inputMapId == 0x4u && actionId == 0x32u)
            return ActivationType.Hold;

        if (inputMapId is 0x5u or 0x6u
            && actionId is >= 0x33u and <= 0x38u)
        {
            return ActivationType.Hold;
        }

        if (inputMapId == 0x5u && actionId is 0x3Du or 0x3Eu)
            return ActivationType.Hold;

        if (inputMapId == 0x10000003u
            && actionId is >= 0x1000005Du and <= 0x1000005Fu)
        {
            return ActivationType.Hold;
        }

        if (inputMapId == 0x10000004u
            && actionId is >= 0x100000F1u and <= 0x100000F3u)
        {
            return ActivationType.Hold;
        }

        return ActivationType.Press;
    }

    public static bool TryGetCharacterOptionId(InputAction action, out uint optionId)
    {
        optionId = 0u;
        if (!TryGetRetailIdentity(action, out var identity)
            || identity.InputMapId != 0x10000008u)
        {
            return false;
        }

        optionId = identity.ActionId switch
        {
            >= 0x10000071u and <= 0x10000074u => identity.ActionId - 0x10000071u,
            >= 0x10000076u and <= 0x10000083u => identity.ActionId - 0x10000071u,
            >= 0x10000085u and <= 0x10000093u => identity.ActionId - 0x10000071u,
            0x1000010Eu => 0x23u,
            0x1000010Fu => 0x24u,
            0x10000110u => 0x25u,
            0x10000112u => 0x26u,
            0x1000011Bu => 0x28u,
            0x1000011Du => 0x29u,
            0x1000011Eu => 0x2Au,
            0x1000011Fu => 0x2Bu,
            0x10000120u => 0x2Cu,
            0x10000123u => 0x2Du,
            0x10000125u => 0x2Eu,
            0x1000012Au => 0x2Fu,
            0x1000012Cu => 0x30u,
            0x1000012Fu => 0x32u,
            0x1000013Eu => 0x13u,
            _ => uint.MaxValue,
        };
        return optionId != uint.MaxValue;
    }

    private static Dictionary<(uint, uint), InputAction> BuildTable()
    {
        var t = new Dictionary<(uint, uint), InputAction>();
        void M(uint inputMapId, uint actionId, InputAction action) => t[(inputMapId, actionId)] = action;

        M(0x4, 0x29, InputAction.MovementForward);
        M(0x4, 0x2A, InputAction.MovementBackup);
        M(0x4, 0x2B, InputAction.MovementStop);
        M(0x4, 0x2C, InputAction.MovementStrafeRight);
        M(0x4, 0x2D, InputAction.MovementStrafeLeft);
        M(0x4, 0x2E, InputAction.MovementTurnRight);
        M(0x4, 0x2F, InputAction.MovementTurnLeft);
        M(0x4, 0x30, InputAction.MovementRunLock);
        M(0x4, 0x31, InputAction.MovementJump);
        M(0x4, 0x32, InputAction.MovementWalkMode);
        M(0x4, 0x10000094, InputAction.Ready);
        M(0x4, 0x10000095, InputAction.Crouch);
        M(0x4, 0x10000096, InputAction.Sitting);
        M(0x4, 0x10000097, InputAction.Sleeping);

        M(0x5, 0x33, InputAction.CameraMoveToward);
        M(0x5, 0x34, InputAction.CameraMoveAway);
        M(0x5, 0x35, InputAction.CameraRotateLeft);
        M(0x5, 0x36, InputAction.CameraRotateRight);
        M(0x5, 0x37, InputAction.CameraRotateUp);
        M(0x5, 0x38, InputAction.CameraRotateDown);
        M(0x5, 0x39, InputAction.CameraViewDefault);
        M(0x5, 0x3A, InputAction.CameraViewFirstPerson);
        M(0x5, 0x3B, InputAction.CameraViewLookDown);
        M(0x5, 0x3C, InputAction.CameraViewMapMode);
        M(0x5, 0x3D, InputAction.CameraInstantMouseLook);
        M(0x5, 0x3E, InputAction.CameraActivateAlternateMode);

        M(0x6, 0x33, InputAction.CameraAlternateMoveToward);
        M(0x6, 0x34, InputAction.CameraAlternateMoveAway);
        M(0x6, 0x35, InputAction.CameraAlternateRotateLeft);
        M(0x6, 0x36, InputAction.CameraAlternateRotateRight);
        M(0x6, 0x37, InputAction.CameraAlternateRotateUp);
        M(0x6, 0x38, InputAction.CameraAlternateRotateDown);
        M(0x6, 0x39, InputAction.CameraAlternateViewDefault);
        M(0x6, 0x3A, InputAction.CameraAlternateViewFirstPerson);
        M(0x6, 0x3B, InputAction.CameraAlternateViewLookDown);
        M(0x6, 0x3C, InputAction.CameraAlternateViewMapMode);

        M(0x10000002, 0x1000005A, InputAction.CombatToggleCombat);

        M(0x10000003, 0x1000005B, InputAction.CombatDecreaseAttackPower);
        M(0x10000003, 0x1000005C, InputAction.CombatIncreaseAttackPower);
        M(0x10000003, 0x1000005D, InputAction.CombatLowAttack);
        M(0x10000003, 0x1000005E, InputAction.CombatMediumAttack);
        M(0x10000003, 0x1000005F, InputAction.CombatHighAttack);

        M(0x10000004, 0x100000EF, InputAction.CombatDecreaseMissileAccuracy);
        M(0x10000004, 0x100000F0, InputAction.CombatIncreaseMissileAccuracy);
        M(0x10000004, 0x100000F1, InputAction.CombatAimLow);
        M(0x10000004, 0x100000F2, InputAction.CombatAimMedium);
        M(0x10000004, 0x100000F3, InputAction.CombatAimHigh);

        M(0x10000005, 0x10000060, InputAction.CombatCastCurrentSpell);
        M(0x10000005, 0x10000061, InputAction.CombatPrevSpell);
        M(0x10000005, 0x10000062, InputAction.CombatNextSpell);
        M(0x10000005, 0x10000063, InputAction.CombatPrevSpellTab);
        M(0x10000005, 0x10000064, InputAction.CombatNextSpellTab);
        M(0x10000005, 0x10000065, InputAction.UseSpellSlot_1);
        M(0x10000005, 0x10000066, InputAction.UseSpellSlot_2);
        M(0x10000005, 0x10000067, InputAction.UseSpellSlot_3);
        M(0x10000005, 0x10000068, InputAction.UseSpellSlot_4);
        M(0x10000005, 0x10000069, InputAction.UseSpellSlot_5);
        M(0x10000005, 0x1000006A, InputAction.UseSpellSlot_6);
        M(0x10000005, 0x1000006B, InputAction.UseSpellSlot_7);
        M(0x10000005, 0x1000006C, InputAction.UseSpellSlot_8);
        M(0x10000005, 0x1000006D, InputAction.UseSpellSlot_9);
        M(0x10000005, 0x1000006E, InputAction.UseSpellSlot_10);
        M(0x10000005, 0x1000006F, InputAction.UseSpellSlot_11);
        M(0x10000005, 0x10000070, InputAction.UseSpellSlot_12);
        M(0x10000005, 0x10000102, InputAction.CombatFirstSpell);
        M(0x10000005, 0x10000103, InputAction.CombatLastSpell);
        M(0x10000005, 0x10000104, InputAction.CombatFirstSpellTab);
        M(0x10000005, 0x10000105, InputAction.CombatLastSpellTab);

        InputAction[] emotes =
        {
            InputAction.EmoteAfkState,
            InputAction.EmoteAkimbo,
            InputAction.EmoteAToyotState,
            InputAction.EmoteAkimboState,
            InputAction.EmoteAtEaseState,
            InputAction.EmoteBeckon,
            InputAction.EmoteBeSeeingYou,
            InputAction.EmoteBlowKiss,
            InputAction.EmoteBowDeep,
            InputAction.EmoteBowDeepState,
            InputAction.Cheer,
            InputAction.EmoteClapHands,
            InputAction.EmoteClapHandsState,
            InputAction.EmoteCringe,
            InputAction.EmoteCrossArmsState,
            InputAction.Cry,
            InputAction.EmoteCurtseyState,
            InputAction.EmoteDrudgeDance,
            InputAction.EmoteDrudgeDanceState,
            InputAction.EmoteHaveASeat,
            InputAction.EmoteHaveASeatState,
            InputAction.EmoteHeartyLaugh,
            InputAction.EmoteHelper,
            InputAction.EmoteKneel,
            InputAction.EmoteKneelState,
            InputAction.EmoteKnock,
            InputAction.Laugh,
            InputAction.EmoteLeanState,
            InputAction.EmoteMeditateState,
            InputAction.EmoteMimeDrinking,
            InputAction.EmoteMimeEating,
            InputAction.EmoteMock,
            InputAction.EmoteNod,
            InputAction.EmoteNudgeLeft,
            InputAction.EmoteNudgeRight,
            InputAction.EmotePlead,
            InputAction.EmotePleadState,
            InputAction.EmotePoint,
            InputAction.PointState,
            InputAction.EmotePointDown,
            InputAction.EmotePointDownState,
            InputAction.EmotePointLeft,
            InputAction.EmotePointLeftState,
            InputAction.EmotePointRight,
            InputAction.EmotePointRightState,
            InputAction.EmotePossumState,
            InputAction.EmotePray,
            InputAction.EmotePrayState,
            InputAction.EmoteReadState,
            InputAction.EmoteSalute,
            InputAction.EmoteSaluteState,
            InputAction.EmoteScanHorizon,
            InputAction.EmoteScratchHead,
            InputAction.EmoteScratchHeadState,
            InputAction.EmoteShakeFist,
            InputAction.EmoteShakeFistState,
            InputAction.EmoteShakeHead,
            InputAction.EmoteShiver,
            InputAction.EmoteShiverState,
            InputAction.EmoteShoo,
            InputAction.EmoteShrug,
            InputAction.EmoteSitState,
            InputAction.EmoteSitBackState,
            InputAction.EmoteSitCrossleggedState,
            InputAction.EmoteSlouch,
            InputAction.EmoteSlouchState,
            InputAction.EmoteSmackHead,
            InputAction.EmoteSnowAngelState,
            InputAction.EmoteSpit,
            InputAction.EmoteSurrender,
            InputAction.EmoteSurrenderState,
            InputAction.EmoteTalkToTheHandState,
            InputAction.EmoteTapFoot,
            InputAction.EmoteTapFootState,
            InputAction.EmoteTeapot,
            InputAction.EmoteThinkerState,
            InputAction.EmoteWarmHands,
            InputAction.Wave,
            InputAction.EmoteWaveState,
            InputAction.EmoteWaveLow,
            InputAction.EmoteWaveHigh,
            InputAction.EmoteWinded,
            InputAction.EmoteWindedState,
            InputAction.EmoteWoah,
            InputAction.EmoteWoahState,
            InputAction.EmoteYawnAndStretch,
            InputAction.EmoteYmca,
        };
        for (int i = 0; i < emotes.Length; i++)
            M(0x10000006, 0x10000098u + (uint)i, emotes[i]);

        M(0x10000007, 0x1000002A, InputAction.SelectionSelf);
        M(0x10000007, 0x1000002C, InputAction.SelectionPlaceInInventory);
        M(0x10000007, 0x1000002D, InputAction.SelectionSplitStack);
        M(0x10000007, 0x1000002E, InputAction.SelectionPreviousSelection);
        M(0x10000007, 0x1000002F, InputAction.SelectionClosestCompassItem);
        M(0x10000007, 0x10000030, InputAction.SelectionPreviousCompassItem);
        M(0x10000007, 0x10000031, InputAction.SelectionNextCompassItem);
        M(0x10000007, 0x10000032, InputAction.SelectionClosestItem);
        M(0x10000007, 0x10000033, InputAction.SelectionPreviousItem);
        M(0x10000007, 0x10000034, InputAction.SelectionNextItem);
        M(0x10000007, 0x10000035, InputAction.SelectionClosestMonster);
        M(0x10000007, 0x10000036, InputAction.SelectionPreviousMonster);
        M(0x10000007, 0x10000037, InputAction.SelectionNextMonster);
        M(0x10000007, 0x10000038, InputAction.SelectionLastAttacker);
        M(0x10000007, 0x10000039, InputAction.SelectionClosestPlayer);
        M(0x10000007, 0x1000003A, InputAction.SelectionPreviousPlayer);
        M(0x10000007, 0x1000003B, InputAction.SelectionNextPlayer);
        M(0x10000007, 0x1000003C, InputAction.SelectionPreviousFellow);
        M(0x10000007, 0x1000003D, InputAction.SelectionNextFellow);
        M(0x10000007, 0x1000003E, InputAction.SelectionUseClosestUnopenedCorpse);
        M(0x10000007, 0x1000003F, InputAction.SelectionUseNextUnopenedCorpse);
        M(0x10000007, 0x10000040, InputAction.SelectionGiveToTarget);
        M(0x10000007, 0x10000041, InputAction.SelectionDrop);
        M(0x10000007, 0x1000011C, InputAction.SelectionPlaceInMainPack);
        M(0x10000007, 0x10000121, InputAction.SelectionClosestUnopenedCorpse);
        M(0x10000007, 0x10000122, InputAction.SelectionNextUnopenedCorpse);

        M(0x10000009, 0x55, InputAction.CaptureScreenshot);
        M(0x10000009, 0x7B, InputAction.ToggleHelp);
        M(0x10000009, 0x7C, InputAction.TogglePluginManager);
        M(0x10000009, 0x10000003, InputAction.ToggleAbuseReportingPanel);
        M(0x10000009, 0x10000005, InputAction.ToggleCharacterInfoPanel);
        M(0x10000009, 0x10000006, InputAction.TogglePositiveMagicPanel);
        M(0x10000009, 0x10000007, InputAction.ToggleNegativeMagicPanel);
        M(0x10000009, 0x10000009, InputAction.ToggleLinkStatusPanel);
        M(0x10000009, 0x1000000B, InputAction.ToggleUrgentAssistancePanel);
        M(0x10000009, 0x1000000C, InputAction.ToggleVitaePanel);
        M(0x10000009, 0x1000000D, InputAction.ToggleSocialPanel);
        M(0x10000009, 0x1000000E, InputAction.ToggleAllegiancePanel);
        M(0x10000009, 0x1000000F, InputAction.ToggleFellowshipPanel);
        M(0x10000009, 0x10000010, InputAction.ToggleSpellManagementPanel);
        M(0x10000009, 0x10000011, InputAction.ToggleSpellbookPanel);
        M(0x10000009, 0x10000012, InputAction.ToggleSpellComponentsPanel);
        M(0x10000009, 0x10000013, InputAction.ToggleCharacterDetailPanel);
        M(0x10000009, 0x10000014, InputAction.ToggleAttributesPanel);
        M(0x10000009, 0x10000015, InputAction.ToggleSkillsPanel);
        M(0x10000009, 0x10000016, InputAction.ToggleWorldPanel);
        M(0x10000009, 0x10000017, InputAction.ToggleMapPage);
        M(0x10000009, 0x10000018, InputAction.ToggleHousePage);
        M(0x10000009, 0x1000001A, InputAction.ToggleOptionsPanel);
        M(0x10000009, 0x10000019, InputAction.ToggleInventoryPanel);
        M(0x10000009, 0x1000001B, InputAction.ToggleGameplayOptionsPage);
        M(0x10000009, 0x1000001C, InputAction.ToggleCharacterSettingsPage);
        M(0x10000009, 0x1000001D, InputAction.ToggleConfigurationPage);
        M(0x10000009, 0x1000001E, InputAction.ToggleCompass);
        M(0x10000009, 0x1000001F, InputAction.ToggleKeyboardConfiguration);
        M(0x10000009, 0x10000114, InputAction.ToggleFloatingChatWindow1);
        M(0x10000009, 0x10000115, InputAction.ToggleFloatingChatWindow2);
        M(0x10000009, 0x10000116, InputAction.ToggleFloatingChatWindow3);
        M(0x10000009, 0x10000117, InputAction.ToggleFloatingChatWindow4);
        M(0x10000009, 0x10000025, InputAction.UseSelected);
        M(0x10000009, 0x10000026, InputAction.LOGOUT);
        M(0x10000009, 0x1000002B, InputAction.SelectionExamine);
        M(0x10000009, 0x10000118, InputAction.ToggleFriendsPage);
        M(0x10000009, 0x1000011A, InputAction.ToggleCharacterTitlesPage);
        M(0x10000009, 0x10000127, InputAction.ToggleQuestDetailPanel);
        M(0x10000009, 0x10000128, InputAction.ToggleQuestJournalPage);
        M(0x10000009, 0x10000129, InputAction.ToggleJournalPageList);
        M(0x10000009, 0x1000012E, InputAction.ToggleContractsPage);
        M(0x1000000A, 0x10000020, InputAction.ChatMonarchReply);
        M(0x1000000A, 0x10000021, InputAction.ChatPatronReply);
        M(0x1000000A, 0x10000022, InputAction.ChatReply);
        M(0x1000000A, 0x10000023, InputAction.EnterChatMode);
        M(0x1000000A, 0x10000028, InputAction.ChatStartCommand);
        M(0x1000000A, 0x10000119, InputAction.ChatTellToSelected);

        M(0x1000000D, 0x10000024, InputAction.ToggleChatEntry);

        M(0x1000000C, 0x10000042, InputAction.UseQuickSlot_1);
        M(0x1000000C, 0x10000043, InputAction.UseQuickSlot_2);
        M(0x1000000C, 0x10000044, InputAction.UseQuickSlot_3);
        M(0x1000000C, 0x10000045, InputAction.UseQuickSlot_4);
        M(0x1000000C, 0x10000046, InputAction.UseQuickSlot_5);
        M(0x1000000C, 0x10000047, InputAction.UseQuickSlot_6);
        M(0x1000000C, 0x10000048, InputAction.UseQuickSlot_7);
        M(0x1000000C, 0x10000049, InputAction.UseQuickSlot_8);
        M(0x1000000C, 0x1000004A, InputAction.UseQuickSlot_9);
        M(0x1000000C, 0x1000004B, InputAction.UseQuickSlot_10);
        M(0x1000000C, 0x1000004C, InputAction.UseQuickSlot_11);
        M(0x1000000C, 0x1000004D, InputAction.UseQuickSlot_12);
        M(0x1000000C, 0x1000004E, InputAction.SelectQuickSlot_1);
        M(0x1000000C, 0x1000004F, InputAction.SelectQuickSlot_2);
        M(0x1000000C, 0x10000050, InputAction.SelectQuickSlot_3);
        M(0x1000000C, 0x10000051, InputAction.SelectQuickSlot_4);
        M(0x1000000C, 0x10000052, InputAction.SelectQuickSlot_5);
        M(0x1000000C, 0x10000053, InputAction.SelectQuickSlot_6);
        M(0x1000000C, 0x10000054, InputAction.SelectQuickSlot_7);
        M(0x1000000C, 0x10000055, InputAction.SelectQuickSlot_8);
        M(0x1000000C, 0x10000056, InputAction.SelectQuickSlot_9);
        M(0x1000000C, 0x1000010D, InputAction.CreateShortcut);
        M(0x1000000C, 0x10000132, InputAction.UseQuickSlot_13);
        M(0x1000000C, 0x10000133, InputAction.UseQuickSlot_14);
        M(0x1000000C, 0x10000134, InputAction.UseQuickSlot_15);
        M(0x1000000C, 0x10000135, InputAction.UseQuickSlot_16);
        M(0x1000000C, 0x10000136, InputAction.UseQuickSlot_17);
        M(0x1000000C, 0x10000137, InputAction.UseQuickSlot_18);

        M(0x10000008, 0x10000071, InputAction.ToggleCharacterOptionAutoRepeatAttack);
        M(0x10000008, 0x10000072, InputAction.ToggleCharacterOptionIgnoreAllegianceRequests);
        M(0x10000008, 0x10000073, InputAction.ToggleCharacterOptionIgnoreFellowshipRequests);
        M(0x10000008, 0x10000074, InputAction.ToggleCharacterOptionIgnoreTradeRequests);
        M(0x10000008, 0x10000076, InputAction.ToggleCharacterOptionPersistentAtDay);
        M(0x10000008, 0x10000077, InputAction.ToggleCharacterOptionAllowGive);
        M(0x10000008, 0x10000078, InputAction.ToggleCharacterOptionViewCombatTarget);
        M(0x10000008, 0x10000079, InputAction.ToggleCharacterOptionShowTooltips);
        M(0x10000008, 0x1000007A, InputAction.ToggleCharacterOptionUseDeception);
        M(0x10000008, 0x1000007B, InputAction.ToggleCharacterOptionToggleRun);
        M(0x10000008, 0x1000007C, InputAction.ToggleCharacterOptionStayInChatMode);
        M(0x10000008, 0x1000007D, InputAction.ToggleCharacterOptionAdvancedCombatUi);
        M(0x10000008, 0x1000007E, InputAction.ToggleCharacterOptionAutoTarget);
        M(0x10000008, 0x1000007F, InputAction.ToggleCharacterOptionVividTargetingIndicator);
        M(0x10000008, 0x10000080, InputAction.ToggleCharacterOptionFellowshipShareXp);
        M(0x10000008, 0x10000081, InputAction.ToggleCharacterOptionAcceptLootPermits);
        M(0x10000008, 0x10000082, InputAction.ToggleCharacterOptionFellowshipShareLoot);
        M(0x10000008, 0x10000083, InputAction.ToggleCharacterOptionFellowshipAutoAcceptRequests);
        M(0x10000008, 0x10000085, InputAction.ToggleCharacterOptionCoordinatesOnRadar);
        M(0x10000008, 0x10000086, InputAction.ToggleCharacterOptionSpellDuration);
        M(0x10000008, 0x10000087, InputAction.ToggleCharacterOptionDisableHouseRestrictionEffects);
        M(0x10000008, 0x10000088, InputAction.ToggleCharacterOptionDragItemOnPlayerOpensSecureTrade);
        M(0x10000008, 0x10000089, InputAction.ToggleCharacterOptionDisplayAllegianceLogonNotifications);
        M(0x10000008, 0x1000008A, InputAction.ToggleCharacterOptionUseChargeAttack);
        M(0x10000008, 0x1000008B, InputAction.ToggleCharacterOptionUseCraftSuccessDialog);
        M(0x10000008, 0x1000008C, InputAction.ToggleCharacterOptionListenToAllegianceChat);
        M(0x10000008, 0x1000008D, InputAction.ToggleCharacterOptionDisplayDateOfBirth);
        M(0x10000008, 0x1000008E, InputAction.ToggleCharacterOptionDisplayAge);
        M(0x10000008, 0x1000008F, InputAction.ToggleCharacterOptionDisplayChessRank);
        M(0x10000008, 0x10000090, InputAction.ToggleCharacterOptionDisplayFishingSkill);
        M(0x10000008, 0x10000091, InputAction.ToggleCharacterOptionDisplayNumberDeaths);
        M(0x10000008, 0x10000092, InputAction.ToggleCharacterOptionDisplayTimeStamps);
        M(0x10000008, 0x10000093, InputAction.ToggleCharacterOptionSalvageMultiple);
        M(0x10000008, 0x1000010E, InputAction.ToggleCharacterOptionListenToGeneralChat);
        M(0x10000008, 0x1000010F, InputAction.ToggleCharacterOptionListenToTradeChat);
        M(0x10000008, 0x10000110, InputAction.ToggleCharacterOptionListenToLfgChat);
        M(0x10000008, 0x10000112, InputAction.ToggleCharacterOptionListenToRoleplayChat);
        M(0x10000008, 0x1000011B, InputAction.ToggleCharacterOptionDisplayNumberCharacterTitles);
        M(0x10000008, 0x1000011D, InputAction.ToggleCharacterOptionMainPackPreferred);
        M(0x10000008, 0x1000011E, InputAction.ToggleCharacterOptionLeadMissileTargets);
        M(0x10000008, 0x1000011F, InputAction.ToggleCharacterOptionUseFastMissiles);
        M(0x10000008, 0x10000120, InputAction.ToggleCharacterOptionFilterLanguage);
        M(0x10000008, 0x10000123, InputAction.ToggleCharacterOptionConfirmVolatileRareUse);
        M(0x10000008, 0x10000125, InputAction.ToggleCharacterOptionListenToSocietyChat);
        M(0x10000008, 0x1000012A, InputAction.ToggleCharacterOptionShowHelm);
        M(0x10000008, 0x1000012C, InputAction.ToggleCharacterOptionDisableDistanceFog);
        M(0x10000008, 0x1000012F, InputAction.ToggleCharacterOptionShowCloak);
        M(0x10000008, 0x1000013E, InputAction.ToggleCharacterOptionSideBySideVitals);

        return t;
    }
}

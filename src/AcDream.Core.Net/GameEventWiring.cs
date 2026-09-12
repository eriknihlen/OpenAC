using System;
using System.Globalization;
using System.Linq;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Player;
using AcDream.Core.Spells;
using AcDream.Core.Social;

namespace AcDream.Core.Net;

public static class GameEventWiring
{
    public static IDisposable WireAll(
        GameEventDispatcher dispatcher,
        ClientObjectTable items,
        CombatState combat,
        Spellbook spellbook,
        ChatLog chat,
        LocalPlayerState? localPlayer = null,
        TurbineChatState? turbineChat = null,
        Action<int /*runSkill*/, int /*jumpSkill*/>? onSkillsUpdated = null,
        Func<uint /*skillId*/, IReadOnlyDictionary<uint, uint> /*attrCurrents*/, uint /*formulaBonus*/>? resolveSkillFormulaBonus = null,
        Action<IReadOnlyList<ShortcutEntry>>? onShortcuts = null,
        Func<uint>? playerGuid = null,
        Action<uint /*weenieError*/>? onUseDone = null,
        Action<AppraiseInfoParser.Parsed>? onAppraisal = null,
        ItemManaState? itemMana = null,
        Action<GameEvents.CharacterConfirmationRequest>? onConfirmationRequest = null,
        Action<GameEvents.CharacterConfirmationDone>? onConfirmationDone = null,
        FriendsState? friends = null,
        SquelchState? squelch = null,
        Action<IReadOnlyList<(uint Id, uint Amount)>>? onDesiredComponents = null,
        Action<uint /*options1*/, uint /*options2*/, bool /*trailerTruncated*/>? onCharacterOptions = null,
        Func<double>? clientTime = null,
        ExternalContainerState? externalContainers = null,
        VendorState? vendor = null,
        Action<string, RetailLogTextType>? onInterfaceText = null,
        Func<bool>? accepting = null,
        Action<GameEvents.FellowshipFullUpdate>? onFellowshipFullUpdate = null,
        Action<GameEvents.FellowshipUpdateFellow>? onFellowshipUpdateFellow = null,
        Action<uint /*quitterGuid*/>? onFellowshipQuit = null,
        Action<uint /*dismissedGuid*/>? onFellowshipDismiss = null,
        Action? onFellowshipDisband = null,
        Action<ClientCommandResponses.AllegianceUpdate>? onAllegianceUpdate = null,
        Action<uint /*weenieError*/>? onAllegianceUpdateDone = null,
        Action<uint /*weenieError*/>? onAllegianceUpdateAborted = null,
        Action<GameEvents.AllegianceLoginNotification>? onAllegianceLoginNotification = null,
        Action<GameEvents.RegisterTrade>? onTradeRegister = null,
        Action<uint /*endReason*/>? onTradeClose = null,
        Action<GameEvents.AddToTrade>? onTradeAdd = null,
        Action<GameEvents.RemoveFromTrade>? onTradeRemove = null,
        Action<uint /*whoAccepted*/>? onTradeAccept = null,
        Action<uint /*whoDeclined*/>? onTradeDecline = null,
        Action<uint /*whoReset*/>? onTradeReset = null,
        Action<GameEvents.TradeFailure>? onTradeFailure = null,
        Action? onTradeClearAcceptance = null,
        Action<GameEvents.HouseData>? onHouseData = null,
        Action<uint /*weenieError*/>? onHouseStatus = null,
        Action<uint /*rentTime*/>? onHouseUpdateRentTime = null,
        Action<IReadOnlyList<GameEvents.HousePayment>>? onHouseUpdateRentPayment = null,
        Action<IReadOnlyDictionary<uint, ContractTracker>>? onContractTable = null,
        Action<ContractTrackerUpdate>? onContractUpdate = null,
        Action<uint /*displayTitleId*/, IReadOnlyList<uint> /*titleIds*/>? onCharacterTitleTable = null,
        Action<uint /*titleId*/, bool /*setAsDisplay*/>? onUpdateTitle = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(spellbook);
        ArgumentNullException.ThrowIfNull(chat);
        clientTime ??= static () => 0d;
        var registrar = new OwnedGameEventRegistrar(dispatcher, accepting);
        using var construction = new RegistrationBuildScope(registrar);

        registrar.Register(GameEventType.ChannelBroadcast, e =>
        {
            var p = GameEvents.ParseChannelBroadcast(e.Payload.Span);
            if (p is not null) chat.OnChannelBroadcast(p.Value.ChannelId, p.Value.SenderName, p.Value.Message);
        });
        registrar.Register(GameEventType.Tell, e =>
        {
            var p = GameEvents.ParseTell(e.Payload.Span);
            if (p is not null)
                chat.OnTellReceived(p.Value.SenderName, p.Value.Message, p.Value.SenderGuid, p.Value.ChatType);
        });
        registrar.Register(GameEventType.CommunicationTransientString, e =>
        {
            var s = GameEvents.ParseTransient(e.Payload.Span);
            if (s is null) return;
            if (onInterfaceText is not null)
                onInterfaceText(s, RetailLogTextType.ClientLocal);
            else
                chat.OnSystemMessage(s, chatType: (uint)RetailLogTextType.ClientLocal);
        });
        registrar.Register(GameEventType.PopupString, e =>
        {
            var s = GameEvents.ParsePopupString(e.Payload.Span);
            if (s is not null) chat.OnPopup(s);
        });
        registrar.Register(GameEventType.QueryAgeResponse, e =>
        {
            var p = GameEvents.ParseQueryAgeResponse(e.Payload.Span);
            if (p is null) return;
            string text = string.IsNullOrEmpty(p.Value.Name)
                ? $"You have played for {p.Value.Age}."
                : $"{p.Value.Name} has played for {p.Value.Age}.";
            chat.OnSystemMessage(text, chatType: 0u);
        });

        registrar.Register(GameEventType.ChannelIndex, e =>
        {
            var channels = ClientCommandResponses.ParseChannelIndex(e.Payload.Span);
            if (channels is null) return;
            foreach (string line in ClientCommandResponses.FormatChannelIndexLines(channels))
                chat.OnSystemMessage(line, chatType: 0u);
        });
        registrar.Register(GameEventType.ChannelList, e =>
        {
            var names = ClientCommandResponses.ParseChannelList(e.Payload.Span);
            if (names is null) return;
            foreach (string line in ClientCommandResponses.FormatChannelListLines(names))
                chat.OnSystemMessage(line, chatType: 0u);
        });
        registrar.Register(GameEventType.AvailableHouses, e =>
        {
            var houses = ClientCommandResponses.ParseAvailableHouses(e.Payload.Span);
            if (houses is null) return;
            foreach (string line in ClientCommandResponses.FormatAvailableHousesLines(houses.Value))
                chat.OnSystemMessage(line, chatType: 0u);
        });
        registrar.Register(GameEventType.AllegianceInfoResponse, e =>
        {
            var info = ClientCommandResponses.ParseAllegianceInfoResponse(e.Payload.Span);
            if (info is null) return;
            foreach (string line in ClientCommandResponses.FormatAllegianceInfoLines(info.Value))
                chat.OnSystemMessage(line, chatType: 0u);
        });

        if (onFellowshipFullUpdate is not null)
        {
            registrar.Register(GameEventType.FellowshipFullUpdate, e =>
            {
                var update = GameEvents.ParseFellowshipFullUpdate(e.Payload.Span);
                if (update is not null) onFellowshipFullUpdate(update.Value);
            });
        }
        if (onFellowshipUpdateFellow is not null)
        {
            registrar.Register(GameEventType.FellowshipUpdateFellow, e =>
            {
                var update = GameEvents.ParseFellowshipUpdateFellow(e.Payload.Span);
                if (update is not null) onFellowshipUpdateFellow(update.Value);
            });
        }
        if (onFellowshipQuit is not null)
        {
            registrar.Register(GameEventType.FellowshipQuit, e =>
            {
                var quit = GameEvents.ParseFellowshipQuit(e.Payload.Span);
                if (quit is not null) onFellowshipQuit(quit.Value.QuitterGuid);
            });
        }
        if (onFellowshipDismiss is not null)
        {
            registrar.Register(GameEventType.FellowshipDismiss, e =>
            {
                var dismiss = GameEvents.ParseFellowshipDismiss(e.Payload.Span);
                if (dismiss is not null) onFellowshipDismiss(dismiss.Value.DismissedGuid);
            });
        }
        if (onFellowshipDisband is not null)
        {
            registrar.Register(GameEventType.FellowshipDisband, e =>
            {
                if (GameEvents.ParseFellowshipDisband(e.Payload.Span))
                    onFellowshipDisband();
            });
        }

        if (onAllegianceUpdate is not null)
        {
            registrar.Register(GameEventType.AllegianceUpdate, e =>
            {
                var update = ClientCommandResponses.ParseAllegianceUpdate(e.Payload.Span);
                if (update is not null) onAllegianceUpdate(update.Value);
            });
        }
        if (onAllegianceUpdateDone is not null)
        {
            registrar.Register(GameEventType.AllegianceUpdateDone, e =>
            {
                var code = GameEvents.ParseAllegianceUpdateDone(e.Payload.Span);
                if (code is not null) onAllegianceUpdateDone(code.Value);
            });
        }
        if (onAllegianceUpdateAborted is not null)
        {
            registrar.Register(GameEventType.AllegianceUpdateAborted, e =>
            {
                var code = GameEvents.ParseAllegianceUpdateAborted(e.Payload.Span);
                if (code is not null) onAllegianceUpdateAborted(code.Value);
            });
        }
        if (onAllegianceLoginNotification is not null)
        {
            registrar.Register(GameEventType.AllegianceLoginNotification, e =>
            {
                var notice = GameEvents.ParseAllegianceLoginNotification(e.Payload.Span);
                if (notice is not null) onAllegianceLoginNotification(notice.Value);
            });
        }

        // ── Secure trade (0x01FD–0x0208) ──────────────────────────
        if (onTradeRegister is not null)
        {
            registrar.Register(GameEventType.RegisterTrade, e =>
            {
                var p = GameEvents.ParseRegisterTrade(e.Payload.Span);
                if (p is not null) onTradeRegister(p.Value);
            });
        }
        if (onTradeClose is not null)
        {
            registrar.Register(GameEventType.CloseTrade, e =>
            {
                var p = GameEvents.ParseCloseTrade(e.Payload.Span);
                if (p is not null) onTradeClose(p.Value);
            });
        }
        if (onTradeAdd is not null)
        {
            registrar.Register(GameEventType.AddToTrade, e =>
            {
                var p = GameEvents.ParseAddToTrade(e.Payload.Span);
                if (p is not null) onTradeAdd(p.Value);
            });
        }
        if (onTradeRemove is not null)
        {
            registrar.Register(GameEventType.RemoveFromTrade, e =>
            {
                var p = GameEvents.ParseRemoveFromTrade(e.Payload.Span);
                if (p is not null) onTradeRemove(p.Value);
            });
        }
        if (onTradeAccept is not null)
        {
            registrar.Register(GameEventType.AcceptTrade, e =>
            {
                var p = GameEvents.ParseAcceptTrade(e.Payload.Span);
                if (p is not null) onTradeAccept(p.Value);
            });
        }
        if (onTradeDecline is not null)
        {
            registrar.Register(GameEventType.DeclineTrade, e =>
            {
                var p = GameEvents.ParseDeclineTrade(e.Payload.Span);
                if (p is not null) onTradeDecline(p.Value);
            });
        }
        if (onTradeReset is not null)
        {
            registrar.Register(GameEventType.ResetTrade, e =>
            {
                var p = GameEvents.ParseResetTrade(e.Payload.Span);
                if (p is not null) onTradeReset(p.Value);
            });
        }
        if (onTradeFailure is not null)
        {
            registrar.Register(GameEventType.TradeFailure, e =>
            {
                var p = GameEvents.ParseTradeFailure(e.Payload.Span);
                if (p is not null) onTradeFailure(p.Value);
            });
        }
        if (onTradeClearAcceptance is not null)
        {
            registrar.Register(GameEventType.ClearTradeAcceptance, _ =>
                onTradeClearAcceptance());
        }

        if (onHouseData is not null)
        {
            registrar.Register(GameEventType.HouseData, e =>
            {
                var p = GameEvents.ParseHouseData(e.Payload.Span);
                if (p is not null) onHouseData(p.Value);
            });
        }
        if (onHouseStatus is not null)
        {
            registrar.Register(GameEventType.HouseStatus, e =>
            {
                var p = GameEvents.ParseHouseStatus(e.Payload.Span);
                if (p is not null) onHouseStatus(p.Value);
            });
        }
        if (onHouseUpdateRentTime is not null)
        {
            registrar.Register(GameEventType.UpdateRentTime, e =>
            {
                var p = GameEvents.ParseUpdateRentTime(e.Payload.Span);
                if (p is not null) onHouseUpdateRentTime(p.Value);
            });
        }
        if (onHouseUpdateRentPayment is not null)
        {
            registrar.Register(GameEventType.UpdateRentPayment, e =>
            {
                var p = GameEvents.ParseUpdateRentPayment(e.Payload.Span);
                if (p is not null) onHouseUpdateRentPayment(p);
            });
        }

        if (onContractTable is not null)
        {
            registrar.Register(GameEventType.SendClientContractTrackerTable, e =>
            {
                var p = ContractTrackerMessages.ParseTable(e.Payload.Span, DateTime.UtcNow);
                if (p is not null) onContractTable(p);
            });
        }
        if (onContractUpdate is not null)
        {
            registrar.Register(GameEventType.SendClientContractTracker, e =>
            {
                var p = ContractTrackerMessages.ParseUpdate(e.Payload.Span, DateTime.UtcNow);
                if (p is not null) onContractUpdate(p.Value);
            });
        }

        if (onCharacterTitleTable is not null)
        {
            registrar.Register(GameEventType.CharacterTitle, e =>
            {
                var p = GameEvents.ParseCharacterTitleTable(e.Payload.Span);
                if (p is not null) onCharacterTitleTable(p.Value.DisplayTitleId, p.Value.TitleIds);
            });
        }
        if (onUpdateTitle is not null)
        {
            registrar.Register(GameEventType.UpdateTitle, e =>
            {
                var p = GameEvents.ParseUpdateTitle(e.Payload.Span);
                if (p is not null) onUpdateTitle(p.Value.TitleId, p.Value.SetAsDisplay);
            });
        }

        if (onConfirmationRequest is not null)
        {
            registrar.Register(GameEventType.CharacterConfirmationRequest, e =>
            {
                var request = GameEvents.ParseCharacterConfirmationRequest(e.Payload.Span);
                if (request is not null)
                    onConfirmationRequest(request.Value);
            });
        }

        if (onConfirmationDone is not null)
        {
            registrar.Register(GameEventType.CharacterConfirmationDone, e =>
            {
                var done = GameEvents.ParseCharacterConfirmationDone(e.Payload.Span);
                if (done is not null)
                    onConfirmationDone(done.Value);
            });
        }

        if (friends is not null)
        {
            registrar.Register(GameEventType.FriendsListUpdate, e =>
            {
                FriendsUpdate? update = SocialStateMessages.ParseFriendsUpdate(e.Payload.Span);
                if (update is not null) friends.Apply(update);
            });
        }

        if (squelch is not null)
        {
            registrar.Register(GameEventType.SetSquelchDB, e =>
            {
                SquelchDatabase? database = SocialStateMessages.ParseSquelchDatabase(e.Payload.Span);
                if (database is not null) squelch.Replace(database);
            });
        }

        if (turbineChat is not null)
        {
            registrar.Register(GameEventType.SetTurbineChatChannels, e =>
            {
                var p = SetTurbineChatChannels.TryParse(e.Payload.Span);
                if (p is null) return;
                turbineChat.OnChannelsReceived(
                    allegianceRoom:           p.Value.AllegianceRoom,
                    generalRoom:              p.Value.GeneralRoom,
                    tradeRoom:                p.Value.TradeRoom,
                    lfgRoom:                  p.Value.LfgRoom,
                    roleplayRoom:             p.Value.RoleplayRoom,
                    olthoiRoom:               p.Value.OlthoiRoom,
                    societyRoom:              p.Value.SocietyRoom,
                    societyCelestialHandRoom: p.Value.SocietyCelestialHandRoom,
                    societyEldrytchWebRoom:   p.Value.SocietyEldrytchWebRoom,
                    societyRadiantBloodRoom:  p.Value.SocietyRadiantBloodRoom);

                Console.WriteLine(
                    $"chat: SetTurbineChatChannels parsed enabled={turbineChat.Enabled} " +
                    $"general=0x{p.Value.GeneralRoom:X8} trade=0x{p.Value.TradeRoom:X8} " +
                    $"lfg=0x{p.Value.LfgRoom:X8} roleplay=0x{p.Value.RoleplayRoom:X8} " +
                    $"society=0x{p.Value.SocietyRoom:X8} olthoi=0x{p.Value.OlthoiRoom:X8} " +
                    $"allegiance=0x{p.Value.AllegianceRoom:X8}");
            });
        }

        registrar.Register(GameEventType.WeenieError, e =>
        {
            var code = GameEvents.ParseWeenieError(e.Payload.Span);
            if (code is null) return;
            if (WeenieErrorMessages.IsSilentClientControlStatus(code.Value)) return;
            var (text, type) = WeenieErrorMessages.Resolve(code.Value, null);
            if (text is null)
            {
                Console.WriteLine($"[weenie-error] unmapped code=0x{code.Value:X4}");
                return;
            }
            if (onInterfaceText is not null)
                onInterfaceText(text, type);
            else
                chat.OnSystemMessage(text, chatType: (uint)type);
        });
        registrar.Register(GameEventType.WeenieErrorWithString, e =>
        {
            var p = GameEvents.ParseWeenieErrorWithString(e.Payload.Span);
            if (p is null) return;
            if (WeenieErrorMessages.IsSilentClientControlStatus(p.Value.ErrorCode)) return;
            var (text, type) = WeenieErrorMessages.Resolve(p.Value.ErrorCode, p.Value.Interpolation);
            if (text is null)
            {
                Console.WriteLine(
                    $"[weenie-error] unmapped code=0x{p.Value.ErrorCode:X4} param={p.Value.Interpolation}");
                return;
            }
            if (onInterfaceText is not null)
                onInterfaceText(text, type);
            else
                chat.OnSystemMessage(text, chatType: (uint)type);
        });

        registrar.Register(GameEventType.UpdateHealth, e =>
        {
            var p = GameEvents.ParseUpdateHealth(e.Payload.Span);
            if (p is not null) combat.OnUpdateHealth(p.Value.TargetGuid, p.Value.HealthPercent);
        });
        if (itemMana is not null)
        {
            registrar.Register(GameEventType.QueryItemManaResponse, e =>
            {
                var p = GameEvents.ParseQueryItemManaResponse(e.Payload.Span);
                if (p is not null)
                    itemMana.OnQueryItemManaResponse(
                        p.Value.ItemGuid, p.Value.ManaPercent, p.Value.Valid);
            });
        }
        registrar.Register(GameEventType.VictimNotification, e =>
        {
            var p = GameEvents.ParseVictimNotification(e.Payload.Span);
            if (p is not null) chat.OnCombatLine(p.Value.DeathMessage, logTextType: 0x00u, kind: CombatLineKind.Error);
        });
        registrar.Register(GameEventType.DefenderNotification, e =>
        {
            var p = GameEvents.ParseDefenderNotification(e.Payload.Span);
            if (p is not null) combat.OnDefenderNotification(
                p.Value.AttackerName, 0u, p.Value.DamageType,
                p.Value.Damage, p.Value.HitQuadrant, p.Value.Critical,
                p.Value.HealthPercent, p.Value.AttackConditions);
        });
        registrar.Register(GameEventType.AttackerNotification, e =>
        {
            var p = GameEvents.ParseAttackerNotification(e.Payload.Span);
            if (p is not null) combat.OnAttackerNotification(
                p.Value.DefenderName, p.Value.DamageType, p.Value.Damage,
                p.Value.HealthPercent, p.Value.Critical, p.Value.AttackConditions);
        });
        registrar.Register(GameEventType.EvasionAttackerNotification, e =>
        {
            var name = GameEvents.ParseEvasionAttackerNotification(e.Payload.Span);
            if (name is not null) combat.OnEvasionAttackerNotification(name);
        });
        registrar.Register(GameEventType.EvasionDefenderNotification, e =>
        {
            var name = GameEvents.ParseEvasionDefenderNotification(e.Payload.Span);
            if (name is not null) combat.OnEvasionDefenderNotification(name);
        });
        registrar.Register(GameEventType.AttackDone, e =>
        {
            var p = GameEvents.ParseAttackDone(e.Payload.Span);
            if (p is not null) combat.OnAttackDone(p.Value.AttackSequence, p.Value.WeenieError);
        });
        registrar.Register(GameEventType.CombatCommenceAttack, e =>
        {
            if (GameEvents.ParseCombatCommenceAttack(e.Payload.Span))
                combat.OnCombatCommenceAttack();
        });
        registrar.Register(GameEventType.KillerNotification, e =>
        {
            var p = GameEvents.ParseKillerNotification(e.Payload.Span);
            // Same handler/type as VictimNotification above — 0x00 Default.
            if (p is not null) chat.OnCombatLine(p.Value.DeathMessage, logTextType: 0x00u, kind: CombatLineKind.Info);
        });

        // ── Spells ────────────────────────────────────────────────
        registrar.Register(GameEventType.MagicUpdateSpell, e =>
        {
            var spellId = GameEvents.ParseMagicUpdateSpell(e.Payload.Span);
            if (spellId is not null) spellbook.OnSpellLearned(spellId.Value);
        });
        registrar.Register(GameEventType.MagicRemoveSpell, e =>
        {
            var spellId = GameEvents.ParseMagicRemoveSpell(e.Payload.Span);
            if (spellId is not null) spellbook.OnSpellForgotten(spellId.Value);
        });
        registrar.Register(GameEventType.MagicUpdateEnchantment, e =>
        {
            var p = GameEvents.ParseMagicUpdateEnchantment(e.Payload.Span);
            if (p is not null) spellbook.OnEnchantmentAdded(ToActiveEnchantment(p.Value, clientTime()));
        });
        registrar.Register(GameEventType.MagicUpdateMultipleEnchantments, e =>
        {
            var entries = GameEvents.ParseMagicUpdateMultipleEnchantments(e.Payload.Span);
            if (entries is not null)
            {
                double receivedAt = clientTime();
                spellbook.OnEnchantmentsAdded(entries.Select(entry =>
                    ToActiveEnchantment(entry, receivedAt)));
            }
        });
        // An enchantment that ran out is announced here, because nothing
        // else says so; one that was dispelled is not, because the dispel's
        // own text arrives as ordinary chat.
        registrar.Register(GameEventType.MagicRemoveEnchantment, e =>
        {
            var p = GameEvents.ParseMagicRemoveEnchantment(e.Payload.Span);
            if (p is null) return;
            spellbook.OnEnchantmentRemoved(p.Value.Layer, p.Value.SpellId);
            NotifyOfEnchantmentRemoval((uint)p.Value.SpellId);
        });
        registrar.Register(GameEventType.MagicRemoveMultipleEnchantments, e =>
        {
            var entries = GameEvents.ParseMagicLayeredSpellList(e.Payload.Span);
            if (entries is null) return;
            spellbook.OnEnchantmentsRemoved(entries.Select(item => ((uint)item.SpellId, (uint)item.Layer)));
            foreach (var entry in entries)
                NotifyOfEnchantmentRemoval((uint)entry.SpellId);
        });
        registrar.Register(GameEventType.MagicDispelEnchantment, e =>
        {
            var p = GameEvents.ParseMagicDispelEnchantment(e.Payload.Span);
            if (p is not null) spellbook.OnEnchantmentRemoved(p.Value.Layer, p.Value.SpellId);
        });
        registrar.Register(GameEventType.MagicDispelMultipleEnchantments, e =>
        {
            var entries = GameEvents.ParseMagicLayeredSpellList(e.Payload.Span);
            if (entries is not null)
                spellbook.OnEnchantmentsRemoved(entries.Select(item => ((uint)item.SpellId, (uint)item.Layer)));
        });

        const uint VitaePenaltySpellId = 0x29Au;

        void NotifyOfEnchantmentRemoval(uint spellId)
        {
            if (onInterfaceText is null)
                return;

            if (spellId >= 0x8000
                || !spellbook.TryGetMetadata(spellId, out SpellMetadata meta))
            {
                return;
            }

            string name = spellId == VitaePenaltySpellId
                ? meta.Name + " penalty"
                : meta.Name;

            onInterfaceText($"{name} has expired.", RetailLogTextType.Magic);
        }
        registrar.Register(GameEventType.MagicPurgeEnchantments,
            _ => spellbook.OnPurgeAll());
        registrar.Register(GameEventType.MagicPurgeBadEnchantments,
            _ => spellbook.OnPurgeBadEnchantments());

        // ── Inventory ─────────────────────────────────────────────
        registrar.Register(GameEventType.WieldObject, e =>
        {
            var p = GameEvents.ParseWieldObject(e.Payload.Span);
            if (p is null) return;

            uint wielderGuid = playerGuid?.Invoke() ?? 0u;
            items.ApplyConfirmedServerWield(
                p.Value.ItemGuid,
                wielderGuid,
                (AcDream.Core.Items.EquipMask)p.Value.EquipLoc);
        });
        registrar.Register(GameEventType.InventoryPutObjInContainer, e =>
        {
            var p = GameEvents.ParsePutObjInContainer(e.Payload.Span);
            if (p is null) return;

            items.ApplyConfirmedServerMove(
                p.Value.ItemGuid,
                p.Value.ContainerGuid,
                newWielderId: 0u,
                newSlot: (int)p.Value.Placement,
                containerTypeHint: p.Value.ContainerType);
        });

        registrar.Register(GameEventType.HouseUpdateRestrictions, e =>
        {
            var p = GameEvents.ParseHouseUpdateRestrictions(e.Payload.Span);
            if (p is null) return;

            items.UpdateHouseRestrictions(p.Value.SenderId, p.Value.Restrictions);
        });

        registrar.Register(GameEventType.ApproachVendor, e =>
        {
            var p = VendorApproach.TryParse(e.Payload.Span);
            if (p is null) return;

            var profile = new VendorShopProfile(
                p.Value.Profile.MerchandiseItemTypes,
                p.Value.Profile.MerchandiseMinValue,
                p.Value.Profile.MerchandiseMaxValue,
                p.Value.Profile.DealMagicalItems,
                p.Value.Profile.BuyPrice,
                p.Value.Profile.SellPrice,
                p.Value.Profile.AlternateCurrencyWcid,
                p.Value.Profile.AlternateCurrencyAmount,
                p.Value.Profile.AlternateCurrencyPluralName);

            var shopItems = new VendorShopItem[p.Value.Items.Count];
            for (int i = 0; i < shopItems.Length; i++)
            {
                VendorApproach.ItemProfile item = p.Value.Items[i];
                shopItems[i] = new VendorShopItem(
                    item.ItemGuid,
                    item.StackSize,
                    item.Desc.WeenieClassId,
                    item.Desc.Name,
                    item.Desc.ItemType,
                    item.Desc.IconId,
                    item.Desc.Value,
                    item.Desc.StackSize,
                    item.Desc.StackSizeMax,
                    item.Desc.IconUnderlayId,
                    item.Desc.IconOverlayId,
                    item.Desc.UiEffects,
                    item.Desc.PluralName,
                    item.Desc.ValidLocations,
                    item.Desc.Priority,
                    item.Desc.ItemsCapacity,
                    item.Desc.ContainersCapacity,
                    item.Desc.Structure,
                    item.Desc.MaxStructure,
                    item.Desc.Workmanship,
                    item.Desc.Burden,
                    item.Desc.MaterialType,
                    item.Desc.TargetType,
                    item.Desc.CombatUse,
                    item.Desc.AmmoType,
                    item.Desc.ObjectDescriptionFlags,
                    item.Desc.Useability,
                    item.Desc.HookItemTypes,
                    item.Desc.HookType);
            }

            vendor?.Apply(p.Value.VendorGuid, profile, shopItems);
        });

        registrar.Register(GameEventType.ViewContents, e =>
        {
            var p = GameEvents.ParseViewContents(e.Payload.Span);
            if (p is null) return;
            var entries = new ContainerContentEntry[p.Value.Items.Count];
            for (int i = 0; i < entries.Length; i++)
                entries[i] = new ContainerContentEntry(
                    p.Value.Items[i].Guid,
                    p.Value.Items[i].ContainerType);
            items.ReplaceContents(p.Value.ContainerGuid, entries);
            externalContainers?.ApplyViewContents(p.Value.ContainerGuid);
        });

        registrar.Register(GameEventType.InventoryPutObjectIn3D, e =>
        {
            var guid = GameEvents.ParsePutObjectIn3D(e.Payload.Span);
            if (guid is not null)
            {
                items.ApplyConfirmedServerMove(
                    guid.Value,
                    newContainerId: 0u,
                    newWielderId: 0u);
            }
        });

        // B-Drag: InventoryServerSaveFailed (0x00A0) — server rejected an optimistic move.
        // Snap the item back to its pre-move slot. Log only when there was no pending move
        // (a server-initiated failure on a non-optimistic path).
        registrar.Register(GameEventType.InventoryServerSaveFailed, e =>
        {
            var p = GameEvents.ParseInventoryServerSaveFailed(e.Payload.Span);
            if (p is null) return;
            // B-Drag: the server rejected an optimistic move — snap the item back to its pre-move slot.
            var item = items.Get(p.Value.ItemGuid);
            string itemInfo = item is null
                ? "unknown"
                : $"'{item.Name}' valid=0x{(uint)item.ValidLocations:X8} equip=0x{(uint)item.CurrentlyEquippedLocation:X8} priority=0x{item.Priority:X8} container=0x{item.ContainerId:X8} wielder=0x{item.WielderId:X8}";
            bool rolledBack = items.RejectMove(p.Value.ItemGuid, p.Value.WeenieError);
            Console.WriteLine($"[B-Drag] InventoryServerSaveFailed guid=0x{p.Value.ItemGuid:X8} err=0x{p.Value.WeenieError:X} rolledBack={rolledBack} item={itemInfo}");
        });

        registrar.Register(GameEventType.UseDone, e =>
        {
            uint? err = GameEvents.ParseUseDone(e.Payload.Span);
            if (err is null) return;
            Console.WriteLine($"[use-done] err=0x{err.Value:X4}");
            onUseDone?.Invoke(err.Value);
            if (err.Value == 0) return;

            if (WeenieErrorMessages.IsSilentClientControlStatus(err.Value)) return;

            var (text, type) = WeenieErrorMessages.Resolve(err.Value, null);
            if (text is null) return;
            if (onInterfaceText is not null)
                onInterfaceText(text, type);
            else
                chat.OnSystemMessage(text, chatType: (uint)type);
        });

        registrar.Register(GameEventType.SalvageOperationsResult, e =>
        {
            var result = GameEvents.ParseSalvageOperationsResult(e.Payload.Span);
            if (result is null)
                return;

            if (result.Value.Results.Count != 0)
            {
                string text = FormatSalvageResults(result.Value);
                if (onInterfaceText is not null)
                    onInterfaceText(text, RetailLogTextType.Salvaging);
                else
                    chat.OnSystemMessage(text, (uint)RetailLogTextType.Salvaging);
            }

            if (result.Value.UnsuitableItemGuids.Count != 0)
            {
                string names = string.Join(", ", result.Value.UnsuitableItemGuids
                    .Select(id => items.Get(id)?.GetAppropriateName() ?? "item"));
                string text = $" The following were not suitable for salvaging: {names}";
                if (onInterfaceText is not null)
                    onInterfaceText(text, RetailLogTextType.Salvaging);
                else
                    chat.OnSystemMessage(text, (uint)RetailLogTextType.Salvaging);
            }

            if (result.Value.Results.Count == 0
                && result.Value.UnsuitableItemGuids.Count == 0)
            {
                const string text = "Salvaging Failed!";
                if (onInterfaceText is not null)
                    onInterfaceText(text, RetailLogTextType.Salvaging);
                else
                    chat.OnSystemMessage(text, (uint)RetailLogTextType.Salvaging);
            }
        });

        registrar.Register(GameEventType.CloseGroundContainer, e =>
        {
            var guid = GameEvents.ParseCloseGroundContainer(e.Payload.Span);
            if (guid is null) return;
            externalContainers?.ApplyClose(guid.Value);
            items.StopViewingContentsTree(guid.Value);
        });

        registrar.Register(GameEventType.IdentifyObjectResponse, e =>
        {
            var p = AppraiseInfoParser.TryParse(e.Payload.Span);
            if (p is null) return;
            if (p.Value.Success && items.Get(p.Value.Guid) is not null)
                items.UpdateAppraisal(
                    p.Value.Guid,
                    p.Value.Properties,
                    p.Value.SpellBook,
                    clientTime());
            if (p.Value.CreatureProfile is { HealthMax: > 0u } creature)
                combat.OnUpdateHealth(
                    p.Value.Guid,
                    Math.Clamp(
                        (float)creature.Health / creature.HealthMax,
                        0f,
                        1f));
            onAppraisal?.Invoke(p.Value);
        });

        registrar.Register(GameEventType.PlayerDescription, e =>
        {
            var p = PlayerDescriptionParser.TryParse(e.Payload.Span);
            if (p is null) return;

            onCharacterOptions?.Invoke(
                p.Value.Options1, p.Value.Options2, p.Value.TrailerTruncated);
            onDesiredComponents?.Invoke(p.Value.DesiredComps);

            double receivedAt = clientTime();
            ActiveEnchantmentRecord[] enchantments = p.Value.Enchantments
                .Select(entry => ToActiveEnchantment(entry, receivedAt))
                .ToArray();
            spellbook.ReplaceManifest(
                p.Value.Spells,
                enchantments,
                p.Value.HotbarSpells,
                p.Value.DesiredComps,
                p.Value.SpellbookFilters);

            if (playerGuid is not null)
                items.UpsertProperties(playerGuid(), p.Value.Properties);

            var attrCurrents = new Dictionary<uint, uint>();
            foreach (var attr in p.Value.Attributes)
            {
                if (attr.AtType >= 1 && attr.AtType <= 6)
                    attrCurrents[attr.AtType] = attr.Ranks + attr.Start;
            }

            if (localPlayer is not null)
            {
                localPlayer.OnProperties(p.Value.Properties);
                localPlayer.OnPositions(p.Value.Positions.ToDictionary(
                    static pair => pair.Key,
                    static pair => ObjectTableWiring.ToPosition(pair.Value)));

                foreach (var attr in p.Value.Attributes)
                {
                    if (attr.Current is uint cur)
                    {
                        localPlayer.OnVitalUpdate(
                            vitalId: attr.AtType,
                            ranks:   attr.Ranks,
                            start:   attr.Start,
                            xp:      attr.Xp,
                            current: cur);
                    }
                    else
                    {
                        // Primary attribute (id 1..6) — Endurance+Self feed
                        // the vital max formula (Endurance/2 for Health,
                        // Endurance for Stamina, Self for Mana).
                        localPlayer.OnAttributeUpdate(
                            atType: attr.AtType,
                            ranks:  attr.Ranks,
                            start:  attr.Start,
                            xp:     attr.Xp);
                    }
                }
            }

            if (localPlayer is not null || onSkillsUpdated is not null)
            {
                int runSkill = -1;
                int jumpSkill = -1;
                foreach (var s in p.Value.Skills)
                {
                    uint formulaBonus = resolveSkillFormulaBonus is not null
                        ? resolveSkillFormulaBonus(s.SkillId, attrCurrents)
                        : 0u;

                    localPlayer?.OnSkillUpdate(
                        skillId:      s.SkillId,
                        ranks:        s.Ranks,
                        status:       s.Status,
                        xp:           s.Xp,
                        init:         s.Init,
                        resistance:   s.Resistance,
                        lastUsed:     s.LastUsed,
                        formulaBonus: formulaBonus);

                    if (s.SkillId != 22u && s.SkillId != 24u) continue;

                    int total = (int)(formulaBonus + s.Init + s.Ranks);
                    if (s.SkillId == 24u) runSkill = total;
                    else if (s.SkillId == 22u) jumpSkill = total;
                }
                if (runSkill >= 0 || jumpSkill >= 0)
                    onSkillsUpdated?.Invoke(runSkill, jumpSkill);
            }

            uint ownerGuid = playerGuid?.Invoke() ?? 0u;
            if (ownerGuid != 0u)
            {
                var entries = new ContainerContentEntry[p.Value.Inventory.Count];
                for (int i = 0; i < entries.Length; i++)
                    entries[i] = new ContainerContentEntry(
                        p.Value.Inventory[i].Guid,
                        p.Value.Inventory[i].ContainerType);
                items.InitializeInventoryManifest(ownerGuid, entries);
            }
            else
            {
                foreach (var inv in p.Value.Inventory)
                    items.RecordMembership(inv.Guid, containerTypeHint: inv.ContainerType);
            }
            if (ownerGuid != 0u)
            {
                var equipment = new EquipmentManifestEntry[p.Value.Equipped.Count];
                for (int i = 0; i < equipment.Length; i++)
                {
                    var eq = p.Value.Equipped[i];
                    equipment[i] = new EquipmentManifestEntry(
                        eq.Guid,
                        (EquipMask)eq.EquipLocation,
                        eq.Priority);
                }
                items.InitializeEquipmentManifest(ownerGuid, equipment);
            }
            else
            {
                foreach (var eq in p.Value.Equipped)
                {
                    items.RecordMembership(
                        eq.Guid,
                        equip: (EquipMask)eq.EquipLocation,
                        priority: eq.Priority);
                }
            }

            onShortcuts?.Invoke(p.Value.Shortcuts);
        });
        return construction.Complete();
    }

    private sealed class OwnedGameEventRegistrar(
        GameEventDispatcher dispatcher,
        Func<bool>? accepting) : IDisposable
    {
        private readonly SubscriptionSet _subscriptions = new();

        public void Register(
            GameEventType type,
            GameEventDispatcher.EventHandler handler)
        {
            GameEventDispatcher.EventHandler registered = accepting is null
                ? handler
                : envelope =>
                {
                    if (accepting()) handler(envelope);
                };
            _subscriptions.Add(dispatcher.RegisterOwned(type, registered));
        }

        public void Dispose() => _subscriptions.Dispose();
    }

    private sealed class RegistrationBuildScope(
        OwnedGameEventRegistrar registration) : IDisposable
    {
        private bool _complete;

        public IDisposable Complete()
        {
            _complete = true;
            return registration;
        }

        public void Dispose()
        {
            if (!_complete)
                registration.Dispose();
        }
    }

    private static ActiveEnchantmentRecord ToActiveEnchantment(
        PlayerDescriptionParser.EnchantmentEntry enchantment,
        double receivedAt) => new(
        SpellId: enchantment.SpellId,
        LayerId: enchantment.Layer,
        Duration: enchantment.Duration,
        CasterGuid: enchantment.CasterGuid,
        StatModType: enchantment.StatModType,
        StatModKey: enchantment.StatModKey,
        StatModValue: enchantment.StatModValue,
        Bucket: enchantment.Bucket == 0
            ? ClassifyLiveEnchantmentBucket(enchantment.StatModType)
            : (uint)enchantment.Bucket,
        StartTime: receivedAt + enchantment.StartTime,
        SpellCategory: enchantment.SpellCategory,
        PowerLevel: enchantment.PowerLevel,
        DegradeModifier: enchantment.DegradeModifier,
        DegradeLimit: enchantment.DegradeLimit,
        LastTimeDegraded: receivedAt + enchantment.LastTimeDegraded,
        SpellSetId: enchantment.SpellSetId);

    private static uint ClassifyLiveEnchantmentBucket(uint statModType)
    {
        const uint Multiplicative = 0x00004000u;
        const uint Additive = 0x00008000u;
        const uint Vitae = 0x00800000u;
        const uint Cooldown = 0x01000000u;
        if ((statModType & Vitae) != 0) return 4u;
        if ((statModType & Cooldown) != 0) return 8u;
        if ((statModType & Multiplicative) != 0) return 1u;
        if ((statModType & Additive) != 0) return 2u;
        return 0u;
    }

    private static string FormatSalvageResults(GameEvents.SalvageOperationsResult result)
    {
        string materials = string.Join(", ", result.Results.Select(FormatSalvageMaterial));
        string augmentation = result.AugmentationBonusPercent == 0
            ? string.Empty
            : string.Format(
                CultureInfo.InvariantCulture,
                " Your augmentation has given you a return bonus of {0}%!",
                result.AugmentationBonusPercent);
        return string.Format(
            CultureInfo.InvariantCulture,
            "You obtain {0} using your knowledge of {1}.{2}",
            materials,
            SalvageSkillName(result.SkillId),
            augmentation);
    }

    private static string FormatSalvageMaterial(GameEvents.SalvageResult result) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} (ws {2:F2})",
            result.Units,
            SalvageMaterialName(result.MaterialType),
            result.Workmanship);

    private static string SalvageMaterialName(uint materialType) => materialType switch
    {
        1u => "Ceramic", 2u => "Porcelain", 3u => "Cloth", 4u => "Linen",
        5u => "Satin", 6u => "Silk", 7u => "Velvet", 8u => "Wool",
        9u => "Gem", 10u => "Agate", 11u => "Amber", 12u => "Amethyst",
        13u => "Aquamarine", 14u => "Azurite", 15u => "Black Garnet",
        16u => "Black Opal", 17u => "Bloodstone", 18u => "Carnelian",
        19u => "Citrine", 20u => "Diamond", 21u => "Emerald", 22u => "Fire Opal",
        23u => "Green Garnet", 24u => "Green Jade", 25u => "Hematite",
        26u => "Imperial Topaz", 27u => "Jet", 28u => "Lapis Lazuli",
        29u => "Lavender Jade", 30u => "Malachite", 31u => "Moonstone",
        32u => "Onyx", 33u => "Opal", 34u => "Peridot", 35u => "Red Garnet",
        36u => "Red Jade", 37u => "Rose Quartz", 38u => "Ruby", 39u => "Sapphire",
        40u => "Smokey Quartz", 41u => "Sunstone", 42u => "Tiger Eye",
        43u => "Tourmaline", 44u => "Turquoise", 45u => "White Jade",
        46u => "White Quartz", 47u => "White Sapphire", 48u => "Yellow Garnet",
        49u => "Yellow Topaz", 50u => "Zircon", 51u => "Ivory", 52u => "Leather",
        53u => "Armoredillo Hide", 54u => "Gromnie Hide", 55u => "Reed Shark Hide",
        56u => "Metal", 57u => "Brass", 58u => "Bronze", 59u => "Copper",
        60u => "Gold", 61u => "Iron", 62u => "Pyreal", 63u => "Silver",
        64u => "Steel", 65u => "Stone", 66u => "Alabaster", 67u => "Granite",
        68u => "Marble", 69u => "Obsidian", 70u => "Sandstone", 71u => "Serpentine",
        72u => "Wood", 73u => "Ebony", 74u => "Mahogany", 75u => "Oak",
        76u => "Pine", 77u => "Teak", _ => "Unknown",
    };

    private static string SalvageSkillName(uint skillId) => skillId switch
    {
        1u => "Axe", 2u => "Bow", 3u => "Crossbow", 4u => "Dagger", 5u => "Mace",
        6u => "Melee Defense", 7u => "Missile Defense", 8u => "Sling", 9u => "Spear",
        10u => "Staff", 11u => "Sword", 12u => "Thrown Weapon", 13u => "Unarmed Combat",
        14u => "Arcane Lore", 15u => "Magic Defense", 16u => "Mana Conversion",
        17u => "Spellcraft", 18u => "Item Tinkering", 19u => "Assess Person",
        20u => "Deception", 21u => "Healing", 22u => "Jump", 23u => "Lockpick",
        24u => "Run", 25u => "Awareness", 26u => "Arms And Armor Repair",
        27u => "Assess Creature", 28u => "Weapon Tinkering", 29u => "Armor Tinkering",
        30u => "Magic Item Tinkering", 31u => "Creature Enchantment",
        32u => "Item Enchantment", 33u => "Life Magic", 34u => "War Magic",
        35u => "Leadership", 36u => "Loyalty", 37u => "Fletching", 38u => "Alchemy",
        39u => "Cooking", 40u => "Salvaging", 41u => "Two Handed Combat",
        42u => "Gearcraft", 43u => "Void Magic", 44u => "Heavy Weapons",
        45u => "Light Weapons", 46u => "Finesse Weapons", 47u => "Missile Weapons",
        48u => "Shield", 49u => "Dual Wield", 50u => "Recklessness",
        51u => "Sneak Attack", 52u => "Dirty Fighting", 53u => "Challenge",
        54u => "Summoning", _ => "Unknown",
    };
}

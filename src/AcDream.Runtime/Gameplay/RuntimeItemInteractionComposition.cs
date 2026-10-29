using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Builds the one item-interaction owner from the runtime's own gameplay
/// state and whatever session it currently holds. Everything a client needs
/// to decide an item request — who owns the item, which container is open,
/// which vendor is trading, whether a request is already in flight — is
/// runtime state, so the owner is the same object whether or not the host
/// draws anything. A host adds only what it alone knows, through the
/// owner's bind methods.
/// </summary>
internal static class RuntimeItemInteractionComposition
{
    internal static RuntimeItemInteraction Create(
        LiveSessionController session,
        RuntimeLocalPlayerIdentityState identity,
        RuntimeInventoryState inventory,
        RuntimeActionState actions,
        RuntimeCharacterState character,
        RuntimeCommunicationState communication,
        RuntimeChessState chess,
        IGameRuntimeClock clock)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(communication);
        ArgumentNullException.ThrowIfNull(chess);
        ArgumentNullException.ThrowIfNull(clock);

        WorldSession? Live() => session.CurrentSession;
        var transport = new RuntimeSessionInteractionTransport(Live);
        RuntimeItemInteraction? owner = null;

        // Without a host route that walks the player to the object first,
        // the request goes straight out; the server is the range authority.
        void DispatchPickup(uint itemGuid, uint containerId, int placement)
        {
            RuntimeItemInteraction target = owner
                ?? throw new InvalidOperationException(
                    "The item-interaction owner is not constructed yet.");
            ulong placementToken = target.TryGetPendingBackpackPlacement(
                itemGuid,
                out PendingBackpackPlacement pending)
                    ? pending.Token
                    : 0u;
            bool dispatched = actions.Transactions.TryDispatchPickup(
                new RuntimePendingPickup(
                    Token: 0u,
                    itemGuid,
                    LocalEntityId: 0u,
                    containerId,
                    placement,
                    placementToken,
                    ApproachToken: default),
                transport,
                out _);
            if (!dispatched)
                target.CancelPendingBackpackPlacement(itemGuid, placementToken);
        }

        owner = new RuntimeItemInteraction(
            inventory.Objects,
            actions.Transactions,
            actions.Interaction,
            playerGuid: () => identity.ServerGuid,
            // The use throttle is one stamp shared by every use entry point: the
            // window click, the command adapter and the headless automation all
            // read the simulation clock, so this owner stamps it from the same
            // clock. A wall-clock stamp here would sit far ahead of the others
            // and refuse every use they attempt after it.
            nowMs: () => checked((long)Math.Floor(clock.SimulationTimeSeconds * 1000d)),
            sendUse: guid => Live()?.SendUse(guid),
            sendUseWithTarget: (source, target) =>
                Live()?.SendUseWithTarget(source, target),
            sendWield: (item, mask) => Live()?.SendGetAndWieldItem(item, mask),
            sendDrop: item => Live()?.SendDropItem(item),
            sendExamine: guid => Live()?.SendAppraise(guid),
            readyForInventoryRequest: () => session.IsInWorld,
            activeVendorId: () => inventory.Vendor.VendorId,
            groundObjectId: () => inventory.ExternalContainers.CurrentContainerId,
            inNonCombatMode: () =>
                actions.Combat.CurrentMode == CombatMode.NonCombat,
            placeInBackpack: DispatchPickup,
            sendSplitToWorld: (item, amount) =>
                Live()?.SendStackableSplitTo3D(item, amount),
            selectedObjectId: () => actions.Selection.SelectedObjectId ?? 0u,
            sendPutItemInContainer: (item, container, placement) =>
                Live()?.SendPutItemInContainer(item, container, placement),
            sendGive: (target, item, amount) =>
                Live()?.SendGiveObject(target, item, amount),
            dragOnPlayerOpensSecureTrade: () =>
                character.Options.DragItemOnPlayerOpensSecureTrade,
            mainPackPreferred: () => character.Options.GetOptionBit(
                AcDream.Core.Net.Messages.CharacterOptionId.MainPackPreferred),
            confirmVolatileRareUses: () => character.Options.GetOptionBit(
                AcDream.Core.Net.Messages.CharacterOptionId.ConfirmVolatileRareUse),
            systemMessage: text => communication.AddText(
                text,
                Core.Chat.RetailLogTextType.ClientLocal),
            sendSplitToContainer: (item, container, placement, amount) =>
                Live()?.SendStackableSplitToContainer(
                    item,
                    container,
                    placement,
                    amount),
            requestExternalContainer: guid =>
            {
                ClientObject? container = inventory.Objects.Get(guid);
                bool isCorpse = container is not null
                    && ((PublicWeenieFlags)(container.PublicWeenieBitfield ?? 0u)
                        & PublicWeenieFlags.Corpse) != 0;
                inventory.ExternalContainers.RequestOpen(guid, isCorpse);
            },
            combatState: actions.Combat,
            sendChangeCombatMode: mode => Live()?.SendChangeCombatMode(mode),
            sendBuy: (vendorGuid, itemGuid, amount, alternateCurrencyId) =>
            {
                if (Live() is not { } live || !session.IsInWorld)
                    return false;
                live.SendBuy(vendorGuid, itemGuid, amount, alternateCurrencyId);
                return true;
            },
            sendBuyAll: (vendorGuid, items, alternateCurrencyId) =>
            {
                if (Live() is not { } live || !session.IsInWorld)
                    return false;
                live.SendBuy(vendorGuid, items, alternateCurrencyId);
                return true;
            },
            sendSell: (vendorGuid, items) =>
            {
                if (Live() is not { } live || !session.IsInWorld)
                    return false;
                live.SendSell(vendorGuid, items);
                return true;
            },
            interfaceText: (text, type) => communication.AddText(text, type),
            sendStackableMerge: (source, target, amount) =>
                Live()?.SendStackableMerge(source, target, amount),
            sendSalvage: (toolGuid, itemGuids) =>
            {
                if (Live() is not { } live || !session.IsInWorld)
                    return false;
                live.SendSalvage(toolGuid, itemGuids);
                return true;
            },
            // Using a board begins its game here, so a client with no window
            // joins exactly as one with a window does.
            beginGame: board =>
            {
                if (chess.Begin(board))
                    Live()?.SendChessJoin(board);
            });
        return owner;
    }
}

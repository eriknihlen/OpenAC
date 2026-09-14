using AcDream.Automation;
using AcDream.Automation.Items;
using AcDream.Content;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Plugins;

/// <summary>
/// The item interaction controller for a headless session: the same
/// controller the graphical client binds its item automation through,
/// built over the runtime's owners and the live session's sends, with no
/// retained UI behind it (no toasts, no drag, no stack-split prompt).
/// </summary>
/// <remarks>
/// Use and pickup dispatch straight away through the runtime's
/// transaction state and the command adapter's transport; the graphical
/// client walks up to a far object first, a headless bot walks itself.
/// </remarks>
internal static class HeadlessItemInteraction
{
    internal static ItemInteractionController Create(
        GameRuntime runtime,
        DirectGameRuntimeCommandAdapter commands,
        Func<WorldSession?> session,
        MagicCatalog? magic)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(session);

        ItemInteractionController items = null!;
        IRuntimeInteractionTransport transport = commands;
        RuntimeInteractionTransactionState transactions =
            runtime.ActionOwner.Transactions;
        bool InWorld() => runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;
        WorldSession? Live() => InWorld() ? session() : null;

        bool IsUseable(uint guid) =>
            runtime.EntityObjects?.Entities.TryGetSnapshot(guid, out var spawn) == true
            && ItemUseability.IsUseable(spawn.Useability ?? ItemUseability.Undef);

        void RequestUse(uint guid, ItemUseRequestReservation reservation) =>
            _ = transactions.TryDispatchUse(
                guid,
                ownedByPlayer: items.IsOwnedByPlayer(guid),
                useable: IsUseable(guid),
                reservation,
                transport,
                out _);

        void SendPickup(uint itemGuid, uint destinationContainerId, int placement)
        {
            ulong pendingPlacementToken = items.TryGetPendingBackpackPlacement(
                itemGuid,
                out PendingBackpackPlacement pendingPlacement)
                    ? pendingPlacement.Token
                    : 0u;
            uint localEntityId =
                runtime.EntityObjects?.Entities.TryGetActive(itemGuid, out var record) == true
                    ? record.LocalEntityId ?? 0u
                    : 0u;
            _ = transactions.TryDispatchPickup(
                new RuntimePendingPickup(
                    Token: 0u,
                    itemGuid,
                    localEntityId,
                    destinationContainerId,
                    placement,
                    pendingPlacementToken,
                    ApproachToken: default),
                transport,
                out _);
        }

        items = new ItemInteractionController(
            runtime.InventoryOwner.Objects,
            transactions,
            runtime.ActionOwner.Interaction,
            playerGuid: () => runtime.PlayerIdentity.ServerGuid,
            sendUse: null,
            sendExamine: guid => Live()?.SendAppraise(guid),
            sendUseWithTarget: (source, target) =>
                Live()?.SendUseWithTarget(source, target),
            sendWield: (item, mask) => Live()?.SendGetAndWieldItem(item, mask),
            sendDrop: item => Live()?.SendDropItem(item),
            sendGive: (target, item, amount) =>
                Live()?.SendGiveObject(target, item, amount),
            dragOnPlayerOpensSecureTrade: () =>
                runtime.CharacterOwner.Options.DragItemOnPlayerOpensSecureTrade,
            toast: null,
            readyForInventoryRequest: InWorld,
            playerOnGround: () =>
                runtime.MovementOwner.Controller is { IsAirborne: false },
            inNonCombatMode: () =>
                runtime.ActionOwner.Combat.CurrentMode == CombatMode.NonCombat,
            combatState: runtime.ActionOwner.Combat,
            sendChangeCombatMode: mode => Live()?.SendChangeCombatMode(mode),
            isComponentPack: magic is null
                ? null
                : magic.IsComponentPack,
            placeInBackpack: SendPickup,
            backpackContainerId: null,
            groundObjectId: () =>
                runtime.InventoryOwner.ExternalContainers.CurrentContainerId,
            activeVendorId: () => runtime.InventoryOwner.Vendor.VendorId,
            sendSplitToWorld: (item, amount) =>
                Live()?.SendStackableSplitTo3D(item, amount),
            selectedObjectId: () =>
                runtime.ActionOwner.Selection.SelectedObjectId ?? 0u,
            stackSplitQuantity: null,
            systemMessage: text =>
                runtime.CommunicationOwner.AddText(text, RetailLogTextType.ClientLocal),
            interfaceText: (text, type) => runtime.CommunicationOwner.AddText(text, type),
            sendPutItemInContainer: (item, container, placement) =>
                Live()?.SendPutItemInContainer(item, container, placement),
            sendSplitToContainer: (item, container, placement, amount) =>
                Live()?.SendStackableSplitToContainer(
                    item,
                    container,
                    placement,
                    amount),
            sendStackableMerge: (source, target, amount) =>
                Live()?.SendStackableMerge(source, target, amount),
            requestExternalContainer: guid =>
            {
                ClientObject? container = runtime.InventoryOwner.Objects.Get(guid);
                bool isCorpse = container is not null
                    && ((PublicWeenieFlags)(container.PublicWeenieBitfield ?? 0u)
                        & PublicWeenieFlags.Corpse) != 0;
                runtime.InventoryOwner.ExternalContainers.RequestOpen(guid, isCorpse);
            },
            requestUse: RequestUse,
            sendBuy: (vendorGuid, itemGuid, amount, alternateCurrencyId) =>
            {
                if (Live() is not { } live)
                    return false;
                live.SendBuy(vendorGuid, itemGuid, amount, alternateCurrencyId);
                return true;
            },
            sendBuyAll: (vendorGuid, buyItems, alternateCurrencyId) =>
            {
                if (Live() is not { } live)
                    return false;
                live.SendBuy(vendorGuid, buyItems, alternateCurrencyId);
                return true;
            },
            sendSell: (vendorGuid, sellItems) =>
            {
                if (Live() is not { } live)
                    return false;
                live.SendSell(vendorGuid, sellItems);
                return true;
            },
            sendSalvage: (toolGuid, itemGuids) =>
            {
                if (Live() is not { } live)
                    return false;
                live.SendSalvage(toolGuid, itemGuids);
                return true;
            });
        return items;
    }

    /// <summary>Binds the surface's item and equipment delegates the way the App does.</summary>
    internal static void Bind(
        RuntimeAutomationSurface surface,
        ItemInteractionController items)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(items);
        surface.BindEquipment(
            (itemId, requestedLocation) =>
                items.TryWieldItem(itemId, (EquipMask)requestedLocation),
            () => items.IsAutoWieldBusy);
        surface.BindItems(
            items.TryUseItemForAutomation,
            items.TryApplyItem,
            items.TryMoveItemForAutomation,
            items.TryMergeItemsForAutomation,
            items.TryDropItemForAutomation,
            items.TryGiveItemForAutomation,
            items.PlaceWorldItemInBackpack,
            items.TryAppraiseForAutomation,
            items.TrySalvageItemsForAutomation,
            (vendorId, itemId, amount) => items.TrySell(
                vendorId,
                [(amount, itemId)]));
    }
}

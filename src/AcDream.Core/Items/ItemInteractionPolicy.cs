using System;
using System.Collections.Generic;

namespace AcDream.Core.Items;

[Flags]
public enum PublicWeenieFlags : uint
{
    None = 0,
    Openable = 0x00000001,
    Inscribable = 0x00000002,
    Stuck = 0x00000004,
    Player = 0x00000008,
    Attackable = 0x00000010,
    PlayerKiller = 0x00000020,
    HiddenAdmin = 0x00000040,
    UiHidden = 0x00000080,
    Book = 0x00000100,
    Vendor = 0x00000200,
    PlayerKillerSwitch = 0x00000400,
    NonPlayerKillerSwitch = 0x00000800,
    Door = 0x00001000,
    Corpse = 0x00002000,
    Lifestone = 0x00004000,
    Food = 0x00008000,
    Healer = 0x00010000,
    Lockpick = 0x00020000,
    Portal = 0x00040000,
    Admin = 0x00100000,
    FreePlayerKiller = 0x00200000,
    ImmuneCellRestrictions = 0x00400000,
    RequiresPackSlot = 0x00800000,
    Retained = 0x01000000,
    PlayerKillerLite = 0x02000000,
    IncludesSecondHeader = 0x04000000,
    Bindstone = 0x08000000,
    VolatileRare = 0x10000000,
    WieldOnUse = 0x20000000,
    WieldLeft = 0x40000000,
}

public enum ItemPrimaryUseResult : ushort
{
    None = 0,
    ItemUse = 1,
    PlaceInBackpack = 2,
    WieldRight = 3,
    AutoSort = 4,
    OpenSecureTrade = 5,
    OpenSalvage = 6,
    BeginGame = 7,
    WieldLeft = 8,
}

public readonly record struct ItemPolicyObject(
    uint Id,
    ItemType Type,
    PublicWeenieFlags Flags,
    uint ContainerId,
    uint WielderId,
    EquipMask ValidLocations,
    EquipMask CurrentLocation,
    byte CombatUse,
    int ItemsCapacity,
    int ContainersCapacity,
    uint Useability,
    uint TargetType,
    bool OwnedByPlayer,
    bool IsContainer,
    bool IsComponentPack,
    int TradeState,
    int StackSize,
    int MaxSplitSize,
    bool IsIn3DView,
    string Name = "item")
{
    public bool IsPlayer => (Flags & PublicWeenieFlags.Player) != 0;
}

public enum ItemPolicyActionKind
{
    PlaceInBackpack,
    WieldRight,
    AutoSort,
    OpenSecureTrade,
    OpenSalvage,
    BeginGame,
    WieldLeft,
    OpenContainedContainer,
    SetGroundObject,
    EnterTargetMode,
    SendUse,
    SendUseWithTarget,
    IncrementBusy,
    ConfirmPlayerKillerSwitch,
    ConfirmNonPlayerKillerSwitch,
    ConfirmVolatileRare,
    MergeStack,
    SellToVendor,
    StartSecureTrade,
    GiveToTarget,
    PlaceInContainer,
    SplitToWorld,
    DropToWorld,
    Reject,
}

public readonly record struct ItemPolicyAction(
    ItemPolicyActionKind Kind,
    uint ObjectId = 0,
    uint TargetId = 0,
    int Amount = 0,
    string? Message = null);

public readonly record struct ItemUsePolicyInput(
    ItemPolicyObject Source,
    uint PlayerId,
    uint GroundObjectId,
    bool ReadyForInventoryRequest,
    uint ActiveVendorId,
    bool BypassClassification,
    bool UseCurrentSelection,
    ItemPolicyObject? SelectedTarget,
    bool ConfirmVolatileRareUses,
    bool InNonCombatMode);

public readonly record struct ItemUsePolicyDecision(
    bool Consumed,
    IReadOnlyList<ItemPolicyAction> Actions);

public readonly record struct ItemPlacementPolicyInput(
    ItemPolicyObject Item,
    uint PlayerId,
    uint GroundObjectId,
    bool ReadyForInventoryRequest,
    uint TargetId,
    ItemPolicyObject? Target,
    bool AllowGroundFallback,
    bool MergeAccepted,
    bool DragOnPlayerOpensSecureTrade,
    bool PlayerOnGround,
    int SplitSize);

public readonly record struct ItemPlacementPolicyDecision(
    bool ReturnValue,
    IReadOnlyList<ItemPolicyAction> Actions);

public static class ItemInteractionPolicy
{
    private static readonly EquipMask BodyLocations = (EquipMask)0x00007E00u;
    private static readonly EquipMask ClothingLocations = (EquipMask)0x080001FFu;
    private static readonly EquipMask HeldLocations = (EquipMask)0x7C0F8000u;
    private const ItemType ToolbarEquipmentTypes =
        ItemType.Armor | ItemType.Clothing | ItemType.Jewelry;

    public static bool IsToolbarUseEnabled(
        ItemType type,
        byte combatUse,
        uint useability)
        => combatUse != 0
            || (type & ToolbarEquipmentTypes) != 0
            || ItemUseability.IsUseable(useability);

    public static ItemPrimaryUseResult DetermineUseResult(
        in ItemPolicyObject item,
        uint playerId,
        uint groundObjectId)
    {
        bool looseOrViewed = (item.ContainerId == 0
                && (item.Flags & PublicWeenieFlags.Stuck) == 0)
            || (groundObjectId != 0 && item.ContainerId == groundObjectId);

        if (looseOrViewed && (item.WielderId == 0 || item.WielderId == playerId))
        {
            bool ordinaryLooseItem = (item.Flags & PublicWeenieFlags.RequiresPackSlot) == 0
                && item.ItemsCapacity == 0
                && item.ContainersCapacity == 0;
            if (ordinaryLooseItem || item.IsComponentPack)
                return ItemPrimaryUseResult.PlaceInBackpack;
        }

        if (item.OwnedByPlayer)
        {
            bool wieldOnPrimaryUse = item.CombatUse != 0
                || (item.Type & ItemType.Caster) != 0
                || (item.Flags & PublicWeenieFlags.WieldOnUse) != 0;
            if (wieldOnPrimaryUse && item.WielderId != playerId)
                return (item.Flags & PublicWeenieFlags.WieldLeft) != 0
                    ? ItemPrimaryUseResult.WieldLeft
                    : ItemPrimaryUseResult.WieldRight;

            if (HasUnusedLocation(item.ValidLocations, item.CurrentLocation))
                return ItemPrimaryUseResult.AutoSort;

            if ((item.Type & ItemType.TinkeringTool) != 0)
                return ItemPrimaryUseResult.OpenSalvage;
        }
        else if ((item.Type & ItemType.Gameboard) != 0)
        {
            return ItemPrimaryUseResult.BeginGame;
        }

        if (ItemUseability.IsUseable(item.Useability))
            return ItemPrimaryUseResult.ItemUse;

        if (item.IsPlayer && item.Id != playerId)
            return ItemPrimaryUseResult.OpenSecureTrade;

        return ItemPrimaryUseResult.None;
    }

    public static ItemUsePolicyDecision DecideUse(in ItemUsePolicyInput input)
    {
        var source = input.Source;
        if (!input.ReadyForInventoryRequest)
            return Consumed();
        if (input.ActiveVendorId != 0 && source.ContainerId == input.ActiveVendorId)
            return Consumed();

        if (!input.BypassClassification)
        {
            var primary = DetermineUseResult(source, input.PlayerId, input.GroundObjectId);
            if (primary is >= ItemPrimaryUseResult.PlaceInBackpack
                and <= ItemPrimaryUseResult.BeginGame)
                return Consumed(BuildUsingItemActions(source, input.PlayerId,
                    input.GroundObjectId, primary));
        }

        if (source.TradeState == 1)
            return Reject($"You cannot use the {NameOf(source)} because you are trading it");

        if (source.CurrentLocation == EquipMask.None
            && ItemUseability.LeastLimitedSourceUse(source.Useability) == ItemUseability.Wielded)
            return Reject($"You must wield the {NameOf(source)} to use it");

        if (ItemUseability.IsTargeted(source.Useability))
        {
            if (!input.UseCurrentSelection)
                return Consumed(new ItemPolicyAction(ItemPolicyActionKind.EnterTargetMode, source.Id));

            if (input.SelectedTarget is not { } target)
                return Reject($"Select your target before using the {NameOf(source)}");
            if (TargetCompatibilityFailure(source, target, input.PlayerId) is { } failure)
                return Reject(failure);

            var actions = new List<ItemPolicyAction>
            {
                new(ItemPolicyActionKind.SendUseWithTarget, source.Id, target.Id),
                new(ItemPolicyActionKind.IncrementBusy),
            };
            actions.AddRange(BuildUsingItemActions(source, input.PlayerId,
                input.GroundObjectId, DetermineUseResult(source, input.PlayerId, input.GroundObjectId)));
            return Consumed(actions);
        }

        if (ItemUseability.IsUseable(source.Useability))
        {
            if ((source.Flags & PublicWeenieFlags.PlayerKillerSwitch) != 0)
                return Consumed(new ItemPolicyAction(
                    ItemPolicyActionKind.ConfirmPlayerKillerSwitch, source.Id));
            if ((source.Flags & PublicWeenieFlags.NonPlayerKillerSwitch) != 0)
                return Consumed(new ItemPolicyAction(
                    ItemPolicyActionKind.ConfirmNonPlayerKillerSwitch, source.Id));
            if ((source.Flags & PublicWeenieFlags.VolatileRare) != 0
                && input.ConfirmVolatileRareUses)
                return Consumed(new ItemPolicyAction(
                    ItemPolicyActionKind.ConfirmVolatileRare, source.Id));

            var actions = new List<ItemPolicyAction>
            {
                new(ItemPolicyActionKind.SendUse, source.Id),
                new(ItemPolicyActionKind.IncrementBusy),
            };
            actions.AddRange(BuildUsingItemActions(source, input.PlayerId,
                input.GroundObjectId, DetermineUseResult(source, input.PlayerId, input.GroundObjectId)));
            return Consumed(actions);
        }

        var fallback = BuildUsingItemActions(source, input.PlayerId,
            input.GroundObjectId, DetermineUseResult(source, input.PlayerId, input.GroundObjectId));
        if (fallback.Count != 0)
            return Consumed(fallback);
        if (source.Id == input.PlayerId)
            return new ItemUsePolicyDecision(false, Array.Empty<ItemPolicyAction>());
        if ((source.Flags & PublicWeenieFlags.Door) != 0)
            return Reject($"You can't open or close this {NameOf(source)} that way");
        if ((source.Flags & PublicWeenieFlags.Attackable) != 0
            && input.InNonCombatMode)
            return Reject($"To attack {NameOf(source)}, click on the dove icon first");
        if ((source.Flags & PublicWeenieFlags.Attackable) == 0
            || input.InNonCombatMode)
            return Reject($"The {NameOf(source)} cannot be used");
        return new ItemUsePolicyDecision(false, Array.Empty<ItemPolicyAction>());
    }

    public static bool IsTargetCompatible(
        in ItemPolicyObject source,
        in ItemPolicyObject target,
        uint playerId)
        => TargetCompatibilityFailure(source, target, playerId) is null;

    private static string? TargetCompatibilityFailure(
        in ItemPolicyObject source,
        in ItemPolicyObject target,
        uint playerId)
    {
        if (source.TradeState == 1)
            return $"You cannot use the {NameOf(source)} because you are trading it";

        if (target.TradeState == 1)
            return $"You can't use the {NameOf(source)} on an item you are trading";

        uint flags = ItemUseability.TargetFlags(source.Useability);
        if (!target.OwnedByPlayer)
        {
            uint least = ItemUseability.LeastLimitedTargetUse(source.Useability);
            if ((least & ItemUseability.Contained) != 0)
            {
                if (!(target.Id == playerId && (flags & ItemUseability.Self) != 0))
                    return $"You can't use the {NameOf(source)} on what you don't own";
            }
            else if ((least & ItemUseability.Wielded) != 0)
            {
                return $"You can't use the {NameOf(source)} on what you aren't wielding";
            }
        }

        if (target.Id == playerId && (flags & ItemUseability.Self) == 0)
            return $"Cannot use the {NameOf(source)} on yourself";

        return (source.TargetType & (uint)target.Type) != 0
            ? null
            : $"Cannot use the {NameOf(source)} with the {NameOf(target)}";
    }

    public static ItemPlacementPolicyDecision DecidePlacement(
        in ItemPlacementPolicyInput input)
    {
        if (!input.ReadyForInventoryRequest)
            return Placement(false);

        if (input.TargetId == input.PlayerId)
            return Placement(true, new ItemPolicyAction(ItemPolicyActionKind.PlaceInBackpack, input.Item.Id));
        if (!input.Item.OwnedByPlayer)
            return Placement(false, RejectAction($"You must first pick up the {NameOf(input.Item)}"));
        if (input.Item.TradeState != 0)
            return Placement(false, RejectAction(
                $"You are trading the {NameOf(input.Item)}, it cannot be dropped"));

        if (input.TargetId == 0)
            return input.AllowGroundFallback ? PlaceOnGround(input) : Placement(false);
        if (input.MergeAccepted)
            return Placement(true, new ItemPolicyAction(ItemPolicyActionKind.MergeStack,
                input.Item.Id, input.TargetId, input.SplitSize));
        if (input.Target is not { } target)
            return input.AllowGroundFallback ? PlaceOnGround(input) : Placement(false);

        if ((target.Flags & PublicWeenieFlags.Vendor) != 0)
        {
            if (input.SplitSize >= input.Item.MaxSplitSize)
                return Placement(false, new ItemPolicyAction(ItemPolicyActionKind.SellToVendor,
                    input.Item.Id, target.Id, input.SplitSize));
            return Placement(false, RejectAction("You must split the stack before selling it."));
        }

        if (input.DragOnPlayerOpensSecureTrade && target.IsPlayer)
            return Placement(false, new ItemPolicyAction(ItemPolicyActionKind.StartSecureTrade,
                input.Item.Id, target.Id, input.SplitSize));

        if (target.Type == ItemType.Creature)
            return Placement(true, new ItemPolicyAction(ItemPolicyActionKind.GiveToTarget,
                input.Item.Id, target.Id, input.SplitSize));

        if (target.IsContainer)
        {
            if ((target.Flags & PublicWeenieFlags.Openable) == 0)
                return Placement(false, RejectAction($"The {NameOf(target)} is locked"));
            if (target.Id != input.GroundObjectId)
                return Placement(false, RejectAction($"You must open the {NameOf(target)} first"));
            return Placement(true, new ItemPolicyAction(ItemPolicyActionKind.PlaceInContainer,
                input.Item.Id, target.Id, input.SplitSize));
        }

        if (input.AllowGroundFallback)
            return PlaceOnGround(input);
        return Placement(false, RejectAction(
            $"Cannot give {NameOf(input.Item)} to {NameOf(target)}"));
    }

    private static IReadOnlyList<ItemPolicyAction> BuildUsingItemActions(
        in ItemPolicyObject item,
        uint playerId,
        uint groundObjectId,
        ItemPrimaryUseResult primary)
    {
        var actions = new List<ItemPolicyAction>(3);
        ItemPolicyActionKind? primaryAction = primary switch
        {
            ItemPrimaryUseResult.PlaceInBackpack => ItemPolicyActionKind.PlaceInBackpack,
            ItemPrimaryUseResult.WieldRight => ItemPolicyActionKind.WieldRight,
            ItemPrimaryUseResult.AutoSort => ItemPolicyActionKind.AutoSort,
            ItemPrimaryUseResult.OpenSecureTrade => ItemPolicyActionKind.OpenSecureTrade,
            ItemPrimaryUseResult.OpenSalvage => ItemPolicyActionKind.OpenSalvage,
            ItemPrimaryUseResult.BeginGame => ItemPolicyActionKind.BeginGame,
            ItemPrimaryUseResult.WieldLeft => ItemPolicyActionKind.WieldLeft,
            _ => null,
        };
        if (primaryAction is { } action)
            actions.Add(new ItemPolicyAction(action, item.Id));

        if (item.OwnedByPlayer && item.IsContainer)
            actions.Add(new ItemPolicyAction(ItemPolicyActionKind.OpenContainedContainer, item.Id));
        if (!item.OwnedByPlayer && item.IsContainer
            && ItemUseability.IsUseable(item.Useability)
            && !ItemUseability.IsTargeted(item.Useability))
            actions.Add(new ItemPolicyAction(ItemPolicyActionKind.SetGroundObject, item.Id,
                groundObjectId));
        return actions;
    }

    private static bool HasUnusedLocation(EquipMask valid, EquipMask current)
        => ((valid & BodyLocations) != 0 && (current & BodyLocations) == 0)
        || ((valid & ClothingLocations) != 0 && (current & ClothingLocations) == 0)
        || ((valid & HeldLocations) != 0 && (current & HeldLocations) == 0);

    private static ItemPlacementPolicyDecision PlaceOnGround(
        in ItemPlacementPolicyInput input)
    {
        if (!input.PlayerOnGround)
            return Placement(false, RejectAction("You cannot do that in mid air"));
        if (input.SplitSize < input.Item.MaxSplitSize)
            return Placement(true, new ItemPolicyAction(ItemPolicyActionKind.SplitToWorld,
                input.Item.Id, Amount: input.SplitSize));
        if (!input.Item.IsIn3DView)
            return Placement(true, new ItemPolicyAction(ItemPolicyActionKind.DropToWorld, input.Item.Id));
        return Placement(false, RejectAction("Move cancelled"));
    }

    private static string NameOf(in ItemPolicyObject item)
        => string.IsNullOrWhiteSpace(item.Name) ? "item" : item.Name;

    private static ItemUsePolicyDecision Consumed(params ItemPolicyAction[] actions)
        => new(true, actions);

    private static ItemUsePolicyDecision Consumed(IReadOnlyList<ItemPolicyAction> actions)
        => new(true, actions);

    private static ItemUsePolicyDecision Reject(string message)
        => Consumed(RejectAction(message));

    private static ItemPolicyAction RejectAction(string message)
        => new(ItemPolicyActionKind.Reject, Message: message);

    private static ItemPlacementPolicyDecision Placement(
        bool returnValue,
        params ItemPolicyAction[] actions)
        => new(returnValue, actions);
}

using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Combat;

/// <summary>
/// Puts the right weapon in hand before an attack and keeps a missile
/// weapon fed, the way RynthAi's weapon swap gate does: the profile names
/// a weapon per style and a monster rule may name another; the equipment
/// contract wields it and, for a missile weapon, a stack of ammunition of
/// the type it fires. One equip per step; the caller waits for the swap
/// to land before pressing the attack.
/// </summary>
public sealed class WeaponReadiness
{
    private const byte CombatUseMelee = 1;
    private const byte CombatUseMissile = 2;
    private const byte CombatUseAmmo = 3;
    private const byte CombatUseTwoHanded = 5;
    private const uint ItemTypeCaster = 0x8000u;
    private const uint ItemTypeMissileWeapon = 0x100u;
    private const double EquipRetrySeconds = 2d;

    private double _lastEquipAt = double.NegativeInfinity;

    public enum Verdict
    {
        /// <summary>Nothing to change, or nothing configured to change.</summary>
        Ready,
        /// <summary>An equip went out; ask again next tick.</summary>
        Swapping,
        /// <summary>The wanted weapon or ammunition is not in the inventory.</summary>
        Missing,
    }

    /// <summary>The name a style wants, from the rule first and the profile second; empty leaves the hands alone.</summary>
    public static string WantedWeapon(CombatSettings combat, MonsterRule? rule) =>
        rule is { Weapon.Length: > 0 } ? rule.Weapon : combat.Style switch
        {
            CombatStyle.Melee => combat.MeleeWeapon,
            CombatStyle.Missile => combat.MissileWeapon,
            _ => combat.Wand,
        };

    public Verdict Ensure(IEquipmentAutomation equipment, CombatSettings combat, MonsterRule? rule, double now, out string detail)
    {
        detail = string.Empty;
        if (!equipment.IsAvailable)
            return Verdict.Ready;
        IReadOnlyList<PluginEquipmentItem> owned = equipment.CaptureOwnedEquipment();

        string wanted = WantedWeapon(combat, rule);
        PluginEquipmentItem? weapon = null;
        if (wanted.Length > 0)
        {
            weapon = FindByName(owned, wanted);
            if (weapon is null)
            {
                detail = $"no weapon named '{wanted}'";
                return Verdict.Missing;
            }
            if (!weapon.Value.IsEquipped)
                return Equip(equipment, weapon.Value, now, out detail);
        }

        if (combat.Style != CombatStyle.Missile || !combat.KeepAmmunition)
            return Verdict.Ready;

        // The bow in hand decides the ammunition, whether the profile named it or not.
        weapon ??= FirstEquipped(owned, item => item.CombatUse == CombatUseMissile || (item.ItemType & ItemTypeMissileWeapon) != 0u);
        if (weapon is null)
            return Verdict.Ready;
        uint ammoType = weapon.Value.AmmoType;
        if (ammoType == 0u)
            return Verdict.Ready;
        if (FirstEquipped(owned, item => item.CombatUse == CombatUseAmmo && (item.AmmoType & ammoType) != 0u) is not null)
            return Verdict.Ready;

        PluginEquipmentItem? best = null;
        foreach (PluginEquipmentItem item in owned)
        {
            if (item.IsEquipped || item.CombatUse != CombatUseAmmo || (item.AmmoType & ammoType) == 0u)
                continue;
            if (best is null || item.StackSize > best.Value.StackSize)
                best = item;
        }
        if (best is null)
        {
            detail = $"no ammunition for {weapon.Value.Name}";
            return Verdict.Missing;
        }
        return Equip(equipment, best.Value, now, out detail);
    }

    private Verdict Equip(IEquipmentAutomation equipment, in PluginEquipmentItem item, double now, out string detail)
    {
        detail = $"wielding {item.Name}";
        if (equipment.IsBusy || now - _lastEquipAt < EquipRetrySeconds)
            return Verdict.Swapping;
        _lastEquipAt = now;
        PluginEquipmentCommandResult result = equipment.Equip(item.ObjectId);
        if (result.Status is PluginEquipmentCommandStatus.Refused or PluginEquipmentCommandStatus.InvalidItem)
        {
            detail = $"could not wield {item.Name}: {result.Status} {result.Notice}";
            return Verdict.Missing;
        }
        return Verdict.Swapping;
    }

    private static PluginEquipmentItem? FindByName(IReadOnlyList<PluginEquipmentItem> owned, string name)
    {
        PluginEquipmentItem? partial = null;
        foreach (PluginEquipmentItem item in owned)
        {
            if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return item;
            if (partial is null && item.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                partial = item;
        }
        return partial;
    }

    private static PluginEquipmentItem? FirstEquipped(IReadOnlyList<PluginEquipmentItem> owned, Func<PluginEquipmentItem, bool> match)
    {
        foreach (PluginEquipmentItem item in owned)
        {
            if (item.IsEquipped && match(item))
                return item;
        }
        return null;
    }

    /// <summary>Whether an item is a caster (wand, orb, staff), for the magic style's default.</summary>
    public static bool IsCaster(in PluginEquipmentItem item) => (item.ItemType & ItemTypeCaster) != 0u;

    /// <summary>Whether an item is a melee weapon.</summary>
    public static bool IsMelee(in PluginEquipmentItem item) => item.CombatUse is CombatUseMelee or CombatUseTwoHanded;
}

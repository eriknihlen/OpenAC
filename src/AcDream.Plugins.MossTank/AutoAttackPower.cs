using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal static class AutoAttackPower
{
    private const uint MeleeWeapon = 0x00000001u;
    private const uint MissileWeapon = 0x00000100u;
    private const uint ShieldLocation = 0x00200000u;
    private const int SlashDamage = 0x0001;
    private const int PierceDamage = 0x0002;
    private const int TripleSlashAttack = 0x0040;
    private const uint RecklessnessSkill = 50u;

    public static float Resolve(
        MonsterRuleActions actions,
        CombatSettings settings,
        ICharacterInfo character,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(inventory);
        if (!settings.AutoAttackPower)
            return settings.AttackPower;

        PluginInventoryItem? weapon = FindWeapon(actions, inventory);
        if (weapon is not { } selected)
            return settings.AttackPower;
        if ((selected.ItemType & MissileWeapon) != 0u)
            return ClampForRecklessness(1f, settings, character);
        if ((selected.ItemType & MeleeWeapon) == 0u)
            return settings.AttackPower;

        int requestedDamage = RawDamage(actions.DamageType);
        if (requestedDamage is not (SlashDamage or PierceDamage))
            return ClampForRecklessness(1f, settings, character);

        PluginInventoryItem? offhand = FindOffhand(actions, selected, inventory);
        bool offhandMelee = offhand is { } held
            && (held.ItemType & MeleeWeapon) != 0u;
        bool offhandShield = offhand is { } shield
            && (shield.EquippedLocation & ShieldLocation) != 0u;
        bool slashPierce = (selected.DamageType & (SlashDamage | PierceDamage))
            == (SlashDamage | PierceDamage);
        bool tripleSlash = (selected.AttackType & TripleSlashAttack) != 0;

        float power;
        if (selected.WeaponType == 1 && !offhandMelee)
        {
            power = requestedDamage == SlashDamage && slashPierce ? 0.5f : 0f;
        }
        else if (requestedDamage == PierceDamage && slashPierce && !tripleSlash)
        {
            power = 0.2f;
        }
        else if (requestedDamage == PierceDamage
            && slashPierce
            && tripleSlash
            && offhandMelee)
        {
            power = 0.49f;
        }
        else if (requestedDamage != PierceDamage
            || !slashPierce
            || !tripleSlash
            || offhandShield)
        {
            power = 1f;
        }
        else
        {
            power = 0.2f;
        }

        return ClampForRecklessness(power, settings, character);
    }

    private static PluginInventoryItem? FindWeapon(
        MonsterRuleActions actions,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        PluginInventoryItem? equipped = null;
        PluginInventoryItem? named = null;
        foreach (PluginInventoryItem item in inventory)
        {
            if (actions.WeaponObjectId != 0u
                && item.ObjectId == actions.WeaponObjectId)
            {
                return item;
            }
            if (!string.IsNullOrWhiteSpace(actions.WeaponName)
                && item.Name.Equals(actions.WeaponName, StringComparison.Ordinal))
            {
                named ??= item;
            }
            if (item.IsEquipped
                && (item.ItemType & (MeleeWeapon | MissileWeapon)) != 0u)
            {
                equipped ??= item;
            }
        }
        return named ?? equipped;
    }

    private static PluginInventoryItem? FindOffhand(
        MonsterRuleActions actions,
        PluginInventoryItem weapon,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        PluginInventoryItem? equipped = null;
        PluginInventoryItem? named = null;
        foreach (PluginInventoryItem item in inventory)
        {
            if (item.ObjectId == weapon.ObjectId)
                continue;
            if (actions.OffhandObjectId != 0u
                && item.ObjectId == actions.OffhandObjectId)
            {
                return item;
            }
            if (!string.IsNullOrWhiteSpace(actions.OffhandName)
                && item.Name.Equals(actions.OffhandName, StringComparison.Ordinal))
            {
                named ??= item;
            }
            if (item.IsEquipped
                && ((item.ItemType & MeleeWeapon) != 0u
                    || (item.EquippedLocation & ShieldLocation) != 0u))
            {
                equipped ??= item;
            }
        }
        return named ?? equipped;
    }

    private static float ClampForRecklessness(
        float power,
        CombatSettings settings,
        ICharacterInfo character)
    {
        if (!settings.UseRecklessness
            || !character.TryGetSkill(
                RecklessnessSkill,
                out PluginSkillInfo recklessness)
            || recklessness.Training is not (
                PluginSkillTraining.Trained or PluginSkillTraining.Specialized))
        {
            return power;
        }
        return Math.Clamp(power, 0.11f, 0.9f);
    }

    private static int RawDamage(MonsterDamageType damage) => damage switch
    {
        MonsterDamageType.Slash => SlashDamage,
        MonsterDamageType.Pierce => PierceDamage,
        MonsterDamageType.Bludgeon => 0x0004,
        MonsterDamageType.Cold => 0x0008,
        MonsterDamageType.Fire => 0x0010,
        MonsterDamageType.Acid => 0x0020,
        MonsterDamageType.Electric => 0x0040,
        MonsterDamageType.Nether => 0x0400,
        _ => 0,
    };
}

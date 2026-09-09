using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal enum VitalKind
{
    Health = 2,
    Stamina = 4,
    Mana = 6,
}

public enum VitalAction
{
    None = 0,
    StaminaToMana,
    Revitalize,
}

/// <summary>
/// VTank's nine <c>Recharge-*</c> sliders and its recharge-handler options.
/// Values are normalized 0..1 at the plugin/UI seam; VTank stores percentages.
/// </summary>
public sealed class VitalSettings
{
    public bool Enabled { get; set; } = true;

    public double NormalHealth { get; set; } = 0.75;
    public double NormalStamina { get; set; } = 0.50;
    public double NormalMana { get; set; } = 0.50;
    public double NoTargetHealth { get; set; } = 0.01;
    public double NoTargetStamina { get; set; } = 0.01;
    public double NoTargetMana { get; set; } = 0.01;
    public double HelperHealth { get; set; } = 0.20;
    public double HelperStamina { get; set; } = 0.01;
    public double HelperMana { get; set; } = 0.01;
    public double HelperHealthDistance { get; set; } = 59.6d;
    public double HelperStaminaDistance { get; set; } = 59.6d;
    public double HelperManaDistance { get; set; } = 32d;

    public bool HelpOthers { get; set; } = true;
    public bool UseHealersHeart { get; set; } = true;
    public double RechargeBoostTimeSeconds { get; set; } = 5d;
    public int RechargeBoostAmount { get; set; } = 40;
    public bool ClearLevelBoostFlagOnCast { get; set; } = true;
    public int DropToPeaceModeRetryCount { get; set; } = 34;
    public string RechargeHandlerSet { get; set; } = "RechargeHandlerSet";
    public IReadOnlyList<RechargeHandlerRow> RechargeHandlerRows { get; set; } =
        VtankDefaultSettingsDatabase.DefaultRechargeHandlerRows;
    public bool UseKitsInMagicMode { get; set; } = true;
    public bool GoToPeaceModeToUseKits { get; set; }
    public int MinimumHealKitSuccessChance { get; set; } = 95;
    public double StaminaToHealthMultiplier { get; set; } = 1.9;
    public double ManaToHealthMultiplier { get; set; } = 2.8;
    public bool CastDispelSelf { get; set; }
    public bool UseDispelItems { get; set; }
    public bool UseDispelDrum { get; set; }

    public double ManaFloor
    {
        get => NormalMana;
        set => NormalMana = Clamp(value);
    }

    public double ManaTarget
    {
        get => NormalMana;
        set => NormalMana = Clamp(value);
    }

    public double StaminaFloor
    {
        get => NormalStamina;
        set => NormalStamina = Clamp(value);
    }

    internal double Threshold(VitalKind vital, bool noTarget) => vital switch
    {
        VitalKind.Health => noTarget
            ? Math.Max(NormalHealth, NoTargetHealth)
            : NormalHealth,
        VitalKind.Stamina => noTarget
            ? Math.Max(NormalStamina, NoTargetStamina)
            : NormalStamina,
        VitalKind.Mana => noTarget
            ? Math.Max(NormalMana, NoTargetMana)
            : NormalMana,
        _ => 0d,
    };

    internal double NormalThreshold(VitalKind vital) => vital switch
    {
        VitalKind.Health => NormalHealth,
        VitalKind.Stamina => NormalStamina,
        VitalKind.Mana => NormalMana,
        _ => 0d,
    };

    private static double Clamp(double value) => Math.Clamp(value, 0d, 1d);
}

public static class VitalPlan
{
    public const string HealSelfStem = "Heal Self";
    public const string StaminaToManaStem = "Stamina to Mana";
    public const string RevitalizeStem = "Revitalize";

    internal static VitalKind? DecideNeed(
        ICharacterInfo character,
        VitalSettings settings,
        bool noTarget,
        int healthCurrentAdjustment = 0,
        int staminaCurrentAdjustment = 0,
        int manaCurrentAdjustment = 0)
    {
        if (!settings.Enabled)
            return null;

        foreach (VitalKind vital in new[]
                 {
                     VitalKind.Health,
                     VitalKind.Stamina,
                     VitalKind.Mana,
                 })
        {
            (uint current, uint maximum) = Read(character, vital);
            if (maximum == 0u)
                continue;
            int adjustment = vital switch
            {
                VitalKind.Health => healthCurrentAdjustment,
                VitalKind.Stamina => staminaCurrentAdjustment,
                VitalKind.Mana => manaCurrentAdjustment,
                _ => 0,
            };
            current = adjustment <= 0
                ? current
                : (uint)Math.Max(0L, (long)current - adjustment);
            if ((double)current / maximum < settings.Threshold(vital, noTarget))
                return vital;
        }
        return null;
    }

    internal static bool IsBelowNormal(
        ICharacterInfo character,
        VitalSettings settings,
        VitalKind vital)
    {
        (uint current, uint maximum) = Read(character, vital);
        return maximum != 0u
            && (double)current / maximum < settings.NormalThreshold(vital);
    }

    internal static int Percent(ICharacterInfo character, VitalKind vital)
    {
        (uint current, uint maximum) = Read(character, vital);
        return maximum == 0u ? 100 : (int)(100u * current / maximum);
    }

    public static VitalAction Decide(ICharacterInfo character, VitalSettings settings)
    {
        if (!settings.Enabled || character.MaxMana == 0 || character.MaxStamina == 0)
            return VitalAction.None;
        double mana = (double)character.CurrentMana / character.MaxMana;
        if (mana >= settings.NormalMana)
            return VitalAction.None;
        double stamina = (double)character.CurrentStamina / character.MaxStamina;
        return stamina > settings.NormalStamina
            ? VitalAction.StaminaToMana
            : VitalAction.Revitalize;
    }

    public static bool TryFind(
        IReadOnlyList<PluginSpellInfo> known,
        string stem,
        IReadOnlyDictionary<uint, uint> skillLevels,
        int skillExcess,
        out PluginSpellInfo pick)
    {
        pick = default;
        bool found = false;
        foreach (PluginSpellInfo spell in known)
        {
            if (spell.Name.IndexOf(stem, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (spell.School != 0
                && skillLevels.TryGetValue(spell.School, out uint level)
                && level < spell.Difficulty + skillExcess)
            {
                continue;
            }
            if (!found
                || spell.Quality > pick.Quality
                || (spell.Quality == pick.Quality && spell.Tier > pick.Tier))
            {
                pick = spell;
                found = true;
            }
        }
        return found;
    }

    private static (uint Current, uint Maximum) Read(
        ICharacterInfo character,
        VitalKind vital) => vital switch
    {
        VitalKind.Health => (character.CurrentHealth, character.MaxHealth),
        VitalKind.Stamina => (character.CurrentStamina, character.MaxStamina),
        VitalKind.Mana => (character.CurrentMana, character.MaxMana),
        _ => (0u, 0u),
    };
}

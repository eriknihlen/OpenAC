using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal enum VitalRechargeSourceKind
{
    LearnedSpell,
    CasterItem,
    Kit,
    Food,
}

internal readonly record struct VitalRechargeChoice(
    VitalKind Vital,
    VitalRechargeSourceKind SourceKind,
    string Name,
    uint SpellId,
    uint ItemObjectId,
    PluginCombatMode? RequiredMode)
{
    public bool UsesItem => SourceKind != VitalRechargeSourceKind.LearnedSpell;
    public uint TargetObjectId { get; init; }
}

internal enum VitalRechargeMethod
{
    RegularSpell,
    StaminaToHealth,
    ManaToHealth,
    HealthToStamina,
    HealthToMana,
    Kit,
    Food,
}

public readonly record struct RechargeHandlerRow(
    int Vital,
    string HandlerString,
    int MinPercent,
    int MaxPercent,
    int Stance)
{
    internal static VitalKind? ToVitalKind(int vital) => vital switch
    {
        1 => VitalKind.Health,
        2 => VitalKind.Stamina,
        3 => VitalKind.Mana,
        _ => null,
    };

    internal static int FromVitalKind(VitalKind vital) => vital switch
    {
        VitalKind.Health => 1,
        VitalKind.Stamina => 2,
        VitalKind.Mana => 3,
        _ => 0,
    };
}

internal static class VitalRechargePlanner
{
    private const uint HealingSkill = 21u;
    private const uint CasterItemType = 0x00008000u;

    public static bool TryPlan(
        VitalKind vital,
        IAutomationSurface automation,
        VitalSettings settings,
        CombatSettings combatSettings,
        out VitalRechargeChoice choice)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(combatSettings);

        PluginCombatMode mode = automation.Combat.Snapshot.Mode;
        int percent = VitalPlan.Percent(automation.Character, vital);
        IReadOnlyList<VitalRechargeMethod> handlers = Handlers(
            vital,
            mode == PluginCombatMode.Magic,
            percent,
            settings.RechargeHandlerSet,
            settings.RechargeHandlerRows.Count != 0
                ? settings.RechargeHandlerRows
                : null);
        IReadOnlyList<PluginInventoryItem> items =
            automation.Items.CaptureOwnedItems();

        foreach (VitalRechargeMethod handler in handlers)
        {
            if (TryHandler(
                    handler,
                    vital,
                    automation,
                    settings,
                    combatSettings,
                    items,
                    out choice))
            {
                return true;
            }
        }
        choice = default;
        return false;
    }

    public static bool TryPlanHelper(
        IAutomationSurface automation,
        VitalSettings settings,
        out VitalRechargeChoice choice) => TryPlanHelper(
            automation,
            settings,
            new CombatSettings(),
            out choice);

    public static bool TryPlanHelper(
        IAutomationSurface automation,
        VitalSettings settings,
        CombatSettings combatSettings,
        out VitalRechargeChoice choice)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.HelpOthers || !automation.Fellowship.IsInFellowship)
        {
            choice = default;
            return false;
        }

        IReadOnlyList<PluginFellowMember> members =
            automation.Fellowship.CaptureMembers();
        foreach ((VitalKind vital, double threshold, double distance, uint baseSpell)
            in new[]
            {
                (VitalKind.Health, settings.HelperHealth,
                    settings.HelperHealthDistance, (uint)SpellId.AdjaSGift),
                (VitalKind.Stamina, settings.HelperStamina,
                    settings.HelperStaminaDistance, (uint)SpellId.Replenish),
                (VitalKind.Mana, settings.HelperMana,
                    settings.HelperManaDistance, (uint)SpellId.GiftOfEssence),
            })
        {
            PluginFellowMember? target = Lowest(members, vital, threshold, (float)distance);
            if (target is not { } fellow)
                continue;
            if (vital == VitalKind.Health
                && TryHealersHeart(
                    automation,
                    settings,
                    fellow,
                    out choice))
            {
                return true;
            }
            if (!automation.Spells.TryGet(baseSpell, out PluginSpellInfo basis)
                || !TryFindFamily(
                    automation.Spells.KnownSelfBuffs,
                    basis.Family,
                    automation.Character,
                    automation.Spells,
                    combatSettings.BlacklistedSpellComponents,
                    out PluginSpellInfo spell))
            {
                continue;
            }

            choice = new VitalRechargeChoice(
                vital,
                VitalRechargeSourceKind.LearnedSpell,
                spell.Name,
                spell.SpellId,
                0u,
                PluginCombatMode.Magic)
            {
                TargetObjectId = fellow.ObjectId,
            };
            return true;
        }
        choice = default;
        return false;
    }

    private static bool TryHealersHeart(
        IAutomationSurface automation,
        VitalSettings settings,
        in PluginFellowMember target,
        out VitalRechargeChoice choice)
    {
        choice = default;
        if (!settings.UseHealersHeart
            || !automation.Character.TryGetSkill(33u, out PluginSkillInfo life)
            || life.Current < 245u
            || !automation.Character.TryGetSkill(14u, out PluginSkillInfo secondary)
            || secondary.Current < 105u)
        {
            return false;
        }

        PluginInventoryItem selected = default;
        int rank = 0;
        foreach (PluginInventoryItem item in automation.Items.CaptureOwnedItems())
        {
            int candidateRank = item.Name switch
            {
                "Legendary Seed of Mornings" => 2,
                "The Healer's Heart" => 1,
                _ => 0,
            };
            if (candidateRank <= rank)
                continue;
            selected = item;
            rank = candidateRank;
        }
        if (selected.ObjectId == 0u)
            return false;

        choice = new VitalRechargeChoice(
            VitalKind.Health,
            VitalRechargeSourceKind.CasterItem,
            selected.Name,
            0u,
            selected.ObjectId,
            null)
        {
            TargetObjectId = target.ObjectId,
        };
        return true;
    }

    internal static IReadOnlyList<VitalRechargeMethod> Handlers(
        VitalKind vital,
        bool magicMode,
        int currentPercent,
        string? handlerSet = null,
        IReadOnlyList<RechargeHandlerRow>? handlerRows = null)
    {
        if (handlerRows is { Count: > 0 }
            && TryHandlersFromRows(
                vital,
                magicMode,
                currentPercent,
                handlerRows,
                out VitalRechargeMethod[] fromRows))
        {
            return fromRows;
        }

        IReadOnlyList<VitalRechargeMethod> defaults;
        if (magicMode)
        {
            defaults = vital switch
            {
                VitalKind.Health when currentPercent <= 15 =>
                [
                    VitalRechargeMethod.StaminaToHealth,
                    VitalRechargeMethod.ManaToHealth,
                    VitalRechargeMethod.RegularSpell,
                    VitalRechargeMethod.Food,
                    VitalRechargeMethod.Kit,
                ],
                VitalKind.Health =>
                [
                    VitalRechargeMethod.Kit,
                    VitalRechargeMethod.StaminaToHealth,
                    VitalRechargeMethod.ManaToHealth,
                    VitalRechargeMethod.RegularSpell,
                    VitalRechargeMethod.Food,
                ],
                VitalKind.Stamina =>
                [
                    VitalRechargeMethod.Kit,
                    VitalRechargeMethod.RegularSpell,
                    VitalRechargeMethod.Food,
                ],
                VitalKind.Mana =>
                [
                    VitalRechargeMethod.Kit,
                    VitalRechargeMethod.Food,
                    VitalRechargeMethod.RegularSpell,
                ],
                _ => [],
            };
        }
        else
        {
            defaults = vital switch
            {
                VitalKind.Health when currentPercent <= 10 =>
                [
                    VitalRechargeMethod.Food,
                    VitalRechargeMethod.Kit,
                    VitalRechargeMethod.StaminaToHealth,
                    VitalRechargeMethod.RegularSpell,
                ],
                VitalKind.Health when currentPercent <= 15 =>
                [
                    VitalRechargeMethod.Food,
                    VitalRechargeMethod.Kit,
                    VitalRechargeMethod.RegularSpell,
                ],
                VitalKind.Health =>
                [
                    VitalRechargeMethod.Kit,
                    VitalRechargeMethod.Food,
                    VitalRechargeMethod.RegularSpell,
                ],
                VitalKind.Stamina or VitalKind.Mana =>
                [
                    VitalRechargeMethod.Kit,
                    VitalRechargeMethod.Food,
                    VitalRechargeMethod.RegularSpell,
                ],
                _ => [],
            };
        }

        return TryParseHandlerSet(
            handlerSet,
            HandlerContext(vital, magicMode, currentPercent),
            out VitalRechargeMethod[] custom)
                ? custom
                : defaults;
    }

    private static bool TryHandlersFromRows(
        VitalKind vital,
        bool magicMode,
        int currentPercent,
        IReadOnlyList<RechargeHandlerRow> rows,
        out VitalRechargeMethod[] handlers)
    {
        int vitalCode = RechargeHandlerRow.FromVitalKind(vital);
        int stance = magicMode ? 1 : 2;
        var matched = new List<VitalRechargeMethod>();
        foreach (RechargeHandlerRow row in rows)
        {
            if (row.Vital != vitalCode
                || row.Stance != stance
                || currentPercent < row.MinPercent
                || currentPercent > row.MaxPercent)
            {
                continue;
            }
            if (TryParseHandlerToken(row.HandlerString, out VitalRechargeMethod method))
                matched.Add(method);
        }
        handlers = [.. matched];
        return handlers.Length != 0;
    }

    private static bool TryParseHandlerToken(string source, out VitalRechargeMethod method)
    {
        string normalized = source.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        switch (normalized)
        {
            case "regularspell":
                method = VitalRechargeMethod.RegularSpell;
                return true;
            case "staminatohealth":
                method = VitalRechargeMethod.StaminaToHealth;
                return true;
            case "manatohealth":
                method = VitalRechargeMethod.ManaToHealth;
                return true;
            case "healthtostamina":
                method = VitalRechargeMethod.HealthToStamina;
                return true;
            case "healthtomana":
                method = VitalRechargeMethod.HealthToMana;
                return true;
            case "kit":
            case "kitrecharge":
                method = VitalRechargeMethod.Kit;
                return true;
            case "food":
            case "rechargewithfood":
                method = VitalRechargeMethod.Food;
                return true;
            default:
                method = default;
                return false;
        }
    }

    private static string HandlerContext(
        VitalKind vital,
        bool magicMode,
        int currentPercent)
    {
        string stance = magicMode ? "magic" : "combat";
        string vitalName = vital.ToString().ToLowerInvariant();
        string band = vital == VitalKind.Health
            ? magicMode
                ? currentPercent <= 15 ? "low" : "normal"
                : currentPercent <= 10
                    ? "critical"
                    : currentPercent <= 15 ? "low" : "normal"
            : "normal";
        return $"{stance}-{vitalName}-{band}";
    }

    private static bool TryParseHandlerSet(
        string? source,
        string context,
        out VitalRechargeMethod[] handlers)
    {
        handlers = [];
        if (string.IsNullOrWhiteSpace(source)
            || source.Equals("RechargeHandlerSet", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string selected = source;
        if (source.Contains('='))
        {
            selected = string.Empty;
            string fallbackContext = context.EndsWith(
                    "-critical",
                    StringComparison.Ordinal)
                || context.EndsWith("-low", StringComparison.Ordinal)
                    ? context[..context.LastIndexOf('-')] + "-normal"
                    : string.Empty;
            foreach (string segment in source.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries))
            {
                int equals = segment.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = segment[..equals].Trim();
                if (key.Equals(context, StringComparison.OrdinalIgnoreCase))
                {
                    selected = segment[(equals + 1)..];
                    break;
                }
                if (selected.Length == 0
                    && fallbackContext.Length != 0
                    && key.Equals(
                        fallbackContext,
                        StringComparison.OrdinalIgnoreCase))
                {
                    selected = segment[(equals + 1)..];
                }
            }
        }
        if (string.IsNullOrWhiteSpace(selected))
            return false;

        var parsed = new List<VitalRechargeMethod>();
        foreach (string token in selected.Split(
            [',', '>', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized = token.Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
            VitalRechargeMethod? value = normalized switch
            {
                "regularspell" => VitalRechargeMethod.RegularSpell,
                "staminatohealth" => VitalRechargeMethod.StaminaToHealth,
                "manatohealth" => VitalRechargeMethod.ManaToHealth,
                "healthtostamina" => VitalRechargeMethod.HealthToStamina,
                "healthtomana" => VitalRechargeMethod.HealthToMana,
                "kit" or "kitrecharge" => VitalRechargeMethod.Kit,
                "food" or "rechargewithfood" => VitalRechargeMethod.Food,
                _ => null,
            };
            if (value is { } method)
                parsed.Add(method);
        }
        if (parsed.Count == 0)
            return false;
        handlers = [.. parsed];
        return true;
    }

    private static bool TryHandler(
        VitalRechargeMethod method,
        VitalKind vital,
        IAutomationSurface automation,
        VitalSettings settings,
        CombatSettings combatSettings,
        IReadOnlyList<PluginInventoryItem> items,
        out VitalRechargeChoice choice)
    {
        switch (method)
        {
            case VitalRechargeMethod.Kit:
                return TryKit(
                    vital,
                    automation,
                    settings,
                    combatSettings,
                    items,
                    out choice);
            case VitalRechargeMethod.Food:
                return TryFood(vital, combatSettings, items, out choice);
        }

        (string stem, VitalKind? sourceVital) = SpellStem(method, vital);
        if (stem.Length == 0
            || sourceVital is { } source
                && vital != VitalKind.Health
                && VitalPlan.IsBelowNormal(automation.Character, settings, source))
        {
            choice = default;
            return false;
        }
        if (!TrySpell(
            vital,
            stem,
            automation,
            combatSettings,
            items,
            out choice))
        {
            return false;
        }
        if (method is VitalRechargeMethod.StaminaToHealth
                or VitalRechargeMethod.ManaToHealth
            && !ConversionWorthwhile(
                method,
                choice.SpellId,
                automation,
                settings))
        {
            choice = default;
            return false;
        }
        return true;
    }

    private static (string Stem, VitalKind? SourceVital) SpellStem(
        VitalRechargeMethod method,
        VitalKind vital) => method switch
    {
        VitalRechargeMethod.RegularSpell => vital switch
        {
            VitalKind.Health => (VitalPlan.HealSelfStem, null),
            VitalKind.Stamina => (VitalPlan.RevitalizeStem, null),
            VitalKind.Mana => (VitalPlan.StaminaToManaStem, VitalKind.Stamina),
            _ => (string.Empty, null),
        },
        VitalRechargeMethod.StaminaToHealth =>
            ("Stamina to Health", VitalKind.Stamina),
        VitalRechargeMethod.ManaToHealth =>
            ("Mana to Health", VitalKind.Mana),
        VitalRechargeMethod.HealthToStamina =>
            ("Health to Stamina", VitalKind.Health),
        VitalRechargeMethod.HealthToMana =>
            ("Health to Mana", VitalKind.Health),
        _ => (string.Empty, null),
    };

    private static bool TrySpell(
        VitalKind vital,
        string stem,
        IAutomationSurface automation,
        CombatSettings settings,
        IReadOnlyList<PluginInventoryItem> items,
        out VitalRechargeChoice choice)
    {
        bool found = false;
        PluginSpellInfo bestSpell = default;
        uint bestItem = 0u;

        foreach (PluginSpellInfo spell in automation.Spells.KnownSelfBuffs)
        {
            if (!Matches(spell.Name, stem))
                continue;
            if (SpellComponentPolicy.UsesBlacklistedComponent(
                    automation.Spells,
                    spell,
                    settings.BlacklistedSpellComponents))
                continue;
            if (!CanCast(automation.Character, spell))
                continue;
            if (!found || Better(spell, 0u, bestSpell, bestItem))
            {
                found = true;
                bestSpell = spell;
                bestItem = 0u;
            }
        }

        foreach (PluginInventoryItem item in items)
        {
            if (!settings.CombatItemObjectIds.Contains(item.ObjectId)
                && !settings.CombatItemNames.Contains(item.Name))
            {
                continue;
            }
            if ((item.ItemType & CasterItemType) == 0u)
                continue;

            foreach (uint spellId in ItemSpellIds(item))
            {
                if (!automation.Spells.TryGet(spellId, out PluginSpellInfo spell)
                    || !Matches(spell.Name, stem))
                {
                    continue;
                }
                if (SpellComponentPolicy.UsesBlacklistedComponent(
                        automation.Spells,
                        spell,
                        settings.BlacklistedSpellComponents))
                {
                    continue;
                }
                if (!found || Better(spell, item.ObjectId, bestSpell, bestItem))
                {
                    found = true;
                    bestSpell = spell;
                    bestItem = item.ObjectId;
                }
            }
        }

        if (!found)
        {
            choice = default;
            return false;
        }
        choice = new VitalRechargeChoice(
            vital,
            bestItem == 0u
                ? VitalRechargeSourceKind.LearnedSpell
                : VitalRechargeSourceKind.CasterItem,
            bestSpell.Name,
            bestSpell.SpellId,
            bestItem,
            PluginCombatMode.Magic);
        return true;
    }

    private static bool TryKit(
        VitalKind vital,
        IAutomationSurface automation,
        VitalSettings settings,
        CombatSettings combatSettings,
        IReadOnlyList<PluginInventoryItem> items,
        out VitalRechargeChoice choice)
    {
        PluginCombatMode mode = automation.Combat.Snapshot.Mode;
        if (mode == PluginCombatMode.Magic && !settings.UseKitsInMagicMode)
        {
            choice = default;
            return false;
        }
        if (vital != VitalKind.Stamina && automation.Character.CurrentStamina < 15u)
        {
            choice = default;
            return false;
        }
        if (!automation.Character.TryGetSkill(HealingSkill, out PluginSkillInfo healing)
            || healing.Training is not (PluginSkillTraining.Trained
                or PluginSkillTraining.Specialized))
        {
            choice = default;
            return false;
        }

        bool found = false;
        PluginInventoryItem best = default;
        foreach (PluginInventoryItem item in items)
        {
            if (!combatSettings.ConsumableNames.Contains(item.Name)
                || item.BoosterVital != (int)vital
                || item.UseRequiresSkill != (int)HealingSkill
                || item.UseRequiresSkillLevel > healing.Current
                || item.UseRequiresSkillSpecialized != 0
                    && healing.Training != PluginSkillTraining.Specialized
                || HealKitChance(
                    healing.Current,
                    item.BoostValue,
                    automation.Character,
                    vital,
                    mode) * 100d < settings.MinimumHealKitSuccessChance)
            {
                continue;
            }
            if (!found
                || item.HealKitModifier > best.HealKitModifier
                || item.HealKitModifier == best.HealKitModifier
                    && item.ObjectId < best.ObjectId)
            {
                best = item;
                found = true;
            }
        }
        if (!found)
        {
            choice = default;
            return false;
        }

        choice = new VitalRechargeChoice(
            vital,
            VitalRechargeSourceKind.Kit,
            best.Name,
            0u,
            best.ObjectId,
            settings.GoToPeaceModeToUseKits
                ? PluginCombatMode.Peace
                : null);
        return true;
    }

    private static bool TryFood(
        VitalKind vital,
        CombatSettings settings,
        IReadOnlyList<PluginInventoryItem> items,
        out VitalRechargeChoice choice)
    {
        foreach (PluginInventoryItem item in items)
        {
            if (settings.ConsumableNames.Contains(item.Name)
                && item.BoosterVital == (int)vital
                && item.UseRequiresSkill != (int)HealingSkill)
            {
                choice = new VitalRechargeChoice(
                    vital,
                    VitalRechargeSourceKind.Food,
                    item.Name,
                    0u,
                    item.ObjectId,
                    null);
                return true;
            }
        }
        choice = default;
        return false;
    }

    internal static double HealKitChance(
        uint healingSkill,
        int skillBonus,
        ICharacterInfo character,
        VitalKind vital,
        PluginCombatMode mode)
    {
        (uint current, uint maximum) = vital switch
        {
            VitalKind.Health => (character.CurrentHealth, character.MaxHealth),
            VitalKind.Stamina => (character.CurrentStamina, character.MaxStamina),
            VitalKind.Mana => (character.CurrentMana, character.MaxMana),
            _ => (0u, 0u),
        };
        double multiplier = mode == PluginCombatMode.Peace ? 2d : 2.2d;
        double missing = Math.Max(0d, (double)maximum - current);
        double difficulty = Math.Ceiling(multiplier * missing);
        return 1d - 1d / (1d + Math.Exp(
            0.03d * (healingSkill + skillBonus - difficulty)));
    }

    private static bool ConversionWorthwhile(
        VitalRechargeMethod method,
        uint conversionSpellId,
        IAutomationSurface automation,
        VitalSettings settings)
    {
        if (!automation.Spells.TryGet(
                conversionSpellId,
                out PluginSpellInfo conversion))
        {
            return false;
        }

        int ordinaryHeal = 0;
        foreach (PluginSpellInfo spell in automation.Spells.KnownSelfBuffs)
        {
            if (!Matches(spell.Name, VitalPlan.HealSelfStem)
                || !CanCast(automation.Character, spell))
            {
                continue;
            }
            ordinaryHeal = Math.Max(ordinaryHeal, EstimatedOrdinaryHeal(spell.Name));
        }

        int missing = checked((int)Math.Max(
            0L,
            (long)automation.Character.MaxHealth
                - automation.Character.CurrentHealth));
        if (ordinaryHeal > missing)
            return false;

        VitalKind source = method == VitalRechargeMethod.StaminaToHealth
            ? VitalKind.Stamina
            : VitalKind.Mana;
        int sourceCurrent = source == VitalKind.Stamina
            ? checked((int)automation.Character.CurrentStamina)
            : checked((int)automation.Character.CurrentMana) - 30;
        sourceCurrent = Math.Max(0, sourceCurrent);
        int converted = Math.Min(
            missing,
            EstimatedTransfer(conversion.Name, sourceCurrent));
        double multiplier = source == VitalKind.Stamina
            ? settings.StaminaToHealthMultiplier
            : settings.ManaToHealthMultiplier;
        return ordinaryHeal * multiplier < missing
            && ordinaryHeal * multiplier < converted;
    }

    internal static int EstimatedOrdinaryHeal(string spellName) => spellName switch
    {
        "Heal Self I" => 17,
        "Heal Self II" => 25,
        "Heal Self III" => 32,
        "Heal Self IV" => 45,
        "Heal Self V" => 67,
        "Heal Self VI" => 87,
        "Adja's Intervention" => 115,
        "Incantation of Heal Self" => 135,
        _ => 10,
    };

    internal static int EstimatedTransfer(string spellName, int sourceCurrent)
    {
        (double multiplier, int cap) = spellName switch
        {
            _ when spellName.EndsWith(" I", StringComparison.Ordinal) =>
                (0.9, 50),
            _ when spellName.EndsWith(" II", StringComparison.Ordinal) =>
                (1.0, 100),
            _ when spellName.EndsWith(" III", StringComparison.Ordinal) =>
                (1.1, 150),
            _ when spellName.EndsWith(" IV", StringComparison.Ordinal) =>
                (1.2, 200),
            _ when spellName.EndsWith(" V", StringComparison.Ordinal) =>
                (1.35, int.MaxValue),
            _ when spellName.EndsWith(" VI", StringComparison.Ordinal) =>
                (1.5, int.MaxValue),
            _ => (1.75, int.MaxValue),
        };
        return Math.Min(
            cap,
            checked((int)Math.Floor(sourceCurrent * multiplier)));
    }

    private static PluginFellowMember? Lowest(
        IReadOnlyList<PluginFellowMember> members,
        VitalKind vital,
        double threshold,
        float maximumDistance)
    {
        PluginFellowMember? best = null;
        double bestFraction = double.PositiveInfinity;
        foreach (PluginFellowMember member in members)
        {
            if (member.Distance > maximumDistance)
                continue;
            (uint current, uint maximum) = vital switch
            {
                VitalKind.Health => (member.CurrentHealth, member.MaxHealth),
                VitalKind.Stamina => (member.CurrentStamina, member.MaxStamina),
                VitalKind.Mana => (member.CurrentMana, member.MaxMana),
                _ => (0u, 0u),
            };
            if (maximum == 0u)
                continue;
            double fraction = (double)current / maximum;
            if (fraction < threshold && fraction < bestFraction)
            {
                best = member;
                bestFraction = fraction;
            }
        }
        return best;
    }

    private static bool TryFindFamily(
        IReadOnlyList<PluginSpellInfo> known,
        uint family,
        ICharacterInfo character,
        ISpellCatalog catalog,
        string blacklistedComponents,
        out PluginSpellInfo pick)
    {
        pick = default;
        bool found = false;
        foreach (PluginSpellInfo spell in known)
        {
            if (spell.Family != family
                || SpellComponentPolicy.UsesBlacklistedComponent(
                    catalog,
                    spell,
                    blacklistedComponents)
                || !CanCast(character, spell))
                continue;
            if (!found
                || spell.Quality > pick.Quality
                || spell.Quality == pick.Quality && spell.Tier > pick.Tier)
            {
                pick = spell;
                found = true;
            }
        }
        return found;
    }

    private static bool CanCast(ICharacterInfo character, PluginSpellInfo spell) =>
        spell.School == 0u
        || !character.TryGetSkill(spell.School, out PluginSkillInfo skill)
        || skill.Current >= spell.Difficulty;

    private static bool Better(
        PluginSpellInfo candidate,
        uint candidateItem,
        PluginSpellInfo current,
        uint currentItem)
    {
        int quality = candidate.Quality.CompareTo(current.Quality);
        if (quality != 0)
            return quality > 0;
        // dz.cs/m.cs: a direct learned spell wins the final source tie.
        if ((candidateItem == 0u) != (currentItem == 0u))
            return candidateItem == 0u;
        return candidateItem < currentItem;
    }

    private static bool Matches(string name, string stem)
    {
        if (name.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return stem switch
        {
            VitalPlan.HealSelfStem => name.Equals(
                "Adja's Intervention",
                StringComparison.OrdinalIgnoreCase),
            VitalPlan.RevitalizeStem => name.Equals(
                "Robustification",
                StringComparison.OrdinalIgnoreCase),
            VitalPlan.StaminaToManaStem => name.Equals(
                "Meditative Trance",
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static IEnumerable<uint> ItemSpellIds(PluginInventoryItem item)
    {
        if (item.SpellId != 0u)
            yield return item.SpellId;
        foreach (uint spellId in item.AppraisedSpellIds)
        {
            if (spellId != 0u && spellId != item.SpellId)
                yield return spellId;
        }
    }
}

/// <summary>One server-receipt-driven self-recharge state machine.</summary>
internal sealed class VitalRechargeController
{
    private readonly IPluginHost _host;
    private readonly VitalSettings _settings;
    private readonly CombatSettings _combatSettings;
    private Pending? _pending;
    private double _retryDelay;
    private double _pendingSeconds;
    private double _healthBoostRemaining;
    private double _staminaBoostRemaining;
    private double _manaBoostRemaining;

    public VitalRechargeController(
        IPluginHost host,
        VitalSettings settings,
        CombatSettings combatSettings)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _combatSettings = combatSettings
            ?? throw new ArgumentNullException(nameof(combatSettings));
    }

    public string Status { get; private set; } = "Vitals idle";

    public bool Tick(double elapsedSeconds, bool enabled, bool noTarget)
    {
        IAutomationSurface automation = _host.Automation;
        double elapsed = Math.Max(0d, elapsedSeconds);
        _retryDelay = Math.Max(0d, _retryDelay - elapsed);
        _healthBoostRemaining = Math.Max(0d, _healthBoostRemaining - elapsed);
        _staminaBoostRemaining = Math.Max(0d, _staminaBoostRemaining - elapsed);
        _manaBoostRemaining = Math.Max(0d, _manaBoostRemaining - elapsed);
        if (!enabled || !_settings.Enabled || !automation.IsAvailable)
        {
            _pending = null;
            ClearBoosts();
            Status = "Vitals idle";
            return false;
        }

        if (_pending is { } pending)
        {
            _pendingSeconds += Math.Max(0d, elapsedSeconds);
            if (TryComplete(automation, pending))
            {
                if (_settings.ClearLevelBoostFlagOnCast
                    && pending.Choice.SourceKind
                        == VitalRechargeSourceKind.LearnedSpell
                    && IsLevelBoostSpell(pending.Choice))
                {
                    ClearBoost(pending.Choice.Vital);
                }
                _pending = null;
                _pendingSeconds = 0d;
                _retryDelay = 0.25d;
            }
            else if (_pendingSeconds >= 15d)
            {
                Status = $"Timed out: {pending.Choice.Name}";
                _pending = null;
                _pendingSeconds = 0d;
                _retryDelay = 1d;
            }
            else
            {
                Status = $"Recharging {pending.Choice.Vital}: {pending.Choice.Name}";
                return true;
            }
        }

        VitalKind? need = VitalPlan.DecideNeed(
            automation.Character,
            _settings,
            noTarget,
            _healthBoostRemaining > 0d ? _settings.RechargeBoostAmount : 0,
            _staminaBoostRemaining > 0d ? _settings.RechargeBoostAmount : 0,
            _manaBoostRemaining > 0d ? _settings.RechargeBoostAmount : 0);
        VitalRechargeChoice helper = default;
        if (need is null
            && !VitalRechargePlanner.TryPlanHelper(
                automation,
                _settings,
                _combatSettings,
                out helper))
        {
            Status = "Vitals ready";
            return false;
        }
        if (_retryDelay > 0d || automation.Magic.IsCasting || automation.Items.IsBusy)
            return true;

        VitalRechargeChoice choice;
        if (need is null)
        {
            choice = helper;
        }
        else if (!VitalRechargePlanner.TryPlan(
                     need.Value,
                     automation,
                     _settings,
                     _combatSettings,
                     out choice))
        {
            Status = $"No {need.Value} recharge available";
            _retryDelay = 1d;
            return false;
        }

        if (choice.RequiredMode is { } required
            && automation.Combat.Snapshot.Mode != required)
        {
            if (required == PluginCombatMode.Magic)
                ArmBoost(choice.Vital);
            PluginCombatCommandResult mode = automation.Combat.EnterMode(required);
            Status = mode.Accepted
                ? $"Switching to {required} for {choice.Name}"
                : $"Waiting for {required}: {choice.Name}";
            return true;
        }

        long revision = choice.SourceKind == VitalRechargeSourceKind.LearnedSpell
            ? automation.Magic.LastCompletion.Revision
            : automation.Items.LastCompletion.Revision;
        bool started = Start(automation, choice);
        if (!started)
        {
            Status = $"Waiting to use {choice.Name}";
            _retryDelay = 0.25d;
            return true;
        }
        _pending = new Pending(choice, revision);
        _pendingSeconds = 0d;
        Status = $"Recharging {choice.Vital}: {choice.Name}";
        return true;
    }

    public void Reset()
    {
        _pending = null;
        _pendingSeconds = 0d;
        _retryDelay = 0d;
        ClearBoosts();
        Status = "Vitals idle";
    }

    private void ArmBoost(VitalKind vital)
    {
        double duration = Math.Max(0d, _settings.RechargeBoostTimeSeconds);
        switch (vital)
        {
            case VitalKind.Health:
                _healthBoostRemaining = duration;
                break;
            case VitalKind.Stamina:
                _staminaBoostRemaining = duration;
                break;
            case VitalKind.Mana:
                _manaBoostRemaining = duration;
                break;
        }
    }

    private void ClearBoost(VitalKind vital)
    {
        switch (vital)
        {
            case VitalKind.Health:
                _healthBoostRemaining = 0d;
                break;
            case VitalKind.Stamina:
                _staminaBoostRemaining = 0d;
                break;
            case VitalKind.Mana:
                _manaBoostRemaining = 0d;
                break;
        }
    }

    private void ClearBoosts()
    {
        _healthBoostRemaining = 0d;
        _staminaBoostRemaining = 0d;
        _manaBoostRemaining = 0d;
    }

    private static bool IsLevelBoostSpell(in VitalRechargeChoice choice) =>
        choice.Vital switch
        {
            VitalKind.Health => choice.Name.Equals(
                "Adja's Intervention",
                StringComparison.OrdinalIgnoreCase),
            VitalKind.Stamina => choice.Name.Equals(
                "Robustification",
                StringComparison.OrdinalIgnoreCase),
            VitalKind.Mana => choice.Name.Equals(
                "Meditative Trance",
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static bool Start(
        IAutomationSurface automation,
        VitalRechargeChoice choice)
    {
        if (choice.SourceKind == VitalRechargeSourceKind.LearnedSpell)
        {
            PluginCastGate gate = choice.TargetObjectId == 0u
                ? automation.Magic.EvaluateGate(choice.SpellId)
                : automation.Magic.EvaluateGate(
                    choice.SpellId,
                    choice.TargetObjectId);
            return gate == PluginCastGate.Ready
                && (choice.TargetObjectId == 0u
                    ? automation.Magic.Cast(choice.SpellId)
                    : automation.Magic.Cast(
                        choice.SpellId,
                        choice.TargetObjectId));
        }

        PluginItemCommandResult result = choice.SourceKind switch
        {
            VitalRechargeSourceKind.Food =>
                automation.Items.Use(choice.ItemObjectId),
            _ => automation.Items.Apply(
                choice.ItemObjectId,
                choice.TargetObjectId == 0u
                    ? automation.Character.ObjectId
                    : choice.TargetObjectId),
        };
        return result.Accepted;
    }

    private static bool TryComplete(IAutomationSurface automation, Pending pending)
    {
        if (pending.Choice.SourceKind == VitalRechargeSourceKind.LearnedSpell)
            return automation.Magic.LastCompletion.Revision > pending.Revision;
        return automation.Items.LastCompletion.Revision > pending.Revision;
    }

    private sealed record Pending(VitalRechargeChoice Choice, long Revision);
}

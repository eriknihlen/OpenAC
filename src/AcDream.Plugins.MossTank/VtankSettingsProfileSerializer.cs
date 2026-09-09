using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal static class VtankSettingsProfileSerializer
{
    private const string SettingsTable = "Settings";

    internal sealed class AllSettings
    {
        public required CombatSettings Combat { get; init; }
        public required BuffSettings Buffs { get; init; }
        public required VitalSettings Vitals { get; init; }
        public required InventorySettings Inventory { get; init; }
        public required NavigationSettings Navigation { get; init; }
    }

    public static VtankDatabase Load(
        string text,
        AllSettings target,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        VtankDatabase database = VtankDatabase.Parse(text);
        VtankTable? settings = database.Find(SettingsTable);
        if (settings is null)
            throw new FormatException("missing required 'Settings' table.");
        int nameColumn = settings.ColumnIndex("Setting");
        int valueColumn = settings.ColumnIndex("Value");
        if (nameColumn < 0 || valueColumn < 0)
            throw new FormatException("'Settings' table is missing Setting/Value columns.");

        foreach (VtankRow row in settings.Rows)
        {
            string name = row.Cells[nameColumn].AsString();
            Apply(name, row.Cells[valueColumn], target);
        }

        if (VtankMonsterRuleTable.TryRead(database, warn) is { Count: > 0 } rules)
        {
            target.Combat.Rules.Clear();
            foreach (MonsterRule rule in rules)
                target.Combat.Rules.Add(rule);
        }
        return database;
    }

    public static string Save(VtankDatabase document, AllSettings source)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        VtankTable? settings = document.Find(SettingsTable);
        if (settings is null)
            throw new FormatException("missing required 'Settings' table.");
        int nameColumn = settings.ColumnIndex("Setting");
        int valueColumn = settings.ColumnIndex("Value");
        foreach (VtankRow row in settings.Rows)
        {
            string name = row.Cells[nameColumn].AsString();
            VtankCell? captured = Capture(name, source);
            if (captured is null)
                continue;
            VtankCell current = row.Cells[valueColumn];
            if (ValuesEqual(current, captured))
                continue;
            row.Cells[valueColumn] = captured;
        }
        VtankMonsterRuleTable.Write(document, [.. source.Combat.Rules]);
        return document.Render();
    }

    public static VtankDatabase CreateNew(AllSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        VtankDatabase database = VtankDefaultSettingsDatabase.Parse();
        _ = Save(database, source);
        return database;
    }

    private static VtankCell Num(string name, double value) =>
        VtankOptionCatalog.DeclaredType(name) switch
        {
            VtankSettingValueType.Bool => VtankCell.Bool(value != 0d),
            VtankSettingValueType.Int or VtankSettingValueType.Enum =>
                VtankCell.Int((int)Math.Round(value, MidpointRounding.AwayFromZero)),
            VtankSettingValueType.Single => VtankCell.Float((float)value),
            VtankSettingValueType.String => VtankCell.String(value.ToString(CultureInfo.InvariantCulture)),
            _ => VtankCell.Double(value),
        };

    private static bool ValuesEqual(VtankCell a, VtankCell b)
    {
        if (a.Tag != b.Tag)
            return false;
        return a.Tag switch
        {
            "b" => a.AsBool() == b.AsBool(),
            "s" => a.AsString() == b.AsString(),
            "i" => a.AsInt() == b.AsInt(),
            "u" => a.AsUInt() == b.AsUInt(),
            "d" or "f" => a.AsDouble() == b.AsDouble(),
            _ => ReferenceEquals(a, b),
        };
    }


    /// <summary>Internal (not private) so the profile store can seed live settings directly from a parsed database (fresh/default profiles, migration).</summary>
    internal static void Apply(string rawName, VtankCell cell, AllSettings s)
    {
        if (!VtankOptionCatalog.IsKnown(rawName))
            return;
        string name = VtankOptionCatalog.Canonical(rawName).ToLowerInvariant();
        CombatSettings c = s.Combat;
        BuffSettings b = s.Buffs;
        VitalSettings v = s.Vitals;
        InventorySettings i = s.Inventory;
        NavigationSettings n = s.Navigation;
        switch (name)
        {
            case "enablelooting": i.Loot.Enabled = cell.AsBool(); break;
            case "enablenav": n.Enabled = cell.AsBool(); break;
            case "enablebuffing": b.Enabled = cell.AsBool(); break;
            case "enablecombat": c.Enabled = cell.AsBool(); break;
            case "spelldiffexcessthreshold-hunt": c.HuntSkillExcessOverDifficulty = cell.AsInt(); break;
            case "spelldiffexcessthreshold-buff": b.SkillExcessOverDifficulty = cell.AsInt(); break;
            case "arrowheadfletchdiffexcessthreshold": i.ArrowheadFletchDifficultyExcess = cell.AsInt(); break;
            case "recharge-norm-hitp": v.NormalHealth = cell.AsDouble() / 100d; break;
            case "recharge-norm-stam": v.NormalStamina = cell.AsDouble() / 100d; break;
            case "recharge-norm-mana": v.NormalMana = cell.AsDouble() / 100d; break;
            case "recharge-notarg-hitp": v.NoTargetHealth = cell.AsDouble() / 100d; break;
            case "recharge-notarg-stam": v.NoTargetStamina = cell.AsDouble() / 100d; break;
            case "recharge-notarg-mana": v.NoTargetMana = cell.AsDouble() / 100d; break;
            case "recharge-helper-hitp": v.HelperHealth = cell.AsDouble() / 100d; break;
            case "recharge-helper-stam": v.HelperStamina = cell.AsDouble() / 100d; break;
            case "recharge-helper-mana": v.HelperMana = cell.AsDouble() / 100d; break;
            case "dohelp": v.HelpOthers = cell.AsBool(); break;
            case "attackdistance": c.MaximumRange = cell.AsDouble() * 240d; break;
            case "attackminimumdistance": c.MinimumRange = cell.AsDouble() * 240d; break;
            case "approachdistance": c.ApproachDistance = cell.AsDouble() * 240d; break;
            case "ringdistance": c.RingDistance = cell.AsDouble() * 240d; break;
            case "corpseapproachrange-max": i.Loot.CorpseApproachRange = cell.AsDouble() * 240d; break;
            case "corpseapproachrange-min": i.Loot.CorpseMinimumApproachRange = cell.AsDouble() * 240d; break;
            case "navclosestoprange": n.MinimumDistanceMeters = cell.AsDouble() * 240d; break;
            case "navfarstoprange": n.MaximumDistanceMeters = cell.AsDouble() * 240d; break;
            case "useportaldistance": n.PortalUseDistanceMeters = cell.AsDouble() * 240d; break;
            case "helperdistancehitp": v.HelperHealthDistance = cell.AsDouble() * 240d; break;
            case "helperdistancestam": v.HelperStaminaDistance = cell.AsDouble() * 240d; break;
            case "helperdistancemana": v.HelperManaDistance = cell.AsDouble() * 240d; break;
            case "minimumringtargets": c.MinimumRingTargets = cell.AsInt(); break;
            case "defaultmeleeattackheight": c.AttackHeight = (PluginAttackHeight)cell.AsInt(); break;
            case "castdispelself": v.CastDispelSelf = cell.AsBool(); break;
            case "usedispelitems": v.UseDispelItems = cell.AsBool(); break;
            case "autocram": i.AutoCram = cell.AsBool(); break;
            case "autostack": i.AutoStack = cell.AsBool(); break;
            case "readunknownscrolls": i.Loot.ReadUnknownScrolls = cell.AsBool(); break;
            case "usedispeldrum": v.UseDispelDrum = cell.AsBool(); break;
            case "switchwandstodebuff": c.SwitchWandsToDebuff = cell.AsBool(); break;
            case "autocraftitems": i.AutoCraftItems = cell.AsBool(); break;
            case "usehealersheart": v.UseHealersHeart = cell.AsBool(); break;
            case "jumpoutwandcasting": c.JumpOutWandCasting = cell.AsBool(); break;
            case "lootallcorpses": i.Loot.LootAllCorpses = cell.AsBool(); break;
            case "lootfellowcorpses": i.Loot.LootFellowCorpses = cell.AsBool(); break;
            case "dojiggle": c.DoJiggle = cell.AsBool(); break;
            case "randomhelperbuffs": b.RandomHelperBuffs = cell.AsBool(); break;
            case "randomhelperintervalseconds": b.RandomHelperIntervalSeconds = cell.AsDouble(); break;
            case "idlepeacemode": c.IdlePeaceMode = cell.AsBool(); break;
            case "targetlock": c.TargetLock = cell.AsBool(); break;
            case "stopmacroondeath": c.StopMacroOnDeath = cell.AsBool(); break;
            case "usearcs": c.UseArcs = (UseArcsMode)cell.AsInt(); break;
            case "arcrange": c.ArcRange = cell.AsDouble() * 240d; break;
            case "targetselectmethod": c.SelectionMethod = (TargetSelectionMethod)(cell.AsInt() - 1); break;
            case "targetselectanglerange": c.TargetSelectAngleRange = cell.AsDouble() * 240d; break;
            case "idlebufftopoff": b.IdleBuffTopoff = cell.AsBool(); break;
            case "idlebufftopofftimeseconds": b.IdleBuffTopoffSeconds = cell.AsDouble(); break;
            case "rebufftimeremainingseconds": b.RebuffWhenUnderSeconds = cell.AsDouble(); break;
            case "refillwornmana": i.RefillWornMana = cell.AsBool(); break;
            case "refillwornmana-item-manapercent": i.RefillWornManaPercent = cell.AsInt(); break;
            case "buffprofile-prots": b.ProtectionElements = cell.AsString(); break;
            case "buffprofile-banes": b.BaneElements = cell.AsString(); break;
            case "buffprofile_prots": b.ProtectionProfileMode = cell.AsInt(); break;
            case "buffprofile_banes": b.BaneProfileMode = cell.AsInt(); break;
            case "debuffeachfirst": c.DebuffEachFirst = (DebuffEachFirst)cell.AsInt(); break;
            case "autoattackpower": c.AutoAttackPower = cell.AsBool(); break;
            case "lootpriorityboost": i.Loot.PriorityBoost = cell.AsBool(); break;
            case "corpsecachetimeoutminutes": i.Loot.CorpseCacheTimeoutMinutes = cell.AsDouble(); break;
            case "corpseitemappearancetimeoutseconds": i.Loot.CorpseItemAppearanceTimeoutSeconds = cell.AsDouble(); break;
            case "corpseitemidtimeoutseconds": i.Loot.CorpseItemIdentifyTimeoutSeconds = cell.AsDouble(); break;
            case "debuffselectionmethod": c.DebuffSelectionMethod = (DebuffSelectionMethod)cell.AsInt(); break;
            case "manastonelootcount": i.Loot.ManaStoneLootCount = cell.AsInt(); break;
            case "manatankminimummana": i.Loot.ManaTankMinimumMana = cell.AsInt(); break;
            case "splitpeas": i.SplitPeas = cell.AsBool(); break;
            case "spellcompmin-critical": i.CriticalComponentMinimum = cell.AsInt(); break;
            case "spellcompmin-normal": i.NormalComponentMinimum = cell.AsInt(); break;
            case "spellcompmin-idle": i.IdleComponentMinimum = cell.AsInt(); break;
            case "rechargeboosttimeseconds": v.RechargeBoostTimeSeconds = cell.AsDouble(); break;
            case "rechargeboostamount": v.RechargeBoostAmount = cell.AsInt(); break;
            case "usespecialammo": c.UseSpecialAmmo = cell.AsInt(); break;
            case "opendoors": n.OpenDoors = cell.AsBool(); break;
            case "dooridrange": n.DoorIdentifyRangeMeters = cell.AsDouble() * 240d; break;
            case "dooropenrange": n.DoorOpenRangeMeters = cell.AsDouble() * 240d; break;
            case "doorlockpickdiffexcessthreshold": n.DoorLockpickExcessThreshold = cell.AsInt(); break;
            case "manachargeswhenoff": i.ManaChargesWhenOff = cell.AsBool(); break;
            case "autofellowmanagement": c.AutoFellowManagement = cell.AsBool(); break;
            case "minimumhealkitsuccesschance": v.MinimumHealKitSuccessChance = cell.AsInt(); break;
            case "usekitsinmagicmode": v.UseKitsInMagicMode = cell.AsBool(); break;
            case "staminatohealthmultiplier": v.StaminaToHealthMultiplier = cell.AsDouble(); break;
            case "manatohealthmultiplier": v.ManaToHealthMultiplier = cell.AsDouble(); break;
            case "navpriorityboost": n.Priority = cell.AsBool(); break;
            case "deleteghostmonsters": c.DeleteGhostMonsters = cell.AsBool(); break;
            case "ghostmonsterspellattemptcount": c.GhostMonsterSpellAttemptCount = cell.AsInt(); break;
            case "whoyougonnacall": c.WhoYouGonnaCall = cell.AsBool(); break;
            case "blacklistmonsterattemptcount": c.BlacklistMonsterAttemptCount = cell.AsInt(); break;
            case "blacklistmonstertimeoutseconds": c.BlacklistMonsterTimeoutSeconds = cell.AsDouble(); break;
            case "combinesalvage": i.Loot.CombineSalvage = cell.AsBool(); break;
            case "lootonlyrarecorpses": i.Loot.LootOnlyRareCorpses = cell.AsBool(); break;
            case "deleteghostmonstersbyhptracker": c.DeleteGhostMonstersByHealthTracker = cell.AsBool(); break;
            case "ghostdeletehptrackerseconds": c.GhostDeleteHealthTrackerSeconds = cell.AsDouble(); break;
            case "gotopeacemodetousekits": v.GoToPeaceModeToUseKits = cell.AsBool(); break;
            case "userecklessness": c.UseRecklessness = cell.AsBool(); break;
            case "debuffprecastseconds": c.DebuffPrecastSeconds = cell.AsDouble(); break;
            case "clearlevelboostflagoncast": v.ClearLevelBoostFlagOnCast = cell.AsBool(); break;
            case "idlecraftcount_healthkits": i.IdleHealthKitCount = cell.AsInt(); break;
            case "idlecraftcount_stamkits": i.IdleStaminaKitCount = cell.AsInt(); break;
            case "idlecraftcount_manakits": i.IdleManaKitCount = cell.AsInt(); break;
            case "idlecraftcount_healthfood": i.IdleHealthFoodCount = cell.AsInt(); break;
            case "idlecraftcount_stamfood": i.IdleStaminaFoodCount = cell.AsInt(); break;
            case "idlecraftcount_manafood": i.IdleManaFoodCount = cell.AsInt(); break;
            case "buffcastrecast_seconds": b.BuffCastRecastSeconds = cell.AsDouble(); break;
            case "buffcastrecastreset_seconds": b.BuffCastRecastResetSeconds = cell.AsDouble(); break;
            case "enablemeta": break; // live MetaEngine.Enabled, not a stored settings field.
            case "blacklistedspellcomps":
                b.BlacklistedSpellComponents = cell.AsString();
                c.BlacklistedSpellComponents = cell.AsString();
                break;
            case "droptopeacemoderetrycount": v.DropToPeaceModeRetryCount = cell.AsInt(); break;
            case "followaroundcorners": n.FollowAroundCorners = cell.AsBool(); break;
            case "blacklistcorpseopenattemptcount": i.Loot.BlacklistCorpseOpenAttemptCount = cell.AsInt(); break;
            case "blacklistcorpseopentimeoutseconds": i.Loot.BlacklistCorpseOpenTimeoutSeconds = cell.AsDouble(); break;
            case "summonpets": c.SummonPets = cell.AsBool(); break;
            case "petrangemode": c.PetRangeMode = (PetRangeMode)cell.AsInt(); break;
            case "petcustomrange": c.PetCustomRange = cell.AsDouble() * 240d; break;
            case "petrefillcount-idle": c.PetRefillCountIdle = cell.AsInt(); break;
            case "petrefillcount-normal": c.PetRefillCountNormal = cell.AsInt(); break;
            case "corpseopentimeoutseconds": i.Loot.CorpseOpenTimeoutSeconds = cell.AsDouble(); break;
            case "petmonsterdensity": c.PetMonsterDensity = cell.AsInt(); break;
            case "corpselootitemmaxattempts": i.Loot.CorpseLootItemMaxAttempts = cell.AsInt(); break;
            case "fastcastbuffs": b.FastCastBuffs = cell.AsBool(); break;
            case "usebreakableturnto": c.UseBreakableTurnTo = cell.AsBool(); break;
            case "useprojectileawareness": c.UseProjectileAwareness = cell.AsBool(); break;
            case "collisionprojectileradius": c.CollisionProjectileRadius = cell.AsDouble(); break;
            case "collisionstepdistance": c.CollisionStepDistance = cell.AsDouble(); break;
            case "showcollisiondebug": c.ShowCollisionDebug = cell.AsBool(); break;
            case "maximumcollisioncheckspertick": c.MaximumCollisionChecksPerTick = cell.AsInt(); break;
            case "spellrangefudge": c.SpellRangeFudge = cell.AsDouble(); break;
            case "buffwithuntrained-item": b.BuffWithUntrainedItemSkill = cell.AsInt(); break;
            case "buffwithuntrained-creature": b.BuffWithUntrainedCreatureSkill = cell.AsInt(); break;
            case "buffwithuntrained-life": b.BuffWithUntrainedLifeSkill = cell.AsInt(); break;
            case "allowdebufffallback": c.AllowDebuffFallback = cell.AsBool(); break;
            case "rechargehandlerset":
                if (cell.Tag == "TABLE" && cell.Table is { } table)
                    v.RechargeHandlerRows = ParseRechargeHandlerSet(table);
                break;
            default: break;
        }
    }


    internal static VtankCell? Capture(string rawName, AllSettings s)
    {
        if (!VtankOptionCatalog.IsKnown(rawName))
            return null;
        string name = VtankOptionCatalog.Canonical(rawName).ToLowerInvariant();
        CombatSettings c = s.Combat;
        BuffSettings b = s.Buffs;
        VitalSettings v = s.Vitals;
        InventorySettings i = s.Inventory;
        NavigationSettings n = s.Navigation;
        return name switch
        {
            "enablelooting" => VtankCell.Bool(i.Loot.Enabled),
            "enablenav" => VtankCell.Bool(n.Enabled),
            "enablebuffing" => VtankCell.Bool(b.Enabled),
            "enablecombat" => VtankCell.Bool(c.Enabled),
            "spelldiffexcessthreshold-hunt" => Num(name, c.HuntSkillExcessOverDifficulty),
            "spelldiffexcessthreshold-buff" => Num(name, b.SkillExcessOverDifficulty),
            "arrowheadfletchdiffexcessthreshold" => Num(name, i.ArrowheadFletchDifficultyExcess),
            "recharge-norm-hitp" => Num(name, v.NormalHealth * 100d),
            "recharge-norm-stam" => Num(name, v.NormalStamina * 100d),
            "recharge-norm-mana" => Num(name, v.NormalMana * 100d),
            "recharge-notarg-hitp" => Num(name, v.NoTargetHealth * 100d),
            "recharge-notarg-stam" => Num(name, v.NoTargetStamina * 100d),
            "recharge-notarg-mana" => Num(name, v.NoTargetMana * 100d),
            "recharge-helper-hitp" => Num(name, v.HelperHealth * 100d),
            "recharge-helper-stam" => Num(name, v.HelperStamina * 100d),
            "recharge-helper-mana" => Num(name, v.HelperMana * 100d),
            "dohelp" => VtankCell.Bool(v.HelpOthers),
            "attackdistance" => Num(name, c.MaximumRange / 240d),
            "attackminimumdistance" => Num(name, c.MinimumRange / 240d),
            "approachdistance" => Num(name, c.ApproachDistance / 240d),
            "ringdistance" => Num(name, c.RingDistance / 240d),
            "corpseapproachrange-max" => Num(name, i.Loot.CorpseApproachRange / 240d),
            "corpseapproachrange-min" => Num(name, i.Loot.CorpseMinimumApproachRange / 240d),
            "navclosestoprange" => Num(name, n.MinimumDistanceMeters / 240d),
            "navfarstoprange" => Num(name, n.MaximumDistanceMeters / 240d),
            "useportaldistance" => Num(name, n.PortalUseDistanceMeters / 240d),
            "helperdistancehitp" => Num(name, v.HelperHealthDistance / 240d),
            "helperdistancestam" => Num(name, v.HelperStaminaDistance / 240d),
            "helperdistancemana" => Num(name, v.HelperManaDistance / 240d),
            "minimumringtargets" => Num(name, c.MinimumRingTargets),
            "defaultmeleeattackheight" => Num(name, (int)c.AttackHeight),
            "castdispelself" => VtankCell.Bool(v.CastDispelSelf),
            "usedispelitems" => VtankCell.Bool(v.UseDispelItems),
            "autocram" => VtankCell.Bool(i.AutoCram),
            "autostack" => VtankCell.Bool(i.AutoStack),
            "readunknownscrolls" => VtankCell.Bool(i.Loot.ReadUnknownScrolls),
            "usedispeldrum" => VtankCell.Bool(v.UseDispelDrum),
            "switchwandstodebuff" => VtankCell.Bool(c.SwitchWandsToDebuff),
            "autocraftitems" => VtankCell.Bool(i.AutoCraftItems),
            "usehealersheart" => VtankCell.Bool(v.UseHealersHeart),
            "jumpoutwandcasting" => VtankCell.Bool(c.JumpOutWandCasting),
            "lootallcorpses" => VtankCell.Bool(i.Loot.LootAllCorpses),
            "lootfellowcorpses" => VtankCell.Bool(i.Loot.LootFellowCorpses),
            "dojiggle" => VtankCell.Bool(c.DoJiggle),
            "randomhelperbuffs" => VtankCell.Bool(b.RandomHelperBuffs),
            "randomhelperintervalseconds" => Num(name, b.RandomHelperIntervalSeconds),
            "idlepeacemode" => VtankCell.Bool(c.IdlePeaceMode),
            "targetlock" => VtankCell.Bool(c.TargetLock),
            "stopmacroondeath" => VtankCell.Bool(c.StopMacroOnDeath),
            "usearcs" => Num(name, (int)c.UseArcs),
            "arcrange" => Num(name, c.ArcRange / 240d),
            "targetselectmethod" => Num(name, (int)c.SelectionMethod + 1),
            "targetselectanglerange" => Num(name, c.TargetSelectAngleRange / 240d),
            "idlebufftopoff" => VtankCell.Bool(b.IdleBuffTopoff),
            "idlebufftopofftimeseconds" => Num(name, b.IdleBuffTopoffSeconds),
            "rebufftimeremainingseconds" => Num(name, b.RebuffWhenUnderSeconds),
            "refillwornmana" => VtankCell.Bool(i.RefillWornMana),
            "refillwornmana-item-manapercent" => Num(name, i.RefillWornManaPercent),
            "buffprofile-prots" => VtankCell.String(b.ProtectionElements),
            "buffprofile-banes" => VtankCell.String(b.BaneElements),
            "buffprofile_prots" => Num(name, b.ProtectionProfileMode),
            "buffprofile_banes" => Num(name, b.BaneProfileMode),
            "debuffeachfirst" => Num(name, (int)c.DebuffEachFirst),
            "autoattackpower" => VtankCell.Bool(c.AutoAttackPower),
            "lootpriorityboost" => VtankCell.Bool(i.Loot.PriorityBoost),
            "corpsecachetimeoutminutes" => Num(name, i.Loot.CorpseCacheTimeoutMinutes),
            "corpseitemappearancetimeoutseconds" => Num(name, i.Loot.CorpseItemAppearanceTimeoutSeconds),
            "corpseitemidtimeoutseconds" => Num(name, i.Loot.CorpseItemIdentifyTimeoutSeconds),
            "debuffselectionmethod" => Num(name, (int)c.DebuffSelectionMethod),
            "manastonelootcount" => Num(name, i.Loot.ManaStoneLootCount),
            "manatankminimummana" => Num(name, i.Loot.ManaTankMinimumMana),
            "splitpeas" => VtankCell.Bool(i.SplitPeas),
            "spellcompmin-critical" => Num(name, i.CriticalComponentMinimum),
            "spellcompmin-normal" => Num(name, i.NormalComponentMinimum),
            "spellcompmin-idle" => Num(name, i.IdleComponentMinimum),
            "rechargeboosttimeseconds" => Num(name, v.RechargeBoostTimeSeconds),
            "rechargeboostamount" => Num(name, v.RechargeBoostAmount),
            "usespecialammo" => Num(name, c.UseSpecialAmmo),
            "opendoors" => VtankCell.Bool(n.OpenDoors),
            "dooridrange" => Num(name, n.DoorIdentifyRangeMeters / 240d),
            "dooropenrange" => Num(name, n.DoorOpenRangeMeters / 240d),
            "doorlockpickdiffexcessthreshold" => Num(name, n.DoorLockpickExcessThreshold),
            "manachargeswhenoff" => VtankCell.Bool(i.ManaChargesWhenOff),
            "autofellowmanagement" => VtankCell.Bool(c.AutoFellowManagement),
            "minimumhealkitsuccesschance" => Num(name, v.MinimumHealKitSuccessChance),
            "usekitsinmagicmode" => VtankCell.Bool(v.UseKitsInMagicMode),
            "staminatohealthmultiplier" => Num(name, v.StaminaToHealthMultiplier),
            "manatohealthmultiplier" => Num(name, v.ManaToHealthMultiplier),
            "navpriorityboost" => VtankCell.Bool(n.Priority),
            "deleteghostmonsters" => VtankCell.Bool(c.DeleteGhostMonsters),
            "ghostmonsterspellattemptcount" => Num(name, c.GhostMonsterSpellAttemptCount),
            "whoyougonnacall" => VtankCell.Bool(c.WhoYouGonnaCall),
            "blacklistmonsterattemptcount" => Num(name, c.BlacklistMonsterAttemptCount),
            "blacklistmonstertimeoutseconds" => Num(name, c.BlacklistMonsterTimeoutSeconds),
            "combinesalvage" => VtankCell.Bool(i.Loot.CombineSalvage),
            "lootonlyrarecorpses" => VtankCell.Bool(i.Loot.LootOnlyRareCorpses),
            "deleteghostmonstersbyhptracker" => VtankCell.Bool(c.DeleteGhostMonstersByHealthTracker),
            "ghostdeletehptrackerseconds" => Num(name, c.GhostDeleteHealthTrackerSeconds),
            "gotopeacemodetousekits" => VtankCell.Bool(v.GoToPeaceModeToUseKits),
            "userecklessness" => VtankCell.Bool(c.UseRecklessness),
            "debuffprecastseconds" => Num(name, c.DebuffPrecastSeconds),
            "clearlevelboostflagoncast" => VtankCell.Bool(v.ClearLevelBoostFlagOnCast),
            "idlecraftcount_healthkits" => Num(name, i.IdleHealthKitCount),
            "idlecraftcount_stamkits" => Num(name, i.IdleStaminaKitCount),
            "idlecraftcount_manakits" => Num(name, i.IdleManaKitCount),
            "idlecraftcount_healthfood" => Num(name, i.IdleHealthFoodCount),
            "idlecraftcount_stamfood" => Num(name, i.IdleStaminaFoodCount),
            "idlecraftcount_manafood" => Num(name, i.IdleManaFoodCount),
            "buffcastrecast_seconds" => Num(name, b.BuffCastRecastSeconds),
            "buffcastrecastreset_seconds" => Num(name, b.BuffCastRecastResetSeconds),
            "blacklistedspellcomps" => VtankCell.String(b.BlacklistedSpellComponents),
            "droptopeacemoderetrycount" => Num(name, v.DropToPeaceModeRetryCount),
            "followaroundcorners" => VtankCell.Bool(n.FollowAroundCorners),
            "blacklistcorpseopenattemptcount" => Num(name, i.Loot.BlacklistCorpseOpenAttemptCount),
            "blacklistcorpseopentimeoutseconds" => Num(name, i.Loot.BlacklistCorpseOpenTimeoutSeconds),
            "summonpets" => VtankCell.Bool(c.SummonPets),
            "petrangemode" => Num(name, (int)c.PetRangeMode),
            "petcustomrange" => Num(name, c.PetCustomRange / 240d),
            "petrefillcount-idle" => Num(name, c.PetRefillCountIdle),
            "petrefillcount-normal" => Num(name, c.PetRefillCountNormal),
            "corpseopentimeoutseconds" => Num(name, i.Loot.CorpseOpenTimeoutSeconds),
            "petmonsterdensity" => Num(name, c.PetMonsterDensity),
            "corpselootitemmaxattempts" => Num(name, i.Loot.CorpseLootItemMaxAttempts),
            "fastcastbuffs" => VtankCell.Bool(b.FastCastBuffs),
            "usebreakableturnto" => VtankCell.Bool(c.UseBreakableTurnTo),
            "useprojectileawareness" => VtankCell.Bool(c.UseProjectileAwareness),
            "collisionprojectileradius" => Num(name, c.CollisionProjectileRadius),
            "collisionstepdistance" => Num(name, c.CollisionStepDistance),
            "showcollisiondebug" => VtankCell.Bool(c.ShowCollisionDebug),
            "maximumcollisioncheckspertick" => Num(name, c.MaximumCollisionChecksPerTick),
            "spellrangefudge" => Num(name, c.SpellRangeFudge),
            "buffwithuntrained-item" => Num(name, b.BuffWithUntrainedItemSkill),
            "buffwithuntrained-creature" => Num(name, b.BuffWithUntrainedCreatureSkill),
            "buffwithuntrained-life" => Num(name, b.BuffWithUntrainedLifeSkill),
            "allowdebufffallback" => VtankCell.Bool(c.AllowDebuffFallback),
            _ => null, // "enablemeta" (live engine state) and "rechargehandlerset"
                       // (no write path exists in real VTank either, section 2 row 137)
                       // are deliberately left untouched.
        };
    }

    internal static RechargeHandlerRow[] ParseRechargeHandlerSet(VtankTable table)
    {
        int vitalColumn = table.ColumnIndex("Vital");
        int handlerColumn = table.ColumnIndex("HandlerString");
        int minColumn = table.ColumnIndex("MinPercent");
        int maxColumn = table.ColumnIndex("MaxPercent");
        int stanceColumn = table.ColumnIndex("Stance");
        if (vitalColumn < 0 || handlerColumn < 0 || minColumn < 0
            || maxColumn < 0 || stanceColumn < 0)
        {
            return [];
        }
        var rows = new RechargeHandlerRow[table.Rows.Count];
        for (int i = 0; i < table.Rows.Count; i++)
        {
            VtankRow row = table.Rows[i];
            rows[i] = new RechargeHandlerRow(
                row.Cells[vitalColumn].AsInt(),
                row.Cells[handlerColumn].AsString(),
                row.Cells[minColumn].AsInt(),
                row.Cells[maxColumn].AsInt(),
                row.Cells[stanceColumn].AsInt());
        }
        return rows;
    }

}

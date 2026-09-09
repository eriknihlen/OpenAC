using System.Reflection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class MossTankPanelTests
{
    [Fact]
    public void CorruptProfileIsPreservedAndReportedBeforeDefaultsLoad()
    {
        var storage = new MemoryStorage();
        storage.Text["profiles/index.json"] = "{ this is not json";

        var panel = new MossTankPanel(
            new FakeHost(new FakeAutomation(), storage));

        Assert.Contains(
            "Raw data was preserved",
            panel.ProfileLifecycleNotice,
            StringComparison.Ordinal);
        KeyValuePair<string, string> backup = Assert.Single(
            storage.Text,
            static pair => pair.Key.StartsWith(
                "recovery/macro/",
                StringComparison.Ordinal));
        Assert.Contains("profiles/index.json", backup.Value, StringComparison.Ordinal);
        Assert.Contains("{ this is not json", backup.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void PanelFillsEveryPositionOfVtanksRuleList()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        IReadOnlyList<IMacroRule> rules = panel.MacroRules;

        Assert.Equal(66, rules.Count);
        Assert.All(rules, static rule => Assert.NotNull(rule));
        Assert.Equal(
            MacroRuleTable.Entries.Count(static entry => entry.Slot is null),
            rules.Count(static rule => rule is MacroRuleSentinel));
        Assert.Equal("IdlePeace", rules[^1].Name);
        Assert.Equal("Sentinel END", rules[^2].Name);
        Assert.All(
            rules.OfType<AbsentMacroRule>(),
            static rule => Assert.NotEmpty(rule.Reason));
    }

    [Fact]
    public void WieldedManaRefillOutranksBuffSelf()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        IReadOnlyList<IMacroRule> rules = panel.MacroRules;

        int mana = rules.ToList().FindIndex(
            static rule => rule.Name == "RefillWieldedMana");
        int buff = rules.ToList().FindIndex(static rule => rule.Name == "BuffSelf");

        Assert.True(mana >= 0 && buff >= 0);
        Assert.True(
            mana < buff,
            $"RefillWieldedMana at {mana} must outrank BuffSelf at {buff}.");
    }

    [Fact]
    public void HelperRechargeSitsBelowBuffSelfAndSelfRechargeAboveIt()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        List<IMacroRule> rules = [.. panel.MacroRules];

        int self = rules.FindIndex(static rule => rule.Name == "RechargeSelfNormal");
        int buff = rules.FindIndex(static rule => rule.Name == "BuffSelf");
        int helper = rules.FindIndex(static rule => rule.Name == "UseHealersHeart");

        Assert.True(self >= 0 && buff >= 0 && helper >= 0);
        Assert.True(
            self < buff,
            $"RechargeSelfNormal at {self} must outrank BuffSelf at {buff}.");
        Assert.True(
            buff < helper,
            $"BuffSelf at {buff} must outrank UseHealersHeart at {helper}.");
    }

    [Fact]
    public void AStagedSelfRechargeIsNotWipedByTheHelperRowsLosingTick()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 10,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            // BoosterVital 2 = VitalKind.Health (VitalPlan.cs:7).
            ItemEntries = [Item(60, "Bread", 1) with { BoosterVital = 2 }],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(60u);
        panel.AddSelectedConsumable();

        panel.ToggleCombat();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([60u], automation.UsedItemIds);
    }

    [Fact]
    public void CorruptSideCarMonsterRuleIsLoggedNotSilentlySwallowed()
    {
        var storage = new MemoryStorage();
        string usdKey = VtankProfileDirectory.AutoCharacterFileName("Barris", string.Empty, "usd");
        storage.Text[usdKey] = VtankDefaultSettingsDatabase.Parse().Render();
        storage.Text["profiles/macro/sidecar/--Barris_.usd.json"] = """
            {
              "CombatRules": [
                { "Expression": "(((" },
                { "Expression": "DEFAULT" }
              ]
            }
            """;

        var host = new FakeHost(new FakeAutomation { Name = "Barris" }, storage);
        _ = new MossTankPanel(host);

        Assert.Contains(host.Logger.Warnings, message =>
            message.Contains("monster rule", StringComparison.OrdinalIgnoreCase));
    }

    private static string LegacyByCharacterProfileKey(string characterName)
    {
        string identity = "char:" + characterName.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)));
        return $"profiles/macro/{hash}.json";
    }

    [Fact]
    public void FirstLoadMigratesLegacyJsonMacroProfileToUsdAndDeletesTheJsonKey()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation { Name = "Barris" };
        string legacyKey = LegacyByCharacterProfileKey("Barris");
        storage.Text[legacyKey] = """
            {
              "Combat": { "MaximumRange": 48.0 },
              "ItemNames": ["Wand of Testing"]
            }
            """;

        var panel = new MossTankPanel(new FakeHost(automation, storage));

        Assert.False(storage.Text.ContainsKey(legacyKey));
        string usdKey = VtankProfileDirectory.AutoCharacterFileName("Barris", string.Empty, "usd");
        Assert.True(storage.Text.ContainsKey(usdKey));
        Assert.Equal(0.2d, panel.EvaluateExpression("uboptget['AttackDistance']").AsNumber(), precision: 7);
        Assert.Contains("Wand of Testing", panel.ItemProfileText, StringComparison.Ordinal);

        var reloaded = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Barris" }, storage));
        Assert.Equal(0.2d, reloaded.EvaluateExpression("uboptget['AttackDistance']").AsNumber(), precision: 7);
        Assert.Contains("Wand of Testing", reloaded.ItemProfileText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingUsdCounterpartLeavesLegacyJsonUntouchedAndUnread()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation { Name = "Barris" };
        string legacyKey = LegacyByCharacterProfileKey("Barris");
        storage.Text[legacyKey] = """{ "Combat": { "MaximumRange": 240.0 } }""";
        string usdKey = VtankProfileDirectory.AutoCharacterFileName("Barris", string.Empty, "usd");
        var seedCombat = new CombatSettings { MaximumRange = 120d };
        VtankDatabase seedDatabase = VtankSettingsProfileSerializer.CreateNew(
            new VtankSettingsProfileSerializer.AllSettings
            {
                Combat = seedCombat,
                Buffs = new BuffSettings(),
                Vitals = new VitalSettings(),
                Inventory = new InventorySettings(),
                Navigation = new NavigationSettings(),
            });
        storage.Text[usdKey] = seedDatabase.Render();

        var panel = new MossTankPanel(new FakeHost(automation, storage));

        Assert.True(storage.Text.ContainsKey(legacyKey));
        Assert.Equal(0.5d, panel.EvaluateExpression("uboptget['AttackDistance']").AsNumber(), precision: 7);
    }

    [Fact]
    public void DropInUsdWithLootingEnabledAndNoSideCarLoadsLootingEnabled()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation { Name = "Barris" };
        string usdKey = VtankProfileDirectory.AutoCharacterFileName("Barris", string.Empty, "usd");

        VtankDatabase database = VtankDefaultSettingsDatabase.Parse();
        VtankTable settingsTable = database.Find("Settings")!;
        int nameColumn = settingsTable.ColumnIndex("Setting");
        int valueColumn = settingsTable.ColumnIndex("Value");
        VtankRow row = settingsTable.Rows.First(candidate =>
            candidate.Cells[nameColumn].AsString().Equals(
                "EnableLooting", StringComparison.OrdinalIgnoreCase));
        row.Cells[valueColumn] = VtankCell.Bool(true);
        storage.Text[usdKey] = database.Render();

        // No side-car key at all — a real drop-in, not something MossTank
        // itself ever saved.
        var panel = new MossTankPanel(new FakeHost(automation, storage));

        Assert.True(panel.LootEnabled);
    }

    private static string LegacyNamedProfileKey(string name)
    {
        string identity = "named:" + name.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)));
        return $"profiles/macro/{hash}.json";
    }

    [Fact]
    public void SettingsRosterSweepConvertsEveryNamedLegacyProfileOnce()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation { Name = "Barris" };
        storage.Text["profiles/index.json"] = """
            {
              "Version": 1,
              "MineOnly": false,
              "Profiles": [
                { "Name": "Farming", "Owner": "Barris" },
                { "Name": "Buffing", "Owner": "Barris" }
              ],
              "SelectedByCharacter": {}
            }
            """;
        storage.Text[LegacyNamedProfileKey("Farming")] =
            """{ "Combat": { "MaximumRange": 42.0 } }""";
        storage.Text[LegacyNamedProfileKey("Buffing")] =
            """{ "Combat": { "MaximumRange": 24.0 } }""";

        var panel = new MossTankPanel(new FakeHost(automation, storage));

        // MineOnly recovered from the old roster's own shape...
        Assert.False(panel.MineOnlyEnabled);
        string farmingUsd = VtankProfileDirectory.SubProfilePrefix("Barris", string.Empty)
            + "Farming.usd";
        string buffingUsd = VtankProfileDirectory.SubProfilePrefix("Barris", string.Empty)
            + "Buffing.usd";
        Assert.Equal(42d, RangeOf(storage, farmingUsd));
        Assert.Equal(24d, RangeOf(storage, buffingUsd));

        // Both legacy JSON keys and the whole (now fully-swept) roster key
        // are gone; MineOnly now lives at its own dedicated key.
        Assert.False(storage.Text.ContainsKey(LegacyNamedProfileKey("Farming")));
        Assert.False(storage.Text.ContainsKey(LegacyNamedProfileKey("Buffing")));
        Assert.False(storage.Text.ContainsKey("profiles/index.json"));
        Assert.True(storage.Text.ContainsKey("profiles/macro/preferences.json"));

        // Idempotent: a fresh panel against the same storage sweeps nothing
        // more (there is no roster key left to read) and keeps both values.
        var reloaded = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Barris" }, storage));
        Assert.False(reloaded.MineOnlyEnabled);
        Assert.Equal(42d, RangeOf(storage, farmingUsd));
        Assert.Equal(24d, RangeOf(storage, buffingUsd));
    }

    private static double RangeOf(MemoryStorage storage, string usdKey)
    {
        string text = Assert.Contains(usdKey, (IDictionary<string, string>)storage.Text);
        var settings = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = new CombatSettings(),
            Buffs = new BuffSettings(),
            Vitals = new VitalSettings(),
            Inventory = new InventorySettings(),
            Navigation = new NavigationSettings(),
        };
        VtankSettingsProfileSerializer.Load(text, settings);
        return settings.Combat.MaximumRange;
    }

    private static string LegacyMetaByCharacterKey(string characterName)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(characterName.ToLowerInvariant()));
        return $"profiles/meta/by-character/{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}.json";
    }

    [Fact]
    public void MetaStoreMigratesLegacyJsonProfileToAfAndDeletesTheJsonKey()
    {
        var storage = new MemoryStorage();
        string legacyKey = LegacyMetaByCharacterKey("Barris");
        var legacyProfile = new MetaProfile
        {
            Rules =
            [
                new MetaRule
                {
                    State = "Default",
                    Condition = MetaCondition.Always(),
                    Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say hi" },
                    Enabled = true,
                },
            ],
        };
        storage.Text[legacyKey] = System.Text.Json.JsonSerializer.Serialize(
            legacyProfile,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var store = new MossTankMetaProfileStore(
            new FakeHost(new FakeAutomation { Name = "Barris" }, storage));
        store.BindCharacter("Barris");
        MetaProfile loaded = store.LoadCurrent();

        Assert.False(storage.Text.ContainsKey(legacyKey));
        MetaRule rule = Assert.Single(loaded.Rules);
        Assert.Equal("/say hi", rule.Action.Text);

        // Idempotent second run: nothing left to migrate, loads straight
        // from the now-real .af file.
        var reopened = new MossTankMetaProfileStore(
            new FakeHost(new FakeAutomation { Name = "Barris" }, storage));
        reopened.BindCharacter("Barris");
        MetaProfile reloaded = reopened.LoadCurrent();
        Assert.Single(reloaded.Rules);
    }

    [Fact]
    public void MetaStoreLeavesLegacyJsonUntouchedWhenAfCounterpartExists()
    {
        var storage = new MemoryStorage();
        string legacyKey = LegacyMetaByCharacterKey("Barris");
        storage.Text[legacyKey] = System.Text.Json.JsonSerializer.Serialize(new MetaProfile
        {
            Rules = [new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say stale" } }],
        });
        string realKey = "metas/" + VtankProfileDirectory.AutoCharacterFileName("Barris", string.Empty, "af");
        storage.Text[realKey] = MetafSerializer.SaveMeta(new MetaProfile
        {
            Rules = [new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say real" } }],
        });

        var store = new MossTankMetaProfileStore(
            new FakeHost(new FakeAutomation { Name = "Barris" }, storage));
        store.BindCharacter("Barris");
        MetaProfile loaded = store.LoadCurrent();

        Assert.True(storage.Text.ContainsKey(legacyKey));
        Assert.Equal("/say real", Assert.Single(loaded.Rules).Action.Text);
    }


    [Fact]
    public void MetaStoreMigratesFlatAfFileIntoMetasFolder()
    {
        var storage = new MemoryStorage();
        storage.Text["Shared.af"] = MetafSerializer.SaveMeta(new MetaProfile
        {
            Rules = [new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say shared" } }],
        });

        var store = new MossTankMetaProfileStore(
            new FakeHost(new FakeAutomation { Name = "Barris" }, storage));
        store.BindCharacter("Barris");
        store.LoadCurrent();

        Assert.True(storage.Text.ContainsKey("metas/Shared.af"));
        Assert.False(storage.Text.ContainsKey("Shared.af"));

        // Idempotent second run: nothing left at the root to migrate.
        var reopened = new MossTankMetaProfileStore(
            new FakeHost(new FakeAutomation { Name = "Barris" }, storage));
        reopened.BindCharacter("Barris");
        reopened.LoadCurrent();
        Assert.True(storage.Text.ContainsKey("metas/Shared.af"));
        Assert.False(storage.Text.ContainsKey("Shared.af"));
    }

    [Fact]
    public void MetaStoreLeavesFlatNavMarkedFilesForTheRouteStore()
    {
        var storage = new MemoryStorage();
        storage.Text["nav_Hunt.af"] = "1\r\n";
        storage.Text["--nav_Barris_Coldeve.af"] = "1\r\n";

        var store = new MossTankMetaProfileStore(
            new FakeHost(new FakeAutomation { Name = "Barris" }, storage));
        store.BindCharacter("Barris");
        store.LoadCurrent();

        Assert.True(storage.Text.ContainsKey("nav_Hunt.af"));
        Assert.True(storage.Text.ContainsKey("--nav_Barris_Coldeve.af"));
        Assert.False(storage.Text.ContainsKey("metas/nav_Hunt.af"));
        Assert.False(storage.Text.ContainsKey("metas/--nav_Barris_Coldeve.af"));
    }

    [Fact]
    public void MetaStoreLeavesFlatFileInPlaceWhenMetasDestinationAlreadyExists()
    {
        var storage = new MemoryStorage();
        string canonicalContent = MetafSerializer.SaveMeta(new MetaProfile
        {
            Rules = [new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say canonical" } }],
        });
        storage.Text["metas/Shared.af"] = canonicalContent;
        storage.Text["Shared.af"] = MetafSerializer.SaveMeta(new MetaProfile
        {
            Rules = [new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say stale-flat" } }],
        });

        var host = new FakeHost(new FakeAutomation { Name = "Barris" }, storage);
        var store = new MossTankMetaProfileStore(host);
        store.BindCharacter("Barris");
        store.LoadCurrent();

        Assert.Equal(canonicalContent, storage.Text["metas/Shared.af"]);
        Assert.True(storage.Text.ContainsKey("Shared.af"));
        Assert.Contains(
            host.Logger.Warnings,
            message => message.Contains("Shared.af", StringComparison.Ordinal)
                && message.Contains("metas/Shared.af", StringComparison.Ordinal));
    }


    [Fact]
    public void MetaStoreRefusesToLoadANavOnlyFileWithNoticeNamingNavsFolder()
    {
        string navOnlyContent = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vtank", "af", "nav_ab.af"));
        var storage = new MemoryStorage();
        storage.Text["metas/Misplaced.af"] = navOnlyContent;

        var host = new FakeHost(new FakeAutomation { Name = "Barris" }, storage);
        var store = new MossTankMetaProfileStore(host);
        store.BindCharacter("Barris");
        Assert.True(store.Select("Misplaced"));

        MetaProfile loaded = store.LoadCurrent();

        Assert.Empty(loaded.Rules);
        Assert.NotNull(store.RecoveryNotice);
        Assert.Contains("navs/", store.RecoveryNotice, StringComparison.Ordinal);
    }

    private static string LegacyMetaNamedKey(string name)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
        return $"profiles/meta/named/{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}.json";
    }

    [Fact]
    public void MetaRosterSweepConvertsEveryNamedLegacyProfileOnce()
    {
        var storage = new MemoryStorage();
        storage.Text["profiles/meta/index.json"] = """{ "Names": ["Farming", "Buffing"] }""";
        storage.Text[LegacyMetaNamedKey("Farming")] = System.Text.Json.JsonSerializer.Serialize(
            new MetaProfile
            {
                Rules = [new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say farming" } }],
            });
        storage.Text[LegacyMetaNamedKey("Buffing")] = System.Text.Json.JsonSerializer.Serialize(
            new MetaProfile
            {
                Rules = [new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say buffing" } }],
            });

        var store = new MossTankMetaProfileStore(
            new FakeHost(new FakeAutomation { Name = "Barris" }, storage));
        store.BindCharacter("Barris");
        store.LoadCurrent();

        Assert.False(storage.Text.ContainsKey(LegacyMetaNamedKey("Farming")));
        Assert.False(storage.Text.ContainsKey(LegacyMetaNamedKey("Buffing")));
        Assert.False(storage.Text.ContainsKey("profiles/meta/index.json"));
        Assert.True(MetafSerializer.TryLoadMeta(
            storage.Text["metas/Farming.af"], NoOpSpellCatalogForExport.Instance, out MetaProfile farming, out _));
        Assert.Equal("/say farming", Assert.Single(farming.Rules).Action.Text);
        Assert.True(MetafSerializer.TryLoadMeta(
            storage.Text["metas/Buffing.af"], NoOpSpellCatalogForExport.Instance, out MetaProfile buffing, out _));
        Assert.Equal("/say buffing", Assert.Single(buffing.Rules).Action.Text);

        // Idempotent: a fresh store against the same storage sweeps nothing
        // more (there is no roster key left to read).
        var reopened = new MossTankMetaProfileStore(
            new FakeHost(new FakeAutomation { Name = "Barris" }, storage));
        reopened.BindCharacter("Barris");
        reopened.LoadCurrent();
        Assert.True(storage.Text.ContainsKey("metas/Farming.af"));
        Assert.True(storage.Text.ContainsKey("metas/Buffing.af"));
    }

    [Fact]
    public void MetaStoreRefusesToSaveADisabledRuleAndKeepsThePriorAfContent()
    {
        var storage = new MemoryStorage();
        var store = new MossTankMetaProfileStore(
            new FakeHost(new FakeAutomation { Name = "Barris" }, storage));
        store.BindCharacter("Barris");
        var enabledOnly = new MetaProfile
        {
            Rules = [new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say good" } }],
        };
        Assert.True(store.SaveCurrent(enabledOnly));
        string key = "metas/" + VtankProfileDirectory.AutoCharacterFileName("Barris", string.Empty, "af");
        string goodContent = storage.Text[key];

        var withDisabledRule = new MetaProfile
        {
            Rules =
            [
                new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say good" } },
                new MetaRule { Action = new MetaAction { Kind = MetaActionKind.ChatCommand, Text = "/say off" }, Enabled = false },
            ],
        };

        bool saved = store.SaveCurrent(withDisabledRule);

        Assert.False(saved);
        Assert.NotNull(store.SaveNotice);
        Assert.Contains("disabled", store.SaveNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(goodContent, storage.Text[key]);
    }

    [Fact]
    public void FirstRunGuidanceExplainsProfilesImportsAndPersistentShelfOnce()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation();
        var panel = new MossTankPanel(new FakeHost(automation, storage));

        panel.OnTick(0d);
        panel.OnTick(0d);

        string message = Assert.Single(
            automation.Messages,
            static value => value.Contains("First run:", StringComparison.Ordinal));
        Assert.Contains("plugin shelf", message, StringComparison.Ordinal);
        Assert.Contains(".nav/.utl/.met", message, StringComparison.Ordinal);
        Assert.Equal("shown", storage.Text["onboarding/v1.txt"]);
    }

    [Fact]
    public void RunningMacroRebuffsNormallyAndUsesWiderIdleTopoffOnlyWhenEnabled()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(
                    1,
                    "Life Magic",
                    PluginSkillTraining.Trained,
                    300),
            ],
            KnownSelfBuffs =
            [
                Spell(
                    1,
                    10,
                    "Increases the caster's Life Magic skill by 10 points."),
            ],
            ActiveEnchantments = [new PluginActiveEnchantment(1, 10, 1, 600)],
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        panel.OnTick(0d);
        Assert.Empty(automation.CastSpellIds);

        panel.ToggleIdleBuffTopoff();
        panel.OnTick(1d);

        Assert.Equal([1u], automation.CastSpellIds);
        Assert.StartsWith("Buffing", panel.BuffStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void StoppingTheMacroEndsAnAutomaticBuffPassInProgress()
    {
        var automation = BuffPassAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        panel.OnTick(0d);
        Assert.NotEmpty(automation.CastSpellIds);
        Assert.StartsWith("Buffing", panel.BuffStatus, StringComparison.Ordinal);

        int castsWhenStopped = automation.CastSpellIds.Count;
        Assert.True(
            castsWhenStopped < 3,
            "the pass must still be mid-queue for this pin to mean anything");

        panel.ToggleCombat();

        for (int i = 0; i < 10; i++)
            panel.OnTick(1d);

        Assert.Equal(castsWhenStopped, automation.CastSpellIds.Count);
        Assert.DoesNotContain(
            "Buffing", panel.BuffStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void StoppingTheMacroEndsTheBuffPassEvenWhenManaChargesKeepTheLoopAlive()
    {
        var automation = BuffPassAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        Command(panel, "opt set ManaChargesWhenOff true");
        panel.ToggleCombat();
        panel.OnTick(0d);
        int castsWhenStopped = automation.CastSpellIds.Count;
        Assert.NotEmpty(automation.CastSpellIds);

        panel.ToggleCombat();

        for (int i = 0; i < 10; i++)
            panel.OnTick(1d);

        Assert.Equal(castsWhenStopped, automation.CastSpellIds.Count);
        Assert.DoesNotContain(
            "Buffing", panel.BuffStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ForceBuffWithTheMacroOffCastsNothingUntilRunMacro()
    {
        var automation = BuffPassAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ForceBuff();
        for (int i = 0; i < 20; i++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);

        panel.ToggleCombat();
        for (int i = 0; i < 20; i++)
            panel.OnTick(0.3d);

        Assert.NotEmpty(automation.CastSpellIds);
    }

    [Fact]
    public void TogglingBuffingWithTheMacroOffCastsNothing()
    {
        var automation = BuffPassAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ForceBuff();
        panel.SetMetaOption("EnableBuffing", Truthy(false));
        for (int i = 0; i < 10; i++)
            panel.OnTick(0.3d);
        panel.SetMetaOption("EnableBuffing", Truthy(true));
        for (int i = 0; i < 20; i++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);
    }

    [Fact]
    public void AServerRejectedCastRePicksTheSameSpellInsteadOfAdvancing()
    {
        var automation = BuffPassAutomation();
        automation.NextCastWeenieError = 0x1Du;
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int i = 0; i < 8; i++)
            panel.OnTick(0.3d);

        Assert.NotEmpty(automation.CastSpellIds);
        Assert.All(automation.CastSpellIds, id => Assert.Equal(1u, id));
        Assert.Contains("Spell 1", panel.BuffStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAcknowledgedCastWithNoResultTextKeepsThePassSuspended()
    {
        var automation = BuffPassAutomation();
        automation.SuppressCastResultText = true;
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        panel.OnTick(0d);
        Assert.Single(automation.CastSpellIds);

        // Three seconds is inside the 4 x 907 ms result budget.
        for (int i = 0; i < 10; i++)
            panel.OnTick(0.3d);

        Assert.False(automation.IsCasting);
        Assert.Single(automation.CastSpellIds);
    }

    [Fact]
    public void AnAcceptedCastAdvancesTheQueueOnItsSuccessfulResult()
    {
        var automation = BuffPassAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int i = 0; i < 8; i++)
            panel.OnTick(0.3d);

        // The plain fake never marks the enchantment active, so a later scan
        // re-queues the same three; only the first pass is under test.
        Assert.Equal([1u, 2u, 3u], automation.CastSpellIds.Take(3));
    }

    [Fact]
    public void APermanentlyRefusedSpellIsPickedAgainForeverBecauseRetailNeverGivesUp()
    {
        var automation = BuffPassAutomation();
        automation.NextCastWeenieError = 0x1Du;
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int i = 0; i < 40; i++)
            panel.OnTick(1d);

        Assert.True(
            automation.CastSpellIds.Count > 5,
            "the refused spell was never re-issued");
        Assert.All(automation.CastSpellIds, id => Assert.Equal(1u, id));
    }

    [Fact]
    public void TheCastSuspensionHoldsForTheResultThoughIsCastingNeverLatched()
    {
        var automation = BuffPassAutomation();
        automation.SuppressCastCompletion = true;
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        panel.OnTick(0d);
        Assert.Single(automation.CastSpellIds);

        for (int i = 0; i < 10; i++)
            panel.OnTick(0.3d);

        Assert.False(automation.IsCasting);
        Assert.Single(automation.CastSpellIds);

        // Past the 5000 ms attempt watchdog (gj.cs:319-324) the tracker drops
        // to idle and re-issues the SAME spell; it never walks the queue.
        for (int i = 0; i < 12; i++)
            panel.OnTick(0.3d);

        Assert.True(automation.CastSpellIds.Count > 1);
        Assert.All(automation.CastSpellIds, id => Assert.Equal(1u, id));
    }

    [Fact]
    public void AMasteryBuffThatLandsUnlocksTheHigherTierOfTheNextPick()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 100),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
                // The second family: tier 2 needs skill 205, tier 1 needs 15.
                Spell(2, 20, "Increases the caster's Strength by 10 points.")
                    with { Tier = 2, Difficulty = 200 },
                Spell(3, 20, "Increases the caster's Strength by 10 points.")
                    with { Tier = 1, Difficulty = 10 },
            ],
            Attributes = [new PluginAttributeInfo(0, "Strength", 100)],
            RaiseSkillOnCast = (1u, 1u, 300u),
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([1u, 2u], automation.CastSpellIds);
    }

    [Fact]
    public void TheIdleTopoffWindowBelongsToRow53NotToTheRebuffRule()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
            // 600 s left: over the 300 s rebuff window, under the 1200 s idle one.
            ActiveEnchantments = [new PluginActiveEnchantment(1u, 10u, 1, 600d)],
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.SetMetaOption("IdleBuffTopoff", Truthy(true));
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log ActiveRule on", "/vt log ActiveRule on"));
        automation.Messages.Clear();

        panel.ToggleCombat();
        panel.OnTick(0.3d);

        Assert.Contains(
            automation.Messages,
            static message => message.StartsWith(
                "[MossTank] Picked BuffSelfIdle", StringComparison.Ordinal));
        Assert.DoesNotContain(
            automation.Messages,
            static message => message.StartsWith(
                "[MossTank] Picked BuffSelf ", StringComparison.Ordinal));
    }

    [Fact]
    public void WithIdleTopoffOffTheWiderWindowIsNotConsideredAtAll()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
            ActiveEnchantments = [new PluginActiveEnchantment(1u, 10u, 1, 600d)],
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);
    }

    [Fact]
    public void AFamilyWhoseTopTierHasNoScarabsFallsToTheCastableLowerTier()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(2, 20, "Increases the caster's Strength by 10 points.")
                    with { Tier = 2, Difficulty = 200, FormulaComponentIds = [7u] },
                Spell(3, 20, "Increases the caster's Strength by 10 points.")
                    with { Tier = 1, Difficulty = 10, FormulaComponentIds = [8u] },
            ],
            Attributes = [new PluginAttributeInfo(0, "Strength", 100)],
            ItemEntries = [Item(50, "Pyreal Scarab", 1)],
        };
        automation.Components[7u] = Component(7u, "Lead Scarab");
        automation.Components[8u] = Component(8u, "Pyreal Scarab");
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([3u], automation.CastSpellIds);
        Assert.Contains(
            "[MossTank] Warning: You do not have enough of the item "
                + "\"Lead Scarab\". Spells using it have been disabled.",
            automation.Messages);
    }

    [Fact]
    public void AFamilyWithNoCastableTierIsSkippedAndTheNextFamilyIsCast()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(2, 20, "Increases the caster's Strength by 10 points.")
                    with { FormulaComponentIds = [7u] },
                Spell(3, 21, "Increases the caster's Endurance by 10 points.")
                    with { FormulaComponentIds = [8u] },
            ],
            Attributes =
            [
                new PluginAttributeInfo(0, "Strength", 100),
                new PluginAttributeInfo(1, "Endurance", 100),
            ],
            ItemEntries = [Item(50, "Pyreal Scarab", 1)],
        };
        automation.Components[7u] = Component(7u, "Lead Scarab");
        automation.Components[8u] = Component(8u, "Pyreal Scarab");
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([3u], automation.CastSpellIds);
        Assert.Contains(
            "[MossTank] No spell known for class including: Spell 2, buff SKIPPED.",
            automation.Messages);
    }

    [Fact]
    public void ARefusedCastSaysWhichSpellAndWhyOnceOnTheCastInfoChannel()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 20, "Increases the caster's Strength by 10 points."),
            ],
            Attributes = [new PluginAttributeInfo(0, "Strength", 100)],
        };
        automation.CastRefusals[1u] = PluginCastRequestResult.MissingComponents;
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log CastInfo on", "/vt log CastInfo on"));
        automation.Messages.Clear();

        panel.ToggleCombat();
        for (int tick = 0; tick < 8; tick++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);
        Assert.Single(
            automation.Messages,
            message => message.Contains("not issued", StringComparison.Ordinal));
        Assert.Contains(
            "[MossTank] SpellCaster: Spell 1 not issued — MissingComponents",
            automation.Messages);
    }

    [Fact]
    public void ATierTheHostHasNoComponentsForIsNotACandidateAndTheWalkDropsToTheNext()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 20, "Increases the caster's Strength by 10 points."),
                Spell(2, 20, "Increases the caster's Strength by 10 points.")
                    with { Tier = 6, Difficulty = 250 },
            ],
            Attributes = [new PluginAttributeInfo(0, "Strength", 100)],
        };
        automation.MissingComponentSpellIds.Add(2u);

        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log CastInfo on", "/vt log CastInfo on"));
        automation.Messages.Clear();

        panel.ToggleCombat();
        for (int tick = 0; tick < 8; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([1u], automation.CastSpellIds);
        Assert.DoesNotContain(
            automation.Messages,
            message => message.Contains("not issued", StringComparison.Ordinal));
    }

    [Fact]
    public void TimersTraceNamesEveryHigherTierAndTheFirstFailingTermWithNumbers()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills = [new PluginSkillInfo(1, "Skill", PluginSkillTraining.Trained, 288)],
            Attributes = [new PluginAttributeInfo(0, "Strength", 100)],
            KnownSelfBuffs =
            [
                // Lowest Difficulty, self-targeted -> BuffLine.Reference (the
                // family's own anchor), and the tier the walk should land on:
                // needed = 250 + 5 = 255 <= 288.
                Spell(6, 90, "Increases the caster's Strength by 10 points.")
                    with { Tier = 6, Difficulty = 250 },
                // needed = 296 + 5 = 301 > 288 -- skill-short.
                Spell(7, 90, "Increases the caster's Strength by 10 points.")
                    with { Tier = 7, Difficulty = 296 },
                Spell(8, 90, "Increases the caster's Strength by 10 points.")
                    with { Tier = 8, Difficulty = 340, IsFellowship = true },
            ],
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        Command(panel, "log Timers on");
        automation.Messages.Clear();

        panel.ToggleCombat();
        panel.OnTick(0.3d);

        Assert.Contains(
            "[MossTank] Buffing: Spell 6 — picked Spell 6 (gen 6); "
                + "rejected: Spell 8 unknown, Spell 7 skill 288 < 301",
            automation.Messages);
    }

    [Fact]
    public void NoTimersLineWhenTheHighestKnownTierIsPicked()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills = [new PluginSkillInfo(1, "Skill", PluginSkillTraining.Trained, 300)],
            Attributes = [new PluginAttributeInfo(0, "Strength", 100)],
            KnownSelfBuffs =
            [
                Spell(1, 91, "Increases the caster's Strength by 10 points.")
                    with { Tier = 1, Difficulty = 10 },
                Spell(2, 91, "Increases the caster's Strength by 10 points.")
                    with { Tier = 2, Difficulty = 20 },
            ],
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        Command(panel, "log Timers on");
        automation.Messages.Clear();

        panel.ToggleCombat();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([2u], automation.CastSpellIds);
        Assert.DoesNotContain(
            automation.Messages,
            message => message.Contains("Buffing:", StringComparison.Ordinal));
    }

    [Fact]
    public void TimersTraceIsNotReemittedAcrossRepeatedIdenticalPasses()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills = [new PluginSkillInfo(1, "Skill", PluginSkillTraining.Trained, 288)],
            Attributes = [new PluginAttributeInfo(0, "Strength", 100)],
            KnownSelfBuffs =
            [
                Spell(6, 90, "Increases the caster's Strength by 10 points.")
                    with { Tier = 6, Difficulty = 250 },
                Spell(7, 90, "Increases the caster's Strength by 10 points.")
                    with { Tier = 7, Difficulty = 296 },
                Spell(8, 90, "Increases the caster's Strength by 10 points.")
                    with { Tier = 8, Difficulty = 340, IsFellowship = true },
            ],
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        Command(panel, "log Timers on");
        automation.Messages.Clear();

        panel.ToggleCombat();
        panel.OnTick(0.3d);
        int afterFirstPass = automation.Messages.Count(
            message => message.StartsWith(
                "[MossTank] Buffing: Spell 6", StringComparison.Ordinal));
        Assert.Equal(1, afterFirstPass);

        for (int tick = 0; tick < 5; tick++)
            panel.OnTick(0.3d);
        int afterFiveMorePasses = automation.Messages.Count(
            message => message.StartsWith(
                "[MossTank] Buffing: Spell 6", StringComparison.Ordinal));
        Assert.Equal(1, afterFiveMorePasses);
    }

    [Fact]
    public void TheBuffCastRecastLockWidensTheDueWindowRatherThanBlockingPicks()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
                Spell(2, 20, "Increases the caster's Strength by 10 points."),
            ],
            Attributes = [new PluginAttributeInfo(0, "Strength", 100)],
            ActiveEnchantments = [new PluginActiveEnchantment(2, 20, 1, 310)],
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([1u, 2u], automation.CastSpellIds);
    }

    [Fact]
    public void ForceBuffZeroesTheDueStampsAndCancelPutsThemBack()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
            ActiveEnchantments = [new PluginActiveEnchantment(1, 10, 1, 1800)],
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        // Nothing is due: 1800 s left against a 300 s threshold.
        panel.ToggleCombat();
        panel.OnTick(0.3d);
        Assert.Empty(automation.CastSpellIds);

        // eq.i() - everything now reads as about to expire...
        panel.ForceBuff();
        // ...and eq.e() puts it back before the next heartbeat can act.
        panel.CancelForceBuff();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);
        Assert.Empty(automation.CastSpellIds);

        panel.ForceBuff();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);
        Assert.Equal([1u], automation.CastSpellIds);
    }

    [Fact]
    public void AForcedEntryStopsBeingForcedOnceItIsRecast()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
            ActiveEnchantments = [new PluginActiveEnchantment(1, 10, 1, 1800)],
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        panel.ForceBuff();
        for (int tick = 0; tick < 20; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([1u], automation.CastSpellIds);
    }

    [Fact]
    public void TheBuffCastEmitsVtanksSpellCastLogLine()
    {
        var automation = BuffPassAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        Command(panel, "log SpellCast on");
        panel.ToggleCombat();
        panel.OnTick(0.3d);

        Assert.Contains(
            automation.Messages,
            static value => value.Contains(
                "Casting: Spell 1 on ", StringComparison.Ordinal));
    }

    [Fact]
    public void AFizzledBuffIsSimplyStillDueOnTheNextHeartbeat()
    {
        var automation = BuffPassAutomation();
        automation.CastResultText = "Your spell fizzled.";
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int tick = 0; tick < 10; tick++)
            panel.OnTick(0.3d);

        Assert.True(automation.CastSpellIds.Count > 2);
        Assert.All(automation.CastSpellIds, id => Assert.Equal(1u, id));
    }

    [Fact]
    public void APermanentFailIsNotADropBecauseEqHasNoSuchBlacklist()
    {
        var automation = BuffPassAutomation();
        automation.CastResultText = "Target is out of range";
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int tick = 0; tick < 10; tick++)
            panel.OnTick(0.3d);

        Assert.True(automation.CastSpellIds.Count > 2);
        Assert.All(automation.CastSpellIds, id => Assert.Equal(1u, id));
    }

    [Fact]
    public void AddingAWandPopulatesItsThreeDefaultAurasAndCastsThemAtTheItem()
    {
        var automation = ItemEnchantAutomation();
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([101u, 102u, 103u], automation.CastSpellIds);
        Assert.Equal(10u, host.Selection.SelectedObjectId);
    }

    [Fact]
    public void AddingAShieldPopulatesTheSevenBanesAndImpenetrability()
    {
        var automation = ItemEnchantAutomation();
        automation.ItemEntries =
        [
            Item(20, "Tower Shield", 2, validLocations: 0x00200000u),
        ];
        automation.KnownSelfBuffs =
        [
            NamedSpell(201, 301, "Blade Bane I", ItemEnchantmentSchoolId),
            NamedSpell(202, 302, "Impenetrability I", ItemEnchantmentSchoolId),
        ];
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(20);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([202u, 201u, 201u, 202u], automation.CastSpellIds);
    }

    [Fact]
    public void ShieldBaneRowsFollowTheDamageElementEnumOrder()
    {
        FakeAutomation automation = ItemEnchantAutomation();
        automation.ItemEntries =
        [
            Item(20, "Tower Shield", 2, validLocations: 0x00200000u),
        ];
        automation.KnownSelfBuffs =
        [
            NamedSpell(210, 310, "Piercing Bane I", ItemEnchantmentSchoolId),
            NamedSpell(211, 311, "Acid Bane I", ItemEnchantmentSchoolId),
            NamedSpell(212, 312, "Flame Bane I", ItemEnchantmentSchoolId),
        ];
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(20);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Equal(
            [211u, 212u, 210u, 210u, 211u, 212u], automation.CastSpellIds);
    }

    [Fact]
    public void UntrainedItemEnchantmentOverTheLevelThresholdBuildsNoItemRows()
    {
        FakeAutomation automation = ItemEnchantAutomation();
        // No Item Enchantment skill at all, and well past
        // BuffWithUntrained-Item's default 80.
        automation.Level = 100;
        var withBanes = new List<PluginSpellInfo>(automation.KnownSelfBuffs)
        {
            Bane(341, 441, "Impenetrability I"),
            Bane(342, 442, "Acid Bane I"),
        };
        automation.KnownSelfBuffs = withBanes;
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);
    }

    [Fact]
    public void UntrainedItemEnchantmentUnderTheLevelThresholdStillBuildsRows()
    {
        FakeAutomation automation = ItemEnchantAutomation();
        automation.Level = 80;
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([101u, 102u, 103u], automation.CastSpellIds);
    }

    [Fact]
    public void AWandAddedWithNoBuffsCastsNothingAtIt()
    {
        var automation = ItemEnchantAutomation();
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItemNoBuffs();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);
    }

    [Fact]
    public void AnItemAlreadyEnchantedForLongerThanTheThresholdIsNotDue()
    {
        var automation = ItemEnchantAutomation();
        automation.ItemEnchantments[10u] =
        [
            new PluginTrackedEnchantment(10u, 101u, 201u, 1, false, 1800d),
            new PluginTrackedEnchantment(10u, 102u, 202u, 1, false, 1800d),
        ];
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([103u], automation.CastSpellIds);
    }

    [Fact]
    public void ForceBuffAlsoForcesTheItemEnchantRowsBecauseEqIEndsOnDmD()
    {
        FakeAutomation automation = ItemEnchantAutomation();
        automation.ItemEnchantments[10u] =
        [
            new PluginTrackedEnchantment(10u, 101u, 201u, 1, false, 1800d),
            new PluginTrackedEnchantment(10u, 102u, 202u, 1, false, 1800d),
            new PluginTrackedEnchantment(10u, 103u, 203u, 1, false, 1800d),
        ];
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 4; tick++)
            panel.OnTick(0.3d);
        Assert.Empty(automation.CastSpellIds);

        panel.ForceBuff();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([101u, 102u, 103u], automation.CastSpellIds);
    }

    /// <summary>
    /// <c>eq.a(out, out)</c> walks <c>b()</c> — the SELF list — to exhaustion
    /// before it ever reaches <c>g()</c> (<c>eq.cs:481</c> then
    /// <c>eq.cs:510</c>). A self buff that is due therefore always outranks
    /// every item enchantment.
    /// Mutation: try the item rows first in TryPickBuff and this fails.
    /// </summary>
    [Fact]
    public void EverySelfBuffIsCastBeforeAnyItemEnchantment()
    {
        var automation = ItemEnchantAutomation();
        automation.Skills =
        [
            new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
        ];
        automation.KnownSelfBuffs =
        [
            .. automation.KnownSelfBuffs,
            Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
        ];
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([1u, 101u, 102u, 103u], automation.CastSpellIds);
    }


    private static FakeAutomation BaneAutomation(
        params PluginSpellInfo[] known) => new()
    {
        CurrentHealth = 100,
        MaxHealth = 100,
        CurrentStamina = 100,
        MaxStamina = 100,
        CurrentMana = 100,
        MaxMana = 100,
        ObjectId = 0x50000001u,
        Skills =
        [
            new PluginSkillInfo(
                ItemEnchantmentSchoolId,
                "Item Enchantment",
                PluginSkillTraining.Trained,
                300),
        ],
        KnownSelfBuffs = known,
    };

    private static PluginSpellInfo Bane(uint id, uint family, string name) =>
        new(
            id,
            name,
            family,
            Tier: 1,
            Difficulty: 10,
            ManaCost: 5,
            DurationSeconds: 1800f,
            School: ItemEnchantmentSchoolId,
            "Increases a shield or piece of armor's resistance by 10%. "
                + "Target yourself to cast this spell on all of your equipped armor.",
            IsSelfTargeted: false,
            IsBeneficial: true);

    [Fact]
    public void ACharacterTargetedBaneIsCastOnceAndThenCoveredByTheLedger()
    {
        FakeAutomation automation = BaneAutomation(
            Bane(301, 401, "Blade Bane I"));
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        panel.ToggleCombat();
        for (int tick = 0; tick < 30; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([301u], automation.CastSpellIds);

        Assert.Empty(automation.ActiveEnchantments);
        Assert.False(
            automation.ItemEnchantments.TryGetValue(
                automation.ObjectId, out var onCharacter)
                && onCharacter.Count > 0,
            "a bane never lands on the character itself");
    }

    [Fact]
    public void ImpenetrabilityIsTheFirstCharacterRow()
    {
        FakeAutomation automation = BaneAutomation(
            Bane(311, 411, "Acid Bane I"),
            Bane(312, 412, "Impenetrability I"));
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        panel.ToggleCombat();
        for (int tick = 0; tick < 30; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([312u, 311u], automation.CastSpellIds);
    }

    [Fact]
    public void BaneRowsFollowTheProfileLettersInReadingOrder()
    {
        FakeAutomation automation = BaneAutomation(
            Bane(321, 421, "Bludgeon Bane I"),
            Bane(322, 422, "Lightning Bane I"));
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);
        panel.SetMetaOption(
            "BuffProfile_Banes",
            AcDream.Plugins.MossTank.Expressions.ExpressionValue.Number(1));
        panel.SetMetaOption(
            "BuffProfile-Banes",
            AcDream.Plugins.MossTank.Expressions.ExpressionValue.String("LB"));

        panel.ToggleCombat();
        for (int tick = 0; tick < 30; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([322u, 321u], automation.CastSpellIds);
    }

    [Fact]
    public void CharacterRowsClimbTheFamilyPastTheRenamedTiers()
    {
        PluginSpellInfo tierOne = Bane(331, 431, "Acid Bane I");
        FakeAutomation automation = BaneAutomation(
            tierOne,
            tierOne with { SpellId = 0x082Cu, Name = "Olthoi's Bane", Tier = 7, Difficulty = 300 },
            tierOne with { SpellId = 0x1127u, Name = "Incantation of Acid Bane", Tier = 8, Difficulty = 400 });
        automation.Skills =
        [
            new PluginSkillInfo(
                ItemEnchantmentSchoolId,
                "Item Enchantment",
                PluginSkillTraining.Trained,
                410),
        ];
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);
        panel.SetMetaOption(
            "BuffProfile_Banes",
            AcDream.Plugins.MossTank.Expressions.ExpressionValue.Number(8));

        panel.ToggleCombat();
        for (int tick = 0; tick < 30; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([0x1127u], automation.CastSpellIds);
    }

    [Fact]
    public void CharacterRowsAreAcceptedAgainstTheirOwnTierOneSpell()
    {
        PluginSpellInfo tierOne = Bane(331, 431, "Acid Bane I") with
        {
            ComponentSet = new PluginSpellComponentSet(7, 34, 42, 57),
        };
        PluginSpellInfo decoy = tierOne with
        {
            SpellId = 900u,
            Name = "Acid Ward Self I",
            IsSelfTargeted = true,
            Difficulty = 5,
            ComponentSet = new PluginSpellComponentSet(7, 34, 42, 61),
        };
        FakeAutomation automation = BaneAutomation(
            tierOne,
            decoy,
            tierOne with { SpellId = 0x1127u, Name = "Incantation of Acid Bane", Tier = 8, Difficulty = 400 });
        automation.Skills =
        [
            new PluginSkillInfo(
                ItemEnchantmentSchoolId,
                "Item Enchantment",
                PluginSkillTraining.Trained,
                410),
        ];
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);
        panel.SetMetaOption(
            "BuffProfile_Banes",
            AcDream.Plugins.MossTank.Expressions.ExpressionValue.Number(8));

        panel.ToggleCombat();
        for (int tick = 0; tick < 30; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([0x1127u], automation.CastSpellIds);
    }

    [Fact]
    public void TheBanePresetsAreTheBaneEnumsNotTheProtectionEnums()
    {
        FakeAutomation automation = BaneAutomation(
            Bane(331, 431, "Acid Bane I"),
            Bane(332, 432, "Blade Bane I"));
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);
        panel.SetMetaOption(
            "BuffProfile_Banes",
            AcDream.Plugins.MossTank.Expressions.ExpressionValue.Number(8));

        panel.ToggleCombat();
        for (int tick = 0; tick < 30; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([331u], automation.CastSpellIds);
    }

    [Fact]
    public void ASelfCastBaneLandsOnArmorAndStampsTheLedgerAtTheCharacter()
    {
        FakeAutomation automation = BaneAutomation(
            Bane(351, 451, "Blade Bane I"));
        automation.CastResultText =
            "You cast Blade Bane I on Alduressa Boots, refreshing Blade Bane I";
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log Misc on", "/vt log Misc on"));

        panel.ToggleCombat();
        for (int tick = 0; tick < 30; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([351u], automation.CastSpellIds);

        Assert.Contains(
            automation.Messages,
            message => message.Contains(
                $"Cast Blade Bane I on {automation.ObjectId} ending at",
                StringComparison.Ordinal));

        Assert.Contains(
            automation.Messages,
            message => message.Contains(
                $"Buffing: character row {automation.Name} "
                    + $"({automation.ObjectId}) → Blade Bane I",
                StringComparison.Ordinal));
    }

    /// <summary>AC's Item Enchantment skill id, the aura school.</summary>
    private const uint ItemEnchantmentSchoolId = 32u;

    private static FakeAutomation ItemEnchantAutomation() => new()
    {
        CurrentHealth = 100,
        MaxHealth = 100,
        CurrentStamina = 100,
        MaxStamina = 100,
        CurrentMana = 100,
        MaxMana = 100,
        ObjectId = 0x50000001u,
        Skills = [],
        KnownSelfBuffs =
        [
            NamedSpell(101, 201, "Aura of Defender Self I", ItemEnchantmentSchoolId)
                with { IsSelfTargeted = true },
            NamedSpell(102, 202, "Aura of Hermetic Link Self I", ItemEnchantmentSchoolId)
                with { IsSelfTargeted = true },
            NamedSpell(103, 203, "Aura of Spirit Drinker Self I", ItemEnchantmentSchoolId)
                with { IsSelfTargeted = true },
        ],
        ItemEntries = [Item(10, "War Wand", 0x8000u, validLocations: 0x01000000u)],
    };

    private static PluginSpellInfo NamedSpell(
        uint id,
        uint family,
        string name,
        uint school) => new(
            id,
            name,
            family,
            Tier: 1,
            Difficulty: 10,
            ManaCost: 5,
            DurationSeconds: 1800f,
            School: school,
            "Increases a weapon's damage value by 2 points.",
            IsSelfTargeted: false,
            IsBeneficial: true);

    [Fact]
    public void RandomHelperDrawsItsTargetAtRandomAcrossTheNearbyPlayers()
    {
        var automation = RandomHelperAutomation();
        automation.WorldObjects.Add(NearbyPlayer(0x50000009u, "Fellow A"));
        automation.WorldObjects.Add(NearbyPlayer(0x5000000Au, "Fellow B"));
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(10);
        panel.AddSelectedItem();   // the gate needs a profiled wand
        panel.SetMetaOption("RandomHelperBuffs", Truthy(true));
        panel.ToggleCombat();

        for (int tick = 0; tick < 200; tick++)
            panel.OnTick(0.3d);

        Assert.True(
            automation.CastTargets.Count >= 5,
            $"only {automation.CastTargets.Count} helper casts");
        Assert.Equal(2, automation.CastTargets.Distinct().Count());
    }

    [Fact]
    public void RandomHelperCastsTheBestKnownTierOfTheDrawnStem()
    {
        var automation = RandomHelperAutomation();
        automation.KnownSelfBuffs =
        [
            NamedSpell(500, 600, "Armor Other I", 33u),
            NamedSpell(506, 600, "Armor Other VI", 33u) with { Tier = 6 },
        ];
        automation.WorldObjects.Add(NearbyPlayer(0x50000009u, "Fellow A"));
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(10);
        panel.AddSelectedItem();   // the gate needs a profiled wand
        panel.SetMetaOption("RandomHelperBuffs", Truthy(true));
        panel.ToggleCombat();

        for (int tick = 0; tick < 20; tick++)
            panel.OnTick(0.3d);

        Assert.NotEmpty(automation.CastSpellIds);
        Assert.All(automation.CastSpellIds, id => Assert.Equal(506u, id));
    }

    private static PluginWorldObject NearbyPlayer(uint objectId, string name) =>
        new(objectId, 123u, name, PluginObjectClass.Player, 0u, 0u, 0u)
        {
            HasPosition = true,
            Position = NavigationAt(0f).Position,
        };

    private static CombatCapableFakeAutomation RandomHelperAutomation()
    {
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            ItemEntries = [Item(10, "War Wand", itemType: 0x00008000u)],
            EquipmentItems =
            [
                EquipmentItem(10, "War Wand", itemType: 0x00008000u),
            ],
            Skills =
            [
                new PluginSkillInfo(33, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs = [NamedSpell(500, 600, "Armor Other I", 33u)],
        };
        automation.CombatSnapshot = automation.CombatSnapshot with
        {
            Mode = PluginCombatMode.Magic,
        };
        return automation;
    }

    private static FakeAutomation BuffPassAutomation() => new()
    {
        CurrentHealth = 100,
        MaxHealth = 100,
        CurrentStamina = 100,
        MaxStamina = 100,
        CurrentMana = 100,
        MaxMana = 100,
        Skills =
        [
            new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            new PluginSkillInfo(2, "War Magic", PluginSkillTraining.Trained, 300),
            new PluginSkillInfo(3, "Item Tinkering", PluginSkillTraining.Trained, 300),
        ],
        KnownSelfBuffs =
        [
            Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            Spell(2, 20, "Increases the caster's War Magic skill by 10 points."),
            Spell(3, 30, "Increases the caster's Item Tinkering skill by 10 points."),
        ],
    };

    [Fact]
    public void MacroWieldsCasterEntersMagicBuffsThenWieldsWeaponFightsThenIdlePeace()
    {
        PluginSpellInfo buff = Spell(
            1, 10, "Increases the caster's Life Magic skill by 10 points.");
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills = [new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300)],
            KnownSelfBuffs = [buff],
            ItemEntries =
            [
                Item(10, "War Wand", itemType: 0x00008000u),
                Item(20, "Battle Axe", itemType: 1),
            ],
            EquipmentItems =
            [
                EquipmentItem(10, "War Wand", itemType: 0x00008000u),
                EquipmentItem(20, "Battle Axe", itemType: 1),
            ],
            Targets = [new PluginCombatTarget(30, "Drudge", 700, 2f, 0f, true, 1f)],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItem(); // Items profile: the wand
        host.Selection.Select(20);
        panel.AddSelectedItem(); // Items profile: the axe
        panel.CycleMonsterWeaponAt(0);
        panel.ToggleIdlePeaceMode();

        panel.ToggleCombat(); // Run Macro, starting in Peace

        bool attacked = false;
        for (int tick = 0; tick < 60 && !attacked; tick++)
        {
            panel.OnTick(0.7);
            attacked = automation.BeginCount > 0;
        }
        Assert.True(
            attacked,
            "Never attacked. CallLog: " + string.Join(" | ", automation.CallLog));

        int equipWand = automation.CallLog.IndexOf("Equip:0000000A");
        int enterMagic = automation.CallLog.IndexOf("EnterMode:Magic");
        int cast = automation.CallLog.IndexOf("Cast:1");
        int equipWeapon = automation.CallLog.IndexOf("Equip:00000014");
        int defaultMode = automation.CallLog.FindIndex(
            entry => entry == "EnterMode:Melee");
        int attack = automation.CallLog.IndexOf("Attack:0000001E");

        // Peace(already) -> Equip wand: no separate peace request was needed
        // for the wand because the macro started in Peace already.
        Assert.True(equipWand >= 0, "wand was never equipped");
        Assert.DoesNotContain(
            "EnterMode:Peace",
            automation.CallLog.Take(equipWand));
        Assert.True(enterMagic > equipWand, "Magic requested before the wand was wielded");
        Assert.True(cast > enterMagic, "cast happened before Magic mode was entered");
        Assert.True(
            equipWeapon > cast,
            "the weapon was wielded before the buff was cast: "
                + string.Join(" | ", automation.CallLog));
        Assert.True(
            defaultMode > equipWeapon,
            "combat mode entered before the weapon was equipped: "
                + string.Join(" | ", automation.CallLog));
        Assert.DoesNotContain("EnterDefaultMode:Melee", automation.CallLog);
        Assert.True(attack > defaultMode, "attack began before the default mode was entered");

        // With the hostile gone and Peace Mode When Idle on, the macro
        // returns to peace by itself.
        automation.Targets = [];
        for (int tick = 0;
             tick < 60 && automation.CombatSnapshot.Mode != PluginCombatMode.Peace;
             tick++)
        {
            panel.OnTick(0.5);
        }
        Assert.Equal(PluginCombatMode.Peace, automation.CombatSnapshot.Mode);
    }

    [Fact]
    public void ACastInFlightFreezesTheWholeMainTrackNotJustTheCastingRules()
    {
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            IsCasting = true,
        };
        automation.CombatSnapshot = automation.CombatSnapshot with
        {
            Mode = PluginCombatMode.Melee,
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleIdlePeaceMode();
        panel.ToggleCombat();

        for (int tick = 0; tick < 10; tick++)
            panel.OnTick(0.3d);

        Assert.DoesNotContain("EnterMode:Peace", automation.CallLog);
        Assert.Equal(PluginCombatMode.Melee, automation.CombatSnapshot.Mode);

        automation.IsCasting = false;
        panel.OnTick(0.01d);
        panel.OnTick(0.01d);

        Assert.Contains("EnterMode:Peace", automation.CallLog);
    }


    [Fact]
    public void VtLogActiveRuleOnPostsThePickedLineNamingTheWinner()
    {
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
        };
        automation.CombatSnapshot = automation.CombatSnapshot with
        {
            Mode = PluginCombatMode.Melee,
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleIdlePeaceMode();
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log ActiveRule on", "/vt log ActiveRule on"));
        automation.Messages.Clear(); // drop "/vt log"'s own "Set ActiveRule" echo
        panel.ToggleCombat();

        panel.OnTick(0.1d);

        Assert.Contains(
            "[MossTank] Picked IdlePeace P: 65   I=False, N=False, S=False",
            automation.Messages);
    }

    [Fact]
    public void VtLogActiveRuleOnPostsAllRulesInactiveWhenNothingIsValid()
    {
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
        };
        automation.CombatSnapshot = automation.CombatSnapshot with
        {
            Mode = PluginCombatMode.Peace,
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleIdlePeaceMode(); // valid only outside Peace — stays silent
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log ActiveRule on", "/vt log ActiveRule on"));
        automation.Messages.Clear();
        panel.ToggleCombat();

        panel.OnTick(0.1d);

        Assert.Contains(
            "[MossTank] All rules inactive.   I=False, N=False, S=False",
            automation.Messages);
    }

    [Fact]
    public void VtLogActiveRuleOffPostsNothing()
    {
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
        };
        automation.CombatSnapshot = automation.CombatSnapshot with
        {
            Mode = PluginCombatMode.Melee,
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleIdlePeaceMode();
        panel.ToggleCombat();

        panel.OnTick(0.1d);

        Assert.DoesNotContain(automation.Messages, message => message.StartsWith(
            "[MossTank] Picked", StringComparison.Ordinal));
        Assert.DoesNotContain(automation.Messages, message => message.StartsWith(
            "[MossTank] All rules inactive", StringComparison.Ordinal));
    }

    [Fact]
    public void VtLogRuleInfoOnPostsRuleRunningLine()
    {
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
        };
        automation.CombatSnapshot = automation.CombatSnapshot with
        {
            Mode = PluginCombatMode.Melee,
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleIdlePeaceMode();
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log RuleInfo on", "/vt log RuleInfo on"));
        automation.Messages.Clear();
        panel.ToggleCombat();

        panel.OnTick(0.1d);

        Assert.Contains("[MossTank] (IdlePeace) Running", automation.Messages);
    }

    /// <summary>
    /// The other half of the sink: turning a type off stops posting it
    /// again, and re-uses the exact <c>Set</c>/<c>Reset</c> echo text
    /// <c>/vt log</c> itself already had.
    /// </summary>
    [Fact]
    public void VtLogOffStopsPostingAfterHavingBeenOn()
    {
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
        };
        automation.CombatSnapshot = automation.CombatSnapshot with
        {
            Mode = PluginCombatMode.Melee,
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleIdlePeaceMode();
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log ActiveRule on", "/vt log ActiveRule on"));
        panel.ToggleCombat();
        panel.OnTick(0.1d);
        Assert.Contains(automation.Messages, message => message.StartsWith(
            "[MossTank] Picked", StringComparison.Ordinal));

        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log ActiveRule off", "/vt log ActiveRule off"));
        automation.Messages.Clear();
        panel.OnTick(0.1d);

        Assert.DoesNotContain(automation.Messages, message => message.StartsWith(
            "[MossTank] Picked", StringComparison.Ordinal));
    }

    [Fact]
    public void AStuckTransactionReleasesTheSuspensionOnVtanksTrackerWatchdog()
    {
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            IsCasting = true,
        };
        automation.CombatSnapshot = automation.CombatSnapshot with
        {
            Mode = PluginCombatMode.Melee,
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleIdlePeaceMode();
        panel.ToggleCombat();

        for (int tick = 0; tick < 34; tick++)
            panel.OnTick(0.3d);

        Assert.Contains("EnterMode:Peace", automation.CallLog);
    }

    [Fact]
    public void RandomHelperPreparesThroughTheSharedGateBeforeCasting()
    {
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            ItemEntries = [Item(10, "War Wand", itemType: 0x00008000u)],
            EquipmentItems =
            [
                EquipmentItem(10, "War Wand", itemType: 0x00008000u),
            ],
        };
        automation.CombatSnapshot = automation.CombatSnapshot with
        {
            Mode = PluginCombatMode.Melee,
        };
        automation.KnownSelfBuffs =
        [
            NamedSpell(500, 600, "Armor Other I", 33u),
        ];
        automation.WorldObjects.Add(new PluginWorldObject(
            0x50000009u,
            123u,
            "Fellow",
            PluginObjectClass.Player,
            0u,
            0u,
            0u)
        {
            HasPosition = true,
            Position = NavigationAt(0f).Position,
        });
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.SetMetaOption("RandomHelperBuffs", Truthy(true));

        panel.ToggleCombat();
        for (int tick = 0; tick < 20; tick++)
            panel.OnTick(0.3d);

        int peace = automation.CallLog.IndexOf("EnterMode:Peace");
        int equip = automation.CallLog.IndexOf("Equip:0000000A");
        int magic = automation.CallLog.IndexOf("EnterMode:Magic");
        Assert.True(
            peace >= 0 && equip > peace && magic > equip,
            "RandomHelper did not sequence through the gate. CallLog: "
                + string.Join(" | ", automation.CallLog));
    }

    [Fact]
    public void DeathWithStopMacroOnDeathStopsTheMacroAndChangesNoSetting()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.SetMetaOption("StopMacroOnDeath", Truthy(true));
        panel.SetMetaOption("EnableNav", Truthy(true));
        panel.SetMetaOption("EnableLooting", Truthy(true));
        panel.SetMetaOption("EnableBuffing", Truthy(true));
        panel.SetMetaOption("EnableCombat", Truthy(true));
        panel.ToggleCombat();
        panel.OnTick(0.1d);
        Assert.True(panel.CombatMacroRunning);

        automation.CurrentHealth = 0;
        panel.OnTick(0.1d);

        Assert.False(panel.CombatMacroRunning);
        Assert.True(panel.GetMetaOptionForTest("EnableNav"));
        Assert.True(panel.GetMetaOptionForTest("EnableLooting"));
        Assert.True(panel.GetMetaOptionForTest("EnableBuffing"));
        Assert.True(panel.GetMetaOptionForTest("EnableCombat"));
        Assert.Contains(
            automation.Messages,
            static value => value.Contains(
                "Macro stopped because the character died.",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            automation.Messages,
            static value => value.Contains("deathrestore", StringComparison.Ordinal));
    }

    /// <summary>The reject branch of the same handler: nothing at all.</summary>
    [Fact]
    public void DeathWithStopMacroOnDeathOffChangesNothing()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.SetMetaOption("StopMacroOnDeath", Truthy(false));
        panel.SetMetaOption("EnableNav", Truthy(true));
        panel.ToggleCombat();
        panel.OnTick(0.1d);

        automation.CurrentHealth = 0;
        panel.OnTick(0.1d);

        Assert.True(panel.CombatMacroRunning);
        Assert.True(panel.GetMetaOptionForTest("EnableNav"));
        Assert.DoesNotContain(
            automation.Messages,
            static value => value.Contains("You died!", StringComparison.Ordinal));
    }

    private static AcDream.Plugins.MossTank.Expressions.ExpressionValue Truthy(
        bool value) =>
        AcDream.Plugins.MossTank.Expressions.ExpressionValue.Boolean(value);

    [Fact]
    public void ForceBuffGoesThroughTheModeGateBecauseMEIsConsumableNotForce()
    {
        PluginSpellInfo buff = Spell(
            1, 10, "Increases the caster's Life Magic skill by 10 points.");
        var automation = new CombatCapableFakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs = [buff],
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        // No wand anywhere: the gate's own path here is
        // PostNoWandNoticeAndStop, exactly as ga.cs:1471-1473.
        panel.ToggleCombat();
        panel.ForceBuff();
        for (int tick = 0; tick < 5; tick++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);
        Assert.Contains(
            automation.Messages,
            static value => value.Contains(
                CombatModeGate.NoWandNotice,
                StringComparison.Ordinal));
        Assert.False(panel.CombatMacroRunning);
    }

    [Fact]
    public void TogglingBuffingOffPausesTheForceBuffInsteadOfCancellingIt()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.SetMetaOption("EnableBuffing", Truthy(false));

        panel.ToggleCombat();
        panel.ForceBuff();
        for (int tick = 0; tick < 4; tick++)
            panel.OnTick(0.3d);
        Assert.Empty(automation.CastSpellIds);   // EnableBuffing holds the pass

        panel.SetMetaOption("EnableBuffing", Truthy(true));
        for (int tick = 0; tick < 4; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([1u], automation.CastSpellIds);
    }

    [Fact]
    public void AForceBuffWithTheMacroOffNeitherBuffsNorTopsUpVitals()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 10,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
            // BoosterVital 2 = VitalKind.Health (VitalPlan.cs:7).
            ItemEntries = [Item(60, "Bread", 1) with { BoosterVital = 2 }],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(60u);
        panel.AddSelectedConsumable();

        // The macro stays OFF, so nothing in the main list runs at all.
        panel.ForceBuff();
        for (int tick = 0; tick < 6; tick++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);
        Assert.Empty(automation.UsedItemIds);
    }

    [Fact]
    public void AnItemRowWhoseSelfAuraLandsOnTheCharacterIsCoveredNextPass()
    {
        FakeAutomation automation = ItemEnchantAutomation();
        // One resolvable row, so the loop would be unmistakable.
        automation.KnownSelfBuffs =
        [
            NamedSpell(101, 201, "Aura of Defender Self I", ItemEnchantmentSchoolId)
                with { IsSelfTargeted = true },
        ];
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 20; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([101u], automation.CastSpellIds);

        Assert.Contains(
            automation.ActiveEnchantments,
            held => held.SpellId == 101u);
        Assert.False(
            automation.ItemEnchantments.TryGetValue(10u, out var onWand)
                && onWand.Count > 0,
            "the server never enchants the item with a Self aura");
    }

    [Fact]
    public void AnItemEnchantRowFallsToTheCastableLowerTierLikeTheSelfWalk()
    {
        FakeAutomation automation = ItemEnchantAutomation();
        automation.Skills =
        [
            new PluginSkillInfo(
                ItemEnchantmentSchoolId,
                "Item Enchantment",
                PluginSkillTraining.Trained,
                300),
        ];
        automation.KnownSelfBuffs =
        [
            NamedSpell(106, 201, "Aura of Defender Self VI", ItemEnchantmentSchoolId)
                with
            {
                IsSelfTargeted = true,
                Tier = 6,
                Difficulty = 200,
                FormulaComponentIds = [7u],
            },
            NamedSpell(101, 201, "Aura of Defender Self I", ItemEnchantmentSchoolId)
                with
            {
                IsSelfTargeted = true,
                Tier = 1,
                Difficulty = 10,
                FormulaComponentIds = [8u],
            },
        ];
        automation.Components[7u] = Component(7u, "Lead Scarab");
        automation.Components[8u] = Component(8u, "Pyreal Scarab");
        automation.ItemEntries =
        [
            Item(10, "War Wand", 0x8000u, validLocations: 0x01000000u),
            Item(50, "Pyreal Scarab", 1),
        ];

        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);
        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.NotEmpty(automation.CastSpellIds);
        Assert.All(automation.CastSpellIds, id => Assert.Equal(101u, id));
    }

    [Fact]
    public void AnItemRowPickAndItsLedgerWriteBothSayWhatHappenedOnMisc()
    {
        FakeAutomation automation = ItemEnchantAutomation();
        automation.KnownSelfBuffs =
        [
            NamedSpell(101, 201, "Aura of Defender Self I", ItemEnchantmentSchoolId)
                with { IsSelfTargeted = true },
        ];
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "log Misc on", "/vt log Misc on"));

        host.Selection.Select(10);
        panel.AddSelectedItem();
        automation.Messages.Clear();
        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Contains(
            automation.Messages,
            message => message.Contains(
                "Buffing: item row War Wand (10) \u2192 Aura of Defender Self I",
                StringComparison.Ordinal));
        Assert.Contains(
            automation.Messages,
            message => message.Contains(
                "Cast Aura of Defender Self I on 10 ending at",
                StringComparison.Ordinal)
                && message.Contains("OVERRIDDEN", StringComparison.Ordinal));
    }

    [Fact]
    public void ASkippedBuffFamilyWarnsOnceInChatAndMirrorsToTheHostLog()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 5),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        Command(panel, "log Misc on");

        panel.ToggleCombat();
        for (int tick = 0; tick < 12; tick++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);

        const string Warning =
            "No spell known for class including: Spell 1, buff SKIPPED.";
        Assert.Equal(
            1,
            automation.Messages.Count(
                message => message.Contains(Warning, StringComparison.Ordinal)));
        Assert.Equal(
            1,
            host.Logger.Infos.Count(
                line => line.Contains(Warning, StringComparison.Ordinal)));
    }

    [Fact]
    public void EnablingBuffingWithTheMacroOffCastsNothing()
    {
        var automation = BuffPassAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        Command(panel, "opt set ManaChargesWhenOff true");
        panel.SetMetaOption("EnableBuffing", Truthy(false));
        for (int tick = 0; tick < 5; tick++)
            panel.OnTick(0.3d);

        panel.SetMetaOption("EnableBuffing", Truthy(true));
        for (int tick = 0; tick < 20; tick++)
            panel.OnTick(0.3d);

        Assert.Empty(automation.CastSpellIds);

        // And the macro still starts it, so the pin is not vacuous.
        panel.ToggleCombat();
        for (int tick = 0; tick < 4; tick++)
            panel.OnTick(0.3d);
        Assert.NotEmpty(automation.CastSpellIds);
    }

    [Fact]
    public void WithTheMacroOffOnlyRefillWieldedManaMayRun()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 10,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            // …and a buff due, so BuffSelf would.
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
            ItemEntries = [Item(60, "Bread", 1) with { BoosterVital = 2 }],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(60u);
        panel.AddSelectedConsumable();
        Command(panel, "opt set ManaChargesWhenOff true");

        var ranWithMacroOff = new HashSet<string>(StringComparer.Ordinal);
        for (int tick = 0; tick < 20; tick++)
        {
            panel.OnTick(0.3d);
            foreach (IMacroRule rule in panel.MacroRules)
            {
                if (rule.Running)
                    ranWithMacroOff.Add(rule.Name);
            }
        }

        Assert.All(
            ranWithMacroOff,
            name => Assert.Equal("RefillWieldedMana", name));
        Assert.DoesNotContain("RechargeSelfNormal", ranWithMacroOff);
        Assert.DoesNotContain("BuffSelf", ranWithMacroOff);
        Assert.Empty(automation.CastSpellIds);
        Assert.Empty(automation.UsedItemIds);

        // The same world with the macro ON runs both of them — so the
        // assertions above are about the GATE, not about an empty fixture.
        panel.ToggleCombat();
        var ranWithMacroOn = new HashSet<string>(StringComparer.Ordinal);
        for (int tick = 0; tick < 20; tick++)
        {
            panel.OnTick(0.3d);
            foreach (IMacroRule rule in panel.MacroRules)
            {
                if (rule.Running)
                    ranWithMacroOn.Add(rule.Name);
            }
        }

        Assert.Contains("RechargeSelfNormal", ranWithMacroOn);
        Assert.NotEmpty(automation.UsedItemIds);
    }

    [Fact]
    public void AForceEndsWhenTheStampsItZeroedHaveBeenRecast()
    {
        var automation = BuffPassAutomation();
        // Spells 1 and 2 are up with hours left: due ONLY under the force.
        automation.ActiveEnchantments =
        [
            new PluginActiveEnchantment(1u, 10u, 1, 1800d),
            new PluginActiveEnchantment(2u, 20u, 1, 1800d),
        ];
        // Spell 3 is genuinely due and can never land, so the pick is never
        // empty. It sorts last, so it does not starve the two forced ones.
        automation.RefusedCastSpellIds.Add(3u);

        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleCombat();
        panel.ForceBuff();

        for (int tick = 0; tick < 40; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([1u, 2u], automation.CastSpellIds);

        for (int tick = 0; tick < 20; tick++)
            panel.OnTick(0.3d);
        Assert.Equal([1u, 2u], automation.CastSpellIds);
    }

    [Fact]
    public void CancelForceBuffStopsTheForceOnTheCall()
    {
        var automation = BuffPassAutomation();
        automation.ActiveEnchantments =
        [
            new PluginActiveEnchantment(1u, 10u, 1, 1800d),
            new PluginActiveEnchantment(2u, 20u, 1, 1800d),
            new PluginActiveEnchantment(3u, 30u, 1, 1800d),
        ];
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleCombat();

        panel.ForceBuff();
        panel.OnTick(0.3d);
        int castsBeforeCancel = automation.CastSpellIds.Count;

        panel.CancelForceBuff();

        for (int tick = 0; tick < 20; tick++)
            panel.OnTick(0.3d);
        Assert.Equal(castsBeforeCancel, automation.CastSpellIds.Count);
    }

    [Fact]
    public void FastCastBuffsHoldsForwardOnlyUntilInstantBuffCastEnds()
    {
        var automation = new FakeAutomation
        {
            Skills =
            [
                new PluginSkillInfo(
                    1,
                    "Life Magic",
                    PluginSkillTraining.Trained,
                    300),
            ],
            KnownSelfBuffs =
            [
                Spell(
                    1,
                    10,
                    "Increases the caster's Life Magic skill by 10 points."),
            ],
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        Command(panel, "opt set FastCastBuffs true");

        panel.ToggleCombat();
        panel.ForceBuff();
        panel.OnTick(0d);

        PluginMovementIntent held = Assert.Single(automation.MovementIntents);
        Assert.True(held.Forward);
        Assert.Equal(0, automation.ClearMovementCount);

        panel.OnTick(0.25d);

        Assert.Equal(1, automation.ClearMovementCount);
    }

    [Fact]
    public void FastCastBuffsNeverAppliesForwardMovementToWarSpells()
    {
        var automation = new FakeAutomation
        {
            Skills =
            [
                new PluginSkillInfo(
                    1,
                    "Life Magic",
                    PluginSkillTraining.Trained,
                    300),
            ],
            KnownSelfBuffs =
            [
                Spell(
                    1,
                    10,
                    "Increases the caster's Life Magic skill by 10 points.")
                    with { School = 34u },
            ],
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        Command(panel, "opt set FastCastBuffs true");

        panel.ToggleCombat();
        panel.ForceBuff();
        panel.OnTick(0d);

        Assert.Empty(automation.MovementIntents);
    }

    [Fact]
    public void RetainedLabelReads_UseUpdateSideSnapshotsWithoutAllocating()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 90,
            MaxHealth = 100,
            CurrentStamina = 80,
            MaxStamina = 110,
            CurrentMana = 70,
            MaxMana = 120,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
                new PluginSkillInfo(2, "War Magic", PluginSkillTraining.Specialized, 350),
                new PluginSkillInfo(3, "Run", PluginSkillTraining.Untrained, 100),
            ],
            Attributes =
            [
                new PluginAttributeInfo(0, "Strength", 100),
                new PluginAttributeInfo(1, "Endurance", 100),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.OnTick(0.0);

        Assert.Equal("Health 90/100   Stam 80/110   Mana 70/120", panel.Vitals);
        Assert.Equal("2 attributes, 2 trained skills, 1 buff lines", panel.Coverage);
        string expectedVitals = panel.Vitals;
        string expectedCoverage = panel.Coverage;

        _ = panel.Vitals;
        _ = panel.Coverage;
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool sameReferences = true;
        for (int i = 0; i < 10_000; i++)
        {
            sameReferences &= ReferenceEquals(expectedVitals, panel.Vitals);
            sameReferences &= ReferenceEquals(expectedCoverage, panel.Coverage);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(sameReferences);
        Assert.Equal(0, allocated);
        Assert.Equal(1, automation.KnownSelfBuffReads);
    }

    [Fact]
    public void UpdateTick_RefreshesRareCoverageAndChangedVitals()
    {
        var automation = new FakeAutomation
        {
            CurrentHealth = 90,
            MaxHealth = 100,
            CurrentStamina = 80,
            MaxStamina = 110,
            CurrentMana = 70,
            MaxMana = 120,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            Attributes = [new PluginAttributeInfo(0, "Strength", 100)],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            ],
        };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.OnTick(0.0);

        automation.CurrentHealth = 75;
        automation.Skills =
        [
            new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            new PluginSkillInfo(2, "War Magic", PluginSkillTraining.Specialized, 350),
        ];
        panel.OnTick(0.5);

        Assert.StartsWith("Health 75/100", panel.Vitals, StringComparison.Ordinal);
        Assert.Equal("1 attributes, 1 trained skills, 1 buff lines", panel.Coverage);

        panel.OnTick(0.5);
        Assert.Equal("1 attributes, 2 trained skills, 1 buff lines", panel.Coverage);

        automation.KnownSelfBuffs =
        [
            Spell(1, 10, "Increases the caster's Life Magic skill by 10 points."),
            Spell(2, 11, "Increases the caster's War Magic skill by 10 points."),
        ];
        panel.OnTick(0.0);

        Assert.Equal("1 attributes, 2 trained skills, 2 buff lines", panel.Coverage);
    }

    [Fact]
    public void ItemsAndConsumablesTabsAddTheSelectedOwnedItemToRealProfiles()
    {
        var automation = new FakeAutomation
        {
            ItemEntries =
            [
                Item(10, "Imperil Lens", 0x8000),
                Item(11, "Iron Phial of Imperil", 0x100),
                Item(12, "Black Marrow Pea", 0x20),
            ],
        };
        var host = new FakeHost(automation);
        automation.CurrentSelection = () => host.Selection.SelectedObjectId ?? 0u;
        var panel = new MossTankPanel(host);

        host.Selection.Select(10);
        panel.AddSelectedItem();
        Assert.Contains("Imperil Lens", panel.ItemProfileText, StringComparison.Ordinal);

        host.Selection.Select(11);
        panel.AddSelectedConsumable();
        panel.AddAllPeas();
        Assert.Contains(
            "Iron Phial of Imperil",
            panel.ConsumableProfileText,
            StringComparison.Ordinal);
        Assert.Contains(
            CraftingPlanner.AllPeas,
            panel.ConsumableProfileText,
            StringComparison.Ordinal);

        Assert.Contains("Imperil Lens", panel.ItemRows);
        panel.RemoveSelectedItem();
        Assert.DoesNotContain("Imperil Lens", panel.ItemRows);
        Assert.Equal(2, panel.ConsumableRows.Count);
        panel.RemoveSelectedConsumable();
        Assert.Single(panel.ConsumableRows);
    }

    [Fact]
    public void ItemsGridNameClickDeletesAndHandsClickCyclesHandedness()
    {
        var automation = new FakeAutomation
        {
            ItemEntries =
            [
                Item(10, "Fire Sword", 1),
                Item(11, "Ice Wand", 1),
            ],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(10);
        panel.AddSelectedItem();
        host.Selection.Select(11);
        panel.AddSelectedItem();
        Assert.Equal(["Fire Sword", "Ice Wand"], panel.ItemNameColumn);

        Assert.Equal("Auto", panel.ItemHandsColumn[0]);
        panel.CycleItemHandsAt(0);
        Assert.Equal("1-Handed", panel.ItemHandsColumn[0]);
        panel.CycleItemHandsAt(0);
        Assert.Equal("2-Handed", panel.ItemHandsColumn[0]);
        panel.CycleItemHandsAt(0);
        Assert.Equal("Auto", panel.ItemHandsColumn[0]);
        Assert.Equal("Auto", panel.ItemHandsColumn[1]);

        panel.DeleteItemRowAt(0); // "Fire Sword"
        Assert.Equal(["Ice Wand"], panel.ItemNameColumn);
    }

    [Fact]
    public void ItemHandsColumnDoesNotReallocateOnEveryReadAndNoBuffSuffixNeverLeaks()
    {
        var automation = new FakeAutomation
        {
            ItemEntries = [Item(10, "Fire Sword", 1)],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(10);
        panel.AddSelectedItemNoBuffs();
        Assert.Equal(["Fire Sword   [no buffs]"], panel.ItemNameColumn);

        Assert.Same(panel.ItemHandsColumn, panel.ItemHandsColumn);
        Assert.Equal("Auto", panel.ItemHandsColumn[0]);

        panel.CycleItemHandsAt(0);
        IReadOnlyList<string> afterCycle = panel.ItemHandsColumn;
        Assert.Equal("1-Handed", afterCycle[0]);
        Assert.Same(afterCycle, panel.ItemHandsColumn);
    }

    [Fact]
    public void ConsumablesLeftListRowClickRemovesTheRowDirectly()
    {
        var automation = new FakeAutomation
        {
            ItemEntries =
            [
                Item(20, "Iron Phial of Imperil", 0x100),
                Item(21, "Black Marrow Pea", 0x20),
            ],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(20);
        panel.AddSelectedConsumable();
        host.Selection.Select(21);
        panel.AddSelectedConsumable();
        Assert.Equal(2, panel.ConsumableRows.Count);

        panel.SelectConsumableRow(0);

        Assert.Single(panel.ConsumableRows);
    }

    [Fact]
    public void ExcludedComponentsGridAddsBySelectionAndDeletesByAnyCellClick()
    {
        var automation = new FakeAutomation
        {
            ItemEntries =
            [
                Item(20, "Charged Yellow Scarab", 0x20)
                    with { IconId = 0x06001234u },
            ],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        Assert.Empty(panel.ExcludedComponentRows);
        host.Selection.Select(20);
        panel.AddSelectedComponent();
        Assert.Equal(["Charged Yellow Scarab"], panel.ExcludedComponentRows);
        Assert.Equal(0x06001234u, panel.ExcludedComponentIcons[0]);

        panel.AddSelectedComponent();
        Assert.Equal(["Charged Yellow Scarab"], panel.ExcludedComponentRows);

        panel.DeleteExcludedComponentAt(0);
        Assert.Empty(panel.ExcludedComponentRows);
    }

    [Fact]
    public void ExcludedComponentIconsDoesNotScanLiveInventoryOnEveryRead()
    {
        var automation = new FakeAutomation
        {
            ItemEntries =
            [
                Item(20, "Charged Yellow Scarab", 0x20) with { IconId = 0x06001234u },
            ],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(20);
        panel.AddSelectedComponent();
        Assert.Equal(0x06001234u, panel.ExcludedComponentIcons[0]);

        int callsAfterAdd = automation.CaptureOwnedItemsCallCount;
        for (int i = 0; i < 5; i++)
            _ = panel.ExcludedComponentIcons;

        Assert.Equal(callsAfterAdd, automation.CaptureOwnedItemsCallCount);
    }

    [Fact]
    public void ExtraBuffAndBlacklistedFamilyNamesPersistAcrossSessions()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation
        {
            Name = "Persist Check",
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Strength by 10 points."),
                Spell(2, 20, "Increases the caster's Focus by 10 points."),
            ],
        };
        var first = new MossTankPanel(new FakeHost(automation, storage));
        first.ShowExtraBuffPicker();
        first.PickBuffAt(0); // "Spell 1"
        first.ShowBlacklistedBuffPicker();
        first.PickBuffAt(1); // "Spell 2"
        Assert.Equal(["Spell 1"], first.ExtraBuffRows);
        Assert.Equal(["Spell 2"], first.BlacklistedBuffFamilyRows);

        var second = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Persist Check" }, storage));

        Assert.Equal(["Spell 1"], second.ExtraBuffRows);
        Assert.Equal(["Spell 2"], second.BlacklistedBuffFamilyRows);
    }

    [Fact]
    public void TheVtLogChannelSelectionSurvivesAReload()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation { Name = "Log Persist" };
        var first = new MossTankPanel(new FakeHost(automation, storage));

        Command(first, "log Misc on");
        Command(first, "log SpellCast on");
        Command(first, "log RuleInfo on");
        Command(first, "log RuleInfo off");

        var reloaded = new FakeAutomation { Name = "Log Persist" };
        var second = new MossTankPanel(new FakeHost(reloaded, storage));
        Command(second, "log");

        string state = Assert.Single(
            reloaded.Messages,
            message => message.StartsWith("Log state:", StringComparison.Ordinal));
        Assert.Equal("Log state:  Misc SpellCast", state);
    }

    [Fact]
    public void BuffPickerAddsToTheRequestedListAndAnyCellClickDeletes()
    {
        var automation = new FakeAutomation
        {
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Strength by 10 points."),
                Spell(2, 20, "Increases the caster's Focus by 10 points."),
            ],
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        Assert.False(panel.BuffPickerVisible);
        panel.ShowExtraBuffPicker();
        Assert.True(panel.BuffPickerVisible);
        Assert.Equal(2, panel.BuffPickerRows.Count);

        panel.SetBuffPickerSearchText("Spell 1");
        Assert.Equal(["Spell 1"], panel.BuffPickerRows);
        panel.PickBuffAt(0);
        Assert.False(panel.BuffPickerVisible);
        Assert.Equal(["Spell 1"], panel.ExtraBuffRows);
        Assert.Empty(panel.BlacklistedBuffFamilyRows);

        panel.ShowBlacklistedBuffPicker();
        panel.SetBuffPickerSearchText(string.Empty);
        panel.PickBuffAt(1); // "Spell 2" (sorted after "Spell 1")
        Assert.Equal(["Spell 2"], panel.BlacklistedBuffFamilyRows);

        panel.DeleteExtraBuffAt(0);
        Assert.Empty(panel.ExtraBuffRows);
        panel.DeleteBlacklistedBuffFamilyAt(0);
        Assert.Empty(panel.BlacklistedBuffFamilyRows);
    }

    [Fact]
    public void ItemProfilesPersistThroughHostScopedStorage()
    {
        var storage = new MemoryStorage();
        var firstAutomation = new FakeAutomation
        {
            ItemEntries = [Item(10, "Imperil Lens", 0x8000)],
        };
        var firstHost = new FakeHost(firstAutomation, storage);
        var first = new MossTankPanel(firstHost);
        firstHost.Selection.Select(10);
        first.AddSelectedItem();

        var second = new MossTankPanel(new FakeHost(
            new FakeAutomation(),
            storage));

        Assert.Contains("Imperil Lens", second.ItemProfileText, StringComparison.Ordinal);
        Assert.Contains(
            storage.Text.Keys,
            key => key.StartsWith("profiles/macro/", StringComparison.Ordinal));
    }

    [Fact]
    public void VitalThresholdsPersistWithTheProfile()
    {
        var storage = new MemoryStorage();
        var first = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));
        first.SetNormalHealth(0.42f);
        first.SetNoTargetMana(0.88f);
        first.ToggleHelpOthers();

        var second = new MossTankPanel(
            new FakeHost(new FakeAutomation(), storage));

        Assert.Equal(0.42f, second.NormalHealthValue, precision: 2);
        Assert.Equal(0.88f, second.NoTargetManaValue, precision: 2);
        Assert.False(second.HelpOthersEnabled);
    }

    [Fact]
    public void AutoStackAndAutoCramUseVtankDefaultsAndPersistPerProfile()
    {
        var storage = new MemoryStorage();
        var first = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        Assert.True(first.AutoStackEnabled);
        Assert.False(first.AutoCramEnabled);
        Assert.True(first.AutoCraftItemsEnabled);
        Assert.True(first.RefillWornManaEnabled);
        first.ToggleAutoStack();
        first.ToggleAutoCram();
        first.ToggleAutoCraftItems();
        first.ToggleRefillWornMana();
        first.SetRefillWornMana(0.44f);

        var second = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));
        Assert.False(second.AutoStackEnabled);
        Assert.True(second.AutoCramEnabled);
        Assert.False(second.AutoCraftItemsEnabled);
        Assert.False(second.RefillWornManaEnabled);
        Assert.Equal(0.44f, second.RefillWornManaValue, precision: 2);
    }

    [Fact]
    public void VitalsPercentWrappersReadAndWriteTheSameFieldAsTheZeroToOnePair()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), new MemoryStorage()));

        panel.SetNormalHealth(0.42f);
        Assert.Equal(42f, panel.NormalHealthPercent, precision: 2);

        panel.SetHelperManaPercent(65f);
        Assert.Equal(0.65f, panel.HelperManaValue, precision: 3);
        Assert.Equal(65f, panel.HelperManaPercent, precision: 2);
    }

    [Fact]
    public void FastCastProjectileAwarenessAndDebuffFallbackToggleTheirVtankDefaultsAndPersist()
    {
        var storage = new MemoryStorage();
        var first = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        Assert.False(first.FastCastBuffsEnabled);
        Assert.True(first.DontShootAtWallsEnabled);
        Assert.False(first.DebuffFallbackEnabled);

        first.ToggleFastCastBuffs();
        first.ToggleDontShootAtWalls();
        first.ToggleDebuffFallback();

        Assert.True(first.FastCastBuffsEnabled);
        Assert.False(first.DontShootAtWallsEnabled);
        Assert.True(first.DebuffFallbackEnabled);

        var second = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));
        Assert.True(second.FastCastBuffsEnabled);
        Assert.False(second.DontShootAtWallsEnabled);
        Assert.True(second.DebuffFallbackEnabled);
    }

    [Fact]
    public void ToggleAdvancedOptionsAndLootEditorVisibilityFlipBothWays()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), new MemoryStorage()));

        Assert.False(panel.AdvancedOptionsVisible);
        panel.ToggleAdvancedOptionsVisible();
        Assert.True(panel.AdvancedOptionsVisible);
        panel.ToggleAdvancedOptionsVisible();
        Assert.False(panel.AdvancedOptionsVisible);

        Assert.False(panel.LootEditorVisible);
        panel.ToggleLootEditorVisible();
        Assert.True(panel.LootEditorVisible);
        panel.ToggleLootEditorVisible();
        Assert.False(panel.LootEditorVisible);
    }

    [Fact]
    public void AdvancedOptionCategoryNamesShowRealNamesNotHexBitmasks()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        Assert.Equal(
            [
                "Misc", "Recharge", "MeleeCombat", "SpellCombat", "Ranges",
                "Navigation", "Buffing", "Crafting", "Looting",
            ],
            panel.AdvancedOptionCategoryNames);
        Assert.DoesNotContain(
            panel.AdvancedOptionCategoryNames, name => name.StartsWith("0x", StringComparison.Ordinal));
    }

    [Fact]
    public void AdvancedOptionCategoryFilterHidesNonMatchingSettings()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        Assert.Contains("EnableLooting", panel.AdvancedOptionNames);
        Assert.Contains("EnableNav", panel.AdvancedOptionNames);

        for (int i = 0; i < panel.AdvancedOptionCategoryEnabled.Count; i++)
            if (VtankOptionCatalog.CategoryBits[i] != 0x100)
                panel.ToggleAdvancedOptionCategoryAt(i);

        Assert.Contains("EnableLooting", panel.AdvancedOptionNames);
        Assert.DoesNotContain("EnableNav", panel.AdvancedOptionNames);

        // Re-enabling everything restores the full list.
        for (int i = 0; i < panel.AdvancedOptionCategoryEnabled.Count; i++)
            if (!panel.AdvancedOptionCategoryEnabled[i])
                panel.ToggleAdvancedOptionCategoryAt(i);
        Assert.Contains("EnableNav", panel.AdvancedOptionNames);
    }

    [Fact]
    public void AdvancedOptionListHidesTStringSettingsButKeepsTheirEnumCounterparts()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        Assert.DoesNotContain("BuffProfile-Prots", panel.AdvancedOptionNames);
        Assert.DoesNotContain("BuffProfile-Banes", panel.AdvancedOptionNames);
        Assert.DoesNotContain("BlacklistedSpellComps", panel.AdvancedOptionNames);

        Assert.Contains("BuffProfile_Prots", panel.AdvancedOptionNames);
        Assert.Contains("BuffProfile_Banes", panel.AdvancedOptionNames);
    }

    [Fact]
    public void AdvancedOptionCategoryEnabledIsNotTheMutableBackingArray()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        IReadOnlyList<bool> categoryEnabled = panel.AdvancedOptionCategoryEnabled;

        Assert.Throws<InvalidCastException>(() => (bool[])categoryEnabled);
    }

    [Fact]
    public void AdvancedOptionDescriptionSurfacesRealRetailHelpText()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        int index = panel.AdvancedOptionNames.ToList().IndexOf("DoHelp");
        Assert.True(index >= 0);

        panel.SelectAdvancedOption(index);

        Assert.StartsWith("DoHelp:", panel.AdvancedOptionDescription, StringComparison.Ordinal);
        Assert.Contains("fellowship", panel.AdvancedOptionDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdvancedOptionValueColumnMirrorsTheLiveSettingValue()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        int index = panel.AdvancedOptionNames.ToList().IndexOf("EnableCombat");
        Assert.True(index >= 0);
        string before = panel.AdvancedOptionValueColumn[index];
        Assert.Equal(panel.CombatEnabled ? "True" : "False", before);

        panel.ToggleCombatEnabled();
        panel.SelectAdvancedOption(index);

        string after = panel.AdvancedOptionValueColumn[index];
        Assert.NotEqual(before, after);
        Assert.Equal(panel.CombatEnabled ? "True" : "False", after);
    }

    [Fact]
    public void ShowAdvancedOptionsRefreshesValuesChangedWhileThePopupWasClosed()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        int index = panel.AdvancedOptionNames.ToList().IndexOf("EnableCombat");
        Assert.True(index >= 0);
        string before = panel.AdvancedOptionValueColumn[index];
        Assert.Equal(panel.CombatEnabled ? "True" : "False", before);

        panel.ToggleCombatEnabled();
        panel.ShowAdvancedOptions();

        string after = panel.AdvancedOptionValueColumn[index];
        Assert.NotEqual(before, after);
        Assert.Equal(panel.CombatEnabled ? "True" : "False", after);
    }

    [Fact]
    public void AdvancedOptionsPopupBindingsDoNotReallocateOnEveryRead()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        Assert.Same(panel.AdvancedOptionNames, panel.AdvancedOptionNames);
        Assert.Same(panel.AdvancedOptionValueColumn, panel.AdvancedOptionValueColumn);

        int index = panel.AdvancedOptionNames.ToList().IndexOf("EnableCombat");
        Assert.True(index >= 0);
        panel.SelectAdvancedOption(index);

        IReadOnlyList<string> namesAfterSelect = panel.AdvancedOptionNames;
        IReadOnlyList<string> valuesAfterSelect = panel.AdvancedOptionValueColumn;
        // ...but once settled, repeated reads must again share one instance.
        Assert.Same(namesAfterSelect, panel.AdvancedOptionNames);
        Assert.Same(valuesAfterSelect, panel.AdvancedOptionValueColumn);
    }

    [Fact]
    public void ClickAdvancedOptionValue_OnBoolRow_FlipsInPlace()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        int index = panel.AdvancedOptionNames.ToList().IndexOf("EnableCombat");
        Assert.True(index >= 0);
        bool before = panel.CombatEnabled;

        panel.ClickAdvancedOptionValue(index);

        Assert.Equal(!before, panel.CombatEnabled);
        Assert.Equal(panel.CombatEnabled ? "True" : "False", panel.AdvancedOptionValueColumn[index]);

        panel.ClickAdvancedOptionValue(index);
        Assert.Equal(before, panel.CombatEnabled);
    }

    [Fact]
    public void ClickAdvancedOptionValue_OnEnumRow_CyclesToTheNextLabelAndWraps()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        int index = panel.AdvancedOptionNames.ToList().IndexOf("UseArcs");
        Assert.True(index >= 0);

        panel.SelectAdvancedOption(index);
        panel.SubmitAdvancedOption("1");
        Assert.Equal("No", panel.AdvancedOptionValueColumn[index]);

        panel.ClickAdvancedOptionValue(index);
        Assert.Equal("At Range", panel.AdvancedOptionValueColumn[index]);

        panel.ClickAdvancedOptionValue(index);
        Assert.Equal("Yes", panel.AdvancedOptionValueColumn[index]);

        panel.ClickAdvancedOptionValue(index);
        Assert.Equal("No", panel.AdvancedOptionValueColumn[index]);
    }

    [Fact]
    public void ClickAdvancedOptionValue_OnNumericRow_SelectsAndLoadsTheEditField()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        int index = panel.AdvancedOptionNames.ToList().IndexOf("SpellDiffExcessThreshold-Hunt");
        Assert.True(index >= 0);

        panel.ClickAdvancedOptionValue(index);

        Assert.Equal(index, panel.SelectedAdvancedOptionIndex);
        Assert.Equal(panel.AdvancedOptionValueColumn[index], panel.AdvancedOptionValueDraft);
    }

    [Fact]
    public void CombatMacroRunningReflectsTheSameStateAsCombatButtonText()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), new MemoryStorage()));

        Assert.False(panel.CombatMacroRunning);
        Assert.Equal("Run Macro", panel.CombatButtonText);

        panel.ToggleCombat();

        Assert.True(panel.CombatMacroRunning);
        Assert.Equal("Stop Macro", panel.CombatButtonText);
    }

    [Fact]
    public void LootingUsesVtankDefaultsAndPersistsTheOrderedRuleEditor()
    {
        var storage = new MemoryStorage();
        var first = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        Assert.False(first.LootEnabled);
        Assert.False(first.LootPriorityBoostEnabled);
        Assert.Empty(first.LootRuleRows);
        first.ToggleLooting();
        first.ToggleLootPriorityBoost();
        first.AddLootRule();
        first.SetLootExpressionDraft("name ~= coin && value >= 10");
        first.ApplyLootRule();
        first.SelectLootAction(nameof(LootAction.KeepUpTo));
        first.LootKeepCountUp();
        first.LootPriorityUp();
        first.LootRangeDown();

        var second = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));
        Assert.True(second.LootEnabled);
        Assert.True(second.LootPriorityBoostEnabled);
        Assert.Single(second.LootRuleRows);
        Assert.Contains("KeepUpTo", second.LootRuleRows[0], StringComparison.Ordinal);
        Assert.Contains("name ~= coin", second.LootRuleRows[0], StringComparison.Ordinal);
        Assert.Equal("Keep up to 2", second.LootKeepCountText);
        Assert.Equal("Priority 1", second.LootPriorityText);
        Assert.Equal("Corpse range 2m", second.LootRangeText);
    }

    private static string LegacyLootProfileKey(string value, bool byCharacter)
    {
        string identity = (byCharacter ? "char:" : "named:") + value.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)));
        return $"profiles/loot/{hash}.json";
    }

    [Fact]
    public void LootRosterSweepConvertsByCharacterAndEveryNamedLegacyProfileOnce()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation { Name = "Barris" };
        storage.Text[LegacyLootProfileKey("Barris", byCharacter: true)] = """
            {
              "Rules": [
                { "Name": "Coins", "Expression": "name ~= coin", "Action": 0, "Priority": 3 }
              ]
            }
            """;
        storage.Text["profiles/loot/index.json"] = """
            {
              "Version": 1,
              "Names": ["Farming"],
              "SelectedByCharacter": {}
            }
            """;
        storage.Text[LegacyLootProfileKey("Farming", byCharacter: false)] = """
            {
              "Rules": [
                { "Name": "Salvage", "Expression": "name ~= salvage", "Action": 0, "Priority": 1 }
              ]
            }
            """;

        var panel = new MossTankPanel(new FakeHost(automation, storage));

        string byCharacterFile = VtankProfileDirectory.AutoCharacterFileName(
            "Barris", string.Empty, "utl");
        Assert.True(storage.Text.ContainsKey(byCharacterFile));
        Assert.True(storage.Text.ContainsKey("Farming.utl"));
        Assert.False(storage.Text.ContainsKey(LegacyLootProfileKey("Barris", byCharacter: true)));
        Assert.False(storage.Text.ContainsKey(LegacyLootProfileKey("Farming", byCharacter: false)));
        Assert.False(storage.Text.ContainsKey("profiles/loot/index.json"));

        Assert.Contains("name ~= coin", Assert.Single(panel.LootRuleRows), StringComparison.Ordinal);

        panel.SelectLootProfile("Farming");
        Assert.Contains("name ~= salvage", Assert.Single(panel.LootRuleRows), StringComparison.Ordinal);

        // Idempotent: a fresh panel against the same storage sweeps nothing
        // more (there is no roster key left to read) and keeps both values.
        var reloaded = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Barris" }, storage));
        Assert.Contains("name ~= coin", Assert.Single(reloaded.LootRuleRows), StringComparison.Ordinal);
    }

    [Fact]
    public void LootProfilesAreIndependentNamedDocuments()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Looter" },
            storage));
        panel.AddLootRule();
        panel.SetLootExpressionDraft("name ~= coin");
        panel.ApplyLootRule();
        panel.SetLootProfileNameDraft("Currency");
        panel.CopyLootProfile();
        panel.AddLootRule();

        Assert.Equal("Currency", panel.LootProfileName);
        Assert.Equal(2, panel.LootRuleRows.Count);
        panel.SelectLootProfile(MossTankLootProfileStore.ByCharacter);
        Assert.Single(panel.LootRuleRows);
        panel.SelectLootProfile("Currency");
        Assert.Equal(2, panel.LootRuleRows.Count);
        Assert.True(storage.Text.ContainsKey("Currency.utl"));
    }

    [Fact]
    public void LootClassifierSelectionIsVisibleAndPersistsWithMacroProfile()
    {
        var storage = new MemoryStorage();
        var classifiers = new FakeLootClassifierRegistry(
            new PluginLootClassifierInfo("utility/loot", "Utility Loot"));
        var panel = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Looter" },
            storage,
            classifiers));

        Assert.Contains("Utility Loot [utility/loot]", panel.LootClassifierNames);
        panel.SelectLootClassifier("Utility Loot [utility/loot]");

        Assert.Equal(
            "Utility Loot [utility/loot]",
            panel.SelectedLootClassifier);
        var restored = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Looter" },
            storage,
            classifiers));
        Assert.Equal(
            "Utility Loot [utility/loot]",
            restored.SelectedLootClassifier);

        restored.SelectLootClassifier("VTClassic");
        Assert.Equal("VTClassic", restored.SelectedLootClassifier);
    }

    [Fact]
    public void VtankRecoveryAndFakeImperilCommandsHaveRealLocalSemantics()
    {
        var automation = new FakeAutomation
        {
            BusyReferences = 2,
            WorldObjects =
            [
                new PluginWorldObject(
                    0x50000001u,
                    123u,
                    "Drudge",
                    PluginObjectClass.Monster,
                    0u,
                    0u,
                    0u),
            ],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        Command(panel, "clearbusy");
        Assert.Equal(1, automation.BusyReferences);
        Assert.Contains(automation.Messages,
            text => text.Contains("2 -> 1", StringComparison.Ordinal));

        Command(panel, "clearlocks");
        Assert.Contains(automation.Messages,
            text => text.Contains("Action locks cleared", StringComparison.Ordinal));

        host.Selection.Select(0x50000001u);
        Command(panel, "fakeimp");
        Assert.Contains(automation.Messages,
            text => text.Contains("Fake cast complete", StringComparison.Ordinal));
    }

    [Fact]
    public void LootCommandsImportAndExportExactVtclassicUtlFiles()
    {
        var storage = new MemoryStorage();
        var legacy = new VtankLootProfile
        {
            Rules =
            [
                new LootRule
                {
                    Name = "Pyreal",
                    Action = LootAction.KeepUpTo,
                    KeepCount = 100,
                    Priority = 7,
                    VtankRequirements =
                    [
                        new VtankLootRequirement
                        {
                            Type = 1,
                            Payload = "^Pyreal$\r\n1\r\n",
                        },
                    ],
                },
            ],
            SalvageCombine = new VtankSalvageCombineSettings
            {
                DefaultCombineString = "1-5, 6-10",
                MaterialCombineStrings = new Dictionary<int, string>
                {
                    [61] = "1-10",
                },
                MaterialValueModeValues = new Dictionary<int, int>
                {
                    [61] = 75_000,
                },
            },
        };
        storage.Text["imports/Legacy.utl"] =
            VtankLootProfileSerializer.Write(legacy);
        var panel = new MossTankPanel(new FakeHost(
            new FakeAutomation(),
            storage));

        Command(panel, "loot load Legacy.utl");

        Assert.Equal("Legacy", panel.LootProfileName);
        Assert.Single(panel.LootRuleRows);
        Assert.Contains("KeepUpTo", panel.LootRuleRows[0], StringComparison.Ordinal);
        string exported = storage.Text["Legacy.utl"];
        Assert.True(VtankLootProfileSerializer.TryRead(
            exported,
            out VtankLootProfile roundTrip,
            out string error), error);
        Assert.Equal("1-5, 6-10", roundTrip.SalvageCombine.DefaultCombineString);
        Assert.Equal(75_000, roundTrip.SalvageCombine.MaterialValueModeValues[61]);
        Assert.Equal(1, Assert.Single(roundTrip.Rules).VtankRequirements[0].Type);
    }

    [Fact]
    public void NamedProfileCopyHotLoadsWithoutMixingByCharacterSettings()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation { Name = "Moss Wart" };
        var panel = new MossTankPanel(new FakeHost(automation, storage));
        panel.SetNormalHealth(0.42f);
        panel.SetProfileNameDraft("Fellowship");
        panel.CopyProfile();

        const string fellowshipFile = "--Moss Wart__Fellowship.usd";
        Assert.Equal(fellowshipFile, panel.SelectedMacroProfile);
        Assert.Contains(fellowshipFile, panel.MacroProfileNames);
        panel.SetNormalHealth(0.88f);
        panel.SelectMacroProfile(MossTankProfileStore.ByCharacter);

        Assert.Equal(0.42f, panel.NormalHealthValue, precision: 2);
        panel.SelectMacroProfile("Fellowship");
        Assert.Equal(fellowshipFile, panel.SelectedMacroProfile);
        Assert.Equal(0.88f, panel.NormalHealthValue, precision: 2);
    }

    [Fact]
    public void DeleteProfileRemovesTheRealFileAndFallsBackToByCharacter()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation { Name = "Moss Wart" };
        var panel = new MossTankPanel(new FakeHost(automation, storage));
        panel.SetNormalHealth(0.42f);
        panel.SetProfileNameDraft("Fellowship");
        panel.CopyProfile();
        const string fellowshipFile = "--Moss Wart__Fellowship.usd";
        Assert.Equal(fellowshipFile, panel.SelectedMacroProfile);
        Assert.Contains(fellowshipFile, panel.MacroProfileNames);

        panel.DeleteProfile();

        Assert.Equal(MossTankProfileStore.ByCharacter, panel.SelectedMacroProfile);
        Assert.DoesNotContain(fellowshipFile, panel.MacroProfileNames);
        Assert.False(storage.Text.ContainsKey(fellowshipFile));
        Assert.Contains("Deleted", panel.ProfileLifecycleNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteProfileRefusesToRemoveByCharacter()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        panel.DeleteProfile();

        Assert.Equal(MossTankProfileStore.ByCharacter, panel.SelectedMacroProfile);
        Assert.Contains("cannot be deleted", panel.ProfileLifecycleNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteRouteProfileRemovesTheRealFileAndFallsBackToByCharacter()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));
        panel.SetRouteProfileNameDraft("Fellowship");
        panel.CopyRouteProfile();
        Assert.Equal("Fellowship", panel.SelectedRouteProfile);
        Assert.Contains("Fellowship", panel.RouteProfileNames);
        Assert.True(storage.Text.ContainsKey("navs/Fellowship.af"));

        panel.DeleteRouteProfile();

        Assert.Equal(MossTankRouteProfileStore.ByCharacter, panel.SelectedRouteProfile);
        Assert.DoesNotContain("Fellowship", panel.RouteProfileNames);
        Assert.False(storage.Text.ContainsKey("navs/Fellowship.af"));
        Assert.Contains("Deleted", panel.RouteNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteRouteProfileRefusesToRemoveByCharacter()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        panel.DeleteRouteProfile();

        Assert.Equal(MossTankRouteProfileStore.ByCharacter, panel.SelectedRouteProfile);
        Assert.Contains("cannot be deleted", panel.RouteNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteMetaProfileRemovesTheRealFileAndFallsBackToByCharacter()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));
        panel.SetMetaProfileNameDraft("Fellowship");
        panel.CopyMetaProfile();
        Assert.Equal("Fellowship", panel.SelectedMetaProfile);
        Assert.Contains("Fellowship", panel.MetaProfileNames);
        Assert.True(storage.Text.ContainsKey("metas/Fellowship.af"));

        panel.DeleteMetaProfile();

        Assert.Equal(MossTankMetaProfileStore.ByCharacter, panel.SelectedMetaProfile);
        Assert.DoesNotContain("Fellowship", panel.MetaProfileNames);
        Assert.False(storage.Text.ContainsKey("metas/Fellowship.af"));
        Assert.Contains("Deleted", panel.MetaNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteMetaProfileRefusesToRemoveByCharacter()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        panel.DeleteMetaProfile();

        Assert.Equal(MossTankMetaProfileStore.ByCharacter, panel.SelectedMetaProfile);
        Assert.Contains("cannot be deleted", panel.MetaNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteLootProfileRemovesTheRealFileAndFallsBackToByCharacter()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));
        panel.SetLootProfileNameDraft("Fellowship");
        panel.CopyLootProfile();
        Assert.Equal("Fellowship", panel.LootProfileName);
        Assert.Contains("Fellowship", panel.LootProfileNames);

        panel.DeleteLootProfile();

        Assert.Equal(MossTankLootProfileStore.ByCharacter, panel.LootProfileName);
        Assert.DoesNotContain("Fellowship", panel.LootProfileNames);
        Assert.False(storage.Text.ContainsKey("Fellowship.utl"));
        Assert.Contains("Deleted", panel.LootEditorNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteLootProfileRefusesToRemoveByCharacter()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        panel.DeleteLootProfile();

        Assert.Equal(MossTankLootProfileStore.ByCharacter, panel.LootProfileName);
        Assert.Contains("cannot be deleted", panel.LootEditorNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void NavCommandsImportLegacyAndExportAf()
    {
        var storage = new MemoryStorage();
        storage.Text["imports/Legacy.nav"] = """
            uTank2 NAV 1.2
            4
            1
            0
            12.5
            -3.25
            0
            0
            """;
        var panel = new MossTankPanel(new FakeHost(
            new FakeAutomation(),
            storage));

        Command(panel, "nav load Legacy.nav");

        Assert.Equal("Legacy", panel.SelectedRouteProfile);
        Assert.Single(panel.RouteRows);
        Assert.Contains("12.5", panel.RouteRows[0], StringComparison.Ordinal);

        Command(panel, "nav save Exported.nav");
        Assert.Contains(
            "NAV: ",
            storage.Text["navs/Exported.af"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void NavSaveAcceptsAnAfSuffixedNameWithoutDoublingIt()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        Command(panel, "nav save Foo.af");

        Assert.True(storage.Text.ContainsKey("navs/Foo.af"));
        Assert.False(storage.Text.ContainsKey("navs/Foo.af.af"));
    }

    [Fact]
    public void MetaAndRouteProfilesWithTheSameNameDoNotCollide()
    {
        var storage = new MemoryStorage();
        storage.Text["imports/Same.met"] =
            "1\r\nCondAct\r\n5\r\nCType\r\nAType\r\nCData\r\nAData\r\nState\r\n"
            + "n\r\nn\r\nn\r\nn\r\nn\r\n1\r\n"
            + "i\r\n1\r\ni\r\n2\r\ni\r\n0\r\ns\r\n/say imported\r\n"
            + "s\r\nDefault\r\n";
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        Command(panel, "meta load Same.met");
        Command(panel, "meta save Same.met");
        Command(panel, "nav save Same.nav");

        Assert.True(storage.Text.ContainsKey("metas/Same.af"));
        Assert.True(storage.Text.ContainsKey("navs/Same.af"));
        Assert.Contains(
            "STATE: ",
            storage.Text["metas/Same.af"],
            StringComparison.Ordinal);
        Assert.Contains(
            "NAV: ",
            storage.Text["navs/Same.af"],
            StringComparison.Ordinal);
    }

    // Item J (slice-1 fix round): renamed from
    // MetaCommandsImportAndExportExactVtankMetFiles for the same reason as
    // the nav test above.
    [Fact]
    public void MetaCommandsImportLegacyAndExportAf()
    {
        var storage = new MemoryStorage();
        storage.Text["imports/Legacy.met"] =
            "1\r\nCondAct\r\n5\r\nCType\r\nAType\r\nCData\r\nAData\r\nState\r\n"
            + "n\r\nn\r\nn\r\nn\r\nn\r\n1\r\n"
            + "i\r\n1\r\ni\r\n2\r\ni\r\n0\r\ns\r\n/say imported\r\n"
            + "s\r\nDefault\r\n";
        var panel = new MossTankPanel(new FakeHost(
            new FakeAutomation(),
            storage));

        Command(panel, "meta load Legacy.met");

        Assert.Equal("Legacy", panel.SelectedMetaProfile);
        Assert.Single(panel.MetaRows);
        Assert.Contains("/say imported", panel.MetaRows[0], StringComparison.Ordinal);

        Command(panel, "meta save Exported.met");
        Assert.Contains(
            "STATE: ",
            storage.Text["metas/Exported.af"],
            StringComparison.Ordinal);
        Assert.True(MetafSerializer.TryLoadMeta(
            storage.Text["metas/Exported.af"],
            NoOpSpellCatalogForExport.Instance,
            out MetaProfile exported,
            out string error), error);
        Assert.Single(exported.Rules);
    }

    [Fact]
    public void MetaSaveAcceptsAnAfSuffixedNameWithoutDoublingIt()
    {
        var storage = new MemoryStorage();
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        Command(panel, "meta save Foo.af");

        Assert.True(storage.Text.ContainsKey("metas/Foo.af"));
        Assert.False(storage.Text.ContainsKey("metas/Foo.af.af"));
    }

    private sealed class NoOpSpellCatalogForExport : ISpellCatalog
    {
        public static NoOpSpellCatalogForExport Instance { get; } = new();
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => [];
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            info = default;
            return false;
        }
    }

    [Fact]
    public void ByCharacterProfilesAreIsolatedByCharacterName()
    {
        var storage = new MemoryStorage();
        var first = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "One" },
            storage));
        first.SetNormalHealth(0.33f);

        var second = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Two" },
            storage));

        Assert.Equal(0.75f, second.NormalHealthValue, precision: 2);
    }

    [Fact]
    public void MonstersGridMutationsPersistAcrossSessions()
    {
        var storage = new MemoryStorage();
        var first = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Rule Maker" },
            storage));

        first.SetMonsterExpressionDraft("species==drudge");
        first.AddMonsterRule(); // row 1: "species==drudge"
        first.ToggleMonsterImperilAt(1);
        first.CycleMonsterDamageAt(1); // Auto -> Void Basic (MonsterDamageCycle order)
        first.CycleMonsterPriorityAt(1); // 0 -> 1

        Assert.Equal(["DEFAULT", "species==drudge"], first.MonsterNameColumn);
        Assert.True(first.MonsterImperilColumn[1]);
        Assert.Equal("Void Basic", first.MonsterDamageColumn[1]);
        Assert.Equal("1", first.MonsterPriorityColumn[1]);
        Assert.Equal(string.Empty, first.MonsterExpressionDraft);

        var second = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Rule Maker" },
            storage));

        Assert.Equal(
            [MonsterRule.RetailDefaultName, "species==drudge"],
            second.MonsterNameColumn);
        Assert.True(second.MonsterImperilColumn[1]);
        Assert.Equal("Void Basic", second.MonsterDamageColumn[1]);
        Assert.Equal("1", second.MonsterPriorityColumn[1]);
    }

    [Fact]
    public void ToggleMonsterFlagAtWritesOnlyTheTargetedRow()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        panel.AddMonsterRule(); // row 1: "New monster" (empty draft default)

        Assert.False(panel.MonsterImperilColumn[0]);
        Assert.False(panel.MonsterImperilColumn[1]);

        panel.ToggleMonsterImperilAt(1);

        Assert.False(panel.MonsterImperilColumn[0]);
        Assert.True(panel.MonsterImperilColumn[1]);

        panel.ToggleMonsterImperilAt(1);
        Assert.False(panel.MonsterImperilColumn[1]);
    }

    [Fact]
    public void CycleMonsterPriorityAtWrapsExactlyNegativeOneThroughFour()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        Assert.Equal("0", panel.MonsterPriorityColumn[0]); // DEFAULT starts at 0

        foreach (string next in new[] { "1", "2", "3", "4", "-1", "0" })
        {
            panel.CycleMonsterPriorityAt(0);
            Assert.Equal(next, panel.MonsterPriorityColumn[0]);
        }
    }

    [Fact]
    public void MonsterDamageColumnsCycleInTheExactRetailOrder()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        AssertCyclesExactly(
            () => panel.MonsterDamageColumn[0],
            () => panel.CycleMonsterDamageAt(0),
            [
                "Pierce", "Bludgeon", "Slash", "Acid", "Lightning", "Cold",
                "Fire", "Harm", "Auto", "Void Basic", "Drain Auto",
                "Prismatic", "Random", "Fists",
            ]);
        AssertCyclesExactly(
            () => panel.MonsterExtraVulnColumn[0],
            () => panel.CycleMonsterExtraVulnerabilityAt(0),
            ["Pierce", "Bludgeon", "Slash", "Acid", "Lightning", "Cold", "Fire", "Auto", "None"]);
        AssertCyclesExactly(
            () => panel.MonsterPetDamageColumn[0],
            () => panel.CycleMonsterPetDamageAt(0),
            [
                "Pierce", "Bludgeon", "Slash", "Acid", "Lightning", "Cold",
                "Fire", "PAuto", "Auto", "None",
            ]);

        static void AssertCyclesExactly(
            Func<string> readCurrent, Action cycle, string[] expected)
        {
            int guard = 0;
            while (readCurrent() != expected[0])
            {
                cycle();
                Assert.True(++guard <= expected.Length, "cycle never reached the array's first entry");
            }
            foreach (string next in expected.Skip(1).Append(expected[0]))
            {
                cycle();
                Assert.Equal(next, readCurrent());
            }
        }
    }

    [Fact]
    public void DeleteMonsterRuleAtRemovesNonDefaultRowsButNeverDefault()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        panel.SetMonsterExpressionDraft("drudge");
        panel.AddMonsterRule();
        panel.SetMonsterExpressionDraft("mosswart");
        panel.AddMonsterRule();
        Assert.Equal(["DEFAULT", "drudge", "mosswart"], panel.MonsterNameColumn);

        panel.DeleteMonsterRuleAt(0); // DEFAULT: refused
        Assert.Equal(["DEFAULT", "drudge", "mosswart"], panel.MonsterNameColumn);

        panel.DeleteMonsterRuleAt(1); // drudge
        Assert.Equal(["DEFAULT", "mosswart"], panel.MonsterNameColumn);
    }

    [Fact]
    public void VtankRefreshCommandRepopulatesTheMonstersGridAfterCreatingTheDefaultRule()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        Type panelType = typeof(MossTankPanel);
        var combatSettings = (CombatSettings)panelType
            .GetField("_combatSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(panel)!;
        combatSettings.Rules.Clear();
        FieldInfo monsterNameColumnField = panelType
            .GetField("_monsterNameColumn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        monsterNameColumnField.SetValue(panel, new[] { "STALE" });

        panel.ExecuteVtankCommand(new PluginCommand("vt", "refresh", "/vt refresh"));

        Assert.Equal(["DEFAULT"], panel.MonsterNameColumn);
    }

    [Fact]
    public void MoveMonsterRuleAtReordersButNeverDisplacesDefault()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        panel.SetMonsterExpressionDraft("drudge");
        panel.AddMonsterRule();
        panel.SetMonsterExpressionDraft("mosswart");
        panel.AddMonsterRule();
        Assert.Equal(["DEFAULT", "drudge", "mosswart"], panel.MonsterNameColumn);

        panel.MoveMonsterRuleDownAt(1); // drudge <-> mosswart
        Assert.Equal(["DEFAULT", "mosswart", "drudge"], panel.MonsterNameColumn);

        panel.MoveMonsterRuleUpAt(2); // back (row 2 "drudge" up to row 1)
        Assert.Equal(["DEFAULT", "drudge", "mosswart"], panel.MonsterNameColumn);

        panel.MoveMonsterRuleUpAt(1); // would displace DEFAULT from row 0: refused
        Assert.Equal(["DEFAULT", "drudge", "mosswart"], panel.MonsterNameColumn);

        Assert.Equal(3, panel.MonsterMoveUpIcons.Count);
        Assert.All(panel.MonsterMoveUpIcons, id => Assert.Equal(0x060028FCu, id));
        Assert.All(panel.MonsterMoveDownIcons, id => Assert.Equal(0x060028FDu, id));
    }

    [Fact]
    public void AddMonsterRuleUsesTheDraftTextAndAddSelectedMonsterUsesTheWorldTarget()
    {
        var automation = new CombatCapableFakeAutomation
        {
            Targets = [new PluginCombatTarget(30, "Drudge", 700, 2f, 0f, true, 1f)],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        panel.SetMonsterExpressionDraft("species==drudge");
        panel.AddMonsterRule();
        Assert.Equal(["DEFAULT", "species==drudge"], panel.MonsterNameColumn);
        Assert.Equal(string.Empty, panel.MonsterExpressionDraft);

        host.Selection.Select(30);
        panel.AddSelectedMonster();
        Assert.Equal(["DEFAULT", "species==drudge", "Drudge"], panel.MonsterNameColumn);
    }

    [Fact]
    public void MonsterWeaponColumnCyclesTheRegisteredRosterAndPersistsByNameAcrossSessions()
    {
        var storage = new MemoryStorage();
        var firstAutomation = new FakeAutomation
        {
            Name = "Rule Maker",
            ItemEntries = [Item(10, "Fire Sword", 1)],
        };
        var firstHost = new FakeHost(firstAutomation, storage);
        var first = new MossTankPanel(firstHost);
        firstHost.Selection.Select(10);
        first.AddSelectedItem(); // registers "Fire Sword" into the weapon roster

        Assert.Equal("<AUTO>", first.MonsterWeaponColumn[0]);
        first.CycleMonsterWeaponAt(0); // AUTO -> Fire Sword (only registered item)
        Assert.Equal("Fire Sword", first.MonsterWeaponColumn[0]);
        first.CycleMonsterWeaponAt(0); // Fire Sword -> AUTO (wraps)
        Assert.Equal("<AUTO>", first.MonsterWeaponColumn[0]);
        first.CycleMonsterWeaponAt(0);
        Assert.Equal("Fire Sword", first.MonsterWeaponColumn[0]);

        var second = new MossTankPanel(new FakeHost(
            new FakeAutomation
            {
                Name = "Rule Maker",
                ItemEntries = [Item(99, "Fire Sword", 1)],
            },
            storage));

        Assert.Equal("Fire Sword", second.MonsterWeaponColumn[0]);
    }

    [Fact]
    public void MonsterGridColumnsDoNotScanLiveInventoryOrAllocateOnEveryRead()
    {
        var automation = new FakeAutomation
        {
            Name = "Perf Check",
            ItemEntries = [Item(10, "Fire Sword", 1)],
        };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        host.Selection.Select(10);
        panel.AddSelectedItem();
        panel.CycleMonsterWeaponAt(0);

        int callsAfterCycle = automation.CaptureOwnedItemsCallCount;
        IReadOnlyList<string> weaponFirstRead = panel.MonsterWeaponColumn;
        for (int i = 0; i < 5; i++)
        {
            _ = panel.MonsterFesterColumn;
            _ = panel.MonsterNameColumn;
            _ = panel.MonsterWeaponColumn;
            _ = panel.MonsterOffhandColumn;
            _ = panel.MonsterMoveUpIcons;
        }

        Assert.Equal(callsAfterCycle, automation.CaptureOwnedItemsCallCount);
        Assert.Same(weaponFirstRead, panel.MonsterWeaponColumn);
    }

    [Fact]
    public void RouteGridDeletesByAnyCellClickAndSelectsNearestByDistance()
    {
        var automation = new FakeAutomation { NavigationSnapshot = NavigationAt(0f) };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.AddRoutePoint(); // waypoint 0 at EastWest=0
        automation.NavigationSnapshot = automation.NavigationSnapshot with
        {
            Position = automation.NavigationSnapshot.Position with { EastWest = 50d },
        };
        panel.AddRoutePoint(); // waypoint 1 at EastWest=50
        Assert.Equal(2, panel.RouteWaypointTextColumn.Count);
        Assert.Equal(["1", "2"], panel.RouteWaypointCountColumn);

        automation.NavigationSnapshot = automation.NavigationSnapshot with
        {
            Position = automation.NavigationSnapshot.Position with { EastWest = 48d },
        };
        panel.SelectNearestRouteWaypoint();
        Assert.Equal(1, panel.SelectedRouteWaypointIndex);

        panel.DeleteRouteWaypointAt(0);
        Assert.Single(panel.RouteWaypointTextColumn);
        Assert.Equal(["1"], panel.RouteWaypointCountColumn);
    }

    [Fact]
    public void RouteInsertModeControlsWhereANewWaypointLands()
    {
        var automation = new FakeAutomation { NavigationSnapshot = NavigationAt(0f) };
        var panel = new MossTankPanel(new FakeHost(automation));
        Assert.Equal("Add to End", panel.SelectedRouteInsertMode);

        automation.NavigationSnapshot = automation.NavigationSnapshot with
        { Position = automation.NavigationSnapshot.Position with { EastWest = 0d } };
        panel.AddRoutePoint(); // index 0 @ 0
        automation.NavigationSnapshot = automation.NavigationSnapshot with
        { Position = automation.NavigationSnapshot.Position with { EastWest = 20d } };
        panel.AddRoutePoint(); // index 1 @ 20 (default Add to End)
        Assert.Equal(2, panel.RouteWaypointTextColumn.Count);
        Assert.Contains("20", panel.RouteWaypointTextColumn[1]);

        panel.SelectRouteInsertMode("Insert Above");
        Assert.Equal("Insert Above", panel.SelectedRouteInsertMode);
        panel.SelectRouteWaypoint(0); // the @0 waypoint
        automation.NavigationSnapshot = automation.NavigationSnapshot with
        { Position = automation.NavigationSnapshot.Position with { EastWest = 99d } };
        panel.AddRoutePoint(); // must land BEFORE index 0, not appended
        Assert.Equal(3, panel.RouteWaypointTextColumn.Count);
        Assert.Contains("99", panel.RouteWaypointTextColumn[0]);

        panel.SelectRouteInsertMode("Insert Below");
        Assert.Equal("Insert Below", panel.SelectedRouteInsertMode);
        panel.SelectRouteWaypoint(0); // the @99 waypoint
        automation.NavigationSnapshot = automation.NavigationSnapshot with
        { Position = automation.NavigationSnapshot.Position with { EastWest = 77d } };
        panel.AddRoutePoint(); // must land right AFTER index 0
        Assert.Equal(4, panel.RouteWaypointTextColumn.Count);
        Assert.Contains("77", panel.RouteWaypointTextColumn[1]);
    }

    [Fact]
    public void RouteRecallComboShowsVtanksTerseCaptionsButTheWaypointKeepsTheFullName()
    {
        var automation = new FakeAutomation { NavigationSnapshot = NavigationAt(0f) };
        var panel = new MossTankPanel(new FakeHost(automation));

        Assert.Equal(
            [
                "Primary", "Secondary", "LS", "LS Sending", "Portal", "Aphus",
                "Sanctuary", "Caul", "GW", "Aerlinthe", "Mt. Lethe", "Ulgrim's",
                "Bur", "PtOIA", "Graveyard", "Colosseum", "Fac. Hub",
                "Gear K. Camp", "Neftet", "Candeth", "Rynthid", "VR Rocks",
                "VR Tree", "Soc. CH", "Soc. RB", "Soc. EW", "Marketplace",
            ],
            panel.RouteRecallNames);

        Assert.Equal("Primary", panel.SelectedRouteRecall);

        panel.SelectRouteRecall("LS");
        Assert.Equal("LS", panel.SelectedRouteRecall);

        panel.AddRouteRecall();
        Assert.Contains("Lifestone Recall", panel.RouteNotice, StringComparison.Ordinal);
        Assert.Contains("Lifestone Recall", Assert.Single(panel.RouteWaypointTextColumn));
    }

    [Fact]
    public void RoutePauseSecondsFieldParsesAndClampsInput()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        Assert.Equal("5", panel.RoutePauseSecondsFieldText);

        panel.SetRoutePauseSecondsText("12");
        Assert.Equal("12", panel.RoutePauseSecondsFieldText);

        panel.SetRoutePauseSecondsText("99999");
        Assert.Equal("3600", panel.RoutePauseSecondsFieldText);

        panel.SetRoutePauseSecondsText("not a number");
        Assert.Equal("3600", panel.RoutePauseSecondsFieldText); // unchanged on bad input
    }

    [Fact]
    public void MetaTabEditsAndExecutesTheLiveStateMachine()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        panel.ShowMeta();
        Assert.True(panel.MetaVisible);
        panel.SetMetaStateDraft(MetaEngine.DefaultState);
        panel.SelectMetaCondition(nameof(MetaConditionKind.Always));
        panel.SelectMetaAction(nameof(MetaActionKind.SetMetaState));
        panel.SetMetaActionTextDraft("Hunt");
        panel.AddMetaRule();

        Assert.Single(panel.MetaRows);
        Assert.Contains("Hunt", panel.MetaRows[0], StringComparison.Ordinal);
        panel.ToggleMeta();
        panel.ToggleCombat();
        panel.OnTick(0.3);

        Assert.True(panel.MetaEnabled);
        Assert.Equal("Hunt", panel.MetaState);
    }

    [Fact]
    public void MetaGridColumnsMatchTheRulesAndDeleteMoveActOnTheClickedRow()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        panel.SetMetaStateDraft("Idle");
        panel.SelectMetaAction(nameof(MetaActionKind.ChatCommand));
        panel.SetMetaActionTextDraft("/mt one");
        panel.AddMetaRule();
        panel.SetMetaStateDraft("Hunt");
        panel.SetMetaActionTextDraft("/mt two");
        panel.AddMetaRule();

        Assert.Equal(["Idle", "Hunt"], panel.MetaStateColumn);
        Assert.Contains("/mt one", panel.MetaActionColumn[0], StringComparison.Ordinal);
        Assert.Contains("/mt two", panel.MetaActionColumn[1], StringComparison.Ordinal);
        Assert.Equal(["X", "X"], panel.MetaDeleteColumn);
        Assert.All(panel.MetaMoveUpIcons, id => Assert.Equal(0x060028FCu, id));
        Assert.All(panel.MetaMoveDownIcons, id => Assert.Equal(0x060028FDu, id));

        panel.MoveMetaRuleDownAt(0); // "Idle" <-> "Hunt"
        Assert.Equal(["Hunt", "Idle"], panel.MetaStateColumn);
        panel.MoveMetaRuleUpAt(1); // back
        Assert.Equal(["Idle", "Hunt"], panel.MetaStateColumn);

        panel.DeleteMetaRuleAt(0); // "Idle"
        Assert.Equal(["Hunt"], panel.MetaStateColumn);
    }

    [Fact]
    public void MetaAndRouteGridColumnsDoNotReallocateOnEveryRead()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation
        {
            NavigationSnapshot = NavigationAt(0f),
        }));
        panel.SelectMetaAction(nameof(MetaActionKind.ChatCommand));
        panel.SetMetaActionTextDraft("/mt one");
        panel.AddMetaRule();
        panel.AddRoutePoint();

        Assert.Same(panel.MetaStateColumn, panel.MetaStateColumn);
        Assert.Same(panel.MetaConditionColumn, panel.MetaConditionColumn);
        Assert.Same(panel.MetaActionColumn, panel.MetaActionColumn);
        Assert.Same(panel.MetaDeleteColumn, panel.MetaDeleteColumn);
        Assert.Same(panel.MetaMoveUpIcons, panel.MetaMoveUpIcons);
        Assert.Same(panel.MetaMoveDownIcons, panel.MetaMoveDownIcons);
        Assert.Same(panel.RouteWaypointCountColumn, panel.RouteWaypointCountColumn);
    }

    [Fact]
    public void MetaEditorPopupOpensOnCellClickOrCreateAndClosesOnApplyOrCancel()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        Assert.False(panel.MetaEditorVisible);

        panel.CreateMetaRule();
        Assert.True(panel.MetaEditorVisible);
        Assert.Single(panel.MetaRows);

        panel.HideMetaEditor();
        Assert.False(panel.MetaEditorVisible);

        panel.SelectMetaRule(0);
        Assert.True(panel.MetaEditorVisible);

        panel.ApplyMetaRule();
        Assert.False(panel.MetaEditorVisible);

        panel.SelectMetaRule(0);
        Assert.True(panel.MetaEditorVisible);
        panel.HideMetaEditor();
        Assert.False(panel.MetaEditorVisible);

        panel.DeleteMetaRuleAt(0);
        Assert.False(panel.MetaEditorVisible);
    }

    [Fact]
    public void SwitchingTabsClosesTheBuffPickerAndMetaEditorPopups()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        panel.ShowExtraBuffPicker();
        Assert.True(panel.BuffPickerVisible);
        panel.ShowOptions(); // switch away from Buffs
        Assert.False(panel.BuffPickerVisible);

        panel.ShowMeta();
        panel.CreateMetaRule();
        Assert.True(panel.MetaEditorVisible);
        panel.ShowOptions(); // switch away from Meta
        Assert.False(panel.MetaEditorVisible);
    }

    [Fact]
    public void MetaCurrentStateMenuForcesTheLiveEngineIntoTheChosenState()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        panel.SetMetaStateDraft(MetaEngine.DefaultState);
        panel.SelectMetaAction(nameof(MetaActionKind.ChatCommand));
        panel.SetMetaActionTextDraft("/mt hi");
        panel.AddMetaRule();
        panel.SetMetaStateDraft("Hunt");
        panel.AddMetaRule();

        Assert.Contains("Hunt", panel.MetaCurrentStateNames);
        Assert.Equal(MetaEngine.DefaultState, panel.SelectedMetaCurrentState);

        panel.SetMetaCurrentState("Hunt");

        Assert.Equal("Hunt", panel.SelectedMetaCurrentState);
        Assert.Equal("Hunt", panel.MetaState);
    }

    [Fact]
    public void MetaProfilesAreIndependentDurableDocuments()
    {
        var storage = new MemoryStorage();
        var first = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Meta Maker" },
            storage));
        first.SelectMetaAction(nameof(MetaActionKind.ChatCommand));
        first.SetMetaActionTextDraft("/mt status");
        first.AddMetaRule();
        first.SetMetaProfileNameDraft("Hunting");
        first.CopyMetaProfile();

        Assert.Equal("Hunting", first.SelectedMetaProfile);
        Assert.Single(first.MetaRows);
        first.SelectMetaProfile(MossTankMetaProfileStore.ByCharacter);
        first.AddMetaRule();
        Assert.Equal(2, first.MetaRows.Count);

        var second = new MossTankPanel(new FakeHost(
            new FakeAutomation { Name = "Meta Maker" },
            storage));
        Assert.Equal(MossTankMetaProfileStore.ByCharacter, second.SelectedMetaProfile);
        Assert.Equal(2, second.MetaRows.Count);
        second.SelectMetaProfile("Hunting");
        Assert.Single(second.MetaRows);
        Assert.Contains("/mt status", second.MetaRows[0], StringComparison.Ordinal);
    }

    [Fact]
    public void UtilityBeltVtankExpressionsControlTheSameLiveOwners()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        panel.SelectMetaCondition(nameof(MetaConditionKind.Expression));
        panel.SetMetaConditionTextDraft("vtmacroenabled[] == 1");
        panel.SelectMetaAction(nameof(MetaActionKind.ExpressionAction));
        panel.SetMetaActionTextDraft(
            "vtsetsetting['MonsterRange',42] + vtsetmetastate['Expression State']");
        panel.AddMetaRule();
        panel.ToggleMeta();
        panel.ToggleCombat();

        panel.OnTick(0.3);

        Assert.Equal("Expression State", panel.MetaState);
        Assert.Equal("Maximum target range: 42m", panel.AttackRangeText);
        Assert.True(panel.EvaluateExpression("uboptset['MonsterRange',33]").IsTruthy);
        Assert.Equal(33d, panel.EvaluateExpression(
            "uboptget['MonsterRange']").AsNumber());
    }

    [Fact]
    public void RunMacroAndEnableCombatAreIndependentVtankStates()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));

        Assert.Equal("Run Macro", panel.CombatButtonText);
        Assert.True(panel.CombatEnabled);

        panel.ToggleCombat();
        panel.ToggleCombatEnabled();
        panel.OnTick(0d);

        Assert.Equal("Stop Macro", panel.CombatButtonText);
        Assert.False(panel.CombatEnabled);
        Assert.Equal("Combat disabled", panel.CombatStatus);
        Assert.True(panel.EvaluateExpression("vtmacroenabled[]").IsTruthy);
        Assert.False(panel.EvaluateExpression("uboptget['EnableCombat']").IsTruthy);
    }

    [Fact]
    public void SameCharacterReconnectClearsOnlySessionStateAndCanRestartCleanly()
    {
        var automation = new FakeAutomation { Name = "Relogger" };
        var panel = new MossTankPanel(new FakeHost(automation));
        panel.ToggleMeta();
        panel.ToggleCombat();
        panel.EvaluateExpression(
            "$session=11;@persistent=22;&global=33;"
            + "delayexec[60000,\"$late=1\"]");
        panel.EvaluateExpression("vtsetmetastate['Hunt']");

        automation.IsAvailable = false;
        panel.OnTick(0.1d);

        Assert.Equal("Run Macro", panel.CombatButtonText);
        Assert.False(panel.MetaEnabled);
        Assert.Equal(MetaEngine.DefaultState, panel.MetaState);
        Assert.Equal(0d, panel.EvaluateExpression("$session").AsNumber());
        Assert.Equal(22d, panel.EvaluateExpression("@persistent").AsNumber());
        Assert.Equal(33d, panel.EvaluateExpression("&global").AsNumber());
        Assert.Equal("Lost the session.", panel.BuffStatus);

        automation.IsAvailable = true;
        panel.OnTick(0.1d);
        panel.OnTick(61d);

        Assert.Equal(0d, panel.EvaluateExpression("$late").AsNumber());
        Assert.Equal("Idle.", panel.BuffStatus);
        panel.ToggleCombat();
        Assert.Equal("Stop Macro", panel.CombatButtonText);
    }

    [Fact]
    public void OfficialVtankOptionDefaultsAndDynamicOverridesAreDurable()
    {
        var storage = new MemoryStorage();
        var first = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));

        Assert.Equal(137, VtankOptionCatalog.Names.Length);
        Assert.Equal(10d, first.EvaluateExpression(
            "uboptget['ArrowheadFletchDiffExcessThreshold']").AsNumber());
        Assert.Equal(0.0833333333333333d, first.EvaluateExpression(
            "uboptget['DoorIDRange']").AsNumber(), precision: 14);
        Assert.True(first.EvaluateExpression(
            "uboptget['ManaChargesWhenOff']").IsTruthy);
        Assert.Equal(4d, first.EvaluateExpression(
            "uboptget['SpellCompMin-Critical']").AsNumber());
        Assert.Equal(20d, first.EvaluateExpression(
            "uboptget['SpellCompMin-Normal']").AsNumber());
        Assert.Equal(20d, first.EvaluateExpression(
            "uboptget['SpellCompMin-Idle']").AsNumber());
        Assert.Equal(2d, first.EvaluateExpression(
            "uboptget['IdleCraftCount_HealthKits']").AsNumber());
        Assert.Equal(15d, first.EvaluateExpression(
            "uboptget['IdleCraftCount_ManaFood']").AsNumber());
        Assert.True(first.EvaluateExpression(
            "uboptset['ArrowheadFletchDiffExcessThreshold',22]").IsTruthy);
        Assert.True(first.EvaluateExpression(
            "uboptset['IdleCraftCount_HealthKits',7]").IsTruthy);

        var second = new MossTankPanel(new FakeHost(new FakeAutomation(), storage));
        Assert.Equal(22d, second.EvaluateExpression(
            "uboptget['arrowheadfletchdiffexcessthreshold']").AsNumber());
        Assert.Equal(7d, second.EvaluateExpression(
            "uboptget['idlecraftcount_healthkits']").AsNumber());
    }

    [Fact]
    public void VtankSetInAllRewritesEveryKnownMacroProfile()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation();
        var panel = new MossTankPanel(new FakeHost(automation, storage));

        Command(panel, "settings save First");
        Command(panel, "opt set AttackDistance 0.01");
        Command(panel, "settings save Second");
        Command(panel, "opt set AttackDistance 0.02");
        Command(panel, "opt setinall AttackDistance 0.03");
        Command(panel, "settings load First");
        Assert.Equal(0.03d, panel.EvaluateExpression(
            "uboptget['AttackDistance']").AsNumber(), precision: 7);
        Command(panel, "settings load Second");
        Assert.Equal(0.03d, panel.EvaluateExpression(
            "uboptget['AttackDistance']").AsNumber(), precision: 7);

        var reloaded = new MossTankPanel(new FakeHost(
            new FakeAutomation(), storage));
        Command(reloaded, "settings load First");
        Assert.Equal(0.03d, reloaded.EvaluateExpression(
            "uboptget['AttackDistance']").AsNumber(), precision: 7);
    }

    [Fact]
    public void VtankSetInAllReportsRetailsExactMessageText()
    {
        var automation = new FakeAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        Command(panel, "opt setinall AttackDistance 0.03");

        string message = Assert.Single(automation.Messages, static text =>
            text.StartsWith("Done saving setting", StringComparison.Ordinal));
        Assert.StartsWith(
            "Done saving setting AttackDistance to all profiles. (Changed ",
            message,
            StringComparison.Ordinal);
        Assert.EndsWith(" profiles)", message, StringComparison.Ordinal);
        Assert.DoesNotContain("=", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("setinall")]
    public void VtankOptSetRejectsAWrongTypedValueWithRetailsExactText(string operation)
    {
        var automation = new FakeAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        Command(panel, $"opt {operation} EnableLooting notaboolean");

        Assert.Equal(
            "Option set: Invalid value specified. Proper type of EnableLooting is System.Boolean.",
            Assert.Single(automation.Messages));
    }

    [Fact]
    public void SetOptionInAllAppendsARowWhenAFileHasNoneForThatSetting()
    {
        var storage = new MemoryStorage();
        var automation = new FakeAutomation { Name = "Barris" };
        const string minimalUsd =
            "1\r\nSettings\r\n4\r\nSetting\r\nValue\r\nDescription\r\nSettingType\r\n"
            + "y\r\nn\r\nn\r\nn\r\n1\r\ns\r\nEnableNav\r\nb\r\nFalse\r\ns\r\n\r\ni\r\n1\r\n";
        storage.Text["Other.usd"] = minimalUsd;

        var store = new MossTankProfileStore(new FakeHost(automation, storage));
        var settings = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = new CombatSettings(),
            Buffs = new BuffSettings(),
            Vitals = new VitalSettings(),
            Inventory = new InventorySettings(),
            Navigation = new NavigationSettings(),
        };
        settings.Inventory.Loot.Enabled = true;

        int count = store.SetOptionInAll("EnableLooting", settings);

        VtankDatabase rewritten = VtankDatabase.Parse(storage.Text["Other.usd"]);
        VtankTable table = rewritten.Find("Settings")!;
        int nameColumn = table.ColumnIndex("Setting");
        int valueColumn = table.ColumnIndex("Value");
        VtankRow appended = Assert.Single(table.Rows, row =>
            row.Cells[nameColumn].AsString().Equals(
                "EnableLooting", StringComparison.OrdinalIgnoreCase));
        Assert.True(appended.Cells[valueColumn].AsBool());
        Assert.Equal("0", appended.Cells[2].Tag);
        Assert.Equal("0", appended.Cells[3].Tag);
        Assert.True(count >= 1);
    }

    [Fact]
    public void VtankHelpAndExpressionsUseTheLocalCommandSurface()
    {
        var automation = new FakeAutomation();
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ExecuteVtankCommand(new PluginCommand("vt", "help", "/vt help"));
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt",
            "mexec 1 + 2 * 3",
            "/vt mexec 1 + 2 * 3"));

        Assert.Equal(6, automation.Messages.Count);
        Assert.StartsWith("/vt commands (profiles):", automation.Messages[0],
            StringComparison.Ordinal);
        Assert.Equal("MExec evaluating expression: \"1 + 2 * 3\"", automation.Messages[4]);
        Assert.Equal("Result: 7", automation.Messages[5]);
    }

    [Fact]
    public void VtankJumpTurnsToTheRequestedHeadingBeforeChargingAndReleasing()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = NavigationAt(90f),
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", "jump 180 false 100", "/vt jump 180 false 100"));
        panel.OnTick(0.05d);

        Assert.True(automation.MovementIntents[^1].TurnRight);
        Assert.False(automation.MovementIntents[^1].Jump);

        automation.NavigationSnapshot = NavigationAt(180f);
        panel.OnTick(0.01d);
        Assert.True(automation.MovementIntents[^1].Jump);

        panel.OnTick(0.1d);
        Assert.False(automation.MovementIntents[^1].Jump);
    }

    [Fact]
    public void RegistersEveryAuditedUtilityBeltExpressionFunction()
    {
        var panel = new MossTankPanel(new FakeHost(new FakeAutomation()));
        string[] expected =
        [
            "abs",
            "acos",
            "actiontryapplyitem",
            "actiontrycastbyid",
            "actiontrycastbyidontarget",
            "actiontrydrop",
            "actiontryequipanywand",
            "actiontrygiveitem",
            "actiontrygiveprofile",
            "actiontrymove",
            "actiontryselect",
            "actiontrysplit",
            "actiontryuseitem",
            "asin",
            "atan",
            "atan2",
            "ceiling",
            "chatbox",
            "chatboxpaste",
            "chr",
            "clearallgvars",
            "clearallpvars",
            "clearallvars",
            "clearexec",
            "cleargvar",
            "clearmotion",
            "clearnextlogin",
            "clearpvar",
            "clearvar",
            "cnumber",
            "componentdata",
            "componentname",
            "coordinatedistanceflat",
            "coordinatedistancewithz",
            "coordinategetns",
            "coordinategetwe",
            "coordinategetz",
            "coordinateparse",
            "coordinatetostring",
            "cos",
            "cosh",
            "cstr",
            "cstrf",
            "delayexec",
            "dictadditem",
            "dictclear",
            "dictcopy",
            "dictcreate",
            "dictgetitem",
            "dicthaskey",
            "dictkeys",
            "dictremovekey",
            "dictsize",
            "dictvalues",
            "echo",
            "exec",
            "floor",
            "getaccounthash",
            "getbusystate",
            "getcancastspell_buff",
            "getcancastspell_hunt",
            "getcharacterindex",
            "getcharattribute_base",
            "getcharattribute_buffed",
            "getcharboolprop",
            "getcharburden",
            "getchardoubleprop",
            "getcharintprop",
            "getcharquadprop",
            "getcharskill_base",
            "getcharskill_buffed",
            "getcharskill_traininglevel",
            "getcharstringprop",
            "getcharvital_base",
            "getcharvital_buffedmax",
            "getcharvital_current",
            "getcombatstate",
            "getcontaineritemcount",
            "getcooldownexpiration",
            "getcorpsesunopenedbyme",
            "getdatetimelocal",
            "getdatetimeutc",
            "getequippedweapontype",
            "getfellowid",
            "getfellowids",
            "getfellowname",
            "getfellownames",
            "getfellowshipcanrecruit",
            "getfellowshipcount",
            "getfellowshipisfull",
            "getfellowshipisleader",
            "getfellowshipisopen",
            "getfellowshipleaderid",
            "getfellowshiplocked",
            "getfellowshipname",
            "getfellowshipstatus",
            "getfreecontainerslots",
            "getfreeitemslots",
            "getgameday",
            "getgamehour",
            "getgamehourname",
            "getgamemonth",
            "getgamemonthname",
            "getgameticks",
            "getgameyear",
            "getgvar",
            "getheading",
            "getheadingto",
            "getinventorycountbytemplatetype",
            "getisday",
            "getisnight",
            "getisspellknown",
            "getitemcountininventorybyname",
            "getitemcountininventorybynamerx",
            "getknownspells",
            "getminutesuntilday",
            "getminutesuntilnight",
            "getmotion",
            "getobjectinternaltype",
            "getplayercoordinates",
            "getplayerlandblock",
            "getplayerlandcell",
            "getpvar",
            "getquestktprogress",
            "getquestktrequired",
            "getqueststatus",
            "getregexmatch",
            "getspellexpiration",
            "getspellexpirationbyname",
            "getunixtime",
            "getvar",
            "getworldname",
            "hascorpsebeenopenedbyme",
            "hexstr",
            "ifthen",
            "iif",
            "isfalse",
            "isportaling",
            "isrefreshingquests",
            "istrue",
            "listadd",
            "listclear",
            "listcontains",
            "listcopy",
            "listcount",
            "listcreate",
            "listfilter",
            "listfromrange",
            "listgetitem",
            "listindexof",
            "listinsert",
            "listlastindexof",
            "listmap",
            "listpop",
            "listreduce",
            "listremove",
            "listremoveat",
            "listreverse",
            "listsort",
            "lumavg",
            "lumtotal",
            "netclients",
            "ord",
            "randint",
            "round",
            "setcombatstate",
            "setgvar",
            "setmotion",
            "setnextlogin",
            "setpvar",
            "setvar",
            "sin",
            "sinh",
            "spelldata",
            "spellname",
            "sqrt",
            "statushud",
            "statushudcolored",
            "stopwatchcreate",
            "stopwatchelapsedseconds",
            "stopwatchstart",
            "stopwatchstop",
            "strlen",
            "tan",
            "tanh",
            "testgvar",
            "testpvar",
            "testquestflag",
            "testvar",
            "tostring",
            "touchgvar",
            "touchpvar",
            "touchvar",
            "uboptget",
            "uboptset",
            "uigetcontrol",
            "uisetlabel",
            "uisetvisible",
            "uiviewexists",
            "uiviewvisible",
            "ustadd",
            "ustopen",
            "ustsalvage",
            "vitae",
            "vtgetmeta",
            "vtgetmetastate",
            "vtgetsetting",
            "vtmacroenabled",
            "vtsetmetastate",
            "vtsetsetting",
            "wobjectfindall",
            "wobjectfindallbycontainer",
            "wobjectfindallbynamerx",
            "wobjectfindallbyobjectclass",
            "wobjectfindallbytemplatetype",
            "wobjectfindallinventory",
            "wobjectfindallinventorybynamerx",
            "wobjectfindallinventorybyobjectclass",
            "wobjectfindallinventorybytemplatetype",
            "wobjectfindalllandscape",
            "wobjectfindalllandscapebynamerx",
            "wobjectfindalllandscapebyobjectclass",
            "wobjectfindalllandscapebytemplatetype",
            "wobjectfindbyid",
            "wobjectfindininventorybyname",
            "wobjectfindininventorybynamerx",
            "wobjectfindininventorybytemplatetype",
            "wobjectfindnearestbynameandobjectclass",
            "wobjectfindnearestbyobjectclass",
            "wobjectfindnearestbytemplatetype",
            "wobjectfindnearestdoor",
            "wobjectfindnearestmonster",
            "wobjectgetactivespellids",
            "wobjectgetboolprop",
            "wobjectgetdoubleprop",
            "wobjectgethealth",
            "wobjectgethealthvalue",
            "wobjectgetid",
            "wobjectgetintprop",
            "wobjectgetisdooropen",
            "wobjectgetmanavalue",
            "wobjectgetname",
            "wobjectgetobjectclass",
            "wobjectgetopencontainer",
            "wobjectgetphysicscoordinates",
            "wobjectgetplayer",
            "wobjectgetselection",
            "wobjectgetspellids",
            "wobjectgetstaminavalue",
            "wobjectgetstringprop",
            "wobjectgettemplatetype",
            "wobjecthasdata",
            "wobjectisvalid",
            "wobjectlastidtime",
            "wobjectrequestdata",
            "xpavg",
            "xpduration",
            "xpmeter",
            "xpreset",
            "xptotal",
        ];

        string[] missing = expected
            .Except(panel.ExpressionFunctionNames, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.True(
            missing.Length == 0,
            "Missing UtilityBelt expression functions: " + string.Join(", ", missing));
    }

    private static PluginInventoryItem Item(
        uint id,
        string name,
        uint itemType,
        uint validLocations = 0u,
        int petClass = 0) => new(
            id, 0, name, itemType, 1, 0, validLocations, 0, 0, 0, 0,
            1, 0, 0, 0, petClass, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, 0);

    private static PluginEquipmentItem EquipmentItem(
        uint id,
        string name,
        uint itemType) => new(
            id,
            name,
            ItemType: itemType,
            ValidLocations: 0x00100000,
            EquippedLocation: 0,
            ContainerObjectId: 1,
            WielderObjectId: 0,
            CombatUse: 1,
            DamageType: 0,
            WeaponSkill: 44,
            Damage: 20,
            DamageVariance: 0.25);

    private static PluginSpellInfo Spell(uint id, uint family, string description) => new(
        id,
        $"Spell {id}",
        family,
        Tier: 1,
        Difficulty: 10,
        ManaCost: 5,
        DurationSeconds: 60f,
        School: 1,
        description,
        IsSelfTargeted: true,
        IsBeneficial: true);

    [Fact]
    public void ADepartedIdIsRetiredEvenWhenTheEntryCountIsUnchanged()
    {
        var tracker = new BuffDueTracker();
        tracker.Observe(
        [
            new PluginActiveEnchantment(1u, 10u, 1, 600d),
            new PluginActiveEnchantment(2u, 20u, 1, 600d),
        ]);
        tracker.ForceAll();
        Assert.Equal([1u, 2u], tracker.ForcedSpellIds.Order());

        // Spell 1 drops; spell 2 gains a second layer. Two entries either way.
        tracker.Observe(
        [
            new PluginActiveEnchantment(2u, 20u, 1, 600d),
            new PluginActiveEnchantment(2u, 20u, 2, 900d),
        ]);

        Assert.Equal([2u], tracker.ForcedSpellIds.Order());
    }

    [Fact]
    public void AnOwnTellDuringTheLaunchWaitDoesNotDropTheCastLatch()
    {
        var automation = new FakeAutomation
        {
            ObjectId = 0x50000001u,
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points.")
                    with { Saying = "abracadabra" },
            ],
            // The server has taken the request and said nothing yet, so the
            // tracker sits in AwaitingLaunch with a Saying to match.
            SuppressCastCompletion = true,
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int tick = 0; tick < 3; tick++)
            panel.OnTick(0.3d);
        Assert.Equal([1u], automation.CastSpellIds);

        automation.PostChatFrom(0x50000001u, 3, "meet me at the portal");
        for (int tick = 0; tick < 3; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([1u], automation.CastSpellIds);
    }

    [Fact]
    public void OwnLocalSpeechOfTheSpellWordsIsStillTheGestureEcho()
    {
        var automation = new FakeAutomation
        {
            ObjectId = 0x50000001u,
            CurrentHealth = 100,
            MaxHealth = 100,
            CurrentStamina = 100,
            MaxStamina = 100,
            CurrentMana = 100,
            MaxMana = 100,
            Skills =
            [
                new PluginSkillInfo(1, "Life Magic", PluginSkillTraining.Trained, 300),
            ],
            KnownSelfBuffs =
            [
                Spell(1, 10, "Increases the caster's Life Magic skill by 10 points.")
                    with { Saying = "abracadabra" },
            ],
            SuppressCastCompletion = true,
        };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.ToggleCombat();
        for (int tick = 0; tick < 3; tick++)
            panel.OnTick(0.3d);
        Assert.Equal([1u], automation.CastSpellIds);

        // A DIFFERENT spell's words, spoken locally by us: gj.cs:359's a(gj.b.a)
        // — this wait is over, the latch drops, and the pass re-derives the
        // same pick.
        automation.PostChatFrom(0x50000001u, 0, "hocus pocus");
        for (int tick = 0; tick < 3; tick++)
            panel.OnTick(0.3d);

        Assert.Equal([1u, 1u], automation.CastSpellIds);
    }


    private static PluginSpellComponentInfo Component(uint id, string name) => new(
        id, id, name, 1d, 0u, 1d, 0u, id, "Scarab", string.Empty);

    private static PluginNavigationSnapshot NavigationAt(float heading) => new(
        IsAvailable: true,
        IsPortalSpace: false,
        LocalObjectId: 1u,
        Position: new PluginNavigationPosition(
            0x00010001u, 0d, 0d, 0d, heading, IsOutdoor: true),
        IsMoving: false,
        IsAirborne: false);

    private static void Command(MossTankPanel panel, string arguments) =>
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", arguments, "/vt " + arguments));

    private sealed class FakeHost(
        IAutomationSurface automation,
        IPluginStorage? storage = null,
        IPluginLootClassifierRegistry? lootClassifiers = null,
        IPluginStorage? vtankProfiles = null) : IPluginHost
    {
        public bool HasUi => false;
        public FakeLogger Logger { get; } = new();
        public IPluginLogger Log => Logger;
        public IGameState State { get; } = new FakeState();
        public IEvents Events { get; } = new FakeEvents();
        public ISelectionService Selection { get; } = new FakeSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IPluginStorage Storage { get; } =
            storage ?? NoOpPluginStorage.Instance;
        public IAutomationSurface Automation { get; } = automation;
        public IPluginLootClassifierRegistry LootClassifiers { get; } =
            lootClassifiers ?? NoOpPluginLootClassifierRegistry.Instance;
        public IPluginStorage VtankProfiles { get; } =
            vtankProfiles ?? storage ?? NoOpPluginStorage.Instance;
    }

    private sealed class FakeLootClassifierRegistry(
        params PluginLootClassifierInfo[] available)
        : IPluginLootClassifierRegistry
    {
        public IReadOnlyList<PluginLootClassifierInfo> Available { get; } =
            available;
    }

    private sealed class FakeAutomation
        : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
          IPluginChat, IItemAutomation, INavigationAutomation,
          IWorldObjectAutomation, IRecoveryAutomation, IEnchantmentAutomation
    {
        /// <summary>
        /// Per-object tracked enchantments, VTank's <c>dm</c>
        /// (<c>dm.cs:287-321</c>) — what an item-enchant row's due test reads.
        /// </summary>
        public Dictionary<uint, List<PluginTrackedEnchantment>> ItemEnchantments
        { get; } = [];

        public IEnchantmentAutomation Enchantments => this;

        public IReadOnlyList<PluginTrackedEnchantment> Capture(uint targetObjectId) =>
            ItemEnchantments.TryGetValue(targetObjectId, out var held)
                ? held
                : [];
        private IReadOnlyList<PluginSpellInfo> _knownSelfBuffs = [];

        public bool IsAvailable { get; set; } = true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => this;
        public IMagicCommands Magic => this;
        public IPluginChat Chat => this;
        public IItemAutomation Items => this;
        public INavigationAutomation Navigation => this;
        public IWorldObjectAutomation Objects => this;
        public IRecoveryAutomation Recovery => this;
        public bool IsInWorld => IsAvailable;
        public string Name { get; set; } = "Test Character";

        public int Level { get; set; }
        public uint ObjectId { get; set; } = 1;
        public uint CurrentHealth { get; set; }
        public uint MaxHealth { get; set; }
        public uint CurrentStamina { get; set; }
        public uint MaxStamina { get; set; }
        public uint CurrentMana { get; set; }
        public uint MaxMana { get; set; }
        public IReadOnlyList<PluginSkillInfo> Skills { get; set; } = [];
        public IReadOnlyList<PluginAttributeInfo> Attributes { get; set; } = [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments { get; set; } = [];
        public IReadOnlyList<PluginInventoryItem> ItemEntries { get; set; } = [];
        public List<string> Messages { get; } = [];
        public List<uint> CastSpellIds { get; } = [];
        public List<PluginMovementIntent> MovementIntents { get; } = [];
        public int ClearMovementCount { get; private set; }
        public IReadOnlyList<PluginWorldObject> WorldObjects { get; set; } = [];
        public int BusyReferences { get; set; }
        public PluginNavigationSnapshot NavigationSnapshot { get; set; }
        public PluginNavigationSnapshot Snapshot => NavigationSnapshot;
        public int CaptureOwnedItemsCallCount { get; private set; }
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems()
        {
            CaptureOwnedItemsCallCount++;
            return ItemEntries;
        }
        public IReadOnlyList<PluginWorldObject> CaptureObjects() => WorldObjects;

        public bool ItemsBusy { get; set; }

        bool IItemAutomation.IsBusy => ItemsBusy;

        /// <summary>
        /// Object ids handed to <c>Items.Use</c>, in order. The request is
        /// accepted and <c>Items.LastCompletion</c> never moves — a server
        /// that took the use and has not answered yet, which is the window a
        /// staged recharge has to survive.
        /// </summary>
        public List<uint> UsedItemIds { get; } = [];

        public PluginItemCommandResult Use(uint objectId)
        {
            UsedItemIds.Add(objectId);
            return new PluginItemCommandResult(PluginItemCommandStatus.Started);
        }

        bool IWorldObjectAutomation.TryGet(
            uint objectId,
            out PluginWorldObject value)
        {
            foreach (PluginWorldObject candidate in WorldObjects)
            {
                if (candidate.ObjectId != objectId)
                    continue;
                value = candidate;
                return true;
            }
            value = default;
            return false;
        }

        public PluginRecoveryResult ClearOneBusyReference()
        {
            int before = BusyReferences;
            BusyReferences = Math.Max(0, BusyReferences - 1);
            return new(true, before, BusyReferences);
        }

        public int KnownSelfBuffReads { get; private set; }

        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs
        {
            get
            {
                KnownSelfBuffReads++;
                return _knownSelfBuffs;
            }
            set => _knownSelfBuffs = value;
        }

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            foreach (PluginSkillInfo candidate in Skills)
            {
                if (candidate.SkillId == skillId)
                {
                    skill = candidate;
                    return true;
                }
            }
            skill = default;
            return false;
        }

        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            foreach (PluginSpellInfo candidate in _knownSelfBuffs)
            {
                if (candidate.SpellId == spellId)
                {
                    info = candidate;
                    return true;
                }
            }
            info = default;
            return false;
        }

        public bool IsCasting { get; set; }

        public uint NextCastWeenieError { get; set; }
        public bool SuppressCastCompletion { get; set; }

        public bool SuppressCastResultText { get; set; }

        public string? CastResultText { get; set; }

        public List<PluginChatMessage> ChatLines { get; } = [];
        private ulong _chatSequence;

        public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence)
        {
            var result = new List<PluginChatMessage>();
            foreach (PluginChatMessage message in ChatLines)
            {
                if (message.Sequence > afterSequence)
                    result.Add(message);
            }
            return result;
        }

        public void PostChat(string text) =>
            ChatLines.Add(new PluginChatMessage(
                ++_chatSequence, 0u, 0, string.Empty, text, string.Empty));
        public void PostChatFrom(uint senderObjectId, int kind, string text) =>
            ChatLines.Add(new PluginChatMessage(
                ++_chatSequence, senderObjectId, kind, string.Empty, text,
                string.Empty));


        private PluginCastCompletion _lastCompletion;
        public PluginCastCompletion LastCompletion => _lastCompletion;

        public Dictionary<uint, PluginSpellComponentInfo> Components { get; } = [];

        public bool TryGetComponent(
            uint componentId, out PluginSpellComponentInfo info) =>
            Components.TryGetValue(componentId, out info);

        public HashSet<uint> RefusedCastSpellIds { get; } = [];

        /// <summary>
        /// Spell ids the host answers <c>HasComponents(false)</c> for — the
        /// authoritative formula test <c>BuffCastability</c> now runs INSIDE
        /// the tier accept, so these are never picked at all.
        /// </summary>
        public HashSet<uint> MissingComponentSpellIds { get; } = [];

        public Dictionary<uint, PluginCastRequestResult> CastRefusals { get; } = [];

        public bool HasComponents(uint spellId) =>
            !MissingComponentSpellIds.Contains(spellId);

        public PluginCastRequestResult RequestCast(uint spellId)
        {
            if (CastRefusals.TryGetValue(
                    spellId, out PluginCastRequestResult refusal))
                return refusal;
            return Cast(spellId)
                ? PluginCastRequestResult.Sent
                : PluginCastRequestResult.Unavailable;
        }

        public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Ready;
        public bool Cast(uint spellId)
        {
            if (RefusedCastSpellIds.Contains(spellId))
                return false;
            CastSpellIds.Add(spellId);
            if (!SuppressCastCompletion)
            {
                _lastCompletion = new PluginCastCompletion(
                    _lastCompletion.Revision + 1,
                    spellId,
                    0u,
                    NextCastWeenieError);
            }
            if (NextCastWeenieError == 0u
                && !SuppressCastCompletion
                && !SuppressCastResultText)
            {
                string name = TryGet(spellId, out PluginSpellInfo spell)
                    ? spell.Name
                    : $"Spell {spellId}";
                PostChat(CastResultText ?? $"You cast {name} on yourself");
            }

            if (NextCastWeenieError == 0u
                && !SuppressCastCompletion
                && CastResultText is null)
            {
                LandEnchantment(spellId);
                RaiseSkill(spellId);
            }
            return true;
        }

        public (uint SpellId, uint SkillId, uint Level)? RaiseSkillOnCast { get; set; }

        private void RaiseSkill(uint spellId)
        {
            if (RaiseSkillOnCast is not { } raise || raise.SpellId != spellId)
                return;
            var next = new List<PluginSkillInfo>();
            foreach (PluginSkillInfo skill in Skills)
            {
                next.Add(skill.SkillId == raise.SkillId
                    ? skill with { Current = raise.Level }
                    : skill);
            }
            Skills = next;
        }

        public Func<uint>? CurrentSelection { get; set; }

        /// <summary><c>eq.a(ActiveSpellInfo)</c> (<c>eq.cs:447-475</c>).</summary>
        private void LandEnchantment(uint spellId)
        {
            foreach (PluginSpellInfo spell in KnownSelfBuffs)
            {
                if (spell.SpellId != spellId)
                    continue;
                uint target = CurrentSelection?.Invoke() ?? 0u;

                if (spell.School == 32u
                    && !spell.IsSelfTargeted
                    && target == ObjectId)
                {
                    return;
                }

                if (spell.School == 32u
                    && !spell.IsSelfTargeted
                    && target != 0u
                    && target != ObjectId)
                {
                    if (!ItemEnchantments.TryGetValue(target, out var onItem))
                    {
                        onItem = [];
                        ItemEnchantments[target] = onItem;
                    }
                    onItem.RemoveAll(held => held.SpellId == spellId);
                    onItem.Add(new PluginTrackedEnchantment(
                        target,
                        spellId,
                        spell.Family,
                        spell.Tier,
                        spell.IsUntargeted,
                        EnchantmentDurationSeconds));
                    return;
                }
                var next = new List<PluginActiveEnchantment>();
                foreach (PluginActiveEnchantment held in ActiveEnchantments)
                {
                    if (held.SpellId != spellId)
                        next.Add(held);
                }
                next.Add(new PluginActiveEnchantment(
                    spellId,
                    spell.Family,
                    spell.Tier,
                    EnchantmentDurationSeconds));
                ActiveEnchantments = next;
                return;
            }
        }

        /// <summary>How long a landed fake buff runs for.</summary>
        public double EnchantmentDurationSeconds { get; set; } = 1800d;
        public void PostSystemMessage(string text) => Messages.Add(text);
        public bool TryGetObject(uint objectId, out PluginNavigationObject value)
        {
            value = default;
            return false;
        }
        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent)
        {
            MovementIntents.Add(intent);
            return PluginNavigationCommandStatus.Accepted;
        }
        public PluginNavigationCommandStatus ClearMovementIntent()
        {
            ClearMovementCount++;
            return PluginNavigationCommandStatus.Accepted;
        }
    }

    private sealed class CombatCapableFakeAutomation :
        IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
        IPluginChat, ICombatAutomation, IEquipmentAutomation, IItemAutomation,
        INavigationAutomation, IWorldObjectAutomation
    {
        public INavigationAutomation Navigation => this;
        public IWorldObjectAutomation Objects => this;
        public PluginNavigationSnapshot NavigationSnapshot { get; set; } =
            NavigationAt(0f);
        public PluginNavigationSnapshot Snapshot => NavigationSnapshot;
        public List<PluginWorldObject> WorldObjects { get; } = [];
        public IReadOnlyList<PluginWorldObject> CaptureObjects() => WorldObjects;
        public bool TryGetObject(uint objectId, out PluginNavigationObject value)
        {
            value = default;
            return false;
        }
        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent) =>
            PluginNavigationCommandStatus.Accepted;
        public PluginNavigationCommandStatus ClearMovementIntent() =>
            PluginNavigationCommandStatus.Accepted;

        public bool IsAvailable { get; set; } = true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => this;
        public IMagicCommands Magic => this;
        public IPluginChat Chat => this;
        public ICombatAutomation Combat => this;
        public IEquipmentAutomation Equipment => this;
        public IItemAutomation Items => this;

        public List<string> CallLog { get; } = [];

        public bool IsInWorld => IsAvailable;
        public uint ObjectId { get; set; } = 1;
        public uint CurrentHealth { get; set; }
        public uint MaxHealth { get; set; }
        public uint CurrentStamina { get; set; }
        public uint MaxStamina { get; set; }
        public uint CurrentMana { get; set; }
        public uint MaxMana { get; set; }
        public int SummoningMastery => 0;
        public IReadOnlyList<PluginSkillInfo> Skills { get; set; } = [];
        public IReadOnlyList<PluginAttributeInfo> Attributes { get; set; } = [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments { get; set; } = [];
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs { get; set; } = [];
        public IReadOnlyList<PluginSpellInfo> KnownAttackSpells { get; set; } = [];
        public IReadOnlyList<PluginSpellInfo> KnownCombatSpells { get; set; } = [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            foreach (PluginSkillInfo candidate in Skills)
            {
                if (candidate.SkillId == skillId)
                {
                    skill = candidate;
                    return true;
                }
            }
            skill = default;
            return false;
        }
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            foreach (PluginSpellInfo candidate in KnownSelfBuffs)
            {
                if (candidate.SpellId == spellId)
                {
                    info = candidate;
                    return true;
                }
            }
            info = default;
            return false;
        }

        // ── magic ─────────────────────────────────────────────────────
        public bool IsCasting { get; set; }
        public List<uint> CastSpellIds { get; } = [];

        public uint NextCastWeenieError { get; set; }
        private PluginCastCompletion _lastCompletion;
        public PluginCastCompletion LastCompletion => _lastCompletion;

        public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Ready;
        public List<PluginChatMessage> ChatLines { get; } = [];
        private ulong _chatSequence;

        public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence)
        {
            var result = new List<PluginChatMessage>();
            foreach (PluginChatMessage message in ChatLines)
            {
                if (message.Sequence > afterSequence)
                    result.Add(message);
            }
            return result;
        }

        public void PostChat(string text) =>
            ChatLines.Add(new PluginChatMessage(
                ++_chatSequence, 0u, 0, string.Empty, text, string.Empty));

        public List<uint> CastTargets { get; } = [];

        public PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) =>
            PluginCastGate.Ready;

        public bool Cast(uint spellId, uint targetObjectId)
        {
            CastTargets.Add(targetObjectId);
            return Cast(spellId);
        }

        public bool Cast(uint spellId)
        {
            CastSpellIds.Add(spellId);
            CallLog.Add($"Cast:{spellId}");
            _lastCompletion = new PluginCastCompletion(
                _lastCompletion.Revision + 1,
                spellId,
                0u,
                NextCastWeenieError);
            if (NextCastWeenieError == 0u)
            {
                string castName = TryGet(spellId, out PluginSpellInfo cast)
                    ? cast.Name
                    : $"Spell {spellId}";
                PostChat($"You cast {castName} on yourself");
            }
            foreach (PluginSpellInfo spell in KnownSelfBuffs)
            {
                if (spell.SpellId != spellId)
                    continue;
                ActiveEnchantments =
                [
                    .. ActiveEnchantments,
                    new PluginActiveEnchantment(
                        spellId,
                        spell.Family,
                        spell.Tier,
                        600d),
                ];
                break;
            }
            return true;
        }

        public List<string> Messages { get; } = [];
        public void PostSystemMessage(string text) => Messages.Add(text);

        bool IItemAutomation.IsAvailable => true;
        bool IItemAutomation.IsBusy => false;
        public IReadOnlyList<PluginInventoryItem> ItemEntries { get; set; } = [];
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => ItemEntries;

        public PluginCombatSnapshot CombatSnapshot { get; set; } = new(
            SelectedObjectId: 0,
            PluginCombatMode.Peace,
            PluginAttackHeight.Medium,
            DesiredPower: 0.5f,
            PowerBarLevel: 0f,
            BuildInProgress: false,
            RequestInProgress: false,
            ServerResponsePending: false,
            RepeatAttackInProgress: false);
        PluginCombatSnapshot ICombatAutomation.Snapshot => CombatSnapshot;
        public IReadOnlyList<PluginCombatTarget> Targets { get; set; } = [];
        public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(
            float maximumDistance) => Targets;
        public int ModeChangeRequests { get; private set; }
        public PluginCombatCommandResult EnterMode(PluginCombatMode mode)
        {
            ModeChangeRequests++;
            CombatSnapshot = CombatSnapshot with { Mode = mode };
            CallLog.Add($"EnterMode:{mode}");
            return new(PluginCombatCommandStatus.ModeChangeSent);
        }
        public PluginCombatCommandResult EnterDefaultMode()
        {
            PluginCombatMode mode = PluginCombatMode.Peace;
            foreach (PluginEquipmentItem item in EquipmentItems)
            {
                if (!item.IsEquipped)
                    continue;
                mode = (item.ItemType & 0x00008000u) != 0u
                    ? PluginCombatMode.Magic
                    : PluginCombatMode.Melee;
                break;
            }
            CombatSnapshot = CombatSnapshot with { Mode = mode };
            CallLog.Add($"EnterDefaultMode:{mode}");
            return new(PluginCombatCommandStatus.ModeChangeSent);
        }
        public int BeginCount { get; private set; }
        public uint LastBeginTarget { get; private set; }
        public PluginCombatCommandResult BeginPhysicalAttack(
            uint targetObjectId, PluginAttackHeight height, float power)
        {
            LastBeginTarget = targetObjectId;
            BeginCount++;
            CallLog.Add($"Attack:{targetObjectId:X8}");
            return new(PluginCombatCommandStatus.Started);
        }
        public PluginCombatCommandResult ReleasePhysicalAttack() =>
            new(PluginCombatCommandStatus.Released);
        public PluginCombatCommandResult AbortPhysicalAttack() =>
            new(PluginCombatCommandStatus.Stopped);

        // ── equipment ─────────────────────────────────────────────────
        bool IEquipmentAutomation.IsAvailable => true;
        bool IEquipmentAutomation.IsBusy => false;
        public IReadOnlyList<PluginEquipmentItem> EquipmentItems { get; set; } = [];
        public IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() =>
            EquipmentItems;
        public PluginEquipmentCommandResult Equip(
            uint objectId,
            uint requestedLocation = 0u)
        {
            CallLog.Add($"Equip:{objectId:X8}");
            EquipmentItems = EquipmentItems
                .Select(item => item.ObjectId == objectId
                    ? item with { EquippedLocation = 0x00100000u }
                    : item with { EquippedLocation = 0u })
                .ToArray();
            return new(PluginEquipmentCommandStatus.Started);
        }
    }

    private sealed class FakeLogger : IPluginLogger
    {
        public List<string> Warnings { get; } = [];

        public List<string> Infos { get; } = [];
        public void Info(string message) => Infos.Add(message);
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        public Dictionary<string, string> Text { get; } =
            new(StringComparer.Ordinal);
        public bool IsAvailable => true;
        public string? ReadText(string key) =>
            Text.TryGetValue(key, out string? value) ? value : null;
        public IReadOnlyList<string> List(string prefix) => Text.Keys
            .Where(key => prefix.Length == 0
                || key.StartsWith(prefix + "/", StringComparison.Ordinal))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        public void WriteText(string key, string content) => Text[key] = content;
        public bool Delete(string key) => Text.Remove(key);
    }

    private sealed class FakeState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class FakeEvents : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }

        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeSelection : ISelectionService
    {
        public uint? SelectedObjectId { get; private set; }
        public uint? PreviousObjectId { get; private set; }

        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }

        public bool Select(uint objectId)
        {
            PreviousObjectId = SelectedObjectId;
            SelectedObjectId = objectId;
            return true;
        }

        public bool Clear()
        {
            PreviousObjectId = SelectedObjectId;
            SelectedObjectId = null;
            return true;
        }
    }
}

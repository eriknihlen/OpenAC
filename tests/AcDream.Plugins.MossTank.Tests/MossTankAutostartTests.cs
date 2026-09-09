using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class MossTankAutostartTests
{
    [Fact]
    public void NoSessionSettingsDeclaredIsANoOp()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var panel = new MossTankPanel(new FakeHost(automation));

        panel.TickAutostart();

        Assert.False(panel.CombatMacroRunning);
        Assert.Empty(automation.Logger.Errors);
    }

    [Fact]
    public void NotInWorldIsANoOpEvenWithSettingsDeclared()
    {
        var automation = new FakeAutomation { IsAvailable = false };
        var settings = new Dictionary<string, string> { ["startMacro"] = "true" };
        var panel = new MossTankPanel(
            new FakeHost(automation, sessionSettings: settings));

        panel.TickAutostart();

        Assert.False(panel.CombatMacroRunning);
    }

    [Fact]
    public void StartMacroTrueStartsTheMacroTheSamePathAsVtStart()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var settings = new Dictionary<string, string> { ["startMacro"] = "true" };
        var panel = new MossTankPanel(
            new FakeHost(automation, sessionSettings: settings));

        panel.TickAutostart();

        Assert.True(panel.CombatMacroRunning);
    }

    [Fact]
    public void MissingNamedProfileLogsOneLineButKeepsGoingToStartTheMacro()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var settings = new Dictionary<string, string>
        {
            ["settingsProfile"] = "DoesNotExist",
            ["metaProfile"] = "DoesNotExistEither",
            ["navProfile"] = "NorThisOne",
            ["lootProfile"] = "NorThisOneEither",
            ["startMacro"] = "true",
        };
        var panel = new MossTankPanel(
            new FakeHost(automation, sessionSettings: settings));

        panel.TickAutostart();

        // "Errors -> one log line each, keep going": four failed selects,
        // and the macro still starts despite all four.
        Assert.Equal(4, automation.Logger.Errors.Count);
        Assert.Contains(
            automation.Logger.Errors,
            message => message.Contains("DoesNotExist", StringComparison.Ordinal));
        Assert.True(panel.CombatMacroRunning);
    }

    [Fact]
    public void RealNamedProfilesAreSelectedThroughTheSameStoreMethodsAsTheProfilesTab()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        Command(panel, "settings save myprofile");
        Command(panel, "meta save myMeta");
        Command(panel, "nav save myNav");
        Command(panel, "loot new myLoot");

        var settings = new Dictionary<string, string>
        {
            ["settingsProfile"] = "myprofile",
            ["metaProfile"] = "myMeta",
            ["navProfile"] = "myNav",
            ["lootProfile"] = "myLoot",
        };
        host.SessionSettingsValue = settings;

        panel.TickAutostart();

        Assert.Empty(automation.Logger.Errors);
    }

    [Fact]
    public void EnableMetaTrueEnablesTheEngineAndSelectsItsDeclaredProfile()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);
        Command(panel, "meta save myMeta");

        Assert.False(panel.MetaEnabled);

        host.SessionSettingsValue = new Dictionary<string, string>
        {
            ["metaProfile"] = "myMeta",
            ["enableMeta"] = "true",
        };

        panel.TickAutostart();

        Assert.True(panel.MetaEnabled);
        Assert.Equal("myMeta", panel.SelectedMetaProfile);
        Assert.Empty(automation.Logger.Errors);
    }

    [Fact]
    public void AbsentEnableMetaKeyLeavesEnableMetaAsWhateverTheSettingsProfileSays()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        Command(panel, "settings save myprofile");
        Command(panel, "opt set enablemeta true");
        Assert.True(panel.MetaEnabled);
        panel.ToggleMeta();
        Assert.False(panel.MetaEnabled);

        host.SessionSettingsValue = new Dictionary<string, string>
        {
            ["settingsProfile"] = "myprofile",
        };

        panel.TickAutostart();

        Assert.True(panel.MetaEnabled);
        Assert.Empty(automation.Logger.Errors);
    }

    [Fact]
    public void AutostartAppliesExactlyOncePerGenerationAndReappliesAfterReconnect()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var settings = new Dictionary<string, string>
        {
            ["settingsProfile"] = "DoesNotExist",
        };
        var panel = new MossTankPanel(
            new FakeHost(automation, sessionSettings: settings));

        panel.TickAutostart();
        Assert.Single(automation.Logger.Errors);

        // Still in world, ticked again: no second application.
        panel.TickAutostart();
        Assert.Single(automation.Logger.Errors);

        // Reconnect: drop out of world, then re-enter — a fresh generation
        // re-applies exactly once more.
        automation.IsAvailable = false;
        panel.TickAutostart();
        Assert.Single(automation.Logger.Errors);

        automation.IsAvailable = true;
        panel.TickAutostart();
        Assert.Equal(2, automation.Logger.Errors.Count);
    }


    [Fact]
    public void AutostartSelectingASettingsProfileDoesNotDestroyItsSavedContent()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        panel.SetMetaOption("spelldiffexcessthreshold-hunt", ExpressionValue.Number(77));
        Command(panel, "settings save myprofile");
        string macroKey = panel.SelectedMacroProfile;
        string savedMyProfileText = host.Storage.ReadText(macroKey)!;
        Assert.False(string.IsNullOrEmpty(savedMyProfileText));

        Command(panel, "settings save otherprofile");
        Assert.NotEqual(macroKey, panel.SelectedMacroProfile);
        panel.SetMetaOption("spelldiffexcessthreshold-hunt", ExpressionValue.Number(13));

        host.SessionSettingsValue = new Dictionary<string, string>
        {
            ["settingsProfile"] = "myprofile",
        };
        panel.TickAutostart();

        Assert.Equal(savedMyProfileText, host.Storage.ReadText(macroKey));
        Assert.Equal(macroKey, panel.SelectedMacroProfile);
        Assert.Empty(automation.Logger.Errors);
    }

    [Fact]
    public void AutostartSelectingAMetaProfileDoesNotDestroyItsSavedContentOrRuleCount()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        panel.CreateNamedMetaProfile("myMeta");
        panel.SelectMetaAction("ExpressionAction");
        panel.SetMetaActionTextDraft("setvar['x',1]");
        panel.AddMetaRule();
        Assert.Single(panel.MetaRows);
        string metaKey = $"{VtankProfileDirectory.MetaFolder}/myMeta.af";
        string savedMyMetaText = host.Storage.ReadText(metaKey)!;
        Assert.False(string.IsNullOrEmpty(savedMyMetaText));

        panel.CreateNamedMetaProfile("otherMeta");
        Assert.Empty(panel.MetaRows);
        Assert.Equal("otherMeta", panel.SelectedMetaProfile);

        host.SessionSettingsValue = new Dictionary<string, string>
        {
            ["metaProfile"] = "myMeta",
        };
        panel.TickAutostart();

        Assert.Equal(savedMyMetaText, host.Storage.ReadText(metaKey));
        Assert.Equal("myMeta", panel.SelectedMetaProfile);
        Assert.Single(panel.MetaRows);
        Assert.Empty(automation.Logger.Errors);
    }

    [Fact]
    public void AutostartSelectingANavProfileDoesNotDestroyItsSavedContent()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        Command(panel, "nav save otherNav");

        // "myNav" is the autostart TARGET: give it one real waypoint.
        Command(panel, "nav save myNav");
        panel.SetRoutePauseSecondsText("7");
        panel.AddRoutePause();
        string navKey = $"{VtankProfileDirectory.NavFolder}/myNav.af";
        string savedMyNavText = host.Storage.ReadText(navKey)!;
        Assert.False(string.IsNullOrEmpty(savedMyNavText));

        Command(panel, "nav load otherNav");
        Assert.Equal("otherNav", panel.SelectedRouteProfile);

        host.SessionSettingsValue = new Dictionary<string, string>
        {
            ["navProfile"] = "myNav",
        };
        panel.TickAutostart();

        Assert.Equal(savedMyNavText, host.Storage.ReadText(navKey));
        Assert.Equal("myNav", panel.SelectedRouteProfile);
        Assert.Empty(automation.Logger.Errors);
    }

    [Fact]
    public void AutostartSelectingALootProfileDoesNotDestroyItsSavedContent()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var panel = new MossTankPanel(host);

        Command(panel, "loot new myLoot");
        panel.AddLootRule();
        Assert.Single(panel.LootRuleRows);
        string lootKey = "myLoot.utl";
        string savedMyLootText = host.Storage.ReadText(lootKey)!;
        Assert.False(string.IsNullOrEmpty(savedMyLootText));

        Command(panel, "loot new otherLoot");
        Assert.Empty(panel.LootRuleRows);
        Assert.Equal("otherLoot", panel.LootProfileName);

        host.SessionSettingsValue = new Dictionary<string, string>
        {
            ["lootProfile"] = "myLoot",
        };
        panel.TickAutostart();

        Assert.Equal(savedMyLootText, host.Storage.ReadText(lootKey));
        Assert.Equal("myLoot", panel.LootProfileName);
        Assert.Single(panel.LootRuleRows);
        Assert.Empty(automation.Logger.Errors);
    }


    [Fact]
    public void SettingsProfileStoreExistsProbeDoesNotMutateSelectionOrStorage()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var store = new MossTankProfileStore(host);
        store.BindCharacter("TestChar");
        var allSettings = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = new CombatSettings(),
            Buffs = new BuffSettings(),
            Vitals = new VitalSettings(),
            Inventory = new InventorySettings(),
            Navigation = new NavigationSettings(),
        };
        Assert.True(store.Create(
            "myprofile", copyCurrent: true, allSettings, new HashSet<string>(),
            new HashSet<string>(), out _));
        Assert.True(store.Create(
            "otherprofile", copyCurrent: true, allSettings, new HashSet<string>(),
            new HashSet<string>(), out _));
        string selectedBefore = store.Selected;
        var storageBefore = new Dictionary<string, string>(
            ((MemoryStorage)host.Storage).Text, StringComparer.Ordinal);

        Assert.True(store.Exists("myprofile"));
        Assert.False(store.Exists("doesNotExist"));

        Assert.Equal(selectedBefore, store.Selected);
        Assert.Equal(storageBefore, ((MemoryStorage)host.Storage).Text);
    }

    [Fact]
    public void MetaProfileStoreExistsProbeDoesNotMutateSelectionOrStorage()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var store = new MossTankMetaProfileStore(host);
        store.BindCharacter("TestChar");
        Assert.True(store.Create("myMeta", copyCurrent: false, new MetaProfile(), out _));
        Assert.True(store.Create("otherMeta", copyCurrent: false, new MetaProfile(), out _));
        string selectedBefore = store.Selected;
        var storageBefore = new Dictionary<string, string>(
            ((MemoryStorage)host.Storage).Text, StringComparer.Ordinal);

        Assert.True(store.Exists("myMeta"));
        Assert.False(store.Exists("doesNotExist"));

        Assert.Equal(selectedBefore, store.Selected);
        Assert.Equal(storageBefore, ((MemoryStorage)host.Storage).Text);
    }

    [Fact]
    public void RouteProfileStoreExistsProbeDoesNotMutateSelectionOrStorage()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var store = new MossTankRouteProfileStore(host);
        store.BindCharacter("TestChar");
        var navigationSettings = new NavigationSettings();
        Assert.True(store.Create("myNav", copyCurrent: true, navigationSettings, out _));
        Assert.True(store.Create("otherNav", copyCurrent: true, navigationSettings, out _));
        string selectedBefore = store.Selected;
        var storageBefore = new Dictionary<string, string>(
            ((MemoryStorage)host.Storage).Text, StringComparer.Ordinal);

        Assert.True(store.Exists("myNav"));
        Assert.False(store.Exists("doesNotExist"));

        Assert.Equal(selectedBefore, store.Selected);
        Assert.Equal(storageBefore, ((MemoryStorage)host.Storage).Text);
    }

    [Fact]
    public void LootProfileStoreExistsProbeDoesNotMutateSelectionOrStorage()
    {
        var automation = new FakeAutomation { IsAvailable = true };
        var host = new FakeHost(automation);
        var store = new MossTankLootProfileStore(host);
        store.BindCharacter("TestChar");
        Assert.True(store.Create("myLoot", copyCurrent: false, Array.Empty<LootRule>(), out _));
        Assert.True(store.Create("otherLoot", copyCurrent: false, Array.Empty<LootRule>(), out _));
        string selectedBefore = store.Selected;
        var storageBefore = new Dictionary<string, string>(
            ((MemoryStorage)host.Storage).Text, StringComparer.Ordinal);

        Assert.True(store.Exists("myLoot"));
        Assert.False(store.Exists("doesNotExist"));

        Assert.Equal(selectedBefore, store.Selected);
        Assert.Equal(storageBefore, ((MemoryStorage)host.Storage).Text);
    }

    private static void Command(MossTankPanel panel, string arguments) =>
        panel.ExecuteVtankCommand(new PluginCommand(
            "vt", arguments, "/vt " + arguments));

    private sealed class FakeHost(
        FakeAutomation automation,
        IPluginStorage? storage = null,
        IReadOnlyDictionary<string, string>? sessionSettings = null)
        : IPluginHost
    {
        private readonly IPluginStorage _storage = storage ?? new MemoryStorage();

        internal IReadOnlyDictionary<string, string>? SessionSettingsValue { get; set; } =
            sessionSettings;

        public bool HasUi => false;
        public IPluginLogger Log => automation.Logger;
        public IGameState State { get; } = new FakeState();
        public IEvents Events { get; } = new FakeEvents();
        public ISelectionService Selection { get; } = new FakeSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = automation;
        public IPluginStorage Storage => _storage;
        public IPluginStorage VtankProfiles => _storage;
        public IReadOnlyDictionary<string, string> SessionSettings =>
            SessionSettingsValue ?? new Dictionary<string, string>();
    }

    private sealed class FakeAutomation
        : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands, IPluginChat
    {
        internal FakeLogger Logger { get; } = new();

        public bool IsAvailable { get; set; }
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => this;
        public IMagicCommands Magic => this;
        public IPluginChat Chat => this;

        public bool IsInWorld => IsAvailable;
        public string Name { get; set; } = "TestChar";
        public uint ObjectId => 1;
        public uint CurrentHealth => 0;
        public uint MaxHealth => 0;
        public uint CurrentStamina => 0;
        public uint MaxStamina => 0;
        public uint CurrentMana => 0;
        public uint MaxMana => 0;
        public IReadOnlyList<PluginSkillInfo> Skills { get; } = [];
        public IReadOnlyList<PluginAttributeInfo> Attributes { get; } = [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments { get; } = [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            skill = default;
            return false;
        }

        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs { get; } = [];
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            info = default;
            return false;
        }

        public bool IsCasting => false;
        public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Unavailable;
        public bool Cast(uint spellId) => false;

        public List<string> Messages { get; } = [];
        public void PostSystemMessage(string text) => Messages.Add(text);
    }

    private sealed class FakeLogger : IPluginLogger
    {
        public List<string> Errors { get; } = [];
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) =>
            Errors.Add(message);
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);
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
            if (SelectedObjectId is null)
                return false;
            PreviousObjectId = SelectedObjectId;
            SelectedObjectId = null;
            return true;
        }
    }
}

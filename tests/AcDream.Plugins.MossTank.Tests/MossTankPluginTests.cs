using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class MossTankPluginTests
{
    [Fact]
    public void EnableRegistersTheLootClassifierAndDisableRevokesIt()
    {
        var lootClassifiers = new RecordingLootClassifierRegistry();
        var host = new FakeHost(lootClassifiers);
        var plugin = new MossTankPlugin();

        plugin.Initialize(host);
        Assert.Empty(lootClassifiers.Registered);

        plugin.Enable();

        RecordingLootClassifierRegistry.Entry registered =
            Assert.Single(lootClassifiers.Registered);
        Assert.Equal("moss-tank", registered.ClassifierId);
        Assert.Equal("MossTank", registered.DisplayName);
        Assert.IsType<MossTankLootClassifier>(registered.Classifier);
        Assert.False(registered.Revoked);

        plugin.Disable();

        Assert.True(registered.Revoked);
    }

    private sealed class RecordingLootClassifierRegistry : IPluginLootClassifierRegistry
    {
        public List<Entry> Registered { get; } = [];

        public IDisposable Register(
            string classifierId,
            string displayName,
            IPluginLootClassifier classifier)
        {
            var entry = new Entry(classifierId, displayName, classifier);
            Registered.Add(entry);
            return new Revocation(entry);
        }

        internal sealed class Entry(
            string classifierId,
            string displayName,
            IPluginLootClassifier classifier)
        {
            public string ClassifierId { get; } = classifierId;
            public string DisplayName { get; } = displayName;
            public IPluginLootClassifier Classifier { get; } = classifier;
            public bool Revoked { get; internal set; }
        }

        private sealed class Revocation(Entry entry) : IDisposable
        {
            public void Dispose() => entry.Revoked = true;
        }
    }

    private sealed class FakeHost(
        IPluginLootClassifierRegistry lootClassifiers) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new NoOpLogger();
        public IGameState State { get; } = new NoOpState();
        public IEvents Events { get; } = new NoOpEvents();
        public ISelectionService Selection { get; } = new NoOpSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = new MinimalAutomation();
        public IPluginLootClassifierRegistry LootClassifiers { get; } =
            lootClassifiers;
    }

    // A minimal but complete IAutomationSurface: only the four members
    // without an interface default (IsAvailable, Character, Spells, Magic,
    // Chat) plus their own required leaves. Everything else on
    // IAutomationSurface falls through to its NoOpAutomationSurface
    // default, which is fine for Enable()/Disable() -- neither touches it.
    private sealed class MinimalAutomation
        : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
          IPluginChat
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => this;
        public IMagicCommands Magic => this;
        public IPluginChat Chat => this;

        public bool IsInWorld => true;
        public uint ObjectId => 0u;
        public uint CurrentHealth => 0u;
        public uint MaxHealth => 0u;
        public uint CurrentStamina => 0u;
        public uint MaxStamina => 0u;
        public uint CurrentMana => 0u;
        public uint MaxMana => 0u;
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
        public PluginCastGate EvaluateGate(uint spellId) =>
            PluginCastGate.Refused;
        public bool Cast(uint spellId) => false;

        public void PostSystemMessage(string text) { }
    }

    private sealed class NoOpLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? error = null) { }
    }

    private sealed class NoOpState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
    }

    private sealed class NoOpEvents : IEvents
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

    private sealed class NoOpSelection : ISelectionService
    {
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent>? Changed;
        public bool Select(uint objectId)
        {
            Changed?.Invoke(default);
            return false;
        }
        public bool Clear() => false;
    }
}

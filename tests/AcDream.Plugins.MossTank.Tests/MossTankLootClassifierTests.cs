using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class MossTankLootClassifierTests
{
    private const uint LongDescPropertyId = 16u;

    [Fact]
    public void ClassifyUsesTheLiveRulesDelegate()
    {
        var host = new Host();
        var rules = new List<LootRule>
        {
            new() { Name = "All", Expression = "*", Action = LootAction.Keep },
        };
        var classifier = new MossTankLootClassifier(host, () => rules);

        PluginLootClassification result = classifier.Classify(
            new PluginLootClassificationContext(Item(), Properties(), []));

        Assert.True(result.Matched);
        Assert.Equal(PluginLootAction.Keep, result.Action);
        Assert.Equal("All", result.RuleName);
    }

    [Fact]
    public void ClassifyMapsManaStoneOntoItsPublicEquivalent()
    {
        var host = new Host();
        var rules = new List<LootRule>
        {
            new() { Name = "Mana", Expression = "*", Action = LootAction.ManaStone },
        };
        var classifier = new MossTankLootClassifier(host, () => rules);

        PluginLootClassification result = classifier.Classify(
            new PluginLootClassificationContext(Item(), Properties(), []));

        // PluginLootAction models MossTank's full vocabulary, including its
        // two mana-transfer actions -- ManaStone maps straight across rather
        // than collapsing onto NoLoot.
        Assert.True(result.Matched);
        Assert.Equal(PluginLootAction.ManaStone, result.Action);
        Assert.Equal("Mana", result.RuleName);
    }

    [Fact]
    public void ClassifyMapsManaTankOntoItsPublicEquivalent()
    {
        var host = new Host();
        var rules = new List<LootRule>
        {
            new() { Name = "Mana", Expression = "*", Action = LootAction.ManaTank },
        };
        var classifier = new MossTankLootClassifier(host, () => rules);

        PluginLootClassification result = classifier.Classify(
            new PluginLootClassificationContext(Item(), Properties(), []));

        Assert.True(result.Matched);
        Assert.Equal(PluginLootAction.ManaTank, result.Action);
        Assert.Equal("Mana", result.RuleName);
    }

    [Fact]
    public void NeedsIdentificationIsFalseWhenTheItemAlreadyHasALongDescription()
    {
        var host = new Host();
        var rules = new List<LootRule> { AppraisalDependentRule() };
        var classifier = new MossTankLootClassifier(host, () => rules);

        bool needsIdentification = classifier.NeedsIdentification(
            new PluginLootClassificationContext(
                Item(), Properties(hasLongDesc: true), []));

        Assert.False(needsIdentification);
    }

    [Fact]
    public void NeedsIdentificationIsTrueWhenUnidentifiedAndARuleReadsAnAppraisalDependentProperty()
    {
        var host = new Host();
        var rules = new List<LootRule> { AppraisalDependentRule() };
        var classifier = new MossTankLootClassifier(host, () => rules);

        bool needsIdentification = classifier.NeedsIdentification(
            new PluginLootClassificationContext(Item(), Properties(), []));

        Assert.True(needsIdentification);
    }

    [Fact]
    public void NeedsIdentificationIsFalseWhenNoRuleReadsAnAppraisalDependentProperty()
    {
        var host = new Host();
        var rules = new List<LootRule>
        {
            new()
            {
                Name = "ObjectClass",
                VtankRequirements =
                [
                    new VtankLootRequirement { Type = 7, Payload = "0" },
                ],
            },
        };
        var classifier = new MossTankLootClassifier(host, () => rules);

        bool needsIdentification = classifier.NeedsIdentification(
            new PluginLootClassificationContext(Item(), Properties(), []));

        Assert.False(needsIdentification);
    }

    [Fact]
    public void TryClassifyWithProfileReturnsFalseWhenTheNamedProfileDoesNotExist()
    {
        var host = new Host();
        var classifier = new MossTankLootClassifier(host, () => []);

        bool found = classifier.TryClassifyWithProfile(
            "does-not-exist",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out PluginLootClassification classification);

        Assert.False(found);
        Assert.False(classification.Matched);
    }

    [Fact]
    public void TryClassifyWithProfileLoadsTheNamedUtlFileFromVtankProfiles()
    {
        var host = new Host(new Dictionary<string, string>
        {
            ["loot-a.utl"] = ReadFixture("loot-a.utl"),
        });
        var classifier = new MossTankLootClassifier(host, () => []);

        // loot-a.utl's one rule ("peas") carries a Type=6 requirement --
        // VTClassic retired that requirement kind, so it never matches any
        // item. The profile is still found; it just never fires.
        bool found = classifier.TryClassifyWithProfile(
            "loot-a",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out PluginLootClassification classification);

        Assert.True(found);
        Assert.False(classification.Matched);
    }

    [Fact]
    public void TryClassifyWithProfileAcceptsTheNameWithOrWithoutTheUtlExtension()
    {
        var host = new Host(new Dictionary<string, string>
        {
            ["loot-a.utl"] = ReadFixture("loot-a.utl"),
        });
        var classifier = new MossTankLootClassifier(host, () => []);

        bool foundBare = classifier.TryClassifyWithProfile(
            "loot-a",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out _);
        bool foundWithExtension = classifier.TryClassifyWithProfile(
            "loot-a.utl",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out _);

        Assert.True(foundBare);
        Assert.True(foundWithExtension);
    }

    [Fact]
    public void TryClassifyWithProfileCachesTheParsedProfileAcrossCalls()
    {
        var host = new Host(new Dictionary<string, string>
        {
            ["loot-a.utl"] = ReadFixture("loot-a.utl"),
        });
        var classifier = new MossTankLootClassifier(host, () => []);

        classifier.TryClassifyWithProfile(
            "loot-a",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out _);
        classifier.TryClassifyWithProfile(
            "loot-a",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out _);

        Assert.Equal(1, host.VtankReadCount);
    }

    [Fact]
    public void TryClassifyWithProfileDoesNotReReadWithinTheRecheckInterval()
    {
        var time = new FakeTimeProvider();
        var host = new Host(new Dictionary<string, string>
        {
            ["loot-a.utl"] = ReadFixture("loot-a.utl"),
        });
        var classifier = new MossTankLootClassifier(host, () => [], time);

        classifier.TryClassifyWithProfile(
            "loot-a",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out _);
        time.Advance(TimeSpan.FromSeconds(4));
        classifier.TryClassifyWithProfile(
            "loot-a",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out _);

        Assert.Equal(1, host.VtankReadCount);
    }

    [Fact]
    public void TryClassifyWithProfileFindsAProfileDroppedInAfterTheRecheckInterval()
    {
        // The cache used to be permanent in both directions: a profile
        // requested before its file existed stayed "not found" forever,
        // even after the file was dropped in later. Bound the cache instead
        // so a later re-check picks it up.
        var time = new FakeTimeProvider();
        var host = new Host(new Dictionary<string, string>());
        var classifier = new MossTankLootClassifier(host, () => [], time);

        bool foundBeforeDropIn = classifier.TryClassifyWithProfile(
            "loot-a",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out _);
        Assert.False(foundBeforeDropIn);

        host.SetVtankText("loot-a.utl", ReadFixture("loot-a.utl"));

        // Still within the interval: the stale "not found" answer is
        // reused, and storage is not re-read.
        bool foundStillWithinInterval = classifier.TryClassifyWithProfile(
            "loot-a",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out _);
        Assert.False(foundStillWithinInterval);
        Assert.Equal(1, host.VtankReadCount);

        time.Advance(TimeSpan.FromSeconds(5));

        bool foundAfterInterval = classifier.TryClassifyWithProfile(
            "loot-a",
            new PluginLootClassificationContext(Item(), Properties(), []),
            out _);
        Assert.True(foundAfterInterval);
        Assert.Equal(2, host.VtankReadCount);
    }

    private static string ReadFixture(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "vtank", name));

    private static LootRule AppraisalDependentRule() => new()
    {
        Name = "Spell",
        VtankRequirements = [new VtankLootRequirement { Type = 9, Payload = "" }],
    };

    private static PluginInventoryItem Item() => new(
        0u, 0u, "Test Item", 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u,
        1, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private static PluginItemProperties Properties(bool hasLongDesc = false) =>
        new(
            Ints: new Dictionary<uint, int>(),
            Int64s: new Dictionary<uint, long>(),
            Bools: new Dictionary<uint, bool>(),
            Floats: new Dictionary<uint, double>(),
            Strings: hasLongDesc
                ? new Dictionary<uint, string>
                {
                    [LongDescPropertyId] = "A description.",
                }
                : new Dictionary<uint, string>(),
            DataIds: new Dictionary<uint, uint>(),
            InstanceIds: new Dictionary<uint, uint>());

    private sealed class Host(
        IReadOnlyDictionary<string, string>? vtankFiles = null) : IPluginHost
    {
        private readonly Dictionary<string, string> _vtankFiles =
            vtankFiles is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(vtankFiles);

        public int VtankReadCount { get; private set; }

        internal void SetVtankText(string key, string text) =>
            _vtankFiles[key] = text;

        public bool HasUi => false;
        public IPluginLogger Log => NoOpLogger.Instance;
        public IGameState State => NoOpState.Instance;
        public IEvents Events => NoOpEvents.Instance;
        public ISelectionService Selection => NoOpSelection.Instance;
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation => NoOpAutomationSurface.Instance;
        public IPluginStorage VtankProfiles => new FakeVtankStorage(this);

        internal string? ReadVtankText(string key)
        {
            VtankReadCount++;
            return _vtankFiles.TryGetValue(key, out string? text) ? text : null;
        }

        private sealed class FakeVtankStorage(Host owner) : IPluginStorage
        {
            public bool IsAvailable => true;
            public string? ReadText(string key) => owner.ReadVtankText(key);
        }
    }

    private sealed class NoOpLogger : IPluginLogger
    {
        public static NoOpLogger Instance { get; } = new();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class NoOpState : IGameState
    {
        public static NoOpState Instance { get; } = new();
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class NoOpEvents : IEvents
    {
        public static NoOpEvents Instance { get; } = new();
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
        public static NoOpSelection Instance { get; } = new();
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

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

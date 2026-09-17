using System.Reflection;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

// ScopedPluginHost wraps the host's IAutomationSurface by hand-forwarding
// one property per member so it can keep the plugin's own chat filters and
// subscriptions revocable. Every member added to IAutomationSurface after
// that wrapper was written has a default interface implementation that
// falls back to NoOpAutomationSurface, so a forwarder that is never written
// compiles clean and silently hands every plugin the no-op surface instead
// of the host's real one. This test walks the interface by reflection so a
// future member cannot go unforwarded without a build-time-visible test
// failure.
// Navigation is forwarded as the host's own object only when that host's
// navigation cannot tell plugins apart (as this stub's cannot); a host whose
// navigation implements IScopedNavigationSource hands each plugin a view of
// its own, covered by ScopedNavigationTests.
public sealed class ScopedAutomationSurfaceTests
{
    [Fact]
    public void EveryAutomationMemberExceptChatIsTheHostsOwnObject()
    {
        var host = new StubHost();
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        IAutomationSurface innerAutomation = host.Automation;
        IAutomationSurface scopedAutomation = scoped.Automation;

        PropertyInfo[] properties = typeof(IAutomationSurface).GetProperties();

        // Sanity check: if this drops below the known count, the interface
        // shrank and the loop below silently checks less than intended.
        Assert.True(
            properties.Length >= 22,
            "IAutomationSurface should still have every member this test knows about.");

        var checkedMembers = new List<string>();
        foreach (PropertyInfo property in properties)
        {
            // IsAvailable is a bool: there is no host identity to preserve.
            // Chat is deliberately wrapped, not forwarded, so the scoped
            // host can revoke a plugin's filters and subscriptions on
            // unload.
            if (property.Name == nameof(IAutomationSurface.IsAvailable)
                || property.Name == nameof(IAutomationSurface.Chat))
            {
                continue;
            }

            object? innerValue = property.GetValue(innerAutomation);
            object? scopedValue = property.GetValue(scopedAutomation);
            Assert.True(
                ReferenceEquals(innerValue, scopedValue),
                "IAutomationSurface." + property.Name + " is not forwarded: "
                    + "the scoped surface returned a different object than "
                    + "the host's own automation surface (likely the "
                    + "interface's no-op default).");
            checkedMembers.Add(property.Name);
        }

        // Every property this loop actually walked should be one of the 18
        // known forwarders, guarding against the loop silently checking
        // zero properties if reflection ever returned nothing.
        Assert.Equal(20, checkedMembers.Count);

        scoped.Dispose();
    }

    [Fact]
    public void SwappingTheHostsAutomationSurfaceIsObservedByTheScopedSurface()
    {
        var mutableHost = new MutableStubHost();
        var surfaceA = new FakeAutomationSurface();
        var surfaceB = new FakeAutomationSurface();
        mutableHost.Automation = surfaceA;

        var scoped = new ScopedPluginHost(mutableHost, "example.plugin", "Example");

        // The wrapper must not have pinned surfaceA at construction: once
        // the host swaps in surfaceB, every forwarded member should follow.
        Assert.Same(surfaceA.Character, scoped.Automation.Character);
        Assert.Same(surfaceA.Combat, scoped.Automation.Combat);
        IPluginChat firstChatWrapper = scoped.Automation.Chat;

        mutableHost.Automation = surfaceB;

        Assert.Same(surfaceB.Character, scoped.Automation.Character);
        Assert.Same(surfaceB.Combat, scoped.Automation.Combat);

        // Chat is wrapped, not forwarded: swapping the live chat instance
        // must re-wrap lazily rather than keep serving the old plugin chat.
        IPluginChat secondChatWrapper = scoped.Automation.Chat;
        Assert.NotSame(firstChatWrapper, secondChatWrapper);

        scoped.Dispose();
    }

    [Fact]
    public void EveryPluginChatMemberIsForwardedByScopedPluginChat()
    {
        var recording = new RecordingIPluginChat();
        var host = new MutableStubHost
        {
            Automation = new SoloChatAutomationSurface(recording),
        };
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");
        IPluginChat chat = scoped.Automation.Chat;

        MethodInfo[] methods = typeof(IPluginChat)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);
        EventInfo[] events = typeof(IPluginChat)
            .GetEvents(BindingFlags.Public | BindingFlags.Instance);
        Assert.True(
            methods.Length == 7 && events.Length == 1,
            "IPluginChat should still have exactly the members this test "
                + "knows about (7 methods incl. event accessors, 1 event) -- "
                + "a member was added or removed without updating this test.");

        chat.CaptureMessages(0);
        Assert.Equal(1, recording.CaptureMessagesCalls);

        chat.PostSystemMessage("a");
        Assert.Equal(1, recording.PostSystemMessageCalls);

        chat.PostMessage("a", 5);
        Assert.Equal(1, recording.PostMessageCalls);

        chat.Submit("a");
        Assert.Equal(1, recording.SubmitCalls);

        chat.RegisterFilter(static _ => true);
        Assert.Equal(1, recording.FilterCount);

        chat.Received += static _ => { };
        Assert.Equal(1, recording.SubscriberCount);

        scoped.Dispose();
    }

    private sealed class SoloChatAutomationSurface(IPluginChat chat) : IAutomationSurface
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character { get; } = new FakeCharacterInfo();
        public ISpellCatalog Spells { get; } = new FakeSpellCatalog();
        public IMagicCommands Magic { get; } = new FakeMagicCommands();
        public IPluginChat Chat { get; } = chat;
    }

    private sealed class RecordingIPluginChat : IPluginChat
    {
        private readonly List<Func<PluginChatMessage, bool>> _filters = [];
        private Action<PluginChatMessage>? _received;

        internal int CaptureMessagesCalls { get; private set; }
        internal int PostSystemMessageCalls { get; private set; }
        internal int PostMessageCalls { get; private set; }
        internal int SubmitCalls { get; private set; }
        internal int FilterCount => _filters.Count;
        internal int SubscriberCount => _received?.GetInvocationList().Length ?? 0;

        public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence)
        {
            CaptureMessagesCalls++;
            return Array.Empty<PluginChatMessage>();
        }

        public void PostSystemMessage(string text) => PostSystemMessageCalls++;

        public void PostMessage(string text, int logTextType) => PostMessageCalls++;

        public bool Submit(string text)
        {
            SubmitCalls++;
            return true;
        }

        public IDisposable RegisterFilter(Func<PluginChatMessage, bool> suppress)
        {
            _filters.Add(suppress);
            return new Removal(this, suppress);
        }

        public event Action<PluginChatMessage> Received
        {
            add => _received += value;
            remove => _received -= value;
        }

        private sealed class Removal(
            RecordingIPluginChat owner,
            Func<PluginChatMessage, bool> suppress) : IDisposable
        {
            public void Dispose() => owner._filters.Remove(suppress);
        }
    }

    private sealed class StubHost : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui { get; } = NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = new FakeAutomationSurface();
        public IPluginStorage Storage { get; } = NoOpPluginStorage.Instance;
        public IPluginClipboard Clipboard { get; } = NoOpPluginClipboard.Instance;

        private sealed class SilentLogger : IPluginLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception? error = null) { }
        }

        private sealed class EmptyGameState : IGameState
        {
            public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
        }

        private sealed class InertSelection : ISelectionService
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

    private sealed class MutableStubHost : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui { get; } = NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; set; } = NoOpAutomationSurface.Instance;
        public IPluginStorage Storage { get; } = NoOpPluginStorage.Instance;
        public IPluginClipboard Clipboard { get; } = NoOpPluginClipboard.Instance;

        private sealed class SilentLogger : IPluginLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception? error = null) { }
        }

        private sealed class EmptyGameState : IGameState
        {
            public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
        }

        private sealed class InertSelection : ISelectionService
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

    // Every member returns its own distinct instance, never
    // NoOpAutomationSurface.Instance, so a scoped property that silently
    // falls through to the interface's no-op default is caught by reference
    // inequality instead of accidentally matching.
    private sealed class FakeAutomationSurface : IAutomationSurface
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character { get; } = new FakeCharacterInfo();
        public ISpellCatalog Spells { get; } = new FakeSpellCatalog();
        public IMagicCommands Magic { get; } = new FakeMagicCommands();
        public IPluginChat Chat { get; } = new FakeChat();
        public IDialogAutomation Dialogs { get; } = new FakeDialogAutomation();
        public ICombatAutomation Combat { get; } = new FakeCombatAutomation();
        public IEquipmentAutomation Equipment { get; } = new FakeEquipmentAutomation();
        public IItemAutomation Items { get; } = new FakeItemAutomation();
        public ILootAutomation Loot { get; } = new FakeLootAutomation();
        public IFellowshipAutomation Fellowship { get; } = new FakeFellowshipAutomation();
        public IEnchantmentAutomation Enchantments { get; } = new FakeEnchantmentAutomation();
        public INavigationAutomation Navigation { get; } = new FakeNavigationAutomation();
        public IWorldObjectAutomation Objects { get; } = new FakeWorldObjectAutomation();
        public IWorldTimeAutomation WorldTime { get; } = new FakeWorldTimeAutomation();
        public ILoginAutomation Login { get; } = new FakeLoginAutomation();
        public INetworkAutomation Network { get; } = new FakeNetworkAutomation();
        public IRecoveryAutomation Recovery { get; } = new FakeRecoveryAutomation();
        public IProjectileAutomation Projectiles { get; } = new FakeProjectileAutomation();
        public ISelectionAutomation Selection { get; } = new FakeSelectionAutomation();
        public ITradeAutomation Trade { get; } = new FakeTradeAutomation();
        public IVendorAutomation Vendor { get; } = new FakeVendorAutomation();
    }

    private sealed class FakeCharacterInfo : ICharacterInfo
    {
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
    }

    private sealed class FakeSpellCatalog : ISpellCatalog
    {
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs { get; } = [];
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            info = default;
            return false;
        }
    }

    private sealed class FakeMagicCommands : IMagicCommands
    {
        public bool IsCasting => false;
        public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Refused;
        public bool Cast(uint spellId) => false;
    }

    private sealed class FakeChat : IPluginChat
    {
        public void PostSystemMessage(string text) { }
    }

    private sealed class FakeDialogAutomation : IDialogAutomation;

    private sealed class FakeCombatAutomation : ICombatAutomation
    {
        public PluginCombatSnapshot Snapshot => default;
        public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(
            float maximumDistance) => [];
        public PluginCombatCommandResult EnterDefaultMode() =>
            new(PluginCombatCommandStatus.Unavailable);
        public PluginCombatCommandResult BeginPhysicalAttack(
            uint targetObjectId,
            PluginAttackHeight height,
            float power) => new(PluginCombatCommandStatus.Unavailable);
        public PluginCombatCommandResult ReleasePhysicalAttack() =>
            new(PluginCombatCommandStatus.Unavailable);
        public PluginCombatCommandResult AbortPhysicalAttack() =>
            new(PluginCombatCommandStatus.Unavailable);
    }

    private sealed class FakeEquipmentAutomation : IEquipmentAutomation;

    private sealed class FakeItemAutomation : IItemAutomation;

    private sealed class FakeLootAutomation : ILootAutomation;

    private sealed class FakeFellowshipAutomation : IFellowshipAutomation;

    private sealed class FakeEnchantmentAutomation : IEnchantmentAutomation;

    private sealed class FakeNavigationAutomation : INavigationAutomation
    {
        public PluginNavigationSnapshot Snapshot => default;
        public bool TryGetObject(uint objectId, out PluginNavigationObject value)
        {
            value = default;
            return false;
        }
        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent) =>
            PluginNavigationCommandStatus.Unavailable;
        public PluginNavigationCommandStatus ClearMovementIntent() =>
            PluginNavigationCommandStatus.Unavailable;
    }

    private sealed class FakeWorldObjectAutomation : IWorldObjectAutomation;

    private sealed class FakeWorldTimeAutomation : IWorldTimeAutomation;

    private sealed class FakeLoginAutomation : ILoginAutomation;

    private sealed class FakeNetworkAutomation : INetworkAutomation;

    private sealed class FakeRecoveryAutomation : IRecoveryAutomation;

    private sealed class FakeProjectileAutomation : IProjectileAutomation;

    private sealed class FakeSelectionAutomation : ISelectionAutomation;

    private sealed class FakeTradeAutomation : ITradeAutomation;

    private sealed class FakeVendorAutomation : IVendorAutomation;
}

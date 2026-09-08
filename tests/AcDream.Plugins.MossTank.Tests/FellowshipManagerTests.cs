using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class FellowshipManagerTests
{
    [Fact]
    public void XpTellQueuesAndRecruitsANearbyPlayerThroughTheHostCommand()
    {
        var automation = new FakeAutomation();
        automation.AddTell(2u, "Alice", "xp");
        var manager = new FellowshipManager(new FakeHost(automation));

        manager.Tick(0.1d, enabled: true);

        Assert.Equal(2u, automation.RecruitedObjectId);
        Assert.Contains(automation.Submitted, value => value.Contains(
            "I will recruit you in a moment", StringComparison.Ordinal));
        Assert.Contains("Alice", manager.WaitingNames);

        automation.Roster.Add(Member(2u, "Alice"));
        manager.Tick(0.1d, enabled: true);
        Assert.DoesNotContain("Alice", manager.WaitingNames);
    }

    [Fact]
    public void MemberVoteExecutesGiveLeaderAfterTheOfficialTwoMinuteWindow()
    {
        var automation = new FakeAutomation();
        automation.Roster.Add(Member(2u, "Alice"));
        automation.Roster.Add(Member(3u, "Bob"));
        var manager = new FellowshipManager(new FakeHost(automation));
        automation.AddTell(2u, "Alice", "startvote giveleader Bob");

        manager.Tick(0.1d, enabled: true);
        automation.AddTell(3u, "Bob", "vote 1 yes");
        manager.Tick(0.1d, enabled: true);
        manager.Tick(120d, enabled: true);

        Assert.Equal(3u, automation.AssignedLeaderObjectId);
        Assert.Contains(automation.Submitted, value => value.Contains(
            "passed (2/0)", StringComparison.Ordinal));
    }

    private static PluginFellowMember Member(uint id, string name) => new(
        id, name, 100u, 100u, 100u, 100u, 100u, 100u, 0f);

    private sealed class FakeHost(FakeAutomation automation) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new StubState();
        public IEvents Events { get; } = new StubEvents();
        public ISelectionService Selection { get; } = new StubSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = automation;
    }

    private sealed class StubLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class StubState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class StubEvents : IEvents
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

    private sealed class StubSelection : ISelectionService
    {
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }
        public bool Select(uint objectId) => false;
        public bool Clear() => false;
    }

    private sealed class FakeAutomation
        : IAutomationSurface, ICharacterInfo, IPluginChat,
          IFellowshipAutomation, INavigationAutomation
    {
        private ulong _sequence;
        private readonly List<PluginChatMessage> _messages = [];

        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic => NoOpAutomationSurface.Instance;
        public IPluginChat Chat => this;
        public IFellowshipAutomation Fellowship => this;
        public INavigationAutomation Navigation => this;
        public bool IsInWorld => true;
        public string Name => "Leader";
        public uint ObjectId => 1u;
        public uint CurrentHealth => 100u;
        public uint MaxHealth => 100u;
        public uint CurrentStamina => 100u;
        public uint MaxStamina => 100u;
        public uint CurrentMana => 100u;
        public uint MaxMana => 100u;
        public IReadOnlyList<PluginSkillInfo> Skills => [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            skill = default;
            return false;
        }

        public bool IsInFellowship => true;
        string IFellowshipAutomation.Name => "Test Fellow";
        public uint LeaderObjectId => 1u;
        public bool IsOpen { get; private set; } = true;
        public bool IsLocked => false;
        public int MemberCount => Roster.Count;
        public List<PluginFellowMember> Roster { get; } = [Member(1u, "Leader")];
        public IReadOnlyList<PluginFellowMember> CaptureMembers() =>
            Roster.Where(static member => member.ObjectId != 1u).ToArray();
        public IReadOnlyList<PluginFellowMember> CaptureRoster() => Roster.ToArray();
        public uint RecruitedObjectId { get; private set; }
        public uint DismissedObjectId { get; private set; }
        public uint AssignedLeaderObjectId { get; private set; }
        public PluginFellowshipCommandResult Recruit(uint targetObjectId)
        {
            RecruitedObjectId = targetObjectId;
            return new(PluginFellowshipCommandStatus.Accepted);
        }
        public PluginFellowshipCommandResult Dismiss(uint targetObjectId)
        {
            DismissedObjectId = targetObjectId;
            return new(PluginFellowshipCommandStatus.Accepted);
        }
        public PluginFellowshipCommandResult AssignLeader(uint targetObjectId)
        {
            AssignedLeaderObjectId = targetObjectId;
            return new(PluginFellowshipCommandStatus.Accepted);
        }
        public PluginFellowshipCommandResult SetOpen(bool isOpen)
        {
            IsOpen = isOpen;
            return new(PluginFellowshipCommandStatus.Accepted);
        }

        public PluginNavigationSnapshot Snapshot => new(
            true,
            false,
            1u,
            Position(0d),
            false,
            false);
        public bool TryGetObject(uint objectId, out PluginNavigationObject value)
        {
            value = objectId == 2u
                ? new PluginNavigationObject(2u, "Alice", Position(0.02d))
                : default;
            return value.ObjectId != 0u;
        }
        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent) => PluginNavigationCommandStatus.Accepted;
        public PluginNavigationCommandStatus ClearMovementIntent() =>
            PluginNavigationCommandStatus.Accepted;

        public List<string> Submitted { get; } = [];
        public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
            _messages.Where(value => value.Sequence > afterSequence).ToArray();
        public void PostSystemMessage(string text)
        {
        }
        public bool Submit(string text)
        {
            Submitted.Add(text);
            return true;
        }
        public void AddTell(uint senderId, string sender, string text) =>
            _messages.Add(new PluginChatMessage(
                ++_sequence, senderId, 3, sender, text, string.Empty));

        private static PluginNavigationPosition Position(double eastWest) => new(
            0x00010001u, eastWest, 0d, 0d, 0f, true);
    }
}

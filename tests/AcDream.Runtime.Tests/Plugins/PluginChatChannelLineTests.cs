using AcDream.Core.Chat;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The channel number and the printed line a plugin is handed with each chat
/// line, taken from the real ingestion route.
/// </summary>
/// <remarks>
/// The numbered channels (patron, vassals, followers, co-vassals, allegiance
/// broadcast, the advocate channels) have no name on the wire; each is worded
/// by its number. Without the number a plugin cannot tell them apart, and a
/// plugin that matches against the printed line cannot rebuild it.
/// </remarks>
public sealed class PluginChatChannelLineTests
{
    [Theory]
    [InlineData(0x1000u, "Your patron <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hi\"")]
    [InlineData(0x2000u, "Your vassal <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hi\"")]
    [InlineData(0x4000u, "Your follower <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hi\"")]
    [InlineData(0x1000000u, "[Co-Vassals] <Tell:IIDString:0:Bob>Bob<\\Tell> says, \"hi\"")]
    [InlineData(0x2000000u, "[Allegiance Broadcast] <Tell:IIDString:0:Bob>Bob<\\Tell> says, \"hi\"")]
    [InlineData(0x8u, "<Tell:IIDString:0:Bob>Bob<\\Tell> says on the Advocate 1 channel, \"hi\"")]
    public void ANumberedChannelLineReachesThePluginWithItsNumberAndPrintedLine(
        uint channelId, string printed)
    {
        using GameRuntime runtime = PluginChatRuntime.Create();
        using var surface = PluginChatRuntime.Bind(runtime);

        runtime.CommunicationOwner.Chat.OnChannelBroadcast(channelId, "Bob", "hi");

        PluginChatMessage message = Assert.Single(surface.CaptureMessages(0UL));
        Assert.Equal(channelId, message.ChannelId);
        Assert.Equal(printed, message.DisplayText);
        // The parts stay as they were: sender and words apart.
        Assert.Equal("Bob", message.Sender);
        Assert.Equal("hi", message.Text);
    }

    /// <summary>
    /// The entry's number field holds a system line's text class and a death
    /// line's killer; neither is a channel, and a plugin must not read one as
    /// such.
    /// </summary>
    [Fact]
    public void ALineThatIsNoChannelLineCarriesNoChannelNumber()
    {
        using GameRuntime runtime = PluginChatRuntime.Create();
        using var surface = PluginChatRuntime.Bind(runtime);

        runtime.CommunicationOwner.Chat.OnSystemMessage(
            "You have entered the Allegiance channel.", chatType: 0x12u);

        PluginChatMessage message = Assert.Single(surface.CaptureMessages(0UL));
        Assert.Equal(0u, message.ChannelId);
        Assert.Equal("You have entered the Allegiance channel.", message.DisplayText);
    }

    /// <summary>
    /// A filter decides before the line is shown, on the same line a reader
    /// later receives: the same number, the same printed line.
    /// </summary>
    [Fact]
    public void AFilterSeesTheSameChannelNumberAndPrintedLineAsAReader()
    {
        using GameRuntime runtime = PluginChatRuntime.Create();
        using var surface = PluginChatRuntime.Bind(runtime);
        var offered = new List<PluginChatMessage>();
        using IDisposable filter = surface.RegisterFilter(message =>
        {
            offered.Add(message);
            return false;
        });

        runtime.CommunicationOwner.Chat.OnChannelBroadcast(0x2000u, "Bob", "hi");

        PluginChatMessage candidate = Assert.Single(offered);
        PluginChatMessage received = Assert.Single(surface.CaptureMessages(0UL));
        Assert.Equal(0x2000u, candidate.ChannelId);
        Assert.Equal(received.ChannelId, candidate.ChannelId);
        Assert.Equal(received.DisplayText, candidate.DisplayText);
        Assert.StartsWith("Your vassal ", candidate.DisplayText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A filter is offered the line before the language filter censors it,
    /// the order the original client offers a line to its plugin host before
    /// censoring it; a reader gets the line as it is printed, censored. Each
    /// message is consistent in itself: its text and its printed line are
    /// both uncensored for the filter and both censored for the reader.
    /// </summary>
    /// <remarks>
    /// Mutation check, run 2026-09-25: censoring before the filters are
    /// consulted turns this red.
    /// </remarks>
    [Fact]
    public void AFilterSeesTheLineBeforeCensoringAndAReaderSeesItAsPrinted()
    {
        using GameRuntime runtime = PluginChatRuntime.Create();
        using var surface = PluginChatRuntime.Bind(runtime);
        runtime.CommunicationOwner.FilterLanguageSource = static () => true;
        runtime.CommunicationOwner.FilterLanguagePatterns = ["zork"];
        var offered = new List<PluginChatMessage>();
        using IDisposable filter = surface.RegisterFilter(message =>
        {
            offered.Add(message);
            return false;
        });

        runtime.CommunicationOwner.Chat.OnTellReceived(
            "Bob", "a zork b", senderGuid: 0x5000_0077u, logTextType: 0x03u);

        PluginChatMessage candidate = Assert.Single(offered);
        PluginChatMessage received = Assert.Single(surface.CaptureMessages(0UL));
        Assert.Equal("a zork b", candidate.Text);
        Assert.EndsWith("tells you, \"a zork b\"", candidate.DisplayText, StringComparison.Ordinal);
        Assert.Equal("a **** b", received.Text);
        Assert.EndsWith("tells you, \"a **** b\"", received.DisplayText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The printed line is the chat window's line, word for word: the two
    /// are taken from the one wording, not composed twice.
    /// </summary>
    [Fact]
    public void ThePrintedLineIsTheChatWindowsLine()
    {
        using GameRuntime runtime = PluginChatRuntime.Create();
        using var surface = PluginChatRuntime.Bind(runtime);

        runtime.CommunicationOwner.Chat.OnTellReceived(
            "Bob", "hello", senderGuid: 0x5000_0077u, logTextType: 0x03u);

        PluginChatMessage message = Assert.Single(surface.CaptureMessages(0UL));
        ChatEntry entry = runtime.CommunicationOwner.Chat.Snapshot()[^1];
        Assert.Equal(ChatLineWording.FormatTagged(entry), message.DisplayText);
        Assert.Equal(
            "<Tell:IIDString:1342177399:Bob>Bob<\\Tell> tells you, \"hello\"",
            message.DisplayText);
    }
}

/// <summary>A runtime with a plugin surface bound to it, and nothing else.</summary>
internal static class PluginChatRuntime
{
    internal static GameRuntime Create()
    {
        var operations = new InertOperations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    internal static RuntimeAutomationSurface Bind(GameRuntime runtime)
    {
        var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        return surface;
    }

    private sealed class InertOperations :
        AcDream.Runtime.Gameplay.IRuntimeCombatAttackOperations,
        AcDream.Runtime.Gameplay.IRuntimeCombatTargetOperations,
        AcDream.Runtime.Gameplay.IRuntimeCombatModeOperations,
        AcDream.Runtime.Gameplay.IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(
            AcDream.Core.Combat.AttackHeight height, float power, bool allowAutoTarget) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<AcDream.Core.Items.ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(AcDream.Core.Combat.CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            AcDream.Core.Spells.SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}

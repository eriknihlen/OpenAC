using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What a plugin reading chat is told about a line, on both clients: which
/// numbered channel it came on and the whole line as the chat window prints
/// it. A plugin that matches its conditions against the printed line -- the
/// channel's own sentence, the linked speaker, the quoted words -- can only
/// rebuild a patron's or a vassal's line if it is told the channel, and is
/// better off not rebuilding anything at all.
///
/// Every value is asserted outright as well as recorded: two clients that
/// both handed a plugin nothing would write identical transcripts.
///
/// Mutation check (2026-09-25), run: leaving the channel number and the
/// printed line out of the runtime's projection of a chat entry turned both
/// scenarios red on both arms; restoring it turned them green.
///
/// Mutation checks (2026-09-25), run, one at a time: dropping the tell's
/// listener at the game-event wiring turned only the thought scenario red;
/// wording a ranged line "shouts" turned only the shout scenario red;
/// classing a speakerless channel line as heard rather than sent turned only
/// the fellowship-broadcast scenario red. Each went green on restoring it.
/// </summary>
public sealed class PluginChatLineParityTests
{
    private const uint PatronChannel = 0x1000u;
    private const uint Speaker = 0x5000_0077u;

    [Fact]
    public void AChannelLineTellsAPluginItsChannelAndPrintedLineOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var seen = new List<PluginChatMessage>();
            arm.Host.Automation.Chat.Received += seen.Add;

            transcript.Step("a patron speaks on the channel");
            arm.Server.ChannelBroadcast(PatronChannel, "Bob", "hi");
            arm.Advance();

            PluginChatMessage line = Assert.Single(seen);
            transcript.Record("channel", line.ChannelId);
            transcript.Record("printed", line.DisplayText);
            Assert.Equal(PatronChannel, line.ChannelId);
            Assert.Equal(
                "Your patron <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hi\"",
                line.DisplayText);
        });

    [Fact]
    public void ATellCarriesNoChannelAndItsPrintedLineOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var seen = new List<PluginChatMessage>();
            arm.Host.Automation.Chat.Received += seen.Add;

            transcript.Step("someone tells the character something");
            arm.Server.Tell(
                "hello",
                "Bob",
                senderGuid: Speaker,
                targetGuid: ParityWorld.Player,
                chatType: 0x03u);
            arm.Advance();

            PluginChatMessage line = Assert.Single(seen);
            transcript.Record("channel", line.ChannelId);
            transcript.Record("printed", line.DisplayText);
            Assert.Equal(0u, line.ChannelId);
            Assert.Equal(
                $"<Tell:IIDString:{Speaker}:Bob>Bob<\\Tell> tells you, \"hello\"",
                line.DisplayText);
        });

    /// <summary>
    /// A tell the character sent itself comes back addressed to itself, and
    /// the chat window prints it as a thought.
    /// </summary>
    [Fact]
    public void ATellToOneselfReadsAsAThoughtOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var seen = new List<PluginChatMessage>();
            arm.Host.Automation.Chat.Received += seen.Add;

            transcript.Step("the character tells itself something");
            arm.Server.Tell(
                "remember the key",
                "Acdream",
                senderGuid: ParityWorld.Player,
                targetGuid: ParityWorld.Player,
                chatType: 0x03u);
            arm.Advance();

            PluginChatMessage line = Assert.Single(seen);
            transcript.Record("printed", line.DisplayText);
            Assert.Equal("You think, \"remember the key\"", line.DisplayText);
        });

    /// <summary>
    /// A shout carries no verb of its own: it prints as speech under the
    /// name it arrived with.
    /// </summary>
    [Fact]
    public void AShoutPrintsAsSpeechOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var seen = new List<PluginChatMessage>();
            arm.Host.Automation.Chat.Received += seen.Add;

            transcript.Step("a guard shouts");
            arm.Server.RangedSpeech("Halt!", "Guard", 0x8000_1234u);
            arm.Advance();

            PluginChatMessage line = Assert.Single(seen);
            transcript.Record("printed", line.DisplayText);
            Assert.Equal("Guard says, \"Halt!\"", line.DisplayText);
        });

    /// <summary>
    /// The fellowship broadcast with no speaker takes the fellowship's text
    /// class, which is what a plugin tells fellowship lines apart by.
    /// </summary>
    [Fact]
    public void ASpeakerlessFellowshipBroadcastIsAFellowshipLineOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var seen = new List<PluginChatMessage>();
            arm.Host.Automation.Chat.Received += seen.Add;

            transcript.Step("the fellowship is told something");
            arm.Server.ChannelBroadcast(0x0400_0000u, "", "Bob has joined the fellowship.");
            arm.Advance();

            PluginChatMessage line = Assert.Single(seen);
            transcript.Record("type", line.LogTextType);
            transcript.Record("printed", line.DisplayText);
            Assert.Equal(0x13, line.LogTextType);
            Assert.Equal("Bob has joined the fellowship.", line.DisplayText);
        });
}

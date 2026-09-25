using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A plugin turning one of the character's own options on or off, run
/// against both clients.
///
/// Parity evidence, in the sense the suite README gives the word: the plugin
/// surface is shared, but each client hands it its own session command
/// adapter, and the change reaches the server through that client's own
/// outbound route. Each step is ASSERTED per arm -- on the answer the plugin
/// was given, on the value the plugin reads back, and on the option id and
/// value that really left the client -- so a client that composed nothing is
/// caught rather than agreeing with the other client about doing nothing.
///
/// Mutation checks (2026-09-24), each run: pointing the surface's change at
/// the next option number turned both change scenarios red (the id read back
/// off the wire, and the value read back); answering every name as unknown
/// turned the same two red; and leaving the surface unforwarded on the
/// scoped host is caught by the scoped surface census instead.
/// </summary>
public sealed class CharacterOptionParityTests
{
    /// <summary>The client action that changes one option on its own.</summary>
    private const uint SetSingleOptionAction = 0x0005u;

    /// <summary>The option number of accepting loot permits.</summary>
    private const uint AcceptLootPermits = 0x10u;

    /// <summary>
    /// Accepting loot permits is one of the options the server is told
    /// about the moment it changes. Both clients send that one change, for
    /// that option, with the value asked for.
    /// </summary>
    [Fact]
    public void AnOptionTheServerSavesAtOnceGoesOutOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ICharacterOptionsAutomation options = StageWorld(arm);

            transcript.Step("turn on accepting loot permits");
            PluginCharacterOptionResult result =
                options.Set("acceptlootpermits", true);
            Record(transcript, "set", result);
            arm.Advance();

            Assert.Equal(PluginCharacterOptionStatus.Accepted, result.Status);
            Assert.True(options.TryGet("AcceptLootPermits", out bool now));
            Assert.True(now);
            ParityOutbound sent = Assert.Single(Sent(arm, SetSingleOptionAction));
            Assert.Equal(AcceptLootPermits, Word(sent, 12));
            Assert.Equal(1u, Word(sent, 16));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Accepting gifts is saved with the whole option set later, not on its
    /// own. Both clients change their own copy now and send nothing yet.
    /// </summary>
    [Fact]
    public void AnOptionSavedWithTheSetChangesAtOnceAndSendsNothingYetOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ICharacterOptionsAutomation options = StageWorld(arm);
            Assert.True(options.TryGet("AllowGive", out bool before));

            transcript.Step("flip accepting gifts");
            PluginCharacterOptionResult result = options.Set("AllowGive", !before);
            Record(transcript, "set", result);
            arm.Advance();

            Assert.Equal(PluginCharacterOptionStatus.Accepted, result.Status);
            Assert.True(options.TryGet("AllowGive", out bool after));
            Assert.Equal(!before, after);
            Assert.Empty(Sent(arm, SetSingleOptionAction));
            transcript.Record("after", after.ToString());
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// A name no option has is refused for the name on both clients, and
    /// nothing is sent. The option list both clients offer is the same, and
    /// carries the options page's own names.
    /// </summary>
    [Fact]
    public void AnUnknownNameIsRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ICharacterOptionsAutomation options = StageWorld(arm);

            transcript.Step("set an option that does not exist");
            PluginCharacterOptionResult result = options.Set("NoSuchOption", true);
            Record(transcript, "set", result);
            arm.Advance();

            Assert.Equal(PluginCharacterOptionStatus.UnknownOption, result.Status);
            Assert.False(options.TryGet("NoSuchOption", out _));
            Assert.False(options.TryGet("6", out _));
            Assert.Empty(Sent(arm, SetSingleOptionAction));
            Assert.Contains("AllowGive", options.Names);
            Assert.Contains("HearGeneralChat", options.Names);
            transcript.Record("names", string.Join(",", options.Names));
            transcript.RecordOutbound(arm);
        });

    private static ICharacterOptionsAutomation StageWorld(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        _ = arm.Operations.TakeOutbound();
        ICharacterOptionsAutomation options = arm.Host.Automation.CharacterOptions;
        Assert.True(options.IsAvailable);
        return options;
    }

    private static IReadOnlyList<ParityOutbound> Sent(ParityArm arm, uint action)
        => [.. arm.Operations.Outbound.Where(
            message => message.GameAction == action)];

    private static uint Word(ParityOutbound message, int offset)
    {
        byte[] body = Convert.FromHexString(message.Body);
        return System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(body.AsSpan(offset));
    }

    private static void Record(
        ParityTranscript transcript,
        string key,
        PluginCharacterOptionResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }
}

using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Leaving the world, as a plugin hears it, on both clients. A plugin that
/// tracks what the character carries saves it from its Logoff handler, so it
/// has to hear Logoff while the session is still whole: told after the
/// teardown, it sees every item released first and reads that as the player
/// dropping everything. A logoff straight into the next character has to
/// read as a leave and then an arrival, or a plugin believes it is still
/// logged off while the next character plays.
///
/// Parity evidence: the leave reaches each client's own plugin event sink
/// and its own plugin host, over each client's own session bindings (what
/// they detach, reset and bind again at the logoff). Both clients complete a
/// logoff the same way once the server has confirmed it, through the
/// session controller they share, which is what the scenario calls; each
/// client's own driver of that call needs the server's confirmation and a
/// drawn world, and is not reached here.
///
/// Every step is asserted outright as well as recorded: two clients that
/// told a plugin nothing would write identical transcripts.
///
/// Mutation check (2026-09-25), run: moving the controller's leaving-world
/// announcement after the session reset in the logoff completion turned
/// both scenarios red (the handler read no kit and no character name);
/// making the windowless client's lifecycle delivery a no-op turned both red
/// on that arm, and making the windowed client's Logoff delivery a no-op
/// turned both red on that arm; dropping the lifecycle gate's re-entry check
/// turned the switch scenario red on its own (no arrival after the leave).
/// Restoring each turned them green.
/// </summary>
public sealed class LogoffParityTests
{
    private const uint SecondCharacter = 0x5000_0002u;
    private const string SecondCharacterName = "Parity Second";

    [Fact]
    public void LogoffReachesAPluginBeforeTheSessionIsTornDownOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.StageCarriedItems(arm.Runtime);
            arm.Advance();
            var order = new List<string>();
            arm.Host.Events.Logoff += () => order.Add(
                "logoff carrying "
                + arm.Host.Automation.Items.CaptureOwnedItems()
                    .Count(static item => item.ObjectId == ParityWorld.Kit));

            transcript.Step("the character logs off to the character list");
            Assert.True(arm.Runtime.Session
                .CompleteCharacterLogOff(arm.Runtime.Generation).Accepted);
            arm.Runtime.SyncLifecycleEmission();
            arm.Advance();
            // The reset that ends the stay has released the kit by now.
            order.Add(
                "after, kit held "
                + (arm.Runtime.InventoryOwner.Objects.Get(ParityWorld.Kit)
                    is not null));
            transcript.Record("order", string.Join("|", order));
            // Told once, while the kit could still be read, and the teardown
            // came after.
            Assert.Equal(
                ["logoff carrying 1", "after, kit held False"],
                order);
        });

    [Fact]
    public void ALogoffIntoTheNextCharacterIsLogoffThenLoginOnBothClients() =>
        ParityScenario.RunFromLogin(static (arm, transcript) =>
        {
            arm.Operations.AddCharacter(SecondCharacter, SecondCharacterName);
            arm.EnterWorld();
            var order = new List<string>();
            arm.Host.Events.Logoff += () => order.Add(
                "logoff " + arm.Host.Automation.Character.Name);
            arm.Host.Events.LoginComplete += () => order.Add(
                "login " + arm.Host.Automation.Character.Name);

            transcript.Step("the next character is chosen");
            Assert.True(arm.Host.Automation.Login.SetNextLogin(SecondCharacter));

            transcript.Step("the character logs off into it");
            Assert.True(arm.Runtime.Session
                .CompleteCharacterLogOff(arm.Runtime.Generation).Accepted);
            arm.Runtime.SyncLifecycleEmission();
            arm.Advance();
            transcript.Record("order", string.Join("|", order));
            Assert.Equal(
                ["logoff Parity", "login " + SecondCharacterName],
                order);
            transcript.Record("inWorld", arm.Runtime.Session.IsInWorld);
            Assert.True(arm.Runtime.Session.IsInWorld);
        });
}

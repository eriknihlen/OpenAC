using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote.Tests;

public sealed class RemoteCommandApplierTests
{
    private static (RemoteTestHost Host, DrakBotPlugin Bot, RemoteCommandApplier Commands, RemoteStatusBuilder Status) Setup()
    {
        var host = new RemoteTestHost();
        (DrakBotPlugin bot, DrakBotRemotePlugin remote) = host.Plugins();
        return (host, bot, remote.Commands!, remote.Status!);
    }

    [Fact]
    public void TogglesAndTheMacroAreSetSemantics()
    {
        (RemoteTestHost host, DrakBotPlugin bot, RemoteCommandApplier commands, _) = Setup();
        BotController controller = bot.Controller!;

        commands.Apply(new RemoteCommand("macro", "true"), 1d);
        Assert.True(controller.Engine.IsRunning);
        commands.Apply(new RemoteCommand("macro", "true"), 2d);
        Assert.True(controller.Engine.IsRunning);
        commands.Apply(new RemoteCommand("macro", "false"), 3d);
        Assert.False(controller.Engine.IsRunning);

        commands.Apply(new RemoteCommand("combat", "off"), 4d);
        commands.Apply(new RemoteCommand("buffing", "0"), 4d);
        commands.Apply(new RemoteCommand("navigation", "false"), 4d);
        commands.Apply(new RemoteCommand("looting", "no"), 4d);
        commands.Apply(new RemoteCommand("meta", "1"), 4d);
        BotProfile profile = controller.Profile;
        Assert.False(profile.Combat.Enabled);
        Assert.False(profile.Buffs.Enabled);
        Assert.False(profile.Navigation.Enabled);
        Assert.False(profile.Loot.Enabled);
        Assert.True(profile.Meta.Enabled);
        Assert.Contains(host.Log.Lines, entry => entry.Contains("remote: combat=off", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePickersTakeAnIndexIntoTheListsTheStatusCarriedOrAName()
    {
        (RemoteTestHost host, DrakBotPlugin bot, RemoteCommandApplier commands, RemoteStatusBuilder status) = Setup();
        BotController controller = bot.Controller!;
        host.Storage.WriteText("loot/gems.utl", "UTL\n1\n0\n");
        host.Storage.WriteText("loot/rares.utl", "UTL\n1\n0\n");
        controller.TryAddWaypoint(out _);
        controller.SaveRoute("loop");
        controller.SaveProfile("hunting");
        status.Build(0d); // refreshes the lists

        Assert.Equal(["gems", "rares"], status.LootProfiles);
        commands.Apply(new RemoteCommand("lootProfile", "1"), 1d);
        Assert.Equal("rares", controller.Profile.Loot.UtlProfile);
        commands.Apply(new RemoteCommand("lootProfile", "gems"), 1d);
        Assert.Equal("gems", controller.Profile.Loot.UtlProfile);
        commands.Apply(new RemoteCommand("lootProfile", "-1"), 1d);
        Assert.Equal(string.Empty, controller.Profile.Loot.UtlProfile);

        Assert.Contains("loop", status.NavProfiles);
        controller.Navigation.SetRoute(null);
        commands.Apply(new RemoteCommand("navProfile", status.NavProfiles.ToList().IndexOf("loop").ToString(System.Globalization.CultureInfo.InvariantCulture)), 1d);
        Assert.Equal("loop", controller.Navigation.Route?.Name);
        commands.Apply(new RemoteCommand("navProfile", "none"), 1d);
        Assert.Null(controller.Navigation.Route);

        Assert.Contains("hunting", status.Profiles);
        commands.Apply(new RemoteCommand("settingsProfile", "hunting"), 1d);
        Assert.Equal("hunting", controller.Profile.Name);
    }

    [Fact]
    public void HeldDirectionsComposeOneIntentAndAQuietHoldIsLetGoOf()
    {
        (RemoteTestHost host, _, RemoteCommandApplier commands, _) = Setup();
        FakeAutomationSurface surface = host.Surface;

        commands.Apply(new RemoteCommand("moveStart", "forward"), 1d);
        Assert.True(surface.Intent?.Forward);
        Assert.True(commands.IsHolding);
        commands.Apply(new RemoteCommand("moveStart", "left"), 1.2d);
        Assert.True(surface.Intent?.Forward);
        Assert.True(surface.Intent?.TurnLeft);
        commands.Apply(new RemoteCommand("moveStop", "forward"), 1.4d);
        Assert.False(surface.Intent?.Forward);
        Assert.True(surface.Intent?.TurnLeft);

        // The phone keeps re-sending a held start; a release does not re-arm,
        // so without one the hold expires MoveHoldSeconds after the last start.
        commands.Drain(1.2d + RemoteCommandApplier.MoveHoldSeconds - 0.1d);
        Assert.True(commands.IsHolding);
        commands.Drain(1.2d + RemoteCommandApplier.MoveHoldSeconds + 0.1d);
        Assert.False(commands.IsHolding);
        Assert.Null(surface.Intent);
        Assert.Equal("move:clear", surface.Commands[^1]);

        commands.Apply(new RemoteCommand("moveStart", "back"), 10d);
        commands.Apply(new RemoteCommand("moveStop", "stop"), 10.1d);
        Assert.Null(surface.Intent);
        Assert.False(commands.IsHolding);
    }

    [Fact]
    public void AssessGoesOutOnceForARepeatedTap()
    {
        (RemoteTestHost host, _, RemoteCommandApplier commands, _) = Setup();

        commands.Apply(new RemoteCommand("assess", "1342177281"), 1d);
        commands.Apply(new RemoteCommand("assess", "1342177281"), 2d);
        commands.Apply(new RemoteCommand("assess", "1342177281"), 1d + RemoteCommandApplier.AssessRepeatSeconds);

        Assert.Equal(2, host.Surface.Commands.Count(command => command == "identify:1342177281"));
    }

    [Fact]
    public void ChatClearBusyAndSettingsReachTheirTargets()
    {
        (RemoteTestHost host, DrakBotPlugin bot, RemoteCommandApplier commands, _) = Setup();

        commands.Apply(new RemoteCommand("sendChat", "/drakbot status"), 1d);
        Assert.Contains("chat:/drakbot status", host.Surface.Commands);

        commands.Apply(new RemoteCommand("clearBusy", "true"), 1d);
        Assert.Equal(1, host.Surface.BusyClears);

        commands.Apply(new RemoteCommand("setSetting", "{\"key\":\"vitals.healBelow\",\"value\":0.5}"), 1d);
        Assert.Equal(0.5, bot.Controller!.Profile.Vitals.HealBelow);

        commands.Apply(new RemoteCommand("setSetting", "{\"key\":\"vitals.healBelow\",\"value\":\"lots\"}"), 1d);
        Assert.Equal(0.5, bot.Controller!.Profile.Vitals.HealBelow);
        Assert.Contains(host.Log.Lines, entry => entry.Contains("takes a number", StringComparison.Ordinal));

        commands.Apply(new RemoteCommand("botCommand", "/drakbot start"), 1d);
        Assert.True(bot.Controller!.Engine.IsRunning);

        commands.Apply(new RemoteCommand("forceRebuff", "true"), 1d);
        Assert.True(bot.Controller!.Buffs.IsForceRebuffPending);
        commands.Apply(new RemoteCommand("cancelRebuff", "true"), 1d);
        Assert.False(bot.Controller!.Buffs.IsForceRebuffPending);
    }

    [Fact]
    public void CloseClientNeedsAHostThatOffersIt()
    {
        var host = new RemoteTestHost();
        (_, DrakBotRemotePlugin withoutClose) = host.Plugins();
        withoutClose.Commands!.Apply(new RemoteCommand("closeClient", "true"), 1d);
        Assert.Contains(host.Log.Lines, entry => entry.Contains("does not close", StringComparison.Ordinal));

        bool closed = false;
        var closable = new RemoteTestHost();
        (_, DrakBotRemotePlugin withClose) = closable.Plugins(new RemoteHostServices { CloseClient = () => closed = true });
        withClose.Commands!.Apply(new RemoteCommand("closeClient", "true"), 1d);
        Assert.True(closed);
    }

    [Fact]
    public void QueuedCommandsApplyOnTheDrainInOrderUpToTheCap()
    {
        (_, DrakBotPlugin bot, RemoteCommandApplier commands, _) = Setup();
        for (int index = 0; index < RemoteCommandApplier.DrainCap + 5; index++)
            commands.Enqueue(new RemoteCommand("macro", index % 2 == 0 ? "on" : "off"));

        commands.Drain(1d);
        Assert.Equal(5, commands.Pending);
        Assert.False(bot.Controller!.Engine.IsRunning); // the 64th (index 63) said off
        commands.Drain(2d);
        Assert.Equal(0, commands.Pending);
        Assert.True(bot.Controller!.Engine.IsRunning);  // the last (index 68) said on
    }

    [Theory]
    [InlineData("/drakbot patrol", "patrol")]
    [InlineData("/bot goto 41.5N 34.2E", "goto 41.5N 34.2E")]
    [InlineData("patrol stop", "patrol stop")]
    public void BotVerbsShedTheirPrefix(string sent, string verb) =>
        Assert.Equal(verb, RemoteCommandApplier.BotVerb(sent));
}

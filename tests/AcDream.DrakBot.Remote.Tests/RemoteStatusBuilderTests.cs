using System.Text;
using System.Text.Json;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote.Tests;

public sealed class RemoteStatusBuilderTests
{
    private static JsonElement Client(byte[] document, out JsonDocument owner)
    {
        owner = JsonDocument.Parse(Encoding.UTF8.GetString(document));
        return owner.RootElement.GetProperty("clients")[0];
    }

    private static PluginInventoryItem Item(uint id, string name, uint equipped = 0u, int stack = 1, PluginObjectClass objectClass = PluginObjectClass.Misc) =>
        new(id, 100u, name, 0u, 0u, 0u, 0u, equipped, 0u, 0u, 0u, stack, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0)
        {
            ObjectClass = objectClass,
            IconId = 0x06001234u,
            Burden = 50,
            Value = 1200,
        };

    [Fact]
    public void TheDocumentCarriesTheClientTheBotAndTheCharacter()
    {
        var host = new RemoteTestHost();
        FakeAutomationSurface surface = host.Surface;
        surface.Name = "Buffy";
        surface.CurrentHealth = 80;
        surface.MaxHealth = 120;
        surface.Attributes = [new PluginAttributeInfo(0, "Strength", 100)];
        surface.OwnedItems.Add(Item(0x80000001u, "Lead Scarab", stack: 40, objectClass: PluginObjectClass.SpellComponent));
        surface.OwnedItems.Add(Item(0x80000002u, "Prismatic Taper", stack: 250, objectClass: PluginObjectClass.SpellComponent));
        surface.OwnedItems.Add(Item(0x80000003u, "Breastplate", equipped: 0x400u, objectClass: PluginObjectClass.Armor));
        surface.Properties[0x80000003u] = new PluginItemProperties(
            new Dictionary<uint, int> { [28] = 320, [19] = 5000, [105] = 8 },
            new Dictionary<uint, long>(),
            new Dictionary<uint, bool>(),
            new Dictionary<uint, double> { [13] = 1.2, [14] = 1.0, [15] = 1.0, [16] = 0.8, [17] = 1.0, [18] = 1.0, [19] = 1.0 },
            new Dictionary<uint, string> { [16] = "A fine plate." },
            new Dictionary<uint, uint>(),
            new Dictionary<uint, uint>());
        surface.Hear("hello there", "Bob");
        (DrakBotPlugin bot, DrakBotRemotePlugin remote) = host.Plugins();
        bot.Enable();
        remote.Enable();
        bot.Controller!.Start();
        host.Events.FireTick(0.1);

        JsonElement client = Client(remote.Status!.Build(1d), out JsonDocument owner);
        using (owner)
        {
            Assert.Equal(RemoteStatusBuilder.Schema, owner.RootElement.GetProperty("schema").GetString());
            Assert.Equal(1, owner.RootElement.GetProperty("clientCount").GetInt32());
            Assert.False(owner.RootElement.GetProperty("capabilities").GetProperty("icons").GetBoolean());
            Assert.True(owner.RootElement.GetProperty("capabilities").GetProperty("movement").GetBoolean());

            Assert.Equal(Environment.ProcessId, client.GetProperty("pid").GetInt32());
            Assert.Equal("Buffy", client.GetProperty("character").GetString());
            Assert.True(client.GetProperty("inWorld").GetBoolean());
            Assert.True(client.GetProperty("macroRunning").GetBoolean());
            Assert.Equal("botting", client.GetProperty("state").GetString());
            Assert.Equal("Idle", client.GetProperty("botAction").GetString()); // running, nothing to do
            Assert.Equal(80u, client.GetProperty("player").GetProperty("hp").GetUInt32());
            Assert.Equal(120u, client.GetProperty("player").GetProperty("maxHp").GetUInt32());
            Assert.True(client.GetProperty("combatEnabled").GetBoolean());
            Assert.Equal("default", client.GetProperty("profile").GetString());
            Assert.Equal("None", client.GetProperty("navProfile").GetString());
            Assert.Equal(-1, client.GetProperty("selectedNavIdx").GetInt32());

            Assert.Equal(40, client.GetProperty("scarabs").GetInt32());
            Assert.Equal(250, client.GetProperty("tapers").GetInt32());
            JsonElement scarab = Assert.Single(client.GetProperty("scarabsByType").EnumerateArray());
            Assert.Equal("Lead Scarab", scarab.GetProperty("name").GetString());
            Assert.Equal(1.0, client.GetProperty("burdenPct").GetDouble()); // 150 burden of 15000 capacity

            JsonElement gear = Assert.Single(client.GetProperty("equipment").EnumerateArray());
            Assert.Equal("Breastplate", gear.GetProperty("name").GetString());
            Assert.Equal(320, gear.GetProperty("armorLevel").GetInt32());
            Assert.Equal(1.2, gear.GetProperty("resist")[0].GetDouble());
            Assert.Equal("A fine plate.", gear.GetProperty("longDesc").GetString());

            JsonElement line = Assert.Single(client.GetProperty("recentChat").EnumerateArray());
            Assert.Equal("Bob: hello there", line.GetProperty("t").GetString());
        }
    }

    [Fact]
    public void StateFollowsTheWorldTheBotAndTheTick()
    {
        Assert.Equal(("loading", true), RemoteStatusBuilder.Classify(inWorld: false, fps: 60, running: true, ticks: 60));
        Assert.Equal(("hung", false), RemoteStatusBuilder.Classify(inWorld: true, fps: 0, running: true, ticks: 0));
        Assert.Equal(("idle", true), RemoteStatusBuilder.Classify(inWorld: true, fps: 60, running: false, ticks: 60));
        Assert.Equal(("wedged", false), RemoteStatusBuilder.Classify(inWorld: true, fps: 60, running: true, ticks: 0));
        Assert.Equal(("botting", true), RemoteStatusBuilder.Classify(inWorld: true, fps: 60, running: true, ticks: 60));
    }

    [Fact]
    public void ChatIsKeptToTheLastLinesAndNeverRepeated()
    {
        var host = new RemoteTestHost();
        (_, DrakBotRemotePlugin remote) = host.Plugins();
        for (int index = 0; index < RemoteStatusBuilder.ChatLines + 10; index++)
            host.Surface.Hear($"line {index}");

        JsonElement first = Client(remote.Status!.Build(1d), out JsonDocument firstOwner);
        using (firstOwner)
        {
            Assert.Equal(RemoteStatusBuilder.ChatLines, first.GetProperty("recentChat").GetArrayLength());
            Assert.Equal("line 10", first.GetProperty("recentChat")[0].GetProperty("t").GetString());
        }

        host.Surface.Hear("newest");
        JsonElement second = Client(remote.Status!.Build(2d), out JsonDocument secondOwner);
        using (secondOwner)
        {
            JsonElement chat = second.GetProperty("recentChat");
            Assert.Equal(RemoteStatusBuilder.ChatLines, chat.GetArrayLength());
            Assert.Equal("newest", chat[chat.GetArrayLength() - 1].GetProperty("t").GetString());
        }
    }

    [Fact]
    public void OutOfTheWorldTheDocumentStillParses()
    {
        var host = new RemoteTestHost();
        host.Surface.IsInWorld = false;
        (_, DrakBotRemotePlugin remote) = host.Plugins();

        JsonElement client = Client(remote.Status!.Build(1d), out JsonDocument owner);
        using (owner)
        {
            Assert.Equal("loading", client.GetProperty("state").GetString());
            Assert.Equal(-1, client.GetProperty("freeSlots").GetInt32());
            Assert.Equal(string.Empty, client.GetProperty("area").GetString());
        }
    }

    [Fact]
    public void CoordinatesReadLikeTheGame()
    {
        var north = new PluginNavigationPosition(0x00010100u, 34.21, 41.56, 0d, 0f, true);
        Assert.Equal("41.6N 34.2E", RemoteStatusBuilder.Coordinates(north));
        var south = new PluginNavigationPosition(0x00010100u, -12.04, -3.5, 0d, 0f, true);
        Assert.Equal("3.5S 12.0W", RemoteStatusBuilder.Coordinates(south));
    }
}

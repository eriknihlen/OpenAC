using System.Text;
using System.Text.Json;
using AcDream.DrakBot.Profiles;

namespace AcDream.DrakBot.Remote.Tests;

public sealed class RemoteSettingsBridgeTests
{
    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void TheFormIsFlatAndHidesTheNameAndTheDashboard()
    {
        BotProfile profile = BotProfile.Default with { Name = "hunting" };

        using JsonDocument form = JsonDocument.Parse(Encoding.UTF8.GetString(RemoteSettingsBridge.Build(profile)));
        JsonElement root = form.RootElement;

        Assert.Equal(RemoteSettingsBridge.Schema, root.GetProperty("schema").GetString());
        Assert.Equal("hunting", root.GetProperty("profile").GetString());
        JsonElement values = root.GetProperty("values");
        Assert.Equal(0.6, values.GetProperty("vitals.healBelow").GetDouble());
        Assert.True(values.GetProperty("combat.enabled").GetBoolean());
        Assert.Equal("melee", values.GetProperty("combat.style").GetString());
        Assert.Contains("Strength Self", values.GetProperty("buffs.spells").GetString());
        Assert.False(values.TryGetProperty("name", out _));
        Assert.DoesNotContain(values.EnumerateObject(), static property => property.Name.StartsWith("dashboard.", StringComparison.Ordinal));
        Assert.DoesNotContain(values.EnumerateObject(), static property => property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array);
    }

    [Fact]
    public void ANumberAndABoolAndAnEnumNameApply()
    {
        BotProfile profile = BotProfile.Default with { Name = "hunting" };

        BotProfile? healed = RemoteSettingsBridge.TryApply(profile, "vitals.healBelow", Value("0.45"), out string error);
        Assert.Null(error.Length == 0 ? null : error);
        Assert.NotNull(healed);
        Assert.Equal(0.45, healed.Vitals.HealBelow);
        Assert.Equal("hunting", healed.Name);

        BotProfile? quiet = RemoteSettingsBridge.TryApply(healed, "combat.enabled", Value("false"), out _);
        Assert.NotNull(quiet);
        Assert.False(quiet.Combat.Enabled);

        BotProfile? magic = RemoteSettingsBridge.TryApply(quiet, "combat.style", Value("\"magic\""), out _);
        Assert.NotNull(magic);
        Assert.Equal(CombatStyle.Magic, magic.Combat.Style);
    }

    [Fact]
    public void AnIntegerSettingRoundsWhatThePhoneSends()
    {
        BotProfile? patched = RemoteSettingsBridge.TryApply(BotProfile.Default, "combat.minRingTargets", Value("4.6"), out string error);

        Assert.Equal(string.Empty, error);
        Assert.NotNull(patched);
        Assert.Equal(5, patched.Combat.MinRingTargets);
    }

    [Fact]
    public void TheWrongKindAnUnknownKeyAndABadEnumAreRefused()
    {
        Assert.Null(RemoteSettingsBridge.TryApply(BotProfile.Default, "vitals.healBelow", Value("true"), out string kind));
        Assert.Contains("number", kind);

        Assert.Null(RemoteSettingsBridge.TryApply(BotProfile.Default, "vitals.nope", Value("1"), out string unknown));
        Assert.Contains("not a setting", unknown);

        Assert.Null(RemoteSettingsBridge.TryApply(BotProfile.Default, "combat.style", Value("\"laser\""), out string badEnum));
        Assert.Contains("refused", badEnum);

        Assert.Null(RemoteSettingsBridge.TryApply(BotProfile.Default, "name", Value("\"x\""), out string hidden));
        Assert.Contains("not a setting", hidden);

        Assert.Null(RemoteSettingsBridge.TryApply(BotProfile.Default, "buffs.spells", Value("\"x\""), out string list));
        Assert.Contains("not a setting", list);
    }
}

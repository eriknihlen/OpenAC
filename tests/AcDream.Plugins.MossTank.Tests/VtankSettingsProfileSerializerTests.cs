using System.Globalization;
using System.Linq;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankSettingsProfileSerializerTests
{
    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "vtank");

    public static TheoryData<string> UsdFixtureData()
    {
        var data = new TheoryData<string>();
        foreach (string name in new[] { "defaultsettings", "owner-a", "owner-b", "owner-c" })
            data.Add(Path.Combine(FixturesRoot, name + ".usd"));
        return data;
    }

    private static VtankSettingsProfileSerializer.AllSettings NewSettings() => new()
    {
        Combat = new CombatSettings(),
        Buffs = new BuffSettings(),
        Vitals = new VitalSettings(),
        Inventory = new InventorySettings(),
        Navigation = new NavigationSettings(),
    };

    [Theory]
    [MemberData(nameof(UsdFixtureData))]
    public void EveryUsdFixtureLoads(string path)
    {
        string text = File.ReadAllText(path);
        VtankSettingsProfileSerializer.AllSettings settings = NewSettings();
        VtankDatabase document = VtankSettingsProfileSerializer.Load(text, settings);
        Assert.NotNull(document.Find("Settings"));
    }

    [Theory]
    [MemberData(nameof(UsdFixtureData))]
    public void UntouchedRoundTripIsByteIdentical(string path)
    {
        string original = File.ReadAllText(path);
        VtankSettingsProfileSerializer.AllSettings settings = NewSettings();
        VtankDatabase document = VtankSettingsProfileSerializer.Load(original, settings);
        string rewritten = VtankSettingsProfileSerializer.Save(document, settings);
        Assert.Equal(original, rewritten);
    }

    [Fact]
    public void DefaultSettingsUsdMatchesEveryCatalogDefault()
    {
        string text = File.ReadAllText(Path.Combine(FixturesRoot, "defaultsettings.usd"));
        VtankSettingsProfileSerializer.AllSettings settings = NewSettings();
        VtankSettingsProfileSerializer.Load(text, settings);

        Assert.Equal(25, settings.Combat.HuntSkillExcessOverDifficulty);
        Assert.True(settings.Combat.Enabled);
        Assert.True(settings.Buffs.Enabled);
        Assert.False(settings.Inventory.Loot.Enabled);
        Assert.False(settings.Navigation.Enabled);
        Assert.Equal(0.75, settings.Vitals.NormalHealth, 6);
        Assert.Equal(0.50, settings.Vitals.NormalStamina, 6);
        Assert.Equal(0.50, settings.Vitals.NormalMana, 6);
        Assert.Equal(5f, settings.Combat.MaximumRange, 3);
        Assert.Equal(2, (int)settings.Combat.AttackHeight);
        Assert.Equal(TargetSelectionMethod.Both, settings.Combat.SelectionMethod);
        Assert.Equal(1.9, settings.Vitals.StaminaToHealthMultiplier, 6);
        Assert.Equal(2.8, settings.Vitals.ManaToHealthMultiplier, 6);
        Assert.Equal("ALFCBPS", settings.Buffs.ProtectionElements);
        Assert.Equal(2, settings.Buffs.ProtectionProfileMode);
        Assert.Equal(30d, settings.Buffs.BuffCastRecastSeconds, 6);
        Assert.Equal(80, settings.Buffs.BuffWithUntrainedItemSkill);
        Assert.Equal(-50, settings.Navigation.DoorLockpickExcessThreshold);
        Assert.Equal(34, settings.Vitals.DropToPeaceModeRetryCount);

        Assert.Equal(26, settings.Vitals.RechargeHandlerRows.Count);
        RechargeHandlerRow first = settings.Vitals.RechargeHandlerRows[0];
        Assert.Equal(1, first.Vital); // Health
        Assert.Equal("Stamina to Health", first.HandlerString);
        Assert.Equal(0, first.MinPercent);
        Assert.Equal(15, first.MaxPercent);
        Assert.Equal(1, first.Stance); // magic
    }

    [Fact]
    public void Every137CatalogNameMapsToExactlyOneField()
    {
        VtankSettingsProfileSerializer.AllSettings settings = NewSettings();
        var unmapped = new List<string>();
        foreach (string name in VtankOptionCatalog.Names)
        {
            if (VtankSettingsProfileSerializer.Capture(name, settings) is null)
                unmapped.Add(name);
        }
        Assert.Equal(["EnableMeta", "RechargeHandlerSet"], unmapped);
    }

    public static TheoryData<string> CatalogNamesWithLiveWritePath()
    {
        var data = new TheoryData<string>();
        foreach (string name in VtankOptionCatalog.Names)
        {
            if (name is "EnableMeta" or "RechargeHandlerSet")
                continue; // no live write path (Every137CatalogNameMapsToExactlyOneField).
            data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CatalogNamesWithLiveWritePath))]
    public void CaptureMatchesDeclaredSettingTypeAndValue(string name)
    {
        string text = File.ReadAllText(Path.Combine(FixturesRoot, "defaultsettings.usd"));
        VtankSettingsProfileSerializer.AllSettings settings = NewSettings();
        VtankDatabase document = VtankSettingsProfileSerializer.Load(text, settings);
        VtankTable table = document.Find("Settings")!;
        int nameColumn = table.ColumnIndex("Setting");
        int valueColumn = table.ColumnIndex("Value");
        VtankRow row = table.Rows.Single(
            r => r.Cells[nameColumn].AsString().Equals(name, StringComparison.Ordinal));
        VtankCell fixtureCell = row.Cells[valueColumn];

        VtankCell? captured = VtankSettingsProfileSerializer.Capture(name, settings);

        Assert.NotNull(captured);
        Assert.Equal(fixtureCell.Tag, captured!.Tag);
        switch (fixtureCell.Tag)
        {
            case "b":
                Assert.Equal(fixtureCell.AsBool(), captured.AsBool());
                break;
            case "s":
                Assert.Equal(fixtureCell.AsString(), captured.AsString());
                break;
            case "i":
                Assert.Equal(fixtureCell.AsInt(), captured.AsInt());
                break;
            case "u":
                Assert.Equal(fixtureCell.AsUInt(), captured.AsUInt());
                break;
            case "d":
            case "f":
                Assert.Equal(
                    fixtureCell.AsDouble(),
                    captured.AsDouble());
                break;
            default:
                Assert.Fail($"{name}: unexpected tag '{fixtureCell.Tag}'.");
                break;
        }
    }

    [Fact]
    public void RechargeHandlerSetRoundTripsFromOwnerFixture()
    {
        string text = File.ReadAllText(Path.Combine(FixturesRoot, "owner-a.usd"));
        VtankSettingsProfileSerializer.AllSettings settings = NewSettings();
        VtankSettingsProfileSerializer.Load(text, settings);
        Assert.NotEmpty(settings.Vitals.RechargeHandlerRows);
        Assert.Contains(
            settings.Vitals.RechargeHandlerRows,
            static row => row.HandlerString == "Kit Recharge");
    }

    [Fact]
    public void CreateNewHasTheSameTableSetAsTheDefaultFixture()
    {
        string fixtureText = File.ReadAllText(
            Path.Combine(FixturesRoot, "defaultsettings.usd"));
        VtankDatabase fixture = VtankDatabase.Parse(fixtureText);

        VtankDatabase created = VtankSettingsProfileSerializer.CreateNew(NewSettings());

        string[] expectedTables = [.. fixture.Tables
            .Select(static entry => entry.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)];
        string[] actualTables = [.. created.Tables
            .Select(static entry => entry.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)];
        Assert.Equal(expectedTables, actualTables);
        Assert.True(expectedTables.Length > 1, "the fixture itself must carry more than one table.");

        // Every row's Description/SettingType survive from the embedded
        // document (not the prior blanket Description="" / SettingType=1).
        VtankTable? settings = created.Find("Settings");
        Assert.NotNull(settings);
        int descriptionColumn = settings!.ColumnIndex("Description");
        int typeColumn = settings.ColumnIndex("SettingType");
        Assert.True(descriptionColumn >= 0);
        Assert.True(typeColumn >= 0);
        Assert.Contains(
            settings.Rows,
            row => row.Cells[descriptionColumn].AsString().Length > 0);
        Assert.Contains(
            settings.Rows,
            row => row.Cells[typeColumn].AsInt() != (int)VtankSettingValueType.Bool);
    }

    [Fact]
    public void UnknownSettingsRowIsPreservedVerbatim()
    {
        string usd = SingleSettingUsd("SomeFutureSetting", "s", "future-value");
        VtankSettingsProfileSerializer.AllSettings settings = NewSettings();
        VtankDatabase document = VtankSettingsProfileSerializer.Load(usd, settings);
        string rewritten = VtankSettingsProfileSerializer.Save(document, settings);
        Assert.Equal(usd, rewritten);
    }

    private static string SingleSettingUsd(string name, string valueTag, string valueText)
    {
        var lines = new List<string>
        {
            "1", "Settings", "4", "Setting", "Value", "Description", "SettingType",
            "y", "n", "n", "n", "1",
            "s", name, valueTag, valueText, "s", string.Empty, "i", "1",
        };
        return string.Join("\r\n", lines) + "\r\n";
    }
}

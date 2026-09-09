using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankProfileDirectoryTests
{
    [Fact]
    public void AutoCharacterFileNameMatchesRealInstalledConvention()
    {
        Assert.Equal(
            "--Barris_Coldeve.usd",
            VtankProfileDirectory.AutoCharacterFileName("Barris", "Coldeve", "usd"));
        Assert.Equal(
            "--Barris_Coldeve.usd",
            VtankProfileDirectory.AutoCharacterFileName("Barris", "Coldeve", ".usd"));
    }

    [Fact]
    public void SubProfilesAreDisplayedAsCharBracketSuffix()
    {
        Assert.Equal(
            "[Char] Base",
            VtankProfileDirectory.TryDisplayName("--Barris_Coldeve_Base.usd", "Barris", "Coldeve"));
        Assert.Equal(
            "[Char] viridian",
            VtankProfileDirectory.TryDisplayName("--Barris_Coldeve_viridian.usd", "Barris", "Coldeve"));
        Assert.Null(
            VtankProfileDirectory.TryDisplayName("--Barris_Coldeve.usd", "Barris", "Coldeve"));
        Assert.Null(
            VtankProfileDirectory.TryDisplayName("--Someone_Coldeve_Base.usd", "Barris", "Coldeve"));
    }

    [Fact]
    public void OtherCharactersHiddenPrefixedFilesAreInvisible()
    {
        Assert.True(VtankProfileDirectory.IsHiddenFromOtherCharacters(
            "--Someone_Coldeve.usd", "Barris", "Coldeve"));
        Assert.False(VtankProfileDirectory.IsHiddenFromOtherCharacters(
            "--Barris_Coldeve_Base.usd", "Barris", "Coldeve"));
        Assert.False(VtankProfileDirectory.IsHiddenFromOtherCharacters(
            "MyNamedProfile.usd", "Barris", "Coldeve"));
    }

    [Fact]
    public void ListSettingsProfilesSeedsDefaultAndByCharFirst()
    {
        var storage = new MemoryStorage();
        storage.WriteText("Shared.usd", "1\r\n");
        storage.WriteText("--Barris_Coldeve.usd", "1\r\n");
        storage.WriteText("--Barris_Coldeve_Base.usd", "1\r\n");
        storage.WriteText("--Someone_Coldeve.usd", "1\r\n");

        IReadOnlyList<VtankProfileDirectory.ProfileEntry> entries =
            VtankProfileDirectory.ListSettingsProfiles(
                storage, "Barris", "Coldeve", mineOnly: false);

        Assert.Equal(VtankProfileDirectory.DefaultLabel, entries[0].DisplayName);
        Assert.Equal(VtankProfileDirectory.ByCharacterLabel, entries[1].DisplayName);
        Assert.Contains(entries, static e => e.DisplayName == "Shared.usd");
        Assert.Contains(entries, static e => e.DisplayName == "[Char] Base");
        Assert.DoesNotContain(entries, static e => e.FileName == "--Someone_Coldeve.usd");
        Assert.DoesNotContain(entries, static e => e.FileName == "--Barris_Coldeve.usd");
    }

    [Fact]
    public void ListNavigationProfilesFiltersBothReservedPrefixesWithinNavsFolder()
    {
        var storage = new MemoryStorage();
        storage.WriteText("navs/Hunt.af", "1\r\n");
        storage.WriteText("navs/--Barris_Coldeve.af", "1\r\n");
        storage.WriteText("navs/~~backup.af", "1\r\n");

        IReadOnlyList<VtankProfileDirectory.ProfileEntry> entries =
            VtankProfileDirectory.ListNavigationProfiles(storage);

        Assert.Equal(VtankProfileDirectory.NoneLabel, entries[0].DisplayName);
        Assert.Equal(VtankProfileDirectory.ByCharacterLabel, entries[1].DisplayName);
        Assert.Contains(entries, static e => e.DisplayName == "Hunt.af");
        Assert.Contains(entries, static e => e.FileName == "navs/Hunt.af");
        Assert.DoesNotContain(entries, static e => e.DisplayName.StartsWith("--", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, static e => e.DisplayName.StartsWith("~~", StringComparison.Ordinal));
    }

    [Fact]
    public void NavigationAndMetaPickersEachSeeOnlyTheirOwnFolder()
    {
        var storage = new MemoryStorage();
        storage.WriteText("navs/Hunt.af", "1\r\n");
        storage.WriteText("metas/Hunt.af", "1\r\n"); // same bare name, other folder.
        storage.WriteText("navs/--Barris_Coldeve.af", "1\r\n");
        storage.WriteText("metas/--Barris_Coldeve.af", "1\r\n");

        IReadOnlyList<VtankProfileDirectory.ProfileEntry> navEntries =
            VtankProfileDirectory.ListNavigationProfiles(storage);
        IReadOnlyList<VtankProfileDirectory.ProfileEntry> metaEntries =
            VtankProfileDirectory.ListMetaProfiles(storage);

        Assert.Contains(navEntries, static e => e.FileName == "navs/Hunt.af");
        Assert.DoesNotContain(navEntries, static e => e.FileName == "metas/Hunt.af");
        Assert.DoesNotContain(navEntries, static e => e.DisplayName.StartsWith("--", StringComparison.Ordinal));

        Assert.Contains(metaEntries, static e => e.FileName == "metas/Hunt.af");
        Assert.DoesNotContain(metaEntries, static e => e.FileName == "navs/Hunt.af");
        Assert.DoesNotContain(metaEntries, static e => e.DisplayName.StartsWith("--", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherCharactersAutoRouteIsHiddenFromTheNavPicker()
    {
        var storage = new MemoryStorage();
        storage.WriteText(
            "navs/" + VtankProfileDirectory.AutoCharacterFileName("Someone", "Coldeve", "af"),
            "1\r\n");

        IReadOnlyList<VtankProfileDirectory.ProfileEntry> navEntries =
            VtankProfileDirectory.ListNavigationProfiles(storage);
        IReadOnlyList<VtankProfileDirectory.ProfileEntry> metaEntries =
            VtankProfileDirectory.ListMetaProfiles(storage);

        Assert.DoesNotContain(navEntries, static e => e.FileName.Contains("Someone", StringComparison.Ordinal));
        Assert.DoesNotContain(metaEntries, static e => e.FileName.Contains("Someone", StringComparison.Ordinal));
    }

    [Fact]
    public void ListingsOnUnavailableStorageOnlySeedTheBuiltInEntries()
    {
        IReadOnlyList<VtankProfileDirectory.ProfileEntry> entries =
            VtankProfileDirectory.ListSettingsProfiles(
                NoOpPluginStorage.Instance, "Barris", "Coldeve", mineOnly: false);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void NestedPathKeysAreNotTreatedAsProfileFiles()
    {
        var storage = new MemoryStorage();
        storage.WriteText("subdir/Nested.usd", "1\r\n");
        storage.WriteText("Flat.usd", "1\r\n");

        IReadOnlyList<VtankProfileDirectory.ProfileEntry> entries =
            VtankProfileDirectory.ListSettingsProfiles(
                storage, "Barris", "Coldeve", mineOnly: false);

        Assert.Contains(entries, static e => e.FileName == "Flat.usd");
        Assert.DoesNotContain(entries, static e => e.FileName.Contains('/'));
    }

    [Fact]
    public void MineOnlyKeepsTheCurrentlySelectedSharedFileVisible()
    {
        var storage = new MemoryStorage();
        storage.WriteText("Shared.usd", "1\r\n");
        storage.WriteText("OtherShared.usd", "1\r\n");

        IReadOnlyList<VtankProfileDirectory.ProfileEntry> withoutCurrent =
            VtankProfileDirectory.ListSettingsProfiles(
                storage, "Barris", "Coldeve", mineOnly: true);
        Assert.DoesNotContain(withoutCurrent, static e => e.FileName == "Shared.usd");
        Assert.DoesNotContain(withoutCurrent, static e => e.FileName == "OtherShared.usd");

        IReadOnlyList<VtankProfileDirectory.ProfileEntry> withCurrent =
            VtankProfileDirectory.ListSettingsProfiles(
                storage, "Barris", "Coldeve", mineOnly: true, currentFileName: "Shared.usd");
        Assert.Contains(withCurrent, static e => e.FileName == "Shared.usd");
        Assert.DoesNotContain(withCurrent, static e => e.FileName == "OtherShared.usd");
    }

    [Fact]
    public void MineOnlyUncheckedIgnoresCurrentFileAndKeepsEverything()
    {
        var storage = new MemoryStorage();
        storage.WriteText("Shared.usd", "1\r\n");

        IReadOnlyList<VtankProfileDirectory.ProfileEntry> entries =
            VtankProfileDirectory.ListSettingsProfiles(
                storage, "Barris", "Coldeve", mineOnly: false, currentFileName: null);

        Assert.Contains(entries, static e => e.FileName == "Shared.usd");
    }

    [Fact]
    public void AstFileNameHasNoHiddenPrefixAndNameServerOrder()
    {
        Assert.Equal(
            "Barris_Coldeve.ast",
            VtankProfileDirectory.AstFileName("Barris", "Coldeve"));
    }

    [Fact]
    public void CdfFileNameUsesServerNameOrderReversedFromAst()
    {
        Assert.Equal(
            "Coldeve_Barris.cdf",
            VtankProfileDirectory.CdfFileName("Barris", "Coldeve"));
    }

    [Fact]
    public void TryReadCharacterBindingParsesAllFourLines()
    {
        var storage = new MemoryStorage();
        storage.WriteText(
            "Coldeve_Barris.cdf",
            "uTank2 CDF 1.0\r\n--Barris_Coldeve.usd\r\nLoot.utl\r\n--Barris_Coldeve.nav\r\nHunt.met\r\n");

        VtankProfileDirectory.VtankCharacterBinding? binding =
            VtankProfileDirectory.TryReadCharacterBinding(storage, "Barris", "Coldeve");

        Assert.NotNull(binding);
        Assert.Equal("--Barris_Coldeve.usd", binding!.Value.SettingsFileName);
        Assert.Equal("Loot.utl", binding.Value.LootFileName);
        Assert.Equal("--Barris_Coldeve.nav", binding.Value.NavFileName);
        Assert.Equal("Hunt.met", binding.Value.MetaFileName);
    }

    [Fact]
    public void TryReadCharacterBindingWithoutMetaLineLeavesMetaNull()
    {
        var storage = new MemoryStorage();
        storage.WriteText(
            "Coldeve_Barris.cdf",
            "uTank2 CDF 1.0\r\n--Barris_Coldeve.usd\r\nLoot.utl\r\n--Barris_Coldeve.nav\r\n");

        VtankProfileDirectory.VtankCharacterBinding? binding =
            VtankProfileDirectory.TryReadCharacterBinding(storage, "Barris", "Coldeve");

        Assert.NotNull(binding);
        Assert.Null(binding!.Value.MetaFileName);
    }

    [Fact]
    public void TryReadCharacterBindingRewritesLegacyUtsSettingsExtension()
    {
        var storage = new MemoryStorage();
        storage.WriteText(
            "Coldeve_Barris.cdf",
            "uTank2 CDF 1.0\r\n--Barris_Coldeve.uts\r\nLoot.utl\r\n--Barris_Coldeve.nav\r\n");

        VtankProfileDirectory.VtankCharacterBinding? binding =
            VtankProfileDirectory.TryReadCharacterBinding(storage, "Barris", "Coldeve");

        Assert.Equal("--Barris_Coldeve.usd", binding!.Value.SettingsFileName);
    }

    [Fact]
    public void TryReadCharacterBindingReturnsNullOnHeaderMismatch()
    {
        var storage = new MemoryStorage();
        storage.WriteText(
            "Coldeve_Barris.cdf",
            "uTank2 CDF 0.9\r\n--Barris_Coldeve.usd\r\nLoot.utl\r\n--Barris_Coldeve.nav\r\n");

        Assert.Null(VtankProfileDirectory.TryReadCharacterBinding(storage, "Barris", "Coldeve"));
    }

    [Fact]
    public void TryReadCharacterBindingReturnsNullWhenFileMissing()
    {
        Assert.Null(VtankProfileDirectory.TryReadCharacterBinding(
            new MemoryStorage(), "Barris", "Coldeve"));
    }

    [Theory]
    [InlineData("owner-a.ast")]
    [InlineData("owner-b.ast")]
    [InlineData("owner-c.ast")]
    public void RealAstFixturesParseAsTheSpellsTable(string fileName)
    {
        string text = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vtank", fileName));

        VtankDatabase database = VtankDatabase.Parse(text);

        VtankTable? spells = database.Find("Spells");
        Assert.NotNull(spells);
        Assert.Equal(
            ["SpellID", "EndTime", "Target", "CastTime"],
            spells!.ColumnNames);
    }

    [Theory]
    [InlineData("owner-a.ast")]
    [InlineData("owner-b.ast")]
    [InlineData("owner-c.ast")]
    public void RealAstFixturesRoundTripByteIdentical(string fileName)
    {
        string original = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vtank", fileName));

        VtankDatabase database = VtankDatabase.Parse(original);
        string rewritten = database.Render();

        Assert.Equal(original, rewritten);
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
        public bool IsAvailable => true;
        public string? ReadText(string key) =>
            _text.TryGetValue(key, out string? value) ? value : null;
        public IReadOnlyList<string> List(string prefix) => _text.Keys
            .Where(key => prefix.Length == 0
                || key.StartsWith(prefix + "/", StringComparison.Ordinal))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        public void WriteText(string key, string content) => _text[key] = content;
        public bool Delete(string key) => _text.Remove(key);
    }
}

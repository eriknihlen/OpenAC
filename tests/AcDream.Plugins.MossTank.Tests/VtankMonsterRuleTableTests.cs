namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankMonsterRuleTableTests
{
    [Fact]
    public void ShippedDefaultRowParsesToRetailsOwnValues()
    {
        // uTank2.Resources.defaultsettings.usd:93-134 — <DEFAULT>, priority 1,
        // DamageType 8 (Auto), WeaponToUse -1, Attack and Streak set,
        // SecondaryVuln 98 (None), SecondaryEquip 0 (Auto),
        // PetDamageType 101 (PAuto).
        List<MonsterRule>? rules = VtankMonsterRuleTable.TryRead(
            VtankDefaultSettingsDatabase.Parse());

        MonsterRule rule = Assert.Single(rules!);
        Assert.True(rule.IsDefault);
        Assert.Equal(VtankMonsterRuleTable.DefaultRowName, rule.Expression);
        Assert.Equal(1, rule.Priority);
        Assert.Equal(MonsterDamageType.Auto, rule.Actions.DamageType);
        Assert.Equal(
            MonsterActionFlags.Attack | MonsterActionFlags.Streak,
            rule.Actions.Flags);
        Assert.Equal(MonsterDamageType.None, rule.Actions.ExtraVulnerability);
        Assert.Equal(MonsterDamageType.PlayerAuto, rule.Actions.PetDamageType);
        Assert.Equal(0u, rule.Actions.WeaponObjectId);
        Assert.Equal(0u, rule.Actions.OffhandObjectId);
    }

    [Fact]
    public void UntouchedRowsRoundTripByteForByte()
    {
        // Save() rewrites the table from the live rules, so an untouched
        // profile must still render exactly what it parsed.
        string original = VtankDefaultSettingsDatabase.Parse().Render();
        VtankDatabase database = VtankDatabase.Parse(original);
        List<MonsterRule> rules = VtankMonsterRuleTable.TryRead(database)!;

        VtankMonsterRuleTable.Write(database, rules);

        Assert.Equal(original, database.Render());
    }

    [Fact]
    public void PriorityWeaponAndSecondaryEquipShapesRoundTripByteForByte()
    {
        VtankDatabase seed = VtankDefaultSettingsDatabase.Parse();
        VtankRow row = seed.Find(VtankMonsterRuleTable.TableName)!.Rows[0];
        row.Cells[1] = VtankCell.Int(7);
        row.Cells[3] = VtankCell.Int(0);
        row.Cells[19] = VtankCell.Int((int)VtankSecondaryEquip.AutoShield);
        string original = seed.Render();

        VtankDatabase database = VtankDatabase.Parse(original);
        List<MonsterRule> rules = VtankMonsterRuleTable.TryRead(database)!;
        Assert.Equal(7, rules[0].Priority);

        VtankMonsterRuleTable.Write(database, rules);

        Assert.Equal(original, database.Render());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EverySecondaryEquipModeSurvivesASave(int mode)
    {
        VtankDatabase database = VtankDefaultSettingsDatabase.Parse();
        VtankTable monsters = database.Find(VtankMonsterRuleTable.TableName)!;
        monsters.Rows[0].Cells[19] = VtankCell.Int(mode);

        List<MonsterRule> rules = VtankMonsterRuleTable.TryRead(database)!;
        VtankMonsterRuleTable.Write(database, rules);

        Assert.Equal(mode, monsters.Rows[0].Cells[19].AsInt());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(0x4000_0011, 0x4000_0011)]
    public void WeaponToUseKeepsTheSpellingTheFileUsed(int stored, int expected)
    {
        VtankDatabase database = VtankDefaultSettingsDatabase.Parse();
        VtankTable monsters = database.Find(VtankMonsterRuleTable.TableName)!;
        monsters.Rows[0].Cells[3] = VtankCell.Int(stored);

        List<MonsterRule> rules = VtankMonsterRuleTable.TryRead(database)!;
        VtankMonsterRuleTable.Write(database, rules);

        Assert.Equal(expected, monsters.Rows[0].Cells[3].AsInt());
    }

    [Fact]
    public void AnUnparseableSpecIsLoggedAndIgnoredInsteadOfLosingTheProfile()
    {
        VtankDatabase database = VtankDefaultSettingsDatabase.Parse();
        VtankTable monsters = database.Find(VtankMonsterRuleTable.TableName)!;
        monsters.Rows[0].Cells[0] = VtankCell.String("(((");
        var warnings = new List<string>();

        List<MonsterRule>? rules = VtankMonsterRuleTable.TryRead(
            database,
            warnings.Add);

        MonsterRule rule = Assert.Single(rules!);
        Assert.Equal("(((", rule.Expression);
        Assert.True(rule.IsIgnoredSpec);
        Assert.False(
            rule.Matches(
                new MonsterExpressionContext(
                    "Drudge",
                    TypeId: 1u,
                    Species: "Drudge",
                    MaximumHealth: 100,
                    Range: 5f,
                    HasShield: false,
                    MetaState: string.Empty,
                    Setting: null),
                out _));
        Assert.Contains(
            warnings,
            warning => warning.StartsWith(
                "Parse error in monster spec: \"(((\", ignoring entry (",
                StringComparison.Ordinal));

        // And the row is still the row: it round-trips like any other.
        VtankMonsterRuleTable.Write(database, rules!);
        Assert.Equal("(((", monsters.Rows[0].Cells[0].AsString());
    }

    [Fact]
    public void LoadKeepsEverySettingWhenOneMonsterSpecIsUnparseable()
    {
        VtankDatabase seed = VtankDefaultSettingsDatabase.Parse();
        seed.Find(VtankMonsterRuleTable.TableName)!.Rows[0].Cells[0] =
            VtankCell.String("(((");
        var combat = new CombatSettings();
        var warnings = new List<string>();

        VtankSettingsProfileSerializer.Load(
            seed.Render(),
            new VtankSettingsProfileSerializer.AllSettings
            {
                Combat = combat,
                Buffs = new BuffSettings(),
                Vitals = new VitalSettings(),
                Inventory = new InventorySettings(),
                Navigation = new NavigationSettings(),
            },
            warnings.Add);

        Assert.NotEmpty(warnings);
        MonsterRule rule = Assert.Single(combat.Rules);
        Assert.Equal("(((", rule.Expression);
    }

    [Fact]
    public void EveryColumnSurvivesAWriteThenReadCycle()
    {
        var rules = new List<MonsterRule>
        {
            new("DEFAULT", new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                Priority = 1,
            }),
            new("species==drudge", new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Imperil
                    | MonsterActionFlags.Vulnerability
                    | MonsterActionFlags.Yield
                    | MonsterActionFlags.GravityWell
                    | MonsterActionFlags.Ring
                    | MonsterActionFlags.Broadside
                    | MonsterActionFlags.Fester
                    | MonsterActionFlags.WeakeningCurse
                    | MonsterActionFlags.FesteringCurse
                    | MonsterActionFlags.Corruption
                    | MonsterActionFlags.DestructiveCurse
                    | MonsterActionFlags.Corrosion
                    | MonsterActionFlags.Streak,
                Priority = 4,
                DamageType = MonsterDamageType.Electric,
                ExtraVulnerability = MonsterDamageType.Acid,
                PetDamageType = MonsterDamageType.Cold,
                WeaponObjectId = 0x4000_0011u,
                OffhandObjectId = 0x4000_0022u,
            }),
            new("name#^Olthoi", new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Imperil,
                Priority = -1,
                DamageType = MonsterDamageType.VoidBasic,
            }),
        };
        VtankDatabase database = VtankDefaultSettingsDatabase.Parse();

        VtankMonsterRuleTable.Write(database, rules);
        List<MonsterRule> read = VtankMonsterRuleTable.TryRead(database)!;

        Assert.Equal(3, read.Count);
        for (int i = 0; i < rules.Count; i++)
        {
            Assert.Equal(rules[i].Actions.Flags, read[i].Actions.Flags);
            Assert.Equal(rules[i].Priority, read[i].Priority);
            Assert.Equal(rules[i].Actions.DamageType, read[i].Actions.DamageType);
            Assert.Equal(
                rules[i].Actions.ExtraVulnerability,
                read[i].Actions.ExtraVulnerability);
            Assert.Equal(
                rules[i].Actions.PetDamageType,
                read[i].Actions.PetDamageType);
            Assert.Equal(
                rules[i].Actions.WeaponObjectId,
                read[i].Actions.WeaponObjectId);
            Assert.Equal(
                rules[i].Actions.OffhandObjectId,
                read[i].Actions.OffhandObjectId);
        }
        Assert.Equal(VtankMonsterRuleTable.DefaultRowName, read[0].Expression);
        Assert.Equal("species==drudge", read[1].Expression);
        Assert.Equal("name#^Olthoi", read[2].Expression);
    }

    [Fact]
    public void AttackColumnIsStoredInvertedExactlyAsRetailDoes()
    {
        VtankDatabase database = VtankDefaultSettingsDatabase.Parse();
        VtankMonsterRuleTable.Write(
            database,
            [
                new MonsterRule("DEFAULT", new MonsterRuleActions
                {
                    Flags = MonsterActionFlags.Attack,
                }),
                new MonsterRule("nope", new MonsterRuleActions
                {
                    Flags = MonsterActionFlags.None,
                }),
            ]);

        VtankTable table = database.Find(VtankMonsterRuleTable.TableName)!;
        Assert.True(table.Rows[0].Cells[8].AsBool());
        Assert.False(table.Rows[1].Cells[8].AsBool());
    }

    [Fact]
    public void DamageElementNumbersAreVtanksNotMossTanks()
    {
        Assert.Equal(4, VtankDamageElement.FromMonsterDamageType(
            MonsterDamageType.Electric));
        Assert.Equal(9, VtankDamageElement.FromMonsterDamageType(
            MonsterDamageType.VoidBasic));
        Assert.Equal(98, VtankDamageElement.FromMonsterDamageType(
            MonsterDamageType.None));
        Assert.Equal(101, VtankDamageElement.FromMonsterDamageType(
            MonsterDamageType.PlayerAuto));
        Assert.Equal(
            MonsterDamageType.Electric,
            VtankDamageElement.ToMonsterDamageType(4));
        Assert.Equal(
            MonsterDamageType.PlayerAuto,
            VtankDamageElement.ToMonsterDamageType(101));
        // bv.cs:94-97 folds the legacy prismatic database id onto Prismatic.
        Assert.Equal(
            MonsterDamageType.Prismatic,
            VtankDamageElement.ToMonsterDamageType(100));
    }

    [Fact]
    public void ADropInVtankProfilesRulesBecomeLive()
    {
        VtankDatabase source = VtankDefaultSettingsDatabase.Parse();
        VtankMonsterRuleTable.Write(
            source,
            [
                new MonsterRule("DEFAULT", new MonsterRuleActions
                {
                    Flags = MonsterActionFlags.Attack,
                }),
                new MonsterRule("name#^Olthoi", new MonsterRuleActions
                {
                    Flags = MonsterActionFlags.Attack | MonsterActionFlags.Imperil,
                    Priority = 4,
                    DamageType = MonsterDamageType.Acid,
                }),
            ]);
        var settings = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = new CombatSettings(),
            Buffs = new BuffSettings(),
            Vitals = new VitalSettings(),
            Inventory = new InventorySettings(),
            Navigation = new NavigationSettings(),
        };

        VtankSettingsProfileSerializer.Load(source.Render(), settings);

        Assert.Equal(2, settings.Combat.Rules.Count);
        Assert.Equal("name#^Olthoi", settings.Combat.Rules[1].Expression);
        Assert.Equal(4, settings.Combat.Rules[1].Priority);
        Assert.Equal(
            MonsterDamageType.Acid,
            settings.Combat.Rules[1].Actions.DamageType);
    }
}

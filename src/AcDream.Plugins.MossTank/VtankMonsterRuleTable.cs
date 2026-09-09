namespace AcDream.Plugins.MossTank;

internal static class VtankMonsterRuleTable
{
    public const string TableName = "MyMonsters";
    public const string DefaultRowName = "<DEFAULT>";

    private const int ColumnCount = 21;

    private static readonly string[] Columns =
    [
        "MonsterName",
        "AttackPriority",
        "DamageType",
        "WeaponToUse",
        "Imperil",
        "Vuln",
        "Yield",
        "GravityW",
        "Attack",
        "Ring",
        "Broadside",
        "Fester",
        "WeakeningCurse",
        "FesteringCurse",
        "Corruption",
        "DestructiveCurse",
        "Corrosion",
        "Streak",
        "SecondaryVuln",
        "SecondaryEquip",
        "PetDamageType",
    ];

    private static readonly (int Column, MonsterActionFlags Flag)[] BoolColumns =
    [
        (4, MonsterActionFlags.Imperil),
        (5, MonsterActionFlags.Vulnerability),
        (6, MonsterActionFlags.Yield),
        (7, MonsterActionFlags.GravityWell),
        (9, MonsterActionFlags.Ring),
        (10, MonsterActionFlags.Broadside),
        (11, MonsterActionFlags.Fester),
        (12, MonsterActionFlags.WeakeningCurse),
        (13, MonsterActionFlags.FesteringCurse),
        (14, MonsterActionFlags.Corruption),
        (15, MonsterActionFlags.DestructiveCurse),
        (16, MonsterActionFlags.Corrosion),
        (17, MonsterActionFlags.Streak),
    ];

    public static List<MonsterRule>? TryRead(
        VtankDatabase database,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        VtankTable? table = database.Find(TableName);
        if (table is null || table.ColumnNames.Count < ColumnCount)
            return null;

        var rules = new List<MonsterRule>(table.Rows.Count);
        foreach (VtankRow row in table.Rows)
        {
            if (row.Cells.Count < ColumnCount)
                return null;
            string expression = row.Cells[0].AsString();
            MonsterActionFlags flags = MonsterActionFlags.None;
            foreach ((int column, MonsterActionFlags flag) in BoolColumns)
            {
                if (row.Cells[column].AsBool())
                    flags |= flag;
            }

            if (row.Cells[8].AsBool())
                flags |= MonsterActionFlags.Attack;

            int weapon = row.Cells[3].AsInt();
            int offhand = row.Cells[19].AsInt();
            MonsterRule rule = MonsterRule.Compile(
                expression,
                new MonsterRuleActions
                {
                    Flags = flags,
                    Priority = row.Cells[1].AsInt(),
                    DamageType = VtankDamageElement.ToMonsterDamageType(row.Cells[2].AsInt()),
                    ExtraVulnerability =
                        VtankDamageElement.ToMonsterDamageType(row.Cells[18].AsInt()),
                    PetDamageType = VtankDamageElement.ToMonsterDamageType(
                        row.Cells[20].AsInt(),
                        MonsterDamageType.PlayerAuto),
                    // ga.cs:1452's `int num = A_1;` treats WeaponToUse as an object
                    // id; VTank's own "no override" values are 0 and -1
                    // (defaultsettings.usd:100).
                    WeaponObjectId = weapon > 0 ? unchecked((uint)weapon) : 0u,
                    WeaponToUseRaw = weapon,
                    // eSecondaryEquipTypeOrObjectID (uTank2/…:3-10) packs four
                    // modes (Auto, AutoShield, AutoWeapon, None) below
                    // LISTEDTYPES_END and an object id above it.
                    OffhandObjectId =
                        offhand >= (int)VtankSecondaryEquip.ListedTypesEnd
                            ? unchecked((uint)offhand)
                            : 0u,
                    SecondaryEquipRaw = offhand,
                },
                out string? parseError);
            if (parseError is not null)
                warn?.Invoke(parseError);
            rules.Add(rule);
        }
        return rules;
    }

    public static void Write(VtankDatabase database, IReadOnlyList<MonsterRule> rules)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(rules);

        VtankTable? table = database.Find(TableName);
        if (table is null)
        {
            if (rules.Count == 0
                || (rules.Count == 1 && rules[0].IsDefault
                    && rules[0].Actions.Flags == MonsterActionFlags.Attack
                    && rules[0].Actions.Priority == 0))
            {
                return;
            }
            table = new VtankTable();
            table.ColumnNames.AddRange(Columns);
            table.IndexFlags.Add(true);
            for (int i = 1; i < ColumnCount; i++)
                table.IndexFlags.Add(false);
            database.Tables.Add((TableName, table));
        }

        var existing = new List<VtankRow>(table.Rows);
        table.Rows.Clear();
        for (int i = 0; i < rules.Count; i++)
        {
            MonsterRule rule = rules[i];
            VtankRow row = i < existing.Count && existing[i].Cells.Count >= ColumnCount
                ? existing[i]
                : NewRow();
            MonsterRuleActions actions = rule.Actions;

            SetString(
                row,
                0,
                rule.IsDefault ? DefaultRowName : rule.Expression);
            SetInt(row, 1, actions.Priority);
            SetInt(row, 2, VtankDamageElement.FromMonsterDamageType(actions.DamageType));
            SetInt(
                row,
                3,
                actions.WeaponObjectId != 0u
                    ? unchecked((int)actions.WeaponObjectId)
                    : actions.WeaponToUseRaw > 0 ? -1 : actions.WeaponToUseRaw);
            foreach ((int column, MonsterActionFlags flag) in BoolColumns)
                SetBool(row, column, (actions.Flags & flag) != 0);
            SetBool(row, 8, (actions.Flags & MonsterActionFlags.Attack) != 0);
            SetInt(
                row,
                18,
                VtankDamageElement.FromMonsterDamageType(actions.ExtraVulnerability));
            // d1.cs:113 stores the raw enum, so AutoShield (1), AutoWeapon
            // (2) and None (3) must survive a save; only a stale object id
            // (>= LISTEDTYPES_END with no live OffhandObjectId) falls back to
            // Auto.
            SetInt(
                row,
                19,
                actions.OffhandObjectId != 0u
                    ? unchecked((int)actions.OffhandObjectId)
                    : actions.SecondaryEquipRaw
                        >= (int)VtankSecondaryEquip.ListedTypesEnd
                        ? (int)VtankSecondaryEquip.Auto
                        : actions.SecondaryEquipRaw);
            SetInt(
                row,
                20,
                VtankDamageElement.FromMonsterDamageType(
                    actions.PetDamageType,
                    MonsterDamageType.PlayerAuto));
            table.Rows.Add(row);
        }
    }

    private static VtankRow NewRow()
    {
        var row = new VtankRow();
        for (int i = 0; i < ColumnCount; i++)
            row.Cells.Add(VtankCell.Int(0));
        return row;
    }

    private static void SetString(VtankRow row, int column, string value)
    {
        if (row.Cells[column].Tag != "s"
            || !string.Equals(row.Cells[column].AsString(), value, StringComparison.Ordinal))
        {
            row.Cells[column] = VtankCell.String(value);
        }
    }

    private static void SetInt(VtankRow row, int column, int value)
    {
        if (row.Cells[column].Tag != "i" || row.Cells[column].AsInt() != value)
            row.Cells[column] = VtankCell.Int(value);
    }

    private static void SetBool(VtankRow row, int column, bool value)
    {
        if (row.Cells[column].Tag != "b" || row.Cells[column].AsBool() != value)
            row.Cells[column] = VtankCell.Bool(value);
    }
}

/// <summary>
/// <c>uTank2/eSecondaryEquipTypeOrObjectID.cs:3-10</c> — the four named modes
/// below <c>LISTEDTYPES_END</c>; anything at or above it is an object id.
/// </summary>
internal enum VtankSecondaryEquip
{
    Auto = 0,
    AutoShield = 1,
    AutoWeapon = 2,
    None = 3,
    ListedTypesEnd = 4,
}

internal static class VtankDamageElement
{
    public const int Pierce = 0;
    public const int Bludgeon = 1;
    public const int Slash = 2;
    public const int Acid = 3;
    public const int Lightning = 4;
    public const int Cold = 5;
    public const int Fire = 6;
    public const int Harm = 7;
    public const int Auto = 8;
    public const int Void = 9;
    public const int DrainAuto = 10;
    public const int Prismatic = 11;
    public const int Random = 12;
    public const int Fists = 13;
    public const int None = 98;
    public const int Physical = 99;
    public const int PrismaticDatabaseEntryOld = 100;
    public const int PAuto = 101;

    public static MonsterDamageType ToMonsterDamageType(
        int value,
        MonsterDamageType fallback = MonsterDamageType.Auto) => value switch
        {
            Pierce => MonsterDamageType.Pierce,
            Bludgeon => MonsterDamageType.Bludgeon,
            Slash => MonsterDamageType.Slash,
            Acid => MonsterDamageType.Acid,
            Lightning => MonsterDamageType.Electric,
            Cold => MonsterDamageType.Cold,
            Fire => MonsterDamageType.Fire,
            Harm => MonsterDamageType.Harm,
            Auto => MonsterDamageType.Auto,
            Void => MonsterDamageType.VoidBasic,
            DrainAuto => MonsterDamageType.DrainAuto,
            // bv.cs:94-97 folds the legacy prismatic id onto Prismatic.
            Prismatic or PrismaticDatabaseEntryOld => MonsterDamageType.Prismatic,
            Random => MonsterDamageType.Random,
            Fists => MonsterDamageType.Fists,
            None => MonsterDamageType.None,
            Physical => MonsterDamageType.Physical,
            PAuto => MonsterDamageType.PlayerAuto,
            _ => fallback,
        };

    public static int FromMonsterDamageType(
        MonsterDamageType value,
        MonsterDamageType fallback = MonsterDamageType.Auto) => value switch
        {
            MonsterDamageType.Pierce => Pierce,
            MonsterDamageType.Bludgeon => Bludgeon,
            MonsterDamageType.Slash => Slash,
            MonsterDamageType.Acid => Acid,
            MonsterDamageType.Electric => Lightning,
            MonsterDamageType.Cold => Cold,
            MonsterDamageType.Fire => Fire,
            MonsterDamageType.Harm => Harm,
            MonsterDamageType.Auto => Auto,
            MonsterDamageType.VoidBasic or MonsterDamageType.Nether => Void,
            MonsterDamageType.DrainAuto => DrainAuto,
            MonsterDamageType.Prismatic => Prismatic,
            MonsterDamageType.Random => Random,
            MonsterDamageType.Fists => Fists,
            MonsterDamageType.None => None,
            MonsterDamageType.Physical => Physical,
            MonsterDamageType.PlayerAuto => PAuto,
            _ => fallback == MonsterDamageType.PlayerAuto ? PAuto : Auto,
        };
}

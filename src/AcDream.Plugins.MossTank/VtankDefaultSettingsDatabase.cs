using System.Reflection;

namespace AcDream.Plugins.MossTank;

internal static class VtankDefaultSettingsDatabase
{
    private const string ResourceSuffix = ".VtankDefaultSettings.usd";
    private static readonly Lazy<string> RawText = new(LoadText);
    private static readonly Lazy<RechargeHandlerRow[]> DefaultRows = new(LoadDefaultRows);
    private static readonly Lazy<IReadOnlyDictionary<string, int>> CategoryBitmasksByName =
        new(LoadCategoryBitmasks);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> DescriptionsByName =
        new(LoadDescriptions);
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<VtankEnumValue>>>
        EnumValuesByName = new(LoadEnumValues);

    public static VtankDatabase Parse() => VtankDatabase.Parse(RawText.Value);

    /// <summary>VTank's own shipped <c>RechargeHandlerSet</c> rows (26, in file order).</summary>
    public static IReadOnlyList<RechargeHandlerRow> DefaultRechargeHandlerRows => DefaultRows.Value;

    public static IReadOnlyDictionary<string, int> SettingCategoryBitmasks => CategoryBitmasksByName.Value;

    public static IReadOnlyDictionary<string, string> SettingDescriptions => DescriptionsByName.Value;

    public static IReadOnlyDictionary<string, IReadOnlyList<VtankEnumValue>> SettingEnumValues =>
        EnumValuesByName.Value;

    private static IReadOnlyDictionary<string, IReadOnlyList<VtankEnumValue>> LoadEnumValues()
    {
        VtankDatabase database = VtankDatabase.Parse(RawText.Value);
        VtankTable? enumInfo = database.Find("SettingsEnumInfo");
        var map = new Dictionary<string, List<VtankEnumValue>>(StringComparer.OrdinalIgnoreCase);
        if (enumInfo is null)
            return map.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<VtankEnumValue>)pair.Value,
                StringComparer.OrdinalIgnoreCase);
        int nameColumn = enumInfo.ColumnIndex("Setting");
        int valueColumn = enumInfo.ColumnIndex("Value");
        int labelColumn = enumInfo.ColumnIndex("EnumValue");
        if (nameColumn < 0 || valueColumn < 0 || labelColumn < 0)
            return map.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<VtankEnumValue>)pair.Value,
                StringComparer.OrdinalIgnoreCase);
        foreach (VtankRow row in enumInfo.Rows)
        {
            string name = row.Cells[nameColumn].AsString();
            var entry = new VtankEnumValue(
                row.Cells[valueColumn].AsInt(),
                row.Cells[labelColumn].AsString());
            if (!map.TryGetValue(name, out List<VtankEnumValue>? list))
                map[name] = list = [];
            list.Add(entry);
        }
        return map.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<VtankEnumValue>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> LoadDescriptions()
    {
        VtankDatabase database = VtankDatabase.Parse(RawText.Value);
        VtankTable? settings = database.Find("Settings");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settings is null)
            return map;
        int nameColumn = settings.ColumnIndex("Setting");
        int descriptionColumn = settings.ColumnIndex("Description");
        if (nameColumn < 0 || descriptionColumn < 0)
            return map;
        foreach (VtankRow row in settings.Rows)
        {
            string description = row.Cells[descriptionColumn].AsString();
            if (!string.IsNullOrWhiteSpace(description))
                map[row.Cells[nameColumn].AsString()] = description;
        }
        return map;
    }

    private static IReadOnlyDictionary<string, int> LoadCategoryBitmasks()
    {
        VtankDatabase database = VtankDatabase.Parse(RawText.Value);
        VtankTable? categories = database.Find("SettingsCategories");
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (categories is null)
            return map;
        int nameColumn = categories.ColumnIndex("Setting");
        int bitsColumn = categories.ColumnIndex("Categories");
        if (nameColumn < 0 || bitsColumn < 0)
            return map;
        foreach (VtankRow row in categories.Rows)
            map[row.Cells[nameColumn].AsString()] = row.Cells[bitsColumn].AsInt();
        return map;
    }

    private static string LoadText()
    {
        Assembly assembly = typeof(VtankDefaultSettingsDatabase).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(
            static name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                "The embedded VTank default settings (.usd) document is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static RechargeHandlerRow[] LoadDefaultRows()
    {
        VtankDatabase database = VtankDatabase.Parse(RawText.Value);
        VtankTable? settings = database.Find("Settings");
        if (settings is null)
            return [];
        int nameColumn = settings.ColumnIndex("Setting");
        int valueColumn = settings.ColumnIndex("Value");
        if (nameColumn < 0 || valueColumn < 0)
            return [];
        foreach (VtankRow row in settings.Rows)
        {
            if (!row.Cells[nameColumn].AsString().Equals(
                    "RechargeHandlerSet", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            VtankCell cell = row.Cells[valueColumn];
            if (cell.Tag == "TABLE" && cell.Table is { } table)
                return VtankSettingsProfileSerializer.ParseRechargeHandlerSet(table);
        }
        return [];
    }
}

internal readonly record struct VtankEnumValue(int Value, string Label);

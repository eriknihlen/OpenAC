using System.Globalization;
using System.Reflection;

namespace AcDream.Plugins.MossTank;

internal readonly record struct VtankCraftRecipe(
    string FirstItem,
    string SecondItem,
    string ResultItem,
    int ResultCount,
    uint RequiredSkill,
    int Difficulty,
    int Id);

internal static class VtankCraftDatabase
{
    private const string ResourceSuffix = ".VtankCraftRecipes.tsv";
    private static readonly Lazy<Catalog> Loaded = new(Load);

    public static IReadOnlyList<VtankCraftRecipe> Recipes => Loaded.Value.All;

    public static IReadOnlyList<VtankCraftRecipe> ForResult(string resultName)
    {
        if (string.IsNullOrWhiteSpace(resultName))
            return Array.Empty<VtankCraftRecipe>();
        return Loaded.Value.ByResult.TryGetValue(
            resultName.Trim(),
            out VtankCraftRecipe[]? recipes)
                ? recipes
                : Array.Empty<VtankCraftRecipe>();
    }

    private static Catalog Load()
    {
        Assembly assembly = typeof(VtankCraftDatabase).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(
            static name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                "The embedded VTank CraftInteractions table is missing.");
        using var reader = new StreamReader(stream);
        var all = new List<VtankCraftRecipe>(757);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] fields = line.Split('\t');
            if (fields.Length != 7)
                throw new InvalidDataException("Malformed VTank craft row.");
            all.Add(new VtankCraftRecipe(
                fields[0],
                fields[1],
                fields[2],
                int.Parse(fields[3], CultureInfo.InvariantCulture),
                uint.Parse(fields[4], CultureInfo.InvariantCulture),
                int.Parse(fields[5], CultureInfo.InvariantCulture),
                int.Parse(fields[6], CultureInfo.InvariantCulture)));
        }
        if (all.Count != 757)
        {
            throw new InvalidDataException(
                $"Expected 757 official VTank craft rows, found {all.Count}.");
        }
        Dictionary<string, VtankCraftRecipe[]> byResult = all
            .GroupBy(static recipe => recipe.ResultItem, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(recipe => recipe.Id).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        return new Catalog(all.ToArray(), byResult);
    }

    private sealed record Catalog(
        VtankCraftRecipe[] All,
        Dictionary<string, VtankCraftRecipe[]> ByResult);
}

using System.Reflection;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class MossTankProfileStoreSideCarTests
{
    private static readonly string[] GroupPrefixes = ["Combat", "Buff", "Vitals", "Inventory"];

    [Fact]
    public void SideCarDocumentHasNoFieldNamedForARealVtankSetting()
    {
        Type sideCar = typeof(MossTankProfileStore).GetNestedType(
            "SideCarDocument", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MossTankProfileStore.SideCarDocument was not found by reflection.");
        var catalogNames = new HashSet<string>(
            VtankOptionCatalog.Names, StringComparer.OrdinalIgnoreCase);

        var overlaps = new List<string>();
        foreach (PropertyInfo property in sideCar.GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            string name = property.Name;
            if (catalogNames.Contains(name))
            {
                overlaps.Add(name);
                continue;
            }
            foreach (string prefix in GroupPrefixes)
            {
                if (name.Length > prefix.Length
                    && name.StartsWith(prefix, StringComparison.Ordinal)
                    && catalogNames.Contains(name[prefix.Length..]))
                {
                    overlaps.Add(name);
                    break;
                }
            }
        }

        Assert.Empty(overlaps);
    }
}

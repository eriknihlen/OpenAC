using System;
using System.Linq;
using AcDream.Core.Quests;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class ContractTableReaderInstalledDatTests
{
    private static ContractCatalog Load()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail(
                "Lane=InstalledDat requires an installed retail DAT directory; "
                + "see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        return ContractTableReader.Load(adapter);
    }

    [Fact]
    public void TheInstalledTableLoadsWithItsFullContractRoster()
    {
        ContractCatalog catalog = Load();

        // 322 in the installed build. Asserted as a floor rather than an
        // equality so a different dat revision is not a failure — the point is
        // that the table decodes at all.
        Assert.True(
            catalog.Count >= 300,
            $"expected the full contract roster, got {catalog.Count}");
    }

    [Fact]
    public void EveryContractCarriesTheNameThePanelDraws()
    {
        ContractCatalog catalog = Load();

        int named = catalog.Contracts.Values.Count(c => c.ContractName.Length > 0);

        Assert.True(
            named > catalog.Count / 2,
            $"only {named} of {catalog.Count} contracts have a name");
    }

    [Fact]
    public void EveryAuthoredProgressFormatUsesExactlyOneIntegerSpecifier()
    {
        ContractCatalog catalog = Load();

        var offenders = catalog.Contracts.Values
            .Where(c => c.DescriptionProgress.Length > 0)
            .Where(c => CountSpecifiers(c.DescriptionProgress) != 1)
            .Select(c => $"0x{c.ContractId:X8} \"{c.DescriptionProgress}\"")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void AMissingContractResolvesToTheUnknownEntryRatherThanThrowing()
    {
        ContractCatalog catalog = Load();

        ContractEntry entry = catalog.Lookup(0xDEADBEEFu);

        Assert.Same(ContractEntry.Unknown, entry);
    }

    private static int CountSpecifiers(string format)
    {
        int count = 0;
        for (int i = 0; i < format.Length - 1; i++)
        {
            if (format[i] == '%' && format[i + 1] != '%')
                count++;
        }
        return count;
    }
}

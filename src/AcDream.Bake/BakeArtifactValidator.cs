using AcDream.Content.Pak;

namespace AcDream.Bake;

public static class BakeArtifactValidator
{
    public static void Validate(
        string path,
        in PakHeader expectedHeader,
        int expectedTocCount,
        IReadOnlyDictionary<PakAssetType, int>? expectedTypeCounts = null)
    {
        using var reader = new PakReader(path);
        var actual = reader.Header;

        if (actual.FormatVersion != PakFormat.CurrentFormatVersion)
            throw new InvalidDataException(
                $"bake format version {actual.FormatVersion} does not match " +
                $"{PakFormat.CurrentFormatVersion}");
        if (actual.BakeToolVersion != PakFormat.CurrentBakeToolVersion)
            throw new InvalidDataException(
                $"bake tool version {actual.BakeToolVersion} does not match " +
                $"{PakFormat.CurrentBakeToolVersion}");
        if (actual.PortalIteration != expectedHeader.PortalIteration ||
            actual.CellIteration != expectedHeader.CellIteration ||
            actual.HighResIteration != expectedHeader.HighResIteration ||
            actual.LanguageIteration != expectedHeader.LanguageIteration)
        {
            throw new InvalidDataException(
                "bake DAT iterations do not match the source collection");
        }
        if (actual.TocCount != checked((uint)expectedTocCount))
            throw new InvalidDataException(
                $"bake TOC count {actual.TocCount} does not match expected " +
                $"{expectedTocCount}");

        reader.ValidateTocStructure();
        if (expectedTypeCounts is null)
            return;

        foreach ((PakAssetType type, int expectedCount) in expectedTypeCounts)
        {
            int actualCount = reader.CountEntries(type);
            if (actualCount != expectedCount)
            {
                throw new InvalidDataException(
                    $"bake catalog type {type} contains {actualCount} keys; " +
                    $"expected {expectedCount}");
            }
        }
    }
}

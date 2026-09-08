using AcDream.App.Tests.Rendering.Walk;

namespace AcDream.App.Tests.Rendering;

public sealed class TerrainParticleCellVisibilityTests
{
    [Fact]
    public void ProductionRetainsNoReconstructedVisibilityOrLightFeedbackSymbols()
    {
        string sourceRoot = Path.Combine(WalkOracleTraceRepoRoot.Find(), "src");
        string[] production = Directory.GetFiles(
            sourceRoot,
            "*.cs",
            SearchOption.AllDirectories);
        string[] forbidden =
        [
            "CollectVisibleCells",
            "TerrainVisibleCellIds",
            "ObserveDrawableCells",
            "ClearDrawableCells",
        ];

        foreach (string symbol in forbidden)
        {
            string[] owners = production
                .Where(path => File.ReadAllText(path).Contains(
                    symbol,
                    StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(sourceRoot, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                owners.Length == 0,
                $"Production symbol {symbol} remains in: {string.Join(", ", owners)}");
        }
    }
}

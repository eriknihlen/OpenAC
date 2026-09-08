namespace AcDream.Launcher.Core.Installation;

public enum ContentWorkKind
{
    None,
    Overlay,
    FullRebuild,
    Verify,
}

/// <summary>
/// One resolved recipe migration. Overlay ids are acdream-bake's existing
/// hexadecimal DAT-id filters; landblocks use its existing 8-bit hexadecimal
/// landblock filter. A plan is deliberately data-only so update orchestration
/// and the UI do not need to understand extraction algorithms.
/// </summary>
public sealed record ContentMigrationPlan(
    uint FromRecipeVersion,
    uint TargetRecipeVersion,
    ContentWorkKind Kind,
    string Reason,
    IReadOnlyList<uint>? DatIds = null,
    IReadOnlyList<byte>? Landblocks = null)
{
    public IReadOnlyList<uint> EffectiveDatIds => DatIds ?? [];

    public IReadOnlyList<byte> EffectiveLandblocks => Landblocks ?? [];
}

public static class ContentMigrationCatalog
{
    private static readonly IReadOnlyDictionary<uint, ContentMigrationPlan> Steps =
        new Dictionary<uint, ContentMigrationPlan>
        {
            [2] = FullRebuild(1, 2, "prepared EnvCell identity changed"),
            [3] = FullRebuild(2, 3, "render-pass translucency moved into prepared meshes"),
            [4] = FullRebuild(3, 4, "flat collision and EnvCell topology were added"),
            [5] = FullRebuild(
                4,
                5,
                "solid-colour positive mesh faces must be regenerated"),
            [6] = FullRebuild(
                5,
                6,
                "the optimized pak v2 texture catalog and compression format require one full rebuild"),
            [7] = FullRebuild(
                6,
                7,
                "GfxObj portal admission now uses the authored DrawingBSP sphere"),
            [8] = FullRebuild(
                7,
                8,
                "exact CellStruct surface-index subset construction"),
            [9] = FullRebuild(
                8,
                9,
                "authored surface opacity in prepared mesh batches"),
            [10] = FullRebuild(
                9,
                10,
                "exact resolved SetSurface state in prepared mesh batches"),
        };

    public static ContentMigrationPlan Resolve(uint fromRecipeVersion, uint targetRecipeVersion)
    {
        if (fromRecipeVersion == 0 || targetRecipeVersion == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fromRecipeVersion),
                "Content recipe versions must be positive.");
        }

        if (fromRecipeVersion == targetRecipeVersion)
        {
            return new ContentMigrationPlan(
                fromRecipeVersion,
                targetRecipeVersion,
                ContentWorkKind.None,
                "Prepared content already matches this client.");
        }

        if (fromRecipeVersion > targetRecipeVersion)
        {
            throw new InvalidOperationException(
                $"Prepared content recipe {fromRecipeVersion} is newer than this "
                + $"launcher's recipe {targetRecipeVersion}.");
        }

        var ids = new HashSet<uint>();
        var landblocks = new HashSet<byte>();
        var reasons = new List<string>();
        ContentWorkKind combinedKind = ContentWorkKind.None;
        for (uint target = checked(fromRecipeVersion + 1);
             target <= targetRecipeVersion;
             target++)
        {
            if (!Steps.TryGetValue(target, out ContentMigrationPlan? step)
                || step.FromRecipeVersion != target - 1)
            {
                throw new InvalidOperationException(
                    $"No prepared-content migration is published for recipe "
                    + $"{target - 1} to {target}.");
            }

            reasons.Add(step.Reason);
            if (step.Kind == ContentWorkKind.FullRebuild)
            {
                combinedKind = ContentWorkKind.FullRebuild;
            }
            else if (combinedKind != ContentWorkKind.FullRebuild
                     && step.Kind == ContentWorkKind.Overlay)
            {
                combinedKind = ContentWorkKind.Overlay;
            }

            foreach (uint id in step.EffectiveDatIds)
            {
                ids.Add(id);
            }

            foreach (byte landblock in step.EffectiveLandblocks)
            {
                landblocks.Add(landblock);
            }
        }

        return new ContentMigrationPlan(
            fromRecipeVersion,
            targetRecipeVersion,
            combinedKind,
            string.Join("; ", reasons),
            ids.Order().ToArray(),
            landblocks.Order().ToArray());
    }

    private static ContentMigrationPlan FullRebuild(
        uint from,
        uint target,
        string reason) =>
        new(from, target, ContentWorkKind.FullRebuild, reason);
}

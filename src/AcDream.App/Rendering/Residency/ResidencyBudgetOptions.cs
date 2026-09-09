using System.Globalization;

namespace AcDream.App.Rendering.Residency;

public sealed record ResidencyBudgetOptions(
    long ObjectMeshGpuBytes,
    int ObjectMeshUnownedEntries,
    long PreparedMeshCpuBytes,
    int PreparedMeshCpuEntries,
    long MeshStagingBytes,
    int MeshStagingEntries,
    long CompositePhysicalBytes,
    long CompositeUnownedBytes,
    long StandaloneUnownedBytes,
    int StandaloneUnownedEntries,
    long AnimationBytes,
    int AnimationEntries,
    long AudioBytes,
    long AlphaScratchBytes)
{
    public const long MiB = 1024L * 1024L;

    public static ResidencyBudgetOptions Default { get; } = new(
        ObjectMeshGpuBytes: 1024 * MiB,
        ObjectMeshUnownedEntries: 50,
        PreparedMeshCpuBytes: 128 * MiB,
        PreparedMeshCpuEntries: 100,
        MeshStagingBytes: 128 * MiB,
        MeshStagingEntries: 256,
        CompositePhysicalBytes: 128 * MiB,
        CompositeUnownedBytes: 64 * MiB,
        StandaloneUnownedBytes: 32 * MiB,
        StandaloneUnownedEntries: 256,
        AnimationBytes: 64 * MiB,
        AnimationEntries: 512,
        AudioBytes: 32 * MiB,
        AlphaScratchBytes: 16 * MiB);

    internal static ResidencyBudgetOptions Parse(
        Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        ResidencyBudgetOptions defaults = Default;
        return new ResidencyBudgetOptions(
            ObjectMeshGpuBytes: ParseMiB(
                env("ACDREAM_RESIDENCY_MESH_GPU_MIB"),
                defaults.ObjectMeshGpuBytes),
            ObjectMeshUnownedEntries: ParseCount(
                env("ACDREAM_RESIDENCY_MESH_UNOWNED_ENTRIES"),
                defaults.ObjectMeshUnownedEntries),
            PreparedMeshCpuBytes: ParseMiB(
                env("ACDREAM_RESIDENCY_PREPARED_MESH_MIB"),
                defaults.PreparedMeshCpuBytes),
            PreparedMeshCpuEntries: ParseCount(
                env("ACDREAM_RESIDENCY_PREPARED_MESH_ENTRIES"),
                defaults.PreparedMeshCpuEntries),
            MeshStagingBytes: ParseMiB(
                env("ACDREAM_RESIDENCY_MESH_STAGING_MIB"),
                defaults.MeshStagingBytes),
            MeshStagingEntries: ParseCount(
                env("ACDREAM_RESIDENCY_MESH_STAGING_ENTRIES"),
                defaults.MeshStagingEntries),
            CompositePhysicalBytes: ParseMiB(
                env("ACDREAM_RESIDENCY_COMPOSITE_PHYSICAL_MIB"),
                defaults.CompositePhysicalBytes),
            CompositeUnownedBytes: ParseMiB(
                env("ACDREAM_RESIDENCY_COMPOSITE_UNOWNED_MIB"),
                defaults.CompositeUnownedBytes),
            StandaloneUnownedBytes: ParseMiB(
                env("ACDREAM_RESIDENCY_STANDALONE_UNOWNED_MIB"),
                defaults.StandaloneUnownedBytes),
            StandaloneUnownedEntries: ParseCount(
                env("ACDREAM_RESIDENCY_STANDALONE_UNOWNED_ENTRIES"),
                defaults.StandaloneUnownedEntries),
            AnimationBytes: ParseMiB(
                env("ACDREAM_RESIDENCY_ANIMATION_MIB"),
                defaults.AnimationBytes),
            AnimationEntries: ParseCount(
                env("ACDREAM_RESIDENCY_ANIMATION_ENTRIES"),
                defaults.AnimationEntries),
            AudioBytes: ParseMiB(
                env("ACDREAM_RESIDENCY_AUDIO_MIB"),
                defaults.AudioBytes),
            AlphaScratchBytes: ParseMiB(
                env("ACDREAM_RESIDENCY_ALPHA_SCRATCH_MIB"),
                defaults.AlphaScratchBytes));
    }

    private static long ParseMiB(string? value, long fallback)
    {
        if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long mebibytes)
            || mebibytes <= 0
            || mebibytes > long.MaxValue / MiB)
        {
            return fallback;
        }
        return checked(mebibytes * MiB);
    }

    private static int ParseCount(string? value, int fallback) =>
        int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int count)
        && count > 0
            ? count
            : fallback;
}

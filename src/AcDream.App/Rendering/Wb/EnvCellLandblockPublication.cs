using System.Numerics;

namespace AcDream.App.Rendering.Wb;

public interface IEnvCellLandblockPublisher
{
    EnvCellLandblockPublication PreparePublication(
        EnvCellLandblockBuild build);

    bool AdvancePreparationOne(EnvCellLandblockPublication publication);

    void CommitPublication(EnvCellLandblockPublication publication);
}

public sealed class EnvCellLandblockPublication
{
    internal EnvCellLandblockPublication(
        object owner,
        EnvCellLandblockBuild build)
    {
        Owner = owner;
        Build = build;
        Replacement = new EnvCellLandblock
        {
            GridX = (int)((build.LandblockId >> 24) & 0xFFu),
            GridY = (int)((build.LandblockId >> 16) & 0xFFu),
        };
        TotalBounds = new WbBoundingBox(
            new Vector3(float.MaxValue),
            new Vector3(float.MinValue));
    }

    internal object Owner { get; }
    internal EnvCellLandblockBuild Build { get; }
    internal EnvCellLandblock Replacement { get; }
    internal WbBoundingBox TotalBounds { get; set; }
    internal int ShellCursor { get; set; }
    internal bool PreparationCommitted { get; set; }
    internal bool PublicationCommitted { get; set; }
}

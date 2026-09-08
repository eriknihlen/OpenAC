namespace AcDream.App.Rendering.Wb;

public static class EnvCellMeshPreparationScheduler
{
    public static void Schedule(
        EnvCellLandblockBuild build,
        ObjectMeshManager meshManager)
    {
        var scheduled = new HashSet<ulong>();
        foreach (var shell in build.Shells)
        {
            if (!scheduled.Add(shell.GeometryId))
                continue;
            _ = meshManager.PrepareEnvCellGeomMeshDataAsync(
                shell.GeometryId,
                shell.CellId,
                shell.EnvironmentId,
                shell.CellStructure,
                new List<ushort>(shell.Surfaces));
        }
    }
}

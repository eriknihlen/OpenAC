using System;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using DatReaderWriter;
using DatReaderWriter.Options;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;
using Env = System.Environment;

namespace AcDream.Core.Tests.Conformance;

public static class ConformanceDats
{
    private const uint EnvironmentFilePrefix = 0x0D000000u; // dat namespace for Environment files

    /// <summary>The Holtburg landblock these fixtures live in.</summary>
    public const uint HoltburgLandblock = 0xA9B40000u;

    public static string? ResolveDatDir()
    {
        var fromEnv = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;
        var def = Path.Combine(
            Env.GetFolderPath(Env.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(def) ? def : null;
    }

    public static string FixturesDir =>
        Path.Combine(SolutionRoot(), "tests", "AcDream.Core.Tests", "Conformance", "Fixtures");

    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AcDream.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate AcDream.slnx from " + AppContext.BaseDirectory);
    }

    public static Matrix4x4 WorldTransform(DatEnvCell datCell) =>
        Matrix4x4.CreateFromQuaternion(datCell.Position.Orientation) *
        Matrix4x4.CreateTranslation(datCell.Position.Origin);

    public static EnvCell LoadEnvCell(DatCollection dats, PhysicsDataCache cache, uint cellId)
    {
        var datCell = dats.Get<DatEnvCell>(cellId)
            ?? throw new InvalidOperationException($"EnvCell 0x{cellId:X8} not found in dats");
        var environment = dats.Get<DatEnvironment>(EnvironmentFilePrefix | datCell.EnvironmentId)
            ?? throw new InvalidOperationException($"Environment 0x{datCell.EnvironmentId:X8} not found");
        if (!environment.Cells.TryGetValue(datCell.CellStructure, out var cellStruct) || cellStruct is null)
            throw new InvalidOperationException($"CellStruct {datCell.CellStructure} missing from environment");

        var world = WorldTransform(datCell);
        cache.CacheCellStruct(cellId, datCell, cellStruct, world);
        return EnvCell.FromDat(cellId, datCell, cellStruct, world);
    }
}

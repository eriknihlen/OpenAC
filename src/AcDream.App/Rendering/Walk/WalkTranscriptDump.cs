using System.Numerics;
using System.Text;

namespace AcDream.App.Rendering.Walk;

internal static class WalkTranscriptDump
{
    private static bool Enabled =>
        AcDream.Core.Rendering.RenderingDiagnostics.DumpWalkTranscriptEnabled;

    internal static void PrintFrameRoot(
        int frameNumber,
        uint cameraCellId,
        Vector3 landblockLocalOrigin,
        Vector3 forward)
    {
        if (!Enabled) return;

        Vector3 worldUp = MathF.Abs(Vector3.Dot(forward, Vector3.UnitZ)) > 0.999f
            ? Vector3.UnitY
            : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, worldUp));
        Vector3 up = Vector3.Cross(right, forward);
        var basis = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            0f, 0f, 0f, 1f);
        Quaternion q = Quaternion.CreateFromRotationMatrix(basis);

        Console.WriteLine($"F {frameNumber}");
        Console.WriteLine(
            $"P {cameraCellId.ToString("x8")} "
            + $"{HexOf(landblockLocalOrigin.X)} {HexOf(landblockLocalOrigin.Y)} {HexOf(landblockLocalOrigin.Z)} "
            + $"{HexOf(q.W)} {HexOf(q.X)} {HexOf(q.Y)} {HexOf(q.Z)}");
    }

    internal static void PrintLandscape()
    {
        if (!Enabled) return;
        Console.WriteLine("LS");
    }

    internal static void PrintBuilding(uint positionCellId)
    {
        if (!Enabled) return;
        Console.WriteLine($"BLD {positionCellId.ToString("x8")}");
    }

    internal static void PrintDrawInside(uint cellId)
    {
        if (!Enabled) return;
        Console.WriteLine($"DI {cellId.ToString("x8")}");
    }

    internal static void PrintDrawCells(
        bool outdoorPview, int outsideViewCount, IReadOnlyList<uint> cells)
    {
        if (!Enabled) return;
        var sb = new StringBuilder(48 + cells.Count * 9);
        sb.Append("DC pv=").Append(outdoorPview ? "00000001" : "00000000");
        sb.Append(" ov=").Append(outsideViewCount);
        sb.Append(" n=").Append(cells.Count).Append(':');
        for (int i = 0; i < cells.Count; i++)
            sb.Append(' ').Append(cells[i].ToString("x8"));
        Console.WriteLine(sb.ToString());
    }

    internal static void PrintLandCell(uint cellId)
    {
        if (!Enabled) return;
        Console.WriteLine($"LC {cellId.ToString("x8")}");
    }

    internal static void PrintSortCell(uint cellId)
    {
        if (!Enabled) return;
        Console.WriteLine($"SC {cellId.ToString("x8")}");
    }

    internal static void PrintEnvCellShell(uint cellId)
    {
        if (!Enabled) return;
        Console.WriteLine($"EC {cellId.ToString("x8")}");
    }

    internal static void PrintObjectCellTurn(uint cellId)
    {
        if (!Enabled) return;
        Console.WriteLine($"OC {cellId.ToString("x8")}");
    }

    internal static uint LodCellId(uint landblockId, int sideCellCount, int cellIndex)
    {
        int x = cellIndex / sideCellCount;
        int y = cellIndex % sideCellCount;
        return (landblockId & 0xFFFF0000u) | checked((uint)(x * 8 + y + 1));
    }

    private static string HexOf(float value)
        => unchecked((uint)BitConverter.SingleToInt32Bits(value)).ToString("x8");
}

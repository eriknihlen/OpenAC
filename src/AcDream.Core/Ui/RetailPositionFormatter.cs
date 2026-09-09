using System.Globalization;
using AcDream.Core.Physics;

namespace AcDream.Core.Ui;

public static class RetailPositionFormatter
{
    public static string Format(Position position)
    {
        var p = position.Frame.Origin;
        var q = position.Frame.Orientation;
        return string.Create(CultureInfo.InvariantCulture,
            $"0x{position.ObjCellId:X8} [{p.X:F6} {p.Y:F6} {p.Z:F6}] " +
            $"{q.W:F6} {q.X:F6} {q.Y:F6} {q.Z:F6}");
    }

    public static string? FormatOutdoorCell(uint cellId) =>
        RadarCoordinates.TryFromCell(cellId, out var coordinates)
            ? coordinates.CombinedText
            : null;
}

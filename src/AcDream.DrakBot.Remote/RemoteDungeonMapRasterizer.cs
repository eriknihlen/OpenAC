using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// One floor plan of a dungeon: one height band, north up, half a metre a
/// pixel, in the frame the phone's dot uses: pixel x = wx / 0.5 - XMin,
/// pixel y = (Height - 1) - (wy / 0.5 - YMin).
/// </summary>
public sealed record RemoteDungeonMapLayer(int Layer, double Z, int Width, int Height, int XMin, int YMin, byte[] Png, string ETag);

/// <summary>A dungeon's floor plans, lowest band first, all in one frame so they overlay.</summary>
public sealed record RemoteDungeonMaps(uint LandblockId, IReadOnlyList<RemoteDungeonMapLayer> Layers, DateTime BuiltUtc);

/// <summary>
/// Draws a dungeon's floor plans from its geometry: the floors painted,
/// the walls drawn over them, one image per height band. A band is a
/// height most of the floor area sits at - a dungeon's storeys - and every
/// polygon goes to the band nearest it, so a ramp lands with the floor it
/// leaves from. Kept plain on purpose: the bot's own layers (its patrol,
/// the hazards, where it stalls) are what make the map worth reading, and
/// the phone draws those over this.
/// </summary>
public static class RemoteDungeonMapRasterizer
{
    public const double MetresPerPixel = 0.5;
    /// <summary>Air around the dungeon so nothing sits on the edge.</summary>
    public const int MarginPixels = 2;
    /// <summary>Floor area is binned by height this finely when the storeys are found.</summary>
    public const double LevelBinMetres = 1d;
    /// <summary>Two candidate storeys closer than this are one storey (the heavier).</summary>
    public const double LevelMergeMetres = 3d;
    /// <summary>
    /// A storey needs this much floor (square metres) at its height, or it
    /// is a landing on a ramp; a dungeon smaller than this all over still
    /// gets its one storey.
    /// </summary>
    public const double LevelMinArea = 60d;
    public const int MaxLayers = 12;
    /// <summary>A landblock is 384 pixels across; anything wider than this is not a dungeon the phone can use.</summary>
    public const int MaxSide = 1024;

    private static readonly Rgba Floor = new(200, 205, 214, 255);
    private static readonly Rgba Ramp = new(160, 168, 182, 255);
    private static readonly Rgba Wall = new(34, 39, 50, 255);

    /// <summary>The floor plans of a dungeon, or null when there is no floor to draw.</summary>
    public static RemoteDungeonMaps? Render(RemoteDungeonGeometry geometry, DateTime builtUtc)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        double[] levels = Levels(geometry.Polygons);
        if (levels.Length == 0)
            return null;

        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        foreach (RemoteDungeonPolygon polygon in geometry.Polygons)
        {
            foreach (Vector2 point in polygon.Points)
            {
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                    continue;
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
        }
        if (!float.IsFinite(minX) || !float.IsFinite(minY))
            return null;
        int xMin = (int)Math.Floor(minX / MetresPerPixel) - MarginPixels;
        int yMin = (int)Math.Floor(minY / MetresPerPixel) - MarginPixels;
        int width = (int)Math.Ceiling(maxX / MetresPerPixel) + MarginPixels - xMin + 1;
        int height = (int)Math.Ceiling(maxY / MetresPerPixel) + MarginPixels - yMin + 1;
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
            return null;

        // Every polygon to its band, in pixel space (y down: row 0 is north).
        var byLevel = new List<(RemoteDungeonSurface Kind, Vector2[] Points)>[levels.Length];
        for (int index = 0; index < byLevel.Length; index++)
            byLevel[index] = [];
        foreach (RemoteDungeonPolygon polygon in geometry.Polygons)
        {
            if (polygon.Points.Count < 2)
                continue;
            double z = polygon.Kind == RemoteDungeonSurface.Wall
                ? polygon.ZMin + 0.5
                : (polygon.ZMin + polygon.ZMax) / 2d;
            var points = new Vector2[polygon.Points.Count];
            for (int index = 0; index < points.Length; index++)
            {
                Vector2 point = polygon.Points[index];
                points[index] = new Vector2(
                    (float)(point.X / MetresPerPixel - xMin),
                    (float)((height - 1) - (point.Y / MetresPerPixel - yMin)));
            }
            byLevel[Nearest(levels, z)].Add((polygon.Kind, points));
        }

        var layers = new RemoteDungeonMapLayer[levels.Length];
        for (int level = 0; level < levels.Length; level++)
        {
            var canvas = new Canvas(width, height);
            // Floors first, ramps and floors both filled and outlined (a
            // ledge thinner than a pixel still leaves its trace), then the
            // walls, but only where they stand beside a floor of this band:
            // a tall room's upper walls belong to no storey but their own
            // floor's, and drawn on the storey above they would read as a
            // room that is not there.
            foreach ((RemoteDungeonSurface kind, Vector2[] points) in byLevel[level])
            {
                if (kind == RemoteDungeonSurface.Wall)
                    continue;
                Rgba colour = kind == RemoteDungeonSurface.Ramp ? Ramp : Floor;
                canvas.Fill(points, colour);
                canvas.Outline(points, colour);
            }
            canvas.MarkFloor();
            foreach ((RemoteDungeonSurface kind, Vector2[] points) in byLevel[level])
            {
                if (kind == RemoteDungeonSurface.Wall)
                    canvas.Outline(points, Wall, besideFloor: true);
            }
            byte[] png = canvas.ToPng();
            layers[level] = new RemoteDungeonMapLayer(level, levels[level], width, height, xMin, yMin, png, ETag(png));
        }
        return new RemoteDungeonMaps(geometry.LandblockId & 0xFFFF0000u, layers, builtUtc);
    }

    /// <summary>
    /// The storeys: heights where the floor area piles up. Floor area is
    /// binned by height; a bin that carries a room's worth and outweighs
    /// its neighbours is a storey, and storeys within a stride of each
    /// other are one. Ascending.
    /// </summary>
    internal static double[] Levels(IReadOnlyList<RemoteDungeonPolygon> polygons)
    {
        ArgumentNullException.ThrowIfNull(polygons);
        var weight = new SortedDictionary<int, double>();
        foreach (RemoteDungeonPolygon polygon in polygons)
        {
            if (polygon.Kind == RemoteDungeonSurface.Wall || polygon.Points.Count < 3)
                continue;
            double z = (polygon.ZMin + polygon.ZMax) / 2d;
            if (!double.IsFinite(z))
                continue;
            int bin = (int)Math.Round(z / LevelBinMetres);
            weight[bin] = weight.GetValueOrDefault(bin) + Area(polygon.Points);
        }
        if (weight.Count == 0)
            return [];
        double heaviest = 0d;
        foreach (double w in weight.Values)
            heaviest = Math.Max(heaviest, w);
        if (heaviest <= 0d)
            return [];

        double enough = Math.Min(LevelMinArea, heaviest);
        var merged = new List<(double Z, double Weight)>();
        foreach ((int bin, double w) in weight)
        {
            if (w < enough)
                continue;
            if (w < weight.GetValueOrDefault(bin - 1) || w < weight.GetValueOrDefault(bin + 1))
                continue;
            double z = bin * LevelBinMetres;
            if (merged.Count > 0 && z - merged[^1].Z < LevelMergeMetres)
            {
                if (w > merged[^1].Weight)
                    merged[^1] = (z, w);
                continue;
            }
            merged.Add((z, w));
        }
        if (merged.Count > MaxLayers)
            merged = merged.OrderByDescending(m => m.Weight).Take(MaxLayers).OrderBy(m => m.Z).ToList();
        var levels = new double[merged.Count];
        for (int index = 0; index < levels.Length; index++)
            levels[index] = merged[index].Z;
        return levels;
    }

    /// <summary>The band nearest a height; the lower on a tie.</summary>
    internal static int Nearest(double[] levels, double z)
    {
        int best = 0;
        double distance = double.PositiveInfinity;
        for (int index = 0; index < levels.Length; index++)
        {
            double d = Math.Abs(levels[index] - z);
            if (d < distance)
            {
                distance = d;
                best = index;
            }
        }
        return best;
    }

    /// <summary>The area a polygon covers on the map (the shoelace, unsigned).</summary>
    public static double Area(IReadOnlyList<Vector2> points)
    {
        double sum = 0d;
        for (int index = 0; index < points.Count; index++)
        {
            Vector2 a = points[index];
            Vector2 b = points[(index + 1) % points.Count];
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return Math.Abs(sum) / 2d;
    }

    private static string ETag(byte[] png)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in png)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return "\"" + hash.ToString("x16", CultureInfo.InvariantCulture) + "\"";
    }

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    /// <summary>An RGBA canvas with the two strokes a floor plan needs: fill a polygon, outline one.</summary>
    private sealed class Canvas(int width, int height)
    {
        /// <summary>Pixels beside a painted one (one step in any direction) count as beside a floor.</summary>
        private const int BesideFloorPixels = 1;

        private readonly byte[] _pixels = new byte[width * height * 4];
        private readonly List<float> _crossings = [];
        private bool[]? _floor;

        /// <summary>Remembers what is painted so far as the floor, for the walls to stand beside.</summary>
        public void MarkFloor()
        {
            _floor = new bool[width * height];
            for (int index = 0; index < _floor.Length; index++)
                _floor[index] = _pixels[index * 4 + 3] != 0;
        }

        /// <summary>Even-odd scanline fill, sampling at pixel centres.</summary>
        public void Fill(Vector2[] points, Rgba colour)
        {
            if (points.Length < 3)
                return;
            float top = float.PositiveInfinity, bottom = float.NegativeInfinity;
            foreach (Vector2 point in points)
            {
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }
            if (!float.IsFinite(top) || !float.IsFinite(bottom))
                return;
            int firstRow = Math.Max(0, (int)Math.Floor(top));
            int lastRow = Math.Min(height - 1, (int)Math.Ceiling(bottom));
            for (int row = firstRow; row <= lastRow; row++)
            {
                float y = row + 0.5f;
                _crossings.Clear();
                for (int index = 0; index < points.Length; index++)
                {
                    Vector2 a = points[index];
                    Vector2 b = points[(index + 1) % points.Length];
                    if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                        _crossings.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
                if (_crossings.Count < 2)
                    continue;
                _crossings.Sort();
                for (int index = 0; index + 1 < _crossings.Count; index += 2)
                {
                    int first = Math.Max(0, (int)Math.Ceiling(_crossings[index] - 0.5f));
                    int last = Math.Min(width - 1, (int)Math.Floor(_crossings[index + 1] - 0.5f));
                    for (int column = first; column <= last; column++)
                        Set(column, row, colour);
                }
            }
        }

        /// <summary>Every edge as a one-pixel line; beside the floor only, when asked (after <see cref="MarkFloor"/>).</summary>
        public void Outline(Vector2[] points, Rgba colour, bool besideFloor = false)
        {
            for (int index = 0; index < points.Length; index++)
            {
                Vector2 a = points[index];
                Vector2 b = points[(index + 1) % points.Length];
                Line(a, b, colour, besideFloor);
            }
        }

        private void Line(Vector2 a, Vector2 b, Rgba colour, bool besideFloor)
        {
            if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(b.X) || !float.IsFinite(b.Y))
                return;
            float dx = b.X - a.X, dy = b.Y - a.Y;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))));
            if (steps > MaxSide * 2)
                return;
            for (int step = 0; step <= steps; step++)
            {
                float t = step / (float)steps;
                int x = (int)Math.Floor(a.X + dx * t);
                int y = (int)Math.Floor(a.Y + dy * t);
                if (!besideFloor || BesideFloor(x, y))
                    Set(x, y, colour);
            }
        }

        private bool BesideFloor(int x, int y)
        {
            if (_floor is null)
                return true;
            for (int dy = -BesideFloorPixels; dy <= BesideFloorPixels; dy++)
            {
                int row = y + dy;
                if (row < 0 || row >= height)
                    continue;
                for (int dx = -BesideFloorPixels; dx <= BesideFloorPixels; dx++)
                {
                    int column = x + dx;
                    if (column >= 0 && column < width && _floor[row * width + column])
                        return true;
                }
            }
            return false;
        }

        private void Set(int x, int y, Rgba colour)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;
            int offset = (y * width + x) * 4;
            _pixels[offset] = colour.R;
            _pixels[offset + 1] = colour.G;
            _pixels[offset + 2] = colour.B;
            _pixels[offset + 3] = colour.A;
        }

        public byte[] ToPng() => RemotePng.Encode(width, height, _pixels);
    }
}

/// <summary>
/// The little of PNG a floor plan needs: 8-bit RGBA, no interlace, no
/// filtering, one zlib stream. Kept here so the remote owes no image
/// library for a few small images.
/// </summary>
internal static class RemotePng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("rgba must hold width * height pixels", nameof(rgba));

        int stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (int row = 0; row < height; row++)
        {
            // The filter byte (none), then the row.
            rgba.Slice(row * stride, stride).CopyTo(raw.AsSpan(row * (stride + 1) + 1, stride));
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // colour type: RGBA
        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>The pixels back out of a PNG this class wrote (tests read their own work with it).</summary>
    internal static byte[] Decode(byte[] png, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG");
        width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        using var idat = new MemoryStream();
        int offset = 8;
        while (offset + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
                idat.Write(png, offset + 8, length);
            offset += 12 + length;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        int stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        zlib.ReadExactly(raw);
        var pixels = new byte[height * stride];
        for (int row = 0; row < height; row++)
        {
            if (raw[row * (stride + 1)] != 0)
                throw new InvalidDataException("filtered rows are not written by this encoder");
            raw.AsSpan(row * (stride + 1) + 1, stride).CopyTo(pixels.AsSpan(row * stride, stride));
        }
        return pixels;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = Crc(typeBytes, 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes, uint crc)
    {
        foreach (byte b in bytes)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

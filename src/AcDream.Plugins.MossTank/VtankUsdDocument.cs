using System.Globalization;
using System.Linq;
using System.Text;

namespace AcDream.Plugins.MossTank;

internal sealed class VtankCell
{
    private static readonly HashSet<string> KnownScalarTags = ["d", "i", "u", "f", "s", "b"];

    internal VtankCell() { }

    /// <summary>The raw type tag: <c>d</c>/<c>i</c>/<c>u</c>/<c>f</c>/<c>s</c>/<c>b</c>/<c>TABLE</c>/<c>ba</c>/other.</summary>
    public required string Tag { get; init; }

    /// <summary>Raw text of the single value line, for every scalar tag except <c>ba</c>.</summary>
    public string? ScalarText { get; set; }

    public VtankTable? Table { get; set; }

    public string? BlobText { get; set; }

    public static VtankCell Double(double value) => new()
    {
        Tag = "d",
        ScalarText = FormatDouble(value),
    };

    public static VtankCell Int(int value) => new()
    {
        Tag = "i",
        ScalarText = value.ToString(CultureInfo.InvariantCulture),
    };

    public static VtankCell UInt(uint value) => new()
    {
        Tag = "u",
        ScalarText = value.ToString(CultureInfo.InvariantCulture),
    };

    public static VtankCell Float(float value) => new()
    {
        Tag = "f",
        ScalarText = FormatDouble(value),
    };

    public static VtankCell String(string value) => new()
    {
        Tag = "s",
        ScalarText = value.Replace("\n", string.Empty, StringComparison.Ordinal),
    };

    public static VtankCell Bool(bool value) => new()
    {
        Tag = "b",
        ScalarText = value ? "True" : "False",
    };

    public static VtankCell NestedTable(VtankTable table) => new()
    {
        Tag = "TABLE",
        Table = table,
    };

    public double AsDouble() => double.Parse(
        ScalarText ?? "0",
        NumberStyles.Float,
        CultureInfo.InvariantCulture);

    public int AsInt() => int.Parse(
        ScalarText ?? "0",
        NumberStyles.Integer,
        CultureInfo.InvariantCulture);

    public uint AsUInt() => uint.Parse(
        ScalarText ?? "0",
        NumberStyles.Integer,
        CultureInfo.InvariantCulture);

    public float AsFloat() => float.Parse(
        ScalarText ?? "0",
        NumberStyles.Float,
        CultureInfo.InvariantCulture);

    public string AsString() => ScalarText ?? string.Empty;

    public bool AsBool() => string.Equals(ScalarText, "True", StringComparison.Ordinal);

    internal static string FormatDouble(double value) =>
        value.ToString("G15", CultureInfo.InvariantCulture);

    internal void WriteTo(StringBuilder sb)
    {
        VtankWriter.AppendLine(sb, Tag);
        if (Tag == "TABLE")
        {
            Table!.WriteTo(sb);
        }
        else if (Tag == "ba")
        {
            string blob = BlobText ?? string.Empty;
            VtankWriter.AppendLine(sb, blob.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append(blob);
        }
        else if (KnownScalarTags.Contains(Tag))
        {
            VtankWriter.AppendLine(sb, ScalarText ?? string.Empty);
        }
    }
}

internal static class VtankWriter
{
    internal static void AppendLine(StringBuilder sb, string text) =>
        sb.Append(text).Append("\r\n");
}

internal sealed class VtankRow
{
    public List<VtankCell> Cells { get; } = [];

    internal void WriteTo(StringBuilder sb)
    {
        foreach (VtankCell cell in Cells)
            cell.WriteTo(sb);
    }
}

internal sealed class VtankTable
{
    public List<string> ColumnNames { get; } = [];
    public List<bool> IndexFlags { get; } = [];
    public List<VtankRow> Rows { get; } = [];

    public int ColumnIndex(string name) => ColumnNames.FindIndex(
        column => column.Equals(name, StringComparison.Ordinal));

    internal static VtankTable Read(VtankLineCursor cursor)
    {
        var table = new VtankTable();
        int columnCount = cursor.ReadInt();
        for (int i = 0; i < columnCount; i++)
            table.ColumnNames.Add(cursor.ReadLine());
        for (int i = 0; i < columnCount; i++)
            table.IndexFlags.Add(cursor.ReadLine() == "y");
        int rowCount = cursor.ReadInt();
        for (int r = 0; r < rowCount; r++)
        {
            var row = new VtankRow();
            for (int c = 0; c < columnCount; c++)
                row.Cells.Add(VtankDatabaseReader.ReadCell(cursor));
            table.Rows.Add(row);
        }
        return table;
    }

    internal void WriteTo(StringBuilder sb)
    {
        VtankWriter.AppendLine(sb, ColumnNames.Count.ToString(CultureInfo.InvariantCulture));
        foreach (string column in ColumnNames)
            VtankWriter.AppendLine(sb, column);
        foreach (bool flag in IndexFlags)
            VtankWriter.AppendLine(sb, flag ? "y" : "n");
        VtankWriter.AppendLine(sb, Rows.Count.ToString(CultureInfo.InvariantCulture));
        foreach (VtankRow row in Rows)
            row.WriteTo(sb);
    }
}

/// <summary>The whole <c>y</c> database: an ordered set of named tables.</summary>
internal sealed class VtankDatabase
{
    public List<(string Name, VtankTable Table)> Tables { get; } = [];

    public VtankTable? Find(string name) => Tables
        .Where(entry => entry.Name.Equals(name, StringComparison.Ordinal))
        .Select(static entry => entry.Table)
        .FirstOrDefault();

    public static VtankDatabase Parse(string text)
    {
        var cursor = new VtankLineCursor(text);
        var database = new VtankDatabase();
        int tableCount = cursor.ReadInt();
        for (int i = 0; i < tableCount; i++)
        {
            string name = cursor.ReadLine();
            VtankTable table = VtankTable.Read(cursor);
            database.Tables.Add((name, table));
        }
        return database;
    }

    public string Render()
    {
        var sb = new StringBuilder();
        var ordered = Tables
            .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        VtankWriter.AppendLine(sb, ordered.Length.ToString(CultureInfo.InvariantCulture));
        foreach ((string name, VtankTable table) in ordered)
        {
            VtankWriter.AppendLine(sb, name);
            table.WriteTo(sb);
        }
        return sb.ToString();
    }
}

internal static class VtankDatabaseReader
{
    public static VtankCell ReadCell(VtankLineCursor cursor)
    {
        string tag = cursor.ReadLine();
        switch (tag)
        {
            case "TABLE":
                return VtankCell.NestedTable(VtankTable.Read(cursor));
            case "ba":
                int length = cursor.ReadInt();
                return new VtankCellBuilder(tag) { BlobText = cursor.ReadBlob(length) }
                    .Build();
            case "d" or "i" or "u" or "f" or "s" or "b":
                return new VtankCellBuilder(tag) { ScalarText = cursor.ReadLine() }.Build();
            default:
                return new VtankCellBuilder(tag).Build();
        }
    }

    private readonly struct VtankCellBuilder(string tag)
    {
        public string? ScalarText { get; init; }
        public string? BlobText { get; init; }

        public VtankCell Build() => VtankCellFactory.Create(tag, ScalarText, BlobText);
    }
}

internal static class VtankCellFactory
{
    public static VtankCell Create(string tag, string? scalarText, string? blobText) => new()
    {
        Tag = tag,
        ScalarText = scalarText,
        BlobText = blobText,
    };
}

internal sealed class VtankLineCursor
{
    private readonly string _text;
    private int _position;
    private int _lineNumber = 1;

    public VtankLineCursor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
    }

    public int LineNumber => _lineNumber;

    public string ReadLine()
    {
        if (_position >= _text.Length)
        {
            throw new FormatException(
                $"line {LineNumber}: unexpected end of file (expected another line).");
        }
        int newlineIndex = _text.IndexOf('\n', _position);
        string line;
        if (newlineIndex < 0)
        {
            line = _text[_position..];
            _position = _text.Length;
        }
        else
        {
            line = _text[_position..newlineIndex];
            _position = newlineIndex + 1;
        }
        if (line.EndsWith('\r'))
            line = line[..^1];
        _lineNumber++;
        return line;
    }

    public int ReadInt()
    {
        string line = ReadLine();
        if (!int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new FormatException(
                $"line {LineNumber - 1}: expected an integer, got '{line}'.");
        }
        return value;
    }

    public string ReadBlob(int length)
    {
        if (length <= 0)
            return string.Empty;
        if (_position + length > _text.Length)
        {
            throw new FormatException(
                $"line {LineNumber}: blob of {length} characters exceeds remaining input.");
        }
        string blob = _text.Substring(_position, length);
        _position += length;
        foreach (char c in blob)
        {
            if (c == '\n')
                _lineNumber++;
        }
        return blob;
    }
}

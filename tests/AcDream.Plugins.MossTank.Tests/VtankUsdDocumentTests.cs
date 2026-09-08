namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankUsdDocumentTests
{
    private const string EmbeddedBlob = "line1\r\nline2";

    private static string SingleBaCellDocument(string blob)
    {
        string header = string.Join(
            "\r\n",
            "1", "T", "1", "Col1", "n", "1", "ba", blob.Length.ToString());
        return header + "\r\n" + blob;
    }

    [Fact]
    public void BaBlobWithEmbeddedCrlfRoundTripsExactCharacterCount()
    {
        string document = SingleBaCellDocument(EmbeddedBlob);

        VtankDatabase parsed = VtankDatabase.Parse(document);
        VtankTable table = parsed.Find("T")!;
        VtankCell cell = table.Rows[0].Cells[0];

        Assert.Equal("ba", cell.Tag);
        Assert.Equal(EmbeddedBlob, cell.BlobText);
        Assert.Equal(EmbeddedBlob.Length, cell.BlobText!.Length);

        string rewritten = parsed.Render();
        Assert.Equal(document, rewritten);
    }

    [Fact]
    public void BaBlobLengthCountsEmbeddedCrAndLfAsSeparateCharacters()
    {
        string lfOnly = "line1\nline2";
        string crlf = "line1\r\nline2";
        Assert.Equal(lfOnly.Length + 1, crlf.Length);

        VtankDatabase parsedLf = VtankDatabase.Parse(SingleBaCellDocument(lfOnly));
        VtankDatabase parsedCrlf = VtankDatabase.Parse(SingleBaCellDocument(crlf));

        Assert.Equal(lfOnly, parsedLf.Find("T")!.Rows[0].Cells[0].BlobText);
        Assert.Equal(crlf, parsedCrlf.Find("T")!.Rows[0].Cells[0].BlobText);
    }

    [Fact]
    public void UnrecognizedTagConsumesOnlyItsOwnLineNotTheNextCellsValue()
    {
        string document = string.Join(
            "\r\n",
            "1", "T", "2", "Col1", "Col2", "n", "n", "1", "0", "s", "hello", string.Empty);

        VtankDatabase parsed = VtankDatabase.Parse(document);
        VtankRow row = parsed.Find("T")!.Rows[0];

        Assert.Equal("0", row.Cells[0].Tag);
        Assert.Null(row.Cells[0].ScalarText);
        Assert.Equal("s", row.Cells[1].Tag);
        Assert.Equal("hello", row.Cells[1].AsString());

        string rewritten = parsed.Render();
        Assert.Equal(document, rewritten);
    }

    [Fact]
    public void RenderEmitsTablesInNameOrderRegardlessOfInsertionOrder()
    {
        var database = new VtankDatabase();
        foreach (string name in new[] { "Zebra", "Apple", "Mango" })
            database.Tables.Add((name, new VtankTable()));

        string rendered = database.Render();
        VtankDatabase reparsed = VtankDatabase.Parse(rendered);

        Assert.Equal(
            ["Apple", "Mango", "Zebra"],
            reparsed.Tables.Select(static entry => entry.Name).ToArray());
    }

    [Fact]
    public void StringCellStripsEmbeddedNewlineAtConstructionNotJustOnWrite()
    {
        VtankCell cell = VtankCell.String("line1\nline2");

        Assert.Equal("line1line2", cell.AsString());

        var sb = new System.Text.StringBuilder();
        cell.WriteTo(sb);
        Assert.Equal("s\r\nline1line2\r\n", sb.ToString());
    }
}

using System.IO;
using AcDream.Core.Spells;

namespace AcDream.Core.Tests.Spells;

public sealed class SpellTableTests
{
    private const string Header =
        "Spell ID,Spell ID [Hex],Name,SortKey,IconId [Hex],Difficulty,Duration,Family,Flags [Hex],Generation,IsDebuff,IsFastWindup,IsFellowship,IsIrresistible,IsOffensive,IsUntargetted,Mana,School,Speed,Spell Words,CasterEffect,TargetEffect,TargetMask [Hex],Type,Description,Unknown1,Unknown2,Unknown3,Unknown4,Unknown5,Unknown6,Unknown7,Unknown8,Unknown9,Unknown10";

    private static SpellTable LoadFrom(string csv) =>
        SpellTable.LoadFromReader(new StringReader(csv));

    [Fact]
    public void Empty_TableHasZeroEntries()
    {
        Assert.Equal(0, SpellTable.Empty.Count);
        Assert.False(SpellTable.Empty.TryGet(1u, out _));
    }

    [Fact]
    public void Create_RejectsDuplicateSpellIds()
    {
        SpellMetadata spell = new(
            1u, "One", "War Magic", 0u, 0u, "", 0f, 0,
            false, false, "", 0, 0, 0u, 0, false, false, true,
            0f, 0u, 0u, 0u, 0);

        Assert.Throws<ArgumentException>(() => SpellTable.Create([spell, spell]));
    }

    [Fact]
    public void LoadFromReader_HeaderOnly_EmptyTable()
    {
        var table = LoadFrom(Header);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void LoadFromReader_SingleSimpleRow_HasMetadata()
    {
        // Spell ID 1 = Strength Other I, Family 1 (the actual real-data row).
        string csv =
            Header + "\n" +
            "1,0x1,Strength Other I,6450,0x600138C,1,1800,1,0x6,1,False,False,False,True,False,False,10,Creature Enchantment,0,Malar Cazael,0,6,0x10,1,Increases Strength.,5,1,1,1,0,0,0,0,0,0";
        var table = LoadFrom(csv);

        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet(1u, out var meta));
        Assert.Equal("Strength Other I", meta.Name);
        Assert.Equal("Creature Enchantment", meta.School);
        Assert.Equal(1u, meta.Family);
        Assert.Equal(0x600138Cu, meta.IconId);
        Assert.Equal("Malar Cazael", meta.SpellWords);
        Assert.Equal(1800f, meta.Duration);
        Assert.Equal(10, meta.ManaCost);
        Assert.False(meta.IsDebuff);
        Assert.False(meta.IsFellowship);
        Assert.Equal("Increases Strength.", meta.Description);
    }

    [Fact]
    public void LoadFromReader_QuotedDescriptionWithCommas_ParsesIntactly()
    {
        string csv =
            Header + "\n" +
            "2,0x2,Strength Self I,6464,0x600138C,1,1800,1,0x400C,1,False,True,False,True,False,True,15,Creature Enchantment,0.01,Malar Cazael,0,6,0x10,1,\"Increases the caster's Strength by 10 points, lasting 30 minutes.\",0,0,1,2,0,0,0,0,0,0";
        var table = LoadFrom(csv);

        Assert.True(table.TryGet(2u, out var meta));
        Assert.Equal("Increases the caster's Strength by 10 points, lasting 30 minutes.", meta.Description);
        Assert.Equal("Strength Self I", meta.Name);
    }

    [Fact]
    public void LoadFromReader_BlankLines_AreSkipped()
    {
        string csv =
            Header + "\n" +
            "1,0x1,Test,0,0x0,1,1,1,0x0,1,False,False,False,False,False,False,1,War Magic,0,Words,0,0,0x0,1,Desc,0,0,0,0,0,0,0,0,0,0\n" +
            "\n" +
            "  \n" +
            "2,0x2,Test2,0,0x0,1,1,1,0x0,1,False,False,False,False,False,False,1,War Magic,0,Words,0,0,0x0,1,Desc,0,0,0,0,0,0,0,0,0,0";
        var table = LoadFrom(csv);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void LoadFromReader_BadSpellId_RowSkipped()
    {
        string csv =
            Header + "\n" +
            "not_a_uint,0x1,Bad,0,0x0,1,1,1,0x0,1,False,False,False,False,False,False,1,War Magic,0,Words,0,0,0x0,1,Desc,0,0,0,0,0,0,0,0,0,0\n" +
            "5,0x5,Good,0,0x0,1,1,1,0x0,1,False,False,False,False,False,False,1,War Magic,0,Words,0,0,0x0,1,Desc,0,0,0,0,0,0,0,0,0,0";
        var table = LoadFrom(csv);
        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet(5u, out _));
    }

    [Fact]
    public void TryGet_UnknownSpellId_ReturnsFalse()
    {
        var table = LoadFrom(Header);
        Assert.False(table.TryGet(99999u, out _));
    }

    [Fact]
    public void ParseRow_SimpleCsv_SplitsOnCommas()
    {
        var fields = SpellTable.ParseRow("a,b,c");
        Assert.Equal(new[] { "a", "b", "c" }, fields);
    }

    [Fact]
    public void ParseRow_QuotedFieldWithComma_KeepsComma()
    {
        var fields = SpellTable.ParseRow("a,\"b,c\",d");
        Assert.Equal(new[] { "a", "b,c", "d" }, fields);
    }

    [Fact]
    public void ParseRow_EscapedDoubleQuoteInsideQuotedField()
    {
        var fields = SpellTable.ParseRow("a,\"b\"\"c\",d");
        Assert.Equal(new[] { "a", "b\"c", "d" }, fields);
    }
}

using AcDream.App.UI.Layout;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

public sealed class AllegianceRankTitleTableTests
{

    [Theory]
    [InlineData(1, "Yeoman")]
    [InlineData(5, "Thane")]
    [InlineData(10, "High King")]
    public void AluvianMale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 1, gender: 1));

    [Theory]
    [InlineData(1, "Yeoman")]
    [InlineData(3, "Baroness")] // diverges from male's "Baron" at rank 3
    [InlineData(10, "High Queen")]
    public void AluvianFemale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 1, gender: 2));

    [Theory]
    [InlineData(1, "Sayyid")]
    [InlineData(5, "Naquib")]
    [InlineData(10, "Sultan")]
    public void GharundimMale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 2, gender: 1));

    [Theory]
    [InlineData(1, "Sayyida")]
    [InlineData(5, "Naquiba")]
    [InlineData(10, "Sultana")]
    public void GharundimFemale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 2, gender: 2));

    [Theory]
    [InlineData(1, "Jinin")]
    [InlineData(5, "Ta-chueh")]
    [InlineData(7, "Kou")]
    [InlineData(9, "Ou")]
    [InlineData(10, "Koutei")]
    public void ShoMale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 3, gender: 1));

    /// <summary>Rank 9 diverges from the male table ("Jo-ou" vs "Ou"); rank 7
    /// shares the male table's "Kou" indirection.</summary>
    [Theory]
    [InlineData(1, "Jinin")]
    [InlineData(7, "Kou")]
    [InlineData(9, "Jo-ou")]
    [InlineData(10, "Koutei")]
    public void ShoFemale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 3, gender: 2));

    [Theory]
    [InlineData(1, "Squire")]
    [InlineData(5, "Count")]
    [InlineData(10, "High King")]
    public void ViamontianMale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 4, gender: 1));

    [Theory]
    [InlineData(1, "Dame")]
    [InlineData(5, "Countess")]
    [InlineData(10, "High Queen")]
    public void ViamontianFemale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 4, gender: 2));

    [Theory]
    [InlineData(1, "Tenebrous")]
    [InlineData(5, "Void Knight")]
    [InlineData(10, "King")]
    public void ShadowboundMale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 5, gender: 1));

    [Theory]
    [InlineData(1, "Tenebrous")]
    [InlineData(6, "Void Lady")] // diverges from male's "Void Lord" at rank 6
    [InlineData(10, "Queen")]
    public void ShadowboundFemale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 5, gender: 2));

    /// <summary>Heritage id 0xA (Penumbraen) ALIASES to the Shadowbound
    /// functions on both gender dispatch branches — identical results to
    /// heritage 5.</summary>
    [Theory]
    [InlineData(1, 1, "Tenebrous")]
    [InlineData(2, 1, "Tenebrous")]
    [InlineData(1, 6, "Void Lord")]
    [InlineData(2, 6, "Void Lady")]
    public void Penumbraen_AliasesShadowbound(int gender, int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 0xA, gender: gender));

    [Fact]
    public void Penumbraen_MatchesShadowboundExactlyAcrossAllRanksAndGenders()
    {
        for (int gender = 1; gender <= 2; gender++)
        for (int rank = 1; rank <= 10; rank++)
        {
            Assert.Equal(
                AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 5, gender),
                AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 0xA, gender));
        }
    }

    [Theory]
    [InlineData(1, "Tribunus")]
    [InlineData(5, "Principes")]
    [InlineData(8, "Dux")] // PE-byte-recovered data-literal indirection
    [InlineData(10, "Primus")]
    public void Gearknight_MaleFunctionReusedForBothGenders(int rank, string expected)
    {
        Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 6, gender: 1));
        Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 6, gender: 2));
    }

    /// <summary>Tumerok authors only a MALE <c>Get*Title</c> function;
    /// dispatched for both genders on heritage 7.</summary>
    [Theory]
    [InlineData(1, "Xutua")]
    [InlineData(3, "Ona")] // PE-byte-recovered data-literal indirection
    [InlineData(6, "Rea")] // PE-byte-recovered data-literal indirection
    [InlineData(10, "Tah")] // PE-byte-recovered data-literal indirection
    public void Tumerok_MaleFunctionReusedForBothGenders(int rank, string expected)
    {
        Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 7, gender: 1));
        Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 7, gender: 2));
    }

    /// <summary>Lugian authors only a FEMALE <c>Get*Title</c> function;
    /// dispatched for both genders on heritage 8.</summary>
    [Theory]
    [InlineData(1, "Laigus")]
    [InlineData(5, "Obeloth")]
    [InlineData(10, "Tiatus")]
    public void Lugian_FemaleFunctionReusedForBothGenders(int rank, string expected)
    {
        Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 8, gender: 1));
        Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 8, gender: 2));
    }

    [Theory]
    [InlineData(1, "Ensign")]
    [InlineData(5, "Captain")]
    [InlineData(10, "Aulin")]
    public void EmpyreanMale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 9, gender: 1));

    [Theory]
    [InlineData(1, "Ensign")]
    [InlineData(9, "Ipharsia")] // diverges from male's "Ipharsin"
    [InlineData(10, "Aulia")]
    public void EmpyreanFemale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 9, gender: 2));

    [Theory]
    [InlineData(1, "Neophyte")]
    [InlineData(7, "Count")]
    [InlineData(8, "Viscount")]
    [InlineData(10, "Annointed")]
    public void UndeadMale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 0xB, gender: 1));

    [Theory]
    [InlineData(1, "Neophyte")]
    [InlineData(7, "Countess")]
    [InlineData(8, "Viscountess")] // diverges from male's "Viscount"
    [InlineData(10, "Annointed")]
    public void UndeadFemale_MatchesTheReferenceTable(int rank, string expected)
        => Assert.Equal(expected, AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 0xB, gender: 2));

    // ── Dispatch bounds and exclusions. ─────────────────────────────────────

    [Theory]
    [InlineData(12)] // Olthoi
    [InlineData(13)] // OlthoiAcid
    [InlineData(0)]
    [InlineData(-1)]
    public void Heritage_OutsideRange_ReturnsNullForBothGenders(int heritageGroup)
    {
        Assert.Null(AllegianceRankTitleTable.GetTitle(rank: 1, heritageGroup, gender: 1));
        Assert.Null(AllegianceRankTitleTable.GetTitle(rank: 1, heritageGroup, gender: 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Gender_Unrecognized_ReturnsNull(int gender)
        => Assert.Null(AllegianceRankTitleTable.GetTitle(rank: 1, heritageGroup: 1, gender));

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Rank_OutsideOneToTen_ReturnsNull(int rank)
        => Assert.Null(AllegianceRankTitleTable.GetTitle(rank, heritageGroup: 1, gender: 1));


    [Fact]
    public void ComposeFullName_ValidRank_PrefixesTitleWithSingleSpace()
    {
        string result = AllegianceRankTitleTable.ComposeFullName(
            rank: 3, heritageGroup: 1, gender: 2, name: "Aluvia");
        Assert.Equal("Baroness Aluvia", result);
    }

    [Theory]
    [InlineData(0)] // rank absent / zero
    [InlineData(11)] // rank out of range
    public void ComposeFullName_NoTitleResolved_ReturnsPlainNameUnmodified(int rank)
    {
        string result = AllegianceRankTitleTable.ComposeFullName(
            rank, heritageGroup: 1, gender: 1, name: "Somebody");
        Assert.Equal("Somebody", result);
    }

    [Fact]
    public void ComposeFullName_UnrecognizedHeritage_ReturnsPlainName()
    {
        string result = AllegianceRankTitleTable.ComposeFullName(
            rank: 5, heritageGroup: 12 /* Olthoi */, gender: 1, name: "Xarabydun");
        Assert.Equal("Xarabydun", result);
    }
}

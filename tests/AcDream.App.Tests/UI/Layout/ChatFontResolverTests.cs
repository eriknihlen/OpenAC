using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using SysEnv = System.Environment;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ChatFontResolverTests
{
    private const uint KnownFontDid = 0x40000000u;

    private static readonly string[] Faces =
        { "Arial", "CourierNew", "PalatinoLinotype", "Tahoma", "TimesNewRoman" };

    private static readonly string[] Sizes =
        { "Tiny", "Small", "Medium", "Large", "XL" };

    [Fact]
    public void TryResolveFontId_KnownPair_ResolvesTheDid()
    {
        var fontMap = new EnumIDMap();
        fontMap.ClientEnumToName[1] = "Chat_PalatinoLinotype_Small";
        fontMap.ClientEnumToID[1] = KnownFontDid;

        bool resolved = ChatFontResolver.TryResolveFontId(fontMap, faceIndex: 2, sizeIndex: 1, out uint fontDid);

        Assert.True(resolved);
        Assert.Equal(KnownFontDid, fontDid);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(5, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 5)]
    public void TryResolveFontId_OutOfRangeIndex_ReturnsFalse(int faceIndex, int sizeIndex)
    {
        var fontMap = new EnumIDMap();
        fontMap.ClientEnumToName[1] = "Chat_PalatinoLinotype_Small";
        fontMap.ClientEnumToID[1] = KnownFontDid;

        Assert.False(ChatFontResolver.TryResolveFontId(fontMap, faceIndex, sizeIndex, out uint fontDid));
        Assert.Equal(0u, fontDid);
    }

    [Fact]
    public void TryResolveFontId_NameNotInMap_ReturnsFalse()
    {
        var fontMap = new EnumIDMap();
        fontMap.ClientEnumToName[1] = "Chat_Arial_Tiny";
        fontMap.ClientEnumToID[1] = KnownFontDid;

        Assert.False(ChatFontResolver.TryResolveFontId(fontMap, faceIndex: 2, sizeIndex: 1, out uint fontDid));
        Assert.Equal(0u, fontDid);
    }

    [Fact]
    public void TryResolveFontId_NamePresentButNoIdEntry_ReturnsFalse()
    {
        var fontMap = new EnumIDMap();
        fontMap.ClientEnumToName[1] = "Chat_PalatinoLinotype_Small";

        Assert.False(ChatFontResolver.TryResolveFontId(fontMap, faceIndex: 2, sizeIndex: 1, out uint fontDid));
        Assert.Equal(0u, fontDid);
    }

    [Fact]
    public void TryResolveFontId_PicksTheNameForItsOwnFaceAndSizeIndex()
    {
        var fontMap = new EnumIDMap();
        var didByName = new Dictionary<string, uint>();
        uint enumValue = 0;
        foreach (string face in Faces)
        foreach (string size in Sizes)
        {
            string name = $"Chat_{face}_{size}";
            uint did = 0x50000000u + enumValue;
            fontMap.ClientEnumToName[enumValue] = name;
            fontMap.ClientEnumToID[enumValue] = did;
            didByName[name] = did;
            enumValue++;
        }

        for (int faceIndex = 0; faceIndex < Faces.Length; faceIndex++)
        for (int sizeIndex = 0; sizeIndex < Sizes.Length; sizeIndex++)
        {
            uint expectedDid = didByName[$"Chat_{Faces[faceIndex]}_{Sizes[sizeIndex]}"];
            bool resolved = ChatFontResolver.TryResolveFontId(fontMap, faceIndex, sizeIndex, out uint fontDid);
            Assert.True(resolved);
            Assert.Equal(expectedDid, fontDid);
        }
    }

    private static readonly (int Face, int Size, uint Did)[] RetailPairs =
    {
        (0, 0, 0x40000026u), (0, 1, 0x40000027u), (0, 2, 0x40000028u), (0, 3, 0x4000002Eu), (0, 4, 0x4000002Fu),
        (1, 0, 0x4000001Eu), (1, 1, 0x4000001Au), (1, 2, 0x4000001Fu), (1, 3, 0x40000030u), (1, 4, 0x40000031u),
        (2, 0, 0x40000002u), (2, 1, 0x40000000u), (2, 2, 0x40000001u), (2, 3, 0x40000004u), (2, 4, 0x40000005u),
        (3, 0, 0x40000008u), (3, 1, 0x40000009u), (3, 2, 0x4000000Au), (3, 3, 0x4000000Bu), (3, 4, 0x4000000Cu),
        (4, 0, 0x4000002Au), (4, 1, 0x4000002Bu), (4, 2, 0x40000032u), (4, 3, 0x4000002Cu), (4, 4, 0x4000002Du),
    };

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledDat_ResolvesAllTwentyFivePairs_AndEachDidLoadsAsAFont()
    {
        string datDir = SysEnv.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                SysEnv.GetFolderPath(SysEnv.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        if (!Directory.Exists(datDir)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir);

        foreach ((int face, int size, uint expectedDid) in RetailPairs)
        {
            bool resolved = ChatFontResolver.TryResolveFontId(dats, face, size, out uint fontDid);
            Assert.True(resolved, $"face {face} size {size} did not resolve");
            Assert.Equal(expectedDid, fontDid);
            Assert.True(
                dats.Portal.TryGet<Font>(fontDid, out _),
                $"0x{fontDid:X8} (face {face} size {size}) did not load as a Font");
        }
    }
}

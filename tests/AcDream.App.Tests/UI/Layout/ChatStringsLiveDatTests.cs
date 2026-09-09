using AcDream.App.UI.Layout;
using DatReaderWriter;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class ChatStringsLiveDatTests
{
    private static string DatDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");

    [InstalledDatFact]
    public void TalkFocusStrings_ResolveToTheAuthoredRetailSet()
    {
        using var dats = new DatCollection(
            DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        var strings = new DatStringResolver(dats);
        const uint table = 0x23000001u;

        string? Resolve(string key)
            => strings.Resolve(table, DatStringResolver.ComputeHash(key));

        // Button shorts (m_pChatTargetButtonText).
        Assert.Equal("Chat", Resolve("ID_Chat_ChatTargetMenu"));
        Assert.Equal("Tell", Resolve("ID_Chat_ChatTargetMenuSelected"));
        Assert.Equal("Fell", Resolve("ID_Chat_ChatTargetMenuFellows"));
        Assert.Equal("Pat", Resolve("ID_Chat_ChatTargetMenuPatron"));
        Assert.Equal("Mon", Resolve("ID_Chat_ChatTargetMenuMonarch"));
        Assert.Equal("Vas", Resolve("ID_Chat_ChatTargetMenuVassals"));
        Assert.Equal("Alg", Resolve("ID_Chat_ChatTargetMenuAllegiance"));
        Assert.Equal("Gen", Resolve("ID_Chat_ChatTargetMenuGeneral"));
        Assert.Equal("Trade", Resolve("ID_Chat_ChatTargetMenuTrade"));
        Assert.Equal("LFG", Resolve("ID_Chat_ChatTargetMenuLFG"));
        Assert.Equal("RP", Resolve("ID_Chat_ChatTargetMenuRoleplay"));
        Assert.Equal("Soc", Resolve("ID_Chat_ChatTargetMenuSociety"));
        Assert.Equal("Olt", Resolve("ID_Chat_ChatTargetMenuOlthoi"));

        Assert.Equal("Chat to All", Resolve("ID_Chat_TellToAll"));
        Assert.Equal("Tell to General Chat", Resolve("ID_Chat_TellToGeneral"));
        Assert.Equal("Tell to ", Resolve("ID_Chat_TellToSelected"));
        Assert.Equal("Tell to Selected", Resolve("ID_Chat_TellToSelectedNoSelection"));
        Assert.Equal("Squelch (ignore) ", Resolve("ID_Chat_SquelchSelected"));
        Assert.Equal(
            "Squelch (ignore) Selected",
            Resolve("ID_Chat_SquelchSelectedNoSelection"));
    }
}

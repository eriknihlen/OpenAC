using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class BotFilesTests
{
    private sealed class MemoryStorage(string? directory = null) : IPluginStorage
    {
        public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);
        public bool IsAvailable => true;
        public string? Directory => directory;
        public string? ReadText(string key) => Text.TryGetValue(key, out string? value) ? value : null;
        public byte[]? ReadBytes(string key) => Text.TryGetValue(key, out string? value) ? System.Text.Encoding.UTF8.GetBytes(value) : null;
        public IReadOnlyList<string> List(string prefix) => Text.Keys.Where(key => prefix.Length == 0 || key.StartsWith(prefix + "/", StringComparison.Ordinal)).ToArray();
        public void WriteText(string key, string content) => Text[key] = content;
        public bool Delete(string key) => Text.Remove(key);
    }

    [Fact]
    public void TheBotsOwnFolderComesFirstAndTheClientsVtankFolderIsTheFallback()
    {
        var own = new MemoryStorage(@"C:\plugins\acdream.drakbot");
        var vtank = new MemoryStorage(@"C:\data\vtank");
        var files = new BotFiles(own, vtank);
        own.Text["loot/mine.utl"] = "own";
        vtank.Text["mine.utl"] = "shared";
        vtank.Text["theirs.utl"] = "shared-only";
        vtank.Text["metas/quest.af"] = "af-in-metas";
        vtank.Text["navs/loop.nav"] = "nav-in-navs";

        Assert.Equal("own", files.ReadUtl("mine"));
        Assert.Equal("shared-only", files.ReadUtl("theirs"));
        Assert.Null(files.ReadUtl("nobody"));
        // VTank's own layout is flat; RynthAi's sorts into metas and navs; both are found.
        Assert.True(files.TryReadMeta("quest", out string? text, out _, out string path));
        Assert.Equal("af-in-metas", text);
        Assert.Equal("quest.af", path);
        Assert.Equal("nav-in-navs", files.ReadNav("loop"));

        // Listings: one name each, the bot's copy first, sorted.
        Assert.Equal(["mine.utl", "theirs.utl"], files.UtlFileNames());
        Assert.Equal(["quest.af"], files.MetaFileNames());
        Assert.Equal(["loop.nav"], files.NavFileNames());

        // Saves land in the bot's folder, never the shared one.
        files.WriteMeta("quest", "edited");
        Assert.Equal("edited", own.Text["metas/quest.af"]);
        Assert.Equal("af-in-metas", vtank.Text["metas/quest.af"]);
        Assert.Equal(@"C:\plugins\acdream.drakbot\loot", files.PathOf(BotFiles.LootFolder));
        Assert.EndsWith(Path.Combine("logs", BotFiles.LogDumpFile), files.WriteLogDump("x"));
        Assert.Equal("x", own.Text["logs/drakbot-log.txt"]);
    }

    [Fact]
    public void WithoutFileStorageThereIsNoFolderToOpen()
    {
        var files = new BotFiles(new MemoryStorage());
        Assert.Null(files.Directory);
        Assert.Null(files.PathOf(BotFiles.LootFolder));
        Assert.False(files.TryOpen(BotFiles.LootFolder, out string path));
        Assert.Equal(string.Empty, path);
        files.EnsureLayout();
        Assert.Empty(files.UtlFileNames());
    }
}

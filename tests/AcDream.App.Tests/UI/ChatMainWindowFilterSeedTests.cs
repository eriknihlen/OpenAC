using System;
using System.IO;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.UI;

public sealed class ChatMainWindowFilterSeedTests : IDisposable
{
    private readonly string _tempPath;

    public ChatMainWindowFilterSeedTests()
    {
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-chat-seed-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public void MountChatSeed_ReadsStoredMainWindowFilter_IntoChatWindowState()
    {
        var store = new SettingsStore(_tempPath);
        store.SaveChat(ChatSettings.Default with { ChatWindowMainFilter = 0x1ul });

        var windows = new ChatWindowState();

        windows.SetFilter(ChatWindowState.MainWindowId, store.LoadChat().ChatWindowMainFilter);

        Assert.Equal(0x1ul, windows.GetFilter(ChatWindowState.MainWindowId));
    }

    [Fact]
    public void MountChatSeed_WithNoStoredFile_SeedsTheRetailPostInitDefault()
    {
        var store = new SettingsStore(_tempPath); // file does not exist yet
        var windows = new ChatWindowState();

        windows.SetFilter(ChatWindowState.MainWindowId, store.LoadChat().ChatWindowMainFilter);

        Assert.Equal(ChatWindowState.MainWindowDefaultFilter, windows.GetFilter(ChatWindowState.MainWindowId));
        Assert.Equal(0xFBFFFFFFul, windows.GetFilter(ChatWindowState.MainWindowId));
    }
}

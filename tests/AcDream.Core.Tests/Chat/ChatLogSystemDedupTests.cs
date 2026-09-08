using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatLogSystemDedupTests
{
    [Fact]
    public void OnSystemMessage_ImmediateDuplicate_OnlyOneEntry()
    {
        var log = new ChatLog();
        log.OnSystemMessage("Unknown command: help", chatType: 0);
        log.OnSystemMessage("Unknown command: help", chatType: 0);

        // Second identical message arrived <1s later — suppressed.
        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void OnSystemMessage_DifferentText_BothEntries()
    {
        var log = new ChatLog();
        log.OnSystemMessage("Welcome to Asheron's Call", chatType: 0);
        log.OnSystemMessage("Use @acecommands to get a complete list of commands.", chatType: 0);

        // Different text — both retained even back-to-back.
        Assert.Equal(2, log.Snapshot().Length);
    }

    [Fact]
    public void OnSystemMessage_TripletDuplicate_StillOnlyOneEntry()
    {
        var log = new ChatLog();
        log.OnSystemMessage("Unknown command: help", chatType: 0);
        log.OnSystemMessage("Unknown command: help", chatType: 0);
        log.OnSystemMessage("Unknown command: help", chatType: 0);

        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void OnSystemMessage_DuplicateBookendingNonDuplicate_KeepsBothUnique()
    {
        var log = new ChatLog();
        log.OnSystemMessage("Unknown command: help", chatType: 0);
        log.OnSystemMessage("Welcome to Asheron's Call", chatType: 0);
        log.OnSystemMessage("Unknown command: help", chatType: 0);

        // First "Unknown..." retained, "Welcome..." retained, second
        // "Unknown..." is no longer the *immediate* duplicate (the
        // most-recent entry is "Welcome..."), so it's kept too.
        Assert.Equal(3, log.Snapshot().Length);
    }
}

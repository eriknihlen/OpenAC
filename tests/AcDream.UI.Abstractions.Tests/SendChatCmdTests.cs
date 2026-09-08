namespace AcDream.UI.Abstractions.Tests;

public sealed class SendChatCmdTests
{
    [Fact]
    public void Construct_DefaultsTargetNameToNull_ForNonTellChannels()
    {
        var cmd = new SendChatCmd(ChatChannelKind.Say, TargetName: null, Text: "hello");

        Assert.Equal(ChatChannelKind.Say, cmd.Channel);
        Assert.Null(cmd.TargetName);
        Assert.Equal("hello", cmd.Text);
    }

    [Fact]
    public void Equality_HoldsForRecordsWithSameValues()
    {
        var a = new SendChatCmd(ChatChannelKind.Tell, "Alice", "hi");
        var b = new SendChatCmd(ChatChannelKind.Tell, "Alice", "hi");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DiffersWhenChannelDiffers()
    {
        var a = new SendChatCmd(ChatChannelKind.Fellowship, null, "raid time");
        var b = new SendChatCmd(ChatChannelKind.Allegiance, null, "raid time");

        Assert.NotEqual(a, b);
    }
}

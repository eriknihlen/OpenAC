using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionChatTests
{
    private static WorldSession NewSession()
    {
        var ep = new IPEndPoint(IPAddress.Loopback, 65000);
        return new WorldSession(ep);
    }

    [Fact]
    public void SendTalk_EmitsBytesIdenticalToChatRequestsBuildTalk()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendTalk("hello");

        byte[] expected = ChatRequests.BuildTalk(1, "hello");
        Assert.NotNull(captured);
        Assert.Equal(expected, captured);
    }

    [Fact]
    public void SendTell_EmitsBytesIdenticalToChatRequestsBuildTell()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendTell("Alice", "hey");

        byte[] expected = ChatRequests.BuildTell(1, "Alice", "hey");
        Assert.NotNull(captured);
        Assert.Equal(expected, captured);
    }

    [Fact]
    public void SendChannel_IncrementsSequence_AndMatchesBuildChatChannel()
    {
        using var session = NewSession();
        var captured = new System.Collections.Generic.List<byte[]>();
        session.GameActionCapture = body => captured.Add(body);

        session.SendChannel(channelId: 0x00000800u, "raid plan");
        session.SendChannel(channelId: 0x02000000u, "allegiance ping");

        Assert.Equal(2, captured.Count);
        Assert.Equal(ChatRequests.BuildChatChannel(1, 0x00000800u, "raid plan"),     captured[0]);
        Assert.Equal(ChatRequests.BuildChatChannel(2, 0x02000000u, "allegiance ping"), captured[1]);
    }

    [Fact]
    public void SendTalk_NullText_Throws()
    {
        using var session = NewSession();
        Assert.Throws<ArgumentNullException>(() => session.SendTalk(null!));
    }

    [Fact]
    public void SendSoulEmote_EmitsRetailGameAction()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendSoulEmote("waves.");

        Assert.Equal(ClientCommandRequests.BuildSoulEmote(1u, "waves."), captured);
    }

    [Fact]
    public void SendTeleportToLifestone_EmitsRetailGameAction()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendTeleportToLifestone();

        Assert.NotNull(captured);
        Assert.Equal(InteractRequests.BuildTeleToLifestone(1), captured);
    }

    [Fact]
    public void SendEnterPkLite_EmitsRetailGameAction()
    {
        using var session = NewSession();
        byte[]? captured = null;
        session.GameActionCapture = body => captured = body;

        session.SendEnterPkLite();

        Assert.NotNull(captured);
        Assert.Equal(ClientCommandRequests.BuildEnterPkLite(1), captured);
    }
}

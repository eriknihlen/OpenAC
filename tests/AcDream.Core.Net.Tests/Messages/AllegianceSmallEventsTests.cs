using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class AllegianceSmallEventsTests
{
    [Fact]
    public void ParseAllegianceLoginNotification_RoundTrips_LoggedIn()
    {
        byte[] wire = new AceWireWriter().Write(0x50000042u).Write((uint)1).ToArray();

        var notice = GameEvents.ParseAllegianceLoginNotification(wire);

        Assert.NotNull(notice);
        Assert.Equal(0x50000042u, notice.Value.CharacterGuid);
        Assert.True(notice.Value.IsLoggedIn);
    }

    [Fact]
    public void ParseAllegianceLoginNotification_RoundTrips_LoggedOut()
    {
        byte[] wire = new AceWireWriter().Write(0x50000042u).Write((uint)0).ToArray();

        var notice = GameEvents.ParseAllegianceLoginNotification(wire);

        Assert.NotNull(notice);
        Assert.False(notice.Value.IsLoggedIn);
    }

    [Fact]
    public void ParseAllegianceLoginNotification_TruncatedPayload_ReturnsNull()
    {
        Assert.Null(GameEvents.ParseAllegianceLoginNotification(new byte[4]));
    }

    [Fact]
    public void ParseAllegianceUpdateDone_ReadsWeenieError()
    {
        byte[] wire = new AceWireWriter().Write(0u).ToArray();
        Assert.Equal(0u, GameEvents.ParseAllegianceUpdateDone(wire));
    }

    [Fact]
    public void ParseAllegianceUpdateDone_NonZeroErrorCode_RoundTrips()
    {
        byte[] wire = new AceWireWriter().Write(0x40Bu).ToArray();
        Assert.Equal(0x40Bu, GameEvents.ParseAllegianceUpdateDone(wire));
    }

    [Fact]
    public void ParseAllegianceUpdateAborted_ReadsWeenieError()
    {
        byte[] wire = new AceWireWriter().Write(0x40Cu).ToArray();
        Assert.Equal(0x40Cu, GameEvents.ParseAllegianceUpdateAborted(wire));
    }

    [Fact]
    public void ParseAllegianceUpdateDone_TruncatedPayload_ReturnsNull()
    {
        Assert.Null(GameEvents.ParseAllegianceUpdateDone(System.ReadOnlySpan<byte>.Empty));
    }
}

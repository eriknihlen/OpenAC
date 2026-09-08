using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ConfirmationTripleTests
{
    [Fact]
    public void ConfirmationType_SwearAllegianceIsOne_FellowshipIsFour()
    {
        Assert.Equal(1u, (uint)GameEvents.ConfirmationType.SwearAllegiance);
        Assert.Equal(4u, (uint)GameEvents.ConfirmationType.Fellowship);
    }

    [Fact]
    public void ParseConfirmationResponse_RoundTripsAgainstExistingBuilder()
    {
        byte[] wire = ClientCommandRequests.BuildConfirmationResponse(
            sequence: 5,
            confirmationType: (uint)GameEvents.ConfirmationType.Fellowship,
            contextId: 0x1234u,
            accepted: true);

        // Strip the 12-byte envelope/seq/opcode header the same way every
        // other GameEvents.Parse* function receives its payload (header
        // already stripped by the dispatcher) — here we strip the GameACTION
        // header by hand since BuildConfirmationResponse is a C→S builder.
        var response = GameEvents.ParseConfirmationResponse(wire.AsSpan(12));

        Assert.NotNull(response);
        Assert.Equal(GameEvents.ConfirmationType.Fellowship, response.Value.Type);
        Assert.Equal(0x1234u, response.Value.ContextId);
        Assert.True(response.Value.Accepted);
    }

    [Fact]
    public void ParseConfirmationResponse_GoldenByteVector_SwearAllegianceDeclined()
    {
        byte[] payload =
        [
            0x01, 0x00, 0x00, 0x00,
            0x99, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, // accepted = 0 (declined)
        ];

        var response = GameEvents.ParseConfirmationResponse(payload);

        Assert.NotNull(response);
        Assert.Equal(GameEvents.ConfirmationType.SwearAllegiance, response.Value.Type);
        Assert.Equal(0x99u, response.Value.ContextId);
        Assert.False(response.Value.Accepted);
    }

    [Fact]
    public void ParseConfirmationResponse_TruncatedPayload_ReturnsNull()
    {
        Assert.Null(GameEvents.ParseConfirmationResponse(new byte[8]));
    }
}

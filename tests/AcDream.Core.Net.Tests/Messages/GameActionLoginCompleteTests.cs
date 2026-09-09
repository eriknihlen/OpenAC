using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class GameActionLoginCompleteTests
{
    [Fact]
    public void Build_ProducesExactly12Bytes_WithCorrectOpcodes()
    {
        var body = GameActionLoginComplete.Build();

        // 4 bytes GameAction opcode + 4 bytes sequence + 4 bytes action type.
        Assert.Equal(12, body.Length);

        // Little-endian decode.
        uint gameActionOpcode = (uint)(body[0] | (body[1] << 8) | (body[2] << 16) | (body[3] << 24));
        uint sequence         = (uint)(body[4] | (body[5] << 8) | (body[6] << 16) | (body[7] << 24));
        uint actionType       = (uint)(body[8] | (body[9] << 8) | (body[10] << 16) | (body[11] << 24));

        Assert.Equal(0xF7B1u, gameActionOpcode);
        Assert.Equal(0u, sequence);
        Assert.Equal(0x000000A1u, actionType);
    }
}

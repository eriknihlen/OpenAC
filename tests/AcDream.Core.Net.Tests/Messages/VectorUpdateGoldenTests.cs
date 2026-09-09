using System;
using System.Numerics;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class VectorUpdateGoldenTests
{
    private static byte[] Golden(
        uint guid,
        Vector3 velocity,
        Vector3 omega,
        ushort instanceSequence,
        ushort vectorSequence)
        => AceWireWriter.GameMessage(VectorUpdate.Opcode)
            .WriteGuid(guid)
            .Write(velocity.X).Write(velocity.Y).Write(velocity.Z)
            .Write(omega.X).Write(omega.Y).Write(omega.Z)
            .Write(instanceSequence)
            .Write(vectorSequence)
            .ToArray();

    public static TheoryData<string, uint, Vector3, Vector3, ushort, ushort> Cases() => new()
    {
        // A remote player jumping: +Z velocity, no spin.
        { "jump", 0x50000001u, new Vector3(0f, 0f, 6.1f), Vector3.Zero, 355, 42 },
        { "run+turn", 0x7C95B01Au, new Vector3(2.94f, -1.25f, 0f), new Vector3(0f, 0f, 1.5f), 1, 2 },
        // Rest state — every field zero except the sequences.
        { "at-rest", 0x800114C0u, Vector3.Zero, Vector3.Zero, 0, 0 },
        { "negatives", 0xA9B40001u, new Vector3(-1f, -2f, -3f), new Vector3(-4f, -5f, -6f), 65535, 65534 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TryParse_AceGoldenBytes_DecodesEveryFieldExactly(
        string label,
        uint guid,
        Vector3 velocity,
        Vector3 omega,
        ushort instanceSequence,
        ushort vectorSequence)
    {
        byte[] body = Golden(guid, velocity, omega, instanceSequence, vectorSequence);

        Assert.Equal(36, body.Length);

        VectorUpdate.Parsed? parsed = VectorUpdate.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(guid, parsed!.Value.Guid);
        Assert.Equal(velocity, parsed.Value.Velocity);
        Assert.Equal(omega, parsed.Value.Omega);
        Assert.Equal(instanceSequence, parsed.Value.InstanceSequence);
        Assert.Equal(vectorSequence, parsed.Value.VectorSequence);
        Assert.False(string.IsNullOrEmpty(label));
    }

    [Fact]
    public void TryParse_WrongOpcode_ReturnsNull()
    {
        byte[] body = AceWireWriter.GameMessage(0xF74Cu)
            .WriteGuid(1u)
            .Write(0f).Write(0f).Write(0f)
            .Write(0f).Write(0f).Write(0f)
            .Write((ushort)0).Write((ushort)0)
            .ToArray();

        Assert.Null(VectorUpdate.TryParse(body));
    }

    [Fact]
    public void TryParse_TruncatedByOneByte_ReturnsNull()
    {
        byte[] body = Golden(1u, Vector3.One, Vector3.One, 1, 1);
        Assert.Null(VectorUpdate.TryParse(body.AsSpan(0, body.Length - 1)));
    }
}

using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class SoundEventTests
{
    private static byte[] Frame(uint opcode, uint guid, uint soundType, float volume)
    {
        var body = new byte[SoundEvent.WireSize];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), guid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), soundType);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(12), volume);
        return body;
    }

    [Fact]
    public void Opcode_And_WireSize_MatchRetail()
    {
        Assert.Equal(0xF750u, SoundEvent.Opcode);
        Assert.Equal(16, SoundEvent.WireSize);
    }

    [Fact]
    public void Parses_AllThreeFields_AtRetailOffsets()
    {
        byte[] body = Frame(SoundEvent.Opcode, 0x8000_1234u, 0x03u, 0.5f);

        SoundEvent? parsed = SoundEvent.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x8000_1234u, parsed!.Value.Guid);
        Assert.Equal(0x03u, parsed.Value.SoundType);
        Assert.Equal(0.5f, parsed.Value.Volume);
    }

    [Theory]
    // A spread across the SoundType range the server actually sends.
    [InlineData(0x30u)]   // HitFlesh1
    [InlineData(0x51u)]   // LifestoneOn
    [InlineData(0x8Fu)]   // PickUpItem
    [InlineData(0x91u)]   // ResistSpell
    [InlineData(0x97u)]   // ItemManaDepleted
    [InlineData(0xCCu)]
    public void Parses_EverySoundTypeSlot_Verbatim(uint soundType)
    {
        SoundEvent? parsed = SoundEvent.TryParse(
            Frame(SoundEvent.Opcode, 0x5000_000Au, soundType, 1f));
        Assert.Equal(soundType, parsed!.Value.SoundType);
    }

    [Fact]
    public void Rejects_WrongOpcode()
    {
        Assert.Null(SoundEvent.TryParse(Frame(0xF751u, 1u, 1u, 1f)));
    }

    [Fact]
    public void Rejects_ShortBody()
    {
        byte[] truncated = Frame(SoundEvent.Opcode, 1u, 1u, 1f)[..15];
        Assert.Null(SoundEvent.TryParse(truncated));
    }

    [Fact]
    public void Accepts_TrailingBytes()
    {
        byte[] padded = new byte[SoundEvent.WireSize + 8];
        Frame(SoundEvent.Opcode, 0xAAu, 0x37u, 0.25f).CopyTo(padded, 0);

        SoundEvent? parsed = SoundEvent.TryParse(padded);

        Assert.NotNull(parsed);
        Assert.Equal(0xAAu, parsed!.Value.Guid);
        Assert.Equal(0x37u, parsed.Value.SoundType);
        Assert.Equal(0.25f, parsed.Value.Volume);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(2.5f)]
    public void Preserves_WireVolume_Unclamped(float volume)
    {
        SoundEvent? parsed = SoundEvent.TryParse(
            Frame(SoundEvent.Opcode, 0x1u, 0x09u, volume));
        Assert.Equal(volume, parsed!.Value.Volume);
    }
}

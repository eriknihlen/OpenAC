using AcDream.Core.Net.Cryptography;

namespace AcDream.Core.Net.Tests.Cryptography;

public class IsaacRandomTests
{
    private static readonly uint[] GoldenVectorsSeed12345678 =
    {
        0xFDD4AFEBu, 0x398311D2u, 0xC71829A4u, 0xB19DF96Au,
        0x7ED4A1E7u, 0xB718A446u, 0x5FA77DF1u, 0x5CB793D2u,
        0x52C95BACu, 0xA8F8FD7Eu, 0x18582C04u, 0xD257F506u,
        0x4D11A3F0u, 0x919DE7A7u, 0x1D809C6Bu, 0xE97F65F8u,
    };

    [Fact]
    public void Next_Seed12345678_MatchesAceGoldenVectors()
    {
        var seed = new byte[] { 0x78, 0x56, 0x34, 0x12 };
        var isaac = new IsaacRandom(seed);

        for (int i = 0; i < GoldenVectorsSeed12345678.Length; i++)
        {
            Assert.Equal(GoldenVectorsSeed12345678[i], isaac.Next());
        }
    }

    [Fact]
    public void Next_TwoInstancesSameSeed_ProduceIdenticalSequence()
    {
        var seed = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var a = new IsaacRandom(seed);
        var b = new IsaacRandom(seed);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(a.Next(), b.Next());
        }
    }

    [Fact]
    public void Next_DifferentSeeds_ProduceDifferentFirstOutput()
    {
        var a = new IsaacRandom(new byte[] { 1, 0, 0, 0 });
        var b = new IsaacRandom(new byte[] { 2, 0, 0, 0 });
        Assert.NotEqual(a.Next(), b.Next());
    }

    [Fact]
    public void Next_512Calls_SpansTwoScrambleBatches()
    {
        // Two full scramble rounds (256 outputs each). Tests that the
        // scramble boundary at offset 0 → offset 255 doesn't emit garbage or
        // repeat values trivially.
        var isaac = new IsaacRandom(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        var outputs = new uint[512];
        for (int i = 0; i < 512; i++) outputs[i] = isaac.Next();

        var distinct = new HashSet<uint>(outputs);
        Assert.True(distinct.Count > 400,
            $"Expected >400 distinct values in 512 outputs, got {distinct.Count}");
    }

    [Fact]
    public void Ctor_ShortSeed_Throws()
    {
        Assert.Throws<ArgumentException>(() => new IsaacRandom(new byte[] { 1, 2, 3 }));
    }
}

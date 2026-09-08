using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public sealed class PlayerKillerStatusBitfieldTests
{
    [Fact]
    public void Pk_SetsPkBit_ClearsFreeAndPkLite()
    {
        uint result = PlayerKillerStatusBitfield.Apply(
            bitfield: 0x2200000u,
            pkStatus: PlayerKillerStatusBitfield.Pk);
        Assert.Equal(0x20u, result);
    }

    [Fact]
    public void PkLite_SetsPkLiteBit_ClearsPkAndFree()
    {
        uint result = PlayerKillerStatusBitfield.Apply(
            bitfield: 0x200020u,
            pkStatus: PlayerKillerStatusBitfield.PkLite);
        Assert.Equal(0x2000000u, result);
    }

    [Fact]
    public void Free_SetsFreeBit_ClearsPkAndPkLite()
    {
        uint result = PlayerKillerStatusBitfield.Apply(
            bitfield: 0x2000020u,
            pkStatus: PlayerKillerStatusBitfield.Free);
        Assert.Equal(0x200000u, result);
    }

    [Fact]
    public void OtherValue_ClearsAllThreeBits()
    {
        uint result = PlayerKillerStatusBitfield.Apply(
            bitfield: 0x2200020u,
            pkStatus: 0);
        Assert.Equal(0u, result);
    }

    [Theory]
    [InlineData(PlayerKillerStatusBitfield.Pk)]
    [InlineData(PlayerKillerStatusBitfield.PkLite)]
    [InlineData(PlayerKillerStatusBitfield.Free)]
    [InlineData(0)]
    public void NeverTouchesUnrelatedBits(int pkStatus)
    {
        const uint unrelatedBits = 0x8u | 0x100u | 0x400000u;
        uint result = PlayerKillerStatusBitfield.Apply(
            bitfield: unrelatedBits,
            pkStatus: pkStatus);
        Assert.Equal(unrelatedBits, result & unrelatedBits);
    }

    [Fact]
    public void MutualExclusivity_TransitioningPkToFreeToPkLite()
    {
        uint bitfield = 0u;
        bitfield = PlayerKillerStatusBitfield.Apply(bitfield, PlayerKillerStatusBitfield.Pk);
        Assert.Equal(0x20u, bitfield);

        bitfield = PlayerKillerStatusBitfield.Apply(bitfield, PlayerKillerStatusBitfield.Free);
        Assert.Equal(0x200000u, bitfield);

        bitfield = PlayerKillerStatusBitfield.Apply(bitfield, PlayerKillerStatusBitfield.PkLite);
        Assert.Equal(0x2000000u, bitfield);

        bitfield = PlayerKillerStatusBitfield.Apply(bitfield, pkStatus: -1);
        Assert.Equal(0u, bitfield);
    }
}

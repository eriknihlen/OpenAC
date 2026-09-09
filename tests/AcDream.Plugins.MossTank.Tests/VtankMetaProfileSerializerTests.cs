using System.Globalization;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankMetaProfileSerializerTests
{
    [Fact]
    public void LoadsKnownTypedCondActRecord()
    {
        const string source = "1\r\nCondAct\r\n5\r\nCType\r\nAType\r\n"
            + "CData\r\nAData\r\nState\r\nn\r\nn\r\nn\r\nn\r\nn\r\n1\r\n"
            + "i\r\n1\r\ni\r\n2\r\ni\r\n0\r\ns\r\n/say ready\r\n"
            + "s\r\nDefault\r\n";

        Assert.True(VtankMetaProfileSerializer.TryLoad(
            source, out MetaProfile profile, out string error), error);

        MetaRule rule = Assert.Single(profile.Rules);
        Assert.Equal(MetaConditionKind.Always, rule.Condition.Kind);
        Assert.Equal(MetaActionKind.ChatCommand, rule.Action.Kind);
        Assert.Equal("/say ready", rule.Action.Text);
        Assert.Equal("Default", rule.State);
    }

    [Fact]
    public void SignedHighBitLandblockIdRoundTripsExactly()
    {
        int highBitValue = unchecked((int)0x8B370000u);
        Assert.True(highBitValue < 0, "0x8B370000 must decode as negative once cast to Int32.");
        string source = "1\r\nCondAct\r\n5\r\nCType\r\nAType\r\n"
            + "CData\r\nAData\r\nState\r\nn\r\nn\r\nn\r\nn\r\nn\r\n1\r\n"
            + "i\r\n17\r\ni\r\n0\r\n"
            + "i\r\n" + highBitValue.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + "i\r\n0\r\n"
            + "s\r\nDefault\r\n";

        Assert.True(VtankMetaProfileSerializer.TryLoad(
            source, out MetaProfile profile, out string error), error);

        MetaRule rule = Assert.Single(profile.Rules);
        Assert.Equal(MetaConditionKind.LandblockEquals, rule.Condition.Kind);
        Assert.Equal((double)highBitValue, rule.Condition.Number);
    }
}

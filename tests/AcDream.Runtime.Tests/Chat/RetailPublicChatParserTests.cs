using AcDream.Runtime.Chat;

namespace AcDream.Runtime.Tests.Chat;

public sealed class RetailPublicChatParserTests
{
    [Fact]
    public void InvalidAndUnmatchedTokensRemainLiteral()
    {
        string text = RetailPublicChatParser.ExtractPoses(
            "*unknown* and *unfinished",
            _ => null,
            _ => throw new Xunit.Sdk.XunitException("must not execute"));

        Assert.Equal("*unknown* and *unfinished", text);
    }

    [Fact]
    public void MultipleValidStarAndAngleTokensAreRemovedInOrder()
    {
        var motions = new List<uint>();
        string text = RetailPublicChatParser.ExtractPoses(
            "a *one* b <two> c",
            command => command switch
            {
                "one" => new RetailChatPose(1u, "", ""),
                "two" => new RetailChatPose(2u, "", ""),
                _ => null,
            },
            pose => motions.Add(pose.MotionCommand));

        Assert.Equal("a  b  c", text);
        Assert.Equal([1u, 2u], motions);
    }
}

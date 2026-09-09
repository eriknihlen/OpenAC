using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class WeenieErrorMessagesTests
{

    [Fact]
    public void Format_YouHaveEnteredChannel_SubstitutesParam()
    {
        Assert.Equal(
            "You have entered the General channel.",
            WeenieErrorMessages.Format(0x051B, "General"));
    }

    [Fact]
    public void Format_YouHaveEnteredChannel_WorksForEachChannelName()
    {
        Assert.Equal("You have entered the Trade channel.",   WeenieErrorMessages.Format(0x051B, "Trade"));
        Assert.Equal("You have entered the LFG channel.",     WeenieErrorMessages.Format(0x051B, "LFG"));
        Assert.Equal("You have entered the Roleplay channel.",WeenieErrorMessages.Format(0x051B, "Roleplay"));
    }

    [Fact]
    public void Format_YouHaveLeftChannel_SubstitutesParam()
    {
        Assert.Equal(
            "You have left the General channel.",
            WeenieErrorMessages.Format(0x051C, "General"));
    }


    [Fact]
    public void Format_0x051D_ReturnsNull_NoRetailCaseExists()
    {
        Assert.Null(WeenieErrorMessages.Format(0x051D, param: null));
    }


    [Fact]
    public void Format_CharacterNotAvailable_NoParam()
    {
        Assert.Equal(
            "That person is not available now.",
            WeenieErrorMessages.Format(0x052B, param: null));
    }

    [Fact]
    public void Format_TradeComplete()
    {
        Assert.Equal("Trade Complete!", WeenieErrorMessages.Format(0x0529, null));
    }

    [Fact]
    public void Format_ThatIsNotAValidCommand()
    {
        Assert.Equal(
            "That is not a valid command.",
            WeenieErrorMessages.Format(0x0026, null));
    }

    [Fact]
    public void Format_YouAreNotInAllegiance()
    {
        Assert.Equal(
            "You are not in an allegiance!",
            WeenieErrorMessages.Format(0x0414, null));
    }

    [Fact]
    public void Format_YouDoNotBelongToAFellowship()
    {
        Assert.Equal(
            "You do not belong to a Fellowship.",
            WeenieErrorMessages.Format(0x050F, null));
    }

    [Theory]
    [InlineData(0x0036u, "Action cancelled!")]
    [InlineData(0x003Du, "You charged too far!")]
    [InlineData(0x004Au, "Ack! You killed yourself!")]
    [InlineData(0x0550u, "Out of Range!")]
    public void Format_CombatMovementErrors(uint code, string expected)
        => Assert.Equal(expected, WeenieErrorMessages.Format(code, null));


    [Fact]
    public void Format_YouAreNonPKAgain_ExactRetailText()
    {
        Assert.Equal(
            "You are enveloped in a feeling of warmth as you are brought back into the protection of the Light. You are once again a Non-Player Killer.",
            WeenieErrorMessages.Format(0x0504, null));
    }

    [Fact]
    public void Format_YoureTooCloseToYourSanctuary()
    {
        Assert.Equal(
            "You're too close to your sanctuary!",
            WeenieErrorMessages.Format(0x0505, null));
    }

    [Fact]
    public void Format_CannotChangePKStatusWhileRecovering()
    {
        Assert.Equal(
            "You cannot modify your player killer status while you are recovering from a PK death.",
            WeenieErrorMessages.Format(0x04EC, null));
    }

    [Fact]
    public void Format_AdvocatesCannotChangePKStatus()
    {
        Assert.Equal(
            "Advocates may not change their player killer status!",
            WeenieErrorMessages.Format(0x04ED, null));
    }

    [Fact]
    public void Format_LevelTooLowToChangePKStatus_NowResolvedByCH2()
    {
        Assert.Equal(
            "Your level is too low to change your player killer status with this object.",
            WeenieErrorMessages.Format(0x04EE, null));
    }


    [Fact]
    public void Format_UnknownCode_NoParam_ReturnsNull()
    {
        Assert.Null(WeenieErrorMessages.Format(0xABCD, null));
    }

    [Fact]
    public void Format_UnknownCode_WithParam_ReturnsNull()
    {
        Assert.Null(WeenieErrorMessages.Format(0xDEAD, "Mana Stone"));
    }

    [Fact]
    public void Format_UnknownCode_EmptyParam_ReturnsNull()
    {
        Assert.Null(WeenieErrorMessages.Format(0xCAFE, ""));
    }

    // ── parameterised templates with non-trivial params ──────────────

    [Fact]
    public void Format_HearListAdded_SubstitutesParam()
    {
        Assert.Equal(
            "Caith has been added to the list of people you can hear.",
            WeenieErrorMessages.Format(0x0521, "Caith"));
    }

    [Fact]
    public void Format_0x004F_ResolvesToRetailText()
    {
        Assert.Equal(
            "You fail to affect Drudge because $s cannot be harmed!",
            WeenieErrorMessages.Format(0x004F, "Drudge"));
    }

    [Fact]
    public void Format_HealingTargetAlreadyFull_SubstitutesParam()
    {
        Assert.Equal(
            "+Acdream is already at full health!",
            WeenieErrorMessages.Format(0x04FF, "+Acdream"));
    }


    [Fact]
    public void Resolve_FullTable_HasExactly344Rows()
    {
        int count = 0;
        for (uint id = 0; id <= 0x600u; id++)
        {
            var (text, _) = WeenieErrorMessages.Resolve(id, null);
            if (text is not null)
                count++;
        }
        Assert.Equal(344, count);
    }

    [Fact]
    public void Resolve_0x4F8_NowResolvesForReal()
    {
        var (text, type) = WeenieErrorMessages.Resolve(0x4F8, "Someone");
        Assert.Equal(
            "Someone fails to affect you because you are not the same sort of player killer as Someone!",
            text);
        Assert.Equal(RetailLogTextType.Magic, type);
    }


    [Theory]
    [InlineData(0x04Fu, "You fail to affect %s because $s cannot be harmed!", RetailLogTextType.Magic)]
    [InlineData(0x3EEu, "The container is closed!", RetailLogTextType.ClientLocal)]
    [InlineData(0x408u, "Your spell cannot be cast inside", RetailLogTextType.ClientLocal)]
    [InlineData(0x48Au, "You must be a monarch to purchase this dwelling.", RetailLogTextType.Default)]
    [InlineData(0x4E8u, "The %s cannot be used while on a hook and only the owner may open the hook.", RetailLogTextType.Default)]
    [InlineData(0x051u, "You fail to affect %s because you are not a player killer!", RetailLogTextType.Magic)]
    [InlineData(0x053u, "You fail to affect %s because you are not the same sort of player killer as %s!", RetailLogTextType.Magic)]
    [InlineData(0x054u, "You fail to affect %s because you are acting across a house boundary!", RetailLogTextType.Magic)]
    [InlineData(0x466u, "You must purchase Asheron's Call: Dark Majesty to interact with that portal.", RetailLogTextType.Magic)]
    [InlineData(0x4A3u, "You must have linked with a portal in order to recall to it!", RetailLogTextType.Magic)]
    [InlineData(0x4B5u, "You must specify a character to query.", RetailLogTextType.ClientLocal)]
    [InlineData(0x4E0u, "You are currently wielding items which require a certain level of skill. Your attributes cannot be transferred while you are wielding these items. Please remove these items and try again.", RetailLogTextType.Default)]
    [InlineData(0x4F7u, "%s fails to affect you because you are not a player killer!", RetailLogTextType.Magic)]
    [InlineData(0x544u, "An unspecified error occurred while attempting to remove %s as an allegiance officer.", RetailLogTextType.Default)]
    [InlineData(0x54Eu, "The hook does not contain a usable item. You cannot open the hook because you do not own the house to which it belongs.", RetailLogTextType.Default)]
    [InlineData(0x552u, "You must purchase Asheron's Call -- Throne of Destiny to use this function.", RetailLogTextType.ClientLocal)]
    [InlineData(0x553u, "You must purchase Asheron's Call -- Throne of Destiny to use this item.", RetailLogTextType.ClientLocal)]
    [InlineData(0x554u, "You must purchase Asheron's Call -- Throne of Destiny to use this portal.", RetailLogTextType.ClientLocal)]
    [InlineData(0x555u, "You must purchase Asheron's Call -- Throne of Destiny to access this quest.", RetailLogTextType.ClientLocal)]
    [InlineData(0x57Fu, "Your allegiance chat privileges have been temporarily removed by %s. Until they are restored, you may not view or speak in the allegiance chat channel.", RetailLogTextType.Default)]
    [InlineData(0x582u, "Your allegiance chat privileges have been restored by %s.", RetailLogTextType.Default)]
    [InlineData(0x4E9u, "The %s cannot be used while on a hook, use the '@house hooks on' command to make the hook openable.", RetailLogTextType.Default)]
    [InlineData(0x518u, "This fellowship is locked; %s cannot be recruited into the fellowship.", RetailLogTextType.Default)]
    public void Resolve_Blocker2CorrectedRows_MatchTheSweptBinaryLiteral(
        uint id, string expectedTemplate, RetailLogTextType expectedType)
    {
        var (text, type) = WeenieErrorMessages.Resolve(id, param: null);
        Assert.Equal(expectedTemplate, text);
        Assert.Equal(expectedType, type);
    }


    [Theory]
    [InlineData(0x017u, "You failed to go to non-combat mode.", RetailLogTextType.ClientLocal)]
    [InlineData(0x02Au, "You are too encumbered to carry that!", RetailLogTextType.ClientLocal)]
    [InlineData(0x04EBu, "You can't do that while in the air!", RetailLogTextType.ClientLocal)]
    [InlineData(0x550u, "Out of Range!", RetailLogTextType.ClientLocal)]
    [InlineData(0x402u, "Your spell fizzled.", RetailLogTextType.Magic)]
    [InlineData(0x49Bu, "You fail to link with the lifestone!", RetailLogTextType.Magic)]
    [InlineData(0x593u, "Olthoi characters can only use Lifestone and PK Arena recalls!", RetailLogTextType.Magic)]
    [InlineData(0x4A, "Ack! You killed yourself!", RetailLogTextType.Default)]
    [InlineData(0x50Cu, "%s is now a closed fellowship.", RetailLogTextType.Default)]
    [InlineData(0x55Fu, "Only Player Killer characters may use this command!", RetailLogTextType.Default)]
    public void Resolve_SpotPins_TextAndTypeMatchAppendixA(uint id, string expectedTemplate, RetailLogTextType expectedType)
    {
        var (text, type) = WeenieErrorMessages.Resolve(id, param: null);
        Assert.Equal(expectedTemplate, text);
        Assert.Equal(expectedType, type);
    }

    [Fact]
    public void Resolve_JumpFamily_SharesClientTextRefusalsConstantsVerbatim()
    {
        Assert.Equal(ClientTextRefusals.CantJumpInAir, WeenieErrorMessages.Resolve(0x024u, null).Text);
        Assert.Equal(ClientTextRefusals.CantJumpPosition, WeenieErrorMessages.Resolve(0x048u, null).Text);
        Assert.Equal(ClientTextRefusals.CantJumpLoad, WeenieErrorMessages.Resolve(0x049u, null).Text);
        Assert.Equal(RetailLogTextType.ClientLocal, WeenieErrorMessages.Resolve(0x024u, null).Type);
        Assert.Equal(RetailLogTextType.ClientLocal, WeenieErrorMessages.Resolve(0x048u, null).Type);
        Assert.Equal(RetailLogTextType.ClientLocal, WeenieErrorMessages.Resolve(0x049u, null).Type);
    }

    [Fact]
    public void Resolve_0x4F4_PreservesRetailDollarSTypo()
    {
        var (text, type) = WeenieErrorMessages.Resolve(0x4F4u, "A drudge");
        Assert.Equal("A drudge fails to affect you because $s cannot affect anyone!", text);
        Assert.Equal(RetailLogTextType.Magic, type);
    }

    [Theory]
    [InlineData(0x0417u)]
    [InlineData(0x0418u)]
    [InlineData(0x0419u)]
    [InlineData(0x041Au)]
    [InlineData(0x041Bu)]
    [InlineData(0x041Cu)]
    [InlineData(0x04DBu)]
    [InlineData(0x04DCu)]
    public void Resolve_FellowshipIdsAbsentFromHandleFailureEvent_ReturnsNoText(uint code)
    {
        var (text, _) = WeenieErrorMessages.Resolve(code, param: null);
        Assert.Null(text);
    }
}

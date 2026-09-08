using AcDream.Core.Ui;

namespace AcDream.Core.Tests.Ui;

public sealed class RetailMessagesTests
{
    [Fact]
    public void CannotBeUsed_FormatsRetailLiteral()
    {
        Assert.Equal("The Holtburg cannot be used",
                     RetailMessages.CannotBeUsed("Holtburg"));
    }

    [Fact]
    public void CantBePickedUp_FormatsRetailLiteral()
    {
        Assert.Equal("The Holtburg can't be picked up!",
                     RetailMessages.CantBePickedUp("Holtburg"));
    }

    [Fact]
    public void CannotPickUpCreatures_IsExactRetailLiteral()
    {
        Assert.Equal("You cannot pick up creatures!",
                     RetailMessages.CannotPickUpCreatures);
    }

    [Fact]
    public void CannotBeUsedWith_FormatsRetailLiteral()
    {
        Assert.Equal("Cannot be used with Lockpick",
                     RetailMessages.CannotBeUsedWith("Lockpick"));
    }

    [Fact]
    public void CannotBePickedUp_FormatsFormalRetailVariant()
    {
        Assert.Equal("The Holtburg cannot be picked up!",
                     RetailMessages.CannotBePickedUp("Holtburg"));
    }

    [Fact]
    public void CannotBeUsedWhileOnHook_HooksOff_PreservesTrailingNewline()
    {
        string actual = RetailMessages.CannotBeUsedWhileOnHook_HooksOff("Chest");
        Assert.Equal(
            "The Chest cannot be used while on a hook, use the '@house hooks on' command to make the hook openable.\n",
            actual);
        Assert.EndsWith("\n", actual);
    }

    [Fact]
    public void CannotBeUsedWhileOnHook_NotOwner_PreservesTrailingNewline()
    {
        string actual = RetailMessages.CannotBeUsedWhileOnHook_NotOwner("Chest");
        Assert.Equal(
            "The Chest cannot be used while on a hook and only the owner may open the hook.\n",
            actual);
        Assert.EndsWith("\n", actual);
    }
}

using AcDream.Core.Chat;
using AcDream.Core.Items;

namespace AcDream.Core.Tests.Chat;

public sealed class InventoryFailureMessagesTests
{
    [Theory]
    [InlineData(
        InventoryRequestKind.DropToWorld, "Bloodstone Chunk", 0x426u,
        "The Bloodstone Chunk can't be dropped")]
    [InlineData(
        InventoryRequestKind.Give, "Sword", 0u,
        "The Sword can't be given")]
    [InlineData(
        InventoryRequestKind.Pickup, "Sword", 0x2Au,
        "The Sword can't be picked up - you are too encumbered")]
    [InlineData(
        InventoryRequestKind.PutInContainer, "Sword", 0x3EEu,
        "The Sword can't be put in the container - the container is closed")]
    [InlineData(
        InventoryRequestKind.Merge, "Arrows", 0x1Du,
        "The Arrows can't be merged - you're too busy")]
    [InlineData(
        InventoryRequestKind.SplitToWorld, "Arrows", 0x38u,
        "The Arrows can't be split - unable to move to object")]
    [InlineData(
        InventoryRequestKind.SplitToContainer, "Arrows", 0x36u,
        "The Arrows can't be split - action cancelled")]
    [InlineData(
        InventoryRequestKind.Move, "Sword", 0u,
        "The Sword can't be moved")]
    [InlineData(
        InventoryRequestKind.Wield, "Sword", 0x1Du,
        "The Sword can't be wielded - you're too busy")]
    public void ComposeMatchesServerSaysAttemptFailed(
        InventoryRequestKind kind,
        string name,
        uint error,
        string expected)
        => Assert.Equal(expected, InventoryFailureMessages.Compose(kind, name, error));

    [Theory]
    [InlineData(0x1Eu)]
    [InlineData(0x2Bu)]
    [InlineData(0x3EFu)]
    [InlineData(0x43Eu)]
    [InlineData(0x4CEu)]
    [InlineData(0x4CFu)]
    [InlineData(0x46Au)]
    public void ExclusionSetSuppressesGenericFailureText(uint error)
        => Assert.True(InventoryFailureMessages.SuppressesGenericFailureText(error));

    [Theory]
    [InlineData(0x426u)]
    [InlineData(0x1Du)]
    [InlineData(0u)]
    public void OtherErrorsDoNotSuppressGenericFailureText(uint error)
        => Assert.False(InventoryFailureMessages.SuppressesGenericFailureText(error));
}

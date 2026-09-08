using AcDream.App.UI.Layout;
using AcDream.Core.Items;

namespace AcDream.App.UI;

public sealed class RetailItemConfirmationController : IDisposable
{
    internal const string PlayerKillerMessage =
        "Using this altar will make you a player killer, able to attack or be attacked by other player killers. Are you sure you want to do this?";
    internal const string NonPlayerKillerMessage =
        "Using this altar will make you a non-player killer, unable to attack or be attacked by other player killers. Are you sure you want to do this?";
    internal const string VolatileRareMessage =
        "Are you sure you want to use this rare item?";

    private readonly RetailDialogFactory _dialogs;
    private readonly ItemInteractionController _items;
    private bool _disposed;

    public RetailItemConfirmationController(
        RetailDialogFactory dialogs,
        ItemInteractionController items)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _items.PolicyActionRequested += OnPolicyActionRequested;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _items.PolicyActionRequested -= OnPolicyActionRequested;
    }

    private void OnPolicyActionRequested(ItemPolicyAction action)
    {
        string? message = action.Kind switch
        {
            ItemPolicyActionKind.ConfirmPlayerKillerSwitch => PlayerKillerMessage,
            ItemPolicyActionKind.ConfirmNonPlayerKillerSwitch => NonPlayerKillerMessage,
            ItemPolicyActionKind.ConfirmVolatileRare => VolatileRareMessage,
            _ => null,
        };
        if (message is null)
            return;

        RetailDialogData data = RetailDialogData.Confirmation(message)
            .Set(RetailDialogProperty.UsageObjectId, action.ObjectId);
        _dialogs.MakeDialog(data, OnUsageDialogDone);
    }

    private void OnUsageDialogDone(RetailDialogData data)
    {
        if (!data.GetBoolean(RetailDialogProperty.ConfirmationResult))
            return;
        uint objectId = data.GetUInt32(RetailDialogProperty.UsageObjectId);
        _items.ExecuteConfirmedUse(objectId);
    }
}

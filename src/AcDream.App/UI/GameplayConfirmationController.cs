using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;

namespace AcDream.App.UI;

public sealed class GameplayConfirmationController : IDisposable
{
    private readonly RetailDialogFactory _dialogs;
    private readonly Action<uint, uint, bool> _sendResponse;
    private readonly Func<uint, string, string?>? _composeMessage;
    private uint _dialogContext;
    private uint _serverType;
    private uint _serverContext;
    private bool _disposed;

    public GameplayConfirmationController(
        RetailDialogFactory dialogs,
        Action<uint, uint, bool> sendResponse,
        Func<uint, string, string?>? composeMessage = null)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _sendResponse = sendResponse ?? throw new ArgumentNullException(nameof(sendResponse));
        _composeMessage = composeMessage;
        _dialogs.DialogClosed += OnDialogClosed;
    }

    public uint ActiveDialogContext => _dialogContext;

    public bool HandleRequest(GameEvents.CharacterConfirmationRequest request)
    {
        _serverType = request.Type;
        _serverContext = request.ContextId;

        if (_dialogContext != 0u)
            return false;

        string message = request.Type is 2u or 3u or 5u or 6u
            ? request.Message + " Continue?"
            : _composeMessage?.Invoke(request.Type, request.Message)
                ?? request.Message;
        var data = RetailDialogData.Confirmation(message);
        _dialogContext = _dialogs.MakeDialog(data);
        return _dialogContext != 0u;
    }

    public bool HandleDone(GameEvents.CharacterConfirmationDone done)
    {
        if (_dialogContext == 0u
            || done.Type != _serverType
            || done.ContextId != _serverContext)
            return false;
        return _dialogs.CloseDialog(_dialogContext);
    }

    public void ResetSession()
    {
        _dialogContext = 0u;
        _serverType = 0u;
        _serverContext = 0u;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dialogs.DialogClosed -= OnDialogClosed;
    }

    private void OnDialogClosed(uint context, RetailDialogData data)
    {
        if (context != _dialogContext
            || data.GetUInt32(RetailDialogProperty.Type) !=
                (uint)RetailDialogType.Confirmation)
            return;

        bool accepted = data.GetBoolean(RetailDialogProperty.ConfirmationResult);
        _sendResponse(_serverType, _serverContext, accepted);
        _dialogContext = 0u;
        _serverType = 0u;
        _serverContext = 0u;
    }
}

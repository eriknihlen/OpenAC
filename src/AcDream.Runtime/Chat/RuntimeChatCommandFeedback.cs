using AcDream.Core.Chat;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Chat;

public sealed class RuntimeChatCommandFeedback : IChatCommandFeedback
{
    private readonly RuntimeCommunicationState _communication;

    public RuntimeChatCommandFeedback(RuntimeCommunicationState communication)
    {
        _communication = communication
            ?? throw new ArgumentNullException(nameof(communication));
    }

    public string? LastIncomingTellSender =>
        _communication.CommandTargets.LastIncomingTellSender;

    public string? LastOutgoingTellTarget =>
        _communication.CommandTargets.LastOutgoingTellTarget;

    public void ShowInterfaceText(string text) =>
        _communication.AddText(text, RetailLogTextType.ClientLocal);

    public void ShowSystemMessage(string text) =>
        _communication.Chat.OnSystemMessage(text, chatType: 0x00u);
}

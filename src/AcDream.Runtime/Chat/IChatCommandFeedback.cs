namespace AcDream.Runtime.Chat;

public interface IChatCommandFeedback
{
    void ShowInterfaceText(string text);

    void ShowSystemMessage(string text);

    string? LastIncomingTellSender { get; }

    string? LastOutgoingTellTarget { get; }
}

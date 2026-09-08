namespace AcDream.Runtime.Chat;

public sealed record ExecuteClientCommandCmd(ClientCommandId Command, string Arguments);

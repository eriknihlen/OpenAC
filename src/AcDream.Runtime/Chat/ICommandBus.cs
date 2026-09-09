namespace AcDream.Runtime.Chat;

public interface ICommandBus
{
    void Publish<T>(T command) where T : notnull;
}

public interface IPluginCommandBus : ICommandBus
{
    bool TryHandlePluginCommand(string commandLine);
}

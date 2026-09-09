namespace AcDream.Runtime.Chat;

public sealed class NullCommandBus : ICommandBus
{
    /// <summary>Shared singleton — the bus is stateless.</summary>
    public static readonly NullCommandBus Instance = new();

    private NullCommandBus() { }

    /// <inheritdoc />
    public void Publish<T>(T command) where T : notnull
    {
    }
}

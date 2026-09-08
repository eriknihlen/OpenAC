namespace AcDream.App.World;

public sealed class LiveEntityPartArrayEnterWorldPort
{
    private readonly Action<uint> _handleEnterWorld;

    public LiveEntityPartArrayEnterWorldPort(Action<uint> handleEnterWorld) =>
        _handleEnterWorld = handleEnterWorld
            ?? throw new ArgumentNullException(nameof(handleEnterWorld));

    public void HandleEnterWorld(uint localEntityId) =>
        _handleEnterWorld(localEntityId);
}

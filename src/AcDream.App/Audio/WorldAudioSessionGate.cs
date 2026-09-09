namespace AcDream.App.Audio;

public sealed class WorldAudioSessionGate
{
    private readonly OpenAlAudioEngine _engine;
    private readonly AmbientSoundController? _ambient;

    public WorldAudioSessionGate(
        OpenAlAudioEngine engine,
        AmbientSoundController? ambient)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ambient = ambient;
    }

    public void SuspendForSessionReset()
    {
        _engine.SuspendWorldAudio();
        _ambient?.StopAll();
    }

    public void ResumeForWorldEntry() => _engine.ResumeWorldAudio();
}

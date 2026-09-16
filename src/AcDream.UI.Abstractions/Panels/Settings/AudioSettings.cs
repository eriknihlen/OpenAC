namespace AcDream.UI.Abstractions.Panels.Settings;

public sealed record AudioSettings(
    float Master,
    float Sfx,
    float Ambient,
    int SoundFeatures = 0,
    bool SfxEnabled = true,
    bool AmbientEnabled = true,
    bool InterfaceEnabled = true,
    float InterfaceVolume = 1.0f,
    bool PlaySoundOnlyWhenActive = true)
{
    public static AudioSettings Default { get; } = new(
        Master:  1.0f,
        Sfx:     1.0f,
        Ambient: 1.0f);
}

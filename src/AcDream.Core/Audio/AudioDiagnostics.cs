using System;

namespace AcDream.Core.Audio;

public static class AudioDiagnostics
{
    public static bool ProbeWireSoundsEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_SOUND_WIRE") == "1";

    /// <summary>
    /// Temporary probe: one line whenever a sound is dropped because every
    /// voice is busy, or takes a voice from a playing one, saying what every
    /// voice is holding at that moment.
    /// </summary>
    public static bool ProbeVoicesEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_AUDIO_VOICES") == "1";
}

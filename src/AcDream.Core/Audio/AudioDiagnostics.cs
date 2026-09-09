using System;

namespace AcDream.Core.Audio;

public static class AudioDiagnostics
{
    public static bool ProbeWireSoundsEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_SOUND_WIRE") == "1";
}

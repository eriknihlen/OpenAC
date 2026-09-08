using System;
using System.Collections.Generic;

namespace AcDream.Core.Audio;




public sealed class WaveData
{
    public int      ChannelCount { get; init; }
    public int      SampleRate   { get; init; }
    public int      BitsPerSample{ get; init; }   // 8 or 16
    public byte[]   PcmBytes     { get; init; } = Array.Empty<byte>();
    public TimeSpan Duration     { get; init; }
}


public interface IAudioEngine : IDisposable
{
    float MasterVolume { get; set; }

    float SfxVolume    { get; set; }

    float AmbientVolume{ get; set; }

    void SetListener(float posX, float posY, float posZ, float headingDegrees);

    /// <summary>Play a 2D UI sound (no falloff).</summary>
    void PlayUi(SoundId id);

    /// <summary>Play a 3D sound at a world position.</summary>
    void Play3D(SoundId id, float x, float y, float z);


}

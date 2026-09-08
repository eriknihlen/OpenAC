using System;
using System.Buffers.Binary;

namespace AcDream.Core.Audio;

public static class WaveDecoder
{
    public enum WaveFormatTag : ushort
    {
        Unknown     = 0x0000,
        Pcm         = 0x0001,  // Microsoft PCM
        Adpcm       = 0x0002,  // Microsoft ADPCM (rare in AC)
        Mp3         = 0x0055,
    }

    public static WaveData? Decode(byte[] header, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(data);

        if (header.Length < 14) return null;

        var h = header.AsSpan();
        ushort fmtTag   = BinaryPrimitives.ReadUInt16LittleEndian(h);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(h.Slice(2));
        uint   sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(4));
        ushort bitsPer    = header.Length >= 16
            ? BinaryPrimitives.ReadUInt16LittleEndian(h.Slice(14))
            : (ushort)16;

        if (channels == 0 || sampleRate == 0)
            return null;

        if ((WaveFormatTag)fmtTag != WaveFormatTag.Pcm)
            return null;

        // For PCM the Data array IS the sample buffer (no framing needed).
        double duration = bitsPer > 0 && channels > 0
            ? data.Length * 8.0 / (double)(sampleRate * channels * bitsPer)
            : 0.0;

        return new WaveData
        {
            ChannelCount  = channels,
            SampleRate    = (int)sampleRate,
            BitsPerSample = bitsPer == 0 ? 16 : bitsPer,
            PcmBytes      = data,
            Duration      = TimeSpan.FromSeconds(duration),
        };
    }

    public static WaveFormatTag PeekFormat(byte[] header)
    {
        if (header is null || header.Length < 2) return WaveFormatTag.Unknown;
        ushort fmtTag = BinaryPrimitives.ReadUInt16LittleEndian(header);
        return (WaveFormatTag)fmtTag;
    }
}

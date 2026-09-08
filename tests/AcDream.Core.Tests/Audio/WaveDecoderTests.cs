using AcDream.Core.Audio;
using Xunit;

namespace AcDream.Core.Tests.Audio;

public sealed class WaveDecoderTests
{
    private static byte[] MakePcmHeader(ushort channels = 1, uint rate = 22050, ushort bits = 16)
    {
        byte[] h = new byte[18];
        h[0] = 0x01; h[1] = 0x00;                    // fmtTag PCM
        h[2] = (byte)channels; h[3] = (byte)(channels >> 8);
        h[4] = (byte)rate;     h[5] = (byte)(rate >> 8);
        h[6] = (byte)(rate >> 16); h[7] = (byte)(rate >> 24);
        uint avg = rate * channels * (uint)(bits / 8);
        h[8]  = (byte)avg;       h[9]  = (byte)(avg >> 8);
        h[10] = (byte)(avg >> 16); h[11] = (byte)(avg >> 24);
        ushort blockAlign = (ushort)(channels * (bits / 8));
        h[12] = (byte)blockAlign; h[13] = (byte)(blockAlign >> 8);
        h[14] = (byte)bits;       h[15] = (byte)(bits >> 8);
        return h;
    }

    [Fact]
    public void Decode_PcmHeader_ReturnsWaveData()
    {
        byte[] header = MakePcmHeader(channels: 1, rate: 22050, bits: 16);
        byte[] data = new byte[22050 * 2];  // 1 second of 16-bit mono

        var decoded = WaveDecoder.Decode(header, data);

        Assert.NotNull(decoded);
        Assert.Equal(1, decoded!.ChannelCount);
        Assert.Equal(22050, decoded.SampleRate);
        Assert.Equal(16, decoded.BitsPerSample);
        Assert.Same(data, decoded.PcmBytes);
        // Duration should be ~1 second.
        Assert.InRange(decoded.Duration.TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public void Decode_StereoPcm_Works()
    {
        byte[] header = MakePcmHeader(channels: 2, rate: 44100, bits: 16);
        byte[] data = new byte[44100 * 2 * 2];   // 1 second stereo
        var decoded = WaveDecoder.Decode(header, data);

        Assert.NotNull(decoded);
        Assert.Equal(2, decoded!.ChannelCount);
        Assert.Equal(44100, decoded.SampleRate);
    }

    [Fact]
    public void Decode_Mp3Header_ReturnsNull()
    {
        // wFormatTag = 0x0055 (MPEGLAYER3) — not yet supported.
        byte[] header = new byte[30];
        header[0] = 0x55; header[1] = 0x00;
        header[2] = 0x01; header[4] = 0x44; header[5] = 0xAC; // stereo 44.1kHz
        byte[] data = new byte[1024];

        var decoded = WaveDecoder.Decode(header, data);
        Assert.Null(decoded);
    }

    [Fact]
    public void Decode_AdpcmHeader_ReturnsNull()
    {
        byte[] header = new byte[20];
        header[0] = 0x02; header[1] = 0x00;  // ADPCM
        byte[] data = new byte[1024];

        var decoded = WaveDecoder.Decode(header, data);
        Assert.Null(decoded);
    }

    [Fact]
    public void Decode_TruncatedHeader_ReturnsNull()
    {
        byte[] header = new byte[5];
        byte[] data = new byte[100];
        Assert.Null(WaveDecoder.Decode(header, data));
    }

    [Fact]
    public void PeekFormat_ReturnsCorrectTag()
    {
        Assert.Equal(WaveDecoder.WaveFormatTag.Pcm, WaveDecoder.PeekFormat(MakePcmHeader()));

        byte[] mp3Header = new byte[4] { 0x55, 0x00, 0x01, 0x00 };
        Assert.Equal(WaveDecoder.WaveFormatTag.Mp3, WaveDecoder.PeekFormat(mp3Header));

        Assert.Equal(WaveDecoder.WaveFormatTag.Unknown, WaveDecoder.PeekFormat(null!));
    }
}

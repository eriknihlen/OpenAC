namespace AcDream.Core.Textures;

public sealed record DecodedTexture(byte[] Rgba8, int Width, int Height)
{
    /// <summary>1x1 magenta fallback for missing/unsupported textures.</summary>
    public static readonly DecodedTexture Magenta = new(
        Rgba8: [0xFF, 0x00, 0xFF, 0xFF],
        Width: 1,
        Height: 1);
}

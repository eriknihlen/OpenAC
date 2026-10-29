using System.Diagnostics.CodeAnalysis;
using AcDream.Core.Textures;
using StbImageSharp;

namespace AcDream.Core.Plugins;

/// <summary>
/// A PNG or JPEG image a plugin ships in its install folder and names from markup, such as
/// <c>&lt;icon file="icons/sword.png"/&gt;</c>. Any size up to <see cref="MaximumDimension"/>
/// on a side; the file must be at most <see cref="MaximumBytes"/>. Never throws: every
/// refusal is a reason, and the caller draws nothing in its place.
/// </summary>
public static class PluginImageFile
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumDimension = 512;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg"];

    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against the plugin folder: relative, ending in
    /// <c>.png</c>, <c>.jpg</c> or <c>.jpeg</c>, and inside the folder after links are followed.
    /// </summary>
    public static bool TryResolvePath(
        string pluginDirectory, string relativePath,
        [NotNullWhen(true)] out string? fullPath, [NotNullWhen(false)] out string? reason) =>
        PluginFilePath.TryResolve(
            pluginDirectory, relativePath, Extensions, "a .png or .jpg file", out fullPath, out reason);

    /// <summary>Reads and decodes the file at <paramref name="fullPath"/> to RGBA.</summary>
    public static bool TryLoad(
        string fullPath, [NotNullWhen(true)] out DecodedTexture? texture, [NotNullWhen(false)] out string? reason)
    {
        texture = null;
        // One handle, a regular file only, and never more than one byte past
        // the limit: a pipe, device or folder named like an image is refused
        // without waiting on it, and a file that grows is refused, not read whole.
        if (!RegularFileReader.TryRead(fullPath, MaximumBytes + 1, out byte[]? bytes, out reason))
            return false;

        if (bytes.Length > MaximumBytes)
        {
            reason = "is larger than 1 MiB";
            return false;
        }
        if (!bytes.AsSpan().StartsWith(PngSignature) && !bytes.AsSpan().StartsWith(JpegSignature))
        {
            reason = "is not a PNG or JPEG file";
            return false;
        }

        try
        {
            // The header is read on its own first: a decode allocates the whole image, so an
            // oversized one is refused on its declared size, before that allocation.
            using (var header = new MemoryStream(bytes, writable: false))
            {
                if (ImageInfo.FromStream(header) is { } info && !WithinMaximumDimension(info.Width, info.Height))
                {
                    reason = Oversized;
                    return false;
                }
            }

            ImageResult decoded = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            if (decoded.Width <= 0 || decoded.Height <= 0 || decoded.Data is null)
            {
                reason = "could not be decoded: it has no pixels";
                return false;
            }
            if (!WithinMaximumDimension(decoded.Width, decoded.Height))
            {
                reason = Oversized;
                return false;
            }

            texture = new DecodedTexture(decoded.Data, decoded.Width, decoded.Height);
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = "could not be decoded: " + ex.Message;
            return false;
        }
    }

    private static string Oversized => $"is wider or taller than {MaximumDimension} pixels";

    private static bool WithinMaximumDimension(int width, int height) =>
        width <= MaximumDimension && height <= MaximumDimension;
}

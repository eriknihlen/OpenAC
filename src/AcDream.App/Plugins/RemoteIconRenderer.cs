using AcDream.Content;
using AcDream.Core.Textures;
using AcDream.Plugin.Abstractions;
using DatReaderWriter.DBObjs;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AcDream.App.Plugins;

/// <summary>
/// Turns an item icon (a render surface in the game's data files) into a
/// PNG for DrakBot Remote's phone inventory. Called on the plugin tick, so
/// the data files are read from the thread that owns them.
/// </summary>
internal static class RemoteIconRenderer
{
    public static byte[]? RenderPng(IDatReaderWriter? dats, uint iconId)
    {
        if (dats is null)
            return null;
        uint id = PluginIcons.Normalize(iconId);
        if (id == 0u)
            return null;
        if (!dats.Portal.TryGet<RenderSurface>(id, out RenderSurface? surface)
            && !dats.HighRes.TryGet<RenderSurface>(id, out surface))
        {
            return null;
        }
        DecodedTexture decoded = SurfaceDecoder.DecodeRenderSurface(surface, palette: null);
        if (ReferenceEquals(decoded, DecodedTexture.Magenta) || decoded.Width <= 0 || decoded.Height <= 0)
            return null;
        using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(decoded.Rgba8, decoded.Width, decoded.Height);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        return png.ToArray();
    }
}

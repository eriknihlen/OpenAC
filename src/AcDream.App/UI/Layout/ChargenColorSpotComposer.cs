using System.Collections.Generic;
using AcDream.App.Rendering;
using AcDream.Content;
using AcDream.Core.CharGen;
using AcDream.Core.Textures;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.App.UI.Layout;

internal interface IChargenSwatchTextureSource
{
    uint BlankSpotTexture { get; }

    uint GradDiskTexture { get; }

    uint GradPlugTexture { get; }

    uint GetActiveSpotTexture(ChargenSwatchRgb rgb);
}

internal sealed class ChargenColorSpotComposer : IChargenSwatchTextureSource
{
    private const uint SpotEnumId = 0x1000000Du;
    private const uint BlankEnumId = 0x1000000Fu;
    private const uint GradDiskEnumId = 0x1000000Eu;
    private const uint GradPlugEnumId = 0x10000010u;
    private const uint EnumCategory = 7u;

    private readonly IDatReaderWriter _dats;
    private readonly TextureCache _cache;

    private DecodedTexture? _spotTemplate;
    private bool _spotResolveTried;
    private readonly Dictionary<(byte R, byte G, byte B), uint> _bakedSpotByColor = new();

    private uint _blankTexture;
    private bool _blankResolveTried;
    private uint _gradDiskTexture;
    private bool _gradDiskResolveTried;
    private uint _gradPlugTexture;
    private bool _gradPlugResolveTried;

    public ChargenColorSpotComposer(IDatReaderWriter dats, TextureCache cache)
    {
        _dats = dats;
        _cache = cache;
    }

    public uint BlankSpotTexture
    {
        get
        {
            if (!_blankResolveTried)
            {
                _blankResolveTried = true;
                if (TryDecode(BlankEnumId, out DecodedTexture decoded))
                    _blankTexture = _cache.UploadRgba8(decoded.Rgba8, decoded.Width, decoded.Height, nearest: true);
            }
            return _blankTexture;
        }
    }

    public uint GradDiskTexture
    {
        get
        {
            if (!_gradDiskResolveTried)
            {
                _gradDiskResolveTried = true;
                if (TryDecode(GradDiskEnumId, out DecodedTexture decoded))
                    _gradDiskTexture = _cache.UploadRgba8(decoded.Rgba8, decoded.Width, decoded.Height, nearest: true);
            }
            return _gradDiskTexture;
        }
    }

    public uint GradPlugTexture
    {
        get
        {
            if (!_gradPlugResolveTried)
            {
                _gradPlugResolveTried = true;
                if (TryDecode(GradPlugEnumId, out DecodedTexture decoded))
                    _gradPlugTexture = _cache.UploadRgba8(decoded.Rgba8, decoded.Width, decoded.Height, nearest: true);
            }
            return _gradPlugTexture;
        }
    }

    public uint GetActiveSpotTexture(ChargenSwatchRgb rgb)
    {
        if (!_spotResolveTried)
        {
            _spotResolveTried = true;
            if (TryDecode(SpotEnumId, out DecodedTexture decoded))
                _spotTemplate = decoded;
        }
        if (_spotTemplate is not { } template)
            return 0u;

        var key = (rgb.R, rgb.G, rgb.B);
        if (_bakedSpotByColor.TryGetValue(key, out uint cached))
            return cached;

        byte[] baked = ReplaceExactBlackWithColor(template.Rgba8, rgb);
        uint texture = _cache.UploadRgba8(baked, template.Width, template.Height, nearest: true);
        _bakedSpotByColor[key] = texture;
        return texture;
    }

    internal static byte[] ReplaceExactBlackWithColor(byte[] rgba, ChargenSwatchRgb rgb)
    {
        byte[] baked = (byte[])rgba.Clone();
        for (int i = 0; i + 3 < baked.Length; i += 4)
        {
            if (baked[i] != 0 || baked[i + 1] != 0 || baked[i + 2] != 0 || baked[i + 3] != 255)
                continue;
            baked[i] = rgb.R;
            baked[i + 1] = rgb.G;
            baked[i + 2] = rgb.B;
            baked[i + 3] = 255;
        }
        return baked;
    }

    private bool TryDecode(uint enumId, out DecodedTexture decoded)
    {
        decoded = null!;
        uint did = RetailDataIdResolver.Resolve(_dats, enumId, EnumCategory);
        if (did == 0) return false;
        if (!_dats.TryGet<RenderSurface>(did, out var rs) || rs is null) return false;
        decoded = SurfaceDecoder.DecodeRenderSurface(rs);
        return true;
    }
}

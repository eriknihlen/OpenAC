using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.Items;
using AcDream.Core.Textures;
using AcDream.Core.Spells;
using DatReaderWriter;
using AcDream.Content;
using DatReaderWriter.DBObjs;

namespace AcDream.App.UI;

public sealed class IconComposer
{
    private readonly IDatReaderWriter _dats;
    private readonly TextureCache _cache;
    private readonly Dictionary<(uint, uint, uint, uint, uint), uint> _byTuple = new();
    private readonly Dictionary<(uint, uint, uint), ComposedIcon> _dragByTuple = new();
    private readonly Dictionary<uint, uint> _spellIcons = new();
    private readonly Dictionary<uint, uint> _componentIcons = new();

    private sealed record ComposedIcon(byte[] Rgba, int Width, int Height, uint Texture);

    private EnumIDMap? _underlaySubMap;
    private bool _underlayResolveTried;
    private readonly Dictionary<uint, uint> _underlayDidByIndex = new();

    private EnumIDMap? _effectSubMap;
    private bool _effectResolveTried;
    private readonly Dictionary<uint, uint> _effectDidByIndex = new();
    private readonly Dictionary<uint, DecodedTexture> _effectTileByDid = new();

    public IconComposer(IDatReaderWriter dats, TextureCache cache)
    {
        _dats = dats;
        _cache = cache;
    }

    internal uint ResolveUnderlayDid(ItemType itemType)
    {
        uint raw = (uint)itemType;
        int lsb = raw == 0 ? -1 : BitOperations.TrailingZeroCount(raw);
        uint index = lsb < 0 ? 0x21u : (uint)(lsb + 1);
        if (_underlayDidByIndex.TryGetValue(index, out var cached)) return cached;
        EnsureUnderlaySubMap();
        uint did = 0;
        if (_underlaySubMap is { } sub && sub.ClientEnumToID.TryGetValue(index, out var d)) did = d;
        _underlayDidByIndex[index] = did;
        return did;
    }

    private void EnsureUnderlaySubMap()
    {
        if (_underlayResolveTried) return;
        _underlayResolveTried = true;
        uint masterDid = (uint)_dats.Portal.Db.Header.MasterMapId;
        if (masterDid == 0) return;
        if (!_dats.Portal.TryGet<EnumIDMap>(masterDid, out var master)) return;
        if (!master.ClientEnumToID.TryGetValue(0x10000004u, out var subDid)) return;
        if (_dats.Portal.TryGet<EnumIDMap>(subDid, out var sub)) _underlaySubMap = sub;
    }

    internal uint ResolveEffectDid(uint effects)
    {
        int lsb = effects == 0 ? -1 : BitOperations.TrailingZeroCount(effects);
        uint index = (uint)(lsb + 1);
        if (_effectDidByIndex.TryGetValue(index, out var cached)) return cached;
        EnsureEffectSubMap();
        uint did = 0;
        if (_effectSubMap is { } sub && sub.ClientEnumToID.TryGetValue(index, out var d)) did = d;
        if (did == 0 && _effectSubMap is { } sub2 && sub2.ClientEnumToID.TryGetValue(0x21u, out var fb))
            did = fb;
        _effectDidByIndex[index] = did;
        return did;
    }

    private void EnsureEffectSubMap()
    {
        if (_effectResolveTried) return;
        _effectResolveTried = true;
        uint masterDid = (uint)_dats.Portal.Db.Header.MasterMapId;
        if (masterDid == 0) return;
        if (!_dats.Portal.TryGet<EnumIDMap>(masterDid, out var master)) return;
        if (!master.ClientEnumToID.TryGetValue(0x10000005u, out var subDid)) return;
        if (_dats.Portal.TryGet<EnumIDMap>(subDid, out var sub)) _effectSubMap = sub;
    }

    internal static void ReplaceWhiteFromSurface(byte[] dst, int dw, int dh, byte[] src, int sw, int sh)
    {
        for (int y = 0; y < dh; y++)
        for (int x = 0; x < dw; x++)
        {
            int di = (y * dw + x) * 4;
            if (dst[di] == 255 && dst[di + 1] == 255 && dst[di + 2] == 255 && dst[di + 3] == 255
                && x < sw && y < sh)
            {
                int si = (y * sw + x) * 4;
                dst[di]     = src[si];     dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2]; dst[di + 3] = src[si + 3];
            }
        }
    }

    internal bool TryGetEffectTile(uint effects, out DecodedTexture tile)
    {
        tile = null!;
        uint did = ResolveEffectDid(effects);
        if (did == 0) return false;
        if (_effectTileByDid.TryGetValue(did, out var cached)) { tile = cached; return true; }
        if (!TryDecode(did, out var d)) return false;
        _effectTileByDid[did] = d;
        tile = d;
        return true;
    }

    private bool TryDecode(uint renderSurfaceId, out DecodedTexture decoded)
    {
        decoded = null!;
        if (renderSurfaceId == 0) return false;
        if (!_dats.Portal.TryGet<RenderSurface>(renderSurfaceId, out var rs) &&
            !_dats.HighRes.TryGet<RenderSurface>(renderSurfaceId, out rs))
            return false;
        decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette: null);
        return true;
    }

    public static (byte[] rgba, int w, int h) Compose(IReadOnlyList<(byte[] rgba, int w, int h)> layers)
    {
        if (layers.Count == 0) return (Array.Empty<byte>(), 0, 0);
        var (baseRgba, w, h) = layers[0];
        var outp = (byte[])baseRgba.Clone();
        for (int li = 1; li < layers.Count; li++)
        {
            var (src, sw, sh) = layers[li];
            int cw = Math.Min(w, sw), ch = Math.Min(h, sh);
            for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int di = (y * w + x) * 4, si = (y * sw + x) * 4;
                float sa = src[si + 3] / 255f;
                if (sa <= 0f) continue;
                float da = 1f - sa;
                outp[di]     = (byte)(src[si]     * sa + outp[di]     * da);
                outp[di + 1] = (byte)(src[si + 1] * sa + outp[di + 1] * da);
                outp[di + 2] = (byte)(src[si + 2] * sa + outp[di + 2] * da);
                outp[di + 3] = (byte)Math.Min(255f, src[si + 3] + outp[di + 3] * da);
            }
        }
        return (outp, w, h);
    }

    public uint GetIcon(ItemType itemType, uint iconId, uint underlayId, uint overlayId, uint effects)
    {
        if (iconId == 0) return 0;
        uint typeUnderlayDid = ResolveUnderlayDid(itemType);
        var key = (typeUnderlayDid, iconId, underlayId, overlayId, effects);
        if (_byTuple.TryGetValue(key, out var tex)) return tex;

        ComposedIcon? drag = GetOrCreateDragIcon(iconId, overlayId, effects);

        var layers = new List<(byte[] rgba, int w, int h)>();
        AddLayer(layers, typeUnderlayDid);
        AddLayer(layers, underlayId);
        if (drag is not null) layers.Add((drag.Rgba, drag.Width, drag.Height));
        if (layers.Count == 0) return 0;

        var (rgba, w, h) = Compose(layers);
        uint handle = _cache.UploadRgba8(rgba, w, h, nearest: true);
        _byTuple[key] = handle;
        return handle;
    }

    public uint GetDragIcon(ItemType itemType, uint iconId, uint underlayId, uint overlayId, uint effects)
    {
        _ = itemType;
        _ = underlayId;
        return iconId == 0 ? 0u : GetOrCreateDragIcon(iconId, overlayId, effects)?.Texture ?? 0u;
    }

    public (uint tex, int w, int h) GetKeyedIcon(uint iconId)
    {
        if (iconId == 0u) return (0u, 0, 0);
        ComposedIcon? icon = GetOrCreateDragIcon(iconId, overlayId: 0u, effects: 0u);
        return icon is null ? (0u, 0, 0) : (icon.Texture, icon.Width, icon.Height);
    }

    internal bool TryGetKeyedIconRgba(uint iconId, out byte[] rgba, out int w, out int h)
    {
        rgba = Array.Empty<byte>(); w = 0; h = 0;
        if (iconId == 0u) return false;
        ComposedIcon? icon = GetOrCreateDragIcon(iconId, overlayId: 0u, effects: 0u);
        if (icon is null) return false;
        rgba = icon.Rgba; w = icon.Width; h = icon.Height;
        return true;
    }

    internal bool TryDecodeRaw(uint renderSurfaceId, out byte[] rgba, out int w, out int h)
    {
        rgba = Array.Empty<byte>(); w = 0; h = 0;
        if (!TryDecode(renderSurfaceId, out DecodedTexture decoded)) return false;
        rgba = decoded.Rgba8; w = decoded.Width; h = decoded.Height;
        return true;
    }

    private ComposedIcon? GetOrCreateDragIcon(uint iconId, uint overlayId, uint effects)
    {
        var key = (iconId, overlayId, effects);
        if (_dragByTuple.TryGetValue(key, out var cached)) return cached;

        var dragLayers = new List<(byte[] rgba, int w, int h)>();
        AddLayer(dragLayers, iconId);
        AddLayer(dragLayers, overlayId);
        if (dragLayers.Count == 0) return null;

        var composed = Compose(dragLayers);
        if (TryGetEffectTile(effects, out var tile))
            ReplaceWhiteFromSurface(composed.rgba, composed.w, composed.h,
                tile.Rgba8, tile.Width, tile.Height);

        uint texture = _cache.UploadRgba8(composed.rgba, composed.w, composed.h, nearest: true);
        var created = new ComposedIcon(composed.rgba, composed.w, composed.h, texture);
        _dragByTuple[key] = created;
        return created;
    }

    private void AddLayer(List<(byte[], int, int)> layers, uint renderSurfaceId)
    {
        if (renderSurfaceId == 0) return;
        if (!_dats.Portal.TryGet<RenderSurface>(renderSurfaceId, out var rs) &&
            !_dats.HighRes.TryGet<RenderSurface>(renderSurfaceId, out rs))
            return;
        var decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette: null);
        layers.Add((decoded.Rgba8, decoded.Width, decoded.Height));
    }

    public uint GetSpellIcon(uint spellId)
    {
        if (_spellIcons.TryGetValue(spellId, out uint cached)) return cached;
        DatReaderWriter.DBObjs.SpellTable? table =
            _dats.Get<DatReaderWriter.DBObjs.SpellTable>(0x0E00000Eu);
        if (table is null || !table.Spells.TryGetValue(spellId, out var spell)) return 0u;

        uint power = spell.Components.Count == 0
            ? 0u
            : RetailSpellFormula.DeterminePowerLevelOfComponent(spell.Components[0]);
        uint powerBacking = RetailDataIdResolver.Resolve(_dats, power, 0x10000006u);
        var layers = new List<(byte[] rgba, int w, int h)>();
        AddLayer(layers, powerBacking);
        AddLayer(layers, spell.Icon);
        if (layers.Count == 0) return 0u;

        var composed = Compose(layers);
        uint tintIndex = (spell.Bitfield & DatReaderWriter.Enums.SpellIndex.Reversed) != 0
            ? 1u : 2u;
        uint tintDid = RetailDataIdResolver.Resolve(_dats, tintIndex, 0x10000007u);
        if (TryDecode(tintDid, out DecodedTexture tint))
            ReplaceWhiteFromSurface(composed.rgba, composed.w, composed.h,
                tint.Rgba8, tint.Width, tint.Height);

        uint overlayIndex = (spell.Bitfield & DatReaderWriter.Enums.SpellIndex.FellowshipSpell) != 0
            ? 4u
            : (spell.Bitfield & DatReaderWriter.Enums.SpellIndex.SelfTargeted) != 0
                ? 3u : 0u;
        if (overlayIndex != 0u)
        {
            uint overlayDid = RetailDataIdResolver.Resolve(_dats, overlayIndex, 0x10000007u);
            if (TryDecode(overlayDid, out DecodedTexture overlay))
                composed = Compose([
                    (composed.rgba, composed.w, composed.h),
                    (overlay.Rgba8, overlay.Width, overlay.Height)]);
        }

        uint texture = _cache.UploadRgba8(composed.rgba, composed.w, composed.h, nearest: true);
        _spellIcons[spellId] = texture;
        return texture;
    }

    public uint GetSpellComponentIcon(uint iconId)
    {
        if (iconId == 0u) return 0u;
        if (_componentIcons.TryGetValue(iconId, out uint cached)) return cached;
        if (!TryDecode(iconId, out DecodedTexture icon)) return 0u;
        byte[] rgba = (byte[])icon.Rgba8.Clone();
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] != 255 || rgba[i + 1] != 255 || rgba[i + 2] != 255 || rgba[i + 3] != 255)
                continue;
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 0;
        }
        uint texture = _cache.UploadRgba8(rgba, icon.Width, icon.Height, nearest: true);
        _componentIcons[iconId] = texture;
        return texture;
    }
}

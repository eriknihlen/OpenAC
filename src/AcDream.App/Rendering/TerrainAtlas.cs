using AcDream.App.Rendering.Gpu;
using AcDream.Core.Textures;
using DatReaderWriter;
using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatPixelFormat = DatReaderWriter.Enums.PixelFormat;

namespace AcDream.App.Rendering;

public sealed class TerrainAtlas : IDisposable
{
    internal static readonly GpuSamplerDescription DetailSamplerDescription =
        GpuSamplerDescription.WorldRepeat;

    internal readonly record struct RetailDetailTextureBinding(
        GpuTextureSlot TextureSlot,
        float Tiling,
        uint SurfaceTextureId,
        uint RenderSurfaceId,
        int Width,
        int Height)
    {
        public bool IsAvailable =>
            TextureSlot.IsAssigned
            && SurfaceTextureId != 0
            && RenderSurfaceId != 0;
    }

    private sealed record DetailTextureResource(
        IGpuTexture Texture,
        RetailDetailTextureBinding Binding);

    public IReadOnlyDictionary<uint, uint> TerrainTypeToLayer { get; }
    public int LayerCount { get; }
    public IReadOnlyList<float> TilingByLayer { get; }

    public int AlphaLayerCount { get; }
    public IReadOnlyList<byte> CornerAlphaLayers { get; }
    /// <summary>Layer indices in the alpha atlas for SideTerrainMaps (typically 4 entries).</summary>
    public IReadOnlyList<byte> SideAlphaLayers { get; }
    public IReadOnlyList<byte> RoadAlphaLayers { get; }

    public IReadOnlyList<uint> CornerAlphaTCodes { get; }
    public IReadOnlyList<uint> SideAlphaTCodes { get; }
    public IReadOnlyList<uint> RoadAlphaRCodes { get; }

    internal RetailDetailTextureBinding BuildingDetailTexture { get; }

    internal RetailDetailTextureBinding EnvironmentDetailTexture { get; }

    private sealed class RhiArrays(
        IGpuDevice device,
        IGpuTexture terrain,
        IGpuTexture alpha,
        IGpuSampler alphaSampler)
    {
        public IGpuDevice Device { get; } = device;
        public IGpuTexture Terrain { get; } = terrain;
        public IGpuTexture Alpha { get; } = alpha;
        public IGpuSampler AlphaSampler { get; } = alphaSampler;
        public IGpuSampler? TerrainSampler { get; set; }
        public IGpuTexture? BuildingDetailTexture { get; set; }
        public IGpuTexture? EnvironmentDetailTexture { get; set; }
        public GpuTextureSlot TerrainSlot { get; set; } = GpuTextureSlot.Unassigned;
        public GpuTextureSlot AlphaSlot { get; set; } = GpuTextureSlot.Unassigned;
    }

    private readonly RhiArrays _rhi;

    internal (GpuTextureSlot Terrain, GpuTextureSlot Alpha) TextureSlots =>
        (_rhi.TerrainSlot, _rhi.AlphaSlot);

    private const float RetailMaxAnisotropy = 16f;

    private TerrainAtlas(
        IGpuDevice device,
        IGpuTexture terrain,
        IGpuTexture alpha,
        IGpuSampler alphaSampler,
        IReadOnlyDictionary<uint, uint> map,
        int layerCount,
        IReadOnlyList<float> tilingByLayer,
        int alphaLayerCount,
        IReadOnlyList<byte> cornerLayers,
        IReadOnlyList<byte> sideLayers,
        IReadOnlyList<byte> roadLayers,
        IReadOnlyList<uint> cornerTCodes,
        IReadOnlyList<uint> sideTCodes,
        IReadOnlyList<uint> roadRCodes,
        DetailTextureResource? buildingDetail,
        DetailTextureResource? environmentDetail)
    {
        _rhi = new RhiArrays(device, terrain, alpha, alphaSampler);
        TerrainTypeToLayer = map;
        LayerCount = layerCount;
        TilingByLayer = tilingByLayer;
        AlphaLayerCount = alphaLayerCount;
        CornerAlphaLayers = cornerLayers;
        SideAlphaLayers = sideLayers;
        RoadAlphaLayers = roadLayers;
        CornerAlphaTCodes = cornerTCodes;
        SideAlphaTCodes = sideTCodes;
        RoadAlphaRCodes = roadRCodes;
        _rhi.BuildingDetailTexture = buildingDetail?.Texture;
        _rhi.EnvironmentDetailTexture = environmentDetail?.Texture;
        BuildingDetailTexture = buildingDetail?.Binding ?? default;
        EnvironmentDetailTexture = environmentDetail?.Binding ?? default;
        _rhi.AlphaSlot = device.RegisterTexture(alpha, alphaSampler);
        ApplyAnisotropic(RetailMaxAnisotropy);
    }

    private readonly record struct TerrainLayerDecode(
        Dictionary<uint, DecodedTexture> DecodedByType,
        Dictionary<uint, uint> TilingByType,
        int MaxWidth,
        int MaxHeight);

    private static TerrainLayerDecode DecodeTerrainLayers(
        IDatReaderWriter dats,
        IReadOnlyList<DatReaderWriter.Types.TMTerrainDesc> terrainDesc)
    {
        var decodedByType = new Dictionary<uint, DecodedTexture>();
        var tilingByType = new Dictionary<uint, uint>();
        int maxW = 1, maxH = 1;
        foreach (var tmtd in terrainDesc)
        {
            uint typeKey = (uint)tmtd.TerrainType;
            if (decodedByType.ContainsKey(typeKey))
                continue;

            tilingByType[typeKey] = tmtd.TerrainTex.TexTiling;

            var surfaceTextureId = (uint)tmtd.TerrainTex.TextureId;
            var st = dats.Get<SurfaceTexture>(surfaceTextureId);
            if (st is null || st.Textures.Count == 0)
            {
                Console.WriteLine($"WARN: TerrainType {tmtd.TerrainType} SurfaceTexture 0x{surfaceTextureId:X8} missing");
                decodedByType[typeKey] = DecodedTexture.Magenta;
                continue;
            }

            var rs = dats.Get<RenderSurface>((uint)st.Textures[0]);
            if (rs is null)
            {
                decodedByType[typeKey] = DecodedTexture.Magenta;
                continue;
            }

            Palette? palette = rs.DefaultPaletteId != 0
                ? dats.Get<Palette>(rs.DefaultPaletteId)
                : null;

            var decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette);
            decodedByType[typeKey] = decoded;
            if (decoded.Width > maxW) maxW = decoded.Width;
            if (decoded.Height > maxH) maxH = decoded.Height;
        }

        return new TerrainLayerDecode(decodedByType, tilingByType, maxW, maxH);
    }

    private sealed record AlphaLayerDecode(
        List<DecodedTexture> Decoded,
        List<byte> CornerLayers,
        List<byte> SideLayers,
        List<byte> RoadLayers,
        List<uint> CornerTCodes,
        List<uint> SideTCodes,
        List<uint> RoadRCodes,
        int MaxWidth,
        int MaxHeight);

    private static AlphaLayerDecode DecodeAlphaLayers(
        IDatReaderWriter dats,
        DatReaderWriter.Types.TexMerge texMerge)
    {
        var decoded = new List<DecodedTexture>();
        var cornerLayers = new List<byte>();
        var sideLayers = new List<byte>();
        var roadLayers = new List<byte>();
        var cornerTCodes = new List<uint>();
        var sideTCodes = new List<uint>();
        var roadRCodes = new List<uint>();

        foreach (var entry in texMerge.CornerTerrainMaps)
        {
            if (TryDecodeAlphaMap(dats, (uint)entry.TextureId, out var dtex))
            {
                cornerLayers.Add((byte)decoded.Count);
                cornerTCodes.Add(entry.TCode);
                decoded.Add(dtex);
            }
            else
            {
                Console.WriteLine($"WARN: CornerTerrainMap TextureId 0x{(uint)entry.TextureId:X8} failed to decode");
            }
        }
        foreach (var entry in texMerge.SideTerrainMaps)
        {
            if (TryDecodeAlphaMap(dats, (uint)entry.TextureId, out var dtex))
            {
                sideLayers.Add((byte)decoded.Count);
                sideTCodes.Add(entry.TCode);
                decoded.Add(dtex);
            }
            else
            {
                Console.WriteLine($"WARN: SideTerrainMap TextureId 0x{(uint)entry.TextureId:X8} failed to decode");
            }
        }
        foreach (var entry in texMerge.RoadMaps)
        {
            if (TryDecodeAlphaMap(dats, (uint)entry.TextureId, out var dtex))
            {
                roadLayers.Add((byte)decoded.Count);
                roadRCodes.Add(entry.RCode);
                decoded.Add(dtex);
            }
            else
            {
                Console.WriteLine($"WARN: RoadMap TextureId 0x{(uint)entry.TextureId:X8} failed to decode");
            }
        }

        int decodedMaxW = 1, decodedMaxH = 1;
        foreach (var d in decoded)
        {
            if (d.Width > decodedMaxW) decodedMaxW = d.Width;
            if (d.Height > decodedMaxH) decodedMaxH = d.Height;
        }

        return new AlphaLayerDecode(
            decoded,
            cornerLayers,
            sideLayers,
            roadLayers,
            cornerTCodes,
            sideTCodes,
            roadRCodes,
            decodedMaxW,
            decodedMaxH);
    }

    internal static TerrainAtlas BuildBackendNeutral(IGpuDevice device, IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(dats);

        var region = dats.Get<Region>(0x13000000u)
            ?? throw new InvalidOperationException("Region dat id 0x13000000 missing");
        var texMerge = region.TerrainInfo?.LandSurfaces?.TexMerge;
        var terrainDesc = texMerge?.TerrainDesc;

        Dictionary<uint, DecodedTexture> decodedByType;
        Dictionary<uint, uint> tilingByType;
        int maxW, maxH;
        if (terrainDesc is null || terrainDesc.Count == 0)
        {
            Console.WriteLine("WARN: TerrainDesc missing, using single white fallback layer");
            decodedByType = new Dictionary<uint, DecodedTexture> { [0u] = WhitePixel() };
            tilingByType = [];
            maxW = 1;
            maxH = 1;
        }
        else
        {
            TerrainLayerDecode decode = DecodeTerrainLayers(dats, terrainDesc);
            decodedByType = decode.DecodedByType;
            tilingByType = decode.TilingByType;
            maxW = decode.MaxWidth;
            maxH = decode.MaxHeight;
        }

        AlphaLayerDecode alpha = texMerge is null
            ? new AlphaLayerDecode([], [], [], [], [], [], [], 1, 1)
            : DecodeAlphaLayers(dats, texMerge);
        List<DecodedTexture> alphaDecoded = alpha.Decoded;
        int alphaLayerCount = Math.Max(1, alphaDecoded.Count);
        int alphaW = alphaDecoded.Count == 0 ? 1 : alpha.MaxWidth;
        int alphaH = alphaDecoded.Count == 0 ? 1 : alpha.MaxHeight;

        int layerCount = decodedByType.Count;
        int mipLevels = Wb.RhiWorldTextureArray.MipLevelsFor(maxW, maxH);
        IGpuTexture? terrainTexture = null;
        IGpuTexture? alphaTexture = null;
        IGpuSampler? detailSampler = null;
        DetailTextureResource? buildingDetail = null;
        DetailTextureResource? environmentDetail = null;
        try
        {
            terrainTexture = device.CreateTexture(new GpuTextureDescription(
                "terrain-atlas",
                GpuTextureKind.Texture2DArray,
                GpuTextureFormat.Rgba8Unorm,
                maxW,
                maxH,
                layerCount,
                mipLevels));

            var map = new Dictionary<uint, uint>(layerCount);
            int layerIdx = 0;
            foreach (var kvp in decodedByType)
            {
                byte[] buffer = ResizeRgba8Nearest(kvp.Value, maxW, maxH);
                terrainTexture.Upload(0, layerIdx, buffer);
                map[kvp.Key] = (uint)layerIdx;
                layerIdx++;
            }

            terrainTexture.GenerateMipChain();

            var tilingByLayer = TerrainTextureTilingTable.Build(
                map.Select(entry =>
                    (entry.Value, tilingByType.TryGetValue(entry.Key, out uint repeatCount)
                        ? repeatCount
                        : 1u)));

            alphaTexture = device.CreateTexture(new GpuTextureDescription(
                "terrain-alpha-atlas",
                GpuTextureKind.Texture2DArray,
                GpuTextureFormat.Rgba8Unorm,
                alphaW,
                alphaH,
                alphaLayerCount,
                MipLevelCount: 1));
            if (alphaDecoded.Count == 0)
            {
                Console.WriteLine("WARN: no alpha maps loaded; alpha atlas will be a 1x1 white fallback");
                alphaTexture.Upload(0, 0, [0xFF, 0xFF, 0xFF, 0xFF]);
            }
            else
            {
                for (int i = 0; i < alphaDecoded.Count; i++)
                    alphaTexture.Upload(0, i, ResizeRgba8Nearest(alphaDecoded[i], alphaW, alphaH));
            }

            IGpuSampler alphaSampler = device.CreateSampler(GpuSamplerDescription.WorldClamp with
            {
                MipFilter = GpuMipFilter.None,
            });

            detailSampler = device.CreateSampler(DetailSamplerDescription);
            if (terrainDesc is { Count: > 1 })
            {
                buildingDetail = TryCreateDetailTexture(
                    device,
                    dats,
                    detailSampler,
                    terrainDesc[1],
                    "building");
            }
            if (terrainDesc is { Count: > 2 })
            {
                environmentDetail = TryCreateDetailTexture(
                    device,
                    dats,
                    detailSampler,
                    terrainDesc[2],
                    "environment");
            }

            Console.WriteLine(
                $"TerrainAtlas: {layerCount} terrain layers at {maxW}x{maxH} ({mipLevels} mip levels)");
            Console.WriteLine(
                $"AlphaAtlas: {alphaLayerCount} layers at {alphaW}x{alphaH}  "
                + $"(corners={alpha.CornerLayers.Count}, sides={alpha.SideLayers.Count}, "
                + $"roads={alpha.RoadLayers.Count})");

            return new TerrainAtlas(
                device,
                terrainTexture,
                alphaTexture,
                alphaSampler,
                map,
                layerCount,
                tilingByLayer,
                alphaLayerCount,
                alpha.CornerLayers,
                alpha.SideLayers,
                alpha.RoadLayers,
                alpha.CornerTCodes,
                alpha.SideTCodes,
                alpha.RoadRCodes,
                buildingDetail,
                environmentDetail);
        }
        catch
        {
            DisposeDetailTextureResource(device, environmentDetail);
            DisposeDetailTextureResource(device, buildingDetail);
            alphaTexture?.Dispose();
            terrainTexture?.Dispose();
            throw;
        }
    }

    private static DetailTextureResource? TryCreateDetailTexture(
        IGpuDevice device,
        IDatReaderWriter dats,
        IGpuSampler sampler,
        DatReaderWriter.Types.TMTerrainDesc terrain,
        string categoryName)
    {
        uint surfaceTextureId = (uint)terrain.TerrainTex.DetailTextureId;
        if (surfaceTextureId == 0)
            return null;

        SurfaceTexture? surfaceTexture = dats.Get<SurfaceTexture>(surfaceTextureId);
        if (surfaceTexture is null || surfaceTexture.Textures.Count == 0)
        {
            Console.WriteLine(
                $"WARN: retail {categoryName} detail SurfaceTexture "
                + $"0x{surfaceTextureId:X8} missing");
            return null;
        }

        uint renderSurfaceId = (uint)surfaceTexture.Textures[0];
        RenderSurface? renderSurface = dats.Get<RenderSurface>(renderSurfaceId);
        if (renderSurface is null)
        {
            Console.WriteLine(
                $"WARN: retail {categoryName} detail RenderSurface "
                + $"0x{renderSurfaceId:X8} missing");
            return null;
        }

        Palette? palette = renderSurface.DefaultPaletteId != 0
            ? dats.Get<Palette>(renderSurface.DefaultPaletteId)
            : null;
        DecodedTexture decoded = SurfaceDecoder.DecodeRenderSurface(
            renderSurface,
            palette);
        if (ReferenceEquals(decoded, DecodedTexture.Magenta))
        {
            Console.WriteLine(
                $"WARN: retail {categoryName} detail RenderSurface "
                + $"0x{renderSurfaceId:X8} failed to decode");
            return null;
        }

        int mipLevels = Wb.RhiWorldTextureArray.MipLevelsFor(
            decoded.Width,
            decoded.Height);
        IGpuTexture? texture = null;
        GpuTextureSlot slot = GpuTextureSlot.Unassigned;
        try
        {
            texture = device.CreateTexture(new GpuTextureDescription(
                $"retail-detail-{categoryName}",
                GpuTextureKind.Texture2DArray,
                GpuTextureFormat.Rgba8Unorm,
                decoded.Width,
                decoded.Height,
                LayerCount: 1,
                MipLevelCount: mipLevels));
            texture.Upload(0, 0, decoded.Rgba8);
            texture.GenerateMipChain();
            slot = device.RegisterTexture(texture, sampler);
            var binding = new RetailDetailTextureBinding(
                slot,
                terrain.TerrainTex.DetailTexTiling,
                surfaceTextureId,
                renderSurfaceId,
                decoded.Width,
                decoded.Height);
            Console.WriteLine(
                $"Retail detail {categoryName}: SurfaceTexture "
                + $"0x{surfaceTextureId:X8} -> RenderSurface "
                + $"0x{renderSurfaceId:X8}, {decoded.Width}x{decoded.Height}, "
                + $"tiling={binding.Tiling}");
            return new DetailTextureResource(texture, binding);
        }
        catch
        {
            if (slot.IsAssigned)
                device.ReleaseTextureSlot(slot);
            texture?.Dispose();
            throw;
        }
    }

    private static void DisposeDetailTextureResource(
        IGpuDevice device,
        DetailTextureResource? resource)
    {
        if (resource is null)
            return;

        if (resource.Binding.IsAvailable)
            device.ReleaseTextureSlot(resource.Binding.TextureSlot);
        resource.Texture.Dispose();
    }

    private static DecodedTexture WhitePixel() =>
        new([0xFF, 0xFF, 0xFF, 0xFF], 1, 1);

    private void ApplyAnisotropic(float level)
    {
        RhiArrays rhi = _rhi!;
        IGpuSampler sampler = rhi.Device.CreateSampler(GpuSamplerDescription.WorldRepeat with
        {
            MaxAnisotropy = Math.Max(1f, level),
        });
        if (ReferenceEquals(rhi.TerrainSampler, sampler) && rhi.TerrainSlot.IsAssigned)
            return;

        if (rhi.TerrainSlot.IsAssigned)
            rhi.Device.ReleaseTextureSlot(rhi.TerrainSlot);
        rhi.TerrainSampler = sampler;
        rhi.TerrainSlot = rhi.Device.RegisterTexture(rhi.Terrain, sampler);
    }

    private static bool TryDecodeAlphaMap(IDatReaderWriter dats, uint surfaceTextureId, out DecodedTexture decoded)
    {
        decoded = DecodedTexture.Magenta;

        var st = dats.Get<SurfaceTexture>(surfaceTextureId);
        if (st is null || st.Textures.Count == 0)
            return false;

        var rs = dats.Get<RenderSurface>((uint)st.Textures[0]);
        if (rs is null)
            return false;

        // Alpha maps ship as PFID_CUSTOM_LSCAPE_ALPHA (AC's landscape-alpha
        // format) or the more generic PFID_A8; terrain blending alpha masks
        // MUST use isAdditive=true so R=G=B=A=val — the terrain fragment shader
        // reads .r for the blend weight. Palette is not used.
        var d = SurfaceDecoder.DecodeRenderSurface(rs, palette: null, isClipMap: false, isAdditive: true);
        if (ReferenceEquals(d, DecodedTexture.Magenta))
            return false;

        decoded = d;
        return true;
    }

    private static byte[] ResizeRgba8Nearest(DecodedTexture src, int dstW, int dstH)
    {
        if (src.Width == dstW && src.Height == dstH)
            return src.Rgba8;

        var dst = new byte[dstW * dstH * 4];
        for (int y = 0; y < dstH; y++)
        {
            int srcY = y * src.Height / dstH;
            for (int x = 0; x < dstW; x++)
            {
                int srcX = x * src.Width / dstW;
                int si = (srcY * src.Width + srcX) * 4;
                int di = (y * dstW + x) * 4;
                dst[di + 0] = src.Rgba8[si + 0];
                dst[di + 1] = src.Rgba8[si + 1];
                dst[di + 2] = src.Rgba8[si + 2];
                dst[di + 3] = src.Rgba8[si + 3];
            }
        }
        return dst;
    }

    public void SetAnisotropic(int level)
    {
        ApplyAnisotropic(level);
        Console.WriteLine($"TerrainAtlas: anisotropic updated to {level}x");
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _rhi.EnvironmentDetailTexture?.Dispose();
        _rhi.BuildingDetailTexture?.Dispose();
        _rhi.Alpha.Dispose();
        _rhi.Terrain.Dispose();
    }
}

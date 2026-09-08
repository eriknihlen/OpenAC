// src/AcDream.App/Rendering/TextureCache.cs
using AcDream.Core.Textures;
using AcDream.Core.World;
using AcDream.Content;
using AcDream.App.Rendering.Gpu;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using System.Linq;
using PixelFormatId = DatReaderWriter.Enums.PixelFormat;
using SurfaceType = DatReaderWriter.Enums.SurfaceType;
using AcDream.App.Rendering.Residency;

namespace AcDream.App.Rendering;

public sealed class TextureCache
    : Wb.IEntityTextureLifetime,
      IDisposable
{
    private readonly IGpuDevice _device;
    private readonly IDatReaderWriter _dats;
    private readonly string _diagnosticsDirectory;
    private readonly Dictionary<(uint SurfaceId, uint OrigTextureId), (int Width, int Height)>
        _decodedDimensionsByTexture = new();

    private readonly record struct GpuUiTextureEntry(
        IGpuTexture Texture,
        GpuTextureSlot Slot,
        uint GlName,
        int Width,
        int Height);

    private readonly Dictionary<(uint SurfaceId, bool Nearest), GpuUiTextureEntry>
        _renderSurfaceGpuTextures = new();

    private readonly HashSet<uint> _loggedMissingRenderSurfaceIds = new();

    private readonly List<GpuUiTextureEntry> _adhocGpuTextures = new();

    private readonly Dictionary<uint, IGpuTexture> _nearestUiTextureSources = new();

    private readonly Dictionary<uint, uint> _linearUiTwinHandles = new();

    private readonly CompositeTextureArrayCache? _compositeTextures;
    private bool _destinationRevealUploadPriority;

    private readonly StandaloneBindlessTextureCache? _particleTextures;
    private readonly Dictionary<(uint surfaceId, uint origTexOverride), bool> _paletteIndexedByTexture = new();

    internal int OwnedBindlessTextureCount => _compositeTextures?.ActiveResourceCount ?? 0;
    internal int TextureOwnerCount => _compositeTextures?.OwnerCount ?? 0;
    internal int CachedCompositeTextureCount => _compositeTextures?.CachedEntryCount ?? 0;
    internal int CachedUnownedCompositeCount => _compositeTextures?.UnownedEntryCount ?? 0;
    internal long CachedUnownedCompositeBytes => _compositeTextures?.UnownedBytes ?? 0;
    internal int CompositeAtlasCount => _compositeTextures?.AtlasCount ?? 0;
    internal long CompositeAtlasBytes => _compositeTextures?.AllocatedBytes ?? 0;
    internal int CompositeFrameUploadCount => _compositeTextures?.FrameUploadCount ?? 0;
    internal long CompositeFrameUploadBytes => _compositeTextures?.FrameUploadBytes ?? 0;
    internal bool CanStartCompositeUpload => _compositeTextures?.CanStartUpload == true;
    internal int CachedParticleTextureCount => _particleTextures?.EntryCount ?? 0;
    internal int ActiveParticleTextureCount => _particleTextures?.ActiveResourceCount ?? 0;
    internal int ParticleTextureOwnerCount => _particleTextures?.OwnerCount ?? 0;
    internal int CachedUnownedParticleTextureCount => _particleTextures?.UnownedEntryCount ?? 0;
    internal long CachedUnownedParticleTextureBytes => _particleTextures?.UnownedBytes ?? 0;

    internal void SetDestinationRevealUploadPriority(bool enabled) =>
        _destinationRevealUploadPriority = enabled;

    private readonly Dictionary<uint, (int Width, int Height, string Format)> _uploadMetadata = new();

    private int _dumpFrameCounter;
    private bool _surfaceHistogramAlreadyDumped;

    internal TextureCache(IGpuDevice device, IDatReaderWriter dats)
        : this(
            device,
            dats,
            ImmediateGpuResourceRetirementQueue.Instance,
            Path.Combine(
                Path.GetTempPath(),
                "acdream",
                "diagnostics"))
    {
    }

    internal TextureCache(
        IGpuDevice device,
        IDatReaderWriter dats,
        IGpuResourceRetirementQueue retirementQueue,
        string diagnosticsDirectory,
        ResidencyBudgetOptions? budgets = null)
    {
        budgets ??= ResidencyBudgetOptions.Default;
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _dats = dats;
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsDirectory);
        _diagnosticsDirectory = diagnosticsDirectory;
        ArgumentNullException.ThrowIfNull(retirementQueue);

        var resources = new ResourceCleanupGroup();
        CompositeTextureArrayCache? composite = null;
        StandaloneBindlessTextureCache? particles = null;
        try
        {
            composite = new CompositeTextureArrayCache(
                new RhiCompositeTextureArrayBackend(device),
                retirementQueue,
                budgets.CompositeUnownedBytes,
                budgets.CompositePhysicalBytes);
            resources.Add("composite texture cache", composite.Dispose);
            particles = new StandaloneBindlessTextureCache(
                new ParticleRhiTextureBackend(this),
                retirementQueue,
                budgets.StandaloneUnownedBytes,
                budgets.StandaloneUnownedEntries);
            resources.Add("particle texture cache", particles.Dispose);
            resources.TransferAll();
        }
        catch (Exception constructionFailure)
        {
            resources.RollbackConstructionAndThrow(
                "TextureCache construction failed and its child-cache prefix did not cleanly roll back.",
                constructionFailure);
        }

        _compositeTextures = composite;
        _particleTextures = particles;
    }

    internal void RegisterResidencySources(ResidencyManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (_compositeTextures is not null)
        {
            manager.RegisterDomainSource(new DelegateResidencyDomainSource(
                ResidencyDomain.CompositeTextures,
                _compositeTextures.CaptureResidency));
        }
        if (_particleTextures is not null)
        {
            manager.RegisterDomainSource(new DelegateResidencyDomainSource(
                ResidencyDomain.StandaloneTextures,
                CaptureStandaloneResidency));
        }
    }

    private ResidencyDomainSnapshot CaptureStandaloneResidency()
    {
        StandaloneBindlessTextureCache textures = EnsureParticleTexturesAvailable();
        return new ResidencyDomainSnapshot(
            ResidencyDomain.StandaloneTextures,
            EntryCount: textures.EntryCount,
            OwnerCount: textures.OwnerCount,
            Charges: new ResidencyCharges(
                GpuResidentBytes: checked(
                    textures.AllocatedBytes - textures.RetiringBytes),
                RetiringBytes: textures.RetiringBytes),
            BudgetBytes: textures.BudgetBytes);
    }

    public uint GetOrUploadRenderSurface(uint renderSurfaceId, out int width, out int height, bool nearest = false)
    {
        var cacheKey = (renderSurfaceId, nearest);
        if (_renderSurfaceGpuTextures.TryGetValue(cacheKey, out GpuUiTextureEntry existing))
        {
            width = existing.Width; height = existing.Height;
            return UiTextureTableHandle.FromSlot(existing.Slot);
        }

        DecodedTexture decoded;
        if (_dats.Portal.TryGet<RenderSurface>(renderSurfaceId, out var rs)
            || _dats.HighRes.TryGet<RenderSurface>(renderSurfaceId, out rs))
        {
            Palette? palette = rs.DefaultPaletteId != 0
                ? _dats.Get<Palette>(rs.DefaultPaletteId)
                : null;
            decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette);
        }
        else
        {
            if (_loggedMissingRenderSurfaceIds.Add(renderSurfaceId))
            {
                Console.WriteLine(
                    $"[UI] TextureCache: RenderSurface 0x{renderSurfaceId:X8} was not "
                    + "found in Portal or HighRes — drawing the 1x1 magenta placeholder.");
            }
            decoded = DecodedTexture.Magenta;
        }

        GpuUiTextureEntry entry = UploadUiTexture(decoded, nearest, $"ui-rendersurface-0x{renderSurfaceId:X8}");
        _renderSurfaceGpuTextures[cacheKey] = entry;
        width = decoded.Width; height = decoded.Height;
        return UiTextureTableHandle.FromSlot(entry.Slot);
    }

    internal GpuTextureSlot RegisterWorldSurface(uint surfaceId, bool repeat)
    {
        var key = (surfaceId, repeat);
        if (_worldSurfaceGpuTextures.TryGetValue(key, out GpuUiTextureEntry existing))
            return existing.Slot;

        DecodedTexture decoded = DecodeFromDats(
            surfaceId,
            origTextureOverride: null,
            paletteOverride: null);
        GpuUiTextureEntry entry = UploadWorldSurfaceTexture(
            decoded,
            repeat,
            $"world-surface-0x{surfaceId:X8}{(repeat ? "-repeat" : "-clamp")}");
        _worldSurfaceGpuTextures[key] = entry;
        return entry.Slot;
    }

    private readonly Dictionary<(uint SurfaceId, bool Repeat), GpuUiTextureEntry>
        _worldSurfaceGpuTextures = new();

    private GpuUiTextureEntry UploadWorldSurfaceTexture(
        DecodedTexture decoded,
        bool repeat,
        string debugName)
    {
        IGpuTexture texture = _device.CreateTexture(new GpuTextureDescription(
            debugName,
            GpuTextureKind.Texture2D,
            GpuTextureFormat.Rgba8Unorm,
            Width: decoded.Width,
            Height: decoded.Height,
            LayerCount: 1,
            MipLevelCount: 1));
        try
        {
            texture.Upload(0, 0, decoded.Rgba8);
            uint glName = UploadAccountingName(texture);
            TrackUploadedTexture(glName, decoded.Width, decoded.Height);

            // Linear/linear with a single level — the filtering
            // TextureCache's own GL uploads have always used for sky surfaces,
            // and the wrap mode SamplerCache's two objects express on GL.
            IGpuSampler sampler = _device.CreateSampler(
                repeat ? GpuSamplerDescription.WorldRepeat : GpuSamplerDescription.WorldClamp);
            GpuTextureSlot slot = _device.RegisterTexture(texture, sampler);
            return new GpuUiTextureEntry(texture, slot, glName, decoded.Width, decoded.Height);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private GpuUiTextureEntry UploadUiTexture(DecodedTexture decoded, bool nearest, string debugName)
    {
        IGpuTexture texture = _device.CreateTexture(new GpuTextureDescription(
            debugName,
            GpuTextureKind.Texture2D,
            GpuTextureFormat.Rgba8Unorm,
            Width: decoded.Width,
            Height: decoded.Height,
            LayerCount: 1,
            MipLevelCount: 1));
        try
        {
            texture.Upload(0, 0, decoded.Rgba8);
            uint glName = UploadAccountingName(texture);
            TrackUploadedTexture(glName, decoded.Width, decoded.Height);

            IGpuSampler sampler = _device.CreateSampler(nearest ? UiNearestRepeat : GpuSamplerDescription.WorldRepeat);
            GpuTextureSlot slot = _device.RegisterTexture(texture, sampler);
            uint handle = UiTextureTableHandle.FromSlot(slot);
            if (nearest)
            {
                _nearestUiTextureSources[handle] = texture;
            }
            return new GpuUiTextureEntry(texture, slot, glName, decoded.Width, decoded.Height);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    internal uint GetOrCreateLinearUiTwin(uint handle)
    {
        if (!_nearestUiTextureSources.TryGetValue(handle, out IGpuTexture? texture))
            return handle;
        if (_linearUiTwinHandles.TryGetValue(handle, out uint twin))
            return twin;

        IGpuSampler linearSampler = _device.CreateSampler(GpuSamplerDescription.WorldRepeat);
        GpuTextureSlot twinSlot = _device.RegisterTexture(texture, linearSampler);
        uint twinHandle = UiTextureTableHandle.FromSlot(twinSlot);
        _linearUiTwinHandles[handle] = twinHandle;
        return twinHandle;
    }

    private uint UploadAccountingName(IGpuTexture texture) => _nextSyntheticUploadName--;

    private uint _nextSyntheticUploadName = uint.MaxValue;

    private static readonly GpuSamplerDescription UiNearestRepeat = new(
        GpuFilter.Nearest,
        GpuFilter.Nearest,
        GpuMipFilter.None,
        GpuAddressMode.Repeat,
        GpuAddressMode.Repeat,
        MaxAnisotropy: 1f);

    internal GpuTextureSlot AcquireParticleTexture(int emitterHandle, uint surfaceId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(emitterHandle);
        ArgumentOutOfRangeException.ThrowIfZero(surfaceId);
        StandaloneBindlessTextureCache textures = EnsureParticleTexturesAvailable();
        uint ownerId = checked((uint)emitterHandle);
        if (textures.TryAcquire(
                ownerId,
                surfaceId,
                out StandaloneBindlessTextureResource? existing))
        {
            return existing.Slot;
        }

        DecodedTexture decoded = DecodeFromDats(
            surfaceId,
            origTextureOverride: null,
            paletteOverride: null);

        return AcquireParticleTextureRhi(textures, ownerId, surfaceId, decoded);
    }

    private GpuTextureSlot AcquireParticleTextureRhi(
        StandaloneBindlessTextureCache textures,
        uint ownerId,
        uint surfaceId,
        DecodedTexture decoded)
    {
        IGpuTexture texture = _device.CreateTexture(new GpuTextureDescription(
            $"particle-surface-0x{surfaceId:X8}",
            GpuTextureKind.Texture2D,
            GpuTextureFormat.Rgba8Unorm,
            Width: decoded.Width,
            Height: decoded.Height,
            LayerCount: 1,
            MipLevelCount: 1));
        try
        {
            texture.Upload(0, 0, decoded.Rgba8);
            uint accountingName = UploadAccountingName(texture);
            TrackUploadedTexture(accountingName, decoded.Width, decoded.Height);

            IGpuSampler sampler = _device.CreateSampler(GpuSamplerDescription.WorldClamp);
            GpuTextureSlot slot = _device.RegisterTexture(texture, sampler);
            textures.AddAndAcquire(ownerId, new StandaloneBindlessTextureResource
            {
                SurfaceId = surfaceId,
                Name = accountingName,
                Texture = texture,
                Slot = slot,
                Bytes = checked((long)decoded.Width * decoded.Height * 4L),
            });
            return slot;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    internal void ReleaseParticleTextureOwner(int emitterHandle)
    {
        if (emitterHandle <= 0 || _particleTextures is null)
            return;
        _particleTextures.ReleaseOwner(checked((uint)emitterHandle));
    }

    internal BindlessTextureLocation GetOrUploadWithOrigTextureOverrideBindless(
        uint ownerLocalId,
        uint surfaceId,
        uint overrideOrigTextureId)
    {
        CompositeTextureArrayCache composites = EnsureCompositeTexturesAvailable();
        var key = new CompositeTextureKey(
            CompositeTextureKind.OriginalTextureOverride,
            surfaceId,
            overrideOrigTextureId,
            Palette: default);
        if (composites.TryAcquire(ownerLocalId, key, out BindlessTextureLocation existing))
            return existing;
        if (!composites.CanStartUpload)
            return default;
        (int width, int height) = ResolveDecodedDimensions(surfaceId, overrideOrigTextureId);
        if (!composites.CanPrepareUpload(width, height))
            return default;

        DecodedTexture decoded = DecodeFromDats(
            surfaceId,
            origTextureOverride: overrideOrigTextureId,
            paletteOverride: null,
            bakeAuthoredTranslucency: true);
        return composites.TryAddAndAcquire(ownerLocalId, key, decoded, out BindlessTextureLocation added)
            ? added
            : default;
    }

    internal BindlessTextureLocation GetOrUploadWithPaletteOverrideBindless(
        uint ownerLocalId,
        uint surfaceId,
        uint? overrideOrigTextureId,
        PaletteOverride paletteOverride,
        PaletteCompositeIdentity paletteIdentity)
    {
        CompositeTextureArrayCache composites = EnsureCompositeTexturesAvailable();
        uint origTexKey = overrideOrigTextureId ?? 0;
        var key = new CompositeTextureKey(
            CompositeTextureKind.PaletteComposite,
            surfaceId,
            origTexKey,
            paletteIdentity);
        if (composites.TryAcquire(ownerLocalId, key, out BindlessTextureLocation existing))
            return existing;
        if (!composites.CanStartUpload)
            return default;
        (int width, int height) = ResolveDecodedDimensions(surfaceId, overrideOrigTextureId);
        if (!composites.CanPrepareUpload(width, height))
            return default;

        DecodedTexture decoded = DecodeFromDats(
            surfaceId,
            origTextureOverride: overrideOrigTextureId,
            paletteOverride: paletteOverride,
            bakeAuthoredTranslucency: true);
        return composites.TryAddAndAcquire(ownerLocalId, key, decoded, out BindlessTextureLocation added)
            ? added
            : default;
    }

    internal bool IsPaletteIndexed(uint surfaceId, uint? overrideOrigTextureId)
    {
        uint origTexKey = overrideOrigTextureId ?? 0;
        var key = (surfaceId, origTexKey);
        if (_paletteIndexedByTexture.TryGetValue(key, out bool indexed))
            return indexed;

        Surface? surface = _dats.Get<Surface>(surfaceId);
        if (surface is null || surface.Type.HasFlag(SurfaceType.Base1Solid))
            return _paletteIndexedByTexture[key] = false;

        uint surfaceTextureId = overrideOrigTextureId ?? (uint)surface.OrigTextureId;
        SurfaceTexture? texture = _dats.Get<SurfaceTexture>(surfaceTextureId);
        if (texture is null || texture.Textures.Count == 0)
            return _paletteIndexedByTexture[key] = false;

        uint renderSurfaceId = (uint)texture.Textures[0];
        if (!_dats.Portal.TryGet<RenderSurface>(renderSurfaceId, out RenderSurface? renderSurface)
            && !_dats.HighRes.TryGet<RenderSurface>(renderSurfaceId, out renderSurface))
            return _paletteIndexedByTexture[key] = false;

        indexed = renderSurface.Format is PixelFormatId.PFID_P8 or PixelFormatId.PFID_INDEX16;
        _paletteIndexedByTexture[key] = indexed;
        return indexed;
    }

    private (int Width, int Height) ResolveDecodedDimensions(
        uint surfaceId,
        uint? overrideOrigTextureId)
    {
        var key = (surfaceId, overrideOrigTextureId ?? 0);
        if (_decodedDimensionsByTexture.TryGetValue(key, out var cached))
            return cached;

        Surface? surface = _dats.Get<Surface>(surfaceId);
        if (surface is null
            || surface.Type.HasFlag(SurfaceType.Base1Solid)
            || (uint)surface.OrigTextureId == 0)
            return _decodedDimensionsByTexture[key] = (1, 1);

        uint surfaceTextureId = overrideOrigTextureId ?? (uint)surface.OrigTextureId;
        SurfaceTexture? texture = _dats.Get<SurfaceTexture>(surfaceTextureId);
        if (texture is null || texture.Textures.Count == 0)
            return _decodedDimensionsByTexture[key] = (1, 1);

        uint renderSurfaceId = (uint)texture.Textures[0];
        if ((!_dats.Portal.TryGet<RenderSurface>(renderSurfaceId, out RenderSurface? renderSurface)
                && !_dats.HighRes.TryGet<RenderSurface>(renderSurfaceId, out renderSurface))
            || renderSurface.Width <= 0
            || renderSurface.Height <= 0
            || renderSurface.SourceData is null)
            return _decodedDimensionsByTexture[key] = (1, 1);

        return _decodedDimensionsByTexture[key] = (renderSurface.Width, renderSurface.Height);
    }

    public void ReleaseOwner(uint localEntityId)
    {
        EnsureCompositeTexturesAvailable().ReleaseOwner(localEntityId);
    }

    private CompositeTextureArrayCache EnsureCompositeTexturesAvailable() =>
        _compositeTextures ?? throw new InvalidOperationException(
            "This TextureCache owns no composite texture array cache.");

    private StandaloneBindlessTextureCache EnsureParticleTexturesAvailable() =>
        _particleTextures ?? throw new InvalidOperationException(
            "This TextureCache owns no standalone particle texture cache.");

    private sealed class ParticleRhiTextureBackend(TextureCache owner)
        : IStandaloneBindlessTextureBackend
    {
        public void MakeNonResident(StandaloneBindlessTextureResource resource)
        {
            if (resource.Slot.IsAssigned)
                owner._device.ReleaseTextureSlot(resource.Slot);
        }

        public void Delete(StandaloneBindlessTextureResource resource)
        {
            resource.Texture?.Dispose();
            owner.UntrackUploadedTexture(resource.Name);
        }
    }

    public void TickCompositeTextureCache() => _compositeTextures?.Tick();

    public void TickParticleTextureCache() => _particleTextures?.Tick();

    public void BeginCompositeTextureFrame() =>
        _compositeTextures?.BeginFrame(_destinationRevealUploadPriority);

    internal static ulong HashPaletteOverride(PaletteOverride p)
    {
        ulong h = 0xCBF29CE484222325UL;  // FNV-1a offset basis
        const ulong prime = 0x100000001B3UL;
        h = (h ^ p.BasePaletteId) * prime;
        foreach (var sp in p.SubPalettes)
        {
            h = (h ^ sp.SubPaletteId) * prime;
            h = (h ^ sp.Offset) * prime;
            h = (h ^ sp.Length) * prime;
        }
        return h;
    }

    internal static PaletteCompositeIdentity GetPaletteIdentity(PaletteOverride palette) =>
        new(palette, HashPaletteOverride(palette));

    public void TickSurfaceHistogramDumpIfEnabled()
    {
        if (_surfaceHistogramAlreadyDumped) return;
        if (!string.Equals(System.Environment.GetEnvironmentVariable("ACDREAM_DUMP_SURFACES"), "1", StringComparison.Ordinal)) return;
        _dumpFrameCounter++;
        if (_dumpFrameCounter < 600) return;
        if (_uploadMetadata.Count < 100) return;

        DumpSurfaceHistogram();
        _surfaceHistogramAlreadyDumped = true;
    }

    private void DumpSurfaceHistogram()
    {
        try
        {
            DumpSurfaceHistogramCore();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[N6-DUMP] Failed to write surface histogram: {ex.Message}");
        }
    }

    private void DumpSurfaceHistogramCore()
    {
        System.IO.Directory.CreateDirectory(_diagnosticsDirectory);
        var outPath = System.IO.Path.Combine(
            _diagnosticsDirectory,
            "n6-surfaces.txt");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# acdream surface-format histogram — generated {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine("# Per-entry: surfaceId(hex), width, height, format, byteCount");
        sb.AppendLine();

        var seen = new HashSet<uint>();
        long totalBytes = 0;
        var bucketsByDim = new Dictionary<(int W, int H), int>();
        var bucketsByFormat = new Dictionary<string, int>();
        var bucketsByTriple = new Dictionary<(int W, int H, string F), int>();

        void Emit(uint surfaceId, uint name)
        {
            if (!seen.Add(name)) return;
            if (!_uploadMetadata.TryGetValue(name, out var meta)) return;
            int bytes = meta.Width * meta.Height * 4;
            totalBytes += bytes;
            sb.AppendLine($"0x{surfaceId:X8}, {meta.Width}, {meta.Height}, {meta.Format}, {bytes}");

            var dimKey = (meta.Width, meta.Height);
            bucketsByDim[dimKey] = bucketsByDim.GetValueOrDefault(dimKey) + 1;
            bucketsByFormat[meta.Format] = bucketsByFormat.GetValueOrDefault(meta.Format) + 1;
            var tripleKey = (meta.Width, meta.Height, meta.Format);
            bucketsByTriple[tripleKey] = bucketsByTriple.GetValueOrDefault(tripleKey) + 1;
        }

        _particleTextures?.VisitEntries(resource => Emit(resource.SurfaceId, resource.Name));
        _compositeTextures?.VisitEntries((surfaceId, width, height) =>
        {
            int bytes = checked(width * height * 4);
            totalBytes += bytes;
            sb.AppendLine($"0x{surfaceId:X8}, {width}, {height}, RGBA8_COMPOSITE_LAYER, {bytes}");
            bucketsByDim[(width, height)] = bucketsByDim.GetValueOrDefault((width, height)) + 1;
            bucketsByFormat["RGBA8_COMPOSITE_LAYER"] =
                bucketsByFormat.GetValueOrDefault("RGBA8_COMPOSITE_LAYER") + 1;
            bucketsByTriple[(width, height, "RGBA8_COMPOSITE_LAYER")] =
                bucketsByTriple.GetValueOrDefault((width, height, "RGBA8_COMPOSITE_LAYER")) + 1;
        });

        sb.AppendLine();
        sb.AppendLine("# Rollups");
        sb.AppendLine($"# Total unique GL textures: {seen.Count}");
        sb.AppendLine($"# Total bytes (sum of W*H*4): {totalBytes}");

        sb.AppendLine("# Top 10 (W,H) dimension buckets:");
        foreach (var kv in bucketsByDim.OrderByDescending(kv => kv.Value).Take(10))
            sb.AppendLine($"#   {kv.Key.W}x{kv.Key.H}: {kv.Value}");

        sb.AppendLine("# Format buckets:");
        foreach (var kv in bucketsByFormat.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"#   {kv.Key}: {kv.Value}");

        sb.AppendLine("# Top 10 (W,H,format) triples — atlas-opportunity input:");
        foreach (var kv in bucketsByTriple.OrderByDescending(kv => kv.Value).Take(10))
            sb.AppendLine($"#   {kv.Key.W}x{kv.Key.H} {kv.Key.F}: {kv.Value}");

        System.IO.File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"[N6-DUMP] Surface histogram written to {outPath} ({seen.Count} textures, {totalBytes} bytes)");
    }

    private DecodedTexture DecodeFromDats(
        uint surfaceId,
        uint? origTextureOverride,
        PaletteOverride? paletteOverride,
        bool bakeAuthoredTranslucency = false)
    {
        var surface = _dats.Get<Surface>(surfaceId);
        if (surface is null)
        {
            Console.WriteLine($"[tex-miss] Surface 0x{surfaceId:X8} -> magenta (thread={System.Environment.CurrentManagedThreadId})");
            return DecodedTexture.Magenta;
        }

        if (surface.Type.HasFlag(SurfaceType.Base1Solid) || (uint)surface.OrigTextureId == 0)
            return SurfaceDecoder.DecodeSolidColor(surface.ColorValue, surface.Translucency);

        // Use the override SurfaceTexture id when present, otherwise the
        // Surface's native OrigTextureId.
        uint surfaceTextureId = origTextureOverride ?? (uint)surface.OrigTextureId;
        var surfaceTexture = _dats.Get<SurfaceTexture>(surfaceTextureId);
        if (surfaceTexture is null || surfaceTexture.Textures.Count == 0)
        {
            Console.WriteLine($"[tex-miss] SurfaceTexture 0x{surfaceTextureId:X8} (surface 0x{surfaceId:X8}) -> magenta (thread={System.Environment.CurrentManagedThreadId})");
            return DecodedTexture.Magenta;
        }

        uint renderSurfaceId = (uint)surfaceTexture.Textures[0];
        if (!_dats.Portal.TryGet<RenderSurface>(renderSurfaceId, out var rs)
            && !_dats.HighRes.TryGet<RenderSurface>(renderSurfaceId, out rs))
        {
            Console.WriteLine($"[tex-miss] RenderSurface 0x{renderSurfaceId:X8} (surface 0x{surfaceId:X8}) -> magenta (thread={System.Environment.CurrentManagedThreadId})");
            return DecodedTexture.Magenta;
        }

        Palette? basePalette = rs.DefaultPaletteId != 0
            ? _dats.Get<Palette>(rs.DefaultPaletteId)
            : null;

        Palette? effectivePalette = basePalette;
        if (paletteOverride is not null && basePalette is not null && paletteOverride.SubPalettes.Count > 0)
        {
            effectivePalette = ComposePalette(basePalette, paletteOverride);
        }

        bool isClipMap = surface.Type.HasFlag(SurfaceType.Base1ClipMap);
        bool isAdditive = surface.Type.HasFlag(SurfaceType.Additive);

        DecodedTexture decoded =
            SurfaceDecoder.DecodeRenderSurface(rs, effectivePalette, isClipMap, isAdditive);

        if (bakeAuthoredTranslucency
            && surface.Translucency > 0.0f
            && !ReferenceEquals(decoded, DecodedTexture.Magenta))
        {
            decoded = SurfaceDecoder.ApplyAuthoredTranslucency(decoded, surface.Translucency);
        }

        return decoded;
    }

    private Palette ComposePalette(Palette basePalette, PaletteOverride paletteOverride)
        => ComposeModifiedPalette(
            basePalette,
            paletteOverride.SubPalettes,
            id => _dats.Get<Palette>(id));

    internal static Palette ComposeModifiedPalette(
        Palette basePalette,
        IReadOnlyList<PaletteOverride.SubPaletteRange> subPalettes,
        Func<uint, Palette?> resolvePalette)
    {
        var modified = new Palette();
        modified.Colors.AddRange(basePalette.Colors);

        foreach (PaletteOverride.SubPaletteRange sub in subPalettes)
        {
            Palette? subPal = resolvePalette(sub.SubPaletteId);
            if (subPal is null) continue;

            int offset = sub.Offset << 3;
            int numcolors = (sub.Length == 0 ? 0x100 : sub.Length) << 3;
            int end = offset + numcolors;

            for (int i = offset; i < end; i++)
            {
                if (i >= modified.Colors.Count || i >= subPal.Colors.Count)
                    break;
                modified.Colors[i] = subPal.Colors[i];
            }
        }

        return modified;
    }

    public uint UploadRgba8(byte[] rgba, int width, int height, bool nearest = false)
    {
        GpuUiTextureEntry entry = UploadUiTexture(
            new DecodedTexture(rgba, width, height), nearest, "ui-adhoc-rgba8");
        _adhocGpuTextures.Add(entry);
        return UiTextureTableHandle.FromSlot(entry.Slot);
    }

    private void TrackUploadedTexture(uint name, int width, int height)
    {
        _uploadMetadata[name] = (width, height, "RGBA8_DECODED");
        long bytes = checked((long)width * height * 4L);
        Wb.GpuMemoryTracker.TrackResourceAllocation(Wb.GpuResourceType.Texture);
        Wb.GpuMemoryTracker.TrackAllocation(bytes, Wb.GpuResourceType.Texture);
    }

    private void UntrackUploadedTexture(uint name)
    {
        if (_uploadMetadata.Remove(name, out var metadata))
        {
            long bytes = checked((long)metadata.Width * metadata.Height * 4L);
            Wb.GpuMemoryTracker.TrackDeallocation(bytes, Wb.GpuResourceType.Texture);
            Wb.GpuMemoryTracker.TrackResourceDeallocation(Wb.GpuResourceType.Texture);
        }
    }

    public void Dispose()
    {
        _particleTextures?.Dispose();
        _compositeTextures?.Dispose();

        _paletteIndexedByTexture.Clear();

        foreach (uint twinHandle in _linearUiTwinHandles.Values)
            _device.ReleaseTextureSlot(UiTextureTableHandle.ToSlot(twinHandle));
        _linearUiTwinHandles.Clear();
        _nearestUiTextureSources.Clear();

        foreach (GpuUiTextureEntry entry in _renderSurfaceGpuTextures.Values)
        {
            entry.Texture.Dispose();
            _device.ReleaseTextureSlot(entry.Slot);
            UntrackUploadedTexture(entry.GlName);
        }
        _renderSurfaceGpuTextures.Clear();

        foreach (GpuUiTextureEntry entry in _worldSurfaceGpuTextures.Values)
        {
            entry.Texture.Dispose();
            _device.ReleaseTextureSlot(entry.Slot);
            UntrackUploadedTexture(entry.GlName);
        }
        _worldSurfaceGpuTextures.Clear();

        foreach (GpuUiTextureEntry entry in _adhocGpuTextures)
        {
            entry.Texture.Dispose();
            _device.ReleaseTextureSlot(entry.Slot);
            UntrackUploadedTexture(entry.GlName);
        }
        _adhocGpuTextures.Clear();
    }
}

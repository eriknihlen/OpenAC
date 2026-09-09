using AcDream.Core.Rendering.Wb;
using AcDream.Core.Meshing;
using AcDream.Content.Vfx;
using BCnEncoder.Decoder;
using BCnEncoder.ImageSharp;
using BCnEncoder.Shared;
using Chorizite.Core.Lib;
using Chorizite.Core.Render.Enums;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using CullMode = DatReaderWriter.Enums.CullMode;
using BoundingBox = Chorizite.Core.Lib.BoundingBox;

namespace AcDream.Content;

public sealed class MeshExtractor {
    private readonly IDatReaderWriter _dats;
    private readonly ILogger _logger;
    private readonly RetailPhysicsScriptLoader _physicsScripts;

    private readonly DecodedTextureCache _decodedTextureCache = new(
        maximumBytes: 64L * 1024 * 1024,
        maximumEntries: 128);
    private readonly ThreadLocal<BcDecoder> _bcDecoder = new(() => new BcDecoder());

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte[]> _solidColorTextureCache = new();

    public CacheStats DecodedTextureCacheStats => _decodedTextureCache.Stats;

    private readonly Action<ObjectMeshData>? _sideStagedSink;

    public MeshExtractor(IDatReaderWriter dats, ILogger logger, Action<ObjectMeshData>? sideStagedSink) {
        _dats = dats;
        _logger = logger;
        _sideStagedSink = sideStagedSink;
        _physicsScripts = new RetailPhysicsScriptLoader(dats.Portal);
    }

    public ObjectMeshData? PrepareMeshData(ulong id, bool isSetup, CancellationToken ct = default) {
        try {
            // Use the low 32 bits as the DAT file ID
            var datId = (uint)(id & 0xFFFFFFFFu);
            if (!_dats.TryResolvePreferred(
                    datId,
                    out IDatDatabase? db,
                    out DBObjType type))
                return null;

            if (type == DBObjType.Setup) {
                if (!db.TryGet<Setup>(datId, out var setup)) return null;
                return PrepareSetupMeshData(id, setup, ct);
            }
            else if (type == DBObjType.GfxObj) {
                if (!db.TryGet<GfxObj>(datId, out var gfxObj)) return null;
                return PrepareGfxObjMeshData(id, gfxObj, Vector3.One, ct);
            }
            else if (type == DBObjType.EnvCell) {
                if (!db.TryGet<EnvCell>(datId, out var envCell)) return null;

                if ((id & 0x1_0000_0000UL) != 0) {
                    uint envId = 0x0D000000u | envCell.EnvironmentId;
                    if (_dats.Portal.TryGet<DatReaderWriter.DBObjs.Environment>(envId, out var environment)) {
                        if (environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct)) {
                            return PrepareCellStructMeshData(id, cellStruct, envCell.Surfaces, Matrix4x4.Identity, ct);
                        }
                    }
                    return null;
                }

                return PrepareEnvCellMeshData(id, envCell, ct);
            }
            else if (type == DBObjType.Environment) {
                if (!db.TryGet<DatReaderWriter.DBObjs.Environment>(datId, out var environment)) return null;

                if (environment.Cells.Count > 0) {
                    var result = PrepareCellStructEdgeLineData(id, environment.Cells, Matrix4x4.Identity, ct);
                    return result;
                }
                return null;
            }
            return null;
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error preparing mesh data for 0x{Id:X16}", id);
            return null;
        }
    }

    private ObjectMeshData? PrepareSetupMeshData(ulong id, Setup setup, CancellationToken ct) {
        var parts = new List<(ulong GfxObjId, Matrix4x4 Transform)>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool hasBounds = false;

        CollectParts((uint)(id & 0xFFFFFFFFu), Matrix4x4.Identity, parts, ref min, ref max, ref hasBounds, ct);

        var emitters = new List<StagedEmitter>();
        var processedScripts = new HashSet<uint>();
        if (setup.DefaultScript.DataId != 0) {
            if (processedScripts.Add(setup.DefaultScript.DataId)) {
                CollectEmittersFromScript(setup.DefaultScript.DataId, emitters, ct);
            }
        }

        return new ObjectMeshData {
            ObjectId = id,
            IsSetup = true,
            SetupParts = parts,
            ParticleEmitters = emitters,
            BoundingBox = hasBounds ? new BoundingBox(min, max) : default,
            SelectionSphere = setup.SelectionSphere
        };
    }

    private void CollectEmittersFromScript(uint scriptId, List<StagedEmitter> emitters, CancellationToken ct) {
        var script = _physicsScripts.LoadPhysicsScript(scriptId);
        if (script is not null) {
            foreach (var hook in script.ScriptData) {
                if (hook.Hook is CreateParticleHook particleHook) {
                    if (_dats.Portal.TryGet<ParticleEmitter>(particleHook.EmitterInfoId.DataId, out var emitter)) {
                         emitters.Add(new StagedEmitter {
                             Emitter = emitter,
                             PartIndex = particleHook.PartIndex,
                             Offset = Matrix4x4.CreateFromQuaternion(particleHook.Offset.Orientation) * Matrix4x4.CreateTranslation(particleHook.Offset.Origin)
                         });

                         // Pre-load and stage the particle's GfxObjs
                         if (emitter.HwGfxObjId.DataId != 0) {
                             var meshData = PrepareMeshData(emitter.HwGfxObjId.DataId, false, ct);
                             if (meshData != null) {
                                 _sideStagedSink?.Invoke(meshData);
                             }
                         }
                         if (emitter.GfxObjId.DataId != 0 && emitter.GfxObjId.DataId != emitter.HwGfxObjId.DataId) {
                             var meshData = PrepareMeshData(emitter.GfxObjId.DataId, false, ct);
                             if (meshData != null) {
                                 _sideStagedSink?.Invoke(meshData);
                             }
                         }
                    }
                }
            }
        }
    }

    public void CollectParts(uint id, Matrix4x4 currentTransform, List<(ulong GfxObjId, Matrix4x4 Transform)> parts, ref Vector3 min, ref Vector3 max, ref bool hasBounds, CancellationToken ct, int depth = 0) {
        if (depth > 50) {
            _logger.LogWarning("Max recursion depth reached while collecting parts for 0x{Id:X8}. Possible circular dependency.", id);
            return;
        }
        ct.ThrowIfCancellationRequested();

        if (!_dats.TryResolvePreferred(
                id,
                out IDatDatabase? db,
                out DBObjType type))
            return;

        if (type == DBObjType.Setup) {
            if (!db.TryGet<Setup>(id, out var setup)) return;

            // Use Resting placement first, then default
            if (!setup.PlacementFrames.TryGetValue(Placement.Resting, out var placementFrame)) {
                if (!setup.PlacementFrames.TryGetValue(Placement.Default, out placementFrame)) {
                    placementFrame = setup.PlacementFrames.Values.FirstOrDefault();
                }
            }
            if (placementFrame == null) return;

            for (int i = 0; i < setup.Parts.Count; i++) {
                var partId = setup.Parts[i];
                var transform = Matrix4x4.Identity;

                if (setup.Flags.HasFlag(SetupFlags.HasDefaultScale) && setup.DefaultScale.Count > i) {
                    transform *= Matrix4x4.CreateScale(setup.DefaultScale[i]);
                }

                if (placementFrame.Frames != null && i < placementFrame.Frames.Count) {
                    var orientation = new System.Numerics.Quaternion(
                        (float)placementFrame.Frames[i].Orientation.X,
                        (float)placementFrame.Frames[i].Orientation.Y,
                        (float)placementFrame.Frames[i].Orientation.Z,
                        (float)placementFrame.Frames[i].Orientation.W
                    );
                    transform *= Matrix4x4.CreateFromQuaternion(orientation)
                        * Matrix4x4.CreateTranslation(placementFrame.Frames[i].Origin);
                }

                CollectParts(partId, transform * currentTransform, parts, ref min, ref max, ref hasBounds, ct, depth + 1);
            }
        }
        else if (type == DBObjType.EnvCell) {
            if (!db.TryGet<EnvCell>(id, out var envCell)) return;

            var cellOrientation = new System.Numerics.Quaternion(
                (float)envCell.Position.Orientation.X,
                (float)envCell.Position.Orientation.Y,
                (float)envCell.Position.Orientation.Z,
                (float)envCell.Position.Orientation.W
            );
            var cellTransform = Matrix4x4.CreateFromQuaternion(cellOrientation) *
                                Matrix4x4.CreateTranslation(envCell.Position.Origin);
            if (!Matrix4x4.Invert(cellTransform, out var invertCellTransform)) {
                invertCellTransform = Matrix4x4.Identity;
            }

            uint envId = 0x0D000000u | envCell.EnvironmentId;
            if (_dats.Portal.TryGet<DatReaderWriter.DBObjs.Environment>(envId, out var environment)) {
                if (environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct)) {
                    foreach (var vert in cellStruct.VertexArray.Vertices.Values) {
                        var transformed = Vector3.Transform(vert.Origin, currentTransform);
                        min = Vector3.Min(min, transformed);
                        max = Vector3.Max(max, transformed);
                    }
                    hasBounds = true;

                    // Add synthetic geometry ID to parts list
                    parts.Add(((ulong)id | 0x1_0000_0000UL, currentTransform));
                }
            }

            foreach (var stab in envCell.StaticObjects) {
                var orientation = new System.Numerics.Quaternion(
                    (float)stab.Frame.Orientation.X,
                    (float)stab.Frame.Orientation.Y,
                    (float)stab.Frame.Orientation.Z,
                    (float)stab.Frame.Orientation.W
                );
                var transform = Matrix4x4.CreateFromQuaternion(orientation)
                                * Matrix4x4.CreateTranslation(stab.Frame.Origin);
                var localizedTransform = transform * invertCellTransform;

                CollectParts(stab.Id, localizedTransform * currentTransform, parts, ref min, ref max, ref hasBounds, ct, depth + 1);
            }
        }
        else if (type == DBObjType.GfxObj) {
            parts.Add((id, currentTransform));

            if (db.TryGet<GfxObj>(id, out var partGfx)) {
                var (partMin, partMax) = ComputeBounds(partGfx, Vector3.One);
                var corners = new Vector3[8];
                corners[0] = new Vector3(partMin.X, partMin.Y, partMin.Z);
                corners[1] = new Vector3(partMin.X, partMin.Y, partMax.Z);
                corners[2] = new Vector3(partMin.X, partMax.Y, partMin.Z);
                corners[3] = new Vector3(partMin.X, partMax.Y, partMax.Z);
                corners[4] = new Vector3(partMax.X, partMin.Y, partMin.Z);
                corners[5] = new Vector3(partMax.X, partMin.Y, partMax.Z);
                corners[6] = new Vector3(partMax.X, partMax.Y, partMin.Z);
                corners[7] = new Vector3(partMax.X, partMax.Y, partMax.Z);

                foreach (var corner in corners) {
                    var transformed = Vector3.Transform(corner, currentTransform);
                    min = Vector3.Min(min, transformed);
                    max = Vector3.Max(max, transformed);
                }
                hasBounds = true;
            }
        }
    }

    private ObjectMeshData? PrepareGfxObjMeshData(ulong id, GfxObj gfxObj, Vector3 scale, CancellationToken ct) {
        var vertices = new List<VertexPositionNormalTexture>();
        var UVLookup = new Dictionary<(ushort vertId, ushort uvIdx, bool isNeg), ushort>();
        var batchesByFormat = new Dictionary<(int Width, int Height, TextureFormat Format), List<TextureBatchData>>();

        var (min, max) = ComputeBounds(gfxObj, scale);
        var boundingBox = new BoundingBox(min, max);

        foreach (var polyEntry in gfxObj.Polygons) {
            ct.ThrowIfCancellationRequested();
            var poly = polyEntry.Value;
            if (poly.VertexIds.Count < 3) continue;

            AddSurfaceToBatch(poly, poly.PosSurface, false);

            bool hasNeg = poly.Stippling.HasFlag(StipplingType.Negative) ||
                         poly.Stippling.HasFlag(StipplingType.Both) ||
                         (!poly.Stippling.HasFlag(StipplingType.NoNeg) && poly.SidesType == CullMode.Clockwise);

            if (hasNeg) {
                AddSurfaceToBatch(poly, poly.NegSurface, true);
            }

            void AddSurfaceToBatch(Polygon poly, short surfaceIdx, bool isNeg) {
                if (surfaceIdx < 0 || surfaceIdx >= gfxObj.Surfaces.Count) return;

                var surfaceId = gfxObj.Surfaces[surfaceIdx];
                if (!_dats.Portal.TryGet<Surface>(surfaceId, out var surface)) {
                    Console.WriteLine($"[tex-skip] gfxobj Surface 0x{surfaceId:X8} miss -> poly batch dropped (obj 0x{gfxObj.Id:X8})");
                    return;
                }

                int texWidth, texHeight;
                byte[] textureData;
                TextureFormat textureFormat;
                UploadPixelFormat? uploadPixelFormat = null;
                UploadPixelType? uploadPixelType = null;
                bool isSolid = RetailUntexturedSurfacePolicy.IsUntextured(surface.Type);
                bool isClipMap = surface.Type.HasFlag(SurfaceType.Base1ClipMap);
                uint paletteId = 0;
                bool texturePresent = false;
                bool isDxt3or5 = false;
                bool textureDataIsCached = false;
                DatReaderWriter.Enums.PixelFormat? sourceFormat = null;
                var isAdditive = false;
                var isTransparent = false;

                if (isSolid) {
                    texWidth = texHeight = 32;
                    textureData = GetOrCreateSolidColorTexture(surface.ColorValue, texWidth, texHeight);
                    textureFormat = TextureFormat.RGBA8;
                    uploadPixelFormat = UploadPixelFormat.Rgba;
                    textureDataIsCached = true;
                }
                else if (_dats.Portal.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfaceTexture)) {
                    texturePresent = true;
                    var renderSurfaceId = surfaceTexture.Textures.First();
                    if (!_dats.Portal.TryGet<RenderSurface>(renderSurfaceId, out var renderSurface)) {
                        if (!_dats.HighRes.TryGet<RenderSurface>(renderSurfaceId, out var hrRenderSurface)) {
                            throw new Exception($"Unable to load RenderSurface: 0x{renderSurfaceId:X8}");
                        }

                        renderSurface = hrRenderSurface;
                    }

                    texWidth = renderSurface.Width;
                    texHeight = renderSurface.Height;
                    paletteId = renderSurface.DefaultPaletteId;
                    sourceFormat = renderSurface.Format;
                    isDxt3or5 = renderSurface.Format is
                        DatReaderWriter.Enums.PixelFormat.PFID_DXT3 or
                        DatReaderWriter.Enums.PixelFormat.PFID_DXT5;
                    var decodedTextureKey = CreateDecodedTextureKey(
                        renderSurfaceId,
                        renderSurface.Format,
                        isClipMap,
                        surface.Type.HasFlag(SurfaceType.Additive));

                    if (CanPreserveCompressedTexture(
                            renderSurface.Format,
                            isClipMap,
                            surface.Translucency)) {
                        textureFormat = ToTextureFormat(renderSurface.Format);
                        textureData = renderSurface.SourceData;
                    }
                    else if (TextureHelpers.IsCompressedFormat(renderSurface.Format)) {
                        isDxt3or5 = renderSurface.Format == DatReaderWriter.Enums.PixelFormat.PFID_DXT3 || renderSurface.Format == DatReaderWriter.Enums.PixelFormat.PFID_DXT5;
                        textureFormat = TextureFormat.RGBA8;
                        uploadPixelFormat = UploadPixelFormat.Rgba;

                        textureData = _decodedTextureCache.GetOrCreate(
                            decodedTextureKey,
                            () => DecodeCompressedTexture(renderSurface, texWidth, texHeight),
                            out textureDataIsCached);

                        if (isClipMap && textureData != null) {
                            if (textureDataIsCached) {
                                var clonedData = new byte[textureData.Length];
                                System.Buffer.BlockCopy(textureData, 0, clonedData, 0, textureData.Length);
                                textureData = clonedData;
                                textureDataIsCached = false;
                            }

                            for (int i = 0; i < textureData.Length; i += 4) {
                                if (textureData[i] == 0 && textureData[i + 1] == 0 && textureData[i + 2] == 0) {
                                    textureData[i + 3] = 0;
                                }
                            }
                        }
                    }
                    else {
                        textureFormat = TextureFormat.RGBA8;
                        uploadPixelFormat = UploadPixelFormat.Rgba;
                        if (_decodedTextureCache.TryGet(
                                decodedTextureKey,
                                out byte[] cachedPixels)) {
                            textureData = cachedPixels;
                            textureDataIsCached = true;
                        }
                        else {
                            textureData = renderSurface.SourceData;
                            switch (renderSurface.Format) {
                            case DatReaderWriter.Enums.PixelFormat.PFID_A8R8G8B8:
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillA8R8G8B8(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_R8G8B8:
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillR8G8B8(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_INDEX16:
                                if (!_dats.Portal.TryGet<Palette>(renderSurface.DefaultPaletteId, out var paletteData))
                                    throw new Exception($"Unable to load Palette: 0x{renderSurface.DefaultPaletteId:X8}");
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillIndex16(renderSurface.SourceData, paletteData, textureData.AsSpan(), texWidth, texHeight, isClipMap);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_P8:
                                if (!_dats.Portal.TryGet<Palette>(renderSurface.DefaultPaletteId, out var p8PaletteData))
                                    throw new Exception($"Unable to load Palette: 0x{renderSurface.DefaultPaletteId:X8}");
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillP8(renderSurface.SourceData, p8PaletteData, textureData.AsSpan(), texWidth, texHeight, isClipMap);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_R5G6B5:
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillR5G6B5(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_A4R4G4B4:
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillA4R4G4B4(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_A8:
                            case DatReaderWriter.Enums.PixelFormat.PFID_CUSTOM_LSCAPE_ALPHA:
                                textureData = new byte[texWidth * texHeight * 4];
                                if (surface.Type.HasFlag(SurfaceType.Additive)) {
                                    TextureHelpers.FillA8Additive(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                }
                                else {
                                    TextureHelpers.FillA8(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                }
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            default:
                                throw new NotSupportedException($"Unsupported surface format: {renderSurface.Format}");
                            }

                            textureData = _decodedTextureCache.RetainOrUse(
                                decodedTextureKey,
                                textureData,
                                out textureDataIsCached);
                        }
                    }

                    if (surface.Translucency > 0.0f && textureData != null) {
                        if (textureDataIsCached) {
                            var clonedData = new byte[textureData.Length];
                            System.Buffer.BlockCopy(textureData, 0, clonedData, 0, textureData.Length);
                            textureData = clonedData;
                        }

                        float alphaScale = 1.0f - surface.Translucency;
                        for (int i = 3; i < textureData.Length; i += 4) {
                            textureData[i] = (byte)(textureData[i] * alphaScale);
                        }
                    }

                    isAdditive = !isSolid && surface.Type.HasFlag(SurfaceType.Additive);
                    isTransparent = isSolid ? surface.ColorValue.Alpha < 255 :
                        (surface.Type.HasFlag(SurfaceType.Translucent) ||
                         surface.Type.HasFlag(SurfaceType.Base1ClipMap) ||
                         ((uint)surface.Type & 0x100) != 0 || // Alpha
                         ((uint)surface.Type & 0x200) != 0 || // InvAlpha
                         isAdditive ||
                         (surface.Translucency > 0.0f && surface.Translucency < 1.0f) ||
                         textureFormat == TextureFormat.A8 ||
                         textureFormat == TextureFormat.Rgba32f ||
                         isDxt3or5 ||
                         (sourceFormat != null && (sourceFormat == DatReaderWriter.Enums.PixelFormat.PFID_A8R8G8B8 ||
                                                     sourceFormat == DatReaderWriter.Enums.PixelFormat.PFID_A4R4G4B4 ||
                                                     sourceFormat == DatReaderWriter.Enums.PixelFormat.PFID_DXT3 ||
                                                     sourceFormat == DatReaderWriter.Enums.PixelFormat.PFID_DXT5)));
                }
                else {
                    Console.WriteLine($"[tex-skip] gfxobj SurfaceTexture 0x{surface.OrigTextureId:X8} miss -> poly batch dropped (surface 0x{surfaceId:X8})");
                    return;
                }

                var format = (texWidth, texHeight, textureFormat);
                var key = new TextureKey {
                    SurfaceId = surfaceId,
                    PaletteId = paletteId,
                    Stippling = poly.Stippling,
                    IsSolid = isSolid
                };

                if (!batchesByFormat.TryGetValue(format, out var batches)) {
                    batches = new List<TextureBatchData>();
                    batchesByFormat[format] = batches;
                }

                var batch = batches.FirstOrDefault(b => b.Key.Equals(key) && b.CullMode == poly.SidesType);
                if (batch == null) {
                    batch = new TextureBatchData {
                        Key = key,
                        CullMode = poly.SidesType,
                        TextureData = textureData!,
                        UploadPixelFormat = uploadPixelFormat,
                        UploadPixelType = uploadPixelType,
                        Translucency =
                            TranslucencyKindExtensions.FromSurfaceType(
                                surface.Type),
                        SurfaceOpacity =
                            TranslucencyKindExtensions.OpacityFromSurfaceTranslucency(
                                surface.Type,
                                surface.Translucency),
                        MaterialState = RetailSetSurfaceMaterialState.Resolve(
                            surface.Type,
                            texturePresent,
                            paletteId != 0),
                        IsTransparent = isTransparent,
                        IsAdditive = isAdditive
                    };
                    batches.Add(batch);
                }

                bool batchHasWrappingUVs = batch.HasWrappingUVs;
                BuildPolygonIndices(poly, gfxObj, scale, UVLookup, vertices, batch.Indices, isNeg, ref batchHasWrappingUVs);
                batch.HasWrappingUVs = batchHasWrappingUVs;
            }
        }

        return new ObjectMeshData {
            ObjectId = id,
            IsSetup = false,
            Vertices = vertices.ToArray(),
            TextureBatches = batchesByFormat,
            BoundingBox = boundingBox,
            SortCenter = gfxObj?.SortCenter ?? Vector3.Zero,
            DIDDegrade = gfxObj != null && gfxObj.Flags.HasFlag(GfxObjFlags.HasDIDDegrade) ? gfxObj.DIDDegrade : 0,
            SelectionSphere = gfxObj?.DrawingBSP?.Root?.BoundingSphere
                ?? new Sphere
                {
                    Origin = boundingBox.Center,
                    Radius = Vector3.Distance(
                        boundingBox.Max,
                        boundingBox.Min) / 2.0f,
                }
        };
    }

    private ObjectMeshData? PrepareEnvCellMeshData(ulong id, EnvCell envCell, CancellationToken ct) {
        var parts = new List<(ulong GfxObjId, Matrix4x4 Transform)>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool hasBounds = false;

        var cellOrientation = new System.Numerics.Quaternion(
            (float)envCell.Position.Orientation.X,
            (float)envCell.Position.Orientation.Y,
            (float)envCell.Position.Orientation.Z,
            (float)envCell.Position.Orientation.W
        );
        var cellTransform = Matrix4x4.CreateFromQuaternion(cellOrientation) *
                            Matrix4x4.CreateTranslation(envCell.Position.Origin);
        if (!Matrix4x4.Invert(cellTransform, out var invertCellTransform)) {
            invertCellTransform = Matrix4x4.Identity;
        }

        // Add static objects
        var emitters = new List<StagedEmitter>();
        foreach (var stab in envCell.StaticObjects) {
            var orientation = new System.Numerics.Quaternion(
                (float)stab.Frame.Orientation.X,
                (float)stab.Frame.Orientation.Y,
                (float)stab.Frame.Orientation.Z,
                (float)stab.Frame.Orientation.W
            );
            var transform = Matrix4x4.CreateFromQuaternion(orientation)
                            * Matrix4x4.CreateTranslation(stab.Frame.Origin);

            var localizedTransform = transform * invertCellTransform;

            CollectParts(stab.Id, localizedTransform, parts, ref min, ref max, ref hasBounds, ct);

            if ((stab.Id & 0xFF000000u) == 0x02000000u
                && _dats.Portal.TryGet<Setup>(stab.Id, out var stabSetup)) {
                var stabEmitters = new List<StagedEmitter>();
                var processedScripts = new HashSet<uint>();
                if (stabSetup.DefaultScript.DataId != 0) {
                    if (processedScripts.Add(stabSetup.DefaultScript.DataId)) {
                        CollectEmittersFromScript(stabSetup.DefaultScript.DataId, stabEmitters, ct);
                    }
                }

                foreach (var emitter in stabEmitters) {
                    emitters.Add(new StagedEmitter {
                        Emitter = emitter.Emitter,
                        PartIndex = emitter.PartIndex,
                        Offset = emitter.Offset * localizedTransform
                    });
                }
            }
        }

        uint envId = 0x0D000000u | envCell.EnvironmentId;
        ObjectMeshData? cellGeometry = null;
        if (_dats.Portal.TryGet<DatReaderWriter.DBObjs.Environment>(envId, out var environment)) {
            if (environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct)) {
                var cellGeomId = id | 0x1_0000_0000UL;
                cellGeometry = PrepareCellStructMeshData(cellGeomId, cellStruct, envCell.Surfaces, Matrix4x4.Identity, ct);
                if (cellGeometry != null) {
                    parts.Add((cellGeomId, Matrix4x4.Identity));
                    min = Vector3.Min(min, cellGeometry.BoundingBox.Min);
                    max = Vector3.Max(max, cellGeometry.BoundingBox.Max);
                    hasBounds = true;
                }
            }
        }

        return new ObjectMeshData {
            ObjectId = id,
            IsSetup = true,
            SetupParts = parts,
            ParticleEmitters = emitters,
            EnvCellGeometry = cellGeometry,
            BoundingBox = hasBounds ? new BoundingBox(min, max) : default,
            SelectionSphere = new Sphere { Origin = hasBounds ? (min + max) / 2f : Vector3.Zero, Radius = hasBounds ? Vector3.Distance(max, min) / 2.0f : 0f }
        };
    }

    private sealed class CellSurfaceSlot {
        public CellSurfaceSlot(Surface surface, uint surfaceId) {
            Surface = surface;
            SurfaceId = surfaceId;
        }

        public Surface Surface { get; }
        public uint SurfaceId { get; }

        public bool IsUntextured => RetailUntexturedSurfacePolicy.IsUntextured(Surface.Type);

        public bool BatchResolutionAttempted;

        public int Mask;

        public TextureBatchData? Batch;

        public (int Width, int Height, TextureFormat Format) Format;
    }

    public ObjectMeshData? PrepareCellStructMeshData(ulong id, CellStruct cellStruct, IReadOnlyList<ushort> surfaceOverrides, Matrix4x4 transform, CancellationToken ct) {
        var vertices = new List<VertexPositionNormalTexture>();
        var vertexLookup = new Dictionary<(ushort vertId, ushort uvIdx, bool negativeLane), ushort>();
        var batchesByFormat = new Dictionary<(int Width, int Height, TextureFormat Format), List<TextureBatchData>>();
        var slots = new Dictionary<int, CellSurfaceSlot?>();

        CellSurfaceSlot? GetOrCreateSlot(int slot) {
            if (slots.TryGetValue(slot, out var existing))
                return existing;
            CellSurfaceSlot? created = null;
            if (TryResolveSlot(slot, out var surface, out var surfaceId)) {
                created = new CellSurfaceSlot(surface, surfaceId) {
                    Mask = CellStructSideCandidates.InitialSurfaceMask(surface.Type),
                };
            }
            slots[slot] = created;
            return created;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vert in cellStruct.VertexArray.Vertices.Values) {
            var localizedPos = Vector3.Transform(vert.Origin, transform);
            min = Vector3.Min(min, localizedPos);
            max = Vector3.Max(max, localizedPos);
        }
        var boundingBox = new BoundingBox(min, max);

        int unknownSidesTypePolygons = 0;

        bool TryResolveSlot(int slot, out Surface surface, out uint surfaceId) {
            if (slot < surfaceOverrides.Count) {
                surfaceId = 0x08000000u | surfaceOverrides[slot];
            }
            else {
                surface = default!;
                surfaceId = 0;
                _logger.LogWarning($"Failed to find surface override for index {slot} in CellStruct id=0x{id:X16}");
                return false;
            }

            if (!_dats.Portal.TryGet<Surface>(surfaceId, out surface!)) {
                Console.WriteLine($"[tex-skip] cellstruct Surface 0x{surfaceId:X8} miss -> slot {slot} dropped (cellstruct id=0x{id:X16})");
                return false;
            }
            return true;
        }

        void ResolveSlotBatch(CellSurfaceSlot state) {
            state.BatchResolutionAttempted = true;
            Surface surface = state.Surface;
            uint surfaceId = state.SurfaceId;

            int texWidth, texHeight;
            byte[] textureData;
            TextureFormat textureFormat;
            UploadPixelFormat? uploadPixelFormat = null;
            UploadPixelType? uploadPixelType = null;
            bool isSolid = RetailUntexturedSurfacePolicy.IsUntextured(surface.Type);
            bool isClipMap = surface.Type.HasFlag(SurfaceType.Base1ClipMap);
            uint paletteId = 0;
            bool texturePresent = false;
            bool isDxt3or5 = false;
            bool textureDataIsCached = false;
            DatReaderWriter.Enums.PixelFormat? sourceFormat = null;
            var isAdditive = false;
            var isTransparent = false;

            if (isSolid) {
                texWidth = texHeight = 32;
                textureData = GetOrCreateSolidColorTexture(surface.ColorValue, texWidth, texHeight);
                textureFormat = TextureFormat.RGBA8;
                uploadPixelFormat = UploadPixelFormat.Rgba;
                textureDataIsCached = true;
            }
            else if (_dats.Portal.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfaceTexture)) {
                texturePresent = true;
                var renderSurfaceId = surfaceTexture.Textures.First();
                if (!_dats.Portal.TryGet<RenderSurface>(renderSurfaceId, out var renderSurface)) {
                    if (!_dats.HighRes.TryGet<RenderSurface>(renderSurfaceId, out var hrRenderSurface)) {
                        Console.WriteLine($"[tex-skip] cellstruct RenderSurface 0x{renderSurfaceId:X8} miss (portal+highres) -> WALL poly batch dropped");
                        return;
                    }
                    renderSurface = hrRenderSurface;
                }

                texWidth = renderSurface.Width;
                texHeight = renderSurface.Height;
                paletteId = renderSurface.DefaultPaletteId;
                sourceFormat = renderSurface.Format;
                isDxt3or5 = renderSurface.Format is
                    DatReaderWriter.Enums.PixelFormat.PFID_DXT3 or
                    DatReaderWriter.Enums.PixelFormat.PFID_DXT5;
                var decodedTextureKey = CreateDecodedTextureKey(
                    renderSurfaceId,
                    renderSurface.Format,
                    isClipMap,
                    surface.Type.HasFlag(SurfaceType.Additive));

                if (CanPreserveCompressedTexture(
                        renderSurface.Format,
                        isClipMap,
                        surface.Translucency)) {
                    textureData = renderSurface.SourceData;
                    textureFormat = ToTextureFormat(renderSurface.Format);
                }
                else if (_decodedTextureCache.TryGet(decodedTextureKey, out var cachedData)) {
                    textureData = cachedData;
                    textureFormat = TextureFormat.RGBA8;
                    uploadPixelFormat = UploadPixelFormat.Rgba;
                    textureDataIsCached = true;
                }
                else {
                    if (TextureHelpers.IsCompressedFormat(renderSurface.Format)) {
                        isDxt3or5 = renderSurface.Format == DatReaderWriter.Enums.PixelFormat.PFID_DXT3 || renderSurface.Format == DatReaderWriter.Enums.PixelFormat.PFID_DXT5;
                        textureFormat = TextureFormat.RGBA8;
                        uploadPixelFormat = UploadPixelFormat.Rgba;

                        textureData = _decodedTextureCache.GetOrCreate(
                            decodedTextureKey,
                            () => DecodeCompressedTexture(renderSurface, texWidth, texHeight),
                            out textureDataIsCached);
                    }
                    else {
                        textureFormat = TextureFormat.RGBA8;
                        textureData = renderSurface.SourceData;
                        switch (renderSurface.Format) {
                            case DatReaderWriter.Enums.PixelFormat.PFID_A8R8G8B8:
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillA8R8G8B8(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_R8G8B8:
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillR8G8B8(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_INDEX16:
                                if (!_dats.Portal.TryGet<Palette>(renderSurface.DefaultPaletteId, out var paletteData)) return;
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillIndex16(renderSurface.SourceData, paletteData, textureData.AsSpan(), texWidth, texHeight, isClipMap);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_P8:
                                if (!_dats.Portal.TryGet<Palette>(renderSurface.DefaultPaletteId, out var p8PaletteData)) return;
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillP8(renderSurface.SourceData, p8PaletteData, textureData.AsSpan(), texWidth, texHeight, isClipMap);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_R5G6B5:
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillR5G6B5(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_A4R4G4B4:
                                textureData = new byte[texWidth * texHeight * 4];
                                TextureHelpers.FillA4R4G4B4(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            case DatReaderWriter.Enums.PixelFormat.PFID_A8:
                            case DatReaderWriter.Enums.PixelFormat.PFID_CUSTOM_LSCAPE_ALPHA:
                                textureData = new byte[texWidth * texHeight * 4];
                                if (surface.Type.HasFlag(SurfaceType.Additive)) {
                                    TextureHelpers.FillA8Additive(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                }
                                else {
                                    TextureHelpers.FillA8(renderSurface.SourceData, textureData.AsSpan(), texWidth, texHeight);
                                }
                                uploadPixelFormat = UploadPixelFormat.Rgba;
                                break;
                            default: return;
                        }
                    }

                    if (!TextureHelpers.IsCompressedFormat(renderSurface.Format))
                    {
                        textureData = _decodedTextureCache.RetainOrUse(
                            decodedTextureKey,
                            textureData,
                            out textureDataIsCached);
                    }
                }

                if (isClipMap && textureData != null) {
                    if (textureDataIsCached) {
                        var clonedData = new byte[textureData.Length];
                        System.Buffer.BlockCopy(textureData, 0, clonedData, 0, textureData.Length);
                        textureData = clonedData;
                        textureDataIsCached = false;
                    }

                    for (int i = 0; i < textureData.Length; i += 4) {
                        if (textureData[i] == 0 && textureData[i + 1] == 0 && textureData[i + 2] == 0) {
                            textureData[i + 3] = 0;
                        }
                    }
                }
            }
            else {
                Console.WriteLine($"[tex-skip] cellstruct SurfaceTexture 0x{surface.OrigTextureId:X8} miss -> WALL poly batch dropped (surface 0x{surfaceId:X8})");
                return;
            }

            isAdditive = !isSolid && surface.Type.HasFlag(SurfaceType.Additive);
            isTransparent = isSolid ? surface.ColorValue.Alpha < 255 :
                (surface.Type.HasFlag(SurfaceType.Translucent) ||
                 surface.Type.HasFlag(SurfaceType.Base1ClipMap) ||
                 ((uint)surface.Type & 0x100) != 0 || // Alpha
                 ((uint)surface.Type & 0x200) != 0 || // InvAlpha
                 isAdditive ||
                 (surface.Translucency > 0.0f && surface.Translucency < 1.0f) ||
                 textureFormat == TextureFormat.A8 ||
                 textureFormat == TextureFormat.Rgba32f ||
                 isDxt3or5 ||
                 (sourceFormat != null && (sourceFormat == DatReaderWriter.Enums.PixelFormat.PFID_A8R8G8B8 ||
                                             sourceFormat == DatReaderWriter.Enums.PixelFormat.PFID_A4R4G4B4 ||
                                             sourceFormat == DatReaderWriter.Enums.PixelFormat.PFID_DXT3 ||
                                             sourceFormat == DatReaderWriter.Enums.PixelFormat.PFID_DXT5)));

            state.Batch = new TextureBatchData {
                Key = new TextureKey {
                    SurfaceId = surfaceId,
                    PaletteId = paletteId,
                    Stippling = StipplingType.None,
                    IsSolid = isSolid,
                },
                TextureData = textureData!,
                UploadPixelFormat = uploadPixelFormat,
                UploadPixelType = uploadPixelType,
                Translucency = TranslucencyKindExtensions.FromSurfaceType(surface.Type),
                SurfaceOpacity =
                    TranslucencyKindExtensions.OpacityFromSurfaceTranslucency(
                        surface.Type,
                        surface.Translucency),
                MaterialState = RetailSetSurfaceMaterialState.Resolve(
                    surface.Type,
                    texturePresent,
                    paletteId != 0),
                IsTransparent = isTransparent,
                IsAdditive = isAdditive,
            };
            state.Format = (texWidth, texHeight, textureFormat);
        }

        foreach (var poly in cellStruct.Polygons.Values) {
            ct.ThrowIfCancellationRequested();

            if (poly.PosSurface >= 0
                && GetOrCreateSlot(poly.PosSurface) is { } positiveSlot) {
                positiveSlot.Mask = CellStructSideCandidates.ApplyStipplingMaskBit(
                    positiveSlot.Mask, CellStructPolygonSurfaceSide.Positive, poly.Stippling);
            }

            if (poly.VertexIds.Count < 3) continue;

            int rawSidesType = (int)poly.SidesType;
            if (!CellStructSideCandidates.IsRetailDefinedSidesType(rawSidesType))
                unknownSidesTypePolygons++;
            ReadOnlySpan<CellStructSideCandidate> candidates =
                CellStructSideCandidates.GetCandidates(rawSidesType);

            foreach (var candidate in candidates) {
                short surfaceIdxRaw = candidate.SurfaceSlot == CellStructPolygonSurfaceSide.Positive
                    ? poly.PosSurface
                    : poly.NegSurface;
                if (surfaceIdxRaw < 0) continue;

                var slotState = GetOrCreateSlot(surfaceIdxRaw);
                if (slotState is null) continue; // override/Surface lookup failed; logged once per slot.

                if (slotState.IsUntextured) continue;

                if (!slotState.BatchResolutionAttempted) {
                    ResolveSlotBatch(slotState);
                }
                if (slotState.Batch is null) continue; // texture resolution failed a dependency lookup; logged once per slot.

                bool useNegUv = candidate.UvSlot == CellStructPolygonSurfaceSide.Negative;
                bool invertNormal = candidate.NormalSign < 0;
                bool uvAbsent = CellStructSideCandidates.IsUvAbsent(candidate, poly.Stippling);

                bool hasWrappingUVs = slotState.Batch.HasWrappingUVs;
                BuildCellStructPolygonIndices(
                    poly,
                    cellStruct,
                    vertexLookup,
                    vertices,
                    slotState.Batch.Indices,
                    useNegUv,
                    uvAbsent,
                    invertNormal,
                    candidate.ReverseWinding,
                    transform,
                    ref hasWrappingUVs);
                slotState.Batch.HasWrappingUVs = hasWrappingUVs;
            }
        }

        int skippedUntexturedSlots = 0;
        foreach (var slot in slots.Keys.OrderBy(s => s)) {
            var state = slots[slot];
            if (state is null) continue;

            if (state.IsUntextured) {
                skippedUntexturedSlots++;
                continue;
            }
            if (state.Batch is null) continue;

            var batch = state.Batch;
            batch.SourceSurfaceIndex = slot;
            batch.RawSurfaceType = (uint)state.Surface.Type;
            batch.RetailSurfaceMask = (byte)state.Mask;
            batch.IsCellShell = true;
            batch.CullMode = CullMode.Clockwise;

            if (!batchesByFormat.TryGetValue(state.Format, out var list)) {
                list = new List<TextureBatchData>();
                batchesByFormat[state.Format] = list;
            }
            list.Add(batch);
        }

        if (unknownSidesTypePolygons > 0) {
            _logger.LogWarning(
                "CellStruct id=0x{Id:X16}: {Count} polygon(s) had a raw sides_type outside 0/1/2 (OH2 contract §3.4); they were constructed as retail's default single-side shape.",
                id, unknownSidesTypePolygons);
        }
        if (skippedUntexturedSlots > 0) {
            _logger.LogDebug(
                "CellStruct id=0x{Id:X16}: {Count} surface slot(s) constructed but not emitted — untextured under retail's built-EnvCell admission (Surface.Type & 6) == 0 (OH2 contract §4).",
                id, skippedUntexturedSlots);
        }

        return new ObjectMeshData {
            ObjectId = id,
            IsSetup = false,
            Vertices = vertices.ToArray(),
            TextureBatches = batchesByFormat,
            BoundingBox = boundingBox,
            SortCenter = Vector3.Zero,
            SelectionSphere = new Sphere { Origin = boundingBox.Center, Radius = Vector3.Distance(boundingBox.Max, boundingBox.Min) / 2.0f }
        };
    }

    private void BuildCellStructPolygonIndices(Polygon poly, CellStruct cellStruct,
        Dictionary<(ushort vertId, ushort uvIdx, bool negativeLane), ushort> vertexLookup,
        List<VertexPositionNormalTexture> vertices, List<ushort> indices,
        bool useNegUv, bool uvAbsent, bool invertNormal, bool reverseWinding,
        Matrix4x4 transform, ref bool hasWrappingUVs) {

        var polyIndices = new List<ushort>();

        for (int i = 0; i < poly.VertexIds.Count; i++) {
            ushort vertId = (ushort)poly.VertexIds[i];

            int uvIdxSigned = 0;
            if (!uvAbsent) {
                if (useNegUv && poly.NegUVIndices != null && i < poly.NegUVIndices.Count)
                    uvIdxSigned = unchecked((sbyte)(byte)poly.NegUVIndices[i]);
                else if (!useNegUv && poly.PosUVIndices != null && i < poly.PosUVIndices.Count)
                    uvIdxSigned = unchecked((sbyte)(byte)poly.PosUVIndices[i]);
            }

            if (!cellStruct.VertexArray.Vertices.TryGetValue(vertId, out var vertex)) continue;

            bool uvInRange = uvIdxSigned >= 0 && uvIdxSigned < vertex.UVs.Count;
            Vector2 uv = uvInRange
                ? new Vector2(vertex.UVs[uvIdxSigned].U, vertex.UVs[uvIdxSigned].V)
                : Vector2.Zero;

            ushort uvKey = uvIdxSigned >= 0 ? (ushort)uvIdxSigned : ushort.MaxValue;
            var key = (vertId, uvKey, invertNormal);

            if (!hasWrappingUVs && uvInRange) {
                if (uv.X < 0f || uv.X > 1f || uv.Y < 0f || uv.Y > 1f) {
                    hasWrappingUVs = true;
                }
            }

            if (!vertexLookup.TryGetValue(key, out var idx)) {

                var normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, transform));
                if (invertNormal) {
                    normal = -normal;
                }

                idx = (ushort)vertices.Count;
                vertices.Add(new VertexPositionNormalTexture(
                    Vector3.Transform(vertex.Origin, transform),
                    normal,
                    uv
                ));
                vertexLookup[key] = idx;
            }
            polyIndices.Add(idx);
        }

        int triangleCount = polyIndices.Count - 2;
        for (int t = 0; t < triangleCount; t++) {
            var (a, b, c) = CellStructSideCandidates.TriangleFanIndices(t, reverseWinding);
            indices.Add(polyIndices[a]);
            indices.Add(polyIndices[b]);
            indices.Add(polyIndices[c]);
        }
    }

    internal byte[] GetOrCreateSolidColorTexture(DatReaderWriter.Types.ColorARGB color, int width, int height) {
        uint key = ((uint)color.Alpha << 24) | ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;
        return _solidColorTextureCache.GetOrAdd(
            key,
            _ => TextureHelpers.CreateSolidColorTexture(color, width, height));
    }

    private static DecodedTextureKey CreateDecodedTextureKey(
        uint renderSurfaceId,
        DatReaderWriter.Enums.PixelFormat format,
        bool isClipMap,
        bool isAdditive)
    {
        bool clipAffectsDecode = format is
            DatReaderWriter.Enums.PixelFormat.PFID_INDEX16 or
            DatReaderWriter.Enums.PixelFormat.PFID_P8;
        bool additiveAffectsDecode = format is
            DatReaderWriter.Enums.PixelFormat.PFID_A8 or
            DatReaderWriter.Enums.PixelFormat.PFID_CUSTOM_LSCAPE_ALPHA;
        return new DecodedTextureKey(
            renderSurfaceId,
            clipAffectsDecode && isClipMap,
            additiveAffectsDecode && isAdditive);
    }

    private byte[] DecodeCompressedTexture(
        RenderSurface renderSurface,
        int width,
        int height)
    {
        var textureData = new byte[width * height * 4];
        CompressionFormat compressionFormat = renderSurface.Format switch
        {
            DatReaderWriter.Enums.PixelFormat.PFID_DXT1 => CompressionFormat.Bc1,
            DatReaderWriter.Enums.PixelFormat.PFID_DXT3 => CompressionFormat.Bc2,
            DatReaderWriter.Enums.PixelFormat.PFID_DXT5 => CompressionFormat.Bc3,
            _ => throw new NotSupportedException(
                $"Unsupported compressed format: {renderSurface.Format}"),
        };

        using var image = _bcDecoder.Value!.DecodeRawToImageRgba32(
            renderSurface.SourceData,
            width,
            height,
            compressionFormat);
        image.CopyPixelDataTo(textureData);
        return textureData;
    }

    private static bool CanPreserveCompressedTexture(
        DatReaderWriter.Enums.PixelFormat format,
        bool isClipMap,
        float translucency) =>
        TextureHelpers.IsCompressedFormat(format)
        && !isClipMap
        && translucency <= 0.0f;

    private static TextureFormat ToTextureFormat(
        DatReaderWriter.Enums.PixelFormat format) => format switch
        {
            DatReaderWriter.Enums.PixelFormat.PFID_DXT1 => TextureFormat.DXT1,
            DatReaderWriter.Enums.PixelFormat.PFID_DXT3 => TextureFormat.DXT3,
            DatReaderWriter.Enums.PixelFormat.PFID_DXT5 => TextureFormat.DXT5,
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "The source is not a supported block-compressed texture."),
        };

    private void BuildPolygonIndices(Polygon poly, GfxObj gfxObj, Vector3 scale,
        Dictionary<(ushort vertId, ushort uvIdx, bool isNeg), ushort> UVLookup,
        List<VertexPositionNormalTexture> vertices, List<ushort> indices, bool useNegSurface, ref bool hasWrappingUVs) {

        var polyIndices = new List<ushort>();

        for (int i = 0; i < poly.VertexIds.Count; i++) {
            ushort vertId = (ushort)poly.VertexIds[i];
            ushort uvIdx = 0;

            if (useNegSurface && poly.NegUVIndices != null && i < poly.NegUVIndices.Count)
                uvIdx = poly.NegUVIndices[i];
            else if (!useNegSurface && poly.PosUVIndices != null && i < poly.PosUVIndices.Count)
                uvIdx = poly.PosUVIndices[i];

            if (!gfxObj.VertexArray.Vertices.TryGetValue(vertId, out var vertex)) continue;

            if (uvIdx >= vertex.UVs.Count) {
                uvIdx = 0;
            }

            var key = (vertId, uvIdx, useNegSurface);

            if (!hasWrappingUVs) {
                var uvCheck = vertex.UVs.Count > 0
                    ? new Vector2(vertex.UVs[uvIdx].U, vertex.UVs[uvIdx].V)
                    : Vector2.Zero;
                if (uvCheck.X < 0f || uvCheck.X > 1f || uvCheck.Y < 0f || uvCheck.Y > 1f) {
                    hasWrappingUVs = true;
                }
            }

            if (!UVLookup.TryGetValue(key, out var idx)) {
                var uv = vertex.UVs.Count > 0
                    ? new Vector2(vertex.UVs[uvIdx].U, vertex.UVs[uvIdx].V)
                    : Vector2.Zero;

                var normal = Vector3.Normalize(vertex.Normal);
                if (useNegSurface) {
                    normal = -normal;
                }

                idx = (ushort)vertices.Count;
                vertices.Add(new VertexPositionNormalTexture(
                    vertex.Origin * scale,
                    normal,
                    uv
                ));
                UVLookup[key] = idx;
            }
            polyIndices.Add(idx);
        }

        if (useNegSurface) {
            // Reverse winding for negative surface so it's visible from the other side
            for (int i = 2; i < polyIndices.Count; i++) {
                indices.Add(polyIndices[0]);
                indices.Add(polyIndices[i - 1]);
                indices.Add(polyIndices[i]);
            }
        }
        else {
            for (int i = 2; i < polyIndices.Count; i++) {
                indices.Add(polyIndices[i]);
                indices.Add(polyIndices[i - 1]);
                indices.Add(polyIndices[0]);
            }
        }
    }

    public (Vector3 Min, Vector3 Max) ComputeBounds(GfxObj gfxObj, Vector3 scale) {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vert in gfxObj.VertexArray.Vertices.Values) {
            var p = vert.Origin * scale;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    private ObjectMeshData? PrepareCellStructEdgeLineData(ulong id, Dictionary<uint, CellStruct> cellStructs, Matrix4x4 transform, CancellationToken ct) {
        var cellStructList = cellStructs.ToList();
        if (cellStructList.Count == 0) {
            return null;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var allEdgeLines = new List<Vector3>();

        foreach (var cellStructKvp in cellStructList) {
            var cellStruct = cellStructKvp.Value;

            var edgeLines = EdgeLineBuilder.BuildEdgeLines(cellStruct);

            foreach (var edgeLine in edgeLines) {
                allEdgeLines.Add(Vector3.Transform(edgeLine, transform));
            }

            foreach (var vert in cellStruct.VertexArray.Vertices.Values) {
                var localizedPos = Vector3.Transform(vert.Origin, transform);
                min = Vector3.Min(min, localizedPos);
                max = Vector3.Max(max, localizedPos);
            }
        }

        if (allEdgeLines.Count == 0) {
            return null;
        }

        var boundingBox = new BoundingBox(min, max);

        var vertices = new List<VertexPositionNormalTexture> {
            new VertexPositionNormalTexture { Position = Vector3.Zero, Normal = Vector3.UnitZ, UV = Vector2.Zero }
        };
        var indices = new List<ushort> { 0, 0, 0 }; // Dummy triangle

        var transparentTexture = TextureHelpers.CreateSolidColorTexture(new ColorARGB { Alpha = 0, Red = 255, Green = 255, Blue = 255 }, 1, 1);

        var result = new ObjectMeshData {
            ObjectId = id,
            IsSetup = false,
            Vertices = vertices.ToArray(),
            Batches = new List<MeshBatchData> {
                new MeshBatchData {
                    Indices = indices.ToArray(),
                    TextureFormat = (1, 1, TextureFormat.RGBA8),
                    TextureKey = new TextureKey {
                        SurfaceId = 0xFFFFFFFF, // Dummy surface ID
                        PaletteId = 0,
                        Stippling = StipplingType.NoPos,
                        IsSolid = true
                    },
                    TextureIndex = 0,
                    TextureData = transparentTexture,
                    UploadPixelFormat = UploadPixelFormat.Rgba,
                    UploadPixelType = UploadPixelType.UnsignedByte,
                    CullMode = CullMode.None
                }
            },
            // Also populate TextureBatches for GPU upload
            TextureBatches = new Dictionary<(int Width, int Height, TextureFormat Format), List<TextureBatchData>> {
                [(1, 1, TextureFormat.RGBA8)] = new List<TextureBatchData> {
                    new TextureBatchData {
                        Indices = indices.ToList(),
                        Key = new TextureKey {
                            SurfaceId = 0xFFFFFFFF, // Dummy surface ID
                            PaletteId = 0,
                            Stippling = StipplingType.NoPos,
                            IsSolid = true
                        },
                        TextureData = transparentTexture,
                        UploadPixelFormat = UploadPixelFormat.Rgba,
                        UploadPixelType = UploadPixelType.UnsignedByte,
                        CullMode = CullMode.None,
                        IsTransparent = false  // Render in opaque pass but transparent
                    }
                }
            },
            BoundingBox = boundingBox,
            SelectionSphere = new Sphere { Origin = boundingBox.Center, Radius = Vector3.Distance(boundingBox.Max, boundingBox.Min) / 2.0f }
        };

        // Store all edge lines in mesh data for later use in UploadMeshData
        result.EdgeLines = allEdgeLines.ToArray();

        return result;
    }
}

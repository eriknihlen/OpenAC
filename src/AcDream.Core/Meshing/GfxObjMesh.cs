using System.Numerics;
using AcDream.Core.Terrain;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using AcDream.Core.Content;
using DatReaderWriter.Enums;

namespace AcDream.Core.Meshing;

public static class GfxObjMesh
{
    public static IReadOnlyList<GfxObjSubMesh> Build(GfxObj gfxObj, IDatObjectSource? dats = null)
    {
        var perBucket = new Dictionary<(int surfaceIdx, bool isNeg),
            (List<Vertex> Vertices, List<uint> Indices,
             Dictionary<(int pos, int uv, bool neg), uint> Dedupe)>();

        foreach (var kvp in gfxObj.Polygons)
        {
            var poly = kvp.Value;
            if (poly.VertexIds.Count < 3)
                continue;  // degenerate — can't form a triangle

            EmitSide(poly, poly.PosSurface, isNeg: false);

            bool hasNeg =
                poly.Stippling.HasFlag(StipplingType.Negative) ||
                poly.Stippling.HasFlag(StipplingType.Both) ||
                (!poly.Stippling.HasFlag(StipplingType.NoNeg) && poly.SidesType == CullMode.Clockwise);

            if (hasNeg)
                EmitSide(poly, poly.NegSurface, isNeg: true);

            void EmitSide(DatReaderWriter.Types.Polygon p, short surfaceIdx, bool isNeg)
            {
                if (surfaceIdx < 0 || surfaceIdx >= gfxObj.Surfaces.Count)
                    return;

                var bucketKey = ((int)surfaceIdx, isNeg);
                if (!perBucket.TryGetValue(bucketKey, out var bucket))
                {
                    bucket = (new List<Vertex>(), new List<uint>(),
                              new Dictionary<(int, int, bool), uint>());
                    perBucket[bucketKey] = bucket;
                }

                var polyOut = new List<uint>(p.VertexIds.Count);
                bool skipPoly = false;
                for (int i = 0; i < p.VertexIds.Count; i++)
                {
                    int posIdx = p.VertexIds[i];

                    // UV index selection: neg side uses NegUVIndices when
                    // present; otherwise fall back to PosUVIndices; otherwise
                    // zero. Matches WorldBuilder/ObjectMeshManager.cs:1521-1524.
                    int uvIdx = 0;
                    if (isNeg && p.NegUVIndices.Count > 0 && i < p.NegUVIndices.Count)
                        uvIdx = p.NegUVIndices[i];
                    else if (!isNeg && i < p.PosUVIndices.Count)
                        uvIdx = p.PosUVIndices[i];
                    else if (i < p.PosUVIndices.Count)
                        uvIdx = p.PosUVIndices[i];  // neg side with no NegUVIndices — borrow pos

                    if (!gfxObj.VertexArray.Vertices.TryGetValue((ushort)posIdx, out var sw))
                    {
                        skipPoly = true;
                        break;
                    }

                    var texcoord = uvIdx >= 0 && uvIdx < sw.UVs.Count
                        ? new Vector2(sw.UVs[uvIdx].U, sw.UVs[uvIdx].V)
                        : Vector2.Zero;

                    var normal = System.Numerics.Vector3.Normalize(isNeg ? -sw.Normal : sw.Normal);

                    var key = (posIdx, uvIdx, isNeg);
                    if (!bucket.Dedupe.TryGetValue(key, out var outIdx))
                    {
                        outIdx = (uint)bucket.Vertices.Count;
                        bucket.Vertices.Add(new Vertex(sw.Origin, normal, texcoord, TerrainLayer: 0));
                        bucket.Dedupe[key] = outIdx;
                    }
                    polyOut.Add(outIdx);
                }

                if (skipPoly || polyOut.Count < 3)
                    return;

                // Fan triangulation. Pos side keeps the original
                // (0, i, i+1) winding the earlier builder used so existing
                // tests and render behavior are preserved. Neg side emits
                // the opposite winding so the two faces point away from
                // each other — matches WorldBuilder/ObjectMeshManager.cs:
                // 1564-1577 once you account for the reversed pos order.
                if (isNeg)
                {
                    for (int i = 1; i < polyOut.Count - 1; i++)
                    {
                        bucket.Indices.Add(polyOut[i + 1]);
                        bucket.Indices.Add(polyOut[i]);
                        bucket.Indices.Add(polyOut[0]);
                    }
                }
                else
                {
                    for (int i = 1; i < polyOut.Count - 1; i++)
                    {
                        bucket.Indices.Add(polyOut[0]);
                        bucket.Indices.Add(polyOut[i]);
                        bucket.Indices.Add(polyOut[i + 1]);
                    }
                }
            }
        }

        var result = new List<GfxObjSubMesh>(perBucket.Count);
        foreach (var kvp in perBucket)
        {
            var (surfaceIdx, _) = kvp.Key;
            var surfaceId = (uint)gfxObj.Surfaces[surfaceIdx];

            var translucency = TranslucencyKind.Opaque;
            var luminosity = 0f;
            var diffuse = 1f;
            var surfOpacity = 1f;
            var disableFog = false;
            if (dats is not null)
            {
                var surface = dats.Get<Surface>(surfaceId);
                if (surface is not null)
                {
                    translucency = TranslucencyKindExtensions.FromSurfaceType(surface.Type);
                    luminosity = surface.Luminosity;
                    diffuse = surface.Diffuse;
                    // Apply the dat's Translucency value as opacity ONLY
                    // when the Translucent flag (0x10) is set on the
                    // Surface. Without this gate, surfaces with
                    // Translucency=0 (non-Translucent default) would
                    // render fully transparent.
                    surfOpacity = TranslucencyKindExtensions.OpacityFromSurfaceTranslucency(
                        surface.Type,
                        surface.Translucency);
                    disableFog = TranslucencyKindExtensions.DisablesFixedFunctionFog(surface.Type);
                }
            }

            bool needsUvRepeat = false;
            foreach (var v in kvp.Value.Vertices)
            {
                if (v.TexCoord.X < 0f || v.TexCoord.X > 1f
                    || v.TexCoord.Y < 0f || v.TexCoord.Y > 1f)
                { needsUvRepeat = true; break; }
            }

            result.Add(new GfxObjSubMesh(
                SurfaceId: surfaceId,
                Vertices: kvp.Value.Vertices.ToArray(),
                Indices: kvp.Value.Indices.ToArray())
            {
                Translucency = translucency,
                Luminosity = luminosity,
                Diffuse = diffuse,
                NeedsUvRepeat = needsUvRepeat,
                SurfOpacity = surfOpacity,
                DisableFog = disableFog,
            });
        }
        return result;
    }
}

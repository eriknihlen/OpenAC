using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using AcDream.Core.Meshing;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using DatReaderWriter;
using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering.Sky;

public sealed partial class SkyRenderer : IDisposable
{
    private readonly IDatReaderWriter _dats;
    private readonly TextureCache _textures;

    private SkyParams _params;

    // Lazily-built GPU resources per sky-GfxObj.
    private readonly Dictionary<uint, List<SubMeshGpu>> _gpuByGfxObj = new();

    private readonly long _animationStartedAtTimestamp = Stopwatch.GetTimestamp();

    internal float? AnimationPhaseSecondsOverride { get; init; }

    public float Near { get; set; } = 0.1f;
    public float Far  { get; set; } = 1_000_000f;

    /// <summary>
    /// Dereth's star layer — the one sky GfxObj (verified identical across
    /// all 20 day groups) whose draw the enhanced night sky replaces.
    /// </summary>
    private const uint StarLayerGfxObjId = 0x010015EFu;

    internal Func<bool>? EnhancedNightSkyActive { get; set; }

    private const float NightSkySeed = 11f;

    public void RenderSky(
        ICamera camera,
        Vector3 cameraWorldPos,
        float dayFraction,
        DayGroupData? group,
        SkyKeyframe keyframe,
        bool environOverrideActive = false)
        => RenderPass(camera, cameraWorldPos, dayFraction, group, keyframe,
            postScenePass: false, environOverrideActive: environOverrideActive);

    public void RenderWeather(
        ICamera camera,
        Vector3 cameraWorldPos,
        float dayFraction,
        DayGroupData? group,
        SkyKeyframe keyframe,
        bool environOverrideActive = false)
        => RenderPass(camera, cameraWorldPos, dayFraction, group, keyframe,
            postScenePass: true, environOverrideActive: environOverrideActive);

    private void RenderPass(
        ICamera camera,
        Vector3 cameraWorldPos,
        float dayFraction,
        DayGroupData? group,
        SkyKeyframe keyframe,
        bool postScenePass,
        bool environOverrideActive)
    {
        if (group is null || group.SkyObjects.Count == 0) return;

        var skyProj = SkyProjection.WithDepthRange(camera.Projection, Near, Far);

        var skyView = camera.View;
        skyView.M41 = 0f;
        skyView.M42 = 0f;
        skyView.M43 = 0f;

        _params.SkyView = skyView;
        _params.SkyProjection = skyProj;

        _params.AmbientColor = keyframe.AmbientColor;
        _params.SunColor = keyframe.SunColor;
        _params.SunDir =
            AcDream.Core.World.SkyStateProvider.SunDirectionFromKeyframe(keyframe);

        // Look up the keyframe's override list so we can apply
        // SkyObjReplace (r12 §2.3): per-keyframe GfxObj swaps + rotation
        // override + transparency fade + luminosity cap.
        var replaces = PickReplaces(group, dayFraction);

        float secondsSinceStart = AnimationPhaseSecondsOverride
            ?? ElapsedAnimationSeconds(
                _animationStartedAtTimestamp,
                Stopwatch.GetTimestamp());

        for (int i = 0; i < group.SkyObjects.Count; i++)
        {
            var obj = group.SkyObjects[i];
            if (obj.IsPostScene != postScenePass) continue;
            if (!obj.IsVisible(dayFraction)) continue;
            if (environOverrideActive && (obj.Properties & 0x02u) != 0u)
                continue;

            // Apply per-keyframe replace overrides.
            uint gfxObjId = obj.GfxObjId;
            float headingDeg = 0f;
            float transparent = 0f;
            float replaceLuminosity = float.NaN;
            float replaceDiffuse = float.NaN;
            if (replaces.TryGetValue((uint)i, out var rep))
            {
                if (rep.GfxObjId != 0) gfxObjId = rep.GfxObjId;
                if (rep.Rotate != 0f)  headingDeg = rep.Rotate;
                transparent = Math.Clamp(rep.Transparent, 0f, 1f);
                if (rep.Luminosity > 0f) replaceLuminosity = rep.Luminosity;
                if (rep.MaxBright > 0f)
                    replaceDiffuse = rep.MaxBright;
            }
            if (gfxObjId == 0) continue;

            float rotationDeg = obj.CurrentAngle(dayFraction);
            float headingRad = headingDeg * (MathF.PI / 180f);
            float rotationRad = rotationDeg * (MathF.PI / 180f);

            var model = Matrix4x4.CreateScale(1.0f)
                      * Matrix4x4.CreateRotationZ(-headingRad)
                      * Matrix4x4.CreateRotationY(-rotationRad);

            if (postScenePass && obj.IsWeather && (obj.Properties & 0x08u) == 0u)
                model = model * Matrix4x4.CreateTranslation(0f, 0f, -120f);

            _params.Model = model;

            // UV scroll accumulates real-time × velocity. Wrap to [0, 1]
            // so long-running sessions don't accumulate float precision
            // loss in the fragment UV.
            float uOffset = (obj.TexVelocityX * secondsSinceStart) % 1f;
            float vOffset = (obj.TexVelocityY * secondsSinceStart) % 1f;
            _params.UvScroll = new Vector2(uOffset, vOffset);
            _params.Transparency = transparent;

            EnsureMeshUploaded(gfxObjId);
            if (!_gpuByGfxObj.TryGetValue(gfxObjId, out var subMeshes)) continue;

            foreach (var sub in subMeshes)
            {
                float effEmissive = float.IsNaN(replaceLuminosity)
                    ? sub.SurfLuminosity
                    : replaceLuminosity;
                float effDiffuse = float.IsNaN(replaceDiffuse)
                    ? sub.SurfDiffuse
                    : replaceDiffuse;
                _params.Emissive = effEmissive;
                _params.DiffuseFactor = effDiffuse;

                _params.SurfOpacity = sub.SurfOpacity;

                _params.ApplyFog = environOverrideActive && !sub.DisableFog ? 1f : 0f;

                bool needsRepeat = sub.NeedsUvRepeat
                    || obj.TexVelocityX != 0f
                    || obj.TexVelocityY != 0f;
                uint slot = TextureTableSlot(sub.SurfaceId, needsRepeat);
                bool nightSky = gfxObjId == StarLayerGfxObjId
                    && (EnhancedNightSkyActive?.Invoke() ?? false);
                DrawSubMeshRhi(sub, slot, nightSky);
            }
        }
    }

    internal static float ElapsedAnimationSeconds(long startTimestamp, long currentTimestamp)
        => (float)Stopwatch.GetElapsedTime(startTimestamp, currentTimestamp).TotalSeconds;

    private uint TextureTableSlot(uint surfaceId, bool repeat) =>
        RhiTextureTableSlot(surfaceId, repeat);

    private static Dictionary<uint, SkyObjectReplaceData> PickReplaces(
        DayGroupData group, float dayFraction)
    {
        var result = new Dictionary<uint, SkyObjectReplaceData>();
        var times = group.SkyTimes;
        if (times.Count == 0) return result;

        // Pick k1 = last keyframe with Begin <= dayFraction.
        DatSkyKeyframeData k1 = times[^1];
        for (int i = 0; i < times.Count; i++)
        {
            if (times[i].Keyframe.Begin <= dayFraction)
                k1 = times[i];
            else
                break;
        }

        foreach (var r in k1.Replaces)
            result[r.ObjectIndex] = r;

        return result;
    }

    private void EnsureMeshUploaded(uint gfxObjId)
    {
        if (_gpuByGfxObj.ContainsKey(gfxObjId)) return;

        if ((gfxObjId & 0xFF000000u) == 0x02000000u)
        {
            EnsureSetupUploaded(gfxObjId);
            return;
        }

        GfxObj? gfx = null;
        try { gfx = _dats.Get<GfxObj>(gfxObjId); }
        catch { gfx = null; }

        if (gfx is null)
        {
            _gpuByGfxObj[gfxObjId] = new List<SubMeshGpu>();
            return;
        }

        System.Collections.Generic.IReadOnlyList<GfxObjSubMesh>? subMeshes = null;
        try { subMeshes = GfxObjMesh.Build(gfx, _dats); }
        catch { subMeshes = null; }

        if (subMeshes is null)
        {
            _gpuByGfxObj[gfxObjId] = new List<SubMeshGpu>();
            return;
        }

        if (System.Environment.GetEnvironmentVariable("ACDREAM_DUMP_SKY") == "1")
            DumpGfxObjSurfaces(gfxObjId, gfx, subMeshes);

        var gpuList = new List<SubMeshGpu>(subMeshes.Count);
        foreach (var sm in subMeshes)
            gpuList.Add(UploadSubMesh(sm));
        _gpuByGfxObj[gfxObjId] = gpuList;
    }

    private void EnsureSetupUploaded(uint setupId)
    {
        Setup? setup = null;
        try { setup = _dats.Get<Setup>(setupId); }
        catch { setup = null; }

        if (setup is null)
        {
            _gpuByGfxObj[setupId] = new List<SubMeshGpu>();
            return;
        }

        var parts = SetupMesh.Flatten(setup);
        var allSubs = new List<SubMeshGpu>(parts.Count);
        foreach (var partRef in parts)
        {
            GfxObj? partGfx = null;
            try { partGfx = _dats.Get<GfxObj>(partRef.GfxObjId); }
            catch { partGfx = null; }
            if (partGfx is null) continue;

            System.Collections.Generic.IReadOnlyList<GfxObjSubMesh>? partSubs = null;
            try { partSubs = GfxObjMesh.Build(partGfx, _dats); }
            catch { partSubs = null; }
            if (partSubs is null) continue;

            // Bake the part's local transform into the vertices. For sky
            // setups we don't expect non-uniform scale, so transforming
            // normals as directions is fine; if a future sky setup ever
            // breaks that assumption we'd need an inverse-transpose here.
            var partTx = partRef.PartTransform;
            foreach (var sub in partSubs)
            {
                var transformed = new Vertex[sub.Vertices.Length];
                for (int i = 0; i < sub.Vertices.Length; i++)
                {
                    var v = sub.Vertices[i];
                    var p = Vector3.Transform(v.Position, partTx);
                    var n = Vector3.Normalize(Vector3.TransformNormal(v.Normal, partTx));
                    transformed[i] = v with { Position = p, Normal = n };
                }
                var rebuilt = sub with { Vertices = transformed };
                allSubs.Add(UploadSubMesh(rebuilt));
            }
        }
        _gpuByGfxObj[setupId] = allSubs;
    }

    private void DumpGfxObjSurfaces(
        uint gfxObjId,
        GfxObj gfx,
        System.Collections.Generic.IReadOnlyList<GfxObjSubMesh> subMeshes)
    {
        Console.WriteLine(
            $"[sky-dump] GfxObj 0x{gfxObjId:X8} Surfaces.Count={gfx.Surfaces.Count} Polygons.Count={gfx.Polygons.Count} SubMeshes.Count={subMeshes.Count}");

        for (int i = 0; i < gfx.Surfaces.Count; i++)
        {
            uint surfaceId = (uint)gfx.Surfaces[i];
            DatReaderWriter.DBObjs.Surface? surface = null;
            try { surface = _dats.Get<DatReaderWriter.DBObjs.Surface>(surfaceId); }
            catch { surface = null; }

            if (surface is null)
            {
                Console.WriteLine($"[sky-dump]   Surface[{i}] 0x{surfaceId:X8} -- (dat read failed)");
                continue;
            }

            uint rawType = (uint)surface.Type;
            string names = surface.Type.ToString();
            uint origTex = surface.OrigTextureId?.DataId ?? 0u;
            var trans = TranslucencyKindExtensions.FromSurfaceType(surface.Type);
            // Surface's own Luminosity (0..1 fraction per test fixture —
            // different from SkyObjectReplace.Luminosity which lives in the keyframe).
            Console.WriteLine(
                $"[sky-dump]   Surface[{i}] 0x{surfaceId:X8} Type=0x{rawType:X8} ({names}) " +
                $"OrigTexture=0x{origTex:X8} Translucency={trans} " +
                $"SurfLuminosity={surface.Luminosity:F4} SurfaceTranslucency={surface.Translucency:F4}");
        }
    }

    private SubMeshGpu UploadSubMesh(GfxObjSubMesh sm) => UploadSubMeshRhi(sm);

    public void Dispose() => DisposeRhi();

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    internal struct SkyParams
    {
        public Matrix4x4 Model;            //   0
        public Matrix4x4 SkyView;          //  64
        public Matrix4x4 SkyProjection;    // 128
        public Vector3 AmbientColor;       // 192
        public float Emissive;             // 204
        public Vector3 SunColor;           // 208
        public float DiffuseFactor;        // 220
        public Vector3 SunDir;             // 224
        public float Transparency;         // 236
        public Vector2 UvScroll;           // 240
        public float ApplyFog;             // 248
        public float SurfOpacity;          // 252

        /// <summary>256 — the std140 size of the block, a whole number of vec4s.</summary>
        public const int SizeInBytes = 256;
    }

    private sealed class SubMeshGpu
    {
        public AcDream.App.Rendering.Gpu.IGpuBuffer? VertexBuffer;
        public AcDream.App.Rendering.Gpu.IGpuBuffer? IndexBuffer;
        public int  IndexCount;
        public uint SurfaceId;
        public bool IsAdditive;
        public float SurfLuminosity;
        public float SurfDiffuse;
        public bool NeedsUvRepeat;
        public float SurfOpacity;
        public bool DisableFog;
    }
}

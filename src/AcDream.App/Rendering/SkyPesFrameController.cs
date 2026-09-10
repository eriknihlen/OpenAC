using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Vfx;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal sealed class SkyPesFrameController
{
    private static readonly Matrix4x4[] IdentityPartPose =
        [Matrix4x4.Identity];

    private readonly record struct SkyPesKey(
        int ObjectIndex,
        uint GfxObjId,
        uint Properties);

    private readonly PhysicsScriptRunner _scripts;
    private readonly ParticleHookSink _particles;
    private readonly EntityEffectPoseRegistry _poses;
    private readonly EntityEffectController? _effects;
    private readonly Action<uint>? _stopAudio;
    private readonly HashSet<SkyPesKey> _active = [];
    private readonly HashSet<SkyPesKey> _missing = [];
    private readonly HashSet<uint> _reportedScriptMismatches = [];
    private readonly HashSet<SkyPesKey> _seenScratch = [];
    private readonly List<SkyPesKey> _stopScratch = [];
    private readonly Action<string>? _diagnostic;

    public SkyPesFrameController(
        PhysicsScriptRunner scripts,
        ParticleHookSink particles,
        EntityEffectPoseRegistry poses,
        EntityEffectController? effects,
        Action<string>? diagnostic = null,
        Action<uint>? stopAudio = null)
    {
        _scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
        _particles = particles ?? throw new ArgumentNullException(nameof(particles));
        _poses = poses ?? throw new ArgumentNullException(nameof(poses));
        _effects = effects;
        _diagnostic = diagnostic;
        _stopAudio = stopAudio;
    }

    public void Update(
        float dayFraction,
        DayGroupData? dayGroup,
        Vector3 cameraWorldPosition,
        bool skyActive = true)
    {
        if (!skyActive)
        {
            SetActive(false);
            return;
        }

        _seenScratch.Clear();
        if (dayGroup is not null)
        {
            for (int index = 0; index < dayGroup.SkyObjects.Count; index++)
            {
                SkyObjectData skyObject = dayGroup.SkyObjects[index];
                if (ResolveScriptId(skyObject) == 0
                    || !skyObject.IsVisible(dayFraction))
                {
                    continue;
                }

                _seenScratch.Add(new SkyPesKey(
                    index,
                    skyObject.GfxObjId,
                    skyObject.Properties));
            }
        }

        StopUnseen(_active, stopScripts: true);
        StopUnseen(_missing, stopScripts: false);

        if (dayGroup is null)
            return;

        for (int index = 0; index < dayGroup.SkyObjects.Count; index++)
        {
            SkyObjectData skyObject = dayGroup.SkyObjects[index];
            uint scriptId = ResolveScriptId(skyObject);
            if (scriptId == 0 || !skyObject.IsVisible(dayFraction))
                continue;

            var key = new SkyPesKey(
                index,
                skyObject.GfxObjId,
                skyObject.Properties);
            uint ownerId = EntityId(key);
            ParticleRenderPass renderPass = skyObject.IsPostScene
                ? ParticleRenderPass.SkyPostScene
                : ParticleRenderPass.SkyPreScene;
            _particles.SetEntityRenderPass(ownerId, renderPass);
            _scripts.SetOwnerAnchor(ownerId, cameraWorldPosition);
            Quaternion rotation = Rotation(skyObject, dayFraction);
            _poses.Publish(
                ownerId,
                Matrix4x4.CreateFromQuaternion(rotation)
                    * Matrix4x4.CreateTranslation(cameraWorldPosition),
                IdentityPartPose,
                cellId: 0u);

            if (_active.Contains(key) || _missing.Contains(key))
                continue;

            _effects?.RegisterSyntheticOwner(ownerId);
            if (_scripts.Play(scriptId, ownerId, cameraWorldPosition))
            {
                _active.Add(key);
            }
            else
            {
                _missing.Add(key);
                _effects?.UnregisterSyntheticOwner(ownerId);
                _particles.ClearEntityRenderPass(ownerId);
                _poses.Remove(ownerId);
            }
        }
    }

    public void SetActive(bool skyActive)
    {
        if (skyActive)
            return;

        _seenScratch.Clear();
        StopUnseen(_active, stopScripts: true);
        StopUnseen(_missing, stopScripts: false);
    }

    private void StopUnseen(HashSet<SkyPesKey> set, bool stopScripts)
    {
        _stopScratch.Clear();
        foreach (SkyPesKey key in set)
        {
            if (!_seenScratch.Contains(key))
                _stopScratch.Add(key);
        }

        foreach (SkyPesKey key in _stopScratch)
        {
            if (stopScripts)
            {
                uint ownerId = EntityId(key);
                _scripts.StopAllForEntity(ownerId);
                _effects?.UnregisterSyntheticOwner(ownerId);
                _particles.StopAllForEntity(ownerId, fadeOut: true);
                _poses.Remove(ownerId);
                _stopAudio?.Invoke(ownerId);
            }

            set.Remove(key);
        }
    }

    private uint ResolveScriptId(SkyObjectData skyObject)
    {
        uint scriptId = skyObject.DefaultScriptId;
        if (scriptId != skyObject.PesObjectId
            && skyObject.GfxObjId != 0
            && _reportedScriptMismatches.Add(skyObject.GfxObjId))
        {
            _diagnostic?.Invoke(
                $"[sky-pes] carrier 0x{skyObject.GfxObjId:X8}: Setup DefaultScript " +
                $"0x{scriptId:X8} != SkyObject pes_id 0x{skyObject.PesObjectId:X8}; " +
                "playing the DefaultScript (retail's source).");
        }

        return scriptId;
    }

    private static uint EntityId(SkyPesKey key)
    {
        uint postScene = (key.Properties & 0x01u) != 0u ? 0x08000000u : 0u;
        return 0xF0000000u
            | postScene
            | ((uint)key.ObjectIndex & 0x07FFFFFFu);
    }

    private static Quaternion Rotation(
        SkyObjectData skyObject,
        float dayFraction)
    {
        float radians = skyObject.CurrentAngle(dayFraction) * (MathF.PI / 180f);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, -radians);
    }
}

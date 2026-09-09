using System;
using System.Collections.Concurrent;
using System.Numerics;
using DatReaderWriter;
using DatReaderWriter.Lib.IO;
using AcDream.Core.Content;
using DatParticleEmitter = DatReaderWriter.DBObjs.ParticleEmitter;
using DatGfxObj = DatReaderWriter.DBObjs.GfxObj;
using DatGfxObjDegradeInfo = DatReaderWriter.DBObjs.GfxObjDegradeInfo;
using DatEmitterType = DatReaderWriter.Enums.EmitterType;
using DatParticleType = DatReaderWriter.Enums.ParticleType;
using DatGfxObjFlags = DatReaderWriter.Enums.GfxObjFlags;

namespace AcDream.Core.Vfx;

public enum EmitterDescResolutionFailureKind
{
    None,
    MissingEmitterInfo,
    InvalidHardwareGfxObjId,
    MissingHardwareGfxObj,
}

public readonly record struct EmitterDescResolutionFailure(
    EmitterDescResolutionFailureKind Kind,
    uint RelatedDatId = 0u)
{
    public static EmitterDescResolutionFailure None => default;
}

public sealed class EmitterDescRegistry
{
    private readonly Func<uint, DatParticleEmitter?>? _resolver;
    private readonly Func<DatParticleEmitter, EmitterDatResolution>?
        _degradeDistanceResolver;
    private readonly ConcurrentDictionary<uint, EmitterDesc> _byId = new();
    private readonly ConcurrentDictionary<uint, EmitterDescResolutionFailure>
        _failures = new();

    public EmitterDescRegistry()
        : this((Func<uint, DatParticleEmitter?>?)null)
    {
    }

    public EmitterDescRegistry(IDatObjectSource dats)
    {
        ArgumentNullException.ThrowIfNull(dats);
        _resolver = id => SafeGet<DatParticleEmitter>(dats, id);
        _degradeDistanceResolver = emitter => ResolveMaxDegradeDistance(dats, emitter);
    }

    public EmitterDescRegistry(Func<uint, DatParticleEmitter?>? resolver)
    {
        _resolver = resolver;
    }

    public void Register(EmitterDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc);
        _byId[desc.DatId] = desc;
        _failures.TryRemove(desc.DatId, out _);
    }

    public EmitterDesc Get(uint emitterId)
    {
        if (TryGet(emitterId, out EmitterDesc? desc))
            return desc;
        throw new KeyNotFoundException(
            $"ParticleEmitterInfo 0x{emitterId:X8} was not found.");
    }

    public bool TryGet(uint emitterId, out EmitterDesc desc)
        => TryGet(emitterId, out desc, out _);

    public bool TryGet(
        uint emitterId,
        out EmitterDesc desc,
        out EmitterDescResolutionFailure failure)
    {
        if (_byId.TryGetValue(emitterId, out desc!))
        {
            failure = EmitterDescResolutionFailure.None;
            return true;
        }
        if (_failures.TryGetValue(emitterId, out failure))
        {
            desc = null!;
            return false;
        }

        if (_resolver is not null)
        {
            var dat = _resolver(emitterId);
            if (dat is not null)
            {
                EmitterDatResolution resolved = _degradeDistanceResolver?.Invoke(dat)
                    ?? EmitterDatResolution.Success(
                        RetailParticleDegradeDistance.DefaultDistance);
                if (!resolved.Succeeded)
                {
                    failure = resolved.Failure;
                    _failures.TryAdd(emitterId, failure);
                    desc = null!;
                    return false;
                }
                desc = FromDat(emitterId, dat, resolved.MaxDegradeDistance);
                _byId[emitterId] = desc;
                failure = EmitterDescResolutionFailure.None;
                return true;
            }
        }

        failure = new EmitterDescResolutionFailure(
            EmitterDescResolutionFailureKind.MissingEmitterInfo,
            emitterId);
        _failures.TryAdd(emitterId, failure);
        desc = null!;
        return false;
    }

    public int Count => _byId.Count;
    public int FailureCount => _failures.Count;

    public static EmitterDesc FromDat(
        uint emitterId,
        DatParticleEmitter dat,
        float maxDegradeDistance = RetailParticleDegradeDistance.DefaultDistance)
    {
        ArgumentNullException.ThrowIfNull(dat);

        float birthrate = MathF.Max(0f, (float)dat.Birthrate);
        float lifespan = MathF.Max(0f, (float)dat.Lifespan);
        float lifespanRand = MathF.Abs((float)dat.LifespanRand);
        float lifetimeMin = MathF.Max(0f, lifespan - lifespanRand);
        float lifetimeMax = MathF.Max(lifetimeMin, lifespan + lifespanRand);

        var flags = EmitterFlags.Billboard | EmitterFlags.FaceCamera;
        if (dat.IsParentLocal)
            flags |= EmitterFlags.AttachLocal;

        float startOpacity = 1f - Math.Clamp((float)dat.StartTrans, 0f, 1f);
        float endOpacity = 1f - Math.Clamp((float)dat.FinalTrans, 0f, 1f);

        return new EmitterDesc
        {
            MaxDegradeDistance = maxDegradeDistance,
            DatId = emitterId,
            Type = MapParticleType(dat.ParticleType),
            EmitterKind = MapEmitterKind(dat.EmitterType),
            Flags = flags,
            GfxObjId = dat.GfxObjId.DataId,
            HwGfxObjId = dat.HwGfxObjId.DataId,
            Birthrate = birthrate,
            EmitRate = dat.EmitterType == DatEmitterType.BirthratePerSec && birthrate > 0f
                ? 1f / birthrate
                : 0f,
            MaxParticles = Math.Max(1, dat.MaxParticles),
            InitialParticles = Math.Max(0, dat.InitialParticles),
            TotalParticles = Math.Max(0, dat.TotalParticles),
            TotalDuration = MathF.Max(0f, (float)dat.TotalSeconds),
            Lifespan = lifespan,
            LifespanRand = lifespanRand,
            LifetimeMin = lifetimeMin,
            LifetimeMax = lifetimeMax,
            OffsetDir = dat.OffsetDir,
            MinOffset = dat.MinOffset,
            MaxOffset = dat.MaxOffset,
            SpawnDiskRadius = dat.MaxOffset,
            InitialVelocity = dat.A,
            Gravity = dat.B,
            A = dat.A,
            MinA = dat.MinA,
            MaxA = dat.MaxA,
            B = dat.B,
            MinB = dat.MinB,
            MaxB = dat.MaxB,
            C = dat.C,
            MinC = dat.MinC,
            MaxC = dat.MaxC,
            StartSize = dat.StartScale,
            EndSize = dat.FinalScale,
            ScaleRand = dat.ScaleRand,
            StartAlpha = startOpacity,
            EndAlpha = endOpacity,
            TransRand = dat.TransRand,
        };
    }

    private static EmitterDatResolution ResolveMaxDegradeDistance(
        IDatObjectSource dats,
        DatParticleEmitter emitter)
    {
        uint gfxObjId = GetRetailHardwareGfxObjId(emitter);
        if (gfxObjId == 0)
        {
            return EmitterDatResolution.Fail(
                EmitterDescResolutionFailureKind.InvalidHardwareGfxObjId);
        }

        DatGfxObj? gfx = SafeGet<DatGfxObj>(dats, gfxObjId);
        if (gfx is null)
        {
            return EmitterDatResolution.Fail(
                EmitterDescResolutionFailureKind.MissingHardwareGfxObj,
                gfxObjId);
        }
        if (!gfx.Flags.HasFlag(DatGfxObjFlags.HasDIDDegrade)
            || gfx.DIDDegrade == 0)
        {
            return EmitterDatResolution.Success(
                RetailParticleDegradeDistance.DefaultDistance);
        }

        DatGfxObjDegradeInfo? degrade = SafeGet<DatGfxObjDegradeInfo>(dats, gfx.DIDDegrade);
        return EmitterDatResolution.Success(
            RetailParticleDegradeDistance.FromEntries(degrade?.Degrades));
    }

    internal static uint GetRetailHardwareGfxObjId(DatParticleEmitter emitter)
        => emitter.HwGfxObjId.DataId;

    private static T? SafeGet<T>(IDatObjectSource dats, uint id) where T : class, IDBObj
    {
        if (dats is null)
            return null;
        try
        {
            return dats.Get<T>(id);
        }
        catch
        {
            return null;
        }
    }

    private static ParticleEmitterKind MapEmitterKind(DatEmitterType type) => type switch
    {
        DatEmitterType.BirthratePerSec => ParticleEmitterKind.BirthratePerSec,
        DatEmitterType.BirthratePerMeter => ParticleEmitterKind.BirthratePerMeter,
        _ => ParticleEmitterKind.Unknown,
    };

    private static ParticleType MapParticleType(DatParticleType type) => type switch
    {
        DatParticleType.Still => ParticleType.Still,
        DatParticleType.LocalVelocity => ParticleType.LocalVelocity,
        DatParticleType.ParabolicLVGA => ParticleType.ParabolicLVGA,
        DatParticleType.ParabolicLVGAGR => ParticleType.ParabolicLVGAGR,
        DatParticleType.Swarm => ParticleType.Swarm,
        DatParticleType.Explode => ParticleType.Explode,
        DatParticleType.Implode => ParticleType.Implode,
        DatParticleType.ParabolicLVLA => ParticleType.ParabolicLVLA,
        DatParticleType.ParabolicLVLALR => ParticleType.ParabolicLVLALR,
        DatParticleType.ParabolicGVGA => ParticleType.ParabolicGVGA,
        DatParticleType.ParabolicGVGAGR => ParticleType.ParabolicGVGAGR,
        DatParticleType.GlobalVelocity => ParticleType.GlobalVelocity,
        _ => ParticleType.Unknown,
    };

    private readonly record struct EmitterDatResolution(
        bool Succeeded,
        float MaxDegradeDistance,
        EmitterDescResolutionFailure Failure)
    {
        public static EmitterDatResolution Success(float maxDegradeDistance) =>
            new(
                true,
                maxDegradeDistance,
                EmitterDescResolutionFailure.None);

        public static EmitterDatResolution Fail(
            EmitterDescResolutionFailureKind kind,
            uint relatedDatId = 0u) =>
            new(
                false,
                0f,
                new EmitterDescResolutionFailure(kind, relatedDatId));
    }
}

public static class RetailParticleDegradeDistance
{
    public const float DefaultDistance = 100f;

    public static float FromEntries(IReadOnlyList<DatReaderWriter.Types.GfxObjInfo>? entries)
    {
        if (entries is null || entries.Count == 0)
            return DefaultDistance;

        int index = entries.Count <= 2 ? 0 : entries.Count - 2;
        return entries[index].MaxDist;
    }
}

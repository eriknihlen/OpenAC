using AcDream.App.World;
using AcDream.Core.Net.Messages;
using AcDream.Core.Vfx;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering.Vfx;

public sealed class EntityEffectProfile : ILiveEntityEffectProfile
{
    private EntityEffectProfile(Setup setup)
    {
        SetupDefaultScriptDid = NormalizePhysicsScriptDid(setup.DefaultScript.DataId);
        CurrentPhysicsScriptTableDid = NormalizeTableDid((uint)setup.DefaultScriptTable);
        CurrentSoundTableDid = NormalizeSoundTableDid((uint)setup.DefaultSoundTable);
    }

    public uint? SetupDefaultScriptDid { get; }
    public uint? CurrentPhysicsScriptTableDid { get; private set; }
    public uint? CurrentSoundTableDid { get; private set; }
    public uint RawDefaultScriptType { get; private set; }
    public float DefaultScriptIntensity { get; private set; }
    public bool HasNetworkDescription { get; private set; }

    /// <summary>Setup defaults remain active for a DAT/static object.</summary>
    public static EntityEffectProfile CreateDatStatic(Setup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return new EntityEffectProfile(setup);
    }

    public static EntityEffectProfile CreateLive(
        Setup setup,
        PhysicsSpawnData physics)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var profile = new EntityEffectProfile(setup);
        profile.ApplyNetworkDescription(physics);
        return profile;
    }

    /// <summary>
    /// Replaces all network-owned effect defaults. Nullable wire fields use
    /// PhysicsDesc's initialized zero values; they never resurrect Setup's
    /// typed table.
    /// </summary>
    public void ApplyNetworkDescription(PhysicsSpawnData physics)
    {
        CurrentPhysicsScriptTableDid = NormalizeTableDid(
            physics.PhysicsScriptTableId.GetValueOrDefault());
        CurrentSoundTableDid = NormalizeSoundTableDid(
            physics.SoundTableId.GetValueOrDefault());
        RawDefaultScriptType = physics.DefaultScriptType.GetValueOrDefault();
        DefaultScriptIntensity = physics.DefaultScriptIntensity.GetValueOrDefault();
        HasNetworkDescription = true;
    }

    private static uint? NormalizePhysicsScriptDid(uint did) =>
        PhysicsScriptTableResolver.IsPhysicsScriptDid(did) ? did : null;

    private static uint? NormalizeTableDid(uint did) =>
        PhysicsScriptTableResolver.IsPhysicsScriptTableDid(did) ? did : null;

    private static uint? NormalizeSoundTableDid(uint did) =>
        (did & 0xFF000000u) == 0x20000000u ? did : null;
}

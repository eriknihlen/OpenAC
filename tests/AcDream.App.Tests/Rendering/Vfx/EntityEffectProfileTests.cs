using AcDream.App.Rendering.Vfx;
using AcDream.Core.Net.Messages;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering.Vfx;

public sealed class EntityEffectProfileTests
{
    [Fact]
    public void DatStatic_RetainsSetupDirectScriptAndTypedTable()
    {
        Setup setup = BuildSetup();

        EntityEffectProfile profile = EntityEffectProfile.CreateDatStatic(setup);

        Assert.Equal(0x330000AAu, profile.SetupDefaultScriptDid);
        Assert.Equal(0x340000BBu, profile.CurrentPhysicsScriptTableDid);
        Assert.Equal(0x200000DDu, profile.CurrentSoundTableDid);
        Assert.False(profile.HasNetworkDescription);
    }

    [Fact]
    public void LivePhysicsDescriptionWithoutPeTableClearsSetupTable()
    {
        EntityEffectProfile profile = EntityEffectProfile.CreateLive(
            BuildSetup(),
            default);

        Assert.Equal(0x330000AAu, profile.SetupDefaultScriptDid);
        Assert.Null(profile.CurrentPhysicsScriptTableDid);
        Assert.Null(profile.CurrentSoundTableDid);
        Assert.Equal(0u, profile.RawDefaultScriptType);
        Assert.Equal(0f, profile.DefaultScriptIntensity);
        Assert.True(profile.HasNetworkDescription);
    }

    [Fact]
    public void LivePhysicsDescriptionReplacesTableAndDrivesTypedDefault()
    {
        PhysicsSpawnData physics = default(PhysicsSpawnData) with
        {
            PhysicsScriptTableId = 0x340000CCu,
            SoundTableId = 0x200000EEu,
            DefaultScriptType = 0xA5A5A5A5u,
            DefaultScriptIntensity = 0.75f,
        };

        EntityEffectProfile profile = EntityEffectProfile.CreateLive(
            BuildSetup(),
            physics);

        Assert.Equal(0x340000CCu, profile.CurrentPhysicsScriptTableDid);
        Assert.Equal(0x200000EEu, profile.CurrentSoundTableDid);
        Assert.Equal(0xA5A5A5A5u, profile.RawDefaultScriptType);
        Assert.Equal(0.75f, profile.DefaultScriptIntensity);
    }

    [Fact]
    public void LaterNetworkDescriptionWithPresentZeroDoesNotRestoreSetup()
    {
        EntityEffectProfile profile = EntityEffectProfile.CreateLive(
            BuildSetup(),
            default(PhysicsSpawnData) with
            {
                PhysicsScriptTableId = 0x340000CCu,
                DefaultScriptType = 7u,
                DefaultScriptIntensity = 1f,
            });

        profile.ApplyNetworkDescription(default(PhysicsSpawnData) with
        {
            PhysicsScriptTableId = 0u,
            SoundTableId = 0u,
            DefaultScriptType = 0u,
            DefaultScriptIntensity = 0f,
        });

        Assert.Null(profile.CurrentPhysicsScriptTableDid);
        Assert.Null(profile.CurrentSoundTableDid);
        Assert.Equal(0u, profile.RawDefaultScriptType);
        Assert.Equal(0f, profile.DefaultScriptIntensity);
    }

    private static Setup BuildSetup() => new()
    {
        DefaultScript = 0x330000AAu,
        DefaultScriptTable = 0x340000BBu,
        DefaultSoundTable = 0x200000DDu,
    };
}

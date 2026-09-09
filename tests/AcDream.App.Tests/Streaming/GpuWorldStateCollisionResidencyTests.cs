using AcDream.App.Streaming;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class GpuWorldStateCollisionResidencyTests
{
    [Fact]
    public void StableSpatialState_DropsPublicationDatBundle()
    {
        const uint landblockId = 0xA9B4_FFFFu;
        var bundle = new PhysicsDatBundle(
            new LandBlockInfo(),
            new Dictionary<uint, EnvCell>
            {
                [0xA9B4_0100u] = new EnvCell(),
            },
            new Dictionary<uint, DatReaderWriter.DBObjs.Environment>(),
            new Dictionary<uint, Setup>
            {
                [0x0200_0001u] = new Setup(),
            },
            new Dictionary<uint, GfxObj>());
        var state = new GpuWorldState();

        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>(),
            bundle));

        Assert.True(state.TryGetLandblock(
            landblockId,
            out LoadedLandblock? retained));
        Assert.NotNull(retained);
        Assert.Same(PhysicsDatBundle.Empty, retained.PhysicsDats);
    }
}

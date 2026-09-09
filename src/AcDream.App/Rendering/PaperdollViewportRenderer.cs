using AcDream.App.Rendering.Wb;
using AcDream.App.UI;
using AcDream.Core.Lighting;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

public sealed class PaperdollViewportRenderer :
    IUiViewportRenderer,
    IPaperdollDollRenderer,
    IDisposable
{
    private readonly PrivateEntityViewportRenderer _renderer;

    internal PaperdollViewportRenderer(
        IWorldPassScope scope,
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        ICurrentGpuFrameSource frames,
        WbDrawDispatcher dispatcher,
        SceneLightingUboBinding lightUbo,
        IEntityTextureLifetime textureLifetime,
        IWbMeshAdapter meshAdapter)
    {
        _renderer = new PrivateEntityViewportRenderer(
            scope,
            device,
            frames,
            dispatcher,
            lightUbo,
            textureLifetime,
            meshAdapter,
            DollEntityBuilder.DollRenderId,
            new DollViewportCamera(),
            "paperdoll");
    }

    public bool TextureIsBottomUp => _renderer.TextureIsBottomUp;

    public void SetDoll(WorldEntity? doll) => _renderer.SetEntity(doll);

    public void Prepare() => _renderer.Prepare();

    public uint Render(int width, int height) =>
        _renderer.Render(width, height);

    public void Dispose() => _renderer.Dispose();
}

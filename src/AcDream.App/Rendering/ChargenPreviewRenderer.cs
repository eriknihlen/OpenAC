using AcDream.App.Rendering.Wb;
using AcDream.App.UI;
using AcDream.Core.Lighting;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal interface IChargenPreviewRenderer
{
    void SetPreview(WorldEntity? entity);

    void SetBackdrop(WorldEntity? entity);

    uint Render(int width, int height);
}

internal sealed class ChargenPreviewRenderer :
    IUiViewportRenderer,
    IChargenPreviewRenderer,
    IDisposable
{
    private readonly PrivateEntityViewportRenderer _renderer;
    private readonly ChargenPreviewViewportCamera _camera;

    internal ChargenPreviewRenderer(
        IWorldPassScope scope,
        AcDream.App.Rendering.Gpu.IGpuDevice device,
        ICurrentGpuFrameSource frames,
        WbDrawDispatcher dispatcher,
        SceneLightingUboBinding lightUbo,
        IEntityTextureLifetime textureLifetime,
        IWbMeshAdapter meshAdapter,
        uint heritageId = 0u,
        ChargenPreviewCamera? camera = null,
        uint renderId = ChargenPreviewEntityBuilder.PreviewRenderId,
        uint backdropRenderId = ChargenPreviewEntityBuilder.PreviewBackdropRenderId)
    {
        _camera = camera is not null
            ? new ChargenPreviewViewportCamera(camera)
            : new ChargenPreviewViewportCamera(heritageId);
        _renderer = new PrivateEntityViewportRenderer(
            scope,
            device,
            frames,
            dispatcher,
            lightUbo,
            textureLifetime,
            meshAdapter,
            renderId,
            _camera,
            "chargen preview",
            backdropRenderId);
    }

    public bool TextureIsBottomUp => _renderer.TextureIsBottomUp;

    public void SetHeritage(uint heritageId) => _camera.SetHeritage(heritageId);

    public void SetPreview(WorldEntity? entity) => _renderer.SetEntity(entity);

    public void SetBackdrop(WorldEntity? entity) => _renderer.SetBackdrop(entity);

    public uint Render(int width, int height) => _renderer.Render(width, height);

    public void Dispose() => _renderer.Dispose();
}

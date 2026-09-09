using System.Diagnostics;
using System.Numerics;
using AcDream.App.UI;
using AcDream.Content;
using AcDream.Core.CharGen;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using DatReaderWriter;

namespace AcDream.App.Rendering;

internal interface IChargenPreviewControl
{
    bool Rebuild(
        ChargenOptions options,
        uint heritageId,
        int genderKey,
        ChargenAppearanceSelection selection);

    void ZoomIn();
    void ZoomOut();
    void RotateClockwise();
    void RotateCounterClockwise();
}

internal interface IChargenPreviewPageVisibility
{
    bool IsVisible { get; }
}

internal interface IChargenPreviewFrameView
{
    bool TryGetVisibleSize(out int width, out int height);

    void SetTextureHandle(uint textureHandle);
}

internal sealed class RetailChargenPreviewPageVisibility : IChargenPreviewPageVisibility
{
    private readonly AcDream.App.UI.RetailUiRuntime _runtime;

    public RetailChargenPreviewPageVisibility(AcDream.App.UI.RetailUiRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public bool IsVisible => _runtime.IsChargenPreviewPageVisible;
}

internal sealed class RetailSummaryPreviewPageVisibility : IChargenPreviewPageVisibility
{
    private readonly AcDream.App.UI.RetailUiRuntime _runtime;

    public RetailSummaryPreviewPageVisibility(AcDream.App.UI.RetailUiRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public bool IsVisible => _runtime.IsSummaryPreviewPageVisible;
}

internal sealed class RetailChargenPreviewFrameView : IChargenPreviewFrameView
{
    private readonly UiViewport _viewport;
    private readonly IChargenPreviewPageVisibility _page;

    public RetailChargenPreviewFrameView(
        UiViewport viewport,
        IChargenPreviewPageVisibility page)
    {
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _page = page ?? throw new ArgumentNullException(nameof(page));
    }

    public bool TryGetVisibleSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!_viewport.Visible || !_page.IsVisible)
            return false;

        width = (int)_viewport.Width;
        height = (int)_viewport.Height;
        return true;
    }

    public void SetTextureHandle(uint textureHandle) =>
        _viewport.TextureSlot = UiTextureTableHandle.ToSlot(textureHandle);
}

internal sealed class ChargenPreviewController :
    IChargenPreviewControl,
    IPrivateEntityViewportFrame,
    IDisposable
{
    private readonly IChargenPreviewRenderer _renderer;
    private readonly IChargenPreviewFrameView _view;
    private readonly ChargenPreviewCamera _camera;
    private readonly ChargenPreviewRotationController _rotation;
    private readonly IDatReaderWriter _dats;
    private readonly IAnimationLoader _animations;
    private readonly IChargenPalSetSource _palSets;
    private readonly IChargenClothingTableSource _clothingTables;
    private readonly object _datLock;
    private readonly bool _useZoomedOutEye;
    private readonly uint _renderId;
    private readonly uint _backdropRenderId;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private ChargenPreviewAnimator? _animator;
    private ChargenPreviewZoomController? _zoom;
    private double _lastElapsedSeconds;
    private bool _hasComposed;
    private uint _lastHeritageId;
    private int _lastGenderKey = -1;
    private ChargenAppearanceSelection _lastSelection;
    private bool _disposed;

    public ChargenPreviewController(
        IChargenPreviewRenderer renderer,
        ChargenPreviewCamera camera,
        IChargenPreviewFrameView view,
        IDatReaderWriter dats,
        IAnimationLoader animations,
        IChargenPalSetSource palSets,
        IChargenClothingTableSource clothingTables,
        object datLock,
        bool useZoomedOutEye = false,
        uint renderId = ChargenPreviewEntityBuilder.PreviewRenderId,
        uint backdropRenderId = ChargenPreviewEntityBuilder.PreviewBackdropRenderId)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _palSets = palSets ?? throw new ArgumentNullException(nameof(palSets));
        _clothingTables = clothingTables ?? throw new ArgumentNullException(nameof(clothingTables));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
        _useZoomedOutEye = useZoomedOutEye;
        _renderId = renderId;
        _backdropRenderId = backdropRenderId;
        _rotation = new ChargenPreviewRotationController();
        if (_useZoomedOutEye)
            _camera.Eye = ChargenPreviewCamera.ResolveZoomedOutEye(0u);
    }

    internal bool IsZoomedIn => _zoom?.IsZoomedIn ?? false;

    /// <summary>Test-observability seam only.</summary>
    internal Vector3 CameraEye => _camera.Eye;

    public bool Rebuild(
        ChargenOptions options,
        uint heritageId,
        int genderKey,
        ChargenAppearanceSelection selection)
    {
        if (_disposed)
            return false;

        if (_hasComposed
            && heritageId == _lastHeritageId
            && genderKey == _lastGenderKey
            && selection.Equals(_lastSelection))
        {
            return true;
        }

        bool composed;
        ChargenAppearanceResult result;
        lock (_datLock)
        {
            composed = ChargenAppearanceFactory.TryCompose(
                options, heritageId, genderKey, selection,
                _palSets, _clothingTables, out result);
        }
        if (!composed)
        {
            return false;
        }

        Quaternion heading = MoveToMath.SetHeading(
            Quaternion.Identity, _rotation.HeadingDegrees);
        ChargenPreviewAnimatedBuild? build = ChargenPreviewEntityBuilder.TryBuildAnimated(
            _dats, _animations, result, heritageId, heading, _datLock, _renderId);
        if (build is null)
            return false;

        bool wasZoomedIn = _animator?.IsZoomedIn ?? false;
        _animator = new ChargenPreviewAnimator(build);
        if (wasZoomedIn)
            _animator.SetZoomedIn(true);

        bool heritageOrGenderChanged =
            !_hasComposed || heritageId != _lastHeritageId || genderKey != _lastGenderKey;
        if (heritageOrGenderChanged)
        {
            _camera.Eye = _useZoomedOutEye
                ? ChargenPreviewCamera.ResolveZoomedOutEye(heritageId)
                : ChargenPreviewCamera.ResolveDefaultEye(heritageId);
        }

        bool heritageChanged = !_hasComposed || heritageId != _lastHeritageId;
        if (heritageChanged)
        {
            WorldEntity? backdrop =
                options.TryGetHeritage(heritageId, out ChargenHeritageOptions? heritage)
                    ? ChargenPreviewEntityBuilder.TryBuildBackdrop(
                        _dats, heritage!.EnvironmentSetupId, _datLock, _backdropRenderId)
                    : null;
            _renderer.SetBackdrop(backdrop);
        }

        _zoom = new ChargenPreviewZoomController(heritageId, _camera, _animator);

        _renderer.SetPreview(_animator.Entity);
        _hasComposed = true;
        _lastHeritageId = heritageId;
        _lastGenderKey = genderKey;
        _lastSelection = selection;
        return true;
    }

    public void ZoomIn() => _zoom?.ZoomIn();
    public void ZoomOut() => _zoom?.ZoomOut();
    public void RotateClockwise() => _rotation.Toggle(ChargenRotateDirection.Clockwise);
    public void RotateCounterClockwise() => _rotation.Toggle(ChargenRotateDirection.CounterClockwise);

    public void Render()
    {
        if (_disposed || !_view.TryGetVisibleSize(out int width, out int height))
            return;

        double now = _clock.Elapsed.TotalSeconds;
        float deltaSeconds = (float)Math.Max(0.0, now - _lastElapsedSeconds);
        _lastElapsedSeconds = now;

        _animator?.Tick(deltaSeconds);
        _rotation.Tick(now);
        _zoom?.Tick(now);
        if (_animator is not null)
            _animator.Entity.Rotation = _rotation.ToOrientation();

        _view.SetTextureHandle(_renderer.Render(width, height));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renderer.SetPreview(null);
        _renderer.SetBackdrop(null);
        _animator = null;
        _zoom = null;
    }
}

using AcDream.Core.Rendering;

namespace AcDream.App.Rendering;

public sealed class CameraController
{
    internal readonly record struct CameraState(
        int ModeCode,
        ChaseCamera? Chase,
        RetailChaseCamera? RetailChase);

    public OrbitCamera        Orbit       { get; }
    public FlyCamera          Fly         { get; }
    public ChaseCamera?       Chase       { get; private set; }
    public RetailChaseCamera? RetailChase { get; private set; }

    public ICamera Active
    {
        get
        {
            if (_mode == Mode.Fly) return Fly;
            if (_mode == Mode.Chase)
            {
                if (CameraDiagnostics.UseRetailChaseCamera && RetailChase is not null)
                    return RetailChase;
                if (Chase is not null) return Chase;
            }
            return Orbit;
        }
    }

    public bool IsFlyMode   => _mode == Mode.Fly;
    public bool IsChaseMode => _mode == Mode.Chase;

    public event Action<bool>? ModeChanged;

    private enum Mode { Orbit, Fly, Chase }
    private Mode _mode = Mode.Orbit;

    public float GameFovRadians { get; private set; } = RetailFieldOfView.DefaultGameFovRadians;

    private float _aspect = 16f / 9f;

    public CameraController(OrbitCamera orbit, FlyCamera fly)
    {
        Orbit = orbit;
        Fly   = fly;
        ApplyProjection();
    }

    public void ToggleFly()
    {
        _mode = IsFlyMode ? Mode.Orbit : Mode.Fly;
        ModeChanged?.Invoke(IsFlyMode);
    }

    public void EnterChaseMode(ChaseCamera legacy, RetailChaseCamera retail)
    {
        Chase       = legacy;
        RetailChase = retail;
        ApplyProjection();
        _mode       = Mode.Chase;
        ModeChanged?.Invoke(IsChaseMode);
    }

    public void ExitChaseMode()
    {
        Chase       = null;
        RetailChase = null;
        if (_mode == Mode.Orbit)
            return;
        _mode       = Mode.Orbit;
        ModeChanged?.Invoke(false);
    }

    public void SetAspect(float aspect)
    {
        _aspect = aspect;
        ApplyProjection();
    }

    public void SetGameFov(float gameFovRadians)
    {
        GameFovRadians = gameFovRadians;
        ApplyProjection();
    }

    private void ApplyProjection()
    {
        bool fovAccepted = RetailFieldOfView.TryAppliedVerticalFov(
            GameFovRadians, _aspect, out float fovY);

        Orbit.Aspect = _aspect;
        Fly.Aspect   = _aspect;
        if (Chase is { } chase) chase.Aspect = _aspect;
        if (RetailChase is { } retailChase) retailChase.Aspect = _aspect;

        if (!fovAccepted)
            return;
        Orbit.FovY = fovY;
        Fly.FovY   = fovY;
        if (Chase is { } chaseFov) chaseFov.FovY = fovY;
        if (RetailChase is { } retailChaseFov) retailChaseFov.FovY = fovY;
    }

    internal CameraState CaptureState() =>
        new((int)_mode, Chase, RetailChase);

    internal void RestoreState(CameraState state)
    {
        if (state.ModeCode < (int)Mode.Orbit || state.ModeCode > (int)Mode.Chase)
            throw new ArgumentOutOfRangeException(nameof(state));

        Chase = state.Chase;
        RetailChase = state.RetailChase;
        ApplyProjection();
        _mode = (Mode)state.ModeCode;
        ModeChanged?.Invoke(IsFlyMode || IsChaseMode);
    }
}

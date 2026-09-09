using System.Globalization;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.Rendering;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Input;

internal interface IRawPointerSurface
{
    void AddMouseMove(Action<Vector2> callback);
    void RemoveMouseMove(Action<Vector2> callback);
}

internal sealed class SilkRawPointerSurface : IRawPointerSurface
{
    private readonly IMouse _mouse;
    private Action<Vector2>? _callback;
    private readonly Action<IMouse, Vector2> _mouseMove;

    public SilkRawPointerSurface(IMouse mouse)
    {
        _mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
        _mouseMove = OnMouseMove;
    }

    public void AddMouseMove(Action<Vector2> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_callback is not null)
            throw new InvalidOperationException("The raw mouse surface is already attached.");

        _callback = callback;
        _mouse.MouseMove += _mouseMove;
    }

    public void RemoveMouseMove(Action<Vector2> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!ReferenceEquals(_callback, callback))
            return;

        _mouse.MouseMove -= _mouseMove;
        _callback = null;
    }

    private void OnMouseMove(IMouse _, Vector2 position) =>
        _callback?.Invoke(position);
}

internal interface IPointerCursorModeTarget
{
    CursorMode CursorMode { get; set; }
}

internal sealed class SilkPointerCursorModeTarget(IMouse mouse)
    : IPointerCursorModeTarget
{
    private readonly IMouse _mouse = mouse
        ?? throw new ArgumentNullException(nameof(mouse));

    public CursorMode CursorMode
    {
        get => _mouse.Cursor.CursorMode;
        set => _mouse.Cursor.CursorMode = value;
    }
}

internal interface IPointerSensitivityCommands
{
    string AdjustSensitivity(float factor);
}

internal sealed class CameraPointerInputController
    : IDisposable, IPointerSensitivityCommands
{
    private readonly IReadOnlyList<IRawPointerSurface> _surfaces;
    private readonly IPointerCursorModeTarget _cursor;
    private readonly HostQuiescenceGate _quiescence;
    private readonly IInputCaptureSource _capture;
    private readonly LocalPlayerModeState _playerMode;
    private readonly CameraController _camera;
    private readonly ChaseCameraInputState _chase;
    private readonly IMouseSource _mouse;
    private readonly PointerPositionState _pointer;
    private readonly IInputMonotonicClock _clock;
    private readonly Action<Vector2> _mouseMoved;
    private readonly Action<bool> _cameraModeChanged;
    private readonly bool[] _surfaceAttached;
    private ResourceShutdownTransaction? _detach;
    private GameplayInputFrameController? _gameplayFrame;
    private bool _cameraAttached;
    private bool _attachStarted;
    private int _disposeRequested;
    private int _active;
    private float _flySensitivity = 1f;
    private float _orbitSensitivity = 1f;

    public CameraPointerInputController(
        IReadOnlyList<IRawPointerSurface> surfaces,
        IPointerCursorModeTarget cursor,
        HostQuiescenceGate quiescence,
        IInputCaptureSource capture,
        LocalPlayerModeState playerMode,
        CameraController camera,
        ChaseCameraInputState chase,
        IMouseSource mouse,
        PointerPositionState pointer,
        IInputMonotonicClock clock)
    {
        _surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        if (_surfaces.Any(static surface => surface is null))
            throw new ArgumentException("Raw pointer surfaces cannot contain null.", nameof(surfaces));
        _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _playerMode = playerMode ?? throw new ArgumentNullException(nameof(playerMode));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _chase = chase ?? throw new ArgumentNullException(nameof(chase));
        _mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
        _pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _surfaceAttached = new bool[_surfaces.Count];
        _mouseMoved = OnMouseMoved;
        _cameraModeChanged = OnCameraModeChanged;
    }

    public static CameraPointerInputController Create(
        IReadOnlyList<IMouse> mice,
        HostQuiescenceGate quiescence,
        IInputCaptureSource capture,
        LocalPlayerModeState playerMode,
        CameraController camera,
        ChaseCameraInputState chase,
        IMouseSource mouse,
        PointerPositionState pointer,
        IInputMonotonicClock clock)
    {
        ArgumentNullException.ThrowIfNull(mice);
        if (mice.Count == 0)
            throw new ArgumentException("At least one mouse is required.", nameof(mice));

        return new CameraPointerInputController(
            mice.Select(static candidate =>
                (IRawPointerSurface)new SilkRawPointerSurface(candidate)).ToArray(),
            new SilkPointerCursorModeTarget(mice[0]),
            quiescence,
            capture,
            playerMode,
            camera,
            chase,
            mouse,
            pointer,
            clock);
    }

    public bool IsDisposalComplete =>
        !_cameraAttached && _surfaceAttached.All(static attached => !attached);

    public float ActiveSensitivity
    {
        get
        {
            if (_playerMode.IsPlayerMode && _camera.IsChaseMode)
                return _chase.Sensitivity;
            if (_camera.IsFlyMode)
                return _flySensitivity;
            return _orbitSensitivity;
        }
    }

    public bool RmbOrbitHeld => _chase.RmbOrbitHeld;

    public void AttachRaw()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);
        if (_attachStarted)
            throw new InvalidOperationException("Raw pointer attachment has already started.");
        _attachStarted = true;

        try
        {
            for (int i = 0; i < _surfaces.Count; i++)
            {
                _surfaceAttached[i] = true;
                _surfaces[i].AddMouseMove(_mouseMoved);
            }

            _cameraAttached = true;
            _camera.ModeChanged += _cameraModeChanged;
            Volatile.Write(ref _active, 1);
        }
        catch (Exception attachError)
        {
            Deactivate();
            try
            {
                EnsureDetachTransaction().CompleteOrThrow();
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Raw pointer registration and rollback both failed.",
                    new InvalidOperationException(
                        "Raw pointer registration failed.", attachError),
                    rollbackError);
            }

            throw new InvalidOperationException(
                "Raw pointer registration failed and was rolled back.",
                attachError);
        }
    }

    public void BindGameplayFrame(GameplayInputFrameController gameplayFrame)
    {
        ArgumentNullException.ThrowIfNull(gameplayFrame);
        if (_gameplayFrame is not null
            && !ReferenceEquals(_gameplayFrame, gameplayFrame))
        {
            throw new InvalidOperationException(
                "The raw pointer owner is already bound to another gameplay frame.");
        }

        _gameplayFrame = gameplayFrame;
    }

    public IDisposable BindGameplayFrameOwned(
        GameplayInputFrameController gameplayFrame)
    {
        ArgumentNullException.ThrowIfNull(gameplayFrame);
        if (_gameplayFrame is not null)
        {
            throw new InvalidOperationException(
                "The raw pointer owner already has a gameplay frame.");
        }

        _gameplayFrame = gameplayFrame;
        return new GameplayFrameBinding(this, gameplayFrame);
    }

    public void UnbindGameplayFrame(GameplayInputFrameController gameplayFrame)
    {
        ArgumentNullException.ThrowIfNull(gameplayFrame);
        if (ReferenceEquals(_gameplayFrame, gameplayFrame))
            _gameplayFrame = null;
    }

    private sealed class GameplayFrameBinding : IDisposable
    {
        private CameraPointerInputController? _owner;
        private readonly GameplayInputFrameController _expected;

        public GameplayFrameBinding(
            CameraPointerInputController owner,
            GameplayInputFrameController expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?
                .UnbindGameplayFrame(_expected);
    }

    public void HandleFocusChanged(bool focused)
    {
        if (!focused)
            _gameplayFrame?.EndMouseLook();
    }

    public void HandleScroll(InputAction action)
    {
        float direction = action switch
        {
            InputAction.ScrollUp => 1f,
            InputAction.ScrollDown => -1f,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        if (_playerMode.IsPlayerMode && _camera.IsChaseMode)
        {
            if (CameraDiagnostics.UseRetailChaseCamera && _chase.Retail is not null)
                _chase.Retail.AdjustDistance(-direction * 0.8f);
            else
                _chase.Legacy?.AdjustDistance(-direction * 0.8f);
        }
        else if (!_camera.IsFlyMode)
        {
            _camera.Orbit.Distance = Math.Clamp(
                _camera.Orbit.Distance - direction * 20f,
                50f,
                2000f);
        }
    }

    public bool HandleCameraAction(
        InputAction action,
        ActivationType activation)
    {
        if (activation != ActivationType.Press
            || !_playerMode.IsPlayerMode
            || !_camera.IsChaseMode)
        {
            return false;
        }

        bool handled = action is
            InputAction.CameraViewDefault
            or InputAction.CameraAlternateViewDefault
            or InputAction.CameraViewFirstPerson
            or InputAction.CameraAlternateViewFirstPerson
            or InputAction.CameraViewLookDown
            or InputAction.CameraAlternateViewLookDown
            or InputAction.CameraViewMapMode
            or InputAction.CameraAlternateViewMapMode;
        if (!handled)
            return false;

        ApplyCameraPreset(_chase.Retail, action);
        ApplyCameraPreset(_chase.Legacy, action);
        return true;
    }

    private static void ApplyCameraPreset(
        RetailChaseCamera? camera,
        InputAction action)
    {
        if (camera is null)
            return;
        switch (action)
        {
            case InputAction.CameraViewDefault:
            case InputAction.CameraAlternateViewDefault:
                camera.SetRetailDefaultView();
                break;
            case InputAction.CameraViewFirstPerson:
            case InputAction.CameraAlternateViewFirstPerson:
                camera.SetRetailFirstPersonView();
                break;
            case InputAction.CameraViewLookDown:
            case InputAction.CameraAlternateViewLookDown:
                camera.ToggleRetailLookDownView();
                break;
            case InputAction.CameraViewMapMode:
            case InputAction.CameraAlternateViewMapMode:
                camera.ToggleRetailMapModeView();
                break;
        }
    }

    private static void ApplyCameraPreset(
        ChaseCamera? camera,
        InputAction action)
    {
        if (camera is null)
            return;
        switch (action)
        {
            case InputAction.CameraViewDefault:
            case InputAction.CameraAlternateViewDefault:
                camera.SetRetailDefaultView();
                break;
            case InputAction.CameraViewFirstPerson:
            case InputAction.CameraAlternateViewFirstPerson:
                camera.SetRetailFirstPersonView();
                break;
            case InputAction.CameraViewLookDown:
            case InputAction.CameraAlternateViewLookDown:
                camera.ToggleRetailLookDownView();
                break;
            case InputAction.CameraViewMapMode:
            case InputAction.CameraAlternateViewMapMode:
                camera.ToggleRetailMapModeView();
                break;
        }
    }

    public string AdjustSensitivity(float factor)
    {
        string mode;
        float current;
        if (_playerMode.IsPlayerMode && _camera.IsChaseMode)
        {
            mode = "Chase";
            current = _chase.Sensitivity;
        }
        else if (_camera.IsFlyMode)
        {
            mode = "Fly";
            current = _flySensitivity;
        }
        else
        {
            mode = "Orbit";
            current = _orbitSensitivity;
        }

        float next = MathF.Min(3f, MathF.Max(0.005f, current * factor));
        if (mode == "Chase")
            _chase.Sensitivity = next;
        else if (mode == "Fly")
            _flySensitivity = next;
        else
            _orbitSensitivity = next;

        return string.Create(CultureInfo.InvariantCulture, $"{mode} sens {next:F3}x");
    }

    public void Deactivate()
    {
        Interlocked.Exchange(ref _active, 0);
        _chase.RmbOrbitHeld = false;
    }

    /// <summary>Release presentation state only after live-session retirement.</summary>
    public void ReleaseMouseLookAfterSessionRetirement() =>
        _gameplayFrame?.EndMouseLook();

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        Deactivate();
        EnsureDetachTransaction().CompleteOrThrow();
    }

    private void OnMouseMoved(Vector2 position) =>
        _quiescence.Invoke(() => ProcessMouseMove(position));

    private void ProcessMouseMove(Vector2 position)
    {
        if (Volatile.Read(ref _active) == 0)
            return;

        if (_capture.WantCaptureMouse)
        {
            _pointer.X = position.X;
            _pointer.Y = position.Y;
            return;
        }

        float dx = position.X - _pointer.X;
        float dy = position.Y - _pointer.Y;

        if (_playerMode.IsPlayerMode && _camera.IsChaseMode
            && _chase.Legacy is not null)
        {
            float sensitivity = _chase.Sensitivity;
            if (_gameplayFrame?.MouseLookActive == true)
            {
                _gameplayFrame.QueueRawMouseDelta(dx, dy);
            }
            else if (_chase.RmbOrbitHeld)
            {
                if (CameraDiagnostics.UseRetailChaseCamera
                    && _chase.Retail is not null)
                {
                    var (filteredDx, filteredDy) = _chase.Retail.FilterMouseDelta(
                        rawX: dx,
                        rawY: dy,
                        weight: 0.5f,
                        nowSec: _clock.NowSeconds);
                    const float retailMouseScale = 0.0666666701f;
                    _chase.Retail.AdjustYaw(-filteredDx * sensitivity * retailMouseScale);
                    _chase.Retail.AdjustPitch(filteredDy * sensitivity * retailMouseScale);
                }
                else
                {
                    _chase.Legacy.YawOffset -= dx * 0.004f * sensitivity;
                    _chase.Legacy.AdjustPitch(dy * 0.003f * sensitivity);
                }
            }
        }
        else if (_camera.IsFlyMode)
        {
            _camera.Fly.Look(dx * _flySensitivity, dy * _flySensitivity);
        }
        else if (_mouse.IsHeld(MouseButton.Left))
        {
            _camera.Orbit.Yaw -= dx * 0.005f * _orbitSensitivity;
            _camera.Orbit.Pitch = Math.Clamp(
                _camera.Orbit.Pitch + dy * 0.005f * _orbitSensitivity,
                0.1f,
                1.5f);
        }

        _pointer.X = position.X;
        _pointer.Y = position.Y;
    }

    private void OnCameraModeChanged(bool _) =>
        _quiescence.Invoke(ApplyCursorForCameraMode);

    private void ApplyCursorForCameraMode()
    {
        if (Volatile.Read(ref _active) == 0)
            return;

        if (_gameplayFrame?.MouseLookActive == true && !_camera.IsChaseMode)
            _gameplayFrame.EndMouseLook();

        _cursor.CursorMode = _camera.IsFlyMode
            ? CursorMode.Raw
            : CursorMode.Normal;
    }

    private ResourceShutdownTransaction EnsureDetachTransaction()
    {
        if (_detach is not null)
            return _detach;

        var operations = new List<ResourceShutdownOperation>
        {
            new("camera mode", RemoveCameraMode),
        };
        for (int i = _surfaces.Count - 1; i >= 0; i--)
        {
            int index = i;
            operations.Add(new ResourceShutdownOperation(
                $"raw mouse {index}",
                () => RemoveSurface(index)));
        }

        _detach = new ResourceShutdownTransaction(
            new ResourceShutdownStage("raw pointer callbacks", operations.ToArray()));
        return _detach;
    }

    private void RemoveCameraMode()
    {
        if (!_cameraAttached)
            return;
        _camera.ModeChanged -= _cameraModeChanged;
        _cameraAttached = false;
    }

    private void RemoveSurface(int index)
    {
        if (!_surfaceAttached[index])
            return;
        _surfaces[index].RemoveMouseMove(_mouseMoved);
        _surfaceAttached[index] = false;
    }
}

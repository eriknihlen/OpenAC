using System;
using System.Numerics;

namespace AcDream.App.Rendering;

public sealed class ChaseCamera : ICamera
{
    private const float RetailDefaultBack = 2.5f;
    private const float RetailDefaultUp = 0.75f;
    private bool _lookingDown;
    private bool _mapMode;
    private bool _inHead;
    private bool _savedInHead;
    private float _savedDistance;
    private float _savedPitch;
    private float _savedYawOffset;
    private Vector3? _targetDirectionLocal;
    private Vector3? _savedTargetDirectionLocal;

    public bool IsLookingDown => _lookingDown;
    public bool IsMapMode => _mapMode;
    public bool IsInHead => _inHead;
    public Vector3 Position { get; private set; }
    public float Aspect { get; set; } = 16f / 9f;
    public float FovY { get; set; } = RetailFieldOfView.DefaultAppliedFovY;

    public float Distance { get; set; } = 8f;
    public const float DistanceMin = 2f;
    public const float DistanceMax = 40f;

    public float Pitch { get; set; } = 0.35f;  // ~20 degrees

    public float YawOffset { get; set; } = 0f;

    /// <summary>Vertical offset from the player's feet to the look-at point (eye height).</summary>
    public float EyeHeight { get; set; } = 1.5f;

    private const float PitchMin = -0.7f;
    private const float PitchMax = 1.4f;

    private float _playerYaw;
    private Vector3 _lookAt;

    private float _trackedZ;
    private bool _trackedZInitialised;

    public Matrix4x4 View =>
        Matrix4x4.CreateLookAt(Position, _lookAt, Vector3.UnitZ);

    public Matrix4x4 Projection =>
        Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, 0.1f, 5000f);

    public void Update(Vector3 playerPosition, float playerYaw, bool isOnGround = true, float dt = 1f / 60f)
    {
        _playerYaw = playerYaw;

        if (!_trackedZInitialised)
        {
            _trackedZ = playerPosition.Z;
            _trackedZInitialised = true;
        }
        else if (isOnGround)
        {
            _trackedZ = playerPosition.Z;
        }
        else if (playerPosition.Z < _trackedZ)
        {
            _trackedZ = playerPosition.Z;
        }
        // else: airborne and rising — keep _trackedZ pinned.

        _lookAt = playerPosition + new Vector3(0f, 0f, EyeHeight);

        float effectiveYaw = playerYaw + YawOffset;
        float forwardX = MathF.Cos(effectiveYaw);
        float forwardY = MathF.Sin(effectiveYaw);

        float horizontalDist = Distance * MathF.Cos(Pitch);
        float verticalDist = Distance * MathF.Sin(Pitch);

        if (_inHead)
        {
            Vector3 forward = new(MathF.Cos(playerYaw), MathF.Sin(playerYaw), 0f);
            Position = new Vector3(
                playerPosition.X,
                playerPosition.Y,
                _trackedZ + EyeHeight) + forward * 0.18f;
            _lookAt = Position + forward;
        }
        else if (_targetDirectionLocal is { } localDirection)
        {
            Vector3 pivot = new(playerPosition.X, playerPosition.Y, _trackedZ + EyeHeight);
            var directedPose = RetailChaseCamera.ComputeTargetDirectionPose(
                pivot,
                new Vector3(MathF.Cos(playerYaw), MathF.Sin(playerYaw), 0f),
                Distance,
                Pitch,
                localDirection);
            Position = directedPose.eye;
            Vector3 direction = directedPose.forward;
            _lookAt = Position + direction;
        }
        else
        {
            Position = new Vector3(
                playerPosition.X - forwardX * horizontalDist,
                playerPosition.Y - forwardY * horizontalDist,
                _trackedZ + EyeHeight + verticalDist);   // ← uses tracked Z (pinned to ground while airborne)
        }
    }

    /// <summary>
    /// Adjust pitch by a delta (from mouse Y movement).
    /// </summary>
    public void AdjustPitch(float delta)
    {
        ExitLookDownForAdjustment();
        ExitInHeadForAdjustment();
        Pitch = Math.Clamp(Pitch + delta, PitchMin, PitchMax);
    }

    public void AdjustDistance(float delta)
    {
        ExitLookDownForAdjustment();
        ExitInHeadForAdjustment();
        Distance = Math.Clamp(Distance + delta, DistanceMin, DistanceMax);
    }

    public void SetRetailDefaultView()
    {
        _lookingDown = false;
        _mapMode = false;
        _inHead = false;
        _targetDirectionLocal = null;
        YawOffset = 0f;
        EyeHeight = 1.5f;
        SetViewerOffset(RetailDefaultBack, RetailDefaultUp);
    }

    public void SetRetailFirstPersonView()
    {
        _lookingDown = false;
        _mapMode = false;
        _inHead = true;
        _targetDirectionLocal = null;
        YawOffset = 0f;
        Distance = 0.18f;
        Pitch = 0f;
    }

    public void ToggleRetailLookDownView()
    {
        if (_lookingDown)
        {
            RestoreLookDownView();
            return;
        }
        SaveLookDownView();
        _lookingDown = true;
        _mapMode = false;
        _inHead = false;
        _targetDirectionLocal = new Vector3(0f, 0.5f, -1.8f);
        SetViewerOffset(2f, RetailDefaultUp);
    }

    public void ToggleRetailMapModeView()
    {
        if (_mapMode)
        {
            RestoreLookDownView();
            return;
        }
        if (!_lookingDown)
            SaveLookDownView();
        _lookingDown = true;
        _mapMode = true;
        _inHead = false;
        _targetDirectionLocal = new Vector3(0f, 0.5f, -1.8f);
        SetViewerOffset(450f, RetailDefaultUp);
    }

    private void SaveLookDownView()
    {
        _savedDistance = Distance;
        _savedPitch = Pitch;
        _savedYawOffset = YawOffset;
        _savedTargetDirectionLocal = _targetDirectionLocal;
        _savedInHead = _inHead;
    }

    private void RestoreLookDownView()
    {
        Distance = _savedDistance;
        Pitch = _savedPitch;
        YawOffset = _savedYawOffset;
        _targetDirectionLocal = _savedTargetDirectionLocal;
        _inHead = _savedInHead;
        _lookingDown = false;
        _mapMode = false;
    }

    private void ExitLookDownForAdjustment()
    {
        if (_lookingDown)
            RestoreLookDownView();
    }

    private void ExitInHeadForAdjustment()
    {
        if (!_inHead)
            return;
        _inHead = false;
        Distance = DistanceMin;
    }

    private void SetViewerOffset(float back, float up)
    {
        Distance = MathF.Sqrt(back * back + up * up);
        Pitch = MathF.Atan2(up, back);
    }
}

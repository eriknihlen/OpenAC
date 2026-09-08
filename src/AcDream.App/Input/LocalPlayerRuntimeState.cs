using AcDream.Core.Physics;
using AcDream.App.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Input;

internal interface ILocalPlayerIdentitySource
{
    uint ServerGuid { get; }
}

/// <summary>
/// Session-scoped identity slot shared by focused update owners.
/// </summary>
internal sealed class LocalPlayerIdentityState : ILocalPlayerIdentitySource
{
    private readonly RuntimeLocalPlayerIdentityState _runtime;

    public LocalPlayerIdentityState()
        : this(new RuntimeLocalPlayerIdentityState())
    {
    }

    public LocalPlayerIdentityState(RuntimeLocalPlayerIdentityState runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public uint ServerGuid
    {
        get => _runtime.ServerGuid;
        set => _runtime.ServerGuid = value;
    }
}

internal interface ILocalPlayerPhysicsHostSource
{
    EntityPhysicsHost? Host { get; }
}

/// <summary>
/// The one mutable local physics-host slot. Player-mode lifecycle owns
/// assignment; teleport and update owners receive the read-only seam.
/// </summary>
internal sealed class LocalPlayerPhysicsHostSlot : ILocalPlayerPhysicsHostSource
{
    public EntityPhysicsHost? Host { get; set; }
}

internal interface ILocalPlayerModeSource
{
    bool IsPlayerMode { get; }
    bool ChaseModeEverEntered { get; }
}

internal sealed class LocalPlayerModeState : ILocalPlayerModeSource
{
    public bool IsPlayerMode { get; set; }
    public bool ChaseModeEverEntered { get; set; }

    public void ResetSession()
    {
        IsPlayerMode = false;
        ChaseModeEverEntered = false;
    }
}

internal interface IViewportAspectSource
{
    float Aspect { get; }
}

/// <summary>Framebuffer aspect published by the window host.</summary>
internal sealed class ViewportAspectState : IViewportAspectSource
{
    public float Aspect { get; private set; } = 16f / 9f;

    public void Update(int width, int height)
    {
        if (width > 0 && height > 0)
            Aspect = width / (float)height;
    }
}

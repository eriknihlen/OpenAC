namespace AcDream.Core.World;

public enum TeleportAnimState
{
    Off            = 0,
    WorldFadeOut   = 1,
    TunnelFadeIn   = 2,
    Tunnel         = 3,
    TunnelContinue = 4,
    TunnelFadeOut  = 5,
    WorldFadeIn    = 6,
}

/// <summary>Why the teleport was triggered — drives per-entry start state (spec §2.5).</summary>
public enum TeleportEntryKind { Portal, Login, Death, Logout }

public enum TeleportAnimEvent
{
    PlayEnterSound,    // Begin(): sound_ui_enter_portal
    EnterTunnel,       // First tunnel-family frame: portal viewport replaces world
    Place,             // Tunnel -> TunnelContinue: world loaded; place the player
    PlayExitSound,     // TunnelFadeOut -> WorldFadeIn: sound_ui_exit_portal
    FireLoginComplete, // WorldFadeIn -> Off: send GameAction 0xA1
}

/// <summary>Immutable per-frame snapshot from the sequencer.</summary>
public readonly record struct TeleportAnimSnapshot(
    TeleportAnimState State,
    float ViewPlaneBlend,  // 0 = game view distance, 1 = transition distance
    bool  ShowTunnel,      // true during TunnelFadeIn..TunnelFadeOut
    bool  ShowPleaseWait); // true during TunnelContinue only

public sealed class TeleportAnimSequencer
{
    public const float FadeTime       = 1.0f;
    public const float MinContinue    = 2.0f;
    public const float MaxContinue    = 5.0f;
    public const float TunnelFramesPerSecond = 40.0f;
    public const int TunnelEndFrame = 120;
    public const float ExitWindowLow = FadeTime + 0.1f;
    public const float ExitWindowHigh = FadeTime + 0.3f;
    internal const short LastVisibleOutgoingAnimationLevel = 1001;

    private static readonly short[] RetailAnimationLevels = BuildRetailAnimationLevels();

    private TeleportAnimState _state          = TeleportAnimState.Off;
    private float             _elapsed        = 0f;
    private float             _continueElapsed = 0f;  // tracks time inside TunnelContinue
    private bool  _enterSoundPending    = false;
    private bool  _enterTunnelPending   = false;

    public bool              IsActive => _state != TeleportAnimState.Off;
    public TeleportAnimState State    => _state;

    /// <summary>
    /// Start the animation. Portal/Login/Death enter at Tunnel (skipping world-fade-out);
    /// Logout enters at WorldFadeOut (spec §2.5).
    /// </summary>
    public void Begin(TeleportEntryKind kind)
    {
        _state = kind == TeleportEntryKind.Logout
            ? TeleportAnimState.WorldFadeOut
            : TeleportAnimState.Tunnel;
        _elapsed              = 0f;
        _continueElapsed      = 0f;
        _enterSoundPending    = true;
        _enterTunnelPending   = _state == TeleportAnimState.Tunnel; // true for Portal/Login/Death
    }

    public void Reset()
    {
        _state = TeleportAnimState.Off;
        _elapsed = 0f;
        _continueElapsed = 0f;
        _enterSoundPending = false;
        _enterTunnelPending = false;
    }

    public (TeleportAnimSnapshot snapshot, IReadOnlyList<TeleportAnimEvent> events)
        Tick(
            float dt,
            bool worldReady,
            int tunnelAnimationFrame = 72,
            bool holdInTunnel = false)
    {
        var evts = new List<TeleportAnimEvent>();

        if (_enterSoundPending)  { evts.Add(TeleportAnimEvent.PlayEnterSound); _enterSoundPending = false; }
        if (_enterTunnelPending) { evts.Add(TeleportAnimEvent.EnterTunnel);    _enterTunnelPending = false; }

        _elapsed += dt;

        // State-machine transitions:
        switch (_state)
        {
            case TeleportAnimState.WorldFadeOut:
                if (OutgoingViewportReachedTerminalProjection())
                    Advance(TeleportAnimState.TunnelFadeIn, enterTunnel: true);
                break;

            case TeleportAnimState.TunnelFadeIn:
                if (_elapsed >= FadeTime)
                    Advance(TeleportAnimState.Tunnel, enterTunnel: false);
                break;

            case TeleportAnimState.Tunnel:
                // Hold here until worldReady (EndTeleportAnimation analogue).
                if (worldReady && !holdInTunnel)
                {
                    evts.Add(TeleportAnimEvent.Place);
                    Advance(TeleportAnimState.TunnelContinue, enterTunnel: false);
                    _continueElapsed = 0f;
                }
                break;

            case TeleportAnimState.TunnelContinue:
                _continueElapsed += dt;
                uint remainingFrames = unchecked((uint)TunnelEndFrame - (uint)tunnelAnimationFrame);
                float remainingSeconds = remainingFrames / TunnelFramesPerSecond;
                bool minMet = _continueElapsed >= MinContinue
                    && remainingSeconds > ExitWindowLow
                    && remainingSeconds < ExitWindowHigh;
                bool maxForce = _continueElapsed >= MaxContinue;
                if (minMet || maxForce)
                    Advance(TeleportAnimState.TunnelFadeOut, enterTunnel: false);
                break;

            case TeleportAnimState.TunnelFadeOut:
                if (OutgoingViewportReachedTerminalProjection())
                {
                    Advance(TeleportAnimState.WorldFadeIn, enterTunnel: false);
                    evts.Add(TeleportAnimEvent.PlayExitSound);
                }
                break;

            case TeleportAnimState.WorldFadeIn:
                if (_elapsed >= FadeTime)
                {
                    Advance(TeleportAnimState.Off, enterTunnel: false);
                    evts.Add(TeleportAnimEvent.FireLoginComplete);
                }
                break;

            case TeleportAnimState.Off:
            default:
                break;
        }

        return (BuildSnapshot(), evts);
    }

    private bool OutgoingViewportReachedTerminalProjection()
        => GetRetailAnimationLevel(_elapsed / FadeTime) > LastVisibleOutgoingAnimationLevel;

    private void Advance(TeleportAnimState next, bool enterTunnel)
    {
        _state   = next;
        _elapsed = 0f;
        if (enterTunnel) _enterTunnelPending = true;
    }

    private TeleportAnimSnapshot BuildSnapshot()
    {
        float viewBlend  = ComputeViewPlaneBlend(_state, _elapsed);
        bool showTunnel  = _state is TeleportAnimState.TunnelFadeIn
                                   or TeleportAnimState.Tunnel
                                   or TeleportAnimState.TunnelContinue
                                   or TeleportAnimState.TunnelFadeOut;
        bool pleaseWait  = _state == TeleportAnimState.TunnelContinue;
        return new TeleportAnimSnapshot(_state, viewBlend, showTunnel, pleaseWait);
    }

    public static short GetRetailAnimationLevel(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int negativeIndex = (int)Math.Truncate(-99.0 * t);
        return RetailAnimationLevels[-negativeIndex];
    }

    private static float ComputeViewPlaneBlend(TeleportAnimState state, float elapsed)
    {
        float t = Math.Clamp(elapsed / FadeTime, 0f, 1f);
        float level = GetRetailAnimationLevel(t) / 1024f;
        return state switch
        {
            // Normal game view distance -> transition distance.
            TeleportAnimState.WorldFadeOut   => level,
            TeleportAnimState.TunnelFadeOut  => level,
            // Transition distance -> normal game view distance.
            TeleportAnimState.TunnelFadeIn   => 1f - level,
            TeleportAnimState.WorldFadeIn    => 1f - level,
            // Stable viewport states use the ordinary game projection.
            TeleportAnimState.Tunnel         => 0f,
            TeleportAnimState.TunnelContinue => 0f,
            _                                => 0f,
        };
    }

    private static short[] BuildRetailAnimationLevels()
    {
        const double retailPi = 3.1415920000000002d;
        const double reciprocal99 = 0.010101010101010102d;
        const double scale = 1024.0d;

        var samples = new short[100];
        int total = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = (short)Math.Truncate(Math.Sin(i * retailPi * reciprocal99) * scale);
            samples[i] = sample;
            total += sample;
        }

        int running = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            running += samples[i];
            samples[i] = (short)((running << 10) / total);
        }

        return samples;
    }
}

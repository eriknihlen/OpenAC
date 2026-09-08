using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.World;

public sealed class TeleportAnimSequencerTests
{
    // --- Task 1.1: type presence ---

    [Fact]
    public void Enums_HaveExpectedValues()
    {
        Assert.Equal(0, (int)TeleportAnimState.Off);
        Assert.Equal(1, (int)TeleportAnimState.WorldFadeOut);
        Assert.Equal(2, (int)TeleportAnimState.TunnelFadeIn);
        Assert.Equal(3, (int)TeleportAnimState.Tunnel);
        Assert.Equal(4, (int)TeleportAnimState.TunnelContinue);
        Assert.Equal(5, (int)TeleportAnimState.TunnelFadeOut);
        Assert.Equal(6, (int)TeleportAnimState.WorldFadeIn);

        _ = TeleportEntryKind.Portal;
        _ = TeleportEntryKind.Login;
        _ = TeleportEntryKind.Death;
        _ = TeleportEntryKind.Logout;

        _ = TeleportAnimEvent.PlayEnterSound;
        _ = TeleportAnimEvent.PlayExitSound;
        _ = TeleportAnimEvent.Place;
        _ = TeleportAnimEvent.FireLoginComplete;
        _ = TeleportAnimEvent.EnterTunnel;
    }

    [Fact]
    public void Snapshot_DefaultsAreOff_WithGameViewPlane()
    {
        var snap = new TeleportAnimSnapshot(
            TeleportAnimState.Off, ViewPlaneBlend: 0f, ShowTunnel: false, ShowPleaseWait: false);
        Assert.Equal(TeleportAnimState.Off, snap.State);
        Assert.Equal(0f, snap.ViewPlaneBlend);
        Assert.False(snap.ShowTunnel);
        Assert.False(snap.ShowPleaseWait);
    }

    [Fact]
    public void Reset_CancelsTransitionAndDiscardsPendingEdges()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);

        seq.Reset();
        var (snap, events) = seq.Tick(0f, worldReady: true);

        Assert.False(seq.IsActive);
        Assert.Equal(TeleportAnimState.Off, snap.State);
        Assert.Equal(0f, snap.ViewPlaneBlend);
        Assert.False(snap.ShowTunnel);
        Assert.Empty(events);
    }

    // --- Task 1.2: Begin(), IsActive, initial state ---

    [Theory]
    [InlineData(TeleportEntryKind.Portal,  TeleportAnimState.Tunnel)]
    [InlineData(TeleportEntryKind.Login,   TeleportAnimState.Tunnel)]
    [InlineData(TeleportEntryKind.Death,   TeleportAnimState.Tunnel)]
    [InlineData(TeleportEntryKind.Logout,  TeleportAnimState.WorldFadeOut)]
    public void Begin_SetsCorrectStartState(TeleportEntryKind kind, TeleportAnimState expectedStart)
    {
        var seq = new TeleportAnimSequencer();
        Assert.False(seq.IsActive);

        seq.Begin(kind);

        Assert.True(seq.IsActive);
        Assert.Equal(expectedStart, seq.State);
    }

    [Fact]
    public void Begin_EmitsPlayEnterSoundOnFirstTick()
    {
        // PlayEnterSound is edge-triggered on the first Tick after Begin.
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        var (_, events) = seq.Tick(dt: 0f, worldReady: false);
        Assert.Contains(TeleportAnimEvent.PlayEnterSound, events);
    }


    // Helper: drive the sequencer forward by total elapsed time using small fixed steps.
    private static (TeleportAnimSnapshot snap, List<TeleportAnimEvent> allEvents)
        DriveSeconds(
            TeleportAnimSequencer seq,
            float seconds,
            bool worldReady,
            float step = 0.016f,
            int tunnelAnimationFrame = 72)
    {
        var allEvts = new List<TeleportAnimEvent>();
        float remaining = seconds;
        TeleportAnimSnapshot last = default;
        while (remaining > 0f)
        {
            float dt = Math.Min(step, remaining);
            var (snap, evts) = seq.Tick(dt, worldReady, tunnelAnimationFrame);
            last = snap;
            allEvts.AddRange(evts);
            remaining -= dt;
        }
        return (last, allEvts);
    }

    // --- Logout path: WorldFadeOut -> TunnelFadeIn -> Tunnel ---

    [Fact]
    public void Logout_AfterFadeTime_TransitionsToTunnelFadeIn()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Logout);

        seq.Tick(0f, worldReady: false);

        // Drive just past FadeTime (1.0s)
        DriveSeconds(seq, TeleportAnimSequencer.FadeTime + 0.02f, worldReady: false);

        Assert.Equal(TeleportAnimState.TunnelFadeIn, seq.State);
    }

    [Fact]
    public void Logout_After2xFadeTime_TransitionsToTunnel()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Logout);
        seq.Tick(0f, worldReady: false);

        DriveSeconds(seq, 2f * TeleportAnimSequencer.FadeTime + 0.02f, worldReady: false);

        Assert.Equal(TeleportAnimState.Tunnel, seq.State);
    }

    // --- Portal/Login/Death path: enters at Tunnel ---

    [Fact]
    public void Portal_StartsInTunnel_HoldsWhileNotReady()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);

        // Drive 10s — should not advance past Tunnel while !worldReady
        DriveSeconds(seq, 10f, worldReady: false);

        Assert.Equal(TeleportAnimState.Tunnel, seq.State);
    }

    [Fact]
    public void Portal_WhenWorldReady_TransitionsToTunnelContinue_EmitsEnterTunnelAndPlace()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);

        // The very first tick with worldReady=true should advance Tunnel→TunnelContinue and emit Place.
        var (_, evts) = seq.Tick(0.016f, worldReady: true);

        Assert.Equal(TeleportAnimState.TunnelContinue, seq.State);
        Assert.Contains(TeleportAnimEvent.Place, evts);
    }

    [Fact]
    public void DiagnosticHold_KeepsReadyPortalInStableTunnelUntilReleased()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);

        var (_, heldEvents) = seq.Tick(
            dt: 30f,
            worldReady: true,
            tunnelAnimationFrame: 72,
            holdInTunnel: true);

        Assert.Equal(TeleportAnimState.Tunnel, seq.State);
        Assert.DoesNotContain(TeleportAnimEvent.Place, heldEvents);

        var (_, releasedEvents) = seq.Tick(
            dt: 0f,
            worldReady: true,
            tunnelAnimationFrame: 72,
            holdInTunnel: false);

        Assert.Equal(TeleportAnimState.TunnelContinue, seq.State);
        Assert.Contains(TeleportAnimEvent.Place, releasedEvents);
    }

    // --- TunnelContinue: MIN_CONTINUE hold then TunnelFadeOut ---

    [Fact]
    public void TunnelContinue_DoesNotAdvance_BeforeMinContinue()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);
        // Enter TunnelContinue
        seq.Tick(0.016f, worldReady: true);
        Assert.Equal(TeleportAnimState.TunnelContinue, seq.State);

        // Drive MinContinue - ε — should still be TunnelContinue
        DriveSeconds(seq, TeleportAnimSequencer.MinContinue - 0.1f, worldReady: true);

        Assert.Equal(TeleportAnimState.TunnelContinue, seq.State);
    }

    [Fact]
    public void TunnelContinue_AdvancesToTunnelFadeOut_AfterMinContinue()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);
        seq.Tick(0.016f, worldReady: true); // -> TunnelContinue

        DriveSeconds(seq, TeleportAnimSequencer.MinContinue + 0.05f, worldReady: true);

        Assert.Equal(TeleportAnimState.TunnelFadeOut, seq.State);
    }

    // --- MAX_CONTINUE forces progress even when !worldReady ---

    [Fact]
    public void TunnelContinue_ForcesAdvance_AtMaxContinue_EvenIfNotReady()
    {
        // Simulate: world never becomes fully ready but MAX_CONTINUE forces fade-out
        // This path exercises the safety-net from spec §3.4.
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);
        // Manually push the state to TunnelContinue by passing worldReady=true for one tick,
        // then simulate worldReady toggling back to false.
        seq.Tick(0.016f, worldReady: true); // -> TunnelContinue

        // Drive MaxContinue + ε with worldReady=false (simulating "world never loaded")
        DriveSeconds(
            seq,
            TeleportAnimSequencer.MaxContinue + 0.05f,
            worldReady: false,
            tunnelAnimationFrame: 0);

        Assert.Equal(TeleportAnimState.TunnelFadeOut, seq.State);
    }

    [Theory]
    [InlineData(68, false)]
    [InlineData(69, true)]  // 1.275 seconds remaining
    [InlineData(72, true)]  // 1.20 seconds remaining
    [InlineData(75, true)]  // 1.125 seconds remaining
    [InlineData(76, false)]
    [InlineData(77, false)] // 1.075 seconds remaining
    [InlineData(121, false)]
    public void TunnelContinue_UsesRetailAnimationExitWindow(int frame, bool shouldFade)
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false, tunnelAnimationFrame: frame);
        seq.Tick(0.016f, worldReady: true, tunnelAnimationFrame: frame);

        DriveSeconds(
            seq,
            TeleportAnimSequencer.MinContinue + 0.01f,
            worldReady: true,
            tunnelAnimationFrame: frame);

        Assert.Equal(
            shouldFade ? TeleportAnimState.TunnelFadeOut : TeleportAnimState.TunnelContinue,
            seq.State);
    }

    [Theory]
    [InlineData(0.00f, 0)]
    [InlineData(0.01f, 0)]
    [InlineData(0.25f, 146)]
    [InlineData(0.50f, 512)]
    [InlineData(0.75f, 877)]
    [InlineData(0.99f, 1024)]
    [InlineData(1.00f, 1024)]
    public void RetailAnimationLevel_MatchesGoldenTable(float t, short expected)
    {
        Assert.Equal(expected, TeleportAnimSequencer.GetRetailAnimationLevel(t));
    }

    [Theory]
    [InlineData(240f)]
    [InlineData(700f)]
    [InlineData(2000f)]
    public void WorldFadeOut_DoesNotPublishTerminalProjectionOnOutgoingWorld(float framesPerSecond)
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Logout);
        seq.Tick(0f, worldReady: false);

        AssertOutgoingViewportRetiresBeforeTerminal(
            seq,
            TeleportAnimState.WorldFadeOut,
            framesPerSecond);
    }

    [Theory]
    [InlineData(240f)]
    [InlineData(700f)]
    [InlineData(2000f)]
    public void TunnelFadeOut_DoesNotPublishTerminalProjectionOnFiniteTunnel(float framesPerSecond)
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);
        seq.Tick(0.016f, worldReady: true);
        DriveSeconds(
            seq,
            TeleportAnimSequencer.MinContinue + 0.05f,
            worldReady: true,
            step: 1f / framesPerSecond);
        Assert.Equal(TeleportAnimState.TunnelFadeOut, seq.State);

        AssertOutgoingViewportRetiresBeforeTerminal(
            seq,
            TeleportAnimState.TunnelFadeOut,
            framesPerSecond);
    }

    [Fact]
    public void TunnelFadeOut_SwapsAfterLastFullViewportCaptureLevel()
    {
        Assert.Equal((short)1001, TeleportAnimSequencer.LastVisibleOutgoingAnimationLevel);

        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);
        seq.Tick(0.016f, worldReady: true);
        seq.Tick(
            TeleportAnimSequencer.MinContinue,
            worldReady: true,
            tunnelAnimationFrame: 72);
        Assert.Equal(TeleportAnimState.TunnelFadeOut, seq.State);

        float lastSafeTime = Enumerable.Range(0, 1001)
            .Select(static i => i / 1000f)
            .First(t => TeleportAnimSequencer.GetRetailAnimationLevel(t)
                == TeleportAnimSequencer.LastVisibleOutgoingAnimationLevel);
        float firstUnsafeTime = Enumerable.Range(0, 1001)
            .Select(static i => i / 1000f)
            .First(t => TeleportAnimSequencer.GetRetailAnimationLevel(t)
                > TeleportAnimSequencer.LastVisibleOutgoingAnimationLevel);

        var (lastSafe, _) = seq.Tick(lastSafeTime, worldReady: true);
        Assert.Equal(TeleportAnimState.TunnelFadeOut, lastSafe.State);
        Assert.Equal(
            TeleportAnimSequencer.LastVisibleOutgoingAnimationLevel / 1024f,
            lastSafe.ViewPlaneBlend);

        var (swapped, events) = seq.Tick(
            firstUnsafeTime - lastSafeTime,
            worldReady: true);
        Assert.Equal(TeleportAnimState.WorldFadeIn, swapped.State);
        Assert.False(swapped.ShowTunnel);
        Assert.Contains(TeleportAnimEvent.PlayExitSound, events);
    }

    private static void AssertOutgoingViewportRetiresBeforeTerminal(
        TeleportAnimSequencer seq,
        TeleportAnimState outgoingState,
        float framesPerSecond)
    {
        float dt = 1f / framesPerSecond;
        bool transitioned = false;
        for (int i = 0; i < (int)MathF.Ceiling(framesPerSecond * 1.1f); i++)
        {
            var (snapshot, _) = seq.Tick(dt, worldReady: true);
            if (snapshot.State == outgoingState)
            {
                Assert.True(
                    snapshot.ViewPlaneBlend
                        <= TeleportAnimSequencer.LastVisibleOutgoingAnimationLevel / 1024f,
                    $"Published unsafe outgoing blend {snapshot.ViewPlaneBlend}.");
                continue;
            }

            transitioned = true;
            break;
        }

        Assert.True(transitioned, "The outgoing viewport did not retire.");
    }

    // --- TunnelFadeOut -> WorldFadeIn: PlayExitSound edge event ---

    [Fact]
    public void TunnelFadeOut_EmitsPlayExitSound_OnTransitionToWorldFadeIn()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);
        seq.Tick(0.016f, worldReady: true); // -> TunnelContinue

        // Drive through MinContinue -> TunnelFadeOut
        DriveSeconds(seq, TeleportAnimSequencer.MinContinue + 0.05f, worldReady: true);
        Assert.Equal(TeleportAnimState.TunnelFadeOut, seq.State);

        // Drive through FadeTime -> WorldFadeIn; PlayExitSound should fire on that edge
        var (_, evts) = DriveSeconds(seq, TeleportAnimSequencer.FadeTime + 0.05f, worldReady: true);
        Assert.Equal(TeleportAnimState.WorldFadeIn, seq.State);
        Assert.Contains(TeleportAnimEvent.PlayExitSound, evts);
    }

    // --- WorldFadeIn -> Off: FireLoginComplete edge event ---

    [Fact]
    public void WorldFadeIn_EmitsFireLoginComplete_OnTransitionToOff()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);
        seq.Tick(0.016f, worldReady: true); // -> TunnelContinue
        DriveSeconds(seq, TeleportAnimSequencer.MinContinue + 0.05f, worldReady: true);
        DriveSeconds(seq, TeleportAnimSequencer.FadeTime + 0.05f, worldReady: true); // -> WorldFadeIn

        var (_, evts) = DriveSeconds(seq, TeleportAnimSequencer.FadeTime + 0.05f, worldReady: true);

        Assert.Equal(TeleportAnimState.Off, seq.State);
        Assert.False(seq.IsActive);
        Assert.Contains(TeleportAnimEvent.FireLoginComplete, evts);
    }


    [Fact]
    public void ViewPlaneBlend_IsZeroAtStart_OfWorldFadeOut()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Logout);
        var (snap, _) = seq.Tick(dt: 0f, worldReady: false);
        // At elapsed=0 the override still equals the ordinary game distance.
        Assert.Equal(0f, snap.ViewPlaneBlend, precision: 4);
    }

    [Fact]
    public void ViewPlaneBlend_ReachesLastVisibleLevel_BeforeWorldFadeOutRetires()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Logout);
        seq.Tick(0f, worldReady: false);

        float lastSafeTime = Enumerable.Range(0, 1001)
            .Select(static i => i / 1000f)
            .First(t => TeleportAnimSequencer.GetRetailAnimationLevel(t)
                == TeleportAnimSequencer.LastVisibleOutgoingAnimationLevel);

        // Drive directly to the last full-viewport quantized table level.
        seq.Tick(lastSafeTime, worldReady: false);
        Assert.Equal(TeleportAnimState.WorldFadeOut, seq.State);

        var (snap, _) = seq.Tick(0f, worldReady: false);
        Assert.True(
            snap.ViewPlaneBlend > 0.95f,
            $"Expected ViewPlaneBlend near 1, got {snap.ViewPlaneBlend}");
        Assert.True(
            snap.ViewPlaneBlend
                <= TeleportAnimSequencer.LastVisibleOutgoingAnimationLevel / 1024f);
    }

    [Fact]
    public void ViewPlaneBlend_IsMonotonicallyIncreasing_DuringWorldFadeOut()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Logout);
        seq.Tick(0f, worldReady: false);

        float prev = -1f;
        // Sample 50 steps within FadeTime
        float step = TeleportAnimSequencer.FadeTime / 50f;
        for (int i = 0; i < 48; i++) // stop before transition
        {
            var (snap, _) = seq.Tick(step, worldReady: false);
            if (seq.State != TeleportAnimState.WorldFadeOut) break;
            Assert.True(snap.ViewPlaneBlend >= prev - 0.001f,
                $"View-plane blend decreased: {prev} -> {snap.ViewPlaneBlend} at step {i}");
            prev = snap.ViewPlaneBlend;
        }
    }

    [Fact]
    public void ViewPlaneBlend_IsZero_DuringStableTunnelStates()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);

        // Stable tunnel space uses the ordinary game projection.
        var (snap, _) = seq.Tick(0.016f, worldReady: false);
        Assert.Equal(TeleportAnimState.Tunnel, seq.State);
        Assert.Equal(0f, snap.ViewPlaneBlend);
    }

    [Fact]
    public void ShowTunnel_TrueInTunnelFamilyStates_FalseOtherwise()
    {
        // Logout path: WorldFadeOut -> TunnelFadeIn -> Tunnel -> TunnelContinue -> TunnelFadeOut -> WorldFadeIn -> Off
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Logout);
        seq.Tick(0f, worldReady: false);

        // WorldFadeOut: ShowTunnel = false
        var (snap, _) = seq.Tick(0.016f, worldReady: false);
        Assert.Equal(TeleportAnimState.WorldFadeOut, seq.State);
        Assert.False(snap.ShowTunnel);

        // Advance to TunnelFadeIn
        DriveSeconds(seq, TeleportAnimSequencer.FadeTime, worldReady: false);
        Assert.Equal(TeleportAnimState.TunnelFadeIn, seq.State);
        var (snap2, _) = seq.Tick(0f, worldReady: false);
        Assert.True(snap2.ShowTunnel);

        // Advance to Tunnel
        DriveSeconds(seq, TeleportAnimSequencer.FadeTime, worldReady: false);
        Assert.Equal(TeleportAnimState.Tunnel, seq.State);
        var (snap3, _) = seq.Tick(0f, worldReady: false);
        Assert.True(snap3.ShowTunnel);
    }

    [Fact]
    public void Logout_EntersPortalViewportOnFirstTunnelFadeInTick()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Logout);

        var (crossing, crossingEvents) = seq.Tick(
            TeleportAnimSequencer.FadeTime,
            worldReady: false);
        Assert.Equal(TeleportAnimState.TunnelFadeIn, crossing.State);
        Assert.True(crossing.ShowTunnel);
        Assert.DoesNotContain(TeleportAnimEvent.EnterTunnel, crossingEvents);

        var (firstFadeInFrame, firstFadeInEvents) = seq.Tick(0f, worldReady: false);
        Assert.Equal(TeleportAnimState.TunnelFadeIn, firstFadeInFrame.State);
        Assert.Contains(TeleportAnimEvent.EnterTunnel, firstFadeInEvents);

        var (_, tunnelEvents) = seq.Tick(TeleportAnimSequencer.FadeTime, worldReady: false);
        Assert.Equal(TeleportAnimState.Tunnel, seq.State);
        Assert.DoesNotContain(TeleportAnimEvent.EnterTunnel, tunnelEvents);
    }

    [Fact]
    public void ShowPleaseWait_TrueOnlyInTunnelContinue()
    {
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);
        seq.Tick(0f, worldReady: false);

        // Tunnel: ShowPleaseWait false
        var (snap1, _) = seq.Tick(0.016f, worldReady: false);
        Assert.Equal(TeleportAnimState.Tunnel, seq.State);
        Assert.False(snap1.ShowPleaseWait);

        // Advance to TunnelContinue
        var (snap2, _) = seq.Tick(0.016f, worldReady: true);
        Assert.Equal(TeleportAnimState.TunnelContinue, seq.State);
        Assert.True(snap2.ShowPleaseWait);
    }

    // --- Task 1.5: Full portal event sequence and logout event sequence ---

    private static List<TeleportAnimEvent> RunToOff(
        TeleportEntryKind kind, bool worldReadyAfterFirstTunnel = true, float step = 0.05f)
    {
        var seq     = new TeleportAnimSequencer();
        var allEvts = new List<TeleportAnimEvent>();
        seq.Begin(kind);

        int safetyNet = 5000;
        while (seq.IsActive && --safetyNet > 0)
        {
            // Simulate world becoming ready on first Tick inside Tunnel
            bool ready = worldReadyAfterFirstTunnel && seq.State == TeleportAnimState.Tunnel;
            var (_, evts) = seq.Tick(step, worldReady: ready);
            allEvts.AddRange(evts);
        }
        return allEvts;
    }

    [Fact]
    public void Portal_FullSequence_EventsInOrder()
    {
        var evts = RunToOff(TeleportEntryKind.Portal);

        int enterIdx = evts.IndexOf(TeleportAnimEvent.PlayEnterSound);
        int placeIdx = evts.IndexOf(TeleportAnimEvent.Place);
        int exitIdx  = evts.IndexOf(TeleportAnimEvent.PlayExitSound);
        int loginIdx = evts.IndexOf(TeleportAnimEvent.FireLoginComplete);

        Assert.True(enterIdx >= 0, "PlayEnterSound must fire");
        Assert.True(placeIdx >= 0, "Place must fire");
        Assert.True(exitIdx  >= 0, "PlayExitSound must fire");
        Assert.True(loginIdx >= 0, "FireLoginComplete must fire");

        Assert.True(enterIdx < placeIdx, "PlayEnterSound must precede Place");
        Assert.True(placeIdx < exitIdx,  "Place must precede PlayExitSound");
        Assert.True(exitIdx  < loginIdx, "PlayExitSound must precede FireLoginComplete");
    }

    [Fact]
    public void Logout_FullSequence_EventsInOrder_WithEnterTunnelAfterFades()
    {
        var evts = RunToOff(TeleportEntryKind.Logout);

        int enterIdx  = evts.IndexOf(TeleportAnimEvent.PlayEnterSound);
        int tunnelIdx = evts.IndexOf(TeleportAnimEvent.EnterTunnel);
        int placeIdx  = evts.IndexOf(TeleportAnimEvent.Place);
        int exitIdx   = evts.IndexOf(TeleportAnimEvent.PlayExitSound);
        int loginIdx  = evts.IndexOf(TeleportAnimEvent.FireLoginComplete);

        Assert.True(enterIdx  >= 0, "PlayEnterSound must fire");
        Assert.True(tunnelIdx >= 0, "EnterTunnel must fire (Logout path goes through WorldFadeOut->TunnelFadeIn->Tunnel)");
        Assert.True(placeIdx  >= 0, "Place must fire");
        Assert.True(exitIdx   >= 0, "PlayExitSound must fire");
        Assert.True(loginIdx  >= 0, "FireLoginComplete must fire");

        Assert.True(enterIdx  < tunnelIdx, "PlayEnterSound must precede EnterTunnel");
        Assert.True(tunnelIdx < placeIdx,  "EnterTunnel must precede Place");
        Assert.True(placeIdx  < exitIdx,   "Place must precede PlayExitSound");
        Assert.True(exitIdx   < loginIdx,  "PlayExitSound must precede FireLoginComplete");
    }

    [Fact]
    public void Portal_NoEventsFiredTwice()
    {
        var evts = RunToOff(TeleportEntryKind.Portal);
        Assert.Equal(1, evts.Count(e => e == TeleportAnimEvent.PlayEnterSound));
        Assert.Equal(1, evts.Count(e => e == TeleportAnimEvent.Place));
        Assert.Equal(1, evts.Count(e => e == TeleportAnimEvent.PlayExitSound));
        Assert.Equal(1, evts.Count(e => e == TeleportAnimEvent.FireLoginComplete));
    }

    [Fact]
    public void Death_BehavesIdenticallyToPortal_FullSequence()
    {
        var portalEvts = RunToOff(TeleportEntryKind.Portal);
        var deathEvts  = RunToOff(TeleportEntryKind.Death);
        // Same event sequence (both enter at Tunnel)
        Assert.Equal(portalEvts, deathEvts);
    }

    [Fact]
    public void Login_BehavesIdenticallyToPortal_FullSequence()
    {
        var portalEvts = RunToOff(TeleportEntryKind.Portal);
        var loginEvts  = RunToOff(TeleportEntryKind.Login);
        Assert.Equal(portalEvts, loginEvts);
    }

    [Fact]
    public void AfterOff_IsActiveIsFalse_AndStateIsOff()
    {
        RunToOff(TeleportEntryKind.Portal);
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);

        // Drain to Off
        for (int i = 0; i < 5000 && seq.IsActive; i++)
        {
            bool ready = seq.State == TeleportAnimState.Tunnel;
            seq.Tick(0.05f, worldReady: ready);
        }

        Assert.False(seq.IsActive);
        Assert.Equal(TeleportAnimState.Off, seq.State);
    }

    // --- Task 1.6: EnterTunnel fired for Portal path via Begin() ---

    [Fact]
    public void Portal_EmitsEnterTunnel_OnFirstTick()
    {
        // Portal begins directly in Tunnel; EnterTunnel signals "world is now hidden".
        var seq = new TeleportAnimSequencer();
        seq.Begin(TeleportEntryKind.Portal);

        // First tick: should emit PlayEnterSound AND EnterTunnel (both on entry)
        var (_, evts) = seq.Tick(0f, worldReady: false);
        Assert.Contains(TeleportAnimEvent.PlayEnterSound, evts);
        Assert.Contains(TeleportAnimEvent.EnterTunnel,   evts);
    }
}

using AcDream.App.Streaming;

namespace AcDream.App.Tests.Streaming;

public sealed class WorldRevealReadinessBarrierTests
{
    private sealed class State
    {
        public bool RenderReady;

        /// <summary>
        /// Radius-aware override, so a test can distinguish "the Near
        /// sub-window is published" from "the whole Far window is published".
        /// </summary>
        public Func<int, int, bool>? RenderReadyByRadius;

        public bool SpawnCellReady;
        public bool TerrainReady;
        public bool CompositeReady;
        public bool Unhydratable;
        public int Invalidations;
        public int Preparations;
        public uint PreparedCell;
        public int PreparedRadius = -1;
        public int RenderNearRadius = -1;
        public int RenderFarRadius = -1;
        public int TerrainRadius = -1;

        public StreamingRevealWindow Window { get; set; } =
            new(NearRadius: 1, FarRadius: 1);

        public WorldRevealReadinessBarrier Build() => new(
            revealWindow: () => Window,
            isRenderNeighborhoodReady: (cell, nearRadius, farRadius) =>
            {
                RenderNearRadius = nearRadius;
                RenderFarRadius = farRadius;
                return RenderReadyByRadius?.Invoke(nearRadius, farRadius)
                    ?? RenderReady;
            },
            isSpawnCellReady: _ => SpawnCellReady,
            isTerrainNeighborhoodReady: (cell, radius) =>
            {
                TerrainRadius = radius;
                return TerrainReady;
            },
            areCompositeTexturesReady: () => CompositeReady,
            prepareCompositeTextures: (cell, radius) =>
            {
                Preparations++;
                PreparedCell = cell;
                PreparedRadius = radius;
            },
            invalidateCompositeTextures: () => Invalidations++,
            isSpawnClaimUnhydratable: _ => Unhydratable);
    }

    [Fact]
    public void Begin_InvalidatesPriorDestinationTextureReadiness()
    {
        var state = new State();
        var barrier = state.Build();

        barrier.Begin();

        Assert.Equal(1, state.Invalidations);
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(5, 15)]
    [InlineData(4, 12)]
    public void OutdoorRequiredWindow_IsTheLiveStreamingWindow(
        int nearRadius,
        int farRadius)
    {
        const uint outdoorCell = 0x11340021u;
        var window = new StreamingRevealWindow(nearRadius, farRadius);
        var state = new State { Window = window };
        var barrier = state.Build();

        Assert.False(barrier.IsReady(outdoorCell));

        Assert.Equal(window.FarRadius, state.RenderFarRadius);
        Assert.Equal(window.NearRadius, state.RenderNearRadius);
        Assert.Equal(window.FarRadius, barrier.RequiredRenderRadius(outdoorCell));
        Assert.Equal(window, barrier.RequiredWindow(outdoorCell));
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(5, 15)]
    public void IndoorRequiredWindow_IsZeroRegardlessOfTheStreamingWindow(
        int nearRadius,
        int farRadius)
    {
        const uint indoorCell = 0x11340100u;
        var state = new State
        {
            Window = new StreamingRevealWindow(nearRadius, farRadius),
        };
        var barrier = state.Build();

        Assert.Equal(
            new StreamingRevealWindow(0, 0),
            barrier.RequiredWindow(indoorCell));
        Assert.Equal(0, barrier.RequiredRenderRadius(indoorCell));
    }

    [Fact]
    public void RequiredWindow_IsRereadOnEveryEvaluationWithoutReconstruction()
    {
        const uint outdoorCell = 0x11340021u;
        var state = new State { Window = new StreamingRevealWindow(3, 8) };
        var barrier = state.Build();

        barrier.Evaluate(outdoorCell);
        Assert.Equal(8, state.RenderFarRadius);

        state.Window = new StreamingRevealWindow(5, 15);
        WorldRevealReadinessSnapshot second = barrier.Evaluate(outdoorCell);

        Assert.Equal(state.Window.FarRadius, state.RenderFarRadius);
        Assert.Equal(state.Window.NearRadius, state.RenderNearRadius);
        Assert.Equal(state.Window.FarRadius, second.RequiredRenderRadius);
        Assert.Equal(state.Window.NearRadius, second.RequiredNearRadius);
    }

    [Fact]
    public void OutdoorReveal_JoinsRenderTexturesAndTerrainOverTheDerivedWindow()
    {
        const uint outdoorCell = 0x11340021u;
        var state = new State { Window = new StreamingRevealWindow(4, 12) };
        var barrier = state.Build();

        Assert.False(barrier.IsReady(outdoorCell));
        Assert.Equal(state.Window.FarRadius, state.RenderFarRadius);

        state.RenderReady = true;
        barrier.Prepare(outdoorCell);
        Assert.Equal(1, state.Preparations);
        Assert.Equal(outdoorCell, state.PreparedCell);
        Assert.Equal(state.Window.NearRadius, state.PreparedRadius);

        state.CompositeReady = true;
        Assert.False(barrier.IsReady(outdoorCell));

        state.TerrainReady = true;
        Assert.True(barrier.IsReady(outdoorCell));
        Assert.Equal(state.Window.FarRadius, state.TerrainRadius);
    }

    [Fact]
    public void IndoorReveal_UsesCenterRenderAndExactEnvCellPhysics()
    {
        const uint indoorCell = 0x11340100u;
        var state = new State
        {
            Window = new StreamingRevealWindow(4, 12),
            RenderReady = true,
            CompositeReady = true,
            TerrainReady = true,
        };
        var barrier = state.Build();

        barrier.Prepare(indoorCell);
        Assert.Equal(0, state.PreparedRadius);
        Assert.False(barrier.IsReady(indoorCell));
        Assert.Equal(-1, state.TerrainRadius);

        state.SpawnCellReady = true;
        Assert.True(barrier.IsReady(indoorCell));
        Assert.Equal(0, state.RenderNearRadius);
        Assert.Equal(0, state.RenderFarRadius);
    }

    [Fact]
    public void Prepare_WaitsForStaticMeshPublication()
    {
        var state = new State();
        var barrier = state.Build();

        barrier.Prepare(0x11340021u);

        Assert.Equal(0, state.Preparations);
    }

    [Fact]
    public void Prepare_StartsWarmupOnceTheNearSubWindowIsPublished()
    {
        const uint outdoorCell = 0x11340021u;
        var state = new State
        {
            Window = new StreamingRevealWindow(4, 12),
            // Published out to the Near radius only — the Far ring is still
            // streaming, which is the normal state for most of the hold.
            RenderReadyByRadius = (_, farRadius) => farRadius <= 4,
        };
        var barrier = state.Build();

        barrier.Prepare(outdoorCell);

        Assert.Equal(1, state.Preparations);
        Assert.Equal(state.Window.NearRadius, state.PreparedRadius);
        Assert.Equal(state.Window.NearRadius, state.RenderFarRadius);
        Assert.False(barrier.IsReady(outdoorCell));
        Assert.Equal(state.Window.FarRadius, state.RenderFarRadius);
    }

    /// <summary>
    /// The Near sub-window is a real precondition, not a formality: an
    /// unpublished destination neighbourhood still blocks warmup.
    /// </summary>
    [Fact]
    public void Prepare_StillWaitsWhenTheNearSubWindowIsIncomplete()
    {
        var state = new State
        {
            Window = new StreamingRevealWindow(4, 12),
            RenderReadyByRadius = (_, _) => false,
        };

        state.Build().Prepare(0x11340021u);

        Assert.Equal(0, state.Preparations);
    }

    [Fact]
    public void ImpossibleClaim_CrossesExistingLoudRecoveryPath()
    {
        var state = new State { Unhydratable = true };
        var barrier = state.Build();

        barrier.Prepare(0x113401FFu);

        Assert.True(barrier.IsReady(0x113401FFu));
        Assert.Equal(0, state.Preparations);
        Assert.Equal(-1, state.RenderFarRadius);
    }

    [Fact]
    public void Evaluate_ExposesTheCanonicalOutdoorDecisionWithoutRepeatingDomains()
    {
        const uint outdoorCell = 0x11340021u;
        var state = new State
        {
            Window = new StreamingRevealWindow(4, 12),
            RenderReady = true,
            CompositeReady = true,
            TerrainReady = true,
        };

        WorldRevealReadinessSnapshot snapshot = state.Build().Evaluate(outdoorCell);

        Assert.Equal(outdoorCell, snapshot.DestinationCell);
        Assert.False(snapshot.IsIndoor);
        Assert.Equal(state.Window.FarRadius, snapshot.RequiredRenderRadius);
        Assert.Equal(state.Window.NearRadius, snapshot.RequiredNearRadius);
        Assert.True(snapshot.IsRenderNeighborhoodReady);
        Assert.True(snapshot.AreCompositeTexturesReady);
        Assert.True(snapshot.IsCollisionReady);
        Assert.True(snapshot.IsReady);
        Assert.Equal(-1, state.PreparedRadius);
    }

    [Fact]
    public void Evaluate_ShortCircuitsDownstreamDomainsUntilRenderPublication()
    {
        var state = new State
        {
            CompositeReady = true,
            TerrainReady = true,
            SpawnCellReady = true,
        };

        WorldRevealReadinessSnapshot snapshot = state.Build().Evaluate(0x11340100u);

        Assert.False(snapshot.IsRenderNeighborhoodReady);
        Assert.False(snapshot.AreCompositeTexturesReady);
        Assert.False(snapshot.IsCollisionReady);
        Assert.False(snapshot.IsReady);
        Assert.Equal(-1, state.TerrainRadius);
    }

    [Fact]
    public void RequiredWindow_ClampsANearRadiusThatExceedsTheFarRadius()
    {
        var state = new State { Window = new StreamingRevealWindow(9, 4) };
        var barrier = state.Build();

        Assert.Equal(
            new StreamingRevealWindow(4, 4),
            barrier.RequiredWindow(0x11340021u));
    }

    [Fact]
    public void RevealRadiusOverride_ReplacesTheDerivedWindowAndClampsTheNearArm()
    {
        var window = new StreamingRevealWindow(4, 12);

        Assert.Equal(
            window,
            StreamingDiagnostics.ApplyRevealRadiusOverride(window, null));
        Assert.Equal(
            new StreamingRevealWindow(1, 1),
            StreamingDiagnostics.ApplyRevealRadiusOverride(window, 1));
        Assert.Equal(
            new StreamingRevealWindow(4, 25),
            StreamingDiagnostics.ApplyRevealRadiusOverride(window, 25));
    }

    /// <summary>
    /// The probe's parser floor is 1, not 0. A zero override makes
    /// <c>RequiredWindow</c> return far = 0 for an OUTDOOR destination, which
    /// Runtime's <c>invalid-readiness-shape</c> invariant rejects on every
    /// acknowledgement — i.e. the probe would hang the exact A/B route it
    /// exists to measure. Reject it where it is read, not where it detonates.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("nonsense", null)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("1", 1)]
    [InlineData("12", 12)]
    public void RevealRadiusOverride_ParserRefusesRadiiRuntimeWouldReject(
        string? raw,
        int? expected) =>
        Assert.Equal(expected, StreamingDiagnostics.ParseRadius(raw));
}

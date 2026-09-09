using AcDream.App.Composition;

namespace AcDream.App.Tests.Composition;

public sealed class GameWindowCompositionPipelineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void FailureRunsExactlyTheRequiredPrefix(int failingPhase)
    {
        var phases = new RecordingPhases(failingPhase);
        GameWindowCompositionPipeline<Token, Token, Token, Token, Token, Token, Token, Token, Token>
            pipeline = phases.BuildPipeline();

        Assert.Throws<InvalidOperationException>(() => pipeline.Run(phases.Platform));

        Assert.Equal(
            Enumerable.Range(1, failingPhase).Select(value => $"phase-{value}"),
            phases.Calls);
    }

    [Fact]
    public void SuccessPassesExactResultsAndStartsSessionLast()
    {
        var phases = new RecordingPhases(failingPhase: 0);
        GameWindowCompositionPipeline<Token, Token, Token, Token, Token, Token, Token, Token, Token>
            pipeline = phases.BuildPipeline();

        pipeline.Run(phases.Platform);

        Assert.Equal(
            Enumerable.Range(1, 9).Select(value => $"phase-{value}"),
            phases.Calls);
        Assert.Equal("phase-9", phases.Calls[^1]);
    }

    [Fact]
    public void ProductionDelegateEntryUsesTheSamePipelineAndExactResults()
    {
        var calls = new List<int>();
        var values = Enumerable.Range(0, 9).Select(value => new Token(value)).ToArray();

        GameWindowCompositionPipeline.Run<
            Token,
            Token,
            Token,
            Token,
            Token,
            Token,
            Token,
            Token,
            Token>(
            values[0],
            platform => Pass(1, values[1], platform),
            (platform, host) => Pass(2, values[2], platform, host),
            (platform, host, content) => Pass(3, values[3], platform, host, content),
            (platform, content, settings) => Pass(4, values[4], platform, content, settings),
            (platform, host, content, settings, world) =>
                Pass(5, values[5], platform, host, content, settings, world),
            (platform, host, content, settings, world, interaction) =>
                Pass(6, values[6], platform, host, content, settings, world, interaction),
            (host, content, settings, world, interaction, live) =>
                Pass(7, values[7], host, content, settings, world, interaction, live),
            (platform, host, content, settings, world, interaction, live, session) =>
                Pass(8, values[8], platform, host, content, settings, world, interaction, live, session),
            frame =>
            {
                Assert.Same(values[8], frame);
                calls.Add(9);
            });

        Assert.Equal(Enumerable.Range(1, 9), calls);

        Token Pass(int phase, Token result, params Token[] actual)
        {
            Token[] expected = phase switch
            {
                1 => [values[0]],
                2 => [values[0], values[1]],
                3 => [values[0], values[1], values[2]],
                4 => [values[0], values[2], values[3]],
                5 => [values[0], values[1], values[2], values[3], values[4]],
                6 => [values[0], values[1], values[2], values[3], values[4], values[5]],
                7 => [values[1], values[2], values[3], values[4], values[5], values[6]],
                8 => [values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]],
                _ => throw new ArgumentOutOfRangeException(nameof(phase)),
            };
            Assert.Equal(expected.Length, actual.Length);
            for (int index = 0; index < expected.Length; index++)
                Assert.Same(expected[index], actual[index]);
            calls.Add(phase);
            return result;
        }
    }

    private sealed record Token(int Phase);

    private sealed class RecordingPhases(int failingPhase) :
        IHostInputCameraCompositionPhase<Token, Token>,
        IContentEffectsAudioCompositionPhase<Token, Token, Token>,
        ISettingsDevToolsCompositionPhase<Token, Token, Token, Token>,
        IWorldRenderCompositionPhase<Token, Token, Token, Token>,
        IInteractionUiCompositionPhase<Token, Token, Token, Token, Token, Token>,
        ILivePresentationCompositionPhase<Token, Token, Token, Token, Token, Token, Token>,
        ISessionPlayerCompositionPhase<Token, Token, Token, Token, Token, Token, Token>,
        IFrameRootCompositionPhase<Token, Token, Token, Token, Token, Token, Token, Token, Token>,
        ISessionStartCompositionPhase<Token>
    {
        public Token Platform { get; } = new(0);
        public List<string> Calls { get; } = [];

        private readonly Token _host = new(1);
        private readonly Token _content = new(2);
        private readonly Token _settings = new(3);
        private readonly Token _world = new(4);
        private readonly Token _interaction = new(5);
        private readonly Token _live = new(6);
        private readonly Token _session = new(7);
        private readonly Token _frame = new(8);

        public GameWindowCompositionPipeline<Token, Token, Token, Token, Token, Token, Token, Token, Token>
            BuildPipeline() => new(this, this, this, this, this, this, this, this, this);

        Token IHostInputCameraCompositionPhase<Token, Token>.Compose(Token platform)
        {
            Assert.Same(Platform, platform);
            Record(1);
            return _host;
        }

        Token IContentEffectsAudioCompositionPhase<Token, Token, Token>.Compose(
            Token platform,
            Token host)
        {
            Assert.Same(Platform, platform);
            Assert.Same(_host, host);
            Record(2);
            return _content;
        }

        Token ISettingsDevToolsCompositionPhase<Token, Token, Token, Token>.Compose(
            Token platform,
            Token host,
            Token content)
        {
            Assert.Same(Platform, platform);
            Assert.Same(_host, host);
            Assert.Same(_content, content);
            Record(3);
            return _settings;
        }

        Token IWorldRenderCompositionPhase<Token, Token, Token, Token>.Compose(
            Token platform,
            Token content,
            Token settings)
        {
            Assert.Same(Platform, platform);
            Assert.Same(_content, content);
            Assert.Same(_settings, settings);
            Record(4);
            return _world;
        }

        Token IInteractionUiCompositionPhase<Token, Token, Token, Token, Token, Token>.Compose(
            Token platform,
            Token host,
            Token content,
            Token settings,
            Token world)
        {
            Assert.Same(Platform, platform);
            Assert.Same(_host, host);
            Assert.Same(_content, content);
            Assert.Same(_settings, settings);
            Assert.Same(_world, world);
            Record(5);
            return _interaction;
        }

        Token ILivePresentationCompositionPhase<Token, Token, Token, Token, Token, Token, Token>.Compose(
            Token platform,
            Token host,
            Token content,
            Token settings,
            Token world,
            Token interaction)
        {
            Assert.Same(Platform, platform);
            Assert.Same(_host, host);
            Assert.Same(_content, content);
            Assert.Same(_settings, settings);
            Assert.Same(_world, world);
            Assert.Same(_interaction, interaction);
            Record(6);
            return _live;
        }

        Token ISessionPlayerCompositionPhase<Token, Token, Token, Token, Token, Token, Token>.Compose(
            Token host,
            Token content,
            Token settings,
            Token world,
            Token interaction,
            Token live)
        {
            Assert.Same(_host, host);
            Assert.Same(_content, content);
            Assert.Same(_settings, settings);
            Assert.Same(_world, world);
            Assert.Same(_interaction, interaction);
            Assert.Same(_live, live);
            Record(7);
            return _session;
        }

        Token IFrameRootCompositionPhase<Token, Token, Token, Token, Token, Token, Token, Token, Token>.Compose(
            Token platform,
            Token host,
            Token content,
            Token settings,
            Token world,
            Token interaction,
            Token live,
            Token session)
        {
            Assert.Same(Platform, platform);
            Assert.Same(_host, host);
            Assert.Same(_content, content);
            Assert.Same(_settings, settings);
            Assert.Same(_world, world);
            Assert.Same(_interaction, interaction);
            Assert.Same(_live, live);
            Assert.Same(_session, session);
            Record(8);
            return _frame;
        }

        void ISessionStartCompositionPhase<Token>.Start(Token frame)
        {
            Assert.Same(_frame, frame);
            Record(9);
        }

        private void Record(int phase)
        {
            Calls.Add($"phase-{phase}");
            if (failingPhase == phase)
                throw new InvalidOperationException($"phase {phase} failed");
        }
    }
}

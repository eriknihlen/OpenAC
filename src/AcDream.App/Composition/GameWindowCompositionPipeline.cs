namespace AcDream.App.Composition;

internal interface IHostInputCameraCompositionPhase<in TPlatform, out TResult>
{
    TResult Compose(TPlatform platform);
}

internal interface IContentEffectsAudioCompositionPhase<in TPlatform, in THost, out TResult>
{
    TResult Compose(TPlatform platform, THost host);
}

internal interface ISettingsDevToolsCompositionPhase<in TPlatform, in THost, in TContent, out TResult>
{
    TResult Compose(TPlatform platform, THost host, TContent content);
}

internal interface IWorldRenderCompositionPhase<in TPlatform, in TContent, in TSettings, out TResult>
{
    TResult Compose(TPlatform platform, TContent content, TSettings settings);
}

internal interface IInteractionUiCompositionPhase<in TPlatform, in THost, in TContent, in TSettings, in TWorld, out TResult>
{
    TResult Compose(TPlatform platform, THost host, TContent content, TSettings settings, TWorld world);
}

internal interface ILivePresentationCompositionPhase<in TPlatform, in THost, in TContent, in TSettings, in TWorld, in TInteraction, out TResult>
{
    TResult Compose(
        TPlatform platform,
        THost host,
        TContent content,
        TSettings settings,
        TWorld world,
        TInteraction interaction);
}

internal interface ISessionPlayerCompositionPhase<in THost, in TContent, in TSettings, in TWorld, in TInteraction, in TLive, out TResult>
{
    TResult Compose(
        THost host,
        TContent content,
        TSettings settings,
        TWorld world,
        TInteraction interaction,
        TLive live);
}

internal interface IFrameRootCompositionPhase<in TPlatform, in THost, in TContent, in TSettings, in TWorld, in TInteraction, in TLive, in TSession, out TResult>
{
    TResult Compose(
        TPlatform platform,
        THost host,
        TContent content,
        TSettings settings,
        TWorld world,
        TInteraction interaction,
        TLive live,
        TSession session);
}

internal interface ISessionStartCompositionPhase<in TFrame>
{
    void Start(TFrame frame);
}

internal static class GameWindowCompositionPipeline
{
    public static void Run<
        TPlatform,
        THost,
        TContent,
        TSettings,
        TWorld,
        TInteraction,
        TLive,
        TSession,
        TFrame>(
        TPlatform platform,
        Func<TPlatform, THost> host,
        Func<TPlatform, THost, TContent> content,
        Func<TPlatform, THost, TContent, TSettings> settings,
        Func<TPlatform, TContent, TSettings, TWorld> world,
        Func<TPlatform, THost, TContent, TSettings, TWorld, TInteraction> interaction,
        Func<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive> live,
        Func<THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession> session,
        Func<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession, TFrame> frame,
        Action<TFrame> start)
    {
        new GameWindowCompositionPipeline<
            TPlatform,
            THost,
            TContent,
            TSettings,
            TWorld,
            TInteraction,
            TLive,
            TSession,
            TFrame>(
                new HostPhase<TPlatform, THost>(host),
                new ContentPhase<TPlatform, THost, TContent>(content),
                new SettingsPhase<TPlatform, THost, TContent, TSettings>(settings),
                new WorldPhase<TPlatform, TContent, TSettings, TWorld>(world),
                new InteractionPhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction>(interaction),
                new LivePhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive>(live),
                new SessionPhase<THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession>(session),
                new FramePhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession, TFrame>(frame),
                new StartPhase<TFrame>(start))
            .Run(platform);
    }

    private sealed class HostPhase<TPlatform, TResult>(
        Func<TPlatform, TResult> compose)
        : IHostInputCameraCompositionPhase<TPlatform, TResult>
    {
        public TResult Compose(TPlatform platform) => compose(platform);
    }

    private sealed class ContentPhase<TPlatform, THost, TResult>(
        Func<TPlatform, THost, TResult> compose)
        : IContentEffectsAudioCompositionPhase<TPlatform, THost, TResult>
    {
        public TResult Compose(TPlatform platform, THost host) => compose(platform, host);
    }

    private sealed class SettingsPhase<TPlatform, THost, TContent, TResult>(
        Func<TPlatform, THost, TContent, TResult> compose)
        : ISettingsDevToolsCompositionPhase<TPlatform, THost, TContent, TResult>
    {
        public TResult Compose(TPlatform platform, THost host, TContent content) =>
            compose(platform, host, content);
    }

    private sealed class WorldPhase<TPlatform, TContent, TSettings, TResult>(
        Func<TPlatform, TContent, TSettings, TResult> compose)
        : IWorldRenderCompositionPhase<TPlatform, TContent, TSettings, TResult>
    {
        public TResult Compose(TPlatform platform, TContent content, TSettings settings) =>
            compose(platform, content, settings);
    }

    private sealed class InteractionPhase<TPlatform, THost, TContent, TSettings, TWorld, TResult>(
        Func<TPlatform, THost, TContent, TSettings, TWorld, TResult> compose)
        : IInteractionUiCompositionPhase<TPlatform, THost, TContent, TSettings, TWorld, TResult>
    {
        public TResult Compose(
            TPlatform platform,
            THost host,
            TContent content,
            TSettings settings,
            TWorld world) => compose(platform, host, content, settings, world);
    }

    private sealed class LivePhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TResult>(
        Func<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TResult> compose)
        : ILivePresentationCompositionPhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TResult>
    {
        public TResult Compose(
            TPlatform platform,
            THost host,
            TContent content,
            TSettings settings,
            TWorld world,
            TInteraction interaction) =>
            compose(platform, host, content, settings, world, interaction);
    }

    private sealed class SessionPhase<THost, TContent, TSettings, TWorld, TInteraction, TLive, TResult>(
        Func<THost, TContent, TSettings, TWorld, TInteraction, TLive, TResult> compose)
        : ISessionPlayerCompositionPhase<THost, TContent, TSettings, TWorld, TInteraction, TLive, TResult>
    {
        public TResult Compose(
            THost host,
            TContent content,
            TSettings settings,
            TWorld world,
            TInteraction interaction,
            TLive live) => compose(host, content, settings, world, interaction, live);
    }

    private sealed class FramePhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession, TResult>(
        Func<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession, TResult> compose)
        : IFrameRootCompositionPhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession, TResult>
    {
        public TResult Compose(
            TPlatform platform,
            THost host,
            TContent content,
            TSettings settings,
            TWorld world,
            TInteraction interaction,
            TLive live,
            TSession session) =>
            compose(platform, host, content, settings, world, interaction, live, session);
    }

    private sealed class StartPhase<TFrame>(Action<TFrame> start)
        : ISessionStartCompositionPhase<TFrame>
    {
        public void Start(TFrame frame) => start(frame);
    }
}

internal sealed class GameWindowCompositionPipeline<
    TPlatform,
    THost,
    TContent,
    TSettings,
    TWorld,
    TInteraction,
    TLive,
    TSession,
    TFrame>
{
    private readonly IHostInputCameraCompositionPhase<TPlatform, THost> _host;
    private readonly IContentEffectsAudioCompositionPhase<TPlatform, THost, TContent> _content;
    private readonly ISettingsDevToolsCompositionPhase<TPlatform, THost, TContent, TSettings> _settings;
    private readonly IWorldRenderCompositionPhase<TPlatform, TContent, TSettings, TWorld> _world;
    private readonly IInteractionUiCompositionPhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction> _interaction;
    private readonly ILivePresentationCompositionPhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive> _live;
    private readonly ISessionPlayerCompositionPhase<THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession> _session;
    private readonly IFrameRootCompositionPhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession, TFrame> _frame;
    private readonly ISessionStartCompositionPhase<TFrame> _start;

    public GameWindowCompositionPipeline(
        IHostInputCameraCompositionPhase<TPlatform, THost> host,
        IContentEffectsAudioCompositionPhase<TPlatform, THost, TContent> content,
        ISettingsDevToolsCompositionPhase<TPlatform, THost, TContent, TSettings> settings,
        IWorldRenderCompositionPhase<TPlatform, TContent, TSettings, TWorld> world,
        IInteractionUiCompositionPhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction> interaction,
        ILivePresentationCompositionPhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive> live,
        ISessionPlayerCompositionPhase<THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession> session,
        IFrameRootCompositionPhase<TPlatform, THost, TContent, TSettings, TWorld, TInteraction, TLive, TSession, TFrame> frame,
        ISessionStartCompositionPhase<TFrame> start)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public void Run(TPlatform platform)
    {
        THost host = _host.Compose(platform);
        TContent content = _content.Compose(platform, host);
        TSettings settings = _settings.Compose(platform, host, content);
        TWorld world = _world.Compose(platform, content, settings);
        TInteraction interaction = _interaction.Compose(platform, host, content, settings, world);
        TLive live = _live.Compose(platform, host, content, settings, world, interaction);
        TSession session = _session.Compose(host, content, settings, world, interaction, live);
        TFrame frame = _frame.Compose(
            platform,
            host,
            content,
            settings,
            world,
            interaction,
            live,
            session);
        _start.Start(frame);
    }
}

using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class ScopedPluginChatTests
{
    [Fact]
    public void UnloadingAPluginRemovesEveryFilterAndChatSubscriptionItLeft()
    {
        var chat = new RecordingChat();
        var inner = new StubHost(chat);
        var scoped = new ScopedPluginHost(inner, "example.plugin", "Example");

        scoped.Automation.Chat.RegisterFilter(static _ => true);
        scoped.Automation.Chat.RegisterFilter(static _ => false);
        scoped.Automation.Chat.Received += static _ => { };
        Assert.Equal(2, chat.FilterCount);
        Assert.Equal(1, chat.SubscriberCount);

        scoped.Dispose();

        Assert.Equal(0, chat.FilterCount);
        Assert.Equal(0, chat.SubscriberCount);
    }

    [Fact]
    public void SubscribingAfterUnloadThrowsWithoutEverTouchingTheHostsChat()
    {
        var chat = new RecordingChat();
        var scoped = new ScopedPluginHost(new StubHost(chat), "example.plugin", "Example");
        scoped.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            scoped.Automation.Chat.Received += static _ => { });

        // The check must fire before subscribing, not after: subscribing
        // first and unwinding second leaves a window where a line can
        // still reach an unloaded plugin's handler.
        Assert.Equal(0, chat.SubscriberCount);
    }

    [Fact]
    public void DisposingOneFilterRegistrationLeavesTheOthers()
    {
        var chat = new RecordingChat();
        var scoped = new ScopedPluginHost(
            new StubHost(chat),
            "example.plugin",
            "Example");

        IDisposable first = scoped.Automation.Chat.RegisterFilter(static _ => true);
        scoped.Automation.Chat.RegisterFilter(static _ => false);

        first.Dispose();
        Assert.Equal(1, chat.FilterCount);

        scoped.Dispose();
        Assert.Equal(0, chat.FilterCount);
    }

    [Fact]
    public void CallingChatMethodsAfterUnloadThrows()
    {
        var chat = new RecordingChat();
        var scoped = new ScopedPluginHost(new StubHost(chat), "example.plugin", "Example");
        IPluginChat scopedChat = scoped.Automation.Chat;

        scoped.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => scopedChat.PostSystemMessage("hello"));
        Assert.Throws<ObjectDisposedException>(
            () => scopedChat.PostMessage("hello", 0));
        Assert.Throws<ObjectDisposedException>(
            () => scopedChat.Submit("hello"));
    }

    [Fact]
    public void ScopedStorageReportsThePluginsOwnDirectory()
    {
        var scoped = new ScopedPluginHost(
            new StubHost(new RecordingChat(), new RootedStorage("/data/plugins")),
            "example.plugin",
            "Example");

        Assert.Equal(
            Path.Combine("/data/plugins", "example.plugin"),
            scoped.Storage.RootPath);

        scoped.Dispose();
    }

    [Fact]
    public void ScopedStorageReportsNoDirectoryWhenTheHostHasNone()
    {
        var scoped = new ScopedPluginHost(
            new StubHost(new RecordingChat()),
            "example.plugin",
            "Example");

        Assert.Null(scoped.Storage.RootPath);

        scoped.Dispose();
    }

    [Fact]
    public void TheHostsClipboardIsForwardedUnchanged()
    {
        var clipboard = new RecordingClipboard();
        var scoped = new ScopedPluginHost(
            new StubHost(new RecordingChat(), clipboard: clipboard),
            "example.plugin",
            "Example");

        Assert.True(scoped.Clipboard.TrySetText("copied"));
        Assert.Equal("copied", clipboard.LastText);

        scoped.Dispose();
    }

    private sealed class RecordingChat : IPluginChat
    {
        private readonly List<Func<PluginChatMessage, bool>> _filters = [];
        private Action<PluginChatMessage>? _received;

        internal int FilterCount => _filters.Count;
        internal int SubscriberCount =>
            _received?.GetInvocationList().Length ?? 0;

        public event Action<PluginChatMessage> Received
        {
            add => _received += value;
            remove => _received -= value;
        }

        public IDisposable RegisterFilter(Func<PluginChatMessage, bool> suppress)
        {
            _filters.Add(suppress);
            return new Removal(this, suppress);
        }

        public void PostSystemMessage(string text)
        {
        }

        private sealed class Removal(
            RecordingChat owner,
            Func<PluginChatMessage, bool> suppress) : IDisposable
        {
            public void Dispose() => owner._filters.Remove(suppress);
        }
    }

    private sealed class RootedStorage(string root) : IPluginStorage
    {
        public bool IsAvailable => true;
        public string? RootPath => root;
    }

    private sealed class RecordingClipboard : IPluginClipboard
    {
        internal string? LastText { get; private set; }

        public bool TrySetText(string text)
        {
            LastText = text;
            return true;
        }
    }

    private sealed class StubHost(
        IPluginChat chat,
        IPluginStorage? storage = null,
        IPluginClipboard? clipboard = null)
        : IPluginHost, IAutomationSurface
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui { get; } = NoOpUiRegistry.Instance;
        public IAutomationSurface Automation => this;
        public IPluginStorage Storage { get; } =
            storage ?? NoOpPluginStorage.Instance;
        public IPluginClipboard Clipboard { get; } =
            clipboard ?? NoOpPluginClipboard.Instance;

        public bool IsAvailable => false;
        public ICharacterInfo Character => NoOpAutomationSurface.Instance.Character;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance.Spells;
        public IMagicCommands Magic => NoOpAutomationSurface.Instance.Magic;
        public IPluginChat Chat { get; } = chat;

        private sealed class SilentLogger : IPluginLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception? error = null) { }
        }

        private sealed class EmptyGameState : IGameState
        {
            public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
        }

        private sealed class InertSelection : ISelectionService
        {
            public uint? SelectedObjectId => null;
            public uint? PreviousObjectId => null;
            public event Action<SelectionChangedEvent>? Changed;
            public bool Select(uint objectId)
            {
                Changed?.Invoke(default);
                return false;
            }
            public bool Clear() => false;
        }
    }
}

using AcDream.Core.Chat;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;

namespace AcDream.Headless.Plugins;

internal sealed class HeadlessAutomationSurface : IAutomationSurface, IPluginChat
{
    private readonly GameRuntime _runtime;
    private readonly Func<string, bool>? _submitChatText;

    internal HeadlessAutomationSurface(
        GameRuntime runtime,
        Func<string, bool>? submitChatText = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _submitChatText = submitChatText;
    }

    public bool IsAvailable =>
        _runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;

    public ICharacterInfo Character => NoOpAutomationSurface.Instance.Character;
    public ISpellCatalog Spells => NoOpAutomationSurface.Instance.Spells;
    public IMagicCommands Magic => NoOpAutomationSurface.Instance.Magic;
    public IPluginChat Chat => this;

    public void PostSystemMessage(string text) =>
        _runtime.CommunicationOwner.AddText(text, RetailLogTextType.Default);

    public bool Submit(string text) =>
        IsAvailable
        && !string.IsNullOrWhiteSpace(text)
        && (_submitChatText?.Invoke(text) ?? false);
}

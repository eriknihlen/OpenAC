using AcDream.Runtime;

namespace AcDream.Automation;

/// <summary>
/// What the automation surface needs from a host's command adapter: the
/// runtime command bus, the generation to stamp commands with, and the
/// host's chat submit (the graphical client and the headless host each
/// route chat through their own command surface).
/// </summary>
public sealed class AutomationSessionCommands
{
    private readonly Func<RuntimeGenerationToken> _generation;
    private readonly Func<string, bool> _submitChatText;

    public AutomationSessionCommands(
        IGameRuntimeCommands commands,
        Func<RuntimeGenerationToken> generation,
        Func<string, bool> submitChatText)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _generation = generation ?? throw new ArgumentNullException(nameof(generation));
        _submitChatText = submitChatText
            ?? throw new ArgumentNullException(nameof(submitChatText));
    }

    public IGameRuntimeCommands Commands { get; }

    public RuntimeGenerationToken Generation => _generation();

    public bool SubmitChatText(string text) => _submitChatText(text);
}

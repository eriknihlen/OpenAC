#pragma warning disable CS1591
namespace AcDream.Plugin.Abstractions;

/// <summary>One invocation of a plugin command.</summary>
public readonly record struct PluginCommand(string Verb, string Arguments, string RawText)
{
    /// <summary>Parses shell-like arguments, preserving quoted multi-word values.</summary>
    public IReadOnlyList<string> ParseArguments() => PluginCommandLineParser.Parse(Arguments);
}

/// <summary>Result returned by a typed command definition.</summary>
public readonly record struct PluginCommandResult(bool Success, string? Message = null)
{
    public static PluginCommandResult Accepted(string? message = null) => new(true, message);
    public static PluginCommandResult Rejected(string message) => new(false, message);
}

/// <summary>A completion candidate returned for a partially typed command.</summary>
public readonly record struct PluginCommandCompletion(string Text, string? Description = null);

/// <summary>Describes a command, its aliases, invocation, and completion behavior.</summary>
public interface IPluginCommandDefinition
{
    string Verb { get; }
    string Description { get; }
    IReadOnlyList<string> Aliases { get; }
    PluginCommandResult Invoke(PluginCommand command);
    IReadOnlyList<PluginCommandCompletion> Complete(PluginCommand command);
}

/// <summary>Lets a plugin claim chat commands, including aliases and typed completion.</summary>
public interface IPluginCommandRegistry
{
    IDisposable Register(string verb, Action<PluginCommand> handler);
    /// <summary>Registers a command and all its aliases as one owned registration.</summary>
    IDisposable Register(IPluginCommandDefinition definition) =>
        Register(definition.Verb, definition.InvokeCommand);
    /// <summary>Returns definitions matching a verb, or all definitions for help.</summary>
    IReadOnlyList<IPluginCommandDefinition> Definitions => Array.Empty<IPluginCommandDefinition>();
    /// <summary>Completes a partially typed command without blocking the caller.</summary>
    ValueTask<IReadOnlyList<PluginCommandCompletion>> CompleteAsync(PluginCommand command, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<PluginCommandCompletion>>(Array.Empty<PluginCommandCompletion>());
    /// <summary>Creates help text from registered command descriptions.</summary>
    string GetHelp() => string.Empty;
}

internal static class PluginCommandDefinitionExtensions
{
    public static void InvokeCommand(this IPluginCommandDefinition definition, PluginCommand command) => definition.Invoke(command);
}

/// <summary>Parses quoted and escaped command arguments.</summary>
public static class PluginCommandLineParser
{
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var result = new List<string>();
        var token = new System.Text.StringBuilder();
        char quote = '\0';
        bool escaped = false;
        foreach (char c in text)
        {
            if (escaped) { token.Append(c); escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; else token.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (token.Length != 0) { result.Add(token.ToString()); token.Clear(); } continue; }
            token.Append(c);
        }
        if (escaped) token.Append('\\');
        if (quote != '\0') throw new FormatException("Unterminated quoted command argument.");
        if (token.Length != 0) result.Add(token.ToString());
        return result;
    }
}

/// <summary>Inert command registry for hosts without a command line.</summary>
public sealed class NoOpPluginCommandRegistry : IPluginCommandRegistry
{
    public static NoOpPluginCommandRegistry Instance { get; } = new();
    private NoOpPluginCommandRegistry() { }
    public IDisposable Register(string verb, Action<PluginCommand> handler) { ArgumentException.ThrowIfNullOrWhiteSpace(verb); ArgumentNullException.ThrowIfNull(handler); return NoOpLease.Instance; }
    private sealed class NoOpLease : IDisposable { public static NoOpLease Instance { get; } = new(); public void Dispose() { } }
}

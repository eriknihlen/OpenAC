using System;
using System.Linq;
using AcDream.Core.Chat;

namespace AcDream.Runtime.Chat;

public enum SubmitOutcome { Empty, ClientHandled, UnknownCommand, Sent, Dropped }

public static class ChatCommandRouter
{
    public static SubmitOutcome Submit(
        string? raw,
        IChatCommandFeedback feedback,
        ICommandBus bus,
        ChatChannelKind defaultChannel,
        string? defaultTellTarget = null)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(bus);
        string trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return SubmitOutcome.Empty;

        if (trimmed[0] is ':' or ';')
        {
            trimmed = "@emote " + trimmed[1..];
        }

        if (RetailClientCommandCatalog.TryMatch(trimmed, out var clientCommand))
        {
            if (!clientCommand.HasValidArguments)
            {
                if (clientCommand.InvalidArgumentsText is { } bespokeRefusal)
                {
                    feedback.ShowInterfaceText(bespokeRefusal);
                }
                else
                {
                    (string? text, RetailLogTextType type) fallback =
                        WeenieErrorMessages.Resolve(0x026u, null);
                    string fallbackText = fallback.text ?? "That is not a valid command.";
                    if (fallback.type == RetailLogTextType.ClientLocal)
                        feedback.ShowInterfaceText(fallbackText);
                    else
                        feedback.ShowSystemMessage(fallbackText);
                }
                return SubmitOutcome.ClientHandled;
            }

            bus.Publish(new ExecuteClientCommandCmd(
                clientCommand.Command, clientCommand.Arguments));
            return SubmitOutcome.ClientHandled;
        }

        if (TryHandleLocalPresentationCommand(trimmed, feedback))
            return SubmitOutcome.ClientHandled;

        if (bus is IPluginCommandBus pluginCommands
            && pluginCommands.TryHandlePluginCommand(trimmed))
        {
            return SubmitOutcome.ClientHandled;
        }

        if (trimmed[0] is '/' or '@'
            && (trimmed.Length == 1 || !char.IsLetter(trimmed[1])))
        {
            feedback.ShowSystemMessage(
                $"Unknown command: {ChatInputParser.GetVerbToken(trimmed)}. Type /help for the list of supported commands.");
            return SubmitOutcome.UnknownCommand;
        }

        SubmitOutcome? fallbackOutcome = TryDispatchChannelFallback(trimmed, bus);
        if (fallbackOutcome is { } outcome)
            return outcome;

        if (TryBuildServerCommand(trimmed, out string serverCommand))
        {
            bus.Publish(new SendServerCommandCmd(serverCommand));
            return SubmitOutcome.Sent;
        }

        if (ChatInputParser.IsBareRegisteredChannelVerb(trimmed))
        {
            feedback.ShowInterfaceText("You must specify the text you wish to say!");
            return SubmitOutcome.ClientHandled;
        }

        if (ChatInputParser.IsReplyMissingLastTeller(
                trimmed,
                feedback.LastIncomingTellSender))
        {
            feedback.ShowInterfaceText("Someone must @tell you first!");
            return SubmitOutcome.ClientHandled;
        }

        var parsed = ChatInputParser.Parse(
            trimmed,
            defaultChannel,
            feedback.LastIncomingTellSender,
            feedback.LastOutgoingTellTarget,
            defaultTellTarget);
        if (parsed is { } chat)
        {
            bus.Publish(new SendChatCmd(chat.Channel, chat.TargetName, chat.Text));
            return SubmitOutcome.Sent;
        }

        return SubmitOutcome.Dropped;
    }

    private static SubmitOutcome? TryDispatchChannelFallback(
        string trimmed,
        ICommandBus bus)
    {
        if (trimmed[0] is not ('/' or '@'))
            return null;

        string verb = ChatInputParser.GetVerbToken(trimmed);
        string tag = verb[1..].TrimEnd(',');

        if (RetailClientCommandCatalog.KnownVerbs.Contains(tag, StringComparer.OrdinalIgnoreCase))
            return null;

        string normalizedChatVerb = "/" + tag;
        if (ChatInputParser.IsKnownVerb(normalizedChatVerb))
            return null;

        if (!RetailChannelTagTable.TryResolve(tag, out uint channelId))
            return null;

        int separator = trimmed.IndexOfAny([' ', '\t']);
        string text = separator < 0 ? string.Empty : trimmed[(separator + 1)..].Trim();
        if (text.Length == 0)
        {
            return null;
        }

        bus.Publish(new SendRawChannelCmd(channelId, text));
        return SubmitOutcome.Sent;
    }

    private static bool TryBuildServerCommand(string trimmed, out string command)
    {
        command = string.Empty;
        if (trimmed[0] is not ('/' or '@'))
            return false;

        string verb = ChatInputParser.GetVerbToken(trimmed);
        string normalizedChatVerb = "/" + verb[1..];
        if (ChatInputParser.IsKnownVerb(normalizedChatVerb))
            return false;

        command = "@" + trimmed[1..];
        return true;
    }

    private static bool TryHandleLocalPresentationCommand(
        string trimmed,
        IChatCommandFeedback feedback)
    {
        if (EqAny(trimmed, "/help", "/?", "@help", "@?"))
        {
            EmitBareHelp(feedback);
            return true;
        }

        if (StartsWithAny(trimmed, "/help ", "@help ", "/? ", "@? "))
        {
            string verb = trimmed[(trimmed.IndexOf(' ') + 1)..].Trim();
            EmitVerbHelp(verb, feedback);
            return true;
        }

        return false;
    }

    private static void EmitBareHelp(IChatCommandFeedback feedback)
    {
        feedback.ShowSystemMessage(RetailCommandHelpTable.HelpPrefixNote);
        feedback.ShowSystemMessage(RetailCommandHelpTable.AvailableHelpListing);
    }

    private static void EmitVerbHelp(
        string verb,
        IChatCommandFeedback feedback)
    {
        if (verb.Length == 0)
        {
            EmitBareHelp(feedback);
            return;
        }

        string normalized = verb.TrimStart('/', '@');

        if (RetailCommandHelpTable.CatalogVerbsWithNoRetailHelp.Contains(
            normalized.TrimEnd(',')))
        {
            feedback.ShowSystemMessage(RetailCommandHelpTable.UnknownCommand);
            return;
        }

        if (RetailCommandHelpTable.TryGetCatalogVerbDetailText(normalized, out string retailDetailText))
        {
            feedback.ShowSystemMessage(RetailCommandHelpTable.HelpPrefixNote);
            feedback.ShowSystemMessage(RetailCommandHelpTable.ForMoreInformationPrefix + retailDetailText);
            return;
        }

        if (RetailClientCommandCatalog.TryGetHelpText(normalized, out string catalogText))
        {
            feedback.ShowSystemMessage(RetailCommandHelpTable.HelpPrefixNote);
            feedback.ShowSystemMessage(RetailCommandHelpTable.ForMoreInformationPrefix + catalogText);
            return;
        }

        if (RetailCommandHelpTable.TryGetHelpText(normalized, out string tableText))
        {
            feedback.ShowSystemMessage(RetailCommandHelpTable.HelpPrefixNote);
            feedback.ShowSystemMessage(RetailCommandHelpTable.ForMoreInformationPrefix + tableText);
            return;
        }

        feedback.ShowSystemMessage(RetailCommandHelpTable.UnknownCommand);
    }

    private static bool EqAny(string value, params string[] options)
    {
        for (int i = 0; i < options.Length; i++)
        {
            if (value.Equals(options[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool StartsWithAny(string value, params string[] options)
    {
        for (int i = 0; i < options.Length; i++)
        {
            if (value.StartsWith(options[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

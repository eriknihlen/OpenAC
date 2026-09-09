namespace AcDream.Runtime.Chat;

public static class ChatInputParser
{
    public readonly record struct ParsedInput(
        ChatChannelKind Channel,
        string? TargetName,
        string Text);

    // Alias tables. Order matters only for error messages — verb
    // matching is exact-token, not prefix.
    private static readonly string[] SayAliases     = { "/say", "/s" };
    private static readonly string[] TellAliases    = { "/tell", "/t", "/send", "/whisper", "/w" };
    private static readonly string[] ReplyAliases   = { "/reply", "/r", "/rp" };
    private static readonly string[] RetellAliases  = { "/retell", "/rt" };

    private static readonly (string Verb, ChatChannelKind Channel)[] ChannelVerbs =
    {
        ("/general",          ChatChannelKind.General),
        ("/cg",               ChatChannelKind.General),
        ("/f",                ChatChannelKind.Fellowship),
        ("/fellow",           ChatChannelKind.Fellowship),
        ("/fellows",          ChatChannelKind.Fellowship),
        ("/fellowship",       ChatChannelKind.Fellowship),
        ("/g",                ChatChannelKind.Fellowship),
        ("/group",            ChatChannelKind.Fellowship),
        ("/party",            ChatChannelKind.Fellowship),
        ("/a",                ChatChannelKind.Allegiance),
        ("/guild",            ChatChannelKind.Allegiance),
        ("/gu",               ChatChannelKind.Allegiance),
        ("/ab",               ChatChannelKind.AllegianceBroadcast),
        ("/m",                ChatChannelKind.Monarch),
        ("/monarch",          ChatChannelKind.Monarch),
        ("/p",                ChatChannelKind.Patron),
        ("/patron",           ChatChannelKind.Patron),
        ("/v",                ChatChannelKind.Vassals),
        ("/vassal",           ChatChannelKind.Vassals),
        ("/vassals",          ChatChannelKind.Vassals),
        ("/c",                ChatChannelKind.CoVassals),
        ("/covassal",         ChatChannelKind.CoVassals),
        ("/covassals",        ChatChannelKind.CoVassals),
        ("/co-vassals",       ChatChannelKind.CoVassals),
        ("/lfg",              ChatChannelKind.Lfg),
        ("/clfg",             ChatChannelKind.Lfg),
        ("/trade",            ChatChannelKind.Trade),
        ("/ct",               ChatChannelKind.Trade),
        ("/crp",              ChatChannelKind.Roleplay),
        ("/roleplay",         ChatChannelKind.Roleplay),
        ("/society",          ChatChannelKind.Society),
        ("/soc",              ChatChannelKind.Society),
        ("/olthoi",           ChatChannelKind.Olthoi),
        ("/o",                ChatChannelKind.Olthoi),
    };

    private static readonly HashSet<ChatChannelKind> LegacyChannelHackKinds =
    [
        ChatChannelKind.Fellowship,
        ChatChannelKind.Allegiance,
        ChatChannelKind.AllegianceBroadcast,
        ChatChannelKind.Vassals,
        ChatChannelKind.Patron,
        ChatChannelKind.Monarch,
        ChatChannelKind.CoVassals,
    ];

    public static bool IsBareRegisteredChannelVerb(string trimmed)
    {
        foreach (var (verb, channel) in ChannelVerbs)
        {
            if (LegacyChannelHackKinds.Contains(channel) && IsBareVerb(trimmed, [verb]))
                return true;
        }

        return false;
    }

    public static bool IsReplyMissingLastTeller(string trimmed, string? lastTellSender) =>
        string.IsNullOrEmpty(lastTellSender) && TryParseMessageOnly(trimmed, ReplyAliases, out _);

    public static ParsedInput? Parse(
        string raw,
        ChatChannelKind defaultChannel,
        string? lastTellSender,
        string? lastOutgoingTellTarget = null,
        string? defaultTellTarget = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        if (trimmed.StartsWith('@'))
        {
            string substituted = "/" + trimmed.Substring(1);
            string verb = ExtractVerb(substituted);
            if (IsKnownVerb(verb))
            {
                return Parse(
                    substituted,
                    defaultChannel,
                    lastTellSender,
                    lastOutgoingTellTarget,
                    defaultTellTarget);
            }
            return new ParsedInput(ChatChannelKind.Say, null, trimmed);
        }

        // /say <msg>
        if (TryParseMessageOnly(trimmed, SayAliases, out var sayMsg))
            return new ParsedInput(ChatChannelKind.Say, null, sayMsg);
        if (IsBareVerb(trimmed, SayAliases))
            return null;

        // /tell <target> <msg> or /t <target> <msg>
        if (TryParseTargeted(trimmed, TellAliases, out var tellTarget, out var tellMsg))
            return new ParsedInput(ChatChannelKind.Tell, tellTarget, tellMsg);
        if (IsBareVerb(trimmed, TellAliases) || IsVerbWithSingleToken(trimmed, TellAliases))
            return null;

        // /r <msg> / /reply <msg> — needs prior incoming tell.
        if (TryParseMessageOnly(trimmed, ReplyAliases, out var replyMsg))
        {
            if (string.IsNullOrEmpty(lastTellSender)) return null;
            return new ParsedInput(ChatChannelKind.Tell, lastTellSender, replyMsg);
        }
        if (IsBareVerb(trimmed, ReplyAliases))
            return null;

        // /retell <msg> — needs prior outgoing tell.
        if (TryParseMessageOnly(trimmed, RetellAliases, out var retellMsg))
        {
            if (string.IsNullOrEmpty(lastOutgoingTellTarget)) return null;
            return new ParsedInput(ChatChannelKind.Tell, lastOutgoingTellTarget, retellMsg);
        }
        if (IsBareVerb(trimmed, RetellAliases))
            return null;

        foreach (var (verb, channel) in ChannelVerbs)
        {
            if (TryParseMessageOnly(trimmed, new[] { verb }, out var msg))
                return new ParsedInput(channel, null, msg);
            if (IsBareVerb(trimmed, new[] { verb }))
                return null;
        }

        if (trimmed.Length > 1 && trimmed[0] == '/' && char.IsLetter(trimmed[1]))
            return new ParsedInput(ChatChannelKind.Say, null, "@" + trimmed.Substring(1));

        if (defaultChannel == ChatChannelKind.Tell)
        {
            return string.IsNullOrEmpty(defaultTellTarget)
                ? null
                : new ParsedInput(ChatChannelKind.Tell, defaultTellTarget, trimmed);
        }

        return new ParsedInput(defaultChannel, null, trimmed);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static bool TryParseTargeted(string command, string[] aliases, out string target, out string message)
    {
        target = string.Empty;
        message = string.Empty;

        int firstWs = IndexOfWhitespace(command);
        if (firstWs < 0) return false;

        var verb = TrimVerbComma(command.Substring(0, firstWs));
        if (!ContainsExact(aliases, verb)) return false;

        var rest = command.Substring(firstWs + 1).TrimStart();
        if (rest.Length == 0) return false;

        int commaIndex = rest.IndexOf(',');
        if (commaIndex < 0)
        {
            int targetEnd = IndexOfWhitespace(rest);
            if (targetEnd < 0) return false; // target only, no message
            target = rest.Substring(0, targetEnd).TrimEnd(',', ';', ':', '.', '!', '?');
            message = rest.Substring(targetEnd + 1).TrimStart();
        }
        else
        {
            target = rest.Substring(0, commaIndex).TrimEnd();
            message = rest.Substring(commaIndex + 1).TrimStart();
        }

        if (target.Length == 0 || message.Length == 0) return false;
        return true;
    }

    private static bool TryParseMessageOnly(string command, string[] aliases, out string message)
    {
        message = string.Empty;
        int firstWs = IndexOfWhitespace(command);
        if (firstWs < 0) return false;

        var verb = TrimVerbComma(command.Substring(0, firstWs));
        if (!ContainsExact(aliases, verb)) return false;

        message = command.Substring(firstWs + 1).TrimStart();
        return message.Length > 0;
    }

    private static bool IsBareVerb(string command, string[] aliases)
    {
        string trimmedVerb = TrimVerbComma(command);
        foreach (var alias in aliases)
            if (trimmedVerb == alias) return true;
        return false;
    }

    private static bool IsVerbWithSingleToken(string command, string[] aliases)
    {
        int firstWs = IndexOfWhitespace(command);
        if (firstWs < 0) return false;
        var verb = TrimVerbComma(command.Substring(0, firstWs));
        if (!ContainsExact(aliases, verb)) return false;

        var rest = command.Substring(firstWs + 1).TrimStart();
        if (rest.Length == 0) return true;
        return IndexOfWhitespace(rest) < 0;
    }

    private static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (char.IsWhiteSpace(s[i])) return i;
        return -1;
    }

    private static bool ContainsExact(string[] aliases, string verb)
    {
        for (int i = 0; i < aliases.Length; i++)
            if (aliases[i] == verb) return true;
        return false;
    }

    private static string ExtractVerb(string command)
    {
        int ws = IndexOfWhitespace(command);
        return ws < 0 ? command : command.Substring(0, ws);
    }

    private static readonly HashSet<string> AllKnownVerbs = BuildKnownVerbs();

    private static HashSet<string> BuildKnownVerbs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in SayAliases)    set.Add(v);
        foreach (var v in TellAliases)   set.Add(v);
        foreach (var v in ReplyAliases)  set.Add(v);
        foreach (var v in RetellAliases) set.Add(v);
        foreach (var (v, _) in ChannelVerbs) set.Add(v);
        return set;
    }

    public static bool IsKnownVerb(string verb) => AllKnownVerbs.Contains(TrimVerbComma(verb));

    public static IReadOnlyCollection<string> KnownVerbs => AllKnownVerbs;

    private static string TrimVerbComma(string verb) => verb.TrimEnd(',');

    public static string GetVerbToken(string command) => ExtractVerb(command);
}

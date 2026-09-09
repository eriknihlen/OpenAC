namespace AcDream.Runtime.Chat;

public static class ChatTextReplacements
{
    private static readonly string[] ReplyVerbs = ["r", "rp", "reply"];

    private const string CommandPrefixes = "/@";

    public static string? Expand(string? text, string? lastTeller)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(lastTeller))
            return null;
        if (CommandPrefixes.IndexOf(text[0]) < 0)
            return null;

        foreach (string verb in ReplyVerbs)
        {
            if (text.Length == verb.Length + 2
                && text[^1] == ' '
                && string.Compare(
                        text, 1, verb, 0, verb.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
            {
                return $"@tell {lastTeller}, ";
            }
        }

        return null;
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace AcDream.Core.Chat;

/// <summary>Censors banned words in a chat line against a loaded pattern list.</summary>
public static class ChatLanguageFilter
{
    private const string Replacement = "****";

    /// <summary>Replaces every space-delimited word that matches a pattern with
    /// <c>****</c>, leaving punctuation attached to a word and every separator
    /// untouched. A missing (null) pattern list fails closed and censors every
    /// word; a loaded-but-empty list leaves the line untouched.</summary>
    public static string Censor(string line, IReadOnlyList<string>? patterns)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (patterns is { Count: 0 } || line.Length == 0)
            return line;

        StringBuilder? result = null;
        int wordStart = -1;
        for (int i = 0; i <= line.Length; i++)
        {
            bool boundary = i == line.Length || line[i] == ' ';
            if (!boundary)
            {
                if (wordStart < 0) wordStart = i;
                continue;
            }

            if (wordStart >= 0)
            {
                ReadOnlySpan<char> word = line.AsSpan(wordStart, i - wordStart);
                if (patterns is null || MatchesAnyPattern(word, patterns))
                {
                    result ??= new StringBuilder(line, 0, wordStart, line.Length);
                    result.Append(Replacement);
                }
                else
                {
                    result?.Append(word);
                }
                wordStart = -1;
            }

            if (i < line.Length)
                result?.Append(line[i]);
        }

        return result?.ToString() ?? line;
    }

    private static bool MatchesAnyPattern(ReadOnlySpan<char> word, IReadOnlyList<string> patterns)
    {
        if (word.IsEmpty) return false;
        Span<char> lowered = word.Length <= 128 ? stackalloc char[word.Length] : new char[word.Length];
        for (int i = 0; i < word.Length; i++)
            lowered[i] = char.ToLowerInvariant(word[i]);

        for (int i = 0; i < patterns.Count; i++)
            if (MatchesPattern(lowered, patterns[i]))
                return true;
        return false;
    }

    /// <summary><c>*</c> matches any run of characters, including none; every
    /// other character must match exactly; the whole word must be consumed.</summary>
    public static bool MatchesPattern(ReadOnlySpan<char> word, ReadOnlySpan<char> pattern)
    {
        int w = 0, p = 0, star = -1, resumeAt = 0;
        while (w < word.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resumeAt = w;
            }
            else if (p < pattern.Length && pattern[p] == word[w])
            {
                w++;
                p++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                w = ++resumeAt;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

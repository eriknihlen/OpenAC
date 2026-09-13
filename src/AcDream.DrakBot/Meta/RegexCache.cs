using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// Process-wide cache of constructed <see cref="Regex"/> instances keyed by
/// (pattern, options). Meta conditions and the expression engine compile the
/// same user pattern every tick / every eval; constructing a Regex parses the
/// pattern each time. Caching the instance removes that per-tick cost.
///
/// Note: deliberately NOT <see cref="RegexOptions.Compiled"/> — that path uses
/// reflection-emit which NativeAOT can't honour (it silently falls back to the
/// interpreter anyway). We cache the interpreter instance instead, which is the
/// real win here. A bad pattern is cached as null so we don't re-throw on it
/// every tick; callers treat a miss/null as "no match".
/// </summary>
public static class RegexCache
{
    // Sized to hold every distinct pattern in a large VTank loot-snob profile
    // (1600+ rules, many with spell-name / string regexes) so the loot evaluator
    // never thrashes the cache mid-corpse. Interpreter Regex instances are small.
    private const int MaxEntries = 2048;
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex?> _cache = new();

    public static Regex? Get(string pattern, RegexOptions options = RegexOptions.None)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        var key = (pattern, options);
        if (_cache.TryGetValue(key, out var rx)) return rx;

        // Unbounded user patterns: cap the cache so a meta that builds patterns
        // dynamically can't grow it without limit. Coarse reset is fine — the
        // hot patterns just get rebuilt once.
        if (_cache.Count >= MaxEntries) _cache.Clear();

        try { rx = new Regex(pattern, options | RegexOptions.CultureInvariant); }
        catch { rx = null; }
        _cache[key] = rx;
        return rx;
    }

    public static bool IsMatch(string input, string pattern, RegexOptions options = RegexOptions.None)
    {
        var rx = Get(pattern, options);
        if (rx == null) return false;
        try { return rx.IsMatch(input ?? ""); }
        catch { return false; }
    }

    public static Match Match(string input, string pattern, RegexOptions options = RegexOptions.None)
    {
        var rx = Get(pattern, options);
        if (rx == null) return System.Text.RegularExpressions.Match.Empty;
        try { return rx.Match(input ?? ""); }
        catch { return System.Text.RegularExpressions.Match.Empty; }
    }
}

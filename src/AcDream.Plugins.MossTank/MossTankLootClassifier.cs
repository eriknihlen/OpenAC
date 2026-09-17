using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

/// <summary>
/// Registers MossTank's own loot-rule engine as a loot classifier so other
/// plugins can evaluate an item against MossTank's live rules, or against a
/// named stored profile, without depending on MossTank's UI surface.
/// </summary>
internal sealed class MossTankLootClassifier : IPluginLootClassifier
{
    /// <summary>
    /// The client's long-description string property. It is written only
    /// once an item has been identified, so its absence is the signal that
    /// an item is still unappraised.
    /// </summary>
    private const uint LongDescPropertyId = 16u;

    /// <summary>
    /// Requirement kinds that can only be evaluated with appraisal data:
    /// spell-name and appraised-spell-count checks, and every buffed or
    /// tinkered rating (they read properties the server sends only after
    /// identification).
    /// </summary>
    private static readonly HashSet<int> AppraisalDependentRequirementTypes =
    [
        0, 8, 9, 10, 2000, 2001, 2003, 2005, 2006, 2007,
    ];

    /// <summary>
    /// How long a resolved profile (found or not-found) is trusted before
    /// TryLoadProfile re-reads storage. A stored profile rarely changes
    /// mid-session, so caching forever was the original design, but that
    /// also means a profile file dropped in after the plugin started was
    /// never found. Bounding the cache keeps the common case (unchanged
    /// file, repeated lookups) cheap while still noticing a later drop-in.
    /// </summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);

    private sealed record ProfileCacheEntry(
        VtankLootProfile? Profile,
        string? SourceText,
        DateTimeOffset CheckedAt);

    private readonly IPluginHost _host;
    private readonly Func<IReadOnlyList<LootRule>> _liveRules;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ProfileCacheEntry> _profileCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal MossTankLootClassifier(
        IPluginHost host,
        Func<IReadOnlyList<LootRule>> liveRules,
        TimeProvider? timeProvider = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _liveRules = liveRules ?? throw new ArgumentNullException(nameof(liveRules));
        _time = timeProvider ?? TimeProvider.System;
    }

    public PluginLootClassification Classify(
        in PluginLootClassificationContext context) =>
        DecideAgainst(_liveRules(), context);

    public bool NeedsIdentification(in PluginLootClassificationContext context)
    {
        if (context.Properties.Strings.ContainsKey(LongDescPropertyId))
            return false;

        foreach (LootRule rule in _liveRules())
        {
            foreach (VtankLootRequirement requirement in rule.VtankRequirements)
            {
                if (AppraisalDependentRequirementTypes.Contains(requirement.Type))
                    return true;
            }
        }
        return false;
    }

    public bool TryClassifyWithProfile(
        string profileName,
        in PluginLootClassificationContext context,
        out PluginLootClassification classification)
    {
        if (!TryLoadProfile(profileName, out VtankLootProfile? profile)
            || profile is null)
        {
            classification = default;
            return false;
        }
        classification = DecideAgainst(profile.Rules, context);
        return true;
    }

    private PluginLootClassification DecideAgainst(
        IReadOnlyList<LootRule> rules,
        in PluginLootClassificationContext context)
    {
        LootDecision? decision = LootRuleEngine.Decide(
            context.Item,
            context.Properties,
            rules,
            context.OwnedItems,
            pendingByName: null,
            _host);
        if (decision is not { } found)
            return default;

        // Both of MossTank's mana-transfer actions map 1:1 onto the public
        // enum (ManaStone/ManaTank). Guard the cast anyway: a future
        // MossTank-only action added without a matching public member
        // must not hand the caller an undefined enum value.
        PluginLootAction publicAction = Enum.IsDefined<PluginLootAction>((PluginLootAction)(int)found.Action)
            ? (PluginLootAction)(int)found.Action
            : PluginLootAction.NoLoot;

        return new PluginLootClassification(
            Matched: true,
            Action: publicAction,
            RuleName: found.RuleName,
            Priority: found.Priority,
            KeepCount: found.RuleIndex >= 0 && found.RuleIndex < rules.Count
                ? rules[found.RuleIndex].KeepCount
                : 0);
    }

    private bool TryLoadProfile(string? profileName, out VtankLootProfile? profile)
    {
        string normalized = (profileName ?? string.Empty).Trim();
        if (normalized.EndsWith(".utl", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        if (normalized.Length == 0)
        {
            profile = null;
            return false;
        }

        DateTimeOffset now = _time.GetUtcNow();
        if (_profileCache.TryGetValue(normalized, out ProfileCacheEntry? cached)
            && now - cached.CheckedAt < RecheckInterval)
        {
            profile = cached.Profile;
            return cached.Profile is not null;
        }

        string? text = _host.VtankProfiles.IsAvailable
            ? _host.VtankProfiles.ReadText(normalized + ".utl")
            : null;

        // Storage content has not changed since the last check: keep the
        // previously parsed result (found or not-found) but refresh the
        // stamp so the next lookup within the interval stays free.
        if (cached is not null && text == cached.SourceText)
        {
            _profileCache[normalized] = cached with { CheckedAt = now };
            profile = cached.Profile;
            return cached.Profile is not null;
        }

        if (text is null
            || !VtankLootProfileSerializer.TryRead(
                text, out VtankLootProfile parsed, out _))
        {
            _profileCache[normalized] = new ProfileCacheEntry(null, text, now);
            profile = null;
            return false;
        }

        _profileCache[normalized] = new ProfileCacheEntry(parsed, text, now);
        profile = parsed;
        return true;
    }
}

namespace AcDream.Plugin.Abstractions;

/// <summary>
/// What a loot classifier says should happen to an item. The client never
/// acts on these itself: a plugin asks a classifier for a verdict and then
/// carries it out.
/// </summary>
public enum PluginLootAction
{
    /// <summary>Leave the item where it is.</summary>
    NoLoot = 0,

    /// <summary>Take the item and hold on to it.</summary>
    Keep = 1,

    /// <summary>Take the item to be broken down for salvage.</summary>
    Salvage = 2,

    /// <summary>Take the item to be sold to a vendor.</summary>
    Sell = 3,

    /// <summary>Take the item to be read, such as a scroll.</summary>
    Read = 4,

    /// <summary>A caller-defined action; its meaning is whatever the plugin agrees it is.</summary>
    User1 = 5,

    /// <summary>A caller-defined action; its meaning is whatever the plugin agrees it is.</summary>
    User2 = 6,

    /// <summary>A caller-defined action; its meaning is whatever the plugin agrees it is.</summary>
    User3 = 7,

    /// <summary>A caller-defined action; its meaning is whatever the plugin agrees it is.</summary>
    User4 = 8,

    /// <summary>A caller-defined action; its meaning is whatever the plugin agrees it is.</summary>
    User5 = 9,

    /// <summary>
    /// Take the item, but only while fewer than
    /// <see cref="PluginLootClassification.KeepCount"/> of it are already held.
    /// </summary>
    KeepUpTo = 10,

    /// <summary>Take the item to use as a mana stone.</summary>
    ManaStone = 11,

    /// <summary>Take the item as a mana source to be drained with a mana stone.</summary>
    ManaTank = 12,
}

/// <summary>
/// Everything a classifier is given about one item it has to judge.
/// </summary>
/// <param name="Item">The item being judged.</param>
/// <param name="Properties">
/// The property tables the client holds for that item, including its weapon
/// and armor profiles when it has been appraised.
/// </param>
/// <param name="OwnedItems">
/// Everything the player already owns, so a rule with a count limit can see
/// how many of something is already held.
/// </param>
public readonly record struct PluginLootClassificationContext(
    PluginInventoryItem Item,
    PluginItemProperties Properties,
    IReadOnlyList<PluginInventoryItem> OwnedItems);

/// <summary>A classifier's verdict on one item.</summary>
/// <param name="Matched">
/// True when a rule matched the item. When it is false no rule applied and
/// the other fields carry no meaning.
/// </param>
/// <param name="Action">What the matched rule says to do with the item.</param>
/// <param name="RuleName">
/// The name of the rule that matched, for display or logging; empty when
/// nothing matched.
/// </param>
/// <param name="Priority">
/// The matched rule's priority. A caller with several candidates can use it
/// to decide which to take first; higher means more wanted.
/// </param>
/// <param name="KeepCount">
/// How many of the item to keep, for
/// <see cref="PluginLootAction.KeepUpTo"/>; zero for every other action.
/// </param>
public readonly record struct PluginLootClassification(
    bool Matched,
    PluginLootAction Action,
    string RuleName = "",
    int Priority = 0,
    int KeepCount = 0);

/// <summary>An item that was taken, and the verdict it was taken under.</summary>
/// <param name="Item">The item that was taken.</param>
/// <param name="Action">The action the classifier had reported for it.</param>
public readonly record struct PluginLootedItem(
    PluginInventoryItem Item,
    PluginLootAction Action);

/// <summary>
/// A plugin-supplied set of loot rules that other plugins can ask for
/// verdicts. Implement it to publish your own rule set; register it through
/// <see cref="IPluginLootClassifierRegistry.Register"/>.
/// </summary>
public interface IPluginLootClassifier
{
    /// <summary>
    /// Judges one item against the classifier's current rules.
    /// </summary>
    PluginLootClassification Classify(
        in PluginLootClassificationContext context);

    /// <summary>
    /// Tells the classifier that an item it judged was actually taken, so it
    /// can keep its own running counts. Does nothing unless overridden.
    /// </summary>
    void OnLooted(in PluginLootedItem item) { }

    /// <summary>
    /// Tells the classifier that an item it was tracking is gone from the
    /// player's possession. Does nothing unless overridden.
    /// </summary>
    void OnItemRemoved(uint objectId) { }

    /// <summary>
    /// True when the item cannot yet be classified with confidence because
    /// it lacks appraisal data and at least one active rule needs an
    /// appraised property to evaluate.
    /// </summary>
    bool NeedsIdentification(in PluginLootClassificationContext context) =>
        false;

    /// <summary>
    /// Classifies <paramref name="context"/> against a named, stored profile
    /// rather than the classifier's live one -- a separate saved rule list,
    /// such as one kept for vendor trips. Returns false when the named
    /// profile does not exist, and for any classifier that has no notion of
    /// named profiles.
    /// </summary>
    bool TryClassifyWithProfile(
        string profileName,
        in PluginLootClassificationContext context,
        out PluginLootClassification classification)
    {
        classification = default;
        return false;
    }
}

/// <summary>Identifies one registered classifier.</summary>
/// <param name="Id">
/// The id other plugins pass to the registry. The host scopes it by the
/// registering plugin's own id, so it reads as
/// "&lt;plugin id&gt;/&lt;classifier id&gt;".
/// </param>
/// <param name="DisplayName">A human-readable name for the classifier.</param>
public readonly record struct PluginLootClassifierInfo(
    string Id,
    string DisplayName);

/// <summary>
/// The client-wide directory of loot classifiers: one plugin publishes its
/// rules here and any other plugin can ask them for verdicts by id. Every
/// call reports false rather than throwing when the id is not registered, and
/// also when the classifier itself throws.
/// </summary>
public interface IPluginLootClassifierRegistry
{
    /// <summary>
    /// Every classifier registered right now, ordered by display name.
    /// Empty when none are.
    /// </summary>
    IReadOnlyList<PluginLootClassifierInfo> Available =>
        Array.Empty<PluginLootClassifierInfo>();

    /// <summary>
    /// Publishes a classifier under an id of your choosing, which the host
    /// scopes by your plugin id before anyone else sees it. Dispose the
    /// returned handle to withdraw it. Throws if that id is already taken, or
    /// if this host does not support classifiers at all.
    /// </summary>
    IDisposable Register(
        string classifierId,
        string displayName,
        IPluginLootClassifier classifier) =>
        throw new NotSupportedException("Loot classifiers are unavailable.");

    /// <summary>
    /// Asks a registered classifier to judge one item. Returns false when no
    /// classifier is registered under that id, or when it threw.
    /// </summary>
    bool TryClassify(
        string classifierId,
        in PluginLootClassificationContext context,
        out PluginLootClassification classification)
    {
        classification = default;
        return false;
    }

    /// <summary>
    /// Forwards to the registered classifier's
    /// <see cref="IPluginLootClassifier.OnLooted"/>. Returns false when no
    /// classifier is registered under that id, or when it threw.
    /// </summary>
    bool TryNotifyLooted(
        string classifierId,
        in PluginLootedItem item) => false;

    /// <summary>
    /// Forwards to the registered classifier's
    /// <see cref="IPluginLootClassifier.OnItemRemoved"/>. Returns false when
    /// no classifier is registered under that id, or when it threw.
    /// </summary>
    bool TryNotifyItemRemoved(
        string classifierId,
        uint objectId) => false;

    /// <summary>Forwards to the registered classifier's <see cref="IPluginLootClassifier.NeedsIdentification"/>.</summary>
    bool TryNeedsIdentification(
        string classifierId,
        in PluginLootClassificationContext context) => false;

    /// <summary>Forwards to the registered classifier's <see cref="IPluginLootClassifier.TryClassifyWithProfile"/>.</summary>
    bool TryClassifyWithProfile(
        string classifierId,
        string profileName,
        in PluginLootClassificationContext context,
        out PluginLootClassification classification)
    {
        classification = default;
        return false;
    }
}

/// <summary>
/// The registry a host installs when it supports no classifiers at all:
/// nothing can be registered and every lookup reports false.
/// </summary>
public sealed class NoOpPluginLootClassifierRegistry
    : IPluginLootClassifierRegistry
{
    /// <summary>The single shared instance of this empty registry.</summary>
    public static NoOpPluginLootClassifierRegistry Instance { get; } = new();

    private NoOpPluginLootClassifierRegistry() { }
}

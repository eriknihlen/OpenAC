using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>Process-local transactional registry for external loot plugins.</summary>
public sealed class PluginLootClassifierRegistry : IPluginLootClassifierRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PluginLootClassifierInfo> Available
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values
                    .Select(static entry => entry.Info)
                    .OrderBy(static info => info.DisplayName,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static info => info.Id,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    public IDisposable Register(
        string classifierId,
        string displayName,
        IPluginLootClassifier classifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classifierId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(classifier);
        string id = classifierId.Trim();
        var entry = new Entry(
            new PluginLootClassifierInfo(id, displayName.Trim()),
            classifier);
        lock (_gate)
        {
            if (!_entries.TryAdd(id, entry))
            {
                throw new InvalidOperationException(
                    $"Loot classifier '{id}' is already registered.");
            }
        }
        return new Registration(this, id, entry);
    }

    public bool TryClassify(
        string classifierId,
        in PluginLootClassificationContext context,
        out PluginLootClassification classification)
    {
        Entry? entry;
        lock (_gate)
            _entries.TryGetValue(classifierId ?? string.Empty, out entry);
        if (entry is null)
        {
            classification = default;
            return false;
        }
        try
        {
            classification = entry.Classifier.Classify(context);
            return true;
        }
        catch
        {
            classification = default;
            return false;
        }
    }

    public bool TryNotifyLooted(
        string classifierId,
        in PluginLootedItem item)
    {
        if (!TryGetClassifier(classifierId, out IPluginLootClassifier classifier))
            return false;
        try
        {
            classifier.OnLooted(item);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryNotifyItemRemoved(string classifierId, uint objectId)
    {
        if (!TryGetClassifier(classifierId, out IPluginLootClassifier classifier))
            return false;
        try
        {
            classifier.OnItemRemoved(objectId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetClassifier(
        string classifierId,
        out IPluginLootClassifier classifier)
    {
        classifier = null!;
        if (string.IsNullOrWhiteSpace(classifierId))
            return false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(classifierId.Trim(), out Entry? entry))
                return false;
            classifier = entry.Classifier;
            return true;
        }
    }

    private void Remove(string id, Entry expected)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out Entry? current)
                && ReferenceEquals(current, expected))
            {
                _entries.Remove(id);
            }
        }
    }

    private sealed record Entry(
        PluginLootClassifierInfo Info,
        IPluginLootClassifier Classifier);

    private sealed class Registration(
        PluginLootClassifierRegistry owner,
        string id,
        Entry entry) : IDisposable
    {
        private PluginLootClassifierRegistry? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?
            .Remove(id, entry);
    }
}

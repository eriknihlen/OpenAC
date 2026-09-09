namespace AcDream.App.UI.Layout;

public sealed class RowTemplateResolver
{
    private readonly Dictionary<(uint LayoutId, uint ElementId), ElementInfo?> _cache = new();
    private readonly Func<uint, uint, ElementInfo?> _importInfos;
    private readonly Func<ElementInfo, UiElement?> _build;

    public int ImportCount { get; private set; }

    public RowTemplateResolver(
        Func<uint, uint, ElementInfo?> importInfos,
        Func<ElementInfo, UiElement?> build)
    {
        _importInfos = importInfos ?? throw new ArgumentNullException(nameof(importInfos));
        _build = build ?? throw new ArgumentNullException(nameof(build));
    }

    public UiElement? Resolve(uint templateLayoutId, uint templateElementId)
    {
        var key = (templateLayoutId, templateElementId);
        if (!_cache.TryGetValue(key, out ElementInfo? info))
        {
            info = _importInfos(templateLayoutId, templateElementId);
            _cache[key] = info;
            ImportCount++;
        }
        return info is null ? null : _build(info);
    }

    public ElementInfo? ResolveInfo(uint templateLayoutId, uint templateElementId)
    {
        var key = (templateLayoutId, templateElementId);
        if (!_cache.TryGetValue(key, out ElementInfo? info))
        {
            info = _importInfos(templateLayoutId, templateElementId);
            _cache[key] = info;
            ImportCount++;
        }
        return info;
    }
}

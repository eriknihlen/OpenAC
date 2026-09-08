using DatReaderWriter;
using AcDream.Content;

namespace AcDream.App.UI.Layout;

public sealed class EffectRowTemplateFactory
{
    private readonly ElementInfo _template;
    private readonly Func<uint, (uint tex, int w, int h)> _resolveSprite;
    private readonly UiDatFont? _defaultFont;
    private readonly IReadOnlyDictionary<uint, UiDatFont?> _fonts;

    public EffectRowTemplateFactory(
        ElementInfo template,
        Func<uint, (uint tex, int w, int h)> resolveSprite,
        UiDatFont? defaultFont,
        IReadOnlyDictionary<uint, UiDatFont?>? fonts = null)
    {
        _template = template ?? throw new ArgumentNullException(nameof(template));
        _resolveSprite = resolveSprite ?? throw new ArgumentNullException(nameof(resolveSprite));
        _defaultFont = defaultFont;
        _fonts = fonts ?? new Dictionary<uint, UiDatFont?>();
    }

    public float Width => _template.Width;
    public float Height => _template.Height;

    public static EffectRowTemplateFactory? TryLoad(
        IDatReaderWriter dats,
        Func<uint, (uint tex, int w, int h)> resolveSprite,
        UiDatFont? defaultFont,
        Func<uint, UiDatFont?>? resolveFont)
    {
        ElementInfo? template = LayoutImporter.ImportInfos(
            dats,
            EffectsUiController.LayoutId,
            EffectsUiController.RowTemplateId);
        if (template is null
            || Find(template, EffectsUiController.RowIconId) is null
            || Find(template, EffectsUiController.RowLabelId) is null
            || Find(template, EffectsUiController.RowDurationId) is null
            || template.Width <= 0f
            || template.Height <= 0f)
            return null;

        var fonts = new Dictionary<uint, UiDatFont?>();
        CaptureFonts(template, resolveFont, fonts);
        return new EffectRowTemplateFactory(
            template, resolveSprite, defaultFont, fonts);
    }

    public EffectRow Create(
        uint spellId,
        uint iconTexture,
        string name,
        string remaining)
    {
        ImportedLayout content = LayoutImporter.Build(
            _template,
            _resolveSprite,
            _defaultFont,
            did => _fonts.TryGetValue(did, out UiDatFont? font)
                ? font
                : _defaultFont);
        UiElement iconHost = Required(content, EffectsUiController.RowIconId);
        UiText label = Required<UiText>(content, EffectsUiController.RowLabelId);
        UiText duration = Required<UiText>(content, EffectsUiController.RowDurationId);

        iconHost.AddChild(new UiTextureElement
        {
            Width = iconHost.Width,
            Height = iconHost.Height,
            Anchors = AnchorEdges.Left | AnchorEdges.Top
                | AnchorEdges.Right | AnchorEdges.Bottom,
            Texture = iconTexture,
        });
        label.LinesProvider = () => [new UiText.Line(name, label.DefaultColor)];

        var slot = new UiTemplateListSlot(
            content,
            spellId,
            UiButtonStateMachine.Normal,
            UiButtonStateMachine.Highlight);
        return new EffectRow(slot, duration, remaining);
    }

    public sealed class EffectRow
    {
        private readonly UiText _duration;
        private string _remaining;

        internal EffectRow(
            UiTemplateListSlot slot,
            UiText duration,
            string remaining)
        {
            Slot = slot;
            _duration = duration;
            _remaining = remaining;
            _duration.LinesProvider = () =>
                [new UiText.Line(_remaining, _duration.DefaultColor)];
        }

        public UiTemplateListSlot Slot { get; }

        public string Remaining
        {
            get => _remaining;
            set => _remaining = value;
        }
    }

    private static T Required<T>(ImportedLayout content, uint id)
        where T : UiElement
        => content.FindElement(id) as T
            ?? throw new InvalidOperationException(
                $"Retail effect template element 0x{id:X8} did not resolve to {typeof(T).Name}.");

    private static UiElement Required(ImportedLayout content, uint id)
        => content.FindElement(id)
            ?? throw new InvalidOperationException(
                $"Retail effect template element 0x{id:X8} is missing.");

    private static ElementInfo? Find(ElementInfo root, uint id)
    {
        if (root.Id == id) return root;
        foreach (ElementInfo child in root.Children)
            if (Find(child, id) is { } found)
                return found;
        return null;
    }

    private static void CaptureFonts(
        ElementInfo info,
        Func<uint, UiDatFont?>? resolveFont,
        Dictionary<uint, UiDatFont?> fonts)
    {
        if (info.FontDid != 0u && !fonts.ContainsKey(info.FontDid))
            fonts[info.FontDid] = resolveFont?.Invoke(info.FontDid);
        foreach (ElementInfo child in info.Children)
            CaptureFonts(child, resolveFont, fonts);
    }
}

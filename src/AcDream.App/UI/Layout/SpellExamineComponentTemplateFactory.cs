using AcDream.Content;
using DatReaderWriter;

namespace AcDream.App.UI.Layout;

public sealed class SpellExamineComponentTemplateFactory
{
    public const uint TemplateId = 0x1000032Eu;
    public const uint MissingOverlayId = 0x10000330u;

    private readonly ElementInfo _template;
    private readonly Func<uint, (uint tex, int w, int h)> _resolveSprite;
    private readonly UiDatFont? _defaultFont;
    private readonly IReadOnlyDictionary<uint, UiDatFont?> _fonts;

    public SpellExamineComponentTemplateFactory(
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

    public static SpellExamineComponentTemplateFactory? TryLoad(
        IDatReaderWriter dats,
        Func<uint, (uint tex, int w, int h)> resolveSprite,
        UiDatFont? defaultFont,
        Func<uint, UiDatFont?>? resolveFont)
    {
        ElementInfo? template = LayoutImporter.ImportInfos(
            dats,
            AppraisalUiController.LayoutId,
            TemplateId);
        if (template is null)
            return null;

        var fonts = new Dictionary<uint, UiDatFont?>();
        CaptureFonts(template, resolveFont, fonts);
        return new SpellExamineComponentTemplateFactory(
            template,
            resolveSprite,
            defaultFont,
            fonts);
    }

    public UiElement Create(uint iconTexture, bool owned)
    {
        ImportedLayout content = LayoutImporter.Build(
            _template,
            _resolveSprite,
            _defaultFont,
            did => _fonts.TryGetValue(did, out UiDatFont? font)
                ? font
                : _defaultFont);
        if (content.Root is not UiDatElement root)
        {
            throw new InvalidOperationException(
                $"Retail spell-component template 0x{TemplateId:X8} "
                + "must resolve to a UIRegion-compatible element.");
        }
        root.RuntimeImageTexture = iconTexture;

        if (content.FindElement(MissingOverlayId) is { } missing)
            missing.Visible = !owned;
        return root;
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

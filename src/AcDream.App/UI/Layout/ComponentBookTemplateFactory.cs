using DatReaderWriter;
using AcDream.Content;

namespace AcDream.App.UI.Layout;

public sealed class ComponentBookTemplateFactory
{
    public const uint LayoutId = 0x21000033u;
    public const uint CategoryTemplateId = 0x10000466u;
    public const uint ComponentTemplateId = 0x10000467u;
    public const uint IconId = 0x10000468u;
    public const uint NameId = 0x10000469u;
    public const uint OwnedCountId = 0x1000046Au;
    public const uint DesiredCountId = 0x1000046Bu;

    private const uint LocalStringTableId = 0x23000001u;
    private const uint HighlightState = UiButtonStateMachine.Highlight;

    private static readonly (string Key, string Fallback)[] CategoryStrings =
    [
        ("ID_SpellComp_Category_Scarabs", "SCARABS"),
        ("ID_SpellComp_Category_Herbs", "HERBS"),
        ("ID_SpellComp_Category_Gems", "POWDERED GEMS"),
        ("ID_SpellComp_Category_Alchemical", "ALCHEMICAL SUBSTANCES"),
        ("ID_SpellComp_Category_Talismans", "TALISMANS"),
        ("ID_SpellComp_Category_Tapers", "TAPERS"),
        ("ID_SpellComp_Category_Peas", "PEAS"),
    ];

    private readonly ElementInfo _categoryTemplate;
    private readonly ElementInfo _componentTemplate;
    private readonly Func<uint, (uint tex, int w, int h)> _resolveSprite;
    private readonly UiDatFont? _defaultFont;
    private readonly IReadOnlyDictionary<uint, UiDatFont?> _fonts;
    private readonly string[] _categoryNames;

    public ComponentBookTemplateFactory(
        ElementInfo categoryTemplate,
        ElementInfo componentTemplate,
        Func<uint, (uint tex, int w, int h)> resolveSprite,
        UiDatFont? defaultFont,
        IReadOnlyDictionary<uint, UiDatFont?>? fonts = null,
        IReadOnlyList<string>? categoryNames = null)
    {
        _categoryTemplate = categoryTemplate;
        _componentTemplate = componentTemplate;
        _resolveSprite = resolveSprite;
        _defaultFont = defaultFont;
        _fonts = fonts ?? new Dictionary<uint, UiDatFont?>();
        _categoryNames = categoryNames?.ToArray()
            ?? CategoryStrings.Select(value => value.Fallback).ToArray();
    }

    public static ComponentBookTemplateFactory? TryLoad(
        IDatReaderWriter dats,
        Func<uint, (uint tex, int w, int h)> resolveSprite,
        UiDatFont? defaultFont,
        Func<uint, UiDatFont?>? resolveFont)
    {
        ElementInfo? category = LayoutImporter.ImportInfos(
            dats, LayoutId, CategoryTemplateId);
        ElementInfo? component = LayoutImporter.ImportInfos(
            dats, LayoutId, ComponentTemplateId);
        if (category is null || component is null)
            return null;

        var fonts = new Dictionary<uint, UiDatFont?>();
        CaptureFonts(category, resolveFont, fonts);
        CaptureFonts(component, resolveFont, fonts);

        var strings = new DatStringResolver(dats);
        string[] categoryNames = CategoryStrings
            .Select(value => strings.Resolve(
                    LocalStringTableId,
                    DatStringResolver.ComputeHash(value.Key))
                ?? value.Fallback)
            .ToArray();

        return new ComponentBookTemplateFactory(
            category, component, resolveSprite, defaultFont, fonts, categoryNames);
    }

    public UiTemplateListSlot CreateCategoryRow(uint category)
    {
        ImportedLayout content = Build(_categoryTemplate);
        if (content.Root is not UiText title)
            throw new InvalidOperationException(
                "Retail component category template did not resolve to UIElement_Text.");

        string label = category < _categoryNames.Length
            ? _categoryNames[category]
            : "OTHER COMPONENTS";
        title.LinesProvider = () => [new UiText.Line(label, title.DefaultColor)];

        return new UiTemplateListSlot(
            content, uint.MaxValue, content.Root is IUiDatStateful stateful
                ? stateful.ActiveRetailStateId
                : UiStateInfo.DirectStateId, selectedState: null);
    }

    public ComponentRow CreateComponentRow(
        uint componentId,
        uint iconTexture,
        string name,
        int ownedCount,
        uint desiredCount)
    {
        ImportedLayout content = Build(_componentTemplate);
        UiElement iconHost = Required(content, IconId);
        UiText nameText = Required<UiText>(content, NameId);
        UiText ownedText = Required<UiText>(content, OwnedCountId);
        UiField desiredField = Required<UiField>(content, DesiredCountId);

        var icon = new UiTextureElement
        {
            Width = iconHost.Width,
            Height = iconHost.Height,
            Anchors = AnchorEdges.Left | AnchorEdges.Top
                | AnchorEdges.Right | AnchorEdges.Bottom,
            Texture = iconTexture,
        };
        iconHost.AddChild(icon);

        nameText.LinesProvider = () => [new UiText.Line(name, nameText.DefaultColor)];
        ownedText.LinesProvider = () =>
            [new UiText.Line(ownedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ownedText.DefaultColor)];

        desiredField.ClearOnSubmit = false;
        desiredField.RecordHistory = false;
        desiredField.SelectAllOnFocus = true;
        desiredField.CharacterFilter = char.IsAsciiDigit;
        desiredField.SetText(desiredCount.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        uint normalState = content.Root is IUiDatStateful stateful
            ? stateful.ActiveRetailStateId
            : UiStateInfo.DirectStateId;
        var slot = new UiTemplateListSlot(
            content, componentId, normalState, HighlightState);
        return new ComponentRow(slot, desiredField);
    }

    private ImportedLayout Build(ElementInfo template)
        => LayoutImporter.Build(
            template,
            _resolveSprite,
            _defaultFont,
            did => _fonts.TryGetValue(did, out UiDatFont? font) ? font : _defaultFont);

    private static T Required<T>(ImportedLayout content, uint id)
        where T : UiElement
        => content.FindElement(id) as T
            ?? throw new InvalidOperationException(
                $"Retail component template element 0x{id:X8} did not resolve to {typeof(T).Name}.");

    private static UiElement Required(ImportedLayout content, uint id)
        => content.FindElement(id)
            ?? throw new InvalidOperationException(
                $"Retail component template element 0x{id:X8} is missing.");

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

    public readonly record struct ComponentRow(
        UiTemplateListSlot Slot,
        UiField DesiredField);
}

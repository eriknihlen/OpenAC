using System.Globalization;
using AcDream.App.Rendering;
using AcDream.Core.CharGen;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.UI.Layout;

internal sealed class CharacterCreationSummaryPage : IDisposable
{
    internal const uint ListBoxId = 0x10000400u;
    internal const uint ScrollId = 0x10000401u;
    internal const uint NameTextId = 0x10000402u;
    internal const uint HowToTextId = 0x10000404u;
    internal const uint ViewportId = 0x10000406u;

    private const uint SingleLineTextId = 0x100002F9u;
    private const uint HeaderTextId = 0x100000FEu;
    private const uint KeyTextId = 0x100002FCu;
    private const uint ValueTextId = 0x100002FDu;

    private const int MaxNameLength = 32;

    private const uint HowToScrollRelativeId = 0x100002E7u;

    private static readonly IReadOnlyDictionary<uint, (string Male, string Female)> NameSuggestionKeysByHeritage =
        new Dictionary<uint, (string, string)>
        {
            [(uint)ChargenHeritageGroup.Aluvian] = ("ID_CharGen_AluMaleNames", "ID_CharGen_AluFemaleNames"),
            [(uint)ChargenHeritageGroup.Gharundim] = ("ID_CharGen_GharuMaleNames", "ID_CharGen_GharuFemaleNames"),
            [(uint)ChargenHeritageGroup.Sho] = ("ID_CharGen_ShoMaleNames", "ID_CharGen_ShoFemaleNames"),
            [(uint)ChargenHeritageGroup.Viamontian] = ("ID_CharGen_ViaMaleNames", "ID_CharGen_ViaFemaleNames"),
        };

    private readonly CharacterCreationRuntimeBindings _bindings;
    private readonly RetailDialogFactory _dialogs;
    private readonly string _nameTooLongMessage;
    private readonly UiTemplateListBox? _list;
    private readonly UiField? _nameField;
    private readonly UiText? _howToText;
    private string _lastCommittedName = string.Empty;
    private uint _nameTooLongDialogContext;
    private bool _disposed;

    internal IChargenPreviewControl? PreviewControl { get; set; }

    internal UiViewport? Viewport { get; }

    internal CharacterCreationSummaryPage(
        UiElement pageRoot,
        CharacterCreationRuntimeBindings bindings,
        RetailDialogFactory dialogs,
        string nameTooLongMessage,
        Func<uint, uint, UiElement?> templateResolver)
    {
        _bindings = bindings;
        _dialogs = dialogs;
        _nameTooLongMessage = nameTooLongMessage;

        _list = UiElement.FindDescendant(pageRoot, ListBoxId) as UiTemplateListBox;
        if (_list is not null)
            _list.TemplateResolver = templateResolver;

        if (_list is not null)
        {
            uint scrollbarElementId = _list.ScrollbarElementId;
            if (scrollbarElementId != 0
                && UiElement.FindDescendant(pageRoot, scrollbarElementId) is UiScrollbar overviewScroll)
            {
                overviewScroll.Model = _list.Scroll;
            }
        }

        _nameField = UiElement.FindDescendant(pageRoot, NameTextId) as UiField;
        if (_nameField is not null)
        {
            _nameField.CharacterFilter = NameInputFilter;
            _nameField.OnFocusLost = CommitNameFromField;
            _nameField.OnSubmit = CommitNameFromField;
            _nameField.ClearOnSubmit = false;
            _nameField.RecordHistory = false;
        }

        Viewport = UiElement.FindDescendant(pageRoot, ViewportId) as UiViewport;

        _howToText = UiElement.FindDescendant(pageRoot, HowToTextId) as UiText;
        if (_howToText is not null
            && UiElement.FindDescendant(_howToText, HowToScrollRelativeId) is UiScrollbar howToScroll)
        {
            howToScroll.Model = _howToText.Scroll;
        }
    }

    internal void Refresh(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        if (_disposed)
            return;

        if (_nameField is { IsFocused: false } field && field.Text != snapshot.Name)
        {
            field.SetText(snapshot.Name);
            _lastCommittedName = snapshot.Name;
        }

        RebuildListbox(view, snapshot);
        RebuildPreview(view, snapshot);
        RefreshHowToText(snapshot);
    }

    private void RefreshHowToText(RuntimeCharacterCreationSnapshot snapshot)
    {
        if (_howToText is null)
            return;

        Func<string, string?>? resolveText = _bindings.ResolveText;
        if (resolveText is null)
            return;

        var builder = new System.Text.StringBuilder();
        if (resolveText("ID_CharGen_SummaryHowTo") is { } howTo)
            builder.Append(howTo);
        if (NameSuggestionKeysByHeritage.TryGetValue(snapshot.HeritageId, out (string Male, string Female) keys))
        {
            string key = snapshot.GenderKey == 2u ? keys.Female : keys.Male;
            if (resolveText(key) is { } nameTokens)
                builder.Append(nameTokens);
        }
        if (resolveText("ID_CharGen_SummaryHowToEnd") is { } howToEnd)
            builder.Append(howToEnd);

        if (builder.Length == 0)
            return;

        string composedText = builder.ToString();
        var segments = new[] { new DatRichText.Segment(composedText, _howToText.DefaultColor) };
        IReadOnlyList<UiText.Line> composedLines = DatRichText.Compose(_howToText, segments);
        _howToText.LinesProvider = () => composedLines;
    }


    private void CommitNameFromField(string text)
    {
        if (_disposed)
            return;

        if (text.Length > MaxNameLength)
        {
            _nameField?.SetText(_lastCommittedName);
            ShowNameTooLongDialog();
            return;
        }

        _lastCommittedName = text;
        _bindings.SetName?.Invoke(text);
    }

    private void ShowNameTooLongDialog()
    {
        if (_nameTooLongDialogContext != 0u)
            return;
        _nameTooLongDialogContext = _dialogs.MakeMessage(
            _nameTooLongMessage,
            data =>
            {
                _ = data;
                _nameTooLongDialogContext = 0u;
            });
    }

    private static bool NameInputFilter(char c) =>
        (c < 0x100 && char.IsAsciiLetter(c)) || c is ' ' or '\'' or '-';


    private void RebuildListbox(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        if (_list is null || _list.Templates.Count < 3)
            return;

        _list.Flush();

        if (!view.Options.TryGetHeritage(snapshot.HeritageId, out ChargenHeritageOptions? heritage))
            return;

        UiTemplateListEntry lineTemplate = _list.Templates[0];
        UiTemplateListEntry headerTemplate = _list.Templates[1];
        UiTemplateListEntry pairTemplate = _list.Templates[2];

        AddLine(lineTemplate, "Profession: " + ProfessionName(heritage, snapshot.Template));
        AddLine(lineTemplate, "Gender: " + GenderName(heritage, snapshot.GenderKey));
        AddLine(lineTemplate, "Heritage: " + heritage.Name);
        AddLine(lineTemplate, "Starting Town: " + StarterAreaName(view.Options, snapshot.StartArea));

        AddHeader(headerTemplate, "Attributes");
        ChargenAttributeValues a = snapshot.Attributes;
        AddPair(pairTemplate, "Strength", a.Strength);
        AddPair(pairTemplate, "Endurance", a.Endurance);
        AddPair(pairTemplate, "Coordination", a.Coordination);
        AddPair(pairTemplate, "Quickness", a.Quickness);
        AddPair(pairTemplate, "Focus", a.Focus);
        AddPair(pairTemplate, "Self", a.Self);
        AddPair(pairTemplate, "Health", a.Endurance / 2);
        AddPair(pairTemplate, "Stamina", a.Endurance);
        AddPair(pairTemplate, "Mana", a.Self);
        AddPair(pairTemplate, "Skill Credits", snapshot.RemainingSkillCredits);

        AddSkillBucket(headerTemplate, pairTemplate, view, snapshot, "Specialized Skills", ChargenSkillAdvancementClass.Specialized);
        AddSkillBucket(headerTemplate, pairTemplate, view, snapshot, "Trained Skills", ChargenSkillAdvancementClass.Trained);
    }

    private void AddLine(UiTemplateListEntry template, string text)
    {
        if (ResolveTemplateChild(template, SingleLineTextId) is { } child)
            SetLine(child, text);
    }

    private void AddHeader(UiTemplateListEntry template, string text)
    {
        if (ResolveTemplateChild(template, HeaderTextId) is { } child)
            SetLine(child, text);
    }

    private void AddPair(UiTemplateListEntry template, string key, int value)
    {
        UiElement? row = ResolveTemplateRow(template);
        if (row is null)
            return;
        if (UiElement.FindDescendant(row, KeyTextId) is UiText keyText)
            SetLine(keyText, key);
        if (UiElement.FindDescendant(row, ValueTextId) is UiText valueText)
            SetLine(valueText, value.ToString(CultureInfo.InvariantCulture));
    }

    private static void SetLine(UiText text, string content) =>
        text.LinesProvider = () => [new UiText.Line(content, text.DefaultColor)];

    private UiElement? ResolveTemplateRow(UiTemplateListEntry template)
    {
        if (_list is null || _list.TemplateResolver is null)
            return null;
        UiElement? row = _list.TemplateResolver(template.TemplateLayoutId, template.TemplateElementId);
        if (row is null)
            return null;
        _list.AddPrebuiltRow(row);
        return row;
    }

    private UiText? ResolveTemplateChild(UiTemplateListEntry template, uint childId)
    {
        UiElement? row = ResolveTemplateRow(template);
        return row is null ? null : UiElement.FindDescendant(row, childId) as UiText;
    }

    private void AddSkillBucket(
        UiTemplateListEntry headerTemplate,
        UiTemplateListEntry pairTemplate,
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot,
        string header,
        ChargenSkillAdvancementClass targetClass)
    {
        AddHeader(headerTemplate, header);
        for (uint skillId = 1; skillId < ChargenSkillAdvancementSet.SlotCount; skillId++)
        {
            if (view.GetSkillLevel(skillId) != targetClass)
                continue;
            uint score = _bindings.GetSkillScore?.Invoke(skillId, snapshot.Attributes, targetClass) ?? 0u;
            AddPair(pairTemplate, ItemAppraisalTextFormatter.SkillName((int)skillId), (int)score);
        }
    }

    private static string ProfessionName(ChargenHeritageOptions heritage, uint template) =>
        template != RuntimeCharacterCreationSnapshot.TemplateUnset
            && template < (uint)heritage.Templates.Count
            ? heritage.Templates[(int)template].Name
            : "None";

    private static string GenderName(ChargenHeritageOptions heritage, uint genderKey) =>
        heritage.GendersByKey.TryGetValue((int)genderKey, out ChargenGenderOptions? gender)
            ? gender.Name
            : "None";

    private static string StarterAreaName(ChargenOptions options, int startArea) =>
        startArea >= 0 && startArea < options.StarterAreas.Count
            ? options.StarterAreas[startArea].Name
            : "None";


    private void RebuildPreview(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        if (PreviewControl is null
            || snapshot.HeritageId == 0u
            || snapshot.GenderKey == 0u)
        {
            return;
        }

        RuntimeCharacterCreationAppearance a = snapshot.Appearance;
        var selection = new ChargenAppearanceSelection(
            a.EyesStrip, a.NoseStrip, a.MouthStrip,
            a.HairStyle, a.HairColor, a.EyeColor,
            a.HeadgearStyle, a.HeadgearColor,
            a.ShirtStyle, a.ShirtColor,
            a.TrousersStyle, a.TrousersColor,
            a.FootwearStyle, a.FootwearColor,
            a.SkinShade, a.HairShade, a.HeadgearShade,
            a.ShirtShade, a.TrousersShade, a.FootwearShade);

        PreviewControl.Rebuild(view.Options, snapshot.HeritageId, (int)snapshot.GenderKey, selection);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_nameField is not null)
        {
            _nameField.OnFocusLost = null;
            _nameField.OnSubmit = null;
        }
        if (_nameTooLongDialogContext != 0u)
        {
            uint closing = _nameTooLongDialogContext;
            _nameTooLongDialogContext = 0u;
            _dialogs.CloseDialog(closing);
        }
        if (_list is not null)
            _list.TemplateResolver = null;
        _list?.Flush();
        PreviewControl = null;
    }
}

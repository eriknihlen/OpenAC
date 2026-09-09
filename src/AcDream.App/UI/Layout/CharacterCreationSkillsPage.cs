using System.Globalization;
using System.Numerics;
using System.Text;
using AcDream.Core.CharGen;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.UI.Layout;

internal sealed class CharacterCreationSkillsPage : IDisposable
{
    private enum SkillBucket
    {
        Specialized,
        Trained,
        UseableUntrained,
        UnuseableUntrained,
    }

    private static readonly (SkillBucket Bucket, string StringKey)[] BucketOrder =
    [
        (SkillBucket.Specialized, "ID_CharGen_Specialized"),
        (SkillBucket.Trained, "ID_CharGen_Trained"),
        (SkillBucket.UseableUntrained, "ID_CharGen_UseableUntrained"),
        (SkillBucket.UnuseableUntrained, "ID_CharGen_UnuseableUntrained"),
    ];

    private static SkillBucket ComputeBucket(ChargenSkillAdvancementClass level, uint minLevel) => level switch
    {
        ChargenSkillAdvancementClass.Specialized => SkillBucket.Specialized,
        ChargenSkillAdvancementClass.Trained => SkillBucket.Trained,
        _ => minLevel <= 1 ? SkillBucket.UseableUntrained : SkillBucket.UnuseableUntrained,
    };

    private const uint HeaderCaptionElementId = 0x100002F6u;

    private const uint RowNameTextId = 0x10000301u;

    private const uint RowLevelTextId = 0x10000302u;

    private const uint RowUpCostTextId = 0x10000303u;

    private const uint RowDownCostTextId = 0x10000306u;

    private const uint RowUpButtonId = 0x10000304u;

    private const uint RowDownButtonId = 0x10000305u;

    private const uint ArrowGhostedStateId = 0x1000001Au;

    private const uint ArrowEnabledStateId = 0x1000001Bu;

    private static readonly Vector4 SelectedNameColor = Vector4.One;

    private readonly record struct SkillRow(
        UiElement Root,
        uint SkillId,
        SkillBucket Bucket,
        UiText? NameText,
        UiText? LevelText,
        UiText? UpCostText,
        UiText? DownCostText,
        UiButton? UpButton,
        UiButton? DownButton,
        Vector4 UnselectedNameColor);

    private readonly CharacterCreationRuntimeBindings _bindings;
    private readonly UiTemplateListBox? _list;
    private readonly UiButton? _credits;
    private readonly UiText? _infoTitle;
    private readonly UiText? _infoText;
    private readonly List<SkillRow> _rows = [];
    private uint _lastHeritageId;
    private uint? _selectedSkillId;
    private bool _rowsBuilt;
    private bool _disposed;

    internal CharacterCreationSkillsPage(
        UiElement pageRoot,
        CharacterCreationRuntimeBindings bindings,
        Func<uint, uint, UiElement?> templateResolver)
    {
        _bindings = bindings;
        _list = UiElement.FindDescendant(pageRoot, 0x100003F7u) as UiTemplateListBox;
        if (_list is not null)
            _list.TemplateResolver = templateResolver;

        if (_list is not null
            && _list.ScrollbarElementId != 0
            && UiElement.FindDescendant(pageRoot, _list.ScrollbarElementId) is UiScrollbar scrollbar)
        {
            scrollbar.Model = _list.Scroll;
        }

        _credits = UiElement.FindDescendant(pageRoot, 0x100003F9u) as UiButton;
        _infoTitle = UiElement.FindDescendant(pageRoot, 0x100003FBu) as UiText;
        _infoText = UiElement.FindDescendant(pageRoot, 0x100003FCu) as UiText;


        if (_infoText is { } clampedInfoText
            && UiElement.FindDescendant(pageRoot, InfoBoxFrameElementId) is { } frame)
        {
            float frameBottom = frame.Top + frame.Height;
            float paneBottom = clampedInfoText.Top + clampedInfoText.Height;
            if (frameBottom < paneBottom)
                clampedInfoText.Height = frameBottom - clampedInfoText.Top;
        }
    }

    private const uint InfoBoxFrameElementId = 0x100003FAu;

    internal void Refresh(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        bool heritageChanged = !_rowsBuilt || _lastHeritageId != snapshot.HeritageId;
        bool bucketsChanged = !heritageChanged && AnyRowBucketChanged(view);
        if (heritageChanged || bucketsChanged)
        {
            uint? preservedSkillId = heritageChanged ? null : _selectedSkillId;
            RebuildRows(view, snapshot.HeritageId);
            _lastHeritageId = snapshot.HeritageId;
            _rowsBuilt = true;
            if (preservedSkillId is { } skillId)
            {
                foreach (SkillRow candidate in _rows)
                {
                    if (candidate.SkillId != skillId)
                        continue;
                    _selectedSkillId = skillId;
                    ApplySelectionHighlight();
                    break;
                }
            }
        }

        foreach (SkillRow row in _rows)
            RefreshRowValues(row, view, snapshot);

        RefreshInfoBox(view, snapshot);

        if (_credits is { } credits)
            credits.ValueLabel = snapshot.RemainingSkillCredits.ToString(CultureInfo.InvariantCulture);
    }

    private bool AnyRowBucketChanged(IRuntimeCharacterCreationView view)
    {
        foreach (SkillRow row in _rows)
        {
            ChargenSkillAdvancementClass level = view.GetSkillLevel(row.SkillId);
            uint minLevel = view.Options.TryGetSkillDetail(row.SkillId, out ChargenSkillDetail detail)
                ? detail.MinLevel
                : 1u; // Unknown detail (missing global SkillTable entry) defaults to useable — the least surprising fallback.
            if (ComputeBucket(level, minLevel) != row.Bucket)
                return true;
        }
        return false;
    }

    private void RebuildRows(IRuntimeCharacterCreationView view, uint heritageId)
    {
        foreach (SkillRow row in _rows)
        {
            if (row.UpButton is not null) row.UpButton.OnClick = null;
            if (row.DownButton is not null) row.DownButton.OnClick = null;
            if (row.Root is UiDatElement datRoot) datRoot.OnClick = null;
        }
        _rows.Clear();
        _list?.Flush();

        _selectedSkillId = null;
        ClearInfoBox();

        if (_list is null
            || _list.Templates.Count < 2
            || _list.TemplateResolver is null
            || !view.Options.TryGetHeritage(heritageId, out ChargenHeritageOptions? heritage))
        {
            return;
        }

        var byBucket = new Dictionary<SkillBucket, List<(uint SkillId, string Name)>>(4)
        {
            [SkillBucket.Specialized] = [],
            [SkillBucket.Trained] = [],
            [SkillBucket.UseableUntrained] = [],
            [SkillBucket.UnuseableUntrained] = [],
        };
        for (uint skillId = 1; skillId < ChargenSkillAdvancementSet.SlotCount; skillId++)
        {
            if (!IsCostable(heritage, view.Options, skillId))
                continue;
            ChargenSkillAdvancementClass level = view.GetSkillLevel(skillId);
            uint minLevel = view.Options.TryGetSkillDetail(skillId, out ChargenSkillDetail detail)
                ? detail.MinLevel
                : 1u;
            string name = ItemAppraisalTextFormatter.SkillName((int)skillId);
            byBucket[ComputeBucket(level, minLevel)].Add((skillId, name));
        }
        foreach (List<(uint SkillId, string Name)> bucketSkills in byBucket.Values)
            bucketSkills.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        if (_list.Templates.Count < 1)
            return;
        UiTemplateListEntry headerTemplate = _list.Templates[0];
        UiTemplateListEntry rowTemplate = _list.Templates[1];

        foreach ((SkillBucket bucket, string stringKey) in BucketOrder)
        {
            BuildHeaderRow(headerTemplate, stringKey);
            foreach ((uint skillId, _) in byBucket[bucket])
                BuildSkillRow(rowTemplate, skillId, bucket);
        }
    }

    private void BuildHeaderRow(UiTemplateListEntry template, string stringKey)
    {
        if (_list!.TemplateResolver!(template.TemplateLayoutId, template.TemplateElementId) is not { } headerRoot)
            return;
        _list.AddPrebuiltRow(headerRoot);
        if (UiElement.FindDescendant(headerRoot, HeaderCaptionElementId) is UiButton caption
            && _bindings.ResolveText?.Invoke(stringKey) is { } text)
        {
            caption.Label = text;
        }
    }

    private void BuildSkillRow(UiTemplateListEntry template, uint skillId, SkillBucket bucket)
    {
        if (_list!.TemplateResolver!(template.TemplateLayoutId, template.TemplateElementId) is not { } rowRoot)
            return;

        _list.AddPrebuiltRow(rowRoot);

        UiText? nameText = UiElement.FindDescendant(rowRoot, RowNameTextId) as UiText;
        if (nameText is not null)
            SetLine(nameText, ItemAppraisalTextFormatter.SkillName((int)skillId));
        Vector4 unselectedColor = nameText?.DefaultColor ?? Vector4.One;
        UiText? levelText = UiElement.FindDescendant(rowRoot, RowLevelTextId) as UiText;
        UiText? upCostText = UiElement.FindDescendant(rowRoot, RowUpCostTextId) as UiText;
        UiText? downCostText = UiElement.FindDescendant(rowRoot, RowDownCostTextId) as UiText;
        UiButton? upButton = UiElement.FindDescendant(rowRoot, RowUpButtonId) as UiButton;
        UiButton? downButton = UiElement.FindDescendant(rowRoot, RowDownButtonId) as UiButton;

        uint capturedSkillId = skillId;
        if (upButton is not null)
            upButton.OnClick = () => { Advance(capturedSkillId); SelectRow(capturedSkillId); };
        if (downButton is not null)
            downButton.OnClick = () => { Retreat(capturedSkillId); SelectRow(capturedSkillId); };

        if (rowRoot is UiDatElement datRow)
        {
            datRow.ClickThrough = false;
            datRow.OnClick = () => SelectRow(capturedSkillId);
        }

        _rows.Add(new SkillRow(
            rowRoot, skillId, bucket, nameText, levelText, upCostText, downCostText,
            upButton, downButton, unselectedColor));
    }

    private void RefreshRowValues(
        SkillRow row,
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        ChargenSkillAdvancementClass level = view.GetSkillLevel(row.SkillId);
        (int trainedCost, int specializedCost) = GetCosts(view, snapshot.HeritageId, row.SkillId);
        uint score = _bindings.GetSkillScore?.Invoke(row.SkillId, snapshot.Attributes, level) ?? 0u;

        if (row.LevelText is { } levelText)
            SetLine(levelText, score.ToString(CultureInfo.InvariantCulture));

        string upCostText;
        string downCostText;
        bool upEnabled;
        bool downEnabled;
        switch (level)
        {
            case ChargenSkillAdvancementClass.Specialized:
                upCostText = "0";
                downCostText = (specializedCost - trainedCost).ToString(CultureInfo.InvariantCulture);
                upEnabled = false;
                downEnabled = specializedCost != 0;
                break;
            case ChargenSkillAdvancementClass.Trained:
                upCostText = FormatGatedCost(specializedCost - trainedCost);
                downCostText = trainedCost.ToString(CultureInfo.InvariantCulture);
                upEnabled = snapshot.RemainingSkillCredits >= specializedCost - trainedCost;
                downEnabled = trainedCost != 0;
                break;
            default:
                upCostText = FormatGatedCost(trainedCost);
                downCostText = "0";
                upEnabled = snapshot.RemainingSkillCredits >= trainedCost;
                downEnabled = false;
                break;
        }

        if (row.UpCostText is { } upCostTextWidget)
            SetLine(upCostTextWidget, upCostText);
        if (row.DownCostText is { } downCostTextWidget)
            SetLine(downCostTextWidget, downCostText);
        row.UpButton?.TrySetRetailState(upEnabled ? ArrowEnabledStateId : ArrowGhostedStateId);
        row.DownButton?.TrySetRetailState(downEnabled ? ArrowEnabledStateId : ArrowGhostedStateId);
    }

    private static string FormatGatedCost(int cost) =>
        cost < 999 ? cost.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static void SetLine(UiText text, string content) =>
        text.LinesProvider = () => [new UiText.Line(content, text.DefaultColor)];

    /// <summary>Same dictionary-presence gate as
    /// <c>RuntimeCharacterCreationState.TryGetSkillCost</c> — heritage list
    /// first, global SkillTable fallback.</summary>
    private static bool IsCostable(
        ChargenHeritageOptions heritage,
        ChargenOptions options,
        uint skillId) =>
        heritage.SkillCostsBySkillId.ContainsKey(skillId)
        || options.GlobalSkillCostsBySkillId.ContainsKey(skillId);

    private static (int Trained, int Specialized) GetCosts(
        IRuntimeCharacterCreationView view,
        uint heritageId,
        uint skillId)
    {
        if (view.Options.TryGetHeritage(heritageId, out ChargenHeritageOptions? heritage))
        {
            if (heritage.SkillCostsBySkillId.TryGetValue(skillId, out ChargenSkillCost cost))
                return (cost.NormalCost, cost.PrimaryCost);
        }
        if (view.Options.GlobalSkillCostsBySkillId.TryGetValue(skillId, out ChargenSkillCost global))
            return (global.NormalCost, global.PrimaryCost);
        return (0, 0);
    }

    private void Advance(uint skillId)
    {
        if (_disposed)
            return;
        ChargenSkillAdvancementClass level = _bindings.View()?.GetSkillLevel(skillId)
            ?? ChargenSkillAdvancementClass.Inactive;
        if (level is ChargenSkillAdvancementClass.Inactive or ChargenSkillAdvancementClass.Untrained)
            _bindings.TrainSkill(skillId);
        else if (level == ChargenSkillAdvancementClass.Trained)
            _bindings.SpecializeSkill(skillId);
    }

    private void Retreat(uint skillId)
    {
        if (_disposed)
            return;
        ChargenSkillAdvancementClass level = _bindings.View()?.GetSkillLevel(skillId)
            ?? ChargenSkillAdvancementClass.Inactive;
        if (level == ChargenSkillAdvancementClass.Specialized)
            _bindings.TrainSkill(skillId);
        else if (level == ChargenSkillAdvancementClass.Trained)
            _bindings.UntrainSkill(skillId);
    }

    private void SelectRow(uint skillId)
    {
        if (_disposed)
            return;
        _selectedSkillId = skillId;
        ApplySelectionHighlight();
        if (_bindings.View() is { } view)
            RefreshInfoBox(view, view.Snapshot);
    }

    private void ApplySelectionHighlight()
    {
        foreach (SkillRow row in _rows)
        {
            if (row.NameText is { } nameText)
                nameText.DefaultColor = row.SkillId == _selectedSkillId ? SelectedNameColor : row.UnselectedNameColor;
        }
    }

    private void RefreshInfoBox(IRuntimeCharacterCreationView view, RuntimeCharacterCreationSnapshot snapshot)
    {
        if (_selectedSkillId is not { } skillId)
        {
            ClearInfoBox();
            return;
        }

        ChargenSkillAdvancementClass level = view.GetSkillLevel(skillId);
        uint score = _bindings.GetSkillScore?.Invoke(skillId, snapshot.Attributes, level) ?? 0u;
        string name = ItemAppraisalTextFormatter.SkillName((int)skillId);

        if (_infoTitle is { } title)
            SetLine(title, $"{name} ({score.ToString(CultureInfo.InvariantCulture)})");

        if (_infoText is { } text)
        {
            string bonus = level switch
            {
                ChargenSkillAdvancementClass.Trained => "Training Bonus  +5",
                ChargenSkillAdvancementClass.Specialized => "Specialization Bonus  +10",
                _ => string.Empty,
            };

            bool hasDetail = view.Options.TryGetSkillDetail(skillId, out ChargenSkillDetail detail);
            var lines = new List<UiText.Line>();
            if (hasDetail && !string.IsNullOrEmpty(detail.Description))
            {
                lines.AddRange(DatRichText.Compose(
                    text, [new DatRichText.Segment(detail.Description, text.DefaultColor)]));
            }
            if (bonus.Length > 0)
                lines.Add(new UiText.Line(bonus, text.DefaultColor));
            if (hasDetail)
                lines.Add(new UiText.Line(ComposeFormula(detail.Formula), text.DefaultColor));

            text.LinesProvider = () => lines;
        }
    }

    private static string ComposeFormula(ChargenSkillFormula formula)
    {
        bool attribute1Active = formula.Attribute1Multiplier >= 1 && formula.Attribute1 != 0;
        bool attribute2Active = formula.Attribute2Multiplier >= 1 && formula.Attribute2 != 0;

        var builder = new StringBuilder("Formula : ");
        if (attribute1Active)
        {
            AppendAttributeTerm(builder, formula.Attribute1Multiplier, formula.Attribute1);
            if (attribute2Active)
                builder.Append(" + ");
        }
        if (attribute2Active)
            AppendAttributeTerm(builder, formula.Attribute2Multiplier, formula.Attribute2);

        if (formula.Divisor != 1)
            builder.Append(CultureInfo.InvariantCulture, $" / {formula.Divisor}");
        if (formula.AdditiveBonus != 0)
            builder.Append(CultureInfo.InvariantCulture, $" +{formula.AdditiveBonus}");
        return builder.ToString();
    }

    private static void AppendAttributeTerm(StringBuilder builder, int multiplier, uint attributeId)
    {
        string name = AttributeName((ChargenAttributeId)attributeId);
        if (multiplier > 1)
            builder.Append(CultureInfo.InvariantCulture, $"({multiplier} x {name})");
        else
            builder.Append(name);
    }

    private static string AttributeName(ChargenAttributeId id) => id switch
    {
        ChargenAttributeId.Strength => "Strength",
        ChargenAttributeId.Endurance => "Endurance",
        ChargenAttributeId.Quickness => "Quickness",
        ChargenAttributeId.Coordination => "Coordination",
        ChargenAttributeId.Focus => "Focus",
        ChargenAttributeId.Self => "Self",
        _ => string.Empty,
    };

    private void ClearInfoBox()
    {
        if (_infoTitle is { } title) SetLine(title, string.Empty);
        if (_infoText is { } text) SetLine(text, string.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (SkillRow row in _rows)
        {
            if (row.UpButton is not null) row.UpButton.OnClick = null;
            if (row.DownButton is not null) row.DownButton.OnClick = null;
            if (row.Root is UiDatElement datRow) datRow.OnClick = null;
        }
        _rows.Clear();
        _list?.Flush();
        if (_list is not null)
            _list.TemplateResolver = null;
    }
}

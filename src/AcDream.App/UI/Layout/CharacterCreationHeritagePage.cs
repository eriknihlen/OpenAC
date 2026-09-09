using System.Numerics;
using AcDream.Core.CharGen;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.UI.Layout;

internal sealed class CharacterCreationHeritagePage : IDisposable
{
    private static readonly IReadOnlyDictionary<uint, uint> HeritageByButtonId =
        new Dictionary<uint, uint>
        {
            [0x100003BFu] = (uint)ChargenHeritageGroup.Aluvian,
            [0x100003C1u] = (uint)ChargenHeritageGroup.Gharundim,
            [0x100003C2u] = (uint)ChargenHeritageGroup.Sho,
            [0x100003C3u] = (uint)ChargenHeritageGroup.Viamontian,
            [0x10000590u] = (uint)ChargenHeritageGroup.Shadowbound,
            [0x100005A9u] = (uint)ChargenHeritageGroup.Gearknight,
            [0x100005E8u] = (uint)ChargenHeritageGroup.Tumerok,
            [0x100005F1u] = (uint)ChargenHeritageGroup.Lugian,
            [0x100005C4u] = (uint)ChargenHeritageGroup.Empyrean,
            [0x10000591u] = (uint)ChargenHeritageGroup.Penumbraen,
            [0x100005BFu] = (uint)ChargenHeritageGroup.Undead,
            [0x100005C7u] = (uint)ChargenHeritageGroup.Olthoi,
            [0x100005C8u] = (uint)ChargenHeritageGroup.OlthoiAcid,
        };

    private static readonly IReadOnlyDictionary<uint, string> BonusSkillsKeyByHeritage =
        new Dictionary<uint, string>
        {
            [(uint)ChargenHeritageGroup.Aluvian] = "ID_CharGen_AluvianText_BonusSkills_Trained",
            [(uint)ChargenHeritageGroup.Gharundim] = "ID_CharGen_GaruText_BonusSkills_Trained",
            [(uint)ChargenHeritageGroup.Sho] = "ID_CharGen_ShoText_BonusSkills_Trained",
            [(uint)ChargenHeritageGroup.Viamontian] = "ID_CharGen_ViaText_BonusSkills_Trained",
            [(uint)ChargenHeritageGroup.Shadowbound] = "ID_CharGen_ShadText_BonusSkills_Trained",
            [(uint)ChargenHeritageGroup.Penumbraen] = "ID_CharGen_ShadText_BonusSkills_Trained",
            [(uint)ChargenHeritageGroup.Gearknight] = "ID_CharGen_GearText_BonusSkills_Trained",
            [(uint)ChargenHeritageGroup.Tumerok] = "ID_CharGen_AunTText_BonusSkills_Trained",
            [(uint)ChargenHeritageGroup.Empyrean] = "ID_CharGen_EmpText_BonusSkills_Trained",
            [(uint)ChargenHeritageGroup.Undead] = "ID_CharGen_UndText_BonusSkills_Trained",
        };

    private static readonly IReadOnlyDictionary<uint, uint> BackdropStateByHeritage =
        new Dictionary<uint, uint>
        {
            [(uint)ChargenHeritageGroup.Aluvian] = 0x10000021u,
            [(uint)ChargenHeritageGroup.Gharundim] = 0x10000022u,
            [(uint)ChargenHeritageGroup.Sho] = 0x10000023u,
            [(uint)ChargenHeritageGroup.Viamontian] = 0x10000024u,
            [(uint)ChargenHeritageGroup.Shadowbound] = 0x10000058u,
            [(uint)ChargenHeritageGroup.Gearknight] = 0x1000005Au,
            [(uint)ChargenHeritageGroup.Tumerok] = 0x1000005Fu,
            [(uint)ChargenHeritageGroup.Lugian] = 0x10000060u,
            [(uint)ChargenHeritageGroup.Empyrean] = 0x1000005Cu,
            [(uint)ChargenHeritageGroup.Penumbraen] = 0x10000059u,
            [(uint)ChargenHeritageGroup.Undead] = 0x1000005Bu,
            [(uint)ChargenHeritageGroup.Olthoi] = 0x1000005Du,
            [(uint)ChargenHeritageGroup.OlthoiAcid] = 0x1000005Eu,
        };

    private readonly CharacterCreationRuntimeBindings _bindings;
    private readonly Action<uint> _onButtonClicked;
    private readonly Dictionary<UiButton, uint> _buttons = [];
    private readonly UiText? _description;
    private readonly UiElement? _backdrop;
    private bool _disposed;

    internal CharacterCreationHeritagePage(
        UiElement pageRoot,
        CharacterCreationRuntimeBindings bindings,
        Action<uint> onButtonClicked)
    {
        _bindings = bindings;
        _onButtonClicked = onButtonClicked;
        foreach ((uint buttonId, uint heritageId) in HeritageByButtonId)
        {
            if (UiElement.FindDescendant(pageRoot, buttonId) is not UiButton button)
                continue;
            _buttons[button] = heritageId;
            button.OnClick = () =>
            {
                _onButtonClicked(buttonId);
                Select(heritageId);
            };
        }

        _description = UiElement.FindDescendant(pageRoot, 0x100003C4u) as UiText;
        _backdrop = UiElement.FindDescendant(pageRoot, 0x100003BEu);

        if (_description is not null
            && UiElement.FindDescendant(_description, 0x100002E7u) is UiScrollbar descriptionScroll)
        {
            descriptionScroll.Model = _description.Scroll;
        }
    }

    internal void Refresh(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        foreach ((UiButton button, uint heritageId) in _buttons)
            button.Selected = heritageId == snapshot.HeritageId;

        if (_backdrop is IUiDatStateful backdropStateful
            && BackdropStateByHeritage.TryGetValue(snapshot.HeritageId, out uint backdropState))
        {
            backdropStateful.TrySetRetailState(backdropState);
        }

        if (_description is null)
            return;

        IReadOnlyList<DatRichText.Segment> segments = ComposeSegments(
            _description, view, snapshot.HeritageId, _bindings.ResolveText);
        IReadOnlyList<UiText.Line> composed = DatRichText.Compose(_description, segments);
        _description.LinesProvider = () => composed;
    }

    internal void Randomize(RuntimeCharacterCreationSnapshot snapshot)
    {
        IRuntimeCharacterCreationView? view = _bindings.View();
        if (view is null || view.Options.HeritagesById.Count == 0)
            return;
        uint[] ids = [.. view.Options.HeritagesById.Keys];
        uint chosen = ids[Random.Shared.Next(ids.Length)];
        Select(chosen);
    }

    private void Select(uint heritageId)
    {
        if (_disposed)
            return;
        _bindings.SelectHeritage(heritageId);
    }

    private static IReadOnlyList<DatRichText.Segment> ComposeSegments(
        UiText description,
        IRuntimeCharacterCreationView view,
        uint heritageId,
        Func<string, string?>? resolveText)
    {
        Vector4 headerColor = DatRichText.PaletteColor(description, 1, new Vector4(0f, 1f, 0f, 1f));
        Vector4 bodyColor = DatRichText.PaletteColor(description, 0, Vector4.One);

        if (resolveText is null)
        {
            string name = view.Options.TryGetHeritage(heritageId, out ChargenHeritageOptions? named)
                ? named.Name
                : string.Empty;
            return [new DatRichText.Segment(name, bodyColor)];
        }

        var segments = new List<DatRichText.Segment>();
        if (resolveText("ID_CharGen_Heritage_StartingSkills_Header") is { } header)
            segments.Add(new(header, headerColor));
        if (resolveText("ID_CharGen_Heritage_StartingSkills") is { } body)
            segments.Add(new(body, bodyColor));
        if (resolveText("ID_CharGen_Heritage_BonusSkills_Trained_Header") is { } bonusHeader)
            segments.Add(new(bonusHeader, headerColor));
        if (heritageId != 0
            && BonusSkillsKeyByHeritage.TryGetValue(heritageId, out string? bonusKey)
            && resolveText(bonusKey) is { } bonusBody)
        {
            segments.Add(new(bonusBody, bodyColor));
        }
        return segments;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (UiButton button in _buttons.Keys)
            button.OnClick = null;
        _buttons.Clear();
    }
}

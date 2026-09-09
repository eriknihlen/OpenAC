using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.UI.Layout;

internal sealed class CharacterCreationTownPage : IDisposable
{
    private static readonly IReadOnlyDictionary<uint, int> StartAreaByButtonId =
        new Dictionary<uint, int>
        {
            [0x1000040Du] = 0, // Holtburg
            [0x1000040Fu] = 1, // Shoushi
            [0x1000040Eu] = 2, // Yaraq
            [0x1000040Bu] = 3,
        };

    private static readonly IReadOnlyDictionary<int, string> TownTextKeyByStartArea =
        new Dictionary<int, string>
        {
            [0] = "ID_CharGen_HoltText",
            [1] = "ID_CharGen_ShoushiText",
            [2] = "ID_CharGen_YaraqText",
            [3] = "ID_CharGen_SanamarText",
        };

    private static readonly IReadOnlyDictionary<int, uint> PageStateByStartArea =
        new Dictionary<int, uint>
        {
            [0] = 0x10000034u, // Holtburg
            [1] = 0x10000037u, // Shoushi
            [2] = 0x10000036u, // Yaraq
            [3] = 0x10000035u, // Sanamar
        };

    private readonly CharacterCreationRuntimeBindings _bindings;
    private readonly UiElement _pageRoot;
    private readonly Dictionary<UiButton, int> _buttons = [];
    private readonly UiText? _description;
    private bool _disposed;

    internal CharacterCreationTownPage(
        UiElement pageRoot,
        CharacterCreationRuntimeBindings bindings)
    {
        _bindings = bindings;
        _pageRoot = pageRoot;
        foreach ((uint buttonId, int startArea) in StartAreaByButtonId)
        {
            if (UiElement.FindDescendant(pageRoot, buttonId) is not UiButton button)
                continue;
            _buttons[button] = startArea;
            button.OnClick = () => Select(startArea);
        }

        _description = UiElement.FindDescendant(pageRoot, 0x10000409u) as UiText;
    }

    internal void Refresh(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        foreach ((UiButton button, int startArea) in _buttons)
            button.Selected = startArea == snapshot.StartArea;

        if (PageStateByStartArea.TryGetValue(snapshot.StartArea, out uint pageStateId)
            && _pageRoot is IUiDatStateful stateful)
        {
            stateful.TrySetRetailState(pageStateId);
        }

        if (_description is null)
            return;

        string composed = ComposeDescription(snapshot.StartArea, _bindings.ResolveText);
        var segments = new[] { new DatRichText.Segment(composed, _description.DefaultColor) };
        IReadOnlyList<UiText.Line> composedLines = DatRichText.Compose(_description, segments);
        _description.LinesProvider = () => composedLines;
    }

    internal void Randomize(IRuntimeCharacterCreationView view)
    {
        int bound = Math.Min(4, view.Options.StarterAreas.Count);
        if (bound <= 0)
            return;
        Select(Random.Shared.Next(bound));
    }

    private void Select(int startArea)
    {
        if (_disposed)
            return;
        _bindings.SelectStartArea(startArea);
    }

    private static string ComposeDescription(int startArea, Func<string, string?>? resolveText)
    {
        if (resolveText is null)
            return string.Empty;
        string? howTo = resolveText("ID_CharGen_TownHowTo");
        string? townText = TownTextKeyByStartArea.TryGetValue(startArea, out string? key)
            ? resolveText(key)
            : null;
        if (howTo is null && townText is null)
            return string.Empty;
        return $"{howTo}\n\n{townText}\n";
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

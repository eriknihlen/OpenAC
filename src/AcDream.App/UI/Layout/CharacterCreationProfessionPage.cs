using System.Globalization;
using AcDream.Core.CharGen;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.UI.Layout;

internal sealed class CharacterCreationProfessionPage : IDisposable
{
    private static readonly IReadOnlyDictionary<uint, uint> TemplateByButtonId =
        new Dictionary<uint, uint>
        {
            [0x100003D9u] = 0u,
            [0x100003DAu] = 1u, // Bow Hunter
            [0x100003DFu] = 2u, // Swashbuckler
            [0x100003DBu] = 3u,
            [0x100003DCu] = 4u,
            [0x100003DDu] = 5u, // Wayfarer
            [0x100003DEu] = 6u, // Soldier
        };

    private static readonly IReadOnlyDictionary<ChargenAttributeId, uint> SliderContainerByAttribute =
        new Dictionary<ChargenAttributeId, uint>
        {
            [ChargenAttributeId.Strength] = 0x100003E6u,
            [ChargenAttributeId.Endurance] = 0x100003E7u,
            [ChargenAttributeId.Coordination] = 0x100003E8u,
            [ChargenAttributeId.Quickness] = 0x100003E9u,
            [ChargenAttributeId.Focus] = 0x100003EAu,
            [ChargenAttributeId.Self] = 0x100003EBu,
        };

    private const uint SliderLockRelativeId = 0x100002ECu;
    private const uint SliderControlRelativeId = 0x100002EEu;
    private const uint SliderValueRelativeId = 0x100002EFu;

    private sealed record SliderWidgets(UiButton? Lock, UiScrollbar? Slider, UiField? Value);

    private const uint SliderNameRelativeId = 0x100002EDu;

    private const uint DescriptionTextId = 0x100003E0u;

    private static readonly IReadOnlyDictionary<uint, uint> BackdropStateByTemplate =
        new Dictionary<uint, uint>
        {
            [0u] = 0x1000002Bu,
            [1u] = 0x1000002Cu, // Bow Hunter
            [2u] = 0x10000031u, // Swashbuckler
            [3u] = 0x1000002Du,
            [4u] = 0x1000002Eu,
            [5u] = 0x1000002Fu, // Wayfarer
            [6u] = 0x10000030u, // Soldier
        };

    private static readonly IReadOnlyDictionary<uint, string> DescriptionKeyByTemplate =
        new Dictionary<uint, string>
        {
            [0u] = "ID_CharGen_CustomText",
            [1u] = "ID_CharGen_BowText",
            [2u] = "ID_CharGen_SwashText",
            [3u] = "ID_CharGen_LifeText",
            [4u] = "ID_CharGen_WarText",
            [5u] = "ID_CharGen_WayText",
            [6u] = "ID_CharGen_SoldierText",
        };

    private readonly CharacterCreationRuntimeBindings _bindings;
    private readonly Dictionary<UiButton, uint> _templateButtons = [];
    private readonly Dictionary<ChargenAttributeId, SliderWidgets> _sliders = [];
    private readonly UiButton? _availableValue;
    private readonly UiButton? _healthValue;
    private readonly UiButton? _staminaValue;
    private readonly UiButton? _manaValue;
    private readonly UiText? _description;
    private readonly UiElement? _backdrop;
    private bool _disposed;

    internal CharacterCreationProfessionPage(
        UiElement pageRoot,
        CharacterCreationRuntimeBindings bindings)
    {
        _bindings = bindings;

        foreach ((uint buttonId, uint templateIndex) in TemplateByButtonId)
        {
            if (UiElement.FindDescendant(pageRoot, buttonId) is not UiButton button)
                continue;
            _templateButtons[button] = templateIndex;
            button.OnClick = () => SelectTemplate(templateIndex);
        }

        foreach ((ChargenAttributeId attribute, uint containerId) in SliderContainerByAttribute)
        {
            if (UiElement.FindDescendant(pageRoot, containerId) is not { } container)
                continue;

            UiButton? lockButton = UiElement.FindDescendant(container, SliderLockRelativeId) as UiButton;
            UiScrollbar? slider = UiElement.FindDescendant(container, SliderControlRelativeId) as UiScrollbar;
            UiField? value = UiElement.FindDescendant(container, SliderValueRelativeId) as UiField;

            ChargenAttributeId capturedAttribute = attribute;
            if (lockButton is not null)
            {
                lockButton.OnClick = () => ToggleLock(capturedAttribute);
            }
            if (slider is not null)
            {
                slider.Horizontal = true;
                slider.ScalarChanged = scalar => SetAttributeFromScalar(capturedAttribute, scalar);
            }
            if (value is not null)
            {
                value.Editable = true;
                value.CharacterFilter = char.IsAsciiDigit;
                value.OnSubmit = text => SetAttributeFromText(capturedAttribute, text);
            }

            if (UiElement.FindDescendant(container, SliderNameRelativeId) is UiButton nameLabel)
                nameLabel.Label = AttributeName(attribute);

            _sliders[attribute] = new SliderWidgets(lockButton, slider, value);
        }

        _availableValue = UiElement.FindDescendant(pageRoot, 0x100003E2u) as UiButton;
        _healthValue = UiElement.FindDescendant(pageRoot, 0x100003E3u) as UiButton;
        _staminaValue = UiElement.FindDescendant(pageRoot, 0x100003E4u) as UiButton;
        _manaValue = UiElement.FindDescendant(pageRoot, 0x100003E5u) as UiButton;

        _description = UiElement.FindDescendant(pageRoot, DescriptionTextId) as UiText;
        _backdrop = UiElement.FindDescendant(pageRoot, 0x100003D8u);
    }

    internal void Refresh(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        _ = view;
        foreach ((UiButton button, uint templateIndex) in _templateButtons)
            button.Selected = templateIndex == snapshot.Template;

        foreach ((ChargenAttributeId attribute, SliderWidgets widgets) in _sliders)
        {
            int value = GetAttribute(snapshot.Attributes, attribute);
            float scalar = value / 100f;
            widgets.Slider?.SetScalarPosition(scalar);
            widgets.Value?.SetText(value.ToString(CultureInfo.InvariantCulture));
            if (widgets.Lock is { } lockButton)
                lockButton.Selected = snapshot.IsAttributeLocked(attribute);
        }

        SetDisplay(_availableValue, snapshot.RemainingAttributeCredits);
        int endurance = snapshot.Attributes.Endurance;
        SetDisplay(_healthValue, endurance / 2);
        SetDisplay(_staminaValue, endurance);
        SetDisplay(_manaValue, snapshot.Attributes.Self);

        // Root 1d: backdrop art per selected template.
        if (_backdrop is IUiDatStateful backdropStateful
            && BackdropStateByTemplate.TryGetValue(snapshot.Template, out uint backdropState))
        {
            backdropStateful.TrySetRetailState(backdropState);
        }

        // GF-3: description textbox — one plain segment (SetStringInfo,
        // not ...WithFont), so a single DefaultColor run.
        if (_description is not null
            && DescriptionKeyByTemplate.TryGetValue(snapshot.Template, out string? key))
        {
            string? text = _bindings.ResolveText?.Invoke(key);
            var segments = new[] { new DatRichText.Segment(text, _description.DefaultColor) };
            IReadOnlyList<UiText.Line> composed = DatRichText.Compose(_description, segments);
            _description.LinesProvider = () => composed;
        }
    }

    internal void Randomize(RuntimeCharacterCreationSnapshot snapshot)
    {
        IRuntimeCharacterCreationView? view = _bindings.View();
        if (view is null
            || !view.Options.TryGetHeritage(snapshot.HeritageId, out ChargenHeritageOptions? heritage)
            || heritage.Templates.Count == 0)
        {
            return;
        }
        SelectTemplate((uint)Random.Shared.Next(heritage.Templates.Count));
    }

    private static int GetAttribute(ChargenAttributeValues values, ChargenAttributeId id) => id switch
    {
        ChargenAttributeId.Strength => values.Strength,
        ChargenAttributeId.Endurance => values.Endurance,
        ChargenAttributeId.Quickness => values.Quickness,
        ChargenAttributeId.Coordination => values.Coordination,
        ChargenAttributeId.Focus => values.Focus,
        ChargenAttributeId.Self => values.Self,
        _ => 0,
    };

    private static void SetDisplay(UiButton? display, int value)
    {
        if (display is null)
            return;
        display.ValueLabel = value.ToString(CultureInfo.InvariantCulture);
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

    private void SelectTemplate(uint templateIndex)
    {
        if (_disposed)
            return;
        _bindings.SelectTemplate(templateIndex);
    }

    private void SetAttributeFromScalar(ChargenAttributeId attribute, float scalar)
    {
        if (_disposed)
            return;
        int value = Math.Max(ChargenAttributeMath.AttributeMin, (int)(scalar * 100f));
        _bindings.SetAttribute(attribute, value);
    }

    private void SetAttributeFromText(ChargenAttributeId attribute, string text)
    {
        if (_disposed)
            return;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            _bindings.SetAttribute(attribute, value);
    }

    private void ToggleLock(ChargenAttributeId attribute)
    {
        if (_disposed)
            return;
        RuntimeCharacterCreationSnapshot? snapshot = _bindings.View()?.Snapshot;
        bool currentlyLocked = snapshot?.IsAttributeLocked(attribute) ?? false;
        _bindings.SetAttributeLock(attribute, !currentlyLocked);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (UiButton button in _templateButtons.Keys)
            button.OnClick = null;
        _templateButtons.Clear();
        foreach (SliderWidgets widgets in _sliders.Values)
        {
            if (widgets.Lock is { } lockButton)
                lockButton.OnClick = null;
            if (widgets.Slider is { } slider)
                slider.ScalarChanged = null;
            if (widgets.Value is { } valueField)
                valueField.OnSubmit = null;
        }
        _sliders.Clear();
    }
}

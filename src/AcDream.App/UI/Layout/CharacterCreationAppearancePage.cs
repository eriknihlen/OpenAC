using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.CharGen;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.UI.Layout;

internal sealed class CharacterCreationAppearancePage : IDisposable
{
    private const uint Unset = RuntimeCharacterCreationAppearance.Unset;

    internal enum Part
    {
        Hair = 1,
        Eyes = 2,
        Nose = 3,
        Mouth = 4,
        Skin = 5,
        Headgear = 6,
        Shirt = 7,
        Trousers = 8,
        Footwear = 9,
    }

    private enum Choice
    {
        Face,
        Clothes,
    }

    internal const uint FemaleButtonId = 0x100003A7u;
    internal const uint MaleButtonId = 0x100003A8u;
    internal const uint FaceButtonId = 0x100003A9u;
    internal const uint ClothesButtonId = 0x100003AAu;
    internal const uint FaceChoicesId = 0x100003AEu;
    internal const uint ClothesChoicesId = 0x100003B4u;
    internal const uint HairSpinId = 0x100003AFu;
    internal const uint EyesSpinId = 0x100003B0u;
    internal const uint NoseSpinId = 0x100003B1u;
    internal const uint MouthSpinId = 0x100003B2u;
    internal const uint SkinSpinId = 0x100003B3u;
    internal const uint HeadgearSpinId = 0x100003B5u;
    internal const uint ShirtSpinId = 0x100003B6u;
    internal const uint TrousersSpinId = 0x100003B7u;
    internal const uint FootwearSpinId = 0x100003B8u;
    internal const uint RotateClockwiseId = 0x10000323u;
    internal const uint RotateCounterClockwiseId = 0x10000324u;
    internal const uint ZoomInId = 0x10000325u;
    internal const uint ZoomOutId = 0x10000326u;
    internal const uint GradCircleId = 0x1000030Eu;
    internal const uint ShadeScrollId = 0x10000321u;
    internal const uint ViewportId = 0x100003BBu;

    internal const uint HelpTextId = 0x100003ABu;

    private const uint HelpScrollRelativeId = 0x100002E7u;

    internal static readonly uint[] SwatchIds =
    [
        0x1000030Fu, 0x10000310u, 0x10000311u, 0x10000312u, 0x10000313u,
        0x10000314u, 0x10000315u, 0x10000316u, 0x10000317u,
    ];

    internal static readonly uint[] SwatchOverlayIds =
    [
        0x10000318u, 0x10000319u, 0x1000031Au, 0x1000031Bu, 0x1000031Cu,
        0x1000031Du, 0x1000031Eu, 0x1000031Fu, 0x10000320u,
    ];

    private const float DecrementZoneStart = 80f;
    private const float IncrementZoneStart = 127f;
    private const float IncrementZoneEnd = 174f;

    private readonly CharacterCreationRuntimeBindings _bindings;
    private readonly UiButton? _femaleButton;
    private readonly UiButton? _maleButton;
    private readonly UiButton? _faceButton;
    private readonly UiButton? _clothesButton;
    private readonly UiElement? _faceChoices;
    private readonly UiElement? _clothesChoices;
    private readonly Dictionary<Part, UiButton> _spins = [];
    private readonly UiButton?[] _swatches = new UiButton?[SwatchIds.Length];
    private readonly UiElement?[] _swatchOverlays = new UiElement?[SwatchOverlayIds.Length];
    private readonly UiScrollbar? _shadeScroll;
    private readonly UiButton? _rotateClockwise;
    private readonly UiButton? _rotateCounterClockwise;
    private readonly UiButton? _zoomIn;
    private readonly UiButton? _zoomOut;
    private readonly UiText? _helpText;

    private readonly UiDatElement? _gradCircle;

    private Choice _currentChoice = Choice.Face;
    private Part _currentPart = Part.Hair;
    private bool _eyesArrowsDisabled;
    private bool _disposed;

    internal IChargenPreviewControl? PreviewControl { get; set; }

    internal IChargenPalSetSource? PalSetSource { get; set; }
    internal IChargenClothingTableSource? ClothingTableSource { get; set; }
    internal IChargenPaletteColorSource? PaletteColorSource { get; set; }

    internal IChargenSwatchTextureSource? SwatchTextureSource { get; set; }

    internal UiViewport? Viewport { get; }

    internal CharacterCreationAppearancePage(
        UiElement pageRoot,
        CharacterCreationRuntimeBindings bindings)
    {
        _bindings = bindings;

        _femaleButton = Find<UiButton>(pageRoot, FemaleButtonId);
        if (_femaleButton is not null)
            _femaleButton.OnClick = () => _bindings.SelectGender(2u);
        _maleButton = Find<UiButton>(pageRoot, MaleButtonId);
        if (_maleButton is not null)
            _maleButton.OnClick = () => _bindings.SelectGender(1u);

        _faceButton = Find<UiButton>(pageRoot, FaceButtonId);
        if (_faceButton is not null)
            _faceButton.OnClick = () => SelectChoice(Choice.Face);
        _clothesButton = Find<UiButton>(pageRoot, ClothesButtonId);
        if (_clothesButton is not null)
            _clothesButton.OnClick = () => SelectChoice(Choice.Clothes);

        _faceChoices = Find<UiElement>(pageRoot, FaceChoicesId);
        _clothesChoices = Find<UiElement>(pageRoot, ClothesChoicesId);

        BindSpin(pageRoot, HairSpinId, Part.Hair);
        BindSpin(pageRoot, EyesSpinId, Part.Eyes);
        BindSpin(pageRoot, NoseSpinId, Part.Nose);
        BindSpin(pageRoot, MouthSpinId, Part.Mouth);
        BindSpin(pageRoot, SkinSpinId, Part.Skin);
        BindSpin(pageRoot, HeadgearSpinId, Part.Headgear);
        BindSpin(pageRoot, ShirtSpinId, Part.Shirt);
        BindSpin(pageRoot, TrousersSpinId, Part.Trousers);
        BindSpin(pageRoot, FootwearSpinId, Part.Footwear);

        for (int i = 0; i < SwatchIds.Length; i++)
        {
            UiButton? swatch = Find<UiButton>(pageRoot, SwatchIds[i]);
            if (swatch is null)
                continue;
            int index = i;
            swatch.OnClick = () => SelectColor(index);
            _swatches[i] = swatch;
        }

        for (int i = 0; i < SwatchOverlayIds.Length; i++)
            _swatchOverlays[i] = Find<UiElement>(pageRoot, SwatchOverlayIds[i]);

        _shadeScroll = Find<UiScrollbar>(pageRoot, ShadeScrollId);
        if (_shadeScroll is not null)
            _shadeScroll.ScalarChanged = SetShadeFromScalar;

        _gradCircle = Find<UiDatElement>(pageRoot, GradCircleId);

        Viewport = Find<UiViewport>(pageRoot, ViewportId);

        _rotateClockwise = Find<UiButton>(pageRoot, RotateClockwiseId);
        if (_rotateClockwise is not null)
            _rotateClockwise.OnClick = () => PreviewControl?.RotateClockwise();
        _rotateCounterClockwise = Find<UiButton>(pageRoot, RotateCounterClockwiseId);
        if (_rotateCounterClockwise is not null)
            _rotateCounterClockwise.OnClick = () => PreviewControl?.RotateCounterClockwise();
        _zoomIn = Find<UiButton>(pageRoot, ZoomInId);
        if (_zoomIn is not null)
            _zoomIn.OnClick = () =>
            {
                PreviewControl?.ZoomIn();
                _zoomIn.TrySetRetailState(UiButtonStateMachine.Highlight);
                _zoomOut?.TrySetRetailState(UiButtonStateMachine.Normal);
            };
        _zoomOut = Find<UiButton>(pageRoot, ZoomOutId);
        if (_zoomOut is not null)
            _zoomOut.OnClick = () =>
            {
                PreviewControl?.ZoomOut();
                _zoomOut.TrySetRetailState(UiButtonStateMachine.Highlight);
                _zoomIn?.TrySetRetailState(UiButtonStateMachine.Normal);
            };

        _helpText = Find<UiText>(pageRoot, HelpTextId);
        if (_helpText is not null)
        {
            _helpText.PreserveEndOnLayout = false;
            if (Find<UiScrollbar>(_helpText, HelpScrollRelativeId) is { } helpScroll)
                helpScroll.Model = _helpText.Scroll;
        }

        ApplyChoiceVisibility();
    }

    internal void Refresh(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        if (_disposed)
            return;

        if (_femaleButton is not null)
            _femaleButton.Selected = snapshot.GenderKey == 2u;
        if (_maleButton is not null)
            _maleButton.Selected = snapshot.GenderKey == 1u;

        bool clothesHidden = IsClothesHiddenHeritage(snapshot.HeritageId);
        if (_clothesButton is not null)
            _clothesButton.Visible = !clothesHidden;
        if (_spins.TryGetValue(Part.Nose, out UiButton? noseSpin))
            noseSpin.Visible = !clothesHidden;
        if (_spins.TryGetValue(Part.Mouth, out UiButton? mouthSpin))
            mouthSpin.Visible = !clothesHidden;
        _eyesArrowsDisabled = clothesHidden;
        if (clothesHidden)
        {
            _currentChoice = Choice.Face;
            _currentPart = Part.Hair;
        }
        ApplyChoiceVisibility();

        // GF-6: heritage-flavored, index-independent — no gender needed.
        RefreshSpinCaptions(snapshot.HeritageId);

        RefreshColorAndShadeControls(view, snapshot);
        RebuildPreview(view, snapshot);
    }

    internal void Randomize()
    {
        if (_disposed)
            return;
        if (_currentChoice == Choice.Clothes)
            _bindings.RandomizeClothing?.Invoke();
        else
            _bindings.RandomizeAppearance?.Invoke();
    }


    private void SelectChoice(Choice choice)
    {
        if (_disposed)
            return;
        _currentChoice = choice;
        _currentPart = choice == Choice.Face ? Part.Hair : Part.Headgear;
        ApplyChoiceVisibility();
        RefreshColorAndShadeControlsFromLatestSnapshot();
    }

    private void ApplyChoiceVisibility()
    {
        if (_faceChoices is not null)
            _faceChoices.Visible = _currentChoice == Choice.Face;
        if (_clothesChoices is not null)
            _clothesChoices.Visible = _currentChoice == Choice.Clothes;
        if (_faceButton is not null)
            _faceButton.Selected = _currentChoice == Choice.Face;
        if (_clothesButton is not null)
            _clothesButton.Selected = _currentChoice == Choice.Clothes;
    }


    private void BindSpin(UiElement pageRoot, uint id, Part part)
    {
        UiButton? spin = Find<UiButton>(pageRoot, id);
        if (spin is null)
            return;
        _spins[part] = spin;

        if (part == Part.Skin)
        {
            spin.OnClickAt = (_, _) => SelectPart(Part.Skin);
            return;
        }

        spin.OnClickAt = (x, _) =>
        {
            if (x >= DecrementZoneStart && x < IncrementZoneStart)
                CycleStyle(part, -1);
            else if (x >= IncrementZoneStart && x < IncrementZoneEnd)
                CycleStyle(part, +1);
            else
                SelectPart(part);
        };
    }

    private void SelectPart(Part part)
    {
        if (_disposed)
            return;
        NormalizeChoiceOnSelect(part);
        _currentPart = part;
        RefreshColorAndShadeControlsFromLatestSnapshot();
    }

    private void NormalizeChoiceOnSelect(Part part)
    {
        ChargenAppearanceSlot? slot = StyleSlotFor(part);
        if (slot is null)
            return;

        IRuntimeCharacterCreationView? view = _bindings.View();
        if (view is null)
            return;
        RuntimeCharacterCreationSnapshot snapshot = view.Snapshot;
        if (!TryGetGender(view, snapshot, out ChargenGenderOptions? gender))
            return;

        int count = StyleCount(part, gender);
        if (count <= 0)
            return;
        uint current = StyleCurrent(part, snapshot.Appearance);

        uint normalized;
        if (part == Part.Headgear)
        {
            if (current != Unset && current >= (uint)count)
                normalized = Unset;
            else
                return;
        }
        else
        {
            if (current != Unset && current >= (uint)count)
                normalized = 0u;
            else if (current == Unset)
                normalized = (uint)(count - 1);
            else
                return;
        }

        _bindings.SetAppearanceIndex?.Invoke(slot.Value, normalized);
    }

    private void CycleStyle(Part part, int delta)
    {
        if (_disposed)
            return;
        if (part == Part.Eyes && _eyesArrowsDisabled)
        {
            SelectPart(part);
            return;
        }

        IRuntimeCharacterCreationView? view = _bindings.View();
        if (view is null)
            return;
        RuntimeCharacterCreationSnapshot snapshot = view.Snapshot;
        if (!TryGetGender(view, snapshot, out ChargenGenderOptions? gender))
            return;

        ChargenAppearanceSlot? slot = StyleSlotFor(part);
        if (slot is null)
        {
            SelectPart(part);
            return;
        }

        int count = StyleCount(part, gender);
        uint current = StyleCurrent(part, snapshot.Appearance);
        uint next = CycleIndex(current, delta, count, allowUnset: part == Part.Headgear);
        _bindings.SetAppearanceIndex?.Invoke(slot.Value, next);
        SelectPart(part);
    }

    internal static uint CycleIndex(uint current, int delta, int count, bool allowUnset)
    {
        if (count <= 0)
            return Unset;

        if (allowUnset)
        {
            int cur = current == Unset ? count : (int)current;
            int size = count + 1;
            int next = Mod(cur + delta, size);
            return next == count ? Unset : (uint)next;
        }

        if (current == Unset)
        {
            int fromUnset = -1 + delta;
            if (fromUnset < 0)
                return (uint)(count - 1);
            if (fromUnset >= count)
                return 0u;
            return (uint)fromUnset;
        }
        return (uint)Mod((int)current + delta, count);
    }

    private static int Mod(int value, int modulus) =>
        ((value % modulus) + modulus) % modulus;


    private void SelectColor(int index)
    {
        if (_disposed)
            return;
        IRuntimeCharacterCreationView? view = _bindings.View();
        if (view is null)
            return;
        RuntimeCharacterCreationSnapshot snapshot = view.Snapshot;
        if (!TryGetGender(view, snapshot, out ChargenGenderOptions? gender))
            return;
        ChargenAppearanceSlot? slot = ColorSlotFor(_currentPart);
        if (slot is null)
            return;

        int count = ColorCount(_currentPart, gender);
        if (index >= count)
            return;

        _bindings.SetAppearanceIndex?.Invoke(slot.Value, (uint)index);
    }

    private void SetShadeFromScalar(float scalar)
    {
        if (_disposed)
            return;
        ChargenShadeSlot? slot = ShadeSlotFor(_currentPart);
        if (slot is null)
            return;
        _bindings.SetShade?.Invoke(slot.Value, scalar);
    }

    private void RefreshColorAndShadeControlsFromLatestSnapshot()
    {
        IRuntimeCharacterCreationView? view = _bindings.View();
        if (view is not null)
            RefreshColorAndShadeControls(view, view.Snapshot);
    }

    private void RefreshColorAndShadeControls(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot)
    {
        foreach ((Part spinPart, UiButton spin) in _spins)
        {
            spin.TrySetRetailState(
                spinPart == _currentPart
                    ? UiButtonStateMachine.Highlight
                    : UiButtonStateMachine.Normal);
        }

        ChargenAppearanceSlot? colorSlot = ColorSlotFor(_currentPart);
        uint currentColor = colorSlot is null ? Unset : ColorCurrent(_currentPart, snapshot.Appearance);
        for (int i = 0; i < _swatchOverlays.Length; i++)
        {
            if (_swatchOverlays[i] is { } overlay)
                overlay.Visible = colorSlot is not null && currentColor == (uint)i;
        }

        bool swatchGenderResolved = TryGetGender(view, snapshot, out ChargenGenderOptions? swatchGender);
        int colorCount = colorSlot is not null && swatchGenderResolved
            ? ColorCount(_currentPart, swatchGender!)
            : 0;
        int displayCount = colorSlot is not null ? colorCount : 1;

        ChargenSwatchRgb?[] swatchColors = swatchGenderResolved
            ? ComputeSwatchColors(swatchGender!, snapshot.Appearance)
            : new ChargenSwatchRgb?[SwatchIds.Length];

        for (int i = 0; i < _swatches.Length; i++)
        {
            if (_swatches[i] is not { } swatch)
                continue;
            bool visible = i < displayCount;
            swatch.Visible = true;
            ChargenSwatchRgb? rgb = visible ? swatchColors[i] : null;
            swatch.Tint = rgb is { } c ? ToTintColor(c) : Vector4.One;
            swatch.ColorKeyFaceResolver = BuildSwatchTextureResolver(visible, rgb);
        }

        if (_gradCircle is not null)
        {
            bool isEyes = _currentPart == Part.Eyes;
            _gradCircle.Visible = true;
            int gradIndex = isEyes
                ? -1
                : colorSlot is null
                    ? 0
                    : (int)ColorCurrent(_currentPart, snapshot.Appearance);
            ChargenSwatchRgb? gradColor =
                gradIndex >= 0 && gradIndex < swatchColors.Length ? swatchColors[gradIndex] : null;
            _gradCircle.Tint = gradColor is { } gc ? ToTintColor(gc) : Vector4.One;
            uint gradTexture = SwatchTextureSource is { } textures
                ? (isEyes ? textures.GradPlugTexture : textures.GradDiskTexture)
                : 0u;
            _gradCircle.RuntimeImageTexture = gradTexture;
        }

        ChargenShadeSlot? shadeSlot = ShadeSlotFor(_currentPart);
        if (_shadeScroll is null)
            return;
        _shadeScroll.Visible = shadeSlot is not null;
        if (shadeSlot is { } slot)
        {
            double shade = ShadeCurrent(slot, snapshot.Appearance);
            float scalar = shade < 0.0 ? 0f : (float)Math.Clamp(shade, 0.0, 1.0);
            _shadeScroll.SetScalarPosition(scalar);
        }
    }

    private Func<uint>? BuildSwatchTextureResolver(bool visible, ChargenSwatchRgb? rgb)
    {
        if (!visible)
            return () => SwatchTextureSource?.BlankSpotTexture ?? 0u;
        if (rgb is { } c)
            return () => SwatchTextureSource?.GetActiveSpotTexture(c) ?? 0u;
        return null;
    }


    private static readonly ChargenSwatchRgb?[] EmptySwatchColors = new ChargenSwatchRgb?[SwatchIds.Length];

    private ChargenSwatchRgb?[] ComputeSwatchColors(
        ChargenGenderOptions gender, RuntimeCharacterCreationAppearance appearance)
    {
        if (PalSetSource is not { } palSets || PaletteColorSource is not { } colors)
            return EmptySwatchColors;

        var result = new ChargenSwatchRgb?[SwatchIds.Length];
        switch (_currentPart)
        {
            case Part.Hair:
                FillPalSetFamily(result, gender.HairColors, palSets, colors, ChargenSwatchColorResolver.HairSampleIndex);
                break;
            case Part.Eyes:
                FillDirectFamily(result, gender.EyeColors, colors, ChargenSwatchColorResolver.EyeSampleIndex);
                break;
            case Part.Nose:
            case Part.Mouth:
            case Part.Skin:
                if (ChargenSwatchColorResolver.TryGetPalSetAverageColor(
                        palSets, colors, gender.SkinPalSetId,
                        ChargenSwatchColorResolver.SkinFamilySampleIndex, out ChargenSwatchRgb skin))
                {
                    result[0] = skin;
                }
                break;
            case Part.Headgear:
                FillClothingFamily(result, gender, gender.Headgears, appearance.HeadgearStyle, palSets, colors);
                break;
            case Part.Shirt:
                FillClothingFamily(result, gender, gender.Shirts, appearance.ShirtStyle, palSets, colors);
                break;
            case Part.Trousers:
                FillClothingFamily(result, gender, gender.Pants, appearance.TrousersStyle, palSets, colors);
                break;
            case Part.Footwear:
                FillClothingFamily(result, gender, gender.Footwear, appearance.FootwearStyle, palSets, colors);
                break;
        }
        return result;
    }

    private static void FillPalSetFamily(
        ChargenSwatchRgb?[] result,
        IReadOnlyList<uint> palSetIds,
        IChargenPalSetSource palSets,
        IChargenPaletteColorSource colors,
        int sampleIndex)
    {
        int count = Math.Min(result.Length, palSetIds.Count);
        for (int i = 0; i < count; i++)
        {
            if (ChargenSwatchColorResolver.TryGetPalSetAverageColor(
                    palSets, colors, palSetIds[i], sampleIndex, out ChargenSwatchRgb c))
            {
                result[i] = c;
            }
        }
    }

    private static void FillDirectFamily(
        ChargenSwatchRgb?[] result,
        IReadOnlyList<uint> paletteIds,
        IChargenPaletteColorSource colors,
        int sampleIndex)
    {
        int count = Math.Min(result.Length, paletteIds.Count);
        for (int i = 0; i < count; i++)
        {
            if (ChargenSwatchColorResolver.TryGetDirectColor(colors, paletteIds[i], sampleIndex, out ChargenSwatchRgb c))
                result[i] = c;
        }
    }

    private void FillClothingFamily(
        ChargenSwatchRgb?[] result,
        ChargenGenderOptions gender,
        IReadOnlyList<ChargenGearOption> gearOptions,
        uint styleIndex,
        IChargenPalSetSource palSets,
        IChargenPaletteColorSource colors)
    {
        if (ClothingTableSource is not { } clothingTables)
            return;
        if (styleIndex == Unset || styleIndex >= (uint)gearOptions.Count)
            return;

        uint clothingTableId = gearOptions[(int)styleIndex].ClothingTableId;
        IReadOnlyList<uint> clothingColors = gender.ClothingColors;
        int count = Math.Min(result.Length, clothingColors.Count);
        for (int i = 0; i < count; i++)
        {
            if (!ChargenSwatchColorResolver.TryGetClothingSwatchPalSetId(
                    clothingTables, clothingTableId, clothingColors[i], out uint palSetId))
            {
                continue;
            }
            if (ChargenSwatchColorResolver.TryGetPalSetAverageColor(
                    palSets, colors, palSetId, ChargenSwatchColorResolver.ClothingSampleIndex, out ChargenSwatchRgb c))
            {
                result[i] = c;
            }
        }
    }

    private static Vector4 ToTintColor(ChargenSwatchRgb rgb) =>
        new(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, 1f);


    private void RefreshSpinCaptions(uint heritageId)
    {
        (string hairKey, string eyesKey, string skinKey) = heritageId switch
        {
            (uint)ChargenHeritageGroup.Gearknight => (
                "ID_CharGen_GearText_HairButton",
                "ID_CharGen_GearText_EyesButton",
                "ID_CharGen_GearText_SkinButton"),
            (uint)ChargenHeritageGroup.Olthoi or (uint)ChargenHeritageGroup.OlthoiAcid => (
                "ID_CharGen_OlthoiText_HairButton",
                "ID_CharGen_OlthoiText_EyesButton",
                "ID_CharGen_OlthoiText_SkinButton"),
            _ => ("ID_CharGen_HairStyle", "ID_CharGen_Eyes", "ID_CharGen_Skin"),
        };

        SetSpinCaption(Part.Hair, hairKey);
        SetSpinCaption(Part.Eyes, eyesKey);
        SetSpinCaption(Part.Skin, skinKey);
    }

    private void SetSpinCaption(Part part, string key)
    {
        if (!_spins.TryGetValue(part, out UiButton? spin))
            return;
        if (_bindings.ResolveText?.Invoke(key) is { } text)
            spin.Label = text;
    }

    // ── Preview rebuild ──────────────────────────────────────────────

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


    private static ChargenAppearanceSlot? StyleSlotFor(Part part) => part switch
    {
        Part.Hair => ChargenAppearanceSlot.HairStyle,
        Part.Eyes => ChargenAppearanceSlot.EyesStrip,
        Part.Nose => ChargenAppearanceSlot.NoseStrip,
        Part.Mouth => ChargenAppearanceSlot.MouthStrip,
        Part.Headgear => ChargenAppearanceSlot.HeadgearStyle,
        Part.Shirt => ChargenAppearanceSlot.ShirtStyle,
        Part.Trousers => ChargenAppearanceSlot.TrousersStyle,
        Part.Footwear => ChargenAppearanceSlot.FootwearStyle,
        _ => null, // Skin.
    };

    private static ChargenAppearanceSlot? ColorSlotFor(Part part) => part switch
    {
        Part.Hair => ChargenAppearanceSlot.HairColor,
        Part.Eyes => ChargenAppearanceSlot.EyeColor,
        Part.Headgear => ChargenAppearanceSlot.HeadgearColor,
        Part.Shirt => ChargenAppearanceSlot.ShirtColor,
        Part.Trousers => ChargenAppearanceSlot.TrousersColor,
        Part.Footwear => ChargenAppearanceSlot.FootwearColor,
        _ => null,
    };

    private static ChargenShadeSlot? ShadeSlotFor(Part part) => part switch
    {
        Part.Hair => ChargenShadeSlot.Hair,
        Part.Nose => ChargenShadeSlot.Skin,
        Part.Mouth => ChargenShadeSlot.Skin,
        Part.Skin => ChargenShadeSlot.Skin,
        Part.Headgear => ChargenShadeSlot.Headgear,
        Part.Shirt => ChargenShadeSlot.Shirt,
        Part.Trousers => ChargenShadeSlot.Trousers,
        Part.Footwear => ChargenShadeSlot.Footwear,
        _ => null, // Eyes.
    };

    private static int StyleCount(Part part, ChargenGenderOptions gender) => part switch
    {
        Part.Hair => gender.HairStyles.Count,
        Part.Eyes => gender.EyeStrips.Count,
        Part.Nose => gender.NoseStrips.Count,
        Part.Mouth => gender.MouthStrips.Count,
        Part.Headgear => gender.Headgears.Count,
        Part.Shirt => gender.Shirts.Count,
        Part.Trousers => gender.Pants.Count,
        Part.Footwear => gender.Footwear.Count,
        _ => 0,
    };

    private static int ColorCount(Part part, ChargenGenderOptions gender) => part switch
    {
        Part.Hair => gender.HairColors.Count,
        Part.Eyes => gender.EyeColors.Count,
        Part.Headgear or Part.Shirt or Part.Trousers or Part.Footwear =>
            gender.ClothingColors.Count,
        _ => 0,
    };

    private static uint StyleCurrent(Part part, RuntimeCharacterCreationAppearance a) => part switch
    {
        Part.Hair => a.HairStyle,
        Part.Eyes => a.EyesStrip,
        Part.Nose => a.NoseStrip,
        Part.Mouth => a.MouthStrip,
        Part.Headgear => a.HeadgearStyle,
        Part.Shirt => a.ShirtStyle,
        Part.Trousers => a.TrousersStyle,
        Part.Footwear => a.FootwearStyle,
        _ => Unset,
    };

    private static uint ColorCurrent(Part part, RuntimeCharacterCreationAppearance a) => part switch
    {
        Part.Hair => a.HairColor,
        Part.Eyes => a.EyeColor,
        Part.Headgear => a.HeadgearColor,
        Part.Shirt => a.ShirtColor,
        Part.Trousers => a.TrousersColor,
        Part.Footwear => a.FootwearColor,
        _ => Unset,
    };

    private static double ShadeCurrent(ChargenShadeSlot slot, RuntimeCharacterCreationAppearance a) => slot switch
    {
        ChargenShadeSlot.Skin => a.SkinShade,
        ChargenShadeSlot.Hair => a.HairShade,
        ChargenShadeSlot.Headgear => a.HeadgearShade,
        ChargenShadeSlot.Shirt => a.ShirtShade,
        ChargenShadeSlot.Trousers => a.TrousersShade,
        ChargenShadeSlot.Footwear => a.FootwearShade,
        _ => 0.0,
    };

    private static bool IsClothesHiddenHeritage(uint heritageId) =>
        heritageId == (uint)ChargenHeritageGroup.Gearknight
        || heritageId == (uint)ChargenHeritageGroup.Olthoi
        || heritageId == (uint)ChargenHeritageGroup.OlthoiAcid;

    private static bool TryGetGender(
        IRuntimeCharacterCreationView view,
        RuntimeCharacterCreationSnapshot snapshot,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChargenGenderOptions? gender)
    {
        gender = null;
        if (snapshot.HeritageId == 0u || snapshot.GenderKey == 0u)
            return false;
        if (!view.Options.TryGetHeritage(snapshot.HeritageId, out ChargenHeritageOptions? heritage))
            return false;
        return heritage.GendersByKey.TryGetValue((int)snapshot.GenderKey, out gender);
    }

    private static T? Find<T>(UiElement root, uint id) where T : UiElement =>
        UiElement.FindDescendant(root, id) as T;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_femaleButton is not null) _femaleButton.OnClick = null;
        if (_maleButton is not null) _maleButton.OnClick = null;
        if (_faceButton is not null) _faceButton.OnClick = null;
        if (_clothesButton is not null) _clothesButton.OnClick = null;
        foreach (UiButton spin in _spins.Values)
            spin.OnClickAt = null;
        _spins.Clear();
        foreach (UiButton? swatch in _swatches)
        {
            if (swatch is not null)
                swatch.OnClick = null;
        }
        if (_shadeScroll is not null)
            _shadeScroll.ScalarChanged = null;
        if (_rotateClockwise is not null) _rotateClockwise.OnClick = null;
        if (_rotateCounterClockwise is not null) _rotateCounterClockwise.OnClick = null;
        if (_zoomIn is not null) _zoomIn.OnClick = null;
        if (_zoomOut is not null) _zoomOut.OnClick = null;
        PreviewControl = null;
        PalSetSource = null;
        ClothingTableSource = null;
        PaletteColorSource = null;
    }
}

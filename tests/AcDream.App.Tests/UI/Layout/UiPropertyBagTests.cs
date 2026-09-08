using System.Numerics;
using AcDream.App.UI.Layout;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.UI.Layout;

public sealed class UiPropertyBagTests
{
    [Fact]
    public void Merge_UsesKeyPresence_ForExplicitFalseAndZero()
    {
        var baseBag = new UiPropertyBag();
        baseBag.Values[0x16u] = Bool(true);
        baseBag.Values[0x14u] = Enum(3u);

        var derivedBag = new UiPropertyBag();
        derivedBag.Values[0x16u] = Bool(false);
        derivedBag.Values[0x14u] = Enum(0u);

        var merged = UiPropertyBag.Merge(baseBag, derivedBag);

        Assert.False(merged.Values[0x16u].BoolValue);
        Assert.Equal(0ul, merged.Values[0x14u].UnsignedValue);
    }

    [Fact]
    public void EffectiveProperty_NamedStateOverridesDirectStateByPresence()
    {
        var info = new ElementInfo { DefaultStateId = 1u };
        info.States[UiStateInfo.DirectStateId] = State(
            UiStateInfo.DirectStateId,
            "",
            (0x16u, Bool(true)));
        info.States[1u] = State(1u, "Normal", (0x16u, Bool(false)));

        Assert.True(info.TryGetEffectiveProperty(0x16u, out var property));
        Assert.False(property.BoolValue);
    }

    [Fact]
    public void ElementMerge_ExplicitCenteredStatePropertyOverridesBaseLeft()
    {
        var baseInfo = new ElementInfo();
        baseInfo.States[UiStateInfo.DirectStateId] = State(
            UiStateInfo.DirectStateId,
            "",
            (0x14u, Enum(0u)));

        var derivedInfo = new ElementInfo();
        derivedInfo.States[UiStateInfo.DirectStateId] = State(
            UiStateInfo.DirectStateId,
            "",
            (0x14u, Enum(1u)));

        var merged = ElementReader.Merge(baseInfo, derivedInfo);

        Assert.Equal(HJustify.Center, merged.HJustify);
        Assert.Equal(1ul, merged.States[UiStateInfo.DirectStateId]
            .Properties.Values[0x14u].UnsignedValue);
    }

    [Fact]
    public void ElementMerge_RetailTextDefaultTwo_IsLeftJustified()
    {
        var info = new ElementInfo();
        info.States[UiStateInfo.DirectStateId] = State(
            UiStateInfo.DirectStateId,
            "",
            (0x14u, Enum(2u)));

        var merged = ElementReader.Merge(new ElementInfo(), info);

        Assert.Equal(HJustify.Left, merged.HJustify);
    }


    [Fact]
    public void ElementMerge_Property0x21True_SetsOutline_WithNoAuthoredColor()
    {
        var info = new ElementInfo { DefaultStateId = 1u };
        info.States[1u] = State(1u, "0x10000002", (0x21u, Bool(true)));

        var merged = ElementReader.Merge(new ElementInfo(), info);

        Assert.True(merged.Outline);
        Assert.Null(merged.OutlineColor);
    }

    [Fact]
    public void ElementMerge_NoOutlineProperty_StaysFalse()
    {
        var info = new ElementInfo();
        info.States[UiStateInfo.DirectStateId] = State(
            UiStateInfo.DirectStateId,
            "",
            (0x1Bu, Color(204, 204, 204, 255)));

        var merged = ElementReader.Merge(new ElementInfo(), info);

        Assert.False(merged.Outline);
        Assert.Null(merged.OutlineColor);
    }

    [Fact]
    public void ElementMerge_Property0x22_SetsOutlineColorFromAuthoredArgb()
    {
        var info = new ElementInfo();
        info.States[UiStateInfo.DirectStateId] = State(
            UiStateInfo.DirectStateId,
            "",
            (0x21u, Bool(true)),
            (0x22u, Color(17, 15, 7, 255)));

        var merged = ElementReader.Merge(new ElementInfo(), info);

        Assert.True(merged.Outline);
        Assert.Equal(new Vector4(17f / 255f, 15f / 255f, 7f / 255f, 1f), merged.OutlineColor);
    }

    [Fact]
    public void ConvertProperty_PreservesNestedRetailValues()
    {
        var source = new StructBaseProperty { MasterPropertyId = 0xA0u };
        var array = new ArrayBaseProperty { MasterPropertyId = 0xA1u };
        array.Value.Add(new IntegerBaseProperty { MasterPropertyId = 0xA2u, Value = -17 });
        array.Value.Add(new Bitfield64BaseProperty
        {
            MasterPropertyId = 0xA3u,
            Value = 0xFEDCBA9876543210ul,
        });
        source.Value[0x55u] = array;
        source.Value[0x56u] = new VectorBaseProperty
        {
            MasterPropertyId = 0xA4u,
            Value = new Vector3(1.25f, -2.5f, 9.75f),
        };

        var converted = LayoutImporter.ConvertProperty(source);

        Assert.Equal(UiPropertyKind.Struct, converted.Kind);
        Assert.Equal(0xA0u, converted.MasterPropertyId);
        var convertedArray = converted.StructValue[0x55u];
        Assert.Equal(UiPropertyKind.Array, convertedArray.Kind);
        Assert.Equal(0xA1u, convertedArray.MasterPropertyId);
        Assert.Equal(-17, convertedArray.ArrayValue[0].IntegerValue);
        Assert.Equal(0xFEDCBA9876543210ul, convertedArray.ArrayValue[1].UnsignedValue);
        Assert.Equal(new Vector3(1.25f, -2.5f, 9.75f), converted.StructValue[0x56u].VectorValue);
    }

    [Fact]
    public void ConvertProperty_PreservesStringInfoAndColorBytes()
    {
        var text = new StringInfoBaseProperty
        {
            Value = new StringInfo
            {
                Token = 7,
                StringId = 0x12345678u,
                TableId = 0x23000001u,
                Override = StringInfoOverrideFlag.AutoGen,
                English = 2,
                Comment = 3,
            },
        };
        var color = new ColorBaseProperty
        {
            Value = new ColorARGB { Blue = 1, Green = 2, Red = 3, Alpha = 4 },
        };

        var convertedText = LayoutImporter.ConvertProperty(text);
        var convertedColor = LayoutImporter.ConvertProperty(color);

        Assert.Equal(
            new UiStringInfoValue(7, 0x12345678u, 0x23000001u, 2, 2, 3),
            convertedText.StringInfoValue);
        Assert.Equal(new UiColorValue(1, 2, 3, 4), convertedColor.ColorValue);
    }

    private static UiPropertyValue Bool(bool value)
        => new() { Kind = UiPropertyKind.Bool, BoolValue = value };

    private static UiPropertyValue Enum(uint value)
        => new() { Kind = UiPropertyKind.Enum, UnsignedValue = value };

    private static UiPropertyValue Color(byte red, byte green, byte blue, byte alpha)
        => new() { Kind = UiPropertyKind.Color, ColorValue = new UiColorValue(blue, green, red, alpha) };

    private static UiStateInfo State(
        uint id,
        string name,
        params (uint Id, UiPropertyValue Value)[] properties)
    {
        var state = new UiStateInfo { Id = id, Name = name };
        foreach (var property in properties)
            state.Properties.Values[property.Id] = property.Value;
        return state;
    }
}

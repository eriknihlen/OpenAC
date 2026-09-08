using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
namespace AcDream.App.Tests.UI.Layout;

public class ElementReaderTests
{

    [Fact]
    public void ToAnchors_TopEdge_StretchesWidth()
    {
        var a = ElementReader.ToAnchors(left: 1, top: 1, right: 1, bottom: 2);
        Assert.True(a.HasFlag(AnchorEdges.Left));
        Assert.True(a.HasFlag(AnchorEdges.Top));
        Assert.True(a.HasFlag(AnchorEdges.Right));
        Assert.False(a.HasFlag(AnchorEdges.Bottom));
    }

    [Fact]
    public void ToAnchors_TlCorner_PinsTopLeftFixed()
    {
        var a = ElementReader.ToAnchors(left: 1, top: 1, right: 2, bottom: 2);
        Assert.True(a.HasFlag(AnchorEdges.Left));
        Assert.True(a.HasFlag(AnchorEdges.Top));
        Assert.False(a.HasFlag(AnchorEdges.Right));
        Assert.False(a.HasFlag(AnchorEdges.Bottom));
    }

    [Fact]
    public void ToAnchors_TrCorner_TracksRight()
    {
        var a = ElementReader.ToAnchors(left: 2, top: 1, right: 1, bottom: 2);
        Assert.False(a.HasFlag(AnchorEdges.Left));
        Assert.True(a.HasFlag(AnchorEdges.Top));
        Assert.True(a.HasFlag(AnchorEdges.Right));
        Assert.False(a.HasFlag(AnchorEdges.Bottom));
    }

    [Fact]
    public void ToAnchors_LeftEdge_StretchesHeight()
    {
        var a = ElementReader.ToAnchors(left: 1, top: 1, right: 2, bottom: 1);
        Assert.True(a.HasFlag(AnchorEdges.Left));
        Assert.True(a.HasFlag(AnchorEdges.Top));
        Assert.False(a.HasFlag(AnchorEdges.Right));
        Assert.True(a.HasFlag(AnchorEdges.Bottom));
    }

    /// <summary>
    /// All-ones (L=1,T=1,R=1,B=1): all four flags fire — Left, Right, Top, Bottom.
    /// A piece pinned to all four sides stretches both horizontally and vertically.
    /// </summary>
    [Fact]
    public void ToAnchors_Meter_StretchesBoth()
    {
        var a = ElementReader.ToAnchors(left: 1, top: 1, right: 1, bottom: 1);
        Assert.True(a.HasFlag(AnchorEdges.Left));
        Assert.True(a.HasFlag(AnchorEdges.Top));
        Assert.True(a.HasFlag(AnchorEdges.Right));
        Assert.True(a.HasFlag(AnchorEdges.Bottom));
    }

    /// <summary>
    /// All-zero edge flags (prototype-only elements) fall back to Left|Top default.
    /// </summary>
    [Fact]
    public void EdgeFlagsToAnchors_AllZero_DefaultsToTopLeft()
    {
        var a = ElementReader.ToAnchors(left: 0, top: 0, right: 0, bottom: 0);
        Assert.Equal(AnchorEdges.Left | AnchorEdges.Top, a);
    }

    [Fact]
    public void EdgeFlagsToAnchors_ValueThree_HorizAxes_YieldsTopBottom()
    {
        var a = ElementReader.ToAnchors(left: 3, top: 1, right: 3, bottom: 1);
        Assert.False(a.HasFlag(AnchorEdges.Left));
        Assert.True(a.HasFlag(AnchorEdges.Top));
        Assert.False(a.HasFlag(AnchorEdges.Right));
        Assert.True(a.HasFlag(AnchorEdges.Bottom));
    }

    // ── Merge ────────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_BaseThenOverride_DerivedWins()
    {
        var base_ = new ElementInfo { Type = 0, FontDid = 0x40000000, Width = 150, Height = 16 };
        var derived = new ElementInfo { Type = 0, Width = 200 }; // overrides width, inherits font + height
        var merged = ElementReader.Merge(base_, derived);
        Assert.Equal(200, merged.Width);          // override
        Assert.Equal(16, merged.Height);          // inherited
        Assert.Equal(0x40000000u, merged.FontDid);// inherited
    }

    [Fact]
    public void Merge_DerivedHasFontDid_OverridesBase()
    {
        var base_ = new ElementInfo { FontDid = 0x40000000, Width = 100, Height = 10 };
        var derived = new ElementInfo { FontDid = 0x40000001, Width = 100 };
        var merged = ElementReader.Merge(base_, derived);
        Assert.Equal(0x40000001u, merged.FontDid);
    }

    [Fact]
    public void Merge_DerivedStateMediaOverridesBase()
    {
        var base_ = new ElementInfo();
        base_.StateMedia[""] = (0x06001000u, 1);
        base_.StateMedia["HideDetail"] = (0x06001001u, 1);

        var derived = new ElementInfo();
        derived.StateMedia[""] = (0x06002000u, 3); // overrides base default state

        var merged = ElementReader.Merge(base_, derived);
        // derived's "" overrides base's ""
        Assert.Equal((0x06002000u, 3), merged.StateMedia[""]);
        // base's "HideDetail" is kept (derived didn't provide it)
        Assert.Equal((0x06001001u, 1), merged.StateMedia["HideDetail"]);
    }

    [Fact]
    public void Merge_DerivedStateCursorOverridesBase()
    {
        var base_ = new ElementInfo();
        base_.StateCursors[""] = new UiCursorMedia(0x06003000u, 1, 2);
        base_.StateCursors["Drag_rollover_accept"] = new UiCursorMedia(0x06003001u, 3, 4);

        var derived = new ElementInfo();
        derived.StateCursors[""] = new UiCursorMedia(0x06004000u, 5, 6);

        var merged = ElementReader.Merge(base_, derived);

        Assert.Equal(new UiCursorMedia(0x06004000u, 5, 6), merged.StateCursors[""]);
        Assert.Equal(new UiCursorMedia(0x06003001u, 3, 4), merged.StateCursors["Drag_rollover_accept"]);
    }

    [Fact]
    public void Merge_ChildrenComeFromDerived()
    {
        var base_ = new ElementInfo();
        base_.Children.Add(new ElementInfo { Id = 0x1u });

        var derived = new ElementInfo();
        derived.Children.Add(new ElementInfo { Id = 0x2u });

        var merged = ElementReader.Merge(base_, derived);
        Assert.Single(merged.Children);
        Assert.Equal(0x2u, merged.Children[0].Id);
    }

    // ── HJustify / VJustify — Merge propagation ─────────────────────────────

    /// <summary>
    /// Authored justification is presence-based: raw 3 on the derived element
    /// overrides raw 1 on its base.
    /// </summary>
    [Fact]
    public void Merge_DerivedHJustifyRight_OverridesBaseCenter()
    {
        ElementInfo base_ = WithJustification(0x14u, 1u);
        ElementInfo derived = WithJustification(0x14u, 3u);
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Equal(HJustify.Right, merged.HJustify);
    }

    [Fact]
    public void Merge_DerivedAuthoredHJustifyCenter_OverridesBaseLeft()
    {
        ElementInfo base_ = WithJustification(0x14u, 2u);
        ElementInfo derived = WithJustification(0x14u, 1u);
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Equal(HJustify.Center, merged.HJustify);
    }

    /// <summary>
    /// VJustify=Top from the base propagates when the derived element authors
    /// no vertical justification.
    /// </summary>
    [Fact]
    public void Merge_BaseVJustifyTop_InheritedWhenDerivedIsUnauthored()
    {
        ElementInfo base_ = WithJustification(0x15u, 4u);
        var derived = new ElementInfo();
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Equal(VJustify.Top, merged.VJustify);
    }

    [Fact]
    public void Merge_DerivedVJustifyBottom_OverridesBaseCenter()
    {
        ElementInfo base_ = WithJustification(0x15u, 1u);
        ElementInfo derived = WithJustification(0x15u, 3u);
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Equal(VJustify.Bottom, merged.VJustify);
    }

    // ── FontColor — Merge propagation (Fix B) ────────────────────────────────

    /// <summary>
    /// When the derived element has an explicit (non-null) FontColor, the derived value
    /// wins in Merge — same "non-null derived wins" rule used for FontDid and HJustify.
    /// </summary>
    [Fact]
    public void Merge_DerivedFontColor_OverridesBaseNull()
    {
        var base_   = new ElementInfo { FontColor = null };
        var derived = new ElementInfo { FontColor = new Vector4(1f, 0f, 0f, 1f) };
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), merged.FontColor);
    }

    /// <summary>
    /// When the derived element has no FontColor (null), the base's FontColor is inherited.
    /// A null-derived must NOT override a non-null base.
    /// </summary>
    [Fact]
    public void Merge_DerivedFontColorNull_InheritsBaseColor()
    {
        var gold = new Vector4(1f, 0.82f, 0.36f, 1f);
        var base_   = new ElementInfo { FontColor = gold };
        var derived = new ElementInfo { FontColor = null };   // default — no dat property
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Equal(gold, merged.FontColor);
    }

    [Fact]
    public void Merge_BothFontColorNull_MergedIsNull()
    {
        var base_   = new ElementInfo { FontColor = null };
        var derived = new ElementInfo { FontColor = null };
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Null(merged.FontColor);
    }


    [Fact]
    public void Merge_DerivedOutlineTrue_OverridesBaseFalse()
    {
        var base_   = new ElementInfo { Outline = false };
        var derived = new ElementInfo { Outline = true };
        var merged  = ElementReader.Merge(base_, derived);
        Assert.True(merged.Outline);
    }

    [Fact]
    public void Merge_DerivedOutlineFalse_InheritsOutlinedBase()
    {
        var base_   = new ElementInfo { Outline = true };
        var derived = new ElementInfo { Outline = false };
        var merged  = ElementReader.Merge(base_, derived);
        Assert.True(merged.Outline);
    }

    [Fact]
    public void Merge_NeitherOutlines_MergedStaysFalse()
    {
        var base_   = new ElementInfo { Outline = false };
        var derived = new ElementInfo { Outline = false };
        var merged  = ElementReader.Merge(base_, derived);
        Assert.False(merged.Outline);
    }

    /// <summary>
    /// OutlineColor follows the exact same "non-null derived wins" rule as
    /// FontColor — property 0x22 is authored on only 9 elements in the whole DAT
    /// set, so most outlined elements inherit null here and fall back to the
    /// widget's own black default.
    /// </summary>
    [Fact]
    public void Merge_DerivedOutlineColor_OverridesBaseNull()
    {
        var base_   = new ElementInfo { OutlineColor = null };
        var derived = new ElementInfo { OutlineColor = new Vector4(0f, 0f, 0.4f, 1f) };
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Equal(new Vector4(0f, 0f, 0.4f, 1f), merged.OutlineColor);
    }

    [Fact]
    public void Merge_DerivedOutlineColorNull_InheritsBaseColor()
    {
        var authored = new Vector4(17f / 255f, 15f / 255f, 7f / 255f, 1f);
        var base_   = new ElementInfo { OutlineColor = authored };
        var derived = new ElementInfo { OutlineColor = null };
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Equal(authored, merged.OutlineColor);
    }

    [Fact]
    public void Merge_BothOutlineColorNull_MergedIsNull()
    {
        var base_   = new ElementInfo { OutlineColor = null };
        var derived = new ElementInfo { OutlineColor = null };
        var merged  = ElementReader.Merge(base_, derived);
        Assert.Null(merged.OutlineColor);
    }


    private static UiPropertyValue EnumProp(uint value) => new()
    {
        Kind = UiPropertyKind.Enum,
        UnsignedValue = value,
    };

    private static UiPropertyValue BoolProp(bool value) => new()
    {
        Kind = UiPropertyKind.Bool,
        BoolValue = value,
    };

    private static ElementInfo WithDirectProperty(uint propertyId, UiPropertyValue value)
    {
        var info = new ElementInfo();
        var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId, Name = "" };
        direct.Properties.Values[propertyId] = value;
        info.States[UiStateInfo.DirectStateId] = direct;
        return info;
    }

    private static ElementInfo WithJustification(uint propertyId, uint raw)
    {
        ElementInfo info = WithDirectProperty(propertyId, EnumProp(raw));
        ElementReader.ApplyCanonicalLegacyProjection(info);
        return info;
    }

    [Fact]
    public void Justification_UnauthoredDefaultsMatchRetailConstructors()
    {
        var info = new ElementInfo();

        Assert.Equal(HJustify.Left, info.HJustify);
        Assert.Equal(VJustify.Top, info.VJustify);
    }

    [Theory]
    [InlineData(0u, HJustify.Left)]
    [InlineData(1u, HJustify.Center)]
    [InlineData(2u, HJustify.Left)]
    [InlineData(3u, HJustify.Right)]
    [InlineData(4u, HJustify.Left)]
    [InlineData(5u, HJustify.Right)]
    public void HorizontalJustification_AllRetailRawValues(
        uint raw,
        HJustify expected)
    {
        ElementInfo info = WithJustification(0x14u, raw);

        Assert.Equal(expected, info.HJustify);
    }

    [Theory]
    [InlineData(0u, VJustify.Top)]
    [InlineData(1u, VJustify.Center)]
    [InlineData(2u, VJustify.Top)]
    [InlineData(3u, VJustify.Bottom)]
    [InlineData(4u, VJustify.Top)]
    [InlineData(5u, VJustify.Bottom)]
    public void VerticalJustification_AllRetailRawValues(
        uint raw,
        VJustify expected)
    {
        ElementInfo info = WithJustification(0x15u, raw);

        Assert.Equal(expected, info.VJustify);
    }

    [Fact]
    public void ReadTabTable_DecodesButtonPageDefaultInAuthoredOrder()
    {
        var entry0 = new UiPropertyValue { Kind = UiPropertyKind.Struct };
        entry0.StructValue[0x30u] = EnumProp(0x1000AAAAu);
        entry0.StructValue[0x31u] = EnumProp(0x1000BBBBu);
        entry0.StructValue[0x32u] = BoolProp(true);

        var entry1 = new UiPropertyValue { Kind = UiPropertyKind.Struct };
        entry1.StructValue[0x30u] = EnumProp(0x1000CCCCu);
        entry1.StructValue[0x31u] = EnumProp(0x1000DDDDu);
        // no 0x32 -> IsDefault false

        var array = new UiPropertyValue { Kind = UiPropertyKind.Array };
        array.ArrayValue.Add(entry0);
        array.ArrayValue.Add(entry1);

        ElementInfo info = WithDirectProperty(0x2Eu, array);
        ElementReader.ApplyCanonicalLegacyProjection(info);

        Assert.Equal(2, info.TabTable.Count);
        Assert.Equal(new UiTabTableEntry(0x1000AAAAu, 0x1000BBBBu, true), info.TabTable[0]);
        Assert.Equal(new UiTabTableEntry(0x1000CCCCu, 0x1000DDDDu, false), info.TabTable[1]);
    }

    [Fact]
    public void ReadTabTable_SkipsEntriesMissingButtonOrPage()
    {
        var missingPage = new UiPropertyValue { Kind = UiPropertyKind.Struct };
        missingPage.StructValue[0x30u] = EnumProp(0x1000EEEEu);
        // no 0x31

        var missingButton = new UiPropertyValue { Kind = UiPropertyKind.Struct };
        missingButton.StructValue[0x31u] = EnumProp(0x1000FFFFu);
        // no 0x30

        var wellFormed = new UiPropertyValue { Kind = UiPropertyKind.Struct };
        wellFormed.StructValue[0x30u] = EnumProp(0x10001111u);
        wellFormed.StructValue[0x31u] = EnumProp(0x10002222u);

        var array = new UiPropertyValue { Kind = UiPropertyKind.Array };
        array.ArrayValue.Add(missingPage);
        array.ArrayValue.Add(missingButton);
        array.ArrayValue.Add(wellFormed);

        ElementInfo info = WithDirectProperty(0x2Eu, array);
        ElementReader.ApplyCanonicalLegacyProjection(info);

        UiTabTableEntry only = Assert.Single(info.TabTable);
        Assert.Equal(new UiTabTableEntry(0x10001111u, 0x10002222u, false), only);
    }

    [Fact]
    public void ReadTemplateList_DecodesLayoutDidAndElementIdInAuthoredOrder()
    {
        var entry0 = new UiPropertyValue { Kind = UiPropertyKind.Struct };
        entry0.StructValue[0x63u] = EnumProp(0x21000099u);
        entry0.StructValue[0x62u] = EnumProp(0x10003333u);

        var entry1 = new UiPropertyValue { Kind = UiPropertyKind.Struct };
        entry1.StructValue[0x63u] = EnumProp(0x21000099u);
        entry1.StructValue[0x62u] = EnumProp(0x10004444u);

        var array = new UiPropertyValue { Kind = UiPropertyKind.Array };
        array.ArrayValue.Add(entry0);
        array.ArrayValue.Add(entry1);

        ElementInfo info = WithDirectProperty(0x64u, array);
        ElementReader.ApplyCanonicalLegacyProjection(info);

        Assert.Equal(2, info.TemplateList.Count);
        Assert.Equal(new UiTemplateListEntry(0x21000099u, 0x10003333u), info.TemplateList[0]);
        Assert.Equal(new UiTemplateListEntry(0x21000099u, 0x10004444u), info.TemplateList[1]);
    }

    [Fact]
    public void ReadReferencedElementId_ScrollbarLinkage_DecodesEnumProperty()
    {
        ElementInfo info = WithDirectProperty(0x72u, EnumProp(0x10005555u));
        ElementReader.ApplyCanonicalLegacyProjection(info);

        Assert.Equal(0x10005555u, info.ScrollbarElementId);
    }

    [Fact]
    public void ReadReferencedElementId_ScrollbarLinkage_AbsentPropertyStaysZero()
    {
        var info = new ElementInfo();
        ElementReader.ApplyCanonicalLegacyProjection(info);

        Assert.Equal(0u, info.ScrollbarElementId);
    }


    private static UiPropertyValue DataIdProp(uint value) => new()
    {
        Kind = UiPropertyKind.DataId,
        UnsignedValue = value,
    };

    private static UiPropertyValue FloatProp(float value) => new()
    {
        Kind = UiPropertyKind.Float,
        FloatValue = value,
    };

    private static UiPropertyValue StringInfoProp(uint tableId, uint stringId) => new()
    {
        Kind = UiPropertyKind.StringInfo,
        StringInfoValue = new UiStringInfoValue(0, stringId, tableId, 0, 0, 0),
    };

    [Fact]
    public void TooltipRootElementId_0x47_DecodesEnumProperty()
    {
        ElementInfo info = WithDirectProperty(0x47u, EnumProp(0x10000487u));
        ElementReader.ApplyCanonicalLegacyProjection(info);
        Assert.Equal(0x10000487u, info.TooltipRootElementId);
    }

    [Fact]
    public void TooltipLayoutDid_0x48_DecodesDataIdProperty()
    {
        ElementInfo info = WithDirectProperty(0x48u, DataIdProp(0x21000041u));
        ElementReader.ApplyCanonicalLegacyProjection(info);
        Assert.Equal(0x21000041u, info.TooltipLayoutDid);
    }

    [Fact]
    public void TooltipText_0x49_KeptRawAsStringInfo()
    {
        ElementInfo info = WithDirectProperty(0x49u, StringInfoProp(0x23000003u, 0x0AAAAAAAu));
        ElementReader.ApplyCanonicalLegacyProjection(info);
        Assert.Equal(0x23000003u, info.TooltipText!.Value.TableId);
        Assert.Equal(0x0AAAAAAAu, info.TooltipText!.Value.StringId);
    }

    [Fact]
    public void TooltipTextChildElementId_0x4A_DecodesEnumProperty()
    {
        ElementInfo info = WithDirectProperty(0x4Au, EnumProp(0x10000396u));
        ElementReader.ApplyCanonicalLegacyProjection(info);
        Assert.Equal(0x10000396u, info.TooltipTextChildElementId);
    }

    [Fact]
    public void TooltipEnabled_0x4B_DecodesBoolProperty()
    {
        ElementInfo info = WithDirectProperty(0x4Bu, BoolProp(true));
        ElementReader.ApplyCanonicalLegacyProjection(info);
        Assert.True(info.TooltipEnabled);
    }

    [Fact]
    public void TooltipEnabled_0x4B_AbsentDefaultsFalse()
    {
        var info = new ElementInfo();
        ElementReader.ApplyCanonicalLegacyProjection(info);
        Assert.False(info.TooltipEnabled);
    }

    [Fact]
    public void TooltipDelaySeconds_0x50_DecodesFloatProperty()
    {
        ElementInfo info = WithDirectProperty(0x50u, FloatProp(0f));
        ElementReader.ApplyCanonicalLegacyProjection(info);
        Assert.Equal(0f, info.TooltipDelaySeconds);
    }

    [Fact]
    public void TooltipDelaySeconds_0x50_AbsentStaysNull()
    {
        var info = new ElementInfo();
        ElementReader.ApplyCanonicalLegacyProjection(info);
        Assert.Null(info.TooltipDelaySeconds);
    }

    [Fact]
    public void AllSixTooltipProperties_AbsentTogether_LeaveEveryFieldAtItsDefault()
    {
        // Regression pin: an element authoring NONE of the tooltip properties
        // must never accidentally pick up a nonzero id/flag from unrelated
        // property parsing.
        var info = new ElementInfo();
        ElementReader.ApplyCanonicalLegacyProjection(info);

        Assert.False(info.TooltipEnabled);
        Assert.Null(info.TooltipText);
        Assert.Equal(0u, info.TooltipRootElementId);
        Assert.Equal(0u, info.TooltipLayoutDid);
        Assert.Equal(0u, info.TooltipTextChildElementId);
        Assert.Null(info.TooltipDelaySeconds);
    }

    [Fact]
    public void Merge_TooltipProperties_RecomputeFromTheMergedStateBag()
    {
        var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId, Name = "" };
        direct.Properties.Values[0x47u] = EnumProp(0x10000487u);
        direct.Properties.Values[0x48u] = DataIdProp(0x21000041u);
        direct.Properties.Values[0x4Bu] = BoolProp(true);
        var base_ = new ElementInfo();
        base_.States[UiStateInfo.DirectStateId] = direct;
        ElementReader.ApplyCanonicalLegacyProjection(base_);

        var derived = new ElementInfo(); // authors nothing of its own
        ElementInfo merged = ElementReader.Merge(base_, derived);

        Assert.Equal(0x10000487u, merged.TooltipRootElementId);
        Assert.Equal(0x21000041u, merged.TooltipLayoutDid);
        Assert.True(merged.TooltipEnabled);
    }
}

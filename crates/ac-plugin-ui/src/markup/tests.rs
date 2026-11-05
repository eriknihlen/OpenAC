//! Ported from OpenAC `tests/AcDream.App.Tests/UI/MarkupDocumentTests.cs`,
//! `MarkupColorBindingTests.cs`, `MarkupListColumnsTests.cs`,
//! `MarkupIconTests.cs` and `MarkupResizableAnchorTests.cs` (the parts
//! that are about reading the markup and resolving its bindings; drawing is
//! the window's, `ac-ui`). The C# binding objects become declared schemas
//! with the same member names and kinds.

use std::collections::BTreeMap;

use super::*;
use crate::value::{BindingKind as K, Rgba, UiValue};

fn schema(type_name: &str, members: &[(&str, K)]) -> BindingSchema {
    let mut s = BindingSchema::new(type_name);
    for (name, kind) in members {
        s.declare(*name, *kind);
    }
    s
}

fn values(pairs: &[(&str, UiValue)]) -> BTreeMap<String, UiValue> {
    pairs
        .iter()
        .map(|(n, v)| ((*n).to_owned(), v.clone()))
        .collect()
}

fn error(xml: &str, schema: &BindingSchema) -> String {
    parse(xml, schema)
        .expect_err("the markup must be refused")
        .0
}

/// MarkupDocumentTests `EditorBinding`.
fn editor() -> BindingSchema {
    schema(
        "EditorBinding",
        &[
            ("Draft", K::Text),
            ("Submitted", K::Text),
            ("Selected", K::Text),
            ("SelectedIndex", K::Int),
            ("Choices", K::TextList),
            ("ChoiceColors", K::UintList),
            ("ChangeDraft", K::TextAction),
            ("SubmitDraft", K::TextAction),
            ("SelectChoice", K::TextAction),
            ("SelectIndex", K::IntAction),
        ],
    )
}

/// MarkupDocumentTests `ButtonBinding`.
fn buttons() -> BindingSchema {
    schema(
        "ButtonBinding",
        &[
            ("Clicks", K::Int),
            ("Status", K::Text),
            ("Go", K::Action),
            ("Missing", K::Action),
            ("CanGo", K::Bool),
            ("OptionsSelected", K::Bool),
            ("OptionsVisible", K::Bool),
            ("CombatEnabled", K::Bool),
            ("AttackPower", K::Float),
            ("ShowOptions", K::Action),
            ("ToggleCombat", K::Action),
            ("SetAttackPower", K::FloatAction),
        ],
    )
}

#[test]
fn field_menu_and_list_bind_editable_plugin_state() {
    // MarkupDocumentTests.FieldAndMenuBindEditablePluginState
    let doc = parse(
        r#"<panel x="0" y="0" w="240" h="120">
              <field x="4" y="4" w="120" h="20" text="{Draft}"
                     onchange="{ChangeDraft}" onsubmit="{SubmitDraft}" />
              <menu x="4" y="32" w="120" h="20" items="{Choices}"
                    selected="{Selected}" onchange="{SelectChoice}" />
              <list x="132" y="4" w="100" h="40" items="{Choices}"
                    colors="{ChoiceColors}"
                    selected="{SelectedIndex}" onchange="{SelectIndex}" />
            </panel>"#,
        &editor(),
    )
    .unwrap();
    assert_eq!(doc.roots, [0, 1, 2]);
    match &doc.nodes[0].element {
        Element::Field {
            text,
            onchange,
            onsubmit,
            one_line,
            editable,
            max_length,
            ..
        } => {
            assert_eq!(*text, TextSource::Bound("Draft".into()));
            assert_eq!(onchange.as_deref(), Some("ChangeDraft"));
            assert_eq!(onsubmit.as_deref(), Some("SubmitDraft"));
            assert!(*one_line && *editable);
            assert_eq!(*max_length, 128);
        }
        other => panic!("{other:?}"),
    }
    match &doc.nodes[1].element {
        Element::Menu {
            items,
            onchange,
            open_upward,
            rows,
            row_height,
            ..
        } => {
            assert_eq!(items, "Choices");
            assert_eq!(onchange.as_deref(), Some("SelectChoice"));
            assert!(!open_upward);
            assert_eq!((*rows, *row_height), (7, 18.0));
        }
        other => panic!("{other:?}"),
    }
    match &doc.nodes[2].element {
        Element::List {
            selected,
            onchange,
            body,
            ..
        } => {
            assert_eq!(selected, "SelectedIndex");
            assert_eq!(onchange.as_deref(), Some("SelectIndex"));
            assert_eq!(
                *body,
                ListBody::Single {
                    items: "Choices".into(),
                    colors: Some("ChoiceColors".into()),
                    icons: None
                }
            );
        }
        other => panic!("{other:?}"),
    }
}

#[test]
fn field_arrow_keys_bind_actions() {
    // MarkupDocumentTests.MarkupFieldDispatchesArrowKeyActions
    let s = schema("ArrowKeyBinding", &[("Up", K::Action), ("Down", K::Action)]);
    let doc = parse(
        r#"<panel x="0" y="0" w="240" h="120">
              <field x="4" y="4" w="120" h="20" onup="{Up}" ondown="{Down}" />
            </panel>"#,
        &s,
    )
    .unwrap();
    let Element::Field { onup, ondown, .. } = &doc.nodes[0].element else {
        panic!()
    };
    assert_eq!(
        (onup.as_deref(), ondown.as_deref()),
        (Some("Up"), Some("Down"))
    );
    let e = error(r#"<panel><field onup="{Down2}"/></panel>"#, &s);
    assert_eq!(
        e,
        "<field onup=\"{Down2}\"> did not resolve to an Action property on ArrowKeyBinding"
    );
}

#[test]
fn control_id_and_name_become_stable_control_names() {
    // MarkupDocumentTests.ControlIdAndNameBecomeStablePluginControlNames
    let doc = parse(
        r#"<panel x="0" y="0" w="200" h="80">
              <button id="ById" x="4" y="4" w="80" h="20" text="One" />
              <label name="ByName" x="4" y="28" text="Two" />
            </panel>"#,
        &BindingSchema::new("Object"),
    )
    .unwrap();
    assert_eq!(doc.nodes[0].common.name.as_deref(), Some("ById"));
    assert_eq!(doc.nodes[1].common.name.as_deref(), Some("ByName"));
    assert_eq!(doc.find("ByName"), Some(1));
    assert_eq!(doc.find("Nobody"), None);
}

#[test]
fn a_meter_reads_fill_and_caption_and_geometry() {
    // MarkupDocumentTests.Build_CreatesPanelWithMeterFillLabelAndGeometry and
    // Build_NullBindingValuesYieldNullFillAndLabel
    let s = schema(
        "FakeBinding",
        &[
            ("HealthPercent", K::Float),
            ("HealthCurrent", K::Uint),
            ("HealthMax", K::Uint),
            ("ManaPercent", K::Float),
            ("ManaCurrent", K::Uint),
            ("ManaMax", K::Uint),
        ],
    );
    let doc = parse(
        "<panel id=\"acdream.vitals\" x=\"10\" y=\"30\" w=\"220\" h=\"96\" title=\"Vitals\">\
           <meter id=\"health\" x=\"8\" y=\"24\" w=\"200\" h=\"14\" fill=\"{HealthPercent}\" cur=\"{HealthCurrent}\" max=\"{HealthMax}\" color=\"#FFFF0000\"/>\
           <meter id=\"mana\" x=\"0\" y=\"0\" w=\"10\" h=\"2\" fill=\"{ManaPercent}\" cur=\"{ManaCurrent}\" max=\"{ManaMax}\" color=\"#FF0000FF\"/>\
         </panel>",
        &s,
    )
    .unwrap();
    assert_eq!(doc.panel.rect.x, 10.0);
    assert_eq!(doc.panel.rect.w, 220.0);
    assert_eq!(doc.panel.title.as_deref(), Some("Vitals"));
    let v = values(&[
        ("HealthPercent", UiValue::Float(0.5)),
        ("HealthCurrent", UiValue::Uint(109)),
        ("HealthMax", UiValue::Uint(218)),
    ]);
    let Element::Meter {
        fill,
        cur,
        max,
        color,
        ..
    } = &doc.nodes[0].element
    else {
        panic!()
    };
    assert_eq!(
        doc.nodes[0].rect,
        Rect {
            x: 8.0,
            y: 24.0,
            w: 200.0,
            h: 14.0
        }
    );
    assert_eq!(fill.read_f32(&v), Some(0.5));
    assert_eq!((cur.read_u32(&v), max.read_u32(&v)), (Some(109), Some(218)));
    assert_eq!(color.read(&v), Rgba::new(1.0, 0.0, 0.0, 1.0));
    // Unset values are null: no fill, no caption.
    let Element::Meter { fill, cur, .. } = &doc.nodes[1].element else {
        panic!()
    };
    assert_eq!(fill.read_f32(&v), None);
    assert_eq!(cur.read_u32(&v), None);
}

#[test]
fn resize_x_sets_horizontal_only_and_meter_sprites_parse() {
    // MarkupDocumentTests.Build_ResizeAttrX_SetsHorizontalOnly and
    // Build_ParsesNineSliceBarSpriteIds
    let doc = parse(
        "<panel id=\"v\" x=\"0\" y=\"0\" w=\"100\" h=\"50\" title=\"V\" resize=\"x\">\
           <meter id=\"h\" x=\"0\" y=\"0\" w=\"100\" h=\"14\" fill=\"{HealthPercent}\" \
             backleft=\"0x06001141\" backtile=\"0x06001140\" backright=\"0x0600113F\" \
             frontleft=\"0x06001131\" fronttile=\"0x06001132\" frontright=\"0x06001133\"/>\
         </panel>",
        &schema("FakeBinding", &[("HealthPercent", K::Float)]),
    )
    .unwrap();
    assert!(doc.panel.resize_x && !doc.panel.resize_y);
    let Element::Meter { sprites, .. } = &doc.nodes[0].element else {
        panic!()
    };
    assert_eq!(
        *sprites,
        [
            0x0600_1141,
            0x0600_1140,
            0x0600_113F,
            0x0600_1131,
            0x0600_1132,
            0x0600_1133
        ]
    );
}

#[test]
fn a_button_binds_its_click_and_an_unresolvable_handler_fails_loudly() {
    // MarkupDocumentTests.Build_ButtonInvokesTheBoundActionOnClick and
    // Build_ButtonWithUnresolvableHandlerFailsLoudly
    let doc = parse(
        "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">\
           <button x=\"4\" y=\"8\" w=\"60\" h=\"20\" text=\"Buff\" onclick=\"{Go}\"/></panel>",
        &buttons(),
    )
    .unwrap();
    let Element::Button { text, onclick, .. } = &doc.nodes[0].element else {
        panic!()
    };
    assert_eq!(*text, TextSource::Literal("Buff".into()));
    assert_eq!(onclick.as_deref(), Some("Go"));
    assert_eq!(doc.nodes[0].rect.w, 60.0);
    assert_eq!(
        error(
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">\
               <button x=\"0\" y=\"0\" w=\"10\" h=\"10\" text=\"X\" onclick=\"{NoSuchProperty}\"/></panel>",
            &buttons()
        ),
        "<button onclick=\"{NoSuchProperty}\"> did not resolve to an Action property on ButtonBinding"
    );
    // A literal handler name is not a binding either.
    assert!(parse("<panel><button onclick=\"Go\"/></panel>", &buttons()).is_err());
    // A handler of the wrong kind is refused.
    assert!(
        parse(
            "<panel><button onclick=\"{SetAttackPower}\"/></panel>",
            &buttons()
        )
        .is_err()
    );
}

#[test]
fn a_bound_label_tracks_its_value_and_a_literal_is_verbatim() {
    // MarkupDocumentTests.Build_BoundLabelTracksTheBindingRatherThanFreezing,
    // Build_LiteralLabelTextIsUsedVerbatim
    let doc = parse(
        "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">\
           <label x=\"2\" y=\"4\" text=\"{Status}\"/>\
           <label x=\"0\" y=\"0\" text=\"Sample Panel\"/>\
           <label x=\"0\" y=\"0\" text=\"{NotDeclared}\"/></panel>",
        &buttons(),
    )
    .unwrap();
    let text = |i: usize| match &doc.nodes[i].element {
        Element::Label { text, .. } => text.clone(),
        other => panic!("{other:?}"),
    };
    let mut v = values(&[("Status", UiValue::Text("idle".into()))]);
    assert_eq!(text(0).read(&v).as_deref(), Some("idle"));
    v.insert("Status".into(), UiValue::Text("casting".into()));
    assert_eq!(text(0).read(&v).as_deref(), Some("casting"));
    assert_eq!(text(1).read(&v).as_deref(), Some("Sample Panel"));
    // An unresolved text binding shows as written (OpenAC BindString).
    assert_eq!(text(2).read(&v).as_deref(), Some("{NotDeclared}"));
}

#[test]
fn bound_enabled_and_literal_button_colours() {
    // MarkupDocumentTests.Build_ProjectsBoundEnabledAndButtonColors
    let doc = parse(
        "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">\
           <button x=\"0\" y=\"0\" w=\"50\" h=\"20\" text=\"Go\" onclick=\"{Go}\" enabled=\"{CanGo}\" \
             background=\"#FF112233\" border=\"#FF445566\"/></panel>",
        &buttons(),
    )
    .unwrap();
    let node = &doc.nodes[0];
    let mut v = values(&[("CanGo", UiValue::Bool(true))]);
    assert!(node.common.enabled.read(&v, true));
    v.insert("CanGo".into(), UiValue::Bool(false));
    assert!(!node.common.enabled.read(&v, true));
    let Element::Button {
        background, border, ..
    } = &node.element
    else {
        panic!()
    };
    assert_eq!(
        background.as_ref().unwrap().read(&v),
        Rgba::new(
            0x11 as f32 / 255.0,
            0x22 as f32 / 255.0,
            0x33 as f32 / 255.0,
            1.0
        )
    );
    assert_eq!(
        border.as_ref().unwrap().read(&v),
        Rgba::new(
            0x44 as f32 / 255.0,
            0x55 as f32 / 255.0,
            0x66 as f32 / 255.0,
            1.0
        )
    );
}

#[test]
fn a_tooltip_binds_live() {
    // MarkupDocumentTests.Build_RuntimeTooltipUsesRetailPopupLocatorAndLiveBinding
    let doc = parse(
        "<panel><button x=\"0\" y=\"0\" w=\"50\" h=\"20\" text=\"Go\" tooltip=\"{Status}\"/></panel>",
        &buttons(),
    )
    .unwrap();
    let tooltip = doc.nodes[0].common.tooltip.clone().unwrap();
    let v = values(&[("Status", UiValue::Text("idle".into()))]);
    assert_eq!(tooltip.read(&v).as_deref(), Some("idle"));
    // A blank tooltip is none.
    let doc = parse(
        "<panel><label text=\"a\" tooltip=\"  \"/></panel>",
        &buttons(),
    )
    .unwrap();
    assert!(doc.nodes[0].common.tooltip.is_none());
}

#[test]
fn nested_group_tab_toggle_and_slider() {
    // MarkupDocumentTests.Build_NestedGroupTabToggleAndSliderStayLiveAndInteractive
    let doc = parse(
        "<panel x=\"0\" y=\"0\" w=\"300\" h=\"180\">\
           <tab x=\"4\" y=\"4\" w=\"60\" h=\"18\" text=\"Options\" selected=\"{OptionsSelected}\" onclick=\"{ShowOptions}\"/>\
           <group x=\"4\" y=\"28\" w=\"280\" h=\"140\" visible=\"{OptionsVisible}\">\
             <toggle x=\"2\" y=\"2\" w=\"140\" h=\"20\" text=\"Enable Combat\" checked=\"{CombatEnabled}\" onclick=\"{ToggleCombat}\"/>\
             <slider x=\"2\" y=\"30\" w=\"140\" h=\"16\" value=\"{AttackPower}\" onchange=\"{SetAttackPower}\"/>\
           </group></panel>",
        &buttons(),
    )
    .unwrap();
    assert_eq!(doc.roots, [0, 1]);
    assert_eq!(doc.nodes[1].children, [2, 3]);
    assert_eq!(doc.nodes[2].parent, Some(1));
    let v = values(&[
        ("OptionsSelected", UiValue::Bool(true)),
        ("OptionsVisible", UiValue::Bool(true)),
        ("CombatEnabled", UiValue::Bool(false)),
        ("AttackPower", UiValue::Float(0.78)),
    ]);
    let Element::Tab {
        text,
        selected,
        onclick,
        ..
    } = &doc.nodes[0].element
    else {
        panic!()
    };
    assert_eq!(
        (text.as_str(), selected.as_str()),
        ("Options", "OptionsSelected")
    );
    assert_eq!(onclick.as_deref(), Some("ShowOptions"));
    assert!(doc.nodes[1].common.visible.read(&v, true));
    let Element::Toggle { checked, .. } = &doc.nodes[2].element else {
        panic!()
    };
    assert_eq!(checked, "CombatEnabled");
    let Element::Slider {
        min,
        max,
        value,
        onchange,
        retail_art,
    } = &doc.nodes[3].element
    else {
        panic!()
    };
    assert_eq!((*min, *max), (0.0, 1.0));
    assert_eq!(value.read_f32(&v), Some(0.78));
    assert_eq!(onchange.as_deref(), Some("SetAttackPower"));
    assert!(!retail_art);
}

#[test]
fn slider_min_and_max() {
    // MarkupDocumentTests.Build_SliderWithMinMax_*, Build_SliderWithMaxLessThanOrEqualToMin_Throws
    let s = schema(
        "RangeBinding",
        &[("Value", K::Float), ("SetValue", K::FloatAction)],
    );
    let doc = parse(
        "<panel x=\"0\" y=\"0\" w=\"160\" h=\"20\"><slider x=\"0\" y=\"0\" w=\"150\" h=\"16\" min=\"0\" max=\"200\" value=\"{Value}\" onchange=\"{SetValue}\" style=\"retail\"/></panel>",
        &s,
    )
    .unwrap();
    let Element::Slider {
        min,
        max,
        retail_art,
        ..
    } = &doc.nodes[0].element
    else {
        panic!()
    };
    assert_eq!((*min, *max, *retail_art), (0.0, 200.0, true));
    for (min, max) in [("100", "0"), ("50", "50")] {
        assert_eq!(
            error(
                &format!(
                    "<panel><slider x=\"0\" y=\"0\" w=\"150\" h=\"16\" min=\"{min}\" max=\"{max}\" value=\"{{Value}}\" onchange=\"{{SetValue}}\"/></panel>"
                ),
                &s
            ),
            format!("<slider min=\"{min}\" max=\"{max}\"> must have max > min")
        );
    }
}

#[test]
fn menu_styles() {
    // MarkupDocumentTests.Menu_NoStyleAttribute_*, Menu_StylePlain_*,
    // Menu_StyleRetail_*, Menu_UnknownStyle_ThrowsFormatException_NamingTheElement
    let s = schema(
        "MenuStyleBinding",
        &[("Choices", K::TextList), ("Selected", K::Text)],
    );
    let menu = |style: &str| {
        parse(
            &format!(
                "<panel x=\"0\" y=\"0\" w=\"240\" h=\"60\"><menu x=\"4\" y=\"4\" w=\"120\" h=\"20\" items=\"{{Choices}}\" selected=\"{{Selected}}\"{style}/></panel>"
            ),
            &s,
        )
    };
    let art = |style: &str| match &menu(style).unwrap().nodes[0].element {
        Element::Menu { retail_art, .. } => *retail_art,
        other => panic!("{other:?}"),
    };
    assert!(!art(""));
    assert!(!art(" style=\"plain\""));
    assert!(art(" style=\"retail\""));
    let e = menu(" style=\"chrome\"").unwrap_err().0;
    assert!(e.contains("menu") && e.contains("chrome"), "{e}");
}

#[test]
fn every_literal_colour_attribute_parses_and_absent_ones_keep_defaults() {
    // MarkupColorBindingTests.EveryLiteralColorAttributeStillParsesToTheSameValue,
    // AnAbsentColorAttributeStillLeavesTheElementDefault
    let s = schema("ColorBinding", &[("On", K::Bool), ("Half", K::Float)]);
    let doc = parse(
        r##"<panel x="0" y="0" w="200" h="200">
              <group x="0" y="0" w="100" h="40" background="#FF112233" border="#FF445566"/>
              <label x="0" y="44" text="ABC" color="#FF7788AA"/>
              <button x="0" y="60" w="90" h="24" text="ABC" background="#FF102030" border="#FF405060" color="#FF708090"/>
              <toggle x="0" y="88" w="100" h="18" text="ABC" checked="{On}" color="#FFAABBCC"/>
              <meter x="0" y="110" w="100" h="12" fill="{Half}" color="#FF00FF00"/>
              <field x="0" y="126" w="120" h="20" text="ABC" background="#FF010203" color="#FF040506"/>
              <group x="0" y="0" w="100" h="40"/>
              <field x="0" y="44" w="120" h="20" text="ABC"/>
            </panel>"##,
        &s,
    )
    .unwrap();
    let v = values(&[]);
    let lit = |c: &Option<ColorSource>| c.as_ref().unwrap().read(&v);
    match &doc.nodes[0].element {
        Element::Group {
            background, border, ..
        } => {
            assert_eq!(lit(background), Rgba::argb(0xFF11_2233));
            assert_eq!(lit(border), Rgba::argb(0xFF44_5566));
        }
        other => panic!("{other:?}"),
    }
    match &doc.nodes[1].element {
        Element::Label { color, .. } => assert_eq!(lit(color), Rgba::argb(0xFF77_88AA)),
        other => panic!("{other:?}"),
    }
    match &doc.nodes[2].element {
        Element::Button {
            background,
            border,
            color,
            ..
        } => {
            assert_eq!(lit(background), Rgba::argb(0xFF10_2030));
            assert_eq!(lit(border), Rgba::argb(0xFF40_5060));
            assert_eq!(lit(color), Rgba::argb(0xFF70_8090));
        }
        other => panic!("{other:?}"),
    }
    match &doc.nodes[3].element {
        Element::Toggle { color, .. } => assert_eq!(lit(color), Rgba::argb(0xFFAA_BBCC)),
        other => panic!("{other:?}"),
    }
    match &doc.nodes[4].element {
        Element::Meter { color, .. } => assert_eq!(color.read(&v), Rgba::argb(0xFF00_FF00)),
        other => panic!("{other:?}"),
    }
    match &doc.nodes[5].element {
        Element::Field {
            background, color, ..
        } => {
            assert_eq!(background.read(&v), Rgba::argb(0xFF01_0203));
            assert_eq!(color.read(&v), Rgba::argb(0xFF04_0506));
        }
        other => panic!("{other:?}"),
    }
    match &doc.nodes[6].element {
        Element::Group {
            background, border, ..
        } => {
            assert!(background.is_none() && border.is_none());
        }
        other => panic!("{other:?}"),
    }
    match &doc.nodes[7].element {
        Element::Field {
            background,
            color,
            default_background,
            default_color,
            ..
        } => {
            assert_eq!(background.read(&v), Rgba::new(0.0, 0.0, 0.0, 0.9));
            assert_eq!(color.read(&v), Rgba::new(0.91, 0.87, 0.76, 1.0));
            assert!(*default_background && *default_color);
        }
        other => panic!("{other:?}"),
    }
}

#[test]
fn bound_colours_follow_their_values() {
    // MarkupColorBindingTests.GroupBackground_BoundToAPackedValue_*,
    // GroupBorder_BoundToASignedPackedValue_IsDrawn,
    // LabelColor_BoundToTheLiteralTextForm_*,
    // AnUnresolvedColorBindingIsSilentAndFallsBackToTheLiteralParser,
    // AColorBoundToSomethingThatIsNotAColorFallsBackInsteadOfThrowing
    let s = schema(
        "ColorBinding",
        &[
            ("Panel", K::Uint),
            ("Edge", K::Int),
            ("Accent", K::Text),
            ("NotAColor", K::Bool),
        ],
    );
    let doc = parse(
        r#"<panel x="0" y="0" w="120" h="60">
              <group x="0" y="0" w="100" h="40" background="{Panel}" border="{Edge}"/>
              <label x="0" y="0" text="ABC" color="{Accent}"/>
              <group x="0" y="0" w="100" h="40" background="{NoSuchSetting}"/>
              <group x="0" y="0" w="100" h="40" background="{NotAColor}"/>
            </panel>"#,
        &s,
    )
    .unwrap();
    let mut v = values(&[
        ("Panel", UiValue::Uint(0xFF11_2233)),
        ("Edge", UiValue::Int(0xFF44_5566_u32 as i32)),
        ("Accent", UiValue::Text("#FF7788AA".into())),
        ("NotAColor", UiValue::Bool(true)),
    ]);
    let Element::Group {
        background, border, ..
    } = &doc.nodes[0].element
    else {
        panic!()
    };
    let (background, border) = (background.clone().unwrap(), border.clone().unwrap());
    assert_eq!(background.read(&v), Rgba::argb(0xFF11_2233));
    assert_eq!(border.read(&v), Rgba::argb(0xFF44_5566));
    v.insert("Panel".into(), UiValue::Uint(0x8020_4060));
    assert_eq!(background.read(&v), Rgba::argb(0x8020_4060));
    let Element::Label { color, .. } = &doc.nodes[1].element else {
        panic!()
    };
    assert_eq!(color.clone().unwrap().read(&v), Rgba::argb(0xFF77_88AA));
    v.insert("Accent".into(), UiValue::Text("#FF010203".into()));
    assert_eq!(color.clone().unwrap().read(&v), Rgba::argb(0xFF01_0203));
    for unresolved in [2, 3] {
        let Element::Group { background, .. } = &doc.nodes[unresolved].element else {
            panic!()
        };
        assert_eq!(background.clone().unwrap().read(&v), Rgba::WHITE);
    }
}

#[test]
fn a_colour_may_name_a_second_colour_for_light_themes() {
    let doc = parse(
        r##"<panel theme="plugin"><label text="a" color="#FF112233|light:#FF445566"/></panel>"##,
        &BindingSchema::new("T"),
    )
    .unwrap();
    let Element::Label { color, .. } = &doc.nodes[0].element else {
        panic!()
    };
    let color = color.clone().unwrap();
    assert_eq!(
        color,
        ColorSource::Paired {
            dark: Rgba::argb(0xFF11_2233),
            light: Rgba::argb(0xFF44_5566),
        }
    );
    assert_eq!(color.read(&NoValues), Rgba::argb(0xFF11_2233));
}

/// A pair is two literals: a binding on either side is refused rather than
/// dropped.
#[test]
fn a_colour_pair_with_a_binding_is_refused() {
    let schema = schema("T", &[("Accent", K::Uint)]);
    for value in ["{Accent}|light:#FF445566", "#FF112233|light:{Accent}"] {
        let markup = format!(r#"<panel theme="plugin"><label text="a" color="{value}"/></panel>"#);
        assert_eq!(
            error(&markup, &schema),
            format!(
                "The colour '{value}' binds a value, so it cannot name a colour for light themes."
            )
        );
    }
}

#[test]
fn theme_colours_name_a_token_and_keep_a_classic_fallback() {
    // OpenAC MarkupDocument's theme branch (PluginUiPalette.Token).
    let doc = parse(
        "<panel theme=\"plugin\"><label text=\"a\" color=\"theme:muted|#FF102030\"/></panel>",
        &BindingSchema::new("T"),
    )
    .unwrap();
    assert!(doc.panel.themed);
    let Element::Label { color, .. } = &doc.nodes[0].element else {
        panic!()
    };
    assert_eq!(
        *color,
        Some(ColorSource::Theme {
            token: "muted".into(),
            classic: Rgba::argb(0xFF10_2030)
        })
    );
    assert_eq!(
        error(
            "<panel><label text=\"a\" color=\"theme:shiny|#FF102030\"/></panel>",
            &BindingSchema::new("T")
        ),
        "Unknown plugin theme color 'shiny'."
    );
}

/// MarkupListColumnsTests `ThreeColumnBinding`.
fn three_columns() -> BindingSchema {
    schema(
        "ThreeColumnBinding",
        &[
            ("Names", K::TextList),
            ("Fester", K::BoolList),
            ("Icons", K::UintList),
            ("Selected", K::Int),
            ("FesterToggled", K::Int),
            ("IconClickedRow", K::Int),
            ("ToggleFester", K::IntAction),
            ("ClickIcon", K::IntAction),
        ],
    )
}

fn list_xml(attributes: &str, columns: &str) -> String {
    format!(
        "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\"><list x=\"0\" y=\"0\" w=\"180\" h=\"60\" selected=\"{{Selected}}\"{attributes}>{columns}</list></panel>"
    )
}

#[test]
fn list_columns_bind_each_columns_own_source() {
    // MarkupListColumnsTests.Columns_TextCheckIcon_BindEachColumnsOwnPerRowSource
    let doc = parse(
        &list_xml(
            " rowheight=\"18\"",
            "<column type=\"text\" width=\"80\" items=\"{Names}\"/>\
             <column type=\"check\" width=\"20\" values=\"{Fester}\" onchange=\"{ToggleFester}\"/>\
             <column type=\"icon\" width=\"20\" iconkind=\"did\" values=\"{Icons}\" onclick=\"{ClickIcon}\"/>",
        ),
        &three_columns(),
    )
    .unwrap();
    let Element::List {
        body: ListBody::Columns(columns),
        ..
    } = &doc.nodes[0].element
    else {
        panic!()
    };
    assert_eq!(
        *columns,
        vec![
            Column {
                width: 80.0,
                auto: false,
                kind: ColumnKind::Text {
                    items: "Names".into(),
                    colors: None,
                    onclick: None
                },
                header: ColumnHeader::None,
                sort: SortKind::None,
                sort_keys: None,
            },
            Column {
                width: 20.0,
                auto: false,
                kind: ColumnKind::Check {
                    values: "Fester".into(),
                    onchange: "ToggleFester".into(),
                    boxed: false
                },
                header: ColumnHeader::None,
                sort: SortKind::None,
                sort_keys: None,
            },
            Column {
                width: 20.0,
                auto: false,
                kind: ColumnKind::Icon {
                    values: "Icons".into(),
                    kind: IconKind::Did,
                    onclick: "ClickIcon".into()
                },
                header: ColumnHeader::None,
                sort: SortKind::None,
                sort_keys: None,
            },
        ]
    );
}

#[test]
fn list_column_errors_name_the_column() {
    // MarkupListColumnsTests *_ThrowsAtBuild
    let s = three_columns();
    let cases: [(&str, &str, &[&str]); 13] = [
        (
            "",
            "<column type=\"bogus\" width=\"80\" items=\"{Names}\"/>",
            &["bogus", "column[0]"],
        ),
        (
            "",
            "<column type=\"text\" width=\"80\"/>",
            &["column[0] type=\"text\" items"],
        ),
        (
            "",
            "<column type=\"check\" width=\"20\" onchange=\"{ToggleFester}\"/>",
            &["column[0] type=\"check\" values"],
        ),
        (
            "",
            "<column type=\"check\" width=\"20\" values=\"{Fester}\"/>",
            &["column[0] type=\"check\" onchange"],
        ),
        (
            "",
            "<column type=\"icon\" width=\"20\" onclick=\"{ClickIcon}\"/>",
            &["column[0] type=\"icon\" values"],
        ),
        (
            "",
            "<column type=\"icon\" width=\"20\" values=\"{Icons}\"/>",
            &["column[0] type=\"icon\" onclick"],
        ),
        (
            "",
            "<column type=\"icon\" width=\"20\" iconkind=\"spel\" values=\"{Icons}\" onclick=\"{ClickIcon}\"/>",
            &["column[0] type=\"icon\" iconkind"],
        ),
        (
            " items=\"{Names}\"",
            "<column type=\"text\" width=\"80\" items=\"{Names}\"/>",
            &["items/icons/colors"],
        ),
        (
            " colors=\"{Icons}\"",
            "<column type=\"text\" width=\"80\" items=\"{Names}\"/>",
            &["items/icons/colors"],
        ),
        (
            " icons=\"{Icons}\"",
            "<column type=\"text\" width=\"80\" items=\"{Names}\"/>",
            &["items/icons/colors"],
        ),
        ("", "<label x=\"0\" y=\"0\" text=\"stray\"/>", &["label"]),
        (
            "",
            "<column type=\"text\" width=\"80\" items=\"{Names}\" colors=\"notabinding\"/>",
            &["column[0] type=\"text\" colors"],
        ),
        (
            "",
            "<column type=\"text\" width=\"80\" items=\"{Names}\" onclick=\"notabinding\"/>",
            &["column[0] type=\"text\" onclick"],
        ),
    ];
    for (attributes, columns, needles) in cases {
        let e = error(&list_xml(attributes, columns), &s);
        for needle in needles {
            assert!(e.contains(needle), "{needle:?} not in {e:?}");
        }
    }
    for (columns, needle) in [
        (
            "<column type=\"text\" items=\"{Names}\"/><column type=\"text\" width=\"40\" items=\"{Names}\"/>",
            "column[0] type=\"text\" width",
        ),
        (
            "<column type=\"text\" width=\"wide\" items=\"{Names}\"/><column type=\"text\" width=\"40\" items=\"{Names}\"/>",
            "column[0] type=\"text\" width",
        ),
        (
            "<column type=\"check\" width=\"0\" values=\"{Fester}\" onchange=\"{ToggleFester}\"/><column type=\"text\" width=\"40\" items=\"{Names}\"/>",
            "column[0] type=\"check\" width",
        ),
    ] {
        let e = error(&list_xml("", columns), &s);
        assert!(e.contains(needle), "{needle:?} not in {e:?}");
    }
    assert_eq!(
        error(
            &list_xml(
                "",
                "<column type=\"text\" items=\"{Names}\"/><column type=\"text\" width=\"40\" items=\"{Names}\"/>"
            ),
            &s
        ),
        "column[0] type=\"text\" width must be a positive number or \"*\", got (missing)"
    );
}

#[test]
fn star_and_last_column_widths() {
    // MarkupListColumnsTests.NonLastColumn_WidthStar_DoesNotThrow_ParsesAsAutoWidth,
    // LastColumn_MissingOrInvalidWidth_DoesNotThrow_StillAbsorbsRemainder,
    // TextColumn_OptionalColorsAttribute_*, TextColumn_OptionalOnclickAttribute_*
    let s = three_columns();
    let columns = |xml: &str| match parse(&list_xml("", xml), &s).unwrap().nodes[0]
        .element
        .clone()
    {
        Element::List {
            body: ListBody::Columns(c),
            ..
        } => c,
        other => panic!("{other:?}"),
    };
    let c = columns(
        "<column type=\"text\" width=\"*\" items=\"{Names}\" colors=\"{Icons}\" onclick=\"{ClickIcon}\"/><column type=\"text\" items=\"{Names}\"/>",
    );
    assert!(c[0].auto);
    assert!(!c[1].auto);
    assert_eq!(c[1].width, 0.0);
    assert_eq!(
        c[0].kind,
        ColumnKind::Text {
            items: "Names".into(),
            colors: Some("Icons".into()),
            onclick: Some("ClickIcon".into())
        }
    );
    // The single-column form keeps no columns.
    let doc = parse(&list_xml(" items=\"{Names}\"", ""), &s).unwrap();
    assert!(matches!(
        &doc.nodes[0].element,
        Element::List {
            body: ListBody::Single { .. },
            ..
        }
    ));
}

#[test]
fn selection_band_attribute() {
    // MarkupListColumnsTests.ListSelectionBandAttribute_ParsesToProperty
    let s = three_columns();
    for (attribute, expected) in [
        ("", false),
        (" selectionband=\"true\"", true),
        (" selectionband=\"false\"", false),
    ] {
        let doc = parse(
            &list_xml(&format!(" items=\"{{Names}}\"{attribute}"), ""),
            &s,
        )
        .unwrap();
        let Element::List { selection_band, .. } = &doc.nodes[0].element else {
            panic!()
        };
        assert_eq!(*selection_band, expected, "{attribute}");
    }
}

#[test]
fn list_requires_its_selected_int() {
    let s = three_columns();
    let e = error("<panel><list items=\"{Names}\"/></panel>", &s);
    assert_eq!(e, "list selected must be an int binding");
    let e = error(
        "<panel><list items=\"{Names}\" selected=\"{Names}\"/></panel>",
        &s,
    );
    assert_eq!(
        e,
        "{Names} did not resolve to an int property on ThreeColumnBinding"
    );
}

#[test]
fn icons_take_one_source_and_literals_or_bindings() {
    // MarkupIconTests (the build-time checks)
    let s = schema("IconBinding", &[("SpellId", K::Uint), ("ItemId", K::Uint)]);
    let doc = parse(
        "<panel>\
           <icon did=\"0x06001234\"/>\
           <icon spell=\"{SpellId}\" w=\"16\"/>\
           <icon item=\"{ItemId}\" tooltip=\"x\"/>\
           <icon did=\"4660\"/>\
           <button icon=\"0x1234\" iconkind=\"spell\" text=\"b\"/>\
         </panel>",
        &s,
    )
    .unwrap();
    let icon = |i: usize| match &doc.nodes[i].element {
        Element::Icon { icon } => icon.clone(),
        other => panic!("{other:?}"),
    };
    assert_eq!(
        icon(0),
        IconSource::Id {
            kind: IconKind::Did,
            id: UintSource::Literal(0x0600_1234)
        }
    );
    assert_eq!(icon(1).kind(), IconKind::Spell);
    assert_eq!(doc.nodes[1].rect.w, 16.0);
    assert_eq!(doc.nodes[1].rect.h, 32.0);
    assert!(matches!(icon(2), IconSource::Id { id: UintSource::Bound(n), .. } if n == "ItemId"));
    assert!(matches!(
        icon(3),
        IconSource::Id {
            id: UintSource::Literal(4660),
            ..
        }
    ));
    let Element::Button { icon, .. } = &doc.nodes[4].element else {
        panic!()
    };
    assert_eq!(
        *icon,
        Some(IconSource::Id {
            kind: IconKind::Spell,
            id: UintSource::Literal(0x1234)
        })
    );
    for (xml, message) in [
        (
            "<panel><icon/></panel>",
            "<icon> requires exactly one of did/spell/item/file",
        ),
        (
            "<panel><icon did=\"1\" spell=\"2\"/></panel>",
            "<icon> requires exactly one of did/spell/item/file",
        ),
        (
            "<panel><icon did=\"1\" iconkind=\"did\"/></panel>",
            "iconkind applies to button, tab and list; icon derives its kind from did/spell/item/file",
        ),
        (
            "<panel><icon did=\"0xZZ\"/></panel>",
            "icon did=\"0xZZ\" is not a valid uint literal",
        ),
        (
            "<panel><icon spell=\"{Nope}\"/></panel>",
            "{Nope} did not resolve to a property on IconBinding (icon spell)",
        ),
        (
            "<panel><button icon=\"1\" iconkind=\"thing\"/></panel>",
            "iconkind must be did, spell, item, or file (got \"thing\")",
        ),
        (
            "<panel><button icon=\"-1\"/></panel>",
            "button icon=\"-1\" is not a valid uint literal",
        ),
    ] {
        assert_eq!(error(xml, &s), message, "{xml}");
    }
    assert_eq!(normalize_icon(0), 0);
    assert_eq!(normalize_icon(0x1234), 0x0600_1234);
    assert_eq!(normalize_icon(0x0600_1234), 0x0600_1234);
}

#[test]
fn a_tab_takes_the_same_icon_attributes_as_a_button() {
    // MarkupIconTests (OpenAC v0.1.25, 82331793): TabIcon_DefaultsToDidKind,
    // TabIcon_BoundSpell, TabWithoutIconAttribute_HasNoIconSource and the
    // build-time refusals.
    let s = schema(
        "TabIconBinding",
        &[("Selected", K::Bool), ("SpellId", K::Uint)],
    );
    let doc = parse(
        "<panel>\
           <tab text=\"Spells\" selected=\"{Selected}\" icon=\"0x06001E37\"/>\
           <tab selected=\"{Selected}\" icon=\"{SpellId}\" iconkind=\"spell\"/>\
           <tab text=\"Spells\" selected=\"{Selected}\"/>\
         </panel>",
        &s,
    )
    .unwrap();
    let icon = |i: usize| match &doc.nodes[i].element {
        Element::Tab { icon, .. } => icon.clone(),
        other => panic!("{other:?}"),
    };
    assert_eq!(
        icon(0),
        Some(IconSource::Id {
            kind: IconKind::Did,
            id: UintSource::Literal(0x0600_1E37)
        })
    );
    assert_eq!(
        icon(1),
        Some(IconSource::Id {
            kind: IconKind::Spell,
            id: UintSource::Bound("SpellId".into())
        })
    );
    assert_eq!(icon(2), None);
    for (xml, message) in [
        (
            "<panel><tab selected=\"{Selected}\" icon=\"1\" iconkind=\"thing\"/></panel>",
            "iconkind must be did, spell, item, or file (got \"thing\")",
        ),
        (
            "<panel><tab selected=\"{Selected}\" icon=\"{Nope}\"/></panel>",
            "{Nope} did not resolve to a property on TabIconBinding (tab icon)",
        ),
    ] {
        assert_eq!(error(xml, &s), message, "{xml}");
    }
}

#[test]
fn anchors_read_comma_or_space_separated_edges() {
    // MarkupResizableAnchorTests (the anchor attribute's parsing)
    let s = BindingSchema::new("T");
    let anchors = |a: &str| {
        parse(
            &format!("<panel><label text=\"x\" anchor=\"{a}\"/></panel>"),
            &s,
        )
        .map(|d| d.nodes[0].common.anchors)
    };
    assert_eq!(
        anchors("right bottom"),
        Ok(Anchors::RIGHT.union(Anchors::BOTTOM))
    );
    assert_eq!(
        anchors("right,bottom"),
        Ok(Anchors::RIGHT.union(Anchors::BOTTOM))
    );
    assert_eq!(
        anchors("LEFT, Right"),
        Ok(Anchors::LEFT.union(Anchors::RIGHT))
    );
    assert_eq!(
        parse("<panel><label text=\"x\"/></panel>", &s)
            .unwrap()
            .nodes[0]
            .common
            .anchors,
        Anchors::TOP_LEFT
    );
    assert_eq!(
        anchors("up").unwrap_err().0,
        "<label> anchor=\"up\" has unknown token \"up\" (expected left, top, right, bottom)"
    );
    assert_eq!(
        parse(
            "<panel><label name=\"n\" text=\"x\" anchor=\",\"/></panel>",
            &s
        )
        .unwrap_err()
        .0,
        "<label name=\"n\"> anchor=\",\" names no edge (expected a comma- or space-separated subset of left, top, right, bottom)"
    );
}

#[test]
fn the_panel_root() {
    let s = schema("T", &[("Shown", K::Bool), ("Name", K::Text)]);
    assert_eq!(error("<window/>", &s), "root must be <panel>, got <window>");
    assert!(parse("", &s).is_err());
    let doc = parse(
        "<panel x=\"1\" y=\"2\" w=\"300\" h=\"200\" resizable=\"true\" minw=\"100\" visible=\"{Shown}\" title=\"T\"/>",
        &s,
    )
    .unwrap();
    assert!(doc.panel.resizable && doc.panel.resize_x && doc.panel.resize_y);
    assert_eq!((doc.panel.min_width, doc.panel.min_height), (100.0, 200.0));
    assert_eq!(doc.panel.visible.as_deref(), Some("Shown"));
    assert_eq!(
        error("<panel visible=\"{Name}\"/>", &s),
        "<panel visible=\"{Name}\"> did not resolve to a bool property on T"
    );
    assert_eq!(
        error("<panel><frame/></panel>", &s),
        "unknown element <frame>"
    );
    // A literal visibility on the root is not read.
    assert_eq!(
        parse("<panel visible=\"false\"/>", &s)
            .unwrap()
            .panel
            .visible,
        None
    );
}

#[test]
fn log_items_and_first_index() {
    let s = schema(
        "LogBinding",
        &[
            ("Lines", K::TextList),
            ("First", K::Int),
            ("Count", K::Uint),
        ],
    );
    let doc = parse(
        "<panel><log items=\"{Lines}\" firstindex=\"{First}\"/><log items=\"{Lines}\"/></panel>",
        &s,
    )
    .unwrap();
    assert_eq!(
        doc.nodes[0].element,
        Element::Log {
            items: "Lines".into(),
            first_index: Some("First".into())
        }
    );
    assert_eq!(
        doc.nodes[1].element,
        Element::Log {
            items: "Lines".into(),
            first_index: None
        }
    );
    assert_eq!(
        error(
            "<panel><log items=\"{Lines}\" firstindex=\"{Count}\"/></panel>",
            &s
        ),
        "{Count} did not resolve to an int property on LogBinding"
    );
    assert_eq!(
        error("<panel><log items=\"Lines\"/></panel>", &s),
        "log items must be a string-list binding"
    );
}

#[test]
fn visible_and_enabled_must_be_bool_bindings() {
    let s = schema("T", &[("Name", K::Text), ("On", K::Bool)]);
    assert_eq!(
        error("<panel><label text=\"x\" visible=\"{Name}\"/></panel>", &s),
        "{Name} did not resolve to a bool property on T"
    );
    let doc = parse(
        "<panel><label text=\"x\" visible=\"false\" enabled=\"maybe\"/></panel>",
        &s,
    )
    .unwrap();
    assert_eq!(doc.nodes[0].common.visible, BoolSource::Literal(false));
    assert_eq!(doc.nodes[0].common.enabled, BoolSource::Unset);
    assert!(!doc.nodes[0].common.visible.read(&NoValues, true));
}

// ── Button columns: OpenAC PR #223 `ListButtonColumnTests` ──

/// `ListButtonColumnTests.FoundBinding`.
fn found() -> BindingSchema {
    schema(
        "FoundBinding",
        &[
            ("Names", K::TextList),
            ("Captions", K::TextList),
            ("Selected", K::Int),
            ("Act", K::IntAction),
        ],
    )
}

/// `ListButtonColumnTests.FoundXml`: a name column and the button column.
fn found_xml(button_column: &str) -> String {
    list_xml(
        " rowheight=\"18\"",
        &format!("<column type=\"text\" width=\"100\" items=\"{{Names}}\"/>{button_column}"),
    )
}

fn second_column(doc: &MarkupDocument) -> ColumnKind {
    let Element::List {
        body: ListBody::Columns(columns),
        ..
    } = &doc.nodes[0].element
    else {
        panic!("a column list")
    };
    columns[1].kind.clone()
}

#[test]
fn a_button_column_binds_its_captions_onclick_and_size() {
    // ButtonColumn_BindsItsCaptionsAndOnclick, ButtonColumn_BindsItsButtonSize.
    let plain = parse(
        &found_xml("<column type=\"button\" width=\"60\" items=\"{Captions}\" onclick=\"{Act}\"/>"),
        &found(),
    )
    .unwrap();
    assert_eq!(
        second_column(&plain),
        ColumnKind::Button {
            items: "Captions".into(),
            onclick: "Act".into(),
            button_width: 0.0,
            button_height: 0.0,
        }
    );
    let sized = parse(
        &found_xml(
            "<column type=\"button\" width=\"60\" items=\"{Captions}\" onclick=\"{Act}\" buttonw=\"40\" buttonh=\"12\"/>",
        ),
        &found(),
    )
    .unwrap();
    assert_eq!(
        second_column(&sized),
        ColumnKind::Button {
            items: "Captions".into(),
            onclick: "Act".into(),
            button_width: 40.0,
            button_height: 12.0,
        }
    );
}

#[test]
fn a_button_column_names_what_is_wrong_with_it() {
    // ButtonColumn_MissingOnclick_ThrowsAtBuild, ButtonColumn_MissingItems_ThrowsAtBuild,
    // UnknownColumnType_NamesButtonAmongTheExpectedTypes,
    // ButtonColumn_NegativeButtonSize_ThrowsAtBuild.
    let s = found();
    for (column, needle) in [
        (
            "<column type=\"button\" width=\"60\" items=\"{Captions}\"/>",
            "column[1] type=\"button\" onclick",
        ),
        (
            "<column type=\"button\" width=\"60\" onclick=\"{Act}\"/>",
            "column[1] type=\"button\" items",
        ),
        (
            "<column type=\"bogus\" width=\"60\" items=\"{Captions}\"/>",
            "button",
        ),
        (
            "<column type=\"button\" width=\"60\" items=\"{Captions}\" onclick=\"{Act}\" buttonh=\"-1\"/>",
            "column[1] type=\"button\" buttonh",
        ),
    ] {
        let e = error(&found_xml(column), &s);
        assert!(e.contains(needle), "{needle:?} not in {e:?}");
    }
}

// ── File icons (OpenAC PR #271's MarkupFileIconTests, the build-time half) ──

fn file_schema() -> BindingSchema {
    schema(
        "FileBinding",
        &[
            ("IconPath", K::Text),
            ("NotAPath", K::Uint),
            ("Rows", K::TextList),
            ("RowIcons", K::TextList),
            ("RowIds", K::UintList),
            ("ClickIcon", K::IntAction),
            ("Selected", K::Int),
            ("TabOn", K::Bool),
        ],
    )
}

fn only(body: &str) -> Element {
    let doc = parse(&format!("<panel>{body}</panel>"), &file_schema()).unwrap();
    doc.nodes[0].element.clone()
}

// Icon_FileLiteral_ResolvesThePathThroughTheResolver (the path it names).
#[test]
fn icon_file_literal_names_its_path() {
    let Element::Icon { icon } = only("<icon x=\"0\" y=\"0\" file=\"icons/sword.png\"/>") else {
        panic!()
    };
    assert_eq!(
        icon,
        IconSource::File(PathSource::Literal("icons/sword.png".into()))
    );
    assert_eq!(icon.kind(), IconKind::File);
}

// Icon_FileBinding_ReadsTheStringPropertyEachDraw and
// Icon_FileBindingWithNoPath_DrawsNothingWithoutAsking.
#[test]
fn icon_file_binding_reads_the_string_property_each_draw() {
    let Element::Icon { icon } = only("<icon x=\"0\" y=\"0\" file=\"{IconPath}\"/>") else {
        panic!()
    };
    let IconSource::File(path) = icon else {
        panic!("{icon:?}")
    };
    assert_eq!(path, PathSource::Bound("IconPath".into()));
    let read = |v: UiValue| path.read(&values(&[("IconPath", v)]));
    assert_eq!(
        read(UiValue::Text("icons/bound.png".into())).as_deref(),
        Some("icons/bound.png")
    );
    assert_eq!(
        read(UiValue::Text("icons/other.jpg".into())).as_deref(),
        Some("icons/other.jpg")
    );
    for blank in ["", "   "] {
        assert_eq!(read(UiValue::Text(blank.into())), None);
    }
    assert_eq!(path.read(&values(&[])), None);
}

// Icon_FileWithAnotherSource_ThrowsNamingFile.
#[test]
fn icon_file_with_another_source_is_refused_naming_file() {
    let e = error(
        "<panel><icon x=\"0\" y=\"0\" did=\"7735\" file=\"icons/a.png\"/></panel>",
        &file_schema(),
    );
    assert!(e.contains("did/spell/item/file"), "{e}");
}

// Icon_FileBindingToAMissingOrNonStringProperty_ThrowsAtBuild.
#[test]
fn icon_file_binding_to_a_missing_or_non_string_property_is_refused() {
    for expression in ["{Typo}", "{NotAPath}"] {
        let e = error(
            &format!("<panel><icon x=\"0\" y=\"0\" file=\"{expression}\"/></panel>"),
            &file_schema(),
        );
        assert!(e.contains("string property"), "{expression}: {e}");
    }
}

// ButtonIcon_KindFile_ResolvesTheIconAsAPath.
#[test]
fn button_icon_kind_file_is_a_path() {
    let Element::Button { icon, .. } = only(
        "<button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" icon=\"{IconPath}\" iconkind=\"file\"/>",
    ) else {
        panic!()
    };
    assert_eq!(
        icon,
        Some(IconSource::File(PathSource::Bound("IconPath".into())))
    );
}

// TabIcon_KindFile_ResolvesTheIconAsAPath.
#[test]
fn tab_icon_kind_file_is_a_path() {
    let Element::Tab { icon, .. } = only(
        "<tab x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Notes\" selected=\"{TabOn}\" icon=\"icons/notes.png\" iconkind=\"file\"/>",
    ) else {
        panic!()
    };
    assert_eq!(
        icon,
        Some(IconSource::File(PathSource::Literal(
            "icons/notes.png".into()
        )))
    );
}

// List_KindFile_GivesEachDistinctPathOneIdAndResolvesItBack (the binding).
#[test]
fn list_kind_file_binds_a_string_list_of_paths() {
    let Element::List { body, .. } = only(
        "<list x=\"0\" y=\"0\" w=\"100\" h=\"60\" selected=\"{Selected}\" items=\"{Rows}\" icons=\"{RowIcons}\" iconkind=\"file\"/>",
    ) else {
        panic!()
    };
    let ListBody::Single { icons, .. } = body else {
        panic!()
    };
    assert_eq!(icons, Some(("RowIcons".into(), IconKind::File)));
}

// List_KindFile_NeedsAStringList.
#[test]
fn list_kind_file_needs_a_string_list() {
    let e = error(
        "<panel><list x=\"0\" y=\"0\" w=\"100\" h=\"60\" selected=\"{Selected}\" items=\"{Rows}\" icons=\"{RowIds}\" iconkind=\"file\"/></panel>",
        &file_schema(),
    );
    assert!(e.contains("IEnumerable<string>"), "{e}");
}

// Column_KindFile_ResolvesEachRowsPath (the binding).
#[test]
fn column_kind_file_binds_a_string_list_of_paths() {
    let Element::List { body, .. } = only(
        "<list x=\"0\" y=\"0\" w=\"100\" h=\"60\" selected=\"{Selected}\">\
           <column type=\"icon\" width=\"20\" iconkind=\"file\" values=\"{RowIcons}\" onclick=\"{ClickIcon}\"/>\
         </list>",
    ) else {
        panic!()
    };
    let ListBody::Columns(columns) = body else {
        panic!()
    };
    assert!(matches!(&columns[0].kind,
        ColumnKind::Icon { values, kind: IconKind::File, .. } if values == "RowIcons"));
    let e = error(
        "<panel><list x=\"0\" y=\"0\" w=\"100\" h=\"60\" selected=\"{Selected}\">\
           <column type=\"icon\" width=\"20\" iconkind=\"file\" values=\"{RowIds}\" onclick=\"{ClickIcon}\"/>\
         </list></panel>",
        &file_schema(),
    );
    assert!(e.contains("IEnumerable<string>"), "{e}");
}

// IconKind_Unknown_NamesFileAmongTheChoices.
#[test]
fn an_unknown_icon_kind_names_file_among_the_choices() {
    let e = error(
        "<panel><button x=\"0\" y=\"0\" w=\"60\" h=\"20\" text=\"Go\" icon=\"1\" iconkind=\"png\"/></panel>",
        &file_schema(),
    );
    assert!(e.contains("did, spell, item, or file"), "{e}");
}

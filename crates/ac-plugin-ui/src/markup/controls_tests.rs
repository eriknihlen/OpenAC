//! The newer markup controls as the markup reads them, ported from OpenAC
//! `tests/AcDream.App.Tests/UI/MarkupCheckboxTests.cs`, `MarkupNumberTests.cs`,
//! `MarkupRadioTests.cs`, `MarkupSeparatorTests.cs`, `MarkupExpanderTests.cs`,
//! `MarkupSplitTests.cs`, `MarkupContextMenuTests.cs`, `MarkupHotkeyTests.cs`,
//! `MarkupColorTests.cs`, `MarkupListHeaderTests.cs`,
//! `MarkupListMultiSelectTests.cs`, `MarkupListTreeTests.cs`,
//! `MarkupListBindingsTests.cs`, `MarkupListSelectBindingsTests.cs` and
//! `MarkupTreeBindingsTests.cs` (the build-time checks: what the markup
//! accepts and the messages it refuses with; drawing and input are the
//! window's). Each C# binding class becomes a schema with the same member
//! names and the Rust kinds of their types.

use super::*;
use crate::value::BindingKind as K;

fn schema() -> BindingSchema {
    let mut s = BindingSchema::new("Binding");
    for (name, kind) in [
        ("Loot", K::Bool),
        ("Maybe", K::OptionalBool),
        ("NotBool", K::Int),
        ("SetLoot", K::BoolAction),
        ("WrongAction", K::IntAction),
        ("Count", K::Int),
        ("SetCount", K::IntAction),
        ("Scale", K::Float),
        ("SetScale", K::FloatAction),
        ("NotNumber", K::Text),
        ("Mode", K::Int),
        ("NotInt", K::Text),
        ("SetMode", K::IntAction),
        ("Modes", K::TextList),
        ("Open", K::Bool),
        ("SetOpen", K::BoolAction),
        ("At", K::Float),
        ("NotFloat", K::Int),
        ("SetAt", K::FloatAction),
        ("NavKey", K::Text),
        ("NotString", K::Int),
        ("SetNavKey", K::TextAction),
        ("Bar", K::Uint),
        ("SetBar", K::UintAction),
        ("Signed", K::Int),
        ("SetSigned", K::IntAction),
        ("Text", K::Text),
        ("SetText", K::TextAction),
        ("Wrong", K::Float),
        ("Menu", K::TextList),
        ("MenuOn", K::BoolList),
        ("NotAList", K::Int),
        ("OnPick", K::IntAction),
        ("WrongType", K::TextAction),
        ("Names", K::TextList),
        ("Dists", K::TextList),
        ("DistKeys", K::FloatList),
        ("On", K::BoolList),
        ("Toggle", K::IntAction),
        ("Acts", K::TextList),
        ("Act", K::IntAction),
        ("Ids", K::TextList),
        ("Icons", K::UintList),
        ("DistTitle", K::Text),
        ("Selected", K::Int),
        ("Pick", K::IntAction),
        ("SortCol", K::Int),
        ("SortDesc", K::Bool),
        ("SetSort", K::IntBoolAction),
        ("Widths", K::FloatList),
        ("SetWidths", K::FloatListAction),
        ("SetAll", K::BoolAction),
        ("Selection", K::IntList),
        ("SetRows", K::IntListAction),
        ("Levels", K::IntList),
        ("Expanded", K::TextList),
        ("SetExpand", K::TextBoolAction),
        ("WrongExpand", K::IntBoolAction),
    ] {
        s.declare(name, kind);
    }
    s
}

fn panel(body: &str) -> String {
    format!(r#"<panel x="0" y="0" w="400" h="300">{body}</panel>"#)
}

fn build(body: &str) -> Result<MarkupDocument, MarkupError> {
    parse(&panel(body), &schema())
}

fn refused(body: &str) -> String {
    match build(body) {
        Ok(_) => panic!("must be refused: {body}"),
        Err(e) => e.0,
    }
}

fn only(body: &str) -> Element {
    build(body).unwrap().nodes.remove(0).element
}

#[test]
fn a_checkbox_binds_bool_or_optional_bool_and_its_new_value() {
    // MarkupCheckboxTests.CheckState_RejectsAnythingButABoolBinding,
    // BoolAction_RejectsLiteralsAndWrongTypes, Checkbox_BadBindingsThrowAtBuild
    assert_eq!(
        only(
            r#"<checkbox x="8" y="8" w="140" h="16" text="Loot" checked="{Loot}" onchange="{SetLoot}"/>"#
        ),
        Element::Checkbox {
            text: TextSource::Literal("Loot".into()),
            checked: "Loot".into(),
            onchange: Some("SetLoot".into()),
            color: None,
        }
    );
    assert!(build(r#"<checkbox checked="{Maybe}"/>"#).is_ok());
    for (body, context) in [
        (
            r#"<checkbox/>"#,
            "checkbox checked must be a bool or bool? binding",
        ),
        (
            r#"<checkbox checked="true"/>"#,
            "checkbox checked must be a bool or bool? binding",
        ),
        (
            r#"<checkbox checked="{NotBool}"/>"#,
            "{NotBool} did not resolve to a bool or bool? property on Binding (checkbox checked)",
        ),
        (
            r#"<checkbox checked="{Loot}" onchange="{WrongAction}"/>"#,
            "{WrongAction} did not resolve to an Action<bool> property on Binding (checkbox onchange)",
        ),
        (
            r#"<checkbox checked="{Loot}" onchange="SetLoot"/>"#,
            "checkbox onchange must be an Action<bool> binding",
        ),
    ] {
        assert_eq!(refused(body), context, "{body}");
    }
}

#[test]
fn a_checkbox_column_is_a_boxed_check_column() {
    // MarkupCheckboxTests.Column_CheckboxIsABoxedCheckColumn
    for (kind, boxed) in [("checkbox", true), ("check", false)] {
        let doc = build(&format!(
            r#"<list x="0" y="0" w="200" h="100" selected="{{Selected}}">
                 <column type="{kind}" width="20" values="{{On}}" onchange="{{Toggle}}"/>
                 <column type="text" items="{{Names}}"/></list>"#
        ))
        .unwrap();
        let Element::List {
            body: ListBody::Columns(columns),
            ..
        } = &doc.nodes[0].element
        else {
            panic!()
        };
        assert!(matches!(columns[0].kind, ColumnKind::Check { boxed: b, .. } if b == boxed));
    }
}

#[test]
fn a_number_checks_its_range_step_and_decimals() {
    // MarkupNumberTests.Number_BadAttributesThrowAtBuild,
    // NumberReader_RejectsNonNumbers, NumberAction_RequiresActionOfTheValueType
    let Element::Number { kind, spec, .. } = only(
        r#"<number x="8" y="8" w="80" h="20" value="{Count}" min="0" max="100" step="5" onchange="{SetCount}"/>"#,
    ) else {
        panic!()
    };
    assert_eq!(kind, NumberKind::Int);
    assert_eq!(spec.decimals, 0);
    assert_eq!(spec.format(crate::number::Dec::from_i64(10)), "10");
    let Element::Number { kind, spec, .. } =
        only(r#"<number value="{Scale}" onchange="{SetScale}"/>"#)
    else {
        panic!()
    };
    assert_eq!(kind, NumberKind::Float);
    assert_eq!(spec.decimals, 2);
    let count = r#"value="{Count}" onchange="{SetCount}""#;
    let scale = r#"value="{Scale}" onchange="{SetScale}""#;
    for attributes in [
        r#"onchange="{SetCount}""#.to_owned(),
        r#"value="{NotNumber}" onchange="{SetCount}""#.to_owned(),
        r#"value="{Count}""#.to_owned(),
        r#"value="{Count}" onchange="{SetScale}""#.to_owned(),
        format!(r#"{count} min="10" max="5""#),
        format!(r#"{count} step="0""#),
        format!(r#"{count} decimals="2""#),
        format!(r#"{scale} decimals="11""#),
        format!(r#"{count} max="3000000000""#),
        format!(r#"{count} step="0.5""#),
        format!(r#"{scale} decimals="1" step="0.25""#),
        format!(r#"{count} min="low""#),
        format!(r#"{scale} decimals="two""#),
    ] {
        assert!(
            build(&format!("<number {attributes}/>")).is_err(),
            "{attributes}"
        );
    }
    assert_eq!(
        refused(r#"<number value="{Count}" onchange="{SetScale}"/>"#),
        "{SetScale} did not resolve to an Action<Int32> property on Binding (number onchange)"
    );
    assert_eq!(
        refused(&format!(r#"<number {count} max="3000000000"/>"#)),
        r#"<number min="-2147483648" max="3000000000"> must fit a Int32 value"#
    );
}

#[test]
fn a_radio_takes_literal_or_bound_items() {
    // MarkupRadioTests.Radio_LiteralItemsAndBoundSelection, Radio_BadAttributesThrowAtBuild
    let Element::Radio {
        items,
        horizontal,
        segmented,
        ..
    } = only(
        r#"<radio x="8" y="8" w="200" h="54" items="Melee|Missile|Magic" selected="{Mode}" onchange="{SetMode}"/>"#,
    )
    else {
        panic!()
    };
    assert_eq!(
        items,
        ItemsSource::Literal(vec!["Melee".into(), "Missile".into(), "Magic".into()])
    );
    assert!(!horizontal && !segmented);
    let Element::Radio { items, .. } = only(
        r#"<radio items="{Modes}" selected="{Mode}" orientation="horizontal" style="segmented"/>"#,
    ) else {
        panic!()
    };
    assert_eq!(items, ItemsSource::Bound("Modes".into()));
    for attributes in [
        r#"selected="{Mode}""#,
        r#"items="" selected="{Mode}""#,
        r#"items="{NotInt}" selected="{Mode}""#,
        r#"items="a|b""#,
        r#"items="a||b" selected="{Mode}""#,
        r#"items="a|" selected="{Mode}""#,
        r#"items="|" selected="{Mode}""#,
        r#"items="a|b" selected="{NotInt}""#,
        r#"items="a|b" selected="{Mode}" onchange="{WrongType}""#,
        r#"items="a|b" selected="{Mode}" orientation="diagonal""#,
        r#"items="a|b" selected="{Mode}" style="tabs""#,
    ] {
        assert!(
            build(&format!("<radio {attributes}/>")).is_err(),
            "{attributes}"
        );
    }
    assert_eq!(
        refused(r#"<radio items="a||b" selected="{Mode}"/>"#),
        "radio items must not have an empty option"
    );
}

#[test]
fn a_separator_takes_a_colour() {
    // MarkupSeparatorTests (the markup it reads)
    assert_eq!(
        only(r#"<separator x="8" y="40" w="200" h="1"/>"#),
        Element::Separator { color: None }
    );
    assert!(matches!(
        only(r#"<separator x="8" y="40" w="1" h="120" color="theme:border|#FF61573B"/>"#),
        Element::Separator {
            color: Some(ColorSource::Theme { .. })
        }
    ));
}

#[test]
fn an_expander_holds_children_and_checks_its_attributes() {
    // MarkupExpanderTests.Expander_BadAttributesThrowAtBuild
    let doc = build(
        r#"<expander name="adv" x="8" y="60" w="240" h="120" text="Advanced">
             <toggle x="4" y="4" w="140" h="16" text="All" checked="{Loot}"/>
           </expander>
           <label x="8" y="190" text="Below"/>"#,
    )
    .unwrap();
    assert_eq!(doc.roots, [0, 2]);
    assert_eq!(doc.nodes[0].children, [1]);
    assert!(matches!(
        doc.nodes[0].element,
        Element::Expander {
            open: true,
            expanded: None,
            framed: true,
            ..
        }
    ));
    let plain = build(r#"<expander h="100" border="false"/>"#).unwrap();
    assert!(matches!(
        plain.nodes[0].element,
        Element::Expander { framed: false, .. }
    ));
    assert_eq!(
        refused(r#"<expander h="100" border="yes"/>"#),
        r#"<expander border="yes"> must be true or false"#
    );
    for body in [
        r#"<expander x="8" y="8" w="200" h="10" text="A"/>"#,
        r#"<expander h="100" expanded="{NotBool}"/>"#,
        r#"<expander h="100" onchange="{WrongAction}"/>"#,
        r#"<expander h="100" open="maybe"/>"#,
    ] {
        assert!(build(body).is_err(), "{body}");
    }
    assert_eq!(
        refused(r#"<expander x="8" y="8" w="200" h="10" text="A"/>"#),
        r#"<expander h="10"> must be at least 18 (its header)"#
    );
}

#[test]
fn a_split_holds_two_groups_and_checks_its_range() {
    // MarkupSplitTests.Split_BadMarkupThrowsAtBuild, Split_DefaultsToHalfAndLaysOutThePanes
    let doc = build(
        r#"<split name="main" x="0" y="0" w="400" h="300" min="80" max="320"><group/><group/></split>"#,
    )
    .unwrap();
    assert_eq!(doc.nodes[0].children, [1, 2]);
    let Element::Split {
        horizontal,
        default_position,
        min,
        max,
        ..
    } = doc.nodes[0].element
    else {
        panic!()
    };
    assert!(horizontal);
    assert_eq!(default_position, 197.0);
    assert_eq!((min, max), (80.0, Some(320.0)));
    for split in [
        r#"<split w="100" h="50"><group/></split>"#,
        r#"<split w="100" h="50"><group/><group/><group/></split>"#,
        r#"<split w="100" h="50"><group/><label text="x"/></split>"#,
        r#"<split w="100" h="50" orientation="diagonal"><group/><group/></split>"#,
        r#"<split w="100" h="50" min="-1"><group/><group/></split>"#,
        r#"<split w="100" h="50" max="x"><group/><group/></split>"#,
        r#"<split w="100" h="50" at="wide"><group/><group/></split>"#,
        r#"<split w="100" h="50" min="100" max="50"><group/><group/></split>"#,
        r#"<split w="100" h="50" position="{NotFloat}"><group/><group/></split>"#,
        r#"<split w="100" h="50" position="12"><group/><group/></split>"#,
        r#"<split w="100" h="50" onchange="{WrongAction}"><group/><group/></split>"#,
    ] {
        assert!(build(split).is_err(), "{split}");
    }
}

#[test]
fn a_hotkey_needs_a_string_and_its_action() {
    // MarkupHotkeyTests.Hotkey_BadBindingsFailTheBuild
    assert_eq!(
        only(r#"<hotkey x="8" y="8" w="140" h="20" value="{NavKey}" onchange="{SetNavKey}"/>"#),
        Element::Hotkey {
            value: "NavKey".into(),
            onchange: "SetNavKey".into()
        }
    );
    for (attributes, context) in [
        (r#"onchange="{SetNavKey}""#, "hotkey value"),
        (r#"value="Ctrl+N" onchange="{SetNavKey}""#, "hotkey value"),
        (
            r#"value="{NotString}" onchange="{SetNavKey}""#,
            "hotkey value",
        ),
        (r#"value="{NavKey}""#, "hotkey onchange"),
        (
            r#"value="{NavKey}" onchange="{WrongAction}""#,
            "hotkey onchange",
        ),
        (
            r#"value="{NavKey}" onchange="{Missing}""#,
            "hotkey onchange",
        ),
    ] {
        let message = refused(&format!("<hotkey {attributes}/>"));
        assert!(message.contains(context), "{attributes}: {message}");
    }
}

#[test]
fn a_colour_pairs_its_value_and_action_types() {
    // MarkupColorTests.Color_BadAttributesFailTheBuild, MarkupColorValueTests.BindRequiredColor_BadBindingsThrow
    assert_eq!(
        only(
            r#"<color x="8" y="8" w="40" h="18" value="{Bar}" onchange="{SetBar}" alpha="true"/>"#
        ),
        Element::Color {
            value: "Bar".into(),
            kind: ColorValueKind::Uint,
            onchange: "SetBar".into(),
            alpha: true
        }
    );
    assert!(matches!(
        only(r#"<color value="{Signed}" onchange="{SetSigned}"/>"#),
        Element::Color {
            kind: ColorValueKind::Int,
            alpha: false,
            ..
        }
    ));
    assert!(matches!(
        only(r#"<color value="{Text}" onchange="{SetText}"/>"#),
        Element::Color {
            kind: ColorValueKind::Text,
            ..
        }
    ));
    for (attributes, context) in [
        (r#"onchange="{SetBar}""#, "color value"),
        (r##"value="#FFFF0000" onchange="{SetBar}""##, "color value"),
        (r#"value="{Wrong}" onchange="{SetBar}""#, "color value"),
        (r#"value="{Bar}""#, "color onchange"),
        (r#"value="{Bar}" onchange="{SetText}""#, "color onchange"),
        (r#"value="{Text}" onchange="{SetBar}""#, "color onchange"),
        (r#"value="{Bar}" onchange="{SetBar}" alpha="yes""#, "alpha"),
    ] {
        let message = refused(&format!("<color {attributes}/>"));
        assert!(message.contains(context), "{attributes}: {message}");
    }
}

fn button(menu: &str) -> String {
    format!(r#"<button x="8" y="8" w="80" h="20" text="Profile">{menu}</button>"#)
}

#[test]
fn a_context_menu_goes_on_its_element() {
    // MarkupContextMenuTests.BadContextMenu_FailsTheBuild,
    // ContextMenuInThePanelOrAColumn_FailsTheBuild
    let doc = build(&button(
        r#"<contextmenu items="Copy|-|Paste" enabled="{MenuOn}" onselect="{OnPick}" onopen="{OnPick}"/>"#,
    ))
    .unwrap();
    assert_eq!(doc.nodes.len(), 1);
    assert_eq!(
        doc.nodes[0].common.context_menu,
        Some(ContextMenu {
            items: ItemsSource::Literal(vec!["Copy".into(), "-".into(), "Paste".into()]),
            enabled: Some("MenuOn".into()),
            onselect: "OnPick".into(),
            onopen: Some("OnPick".into()),
        })
    );
    let group = build(
        r#"<group x="0" y="0" w="100" h="100"><label text="a"/><contextmenu items="{Menu}" onselect="{OnPick}"/></group>"#,
    )
    .unwrap();
    assert_eq!(group.nodes.len(), 2);
    assert!(group.nodes[0].common.context_menu.is_some());
    for menu in [
        r#"<contextmenu items="{Menu}"/>"#,
        r#"<contextmenu items="{Menu}" onselect="{WrongType}"/>"#,
        r#"<contextmenu onselect="{OnPick}"/>"#,
        r#"<contextmenu items="a||b" onselect="{OnPick}"/>"#,
        r#"<contextmenu items="{NotAList}" onselect="{OnPick}"/>"#,
        r#"<contextmenu items="{Menu}" enabled="{NotAList}" onselect="{OnPick}"/>"#,
        r#"<contextmenu items="{Menu}" onopen="{WrongType}" onselect="{OnPick}"/>"#,
        r#"<contextmenu items="{Menu}" onselect="{OnPick}"/><contextmenu items="{Menu}" onselect="{OnPick}"/>"#,
        r#"<contextmenu items="{Menu}" onselect="{OnPick}"><label text="x"/></contextmenu>"#,
    ] {
        assert!(build(&button(menu)).is_err(), "{menu}");
    }
    assert!(build(r#"<contextmenu items="{Menu}" onselect="{OnPick}"/>"#).is_err());
    assert_eq!(
        refused(
            r#"<list x="0" y="0" w="200" h="100" selected="{Selected}"><column type="text" items="{Names}"><contextmenu items="{Menu}" onselect="{OnPick}"/></column></list>"#
        ),
        "<contextmenu> goes on the <list>, not on a <column>"
    );
}

/// MarkupListHeaderTests `ColumnsXml`.
const COLUMNS: &str = r#"
  <column type="text" header="Name" width="120" items="{Names}" sort="text"/>
  <column type="text" header="{DistTitle}" width="80" items="{Dists}" sort="number" sortkeys="{DistKeys}"/>
  <column type="checkbox" header="On" width="40" values="{On}" onchange="{Toggle}" sort="bool"/>
  <column type="button" items="{Acts}" onclick="{Act}"/>"#;

fn list(attributes: &str, columns: &str) -> Result<MarkupDocument, MarkupError> {
    build(&format!(
        r#"<list x="0" y="0" w="300" h="108" selected="{{Selected}}" onchange="{{Pick}}" {attributes}>{columns}</list>"#
    ))
}

#[test]
fn a_list_header_reads_sort_and_widths() {
    // MarkupListHeaderTests (the attributes the header reads)
    let doc = list(
        r#"header="true" resizable="true" sortcolumn="{SortCol}" sortdescending="{SortDesc}" onsort="{SetSort}" widths="{Widths}" onresize="{SetWidths}""#,
        COLUMNS,
    )
    .unwrap();
    let Element::List {
        header,
        body: ListBody::Columns(columns),
        ..
    } = &doc.nodes[0].element
    else {
        panic!()
    };
    assert_eq!(
        *header,
        ListHeader {
            show: true,
            sort: Some(("SortCol".into(), "SortDesc".into())),
            onsort: Some("SetSort".into()),
            resizable: true,
            widths: Some("Widths".into()),
            onresize: Some("SetWidths".into()),
        }
    );
    assert_eq!(columns[0].sort, SortKind::Text);
    assert_eq!(
        columns[1].sort_keys,
        Some(SortKeysSource::Numbers("DistKeys".into()))
    );
    assert_eq!(
        columns[1].header,
        ColumnHeader::Text(TextSource::Bound("DistTitle".into()))
    );
    assert_eq!(columns[2].sort, SortKind::Bool);
    assert_eq!(columns[3].header, ColumnHeader::None);
}

#[test]
fn bad_header_sort_and_resize_attributes_are_refused() {
    // MarkupListHeaderTests.Header_BadValueThrows, Header_AttributesOnASingleColumnListThrow,
    // Sort_BadColumnsThrow, Sort_BadListAttributesThrow, Resize_BadAttributesThrow,
    // SelectAll_BadColumnsThrow
    assert!(list(r#"header="maybe""#, COLUMNS).is_err());
    for attributes in [
        r#"header="true""#,
        r#"resizable="true""#,
        r#"sortcolumn="{SortCol}" sortdescending="{SortDesc}""#,
        r#"onsort="{SetSort}""#,
        r#"widths="{Widths}""#,
        r#"onresize="{SetWidths}""#,
    ] {
        let message = refused(&format!(
            r#"<list x="0" y="0" w="300" h="108" items="{{Names}}" selected="{{Selected}}" {attributes}/>"#
        ));
        assert!(message.contains("needs <column> children"), "{message}");
    }
    let tail = r#"<column type="button" items="{Acts}" onclick="{Act}"/>"#;
    for column in [
        r#"<column type="text" width="80" items="{Names}" sort="weird"/>"#,
        r#"<column type="text" width="80" items="{Names}" sort="bool"/>"#,
        r#"<column type="icon" width="20" values="{Icons}" onclick="{Act}" sort="text"/>"#,
        r#"<column type="checkbox" width="20" values="{On}" onchange="{Toggle}" sort="bool" sortkeys="{DistKeys}"/>"#,
        r#"<column type="text" width="80" items="{Names}" sortkeys="{DistKeys}"/>"#,
        r#"<column type="text" width="80" items="{Names}" sort="number" sortkeys="{On}"/>"#,
        r#"<column type="checkbox" width="40" values="{On}" onchange="{Toggle}" onall="{SetAll}"/>"#,
        r#"<column type="text" header="all" width="40" items="{Names}" onall="{SetAll}"/>"#,
        r#"<column type="checkbox" header="all" width="40" values="{On}" onchange="{Toggle}" onall="{SetAll}" sort="bool"/>"#,
        r#"<column type="checkbox" header="all" width="40" values="{On}" onchange="{Toggle}" onall="{Toggle}"/>"#,
    ] {
        assert!(
            list(r#"header="true""#, &format!("{column}{tail}")).is_err(),
            "{column}"
        );
    }
    for attributes in [
        r#"header="true" sortcolumn="{SortCol}""#,
        r#"header="true" sortdescending="{SortDesc}""#,
        r#"header="true" onsort="{Toggle}""#,
        r#"header="false" resizable="true""#,
        r#"header="true" widths="{Widths}""#,
        r#"header="true" onresize="{SetWidths}""#,
        r#"header="true" resizable="true" widths="{Names}""#,
        r#"header="true" resizable="true" onresize="{Toggle}""#,
    ] {
        assert!(list(attributes, COLUMNS).is_err(), "{attributes}");
    }
    let doc = list(
        r#"header="true""#,
        &format!(
            r#"<column type="checkbox" header="all" width="40" values="{{On}}" onchange="{{Toggle}}" onall="{{SetAll}}"/>{tail}"#
        ),
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
        columns[0].header,
        ColumnHeader::SelectAll(Some("SetAll".into()))
    );
}

fn column_list(attributes: &str) -> String {
    format!(
        r#"<list x="0" y="0" w="300" h="200" {attributes}><column type="text" width="100" items="{{Names}}"/><column type="text" items="{{Dists}}"/></list>"#
    )
}

#[test]
fn a_multi_select_list_binds_an_int_list() {
    // MarkupListMultiSelectTests.BuildErrors, SingleMode_IsUnchanged
    let doc = build(&column_list(
        r#"selectmode="multi" selected="{Selection}" onchange="{SetRows}""#,
    ))
    .unwrap();
    assert!(matches!(
        doc.nodes[0].element,
        Element::List {
            multi: true,
            selection_band: true,
            ..
        }
    ));
    for (attributes, context) in [
        (r#"selectmode="many" selected="{Selection}""#, "selectmode"),
        (r#"selectmode="multi""#, "list selected"),
        (
            r#"selectmode="multi" selected="{Selected}""#,
            "list selected",
        ),
        (
            r#"selectmode="multi" selected="{Selection}" onchange="{Pick}""#,
            "list onchange",
        ),
        (
            r#"selectmode="multi" selected="{Selection}" selectionband="false""#,
            "selectionband",
        ),
    ] {
        let message = refused(&column_list(attributes));
        assert!(message.contains(context), "{attributes}: {message}");
    }
    let message = refused(&column_list(
        r#"selected="{Selected}" onchange="{SetRows}""#,
    ));
    assert!(message.contains("did not resolve to an Action<int> property"));
}

#[test]
fn a_tree_list_names_its_levels_keys_and_expansion() {
    // MarkupListTreeTests.BuildErrors
    let tree = r#"tree="true" levels="{Levels}" keys="{Ids}" selected="{Selected}""#;
    let doc = build(&column_list(&format!(
        r#"{tree} expanded="{{Expanded}}" onexpand="{{SetExpand}}""#
    )))
    .unwrap();
    let Element::List { tree: Some(t), .. } = &doc.nodes[0].element else {
        panic!()
    };
    assert_eq!(t.column, 0);
    assert_eq!(t.expanded.as_deref(), Some("Expanded"));
    for (attributes, expected) in [
        (
            r#"items="{Names}" selected="{Selected}" tree="true" levels="{Levels}" keys="{Ids}""#,
            "<column>",
        ),
        (
            r#"tree="true" keys="{Ids}" selected="{Selected}""#,
            "list levels",
        ),
        (
            r#"tree="true" levels="{Names}" keys="{Ids}" selected="{Selected}""#,
            "list levels",
        ),
        (
            r#"tree="true" levels="{Levels}" selected="{Selected}""#,
            "list keys",
        ),
        (r#"levels="{Levels}" selected="{Selected}""#, "tree"),
    ] {
        let markup = if attributes.contains("items=") {
            format!(r#"<list x="0" y="0" w="300" h="200" {attributes}/>"#)
        } else {
            column_list(attributes)
        };
        let message = refused(&markup);
        assert!(message.contains(expected), "{attributes}: {message}");
    }
    for (extra, expected) in [
        (r#"expanded="{Levels}""#, "list expanded"),
        (r#"onexpand="{WrongExpand}""#, "list onexpand"),
        (r#"expanded="{Expanded}" collapsed="true""#, "collapsed"),
    ] {
        let message = refused(&column_list(&format!("{tree} {extra}")));
        assert!(message.contains(expected), "{extra}: {message}");
    }
    let message = refused(&format!(
        r#"<list x="0" y="0" w="300" h="200" {tree}><column type="checkbox" values="{{On}}" onchange="{{Toggle}}"/></list>"#
    ));
    assert!(message.contains("text column"), "{message}");
}

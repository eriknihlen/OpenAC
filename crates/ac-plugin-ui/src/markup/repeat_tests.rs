//! `<repeat>` as the markup reads it: where it may stand, what it takes,
//! the item type its template's names resolve on, and the key rules. Ported
//! from OpenAC `tests/AcDream.App.Tests/UI/MarkupRepeatBuildTests.cs` (the
//! build-time checks; the rows themselves are the window's) and
//! `tests/AcDream.App.Tests/Plugins/BufferedUiRegistryTests.cs`
//! (`NamedControlLookupsSkipRepeatRowsButFindTheControlsAfterThem`, as the
//! document's by-name lookup the session's calls use). Each C# binding class
//! becomes a schema with the same member names; a C# list of a row type is
//! an items binding with that row type's schema.

use super::*;
use crate::value::BindingKind as K;

fn row() -> BindingSchema {
    BindingSchema::new("Row")
        .with("Label", K::Text)
        .with("Id", K::Int)
        .with("On", K::Bool)
        .with("Click", K::Action)
        .with("SetOn", K::BoolAction)
        .with("Pick", K::IntAction)
        .with("Tags", K::TextList)
}

fn section() -> BindingSchema {
    BindingSchema::new("Section")
        .with("Title", K::Text)
        .with("Id", K::Int)
        .with_items("Rows", row())
}

fn schema() -> BindingSchema {
    BindingSchema::new("Binding")
        .with_items("Rows", row())
        .with_items("Sections", section())
        .with("Count", K::Int)
        .with("Text", K::Text)
        .with("Names", K::TextList)
}

fn window(body: &str) -> Result<MarkupDocument, MarkupError> {
    window_with(body, "layout=\"column\"")
}

fn window_with(body: &str, attrs: &str) -> Result<MarkupDocument, MarkupError> {
    parse(
        &format!("<panel x=\"0\" y=\"0\" w=\"300\" {attrs}>{body}</panel>"),
        &schema(),
    )
}

fn message(result: Result<MarkupDocument, MarkupError>) -> String {
    result.expect_err("the markup is refused").0
}

const LABEL_TEMPLATE: &str = "<repeat items=\"{Rows}\"><label text=\"{Label}\"/></repeat>";

/// OpenAC `A_copy_reads_its_item_and_calls_back_on_its_item` (its build
/// half): the template's names resolve on the item type, the repeat holds
/// the list's name and its template.
#[test]
fn a_template_resolves_its_names_on_the_item_type() {
    let doc = window(
        "<repeat items=\"{Rows}\"><button text=\"{Label}\" onclick=\"{Click}\"/>\
         <checkbox checked=\"{On}\" onchange=\"{SetOn}\"/></repeat>",
    )
    .unwrap();
    let repeat = doc.roots[0];
    assert_eq!(
        doc.nodes[repeat].element,
        Element::Repeat {
            items: "Rows".to_owned(),
            key: None,
        }
    );
    assert_eq!(doc.nodes[repeat].flex, None, "a marker has no flex item");
    let template = &doc.nodes[repeat].children;
    assert_eq!(template.len(), 2);
    for &t in template {
        assert_eq!(doc.nodes[t].repeat, Some(repeat));
        assert!(
            doc.nodes[t].flex.is_some(),
            "a template root is a flex item"
        );
    }
    let Element::Button { text, onclick, .. } = &doc.nodes[template[0]].element else {
        panic!("a button");
    };
    assert_eq!(text, &TextSource::Bound("Label".to_owned()));
    assert_eq!(onclick.as_deref(), Some("Click"));
    assert_eq!(doc.nodes[repeat].repeat, None);
}

/// OpenAC `A_misspelt_required_binding_fails_with_an_empty_or_null_list`
/// and `A_member_only_on_a_subtype_is_invisible`: a name the item type does
/// not declare fails, whatever the list holds; and a window name is not
/// visible inside the template.
#[test]
fn a_misspelt_or_window_name_in_a_template_fails() {
    let refused = message(window(
        "<repeat items=\"{Rows}\"><checkbox checked=\"{Onn}\"/></repeat>",
    ));
    assert!(refused.contains("Row"), "{refused}");
    assert!(
        window("<repeat items=\"{Rows}\"><label text=\"{Count}\"/></repeat>")
            .map(|d| d.nodes[1].element.clone())
            .is_ok_and(|e| e
                == Element::Label {
                    text: TextSource::Literal("{Count}".to_owned()),
                    color: None,
                }),
        "an undeclared text name shows as written, as in a window"
    );
    assert!(window("<repeat items=\"{Rows}\"><checkbox checked=\"{Count}\"/></repeat>").is_err());
}

/// OpenAC `Items_must_bind_a_typed_list`.
#[test]
fn items_must_bind_a_list_of_items() {
    for items in [
        "",
        "items=\"Rows\"",
        "items=\"(Rows)\"",
        "items=\"{Count}\"",
        "items=\"{Names}\"",
        "items=\"{Text}\"",
        "items=\"{Missing}\"",
    ] {
        assert!(
            window(&format!("<repeat {items}><label text=\"x\"/></repeat>")).is_err(),
            "{items}"
        );
    }
    assert!(
        message(window(
            "<repeat items=\"{Missing}\"><label text=\"x\"/></repeat>"
        ))
        .contains("did not resolve to a property on Binding")
    );
}

/// OpenAC `A_repeat_goes_in_a_flex_container`.
#[test]
fn a_repeat_goes_in_a_flex_container() {
    assert!(window_with(LABEL_TEMPLATE, "h=\"200\"").is_err());
    assert!(
        window(&format!(
            "<group w=\"100\" h=\"100\">{LABEL_TEMPLATE}</group>"
        ))
        .is_err()
    );
    assert!(
        window(&format!(
            "<group layout=\"column\"><group w=\"100\" h=\"100\">{LABEL_TEMPLATE}</group></group>"
        ))
        .is_err()
    );
    assert!(window(&format!("<group layout=\"row\">{LABEL_TEMPLATE}</group>")).is_ok());
    assert_eq!(
        message(window_with(LABEL_TEMPLATE, "h=\"200\"")),
        "<repeat> goes in a flex container (a window or <group> with layout)"
    );
}

/// OpenAC `A_repeat_takes_only_items_and_key`.
#[test]
fn a_repeat_takes_only_items_and_key() {
    for attr in [
        "x=\"0\"",
        "w=\"100\"",
        "h=\"20\"",
        "grow=\"1\"",
        "anchor=\"left\"",
        "name=\"rows\"",
        "visible=\"false\"",
    ] {
        assert!(
            window(&format!(
                "<repeat items=\"{{Rows}}\" {attr}><label text=\"x\"/></repeat>"
            ))
            .is_err(),
            "{attr}"
        );
    }
    assert_eq!(
        message(window(
            "<repeat items=\"{Rows}\" w=\"100\"><label text=\"x\"/></repeat>"
        )),
        "<repeat> takes only items= and key=, not w=; put layout attributes on the template's elements"
    );
}

/// OpenAC `A_named_element_needs_a_key_on_every_enclosing_repeat`.
#[test]
fn a_named_element_needs_a_key_on_every_enclosing_repeat() {
    let keyless = message(window(
        "<repeat items=\"{Rows}\"><label name=\"n\" text=\"{Label}\"/></repeat>",
    ));
    assert!(keyless.contains("needs key="), "{keyless}");
    let keyless_id = message(window(
        "<repeat items=\"{Rows}\"><label id=\"n\" text=\"{Label}\"/></repeat>",
    ));
    assert!(keyless_id.contains("id=\"n\""), "{keyless_id}");
    assert!(
        window(
            "<repeat items=\"{Sections}\"><group layout=\"column\">\
             <repeat items=\"{Rows}\" key=\"{Id}\"><label name=\"n\" text=\"{Label}\"/></repeat>\
             </group></repeat>"
        )
        .is_err()
    );
    let keyed = window(
        "<repeat items=\"{Rows}\" key=\"{Id}\"><label name=\"n\" text=\"{Label}\"/></repeat>",
    )
    .unwrap();
    assert_eq!(
        keyed.nodes[keyed.roots[0]].element,
        Element::Repeat {
            items: "Rows".to_owned(),
            key: Some("Id".to_owned()),
        }
    );
}

/// OpenAC `A_key_must_bind_a_member_of_the_item_type`.
#[test]
fn a_key_must_bind_a_member_of_the_item_type() {
    assert!(
        message(window(
            "<repeat items=\"{Rows}\" key=\"{Nope}\"><label text=\"x\"/></repeat>"
        ))
        .contains("did not resolve to a property on Row")
    );
    assert!(window("<repeat items=\"{Rows}\" key=\"(Id)\"><label text=\"x\"/></repeat>").is_err());
    // A window name is not a member of the item.
    assert!(
        window("<repeat items=\"{Rows}\" key=\"{Count}\"><label text=\"x\"/></repeat>").is_err()
    );
    // A handler or a list is no key.
    assert!(
        window("<repeat items=\"{Rows}\" key=\"{Click}\"><label text=\"x\"/></repeat>").is_err()
    );
    assert!(
        window("<repeat items=\"{Rows}\" key=\"{Tags}\"><label text=\"x\"/></repeat>").is_err()
    );
}

/// OpenAC `A_context_menu_goes_on_a_template_element_not_on_the_repeat`.
#[test]
fn a_context_menu_goes_on_a_template_element_not_on_the_repeat() {
    assert_eq!(
        message(window(
            "<repeat items=\"{Rows}\"><label text=\"x\"/>\
             <contextmenu items=\"A|B\" onselect=\"{Pick}\"/></repeat>"
        )),
        "<contextmenu> goes inside a <repeat>'s template element, not directly in <repeat>"
    );
    let inside = window(
        "<repeat items=\"{Rows}\"><button text=\"{Label}\" w=\"80\" h=\"20\">\
         <contextmenu items=\"Edit|Delete\" onselect=\"{Pick}\"/></button></repeat>",
    )
    .unwrap();
    let button = inside.nodes[inside.roots[0]].children[0];
    assert_eq!(
        inside.nodes[button]
            .common
            .context_menu
            .as_ref()
            .map(|m| m.onselect.as_str()),
        Some("Pick")
    );
}

/// OpenAC `Repeats_nest_and_inner_bindings_read_the_inner_item`: the inner
/// `items` is read on the outer item, and the inner template resolves on
/// the inner item type.
#[test]
fn repeats_nest_and_inner_names_resolve_on_the_inner_item() {
    let doc = window(
        "<repeat items=\"{Sections}\" key=\"{Id}\"><group layout=\"column\"><label text=\"{Title}\"/>\
         <repeat items=\"{Rows}\" key=\"{Id}\"><label text=\"{Label}\"/></repeat></group></repeat>",
    )
    .unwrap();
    let outer = doc.roots[0];
    let group = doc.nodes[outer].children[0];
    let inner = doc.nodes[group].children[1];
    assert_eq!(
        doc.nodes[inner].element,
        Element::Repeat {
            items: "Rows".to_owned(),
            key: Some("Id".to_owned()),
        }
    );
    assert_eq!(doc.nodes[group].repeat, Some(outer));
    assert_eq!(doc.nodes[inner].repeat, Some(outer));
    let label = doc.nodes[inner].children[0];
    assert_eq!(doc.nodes[label].repeat, Some(inner));
    assert!(doc.is_within(label, outer));
    assert!(doc.is_within(label, inner));
    assert!(!doc.is_within(group, inner));
    // The inner template cannot see the outer item's names.
    assert!(
        window(
            "<repeat items=\"{Sections}\"><group layout=\"column\">\
             <repeat items=\"{Rows}\"><checkbox checked=\"{Title}\"/></repeat></group></repeat>"
        )
        .is_err()
    );
    // Directly inside a repeat: the inner repeat's parent is the outer one.
    let direct = window(
        "<repeat items=\"{Sections}\"><label text=\"{Title}\"/>\
         <repeat items=\"{Rows}\"><label text=\"{Label}\"/></repeat></repeat>",
    )
    .unwrap();
    let outer = direct.roots[0];
    let inner = direct.nodes[outer].children[1];
    assert!(matches!(
        direct.nodes[inner].element,
        Element::Repeat { .. }
    ));
    assert!(direct.nodes[direct.nodes[inner].children[0]].flex.is_some());
}

/// OpenAC `A_stray_attribute_in_a_template_is_reported_once_for_all_its_rows`
/// (the document half: the template is read once, so its stray attribute
/// is one warning however many rows it builds).
#[test]
fn a_stray_attribute_in_a_template_is_one_warning() {
    let doc = window(
        "<repeat items=\"{Rows}\"><group w=\"40\" h=\"20\"><label text=\"{Label}\" grow=\"1\"/></group></repeat>",
    )
    .unwrap();
    assert_eq!(doc.warnings.len(), 1, "{:?}", doc.warnings);
    assert!(doc.warnings[0].contains("grow=\"1\""), "{:?}", doc.warnings);
}

/// OpenAC `NamedControlLookupsSkipRepeatRowsButFindTheControlsAfterThem`:
/// a name inside a template names no single control.
#[test]
fn a_by_name_lookup_skips_template_names_but_finds_what_follows() {
    let doc = window(
        "<repeat items=\"{Rows}\" key=\"{Id}\"><label name=\"row\" h=\"20\" text=\"{Label}\"/></repeat>\
         <label name=\"after\" h=\"20\" text=\"after\"/>",
    )
    .unwrap();
    assert_eq!(doc.find("row"), None);
    assert_eq!(doc.find("after"), Some(2));
}

/// OpenAC `UiMarkupRepeat.TemplateNames`: the remembered names of a
/// repeat's template, nested templates included.
#[test]
fn template_names_include_nested_templates() {
    let doc = window(
        "<repeat items=\"{Sections}\" key=\"{Id}\"><group layout=\"column\">\
         <expander name=\"head\" h=\"40\" text=\"{Title}\"/>\
         <repeat items=\"{Rows}\" key=\"{Id}\"><expander id=\"inner\" h=\"40\" text=\"{Label}\"/></repeat>\
         </group></repeat><expander name=\"outside\" h=\"40\" text=\"x\"/>",
    )
    .unwrap();
    let outer = doc.roots[0];
    let names = doc.template_names(outer);
    assert_eq!(
        names.into_iter().collect::<Vec<_>>(),
        vec!["head".to_owned(), "inner".to_owned()]
    );
}

//! The flex attributes of plugin markup. Inside flex (a container with
//! `layout` and its items) every misuse fails the build: placement
//! attributes on a flex item, unknown keywords, negative or non-finite
//! numbers and inverted limits. Markup that does not use flex was written
//! before these attributes existed, when they were ignored, so on it a
//! stray one (container attributes without `layout`, item attributes under
//! an absolute container, an unknown `layout` or `scroll`) is reported to
//! the document's warnings and ignored.

use roxmltree::Node as XmlNode;

use super::{MarkupError, attr, element_identity};
use crate::flex::{FlexAlign, FlexDirection, FlexEdges, FlexJustify, FlexNode};
use crate::value::{net_float, parse_float};

const CONTAINER_ATTRIBUTES: [&str; 5] = ["gap", "padding", "justify", "align", "wrap"];
const ITEM_ONLY_ATTRIBUTES: [&str; 4] = ["grow", "shrink", "basis", "alignself"];
const LIMIT_ATTRIBUTES: [&str; 4] = ["minw", "maxw", "minh", "maxh"];
const PLACEMENT_ATTRIBUTES: [&str; 3] = ["x", "y", "anchor"];

/// A flex container's attributes: `layout` and its companions.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FlexContainer {
    /// `layout`: the main axis.
    pub direction: FlexDirection,
    /// `gap` between items, pixels.
    pub gap: f32,
    /// `padding` inside the container.
    pub padding: FlexEdges,
    /// `justify`: where free main-axis space goes.
    pub justify: FlexJustify,
    /// `align`: the items' cross-axis placement.
    pub align: FlexAlign,
    /// `wrap`: a row breaks into lines.
    pub wrap: bool,
}

impl FlexContainer {
    /// Puts these attributes on `node`.
    pub fn apply(&self, node: &mut FlexNode) {
        node.direction = self.direction;
        node.gap = self.gap;
        node.padding = self.padding;
        node.justify = self.justify;
        node.align = self.align;
        node.wrap = self.wrap;
    }
}

/// A flex item's attributes.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FlexItem {
    /// `grow`.
    pub grow: f32,
    /// `shrink` (default 1).
    pub shrink: f32,
    /// `basis` (`None`: auto).
    pub basis: Option<f32>,
    /// `w`.
    pub width: Option<f32>,
    /// `h`.
    pub height: Option<f32>,
    /// `minw`.
    pub min_width: Option<f32>,
    /// `maxw`.
    pub max_width: Option<f32>,
    /// `minh`.
    pub min_height: Option<f32>,
    /// `maxh`.
    pub max_height: Option<f32>,
    /// `alignself`.
    pub align_self: Option<FlexAlign>,
}

impl FlexItem {
    /// Puts these attributes on `node`.
    pub fn apply(&self, node: &mut FlexNode) {
        node.grow = self.grow;
        node.shrink = self.shrink;
        node.basis = self.basis;
        node.width = self.width;
        node.height = self.height;
        node.min_width = self.min_width;
        node.max_width = self.max_width;
        node.min_height = self.min_height;
        node.max_height = self.max_height;
        node.align_self = self.align_self;
    }
}

/// Whether `el` is a flex container. An unknown `layout` is reported and
/// leaves it absolute.
pub(super) fn is_container(el: XmlNode<'_, '_>, warnings: &mut Vec<String>) -> bool {
    match attr(el, "layout") {
        None => false,
        Some("row" | "column") => true,
        Some(other) => {
            warnings.push(ignored(el, "layout", other, "must be row or column"));
            false
        }
    }
}

/// Reports container attributes on `el`, which has no `layout`.
pub(super) fn reject_container(el: XmlNode<'_, '_>, warnings: &mut Vec<String>) {
    for name in CONTAINER_ATTRIBUTES {
        if let Some(value) = attr(el, name) {
            warnings.push(ignored(
                el,
                name,
                value,
                "needs layout=\"row\" or layout=\"column\" on the same element",
            ));
        }
    }
}

/// The container attributes of `el`, which has `layout`.
pub(super) fn read_container(el: XmlNode<'_, '_>) -> Result<FlexContainer, MarkupError> {
    let direction = if attr(el, "layout") == Some("row") {
        FlexDirection::Row
    } else {
        FlexDirection::Column
    };
    let gap = non_negative(el, "gap")?.unwrap_or(0.0);
    let padding = padding(el)?;
    let justify = match attr(el, "justify") {
        None | Some("start") => FlexJustify::Start,
        Some("center") => FlexJustify::Center,
        Some("end") => FlexJustify::End,
        Some("space-between") => FlexJustify::SpaceBetween,
        Some(other) => {
            return Err(error(
                el,
                "justify",
                other,
                "must be start, center, end or space-between",
            ));
        }
    };
    let align = align(el, "align")?.unwrap_or(FlexAlign::Stretch);
    let wrap = match attr(el, "wrap") {
        None | Some("false") => false,
        Some("true") => true,
        Some(other) => return Err(error(el, "wrap", other, "must be true or false")),
    };
    if wrap && direction == FlexDirection::Column {
        return Err(error(
            el,
            "wrap",
            "true",
            "is not supported on a column (only rows wrap in this version)",
        ));
    }
    Ok(FlexContainer {
        direction,
        gap,
        padding,
        justify,
        align,
        wrap,
    })
}

/// The item attributes of `el`, a child of a flex container. `x`, `y` and
/// `anchor` are errors: the container places its items.
pub(super) fn read_item(el: XmlNode<'_, '_>) -> Result<FlexItem, MarkupError> {
    for name in PLACEMENT_ATTRIBUTES {
        if let Some(value) = attr(el, name) {
            return Err(error(
                el,
                name,
                value,
                "cannot be used on a flex item; its container places it",
            ));
        }
    }
    let item = FlexItem {
        grow: non_negative(el, "grow")?.unwrap_or(0.0),
        shrink: non_negative(el, "shrink")?.unwrap_or(1.0),
        basis: if attr(el, "basis") == Some("auto") {
            None
        } else {
            non_negative(el, "basis")?
        },
        width: non_negative(el, "w")?,
        height: non_negative(el, "h")?,
        min_width: non_negative(el, "minw")?,
        max_width: non_negative(el, "maxw")?,
        min_height: non_negative(el, "minh")?,
        max_height: non_negative(el, "maxh")?,
        align_self: align(el, "alignself")?,
    };
    check_limits(el, "minw", item.min_width, "maxw", item.max_width)?;
    check_limits(el, "minh", item.min_height, "maxh", item.max_height)?;
    Ok(item)
}

/// Reports item attributes on `el`, a child of an absolute container.
pub(super) fn reject_item(el: XmlNode<'_, '_>, warnings: &mut Vec<String>) {
    for name in ITEM_ONLY_ATTRIBUTES.iter().chain(LIMIT_ATTRIBUTES.iter()) {
        if let Some(value) = attr(el, name) {
            warnings.push(ignored(
                el,
                name,
                value,
                "only applies to a child of a layout=\"row\" or layout=\"column\" container",
            ));
        }
    }
}

/// The axes `scroll` asks `el` (a group or the root panel) to scroll on
/// (x, y); neither when absent. An unknown value is reported and scrolls
/// neither.
pub(super) fn scroll(el: XmlNode<'_, '_>, warnings: &mut Vec<String>) -> (bool, bool) {
    match attr(el, "scroll") {
        None => (false, false),
        Some("x") => (true, false),
        Some("y") => (false, true),
        Some("both") => (true, true),
        Some(other) => {
            warnings.push(ignored(el, "scroll", other, "must be x, y or both"));
            (false, false)
        }
    }
}

/// A root panel's authored content-area size or limit, checked like a flex
/// number: `None` when absent.
pub(super) fn root_size(root: XmlNode<'_, '_>, name: &str) -> Result<Option<f32>, MarkupError> {
    non_negative(root, name)
}

fn align(el: XmlNode<'_, '_>, name: &str) -> Result<Option<FlexAlign>, MarkupError> {
    Ok(match attr(el, name) {
        None => None,
        Some("start") => Some(FlexAlign::Start),
        Some("center") => Some(FlexAlign::Center),
        Some("end") => Some(FlexAlign::End),
        Some("stretch") => Some(FlexAlign::Stretch),
        Some(other) => {
            return Err(error(
                el,
                name,
                other,
                "must be start, center, end or stretch",
            ));
        }
    })
}

fn padding(el: XmlNode<'_, '_>) -> Result<FlexEdges, MarkupError> {
    let Some(raw) = attr(el, "padding") else {
        return Ok(FlexEdges::default());
    };
    let values = raw
        .split([' ', ',', '\t'])
        .filter(|p| !p.is_empty())
        .map(|p| number(el, "padding", p, raw))
        .collect::<Result<Vec<f32>, _>>()?;
    if values.iter().any(|v| *v < 0.0) {
        return Err(error(el, "padding", raw, "must not be negative"));
    }
    match values[..] {
        [all] => Ok(FlexEdges::all(all)),
        [vertical, horizontal] => Ok(FlexEdges {
            top: vertical,
            right: horizontal,
            bottom: vertical,
            left: horizontal,
        }),
        [top, right, bottom, left] => Ok(FlexEdges {
            top,
            right,
            bottom,
            left,
        }),
        _ => Err(error(
            el,
            "padding",
            raw,
            "must be 1, 2 or 4 numbers (all; vertical horizontal; top right bottom left)",
        )),
    }
}

fn non_negative(el: XmlNode<'_, '_>, name: &str) -> Result<Option<f32>, MarkupError> {
    let Some(raw) = attr(el, name) else {
        return Ok(None);
    };
    let value = number(el, name, raw, raw)?;
    if value < 0.0 {
        return Err(error(el, name, raw, "must not be negative"));
    }
    Ok(Some(value))
}

fn number(el: XmlNode<'_, '_>, name: &str, text: &str, raw: &str) -> Result<f32, MarkupError> {
    // "Infinity" and "NaN" read as numbers and are reported as not finite.
    let value = parse_float(text).ok_or_else(|| error(el, name, raw, "is not a number"))?;
    if !value.is_finite() {
        return Err(error(el, name, raw, "must be a finite number"));
    }
    Ok(value)
}

fn check_limits(
    el: XmlNode<'_, '_>,
    min_name: &str,
    min: Option<f32>,
    max_name: &str,
    max: Option<f32>,
) -> Result<(), MarkupError> {
    if let (Some(lo), Some(hi)) = (min, max)
        && lo > hi
    {
        return Err(MarkupError(format!(
            "{} {min_name}=\"{}\" is greater than {max_name}=\"{}\"",
            element_identity(el),
            net_float(lo),
            net_float(hi)
        )));
    }
    Ok(())
}

fn error(el: XmlNode<'_, '_>, name: &str, value: &str, problem: &str) -> MarkupError {
    MarkupError(format!(
        "{} {name}=\"{value}\" {problem}",
        element_identity(el)
    ))
}

/// The warning for an attribute the build ignores.
pub(super) fn ignored(el: XmlNode<'_, '_>, name: &str, value: &str, problem: &str) -> String {
    format!(
        "{} {name}=\"{value}\" {problem}; ignored",
        element_identity(el)
    )
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/MarkupFlexAttributesTests.cs`.

    use super::*;

    fn with<T>(xml: &str, f: impl FnOnce(XmlNode<'_, '_>) -> T) -> T {
        let doc = roxmltree::Document::parse(xml).expect("xml");
        f(doc.root_element())
    }

    fn container(attrs: &str) -> Result<FlexContainer, MarkupError> {
        with(&format!("<group {attrs} />"), read_container)
    }

    fn item(attrs: &str) -> Result<FlexItem, MarkupError> {
        with(&format!("<label {attrs} />"), read_item)
    }

    #[test]
    fn container_defaults_are_css_defaults() {
        // Container_defaults_are_css_defaults
        let c = container("layout=\"column\"").unwrap();
        assert_eq!(c.direction, FlexDirection::Column);
        assert_eq!(c.gap, 0.0);
        assert_eq!(c.padding, FlexEdges::default());
        assert_eq!(c.justify, FlexJustify::Start);
        assert_eq!(c.align, FlexAlign::Stretch);
        assert!(!c.wrap);
    }

    #[test]
    fn container_attributes_are_read() {
        // Container_attributes_are_read
        let c = container(
            "layout=\"row\" gap=\"6\" justify=\"space-between\" align=\"center\" wrap=\"true\"",
        )
        .unwrap();
        assert_eq!(c.direction, FlexDirection::Row);
        assert_eq!(c.gap, 6.0);
        assert_eq!(c.justify, FlexJustify::SpaceBetween);
        assert_eq!(c.align, FlexAlign::Center);
        assert!(c.wrap);
    }

    #[test]
    fn padding_takes_one_two_or_four_numbers_in_css_order() {
        // Padding_takes_one_two_or_four_numbers_in_css_order
        for (padding, [top, right, bottom, left]) in [
            ("8", [8.0, 8.0, 8.0, 8.0]),
            ("4 10", [4.0, 10.0, 4.0, 10.0]),
            ("1 2 3 4", [1.0, 2.0, 3.0, 4.0]),
            ("1,2,3,4", [1.0, 2.0, 3.0, 4.0]),
        ] {
            let c = container(&format!("layout=\"row\" padding=\"{padding}\"")).unwrap();
            assert_eq!(
                c.padding,
                FlexEdges {
                    top,
                    right,
                    bottom,
                    left
                }
            );
        }
    }

    #[test]
    fn item_defaults_are_css_defaults_and_attributes_are_read() {
        // Item_defaults_are_css_defaults, Item_attributes_are_read, Basis_auto_is_the_default
        let i = item("").unwrap();
        assert_eq!((i.grow, i.shrink), (0.0, 1.0));
        assert!(i.basis.is_none() && i.width.is_none() && i.height.is_none());
        assert!(i.min_width.is_none() && i.max_width.is_none());
        assert!(i.min_height.is_none() && i.max_height.is_none() && i.align_self.is_none());
        let i = item(
            "grow=\"2\" shrink=\"0\" basis=\"40\" w=\"50\" h=\"20\" minw=\"10\" maxw=\"90\" minh=\"5\" maxh=\"30\" alignself=\"end\"",
        )
        .unwrap();
        assert_eq!((i.grow, i.shrink, i.basis), (2.0, 0.0, Some(40.0)));
        assert_eq!((i.width, i.height), (Some(50.0), Some(20.0)));
        assert_eq!((i.min_width, i.max_width), (Some(10.0), Some(90.0)));
        assert_eq!((i.min_height, i.max_height), (Some(5.0), Some(30.0)));
        assert_eq!(i.align_self, Some(FlexAlign::End));
        assert!(item("basis=\"auto\"").unwrap().basis.is_none());
    }

    #[test]
    fn bad_container_values_name_the_attribute_and_value() {
        // Bad_container_values_name_the_attribute_and_value
        for (xml, expected) in [
            (
                "<group layout=\"row\" justify=\"around\" />",
                "justify=\"around\"",
            ),
            (
                "<group layout=\"row\" align=\"baseline\" />",
                "align=\"baseline\"",
            ),
            ("<group layout=\"row\" wrap=\"yes\" />", "wrap=\"yes\""),
            ("<group layout=\"column\" wrap=\"true\" />", "wrap=\"true\""),
            ("<group layout=\"row\" gap=\"-1\" />", "gap=\"-1\""),
            ("<group layout=\"row\" gap=\"NaN\" />", "gap=\"NaN\""),
            (
                "<group layout=\"row\" gap=\"Infinity\" />",
                "gap=\"Infinity\"",
            ),
            ("<group layout=\"row\" gap=\"wide\" />", "gap=\"wide\""),
            (
                "<group layout=\"row\" padding=\"1 2 3\" />",
                "padding=\"1 2 3\"",
            ),
            (
                "<group layout=\"row\" padding=\"1 x\" />",
                "padding=\"1 x\"",
            ),
            ("<group layout=\"row\" padding=\"-2\" />", "padding=\"-2\""),
            (
                "<group layout=\"row\" padding=\"NaN\" />",
                "padding=\"NaN\"",
            ),
        ] {
            let message = with(xml, |el| {
                assert!(is_container(el, &mut Vec::new()));
                read_container(el).unwrap_err().0
            });
            assert!(message.contains("<group>"), "{message}");
            assert!(message.contains(expected), "{message}");
        }
    }

    #[test]
    fn container_attributes_without_layout_are_reported_and_ignored() {
        // Container_attributes_without_layout_are_reported_and_ignored
        for a in [
            "gap=\"4\"",
            "padding=\"4\"",
            "justify=\"end\"",
            "align=\"center\"",
            "wrap=\"true\"",
        ] {
            let mut warnings = Vec::new();
            with(&format!("<group name=\"g\" {a} />"), |el| {
                reject_container(el, &mut warnings);
            });
            assert_eq!(warnings.len(), 1);
            let w = &warnings[0];
            assert!(w.contains("<group name=\"g\">") && w.contains(a) && w.contains("layout"));
            assert!(w.ends_with("ignored"), "{w}");
        }
    }

    #[test]
    fn unknown_layout_and_scroll_are_reported() {
        // An_unknown_layout_is_reported_and_leaves_the_element_absolute,
        // An_unknown_scroll_is_reported_and_scrolls_neither_way
        let mut warnings = Vec::new();
        with("<group layout=\"grid\" />", |el| {
            assert!(!is_container(el, &mut warnings));
        });
        assert_eq!(warnings.len(), 1);
        assert!(warnings[0].contains("layout=\"grid\" must be row or column"));
        let mut warnings = Vec::new();
        with("<group scroll=\"true\" />", |el| {
            assert_eq!(scroll(el, &mut warnings), (false, false));
        });
        assert_eq!(warnings.len(), 1);
        assert!(warnings[0].contains("scroll=\"true\" must be x, y or both"));
    }

    #[test]
    fn item_attributes_under_an_absolute_container_are_reported_and_ignored() {
        // Item_attributes_under_an_absolute_container_are_reported_and_ignored
        for a in [
            "grow=\"1\"",
            "shrink=\"0\"",
            "basis=\"20\"",
            "alignself=\"end\"",
            "minw=\"5\"",
            "maxw=\"5\"",
            "minh=\"5\"",
            "maxh=\"5\"",
        ] {
            let mut warnings = Vec::new();
            with(&format!("<label {a} />"), |el| {
                reject_item(el, &mut warnings)
            });
            assert_eq!(warnings.len(), 1);
            assert!(warnings[0].contains(a));
        }
    }

    #[test]
    fn placement_on_a_flex_item_is_an_error() {
        // Placement_on_a_flex_item_is_an_error
        for a in ["x=\"4\"", "y=\"4\"", "anchor=\"right\""] {
            let message = item(a).unwrap_err().0;
            assert!(
                message.contains(a) && message.contains("flex item"),
                "{message}"
            );
        }
    }

    #[test]
    fn bad_item_values_name_the_attribute_and_value() {
        // Bad_item_values_name_the_attribute_and_value
        for a in [
            "grow=\"-1\"",
            "shrink=\"-0.5\"",
            "basis=\"-3\"",
            "w=\"-3\"",
            "grow=\"NaN\"",
            "shrink=\"Infinity\"",
            "basis=\"-Infinity\"",
            "w=\"NaN\"",
            "h=\"Infinity\"",
            "minw=\"NaN\"",
            "maxw=\"Infinity\"",
            "minh=\"NaN\"",
            "maxh=\"NaN\"",
            "alignself=\"middle\"",
            "basis=\"big\"",
        ] {
            let message = item(a).unwrap_err().0;
            assert!(
                message.contains("<label>") && message.contains(a),
                "{message}"
            );
        }
    }

    #[test]
    fn a_minimum_above_its_maximum_is_an_error() {
        // A_minimum_above_its_maximum_is_an_error
        for (attrs, expected) in [
            (
                "minw=\"50\" maxw=\"40\"",
                "minw=\"50\" is greater than maxw=\"40\"",
            ),
            (
                "minh=\"9\" maxh=\"8.5\"",
                "minh=\"9\" is greater than maxh=\"8.5\"",
            ),
        ] {
            let message = item(attrs).unwrap_err().0;
            assert!(message.contains(expected), "{message}");
        }
    }
}

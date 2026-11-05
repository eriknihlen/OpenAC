//! What a panel's `{Binding}` names stand for: the kinds a plugin declares
//! them as, the values it pushes, and how each attribute reads a value
//! (the same conversions OpenAC's markup applies to a bound property).

use std::collections::BTreeMap;
use std::fmt::Write as _;

/// What a binding name is: a value of one type, or a handler the panel
/// calls (an `on...` attribute).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum BindingKind {
    /// `bool`.
    Bool,
    /// `int` (32-bit signed).
    Int,
    /// `uint` (32-bit unsigned).
    Uint,
    /// `float` (32-bit).
    Float,
    /// `string`.
    Text,
    /// A list of strings.
    TextList,
    /// A list of `uint`.
    UintList,
    /// A list of `int`.
    IntList,
    /// A list of `bool`.
    BoolList,
    /// A handler with no argument (a click).
    Action,
    /// A handler taking an `int` (a list row).
    IntAction,
    /// A handler taking a `float` (a slider).
    FloatAction,
    /// A handler taking a `string` (a field, a menu).
    TextAction,
    /// `bool` or none (a checkbox's `bool?`: none is indeterminate).
    OptionalBool,
    /// A list of `float` (a list's column widths, numeric sort keys).
    FloatList,
    /// A list of item records (a `<repeat>`'s rows), each with the bindings
    /// its item type declares.
    Items,
    /// A handler taking a `bool` (a checkbox, an expander).
    BoolAction,
    /// A handler taking a `uint` (a colour picker bound to a `uint`).
    UintAction,
    /// A handler taking an `int` and a `bool` (a list's sort request).
    IntBoolAction,
    /// A handler taking a `string` and a `bool` (a tree row's expansion).
    TextBoolAction,
    /// A handler taking a list of `float` (a list's column widths).
    FloatListAction,
    /// A handler taking a list of `int` (a multi-select list's rows).
    IntListAction,
}

impl BindingKind {
    /// Every kind, in declaration order.
    pub const ALL: [Self; 22] = [
        Self::Bool,
        Self::Int,
        Self::Uint,
        Self::Float,
        Self::Text,
        Self::TextList,
        Self::UintList,
        Self::IntList,
        Self::BoolList,
        Self::Action,
        Self::IntAction,
        Self::FloatAction,
        Self::TextAction,
        Self::OptionalBool,
        Self::FloatList,
        Self::Items,
        Self::BoolAction,
        Self::UintAction,
        Self::IntBoolAction,
        Self::TextBoolAction,
        Self::FloatListAction,
        Self::IntListAction,
    ];

    /// A handler rather than a value.
    #[must_use]
    pub const fn is_action(self) -> bool {
        matches!(
            self,
            Self::Action
                | Self::IntAction
                | Self::FloatAction
                | Self::TextAction
                | Self::BoolAction
                | Self::UintAction
                | Self::IntBoolAction
                | Self::TextBoolAction
                | Self::FloatListAction
                | Self::IntListAction
        )
    }
}

/// The binding names a panel's markup may use, with their kinds, and the
/// name of the plugin type they belong to (it appears in error messages,
/// as the C# property owner's type name does).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct BindingSchema {
    /// The binding type's name (`MossTankBindings`, ...).
    pub type_name: String,
    kinds: BTreeMap<String, BindingKind>,
    /// The item type of each [`BindingKind::Items`] binding: the names an
    /// item record carries (OpenAC: the list's element type).
    items: BTreeMap<String, BindingSchema>,
}

impl BindingSchema {
    /// No bindings yet, for a type called `type_name`.
    #[must_use]
    pub fn new(type_name: impl Into<String>) -> Self {
        Self {
            type_name: type_name.into(),
            kinds: BTreeMap::new(),
            items: BTreeMap::new(),
        }
    }

    /// Declares `name` as a list of items of type `item` (a `<repeat>`'s
    /// `items`).
    #[must_use]
    pub fn with_items(mut self, name: impl Into<String>, item: BindingSchema) -> Self {
        self.declare_items(name, item);
        self
    }

    /// Declares `name` as a list of items of type `item`.
    pub fn declare_items(&mut self, name: impl Into<String>, item: BindingSchema) {
        let name = name.into();
        self.kinds.insert(name.clone(), BindingKind::Items);
        self.items.insert(name, item);
    }

    /// The item type of list `name`, when it is declared as items.
    #[must_use]
    pub fn item_schema(&self, name: &str) -> Option<&BindingSchema> {
        self.items.get(name)
    }

    /// Declares `name` as `kind` (a later declaration of the same name
    /// replaces an earlier one).
    #[must_use]
    pub fn with(mut self, name: impl Into<String>, kind: BindingKind) -> Self {
        self.declare(name, kind);
        self
    }

    /// Declares `name` as `kind`.
    pub fn declare(&mut self, name: impl Into<String>, kind: BindingKind) {
        self.kinds.insert(name.into(), kind);
    }

    /// The kind `name` was declared as.
    #[must_use]
    pub fn kind(&self, name: &str) -> Option<BindingKind> {
        self.kinds.get(name).copied()
    }

    /// The kind of a handler a control fired: a window handler's own, or
    /// for a handler in `<repeat>` rows (`List[i]/.../Name`, see
    /// [`crate::row`]) the kind its item type declares, each list resolved
    /// on the item type around it.
    #[must_use]
    pub fn handler_kind(&self, handler: &str) -> Option<BindingKind> {
        let Some((steps, name)) = crate::row::split(handler) else {
            return self.kind(handler);
        };
        let mut scope = self;
        for step in &steps {
            scope = scope.item_schema(&step.list)?;
        }
        scope.kind(name)
    }

    /// Every declared name and its kind, by name.
    pub fn iter(&self) -> impl Iterator<Item = (&str, BindingKind)> {
        self.kinds.iter().map(|(n, k)| (n.as_str(), *k))
    }
}

/// A value a plugin pushes for a binding name.
#[derive(Debug, Clone, PartialEq)]
pub enum UiValue {
    /// `bool`.
    Bool(bool),
    /// `int`.
    Int(i32),
    /// `uint`.
    Uint(u32),
    /// `float`.
    Float(f32),
    /// `string`.
    Text(String),
    /// Strings.
    TextList(Vec<String>),
    /// `uint`s.
    UintList(Vec<u32>),
    /// `int`s.
    IntList(Vec<i32>),
    /// `bool`s.
    BoolList(Vec<bool>),
    /// `bool` or none (indeterminate).
    OptionalBool(Option<bool>),
    /// `float`s.
    FloatList(Vec<f32>),
    /// Item records, in order (a `<repeat>`'s list).
    Items(std::sync::Arc<Vec<ItemRecord>>),
}

/// One item of an [`UiValue::Items`] list: its values by binding name (the
/// names its item type declares; a nested list is an [`UiValue::Items`]).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ItemRecord {
    /// The item's values, by name.
    pub values: BTreeMap<String, UiValue>,
}

impl ItemRecord {
    /// An item with no values yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Sets one value.
    #[must_use]
    pub fn with(mut self, name: impl Into<String>, value: UiValue) -> Self {
        self.values.insert(name.into(), value);
        self
    }
}

impl UiValue {
    /// The kind of binding this value fills.
    #[must_use]
    pub const fn kind(&self) -> BindingKind {
        match self {
            Self::Bool(_) => BindingKind::Bool,
            Self::Int(_) => BindingKind::Int,
            Self::Uint(_) => BindingKind::Uint,
            Self::Float(_) => BindingKind::Float,
            Self::Text(_) => BindingKind::Text,
            Self::TextList(_) => BindingKind::TextList,
            Self::UintList(_) => BindingKind::UintList,
            Self::IntList(_) => BindingKind::IntList,
            Self::BoolList(_) => BindingKind::BoolList,
            Self::OptionalBool(_) => BindingKind::OptionalBool,
            Self::FloatList(_) => BindingKind::FloatList,
            Self::Items(_) => BindingKind::Items,
        }
    }

    /// The value as text, as .NET's `ToString()` writes the C# property's
    /// value (`True`, `0.5`, `1E+15`, ...): what a bound `text` shows.
    #[must_use]
    pub fn display(&self) -> String {
        match self {
            Self::Bool(true) => "True".to_owned(),
            Self::Bool(false) => "False".to_owned(),
            Self::Int(v) => v.to_string(),
            Self::Uint(v) => v.to_string(),
            Self::Float(v) => net_float(*v),
            Self::Text(t) => t.clone(),
            Self::TextList(_) => "System.String[]".to_owned(),
            Self::UintList(_) => "System.UInt32[]".to_owned(),
            Self::IntList(_) => "System.Int32[]".to_owned(),
            Self::BoolList(_) => "System.Boolean[]".to_owned(),
            // A `bool?` writes as its value, and null as nothing.
            Self::OptionalBool(Some(v)) => Self::Bool(*v).display(),
            Self::OptionalBool(None) => String::new(),
            Self::FloatList(_) => "System.Single[]".to_owned(),
            Self::Items(_) => "System.Object[]".to_owned(),
        }
    }

    /// The value as a `float` (.NET `Convert.ToSingle`); `None` for what
    /// does not convert.
    #[must_use]
    pub fn to_f32(&self) -> Option<f32> {
        match self {
            Self::Float(v) => Some(*v),
            Self::Int(v) => Some(*v as f32),
            Self::Uint(v) => Some(*v as f32),
            Self::Bool(v) => Some(if *v { 1.0 } else { 0.0 }),
            Self::Text(t) => parse_float(t),
            _ => None,
        }
    }

    /// The value as a `uint` (.NET `Convert.ToUInt32`); `None` for what does
    /// not convert or overflows.
    #[must_use]
    pub fn to_u32(&self) -> Option<u32> {
        match self {
            Self::Uint(v) => Some(*v),
            Self::Int(v) => u32::try_from(*v).ok(),
            Self::Bool(v) => Some(u32::from(*v)),
            Self::Float(v) => {
                // Convert.ToUInt32(float) rounds half to even.
                let r = round_half_even(f64::from(*v));
                (r >= 0.0 && r <= f64::from(u32::MAX)).then_some(r as u32)
            }
            Self::Text(t) => t.trim().parse().ok(),
            _ => None,
        }
    }
}

fn round_half_even(v: f64) -> f64 {
    let r = v.round();
    if (v - v.trunc()).abs() == 0.5 && r % 2.0 != 0.0 {
        r - v.signum()
    } else {
        r
    }
}

/// A `float` as .NET Core writes it (`float.ToString()`): the shortest text
/// that reads back the same, in scientific form (`1E+15`, `1.5E-05`) when
/// the decimal exponent is 15 or more or below -4.
#[must_use]
pub fn net_float(v: f32) -> String {
    if v.is_nan() {
        return "NaN".to_owned();
    }
    if v.is_infinite() {
        return if v > 0.0 { "∞" } else { "-∞" }.to_owned();
    }
    if v == 0.0 {
        return if v.is_sign_negative() { "-0" } else { "0" }.to_owned();
    }
    // Rust's `{:e}` is the shortest round-trip mantissa: "1.5e-5".
    let sci = format!("{v:e}");
    let (mantissa, exponent) = sci.split_once('e').unwrap_or((&sci, "0"));
    let exponent: i32 = exponent.parse().unwrap_or(0);
    let negative = mantissa.starts_with('-');
    let digits: String = mantissa.chars().filter(char::is_ascii_digit).collect();
    let mut out = String::new();
    if negative {
        out.push('-');
    }
    if !(-4..15).contains(&exponent) {
        out.push_str(&digits[..1]);
        if digits.len() > 1 {
            out.push('.');
            out.push_str(&digits[1..]);
        }
        let sign = if exponent < 0 { '-' } else { '+' };
        let _ = write!(out, "E{sign}{:02}", exponent.abs());
        return out;
    }
    if exponent < 0 {
        out.push_str("0.");
        for _ in 0..(-exponent - 1) {
            out.push('0');
        }
        out.push_str(&digits);
        return out;
    }
    let point = usize::try_from(exponent).unwrap_or(0) + 1;
    if digits.len() <= point {
        out.push_str(&digits);
        for _ in digits.len()..point {
            out.push('0');
        }
    } else {
        out.push_str(&digits[..point]);
        out.push('.');
        out.push_str(&digits[point..]);
    }
    out
}

/// .NET `float.TryParse(text, NumberStyles.Float, InvariantCulture)`:
/// surrounding white space, a sign, a decimal point and an exponent.
#[must_use]
pub fn parse_float(text: &str) -> Option<f32> {
    let t = text.trim();
    if t.is_empty() {
        return None;
    }
    // Rust also reads "inf" and "nan" spellings .NET does not; .NET reads
    // "Infinity" and "NaN" (any case), which Rust reads too.
    let lower = t.trim_start_matches(['+', '-']).to_ascii_lowercase();
    if lower == "inf" {
        return None;
    }
    t.parse().ok()
}

/// .NET `int.TryParse(text, NumberStyles.Integer, InvariantCulture)`.
#[must_use]
pub fn parse_int(text: &str) -> Option<i32> {
    text.trim().parse().ok()
}

/// .NET `bool.TryParse`: `true` or `false` in any case, surrounding white
/// space ignored.
#[must_use]
pub fn parse_bool(text: &str) -> Option<bool> {
    let t = text.trim();
    if t.eq_ignore_ascii_case("true") {
        Some(true)
    } else if t.eq_ignore_ascii_case("false") {
        Some(false)
    } else {
        None
    }
}

/// A colour, red, green, blue and alpha each from 0 to 1.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Rgba {
    /// Red.
    pub r: f32,
    /// Green.
    pub g: f32,
    /// Blue.
    pub b: f32,
    /// Alpha.
    pub a: f32,
}

impl Rgba {
    /// Opaque white: what an unreadable colour falls back to.
    pub const WHITE: Self = Self::new(1.0, 1.0, 1.0, 1.0);
    /// Transparent black.
    pub const TRANSPARENT: Self = Self::new(0.0, 0.0, 0.0, 0.0);

    /// A colour from its parts.
    #[must_use]
    pub const fn new(r: f32, g: f32, b: f32, a: f32) -> Self {
        Self { r, g, b, a }
    }

    /// `0xAARRGGBB` unpacked (the byte order of the `#AARRGGBB` literal).
    #[must_use]
    pub fn argb(packed: u32) -> Self {
        Self::new(
            ((packed >> 16) & 0xFF) as f32 / 255.0,
            ((packed >> 8) & 0xFF) as f32 / 255.0,
            (packed & 0xFF) as f32 / 255.0,
            ((packed >> 24) & 0xFF) as f32 / 255.0,
        )
    }

    /// `0xRRGGBB` as an opaque colour (a list row's colour).
    #[must_use]
    pub fn rgb(packed: u32) -> Self {
        Self::new(
            ((packed >> 16) & 0xFF) as f32 / 255.0,
            ((packed >> 8) & 0xFF) as f32 / 255.0,
            (packed & 0xFF) as f32 / 255.0,
            1.0,
        )
    }
}

/// What comes before a literal's colour for light themes.
pub(crate) const LIGHT_COLOR_MARKER: &str = "|light:";

/// A colour attribute's literal: `#AARRGGBB` (exactly nine characters),
/// the fallback after `|` of a `theme:token|#AARRGGBB` value, the first
/// colour of a `#AARRGGBB|light:#AARRGGBB` pair, otherwise opaque white.
#[must_use]
pub fn parse_color(text: Option<&str>) -> Rgba {
    let mut text = text;
    if let Some(t) = text {
        if t.starts_with("theme:") {
            text = t.split_once('|').map(|(_, fallback)| fallback);
        } else if let Some((first, _)) = t.split_once(LIGHT_COLOR_MARKER) {
            text = Some(first);
        }
    }
    match text {
        Some(t) if t.len() == 9 && t.starts_with('#') => {
            u32::from_str_radix(&t[1..], 16).map_or(Rgba::WHITE, Rgba::argb)
        }
        _ => Rgba::WHITE,
    }
}

/// A literal's colour for light themes, the part after `|light:`; none when
/// it has none. A `theme:` colour never has one: the theme gives its colour.
#[must_use]
pub fn light_color(text: &str) -> Option<Rgba> {
    if text.starts_with("theme:") {
        return None;
    }
    let (_, light) = text.split_once(LIGHT_COLOR_MARKER)?;
    Some(parse_color(Some(light)))
}

/// A bound colour: a 32-bit `0xAARRGGBB` (signed or unsigned) or the
/// literal's text; anything else is white.
#[must_use]
pub fn bound_color(value: Option<&UiValue>) -> Rgba {
    match value {
        Some(UiValue::Uint(packed)) => Rgba::argb(*packed),
        Some(UiValue::Int(packed)) => Rgba::argb(*packed as u32),
        Some(UiValue::Text(text)) => parse_color(Some(text)),
        _ => Rgba::WHITE,
    }
}

/// A `0x`-prefixed or bare hexadecimal sprite id, 0 when absent or
/// unreadable.
#[must_use]
pub fn parse_hex(text: Option<&str>) -> u32 {
    let Some(t) = text.map(str::trim).filter(|t| !t.is_empty()) else {
        return 0;
    };
    let t = t
        .strip_prefix("0x")
        .or_else(|| t.strip_prefix("0X"))
        .unwrap_or(t);
    u32::from_str_radix(t, 16).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn floats_are_written_as_dotnet_writes_them() {
        for (v, text) in [
            (0.5f32, "0.5"),
            (100.0, "100"),
            (0.0, "0"),
            (-0.0, "-0"),
            (1.0e15, "1E+15"),
            (1.5e-5, "1.5E-05"),
            (0.0001, "0.0001"),
            (123.456, "123.456"),
            (1.0e14, "100000000000000"),
            (-2.5, "-2.5"),
        ] {
            assert_eq!(net_float(v), text, "{v}");
        }
    }

    #[test]
    fn colours_unpack_aarrggbb() {
        assert_eq!(
            parse_color(Some("#FF112233")),
            Rgba::new(
                0x11 as f32 / 255.0,
                0x22 as f32 / 255.0,
                0x33 as f32 / 255.0,
                1.0
            )
        );
        assert_eq!(parse_color(Some("#112233")), Rgba::WHITE);
        assert_eq!(parse_color(Some("{Color}")), Rgba::WHITE);
        assert_eq!(parse_color(None), Rgba::WHITE);
        assert_eq!(
            parse_color(Some("theme:text|#80000000")),
            Rgba::new(0.0, 0.0, 0.0, 128.0 / 255.0)
        );
        assert_eq!(parse_color(Some("theme:text")), Rgba::WHITE);
        assert_eq!(
            parse_color(Some("#FF112233|light:#FF445566")),
            Rgba::argb(0xFF11_2233)
        );
        assert_eq!(
            light_color("#FF112233|light:#FF445566"),
            Some(Rgba::argb(0xFF44_5566))
        );
        assert_eq!(light_color("#FF112233|light:pink"), Some(Rgba::WHITE));
        assert_eq!(light_color("#FF112233"), None);
        assert_eq!(light_color("theme:text|#FF112233|light:#FF445566"), None);
        assert_eq!(bound_color(Some(&UiValue::Int(-1))), Rgba::WHITE);
        assert_eq!(bound_color(Some(&UiValue::Bool(true))), Rgba::WHITE);
    }

    #[test]
    fn numbers_read_like_dotnet() {
        assert_eq!(parse_float(" 1.5 "), Some(1.5));
        assert_eq!(parse_float("1e2"), Some(100.0));
        assert_eq!(parse_float("abc"), None);
        assert_eq!(parse_int(" -3 "), Some(-3));
        assert_eq!(parse_bool(" TRUE "), Some(true));
        assert_eq!(parse_bool("yes"), None);
        assert_eq!(parse_hex(Some("0x06001141")), 0x0600_1141);
        assert_eq!(parse_hex(Some("06001141")), 0x0600_1141);
        assert_eq!(parse_hex(Some("zz")), 0);
        assert_eq!(UiValue::Float(2.5).to_u32(), Some(2));
        assert_eq!(UiValue::Float(3.5).to_u32(), Some(4));
        assert_eq!(UiValue::Int(-1).to_u32(), None);
    }
}

//! Plugin panel markup: OpenAC's XML vocabulary (`panel`, `group`, `label`,
//! `button`, `icon`, `meter`, `tab`, `toggle`, `slider`, `field`, `menu`,
//! `log`, `list` with `column`) read into a [`MarkupDocument`].
//!
//! Every `{Binding}` is checked against the plugin's declared
//! [`BindingSchema`] when the panel is registered, with the messages
//! OpenAC's markup builder gives when a C# property does not resolve, so a
//! plugin finds a broken panel at registration, with or without a window.
//! What a binding means for the drawing (read every frame, silent when
//! unresolved, ...) is kept in the document's sources ([`TextSource`],
//! [`BoolSource`], ...), which a window reads against the panel's pushed
//! values.

use std::fmt;

use roxmltree::Node as XmlNode;

pub use flex_attrs::{FlexContainer, FlexItem};

mod flex_attrs;

use crate::value::{
    BindingKind, BindingSchema, LIGHT_COLOR_MARKER, Rgba, UiValue, bound_color, light_color,
    parse_bool, parse_color, parse_float, parse_hex, parse_int,
};

/// Why markup cannot become a panel.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MarkupError(pub String);

impl fmt::Display for MarkupError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for MarkupError {}

fn err<T>(message: impl Into<String>) -> Result<T, MarkupError> {
    Err(MarkupError(message.into()))
}

/// Which edges of its parent an element keeps its distance to when the
/// parent resizes (bit set).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct Anchors(pub u8);

impl Anchors {
    /// No edge.
    pub const NONE: Self = Self(0);
    /// The left edge.
    pub const LEFT: Self = Self(1);
    /// The top edge.
    pub const TOP: Self = Self(2);
    /// The right edge.
    pub const RIGHT: Self = Self(4);
    /// The bottom edge.
    pub const BOTTOM: Self = Self(8);
    /// The default: left and top.
    pub const TOP_LEFT: Self = Self(1 | 2);

    /// Both sets of edges.
    #[must_use]
    pub const fn union(self, other: Self) -> Self {
        Self(self.0 | other.0)
    }

    /// True when every edge of `other` is set.
    #[must_use]
    pub const fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }
}

/// A rectangle in its parent, pixels.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Rect {
    /// Left.
    pub x: f32,
    /// Top.
    pub y: f32,
    /// Width.
    pub w: f32,
    /// Height.
    pub h: f32,
}

/// A text attribute: absent, literal, or a declared binding read as text.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub enum TextSource {
    /// No attribute.
    #[default]
    None,
    /// The attribute's text (also a `{Name}` the plugin did not declare,
    /// which shows as written).
    Literal(String),
    /// A declared binding; its value as text, nothing while unset.
    Bound(String),
}

impl TextSource {
    /// The text now, given the panel's values.
    #[must_use]
    pub fn read(&self, values: &dyn Values) -> Option<String> {
        match self {
            Self::None => None,
            Self::Literal(text) => Some(text.clone()),
            Self::Bound(name) => values.value(name).map(UiValue::display),
        }
    }
}

/// A `visible` or `enabled` attribute.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub enum BoolSource {
    /// No attribute, or a literal that is not `true`/`false`: the element's
    /// default stays.
    #[default]
    Unset,
    /// `true` or `false`.
    Literal(bool),
    /// A declared `bool` binding: true only while it is `true`.
    Bound(String),
}

impl BoolSource {
    /// The flag now, `default` when unset.
    #[must_use]
    pub fn read(&self, values: &dyn Values, default: bool) -> bool {
        match self {
            Self::Unset => default,
            Self::Literal(v) => *v,
            Self::Bound(name) => matches!(values.value(name), Some(UiValue::Bool(true))),
        }
    }
}

/// A number attribute bound to a value (a meter's fill, a slider's value).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub enum NumberSource {
    /// No attribute or no such binding: reads `missing`.
    #[default]
    Missing,
    /// A declared binding: its value converted, nothing while unset.
    Bound(String),
}

impl NumberSource {
    /// A float: `Some(0.0)` when missing (OpenAC's fill of an unknown
    /// property), `None` while the bound value is unset.
    #[must_use]
    pub fn read_f32(&self, values: &dyn Values) -> Option<f32> {
        match self {
            Self::Missing => Some(0.0),
            Self::Bound(name) => values.value(name).and_then(UiValue::to_f32),
        }
    }

    /// A uint: `None` when missing or unset.
    #[must_use]
    pub fn read_u32(&self, values: &dyn Values) -> Option<u32> {
        match self {
            Self::Missing => None,
            Self::Bound(name) => values.value(name).and_then(UiValue::to_u32),
        }
    }
}

/// A colour attribute.
#[derive(Debug, Clone, PartialEq)]
pub enum ColorSource {
    /// A literal (`#AARRGGBB`, white when unreadable, or the element's
    /// default when the attribute is absent).
    Literal(Rgba),
    /// `#AARRGGBB|light:#AARRGGBB`: the first colour, or the second in a
    /// themed window under a light theme.
    Paired {
        /// Under Classic, every dark theme, and in an unthemed window.
        dark: Rgba,
        /// Under a light theme, in a themed window.
        light: Rgba,
    },
    /// A declared binding read as a colour.
    Bound(String),
    /// `theme:token|#fallback`: the plugin theme's colour on a themed
    /// panel, the fallback otherwise.
    Theme {
        /// The theme colour's name (`text`, `muted`, `field`, `border`,
        /// `accent`, `background`).
        token: String,
        /// The colour when no theme applies.
        classic: Rgba,
    },
}

impl ColorSource {
    /// The colour now (a theme colour reads its classic fallback and a pair
    /// its first colour; the window picks the theme's when one applies).
    #[must_use]
    pub fn read(&self, values: &dyn Values) -> Rgba {
        match self {
            Self::Literal(c) => *c,
            Self::Paired { dark, .. } => *dark,
            Self::Bound(name) => bound_color(values.value(name)),
            Self::Theme { classic, .. } => *classic,
        }
    }
}

/// A `uint` attribute that may be a literal or a binding (an icon id).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UintSource {
    /// A number written in the markup.
    Literal(u32),
    /// A declared binding converted to `uint`; 0 while unset or when it
    /// does not convert.
    Bound(String),
}

impl UintSource {
    /// The number now.
    #[must_use]
    pub fn read(&self, values: &dyn Values) -> u32 {
        match self {
            Self::Literal(v) => *v,
            Self::Bound(name) => values.value(name).and_then(UiValue::to_u32).unwrap_or(0),
        }
    }
}

/// What an icon id names.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum IconKind {
    /// A dat image (a render surface, a bare index gets `0x06000000`).
    #[default]
    Did,
    /// A spell's icon.
    Spell,
    /// A live object's composed icon.
    Item,
    /// A PNG or JPEG the plugin ships, by its path in the plugin's folder
    /// (a string, or a string list for a list's rows).
    File,
}

/// Turns a bare image index into the full render-surface id: below
/// `0x01000000` the image block `0x06000000` is added; 0 stays 0.
#[must_use]
pub const fn normalize_icon(id_or_index: u32) -> u32 {
    if id_or_index == 0 {
        0
    } else if id_or_index < 0x0100_0000 {
        0x0600_0000 + id_or_index
    } else {
        id_or_index
    }
}

/// A file icon's path: written in the markup or a declared string
/// binding, relative to the plugin's install folder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PathSource {
    /// A path written in the markup.
    Literal(String),
    /// A declared string binding.
    Bound(String),
}

impl PathSource {
    /// The path now; none while unset or blank (which draws nothing).
    #[must_use]
    pub fn read(&self, values: &dyn Values) -> Option<String> {
        let path = match self {
            Self::Literal(path) => path.clone(),
            Self::Bound(name) => match values.value(name) {
                Some(UiValue::Text(path)) => path.clone(),
                _ => return None,
            },
        };
        (!path.trim().is_empty()).then_some(path)
    }
}

/// An icon: a dat image, spell or object by its id, or an image file the
/// plugin ships by its path.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IconSource {
    /// An id and what it names.
    Id {
        /// What the id names.
        kind: IconKind,
        /// The id.
        id: UintSource,
    },
    /// A PNG or JPEG in the plugin's folder.
    File(PathSource),
}

impl IconSource {
    /// What it names.
    #[must_use]
    pub fn kind(&self) -> IconKind {
        match self {
            Self::Id { kind, .. } => *kind,
            Self::File(_) => IconKind::File,
        }
    }
}

/// One column of a `list`.
#[derive(Debug, Clone, PartialEq)]
pub struct Column {
    /// Width in pixels (unused when `auto`).
    pub width: f32,
    /// Shares the room the fixed columns leave (`width="*"`; the last
    /// column always does).
    pub auto: bool,
    /// What the column shows.
    pub kind: ColumnKind,
    /// Its header cell.
    pub header: ColumnHeader,
    /// How a header click sorts it.
    pub sort: SortKind,
    /// What it sorts by instead of its cells (`sortkeys`): a float-list
    /// binding (numbers) or a string-list binding (text).
    pub sort_keys: Option<SortKeysSource>,
}

/// A column's header cell.
#[derive(Debug, Clone, PartialEq, Default)]
pub enum ColumnHeader {
    /// No caption.
    #[default]
    None,
    /// A caption (literal or bound text).
    Text(TextSource),
    /// A checkbox column's select-all box (`header="all"`), with its
    /// `onall` (`Action<bool>`).
    SelectAll(Option<String>),
}

/// How a column sorts (`sort`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SortKind {
    /// It does not.
    #[default]
    None,
    /// By text, ignoring case.
    Text,
    /// By number.
    Number,
    /// By its check state.
    Bool,
}

/// A column's `sortkeys` binding.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SortKeysSource {
    /// A float list, compared as numbers.
    Numbers(String),
    /// A string list, compared as the column's sort says.
    Texts(String),
}

/// A column list's header, sort and widths (`header resizable sortcolumn
/// sortdescending onsort widths onresize`).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ListHeader {
    /// The header row is shown.
    pub show: bool,
    /// The bound sort: the column (`int`) and descending (`bool`); none:
    /// the client keeps (and remembers) the sort.
    pub sort: Option<(String, String)>,
    /// A sort asked for (`Action<int, bool>`).
    pub onsort: Option<String>,
    /// The player can drag the column edges.
    pub resizable: bool,
    /// The bound widths (a float list); none: the client keeps (and
    /// remembers) them.
    pub widths: Option<String>,
    /// Every column's width after a drag (`Action<IReadOnlyList<float>>`).
    pub onresize: Option<String>,
}

/// A tree list (`tree="true"`).
#[derive(Debug, Clone, PartialEq)]
pub struct ListTree {
    /// The column the tree is drawn in (the first text column).
    pub column: usize,
    /// Each row's depth (an int list).
    pub levels: String,
    /// Each row's key (a string list).
    pub keys: String,
    /// The expanded rows' keys (a string list); none: the client keeps
    /// (and remembers) the expansion.
    pub expanded: Option<String>,
    /// Rows start folded (`collapsed`, only without `expanded`).
    pub start_collapsed: bool,
    /// An expansion asked for (`Action<string, bool>`).
    pub onexpand: Option<String>,
}

/// Options given as a literal `a|b|c` list or a bound string list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ItemsSource {
    /// Written in the markup.
    Literal(Vec<String>),
    /// A string-list binding.
    Bound(String),
}

impl ItemsSource {
    /// The items now.
    #[must_use]
    pub fn read(&self, values: &dyn Values) -> Vec<String> {
        match self {
            Self::Literal(items) => items.clone(),
            Self::Bound(name) => match values.value(name) {
                Some(UiValue::TextList(items)) => items.clone(),
                _ => Vec::new(),
            },
        }
    }
}

/// An element's right-click menu (`<contextmenu>`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ContextMenu {
    /// The items; `-` is a separator.
    pub items: ItemsSource,
    /// One flag per item (a bool list).
    pub enabled: Option<String>,
    /// The item picked (`Action<int>`, separators counted).
    pub onselect: String,
    /// The menu is about to open (`Action<int>`: the plugin row on a list,
    /// else −1).
    pub onopen: Option<String>,
}

/// What a `<number>` binds: an `int` or a `float` value and an `onchange`
/// of the same type.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NumberKind {
    /// `int`, `Action<int>`.
    Int,
    /// `float`, `Action<float>`.
    Float,
}

/// What a `<color>` binds: a `uint`, `int` or `string` value and an
/// `onchange` of the same type.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ColorValueKind {
    /// `0xAARRGGBB` as `uint`.
    Uint,
    /// `0xAARRGGBB` as `int`.
    Int,
    /// `#AARRGGBB` text.
    Text,
}

/// What a column shows.
#[derive(Debug, Clone, PartialEq)]
pub enum ColumnKind {
    /// Text per row.
    Text {
        /// The rows' text (a string-list binding).
        items: String,
        /// Per-row `0xRRGGBB` colours (a uint-list binding).
        colors: Option<String>,
        /// A row clicked in this column (an `Action<int>` binding); without
        /// one a click selects the row.
        onclick: Option<String>,
    },
    /// A check box per row.
    Check {
        /// The rows' states (a bool-list binding).
        values: String,
        /// A row's box clicked (an `Action<int>` binding).
        onchange: String,
        /// Drawn as tick boxes (`type="checkbox"`) rather than lamps.
        boxed: bool,
    },
    /// An icon per row.
    Icon {
        /// The rows' icon ids (a uint-list binding; for file icons a
        /// string list of paths).
        values: String,
        /// What the ids name.
        kind: IconKind,
        /// A row's icon clicked (an `Action<int>` binding).
        onclick: String,
    },
    /// A button per row, with the row's caption on it; an empty caption
    /// draws no button.
    Button {
        /// The rows' captions (a string-list binding).
        items: String,
        /// A row's button clicked (an `Action<int>` binding); the click
        /// does not select the row.
        onclick: String,
        /// The button's width inside its cell, pixels; 0 fills the cell.
        /// Never larger than the cell.
        button_width: f32,
        /// The button's height inside its cell, pixels; 0 fills the cell.
        button_height: f32,
    },
}

/// A list's rows: one column of text (with optional colours and icons), or
/// columns.
#[derive(Debug, Clone, PartialEq)]
pub enum ListBody {
    /// The single-column form.
    Single {
        /// The rows (a string-list binding).
        items: String,
        /// Per-row colours (a uint-list binding).
        colors: Option<String>,
        /// Per-row icons (a uint-list binding; for file icons a string list
        /// of paths) and their kind.
        icons: Option<(String, IconKind)>,
    },
    /// `<column>` children.
    Columns(Vec<Column>),
}

/// An element and its own attributes.
#[derive(Debug, Clone, PartialEq)]
pub enum Element {
    /// A container.
    Group {
        /// Fill (transparent when absent).
        background: Option<ColorSource>,
        /// One-pixel border (none when absent).
        border: Option<ColorSource>,
        /// `layout`: it places its children with flex.
        layout: Option<FlexContainer>,
        /// `scroll` includes x.
        scroll_x: bool,
        /// `scroll` includes y.
        scroll_y: bool,
    },
    /// A bar filled to a share.
    Meter {
        /// The share, 0..=1.
        fill: NumberSource,
        /// The current amount (the caption is `cur/max`).
        cur: NumberSource,
        /// The maximum.
        max: NumberSource,
        /// The bar colour.
        color: ColorSource,
        /// Sprite ids: back left, back tile, back right, front left, front
        /// tile, front right (0: none).
        sprites: [u32; 6],
    },
    /// Text.
    Label {
        /// The text.
        text: TextSource,
        /// The colour (`None`: the label's default).
        color: Option<ColorSource>,
    },
    /// A push button.
    Button {
        /// The caption.
        text: TextSource,
        /// The click handler.
        onclick: Option<String>,
        /// Caption colour.
        color: Option<ColorSource>,
        /// Fill.
        background: Option<ColorSource>,
        /// Border.
        border: Option<ColorSource>,
        /// An icon drawn on it.
        icon: Option<IconSource>,
    },
    /// An image.
    Icon {
        /// What it shows.
        icon: IconSource,
    },
    /// A tab header.
    Tab {
        /// The caption (literal).
        text: String,
        /// Whether it is the selected tab (a bool binding).
        selected: String,
        /// The click handler.
        onclick: Option<String>,
        /// An icon drawn on it (dimmed while the tab is not selected).
        icon: Option<IconSource>,
    },
    /// A check box with a caption.
    Toggle {
        /// The caption.
        text: TextSource,
        /// Whether it is checked (a bool binding).
        checked: String,
        /// The click handler.
        onclick: Option<String>,
        /// Caption colour.
        color: Option<ColorSource>,
    },
    /// A value slider.
    Slider {
        /// The value at the left end.
        min: f32,
        /// The value at the right end.
        max: f32,
        /// The value shown.
        value: NumberSource,
        /// The value-changed handler (`Action<float>`).
        onchange: Option<String>,
        /// Retail scrollbar art (`style="retail"`) instead of the plain look.
        retail_art: bool,
    },
    /// A text entry.
    Field {
        /// The text shown while nobody types in it.
        text: TextSource,
        /// Every change typed (`Action<string>`).
        onchange: Option<String>,
        /// Enter (`Action<string>`).
        onsubmit: Option<String>,
        /// Keyboard focus lost, with the text then (`Action<string>`): a
        /// click elsewhere, Escape, Tab, the panel hiding or closing, or an
        /// Enter that `onsubmit` (or `clearonsubmit`) did not already take.
        onblur: Option<String>,
        /// The up arrow (`Action`).
        onup: Option<String>,
        /// The down arrow (`Action`).
        ondown: Option<String>,
        /// One line (default) or wrapped lines.
        one_line: bool,
        /// Typing allowed (default).
        editable: bool,
        /// Fill.
        background: ColorSource,
        /// Text colour.
        color: ColorSource,
        /// The background attribute was absent (a theme may restyle it).
        default_background: bool,
        /// The color attribute was absent.
        default_color: bool,
        /// The most characters.
        max_length: i32,
        /// Empty after Enter.
        clear_on_submit: bool,
    },
    /// A drop-down menu.
    Menu {
        /// The entries (a string-list binding).
        items: String,
        /// The entry shown on the button.
        selected: TextSource,
        /// An entry picked (`Action<string>`).
        onchange: Option<String>,
        /// Typing filters the entries.
        searchable: bool,
        /// Rows per popup column.
        rows: i32,
        /// Popup row height.
        row_height: f32,
        /// The popup opens above the button.
        open_upward: bool,
        /// Retail button art (`style="retail"`).
        retail_art: bool,
    },
    /// A wrapped transcript.
    Log {
        /// The entries (a string-list binding).
        items: String,
        /// The number of the first entry (an int binding), for keeping the
        /// reader's place when old entries drop off.
        first_index: Option<String>,
    },
    /// A list of rows.
    List {
        /// The selected row (an int binding; -1 none), or with `multi` the
        /// selected plugin rows (an int-list binding).
        selected: String,
        /// A row selected (`Action<int>`), or with `multi` the new
        /// selection (`Action<IReadOnlyList<int>>`).
        onchange: Option<String>,
        /// Row height.
        row_height: f32,
        /// Draw a band behind the selected row (always with `multi`).
        selection_band: bool,
        /// `selectmode="multi"`: click, Ctrl and Shift pick several rows.
        multi: bool,
        /// The rows.
        body: ListBody,
        /// A column list's header, sort and widths.
        header: ListHeader,
        /// A tree list's attributes.
        tree: Option<ListTree>,
    },
    /// A tick box with a caption, optionally indeterminate.
    Checkbox {
        /// The caption.
        text: TextSource,
        /// Its state: a `bool`, or an optional bool whose none draws the box
        /// indeterminate.
        checked: String,
        /// The value a click asks for (`Action<bool>`).
        onchange: Option<String>,
        /// Caption colour.
        color: Option<ColorSource>,
    },
    /// A number field with spin buttons.
    Number {
        /// The value (`int` or `float`).
        value: String,
        /// A new value (`Action<int>` or `Action<float>`).
        onchange: String,
        /// Which of the two it binds.
        kind: NumberKind,
        /// Range, step and decimals.
        spec: crate::number::NumberSpec,
        /// Fill.
        background: ColorSource,
        /// Text colour.
        color: ColorSource,
        /// The background attribute was absent.
        default_background: bool,
        /// The color attribute was absent.
        default_color: bool,
    },
    /// One choice of several.
    Radio {
        /// The options.
        items: ItemsSource,
        /// The chosen option (an int binding; −1 none).
        selected: String,
        /// The option picked (`Action<int>`).
        onchange: Option<String>,
        /// Options side by side rather than stacked.
        horizontal: bool,
        /// A joined bar of buttons rather than round buttons.
        segmented: bool,
        /// Caption colour.
        color: Option<ColorSource>,
    },
    /// A 1 px line.
    Separator {
        /// Its colour (`None`: the default).
        color: Option<ColorSource>,
    },
    /// A header that folds away the content below it.
    Expander {
        /// The header's caption.
        text: TextSource,
        /// The bound state (`bool`); none: the client keeps (and remembers)
        /// it.
        expanded: Option<String>,
        /// A new state (`Action<bool>`).
        onchange: Option<String>,
        /// Open at first (`open`, default true).
        open: bool,
        /// Header caption colour.
        color: Option<ColorSource>,
        /// Framed like a group box (`border`, default true).
        framed: bool,
    },
    /// Two panes with a bar between them the player drags.
    Split {
        /// Side by side (`horizontal`, the default) or stacked.
        horizontal: bool,
        /// The first pane's least size.
        min: f32,
        /// Its largest size (none: all the room).
        max: Option<f32>,
        /// Where the bar starts (`at`, else half the room left after the
        /// bar).
        default_position: f32,
        /// `at` was given.
        at_given: bool,
        /// The bound position (`float`); none: the client keeps (and
        /// remembers) it.
        position: Option<String>,
        /// A position asked for (`Action<float>`).
        onchange: Option<String>,
    },
    /// A box that records a key chord.
    Hotkey {
        /// The chord as text (a string binding).
        value: String,
        /// A new chord (`Action<string>`).
        onchange: String,
    },
    /// A colour swatch that opens a picker.
    Color {
        /// The colour.
        value: String,
        /// What the value is.
        kind: ColorValueKind,
        /// A new colour, of the same type.
        onchange: String,
        /// The picker has an alpha strip.
        alpha: bool,
    },
    /// Where a `<repeat>` stands in its flex container: its children are
    /// the template the window builds once per item of the list, as flex
    /// items of that container in list order. It takes no space itself.
    Repeat {
        /// The list (an items binding of the enclosing scope).
        items: String,
        /// The item name each row is kept by (a value of the item type).
        key: Option<String>,
    },
}

impl Element {
    /// The markup element's name.
    #[must_use]
    pub const fn tag(&self) -> &'static str {
        match self {
            Self::Group { .. } => "group",
            Self::Meter { .. } => "meter",
            Self::Label { .. } => "label",
            Self::Button { .. } => "button",
            Self::Icon { .. } => "icon",
            Self::Tab { .. } => "tab",
            Self::Toggle { .. } => "toggle",
            Self::Slider { .. } => "slider",
            Self::Field { .. } => "field",
            Self::Menu { .. } => "menu",
            Self::Log { .. } => "log",
            Self::List { .. } => "list",
            Self::Checkbox { .. } => "checkbox",
            Self::Number { .. } => "number",
            Self::Radio { .. } => "radio",
            Self::Separator { .. } => "separator",
            Self::Expander { .. } => "expander",
            Self::Split { .. } => "split",
            Self::Hotkey { .. } => "hotkey",
            Self::Color { .. } => "color",
            Self::Repeat { .. } => "repeat",
        }
    }

    /// Whether `SetControlLabel` reaches it (buttons, tabs, toggles,
    /// checkboxes, expanders and labels carry text).
    #[must_use]
    pub const fn carries_text(&self) -> bool {
        matches!(
            self,
            Self::Button { .. }
                | Self::Tab { .. }
                | Self::Toggle { .. }
                | Self::Label { .. }
                | Self::Checkbox { .. }
                | Self::Expander { .. }
        )
    }
}

/// What every element has.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Common {
    /// Its control name (`name`, else `id`).
    pub name: Option<String>,
    /// The parent edges it follows.
    pub anchors: Anchors,
    /// Shown.
    pub visible: BoolSource,
    /// Takes input.
    pub enabled: BoolSource,
    /// Tooltip text.
    pub tooltip: Option<TextSource>,
    /// The `color` attribute was absent (a theme may restyle it).
    pub default_color: bool,
    /// The `background` attribute was absent.
    pub default_background: bool,
    /// The `border` attribute was absent.
    pub default_border: bool,
    /// Its right-click menu.
    pub context_menu: Option<ContextMenu>,
}

/// One element in the document.
#[derive(Debug, Clone, PartialEq)]
pub struct Node {
    /// What it is.
    pub element: Element,
    /// Its common attributes.
    pub common: Common,
    /// Where it sits in its parent.
    pub rect: Rect,
    /// Its parent (`None`: the panel).
    pub parent: Option<usize>,
    /// Its children (groups, expanders and splits), in order.
    pub children: Vec<usize>,
    /// Its flex item attributes, when its parent is a flex container.
    pub flex: Option<FlexItem>,
    /// The innermost `<repeat>` whose template it is in (`None`: the
    /// window's own element). Its bindings resolve on that repeat's item
    /// type, and the window builds it once per item.
    pub repeat: Option<usize>,
}

/// The `<panel>` root's own attributes.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PanelSpec {
    /// Where it opens, pixels.
    pub rect: Rect,
    /// Resizable from its border.
    pub resizable: bool,
    /// Smallest width (default: its width).
    pub min_width: f32,
    /// Smallest height (default: its height).
    pub min_height: f32,
    /// Width may change.
    pub resize_x: bool,
    /// Height may change.
    pub resize_y: bool,
    /// A bool binding the window is shown only while true.
    pub visible: Option<String>,
    /// The caption drawn at (8, 4).
    pub title: Option<String>,
    /// `theme="plugin"`: follows the player's plugin theme.
    pub themed: bool,
    /// `layout`: the content area is a flex container.
    pub layout: Option<FlexContainer>,
    /// The host title bar (`titlebar`, else on exactly with `layout`).
    pub title_bar: bool,
    /// `scroll` includes x (only with a content area).
    pub scroll_x: bool,
    /// `scroll` includes y (only with a content area).
    pub scroll_y: bool,
    /// A layout root's authored content size (`w`, `h`); `None` leaves it
    /// to the content.
    pub authored_size: (Option<f32>, Option<f32>),
    /// A layout root's authored content minimums (`minw`, `minh`).
    pub authored_min: (Option<f32>, Option<f32>),
    /// The revision the window's saved geometry is kept under: from the
    /// authored geometry attributes for a window with a content area, 0
    /// otherwise.
    pub geometry_revision: i32,
}

impl PanelSpec {
    /// A window with chrome or a flex layout has a content area: its
    /// authored sizes are the area inside the border (and title bar).
    #[must_use]
    pub fn has_content_area(&self) -> bool {
        self.layout.is_some() || self.title_bar
    }
}

/// A parsed, checked panel.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct MarkupDocument {
    /// The root's attributes.
    pub panel: PanelSpec,
    /// Every element, parents before children (document order).
    pub nodes: Vec<Node>,
    /// The root's children.
    pub roots: Vec<usize>,
    /// What the build ignored, one line each: flex attributes on markup
    /// that does not use flex, which older builds ignored silently.
    pub warnings: Vec<String>,
}

impl MarkupDocument {
    /// The first element (document order) named `name`, outside every
    /// `<repeat>`: a name in a template is on every row, so it names no one
    /// control.
    #[must_use]
    pub fn find(&self, name: &str) -> Option<usize> {
        self.nodes
            .iter()
            .position(|n| n.repeat.is_none() && n.common.name.as_deref() == Some(name))
    }

    /// Whether `node` is in the template of `repeat`, at any depth.
    #[must_use]
    pub fn is_within(&self, node: usize, repeat: usize) -> bool {
        let mut around = self.nodes.get(node).and_then(|n| n.repeat);
        while let Some(r) = around {
            if r == repeat {
                return true;
            }
            around = self.nodes[r].repeat;
        }
        false
    }

    /// The remembered names (`name`, else `id`) in `repeat`'s template,
    /// nested templates included.
    #[must_use]
    pub fn template_names(&self, repeat: usize) -> std::collections::BTreeSet<String> {
        self.nodes
            .iter()
            .enumerate()
            .filter(|(i, _)| self.is_within(*i, repeat))
            .filter_map(|(_, n)| n.common.name.clone())
            .collect()
    }
}

/// The panel's values, as the document's sources read them.
pub trait Values {
    /// The value pushed for `name`, if any.
    fn value(&self, name: &str) -> Option<&UiValue>;
}

impl Values for std::collections::BTreeMap<String, UiValue> {
    fn value(&self, name: &str) -> Option<&UiValue> {
        self.get(name)
    }
}

/// No values (reads every binding as unset).
#[derive(Debug, Clone, Copy, Default)]
pub struct NoValues;

impl Values for NoValues {
    fn value(&self, _name: &str) -> Option<&UiValue> {
        None
    }
}

/// An `<expander>`'s header height, pixels: its `h` must be at least this.
pub const EXPANDER_HEADER_HEIGHT: f32 = 18.0;

/// A `<split>`'s bar thickness, pixels.
pub const SPLIT_BAR_SIZE: f32 = 5.0;

/// The plugin theme's colour names a `theme:` colour may use.
pub const THEME_TOKENS: [&str; 6] = ["text", "muted", "field", "border", "accent", "background"];

/// What separates an anchor attribute's edge names.
const ANCHOR_SEPARATORS: [char; 5] = [',', ' ', '\t', '\r', '\n'];

/// Reads `xml` as a panel whose bindings are `schema`.
///
/// # Errors
/// Malformed XML, an unknown element, a malformed attribute, or a binding
/// that does not resolve where the markup requires it to, with OpenAC's
/// message for each.
pub fn parse(xml: &str, schema: &BindingSchema) -> Result<MarkupDocument, MarkupError> {
    let document = roxmltree::Document::parse(xml).map_err(|e| MarkupError(e.to_string()))?;
    let root = document.root_element();
    let name = root.tag_name().name();
    if name != "panel" {
        return err(format!("root must be <panel>, got <{name}>"));
    }
    if root
        .children()
        .any(|c| c.is_element() && c.tag_name().name() == "contextmenu")
    {
        return err("<contextmenu> goes inside the element it belongs to, not directly in <panel>");
    }
    let mut parser = Parser {
        schema,
        doc: MarkupDocument::default(),
        repeats: Vec::new(),
    };
    let resizable = b(root, "resizable", false);
    // A window with chrome or a flex layout has a content area: its
    // authored sizes are the area inside the border (and title bar), and
    // the frame adds the chrome. A root with layout gets the title bar by
    // default.
    let mut warnings = Vec::new();
    let has_layout = flex_attrs::is_container(root, &mut warnings);
    let title_bar = match attr(root, "titlebar") {
        None => has_layout,
        Some("false") => false,
        Some("true") => true,
        Some(other) => {
            warnings.push(flex_attrs::ignored(
                root,
                "titlebar",
                other,
                "must be true or false",
            ));
            has_layout
        }
    };
    let content_area = has_layout || title_bar;
    let (mut scroll_x, mut scroll_y) = flex_attrs::scroll(root, &mut warnings);
    if (scroll_x || scroll_y) && !content_area {
        warnings.push(flex_attrs::ignored(
            root,
            "scroll",
            attr(root, "scroll").unwrap_or_default(),
            "scrolls the window's content area, which needs layout=\"row|column\" or titlebar=\"true\" on the panel",
        ));
        (scroll_x, scroll_y) = (false, false);
    }
    let chrome_w = if content_area {
        crate::chrome::HORIZONTAL_INSETS
    } else {
        0.0
    };
    let chrome_h = if content_area {
        crate::chrome::vertical_insets_for(title_bar)
    } else {
        0.0
    };
    if !has_layout {
        flex_attrs::reject_container(root, &mut warnings);
    }
    let layout = if has_layout {
        Some(flex_attrs::read_container(root)?)
    } else {
        None
    };
    // A layout root may leave its size to its content; it is measured once
    // its children exist (by the window), so these are provisional for it.
    let (authored_w, authored_h, authored_min_w, authored_min_h) = if has_layout {
        (
            flex_attrs::root_size(root, "w")?,
            flex_attrs::root_size(root, "h")?,
            flex_attrs::root_size(root, "minw")?,
            flex_attrs::root_size(root, "minh")?,
        )
    } else {
        (Some(f(root, "w")), Some(f(root, "h")), None, None)
    };
    let content_w = authored_w.unwrap_or(0.0);
    let content_h = authored_h.unwrap_or(0.0);
    let rect = Rect {
        x: f(root, "x"),
        y: f(root, "y"),
        w: content_w + chrome_w,
        h: content_h + chrome_h,
    };
    let min_width = if content_area {
        crate::chrome::frame_minimum_width(f_or(root, "minw", content_w), title_bar)
    } else {
        f_or(root, "minw", content_w)
    };
    let mut panel = PanelSpec {
        rect,
        resizable,
        min_width,
        min_height: f_or(root, "minh", content_h) + chrome_h,
        resize_x: resizable,
        resize_y: resizable,
        themed: attr(root, "theme") == Some("plugin"),
        layout,
        title_bar,
        scroll_x,
        scroll_y,
        authored_size: if has_layout {
            (authored_w, authored_h)
        } else {
            (None, None)
        },
        authored_min: (authored_min_w, authored_min_h),
        geometry_revision: if content_area {
            crate::chrome::authored_geometry_revision(&crate::chrome::authored_inputs(|n| {
                attr(root, n)
            }))
        } else {
            0
        },
        ..PanelSpec::default()
    };
    if let Some(resize) = attr(root, "resize") {
        panel.resize_x = matches!(resize, "x" | "both");
        panel.resize_y = matches!(resize, "y" | "both");
    }
    if let Some(visible) = attr(root, "visible")
        && is_binding(visible)
    {
        let name = inner(visible);
        if schema.kind(name) != Some(BindingKind::Bool) {
            return err(format!(
                "<panel visible=\"{visible}\"> did not resolve to a bool property on {}",
                schema.type_name
            ));
        }
        panel.visible = Some(name.to_owned());
    }
    panel.title = attr(root, "title")
        .filter(|t| !t.is_empty())
        .map(str::to_owned);
    parser.doc.panel = panel;
    parser.doc.warnings = warnings;
    for child in root.children().filter(XmlNode::is_element) {
        let index = parser.element(child, None)?;
        parser.doc.roots.push(index);
    }
    Ok(parser.doc)
}

struct Parser<'a> {
    schema: &'a BindingSchema,
    doc: MarkupDocument,
    /// The `<repeat>`s around the element being read, outermost first.
    repeats: Vec<usize>,
}

fn attr<'a>(el: XmlNode<'a, '_>, name: &str) -> Option<&'a str> {
    el.attribute(name)
}

fn f(el: XmlNode<'_, '_>, name: &str) -> f32 {
    attr(el, name).and_then(parse_float).unwrap_or(0.0)
}

fn f_or(el: XmlNode<'_, '_>, name: &str, fallback: f32) -> f32 {
    attr(el, name).and_then(parse_float).unwrap_or(fallback)
}

fn i(el: XmlNode<'_, '_>, name: &str, fallback: i32) -> i32 {
    attr(el, name).and_then(parse_int).unwrap_or(fallback)
}

fn b(el: XmlNode<'_, '_>, name: &str, fallback: bool) -> bool {
    attr(el, name).and_then(parse_bool).unwrap_or(fallback)
}

fn rect(el: XmlNode<'_, '_>) -> Rect {
    Rect {
        x: f(el, "x"),
        y: f(el, "y"),
        w: f(el, "w"),
        h: f(el, "h"),
    }
}

/// `{Name}`: longer than two characters, braced.
#[must_use]
pub fn is_binding(value: &str) -> bool {
    value.len() > 2 && value.starts_with('{') && value.ends_with('}')
}

fn inner(binding: &str) -> &str {
    &binding[1..binding.len() - 1]
}

/// `.NET` formatting of a float in a message.
fn num(v: f32) -> String {
    crate::value::net_float(v)
}

fn element_identity(el: XmlNode<'_, '_>) -> String {
    let tag = el.tag_name().name();
    match attr(el, "name").or_else(|| attr(el, "id")) {
        Some(name) => format!("<{tag} name=\"{name}\">"),
        None => format!("<{tag}>"),
    }
}

fn validate_icon_kind(kind: Option<&str>, context: &str) -> Result<IconKind, MarkupError> {
    match kind.unwrap_or("did") {
        "did" => Ok(IconKind::Did),
        "spell" => Ok(IconKind::Spell),
        "item" => Ok(IconKind::Item),
        "file" => Ok(IconKind::File),
        other => err(format!(
            "{context} must be did, spell, item, or file (got \"{other}\")"
        )),
    }
}

fn validate_art_style(element: &str, style: Option<&str>) -> Result<bool, MarkupError> {
    match style {
        None | Some("plain") => Ok(false),
        Some("retail") => Ok(true),
        Some(other) => err(format!(
            "<{element} style=\"{other}\"> must be plain or retail"
        )),
    }
}

fn parse_uint_literal(text: &str, context: &str) -> Result<u32, MarkupError> {
    let trimmed = text.trim();
    let parsed = if let Some(hex) = trimmed
        .strip_prefix("0x")
        .or_else(|| trimmed.strip_prefix("0X"))
    {
        u32::from_str_radix(hex, 16).ok()
    } else {
        trimmed.parse::<u32>().ok()
    };
    parsed.ok_or_else(|| MarkupError(format!("{context}=\"{text}\" is not a valid uint literal")))
}

impl<'a> Parser<'a> {
    fn kind(&self, name: &str) -> Option<BindingKind> {
        self.schema.kind(name)
    }

    fn type_name(&self) -> &str {
        &self.schema.type_name
    }

    /// OpenAC `BindString`: absent → none; literal or undeclared → the text
    /// as written; declared → bound.
    fn text(&self, attribute: Option<&str>) -> TextSource {
        match attribute {
            None => TextSource::None,
            Some(a) if is_binding(a) && self.kind(inner(a)).is_some() => {
                TextSource::Bound(inner(a).to_owned())
            }
            Some(a) => TextSource::Literal(a.to_owned()),
        }
    }

    /// A handler attribute: `Ok(None)` when absent; the name when it
    /// resolves to `kind`; OpenAC's error otherwise.
    fn handler(
        &self,
        attribute: Option<&str>,
        kind: BindingKind,
        message: impl FnOnce(&str) -> String,
    ) -> Result<Option<String>, MarkupError> {
        let Some(a) = attribute else {
            return Ok(None);
        };
        if is_binding(a) && self.kind(inner(a)) == Some(kind) {
            return Ok(Some(inner(a).to_owned()));
        }
        err(message(a))
    }

    /// A button-like control's optional `icon`/`iconkind` pair, both
    /// checked at build time.
    fn button_icon(
        &self,
        el: XmlNode<'_, '_>,
        context: &str,
    ) -> Result<Option<IconSource>, MarkupError> {
        let Some(icon) = attr(el, "icon") else {
            return Ok(None);
        };
        let kind = validate_icon_kind(attr(el, "iconkind"), "iconkind")?;
        if kind == IconKind::File {
            return self
                .path_literal_or_binding(icon, context)
                .map(|path| Some(IconSource::File(path)));
        }
        let id = self.uint_literal_or_binding(icon, context)?;
        Ok(Some(IconSource::Id { kind, id }))
    }

    fn action_message(
        &self,
        element: &str,
        attribute: &str,
        kind: &str,
    ) -> impl FnOnce(&str) -> String {
        let type_name = self.type_name().to_owned();
        let element = element.to_owned();
        let attribute = attribute.to_owned();
        let kind = kind.to_owned();
        move |value| {
            format!(
                "<{element} {attribute}=\"{value}\"> did not resolve to an {kind} property on {type_name}"
            )
        }
    }

    /// OpenAC `BindColorSource` (and its theme branch): `None` when the
    /// attribute is absent.
    fn color(&self, attribute: Option<&str>) -> Result<Option<ColorSource>, MarkupError> {
        let Some(a) = attribute else {
            return Ok(None);
        };
        if let Some(rest) = a.strip_prefix("theme:") {
            let token = rest.split('|').next().unwrap_or("");
            if !THEME_TOKENS.contains(&token) {
                return err(format!("Unknown plugin theme color '{token}'."));
            }
            return Ok(Some(ColorSource::Theme {
                token: token.to_owned(),
                classic: parse_color(Some(a)),
            }));
        }
        if let Some(light) = light_color(a) {
            // A pair is two literals: a binding in either would be dropped.
            if a.split(LIGHT_COLOR_MARKER)
                .any(|part| is_binding(part.trim()))
            {
                return err(format!(
                    "The colour '{a}' binds a value, so it cannot name a colour for light themes."
                ));
            }
            return Ok(Some(ColorSource::Paired {
                dark: parse_color(Some(a)),
                light,
            }));
        }
        if is_binding(a) && self.kind(inner(a)).is_some() {
            return Ok(Some(ColorSource::Bound(inner(a).to_owned())));
        }
        Ok(Some(ColorSource::Literal(parse_color(Some(a)))))
    }

    /// `BindBool` (visible, enabled).
    fn bool_attr(&self, attribute: Option<&str>) -> Result<BoolSource, MarkupError> {
        let Some(a) = attribute.filter(|a| !a.trim().is_empty()) else {
            return Ok(BoolSource::Unset);
        };
        if !is_binding(a) {
            return Ok(parse_bool(a).map_or(BoolSource::Unset, BoolSource::Literal));
        }
        if self.kind(inner(a)) != Some(BindingKind::Bool) {
            return err(format!(
                "{a} did not resolve to a bool property on {}",
                self.type_name()
            ));
        }
        Ok(BoolSource::Bound(inner(a).to_owned()))
    }

    /// `BindRequiredBoolReader`.
    fn required_bool(&self, attribute: Option<&str>, context: &str) -> Result<String, MarkupError> {
        let Some(a) = attribute.filter(|a| !a.trim().is_empty() && is_binding(a)) else {
            return err(format!("{context} must be a bool binding"));
        };
        if self.kind(inner(a)) != Some(BindingKind::Bool) {
            return err(format!(
                "{a} did not resolve to a bool property on {}",
                self.type_name()
            ));
        }
        Ok(inner(a).to_owned())
    }

    /// `BindRequiredIntReader`.
    fn required_int(&self, attribute: Option<&str>, context: &str) -> Result<String, MarkupError> {
        let Some(a) = attribute.filter(|a| !a.trim().is_empty() && is_binding(a)) else {
            return err(format!("{context} must be an int binding"));
        };
        if self.kind(inner(a)) != Some(BindingKind::Int) {
            return err(format!(
                "{a} did not resolve to an int property on {}",
                self.type_name()
            ));
        }
        Ok(inner(a).to_owned())
    }

    /// `BindStringList`.
    fn string_list(&self, attribute: Option<&str>, context: &str) -> Result<String, MarkupError> {
        let Some(a) = attribute.filter(|a| !a.trim().is_empty() && is_binding(a)) else {
            return err(format!("{context} must be a string-list binding"));
        };
        if self.kind(inner(a)) != Some(BindingKind::TextList) {
            return err(format!(
                "{a} did not resolve to an IEnumerable<string> property on {}",
                self.type_name()
            ));
        }
        Ok(inner(a).to_owned())
    }

    /// `BindUintList`: `None` when absent or blank.
    fn uint_list(
        &self,
        attribute: Option<&str>,
        context: &str,
    ) -> Result<Option<String>, MarkupError> {
        let Some(a) = attribute.filter(|a| !a.trim().is_empty()) else {
            return Ok(None);
        };
        if !is_binding(a) {
            return err(format!("{context} must be a uint-list binding"));
        }
        match self.kind(inner(a)) {
            Some(BindingKind::UintList | BindingKind::IntList) => Ok(Some(inner(a).to_owned())),
            _ => err(format!(
                "{a} did not resolve to an IEnumerable<uint> or IEnumerable<int> property on {}",
                self.type_name()
            )),
        }
    }

    /// `BindBoolList`.
    fn bool_list(&self, attribute: Option<&str>, context: &str) -> Result<String, MarkupError> {
        let Some(a) = attribute.filter(|a| !a.trim().is_empty() && is_binding(a)) else {
            return err(format!("{context} must be a bool-list binding"));
        };
        if self.kind(inner(a)) != Some(BindingKind::BoolList) {
            return err(format!(
                "{a} did not resolve to an IEnumerable<bool> property on {} ({context})",
                self.type_name()
            ));
        }
        Ok(inner(a).to_owned())
    }

    /// `BindRequiredIntAction`.
    fn required_int_action(
        &self,
        attribute: Option<&str>,
        context: &str,
    ) -> Result<String, MarkupError> {
        let Some(a) = attribute.filter(|a| is_binding(a)) else {
            return err(format!("{context} must be an Action<int> binding"));
        };
        if self.kind(inner(a)) != Some(BindingKind::IntAction) {
            return err(format!(
                "{a} did not resolve to an Action<int> property on {} ({context})",
                self.type_name()
            ));
        }
        Ok(inner(a).to_owned())
    }

    /// A file path written literally or bound to a string property. Like
    /// every other binding, a missing property or one of another type is
    /// refused at build; a blank path at draw time draws nothing.
    fn path_literal_or_binding(
        &self,
        expression: &str,
        context: &str,
    ) -> Result<PathSource, MarkupError> {
        if !is_binding(expression) {
            return Ok(PathSource::Literal(expression.to_owned()));
        }
        if self.kind(inner(expression)) != Some(BindingKind::Text) {
            return err(format!(
                "{expression} did not resolve to a string property on {} ({context})",
                self.type_name()
            ));
        }
        Ok(PathSource::Bound(inner(expression).to_owned()))
    }

    /// `BindUintLiteralOrBinding`.
    fn uint_literal_or_binding(
        &self,
        expression: &str,
        context: &str,
    ) -> Result<UintSource, MarkupError> {
        if !is_binding(expression) {
            return parse_uint_literal(expression, context).map(UintSource::Literal);
        }
        if self.kind(inner(expression)).is_none() {
            return err(format!(
                "{expression} did not resolve to a property on {} ({context})",
                self.type_name()
            ));
        }
        Ok(UintSource::Bound(inner(expression).to_owned()))
    }

    /// `BindFloat` / `BindUint`: a declared binding, else missing.
    fn number(&self, attribute: Option<&str>) -> NumberSource {
        match attribute {
            Some(a) if is_binding(a) && self.kind(inner(a)).is_some() => {
                NumberSource::Bound(inner(a).to_owned())
            }
            _ => NumberSource::Missing,
        }
    }

    // ── The newer elements' binding helpers (OpenAC `MarkupBindings`):
    // `required_*` fail when the attribute is absent or mistyped; the others
    // are `None` when absent and fail when present but mistyped. `context`
    // is "<element> <attribute>".

    /// A required binding of one of `kinds`; `what` names them in the
    /// messages ("a bool or bool?", "a float", ...), `binding` is what the
    /// absent attribute must be ("bool or bool? binding").
    fn required_of(
        &self,
        attribute: Option<&str>,
        context: &str,
        kinds: &[BindingKind],
        binding: &str,
        property: &str,
    ) -> Result<String, MarkupError> {
        let Some(a) = attribute.filter(|a| !a.trim().is_empty() && is_binding(a)) else {
            return err(format!("{context} must be {binding}"));
        };
        match self.kind(inner(a)) {
            Some(k) if kinds.contains(&k) => Ok(inner(a).to_owned()),
            _ => err(format!(
                "{a} did not resolve to {property} on {} ({context})",
                self.type_name()
            )),
        }
    }

    /// An optional `Action<T>`.
    fn optional_action(
        &self,
        attribute: Option<&str>,
        context: &str,
        kind: BindingKind,
        type_name: &str,
    ) -> Result<Option<String>, MarkupError> {
        let Some(a) = attribute else {
            return Ok(None);
        };
        if !is_binding(a) {
            return err(format!("{context} must be an Action<{type_name}> binding"));
        }
        if self.kind(inner(a)) != Some(kind) {
            return err(format!(
                "{a} did not resolve to an Action<{type_name}> property on {} ({context})",
                self.type_name()
            ));
        }
        Ok(Some(inner(a).to_owned()))
    }

    /// A required `Action<T>`.
    fn required_action(
        &self,
        attribute: Option<&str>,
        context: &str,
        kind: BindingKind,
        type_name: &str,
    ) -> Result<String, MarkupError> {
        self.optional_action(attribute, context, kind, type_name)?
            .map_or_else(
                || err(format!("{context} must be an Action<{type_name}> binding")),
                Ok,
            )
    }

    /// A literal `a|b|c` list (no empty item) or a string-list binding.
    fn items(
        &self,
        expression: Option<&str>,
        element: &str,
        noun: &str,
    ) -> Result<ItemsSource, MarkupError> {
        let Some(expression) = expression.filter(|e| !e.is_empty()) else {
            return err(format!("{element} items must list at least one {noun}"));
        };
        if is_binding(expression) {
            return self
                .string_list(Some(expression), &format!("{element} items"))
                .map(ItemsSource::Bound);
        }
        let items: Vec<String> = expression.split('|').map(str::to_owned).collect();
        if items.iter().any(String::is_empty) {
            return err(format!("{element} items must not have an empty {noun}"));
        }
        Ok(ItemsSource::Literal(items))
    }

    /// `true` or `false` (absent: `fallback`), anything else refused.
    fn strict_bool(
        el: XmlNode<'_, '_>,
        name: &str,
        fallback: bool,
        message: impl FnOnce(&str) -> String,
    ) -> Result<bool, MarkupError> {
        match attr(el, name) {
            None => Ok(fallback),
            Some("true") => Ok(true),
            Some("false") => Ok(false),
            Some(other) => err(message(other)),
        }
    }

    /// An element's `<contextmenu>` child (OpenAC `MarkupContextMenu.Attach`).
    fn context_menu(&self, owner: XmlNode<'_, '_>) -> Result<Option<ContextMenu>, MarkupError> {
        let menus: Vec<XmlNode<'_, '_>> = owner
            .children()
            .filter(|c| c.is_element() && c.tag_name().name() == "contextmenu")
            .collect();
        let Some(el) = menus.first().copied() else {
            return Ok(None);
        };
        if menus.len() > 1 {
            return err(format!(
                "<{}> may have only one <contextmenu>",
                owner.tag_name().name()
            ));
        }
        if el.children().any(|c| c.is_element()) {
            return err("<contextmenu> takes no children");
        }
        let onopen = self.optional_action(
            attr(el, "onopen"),
            "contextmenu onopen",
            BindingKind::IntAction,
            "int",
        )?;
        let items = self.items(attr(el, "items"), "contextmenu", "item")?;
        let enabled = match attr(el, "enabled") {
            None => None,
            Some(e) => Some(self.bool_list(Some(e), "contextmenu enabled")?),
        };
        let onselect = self.required_int_action(attr(el, "onselect"), "contextmenu onselect")?;
        Ok(Some(ContextMenu {
            items,
            enabled,
            onselect,
            onopen,
        }))
    }

    fn checkbox(&self, el: XmlNode<'_, '_>) -> Result<Element, MarkupError> {
        let text = match attr(el, "text") {
            None => TextSource::Literal(String::new()),
            Some(c) => self.text(Some(c)),
        };
        let checked = self.required_of(
            attr(el, "checked"),
            "checkbox checked",
            &[BindingKind::Bool, BindingKind::OptionalBool],
            "a bool or bool? binding",
            "a bool or bool? property",
        )?;
        let onchange = self.optional_action(
            attr(el, "onchange"),
            "checkbox onchange",
            BindingKind::BoolAction,
            "bool",
        )?;
        Ok(Element::Checkbox {
            text,
            checked,
            onchange,
            color: self.color(attr(el, "color"))?,
        })
    }

    fn number_element(&self, el: XmlNode<'_, '_>) -> Result<Element, MarkupError> {
        use crate::number::{Dec, NumberSpec};
        let value = self.required_of(
            attr(el, "value"),
            "number value",
            &[BindingKind::Int, BindingKind::Float],
            "a number binding",
            "an int or float property",
        )?;
        let kind = if self.kind(&value) == Some(BindingKind::Int) {
            NumberKind::Int
        } else {
            NumberKind::Float
        };
        let (action, type_name) = match kind {
            NumberKind::Int => (BindingKind::IntAction, "Int32"),
            NumberKind::Float => (BindingKind::FloatAction, "Single"),
        };
        let onchange = match attr(el, "onchange") {
            Some(a) if !a.trim().is_empty() && is_binding(a) => {
                if self.kind(inner(a)) != Some(action) {
                    return err(format!(
                        "{a} did not resolve to an Action<{type_name}> property on {} (number onchange)",
                        self.type_name()
                    ));
                }
                inner(a).to_owned()
            }
            _ => {
                return err(format!(
                    "number onchange must be an Action<{type_name}> binding"
                ));
            }
        };
        let integral = kind == NumberKind::Int;
        let (type_min, type_max) = if integral {
            (
                Dec::from_i64(i64::from(i32::MIN)),
                Dec::from_i64(i64::from(i32::MAX)),
            )
        } else {
            (Dec::MIN, Dec::MAX)
        };
        let decimal = |name: &str, fallback: Dec| -> Result<Dec, MarkupError> {
            match attr(el, name) {
                None => Ok(fallback),
                Some(raw) => Dec::parse_float_style(raw).map_or_else(
                    || err(format!("<number {name}=\"{raw}\"> is not a number")),
                    Ok,
                ),
            }
        };
        let min = decimal("min", type_min)?;
        let max = decimal("max", type_max)?;
        let step = decimal("step", Dec::ONE)?;
        let decimals = match attr(el, "decimals") {
            None => {
                if integral {
                    0
                } else {
                    2
                }
            }
            Some(raw) => match parse_int(raw) {
                Some(d) => d,
                None => {
                    return err(format!("<number decimals=\"{raw}\"> is not a whole number"));
                }
            },
        };
        if max < min {
            return err(format!(
                "<number min=\"{min}\" max=\"{max}\"> must have max >= min"
            ));
        }
        if min < type_min || max > type_max {
            return err(format!(
                "<number min=\"{min}\" max=\"{max}\"> must fit a {type_name} value"
            ));
        }
        if step <= Dec::ZERO {
            return err(format!("<number step=\"{step}\"> must be greater than 0"));
        }
        if !(0..=10).contains(&decimals) {
            return err(format!(
                "<number decimals=\"{decimals}\"> must be from 0 to 10"
            ));
        }
        let decimals = decimals.unsigned_abs();
        if step.round(decimals) != step {
            return err(format!(
                "<number step=\"{step}\"> must not have more decimal places than decimals=\"{decimals}\""
            ));
        }
        if integral && decimals != 0 {
            return err(format!(
                "<number decimals=\"{decimals}\"> must be 0 for a {type_name} value"
            ));
        }
        let background = match attr(el, "background") {
            None => ColorSource::Literal(Rgba::new(0.0, 0.0, 0.0, 0.9)),
            Some(_) => self
                .color(attr(el, "background"))?
                .unwrap_or(ColorSource::Literal(Rgba::WHITE)),
        };
        let color = match attr(el, "color") {
            None => ColorSource::Literal(Rgba::new(0.91, 0.87, 0.76, 1.0)),
            Some(_) => self
                .color(attr(el, "color"))?
                .unwrap_or(ColorSource::Literal(Rgba::WHITE)),
        };
        Ok(Element::Number {
            value,
            onchange,
            kind,
            spec: NumberSpec {
                min,
                max,
                step,
                decimals,
            },
            background,
            color,
            default_background: attr(el, "background").is_none(),
            default_color: attr(el, "color").is_none(),
        })
    }

    fn radio(&self, el: XmlNode<'_, '_>) -> Result<Element, MarkupError> {
        let items = self.items(attr(el, "items"), "radio", "option")?;
        let selected = self.required_int(attr(el, "selected"), "radio selected")?;
        let onchange = self.optional_action(
            attr(el, "onchange"),
            "radio onchange",
            BindingKind::IntAction,
            "int",
        )?;
        let horizontal = match attr(el, "orientation") {
            None | Some("vertical") => false,
            Some("horizontal") => true,
            Some(other) => {
                return err(format!(
                    "<radio orientation=\"{other}\"> must be vertical or horizontal"
                ));
            }
        };
        let segmented = match attr(el, "style") {
            None | Some("radio") => false,
            Some("segmented") => true,
            Some(other) => {
                return err(format!(
                    "<radio style=\"{other}\"> must be radio or segmented"
                ));
            }
        };
        Ok(Element::Radio {
            items,
            selected,
            onchange,
            horizontal,
            segmented,
            color: self.color(attr(el, "color"))?,
        })
    }

    fn hotkey(&self, el: XmlNode<'_, '_>) -> Result<Element, MarkupError> {
        let value = self.required_of(
            attr(el, "value"),
            "hotkey value",
            &[BindingKind::Text],
            "a string binding",
            "a string property",
        )?;
        let onchange = self.required_action(
            attr(el, "onchange"),
            "hotkey onchange",
            BindingKind::TextAction,
            "string",
        )?;
        Ok(Element::Hotkey { value, onchange })
    }

    fn color_element(&self, el: XmlNode<'_, '_>) -> Result<Element, MarkupError> {
        let value = self.required_of(
            attr(el, "value"),
            "color value",
            &[BindingKind::Uint, BindingKind::Int, BindingKind::Text],
            "a uint, int or string binding",
            "a uint, int or string property",
        )?;
        let (kind, action, type_name) = match self.kind(&value) {
            Some(BindingKind::Uint) => (ColorValueKind::Uint, BindingKind::UintAction, "uint"),
            Some(BindingKind::Int) => (ColorValueKind::Int, BindingKind::IntAction, "int"),
            _ => (ColorValueKind::Text, BindingKind::TextAction, "string"),
        };
        let onchange =
            self.required_action(attr(el, "onchange"), "color onchange", action, type_name)?;
        let alpha = Self::strict_bool(el, "alpha", false, |other| {
            format!("<color alpha=\"{other}\"> must be true or false")
        })?;
        Ok(Element::Color {
            value,
            kind,
            onchange,
            alpha,
        })
    }

    /// The first group's size in the split's axis when `at` is absent: half
    /// the room the 5 px bar leaves, rounded down.
    fn split_length(el: XmlNode<'_, '_>, name: &str) -> Result<Option<f32>, MarkupError> {
        let Some(text) = attr(el, name) else {
            return Ok(None);
        };
        match parse_float(text) {
            Some(v) if v.is_finite() && v >= 0.0 => Ok(Some(v)),
            _ => err(format!(
                "<split {name}=\"{text}\"> must be a number of pixels, 0 or more"
            )),
        }
    }

    // ── Lists (OpenAC `MarkupListSelect`, `MarkupListHeader`, `MarkupListTree`).

    fn list_bool(el: XmlNode<'_, '_>, name: &str) -> Result<bool, MarkupError> {
        Self::strict_bool(el, name, false, |other| {
            format!("<list {name}=\"{other}\"> must be true or false")
        })
    }

    fn list_header(
        &self,
        el: XmlNode<'_, '_>,
        column_els: &[XmlNode<'_, '_>],
        columns: &mut [Column],
    ) -> Result<ListHeader, MarkupError> {
        let mut header = ListHeader {
            show: Self::list_bool(el, "header")?,
            ..ListHeader::default()
        };
        let sort_column = attr(el, "sortcolumn");
        let sort_descending = attr(el, "sortdescending");
        if sort_column.is_some() != sort_descending.is_some() {
            return err(
                "<list sortcolumn> and <list sortdescending> are bound together or not at all",
            );
        }
        if sort_column.is_some() {
            header.sort = Some((
                self.required_int(sort_column, "list sortcolumn")?,
                self.required_bool(sort_descending, "list sortdescending")?,
            ));
        }
        header.onsort = self.int_bool_action(attr(el, "onsort"), "list onsort")?;
        header.resizable = Self::list_bool(el, "resizable")?;
        if header.resizable && !header.show {
            return err("<list resizable=\"true\"> needs header=\"true\"");
        }
        let widths = attr(el, "widths");
        let onresize = attr(el, "onresize");
        if !header.resizable && (widths.is_some() || onresize.is_some()) {
            return err("<list widths> and <list onresize> need resizable=\"true\"");
        }
        if widths.is_some() {
            header.widths = Some(self.required_of(
                widths,
                "list widths",
                &[BindingKind::FloatList],
                "a float-list binding",
                "an IEnumerable<float> property",
            )?);
        }
        header.onresize = self.optional_action(
            onresize,
            "list onresize",
            BindingKind::FloatListAction,
            "IReadOnlyList<float>",
        )?;
        for (index, (column_el, column)) in column_els.iter().zip(columns.iter_mut()).enumerate() {
            self.column_header(*column_el, column, index)?;
        }
        Ok(header)
    }

    fn int_bool_action(
        &self,
        attribute: Option<&str>,
        context: &str,
    ) -> Result<Option<String>, MarkupError> {
        let Some(a) = attribute else {
            return Ok(None);
        };
        if !is_binding(a) {
            return err(format!("{context} must be an Action<int, bool> binding"));
        }
        if self.kind(inner(a)) != Some(BindingKind::IntBoolAction) {
            return err(format!(
                "{a} did not resolve to an Action<int, bool> property on {} ({context})",
                self.type_name()
            ));
        }
        Ok(Some(inner(a).to_owned()))
    }

    fn column_header(
        &self,
        el: XmlNode<'_, '_>,
        column: &mut Column,
        index: usize,
    ) -> Result<(), MarkupError> {
        let header = attr(el, "header");
        let onall = attr(el, "onall");
        let is_checkbox = matches!(column.kind, ColumnKind::Check { boxed: true, .. });
        let select_all = header == Some("all") && is_checkbox;
        if onall.is_some() && !select_all {
            return err(format!(
                "column[{index}] onall needs header=\"all\" on a checkbox column"
            ));
        }
        if select_all {
            column.header = ColumnHeader::SelectAll(self.optional_action(
                onall,
                &format!("column[{index}] onall"),
                BindingKind::BoolAction,
                "bool",
            )?);
        } else if let Some(h) = header {
            column.header = ColumnHeader::Text(self.text(Some(h)));
        }
        let sort = match attr(el, "sort") {
            None | Some("none") => SortKind::None,
            Some("text") => SortKind::Text,
            Some("number") => SortKind::Number,
            Some("bool") => SortKind::Bool,
            Some(other) => {
                return err(format!(
                    "column[{index}] sort=\"{other}\" must be text, number, bool or none"
                ));
            }
        };
        let keys = attr(el, "sortkeys");
        if keys.is_some() && !matches!(sort, SortKind::Text | SortKind::Number) {
            return err(format!(
                "column[{index}] sortkeys needs sort=\"text\" or sort=\"number\""
            ));
        }
        let is_check = matches!(column.kind, ColumnKind::Check { .. });
        if sort == SortKind::Bool && !is_check {
            return err(format!(
                "column[{index}] sort=\"bool\" needs a check or checkbox column"
            ));
        }
        if matches!(sort, SortKind::Text | SortKind::Number)
            && keys.is_none()
            && !matches!(
                column.kind,
                ColumnKind::Text { .. } | ColumnKind::Button { .. }
            )
        {
            let kind = match column.kind {
                ColumnKind::Check { .. } => "Check",
                ColumnKind::Icon { .. } => "Icon",
                _ => "Text",
            };
            return err(format!(
                "column[{index}] sort=\"{}\" on a {kind} column needs sortkeys",
                attr(el, "sort").unwrap_or_default()
            ));
        }
        column.sort = sort;
        if select_all && sort != SortKind::None {
            return err(format!("column[{index}] header=\"all\" can't also sort"));
        }
        if let Some(keys) = keys {
            let context = format!("column[{index}] sortkeys");
            if keys.trim().is_empty() || !is_binding(keys) {
                return err(format!(
                    "{context} must be a binding to a list of double or string"
                ));
            }
            column.sort_keys = Some(match self.kind(inner(keys)) {
                Some(BindingKind::FloatList) => SortKeysSource::Numbers(inner(keys).to_owned()),
                Some(BindingKind::TextList) => SortKeysSource::Texts(inner(keys).to_owned()),
                _ => {
                    return err(format!(
                        "{keys} did not resolve to an IReadOnlyList<double> or IReadOnlyList<string> property on {} ({context})",
                        self.type_name()
                    ));
                }
            });
        }
        Ok(())
    }

    fn list_tree(
        &self,
        el: XmlNode<'_, '_>,
        columns: Option<&[Column]>,
    ) -> Result<Option<ListTree>, MarkupError> {
        if !b(el, "tree", false) {
            for name in ["levels", "keys", "expanded", "onexpand", "collapsed"] {
                if attr(el, name).is_some() {
                    return err(format!("<list {name}> needs tree=\"true\""));
                }
            }
            return Ok(None);
        }
        let Some(columns) = columns.filter(|c| !c.is_empty()) else {
            return err("<list tree=\"true\"> needs <column> children");
        };
        let Some(column) = columns
            .iter()
            .position(|c| matches!(c.kind, ColumnKind::Text { .. }))
        else {
            return err("<list tree=\"true\"> needs a text column to draw the tree in");
        };
        let levels = self.required_of(
            attr(el, "levels"),
            "list levels",
            &[BindingKind::IntList],
            "an int-list binding",
            "an IEnumerable<int> property",
        )?;
        let keys = self.string_list(attr(el, "keys"), "list keys")?;
        let (expanded, start_collapsed) = match attr(el, "expanded") {
            Some(e) => {
                if attr(el, "collapsed").is_some() {
                    return err("<list collapsed> only applies when expanded is not bound");
                }
                let name = self.required_of(
                    Some(e),
                    "list expanded",
                    &[BindingKind::TextList],
                    "a string-set binding",
                    "an IEnumerable<string> property",
                )?;
                (Some(name), false)
            }
            None => (None, b(el, "collapsed", false)),
        };
        let onexpand = match attr(el, "onexpand") {
            None => None,
            Some(a) => {
                let context = "list onexpand";
                if !is_binding(a) {
                    return err(format!("{context} must be an Action<string, bool> binding"));
                }
                if self.kind(inner(a)) != Some(BindingKind::TextBoolAction) {
                    return err(format!(
                        "{a} did not resolve to an Action<string, bool> property on {} ({context})",
                        self.type_name()
                    ));
                }
                Some(inner(a).to_owned())
            }
        };
        Ok(Some(ListTree {
            column,
            levels,
            keys,
            expanded,
            start_collapsed,
            onexpand,
        }))
    }

    fn parse_anchor(&self, el: XmlNode<'_, '_>) -> Result<Anchors, MarkupError> {
        let Some(tokens) = attr(el, "anchor") else {
            return Ok(Anchors::TOP_LEFT);
        };
        let mut edges = Anchors::NONE;
        for token in tokens.split(ANCHOR_SEPARATORS).filter(|t| !t.is_empty()) {
            edges = edges.union(match token.to_lowercase().as_str() {
                "left" => Anchors::LEFT,
                "top" => Anchors::TOP,
                "right" => Anchors::RIGHT,
                "bottom" => Anchors::BOTTOM,
                _ => {
                    return err(format!(
                        "{} anchor=\"{tokens}\" has unknown token \"{token}\" (expected left, top, right, bottom)",
                        element_identity(el)
                    ));
                }
            });
        }
        if edges == Anchors::NONE {
            return err(format!(
                "{} anchor=\"{tokens}\" names no edge (expected a comma- or space-separated subset of left, top, right, bottom)",
                element_identity(el)
            ));
        }
        Ok(edges)
    }

    /// OpenAC `ApplyCommon`: name, anchors, visible, enabled, tooltip.
    fn common(&self, el: XmlNode<'_, '_>) -> Result<Common, MarkupError> {
        let name = attr(el, "name")
            .or_else(|| attr(el, "id"))
            .map(str::to_owned);
        let anchors = self.parse_anchor(el)?;
        let visible = self.bool_attr(attr(el, "visible"))?;
        let enabled = self.bool_attr(attr(el, "enabled"))?;
        let tooltip = attr(el, "tooltip")
            .filter(|t| !t.trim().is_empty())
            .map(|t| self.text(Some(t)));
        Ok(Common {
            name,
            anchors,
            visible,
            enabled,
            tooltip,
            default_color: attr(el, "color").is_none(),
            default_background: attr(el, "background").is_none(),
            default_border: attr(el, "border").is_none(),
            context_menu: None,
        })
    }

    fn push(
        &mut self,
        element: Element,
        common: Common,
        rect: Rect,
        parent: Option<usize>,
    ) -> usize {
        let index = self.doc.nodes.len();
        self.doc.nodes.push(Node {
            element,
            common,
            rect,
            parent,
            children: Vec::new(),
            flex: None,
            repeat: self.repeats.last().copied(),
        });
        if let Some(p) = parent {
            self.doc.nodes[p].children.push(index);
        }
        index
    }

    /// Builds `el` and its children, then its `<contextmenu>`. Under a flex
    /// container its item attributes are read; elsewhere a stray one is
    /// reported and ignored.
    fn element(
        &mut self,
        el: XmlNode<'_, '_>,
        parent: Option<usize>,
    ) -> Result<usize, MarkupError> {
        let flex_parent = match parent {
            None => self.doc.panel.layout.is_some(),
            // A template's top elements are items of the container the
            // `<repeat>` stands in, which is always a flex container.
            Some(p) => matches!(
                self.doc.nodes[p].element,
                Element::Group {
                    layout: Some(_),
                    ..
                } | Element::Repeat { .. }
            ),
        };
        if el.tag_name().name() == "repeat" {
            return self.repeat(el, parent, flex_parent);
        }
        if !flex_parent {
            flex_attrs::reject_item(el, &mut self.doc.warnings);
        }
        let index = self.element_inner(el, parent)?;
        let menu = self.context_menu(el)?;
        self.doc.nodes[index].common.context_menu = menu;
        if flex_parent {
            self.doc.nodes[index].flex = Some(flex_attrs::read_item(el)?);
        }
        Ok(index)
    }

    /// OpenAC `MarkupRepeat.Build` (its checks) and `RejectUnkeyedNames`:
    /// a `<repeat>` stands directly in a flex container, takes only `items`
    /// (an items list of the enclosing scope) and `key` (a value of the
    /// item type), and its template is read against the item type. A named
    /// element in a template needs `key` on every enclosing repeat, as its
    /// remembered state is kept per key.
    fn repeat(
        &mut self,
        el: XmlNode<'_, '_>,
        parent: Option<usize>,
        flex_parent: bool,
    ) -> Result<usize, MarkupError> {
        if !flex_parent {
            return err("<repeat> goes in a flex container (a window or <group> with layout)");
        }
        for attribute in el.attributes() {
            let name = attribute.name();
            if name != "items" && name != "key" {
                return err(format!(
                    "<repeat> takes only items= and key=, not {name}=; put layout attributes on the template's elements"
                ));
            }
        }
        if el
            .children()
            .any(|c| c.is_element() && c.tag_name().name() == "contextmenu")
        {
            return err(
                "<contextmenu> goes inside a <repeat>'s template element, not directly in <repeat>",
            );
        }
        let Some(items_expression) = attr(el, "items").filter(|e| is_binding(e)) else {
            return err("<repeat items=\"…\"> must be a {binding} to a list");
        };
        let items = inner(items_expression);
        let item: &'a BindingSchema = match self.kind(items) {
            None => {
                return err(format!(
                    "<repeat items=\"{items_expression}\"> did not resolve to a property on {}",
                    self.type_name()
                ));
            }
            Some(BindingKind::Items) => match self.schema.item_schema(items) {
                Some(item) => item,
                None => {
                    return err(format!(
                        "<repeat items=\"{items_expression}\"> has no item type on {}",
                        self.type_name()
                    ));
                }
            },
            Some(kind) => {
                return err(format!(
                    "<repeat items=\"{items_expression}\"> must be a list of items (declared with its item type), not {kind:?}"
                ));
            }
        };
        let key = match attr(el, "key") {
            None => None,
            Some(expression) => {
                if !is_binding(expression) {
                    return err(format!(
                        "<repeat key=\"{expression}\"> must be a {{binding}} to a property of the item"
                    ));
                }
                let name = inner(expression);
                match item.kind(name) {
                    None => {
                        return err(format!(
                            "<repeat key=\"{expression}\"> did not resolve to a property on {}",
                            item.type_name
                        ));
                    }
                    Some(
                        BindingKind::Bool
                        | BindingKind::Int
                        | BindingKind::Uint
                        | BindingKind::Float
                        | BindingKind::Text
                        | BindingKind::OptionalBool,
                    ) => Some(name.to_owned()),
                    Some(kind) => {
                        return err(format!(
                            "<repeat key=\"{expression}\"> must be a bool, int, uint, float or string property of {}, not {kind:?}",
                            item.type_name
                        ));
                    }
                }
            }
        };
        if key.is_none() {
            for d in el.descendants().skip(1).filter(XmlNode::is_element) {
                let named = d.attribute_node("name").or_else(|| d.attribute_node("id"));
                if let Some(a) = named {
                    return err(format!(
                        "<{} {}=\"{}\"> inside a <repeat> needs key= on every enclosing repeat",
                        d.tag_name().name(),
                        a.name(),
                        a.value()
                    ));
                }
            }
        }
        let index = self.push(
            Element::Repeat {
                items: items.to_owned(),
                key,
            },
            Common::default(),
            Rect::default(),
            parent,
        );
        let outer = self.schema;
        self.schema = item;
        self.repeats.push(index);
        let mut result = Ok(());
        for child in el.children().filter(XmlNode::is_element) {
            if let Err(e) = self.element(child, Some(index)) {
                result = Err(e);
                break;
            }
        }
        self.repeats.pop();
        self.schema = outer;
        result.map(|()| index)
    }

    /// The children of a container, its `<contextmenu>` left out.
    fn children_of(&mut self, el: XmlNode<'_, '_>, index: usize) -> Result<(), MarkupError> {
        for child in el
            .children()
            .filter(|c| c.is_element() && c.tag_name().name() != "contextmenu")
        {
            self.element(child, Some(index))?;
        }
        Ok(())
    }

    #[allow(clippy::too_many_lines)]
    fn element_inner(
        &mut self,
        el: XmlNode<'_, '_>,
        parent: Option<usize>,
    ) -> Result<usize, MarkupError> {
        let tag = el.tag_name().name();
        let element = match tag {
            "group" => {
                let background = self.color(attr(el, "background"))?;
                let border = self.color(attr(el, "border"))?;
                let (scroll_x, scroll_y) = flex_attrs::scroll(el, &mut self.doc.warnings);
                let layout = if flex_attrs::is_container(el, &mut self.doc.warnings) {
                    Some(flex_attrs::read_container(el)?)
                } else {
                    flex_attrs::reject_container(el, &mut self.doc.warnings);
                    None
                };
                let common = self.common(el)?;
                let index = self.push(
                    Element::Group {
                        background,
                        border,
                        layout,
                        scroll_x,
                        scroll_y,
                    },
                    common,
                    rect(el),
                    parent,
                );
                self.children_of(el, index)?;
                return Ok(index);
            }
            "meter" => {
                let cur = self.number(attr(el, "cur"));
                let max = self.number(attr(el, "max"));
                let fill = self.number(attr(el, "fill"));
                let color = self
                    .color(attr(el, "color"))?
                    .unwrap_or(ColorSource::Literal(parse_color(None)));
                let sprites = [
                    parse_hex(attr(el, "backleft")),
                    parse_hex(attr(el, "backtile")),
                    parse_hex(attr(el, "backright")),
                    parse_hex(attr(el, "frontleft")),
                    parse_hex(attr(el, "fronttile")),
                    parse_hex(attr(el, "frontright")),
                ];
                Element::Meter {
                    fill,
                    cur,
                    max,
                    color,
                    sprites,
                }
            }
            "label" => Element::Label {
                text: self.text(attr(el, "text")),
                color: self.color(attr(el, "color"))?,
            },
            "button" => {
                let onclick = self.handler(
                    attr(el, "onclick"),
                    BindingKind::Action,
                    self.action_message("button", "onclick", "Action"),
                )?;
                let text = match attr(el, "text") {
                    None => TextSource::Literal(String::new()),
                    Some(caption) => self.text(Some(caption)),
                };
                let color = self.color(attr(el, "color"))?;
                let background = self.color(attr(el, "background"))?;
                let border = self.color(attr(el, "border"))?;
                let icon = self.button_icon(el, "button icon")?;
                Element::Button {
                    text,
                    onclick,
                    color,
                    background,
                    border,
                    icon,
                }
            }
            "icon" => {
                if attr(el, "iconkind").is_some() {
                    return err(
                        "iconkind applies to button, tab and list; icon derives its kind from did/spell/item/file",
                    );
                }
                let did = attr(el, "did");
                let spell = attr(el, "spell");
                let item = attr(el, "item");
                let file = attr(el, "file");
                let sources = usize::from(did.is_some())
                    + usize::from(spell.is_some())
                    + usize::from(item.is_some())
                    + usize::from(file.is_some());
                if sources != 1 {
                    return err("<icon> requires exactly one of did/spell/item/file");
                }
                let icon = match (did, spell, item, file) {
                    (_, _, _, Some(path)) => {
                        IconSource::File(self.path_literal_or_binding(path, "icon file")?)
                    }
                    (Some(e), ..) => IconSource::Id {
                        kind: IconKind::Did,
                        id: self.uint_literal_or_binding(e, "icon did")?,
                    },
                    (_, Some(e), ..) => IconSource::Id {
                        kind: IconKind::Spell,
                        id: self.uint_literal_or_binding(e, "icon spell")?,
                    },
                    (_, _, Some(e), _) => IconSource::Id {
                        kind: IconKind::Item,
                        id: self.uint_literal_or_binding(e, "icon item")?,
                    },
                    _ => unreachable!("exactly one source was checked above"),
                };
                let common = self.common(el)?;
                let rect = Rect {
                    x: f(el, "x"),
                    y: f(el, "y"),
                    w: f_or(el, "w", 32.0),
                    h: f_or(el, "h", 32.0),
                };
                return Ok(self.push(Element::Icon { icon }, common, rect, parent));
            }
            "tab" => {
                let onclick = self.handler(
                    attr(el, "onclick"),
                    BindingKind::Action,
                    self.action_message("tab", "onclick", "Action"),
                )?;
                let selected = self.required_bool(attr(el, "selected"), "tab selected")?;
                let icon = self.button_icon(el, "tab icon")?;
                Element::Tab {
                    text: attr(el, "text").unwrap_or("").to_owned(),
                    selected,
                    onclick,
                    icon,
                }
            }
            "toggle" => {
                let onclick = self.handler(
                    attr(el, "onclick"),
                    BindingKind::Action,
                    self.action_message("toggle", "onclick", "Action"),
                )?;
                let caption = attr(el, "text");
                let checked = self.required_bool(attr(el, "checked"), "toggle checked")?;
                let text = match caption {
                    None => TextSource::Literal(String::new()),
                    Some(c) => self.text(Some(c)),
                };
                Element::Toggle {
                    text,
                    checked,
                    onclick,
                    color: self.color(attr(el, "color"))?,
                }
            }
            "slider" => {
                let onchange = self.handler(
                    attr(el, "onchange"),
                    BindingKind::FloatAction,
                    self.action_message("slider", "onchange", "Action<float>"),
                )?;
                let min = f_or(el, "min", 0.0);
                let max = f_or(el, "max", 1.0);
                if max <= min {
                    return err(format!(
                        "<slider min=\"{}\" max=\"{}\"> must have max > min",
                        num(min),
                        num(max)
                    ));
                }
                let value = self.number(attr(el, "value"));
                let retail_art = validate_art_style("slider", attr(el, "style"))?;
                Element::Slider {
                    min,
                    max,
                    value,
                    onchange,
                    retail_art,
                }
            }
            "field" => {
                let onchange = self.handler(
                    attr(el, "onchange"),
                    BindingKind::TextAction,
                    self.action_message("field", "onchange", "Action<string>"),
                )?;
                let onsubmit = self.handler(
                    attr(el, "onsubmit"),
                    BindingKind::TextAction,
                    self.action_message("field", "onsubmit", "Action<string>"),
                )?;
                let onblur = self.handler(
                    attr(el, "onblur"),
                    BindingKind::TextAction,
                    self.action_message("field", "onblur", "Action<string>"),
                )?;
                let onup = self.handler(
                    attr(el, "onup"),
                    BindingKind::Action,
                    self.action_message("field", "onup", "Action"),
                )?;
                let ondown = self.handler(
                    attr(el, "ondown"),
                    BindingKind::Action,
                    self.action_message("field", "ondown", "Action"),
                )?;
                let background = match attr(el, "background") {
                    None => ColorSource::Literal(Rgba::new(0.0, 0.0, 0.0, 0.9)),
                    Some(_) => self
                        .color(attr(el, "background"))?
                        .unwrap_or(ColorSource::Literal(Rgba::WHITE)),
                };
                let color = match attr(el, "color") {
                    None => ColorSource::Literal(Rgba::new(0.91, 0.87, 0.76, 1.0)),
                    Some(_) => self
                        .color(attr(el, "color"))?
                        .unwrap_or(ColorSource::Literal(Rgba::WHITE)),
                };
                Element::Field {
                    text: self.text(attr(el, "text")),
                    onchange,
                    onsubmit,
                    onblur,
                    onup,
                    ondown,
                    one_line: b(el, "oneline", true),
                    editable: b(el, "editable", true),
                    background,
                    color,
                    default_background: attr(el, "background").is_none(),
                    default_color: attr(el, "color").is_none(),
                    max_length: i(el, "maxlength", 128).max(1),
                    clear_on_submit: b(el, "clearonsubmit", false),
                }
            }
            "menu" => {
                let onchange = self.handler(
                    attr(el, "onchange"),
                    BindingKind::TextAction,
                    self.action_message("menu", "onchange", "Action<string>"),
                )?;
                let items = self.string_list(attr(el, "items"), "menu items")?;
                let selected = self.text(attr(el, "selected"));
                let retail_art = validate_art_style("menu", attr(el, "style"))?;
                Element::Menu {
                    items,
                    selected,
                    onchange,
                    searchable: b(el, "searchable", false),
                    rows: i(el, "rows", 7).max(1),
                    row_height: f_or(el, "rowheight", 18.0).max(12.0),
                    open_upward: b(el, "openupward", false),
                    retail_art,
                }
            }
            "log" => {
                let items = self.string_list(attr(el, "items"), "log items")?;
                let first_index = match attr(el, "firstindex") {
                    None => None,
                    Some(a) => Some(self.required_int(Some(a), "log firstindex")?),
                };
                Element::Log { items, first_index }
            }
            "list" => {
                let children: Vec<XmlNode<'_, '_>> = el
                    .children()
                    .filter(|c| c.is_element() && c.tag_name().name() != "contextmenu")
                    .collect();
                for child in &children {
                    let name = child.tag_name().name();
                    if name != "column" {
                        return err(format!(
                            "<list> children must all be <column>, got <{name}>"
                        ));
                    }
                    if child
                        .children()
                        .any(|c| c.is_element() && c.tag_name().name() == "contextmenu")
                    {
                        return err("<contextmenu> goes on the <list>, not on a <column>");
                    }
                }
                let columns = !children.is_empty();
                if columns
                    && (attr(el, "items").is_some()
                        || attr(el, "icons").is_some()
                        || attr(el, "colors").is_some())
                {
                    return err(
                        "<list> with <column> children cannot also use the items/icons/colors attributes (the single-column form) — express every row source as a <column> instead",
                    );
                }
                // OpenAC `MarkupListSelect`.
                let (selected, onchange, multi, selection_band) = match attr(el, "selectmode")
                    .unwrap_or("single")
                {
                    "single" => {
                        let onchange = self.handler(
                            attr(el, "onchange"),
                            BindingKind::IntAction,
                            self.action_message("list", "onchange", "Action<int>"),
                        )?;
                        let selected = self.required_int(attr(el, "selected"), "list selected")?;
                        (selected, onchange, false, b(el, "selectionband", false))
                    }
                    "multi" => {
                        if !b(el, "selectionband", true) {
                            return err(
                                "<list selectmode=\"multi\"> always draws the selection band; remove selectionband=\"false\"",
                            );
                        }
                        let selected = self.required_of(
                            attr(el, "selected"),
                            "list selected",
                            &[BindingKind::IntList],
                            "an int-list binding",
                            "an IEnumerable<int> property",
                        )?;
                        let onchange = self.optional_action(
                            attr(el, "onchange"),
                            "list onchange",
                            BindingKind::IntListAction,
                            "IReadOnlyList<int>",
                        )?;
                        (selected, onchange, true, true)
                    }
                    other => {
                        return err(format!(
                            "<list selectmode=\"{other}\"> must be single or multi"
                        ));
                    }
                };
                let body = if columns {
                    let last = children.len() - 1;
                    let mut built = Vec::with_capacity(children.len());
                    for (index, column) in children.iter().enumerate() {
                        built.push(self.column(*column, index, index == last)?);
                    }
                    ListBody::Columns(built)
                } else {
                    let items = self.string_list(attr(el, "items"), "list items")?;
                    let colors = self.uint_list(attr(el, "colors"), "list colors")?;
                    let icons = match attr(el, "icons").filter(|a| !a.trim().is_empty()) {
                        None => None,
                        Some(icons) => {
                            let kind = validate_icon_kind(attr(el, "iconkind"), "iconkind")?;
                            if kind == IconKind::File {
                                // Paths, one per row.
                                Some((self.string_list(Some(icons), "list icons")?, kind))
                            } else {
                                self.uint_list(Some(icons), "list icons")?
                                    .map(|name| (name, kind))
                            }
                        }
                    };
                    ListBody::Single {
                        items,
                        colors,
                        icons,
                    }
                };
                let common = self.common(el)?;
                let mut body = body;
                let header = match &mut body {
                    ListBody::Columns(built) => self.list_header(el, &children, built)?,
                    ListBody::Single { .. } => {
                        for name in [
                            "header",
                            "resizable",
                            "sortcolumn",
                            "sortdescending",
                            "onsort",
                            "widths",
                            "onresize",
                        ] {
                            if attr(el, name).is_some() {
                                return err(format!("<list {name}> needs <column> children"));
                            }
                        }
                        ListHeader::default()
                    }
                };
                let tree = self.list_tree(
                    el,
                    match &body {
                        ListBody::Columns(c) => Some(c.as_slice()),
                        ListBody::Single { .. } => None,
                    },
                )?;
                let element = Element::List {
                    selected,
                    onchange,
                    row_height: f_or(el, "rowheight", 18.0).max(12.0),
                    selection_band,
                    multi,
                    body,
                    header,
                    tree,
                };
                return Ok(self.push(element, common, rect(el), parent));
            }
            "checkbox" => self.checkbox(el)?,
            "number" => self.number_element(el)?,
            "radio" => self.radio(el)?,
            "separator" => Element::Separator {
                color: self.color(attr(el, "color"))?,
            },
            "hotkey" => self.hotkey(el)?,
            "color" => self.color_element(el)?,
            "expander" => {
                let height = f(el, "h");
                if height < EXPANDER_HEADER_HEIGHT {
                    return err(format!(
                        "<expander h=\"{}\"> must be at least {} (its header)",
                        num(height),
                        num(EXPANDER_HEADER_HEIGHT)
                    ));
                }
                let open = Self::strict_bool(el, "open", true, |other| {
                    format!("<expander open=\"{other}\"> must be true or false")
                })?;
                let framed = Self::strict_bool(el, "border", true, |other| {
                    format!("<expander border=\"{other}\"> must be true or false")
                })?;
                let onchange = self.optional_action(
                    attr(el, "onchange"),
                    "expander onchange",
                    BindingKind::BoolAction,
                    "bool",
                )?;
                let expanded = match attr(el, "expanded") {
                    None => None,
                    Some(e) => Some(self.required_bool(Some(e), "expander expanded")?),
                };
                let text = match attr(el, "text") {
                    None => TextSource::Literal(String::new()),
                    Some(c) => self.text(Some(c)),
                };
                let color = self.color(attr(el, "color"))?;
                let common = self.common(el)?;
                let index = self.push(
                    Element::Expander {
                        text,
                        expanded,
                        onchange,
                        open,
                        color,
                        framed,
                    },
                    common,
                    rect(el),
                    parent,
                );
                self.children_of(el, index)?;
                return Ok(index);
            }
            "split" => {
                let horizontal = match attr(el, "orientation") {
                    None | Some("horizontal") => true,
                    Some("vertical") => false,
                    Some(other) => {
                        return err(format!(
                            "<split orientation=\"{other}\"> must be horizontal or vertical"
                        ));
                    }
                };
                let panes: Vec<XmlNode<'_, '_>> = el
                    .children()
                    .filter(|c| c.is_element() && c.tag_name().name() != "contextmenu")
                    .collect();
                if panes.len() != 2 || panes.iter().any(|p| p.tag_name().name() != "group") {
                    return err("<split> must contain exactly two <group> elements");
                }
                let min = Self::split_length(el, "min")?.unwrap_or(0.0);
                let max = Self::split_length(el, "max")?;
                let onchange = self.optional_action(
                    attr(el, "onchange"),
                    "split onchange",
                    BindingKind::FloatAction,
                    "float",
                )?;
                if let Some(max) = max
                    && max < min
                {
                    return err(format!(
                        "<split max=\"{}\"> must not be less than min=\"{}\"",
                        num(max),
                        num(min)
                    ));
                }
                let r = rect(el);
                let room = ((if horizontal { r.w } else { r.h }) - SPLIT_BAR_SIZE).max(0.0);
                let at = Self::split_length(el, "at")?;
                let position = match attr(el, "position") {
                    None => None,
                    Some(p) => Some(self.required_of(
                        Some(p),
                        "split position",
                        &[BindingKind::Float],
                        "a float binding",
                        "a float property",
                    )?),
                };
                let common = self.common(el)?;
                let index = self.push(
                    Element::Split {
                        horizontal,
                        min,
                        max,
                        default_position: at.unwrap_or((room / 2.0).floor()),
                        at_given: at.is_some(),
                        position,
                        onchange,
                    },
                    common,
                    r,
                    parent,
                );
                for pane in panes {
                    self.element(pane, Some(index))?;
                }
                return Ok(index);
            }
            other => return err(format!("unknown element <{other}>")),
        };
        let common = self.common(el)?;
        Ok(self.push(element, common, rect(el), parent))
    }

    fn column(&self, el: XmlNode<'_, '_>, index: usize, last: bool) -> Result<Column, MarkupError> {
        let kind_name = attr(el, "type");
        let context = |type_name: &str, attribute: &str| {
            format!("column[{index}] type=\"{type_name}\" {attribute}")
        };
        let raw = attr(el, "width");
        let (width, auto) = if raw == Some("*") {
            (0.0, true)
        } else if last {
            (f(el, "width"), false)
        } else {
            match raw.and_then(parse_float).filter(|w| *w > 0.0) {
                Some(w) => (w, false),
                None => {
                    return err(format!(
                        "{} must be a positive number or \"*\", got {}",
                        context(kind_name.unwrap_or("(missing)"), "width"),
                        raw.map_or_else(|| "(missing)".to_owned(), |r| format!("\"{r}\""))
                    ));
                }
            }
        };
        let kind = match kind_name {
            Some("text") => {
                let items = self.string_list(attr(el, "items"), &context("text", "items"))?;
                let colors = match attr(el, "colors") {
                    None => None,
                    Some(c) => self.uint_list(Some(c), &context("text", "colors"))?,
                };
                let onclick = self.handler(attr(el, "onclick"), BindingKind::IntAction, |_| {
                    format!(
                        "{} did not resolve to an Action<int> property on {}",
                        context("text", "onclick"),
                        self.type_name()
                    )
                })?;
                ColumnKind::Text {
                    items,
                    colors,
                    onclick,
                }
            }
            Some(t @ ("check" | "checkbox")) => {
                let values = self.bool_list(attr(el, "values"), &context(t, "values"))?;
                let onchange =
                    self.required_int_action(attr(el, "onchange"), &context(t, "onchange"))?;
                ColumnKind::Check {
                    values,
                    onchange,
                    boxed: t == "checkbox",
                }
            }
            Some("icon") => {
                let values_context = context("icon", "values");
                let kind = validate_icon_kind(attr(el, "iconkind"), &context("icon", "iconkind"))?;
                let values = match attr(el, "values").filter(|a| !a.trim().is_empty()) {
                    None if kind == IconKind::File => {
                        return err(format!("{values_context} is required"));
                    }
                    None => return err(format!("{values_context} must be a uint-list binding")),
                    // Paths, one per row.
                    Some(v) if kind == IconKind::File => {
                        self.string_list(Some(v), &values_context)?
                    }
                    Some(v) => self
                        .uint_list(Some(v), &values_context)?
                        .unwrap_or_default(),
                };
                let onclick =
                    self.required_int_action(attr(el, "onclick"), &context("icon", "onclick"))?;
                ColumnKind::Icon {
                    values,
                    kind,
                    onclick,
                }
            }
            Some("button") => {
                let items = self.string_list(attr(el, "items"), &context("button", "items"))?;
                let onclick =
                    self.required_int_action(attr(el, "onclick"), &context("button", "onclick"))?;
                let size = |attribute: &str| {
                    let size = f_or(el, attribute, 0.0);
                    if size < 0.0 {
                        err(format!(
                            "{} must not be negative",
                            context("button", attribute)
                        ))
                    } else {
                        Ok(size)
                    }
                };
                ColumnKind::Button {
                    items,
                    onclick,
                    button_width: size("buttonw")?,
                    button_height: size("buttonh")?,
                }
            }
            other => {
                return err(format!(
                    "column[{index}] has unknown type=\"{}\" (expected text, check, checkbox, icon, or button)",
                    other.unwrap_or("")
                ));
            }
        };
        Ok(Column {
            width,
            auto,
            kind,
            header: ColumnHeader::None,
            sort: SortKind::None,
            sort_keys: None,
        })
    }
}

#[cfg(test)]
mod controls_tests;
#[cfg(test)]
mod repeat_tests;
#[cfg(test)]
mod tests;

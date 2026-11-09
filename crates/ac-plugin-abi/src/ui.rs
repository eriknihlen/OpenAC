//! Plugin UI records (the plugin API's `ui` interface): markup panels with declared bindings, canvases and their
//! paint commands, images, world labels and lines, client windows, and the
//! events a plugin hears from its UI ([`UiEvent`]).
//!
//! Everything here is session state on the client side: the answers are the
//! same with or without a window, except where an import says it needs one
//! ([`crate::Import::needs_window`]).

use crate::wire::{DecodeError, Reader, Wire, Writer};
use crate::wire_record;

/// [`crate::Event::Ui`]'s tag: well clear of the other event groups'
/// numbering.
pub const EVENT_TAG: u32 = 100;

/// Writes a fieldless-or-tuple enum's `Wire` from one list: each variant's
/// tag and its fields in order. A field a later API minor version added goes
/// last, after a `;` and `since (MAJOR, MINOR)`: a plugin built for an older
/// version writes and reads the variant without it, and it reads as its
/// default ([`Wire::read_at`]), so such a plugin still loads.
macro_rules! wire_enum {
    (
        $(#[$meta:meta])*
        pub enum $name:ident ($what:literal) {
            $( $(#[$vmeta:meta])* $variant:ident $( (
                $($field:ident : $ty:ty),*
                $(; since ($smaj:literal, $smin:literal) $later:ident : $lty:ty)?
            ) )? = $tag:literal ),* $(,)?
        }
    ) => {
        $(#[$meta])*
        pub enum $name {
            $( $(#[$vmeta])* $variant $( ( $($ty,)* $($lty)? ) )?, )*
        }

        impl Wire for $name {
            const SHAPE: u64 = $crate::wire::Shape::new("enum")
                $(
                    .tag($tag as u64)
                    .name(stringify!($variant))
                    $(
                        $( .name(stringify!($field)).of(<$ty as Wire>::SHAPE) )*
                        $( .name(stringify!($later)).of(<$lty as Wire>::SHAPE) )?
                    )?
                )*
                .finish();
            fn encode(&self, w: &mut Writer) {
                match self {
                    $( Self::$variant $( ( $($field,)* $($later)? ) )? => {
                        ($tag as u8).encode(w);
                        $(
                            $( $field.encode(w); )*
                            $( $later.encode(w); )?
                        )?
                    } )*
                }
            }
            fn read(r: &mut Reader<'_>) -> Self {
                match u8::read(r) {
                    $( $tag => Self::$variant $( (
                        $( <$ty as Wire>::read(r), )*
                        $( <$lty as Wire>::read(r) )?
                    ) )?, )*
                    tag => {
                        r.fail(DecodeError::UnknownTag { what: $what, tag: u32::from(tag) });
                        // A stand-in: the first variant, read from the
                        // failed reader (no bytes left).
                        wire_enum!(@first r; $( $variant $( ( $($field : $ty),* ) )? );*)
                    }
                }
            }
            const VERSIONED: bool = false
                $( $(
                    $( || <$ty as Wire>::VERSIONED )*
                    $( || $crate::__wire_marked!(($smaj, $smin)) || <$lty as Wire>::VERSIONED )?
                )? )*;
            fn shape_at(version: $crate::ApiVersion) -> u64 {
                let _ = version;
                #[allow(unused_mut)]
                let mut shape = $crate::wire::Shape::new("enum");
                $(
                    shape = shape.tag($tag as u64).name(stringify!($variant));
                    $(
                        $( shape = shape.name(stringify!($field)).of(<$ty as Wire>::shape_at(version)); )*
                        $(
                            if $crate::__wire_since!(($smaj, $smin)).is_at_most(version) {
                                shape = shape.name(stringify!($later)).of(<$lty as Wire>::shape_at(version));
                            }
                        )?
                    )?
                )*
                shape.finish()
            }
            fn encode_at(&self, version: $crate::ApiVersion, w: &mut Writer) {
                let _ = version;
                match self {
                    $( Self::$variant $( ( $($field,)* $($later)? ) )? => {
                        ($tag as u8).encode(w);
                        $(
                            $( $field.encode_at(version, w); )*
                            $(
                                if $crate::__wire_since!(($smaj, $smin)).is_at_most(version) {
                                    $later.encode_at(version, w);
                                }
                            )?
                        )?
                    } )*
                }
            }
            fn read_at(r: &mut Reader<'_>, version: $crate::ApiVersion) -> Self {
                let _ = version;
                match u8::read(r) {
                    $( $tag => Self::$variant $( (
                        $( <$ty as Wire>::read_at(r, version), )*
                        $(
                            if $crate::__wire_since!(($smaj, $smin)).is_at_most(version) {
                                <$lty as Wire>::read_at(r, version)
                            } else {
                                ::core::default::Default::default()
                            }
                        )?
                    ) )?, )*
                    tag => {
                        r.fail(DecodeError::UnknownTag { what: $what, tag: u32::from(tag) });
                        wire_enum!(@first r; $( $variant $( ( $($field : $ty),* ) )? );*)
                    }
                }
            }
        }
    };
    (@first $r:ident; $variant:ident $( ( $($field:ident : $ty:ty),* ) )? $(; $($rest:tt)*)?) => {
        Self::$variant $( ( $( <$ty as Wire>::read($r) ),* ) )?
    };
}

wire_enum! {
    /// What a markup binding name is (OpenAC: the type of the C# property
    /// it names).
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
    pub enum BindingKind ("binding kind") {
        /// `bool`.
        Bool = 0,
        /// `int`.
        Int = 1,
        /// `uint`.
        Uint = 2,
        /// `float`.
        Float = 3,
        /// `string`.
        #[default]
        Text = 4,
        /// Strings.
        TextList = 5,
        /// `uint`s.
        UintList = 6,
        /// `int`s.
        IntList = 7,
        /// `bool`s.
        BoolList = 8,
        /// A handler with no argument (`Action`).
        Action = 9,
        /// A handler taking an `int` (`Action<int>`).
        IntAction = 10,
        /// A handler taking a `float` (`Action<float>`).
        FloatAction = 11,
        /// A handler taking a `string` (`Action<string>`).
        TextAction = 12,
    }
}

wire_enum! {
    /// A value pushed for a binding.
    #[derive(Debug, Clone, PartialEq)]
    pub enum UiValue ("binding value") {
        /// `bool`.
        Bool(v: bool) = 0,
        /// `int`.
        Int(v: i32) = 1,
        /// `uint`.
        Uint(v: u32) = 2,
        /// `float`.
        Float(v: f32) = 3,
        /// `string`.
        Text(v: String) = 4,
        /// Strings.
        TextList(v: Vec<String>) = 5,
        /// `uint`s.
        UintList(v: Vec<u32>) = 6,
        /// `int`s.
        IntList(v: Vec<i32>) = 7,
        /// `bool`s.
        BoolList(v: Vec<bool>) = 8,
    }
}

impl From<bool> for UiValue {
    fn from(v: bool) -> Self {
        Self::Bool(v)
    }
}

impl From<i32> for UiValue {
    fn from(v: i32) -> Self {
        Self::Int(v)
    }
}

impl From<u32> for UiValue {
    fn from(v: u32) -> Self {
        Self::Uint(v)
    }
}

impl From<f32> for UiValue {
    fn from(v: f32) -> Self {
        Self::Float(v)
    }
}

impl From<&str> for UiValue {
    fn from(v: &str) -> Self {
        Self::Text(v.to_owned())
    }
}

impl From<String> for UiValue {
    fn from(v: String) -> Self {
        Self::Text(v)
    }
}

impl From<Vec<String>> for UiValue {
    fn from(v: Vec<String>) -> Self {
        Self::TextList(v)
    }
}

impl From<Vec<u32>> for UiValue {
    fn from(v: Vec<u32>) -> Self {
        Self::UintList(v)
    }
}

impl From<Vec<i32>> for UiValue {
    fn from(v: Vec<i32>) -> Self {
        Self::IntList(v)
    }
}

impl From<Vec<bool>> for UiValue {
    fn from(v: Vec<bool>) -> Self {
        Self::BoolList(v)
    }
}

wire_record! {
    /// One binding name a panel's markup may use, and its kind.
    pub struct BindingDecl {
        /// The name inside `{...}`.
        pub name: String,
        /// What it is.
        pub kind: BindingKind,
    }
}

impl Default for UiValue {
    fn default() -> Self {
        Self::Bool(false)
    }
}

// ── The richer binding kinds the newer markup controls use (`<checkbox>`,
// `<number>`, `<radio>`, `<expander>`, `<split>`, `<hotkey>`, `<color>`,
// `<contextmenu>`, list headers, multi-select and trees, `<repeat>`). They
// travel through their own imports ([`crate::Import::UiPanelRegisterTyped`],
// [`crate::Import::UiPanelSetTyped`]), so the first imports and every
// plugin built against them stay as they were. The handlers they add reach
// the plugin as [`UiEvent::Action`] with the argument carried in an
// [`ActionValue`] as [`ActionValue`]'s helpers read it.

wire_enum! {
    /// Every kind a markup binding name may be (OpenAC: the type of the C#
    /// property it names): [`BindingKind`]'s, with the same tags, and more.
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
    pub enum TypedBindingKind ("typed binding kind") {
        /// `bool`.
        Bool = 0,
        /// `int`.
        Int = 1,
        /// `uint`.
        Uint = 2,
        /// `float`.
        Float = 3,
        /// `string`.
        #[default]
        Text = 4,
        /// Strings.
        TextList = 5,
        /// `uint`s.
        UintList = 6,
        /// `int`s.
        IntList = 7,
        /// `bool`s.
        BoolList = 8,
        /// A handler with no argument (`Action`).
        Action = 9,
        /// A handler taking an `int` (`Action<int>`).
        IntAction = 10,
        /// A handler taking a `float` (`Action<float>`).
        FloatAction = 11,
        /// A handler taking a `string` (`Action<string>`).
        TextAction = 12,
        /// `bool` or none (`bool?`: none is a checkbox's indeterminate).
        OptionalBool = 13,
        /// `float`s (column widths, numeric sort keys).
        FloatList = 14,
        /// Item records, each with the names its item type declares (a
        /// `<repeat>`'s `items`); its type is an [`ItemTypeDecl`].
        Items = 15,
        /// A handler taking a `bool` (`Action<bool>`).
        BoolAction = 16,
        /// A handler taking a `uint` (`Action<uint>`).
        UintAction = 17,
        /// A handler taking an `int` and a `bool` (`Action<int, bool>`).
        IntBoolAction = 18,
        /// A handler taking a `string` and a `bool` (`Action<string, bool>`).
        TextBoolAction = 19,
        /// A handler taking `float`s (`Action<IReadOnlyList<float>>`).
        FloatListAction = 20,
        /// A handler taking `int`s (`Action<IReadOnlyList<int>>`).
        IntListAction = 21,
    }
}

impl From<BindingKind> for TypedBindingKind {
    fn from(kind: BindingKind) -> Self {
        match kind {
            BindingKind::Bool => Self::Bool,
            BindingKind::Int => Self::Int,
            BindingKind::Uint => Self::Uint,
            BindingKind::Float => Self::Float,
            BindingKind::Text => Self::Text,
            BindingKind::TextList => Self::TextList,
            BindingKind::UintList => Self::UintList,
            BindingKind::IntList => Self::IntList,
            BindingKind::BoolList => Self::BoolList,
            BindingKind::Action => Self::Action,
            BindingKind::IntAction => Self::IntAction,
            BindingKind::FloatAction => Self::FloatAction,
            BindingKind::TextAction => Self::TextAction,
        }
    }
}

wire_record! {
    /// One binding name and its kind, of the typed registration.
    pub struct TypedBindingDecl {
        /// The name inside `{...}`.
        pub name: String,
        /// What it is.
        pub kind: TypedBindingKind,
    }
}

wire_record! {
    /// The item type of an [`TypedBindingKind::Items`] binding: the names an
    /// item record carries. `list` is the path of the list: its binding name
    /// on the window, or `Outer/Inner` for a list an item of `Outer` carries.
    pub struct ItemTypeDecl {
        /// The list's path.
        pub list: String,
        /// The item type's name, for messages.
        pub type_name: String,
        /// Its names.
        pub bindings: Vec<TypedBindingDecl>,
    }
}

wire_record! {
    /// A window from markup whose bindings may use every kind
    /// ([`crate::Import::UiPanelRegisterTyped`]); otherwise as
    /// [`PanelRegistration`]. Reply `Result<u32, String>`.
    pub struct TypedPanelRegistration {
        /// The plugin's own short name for the window.
        pub window_id: String,
        /// The caption.
        pub title: String,
        /// Initials on the dock slot when there is no icon.
        pub icon_text: Option<String>,
        /// The dock icon (a full image id or a bare index; 0 none).
        pub icon_surface_id: u32,
        /// Open at once.
        pub start_visible: bool,
        /// A slot on the plugin dock.
        pub show_in_side_panel: bool,
        /// The markup.
        pub markup: String,
        /// The binding type's name, for messages.
        pub binding_type: String,
        /// Every binding name the markup may use.
        pub bindings: Vec<TypedBindingDecl>,
        /// The item type of every items list.
        pub item_types: Vec<ItemTypeDecl>,
        /// The window's own dock icon: a `.png` in the plugin's folder, by a
        /// path relative to it, tried ahead of the plugin's `icon.png`.
        since (1, 4) pub icon_file: Option<String>,
    }
}

wire_enum! {
    /// A value inside an item record: every [`TypedValue`] but items (a
    /// nested list is rows of its own, see [`ItemRow`]).
    #[derive(Debug, Clone, PartialEq)]
    pub enum ItemValue ("item value") {
        /// `bool`.
        Bool(v: bool) = 0,
        /// `int`.
        Int(v: i32) = 1,
        /// `uint`.
        Uint(v: u32) = 2,
        /// `float`.
        Float(v: f32) = 3,
        /// `string`.
        Text(v: String) = 4,
        /// Strings.
        TextList(v: Vec<String>) = 5,
        /// `uint`s.
        UintList(v: Vec<u32>) = 6,
        /// `int`s.
        IntList(v: Vec<i32>) = 7,
        /// `bool`s.
        BoolList(v: Vec<bool>) = 8,
        /// `bool` or none.
        OptionalBool(v: Option<bool>) = 9,
        /// `float`s.
        FloatList(v: Vec<f32>) = 10,
    }
}

impl Default for ItemValue {
    fn default() -> Self {
        Self::Bool(false)
    }
}

wire_record! {
    /// One named value of an item.
    pub struct NamedItemValue {
        /// The binding name.
        pub name: String,
        /// Its value.
        pub value: ItemValue,
    }
}

wire_record! {
    /// One item of an items list, flattened: the rows of every list nested
    /// in it follow it, each naming its parent row (its index in the same
    /// [`ItemRows`]) and the parent's list binding it belongs to.
    pub struct ItemRow {
        /// The parent row's index, or −1 for a row of the list itself.
        pub parent: i32,
        /// The parent item's list binding this row is in (empty for a row
        /// of the list itself).
        pub list: String,
        /// Its values.
        pub values: Vec<NamedItemValue>,
    }
}

wire_record! {
    /// An items list: its rows and its nested lists' rows, parents first.
    pub struct ItemRows {
        /// Every row.
        pub rows: Vec<ItemRow>,
    }
}

wire_enum! {
    /// A value pushed for a typed binding.
    #[derive(Debug, Clone, PartialEq)]
    pub enum TypedValue ("typed binding value") {
        /// `bool`.
        Bool(v: bool) = 0,
        /// `int`.
        Int(v: i32) = 1,
        /// `uint`.
        Uint(v: u32) = 2,
        /// `float`.
        Float(v: f32) = 3,
        /// `string`.
        Text(v: String) = 4,
        /// Strings.
        TextList(v: Vec<String>) = 5,
        /// `uint`s.
        UintList(v: Vec<u32>) = 6,
        /// `int`s.
        IntList(v: Vec<i32>) = 7,
        /// `bool`s.
        BoolList(v: Vec<bool>) = 8,
        /// `bool` or none.
        OptionalBool(v: Option<bool>) = 9,
        /// `float`s.
        FloatList(v: Vec<f32>) = 10,
        /// Item records.
        Items(v: ItemRows) = 11,
    }
}

impl Default for TypedValue {
    fn default() -> Self {
        Self::Bool(false)
    }
}

impl From<UiValue> for TypedValue {
    fn from(v: UiValue) -> Self {
        match v {
            UiValue::Bool(v) => Self::Bool(v),
            UiValue::Int(v) => Self::Int(v),
            UiValue::Uint(v) => Self::Uint(v),
            UiValue::Float(v) => Self::Float(v),
            UiValue::Text(v) => Self::Text(v),
            UiValue::TextList(v) => Self::TextList(v),
            UiValue::UintList(v) => Self::UintList(v),
            UiValue::IntList(v) => Self::IntList(v),
            UiValue::BoolList(v) => Self::BoolList(v),
        }
    }
}

wire_record! {
    /// One pushed typed value.
    pub struct NamedTypedValue {
        /// The binding name.
        pub name: String,
        /// Its value.
        pub value: TypedValue,
    }
}

wire_record! {
    /// Typed values for a panel ([`crate::Import::UiPanelSetTyped`]); reply
    /// `Result<(), String>` (all or nothing).
    pub struct TypedPanelValues {
        /// The panel.
        pub panel: u32,
        /// The values.
        pub values: Vec<NamedTypedValue>,
    }
}

wire_record! {
    /// A question for the player in a dialog box
    /// ([`crate::Import::UiDialogConfirm`]; OpenAC `PluginDialog`). Reply
    /// `Result<Option<u32>, String>`: the dialog's handle once it is
    /// queued, none when no window draws it (then it is never answered),
    /// or why the request is refused (no buttons, more than four, a blank
    /// one). The answer arrives as [`UiEvent::Action`] on panel
    /// [`DIALOG_PANEL`] (see [`UiAction::dialog_answer`]).
    pub struct DialogRequest {
        /// The caption (may be empty).
        pub title: String,
        /// The question; long text wraps (may be empty).
        pub message: String,
        /// The buttons left to right, one to four; the last is the cancel
        /// button (Escape, the close button, the owner window closing, the
        /// window detaching and the plugin unloading answer with it).
        /// Empty means OK and Cancel.
        pub buttons: Vec<String>,
        /// The id or title of the plugin's window the dialog is centred on
        /// and blocks until it is answered; none (or a window not shown)
        /// centres it on the screen and blocks nothing.
        pub owner_view: Option<String>,
    }
}

wire_record! {
    /// Show one of the plugin's windows modal over another
    /// ([`crate::Import::UiPanelShowModal`]); reply `bool`.
    pub struct ModalShow {
        /// The id or title of the window to show.
        pub view: String,
        /// The id or title of the window it blocks (none: nothing).
        pub owner_view: Option<String>,
    }
}

/// One row around a handler fired inside `<repeat>` ([`RowAction`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RowStep {
    /// The list's binding name (on the window, or on the enclosing item).
    pub list: String,
    /// The row's place in the list as the window last read it.
    pub index: usize,
    /// The row's key as text (`Display` of the key value), on a repeat
    /// with `key`.
    pub key: Option<String>,
}

/// A handler fired inside `<repeat>` rows ([`UiAction::row_action`]): the
/// rows around the control, outermost first, and the handler's name on the
/// innermost item type. On the wire the handler name carries them as
/// `List[index]/` or `List[index:key]/` before the name (a key's `%` and
/// `/` written `%25` and `%2F`): `Sections[0:a]/Rows[2:7]/SetOn`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RowAction {
    /// The rows, outermost first.
    pub rows: Vec<RowStep>,
    /// The handler's name on the row's item type.
    pub handler: String,
}

impl RowAction {
    /// Reads a handler name; `None` for a window handler.
    #[must_use]
    pub fn parse(handler: &str) -> Option<Self> {
        let (path, name) = handler.rsplit_once('/')?;
        if name.is_empty() {
            return None;
        }
        let rows = path
            .split('/')
            .map(|step| {
                let (list, rest) = step.split_once('[')?;
                let inside = rest.strip_suffix(']')?;
                let (index, key) = match inside.split_once(':') {
                    Some((i, k)) => (i, Some(k.replace("%2F", "/").replace("%25", "%"))),
                    None => (inside, None),
                };
                if list.is_empty() || index.is_empty() || !index.bytes().all(|b| b.is_ascii_digit())
                {
                    return None;
                }
                Some(RowStep {
                    list: list.to_owned(),
                    index: index.parse().ok()?,
                    key,
                })
            })
            .collect::<Option<Vec<_>>>()?;
        Some(Self {
            rows,
            handler: name.to_owned(),
        })
    }
}

/// The panel number a dialog's answer arrives on ([`UiAction::dialog_answer`]):
/// no window has it.
pub const DIALOG_PANEL: u32 = 0;

/// What a dialog answer's handler name starts with; the dialog's handle
/// follows.
pub const DIALOG_HANDLER_PREFIX: &str = "dialog:";

impl UiAction {
    /// The rows and handler of a handler fired inside `<repeat>` rows;
    /// `None` for a window handler.
    #[must_use]
    pub fn row_action(&self) -> Option<RowAction> {
        RowAction::parse(&self.handler)
    }

    /// A dialog's answer: its handle and the button picked (by index).
    #[must_use]
    pub fn dialog_answer(&self) -> Option<(u32, i32)> {
        if self.panel != DIALOG_PANEL {
            return None;
        }
        let handle = self
            .handler
            .strip_prefix(DIALOG_HANDLER_PREFIX)?
            .parse()
            .ok()?;
        match self.value {
            ActionValue::Int(button) => Some((handle, button)),
            _ => None,
        }
    }
}

/// How the newer handler arguments travel in an [`ActionValue`]: the plugin
/// declared the handler's kind, so it reads the argument back with the
/// matching helper.
impl ActionValue {
    /// `Action<bool>`: `Int(1)` or `Int(0)`.
    #[must_use]
    pub fn as_bool(&self) -> Option<bool> {
        match self {
            Self::Int(v) => Some(*v != 0),
            _ => None,
        }
    }

    /// `Action<uint>`: the bits in `Int`.
    #[must_use]
    pub fn as_uint(&self) -> Option<u32> {
        match self {
            Self::Int(v) => Some(*v as u32),
            _ => None,
        }
    }

    /// `Action<int, bool>`: `Text` of `1` or `0` then the number.
    #[must_use]
    pub fn as_int_bool(&self) -> Option<(i32, bool)> {
        let (flag, rest) = self.flagged()?;
        Some((rest.parse().ok()?, flag))
    }

    /// `Action<string, bool>`: `Text` of `1` or `0` then the text.
    #[must_use]
    pub fn as_text_bool(&self) -> Option<(String, bool)> {
        let (flag, rest) = self.flagged()?;
        Some((rest.to_owned(), flag))
    }

    /// `Action<IReadOnlyList<float>>`: `Text` of the numbers joined by `,`.
    #[must_use]
    pub fn as_float_list(&self) -> Option<Vec<f32>> {
        self.list()?.map(|s| s.parse().ok()).collect()
    }

    /// `Action<IReadOnlyList<int>>`: `Text` of the numbers joined by `,`.
    #[must_use]
    pub fn as_int_list(&self) -> Option<Vec<i32>> {
        self.list()?.map(|s| s.parse().ok()).collect()
    }

    fn flagged(&self) -> Option<(bool, &str)> {
        let Self::Text(text) = self else {
            return None;
        };
        match text.as_bytes().first() {
            Some(b'1') => Some((true, &text[1..])),
            Some(b'0') => Some((false, &text[1..])),
            _ => None,
        }
    }

    fn list(&self) -> Option<impl Iterator<Item = &str>> {
        let Self::Text(text) = self else {
            return None;
        };
        Some(text.split(',').filter(|s| !s.is_empty()))
    }

    /// An `Action<bool>` argument.
    #[must_use]
    pub fn from_bool(v: bool) -> Self {
        Self::Int(i32::from(v))
    }

    /// An `Action<uint>` argument.
    #[must_use]
    pub fn from_uint(v: u32) -> Self {
        Self::Int(v as i32)
    }

    /// An `Action<int, bool>` argument.
    #[must_use]
    pub fn from_int_bool(v: i32, flag: bool) -> Self {
        Self::Text(format!("{}{v}", u8::from(flag)))
    }

    /// An `Action<string, bool>` argument.
    #[must_use]
    pub fn from_text_bool(v: &str, flag: bool) -> Self {
        Self::Text(format!("{}{v}", u8::from(flag)))
    }

    /// An `Action<IReadOnlyList<float>>` argument (each written so it reads
    /// back exactly).
    #[must_use]
    pub fn from_float_list(v: &[f32]) -> Self {
        Self::Text(v.iter().map(f32::to_string).collect::<Vec<_>>().join(","))
    }

    /// An `Action<IReadOnlyList<int>>` argument.
    #[must_use]
    pub fn from_int_list(v: &[i32]) -> Self {
        Self::Text(v.iter().map(i32::to_string).collect::<Vec<_>>().join(","))
    }
}

wire_record! {
    /// A window from markup ([`crate::Import::UiPanelRegister`]; OpenAC
    /// `RegisterPanelContent` with a `PluginPanelDescriptor`). Reply
    /// `Result<u32, String>`: the panel's number, or why the markup is
    /// refused.
    pub struct PanelRegistration {
        /// The plugin's own short name for the window.
        pub window_id: String,
        /// The caption.
        pub title: String,
        /// Initials on the dock slot when there is no icon.
        pub icon_text: Option<String>,
        /// The dock icon (a full image id or a bare index; 0 none).
        pub icon_surface_id: u32,
        /// Open at once.
        pub start_visible: bool,
        /// A slot on the plugin dock.
        pub show_in_side_panel: bool,
        /// The markup.
        pub markup: String,
        /// The binding type's name, for messages.
        pub binding_type: String,
        /// Every binding name the markup may use.
        pub bindings: Vec<BindingDecl>,
        /// The window's own dock icon: a `.png` in the plugin's folder, by a
        /// path relative to it, tried ahead of the plugin's `icon.png`.
        since (1, 4) pub icon_file: Option<String>,
    }
}

wire_record! {
    /// One pushed value.
    pub struct NamedValue {
        /// The binding name.
        pub name: String,
        /// Its value.
        pub value: UiValue,
    }
}

wire_record! {
    /// Bound values for a panel ([`crate::Import::UiPanelSet`]); reply
    /// `Result<(), String>` (all or nothing).
    pub struct PanelValues {
        /// The panel.
        pub panel: u32,
        /// The values.
        pub values: Vec<NamedValue>,
    }
}

wire_record! {
    /// Show or hide one of the plugin's windows by id or title
    /// ([`crate::Import::UiPanelShow`]); reply `bool`.
    pub struct ViewShow {
        /// The window's id or title.
        pub view: String,
        /// Show (true) or hide.
        pub show: bool,
    }
}

wire_record! {
    /// A named control in one of the plugin's windows
    /// ([`crate::Import::UiControlExists`]); reply `bool`.
    pub struct ControlRef {
        /// The window's id or title.
        pub view: String,
        /// The control's `name` (or `id`).
        pub control: String,
    }
}

wire_record! {
    /// Replace a control's text ([`crate::Import::UiControlLabel`]); reply
    /// `bool`.
    pub struct ControlLabel {
        /// The window's id or title.
        pub view: String,
        /// The control.
        pub control: String,
        /// The text.
        pub label: String,
    }
}

wire_record! {
    /// Show or hide a control ([`crate::Import::UiControlVisible`]); reply
    /// `bool`.
    pub struct ControlVisible {
        /// The window's id or title.
        pub view: String,
        /// The control.
        pub control: String,
        /// Shown.
        pub visible: bool,
    }
}

wire_record! {
    /// One of the client's windows ([`crate::Import::UiClientWindow`]):
    /// `window` numbers Inventory, Character, CharacterInformation,
    /// Spellbook, Map, Options, Social, Journal, PositiveEffects,
    /// NegativeEffects, LinkStatus, Vitae, Radar (0..=12); `action` 0
    /// toggle, 1 show, 2 hide, 3 is it visible. Reply `bool`.
    pub struct ClientWindowRequest {
        /// Which window.
        pub window: u32,
        /// What to do.
        pub action: u8,
    }
}

wire_record! {
    /// A canvas ([`crate::Import::UiCanvasRegister`]; OpenAC
    /// `PluginCanvasDescriptor`). Reply `Result<u32, String>`.
    pub struct CanvasRegistration {
        /// The plugin's own name for it.
        pub canvas_id: String,
        /// Width, pixels.
        pub width: i32,
        /// Height, pixels.
        pub height: i32,
        /// Anchor: TopLeft, TopCenter, TopRight, CenterLeft, Center,
        /// CenterRight, BottomLeft, BottomCenter, BottomRight (0..=8).
        pub anchor: u8,
        /// Pixels from the anchor.
        pub offset_x: f32,
        /// Pixels from the anchor.
        pub offset_y: f32,
        /// Shown at once.
        pub start_visible: bool,
        /// Takes pointer input while the plugin listens.
        pub accepts_pointer_input: bool,
    }
}

wire_record! {
    /// A paint colour: red, green, blue, alpha bytes.
    pub struct Color {
        /// Red.
        pub r: u8,
        /// Green.
        pub g: u8,
        /// Blue.
        pub b: u8,
        /// Alpha.
        pub a: u8,
    }
}

wire_record! {
    /// A rectangle in canvas pixels.
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
}

wire_enum! {
    /// One drawing step (OpenAC `IPluginPainter`).
    #[derive(Debug, Clone, PartialEq)]
    pub enum PaintCmd ("paint command") {
        /// Fill the canvas.
        Clear(color: Color) = 0,
        /// Fill a rectangle.
        FillRect(rect: Rect, color: Color) = 1,
        /// Outline a rectangle (inside its edges), with a thickness.
        StrokeRect(rect: Rect, color: Color, thickness: f32) = 2,
        /// A line: from x, from y, to x, to y, colour, thickness.
        Line(x0: f32, y0: f32, x1: f32, y1: f32, color: Color, thickness: f32) = 3,
        /// Text at x, y (top-left), colour, outline, in one of the plugin's
        /// fonts ([`crate::Import::UiFontBundled`], [`crate::Import::UiFontLoad`];
        /// 0: the interface font). Text in a font the plugin no longer holds
        /// draws nothing. The font came with API 1.4: an older plugin's text
        /// is in the interface font.
        Text(text: String, x: f32, y: f32, color: Color, outline: bool; since (1, 4) font: u32) = 4,
        /// An image handle over a rectangle, tinted.
        Image(image: u32, rect: Rect, tint: Color) = 5,
        /// An image over a rectangle turned (radians, clockwise) and scaled
        /// about a pivot (pixels from the rectangle's top-left).
        ImageTransformed(image: u32, rect: Rect, tint: Color, rotation: f32, pivot_x: f32, pivot_y: f32, scale_x: f32, scale_y: f32) = 6,
        /// Clip what follows.
        PushClip(rect: Rect) = 7,
        /// Undo the last clip.
        PopClip = 8,
    }
}

wire_record! {
    /// A canvas's new paint ([`crate::Import::UiCanvasPaint`]); reply
    /// `Result<(), String>`.
    pub struct CanvasPaint {
        /// The canvas.
        pub canvas: u32,
        /// What it shows from now on.
        pub commands: Vec<PaintCmd>,
    }
}

wire_enum! {
    /// A change to a canvas.
    #[derive(Debug, Clone, PartialEq, Default)]
    pub enum CanvasChange ("canvas change") {
        /// Show or hide.
        Visible(visible: bool) = 0,
        /// Move its anchor (0..=8).
        Anchor(anchor: u8) = 1,
        /// Move its offset.
        Offset(x: f32, y: f32) = 2,
        /// Start or stop listening for its pointer (OpenAC setting or
        /// clearing `PointerHandler`).
        Listen(listening: bool) = 3,
        /// End the press it holds (OpenAC `ReleasePointer`).
        #[default]
        ReleasePointer = 4,
    }
}

wire_record! {
    /// One canvas change ([`crate::Import::UiCanvasSet`]); reply `bool`.
    pub struct CanvasSet {
        /// The canvas.
        pub canvas: u32,
        /// The change.
        pub change: CanvasChange,
    }
}

wire_record! {
    /// A canvas as it stands ([`crate::Import::UiCanvasState`] replies
    /// `Option<CanvasState>`).
    pub struct CanvasState {
        /// Width, pixels.
        pub width: i32,
        /// Height, pixels.
        pub height: i32,
        /// Shown.
        pub visible: bool,
        /// Anchor (0..=8).
        pub anchor: u8,
        /// Offset.
        pub offset_x: f32,
        /// Offset.
        pub offset_y: f32,
        /// The plugin listens for its pointer.
        pub listening: bool,
    }
}

wire_record! {
    /// A measured piece of text ([`crate::Import::UiMeasureText`]).
    pub struct TextSize {
        /// Width, pixels.
        pub width: f32,
        /// The font's line height, pixels.
        pub height: f32,
    }
}

wire_record! {
    /// An image the plugin holds (handle 0: refused, see the session log).
    pub struct ImageInfo {
        /// The handle.
        pub handle: u32,
        /// Width, pixels.
        pub width: u32,
        /// Height, pixels.
        pub height: u32,
    }
}

wire_record! {
    /// The plugin's own pixels ([`crate::Import::UiImageRgba`]): 8-bit
    /// RGBA rows from the top, under the plugin's own name for them.
    pub struct RgbaImage {
        /// The plugin's name for the image.
        pub name: String,
        /// Width, pixels.
        pub width: u32,
        /// Height, pixels.
        pub height: u32,
        /// `width * height * 4` bytes.
        pub rgba: Vec<u8>,
    }
}

wire_record! {
    /// The plugin's own art as a file ([`crate::Import::UiImageDecode`]):
    /// PNG, JPEG, BMP, TGA or GIF bytes, under the plugin's own name for
    /// them. The client decodes them; a GIF gives its first frame.
    pub struct EncodedImage {
        /// The plugin's name for the image (unique within the plugin, such
        /// as its file path).
        pub name: String,
        /// The encoded file.
        pub bytes: Vec<u8>,
    }
}

wire_record! {
    /// A point on the global map grid: east-west and north-south in map
    /// units (240 m, as the location readout), elevation in the same units.
    pub struct MapPoint {
        /// East-west, growing east.
        pub east_west: f64,
        /// North-south, growing north.
        pub north_south: f64,
        /// Height.
        pub elevation: f64,
    }
}

wire_record! {
    /// A line of text over an object (OpenAC `PluginWorldLabel`).
    pub struct WorldLabel {
        /// The object.
        pub object_id: u32,
        /// The text (one line, at most 128 characters).
        pub text: String,
        /// Red, 0..=1.
        pub r: f32,
        /// Green.
        pub g: f32,
        /// Blue.
        pub b: f32,
        /// Alpha.
        pub a: f32,
        /// Metres added to the object's height.
        pub height_offset: f32,
        /// Which line over the object (0 at the head).
        pub line: i32,
        /// Shown up to this many metres away.
        pub max_range: f32,
        /// A dark outline.
        pub outline: bool,
    }
}

wire_record! {
    /// A line in the world (OpenAC `PluginWorldLine`).
    pub struct WorldLine {
        /// One end.
        pub start: MapPoint,
        /// The other end.
        pub end: MapPoint,
        /// `0xRRGGBB`.
        pub color_rgb: u32,
        /// Follows the ground under it along its length: the terrain, or the
        /// floors (stairs, ramps, bridges) when both ends stand on floors.
        pub follow_terrain: bool,
        /// Thickness, metres.
        pub width_metres: f32,
    }
}

wire_record! {
    /// A layer's lines ([`crate::Import::UiLinesSet`]); reply `bool`.
    pub struct LineSet {
        /// The layer.
        pub layer: u32,
        /// Its lines from now on.
        pub lines: Vec<WorldLine>,
    }
}

wire_record! {
    /// A filled triangle in the world: flat between its corners,
    /// see-through by its colours' alpha, drawn from both sides and unlit.
    /// Hills and walls in front of it hide it; it writes no depth, so
    /// overlapping triangles blend. It does not follow the ground: one
    /// meant to lie on it is lifted a little (about 0.1 m) by the plugin.
    /// API 1.3.
    pub struct WorldTriangle {
        /// The first corner.
        pub a: MapPoint,
        /// The second corner.
        pub b: MapPoint,
        /// The third corner.
        pub c: MapPoint,
        /// Corner `a`'s colour, `0xRRGGBBAA`; its alpha is how opaque the
        /// fill is there.
        pub color_a: u32,
        /// Corner `b`'s colour, blended across the face.
        pub color_b: u32,
        /// Corner `c`'s colour.
        pub color_c: u32,
    }
}

wire_record! {
    /// A layer's triangles ([`crate::Import::UiTrianglesSet`]); reply
    /// `bool`. API 1.3.
    pub struct TriangleSet {
        /// The layer.
        pub layer: u32,
        /// Its triangles from now on. Refused (false, the layer keeps what
        /// it had) when they and the plugin's other layers' triangles come to
        /// more than 65,536.
        pub triangles: Vec<WorldTriangle>,
    }
}

wire_enum! {
    /// What a world marker is pinned to. API 1.3.
    #[derive(Debug, Clone, PartialEq, Default)]
    pub enum MarkerAnchor ("marker anchor") {
        /// Nothing: a marker pinned to nothing is dropped.
        #[default]
        None = 0,
        /// An object, by its id. While the client does not hold it the
        /// marker is not drawn, and it comes back when it does.
        Object(object_id: u32) = 1,
        /// A fixed position, and whether it is on the landscape rather than
        /// inside a building or dungeon.
        Position(point: MapPoint, is_outdoor: bool) = 2,
    }
}

wire_record! {
    /// An image hung in the world at a constant size on the screen. It is
    /// drawn after the world, like a label, so walls and hills do not hide
    /// it unless it asks with `hidden_by_world`. API 1.3.
    pub struct WorldIcon {
        /// Over an object, the client lays its icons out in rows above its
        /// head and its labels; at a position, the icon is centred on it.
        pub anchor: MarkerAnchor,
        /// The image, one of this plugin's own handles. One that is
        /// released is not drawn.
        pub image: u32,
        /// The side of the square the image is fitted into, interface points
        /// (scaled with the interface by the player's UI Scale; at 100% a
        /// pixel, two on a Retina Mac), kept to 8..=128; an icon whose size is
        /// not a finite number is dropped.
        pub size: f32,
        /// Multiplied into the image, `0xRRGGBBAA`: white leaves it as it
        /// is, and the alpha is the icon's opacity.
        pub tint_rgba: u32,
        /// A thin frame around the image, `0xRRGGBBAA`.
        pub border_rgba: Option<u32>,
        /// Shown up to this many metres away, fading over the last fifth;
        /// an icon whose range is not a positive finite number is dropped.
        pub max_range: f32,
        /// The whole icon goes while walls, doors, hills or anything else in
        /// the world stands between the camera and its anchor. It comes and
        /// goes a frame or two after the view changes.
        pub hidden_by_world: bool,
    }
}

wire_record! {
    /// A marker layer's icons ([`crate::Import::UiMarkersSetIcons`]); reply
    /// `bool`: false, and the layer keeps what it had, when the plugin has
    /// no such layer or this set and its other layers' icons come to more
    /// than 256. API 1.3.
    pub struct IconSet {
        /// The layer.
        pub layer: u32,
        /// Its icons from now on.
        pub icons: Vec<WorldIcon>,
    }
}

wire_enum! {
    /// What form a ground shape takes. API 1.3.
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
    pub enum GroundShapeKind ("ground shape kind") {
        /// Nothing: a shape of no kind is dropped.
        #[default]
        None = 0,
        /// A band all the way round the anchor, from the radius less the
        /// width out to the radius.
        Ring = 1,
        /// A circle around the anchor, filled from its centre.
        Disc = 2,
        /// Part of a circle: a wedge filled from the centre, or a band like
        /// a ring's.
        Arc = 3,
    }
}

wire_record! {
    /// A shape lying on the ground around an anchor: a ring under a target,
    /// a disc over an area, a wedge in front of a creature. Drawn in the
    /// world, so walls and hills hide it, and outdoors it follows the slope
    /// of the land. API 1.3.
    pub struct GroundShape {
        /// Around an object's feet, following it, or around a fixed position.
        pub anchor: MarkerAnchor,
        /// What form it takes.
        pub kind: GroundShapeKind,
        /// The outer radius, metres, 0.1 to 100; a shape outside that is
        /// dropped.
        pub radius: f32,
        /// How wide a ring's or unfilled arc's band is, metres, inward from
        /// the radius: more than zero and at most the radius, or the shape
        /// is dropped. Not used by discs and filled arcs.
        pub width: f32,
        /// Where an arc starts, as a compass bearing in degrees (0 north, 90
        /// east); with `faces_object`, from the way the object faces.
        pub start_degrees: f32,
        /// How far round an arc goes, clockwise from its start, in degrees:
        /// more than 0 and at most 360, or the arc is dropped.
        pub sweep_degrees: f32,
        /// An arc filled from the centre, a wedge, rather than a band.
        pub filled: bool,
        /// An arc over an object turns with it.
        pub faces_object: bool,
        /// Its colour, `0xRRGGBBAA`; the alpha is how opaque it is.
        pub color_rgba: u32,
    }
}

wire_record! {
    /// A marker layer's ground shapes
    /// ([`crate::Import::UiMarkersSetShapes`]); reply `bool`: false, and the
    /// layer keeps what it had, when the plugin has no such layer or this
    /// set and its other layers' shapes come to more than 256. API 1.3.
    pub struct ShapeSet {
        /// The layer.
        pub layer: u32,
        /// Its shapes from now on.
        pub shapes: Vec<GroundShape>,
    }
}

wire_record! {
    /// A projectile marker (OpenAC `PluginProjectileDebugSample`).
    pub struct ProjectileSample {
        /// Where the step ended.
        pub position: MapPoint,
        /// Unobstructed.
        pub is_clear: bool,
        /// The probe's radius, metres.
        pub radius: f32,
    }
}

wire_enum! {
    /// A handler's argument.
    #[derive(Debug, Clone, PartialEq, Default)]
    pub enum ActionValue ("action value") {
        /// A click.
        #[default]
        None = 0,
        /// A row (`Action<int>`).
        Int(v: i32) = 1,
        /// A slider value (`Action<float>`).
        Float(v: f32) = 2,
        /// Text (`Action<string>`).
        Text(v: String) = 3,
    }
}

wire_record! {
    /// A handler of one of the plugin's panels fired.
    pub struct UiAction {
        /// The panel.
        pub panel: u32,
        /// The handler's binding name (`onclick="{Go}"` names `Go`).
        pub handler: String,
        /// Its argument.
        pub value: ActionValue,
    }
}

wire_record! {
    /// Pointer input on one of the plugin's canvases (OpenAC
    /// `PluginPointerEvent`).
    pub struct PointerEvent {
        /// The canvas.
        pub canvas: u32,
        /// 0 down, 1 up, 2 move, 3 wheel, 4 cancelled; from API 1.6 also
        /// 5 hover and 6 leave (the constants [`PointerEvent::DOWN`] to
        /// [`PointerEvent::LEAVE`]).
        pub kind: u8,
        /// Canvas pixels from its top-left (outside it only in a drag).
        pub x: f32,
        /// Canvas pixels.
        pub y: f32,
        /// 0 none, 1 left, 2 right, 3 middle.
        pub button: u8,
        /// Bits: 1 shift, 2 control, 4 alt.
        pub modifiers: u8,
        /// Wheel notches, positive away from the user.
        pub wheel_delta: i32,
    }
}

/// The API version that added the hover kinds, [`PointerEvent::HOVER`] and
/// [`PointerEvent::LEAVE`].
pub const POINTER_HOVER_SINCE: crate::ApiVersion = crate::ApiVersion::new(1, 6);

impl PointerEvent {
    /// A button went down inside the canvas.
    pub const DOWN: u8 = 0;
    /// The held button came up (wherever the pointer is).
    pub const UP: u8 = 1;
    /// The pointer moved with a button held (wherever it is).
    pub const MOVE: u8 = 2;
    /// The wheel turned over the canvas.
    pub const WHEEL: u8 = 3;
    /// A press ended without its up (the canvas was hidden or removed, or
    /// the press was taken away).
    pub const CANCELLED: u8 = 4;
    /// The pointer is over the canvas with no button held (API 1.6): sent
    /// when it comes onto the canvas and as it moves there, at most one
    /// for a canvas in each delivery (the latest position); `button` is 0.
    pub const HOVER: u8 = 5;
    /// The pointer stopped being over the canvas after a [`Self::HOVER`]
    /// (API 1.6): it moved off it or onto something drawn above it, a
    /// press began elsewhere, the canvas was hidden, stopped taking the
    /// pointer or was removed, or the window went. One leave ends each
    /// stretch of hovers; a press on the canvas does not end it.
    pub const LEAVE: u8 = 6;

    /// The API version that added this event's kind: a plugin built for an
    /// older version is never sent it (an older plugin may read any kind it
    /// does not know as a drag).
    #[must_use]
    pub const fn since(&self) -> crate::ApiVersion {
        match self.kind {
            Self::HOVER | Self::LEAVE => POINTER_HOVER_SINCE,
            _ => crate::ApiVersion::new(1, 0),
        }
    }
}

wire_record! {
    /// A key and its modifiers (OpenAC `PluginKeyChord`): `key` is OpenAC's
    /// `PluginKey` number (`ac_plugin_ui::hotkeys::PluginKey::code`).
    pub struct KeyChord {
        /// The key (0: none).
        pub key: u32,
        /// Ctrl held.
        pub ctrl: bool,
        /// Alt held.
        pub alt: bool,
        /// Shift held.
        pub shift: bool,
    }
}

wire_record! {
    /// A hotkey to register ([`crate::Import::UiHotkeyRegister`]).
    pub struct HotkeyRegistration {
        /// The plugin's id for it (overrides are stored under
        /// `<plugin id>:<id>`).
        pub id: String,
        /// Its name for the player.
        pub display_name: String,
        /// The chord it asks for.
        pub chord: KeyChord,
    }
}

wire_record! {
    /// How a hotkey stands (OpenAC `IsBound`, `EffectiveChord`).
    pub struct HotkeyState {
        /// It fires: a window is attached and nothing else holds the chord.
        pub is_bound: bool,
        /// The chord it binds to (empty with no window attached).
        pub effective: KeyChord,
    }
}

/// A plugin theme the player can pick under Appearance (OpenAC
/// `PluginUiThemeKind`). The kinds after [`Self::Brass`] came with API 1.4:
/// a plugin built for an older version is told [`Self::Moss`] under any of
/// them, with the theme's real name and colours, so it still draws in them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ThemeKind {
    /// The client's own black and gold; themed windows draw unthemed.
    #[default]
    Classic,
    /// Charcoal with moss-green accents.
    Moss,
    /// Warm graphite with brass accents.
    Brass,
    /// Dark violet with lavender accents.
    LavenderDusk,
    /// Blue slate with orange accents.
    Ember,
    /// Dark warm grey with coral-red accents.
    Bloodmoon,
    /// Blue-black with icy cyan accents.
    Glacier,
    /// A light theme: paper with ink text and rust accents.
    Parchment,
}

/// The API version that added the theme kinds after [`ThemeKind::Brass`].
const THEME_KINDS_SINCE: crate::ApiVersion = crate::ApiVersion::new(1, 4);

impl ThemeKind {
    /// The kind a plugin built for API `version` reads for this one.
    #[must_use]
    pub fn known_at(self, version: crate::ApiVersion) -> Self {
        match self {
            Self::Classic | Self::Moss | Self::Brass => self,
            _ if THEME_KINDS_SINCE.is_at_most(version) => self,
            _ => Self::Moss,
        }
    }
}

impl Wire for ThemeKind {
    const SHAPE: u64 = crate::wire::Shape::new("enum")
        .tag(0)
        .name("Classic")
        .tag(1)
        .name("Moss")
        .tag(2)
        .name("Brass")
        .tag(3)
        .name("LavenderDusk")
        .tag(4)
        .name("Ember")
        .tag(5)
        .name("Bloodmoon")
        .tag(6)
        .name("Glacier")
        .tag(7)
        .name("Parchment")
        .finish();
    const VERSIONED: bool = true;
    fn shape_at(version: crate::ApiVersion) -> u64 {
        if THEME_KINDS_SINCE.is_at_most(version) {
            return Self::SHAPE;
        }
        crate::wire::Shape::new("enum")
            .tag(0)
            .name("Classic")
            .tag(1)
            .name("Moss")
            .tag(2)
            .name("Brass")
            .finish()
    }
    fn encode(&self, w: &mut Writer) {
        (*self as u8).encode(w);
    }
    fn read(r: &mut Reader<'_>) -> Self {
        match u8::read(r) {
            0 => Self::Classic,
            1 => Self::Moss,
            2 => Self::Brass,
            3 => Self::LavenderDusk,
            4 => Self::Ember,
            5 => Self::Bloodmoon,
            6 => Self::Glacier,
            7 => Self::Parchment,
            tag => {
                r.fail(DecodeError::UnknownTag {
                    what: "theme kind",
                    tag: u32::from(tag),
                });
                Self::Classic
            }
        }
    }
    fn encode_at(&self, version: crate::ApiVersion, w: &mut Writer) {
        self.known_at(version).encode(w);
    }
}

wire_record! {
    /// The colours a plugin theme draws themed windows in (OpenAC
    /// `PluginThemePalette`).
    pub struct ThemePalette {
        /// The window's face.
        pub background: Color,
        /// Inputs, lists and other sunken areas.
        pub field: Color,
        /// Window and control edges.
        pub border: Color,
        /// Body text.
        pub text: Color,
        /// Captions, hints and disabled labels.
        pub muted: Color,
        /// Highlights: a switch that is on, focus, links.
        pub accent: Color,
        /// The fill behind a selected row or tab.
        pub selected: Color,
    }
}

impl ThemePalette {
    /// Whether the face is pale: a light palette, meant for dark text and
    /// art.
    #[must_use]
    pub fn is_light(&self) -> bool {
        let b = &self.background;
        0.2126 * f64::from(b.r) + 0.7152 * f64::from(b.g) + 0.0722 * f64::from(b.b) > 127.5
    }
}

wire_record! {
    /// The plugin theme the player picked ([`crate::Import::UiTheme`],
    /// [`crate::Event::ThemeChanged`]; OpenAC `PluginUiThemeInfo`), so a
    /// canvas can draw like the plugin's themed windows.
    pub struct ThemeInfo {
        /// Which theme.
        pub kind: ThemeKind,
        /// What the Appearance picker calls it: "Classic", "Charcoal +
        /// moss", "Warm graphite + brass", "Lavender Dusk", "Ember",
        /// "Bloodmoon", "Glacier" or "Parchment".
        pub display_name: String,
        /// Its colours; none under Classic, where themed windows draw
        /// unthemed.
        pub palette: Option<ThemePalette>,
    }
}

impl ThemeInfo {
    /// Whether the theme is light: a pale face meant for dark text and art.
    /// False under Classic.
    #[must_use]
    pub fn is_light(&self) -> bool {
        self.palette.as_ref().is_some_and(ThemePalette::is_light)
    }
}

wire_enum! {
    /// The weights the client's bundled font comes in.
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
    pub enum FontWeight ("font weight") {
        /// Body text.
        #[default]
        Regular = 0,
        /// The weight themed window titles are set in.
        SemiBold = 1,
    }
}

/// The most distinct fonts one plugin may hold.
pub const FONT_MAXIMUM_COUNT: u32 = 16;
/// The most memory one plugin's own fonts may take: the files and their
/// atlases, and the atlases of bundled fonts it alone holds, bytes.
pub const FONT_MAXIMUM_BYTES: u64 = 16 * 1024 * 1024;
/// The largest font file a plugin may hand over, bytes.
pub const FONT_MAXIMUM_FILE_BYTES: usize = 15 * 1024 * 1024;
/// The most characters one font may prepare.
pub const FONT_MAXIMUM_GLYPHS: u32 = 2048;
/// The smallest size a font may be asked for, pixels.
pub const FONT_MINIMUM_PIXEL_SIZE: f32 = 6.0;
/// The largest size a font may be asked for, pixels.
pub const FONT_MAXIMUM_PIXEL_SIZE: f32 = 64.0;

wire_record! {
    /// The bundled font in a weight at a size ([`crate::Import::UiFontBundled`]).
    pub struct BundledFontRequest {
        /// The size, pixels.
        pub pixel_size: f32,
        /// The weight.
        pub weight: FontWeight,
    }
}

wire_record! {
    /// An inclusive range of code points.
    pub struct CodepointRange {
        /// The first.
        pub first: u32,
        /// The last.
        pub last: u32,
    }
}

wire_record! {
    /// A font the plugin ships ([`crate::Import::UiFontLoad`]).
    pub struct FontLoadRequest {
        /// The plugin's own name for the file, unique within the plugin.
        pub name: String,
        /// The TrueType or OpenType file.
        pub bytes: Vec<u8>,
        /// The size, pixels.
        pub pixel_size: f32,
        /// The characters to prepare, where the font has them; none: U+0020 to
        /// U+024F, U+0370 to U+052F and U+2000 to U+206F.
        pub ranges: Vec<CodepointRange>,
    }
}

wire_record! {
    /// What a font request is answered with; all zero when it was refused.
    pub struct FontInfo {
        /// The client's number for it; 0 means no font.
        pub handle: u32,
        /// The size prepared: the size asked for, to the nearest quarter
        /// pixel.
        pub pixel_size: f32,
        /// How far apart two lines sit, pixels.
        pub line_height: f32,
        /// How far below the top of a line the baseline sits, pixels.
        pub ascent: f32,
    }
}

wire_record! {
    /// Text to measure in a font ([`crate::Import::UiFontMeasure`]).
    pub struct FontMeasure {
        /// The font.
        pub font: u32,
        /// The text.
        pub text: String,
    }
}

/// The sizes themed windows are drawn with, in interface pixels, so a canvas
/// drawn to match uses the same ones. A plugin takes the values of the SDK it
/// was built with.
pub mod theme_metrics {
    /// The corner radius of a themed window.
    pub const WINDOW_RADIUS: f32 = 10.0;
    /// The corner radius of a button, input or other control.
    pub const CONTROL_RADIUS: f32 = 6.0;
    /// The height of a themed window's title band.
    pub const HEADER_HEIGHT: f32 = 24.0;
    /// The width of a switch's track.
    pub const SWITCH_WIDTH: f32 = 26.0;
    /// The height of a switch's track.
    pub const SWITCH_HEIGHT: f32 = 14.0;
    /// The diameter of a switch's knob.
    pub const SWITCH_KNOB: f32 = 8.0;
}

wire_record! {
    /// A rebind ([`crate::Import::UiHotkeyRebind`]).
    pub struct HotkeyRebind {
        /// The hotkey.
        pub id: String,
        /// The new chord.
        pub chord: KeyChord,
    }
}

wire_enum! {
    /// Something a plugin hears from its UI ([`crate::Event::Ui`]).
    #[derive(Debug, Clone, PartialEq)]
    pub enum UiEvent ("ui event") {
        /// A panel handler fired.
        Action(action: UiAction) = 0,
        /// Pointer input on a canvas.
        Pointer(event: PointerEvent) = 1,
        /// One of the plugin's hotkeys was pressed (its id).
        Hotkey(id: String) = 2,
        /// The client's key map fired an input action (the plugin listens,
        /// [`crate::Import::UiInputActionsListen`]): the action's name as
        /// the key bindings store it (`MovementForward`, `UseQuickSlot_1`)
        /// and how it fired (`Press`, `Release`, `Click`, `DoubleClick`).
        InputAction(action: String, activation: String) = 3,
    }
}

impl Default for UiEvent {
    fn default() -> Self {
        Self::Action(UiAction::default())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ui_records_round_trip() {
        let registration = PanelRegistration {
            window_id: "main".into(),
            title: "Demo".into(),
            icon_text: Some("UD".into()),
            icon_surface_id: 0x0600_2C41,
            start_visible: true,
            show_in_side_panel: true,
            markup: "<panel/>".into(),
            binding_type: "Demo".into(),
            bindings: vec![
                BindingDecl {
                    name: "Go".into(),
                    kind: BindingKind::Action,
                },
                BindingDecl {
                    name: "Rows".into(),
                    kind: BindingKind::TextList,
                },
            ],
            icon_file: Some("icons/demo.png".into()),
        };
        assert_eq!(
            PanelRegistration::from_bytes(&registration.to_bytes()),
            Ok(registration)
        );
        let paint = CanvasPaint {
            canvas: 3,
            commands: vec![
                PaintCmd::Clear(Color::default()),
                PaintCmd::Line(
                    0.0,
                    1.0,
                    2.0,
                    3.0,
                    Color {
                        r: 1,
                        g: 2,
                        b: 3,
                        a: 4,
                    },
                    1.5,
                ),
                PaintCmd::Text("hi".into(), 1.0, 2.0, Color::default(), true, 9),
                PaintCmd::ImageTransformed(
                    7,
                    Rect::default(),
                    Color::default(),
                    0.5,
                    1.0,
                    2.0,
                    1.0,
                    1.0,
                ),
                PaintCmd::PopClip,
            ],
        };
        assert_eq!(CanvasPaint::from_bytes(&paint.to_bytes()), Ok(paint));
        let values = PanelValues {
            panel: 1,
            values: vec![
                NamedValue {
                    name: "A".into(),
                    value: UiValue::TextList(vec!["x".into()]),
                },
                NamedValue {
                    name: "B".into(),
                    value: UiValue::Float(0.5),
                },
            ],
        };
        assert_eq!(PanelValues::from_bytes(&values.to_bytes()), Ok(values));
        let event = UiEvent::Action(UiAction {
            panel: 1,
            handler: "Go".into(),
            value: ActionValue::Text("x".into()),
        });
        assert_eq!(UiEvent::from_bytes(&event.to_bytes()), Ok(event));
        assert_eq!(
            UiValue::from_bytes(&[42]),
            Err(DecodeError::UnknownTag {
                what: "binding value",
                tag: 42
            })
        );
    }
}

impl KeyChord {
    /// The text a markup `<hotkey>` box holds for this chord (OpenAC
    /// `PluginKeyChord.ToHotkeyText`): `Ctrl+`, `Alt+`, `Shift+` in that
    /// order, then the key's [`crate::keys::PluginKey`] name; empty without
    /// a key.
    #[must_use]
    pub fn to_hotkey_text(&self) -> String {
        let key = crate::keys::PluginKey::from_code(self.key);
        if key == crate::keys::PluginKey::Unknown {
            return String::new();
        }
        let mut text = String::new();
        for (held, name) in [
            (self.ctrl, "Ctrl+"),
            (self.alt, "Alt+"),
            (self.shift, "Shift+"),
        ] {
            if held {
                text.push_str(name);
            }
        }
        text.push_str(key.name());
        text
    }

    /// Reads a `<hotkey>` box's text (OpenAC
    /// `PluginKeyChord.TryParseHotkeyText`): modifiers in any order and any
    /// case, each once, then a key's name in any case; none for empty text
    /// (an unbound box) and for text it cannot read.
    #[must_use]
    pub fn parse_hotkey_text(text: &str) -> Option<Self> {
        if text.is_empty() {
            return None;
        }
        let parts: Vec<&str> = text.split('+').collect();
        let (name, modifiers) = parts.split_last()?;
        let mut chord = Self::default();
        for part in modifiers {
            if !chord.ctrl && part.eq_ignore_ascii_case("Ctrl") {
                chord.ctrl = true;
            } else if !chord.alt && part.eq_ignore_ascii_case("Alt") {
                chord.alt = true;
            } else if !chord.shift && part.eq_ignore_ascii_case("Shift") {
                chord.shift = true;
            } else {
                return None;
            }
        }
        let first = name.chars().next()?;
        if !first.is_ascii_alphabetic() || !name.chars().all(|c| c.is_ascii_alphanumeric()) {
            return None;
        }
        let key = crate::keys::PluginKey::ALL
            .iter()
            .copied()
            .find(|k| k.name().eq_ignore_ascii_case(name))
            .filter(|k| *k != crate::keys::PluginKey::Unknown)?;
        chord.key = key.code();
        Some(chord)
    }
}

#[cfg(test)]
mod hotkey_text_tests {
    //! OpenAC `tests/AcDream.Plugin.Tests/PluginKeyChordTextTests.cs`.
    use super::KeyChord;
    use crate::keys::PluginKey;

    fn chord(key: PluginKey, ctrl: bool, alt: bool, shift: bool) -> KeyChord {
        KeyChord {
            key: key.code(),
            ctrl,
            alt,
            shift,
        }
    }

    #[test]
    fn reads_a_hotkey_box_string() {
        // TryParseHotkeyText_ReadsAHotkeyBoxString
        for (text, expected) in [
            ("Ctrl+Alt+Shift+F", chord(PluginKey::F, true, true, true)),
            (
                "shift+ctrl+numpad5",
                chord(PluginKey::Numpad5, true, false, true),
            ),
            ("Numpad5", chord(PluginKey::Numpad5, false, false, false)),
            ("Grave", chord(PluginKey::Grave, false, false, false)),
            (
                "NumpadEnter",
                chord(PluginKey::NumpadEnter, false, false, false),
            ),
            ("F12", chord(PluginKey::F12, false, false, false)),
        ] {
            assert_eq!(KeyChord::parse_hotkey_text(text), Some(expected), "{text}");
        }
    }

    #[test]
    fn refuses_what_no_plugin_hotkey_can_hold() {
        // TryParseHotkeyText_RefusesWhatNoPluginHotkeyCanHold
        for text in [
            "",
            "F13",
            "CapsLock",
            "Keypad5",
            "GraveAccent",
            "Ctrl+",
            "+F",
            "Ctrl+Ctrl+F",
            "Meta+F",
            "5",
            "A,B",
            "Unknown",
            "Ctrl+Nope",
        ] {
            assert_eq!(KeyChord::parse_hotkey_text(text), None, "{text}");
        }
    }

    #[test]
    fn spells_the_key_by_its_name_and_round_trips() {
        // ToHotkeyText_SpellsTheKeyAsItsPluginKeyName, ToHotkeyText_NoKeyIsEmpty,
        // EveryPluginKey_RoundTripsWithEveryModifierSet
        assert_eq!(
            chord(PluginKey::F, true, true, true).to_hotkey_text(),
            "Ctrl+Alt+Shift+F"
        );
        assert_eq!(
            chord(PluginKey::NumpadEnter, false, true, false).to_hotkey_text(),
            "Alt+NumpadEnter"
        );
        assert_eq!(KeyChord::default().to_hotkey_text(), "");
        assert_eq!(
            KeyChord {
                key: 999,
                ctrl: true,
                ..KeyChord::default()
            }
            .to_hotkey_text(),
            ""
        );
        for key in PluginKey::ALL.iter().skip(1) {
            for mods in 0..8 {
                let c = chord(*key, mods & 1 != 0, mods & 2 != 0, mods & 4 != 0);
                assert_eq!(KeyChord::parse_hotkey_text(&c.to_hotkey_text()), Some(c));
            }
        }
    }
}

macro_rules! typed_from {
    ($($ty:ty => $variant:ident),* $(,)?) => {$(
        impl From<$ty> for TypedValue {
            fn from(v: $ty) -> Self {
                Self::$variant(v.into())
            }
        }
    )*};
}

typed_from!(
    bool => Bool,
    i32 => Int,
    u32 => Uint,
    f32 => Float,
    &str => Text,
    String => Text,
    Vec<String> => TextList,
    Vec<u32> => UintList,
    Vec<i32> => IntList,
    Vec<bool> => BoolList,
    Option<bool> => OptionalBool,
    Vec<f32> => FloatList,
    ItemRows => Items,
);

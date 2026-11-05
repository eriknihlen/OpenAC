//! The retail dialog factory: every kind of shared dialog (confirmation,
//! wait, message, confirmation with text input, confirmation with a menu)
//! made from the dialog catalog, one open per queue key, later ones waiting
//! in order, a priority dialog suspending the open one, dialogs outside the
//! queues (key 1) open together, a dialog whose layout cannot be made
//! retried on the next tick, and a reset that completes everything.
//!
//! What a dialog's answer does is the owner's: each dialog carries the
//! owner's tag, and a closed dialog comes back from
//! [`DialogFactory::take_closed`] with its data (the result properties set)
//! and the tag, in the order they closed.

use std::collections::{BTreeMap, VecDeque};

use crate::geometry::AnchorEdges;
use crate::import::ImportedLayout;
use crate::root::{ElementMessage, UiRoot};
use crate::text::{Line, wrap_words};
use crate::tree::ElementId;
use crate::widgets::{Button, Field, Menu, MenuItem, Text};

/// The queue key of the default queue.
pub const DEFAULT_QUEUE_KEY: u32 = 2;
/// The queue key of dialogs outside the queues.
pub const NON_QUEUED_KEY: u32 = 1;

/// The dialog data's property ids.
pub mod property {
    /// Preempts the queue's open dialog.
    pub const PRIORITY: u32 = 0x8D;
    /// The dialog type.
    pub const TYPE: u32 = 0x8E;
    /// The accept button's caption.
    pub const ACCEPT_LABEL: u32 = 0x90;
    /// The reject button's caption.
    pub const REJECT_LABEL: u32 = 0x91;
    /// Accepted (true) or rejected.
    pub const CONFIRMATION_RESULT: u32 = 0x92;
    /// The text-input accept caption.
    pub const TEXT_INPUT_ACCEPT_LABEL: u32 = 0x9A;
    /// The text-input reject caption.
    pub const TEXT_INPUT_REJECT_LABEL: u32 = 0x9B;
    /// The typed text ("" when rejected).
    pub const TEXT_INPUT_RESULT: u32 = 0x9C;
    /// The menu's entries.
    pub const MENU_ITEMS: u32 = 0xA6;
    /// The menu's accept caption.
    pub const MENU_ACCEPT_LABEL: u32 = 0xA8;
    /// The menu's reject caption.
    pub const MENU_REJECT_LABEL: u32 = 0xA9;
    /// The chosen entry (-1 when rejected).
    pub const MENU_SELECTION: u32 = 0xAB;
    /// The element attribute the confirmation kinds set.
    pub const ELEMENT_ATTRIBUTE_40: u32 = 0xAC;
    /// The queue key.
    pub const QUEUE_KEY: u32 = 0xC3;
    /// The message.
    pub const MESSAGE: u32 = 0xC5;
}

/// A dialog kind (the catalog's).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DialogType {
    /// Yes / no.
    Confirmation = 1,
    /// A message with no buttons (closed by its opener).
    Wait = 2,
    /// A message with OK.
    Message = 3,
    /// Text input (no presenter: as OpenAC, never made).
    TextInput = 4,
    /// Yes / no with a text field.
    ConfirmationTextInput = 5,
    /// A menu (no presenter: as OpenAC, never made).
    Menu = 6,
    /// Yes / no with a menu.
    ConfirmationMenu = 7,
}

impl DialogType {
    /// The kind of a type value.
    #[must_use]
    pub fn from_u32(v: u32) -> Option<Self> {
        Some(match v {
            1 => Self::Confirmation,
            2 => Self::Wait,
            3 => Self::Message,
            4 => Self::TextInput,
            5 => Self::ConfirmationTextInput,
            6 => Self::Menu,
            7 => Self::ConfirmationMenu,
            _ => return None,
        })
    }

    /// The kind's root element in the catalog.
    #[must_use]
    pub fn root_element_id(self) -> u32 {
        match self {
            Self::Confirmation => 0x15,
            Self::Wait => 0x31,
            Self::Message => 0x24,
            Self::TextInput => 0x28,
            Self::ConfirmationTextInput => 0x2C,
            Self::Menu => 0x1B,
            Self::ConfirmationMenu => 0x1F,
        }
    }
}

/// A dialog property's value.
#[derive(Debug, Clone, PartialEq)]
pub enum DialogValue {
    /// A flag.
    Bool(bool),
    /// An unsigned number.
    U32(u32),
    /// A signed number.
    I32(i32),
    /// A string.
    Str(String),
    /// A list of strings.
    List(Vec<String>),
}

/// A dialog's properties.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DialogData {
    values: BTreeMap<u32, DialogValue>,
}

impl DialogData {
    /// Set a property.
    #[must_use]
    pub fn with(mut self, id: u32, value: DialogValue) -> Self {
        self.set(id, value);
        self
    }

    /// Set a property.
    pub fn set(&mut self, id: u32, value: DialogValue) {
        self.values.insert(id, value);
    }

    /// Whether a property is set.
    #[must_use]
    pub fn contains(&self, id: u32) -> bool {
        self.values.contains_key(&id)
    }

    /// A flag (numbers count as true when not 0).
    #[must_use]
    pub fn get_bool(&self, id: u32) -> bool {
        match self.values.get(&id) {
            Some(DialogValue::Bool(b)) => *b,
            Some(DialogValue::U32(v)) => *v != 0,
            Some(DialogValue::I32(v)) => *v != 0,
            _ => false,
        }
    }

    /// An unsigned number (a negative one counts as missing).
    #[must_use]
    pub fn get_u32(&self, id: u32, default: u32) -> u32 {
        match self.values.get(&id) {
            Some(DialogValue::U32(v)) => *v,
            Some(DialogValue::I32(v)) if *v >= 0 => v.unsigned_abs(),
            _ => default,
        }
    }

    /// A signed number.
    #[must_use]
    pub fn get_i32(&self, id: u32, default: i32) -> i32 {
        match self.values.get(&id) {
            Some(DialogValue::I32(v)) => *v,
            Some(DialogValue::U32(v)) => i32::try_from(*v).unwrap_or(default),
            _ => default,
        }
    }

    /// A string.
    #[must_use]
    pub fn get_str(&self, id: u32) -> Option<&str> {
        match self.values.get(&id) {
            Some(DialogValue::Str(s)) => Some(s),
            _ => None,
        }
    }

    /// A list of strings.
    #[must_use]
    pub fn get_list(&self, id: u32) -> Option<&[String]> {
        match self.values.get(&id) {
            Some(DialogValue::List(l)) => Some(l),
            _ => None,
        }
    }

    /// The dialog's kind.
    #[must_use]
    pub fn kind(&self) -> Option<DialogType> {
        DialogType::from_u32(self.get_u32(property::TYPE, 0))
    }

    fn typed(kind: DialogType, message: &str, attribute: bool) -> Self {
        let d = Self::default()
            .with(property::TYPE, DialogValue::U32(kind as u32))
            .with(property::MESSAGE, DialogValue::Str(message.to_owned()));
        if attribute {
            d.with(property::ELEMENT_ATTRIBUTE_40, DialogValue::Bool(true))
        } else {
            d
        }
    }

    /// A yes/no question.
    #[must_use]
    pub fn confirmation(message: &str) -> Self {
        Self::typed(DialogType::Confirmation, message, true)
    }

    /// A message the opener closes.
    #[must_use]
    pub fn wait(message: &str) -> Self {
        Self::typed(DialogType::Wait, message, true)
    }

    /// A message with OK.
    #[must_use]
    pub fn message(message: &str) -> Self {
        Self::typed(DialogType::Message, message, false)
    }

    /// A question answered with typed text.
    #[must_use]
    pub fn confirmation_text_input(message: &str) -> Self {
        Self::typed(DialogType::ConfirmationTextInput, message, true)
    }

    /// A question answered by choosing from `items` (`selected` first).
    #[must_use]
    pub fn confirmation_menu(items: &[String], selected: i32) -> Self {
        Self::default()
            .with(
                property::TYPE,
                DialogValue::U32(DialogType::ConfirmationMenu as u32),
            )
            .with(property::ELEMENT_ATTRIBUTE_40, DialogValue::Bool(true))
            .with(property::MENU_ITEMS, DialogValue::List(items.to_vec()))
            .with(property::MENU_SELECTION, DialogValue::I32(selected))
    }

    /// The same, on queue `key`.
    #[must_use]
    pub fn on_queue(self, key: u32) -> Self {
        self.with(property::QUEUE_KEY, DialogValue::U32(key))
    }

    /// The same, as a priority dialog.
    #[must_use]
    pub fn priority(self) -> Self {
        self.with(property::PRIORITY, DialogValue::Bool(true))
    }

    /// Whether the dialog preempts.
    #[must_use]
    pub fn is_priority(&self) -> bool {
        self.get_bool(property::PRIORITY)
    }
}

// ── Views ───────────────────────────────────────────────────────────────

/// The catalog parts every view uses.
const POPUP_ELEMENT_ID: u32 = 0x3D;
const MESSAGE_ELEMENT_ID: u32 = 0x3E;
const CONFIRMATION_ACCEPT_ID: u32 = 0x17;
const CONFIRMATION_REJECT_ID: u32 = 0x19;
const PENDING_DISPLAY_ID: u32 = 0x33;
const MESSAGE_OK_ID: u32 = 0x26;
const TEXT_INPUT_FIELD_ID: u32 = 0x2C;
const TEXT_INPUT_ACCEPT_ID: u32 = 0x2E;
const TEXT_INPUT_REJECT_ID: u32 = 0x2F;
const MENU_ELEMENT_ID: u32 = 0x21;
const MENU_ACCEPT_ID: u32 = 0x22;
const MENU_REJECT_ID: u32 = 0x23;
/// The waiting marker's states.
const PENDING_STATE: u32 = 0x18;
const NO_PENDING_STATE: u32 = 0x19;

/// One open dialog's elements.
#[derive(Debug, Clone)]
pub struct DialogView {
    kind: DialogType,
    root: ElementId,
    popup: Option<ElementId>,
    message: Option<ElementId>,
    accept: Option<ElementId>,
    reject: Option<ElementId>,
    input: Option<ElementId>,
    menu: Option<ElementId>,
    pending: Option<ElementId>,
    base_popup_height: f32,
    base_message_height: f32,
    focus_pending: bool,
}

/// What a view did with an element's message.
enum ViewAction {
    /// Nothing for this view.
    None,
    /// Handled; the dialog stays.
    Handled,
    /// The dialog closes.
    Close,
}

fn find_button(ui: &UiRoot, layout: &ImportedLayout, id: u32) -> Option<ElementId> {
    layout
        .find(id)
        .filter(|b| ui.widget::<Button>(*b).is_some())
}

fn set_label(ui: &mut UiRoot, button: Option<ElementId>, label: Option<&str>) {
    if let (Some(b), Some(label)) = (button, label)
        && let Some(w) = ui.widget_mut::<Button>(b)
    {
        w.label = Some(label.to_owned());
    }
}

impl DialogView {
    /// Make the view of `data` from `layout` (its catalog root); `None`
    /// when a part the kind needs is missing.
    fn open(ui: &mut UiRoot, layout: &ImportedLayout, data: &DialogData) -> Option<Self> {
        let kind = data.kind()?;
        let popup = layout.find(POPUP_ELEMENT_ID);
        let text = |ui: &UiRoot| {
            layout
                .find(MESSAGE_ELEMENT_ID)
                .filter(|m| ui.widget::<Text>(*m).is_some())
        };
        let mut view = Self {
            kind,
            root: layout.root,
            popup,
            message: None,
            accept: None,
            reject: None,
            input: None,
            menu: None,
            pending: None,
            base_popup_height: 0.0,
            base_message_height: 0.0,
            focus_pending: false,
        };
        match kind {
            DialogType::Confirmation => {
                view.popup = Some(popup?);
                view.message = Some(text(ui)?);
                view.accept = Some(find_button(ui, layout, CONFIRMATION_ACCEPT_ID)?);
                view.reject = Some(find_button(ui, layout, CONFIRMATION_REJECT_ID)?);
                view.pending = layout.find(PENDING_DISPLAY_ID);
                set_label(ui, view.accept, data.get_str(property::ACCEPT_LABEL));
                set_label(ui, view.reject, data.get_str(property::REJECT_LABEL));
            }
            DialogType::Wait => {
                view.popup = Some(popup?);
                view.message = Some(text(ui)?);
            }
            DialogType::Message => {
                view.popup = Some(popup?);
                view.message = Some(text(ui)?);
                view.accept = Some(find_button(ui, layout, MESSAGE_OK_ID)?);
            }
            DialogType::ConfirmationTextInput => {
                view.popup = Some(popup?);
                view.message = Some(text(ui)?);
                view.input = Some(
                    layout
                        .find(TEXT_INPUT_FIELD_ID)
                        .filter(|f| ui.widget::<Field>(*f).is_some())?,
                );
                view.accept = Some(find_button(ui, layout, TEXT_INPUT_ACCEPT_ID)?);
                view.reject = Some(find_button(ui, layout, TEXT_INPUT_REJECT_ID)?);
                if let Some(f) = view.input.and_then(|i| ui.widget_mut::<Field>(i)) {
                    f.clear_on_submit = false;
                    f.record_history = false;
                }
                set_label(
                    ui,
                    view.accept,
                    data.get_str(property::TEXT_INPUT_ACCEPT_LABEL),
                );
                set_label(
                    ui,
                    view.reject,
                    data.get_str(property::TEXT_INPUT_REJECT_LABEL),
                );
                view.focus_pending = true;
            }
            DialogType::ConfirmationMenu => {
                view.menu = Some(
                    layout
                        .find(MENU_ELEMENT_ID)
                        .filter(|m| ui.widget::<Menu>(*m).is_some())?,
                );
                view.accept = Some(find_button(ui, layout, MENU_ACCEPT_ID)?);
                view.reject = Some(find_button(ui, layout, MENU_REJECT_ID)?);
                let items = data
                    .get_list(property::MENU_ITEMS)
                    .unwrap_or_default()
                    .to_vec();
                let selected = data
                    .get_i32(property::MENU_SELECTION, 0)
                    .clamp(0, i32::try_from(items.len().saturating_sub(1)).unwrap_or(0));
                if let Some(m) = view.menu.and_then(|m| ui.widget_mut::<Menu>(m)) {
                    m.items = items
                        .iter()
                        .enumerate()
                        .map(|(i, l)| MenuItem::new(l.clone(), i as u64))
                        .collect();
                    m.selected = (!items.is_empty()).then_some(selected.unsigned_abs().into());
                    m.button_label = m
                        .selected
                        .and_then(|i| items.get(i as usize).cloned())
                        .unwrap_or_default();
                }
                set_label(ui, view.accept, data.get_str(property::MENU_ACCEPT_LABEL));
                set_label(ui, view.reject, data.get_str(property::MENU_REJECT_LABEL));
            }
            DialogType::TextInput | DialogType::Menu => return None,
        }
        for b in [view.accept, view.reject].into_iter().flatten() {
            if let Some(w) = ui.widget_mut::<Button>(b) {
                w.listeners.click = true;
            }
        }
        if let (Some(p), Some(m)) = (view.popup, view.message) {
            view.base_popup_height = ui.element(p)?.height;
            view.base_message_height = ui.element(m)?.height;
            for id in [p, m] {
                if let Some(e) = ui.element_mut(id) {
                    e.layout_policy = None;
                    e.anchors = AnchorEdges::NONE;
                }
            }
            ui.with::<Text, _>(m, |t, ui| {
                t.padding = 0.0;
                if let Some(e) = ui.element_mut(m) {
                    t.set_selectable(e, false);
                }
            });
            view.set_message(ui, data.get_str(property::MESSAGE).unwrap_or(""));
        }
        Some(view)
    }

    /// The dialog's root.
    #[must_use]
    pub fn root(&self) -> ElementId {
        self.root
    }

    /// The dialog's kind.
    #[must_use]
    pub fn kind(&self) -> DialogType {
        self.kind
    }

    /// The accept (or OK) and reject buttons.
    #[must_use]
    pub fn buttons(&self) -> (Option<ElementId>, Option<ElementId>) {
        (self.accept, self.reject)
    }

    /// The text field of a text-input dialog.
    #[must_use]
    pub fn input(&self) -> Option<ElementId> {
        self.input
    }

    /// The menu of a menu dialog.
    #[must_use]
    pub fn menu(&self) -> Option<ElementId> {
        self.menu
    }

    /// The message text element.
    #[must_use]
    pub fn message(&self) -> Option<ElementId> {
        self.message
    }

    fn set_message(&mut self, ui: &mut UiRoot, text: &str) {
        let (Some(popup), Some(message)) = (self.popup, self.message) else {
            return;
        };
        let Some(width) = ui.element(message).map(|e| e.width) else {
            return;
        };
        let Some(t) = ui.widget_mut::<Text>(message) else {
            return;
        };
        let font = t.font.clone();
        let color = t.default_color;
        let maximum = (width - 2.0 * t.padding).max(1.0);
        let wrapped = match &font {
            Some(f) => wrap_words(text, |s| f.measure_width(s), maximum),
            None => wrap_words(text, |s| s.chars().count() as f32 * 8.0, maximum),
        };
        let line_height = font.as_ref().map_or(16.0, |f| f.line_height);
        let count = wrapped.len();
        t.set_lines(wrapped.into_iter().map(|l| Line::new(l, color)).collect());
        let height = self.base_message_height.max(count as f32 * line_height);
        if let Some(e) = ui.element_mut(message) {
            e.height = height;
        }
        if let Some(e) = ui.element_mut(popup) {
            e.height = self.base_popup_height + (height - self.base_message_height);
        }
    }

    /// Fill the screen and centre the box; a text-input dialog takes the
    /// keyboard focus once it is on the screen.
    fn tick(&mut self, ui: &mut UiRoot) {
        let (w, h) = ui.effective_canvas_size();
        if let Some(e) = ui.element_mut(self.root) {
            e.left = 0.0;
            e.top = 0.0;
            e.width = w;
            e.height = h;
        }
        if let Some(popup) = self.popup
            && let Some((pw, ph)) = ui.element(popup).map(|e| (e.width, e.height))
            && let Some(e) = ui.element_mut(popup)
        {
            e.layout_policy = None;
            e.anchors = AnchorEdges::NONE;
            e.left = ((w - pw) * 0.5).round();
            e.top = ((h - ph) * 0.5).round();
        }
        if self.focus_pending && ui.parent(self.root).is_some() {
            ui.set_keyboard_focus(self.input);
            self.focus_pending = false;
        }
    }

    fn set_pending_count(&self, ui: &mut UiRoot, count: usize) {
        if self.kind == DialogType::Confirmation
            && let Some(p) = self.pending
        {
            ui.set_retail_state(
                p,
                if count == 0 {
                    NO_PENDING_STATE
                } else {
                    PENDING_STATE
                },
            );
        }
    }

    /// Answer `message` from `element` into `data`.
    fn handle(
        &self,
        ui: &UiRoot,
        element: ElementId,
        message: &ElementMessage,
        data: &mut DialogData,
    ) -> ViewAction {
        let clicked = |b: Option<ElementId>| {
            matches!(message, ElementMessage::Clicked { .. }) && Some(element) == b
        };
        let cancel = matches!(message, ElementMessage::Cancel) && element == self.root;
        match self.kind {
            DialogType::Confirmation => {
                if clicked(self.accept) {
                    data.set(property::CONFIRMATION_RESULT, DialogValue::Bool(true));
                    ViewAction::Close
                } else if clicked(self.reject) || cancel {
                    data.set(property::CONFIRMATION_RESULT, DialogValue::Bool(false));
                    ViewAction::Close
                } else {
                    ViewAction::None
                }
            }
            DialogType::Message => {
                if clicked(self.accept) || cancel {
                    ViewAction::Close
                } else {
                    ViewAction::None
                }
            }
            DialogType::ConfirmationTextInput => {
                let submitted =
                    matches!(message, ElementMessage::Submitted(_)) && Some(element) == self.input;
                if clicked(self.accept) || submitted {
                    let text = self
                        .input
                        .and_then(|i| ui.widget::<Field>(i))
                        .map(Field::text)
                        .unwrap_or_default();
                    data.set(property::TEXT_INPUT_RESULT, DialogValue::Str(text));
                    ViewAction::Close
                } else if clicked(self.reject) || cancel {
                    data.set(property::TEXT_INPUT_RESULT, DialogValue::Str(String::new()));
                    ViewAction::Close
                } else if Some(element) == self.input {
                    ViewAction::Handled
                } else {
                    ViewAction::None
                }
            }
            DialogType::ConfirmationMenu => {
                if clicked(self.accept) {
                    let selected = self
                        .menu
                        .and_then(|m| ui.widget::<Menu>(m))
                        .and_then(|m| m.selected)
                        .and_then(|s| i32::try_from(s).ok())
                        .unwrap_or(-1);
                    data.set(property::MENU_SELECTION, DialogValue::I32(selected));
                    ViewAction::Close
                } else if clicked(self.reject) || cancel {
                    data.set(property::MENU_SELECTION, DialogValue::I32(-1));
                    ViewAction::Close
                } else if Some(element) == self.menu {
                    ViewAction::Handled
                } else {
                    ViewAction::None
                }
            }
            DialogType::Wait | DialogType::TextInput | DialogType::Menu => ViewAction::None,
        }
    }

    /// A menu entry was chosen: it becomes the selection (and the button's
    /// caption).
    fn menu_selected(&self, ui: &mut UiRoot, payload: u64) {
        if let Some(m) = self.menu.and_then(|m| ui.widget_mut::<Menu>(m)) {
            m.selected = Some(payload);
            m.button_label = m
                .items
                .iter()
                .find(|i| i.payload == payload)
                .map(|i| i.label.clone())
                .unwrap_or_default();
        }
    }
}

// ── The factory ─────────────────────────────────────────────────────────

/// Makes a dialog kind's layout from the catalog (detached).
pub type MakeLayout<'a> = &'a mut dyn FnMut(&mut UiRoot, DialogType) -> Option<ImportedLayout>;

#[derive(Debug)]
struct Info<T> {
    data: DialogData,
    context: u32,
    queue_key: u32,
    sequence: u64,
    tag: T,
    view: Option<DialogView>,
}

/// A dialog that closed: its context, its data with the result set, and
/// the owner's tag.
#[derive(Debug)]
pub struct Closed<T> {
    /// The dialog's context.
    pub context: u32,
    /// The data, results set.
    pub data: DialogData,
    /// The owner's tag.
    pub tag: T,
}

/// The factory. See the module documentation.
#[derive(Debug)]
pub struct DialogFactory<T> {
    active_queued: BTreeMap<u32, Info<T>>,
    active_non_queued: BTreeMap<u32, Info<T>>,
    pending: BTreeMap<u32, VecDeque<Info<T>>>,
    retryable: VecDeque<Info<T>>,
    open_order: Vec<u32>,
    global_context: u32,
    global_sequence: u64,
    closed: Vec<Closed<T>>,
    opened: Vec<u32>,
    failures: Vec<String>,
    to_open: Vec<u32>,
}

impl<T> Default for DialogFactory<T> {
    fn default() -> Self {
        Self {
            active_queued: BTreeMap::new(),
            active_non_queued: BTreeMap::new(),
            pending: BTreeMap::new(),
            retryable: VecDeque::new(),
            open_order: Vec::new(),
            global_context: 0,
            global_sequence: 0,
            closed: Vec::new(),
            opened: Vec::new(),
            failures: Vec::new(),
            to_open: Vec::new(),
        }
    }
}

impl<T> DialogFactory<T> {
    /// No dialogs.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// A dialog is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        !self.active_queued.is_empty() || !self.active_non_queued.is_empty()
    }

    /// How many dialogs are open.
    #[must_use]
    pub fn active_count(&self) -> usize {
        self.active_queued.len() + self.active_non_queued.len()
    }

    /// How many wait in the queues.
    #[must_use]
    pub fn pending_count(&self) -> usize {
        self.pending.values().map(VecDeque::len).sum()
    }

    /// How many wait to be made again.
    #[must_use]
    pub fn retry_count(&self) -> usize {
        self.retryable.len()
    }

    /// Dialogs closed since the last call, in the order they closed.
    pub fn take_closed(&mut self) -> Vec<Closed<T>> {
        std::mem::take(&mut self.closed)
    }

    /// Contexts opened since the last call.
    pub fn take_opened(&mut self) -> Vec<u32> {
        std::mem::take(&mut self.opened)
    }

    /// Dialogs that could not be made since the last call, worded.
    pub fn take_failures(&mut self) -> Vec<String> {
        std::mem::take(&mut self.failures)
    }

    fn infos(&self) -> impl Iterator<Item = &Info<T>> {
        self.active_non_queued
            .values()
            .chain(self.active_queued.values())
            .chain(self.pending.values().flatten())
            .chain(self.retryable.iter())
    }

    /// The open view of `context`.
    #[must_use]
    pub fn view(&self, context: u32) -> Option<&DialogView> {
        self.infos()
            .find(|i| i.context == context)
            .and_then(|i| i.view.as_ref())
    }

    /// The data of `context` (open, waiting or retrying).
    #[must_use]
    pub fn data(&self, context: u32) -> Option<&DialogData> {
        self.infos().find(|i| i.context == context).map(|i| &i.data)
    }

    /// The tag of `context`.
    #[must_use]
    pub fn tag(&self, context: u32) -> Option<&T> {
        self.infos().find(|i| i.context == context).map(|i| &i.tag)
    }

    /// The contexts open now, oldest first.
    #[must_use]
    pub fn open_contexts(&self) -> &[u32] {
        &self.open_order
    }

    /// Whether any open, waiting or retrying dialog's tag matches.
    #[must_use]
    pub fn any(&self, matches: impl Fn(&T) -> bool) -> bool {
        self.infos().any(|i| matches(&i.tag))
    }

    /// The contexts whose tags match.
    #[must_use]
    pub fn contexts_where(&self, matches: impl Fn(&T) -> bool) -> Vec<u32> {
        self.infos()
            .filter(|i| matches(&i.tag))
            .map(|i| i.context)
            .collect()
    }

    fn next_context(&mut self) -> u32 {
        self.global_context = self.global_context.wrapping_add(1);
        if self.global_context == 0 {
            self.global_context = 1;
        }
        self.global_context
    }

    fn next_sequence(&mut self) -> u64 {
        self.global_sequence = self.global_sequence.wrapping_add(1);
        if self.global_sequence == 0 {
            self.global_sequence = 1;
        }
        self.global_sequence
    }

    /// Make a dialog of `data`; its context comes back. `tag` returns
    /// with it when it closes.
    pub fn make(&mut self, ui: &mut UiRoot, data: DialogData, tag: T, make: MakeLayout<'_>) -> u32 {
        let context = self.next_context();
        let mut queue_key = data.get_u32(property::QUEUE_KEY, DEFAULT_QUEUE_KEY);
        if queue_key == 0 {
            queue_key = DEFAULT_QUEUE_KEY;
        }
        let mut info = Info {
            data,
            context,
            queue_key,
            sequence: self.next_sequence(),
            tag,
            view: None,
        };
        if queue_key == NON_QUEUED_KEY {
            if self.try_create(ui, &mut info, make) {
                self.active_non_queued.insert(context, info);
            } else {
                self.queue_retry(info);
            }
            return context;
        }
        if !self.active_queued.contains_key(&queue_key) {
            if self.has_retry(queue_key) && !info.data.is_priority() {
                self.pending.entry(queue_key).or_default().push_back(info);
                return context;
            }
            if self.try_create(ui, &mut info, make) {
                self.active_queued.insert(queue_key, info);
                self.update_pending_displays(ui);
            } else {
                self.queue_retry(info);
            }
            return context;
        }
        if !info.data.is_priority() {
            self.pending.entry(queue_key).or_default().push_back(info);
            self.update_pending_displays(ui);
            return context;
        }
        // A priority dialog suspends the open one, which waits first.
        let mut current = self.active_queued.remove(&queue_key).expect("open");
        self.suspend(ui, &mut current);
        if self.try_create(ui, &mut info, make) {
            self.pending
                .entry(queue_key)
                .or_default()
                .push_front(current);
            self.active_queued.insert(queue_key, info);
            self.update_pending_displays(ui);
        } else {
            self.open_specific(ui, current, make);
            self.queue_retry(info);
        }
        context
    }

    /// A yes/no question on `queue_key`.
    pub fn make_confirmation(
        &mut self,
        ui: &mut UiRoot,
        message: &str,
        tag: T,
        queue_key: u32,
        priority: bool,
        make: MakeLayout<'_>,
    ) -> u32 {
        let mut data = DialogData::confirmation(message).on_queue(queue_key);
        if priority {
            data = data.priority();
        }
        self.make(ui, data, tag, make)
    }

    /// Set the open question's answer (it closes when the owner closes
    /// it); false when `context` is not open.
    pub fn set_confirmation_result(&mut self, context: u32, accept: bool) -> bool {
        if context == 0 {
            return false;
        }
        let info = self.active_non_queued.get_mut(&context).or_else(|| {
            self.active_queued
                .values_mut()
                .find(|i| i.context == context)
        });
        match info {
            Some(i) => {
                i.data
                    .set(property::CONFIRMATION_RESULT, DialogValue::Bool(accept));
                true
            }
            None => false,
        }
    }

    /// Close `context` wherever it is (open, waiting or retrying): it comes
    /// back from [`Self::take_closed`]; its queue moves on at the next
    /// [`Self::settle`], after the owner acted on the answer (a dialog the
    /// answer makes opens before the ones waiting, as OpenAC's callbacks
    /// run before the queue moves).
    pub fn close(&mut self, ui: &mut UiRoot, context: u32) -> bool {
        if context == 0 {
            return false;
        }
        if let Some(info) = self.active_non_queued.remove(&context) {
            self.done(ui, info);
            return true;
        }
        if let Some(key) = self
            .active_queued
            .iter()
            .find(|(_, i)| i.context == context)
            .map(|(k, _)| *k)
        {
            let info = self.active_queued.remove(&key).expect("found");
            self.done(ui, info);
            self.to_open.push(key);
            return true;
        }
        let pending = self
            .pending
            .iter()
            .find_map(|(k, q)| q.iter().position(|i| i.context == context).map(|p| (*k, p)));
        if let Some((key, at)) = pending {
            let queue = self.pending.get_mut(&key).expect("found");
            let info = queue.remove(at).expect("found");
            if queue.is_empty() {
                self.pending.remove(&key);
            }
            self.done(ui, info);
            self.update_pending_displays(ui);
            return true;
        }
        if let Some(at) = self.retryable.iter().position(|i| i.context == context) {
            let info = self.retryable.remove(at).expect("found");
            let key = info.queue_key;
            self.done(ui, info);
            if key != NON_QUEUED_KEY {
                self.to_open.push(key);
            }
            return true;
        }
        false
    }

    /// `message` from `element` answered an open dialog: its result is
    /// set and it closes. True when a dialog took the message.
    pub fn handle(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let contexts = self.open_order.clone();
        for context in contexts.into_iter().rev() {
            let Some(info) = self.active_non_queued.get_mut(&context).or_else(|| {
                self.active_queued
                    .values_mut()
                    .find(|i| i.context == context)
            }) else {
                continue;
            };
            let Some(view) = info.view.clone() else {
                continue;
            };
            if let ElementMessage::MenuSelected(payload) = message
                && Some(element) == view.menu
            {
                view.menu_selected(ui, *payload);
                return true;
            }
            match view.handle(ui, element, message, &mut info.data) {
                ViewAction::None => {}
                ViewAction::Handled => return true,
                ViewAction::Close => {
                    self.close(ui, context);
                    return true;
                }
            }
        }
        false
    }

    /// Move on every queue a close emptied: its next dialog opens.
    pub fn settle(&mut self, ui: &mut UiRoot, make: MakeLayout<'_>) {
        for key in std::mem::take(&mut self.to_open) {
            self.open_next(ui, key, make);
        }
    }

    /// One frame: dialogs that could not be made are tried again; every
    /// open dialog stays on top, centred.
    pub fn tick(&mut self, ui: &mut UiRoot, make: MakeLayout<'_>) {
        self.retry_failed(ui, make);
        self.settle(ui, make);
        for context in self.open_order.clone() {
            let info = self.active_non_queued.get_mut(&context).or_else(|| {
                self.active_queued
                    .values_mut()
                    .find(|i| i.context == context)
            });
            if let Some(view) = info.and_then(|i| i.view.as_mut()) {
                ui.bring_to_front(view.root);
                view.tick(ui);
            }
        }
    }

    /// Complete every dialog (open, waiting, retrying).
    pub fn reset(&mut self, ui: &mut UiRoot) {
        loop {
            let mut infos: Vec<Info<T>> = Vec::new();
            infos.extend(std::mem::take(&mut self.active_non_queued).into_values());
            infos.extend(std::mem::take(&mut self.active_queued).into_values());
            for (_, q) in std::mem::take(&mut self.pending) {
                infos.extend(q);
            }
            infos.extend(std::mem::take(&mut self.retryable));
            if infos.is_empty() {
                break;
            }
            for info in infos {
                self.done(ui, info);
            }
        }
        self.refresh_modal(ui);
    }

    fn try_create(&mut self, ui: &mut UiRoot, info: &mut Info<T>, make: MakeLayout<'_>) -> bool {
        let Some(kind) = info.data.kind() else {
            return false;
        };
        let made = if matches!(kind, DialogType::TextInput | DialogType::Menu) {
            None
        } else {
            make(ui, kind)
        };
        let Some(layout) = made else {
            self.failures.push(format!(
                "ui: dialog type {} context {} waits for the catalog",
                kind as u32, info.context
            ));
            return false;
        };
        let Some(mut view) = DialogView::open(ui, &layout, &info.data) else {
            ui.destroy(layout.root);
            self.failures.push(format!(
                "ui: dialog type {} context {} is missing a catalog part",
                kind as u32, info.context
            ));
            return false;
        };
        let screen = ui.root();
        ui.add_child(screen, view.root);
        ui.bring_to_front(view.root);
        self.open_order.push(info.context);
        ui.modal = Some(view.root);
        view.tick(ui);
        info.view = Some(view);
        self.opened.push(info.context);
        true
    }

    fn suspend(&mut self, ui: &mut UiRoot, info: &mut Info<T>) {
        if let Some(view) = info.view.take() {
            self.open_order.retain(|&c| c != info.context);
            ui.destroy(view.root);
            self.refresh_modal(ui);
        }
    }

    fn done(&mut self, ui: &mut UiRoot, mut info: Info<T>) {
        if let Some(view) = info.view.take() {
            self.open_order.retain(|&c| c != info.context);
            ui.destroy(view.root);
        }
        self.refresh_modal(ui);
        self.closed.push(Closed {
            context: info.context,
            data: info.data,
            tag: info.tag,
        });
    }

    fn open_next(&mut self, ui: &mut UiRoot, key: u32, make: MakeLayout<'_>) {
        if self.active_queued.contains_key(&key) {
            return;
        }
        if self.try_activate_retry(ui, key, make) {
            return;
        }
        let Some(queue) = self.pending.get_mut(&key) else {
            return;
        };
        let Some(mut next) = queue.pop_front() else {
            return;
        };
        if queue.is_empty() {
            self.pending.remove(&key);
        }
        if self.try_create(ui, &mut next, make) {
            self.active_queued.insert(key, next);
            self.update_pending_displays(ui);
        } else {
            self.queue_retry(next);
        }
    }

    fn open_specific(&mut self, ui: &mut UiRoot, mut info: Info<T>, make: MakeLayout<'_>) {
        let key = info.queue_key;
        if self.try_create(ui, &mut info, make) {
            self.active_queued.insert(key, info);
            self.update_pending_displays(ui);
        } else {
            self.queue_retry(info);
        }
    }

    fn first_retry(&self, key: u32) -> Option<usize> {
        self.retryable.iter().position(|i| i.queue_key == key)
    }

    fn has_retry(&self, key: u32) -> bool {
        self.first_retry(key).is_some()
    }

    fn try_activate_retry(&mut self, ui: &mut UiRoot, key: u32, make: MakeLayout<'_>) -> bool {
        let Some(at) = self.first_retry(key) else {
            return false;
        };
        let mut info = self.retryable.remove(at).expect("found");
        if self.try_create(ui, &mut info, make) {
            self.active_queued.insert(key, info);
            self.update_pending_displays(ui);
        } else {
            self.retryable.insert(at, info);
        }
        true
    }

    fn retry_failed(&mut self, ui: &mut UiRoot, make: MakeLayout<'_>) {
        let contexts: Vec<u32> = self.retryable.iter().map(|i| i.context).collect();
        for context in contexts {
            let Some(at) = self.retryable.iter().position(|i| i.context == context) else {
                continue;
            };
            let key = self.retryable[at].queue_key;
            if key == NON_QUEUED_KEY {
                let mut info = self.retryable.remove(at).expect("found");
                if self.try_create(ui, &mut info, make) {
                    self.active_non_queued.insert(context, info);
                } else {
                    self.retryable.insert(at, info);
                }
                continue;
            }
            if self.first_retry(key) != Some(at) {
                continue;
            }
            match self.active_queued.get(&key) {
                None => {
                    self.try_activate_retry(ui, key, make);
                }
                Some(active) => {
                    let candidate = &self.retryable[at];
                    if candidate.data.is_priority()
                        && (!active.data.is_priority() || candidate.sequence > active.sequence)
                    {
                        let priority = self.retryable.remove(at).expect("found");
                        self.preempt_with_retry(ui, priority, at, make);
                    }
                }
            }
        }
    }

    fn preempt_with_retry(
        &mut self,
        ui: &mut UiRoot,
        mut priority: Info<T>,
        at: usize,
        make: MakeLayout<'_>,
    ) {
        let key = priority.queue_key;
        let mut current = self.active_queued.remove(&key).expect("open");
        self.suspend(ui, &mut current);
        if self.try_create(ui, &mut priority, make) {
            self.pending.entry(key).or_default().push_front(current);
            self.active_queued.insert(key, priority);
            self.update_pending_displays(ui);
            return;
        }
        self.open_specific(ui, current, make);
        self.retryable
            .insert(at.min(self.retryable.len()), priority);
    }

    fn queue_retry(&mut self, info: Info<T>) {
        if !info.data.is_priority() {
            self.retryable.push_back(info);
            return;
        }
        match self.first_retry(info.queue_key) {
            Some(at) => self.retryable.insert(at, info),
            None => self.retryable.push_back(info),
        }
    }

    fn update_pending_displays(&self, ui: &mut UiRoot) {
        for (key, active) in &self.active_queued {
            let count = self.pending.get(key).map_or(0, VecDeque::len);
            if let Some(view) = &active.view {
                view.set_pending_count(ui, count);
            }
        }
    }

    fn refresh_modal(&self, ui: &mut UiRoot) {
        ui.modal = self.open_order.last().and_then(|c| {
            self.active_non_queued
                .get(c)
                .or_else(|| self.active_queued.values().find(|i| i.context == *c))
                .and_then(|i| i.view.as_ref())
                .map(|v| v.root)
        });
    }
}

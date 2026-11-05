//! The examination window's inscription: what an inscribable item has
//! written on it and who wrote it, editable by its owner when no one else
//! has signed it. Leaving the field sends the new text (an empty field
//! clears it); a click where the player may not write says why.

use ac_core::items::{ClientObject, PublicWeenieFlags};
use ac_proto::appraise::IdentifyObjectResponse;
use ac_runtime::inventory::{ItemCommand, ObjectRef};
use ac_runtime::session::SessionCommand;

use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::items::emit;
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{DatElement, Field, ScrollSource, Scrollbar, Text};

/// The panel behind the inscription.
pub const INSCRIPTION_BACKGROUND_ID: u32 = 0x1000_0137;
/// The inscription's text.
pub const INSCRIPTION_TEXT_ID: u32 = 0x1000_013E;
/// Who signed it.
pub const SIGNATURE_TEXT_ID: u32 = 0x1000_013F;
/// The inscription's scrollbar.
pub const INSCRIPTION_SCROLLBAR_ID: u32 = 0x1000_046E;

/// What an unwritten inscription shows.
pub const INSCRIBE_HERE: &str = "<Inscribe here>";

const INSCRIPTION_STRING_PROPERTY: u32 = 7;
const SCRIBE_NAME_STRING_PROPERTY: u32 = 8;

/// Who is looking: the player's own name, and whether the item is theirs.
#[derive(Debug, Clone, Copy, Default)]
pub struct InscriptionViewer<'a> {
    /// The player's name.
    pub player_name: &'a str,
    /// The item is in the player's possession.
    pub owned: bool,
}

/// The inscription part of the window.
#[derive(Debug, Default)]
pub struct Inscription {
    field: Option<ElementId>,
    text: Option<ElementId>,
    signature: Option<ElementId>,
    background: Option<ElementId>,
    item: u32,
    value: String,
    old: String,
    scribe: String,
    signature_value: String,
    inscribable: bool,
    owned: bool,
    player_name: String,
    focused: bool,
}

fn set_lines(ui: &mut UiRoot, id: Option<ElementId>, value: &str) {
    let Some(id) = id else {
        return;
    };
    if let Some(w) = ui.widget_mut::<Text>(id) {
        let color = w.default_color;
        let lines = if value.is_empty() {
            Vec::new()
        } else {
            value
                .split('\n')
                .map(|l| Line::new(l.to_owned(), color))
                .collect()
        };
        w.set_lines(lines);
    }
}

fn same_name(a: &str, b: &str) -> bool {
    a.to_lowercase() == b.to_lowercase()
}

impl Inscription {
    /// Bind to the window's inscription elements (each may be missing).
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout) -> Self {
        let inscription = layout.find(INSCRIPTION_TEXT_ID);
        let field = inscription.filter(|id| ui.widget::<Field>(*id).is_some());
        let text = inscription.filter(|id| ui.widget::<Text>(*id).is_some());
        if let Some(id) = field {
            ui.with::<Field, _>(id, |f, ui| {
                f.set_text("");
                f.clear_on_submit = false;
                f.record_history = false;
                f.set_editable(ui, id, false);
                f.selectable = false;
            });
        }
        if let Some(id) = inscription
            && let Some(bar) = layout.find(INSCRIPTION_SCROLLBAR_ID)
            && let Some(b) = ui.widget_mut::<Scrollbar>(bar)
        {
            b.model = ScrollSource::Element(id);
        }
        let background = layout.find(INSCRIPTION_BACKGROUND_ID);
        if let Some(id) = background {
            if let Some(d) = ui.widget_mut::<DatElement>(id) {
                d.emits_clicks = true;
            }
            if let Some(e) = ui.element_mut(id) {
                e.click_through = false;
            }
        }
        Self {
            field,
            text,
            signature: layout
                .find(SIGNATURE_TEXT_ID)
                .filter(|id| ui.widget::<Text>(*id).is_some()),
            background,
            ..Self::default()
        }
    }

    fn show_value(&self, ui: &mut UiRoot) {
        if let Some(id) = self.field {
            ui.with::<Field, _>(id, |f, _| f.set_text(&self.value));
        }
        set_lines(ui, self.text, &self.value);
        set_lines(ui, self.signature, &self.signature_value);
    }

    fn set_visible(&self, ui: &mut UiRoot, visible: bool) {
        for id in [self.field, self.text, self.signature]
            .into_iter()
            .flatten()
        {
            ui.set_visible(id, visible);
        }
    }

    fn set_editable(&self, ui: &mut UiRoot, editable: bool) {
        if let Some(id) = self.field {
            ui.with::<Field, _>(id, |f, ui| {
                f.set_editable(ui, id, editable);
                f.selectable = editable;
            });
        }
        if let Some(id) = self.background
            && let Some(e) = ui.element_mut(id)
        {
            e.click_through = editable;
        }
    }

    fn editable(&self, ui: &UiRoot) -> bool {
        self.field
            .and_then(|id| ui.widget::<Field>(id))
            .is_some_and(Field::editable)
    }

    /// Show `obj`'s inscription from its appraisal.
    pub fn set(
        &mut self,
        ui: &mut UiRoot,
        obj: &ClientObject,
        appraisal: &IdentifyObjectResponse,
        viewer: InscriptionViewer<'_>,
    ) {
        self.item = obj.object_id();
        self.owned = viewer.owned;
        viewer.player_name.clone_into(&mut self.player_name);
        self.old.clear();
        self.scribe.clear();
        self.value.clear();
        self.signature_value.clear();
        let public = obj.public_weenie_bitfield.map_or(0, |f| f.0);
        let public_inscribable = public & PublicWeenieFlags::INSCRIBABLE.0 != 0;
        self.inscribable = if obj.is_hook() {
            appraisal.hook_profile.is_some_and(|h| h.flags & 0x1 != 0)
        } else {
            public_inscribable
        };
        if !self.inscribable {
            if let Some(id) = self.field {
                ui.with::<Field, _>(id, |f, _| f.set_text(""));
            }
            self.set_editable(ui, false);
            self.set_visible(ui, false);
            if let Some(id) = self.background
                && let Some(e) = ui.element_mut(id)
            {
                e.click_through = false;
            }
            return;
        }
        let p = &appraisal.properties;
        let get = |id| p.strings.get(&id).cloned().unwrap_or_default();
        self.scribe = get(SCRIBE_NAME_STRING_PROPERTY);
        self.old = get(INSCRIPTION_STRING_PROPERTY);
        if self.scribe.is_empty() {
            INSCRIBE_HERE.clone_into(&mut self.value);
        } else if !self.old.is_empty() {
            self.value.clone_from(&self.old);
            self.signature_value = format!("--{}", self.scribe);
        }
        self.show_value(ui);
        self.set_visible(ui, true);
        let editable = public_inscribable
            && self.owned
            && (self.scribe.is_empty() || same_name(&self.scribe, &self.player_name));
        self.set_editable(ui, editable);
    }

    /// Scroll back to the top (a newly examined item).
    pub fn scroll_to_top(&self, ui: &mut UiRoot) {
        if let Some(id) = self.field
            && let Some(f) = ui.widget_mut::<Field>(id)
        {
            f.scroll.set_scroll_y(0);
        }
        if let Some(id) = self.text
            && let Some(t) = ui.widget_mut::<Text>(id)
        {
            t.scroll.set_scroll_y(0);
        }
    }

    /// The keyboard focus moved to `focus`: entering the field readies it
    /// for writing, leaving it sends what was written.
    pub fn focus_changed(&mut self, ui: &mut UiRoot, focus: Option<ElementId>) {
        let Some(field) = self.field else {
            return;
        };
        let focused = focus == Some(field);
        if focused == self.focused {
            return;
        }
        self.focused = focused;
        if focused {
            self.gain_focus(ui);
        } else {
            let text = ui
                .widget::<Field>(field)
                .map(Field::text)
                .unwrap_or_default();
            self.lose_focus(ui, &text);
        }
    }

    fn gain_focus(&mut self, ui: &mut UiRoot) {
        if !self.editable(ui) {
            return;
        }
        if self.scribe.is_empty() {
            self.value.clear();
            if let Some(id) = self.field {
                ui.with::<Field, _>(id, |f, _| f.set_text(""));
            }
        }
        self.signature_value = format!("--{}", self.player_name);
        set_lines(ui, self.signature, &self.signature_value);
    }

    fn lose_focus(&mut self, ui: &mut UiRoot, text: &str) {
        if self.item == 0 || !self.inscribable {
            return;
        }
        let skip_empty_unscribed = text.is_empty() && self.scribe.is_empty();
        if !skip_empty_unscribed && text != self.old {
            emit(
                ui,
                PanelOutput::Session(SessionCommand::Item(ItemCommand::Inscribe {
                    item: ObjectRef::Guid(self.item),
                    text: text.to_owned(),
                })),
            );
        }
        if text.is_empty() {
            INSCRIBE_HERE.clone_into(&mut self.value);
            self.signature_value.clear();
            self.scribe.clear();
            if let Some(id) = self.field {
                ui.with::<Field, _>(id, |f, _| f.set_text(INSCRIBE_HERE));
            }
        } else {
            text.clone_into(&mut self.value);
            self.scribe.clone_from(&self.player_name);
            self.signature_value = format!("--{}", self.scribe);
        }
        set_lines(ui, self.text, &self.value);
        set_lines(ui, self.signature, &self.signature_value);
        text.clone_into(&mut self.old);
    }

    /// A click on the read-only inscription or its panel: say why it
    /// cannot be written. True when the message was the inscription's.
    pub fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        let read_only_click =
            Some(element) == self.field && matches!(message, ElementMessage::ReadOnlyClicked);
        let background_click =
            Some(element) == self.background && matches!(message, ElementMessage::Clicked { .. });
        if !read_only_click && !background_click {
            return false;
        }
        if self.editable(ui) {
            return true;
        }
        let text = if !self.scribe.is_empty() && !same_name(&self.scribe, &self.player_name) {
            format!("Only {} can change the inscription", self.scribe)
        } else if self.item == 0 || !self.owned {
            "Item must be in your inventory to inscribe.".to_owned()
        } else {
            "This item is not inscribable.".to_owned()
        };
        emit(ui, PanelOutput::Notice(text));
        true
    }

    /// Forget the item (a new session).
    pub fn reset(&mut self, ui: &mut UiRoot) {
        self.value.clear();
        self.signature_value.clear();
        self.scribe.clear();
        self.old.clear();
        self.inscribable = false;
        self.owned = false;
        self.item = 0;
        if let Some(id) = self.field {
            ui.with::<Field, _>(id, |f, _| {
                f.set_text("");
                f.scroll.set_scroll_y(0);
            });
        }
        self.set_editable(ui, false);
        if let Some(id) = self.field {
            ui.set_visible(id, false);
        }
        if let Some(id) = self.signature {
            ui.set_visible(id, false);
        }
        set_lines(ui, self.text, "");
        set_lines(ui, self.signature, "");
    }
}

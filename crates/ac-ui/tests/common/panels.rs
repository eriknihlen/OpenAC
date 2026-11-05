//! Builders shared by the panel tests (trade, vendor, social,
//! contracts): buttons, texts, datelements and the dat content.

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::items::ClientObject;
use ac_ui::element_info::{ElementInfo, ImageMedia};
use ac_ui::panels::PanelStrings;
use ac_ui::panels::appraisal::{AppraisalNames, NameTables};
use ac_ui::root::UiRoot;
use ac_ui::tree::Element;
use ac_ui::widgets::{Button, DatElement, Text};
use ac_ui::{ElementId, MouseButton};

/// A button description with Normal, Highlight and Ghosted images.
pub fn std_button_info(id: u32) -> Arc<ElementInfo> {
    let mut info = ElementInfo {
        id,
        kind: 1,
        width: 32.0,
        height: 16.0,
        default_state_name: "Normal".to_owned(),
        ..ElementInfo::default()
    };
    for (name, file) in [("Normal", 1), ("Highlight", 2), ("Ghosted", 3)] {
        info.state_media
            .insert(name.to_owned(), ImageMedia { file, draw_mode: 1 });
    }
    Arc::new(info)
}

/// A button with dat id `id` under `parent`, recorded in `ids`.
pub fn add_std_button(
    ui: &mut UiRoot,
    ids: &mut HashMap<u32, ElementId>,
    parent: ElementId,
    id: u32,
) -> ElementId {
    let mut e = Element::at(0.0, 0.0, 32.0, 16.0);
    e.dat_element_id = id;
    let b = ui.add(
        parent,
        e,
        Button::new(std_button_info(id), None, Vec::new(), 0.0),
    );
    ids.insert(id, b);
    b
}

/// A text with dat id `id` under `parent`, recorded in `ids`.
pub fn add_std_text(
    ui: &mut UiRoot,
    ids: &mut HashMap<u32, ElementId>,
    parent: ElementId,
    id: u32,
) -> ElementId {
    let mut e = Element::at(0.0, 0.0, 200.0, 16.0);
    e.dat_element_id = id;
    Text::configure(&mut e);
    let t = ui.add(parent, e, Text::default());
    ids.insert(id, t);
    t
}

/// A plain dat element (states only) with dat id `id` under `parent`.
pub fn add_std_dat(
    ui: &mut UiRoot,
    ids: &mut HashMap<u32, ElementId>,
    parent: ElementId,
    id: u32,
    w: f32,
    h: f32,
) -> ElementId {
    let mut e = Element::at(0.0, 0.0, w, h);
    e.dat_element_id = id;
    DatElement::configure(&mut e);
    let info = Arc::new(ElementInfo {
        id,
        width: w,
        height: h,
        ..ElementInfo::default()
    });
    let d = ui.add(parent, e, DatElement::new(info));
    ids.insert(id, d);
    d
}

/// The text a text element shows, its lines joined.
pub fn text_of(ui: &UiRoot, id: ElementId) -> String {
    ui.widget::<Text>(id)
        .map(|t| {
            t.content_lines()
                .iter()
                .map(|l| l.text.clone())
                .collect::<String>()
        })
        .unwrap_or_default()
}

/// OpenAC's `ItemTooltipCaptionNames`: material 1 is "Pyreal", 60 is
/// "Silver".
pub fn caption_names() -> NameTables {
    NameTables {
        materials: HashMap::from([(1, "Pyreal".to_owned()), (60, "Silver".to_owned())]),
        ..NameTables::default()
    }
}

/// An item whose name reads right only with its material ("Pyreal
/// Scarab").
pub fn material_item(guid: u32) -> ClientObject {
    let mut o = ClientObject::new(guid);
    o.name = "Scarab".into();
    o.plural_name = "Scarabs".into();
    o.material_type = Some(1);
    o
}

/// An item with no material ("Bread Loaf").
pub fn plain_item(guid: u32) -> ClientObject {
    let mut o = ClientObject::new(guid);
    o.name = "Bread Loaf".into();
    o
}

/// The composed name of `o` with [`caption_names`].
pub fn composed_name(o: &ClientObject) -> String {
    caption_names().appropriate_name(o)
}

/// Composes a template from its variables.
pub type TemplateFn = Box<dyn Fn(&HashMap<u32, String>) -> String>;

/// Strings from a map: plain entries by (table, id), templates by (table,
/// key) composed by a closure.
#[derive(Default)]
pub struct FakeStrings {
    pub plain: HashMap<(u32, u32), String>,
    pub templates: HashMap<(u32, u32), TemplateFn>,
}

impl PanelStrings for FakeStrings {
    fn string(&self, table_id: u32, string_id: u32) -> Option<String> {
        self.plain.get(&(table_id, string_id)).cloned()
    }

    fn template(
        &self,
        table_id: u32,
        key_hash: u32,
        variables: &HashMap<u32, String>,
    ) -> Option<String> {
        self.templates
            .get(&(table_id, key_hash))
            .map(|f| f(variables))
    }
}

/// Press and click element `id` with the left button through the root
/// (a full press-release on its centre).
pub fn click_element(ui: &mut UiRoot, id: ElementId) {
    let (x, y) = ui.screen_position(id);
    let (w, h) = ui.element(id).map_or((0.0, 0.0), |e| (e.width, e.height));
    let (cx, cy) = ((x + w / 2.0) as i32, (y + h / 2.0) as i32);
    ui.on_mouse_move(cx, cy);
    ui.on_mouse_down(MouseButton::Left, cx, cy, 0);
    ui.on_mouse_up(MouseButton::Left, cx, cy, 0);
}

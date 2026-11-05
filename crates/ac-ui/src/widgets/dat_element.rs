//! The generic imported element: draws its active state's image and
//! follows retail state changes (including passing them to its children).

use std::sync::Arc;

use ac_dat::objects::{DIRECT_STATE_ID, PropertyValue};

use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext, TextureRef};
use crate::element_info::{ElementInfo, ImageMedia};
use crate::event::{UiEvent, kind};
use crate::font::UiFont;
use crate::root::{ElementMessage, UiRoot};
use crate::states;
use crate::tree::{Element, ElementId, Widget};

/// An imported element with no behaviour of its own beyond its states.
#[derive(Debug, Clone)]
pub struct DatElement {
    info: Arc<ElementInfo>,
    /// The state drawn ("" = the unnamed default state).
    pub active_state: String,

    /// Centred label text.
    pub label: Option<String>,
    /// Label font.
    pub label_font: Option<Arc<UiFont>>,
    /// Label colour.
    pub label_color: Color,
    /// Multiplied into the image.
    pub tint: Color,
    /// Outlined label.
    pub outline: bool,
    /// Label outline colour.
    pub outline_color: Color,
    /// Draw the image (the label always draws).
    pub media_visible: bool,
    /// A run-time image drawn instead of the state's (`Some(None)` draws
    /// nothing but still suppresses the state image).
    pub runtime_image: Option<Option<TextureRef>>,
    /// Clicks are reported as [`ElementMessage::Clicked`] and stop there.
    pub emits_clicks: bool,
    /// Which dat children the importer builds.
    pub child_import: crate::widgets::ChildImport,
}

impl DatElement {
    /// An element for `info`, starting in its default state: the authored
    /// default state name, else Normal when it has an image, else the
    /// unnamed default state.
    #[must_use]
    pub fn new(info: Arc<ElementInfo>) -> Self {
        let active_state = if !info.default_state_name.is_empty() {
            info.default_state_name.clone()
        } else if info.state_media.contains_key("Normal") {
            "Normal".to_owned()
        } else {
            String::new()
        };
        Self {
            outline: info.outline,
            outline_color: info.outline_color.unwrap_or(DEFAULT_OUTLINE_COLOR),
            info,
            active_state,

            label: None,
            label_font: None,
            label_color: Color::WHITE,
            tint: Color::WHITE,
            media_visible: true,
            runtime_image: None,
            emits_clicks: false,
            child_import: crate::widgets::ChildImport::All,
        }
    }

    /// The element flags a generic imported element starts with: decoration
    /// is click-through until a behaviour opts back in.
    pub fn configure(element: &mut Element) {
        element.click_through = true;
    }

    /// The element's description.
    #[must_use]
    pub fn info(&self) -> &Arc<ElementInfo> {
        &self.info
    }

    /// The image of the active state, else the default state's; none when
    /// neither has one.
    #[must_use]
    pub fn active_media(&self) -> ImageMedia {
        self.info
            .media_or_default(&self.active_state)
            .unwrap_or_default()
    }

    /// The id of the active state: the default state for "", the authored
    /// state of that name, else the standard id of the name (0 if none).
    #[must_use]
    pub fn active_retail_state_id(&self) -> u32 {
        active_state_id(&self.info, &self.active_state)
    }
}

/// The id of state `name` of `info`: the default state for "", the authored
/// state of that name, else the standard id of the name (0 if none).
#[must_use]
pub fn active_state_id(info: &ElementInfo, name: &str) -> u32 {
    if name.is_empty() {
        return DIRECT_STATE_ID;
    }
    if let Some((id, _)) = info.state_named(name) {
        return id;
    }
    states::standard_state_id(name)
}

/// Apply a state's "invisible" property (0x3B), when it has one.
pub(crate) fn apply_state_visibility(
    ui: &mut UiRoot,
    me: ElementId,
    info: &ElementInfo,
    state: u32,
) {
    if let Some(s) = info.state(state)
        && let Some(PropertyValue::Bool(invisible)) = s.properties.get(0x3B).map(|p| &p.value)
    {
        ui.set_visible(me, !invisible);
    }
}

/// Pass state `state` to every child of `me`.
pub(crate) fn cascade_state(ui: &mut UiRoot, me: ElementId, state: u32) {
    for child in ui.children(me) {
        ui.set_retail_state(child, state);
    }
}

impl Widget for DatElement {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if ui.dispatch_to_pointer_region(me, e) {
            return true;
        }
        if e.kind == kind::CLICK && self.emits_clicks {
            ui.send_message(
                me,
                ElementMessage::Clicked {
                    x: e.data1,
                    y: e.data2,
                },
            );
            return true;
        }
        false
    }

    fn handles_click(&self, element: &Element) -> bool {
        self.emits_clicks
            || element
                .pointer_region
                .as_ref()
                .is_some_and(|r| r.handler.handles_click())
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        if self.media_visible {
            if let Some(runtime) = self.runtime_image {
                if let Some(texture) = runtime {
                    rc.sprite(texture, 0.0, 0.0, w, h, (0.0, 0.0, 1.0, 1.0), self.tint);
                }
            } else {
                let file = self.active_media().file;
                if file != 0 {
                    rc.surface(file, 0.0, 0.0, w, h, self.tint);
                }
            }
        }
        if let (Some(label), Some(font)) = (self.label.as_deref(), self.label_font.as_deref())
            && !label.is_empty()
        {
            let tx = (w - font.measure_width(label)) * 0.5;
            let ty = (h - font.line_height) * 0.5;
            rc.string_dat(
                font,
                label,
                tx,
                ty,
                self.label_color,
                self.outline,
                self.outline_color,
            );
        }
    }

    fn active_cursor_state_name(&self) -> &str {
        &self.active_state
    }

    fn consumes_dat_children(&self) -> bool {
        self.child_import != crate::widgets::ChildImport::All
    }

    fn child_import(&self) -> crate::widgets::ChildImport {
        self.child_import
    }

    fn retail_state(&self) -> Option<u32> {
        Some(self.active_retail_state_id())
    }

    fn set_retail_state(&mut self, ui: &mut UiRoot, me: ElementId, state: u32) -> bool {
        let info = self.info.clone();
        let mut applied = state;
        if state == DIRECT_STATE_ID {
            if !info.has_state(state) && !info.state_media.contains_key("") {
                return false;
            }
            self.active_state.clear();
        } else if let Some(s) = info.state(state) {
            self.active_state = s.name.clone();
        } else {
            let name = states::standard_state_name(state);
            if name.is_empty() || !info.state_media.contains_key(name) {
                self.active_state.clear();
                applied = DIRECT_STATE_ID;
            } else {
                self.active_state = name.to_owned();
            }
        }
        apply_state_visibility(ui, me, &info, applied);
        if info.state(applied).is_some_and(|s| s.pass_to_children) {
            cascade_state(ui, me, applied);
        }
        true
    }
}

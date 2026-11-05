//! The 3D viewport: a rectangle the renderer fills with a private scene (the
//! paperdoll figure, an examined object's portrait, the character-creation
//! preview). The UI only places it: the draw list gets one textured quad
//! naming the runtime texture the renderer drew the scene into.

use crate::draw::{Color, RenderContext, TextureRef};
use crate::event::{UiEvent, kind};
use crate::root::{ElementMessage, UiRoot};
use crate::tree::{Element, ElementId, Widget};

/// A viewport.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Viewport {
    /// The texture the renderer drew the scene into (`None`: nothing yet;
    /// the rectangle stays empty).
    pub texture: Option<TextureRef>,
    /// The texture's rows run bottom-up (flip it when drawing).
    pub texture_is_bottom_up: bool,
    /// Clicks are reported as [`ElementMessage::ViewportClicked`].
    pub emits_clicks: bool,
}

impl Widget for Viewport {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if !self.emits_clicks {
            return ui.dispatch_to_pointer_region(me, e);
        }
        match e.kind {
            kind::MOUSE_DOWN => true,
            kind::CLICK => {
                ui.send_message(
                    me,
                    ElementMessage::ViewportClicked {
                        x: e.data1,
                        y: e.data2,
                    },
                );
                true
            }
            _ => ui.dispatch_to_pointer_region(me, e),
        }
    }

    fn handles_click(&self, element: &Element) -> bool {
        self.emits_clicks
            || element
                .pointer_region
                .as_ref()
                .is_some_and(|r| r.handler.handles_click())
    }

    fn consumes_dat_children(&self) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some(texture) = self.texture else {
            return;
        };
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        let (v0, v1) = if self.texture_is_bottom_up {
            (1.0, 0.0)
        } else {
            (0.0, 1.0)
        };
        rc.sprite(texture, 0.0, 0.0, w, h, (0.0, v0, 1.0, v1), Color::WHITE);
    }
}

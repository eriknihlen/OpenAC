//! `uidemo`: the plugin UI end to end. A markup window with bound text, a
//! meter, a toggle, a slider, a list, a field and buttons; a canvas that
//! paints shapes, measured text and its own image and takes the pointer;
//! a world label, a filled triangle, an icon and a ring under the character.
//! All of it is session state: the plugin runs the same with no window, and
//! a window that attaches later shows it as it stands.
//!
//! - `/uidemo` (or `/uidemo status`) logs and posts what the plugin reads
//!   back: window shown, controls, canvas, text measure, images, window
//!   attached.
//! - `/uidemo show` | `hide` shows or hides its window.
//! - `/uidemo inventory` toggles the client's inventory window (false with
//!   no window attached).
//! - `/uidemo label on|off` shows or clears the label, triangle, icon and
//!   ring.
//! - `/uidemo rename <text>` sets the title label's text
//!   (`SetControlLabel`).
//! - Two hotkeys: `ping` on Ctrl+Shift+P, and `escape` on Escape, which
//!   the client's key map holds (refused while a window is attached).
//!   `/uidemo hotkeys` logs how they stand, `/uidemo rebind` moves `ping`
//!   to Ctrl+Shift+O; a press logs `uidemo hotkey <id>`.
//! - `/uidemo typed` opens a window whose checkbox and colour picker use typed
//!   bindings; what they send logs `uidemo typed <handler> <value>`.

use openac_plugin::ui::{
    ActionValue, Anchor, BindingKind, Canvas, CanvasSpec, ClientWindow, Image, MapPoint,
    MarkerAnchor, MarkerLayer, Panel, PanelSpec, PointerEvent, TriangleLayer, UiEvent, ground_ring,
    label, rgba, triangle, world_icon,
};
use openac_plugin::ui::{Font, FontWeight, PluginKey, TypedBindingKind, TypedValue, chord};
use openac_plugin::{Event, Host, Plugin, StartInfo};

/// The window's markup: OpenAC's vocabulary, `{Binding}`s declared below.
pub const MARKUP: &str = r##"<panel x="40" y="120" w="260" h="250" resizable="true" minw="220" minh="200" title="UI Demo">
  <label name="Headline" x="10" y="24" text="Plugin UI demo" color="#FFFFD060"/>
  <label name="Status" x="10" y="42" text="{Status}"/>
  <meter x="10" y="62" w="236" h="12" fill="{Progress}" color="#FF40A0FF"/>
  <toggle name="LabelToggle" x="10" y="80" w="200" h="18" text="Name over the character" checked="{LabelOn}" onclick="{ToggleLabel}"/>
  <slider x="10" y="102" w="236" h="16" min="0" max="100" value="{Volume}" onchange="{SetVolume}"/>
  <label x="10" y="120" text="{VolumeText}"/>
  <list name="Items" x="10" y="138" w="236" h="54" items="{Items}" colors="{ItemColors}" selected="{Selected}" onchange="{Pick}" selectionband="true" anchor="left,top,right"/>
  <field name="Entry" x="10" y="198" w="160" h="20" text="{Draft}" onsubmit="{Submit}" anchor="left,bottom"/>
  <button name="Ping" x="176" y="198" w="70" h="20" text="Ping" onclick="{Ping}" anchor="right,bottom"/>
  <button x="10" y="222" w="110" h="20" text="Inventory" onclick="{Inventory}" anchor="left,bottom"/>
  <button x="126" y="222" w="120" h="20" text="Hide canvas" onclick="{ToggleCanvas}" anchor="right,bottom"/>
</panel>"##;

/// A window whose controls use typed bindings.
const TYPED_MARKUP: &str = r#"<panel x="40" y="400" w="200" h="80" title="Typed">
  <checkbox x="8" y="28" w="100" h="18" text="Loot" checked="{Maybe}" onchange="{SetFlag}"/>
  <color x="8" y="50" w="28" h="20" value="{Colour}" onchange="{SetColour}"/>
</panel>"#;

/// A CFF OpenType font of the plugin's own.
const OWN_FONT: &[u8] =
    include_bytes!("../../../../../../ac-plugin-ui/tests/data/fonts/NotoSansCffFixture.otf");

/// The canvas's size.
pub const CANVAS: (i32, i32) = (180, 110);

#[derive(Default)]
pub struct UiDemo {
    panel: Option<Panel>,
    canvas: Option<Canvas>,
    dot: Image,
    ticks: u64,
    pings: u32,
    label_on: bool,
    volume: f32,
    selected: i32,
    marker: (f32, f32),
    dragging: bool,
    canvas_shown: bool,
    character: Option<u32>,
    heading: Font,
    body: Font,
    own: Font,
    submitted: Vec<String>,
    triangles: Option<TriangleLayer>,
    markers: Option<MarkerLayer>,
}

const ITEMS: [&str; 4] = ["Mosswart", "Drudge", "Olthoi", "Banderling"];

impl UiDemo {
    fn hotkeys(host: &mut Host<'_>, why: &str) {
        let ping = host.hotkey_state("ping").unwrap_or_default();
        let escape = host.hotkey_state("escape").unwrap_or_default();
        host.info(&format!(
            "uidemo hotkeys {why}: ping bound={} key={} escape bound={}",
            ping.is_bound, ping.effective.key, escape.is_bound
        ));
    }

    fn status(&self) -> String {
        format!("ticks {} - pings {}", self.ticks / 66, self.pings)
    }

    fn push(&mut self, host: &mut Host<'_>) {
        let status = self.status();
        let Some(panel) = self.panel.as_mut() else {
            return;
        };
        panel.set("Status", status);
        panel.set("Progress", ((self.ticks % 660) as f32) / 660.0);
        panel.set("LabelOn", self.label_on);
        panel.set("Volume", self.volume);
        panel.set("VolumeText", format!("Volume: {:.0}", self.volume));
        panel.set("Selected", self.selected);
        if let Err(error) = panel.flush(host) {
            host.error(&format!("uidemo: push refused: {error}"));
        }
    }

    fn paint(&self, host: &mut Host<'_>) {
        let Some(canvas) = self.canvas else {
            return;
        };
        let (title_w, line_h) = host.measure_text("uidemo canvas");
        let dot = self.dot;
        let marker = self.marker;
        let pings = self.pings;
        let (heading, body, own) = (self.heading, self.body, self.own);
        let result = canvas.paint(host, |p| {
            p.clear(rgba(0, 0, 0, 170));
            p.stroke_rect(
                0.0,
                0.0,
                CANVAS.0 as f32,
                CANVAS.1 as f32,
                rgba(200, 160, 60, 255),
                1.0,
            );
            p.text("uidemo canvas", (6.0, 4.0), rgba(255, 220, 120, 255), true);
            p.text_in(heading, "Golem", (6.0, 40.0), rgba(255, 255, 255, 255), false);
            p.text_in(body, "body text", (6.0, 62.0), rgba(255, 255, 255, 255), true);
            p.text_in(own, "AVTo", (90.0, 40.0), rgba(255, 220, 120, 255), false);
            // The title's measured width, underlined.
            p.line(
                (6.0, 5.0 + line_h),
                (6.0 + title_w, 5.0 + line_h),
                rgba(255, 220, 120, 255),
                1.0,
            );
            p.push_clip(4.0, 26.0, CANVAS.0 as f32 - 8.0, CANVAS.1 as f32 - 30.0);
            for i in 0..=pings.min(8) {
                let x = 10.0 + i as f32 * 18.0;
                p.fill_rect(x, 80.0, 12.0, 12.0, rgba(60, 200, 90, 255));
            }
            p.image(
                dot,
                marker.0 - 6.0,
                marker.1 - 6.0,
                12.0,
                12.0,
                rgba(255, 255, 255, 255),
            );
            p.pop_clip();
        });
        if let Err(error) = result {
            host.error(&format!("uidemo: paint refused: {error}"));
        }
    }

    fn show_label(&mut self, host: &mut Host<'_>) {
        let labels = match (self.label_on, self.character) {
            (true, Some(id)) => {
                let mut l = label(id, "uidemo");
                l.r = 0.4;
                l.g = 1.0;
                l.b = 0.4;
                l.line = 1;
                vec![l]
            }
            _ => Vec::new(),
        };
        host.show_labels(&labels);
        self.show_world_drawing(host);
    }

    /// The world drawing that goes with the label: a triangle near the map's
    /// centre, and an icon over the character and a ring under it.
    fn show_world_drawing(&mut self, host: &mut Host<'_>) {
        let triangles = *self
            .triangles
            .get_or_insert_with(|| host.create_triangle_layer());
        let markers = *self
            .markers
            .get_or_insert_with(|| host.create_marker_layer());
        let shown = self.label_on.then_some(self.character).flatten();
        let at = |east_west| MapPoint {
            east_west,
            north_south: 0.0,
            elevation: 0.0,
        };
        let corners = shown.map(|_| triangle(at(0.0), at(0.01), at(0.02), 0x28DC_285A));
        triangles.set_triangles(host, corners.as_slice());
        let icon = shown.map(|id| world_icon(MarkerAnchor::Object(id), self.dot));
        markers.set_icons(host, icon.as_slice());
        let ring = shown.map(|id| ground_ring(MarkerAnchor::Object(id), 2.0, 0.2, 0x28DC_2880));
        markers.set_shapes(host, ring.as_slice());
    }

    fn report(&self, host: &mut Host<'_>, why: &str) {
        let shown = host.view_visible("main");
        let exists = host.view_exists("UI Demo");
        let control = host.control_exists("main", "Ping");
        let canvas = self.canvas.and_then(|c| c.state(host)).map_or_else(
            || "none".to_owned(),
            |s| {
                format!(
                    "{}x{} visible={} listening={}",
                    s.width, s.height, s.visible, s.listening
                )
            },
        );
        let (w, h) = host.measure_text("Plugin UI demo");
        let images = host.image_count();
        let window = host.window_attached();
        host.info(&format!(
            "uidemo {why}: panel shown={shown} exists={exists} ping-control={control} canvas={canvas} measure={w}x{h} images={images} window={window}"
        ));
    }
}

impl Plugin for UiDemo {
    fn enable(&mut self, host: &mut Host<'_>, _info: &StartInfo) -> Result<(), String> {
        host.register_command(
            "uidemo",
            "The plugin UI demo (status, show, hide, inventory, label on|off, rename <text>)",
        )?;
        let spec = PanelSpec::new("main", "UI Demo", MARKUP)
            .binding_type("UiDemoBindings")
            .icon_text("UD")
            .bind("Status", BindingKind::Text)
            .bind("Progress", BindingKind::Float)
            .bind("LabelOn", BindingKind::Bool)
            .bind("ToggleLabel", BindingKind::Action)
            .bind("Volume", BindingKind::Float)
            .bind("SetVolume", BindingKind::FloatAction)
            .bind("VolumeText", BindingKind::Text)
            .bind("Items", BindingKind::TextList)
            .bind("ItemColors", BindingKind::UintList)
            .bind("Selected", BindingKind::Int)
            .bind("Pick", BindingKind::IntAction)
            .bind("Draft", BindingKind::Text)
            .bind("Submit", BindingKind::TextAction)
            .bind("Ping", BindingKind::Action)
            .bind("Inventory", BindingKind::Action)
            .bind("ToggleCanvas", BindingKind::Action);
        let mut panel = host.register_panel(&spec)?;
        panel.set(
            "Items",
            ITEMS.iter().map(|s| (*s).to_owned()).collect::<Vec<_>>(),
        );
        panel.set(
            "ItemColors",
            vec![0x00FF_D070u32, 0x00A0_E0A0, 0x00FF_8080, 0x0080_C0FF],
        );
        panel.set("Draft", "type here");
        self.panel = Some(panel);
        self.volume = 50.0;
        self.selected = -1;
        self.label_on = true;
        self.canvas_shown = true;
        self.marker = (90.0, 55.0);
        self.canvas = Some(host.register_canvas(&CanvasSpec {
            anchor: Anchor::TopRight,
            offset: (-220.0, 40.0),
            accepts_pointer_input: true,
            ..CanvasSpec::new("board", CANVAS.0, CANVAS.1)
        })?);
        if let Some(canvas) = self.canvas {
            canvas.listen_pointer(host, true);
        }
        // A 4x4 round dot of the plugin's own pixels.
        let mut dot = Vec::with_capacity(64);
        for y in 0..4 {
            for x in 0..4 {
                let corner = (x == 0 || x == 3) && (y == 0 || y == 3);
                dot.extend_from_slice(if corner {
                    &[0, 0, 0, 0]
                } else {
                    &[255, 80, 80, 255]
                });
            }
        }
        self.dot = host.image_rgba("dot", 4, 4, &dot);
        // Its fonts: the bundled one in both weights, an OpenType file of its
        // own, and a file that is no font, which is refused without harm.
        self.heading = host.font_bundled(18.0, FontWeight::SemiBold);
        self.body = host.font_bundled(12.0, FontWeight::Regular);
        self.own = host.font_load("fonts/own.otf", OWN_FONT, 16.0, &[]);
        let bad = host.font_load("fonts/bad.ttf", b"not a font", 16.0, &[]);
        let spare = host.font_bundled(12.0, FontWeight::Regular);
        let released = host.font_release(spare);
        let (own_w, own_h) = host.font_measure(self.own, "AV");
        let count = host.font_count();
        host.info(&format!(
            "uidemo fonts: heading={} body={} own={} bad={} released={released} count={count} own_size={own_w}x{own_h}",
            self.heading.is_valid(),
            self.body.is_valid(),
            self.own.is_valid(),
            bad.is_valid(),
        ));
        self.push(host);
        self.paint(host);
        host.register_hotkey("ping", "Ping", chord(PluginKey::P, true, false, true))?;
        host.register_hotkey(
            "escape",
            "Escape",
            chord(PluginKey::Escape, false, false, false),
        )?;
        Self::hotkeys(host, "enable");
        self.report(host, "enable");
        Ok(())
    }

    fn disable(&mut self, host: &mut Host<'_>) {
        host.info(&format!(
            "uidemo stop: ticks={} pings={}",
            self.ticks, self.pings
        ));
    }

    fn tick(&mut self, host: &mut Host<'_>) {
        self.ticks += 1;
        if self.character.is_none()
            && let Some(c) = host.character()
        {
            self.character = Some(c.id);
            self.show_label(host);
            self.report(host, "in-world");
        }
        if self.ticks % 66 == 0 {
            self.push(host);
        }
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        match event {
            Event::Command(call) => {
                let (sub, rest) = match call.arguments.split_once(' ') {
                    Some((sub, rest)) => (sub, rest.trim()),
                    None => (call.arguments.as_str(), ""),
                };
                match sub {
                    "show" => {
                        let done = host.show_panel("main", true);
                        host.info(&format!("uidemo show: {done}"));
                    }
                    "hide" => {
                        let done = host.show_panel("main", false);
                        host.info(&format!("uidemo hide: {done}"));
                    }
                    "inventory" => {
                        let shown = host.toggle_client_window(ClientWindow::Inventory);
                        host.info(&format!("uidemo inventory toggled: visible={shown}"));
                    }
                    "label" => {
                        self.label_on = rest != "off";
                        self.show_label(host);
                        self.push(host);
                    }
                    "rename" => {
                        let done = host.set_control_label("main", "Headline", rest);
                        host.info(&format!("uidemo rename: {done}"));
                    }
                    "typed" => {
                        let spec = PanelSpec::new("typed", "Typed", TYPED_MARKUP)
                            .bind_typed("Maybe", TypedBindingKind::OptionalBool)
                            .bind_typed("SetFlag", TypedBindingKind::BoolAction)
                            .bind_typed("Colour", TypedBindingKind::Uint)
                            .bind_typed("SetColour", TypedBindingKind::UintAction);
                        match host.register_panel(&spec) {
                            Ok(mut panel) => {
                                panel.set("Maybe", TypedValue::OptionalBool(None));
                                panel.set("Colour", TypedValue::Uint(0xFF33_6699));
                                let flushed = panel.flush(host);
                                panel.show(host, true);
                                host.info(&format!("uidemo typed: {flushed:?}"));
                            }
                            Err(why) => host.info(&format!("uidemo typed refused: {why}")),
                        }
                    }
                    "hotkeys" => Self::hotkeys(host, "now"),
                    "actions" => {
                        let listen = rest != "off";
                        let before = host.listen_input_actions(listen);
                        host.info(&format!("uidemo actions {listen} (was {before})"));
                    }
                    "rebind" => {
                        host.rebind_hotkey("ping", chord(PluginKey::O, true, false, true));
                        Self::hotkeys(host, "rebound");
                    }
                    _ => self.report(host, "status"),
                }
            }
            Event::Ui(UiEvent::Action(action)) if action.handler == "SetFlag" => {
                host.info(&format!("uidemo typed SetFlag {:?}", action.value.as_bool()));
            }
            Event::Ui(UiEvent::Action(action)) if action.handler == "SetColour" => {
                let colour = action.value.as_uint().map(|c| format!("{c:08X}"));
                host.info(&format!("uidemo typed SetColour {colour:?}"));
            }
            Event::Ui(UiEvent::Action(action)) => {
                match (action.handler.as_str(), action.value) {
                    ("Ping", _) => {
                        self.pings += 1;
                        self.paint(host);
                    }
                    ("ToggleLabel", _) => {
                        self.label_on = !self.label_on;
                        self.show_label(host);
                    }
                    ("SetVolume", ActionValue::Float(v)) => self.volume = v,
                    ("Pick", ActionValue::Int(row)) => self.selected = row,
                    ("Submit", ActionValue::Text(text)) => self.submitted.push(text),
                    ("Inventory", _) => {
                        let shown = host.toggle_client_window(ClientWindow::Inventory);
                        host.info(&format!("uidemo inventory toggled: visible={shown}"));
                    }
                    ("ToggleCanvas", _) => {
                        self.canvas_shown = !self.canvas_shown;
                        if let Some(canvas) = self.canvas {
                            canvas.set_visible(host, self.canvas_shown);
                        }
                    }
                    (other, value) => host.warn(&format!("uidemo: unexpected {other} {value:?}")),
                }
                host.info(&format!(
                    "uidemo ui {}: pings={} label={} volume={:.0} selected={} submitted={:?}",
                    action.handler,
                    self.pings,
                    self.label_on,
                    self.volume,
                    self.selected,
                    self.submitted
                ));
                self.push(host);
            }
            Event::Ui(UiEvent::Hotkey(id)) => {
                host.info(&format!("uidemo hotkey {id}"));
            }
            Event::Ui(UiEvent::InputAction(action, activation)) => {
                host.info(&format!("uidemo action {action} {activation}"));
            }
            Event::Ui(UiEvent::Pointer(pointer)) => {
                // Hover and leave (API 1.6) are logged and draw nothing.
                match pointer.kind {
                    PointerEvent::DOWN => self.dragging = true,
                    PointerEvent::UP | PointerEvent::CANCELLED => self.dragging = false,
                    _ => {}
                }
                if self.dragging || pointer.kind == PointerEvent::UP {
                    self.marker = (
                        pointer.x.clamp(6.0, CANVAS.0 as f32 - 6.0),
                        pointer.y.clamp(32.0, CANVAS.1 as f32 - 6.0),
                    );
                    self.paint(host);
                }
                if pointer.kind != PointerEvent::MOVE {
                    host.info(&format!(
                        "uidemo pointer kind={} at {:.0},{:.0} button={}",
                        pointer.kind, pointer.x, pointer.y, pointer.button
                    ));
                }
            }
            Event::WindowAttached(attached) => {
                self.report(
                    host,
                    if attached {
                        "window-attached"
                    } else {
                        "window-left"
                    },
                );
            }
            _ => {}
        }
    }
}

openac_plugin::export_plugin!(UiDemo);

#[cfg(test)]
mod tests {
    //! The plugin against the SDK's fake host, which keeps its UI exactly
    //! as the client does: the markup is checked against the declared
    //! bindings, and the values and events go through the same code.

    use openac_plugin::testing::FakeHost;
    use openac_plugin::ui::UiValue;

    use super::*;

    #[test]
    fn the_panel_registers_and_answers_its_controls() {
        let mut host = FakeHost::new("openac.uidemo");
        let mut plugin = UiDemo::default();
        host.enable(&mut plugin).unwrap();
        let panel = plugin.panel.as_ref().unwrap().id();
        assert_eq!(
            host.panel_value(panel, "VolumeText"),
            Some(UiValue::Text("Volume: 50".into()))
        );
        host.click(&mut plugin, panel, "Ping");
        host.click(&mut plugin, panel, "Ping");
        assert_eq!(plugin.pings, 2);
        host.act(
            &mut plugin,
            panel,
            "Pick",
            openac_plugin::ui::ActionValue::Int(2),
        );
        assert_eq!(host.panel_value(panel, "Selected"), Some(UiValue::Int(2)));
        // No window: the client windows are unavailable.
        assert!(host.type_line(&mut plugin, "/uidemo inventory"));
        assert!(
            host.logs
                .iter()
                .any(|(_, l)| l == "uidemo inventory toggled: visible=false")
        );
        assert!(host.type_line(&mut plugin, "/uidemo hide"));
        assert!(!host.ui.panels().next().unwrap().shown());
    }
}

//! The modern look must never leak into Classic: a themed window switched
//! back to Classic draws exactly what a window that never opted in draws, and
//! that is the draw list the windows had before the modern look existed,
//! pinned here by hash.

use std::cell::RefCell;
use std::collections::HashSet;
use std::fmt::Write as _;
use std::rc::Rc;
use std::sync::Arc;

use ac_dat::objects::FontCharDesc;
use ac_plugin_ui::{
    BindingKind as K, BindingSchema, DialogView, PanelDescriptor, PanelView, PluginUi, UiOwner,
    UiValue,
};

use super::*;
use crate::draw::{DrawList, SpriteSizes, TextureRef};
use crate::event::{Key, Modifiers, MouseButton};
use crate::font::Atlas;
use crate::host::UiHost;

pub(super) const CLASSIC_TEXTURE: u32 = 0x0600_0001;
pub(super) const REGULAR_TEXTURE: u32 = 0x0600_0002;
pub(super) const SEMIBOLD_TEXTURE: u32 = 0x0600_0003;

/// A font with a glyph for every printable ASCII character, seven pixels
/// apart on 14-pixel lines, so its measurements are the same on every machine.
pub(super) fn test_font(texture: u32) -> Arc<UiFont> {
    let atlas = Atlas {
        texture: TextureRef::Surface(texture),
        width: 256,
        height: 256,
    };
    let glyphs = (0x20u16..0x7F).map(|unicode| FontCharDesc {
        unicode,
        offset_x: 0,
        offset_y: 0,
        width: 6,
        height: 12,
        horizontal_offset_before: 0,
        horizontal_offset_after: 1,
        vertical_offset_before: 1,
    });
    Arc::new(UiFont::new(0, atlas, None, 14.0, 11.0, glyphs, 0, 0))
}

/// Every sprite is known, so retail art draws.
#[derive(Debug)]
struct EverySprite;

impl SpriteSizes for EverySprite {
    fn sprite_size(&self, _file: u32) -> Option<(u32, u32)> {
        Some((16, 16))
    }
}

struct SurfaceIcons;

impl MarkupIcons for SurfaceIcons {
    fn surface(&mut self, id: u32) -> Option<Icon> {
        Some((TextureRef::Surface(id), 16.0, 16.0))
    }
    fn spell(&mut self, _spell: u32) -> Option<Icon> {
        None
    }
    fn item(&mut self, _object: u32) -> Option<Icon> {
        None
    }
}

fn schema() -> BindingSchema {
    BindingSchema::new("Gallery")
        .with("Live", K::Bool)
        .with("Click", K::Action)
        .with("TabOn", K::Bool)
        .with("TabOff", K::Bool)
        .with("On", K::Bool)
        .with("Flag", K::Bool)
        .with("Maybe", K::OptionalBool)
        .with("SetFlag", K::BoolAction)
        .with("Value", K::Float)
        .with("Changed", K::FloatAction)
        .with("Text", K::Text)
        .with("Items", K::TextList)
        .with("Selected", K::Text)
        .with("Pick", K::TextAction)
        .with("Index", K::Int)
        .with("Choose", K::IntAction)
        .with("Flags", K::BoolList)
        .with("Log", K::TextList)
        .with("Fill", K::Float)
        .with("Choice", K::Int)
        .with("Count", K::Int)
        .with("SetCount", K::IntAction)
        .with("Key", K::Text)
        .with("SetKey", K::TextAction)
        .with("Colour", K::Uint)
        .with("SetColour", K::UintAction)
        .with("Levels", K::IntList)
        .with("Keys", K::TextList)
        .with("Buttons", K::TextList)
}

fn values() -> Vec<(String, UiValue)> {
    let texts = |items: &[&str]| UiValue::TextList(items.iter().map(|s| (*s).to_owned()).collect());
    let log: Vec<String> = (0..40).map(|i| format!("line {i}")).collect();
    [
        ("Live", UiValue::Bool(true)),
        ("TabOn", UiValue::Bool(true)),
        ("TabOff", UiValue::Bool(false)),
        ("On", UiValue::Bool(true)),
        ("Flag", UiValue::Bool(false)),
        ("Maybe", UiValue::OptionalBool(None)),
        ("Value", UiValue::Float(0.6)),
        ("Text", UiValue::Text("Asheron".to_owned())),
        (
            "Items",
            texts(&["Alpha", "Beta", "Gamma", "Delta", "Omega"]),
        ),
        ("Selected", UiValue::Text("Beta".to_owned())),
        ("Index", UiValue::Int(1)),
        (
            "Flags",
            UiValue::BoolList(vec![true, false, true, false, true]),
        ),
        ("Log", UiValue::TextList(log)),
        ("Fill", UiValue::Float(0.4)),
        ("Choice", UiValue::Int(1)),
        ("Count", UiValue::Int(3)),
        ("Key", UiValue::Text("Ctrl+K".to_owned())),
        ("Colour", UiValue::Uint(0x8033_6699)),
        ("Levels", UiValue::IntList(vec![0, 1, 0, 1, 0])),
        ("Keys", texts(&["a", "b", "c", "d", "e"])),
        ("Buttons", texts(&["Go", "", "Go", "Go", "Go"])),
    ]
    .into_iter()
    .map(|(n, v)| (n.to_owned(), v))
    .collect()
}

/// Every control, enabled through one binding.
const BODY: &str = r##"
  <group x="8" y="28" w="300" h="20" background="#FF202020" border="#FF404040" />
  <label x="12" y="52" text="Target" />
  <label x="60" y="52" text="Tinted" color="#FFCC3333" />
  <button name="Start" x="12" y="70" w="80" h="24" text="Start" onclick="{Click}" enabled="{Live}"><contextmenu items="{Items}" onselect="{Choose}"/></button>
  <button x="96" y="70" w="80" h="24" text="Iconed" icon="0x06001234" onclick="{Click}" enabled="{Live}"/>
  <tab x="180" y="70" w="80" h="24" text="Status" selected="{TabOn}" onclick="{Click}" enabled="{Live}"/>
  <tab x="264" y="70" w="80" h="24" text="Spells" selected="{TabOff}" onclick="{Click}" enabled="{Live}"/>
  <toggle x="12" y="100" w="160" h="20" text="Auto-rebuff" checked="{On}" onclick="{Click}" enabled="{Live}"/>
  <toggle x="180" y="100" w="160" h="20" text="Off" checked="{Flag}" onclick="{Click}" enabled="{Live}"/>
  <slider x="12" y="126" w="200" h="16" value="{Value}" onchange="{Changed}" enabled="{Live}"/>
  <field name="Entry" x="12" y="148" w="200" h="24" text="{Text}" enabled="{Live}"/>
  <menu name="Picker" x="12" y="178" w="200" h="24" items="{Items}" selected="{Selected}" onchange="{Pick}" rows="3" enabled="{Live}"/>
  <list x="12" y="208" w="200" h="60" items="{Items}" selected="{Index}" onchange="{Choose}" selectionband="true" enabled="{Live}"/>
  <list x="220" y="208" w="160" h="60" selected="{Index}" enabled="{Live}">
    <column type="check" width="20" values="{Flags}" onchange="{Choose}" />
    <column type="text" width="*" items="{Items}" />
  </list>
  <list x="388" y="208" w="200" h="60" selected="{Index}" enabled="{Live}">
    <column type="text" width="*" items="{Items}" />
    <column type="button" width="60" items="{Buttons}" onclick="{Choose}" />
  </list>
  <list x="12" y="274" w="200" h="80" header="true" selected="{Index}" onchange="{Choose}" enabled="{Live}">
    <column type="text" header="Name" items="{Items}" />
  </list>
  <list x="220" y="274" w="160" h="80" tree="true" levels="{Levels}" keys="{Keys}" selected="{Index}" onchange="{Choose}" enabled="{Live}">
    <column type="text" items="{Items}" />
  </list>
  <log x="388" y="274" w="200" h="80" items="{Log}" />
  <meter x="12" y="360" w="200" h="12" fill="{Fill}" color="#FFCC3333" />
  <checkbox x="220" y="360" w="100" h="20" text="Loot" checked="{On}" onchange="{SetFlag}" enabled="{Live}"/>
  <checkbox x="324" y="360" w="100" h="20" text="Off" checked="{Flag}" onchange="{SetFlag}" enabled="{Live}"/>
  <checkbox x="428" y="360" w="100" h="20" text="Some" checked="{Maybe}" onchange="{SetFlag}" enabled="{Live}"/>
  <separator x="12" y="384" w="200" h="4" />
  <radio x="12" y="392" w="120" h="40" items="A|B" selected="{Choice}" onchange="{Choose}" enabled="{Live}"/>
  <radio x="140" y="392" w="120" h="24" items="A|B" orientation="horizontal" style="segmented" selected="{Choice}" onchange="{Choose}" enabled="{Live}"/>
  <number x="268" y="392" w="100" h="24" value="{Count}" onchange="{SetCount}" enabled="{Live}"/>
  <hotkey x="376" y="392" w="120" h="24" value="{Key}" onchange="{SetKey}" enabled="{Live}"/>
  <color name="Swatch" x="504" y="392" w="40" h="24" value="{Colour}" onchange="{SetColour}" enabled="{Live}"/>
  <expander x="12" y="436" w="200" h="60" text="Advanced" enabled="{Live}"><label x="4" y="4" text="Inside"/></expander>
  <expander x="220" y="436" w="160" h="60" text="Folded" open="false" enabled="{Live}"/>
  <split x="388" y="436" w="200" h="60" enabled="{Live}"><group/><group/></split>
"##;

/// One window under test.
pub(super) struct Gallery {
    pub(super) host: UiHost,
    state: PluginUi,
    panel: u32,
    revision: u64,
    theme: PluginTheme,
    /// What the window's controls sent.
    pub(super) outbox: PluginOutbox,
}

impl Gallery {
    /// The gallery with `attributes` on its root, under `theme`.
    fn new(attributes: &str, theme: PluginTheme) -> Self {
        Self::of(
            &format!(
                r#"<panel x="10" y="10" w="600" h="510" title="Gallery" resizable="true" {attributes}>{BODY}</panel>"#
            ),
            theme,
        )
    }

    /// The window `markup` describes, under `theme`.
    pub(super) fn of(markup: &str, theme: PluginTheme) -> Self {
        let mut state = PluginUi::new();
        let panel = state
            .register_panel(
                &UiOwner::new("test.gallery", "Gallery"),
                PanelDescriptor::new("main", "Gallery"),
                markup,
                schema(),
            )
            .unwrap();
        state.set_values("test.gallery", panel, values()).unwrap();
        let view = state.panels().next().unwrap().clone();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
        let mut controller = build_panel(
            &mut host.root,
            &view,
            Some(test_font(CLASSIC_TEXTURE)),
            Rc::clone(&outbox),
        )
        .with_modern_fonts(Some(ModernFonts {
            regular: test_font(REGULAR_TEXTURE),
            semibold: test_font(SEMIBOLD_TEXTURE),
        }));
        let root = host.root.root();
        host.root.add_child(root, controller.root);
        host.root
            .register_window(
                &controller.window_name,
                controller.root,
                Some(controller.root),
                0,
            )
            .unwrap();
        controller.sync(&mut host.root, &view, 1, &mut SurfaceIcons, theme);
        host.attach_controller(&view.window_name(), Box::new(controller))
            .unwrap();
        let mut gallery = Self {
            host,
            state,
            panel,
            revision: 1,
            theme,
            outbox,
        };
        gallery.tick();
        gallery
    }

    fn view(&self) -> PanelView {
        self.state.panels().next().unwrap().clone()
    }

    fn sync(&mut self) {
        self.revision += 1;
        let (view, revision, theme) = (self.view(), self.revision, self.theme);
        self.host
            .with_controller::<PluginPanelController, _>(&view.window_name(), |c, root| {
                c.sync(root, &view, revision, &mut SurfaceIcons, theme);
            })
            .unwrap();
        self.tick();
    }

    pub(super) fn set_theme(&mut self, theme: PluginTheme) {
        self.theme = theme;
        self.sync();
    }

    pub(super) fn set(&mut self, name: &str, value: UiValue) {
        self.state
            .set_values("test.gallery", self.panel, vec![(name.to_owned(), value)])
            .unwrap();
        self.sync();
    }

    pub(super) fn tick(&mut self) {
        self.host.tick(0.016, 16, None);
    }

    pub(super) fn draw(&mut self) -> DrawList {
        self.tick();
        self.host.draw((1024.0, 768.0), &EverySprite)
    }

    /// The screen centre of the named element.
    pub(super) fn centre(&self, name: &str) -> (i32, i32) {
        let ui = &self.host.root;
        let found = find_named(ui, ui.root(), name).unwrap();
        let (x, y) = ui.screen_position(found);
        let e = ui.element(found).unwrap();
        ((x + e.width / 2.0) as i32, (y + e.height / 2.0) as i32)
    }

    pub(super) fn click(&mut self, button: MouseButton, (x, y): (i32, i32)) {
        self.host.mouse_move(x, y);
        self.host.mouse_down(button, x, y, 0);
        self.host.mouse_up(button, x, y, 0);
    }
}

pub(super) fn find_named(ui: &UiRoot, start: ElementId, name: &str) -> Option<ElementId> {
    if ui.element(start)?.name.as_deref() == Some(name) {
        return Some(start);
    }
    ui.children(start)
        .into_iter()
        .find_map(|c| find_named(ui, c, name))
}

fn fnv(text: &str) -> u64 {
    text.bytes().fold(0xcbf2_9ce4_8422_2325, |hash, byte| {
        (hash ^ u64::from(byte)).wrapping_mul(0x0100_0000_01b3)
    })
}

/// A draw list, one command a line.
fn dump(list: &DrawList) -> String {
    let mut out = String::new();
    for command in &list.main {
        writeln!(out, "main {command:?}").unwrap();
    }
    for command in &list.overlay {
        writeln!(out, "overlay {command:?}").unwrap();
    }
    out
}

/// Each state the Classic windows are pinned in: its name and draw list.
fn scenes(attributes: &str, through_moss: bool) -> Vec<(String, DrawList)> {
    let mut g = Gallery::new(attributes, PluginTheme::Classic);
    if through_moss {
        g.set_theme(PluginTheme::Moss);
        g.draw();
        g.set_theme(PluginTheme::Classic);
    }
    let mut out = vec![("rest".to_owned(), g.draw())];

    // Keyboard focus through the tab stops, from the text field.
    let entry = g.centre("Entry");
    g.click(MouseButton::Left, entry);
    out.push(("field".to_owned(), g.draw()));
    for i in 0..18 {
        g.host.key_down(Key::Tab, Modifiers::NONE);
        g.host.key_up(Key::Tab, Modifiers::NONE);
        out.push((format!("tab {i}"), g.draw()));
    }
    g.host.root.set_keyboard_focus(None);

    // Hover and press.
    let start = g.centre("Start");
    g.host.mouse_move(start.0, start.1);
    out.push(("hover".to_owned(), g.draw()));
    g.host.mouse_down(MouseButton::Left, start.0, start.1, 0);
    out.push(("press".to_owned(), g.draw()));
    g.host.mouse_up(MouseButton::Left, start.0, start.1, 0);

    // The popups.
    g.click(MouseButton::Right, start);
    out.push(("context menu".to_owned(), g.draw()));
    g.host.key_down(Key::Down, Modifiers::NONE);
    out.push(("context menu item".to_owned(), g.draw()));
    g.host.key_down(Key::Escape, Modifiers::NONE);
    g.host.key_up(Key::Escape, Modifiers::NONE);
    g.tick();
    let picker = g.centre("Picker");
    g.click(MouseButton::Left, picker);
    out.push(("menu".to_owned(), g.draw()));
    g.click(MouseButton::Left, picker);
    g.tick();
    let swatch = g.centre("Swatch");
    g.click(MouseButton::Left, swatch);
    out.push(("colour".to_owned(), g.draw()));
    g.host.key_down(Key::Escape, Modifiers::NONE);
    g.host.key_up(Key::Escape, Modifiers::NONE);
    g.tick();

    g.host.mouse_move(1000, 760);
    g.set("Live", UiValue::Bool(false));
    out.push(("disabled".to_owned(), g.draw()));
    out
}

/// A window with a legacy title, then one with a title bar.
const WINDOWS: [&str; 2] = ["", r#"titlebar="true""#];

/// What the Classic windows drew, by state: as before the modern look existed,
/// plus the frame on expanders.
const PINNED: &str = include_str!("../../tests/data/plugin_classic/pinned.txt");

#[test]
fn a_window_under_classic_draws_what_it_drew_before_the_modern_look_and_framed_expanders() {
    let mut report = String::new();
    for attributes in WINDOWS {
        for (name, list) in scenes(attributes, false) {
            writeln!(report, "[{attributes}] {name}: {:#x}", fnv(&dump(&list))).unwrap();
        }
    }
    report.push_str(&dialog_line());
    // The states must be ones that show: focus moves from control to control.
    let focus: HashSet<&str> = report
        .lines()
        .filter(|l| l.contains(" tab "))
        .filter_map(|l| l.rsplit(' ').next())
        .collect();
    assert!(focus.len() > 20, "{} focus states", focus.len());
    assert_eq!(report, PINNED);
}

#[test]
fn a_themed_window_switched_back_to_classic_draws_exactly_like_a_classic_window() {
    for chrome in WINDOWS {
        let themed = format!(r#"theme="plugin" {chrome}"#);
        let plain = scenes(chrome, false);
        let back = scenes(&themed, true);
        for ((name, a), (_, b)) in plain.iter().zip(&back) {
            assert_eq!(dump(a), dump(b), "{chrome} / {name}");
        }
        // The capture records a real window, and sees the theme.
        assert!(plain[0].1.main.len() > 100, "{}", plain[0].1.main.len());
        let mut moss = Gallery::new(&themed, PluginTheme::Moss);
        assert_ne!(dump(&moss.draw()), dump(&plain[0].1));
    }
}

/// The dialog box's line in the pinned list.
fn dialog_line() -> String {
    let dialog = DialogView {
        handle: 7,
        owner: UiOwner::new("test.gallery", "Gallery"),
        owner_panel: None,
        title: "Delete".to_owned(),
        message: "Delete it?".to_owned(),
        buttons: vec!["OK".to_owned(), "Cancel".to_owned()],
    };
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let (widget, w, h) =
        dialog::PluginDialogBox::new(&dialog, Some(test_font(CLASSIC_TEXTURE)), outbox);
    let mut host = UiHost::new();
    host.root.set_size(1024.0, 768.0);
    let root = host.root.root();
    host.root.add(root, Element::at(0.0, 0.0, w, h), widget);
    let list = host.draw((1024.0, 768.0), &EverySprite);
    format!("dialog: {:#x}\n", fnv(&dump(&list)))
}

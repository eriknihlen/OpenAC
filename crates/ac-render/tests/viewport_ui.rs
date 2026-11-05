//! The UI's 3-D viewports against the retail dats: the inventory window's
//! figure viewport and the examination window's portrait viewport are
//! viewport widgets the renderer is asked to fill while they show.
//! Ignored by default; `scripts/check.*` runs them when the dats are
//! present.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Instant;

use ac_dat::DatCollection;
use ac_render::scene::Scene;
use ac_render::ui::WindowUi;
use ac_render::ui::viewports::EXAMINE_VIEWPORT_ID;
use ac_render::ui::window::INVENTORY_WINDOW;
use ac_render::viewport::{ViewportOutcome, ViewportSlot, ViewportSubject};
use ac_ui::widgets::Viewport;

fn dats() -> Arc<DatCollection> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(DatCollection::open(dir).expect("open the retail dats"))
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_inventory_figure_is_asked_for_while_the_inventory_shows() {
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = Scene::default();
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let (requests, _) = ui.viewport_requests(&scene, 1.0);
    assert!(
        requests.iter().all(|r| r.slot != ViewportSlot::Paperdoll),
        "the inventory is closed: {requests:?}"
    );

    ui.host_mut().root.show_window(INVENTORY_WINDOW);
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let (requests, _) = ui.viewport_requests(&scene, 1.0);
    let doll = requests
        .iter()
        .find(|r| r.slot == ViewportSlot::Paperdoll)
        .expect("the figure is asked for");
    // Before the character description the heritage is unknown.
    assert_eq!(doll.subject, ViewportSubject::Paperdoll { heritage: 0 });
    let (w, h) = doll.size;
    assert!(
        w > 100 && h > 100,
        "the figure frame (224 x 214 authored): {w} x {h}"
    );
    assert!((doll.aspect - w as f32 / h as f32).abs() < 0.05);

    // The drawn texture reaches the widget; a figure not drawn keeps it.
    ui.show_viewports(&[(ViewportSlot::Paperdoll, ViewportOutcome::Drawn)]);
    assert_eq!(
        ui.viewport_texture(ViewportSlot::Paperdoll),
        Some(ViewportSlot::Paperdoll.texture())
    );
    ui.show_viewports(&[(ViewportSlot::Paperdoll, ViewportOutcome::Keep)]);
    assert_eq!(
        ui.viewport_texture(ViewportSlot::Paperdoll),
        Some(ViewportSlot::Paperdoll.texture())
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_figure_flash_from_the_paperdoll_reaches_the_renderer() {
    // OpenAC `PaperdollFigureLightingRelay`: the paperdoll's flash request
    // goes to the figure's lighting (here: the world renderer's selection
    // flash, `renderer::viewport::flash_doll`).
    use ac_ui::panels::PanelOutput;
    use ac_ui::root::UiOutput;
    let mut ui = WindowUi::build(dats(), (1280, 720)).expect("the UI builds");
    assert!(ui.take_figure_flashes().is_empty());
    let commands = ui.handle(vec![
        UiOutput::Panel(PanelOutput::FlashFigure {
            whole_figure: false,
            part_mask: 0b101,
        }),
        UiOutput::Panel(PanelOutput::FlashFigure {
            whole_figure: true,
            part_mask: 0,
        }),
    ]);
    assert!(commands.is_empty());
    assert_eq!(ui.take_figure_flashes(), [(false, 0b101), (true, 0)]);
    assert!(ui.take_figure_flashes().is_empty());
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_examination_window_holds_the_portrait_viewport() {
    let ui = WindowUi::build(dats(), (1280, 720)).expect("the UI builds");
    let root = &ui.host().root;
    let frame = root
        .window(ac_render::ui::window::EXAMINATION_WINDOW)
        .expect("the examination window")
        .frame;
    let portrait = root
        .find_descendant(frame, EXAMINE_VIEWPORT_ID)
        .expect("the creature page's viewport");
    assert!(root.widget::<Viewport>(portrait).is_some());
    let e = root.element(portrait).unwrap();
    assert!(
        e.width > 50.0 && e.height > 50.0,
        "{} x {}",
        e.width,
        e.height
    );
}

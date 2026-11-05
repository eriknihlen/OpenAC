//! The jump power bar.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/JumpPowerbarControllerTests.cs`
//! and `JumpPowerbarLayoutConformanceTests.cs` (against the dats instead of
//! its fixture). OpenAC's window-visibility callback is the value `update`
//! returns.

use std::collections::HashMap;

use ac_ui::import::ImportedLayout;
use ac_ui::panels::jump_powerbar::{self as jp, JumpPowerbarController};
use ac_ui::root::UiRoot;
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::meter::compute_fill_rect;
use ac_ui::widgets::{Meter, Panel};

const JUMP_FILL: u32 = 0x0600_1354;

fn layout(ui: &mut UiRoot) -> (ImportedLayout, ElementId) {
    let r = ui.root();
    let root = ui.add(r, Element::at(0.0, 0.0, 610.0, 25.0), Panel::default());
    let mut m = Meter::default();
    m.element_id = jp::METER_ID;
    m.configure_state_fill(jp::JUMP_MODE_STATE, JUMP_FILL);
    let meter = ui.add(root, Element::at(5.0, 5.0, 600.0, 15.0), m);
    (
        ImportedLayout::from_parts(root, [(jp::METER_ID, meter)].into_iter().collect()),
        meter,
    )
}

fn fill(ui: &UiRoot, meter: ElementId) -> Option<f32> {
    ui.widget::<Meter>(meter).unwrap().fill
}

#[test]
fn a_charge_shows_fills_and_hides_the_authored_jump_meter() {
    let mut ui = UiRoot::new();
    let (layout, meter) = layout(&mut ui);
    let mut c = JumpPowerbarController::bind(&mut ui, &layout).unwrap();
    let mut shown = Vec::new();
    shown.extend(c.update(&mut ui, None, true));
    assert_eq!(shown, [false]);
    assert_eq!(fill(&ui, meter), Some(0.0));
    let m = ui.widget::<Meter>(meter).unwrap();
    assert_eq!(m.active_retail_state_id(), jp::JUMP_MODE_STATE);
    assert_eq!(m.front_tile, JUMP_FILL);

    shown.extend(c.update(&mut ui, Some(0.35), false));
    assert_eq!(shown, [false, true]);
    assert_eq!(fill(&ui, meter), Some(0.35));

    shown.extend(c.update(&mut ui, Some(1.4), false));
    assert_eq!(shown, [false, true]);
    assert_eq!(fill(&ui, meter), Some(1.0));

    shown.extend(c.update(&mut ui, None, false));
    assert_eq!(shown, [false, true, false]);
    assert_eq!(fill(&ui, meter), Some(0.0));
}

#[test]
fn bind_refuses_a_meter_without_the_jump_state() {
    let mut ui = UiRoot::new();
    let r = ui.root();
    let root = ui.add(r, Element::default(), Panel::default());
    let meter = ui.add(root, Element::default(), Meter::default());
    let layout = ImportedLayout::from_parts(root, HashMap::from([(jp::METER_ID, meter)]));
    assert!(JumpPowerbarController::bind(&mut ui, &layout).is_none());
}

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import, import_infos};
    use std::path::PathBuf;
    use std::sync::Arc;

    fn content() -> DatContent {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatContent::new(Arc::new(
            ac_dat::DatCollection::open(dir).expect("open the retail dats"),
        ))
        .expect("master property table")
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_retail_bar_has_the_floaty_geometry_track_and_jump_fill() {
        let c = content();
        let info = import_infos(&c, jp::LAYOUT_ID).unwrap();
        let mut ui = UiRoot::new();
        let layout = import(&mut ui, &c, jp::LAYOUT_ID, c.font(DEFAULT_FONT_ID)).unwrap();
        let root = ui.element(layout.root).unwrap();
        assert_eq!((root.width, root.height), (610.0, 25.0));
        let meter = layout.find(jp::METER_ID).unwrap();
        let e = ui.element(meter).unwrap();
        assert_eq!((e.left, e.top, e.width, e.height), (5.0, 5.0, 600.0, 15.0));
        assert_eq!(ui.widget::<Meter>(meter).unwrap().back_tile, 0x0600_4D0B);
        let c2 = JumpPowerbarController::bind(&mut ui, &layout).unwrap();
        assert_eq!(c2.meter(), meter);
        assert_eq!(ui.widget::<Meter>(meter).unwrap().front_tile, JUMP_FILL);
        assert_eq!(compute_fill_rect(0.5, 600.0, 15.0), (0.0, 0.0, 300.0, 15.0));
        let limits = (
            info.effective_integer(0x3F, None),
            info.effective_integer(0x3D, None),
            info.effective_integer(0x3E, None),
            info.effective_integer(0x3C, None),
        );
        assert_eq!(limits, (Some(50), Some(810), Some(25), Some(25)));
    }
}

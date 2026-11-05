//! The frame-rate display.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/RetailFpsControllerTests.cs`
//! (against the dats instead of its committed fixture) and the frame-rate
//! cases of `tests/AcDream.App.Tests/Rendering/BuildingDegradeControllerTests.cs`.

use std::collections::HashMap;

use ac_ui::import::ImportedLayout;
use ac_ui::panels::fps::{self, FpsController, FrameRate};
use ac_ui::root::UiRoot;
use ac_ui::tree::{Container, Element};

#[test]
fn the_rate_uses_the_prior_twenty_frames_then_takes_the_finished_one() {
    let mut rate = FrameRate::new();
    for _ in 0..20 {
        rate.tick(0.05);
    }
    assert!((rate.fps() - 20.0 / 0.95).abs() < 1e-4);
    rate.tick(0.05);
    assert!((rate.fps() - 20.0).abs() < 1e-4);
}

#[test]
fn the_wide_sum_narrows_once_and_matches_the_retail_bits() {
    let mut rate = FrameRate::new();
    for _ in 0..10 {
        rate.tick(0.001);
        rate.tick(0.005);
    }
    rate.tick(0.0);
    assert_eq!(rate.fps().to_bits(), 0x43A6_AAAB);
}

#[test]
fn the_lines_have_two_decimals() {
    assert_eq!(fps::lines_text(59.994, 0.875), ["FPS: 59.99", "DEG: 0.88"]);
    assert_eq!(fps::lines_text(144.0, 1.0), ["FPS: 144.00", "DEG: 1.00"]);
}

#[test]
fn bind_refuses_a_layout_without_the_display_text() {
    let mut ui = UiRoot::new();
    let root = ui.insert(Element::default(), Container);
    let layout = ImportedLayout::from_parts(root, HashMap::new());
    assert!(FpsController::bind(&mut ui, &layout).is_none());
}

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import_infos_rooted, import_rooted};
    use ac_ui::widgets::Text;
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
    fn the_authored_smart_box_display_formats_updates_and_toggles() {
        let c = content();
        let info =
            import_infos_rooted(&c, fps::LAYOUT_ID, fps::DISPLAY_ELEMENT_ID).expect("display");
        assert_eq!(info.font_did, 0x4000_001A);
        assert_eq!((info.width, info.height), (128.0, 36.0));
        assert_eq!((info.left, info.top), (1, 1));

        let mut ui = UiRoot::new();
        ui.set_size(800.0, 600.0);
        let layout = import_rooted(
            &mut ui,
            &c,
            fps::LAYOUT_ID,
            fps::DISPLAY_ELEMENT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .unwrap();
        let r = ui.root();
        ui.add_child(r, layout.root);
        let controller = FpsController::bind(&mut ui, &layout).unwrap();
        let d = controller.display();
        let e = ui.element(d).unwrap();
        assert_eq!(e.dat_element_id, fps::DISPLAY_ELEMENT_ID);
        assert_eq!((e.left, e.top, e.width, e.height), (1.0, 1.0, 128.0, 36.0));

        controller.update(&mut ui, 59.994, 0.875, false);
        assert!(!ui.element(d).unwrap().visible());
        let texts = |ui: &UiRoot| -> Vec<String> {
            ui.widget::<Text>(d)
                .unwrap()
                .content_lines()
                .into_iter()
                .map(|l| l.text)
                .collect()
        };
        assert_eq!(texts(&ui), ["FPS: 59.99", "DEG: 0.88"]);

        controller.update(&mut ui, 144.0, 1.0, true);
        assert!(ui.element(d).unwrap().visible());
        assert_eq!(texts(&ui), ["FPS: 144.00", "DEG: 1.00"]);
    }
}

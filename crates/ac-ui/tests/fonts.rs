//! The retail dat fonts' border pixels and toggle glyphs.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiDatFontBorderPixelTests.cs`
//! (the installed-dat cases; the constructor cases are `font::tests`).

mod real_dats {
    use std::path::PathBuf;
    use std::sync::Arc;

    use ac_dat::DatCollection;
    use ac_dat::objects::Font;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::LayoutSource;

    fn dats() -> DatCollection {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatCollection::open(dir).unwrap()
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_retail_fonts_have_their_border_pixels() {
        let dats = dats();
        for (id, horizontal, vertical) in [
            (0x4000_0000, 4, 4),
            (0x4000_0001, 4, 4),
            (0x4000_0002, 3, 3),
            (0x4000_0025, 3, 3),
        ] {
            let font = dats.get::<Font>(id).unwrap().expect("the font");
            assert_eq!(
                (
                    font.num_horizontal_border_pixels,
                    font.num_vertical_border_pixels
                ),
                (horizontal, vertical),
                "{id:#010x}"
            );
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn a_font_has_a_border_exactly_when_it_has_a_background_atlas() {
        let dats = dats();
        let mut checked = 0;
        for id in 0x4000_0000..=0x4000_0032_u32 {
            let Ok(Some(font)) = dats.get::<Font>(id) else {
                continue;
            };
            checked += 1;
            let background = font.background_surface_data_id != 0;
            let border =
                font.num_horizontal_border_pixels > 0 || font.num_vertical_border_pixels > 0;
            assert_eq!(background, border, "{id:#010x}");
        }
        assert!(checked > 10, "the sweep found {checked} fonts");
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_default_font_has_the_toggle_glyphs() {
        let content = DatContent::new(Arc::new(dats())).unwrap();
        let font = content.font(DEFAULT_FONT_ID).expect("the default font");
        assert!(font.glyph('<').is_some());
        assert!(font.glyph('>').is_some());
    }
}

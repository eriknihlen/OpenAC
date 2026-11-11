//! The character window's titles page and the character information panel
//! in the game window: the titles from the session's title facts, named
//! from the dat's title table; the information panel mounted as OpenAC's
//! `RetailUiRuntime.MountCharacterInformationPanel` mounts it (main panel
//! 3, at (18, 18), resizable at the bottom) and fed the player's numbers
//! while it shows.

use ac_runtime::local_time::LocalZone;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::geometry::{AnchorEdges, ResizeEdges};
use ac_ui::import::{LayoutSource, import_infos_rooted, import_rooted};
use ac_ui::panels::character::CharacterStatController;
use ac_ui::panels::character_info::{
    self as info, CharacterInfoController, CharacterInfoSource, CharacterInfoStrings,
    format_birth_date,
};
use ac_ui::panels::character_titles::TitleNames;
use ac_ui::panels::main_panels::RESTORE_PREVIOUS_PROPERTY;
use ac_ui::panels::{DatRowTemplates, panel_id};
use ac_ui::strings::compute_hash;
use ac_ui::widgets::NineSliceFrame;

use super::facts::Dirty;
use super::mount::{Chrome, MountOptions, mount_window};
use super::social_panels::authors_full_panel_center;
use super::window::{CHARACTER_WINDOW, UI_STRING_TABLE, WindowUi};

/// The character information panel's name.
pub const CHARACTER_INFO_WINDOW: &str = "character-information";

/// The player's birth timestamp property (Unix seconds).
const CREATION_TIMESTAMP: u32 = 0x62;

/// What the two pages keep between frames: the title names and the
/// report's phrases (read from the dats once), the local zone.
#[derive(Debug)]
pub struct CharacterPagesState {
    pub(super) titles: TitleNames,
    phrases: CharacterInfoStrings,
    zone: LocalZone,
}

impl CharacterPagesState {
    /// The names and phrases of `content`'s dats.
    #[must_use]
    pub fn new(content: &ac_ui::DatContent) -> Self {
        let strings = content.strings();
        Self {
            titles: TitleNames::new(content.dats()),
            phrases: CharacterInfoStrings::from_dat(&|key| {
                strings.resolve_all(UI_STRING_TABLE, compute_hash(key))
            }),
            zone: LocalZone::system(),
        }
    }
}

impl WindowUi {
    /// The displayed title's text, for the character window's header.
    pub(super) fn display_title_text(&mut self) -> Option<String> {
        let id = self.facts.titles.as_ref()?.display_title_id;
        let strings = self.content.strings();
        self.character_pages.titles.resolve(strings, id)
    }

    /// OpenAC `RetailUiRuntime.MountCharacterInformationPanel`.
    pub(super) fn mount_character_info(&mut self) {
        match self.mount_character_info_window() {
            Ok(()) => self.mounted.push(CHARACTER_INFO_WINDOW),
            Err(e) => self
                .notices
                .push(format!("ui: {CHARACTER_INFO_WINDOW}: {e}")),
        }
    }

    fn mount_character_info_window(&mut self) -> Result<(), String> {
        let root_info = import_infos_rooted(&self.content, info::HOST_LAYOUT_ID, info::ROOT_ID)
            .ok_or("layout 0x2100006E slot 0x10000183 is not in the dats")?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            info::HOST_LAYOUT_ID,
            info::ROOT_ID,
            font.clone(),
        )
        .ok_or("layout 0x2100006E slot 0x10000183 is not in the dats")?;
        let ui = &mut self.host.root;
        let controller = CharacterInfoController::bind(ui, &layout, font);
        let frame = mount_window(
            ui,
            CHARACTER_INFO_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 18.0,
                top: 18.0,
                visible: false,
                resize_x: false,
                resize_y: true,
                resizable_edges: ResizeEdges::BOTTOM,
                constrain_resize_to_parent: true,
                content_anchors: AnchorEdges::ALL,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        if let Some(f) = ui.widget_mut::<NineSliceFrame>(frame) {
            f.draw_center_fill = !authors_full_panel_center(&root_info);
        }
        self.host
            .attach_controller(CHARACTER_INFO_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        let restore = root_info.effective_bool(RESTORE_PREVIOUS_PROPERTY, None) == Some(true);
        self.panels.register(
            &mut self.host.root,
            panel_id::CHARACTER_INFORMATION,
            CHARACTER_INFO_WINDOW,
            restore,
        );
        Ok(())
    }

    /// Feed the titles page when the titles change or the character window
    /// shows again, and the information panel while it shows.
    pub(super) fn update_character_pages(&mut self, dirty: Dirty, stale: &[&str]) {
        if let Some(view) = self.facts.titles.clone()
            && (dirty.titles || stale.contains(&CHARACTER_WINDOW))
        {
            let Self {
                host,
                content,
                character_pages,
                ..
            } = self;
            let strings = content.strings();
            let names = std::cell::RefCell::new(&mut character_pages.titles);
            let resolve = |id| names.borrow_mut().resolve(strings, id);
            let mut rows = DatRowTemplates::new(&*content, content.font(DEFAULT_FONT_ID));
            host.with_controller::<CharacterStatController, _>(CHARACTER_WINDOW, |c, ui| {
                if let Some(t) = c.titles_mut() {
                    t.update(ui, &view, &resolve, &mut rows);
                }
            });
        }
        let shown = self.host.root.is_window_visible(CHARACTER_INFO_WINDOW);
        if !shown || !(dirty.player || dirty.items || stale.contains(&CHARACTER_INFO_WINDOW)) {
            return;
        }
        let player = self.facts.state.player;
        let Some(object) = self.facts.objects.get(player) else {
            return;
        };
        let ints = object.properties.ints.clone();
        let (attributes, base, innate) = self
            .host
            .with_controller::<CharacterStatController, _>(CHARACTER_WINDOW, |c, _| {
                let s = c.sheet();
                (s.attributes, s.attribute_base, s.attribute_innate)
            })
            .unwrap_or_default();
        let birth_text = ints.get(&CREATION_TIMESTAMP).map(|&born| {
            let offset = self
                .character_pages
                .zone
                .utc_offset_seconds(i64::from(born) * 1000);
            format_birth_date(born, offset)
        });
        let source = CharacterInfoSource {
            ints,
            attributes,
            base,
            innate,
            birth_text,
        };
        let Self {
            host,
            character_pages,
            ..
        } = self;
        host.with_controller::<CharacterInfoController, _>(CHARACTER_INFO_WINDOW, |c, ui| {
            c.update(ui, &source, &character_pages.phrases);
        });
    }
}

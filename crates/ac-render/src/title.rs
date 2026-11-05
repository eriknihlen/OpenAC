//! The window's title: whose window it is and, in the world, how the frames
//! and the world are doing, in words a player can read:
//!
//! `+Name — OpenAC 0.1.8 | 165 fps, 6.1 ms a frame | landblocks 574 of 2652 drawn |
//! objects 1619, 10 animated | PY 122 Solclaim 11, Midsong-and-Half`
//!
//! Before a character is in the world (connecting, character selection and
//! creation) it is the program and its version alone.

use ac_core::world::CalendarDate;

use crate::scene::Scene;

/// The program and its version, as the title names them ("OpenAC 0.1.8").
#[must_use]
pub fn product() -> String {
    format!("OpenAC {}", env!("CARGO_PKG_VERSION"))
}

/// Whose window it is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TitleSubject<'a> {
    /// No character is in the world yet (or the window has no session).
    BeforeWorld,
    /// A character is in the world: its name, once the session has sent
    /// it.
    InWorld(Option<&'a str>),
}

impl<'a> TitleSubject<'a> {
    /// Whose window shows `scene`: the character its own session plays, so
    /// each window names its own. The name is the account's character list
    /// entry for the player, as character selection shows it (an admin's
    /// with its "+"; the server may name the object in the world without
    /// it, as while cloaked); the player object's name until the list is
    /// known.
    #[must_use]
    pub fn of(scene: Option<&'a Scene>) -> Self {
        match scene {
            Some(scene) if scene.in_world() => {
                let listed = scene.player_guid().and_then(|guid| {
                    scene
                        .pregame()
                        .selection
                        .as_ref()?
                        .entries
                        .iter()
                        .find(|entry| entry.character_id == guid)
                        .map(|entry| entry.name.as_str())
                });
                Self::InWorld(listed.or_else(|| scene.player().and_then(|p| p.name.as_deref())))
            }
            _ => Self::BeforeWorld,
        }
    }
}

/// What the title shows of the world.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct TitleFacts {
    /// Landblocks drawn last frame.
    pub visible_landblocks: usize,
    /// Landblocks loaded.
    pub total_landblocks: usize,
    /// Objects in the loaded world: the landblocks' statics, scenery and
    /// buildings, and the server's objects placed in the world.
    pub objects: usize,
    /// Server objects the window animates.
    pub animated: usize,
    /// The in-game date and hour, once the region's calendar is known.
    pub calendar: Option<CalendarDate>,
}

/// The frame rate and the world, refreshed twice a second.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TitleStats {
    /// Frames a second over the last refresh interval.
    pub fps: f64,
    /// The mean frame time over it, milliseconds.
    pub mean_ms: f64,
    /// The world.
    pub facts: TitleFacts,
}

/// The title for `subject`; `stats` are shown only in the world.
#[must_use]
pub fn format_title(subject: TitleSubject<'_>, stats: &TitleStats) -> String {
    let name = match subject {
        TitleSubject::BeforeWorld => return product(),
        TitleSubject::InWorld(name) => name,
    };
    let mut title = match name {
        Some(name) => format!("{name} — {}", product()),
        None => product(),
    };
    let facts = &stats.facts;
    title.push_str(&format!(
        " | {:.0} fps, {:.1} ms a frame | landblocks {} of {} drawn | objects {}, {} animated",
        stats.fps,
        stats.mean_ms,
        facts.visible_landblocks,
        facts.total_landblocks,
        facts.objects,
        facts.animated
    ));
    if let Some(date) = facts.calendar {
        title.push_str(&format!(
            " | PY {} {:?} {}, {}",
            date.year,
            date.month,
            date.day,
            ac_ui::panels::map_house::hour_text(date.hour)
        ));
    }
    title
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_core::world::{HourName, MonthName};
    use ac_proto::ModelData;
    use ac_runtime::session::PresentationEvent;
    use ac_runtime::world::{ObjectKey, ObjectView, WorldSnapshot};

    fn stats(calendar: Option<CalendarDate>) -> TitleStats {
        TitleStats {
            fps: 165.2,
            mean_ms: 6.06,
            facts: TitleFacts {
                visible_landblocks: 574,
                total_landblocks: 2652,
                objects: 1619,
                animated: 10,
                calendar,
            },
        }
    }

    fn solclaim_11(hour: HourName) -> Option<CalendarDate> {
        Some(CalendarDate {
            year: 122,
            month: MonthName::Solclaim,
            day: 11,
            hour,
        })
    }

    #[test]
    fn in_the_world_the_title_names_the_character_first_then_labelled_numbers() {
        let title = format_title(
            TitleSubject::InWorld(Some("+Aldric")),
            &stats(solclaim_11(HourName::MidsongAndHalf)),
        );
        assert_eq!(
            title,
            format!(
                "+Aldric — OpenAC {} | 165 fps, 6.1 ms a frame | landblocks 574 of 2652 drawn \
                 | objects 1619, 10 animated | PY 122 Solclaim 11, Midsong-and-Half",
                env!("CARGO_PKG_VERSION")
            )
        );
    }

    #[test]
    fn a_whole_hour_reads_by_its_name_and_an_unknown_calendar_is_left_out() {
        let whole = format_title(
            TitleSubject::InWorld(Some("+Aldric")),
            &stats(solclaim_11(HourName::Darktide)),
        );
        assert!(
            whole.ends_with(" | PY 122 Solclaim 11, Darktide"),
            "{whole}"
        );
        let unknown = format_title(TitleSubject::InWorld(Some("+Aldric")), &stats(None));
        assert!(unknown.ends_with("objects 1619, 10 animated"), "{unknown}");
        // The player's object not sent yet: the program leads.
        let nameless = format_title(TitleSubject::InWorld(None), &stats(None));
        assert!(
            nameless.starts_with(&format!("{} | 165 fps", product())),
            "{nameless}"
        );
    }

    #[test]
    fn before_the_world_the_title_is_the_program_and_its_version() {
        assert_eq!(
            format_title(TitleSubject::BeforeWorld, &stats(None)),
            format!("OpenAC {}", env!("CARGO_PKG_VERSION"))
        );
        assert_eq!(TitleSubject::of(None), TitleSubject::BeforeWorld);
    }

    fn object(local_id: u32, guid: u32, name: &str) -> ObjectView {
        ObjectView {
            key: ObjectKey {
                local_id,
                incarnation: 1,
            },
            guid,
            name: Some(name.to_owned()),
            setup_id: None,
            position: None,
            parent: None,
            placement: None,
            rest_placement: 0,
            scale: None,
            translucency: None,
            state: 0,
            velocity: None,
            model: ModelData::default(),
            motion_table_id: None,
            sound_table_id: None,
            radar: ac_runtime::world::RadarFacts::default(),
            motion: None,
            effects: ac_runtime::world::EffectFacts::default(),
            step_blend: None,
        }
    }

    fn scene(player_guid: Option<u32>, objects: Vec<ObjectView>) -> Scene {
        let mut scene = Scene::default();
        scene.apply(PresentationEvent::Snapshot(WorldSnapshot {
            player_guid,
            objects,
        }));
        scene
    }

    #[test]
    fn each_window_is_named_for_the_character_its_own_session_plays() {
        let first = scene(
            Some(0x5000_0001),
            vec![
                object(1, 0x7000_0001, "Drudge Skulker"),
                object(2, 0x5000_0001, "+Aldric"),
            ],
        );
        let second = scene(Some(0x5000_0002), vec![object(1, 0x5000_0002, "Brienne")]);
        assert_eq!(
            TitleSubject::of(Some(&first)),
            TitleSubject::InWorld(Some("+Aldric"))
        );
        assert_eq!(
            TitleSubject::of(Some(&second)),
            TitleSubject::InWorld(Some("Brienne"))
        );
    }

    #[test]
    fn a_session_without_a_character_in_the_world_is_before_the_world() {
        // Character selection: objects may be known, no player.
        let selecting = scene(None, vec![object(1, 0x7000_0001, "Drudge Skulker")]);
        assert_eq!(
            TitleSubject::of(Some(&selecting)),
            TitleSubject::BeforeWorld
        );
        assert_eq!(
            TitleSubject::of(Some(&Scene::default())),
            TitleSubject::BeforeWorld
        );
        // In the world before the player's object arrived.
        let arriving = scene(Some(0x5000_0001), Vec::new());
        assert_eq!(
            TitleSubject::of(Some(&arriving)),
            TitleSubject::InWorld(None)
        );
    }

    #[test]
    fn the_name_is_the_character_list_entry_as_character_selection_shows_it() {
        use ac_runtime::session::pregame::PreGameEvent;
        use ac_runtime::session::selection::{CharacterSelection, SelectionEntry};
        // The server names an admin's object in the world without the "+"
        // the account's character list gives it.
        let mut scene = scene(Some(0x5000_0001), vec![object(1, 0x5000_0001, "Aldric")]);
        let mut list = CharacterSelection::new().snapshot();
        list.entries = vec![
            SelectionEntry {
                active_index: 0,
                character_id: 0x5000_0002,
                name: "Brienne".into(),
                seconds_greyed_out: 0,
            },
            SelectionEntry {
                active_index: 1,
                character_id: 0x5000_0001,
                name: "+Aldric".into(),
                seconds_greyed_out: 0,
            },
        ];
        scene.apply(PresentationEvent::PreGame(PreGameEvent::Selection(
            Box::new(list),
        )));
        assert_eq!(
            TitleSubject::of(Some(&scene)),
            TitleSubject::InWorld(Some("+Aldric"))
        );
    }
}

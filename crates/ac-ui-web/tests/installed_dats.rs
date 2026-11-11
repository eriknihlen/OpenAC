//! The module driven as a page drives it, over the installed dats: every
//! header and every file it asks for is answered from the dat files
//! (exactly what a dat reader returns for the id, from the dat it named),
//! until the panels are built and want nothing more; then a small stream,
//! keys and pointer presses go in and what comes out is checked, and one
//! frame is compared with the native window UI drawing the same.
//!
//! Needs the dats in `Documents\Asheron's Call` under the user's profile.

use std::collections::{BTreeMap, HashSet};
use std::sync::Arc;

use ac_clock::Instant;
use ac_core::chat::ChatKind;
use ac_dat::{DatCollection, DatSource};
use ac_remote_wire::{DecodeContext, EncodeContext, PresentationEvent, SessionCommand};
use ac_runtime::communication::ChatLine;
use ac_runtime::movement::{MovementCommand, MovementKeys};
use ac_runtime::session::Generation;
use ac_runtime::world::{
    EffectFacts, ObjectKey, ObjectView, PlayerView, RadarFacts, VitalReading, WorldSnapshot,
};
use ac_ui_web::client::ClientPanels;
use ac_ui_web::host::{Modifiers, Module, Panels, Session};
use ac_ui_web::output::kind;
use ac_ui_web::page::{self, HEADER_BYTES, HEADER_ID};
use ac_ui_window::ui::{TextureLedger, UiBatch, WindowUi};

const WIDTH: u32 = 1024;
const HEIGHT: u32 = 768;
const PLAYER: u32 = 0x5000_0001;
const GENERATION: u64 = 3;

fn words(bytes: &[u8]) -> Vec<u32> {
    bytes
        .chunks_exact(4)
        .map(|w| u32::from_le_bytes([w[0], w[1], w[2], w[3]]))
        .collect()
}

/// What the page answered, by dat: files found, their bytes, files not
/// found.
#[derive(Debug, Default, Clone, Copy)]
struct Answered {
    files: usize,
    bytes: u64,
    missing: usize,
}

/// A page: the module, the installed dats it answers from, and what it
/// holds of the module's textures.
struct Page {
    module: Module<ClientPanels>,
    dats: DatCollection,
    dir: std::path::PathBuf,
    /// The page hands in the high-resolution dat (when the install has one).
    high_res: bool,
    now: f64,
    answered: BTreeMap<u32, Answered>,
    textures: HashSet<(u32, u32)>,
    /// The last picture: vertices (32 bytes each) and runs.
    vertices: Vec<u8>,
    runs: Vec<[u32; 5]>,
    /// Command frames taken since the last look.
    commands: Vec<Vec<u8>>,
    log: String,
}

impl Page {
    fn open() -> Self {
        Self::with_high_res(true)
    }

    fn with_high_res(high_res: bool) -> Self {
        let dir = ac_dat::default_dat_dir().expect("a user profile");
        let dats = DatCollection::open(&dir).expect("the installed dats");
        Self {
            module: Module::new(ClientPanels::new()),
            dats,
            dir,
            high_res,
            now: 0.0,
            answered: BTreeMap::new(),
            textures: HashSet::new(),
            vertices: Vec::new(),
            runs: Vec::new(),
            commands: Vec::new(),
            log: String::new(),
        }
    }

    /// The first bytes of dat `source`'s file, `None` when the install has
    /// none.
    fn header(&self, source: u32) -> Option<Vec<u8>> {
        let name = match page::source_of(source)? {
            DatSource::Portal => ac_dat::PORTAL_DAT,
            DatSource::Cell => ac_dat::CELL_DAT,
            DatSource::HighRes if !self.high_res => return None,
            DatSource::HighRes => ac_dat::HIGHRES_DAT,
            DatSource::Local => ac_dat::LOCAL_DAT,
        };
        let bytes = std::fs::read(self.dir.join(name)).ok()?;
        Some(bytes[..HEADER_BYTES.min(bytes.len())].to_vec())
    }

    fn answer(&mut self, source: u32, id: u32) {
        if id == HEADER_ID {
            let header = self.header(source);
            self.module.panels().dat(source, id, header.as_deref());
            return;
        }
        let dat = page::source_of(source).expect("a dat the page knows");
        let bytes = self
            .dats
            .file(dat)
            .and_then(|file| file.get_bytes(id).expect("a readable dat"));
        let tally = self.answered.entry(source).or_default();
        match &bytes {
            Some(b) => {
                tally.files += 1;
                tally.bytes += b.len() as u64;
            }
            None => tally.missing += 1,
        }
        self.module.panels().dat(source, id, bytes.as_deref());
    }

    /// One frame: take everything, answer every request. Returns how many
    /// requests it made and whether a picture came.
    fn frame(&mut self) -> (usize, bool) {
        self.now += 16.0;
        let mask = self.module.frame(self.now);
        let bit = |k: u32| mask & (1 << (k - 1)) != 0;
        let releases = words(self.module.take(kind::TEXTURE_RELEASES));
        for pair in releases.chunks_exact(2) {
            self.textures.remove(&(pair[0], pair[1]));
        }
        let uploads = self.module.take(kind::TEXTURE_UPLOADS).to_vec();
        self.read_uploads(&uploads);
        let picture = bit(kind::VERTICES);
        if picture {
            self.vertices = self.module.take(kind::VERTICES).to_vec();
            self.runs = words(self.module.take(kind::RUNS))
                .chunks_exact(5)
                .map(|r| [r[0], r[1], r[2], r[3], r[4]])
                .collect();
            for run in &self.runs {
                if run[0] != 0 {
                    assert!(
                        self.textures.contains(&(run[0], run[1])),
                        "a run draws texture {}:{:#010x}, never uploaded",
                        run[0],
                        run[1]
                    );
                }
                assert!((run[3] + run[4]) as usize * 32 <= self.vertices.len());
            }
        }
        let frames = self.module.take(kind::COMMAND_FRAMES).to_vec();
        let mut at = 0;
        while at < frames.len() {
            let len = words(&frames[at..at + 4])[0] as usize;
            self.commands.push(frames[at + 4..at + 4 + len].to_vec());
            at += 4 + len;
        }
        self.log
            .push_str(&String::from_utf8_lossy(self.module.take(kind::LOG)));
        for k in [
            kind::STORAGE_WRITES,
            kind::CLIPBOARD_COPY,
            kind::CLIPBOARD_PASTE,
            kind::CURSOR,
        ] {
            self.module.take(k);
        }
        let requests = words(self.module.take(kind::DAT_REQUESTS));
        for pair in requests.chunks_exact(2) {
            self.answer(pair[0], pair[1]);
        }
        (requests.len() / 2, picture)
    }

    fn read_uploads(&mut self, bytes: &[u8]) {
        let mut at = 0;
        while at < bytes.len() {
            let head = words(&bytes[at..at + 24]);
            let (texture_kind, id, format, levels) = (head[0], head[1], head[2], head[5]);
            at += 24;
            for _ in 0..levels {
                let size = words(&bytes[at..at + 8]);
                let per_texel = if format == 0 { 4 } else { 1 };
                at += 8 + (size[0] * size[1] * per_texel) as usize;
            }
            self.textures.insert((texture_kind, id));
        }
        assert_eq!(at, bytes.len(), "uploads end on a record");
    }

    /// Frames until a frame makes no request and the panels are built;
    /// returns the frames run.
    fn settle(&mut self, limit: usize) -> usize {
        for n in 1..=limit {
            let (requests, _) = self.frame();
            if requests == 0 && self.module.panels().ui().is_some() {
                return n;
            }
        }
        panic!(
            "not settled after {limit} frames ({} rounds)\n{}",
            self.module.panels().rounds(),
            self.log
        );
    }

    /// The commands sent since the last look, unstamped.
    fn sent(&mut self) -> Vec<SessionCommand> {
        let cx = DecodeContext::new(Instant::now(), None, None);
        std::mem::take(&mut self.commands)
            .iter()
            .map(
                |frame| match ac_remote_wire::decode_command(frame, &cx).unwrap() {
                    SessionCommand::AtGeneration {
                        generation,
                        command,
                    } => {
                        assert_eq!(generation, Generation(GENERATION));
                        *command
                    }
                    other => panic!("not stamped: {other:?}"),
                },
            )
            .collect()
    }
}

fn player() -> ObjectView {
    ObjectView {
        key: ObjectKey {
            local_id: 1,
            incarnation: 1,
        },
        guid: PLAYER,
        name: Some("Tester".into()),
        setup_id: None,
        position: Some(ac_proto::Position {
            cell_id: 0xA9B4_0021,
            origin: ac_proto::Vector3 {
                x: 100.0,
                y: 100.0,
                z: 50.0,
            },
            orientation: ac_proto::Quaternion::IDENTITY,
        }),
        parent: None,
        placement: None,
        rest_placement: 0,
        scale: None,
        translucency: None,
        state: 0,
        velocity: None,
        model: ac_proto::ModelData::default(),
        motion_table_id: None,
        sound_table_id: None,
        radar: RadarFacts::default(),
        motion: None,
        effects: EffectFacts::default(),
        step_blend: None,
    }
}

/// A snapshot with the player in the world, a chat line and the player's
/// vitals.
fn stream() -> Vec<PresentationEvent> {
    let vital = |current: u32, max: u32| VitalReading {
        current: Some(current),
        max: Some(max),
        percent: Some(current as f32 / max as f32),
    };
    vec![
        PresentationEvent::Snapshot(WorldSnapshot {
            player_guid: Some(PLAYER),
            objects: vec![player()],
        }),
        PresentationEvent::Chat(vec![ChatLine {
            sequence: 1,
            kind: ChatKind::Channel,
            log_text_type: 8,
            combat_kind: None,
            received_unix_ms: 1_790_000_000_000,
            text: "[General] Bob says, \"hi\"".into(),
            channel_name: "General".into(),
            sender: "Bob".into(),
            sender_guid: 0x5000_0002,
            words: "hi".into(),
            display_text: "Bob: hi".into(),
            channel_id: 0x0B,
            spans: None,
        }]),
        PresentationEvent::Player(PlayerView {
            health: vital(80, 100),
            stamina: vital(150, 200),
            mana: vital(60, 120),
            ..PlayerView::default()
        }),
    ]
}

fn forward_keys(command: &SessionCommand) -> bool {
    matches!(
        command,
        SessionCommand::Movement(MovementCommand::Keys(MovementKeys { forward: true, .. }))
    )
}

/// Hand in the headers, then the canvas, and run frames until the panels
/// are built and want nothing more; print what it took.
fn build(page: &mut Page) {
    // Before init: the headers, then the canvas.
    for (source, id) in page.module.panels().dat_preload() {
        page.answer(source, id);
    }
    page.module.panels().init(WIDTH, HEIGHT, 1.0, 0.0);
    page.module.panels().focus(true);
    let frames = page.settle(400);
    let rounds = page.module.panels().rounds();
    let total = page
        .answered
        .values()
        .fold(Answered::default(), |a, b| Answered {
            files: a.files + b.files,
            bytes: a.bytes + b.bytes,
            missing: a.missing + b.missing,
        });
    println!(
        "built in {rounds} rounds, {frames} frames; {} files, {} bytes, {} not found",
        total.files, total.bytes, total.missing
    );
    for (source, a) in &page.answered {
        println!(
            "  dat {source}: {} files, {} bytes, {} not found",
            a.files, a.bytes, a.missing
        );
    }
    println!("{}", page.log);
    assert!(rounds > 0 && rounds < 40, "{rounds} rounds");
}

#[test]
#[ignore = "reads the installed dats"]
fn without_the_high_resolution_dat_the_page_is_never_asked_for_it() {
    let mut page = Page::with_high_res(false);
    build(&mut page);
    assert!(!page.answered.contains_key(&2), "{:?}", page.answered);
}

#[test]
#[ignore = "reads the installed dats"]
fn the_panels_build_from_the_installed_dats_and_answer_a_page() {
    let mut page = Page::open();
    build(&mut page);

    // The session: a snapshot, a chat line, the vitals.
    let epoch = Instant::now();
    let batch = ac_remote_wire::encode_batch(&stream(), &EncodeContext::new(epoch)).unwrap();
    page.module.panels().stream(&batch, page.now).unwrap();
    let after_stream = page.settle(200);
    println!(
        "after the stream: {after_stream} frames; log:\n{}",
        page.log
    );
    assert!(
        !page.runs.is_empty() && !page.vertices.is_empty(),
        "a picture"
    );
    assert!(
        page.module
            .panels()
            .stream(&[0xFF, 1, 2], page.now)
            .is_err()
    );

    // Over a panel the pointer is the panels'; over empty space it is not.
    let mut over_panel = None;
    let mut over_world = None;
    for y in (8..HEIGHT).step_by(24) {
        for x in (8..WIDTH).step_by(24) {
            let used = page
                .module
                .panels()
                .pointer_move(x as f32, y as f32, page.now);
            if used {
                over_panel.get_or_insert((x as f32, y as f32));
            } else {
                over_world.get_or_insert((x as f32, y as f32));
            }
        }
    }
    let (panel, world) = (over_panel.expect("a panel"), over_world.expect("space"));
    let none = Modifiers::default();
    let panels = page.module.panels();
    assert!(panels.pointer_button(0, true, panel, none, page.now));
    assert!(panels.pointer_button(0, false, panel, none, page.now));
    assert!(!panels.pointer_button(0, true, world, none, page.now));
    assert!(!panels.pointer_button(0, false, world, none, page.now));
    page.frame();

    // Without control nothing goes to the session.
    page.module.panels().session(Session {
        generation: GENERATION,
        may_control: true,
        ..Session::default()
    });
    page.module
        .panels()
        .key(true, "KeyW", none, false, page.now);
    page.frame();
    page.frame();
    page.module
        .panels()
        .key(false, "KeyW", none, false, page.now);
    page.frame();
    assert!(page.sent().is_empty(), "nothing sent without control");

    // With control, W walks; held, it is sent again after a quarter
    // second; losing the keyboard lets it go.
    page.module.panels().session(Session {
        generation: GENERATION,
        controlling: true,
        may_control: true,
        ..Session::default()
    });
    assert!(
        page.module
            .panels()
            .key(true, "KeyW", none, false, page.now)
    );
    page.frame();
    let sent = page.sent();
    assert!(sent.iter().any(forward_keys), "{sent:?}");
    page.frame();
    assert!(page.sent().is_empty(), "not again within 250 ms");
    let started = page.now;
    while page.now < started + 250.0 {
        page.frame();
    }
    let again = page.sent();
    assert!(
        again.iter().any(forward_keys),
        "again after 250 ms: {again:?}"
    );
    page.module.panels().focus(false);
    page.frame();
    let released = page.sent();
    assert!(
        matches!(
            released.last(),
            Some(SessionCommand::Movement(MovementCommand::ReleaseKeys))
        ),
        "{released:?}"
    );
}

/// The native window UI over the mapped dats, fed `events` (none: no
/// session yet), drawn three frames: its last frame's vertex count and the
/// runs that draw, as the page reads them.
fn native_frame(
    dir: &std::path::Path,
    events: Option<Vec<PresentationEvent>>,
) -> (usize, Vec<[u32; 5]>) {
    let dats = Arc::new(DatCollection::open(dir).unwrap());
    let size = (WIDTH, HEIGHT);
    let mut ui = WindowUi::build_with(
        Arc::clone(&dats),
        size,
        &ac_host::settings::PlayerSettings::default(),
        ac_ui_window::ui::options::resolution_choices(&[], None),
        Some(Box::new(ac_ui::storage::MemoryUiStorage::new())),
    )
    .unwrap();
    ui.attach_key_bindings(&ac_ui::input::KeyBindings::retail_defaults());
    let mut scene = events.map(|events| {
        let mut scene = ac_ui_window::scene::Scene::default();
        for event in events {
            scene.apply(event);
        }
        scene
    });
    ui.set_pixels_per_unit(1.0);
    let mut batch = UiBatch::default();
    for _ in 0..3 {
        if let Some(scene) = scene.as_mut() {
            scene.present_at(Instant::now());
            ui.feed(scene);
        }
        let list = ui.frame(Instant::now(), size);
        batch = UiBatch::build_scaled(&list, 1.0);
        let _ = ui.drain();
    }
    let plan = TextureLedger::new().plan(&batch, size, &mut ui.surfaces());
    let runs = plan
        .runs
        .iter()
        .map(|run| {
            let (texture_kind, id) = page::texture_of(run.key.texture).words();
            let filter = match run.key.filter {
                ac_ui::draw::Filter::Linear => 0,
                ac_ui::draw::Filter::Nearest => 1,
            };
            [texture_kind, id, filter, run.first, run.count]
        })
        .collect();
    (batch.vertices.len(), runs)
}

#[test]
#[ignore = "reads the installed dats"]
fn a_frame_of_the_module_is_the_native_window_uis_frame() {
    let mut page = Page::open();
    for (source, id) in page.module.panels().dat_preload() {
        page.answer(source, id);
    }
    page.module.panels().init(WIDTH, HEIGHT, 1.0, 0.0);
    page.settle(400);
    page.frame();

    // Built, before any session: the same frame as the native UI's.
    let (vertices, runs) = native_frame(&page.dir, None);
    println!(
        "before the stream: module {} vertices, {} runs; native {vertices} vertices, {} runs",
        page.vertices.len() / 32,
        page.runs.len(),
        runs.len()
    );
    assert_eq!(page.vertices.len() / 32, vertices);
    assert_eq!(page.runs, runs);

    // After the stream the in-game windows show and read what they need
    // first now; once it has arrived they draw what the native UI draws.
    let epoch = Instant::now();
    let events = stream();
    let batch = ac_remote_wire::encode_batch(&events, &EncodeContext::new(epoch)).unwrap();
    page.module.panels().stream(&batch, page.now).unwrap();
    page.settle(200);
    page.frame();
    let (vertices, runs) = native_frame(&page.dir, Some(events));
    println!(
        "after the stream: module {} vertices, {} runs; native {vertices} vertices, {} runs",
        page.vertices.len() / 32,
        page.runs.len(),
        runs.len()
    );
    assert_eq!(page.vertices.len() / 32, vertices);
    assert_eq!(page.runs, runs);
}

#[test]
#[ignore = "reads the installed dats"]
fn once_its_tables_are_in_the_module_decodes_spellbooks_and_skills_with_them() {
    use ac_core::player::LocalPlayerState;
    use ac_core::spells::{ActiveEnchantmentRecord, EnchantmentTypeFlags, Spellbook};
    use ac_runtime::session::interface::{InterfaceEvent, PlayerFacts};

    let mut page = Page::open();
    build(&mut page);
    let e = EncodeContext::new(Instant::now());
    let batch = ac_remote_wire::encode_batch(&stream(), &e).unwrap();
    page.module.panels().stream(&batch, page.now).unwrap();
    page.settle(200);
    let answered: usize = page.answered.values().map(|a| a.files).sum();
    println!("with the tables: {answered} files");

    // The sender: the session's own tables, a spellbook and a blessing
    // that adds 9 to every attribute, so the skill formulas matter.
    let tables =
        ac_runtime::gameplay::GameTables::load(&DatCollection::open(&page.dir).unwrap()).unwrap();
    let spell = tables.spells().spell_ids().next().expect("a spell");
    let mut book = Spellbook::new(Some(Arc::clone(tables.spells())));
    book.on_enchantment_added(ActiveEnchantmentRecord {
        stat_mod_type: Some(
            (EnchantmentTypeFlags::ATTRIBUTE | EnchantmentTypeFlags::MULTIPLE_STAT).0 | 0x8000,
        ),
        stat_mod_key: Some(0),
        stat_mod_value: Some(9.0),
        bucket: 2,
        spell_category: 2,
        ..ActiveEnchantmentRecord::new(2, 2, 60.0, 0)
    });
    let mut numbers = LocalPlayerState::new(Some(book));
    numbers.set_skill_formula_bonus_resolver(tables.skill_formula_resolver());
    numbers.on_attribute_update(2, 0, 200, 0);
    numbers.on_skill_update(24, 0, 2, 0, 0, 0, 0.0, 100);
    let term = numbers.attribute_enchantment_skill_delta(24);
    assert_ne!(term, 0, "the blessing changes the skill");
    let expected = numbers.get_skill_value(24, None);
    let name = tables.spells().get(spell).map(|s| s.name.clone());
    let event = PresentationEvent::Interface(InterfaceEvent::Player(Box::new(PlayerFacts {
        revision: 1,
        numbers,
        session_seconds: 0.0,
    })));
    let bytes = ac_remote_wire::encode_batch(&[event], &e).unwrap();

    let cx = page
        .module
        .panels()
        .decode_context()
        .expect("attached")
        .clone();
    assert!(
        cx.spell_table.is_some(),
        "the spell table is in the context"
    );
    assert!(
        cx.skill_formula_bonus_resolver.is_some(),
        "and the formulas"
    );
    let back = ac_remote_wire::decode_batch(&bytes, &cx).unwrap();
    let [PresentationEvent::Interface(InterfaceEvent::Player(facts))] = &back[..] else {
        panic!("{back:?}");
    };
    let book = facts.numbers.spellbook().expect("a spellbook");
    assert_eq!(book.try_get_metadata(spell).map(|s| s.name.clone()), name);
    assert_eq!(facts.numbers.attribute_enchantment_skill_delta(24), term);
    assert_eq!(facts.numbers.get_skill_value(24, None), expected);
    page.module.panels().stream(&bytes, page.now).unwrap();
}

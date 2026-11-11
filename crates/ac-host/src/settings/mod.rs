//! The player's settings file (`settings.json` in the configuration
//! directory): the display, audio, mixer, chat, camera, misc and
//! per-character sections and the window layouts, each read with per-field
//! fallbacks and the file's schema migrations, and written back without
//! disturbing the other sections.

use std::path::{Path, PathBuf};

use crate::json::{self, Member, Node, Value};

mod layout;
mod markup_controls;
pub mod render_pack;
mod sections;

pub use layout::{UiWindowLayout, UiWindowPlacement, UiWindowPosition};
pub use markup_controls::MarkupControlKey;
pub use render_pack::RenderPackSelection;
pub use sections::{
    AudioMixerSettings, AudioSettings, CameraTurningSettings, CharacterSettings, ChatSettings,
    MAXIMUM_MAX_VOICES_PER_WAVE, MAXIMUM_VOICE_COUNT, MINIMUM_VOICE_COUNT, MiscSettings,
    MouseTurningMacro, NO_PER_WAVE_CAP, PlayerSettings, PluginUiSettings, chat_opacity,
    mouse_turning_macro, mouse_turning_text,
};

/// The schema version this code writes.
pub const CURRENT_SCHEMA_VERSION: i32 = 4;

/// Before version 4 the two texture-detail rows had no effect and their
/// labels ran the other way, so their stored values are not choices.
const TEXTURE_DETAIL_ROWS_LIVE_SCHEMA_VERSION: i32 = 4;

/// The graphics quality preset.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum QualityPreset {
    /// Low.
    Low,
    /// Medium.
    Medium,
    /// High (the default).
    High,
    /// Ultra.
    Ultra,
    /// The cheapest of everything.
    Potato,
}

const QUALITY_NAMES: [(&str, QualityPreset); 5] = [
    ("Low", QualityPreset::Low),
    ("Medium", QualityPreset::Medium),
    ("High", QualityPreset::High),
    ("Ultra", QualityPreset::Ultra),
    ("Potato", QualityPreset::Potato),
];

/// The smallest interface scale, percent.
pub const UI_SCALE_MIN_PERCENT: i32 = 50;
/// The largest interface scale, percent.
pub const UI_SCALE_MAX_PERCENT: i32 = 300;
/// The interface scale's step, percent.
pub const UI_SCALE_STEP_PERCENT: i32 = 10;
/// The interface scale by default: one interface unit a pixel.
pub const UI_SCALE_DEFAULT_PERCENT: i32 = 100;

/// `percent` as an interface scale the options offer: within
/// [`UI_SCALE_MIN_PERCENT`] to [`UI_SCALE_MAX_PERCENT`], on the nearest
/// [`UI_SCALE_STEP_PERCENT`] step (a half step rounds up).
#[must_use]
pub fn ui_scale_percent(percent: i32) -> i32 {
    let clamped = percent.clamp(UI_SCALE_MIN_PERCENT, UI_SCALE_MAX_PERCENT);
    let steps =
        (clamped - UI_SCALE_MIN_PERCENT + UI_SCALE_STEP_PERCENT / 2) / UI_SCALE_STEP_PERCENT;
    (UI_SCALE_MIN_PERCENT + steps * UI_SCALE_STEP_PERCENT).min(UI_SCALE_MAX_PERCENT)
}

/// How far particles are drawn.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParticleRange {
    /// As retail.
    Retail,
    /// Further (the default).
    Extended,
}

const PARTICLE_NAMES: [(&str, ParticleRange); 2] = [
    ("Retail", ParticleRange::Retail),
    ("Extended", ParticleRange::Extended),
];

/// The display settings. Every field is kept; the ones the window does not
/// use yet are carried through unchanged.
#[derive(Debug, Clone, PartialEq)]
pub struct DisplaySettings {
    /// `"<width>x<height>"`.
    pub resolution: String,
    /// Fullscreen window.
    pub fullscreen: bool,
    /// Present on the display's refresh.
    pub vsync: bool,
    /// Horizontal field of view, degrees.
    pub field_of_view: f32,
    /// Gamma.
    pub gamma: f32,
    /// Show the frame rate in the interface.
    pub show_fps: bool,
    /// Quality preset.
    pub quality: QualityPreset,
    /// Particle range.
    pub particle_range: ParticleRange,
    /// Screen brightness.
    pub screen_brightness: f32,
    /// Automatic degrades.
    pub automatic_degrades: bool,
    /// Graphics performance slider.
    pub graphics_performance: f32,
    /// Degrade distance.
    pub degrade_distance: f32,
    /// Landscape texture detail (0 is full).
    pub landscape_texture_detail: i32,
    /// Environment texture detail.
    pub environment_texture_detail: i32,
    /// Texture filtering.
    pub texture_filtering: i32,
    /// Landscape draw distance.
    pub landscape_draw_distance: i32,
    /// Building detail textures.
    pub building_detail_textures: bool,
    /// Landscape detail textures: a fine pattern repeated over the outdoor
    /// terrain near the player. Off by default (the game shipped it off).
    pub landscape_detail_textures: bool,
    /// Multi-pass alpha.
    pub multi_pass_alpha: bool,
    /// Keep distant buildings.
    pub keep_distant_buildings: bool,
    /// Potato mode.
    pub potato_mode: bool,
    /// Interface only (no world pass).
    pub ui_only: bool,
    /// Interface only while the window is in the background.
    pub ui_only_when_unfocused: bool,
    /// How large the in-game interface is drawn, percent
    /// ([`UI_SCALE_MIN_PERCENT`] to [`UI_SCALE_MAX_PERCENT`] in steps of
    /// [`UI_SCALE_STEP_PERCENT`]; 100 is one interface unit a pixel).
    pub ui_scale_percent: i32,
    /// The render-pack selection, kept as read (`None`: the retail pack).
    pub render_pack: Option<Node>,
}

impl Default for DisplaySettings {
    fn default() -> Self {
        Self {
            resolution: "1280x720".into(),
            fullscreen: false,
            vsync: true,
            field_of_view: 90.0,
            gamma: 1.0,
            show_fps: false,
            quality: QualityPreset::High,
            particle_range: ParticleRange::Extended,
            screen_brightness: 0.0,
            automatic_degrades: false,
            graphics_performance: 0.0,
            degrade_distance: 50.0,
            landscape_texture_detail: 0,
            environment_texture_detail: 1,
            texture_filtering: 1,
            landscape_draw_distance: 8,
            building_detail_textures: true,
            landscape_detail_textures: false,
            multi_pass_alpha: false,
            keep_distant_buildings: true,
            potato_mode: false,
            ui_only: false,
            ui_only_when_unfocused: false,
            ui_scale_percent: UI_SCALE_DEFAULT_PERCENT,
            render_pack: None,
        }
    }
}

impl DisplaySettings {
    /// The resolution as width and height, when it reads as `WxH` with
    /// both positive.
    #[must_use]
    pub fn resolution_size(&self) -> Option<(u32, u32)> {
        let (w, h) = self.resolution.split_once(['x', 'X'])?;
        let w = w.trim().parse::<u32>().ok().filter(|v| *v > 0)?;
        let h = h.trim().parse::<u32>().ok().filter(|v| *v > 0)?;
        Some((w, h))
    }
}

/// A field-of-view value from before version 3 (a vertical angle) in
/// today's terms: 60 becomes the default 90, others scale by the 16:9
/// aspect less 0.1, within 10 to 160 degrees.
#[must_use]
pub fn migrate_legacy_vertical_fov_degrees(legacy: f32) -> f32 {
    if legacy == 60.0 {
        return 90.0;
    }
    (legacy * (16.0 / 9.0 - 0.1)).clamp(10.0, 160.0)
}

/// Where a settings file's bytes are kept when they are not a file on disk
/// (a browser's storage): read whole and written whole, exactly the bytes
/// the file would hold.
pub trait SettingsBytes: Send + Sync {
    /// The bytes; `Ok(None)` when nothing is stored.
    ///
    /// # Errors
    /// They are stored but cannot be read.
    fn read(&self) -> std::io::Result<Option<Vec<u8>>>;

    /// Store `bytes`, replacing what was stored.
    ///
    /// # Errors
    /// They cannot be stored.
    fn write(&self, bytes: &[u8]) -> std::io::Result<()>;
}

/// The settings file.
#[derive(Clone)]
pub struct SettingsStore {
    /// The file, or for kept bytes the name messages use.
    path: PathBuf,
    /// The bytes, when they are kept somewhere other than the file.
    bytes: Option<std::sync::Arc<dyn SettingsBytes>>,
}

impl std::fmt::Debug for SettingsStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SettingsStore")
            .field("path", &self.path)
            .field("kept_bytes", &self.bytes.is_some())
            .finish()
    }
}

/// A value that could not be read as its field's type.
#[derive(Debug)]
pub(crate) struct Unreadable;

pub(crate) fn member<'a>(members: &'a [Member], name: &str) -> Option<&'a Node> {
    members.iter().find(|m| m.name == name).map(|m| &m.value)
}

pub(crate) fn read_string(members: &[Member], name: &str, fallback: &str) -> String {
    match member(members, name).map(|n| &n.value) {
        Some(Value::String(s)) => s.clone(),
        _ => fallback.to_owned(),
    }
}

pub(crate) fn read_bool(members: &[Member], name: &str, fallback: bool) -> bool {
    match member(members, name).map(|n| &n.value) {
        Some(Value::Bool(b)) => *b,
        _ => fallback,
    }
}

pub(crate) fn read_float(members: &[Member], name: &str, fallback: f32) -> Result<f32, Unreadable> {
    match member(members, name).map(|n| &n.value) {
        Some(Value::Number(raw)) => raw.parse::<f32>().map_err(|_| Unreadable),
        _ => Ok(fallback),
    }
}

pub(crate) fn read_int(members: &[Member], name: &str, fallback: i32) -> Result<i32, Unreadable> {
    match member(members, name) {
        Some(
            node @ Node {
                value: Value::Number(_),
                ..
            },
        ) => json::int32(node).map_err(|_| Unreadable),
        _ => Ok(fallback),
    }
}

/// An enumeration stored by name (any case) or by the number of a defined
/// value; anything else falls back.
pub(crate) fn read_enum<T: Copy>(
    members: &[Member],
    name: &str,
    names: &[(&str, T)],
    fallback: T,
) -> T {
    let Some(Value::String(text)) = member(members, name).map(|n| &n.value) else {
        return fallback;
    };
    if let Some(&(_, value)) = names.iter().find(|(n, _)| n.eq_ignore_ascii_case(text)) {
        return value;
    }
    text.trim()
        .parse::<usize>()
        .ok()
        .and_then(|i| names.get(i))
        .map_or(fallback, |&(_, value)| value)
}

pub(crate) fn schema_version(root: &[Member]) -> i32 {
    member(root, "version")
        .and_then(|n| json::int32(n).ok())
        .unwrap_or(1)
}

impl SettingsStore {
    /// The store at `path`.
    #[must_use]
    pub fn new(path: PathBuf) -> Self {
        Self { path, bytes: None }
    }

    /// A store whose file is `bytes`, kept by its host (a page with no file
    /// system); `name` stands for the file in messages. Read and written
    /// exactly as the file is; clones share the bytes.
    #[must_use]
    pub fn with_bytes(name: impl Into<PathBuf>, bytes: std::sync::Arc<dyn SettingsBytes>) -> Self {
        Self {
            path: name.into(),
            bytes: Some(bytes),
        }
    }

    /// Where the file is (for kept bytes, their name).
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// The file's bytes; `Ok(None)` when there is no file.
    fn read_bytes(&self) -> std::io::Result<Option<Vec<u8>>> {
        if let Some(bytes) = &self.bytes {
            return bytes.read();
        }
        match std::fs::read(&self.path) {
            Ok(bytes) => Ok(Some(bytes)),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error),
        }
    }

    pub(crate) fn read_root(&self) -> Result<Option<Vec<Member>>, String> {
        let bytes = match self.read_bytes() {
            Ok(Some(bytes)) => bytes,
            Ok(None) => return Ok(None),
            Err(error) => return Err(error.to_string()),
        };
        let node = json::parse(&bytes).map_err(|e| e.to_string())?;
        match node.value {
            Value::Object(members) => Ok(Some(members)),
            _ => Err("the file is not a JSON object".into()),
        }
    }

    /// The display settings: the defaults for anything missing, the defaults
    /// for everything when the file cannot be read (the second value then
    /// says why).
    #[must_use]
    pub fn load_display(&self) -> (DisplaySettings, Option<String>) {
        let root = match self.read_root() {
            Ok(Some(root)) => root,
            Ok(None) => return (DisplaySettings::default(), None),
            Err(why) => {
                return (
                    DisplaySettings::default(),
                    Some(format!(
                        "settings: failed to load {}: {why}; using defaults",
                        self.path.display()
                    )),
                );
            }
        };
        let Some(Value::Object(display)) = member(&root, "display").map(|n| &n.value) else {
            return (DisplaySettings::default(), None);
        };
        match read_display(display, schema_version(&root)) {
            Ok(settings) => (settings, None),
            Err(Unreadable) => (
                DisplaySettings::default(),
                Some(format!(
                    "settings: failed to load {}: a display value has the wrong type; using defaults",
                    self.path.display()
                )),
            ),
        }
    }

    /// Write the display section, keeping every other section of the file
    /// as it was, and stamp the current schema version.
    pub fn save_display(&self, display: &DisplaySettings) -> std::io::Result<()> {
        self.save_section("display", &display_object(display))
    }

    /// Write one top-level section (its object text, indented one level),
    /// keeping every other section as it was (in name order, a pre-version-4
    /// display section migrated first, since the version stamped promises
    /// it), and stamp the current schema version. A file that does not parse
    /// is replaced (its loads give the defaults); one that cannot be read
    /// fails the save, which would otherwise write away every other section.
    pub(crate) fn save_section(&self, name: &str, object_text: &str) -> std::io::Result<()> {
        let mut preserved: Vec<Member> = match self.read_root_for_save()? {
            Some(root) => {
                let version = schema_version(&root);
                root.into_iter()
                    .filter(|m| m.name != name && m.name != "version")
                    .map(|mut m| {
                        if m.name == "display" && version < TEXTURE_DETAIL_ROWS_LIVE_SCHEMA_VERSION
                        {
                            reset_dead_texture_detail_rows(&mut m.value);
                        }
                        m
                    })
                    .collect()
            }
            None => Vec::new(),
        };
        preserved.sort_by(|a, b| a.name.cmp(&b.name));
        let mut out = String::from("{\n");
        for m in &preserved {
            out.push_str("  ");
            json::write_string(&mut out, &m.name);
            out.push_str(": ");
            json::write_value(&mut out, &m.value, 1);
            out.push_str(",\n");
        }
        out.push_str("  ");
        json::write_string(&mut out, name);
        out.push_str(": ");
        out.push_str(object_text);
        out.push_str(",\n");
        out.push_str(&format!("  \"version\": {CURRENT_SCHEMA_VERSION}\n}}\n"));
        self.write(&out)
    }

    /// The whole file as members for a save: empty when missing or when it
    /// does not parse (a file that does not parse loads as defaults, so a
    /// save starts it over), a pre-version-4 display section migrated.
    ///
    /// # Errors
    /// The file exists but cannot be read: a save must then fail, as
    /// starting from nothing would write away every section it does not own.
    pub(crate) fn load_mutable_root(&self) -> std::io::Result<Vec<Member>> {
        match self.read_root_for_save()? {
            Some(mut root) => {
                if schema_version(&root) < TEXTURE_DETAIL_ROWS_LIVE_SCHEMA_VERSION
                    && let Some(display) = root.iter_mut().find(|m| m.name == "display")
                {
                    reset_dead_texture_detail_rows(&mut display.value);
                }
                Ok(root)
            }
            None => Ok(Vec::new()),
        }
    }

    /// The file's members for a save: `None` when it is missing or does not
    /// parse as a JSON object.
    ///
    /// # Errors
    /// The file exists but cannot be read.
    fn read_root_for_save(&self) -> std::io::Result<Option<Vec<Member>>> {
        let Some(bytes) = self.read_bytes()? else {
            return Ok(None);
        };
        Ok(match json::parse(&bytes) {
            Ok(Node {
                value: Value::Object(members),
                ..
            }) => Some(members),
            _ => None,
        })
    }

    /// Write the whole file from `root` with the current schema version
    /// (members in their order; a new version member goes last).
    pub(crate) fn write_mutable_root(&self, mut root: Vec<Member>) -> std::io::Result<()> {
        set_member(
            &mut root,
            "version",
            Value::Number(CURRENT_SCHEMA_VERSION.to_string()),
        );
        let mut out = String::new();
        json::write_value(&mut out, &Node::made(Value::Object(root)), 0);
        out.push('\n');
        self.write(&out)
    }

    /// Writes the whole file through a temporary file beside it, then a
    /// rename, so a save that fails part way leaves the old file whole.
    /// Kept bytes are replaced whole.
    fn write(&self, text: &str) -> std::io::Result<()> {
        if let Some(bytes) = &self.bytes {
            return bytes.write(text.as_bytes());
        }
        let dir = self
            .path
            .parent()
            .filter(|d| !d.as_os_str().is_empty())
            .map(Path::to_path_buf);
        if let Some(dir) = &dir {
            std::fs::create_dir_all(dir)?;
        }
        let file_name = self
            .path
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default();
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_nanos());
        let temporary_name = format!(".{file_name}.{}.{nanos:x}.tmp", std::process::id());
        let temporary = match &dir {
            Some(dir) => dir.join(temporary_name),
            None => PathBuf::from(temporary_name),
        };
        let result =
            std::fs::write(&temporary, text).and_then(|()| std::fs::rename(&temporary, &self.path));
        if result.is_err() {
            let _ = std::fs::remove_file(&temporary);
        }
        result
    }

    /// One section of the file: `Ok(None)` when the file or the section is
    /// missing (or the section is not an object), `Err` when the file
    /// cannot be read (the message says why).
    pub(crate) fn section(&self, name: &str) -> Result<Option<Vec<Member>>, String> {
        let Some(root) = self.read_root()? else {
            return Ok(None);
        };
        Ok(root
            .into_iter()
            .find(|m| m.name == name)
            .and_then(|m| match m.value.value {
                Value::Object(members) => Some(members),
                _ => None,
            }))
    }

    /// The warning for a section that could not be used.
    pub(crate) fn failed(&self, why: &str) -> String {
        format!(
            "settings: failed to load {}: {why}; using defaults",
            self.path.display()
        )
    }
}

/// Set member `name` of `members` to `value` (in place, else appended).
pub(crate) fn set_member(members: &mut Vec<Member>, name: &str, value: Value) {
    match members.iter_mut().find(|m| m.name == name) {
        Some(m) => m.value = Node::made(value),
        None => members.push(Member::made(name, value)),
    }
}

/// The object member `name` of `members`, made empty first when missing or
/// not an object.
pub(crate) fn object_member<'a>(members: &'a mut Vec<Member>, name: &str) -> &'a mut Vec<Member> {
    let i = match members.iter().position(|m| m.name == name) {
        Some(i) => {
            if !matches!(members[i].value.value, Value::Object(_)) {
                members[i].value = Node::made(Value::Object(Vec::new()));
            }
            i
        }
        None => {
            members.push(Member::made(name, Value::Object(Vec::new())));
            members.len() - 1
        }
    };
    match &mut members[i].value.value {
        Value::Object(inner) => inner,
        _ => unreachable!("made an object above"),
    }
}

/// Before version 4 the texture-detail rows were never seen on screen:
/// their stored values go back to the defaults.
fn reset_dead_texture_detail_rows(display: &mut Node) {
    let Value::Object(members) = &mut display.value else {
        return;
    };
    let def = DisplaySettings::default();
    for (name, value) in [
        ("landscapeTextureDetail", def.landscape_texture_detail),
        ("environmentTextureDetail", def.environment_texture_detail),
    ] {
        if let Some(m) = members.iter_mut().find(|m| m.name == name) {
            m.value = Node::made(Value::Number(value.to_string()));
        }
    }
}

fn read_display(d: &[Member], version: i32) -> Result<DisplaySettings, Unreadable> {
    let def = DisplaySettings::default();
    let mut field_of_view = read_float(d, "fieldOfView", def.field_of_view)?;
    if version < 3 && member(d, "fieldOfView").is_some() {
        field_of_view = migrate_legacy_vertical_fov_degrees(field_of_view);
    }
    let rows_were_dead = version < TEXTURE_DETAIL_ROWS_LIVE_SCHEMA_VERSION;
    let landscape_texture_detail = if rows_were_dead {
        def.landscape_texture_detail
    } else {
        read_int(d, "landscapeTextureDetail", def.landscape_texture_detail)?
    };
    let environment_texture_detail = if rows_were_dead {
        def.environment_texture_detail
    } else {
        read_int(
            d,
            "environmentTextureDetail",
            def.environment_texture_detail,
        )?
    };
    Ok(DisplaySettings {
        resolution: read_string(d, "resolution", &def.resolution),
        fullscreen: read_bool(d, "fullscreen", def.fullscreen),
        vsync: read_bool(d, "vsync", def.vsync),
        field_of_view,
        gamma: read_float(d, "gamma", def.gamma)?,
        show_fps: read_bool(d, "showFps", def.show_fps),
        quality: read_enum(d, "quality", &QUALITY_NAMES, def.quality),
        particle_range: read_enum(d, "particleRange", &PARTICLE_NAMES, def.particle_range),
        screen_brightness: read_float(d, "screenBrightness", def.screen_brightness)?,
        automatic_degrades: read_bool(d, "automaticDegrades", def.automatic_degrades),
        graphics_performance: read_float(d, "graphicsPerformance", def.graphics_performance)?,
        degrade_distance: read_float(d, "degradeDistance", def.degrade_distance)?,
        landscape_texture_detail,
        environment_texture_detail,
        texture_filtering: read_int(d, "textureFiltering", def.texture_filtering)?,
        landscape_draw_distance: read_int(d, "landscapeDrawDistance", def.landscape_draw_distance)?,
        building_detail_textures: read_bool(
            d,
            "buildingDetailTextures",
            def.building_detail_textures,
        ),
        landscape_detail_textures: read_bool(
            d,
            "landscapeDetailTextures",
            def.landscape_detail_textures,
        ),
        multi_pass_alpha: read_bool(d, "multiPassAlpha", def.multi_pass_alpha),
        keep_distant_buildings: read_bool(d, "keepDistantBuildings", def.keep_distant_buildings),
        potato_mode: read_bool(d, "potatoMode", def.potato_mode),
        ui_only: read_bool(d, "uiOnly", def.ui_only),
        ui_only_when_unfocused: read_bool(d, "uiOnlyWhenUnfocused", def.ui_only_when_unfocused),
        ui_scale_percent: ui_scale_percent(read_int(d, "uiScalePercent", def.ui_scale_percent)?),
        render_pack: member(d, "renderPack")
            .filter(|n| !is_retail_pack(n))
            .cloned(),
    })
}

/// A render-pack member that is not an object, names no pack or preset, or
/// is the retail pack with its `off` preset and no overrides, means the
/// retail pack.
fn is_retail_pack(node: &Node) -> bool {
    let Value::Object(pack) = &node.value else {
        return true;
    };
    let text = |name| match member(pack, name).map(|n| &n.value) {
        Some(Value::String(s)) => s.trim().to_owned(),
        _ => String::new(),
    };
    let (id, preset) = (text("packId"), text("presetId"));
    let no_overrides = match member(pack, "settingOverrides").map(|n| &n.value) {
        Some(Value::Object(overrides)) => overrides.is_empty(),
        _ => true,
    };
    id.is_empty()
        || preset.is_empty()
        || (id.eq_ignore_ascii_case("retail") && preset == "off" && no_overrides)
}

fn name_of<T: PartialEq>(names: &[(&'static str, T)], value: &T) -> &'static str {
    names
        .iter()
        .find(|(_, v)| v == value)
        .map_or("", |(n, _)| n)
}

/// The display section as written: keys in ordinal order, two-space indent
/// one level in.
fn display_object(d: &DisplaySettings) -> String {
    let boolean = |b: bool| if b { "true" } else { "false" }.to_owned();
    let float = |f: f32| format!("{f}");
    let string = |s: &str| {
        let mut out = String::new();
        json::write_string(&mut out, s);
        out
    };
    let render_pack = d.render_pack.as_ref().map_or_else(
        || {
            concat!(
                "{\n",
                "      \"packId\": \"retail\",\n",
                "      \"packVersion\": null,\n",
                "      \"presetId\": \"off\",\n",
                "      \"settingOverrides\": {}\n",
                "    }"
            )
            .to_owned()
        },
        |node| {
            let mut out = String::new();
            json::write_value(&mut out, node, 2);
            out
        },
    );
    let fields: [(&str, String); 25] = [
        ("automaticDegrades", boolean(d.automatic_degrades)),
        (
            "buildingDetailTextures",
            boolean(d.building_detail_textures),
        ),
        ("degradeDistance", float(d.degrade_distance)),
        (
            "environmentTextureDetail",
            d.environment_texture_detail.to_string(),
        ),
        ("fieldOfView", float(d.field_of_view)),
        ("fullscreen", boolean(d.fullscreen)),
        ("gamma", float(d.gamma)),
        ("graphicsPerformance", float(d.graphics_performance)),
        ("keepDistantBuildings", boolean(d.keep_distant_buildings)),
        (
            "landscapeDetailTextures",
            boolean(d.landscape_detail_textures),
        ),
        (
            "landscapeDrawDistance",
            d.landscape_draw_distance.to_string(),
        ),
        (
            "landscapeTextureDetail",
            d.landscape_texture_detail.to_string(),
        ),
        ("multiPassAlpha", boolean(d.multi_pass_alpha)),
        (
            "particleRange",
            string(name_of(&PARTICLE_NAMES, &d.particle_range)),
        ),
        ("potatoMode", boolean(d.potato_mode)),
        ("quality", string(name_of(&QUALITY_NAMES, &d.quality))),
        ("renderPack", render_pack),
        ("resolution", string(&d.resolution)),
        ("screenBrightness", float(d.screen_brightness)),
        ("showFps", boolean(d.show_fps)),
        ("textureFiltering", d.texture_filtering.to_string()),
        ("uiOnly", boolean(d.ui_only)),
        ("uiOnlyWhenUnfocused", boolean(d.ui_only_when_unfocused)),
        ("uiScalePercent", d.ui_scale_percent.to_string()),
        ("vsync", boolean(d.vsync)),
    ];
    object_text(&fields)
}

/// A section's object as written: the fields in the order given (each
/// section lists its keys in ordinal order), four-space indent inside,
/// closed at two.
pub(crate) fn object_text(fields: &[(&str, String)]) -> String {
    let mut out = String::from("{\n");
    for (i, (name, value)) in fields.iter().enumerate() {
        out.push_str("    ");
        json::write_string(&mut out, name);
        out.push_str(": ");
        out.push_str(value);
        out.push_str(if i + 1 < fields.len() { ",\n" } else { "\n" });
    }
    out.push_str("  }");
    out
}

/// `true` or `false` as written.
pub(crate) fn bool_text(b: bool) -> String {
    if b { "true" } else { "false" }.to_owned()
}

/// A float as written (the shortest form that reads back the same).
pub(crate) fn float_text(f: f32) -> String {
    format!("{f}")
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.UI.Abstractions.Tests/Panels/Settings/SettingsStoreTests.cs`
    //! (the display section).
    use super::*;

    struct Temp(PathBuf);

    impl Temp {
        fn new(name: &str) -> Self {
            let path = std::env::temp_dir().join(format!(
                "openac-settings-{name}-{}-{}.json",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ));
            let _ = std::fs::remove_file(&path);
            Self(path)
        }
        fn store(&self) -> SettingsStore {
            SettingsStore::new(self.0.clone())
        }
        fn write(&self, text: &str) {
            std::fs::write(&self.0, text).unwrap();
        }
    }

    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }

    #[test]
    fn a_missing_file_gives_the_defaults() {
        let t = Temp::new("missing");
        assert_eq!(t.store().load_display(), (DisplaySettings::default(), None));
    }

    /// Bytes kept in memory, as a browser's storage keeps them.
    #[derive(Default)]
    struct Kept(std::sync::Mutex<Option<Vec<u8>>>);

    impl SettingsBytes for Kept {
        fn read(&self) -> std::io::Result<Option<Vec<u8>>> {
            Ok(self.0.lock().unwrap().clone())
        }
        fn write(&self, bytes: &[u8]) -> std::io::Result<()> {
            *self.0.lock().unwrap() = Some(bytes.to_vec());
            Ok(())
        }
    }

    #[test]
    fn a_store_over_kept_bytes_reads_and_writes_what_the_file_holds() {
        let t = Temp::new("kept");
        let kept = std::sync::Arc::new(Kept::default());
        let memory = SettingsStore::with_bytes("browser settings", kept.clone());
        assert_eq!(memory.load_display(), (DisplaySettings::default(), None));
        let scaled = DisplaySettings {
            ui_scale_percent: 150,
            ..DisplaySettings::default()
        };
        memory.save_display(&scaled).unwrap();
        t.store().save_display(&scaled).unwrap();
        let file = std::fs::read(&t.0).unwrap();
        assert_eq!(
            kept.0.lock().unwrap().as_deref(),
            Some(file.as_slice()),
            "the same bytes as the file"
        );
        assert_eq!(memory.load_display().0, scaled);
        assert_eq!(
            memory.clone().load_display().0,
            scaled,
            "a clone shares them"
        );
        assert_eq!(memory.path(), Path::new("browser settings"));
    }

    #[test]
    fn save_then_load_round_trips_every_field() {
        let t = Temp::new("roundtrip");
        let original = DisplaySettings {
            resolution: "1920x1080".into(),
            fullscreen: true,
            vsync: false,
            field_of_view: 100.0,
            gamma: 1.25,
            show_fps: true,
            quality: QualityPreset::Ultra,
            particle_range: ParticleRange::Retail,
            screen_brightness: 0.5,
            automatic_degrades: true,
            graphics_performance: 0.75,
            degrade_distance: 80.0,
            landscape_texture_detail: 2,
            environment_texture_detail: 3,
            texture_filtering: 2,
            landscape_draw_distance: 5,
            building_detail_textures: false,
            landscape_detail_textures: true,
            multi_pass_alpha: true,
            keep_distant_buildings: false,
            potato_mode: true,
            ui_only: true,
            ui_only_when_unfocused: true,
            ui_scale_percent: 230,
            render_pack: None,
        };
        t.store().save_display(&original).unwrap();
        let (loaded, warning) = t.store().load_display();
        assert_eq!(warning, None);
        assert_eq!(loaded, original);
    }

    #[test]
    fn landscape_detail_textures_default_off_and_round_trip() {
        let t = Temp::new("landscapedetail");
        assert!(!DisplaySettings::default().landscape_detail_textures);
        // A file from before the option reads it as off.
        t.write(r#"{ "version": 4, "display": { "buildingDetailTextures": true } }"#);
        assert!(!t.store().load_display().0.landscape_detail_textures);
        let on = DisplaySettings {
            landscape_detail_textures: true,
            ..DisplaySettings::default()
        };
        t.store().save_display(&on).unwrap();
        let text = std::fs::read_to_string(&t.0).unwrap();
        assert!(text.contains("\"landscapeDetailTextures\": true"), "{text}");
        assert!(t.store().load_display().0.landscape_detail_textures);
    }

    #[test]
    fn the_ui_scale_defaults_to_one_hundred_percent_and_round_trips() {
        let t = Temp::new("uiscale");
        assert_eq!(t.store().load_display().0.ui_scale_percent, 100);
        let scaled = DisplaySettings {
            ui_scale_percent: 150,
            ..DisplaySettings::default()
        };
        t.store().save_display(&scaled).unwrap();
        let text = std::fs::read_to_string(&t.0).unwrap();
        assert!(text.contains("\"uiScalePercent\": 150"), "{text}");
        assert_eq!(t.store().load_display().0.ui_scale_percent, 150);
    }

    #[test]
    fn a_stored_ui_scale_off_the_steps_reads_as_the_nearest_offered_one() {
        let t = Temp::new("uiscale-range");
        for (stored, read) in [
            (10, 50),
            (49, 50),
            (154, 150),
            (155, 160),
            (1000, 300),
            (-5, 50),
        ] {
            t.write(&format!(
                r#"{{ "version": 4, "display": {{ "uiScalePercent": {stored} }} }}"#
            ));
            assert_eq!(
                t.store().load_display().0.ui_scale_percent,
                read,
                "{stored}"
            );
        }
    }

    #[test]
    fn the_ui_scale_steps_are_ten_percent_from_fifty_to_three_hundred() {
        let mut offered: Vec<i32> = (0..=400).map(ui_scale_percent).collect();
        offered.dedup();
        assert_eq!(offered, (50..=300).step_by(10).collect::<Vec<_>>());
        assert_eq!(ui_scale_percent(100), 100);
        assert_eq!(ui_scale_percent(i32::MAX), 300);
        assert_eq!(ui_scale_percent(i32::MIN), 50);
    }

    #[test]
    fn a_corrupt_file_falls_back_to_defaults_and_says_why() {
        let t = Temp::new("corrupt");
        t.write("{ not json");
        let (loaded, warning) = t.store().load_display();
        assert_eq!(loaded, DisplaySettings::default());
        assert!(warning.is_some_and(|w| w.contains("using defaults")));
    }

    #[test]
    fn missing_keys_fall_back_one_by_one() {
        let t = Temp::new("partial");
        t.write(r#"{ "version": 3, "display": { "resolution": "1366x768" } }"#);
        let (loaded, _) = t.store().load_display();
        assert_eq!(loaded.resolution, "1366x768");
        assert_eq!(loaded.resolution_size(), Some((1366, 768)));
        let def = DisplaySettings::default();
        assert_eq!(
            (loaded.fullscreen, loaded.vsync, loaded.field_of_view),
            (def.fullscreen, def.vsync, def.field_of_view)
        );
        assert!(loaded.keep_distant_buildings);
    }

    #[test]
    fn a_pre_pack_file_reads_as_the_retail_pack() {
        let t = Temp::new("prepack");
        t.write(
            r#"{ "version": 3, "display": { "resolution": "1920x1080", "fullscreen": false } }"#,
        );
        assert_eq!(t.store().load_display().0.render_pack, None);
    }

    #[test]
    fn pre_v3_field_of_view_is_migrated_once() {
        for (stored, expected) in [(60.0, 90.0), (45.0, 75.5), (120.0, 160.0)] {
            let t = Temp::new("fov");
            t.write(&format!(
                r#"{{ "version": 2, "display": {{ "fieldOfView": {stored} }} }}"#
            ));
            let fov = t.store().load_display().0.field_of_view;
            assert!((fov - expected).abs() < 0.05, "{stored}: {fov}");
        }
        let t = Temp::new("fov3");
        t.write(r#"{ "version": 3, "display": { "fieldOfView": 60 } }"#);
        assert_eq!(t.store().load_display().0.field_of_view, 60.0);
        let t = Temp::new("fovabsent");
        t.write(r#"{ "version": 2, "display": { "resolution": "1920x1080" } }"#);
        assert_eq!(t.store().load_display().0.field_of_view, 90.0);
        // Saving stamps the current version, so the migration runs once.
        let t = Temp::new("fovstamp");
        t.write(r#"{ "version": 2, "display": { "fieldOfView": 60 } }"#);
        let store = t.store();
        let migrated = store.load_display().0;
        store.save_display(&migrated).unwrap();
        assert_eq!(store.load_display().0.field_of_view, 90.0);
    }

    #[test]
    fn pre_v4_texture_detail_rows_reset_to_defaults() {
        for (version, landscape, environment, want_l, want_e) in [
            (3, 2, 4, 0, 1),
            (3, 4, 4, 0, 1),
            (2, 0, 0, 0, 1),
            (4, 2, 4, 2, 4),
        ] {
            let t = Temp::new("detail");
            t.write(&format!(
                r#"{{ "version": {version}, "display": {{ "landscapeTextureDetail": {landscape}, "environmentTextureDetail": {environment} }} }}"#
            ));
            let d = t.store().load_display().0;
            assert_eq!(
                (d.landscape_texture_detail, d.environment_texture_detail),
                (want_l, want_e),
                "version {version}"
            );
        }
    }

    #[test]
    fn an_unknown_particle_range_falls_back() {
        for stored in ["Unlimited", "999"] {
            let t = Temp::new("particles");
            t.write(&format!(
                r#"{{ "version": 2, "display": {{ "particleRange": "{stored}" }} }}"#
            ));
            assert_eq!(
                t.store().load_display().0.particle_range,
                ParticleRange::Extended
            );
        }
    }

    #[test]
    fn saving_keeps_the_other_sections() {
        let t = Temp::new("preserve");
        t.write(
            r#"{ "version": 1, "display": { "resolution": "1280x720" }, "audio": { "master": 0.5, "music": 0.7 } }"#,
        );
        t.store()
            .save_display(&DisplaySettings {
                resolution: "1920x1080".into(),
                ..DisplaySettings::default()
            })
            .unwrap();
        let raw = std::fs::read_to_string(&t.0).unwrap();
        assert!(raw.contains("\"audio\""));
        assert!(raw.contains("\"master\": 0.5"));
        assert!(raw.contains("\"music\": 0.7"));
        assert!(raw.contains("1920x1080"));
        assert!(raw.contains("\"version\": 4"));
        assert!(json::parse(raw.as_bytes()).is_ok());
    }

    #[test]
    fn a_render_pack_selection_is_kept_as_read() {
        let t = Temp::new("pack");
        t.write(r#"{ "version": 4, "display": { "renderPack": { "packId": "shiny", "packVersion": "1.2", "presetId": "max", "settingOverrides": { "bloom": "on" } } } }"#);
        let store = t.store();
        let loaded = store.load_display().0;
        store.save_display(&loaded).unwrap();
        let raw = std::fs::read_to_string(&t.0).unwrap();
        assert!(raw.contains("\"packId\": \"shiny\""));
        assert!(raw.contains("\"bloom\": \"on\""));
        let t = Temp::new("retailpack");
        t.store().save_display(&DisplaySettings::default()).unwrap();
        let raw = std::fs::read_to_string(&t.0).unwrap();
        assert!(raw.contains("    \"renderPack\": {\n      \"packId\": \"retail\",\n"));
        assert!(raw.contains("\"presetId\": \"off\""));
        assert!(raw.contains("\"settingOverrides\": {}\n    },\n    \"resolution\""));
    }
}

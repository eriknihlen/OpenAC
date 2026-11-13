//! The import table: every client function, written once. Read through
//! [`crate::imports`] (the [`crate::Import`] enum and the [`crate::calls`]
//! marker types) and by the SDK, which builds its wasm imports from it.
//!
//! Each entry: `Variant = import_name(Request) -> Reply [= inert reply],
//! permission, window, since`. `window: true` marks the few calls whose
//! answer depends on a window being attached (the import census keeps
//! that list honest). The inert reply is what a client with nothing to say
//! answers (no character, nothing found, unavailable); without one it is
//! the reply type's default.

/// Hands the import table to `$callback` (a `macro_rules!` macro in scope
/// at the call site).
#[macro_export]
macro_rules! import_table {
    ($callback:ident) => {
        $callback! {
            // ── Plumbing ────────────────────────────────────────────────
            /// Copy the last reply into the plugin's memory: `(ptr, len)`;
            /// returns 0.
            AbiReplyRead = abi_reply_read(()) -> (),
                permission: None, window: false, since: (1, 0);
            /// The plugin is about to trap on a panic: the message (UTF-8)
            /// for the failure report. Returns 0.
            AbiPanic = abi_panic(()) -> (),
                permission: None, window: false, since: (1, 0);

            // ── A. Host services ────────────────────────────────────────
            /// Write a line to the session's log.
            HostLog = host_log($crate::types::LogRequest) -> (),
                permission: None, window: false, since: (1, 0);
            /// Whether a window is attached to the session.
            HostWindowAttached = host_window_attached(()) -> bool,
                permission: None, window: true, since: (1, 0);
            /// One of the plugin's per-session settings (the key), or none.
            HostSetting = host_setting(String) -> Option<String>,
                permission: None, window: false, since: (1, 0);
            // ── O. Plugin UI (`crate::ui`, served by `crate::ui_host`):
            // session state, answered the same with or without a window
            // except where marked.
            /// Register a window from markup.
            UiPanelRegister = ui_panel_register($crate::ui::PanelRegistration)
                -> Result<u32, String> = Err("no plugin UI".to_owned()),
                permission: None, window: false, since: (1, 0);
            /// Remove one of the plugin's windows.
            UiPanelRemove = ui_panel_remove(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Push bound values.
            UiPanelSet = ui_panel_set($crate::ui::PanelValues) -> Result<(), String> = Ok(()),
                permission: None, window: false, since: (1, 0);
            /// Show or hide one of the plugin's windows.
            UiPanelShow = ui_panel_show($crate::ui::ViewShow) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Whether the plugin has a window by that id or title.
            UiViewExists = ui_view_exists(String) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Whether one of the plugin's windows is shown.
            UiViewVisible = ui_view_visible(String) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Whether a window holds a named control.
            UiControlExists = ui_control_exists($crate::ui::ControlRef) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Replace a control's text.
            UiControlLabel = ui_control_label($crate::ui::ControlLabel) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Show or hide a control.
            UiControlVisible = ui_control_visible($crate::ui::ControlVisible) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Register a window from markup whose bindings may use every
            /// kind (the newer controls' and `<repeat>`'s).
            UiPanelRegisterTyped = ui_panel_register_typed($crate::ui::TypedPanelRegistration)
                -> Result<u32, String> = Err("no plugin UI".to_owned()),
                permission: None, window: false, since: (1, 0);
            /// Push typed bound values.
            UiPanelSetTyped = ui_panel_set_typed($crate::ui::TypedPanelValues)
                -> Result<(), String> = Ok(()),
                permission: None, window: false, since: (1, 0);
            /// Ask the player a question in a dialog box: its handle, none
            /// when no window draws it, or why the request is refused.
            UiDialogConfirm = ui_dialog_confirm($crate::ui::DialogRequest)
                -> Result<Option<u32>, String> = Ok(None),
                permission: None, window: true, since: (1, 0);
            /// Withdraw one of the plugin's dialogs (answered with its last
            /// button); false once it is answered.
            UiDialogWithdraw = ui_dialog_withdraw(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Show one of the plugin's windows modal over another (false
            /// without a window).
            UiPanelShowModal = ui_panel_show_modal($crate::ui::ModalShow) -> bool,
                permission: None, window: true, since: (1, 0);
            /// Toggle, show, hide or ask about one of the client's windows
            /// (false without a window).
            UiClientWindow = ui_client_window($crate::ui::ClientWindowRequest) -> bool,
                permission: None, window: true, since: (1, 0);
            /// Register a canvas.
            UiCanvasRegister = ui_canvas_register($crate::ui::CanvasRegistration)
                -> Result<u32, String> = Err("no plugin UI".to_owned()),
                permission: None, window: false, since: (1, 0);
            /// Replace what a canvas shows.
            UiCanvasPaint = ui_canvas_paint($crate::ui::CanvasPaint) -> Result<(), String> = Ok(()),
                permission: None, window: false, since: (1, 0);
            /// Show, hide, move a canvas, listen for or release its pointer.
            UiCanvasSet = ui_canvas_set($crate::ui::CanvasSet) -> bool,
                permission: None, window: false, since: (1, 0);
            /// A canvas as it stands.
            UiCanvasState = ui_canvas_state(u32) -> Option<$crate::ui::CanvasState>,
                permission: None, window: false, since: (1, 0);
            /// Whether a window draws the canvas now.
            UiCanvasAvailable = ui_canvas_available(u32) -> bool,
                permission: None, window: true, since: (1, 0);
            /// Remove a canvas.
            UiCanvasRemove = ui_canvas_remove(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Resize a canvas on the screen: false for a size below 1, no
            /// such canvas, or an in-markup canvas (its element sizes it).
            UiCanvasResize = ui_canvas_resize($crate::ui::CanvasResize) -> bool,
                permission: None, window: false, since: (1, 12);
            /// Measure text in the interface font.
            UiMeasureText = ui_measure_text(String) -> $crate::ui::TextSize,
                permission: None, window: false, since: (1, 0);
            /// The client's bundled font in a weight at a size; handle 0 when
            /// refused. Asking twice holds it twice.
            UiFontBundled = ui_font_bundled($crate::ui::BundledFontRequest) -> $crate::ui::FontInfo,
                permission: None, window: false, since: (1, 4);
            /// A TrueType or OpenType font the plugin ships, read by the
            /// client outside the plugin's sandbox; handle 0 when refused. A
            /// second request under the same name, size and characters is the
            /// same font held once more.
            UiFontLoad = ui_font_load($crate::ui::FontLoadRequest) -> $crate::ui::FontInfo,
                permission: None, window: false, since: (1, 4);
            /// Measure text in one of the plugin's fonts: nothing for a font
            /// it does not hold.
            UiFontMeasure = ui_font_measure($crate::ui::FontMeasure) -> $crate::ui::TextSize,
                permission: None, window: false, since: (1, 4);
            /// Let go of one hold on a font.
            UiFontRelease = ui_font_release(u32) -> bool,
                permission: None, window: false, since: (1, 4);
            /// How many distinct fonts the plugin holds.
            UiFontCount = ui_font_count(()) -> u32,
                permission: None, window: false, since: (1, 4);
            /// The plugin theme the player picked under Appearance, with
            /// its colours, so a canvas can draw like the themed windows;
            /// Classic until a window tells the session (and on a session
            /// that never had one). `Event::ThemeChanged` carries each
            /// change.
            UiTheme = ui_theme(()) -> $crate::ui::ThemeInfo,
                permission: None, window: false, since: (1, 0);
            /// The client's art for a render surface.
            UiImageClientArt = ui_image_client_art(u32) -> $crate::ui::ImageInfo,
                permission: None, window: false, since: (1, 0);
            /// A spell's icon.
            UiImageSpellIcon = ui_image_spell_icon(u32) -> $crate::ui::ImageInfo,
                permission: None, window: false, since: (1, 0);
            /// A live object's composed icon.
            UiImageObjectIcon = ui_image_object_icon(u32) -> $crate::ui::ImageInfo,
                permission: None, window: false, since: (1, 0);
            /// The plugin's own pixels.
            UiImageRgba = ui_image_rgba($crate::ui::RgbaImage) -> $crate::ui::ImageInfo,
                permission: None, window: false, since: (1, 0);
            /// The plugin's own art as a PNG, JPEG, BMP, TGA, GIF or WebP file,
            /// decoded by the client (OpenAC `FromStream`); a second request
            /// under the same name is the same image held once more.
            UiImageDecode = ui_image_decode($crate::ui::EncodedImage) -> $crate::ui::ImageInfo,
                permission: None, window: false, since: (1, 0);
            /// Let go of one hold on an image.
            UiImageRelease = ui_image_release(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// How many distinct images the plugin holds.
            UiImageCount = ui_image_count(()) -> u32,
                permission: None, window: false, since: (1, 0);
            /// Replace the plugin's world labels.
            UiLabelsShow = ui_labels_show(Vec<$crate::ui::WorldLabel>) -> bool,
                permission: None, window: false, since: (1, 0);
            /// A new world-line layer.
            UiLinesCreate = ui_lines_create(()) -> u32,
                permission: None, window: false, since: (1, 0);
            /// Replace a layer's lines.
            UiLinesSet = ui_lines_set($crate::ui::LineSet) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Remove a line layer.
            UiLinesRemove = ui_lines_remove(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// A new world-triangle layer.
            UiTrianglesCreate = ui_triangles_create(()) -> u32,
                permission: None, window: false, since: (1, 3);
            /// Replace a layer's triangles.
            UiTrianglesSet = ui_triangles_set($crate::ui::TriangleSet) -> bool,
                permission: None, window: false, since: (1, 3);
            /// Remove a triangle layer.
            UiTrianglesRemove = ui_triangles_remove(u32) -> bool,
                permission: None, window: false, since: (1, 3);
            /// A new world-marker layer.
            UiMarkersCreate = ui_markers_create(()) -> u32,
                permission: None, window: false, since: (1, 3);
            /// Replace a marker layer's icons.
            UiMarkersSetIcons = ui_markers_set_icons($crate::ui::IconSet) -> bool,
                permission: None, window: false, since: (1, 3);
            /// Replace a marker layer's ground shapes.
            UiMarkersSetShapes = ui_markers_set_shapes($crate::ui::ShapeSet) -> bool,
                permission: None, window: false, since: (1, 3);
            /// Remove a marker layer and its markers.
            UiMarkersRemove = ui_markers_remove(u32) -> bool,
                permission: None, window: false, since: (1, 3);
            /// Projectile markers to draw for a moment.
            UiProjectileDebug = ui_projectile_debug(Vec<$crate::ui::ProjectileSample>) -> (),
                permission: None, window: false, since: (1, 0);
            /// Register a hotkey (a second registration under the id
            /// replaces the first): how it stands. It binds only while a
            /// window with a key map is attached; the press arrives as a UI
            /// event.
            UiHotkeyRegister = ui_hotkey_register($crate::ui::HotkeyRegistration)
                -> Result<$crate::ui::HotkeyState, String> = Err("no plugin UI".to_owned()),
                permission: None, window: true, since: (1, 0);
            /// Revoke one of the plugin's hotkeys.
            UiHotkeyUnregister = ui_hotkey_unregister(String) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Rebind one of the plugin's hotkeys, stored as the player's
            /// override (nothing changes with no window); none for an
            /// unknown id.
            UiHotkeyRebind = ui_hotkey_rebind($crate::ui::HotkeyRebind)
                -> Option<$crate::ui::HotkeyState>,
                permission: None, window: true, since: (1, 0);
            /// How one of the plugin's hotkeys stands now (unbound while no
            /// window with a key map is attached).
            UiHotkeyState = ui_hotkey_state(String) -> Option<$crate::ui::HotkeyState>,
                permission: None, window: true, since: (1, 0);
            /// Hear (true) or stop hearing (false) the client's input
            /// actions as its key map fires them: each arrives as a UI
            /// event with the action's and the activation's names (a held
            /// action's per-tick repeats are not sent). Answers the setting
            /// before. Actions fire only from an attached window's keys,
            /// buttons and wheel.
            UiInputActionsListen = ui_input_actions_listen(bool) -> bool,
                permission: None, window: false, since: (1, 0);
            /// All of the plugin's per-session settings, by key.
            HostSettings = host_settings(()) -> Vec<(String, String)>,
                permission: None, window: false, since: (1, 0);
            /// The clock: wall time (UTC milliseconds since the Unix
            /// epoch), the local offset from UTC, a monotonic clock and a
            /// random seed.
            HostClock = host_clock(()) -> $crate::host::Clock,
                permission: None, window: false, since: (1, 0);
            /// Log the character off and end this session, gracefully.
            /// False when there is no session to end.
            HostRequestClose = host_request_close(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// The client process's own numbers: its working set (bytes),
            /// its open handles, and its CPU use since this plugin's
            /// previous call, percent of all cores (0 on its first call).
            /// Zeros where the platform does not give them cheaply.
            HostProcessStats = host_process_stats(()) -> $crate::host::ProcessStats,
                permission: None, window: false, since: (1, 0);

            // ── L. Commands ─────────────────────────────────────────────
            /// Register a chat verb.
            CommandsRegister = commands_register($crate::types::CommandDef)
                -> Result<(), String> = Ok(()),
                permission: None, window: false, since: (1, 0);
            /// Remove one of the plugin's verbs (whether it was registered).
            CommandsUnregister = commands_unregister(String) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Every plugin's verbs as registered (verb, aliases,
            /// description), in registration order.
            CommandsDefinitions = commands_definitions(()) -> Vec<$crate::types::CommandDef>,
                permission: None, window: false, since: (1, 0);
            /// The help list: `/verb - description` for every plugin verb,
            /// ordered by verb ignoring case, one per line.
            CommandsHelp = commands_help(()) -> String,
                permission: None, window: false, since: (1, 0);
            /// Completions for a partly typed command, best first, from the
            /// plugin owning its verb (asked through its callback); none
            /// for a verb no other plugin holds.
            CommandsComplete = commands_complete($crate::types::CommandCall)
                -> Vec<$crate::types::CommandCompletion>,
                permission: None, window: false, since: (1, 0);

            // ── K. Chat ─────────────────────────────────────────────────
            /// Submit a line as if typed into the chat box (the whole
            /// pipeline: client commands, plugin verbs, the server). Runs
            /// after the current plugin call returns.
            ChatSubmit = chat_submit(String) -> (),
                permission: Some($crate::Permission::Chat), window: false, since: (1, 0);
            /// Show a line in the chat transcript, locally.
            ChatPost = chat_post($crate::types::ChatPost) -> (),
                permission: Some($crate::Permission::Chat), window: false, since: (1, 0);
            /// The retained lines (the latest 512) whose sequence is above
            /// this one, oldest first.
            ChatCaptured = chat_captured(u64) -> Vec<$crate::types::ChatLine>,
                permission: Some($crate::Permission::Chat), window: false, since: (1, 0);
            /// Put text in the chat entry without sending it; false while
            /// the player is typing there.
            ChatCompose = chat_compose(String) -> bool,
                permission: Some($crate::Permission::Chat), window: false, since: (1, 0);
            /// The player is typing into the chat entry (held keys go to
            /// text, not the character).
            ChatIsInputActive = chat_is_input_active(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Install an input interceptor (asked through the callback
            /// export about each line sent): its handle, or why not.
            ChatAddInterceptor = chat_add_interceptor(()) -> Result<u32, String> = Ok(0),
                permission: Some($crate::Permission::Chat), window: false, since: (1, 0);
            /// Remove one of the plugin's interceptors.
            ChatRemoveInterceptor = chat_remove_interceptor(u32) -> bool,
                permission: Some($crate::Permission::Chat), window: false, since: (1, 0);
            /// Install a chat filter (asked through the callback export
            /// about each line before the transcript keeps it, and each
            /// status notice): its handle.
            ChatAddFilter = chat_add_filter(()) -> u32,
                permission: Some($crate::Permission::Chat), window: false, since: (1, 0);
            /// Remove one of the plugin's filters.
            ChatRemoveFilter = chat_remove_filter(u32) -> bool,
                permission: Some($crate::Permission::Chat), window: false, since: (1, 0);

            // ── D. Character ────────────────────────────────────────────
            /// The character in the world, or none.
            CharacterInfo = character_info(()) -> Option<$crate::character::CharacterInfo>,
                permission: None, window: false, since: (1, 0);
            /// The character's experience and luminance; none before its
            /// description arrives and from the moment it leaves the world.
            CharacterExperience = character_experience(())
                -> Option<$crate::character::CharacterExperience>,
                permission: None, window: false, since: (1, 0);
            /// Every skill the server told, by name.
            CharacterSkills = character_skills(()) -> Vec<$crate::character::Skill>,
                permission: None, window: false, since: (1, 0);
            /// One skill by id.
            CharacterSkill = character_skill(u32) -> Option<$crate::character::Skill>,
                permission: None, window: false, since: (1, 0);
            /// The six attributes.
            CharacterAttributes = character_attributes(()) -> Vec<$crate::character::Attribute>,
                permission: None, window: false, since: (1, 0);
            /// Health, stamina and mana.
            CharacterVitals = character_vitals(()) -> Vec<$crate::character::Vital>,
                permission: None, window: false, since: (1, 0);
            /// The enchantments in effect (`false`), or every timed one in
            /// the multiplicative and additive lists (`true`).
            CharacterEnchantments = character_enchantments(bool)
                -> Vec<$crate::character::ActiveEnchantment>,
                permission: None, window: false, since: (1, 0);
            /// Every enchantment on the character, in every list (the
            /// multiplicative and additive ones, vitae and cooldowns),
            /// including one a stronger enchantment of its family surpasses
            /// (which `character_enchantments(false)` leaves out). One with no
            /// spell-table entry (such as a cooldown) reads family 0 and tier 0.
            /// In order of layer, then spell id.
            CharacterAllEnchantments = character_all_enchantments(())
                -> Vec<$crate::character::ActiveEnchantment>,
                permission: None, window: false, since: (1, 10);
            /// Spend experience or credits on one stat.
            CharacterAdvance = character_advance($crate::character::AdvancementRequest)
                -> $crate::character::AdvancementResult,
                permission: None, window: false, since: (1, 0);
            /// Every character option by name, with its value.
            CharacterOptions = character_options(()) -> Vec<(String, bool)>,
                permission: None, window: false, since: (1, 0);
            /// Set one character option by name, as the options panel does.
            CharacterSetOption = character_set_option((String, bool))
                -> $crate::character::OptionResult,
                permission: None, window: false, since: (1, 0);

            // ── C. World reads ──────────────────────────────────────────
            /// Every object the client knows, by id.
            WorldObjects = world_objects(()) -> Vec<$crate::world::WorldObject>,
                permission: None, window: false, since: (1, 0);
            /// One object.
            WorldObject = world_object(u32) -> Option<$crate::world::WorldObject>,
                permission: None, window: false, since: (1, 0);
            /// Every property the client holds for an object.
            WorldProperties = world_properties(u32) -> Option<$crate::world::ItemProperties>,
                permission: None, window: false, since: (1, 0);
            /// One integer property of an object: (object, property).
            WorldIntProperty = world_int_property((u32, u32)) -> Option<i32>,
                permission: None, window: false, since: (1, 0);
            /// Ask for an object's appraisal without opening the assess
            /// window (the player's own question wins the slot).
            WorldIdentify = world_identify(u32) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Use a world object (a door, lever, portal, NPC): the
            /// character walks there first when it must.
            WorldActivate = world_activate(u32) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// The last activation a plugin started that ended.
            WorldLastActivation = world_last_activation(())
                -> $crate::world::ActivationCompletion,
                permission: None, window: false, since: (1, 0);
            /// The open outside container, 0 for none.
            WorldOpenContainer = world_open_container(()) -> u32,
                permission: None, window: false, since: (1, 0);
            /// Every object placed in the world with where it stands.
            WorldEntities = world_entities(()) -> Vec<$crate::world::EntitySnapshot>,
                permission: None, window: false, since: (1, 0);
            /// The fixed decoration of the landscape around the character
            /// (buildings, dat objects, trees and rocks; no server identity,
            /// nothing a command can name), with where each stands.
            WorldScenery = world_scenery(()) -> Vec<$crate::world::EntitySnapshot>,
                permission: None, window: false, since: (1, 0);
            /// The tracked contracts.
            WorldContracts = world_contracts(()) -> Vec<$crate::world::Contract>,
                permission: None, window: false, since: (1, 0);
            /// The game's calendar and time of day.
            WorldTime = world_time(()) -> $crate::world::WorldTime,
                permission: None, window: false, since: (1, 0);
            /// The landblock the character stands in, 0 out of the world.
            DungeonLandblock = dungeon_landblock(()) -> u32,
                permission: None, window: false, since: (1, 0);
            /// Whether a cell belongs to a sealed dungeon.
            DungeonIsSealed = dungeon_is_sealed(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// A landblock's dungeon floorplan, none when it has no indoor
            /// cells.
            DungeonFloorplan = dungeon_floorplan(u32) -> Option<$crate::world::Floorplan>,
                permission: None, window: false, since: (1, 0);
            /// Every indoor cell a landblock lists, with its environment
            /// piece and placement as stored (a cell without geometry
            /// too); empty for a landblock with none.
            DungeonIndoorCells = dungeon_indoor_cells(u32) -> Vec<$crate::world::IndoorCell>,
                permission: None, window: false, since: (1, 0);
            /// The outdoor terrain of each landblock asked for (`x << 8 |
            /// y`), in the order asked, from the game files (API 1.5): its
            /// 9 by 9 vertices' terrain types, roads and heights. A
            /// landblock with no terrain in the files is left out; only the
            /// first `world::LANDSCAPE_TERRAIN_MAX` asked are answered.
            LandscapeTerrain = landscape_terrain(Vec<u16>) -> Vec<$crate::world::LandblockTerrain>,
                permission: None, window: false, since: (1, 5);
            /// Each terrain type the region defines, with the mean colour
            /// of its landscape texture, the colour the game's data assigns
            /// it for maps and its name, then the road's entry
            /// (`world::ROAD_TERRAIN_TYPE`) (API 1.5); empty without the
            /// game files.
            LandscapeTerrainPalette = landscape_terrain_palette(())
                -> Vec<$crate::world::TerrainColor>,
                permission: None, window: false, since: (1, 5);
            /// Whether projectile paths can be asked now (in the world).
            ProjectilesAvailable = projectiles_available(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Fly a probe sphere from the character to a target through
            /// the session's collision world and say what it met.
            ProjectilesEvaluatePath = projectiles_evaluate_path(
                $crate::projectile::ProjectilePathQuery) -> $crate::projectile::ProjectilePathResult,
                permission: None, window: false, since: (1, 0);

            // ── E. Spells and magic ─────────────────────────────────────
            /// A page of the spell table, by id: (first index, how many; at
            /// most `magic::SPELL_PAGE_MAX`). The SDK reads the whole table
            /// page by page.
            SpellsPage = spells_page((u32, u32)) -> $crate::magic::SpellPage,
                permission: None, window: false, since: (1, 0);
            /// One spell by id.
            SpellsGet = spells_get(u32) -> Option<$crate::magic::SpellInfo>,
                permission: None, window: false, since: (1, 0);
            /// A spell by name: exact (ignoring case), then, when the flag
            /// is set, the first whose name contains it.
            SpellsFind = spells_find((String, bool)) -> Option<$crate::magic::SpellInfo>,
                permission: None, window: false, since: (1, 0);
            /// A list of the character's known spells.
            SpellsKnown = spells_known($crate::magic::SpellList)
                -> Vec<$crate::magic::SpellInfo>,
                permission: None, window: false, since: (1, 0);
            /// Whether the character knows a spell.
            SpellsIsKnown = spells_is_known(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// The known-spell lists' revision: the same number means the
            /// same three lists, so a plugin can keep what it read; it
            /// advances when a spell is learned or forgotten or the
            /// spellbook is replaced, and never goes back in a session.
            /// 0 until the character's spellbook is first read.
            SpellsKnownRevision = spells_known_revision(()) -> u64,
                permission: None, window: false, since: (1, 0);
            /// A list of the character's known spells as ids only, in the
            /// list's order (the cheap form of `spells_known`).
            SpellsKnownIds = spells_known_ids($crate::magic::SpellList) -> Vec<u32>,
                permission: None, window: false, since: (1, 0);
            /// One spell component by component id.
            SpellsComponent = spells_component(u32) -> Option<$crate::magic::ComponentInfo>,
                permission: None, window: false, since: (1, 0);
            /// Seconds left on a cooldown group, 0 when not running.
            SpellsCooldown = spells_cooldown(u32) -> f64,
                permission: None, window: false, since: (1, 0);
            /// Whether the client holds up another cast (a cast still
            /// waiting for its answer does not).
            MagicIsCasting = magic_is_casting(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Whether a spell could be cast now (a named target is
            /// selected first).
            MagicGate = magic_gate($crate::magic::CastRequest) -> $crate::magic::CastGate,
                permission: None, window: false, since: (1, 0);
            /// Cast a spell (a named target is selected first).
            MagicCast = magic_cast($crate::magic::CastRequest) -> $crate::magic::CastResult
                = $crate::magic::CastResult::Unavailable,
                permission: None, window: false, since: (1, 0);
            /// Whether a spell could be cast now at a named object, without
            /// selecting it (the selection is neither read nor changed).
            MagicGateWithoutSelecting = magic_gate_without_selecting($crate::magic::CastRequest)
                -> $crate::magic::CastGate = $crate::magic::CastGate::Refused,
                permission: None, window: false, since: (1, 0);
            /// Cast a spell at a named object without selecting it (the
            /// selection is neither read nor changed).
            MagicCastWithoutSelecting = magic_cast_without_selecting($crate::magic::CastRequest)
                -> $crate::magic::CastResult = $crate::magic::CastResult::Unavailable,
                permission: None, window: false, since: (1, 0);
            /// Whether the character carries a spell's components.
            MagicHasComponents = magic_has_components(u32) -> bool = true,
                permission: None, window: false, since: (1, 0);
            /// The server's answer to the last cast.
            MagicLastCompletion = magic_last_completion(()) -> $crate::magic::CastCompletion,
                permission: None, window: false, since: (1, 0);
            /// Tell the enchantment tracker a spell landed on a target.
            EnchantmentsReport = enchantments_report($crate::magic::ReportedCast) -> bool,
                permission: None, window: false, since: (1, 0);
            /// What the tracker believes is on a target.
            EnchantmentsCapture = enchantments_capture(u32)
                -> Vec<$crate::magic::TrackedEnchantment>,
                permission: None, window: false, since: (1, 0);
            /// Forget what the tracker holds for a target (0: everything).
            EnchantmentsForget = enchantments_forget(u32) -> (),
                permission: None, window: false, since: (1, 0);

            // ── F. Combat ───────────────────────────────────────────────
            /// The combat state now.
            CombatSnapshot = combat_snapshot(()) -> $crate::combat::CombatSnapshot,
                permission: None, window: false, since: (1, 0);
            /// The creatures worth attacking within so many metres.
            CombatHostileTargets = combat_hostile_targets(f32)
                -> Vec<$crate::combat::CombatTarget>,
                permission: None, window: false, since: (1, 0);
            /// Enter a combat mode.
            CombatEnterMode = combat_enter_mode($crate::combat::CombatMode)
                -> $crate::combat::CombatResult,
                permission: None, window: false, since: (1, 0);
            /// Leave peace for the wielded weapon's mode (the combat key).
            CombatEnterDefaultMode = combat_enter_default_mode(())
                -> $crate::combat::CombatResult,
                permission: None, window: false, since: (1, 0);
            /// Start a swing at a creature (a swing at another one ends).
            CombatBeginAttack = combat_begin_attack($crate::combat::AttackRequest)
                -> $crate::combat::CombatResult,
                permission: None, window: false, since: (1, 0);
            /// Release the swing.
            CombatReleaseAttack = combat_release_attack(()) -> $crate::combat::CombatResult,
                permission: None, window: false, since: (1, 0);
            /// Stop attacking.
            CombatAbortAttack = combat_abort_attack(()) -> $crate::combat::CombatResult,
                permission: None, window: false, since: (1, 0);
            /// Let go of a creature the server stopped talking about.
            CombatDismissGhost = combat_dismiss_ghost(u32) -> $crate::combat::CombatResult,
                permission: None, window: false, since: (1, 0);
            /// Take combat over from the client's own automatic targeting
            /// and repeat (`true`), or hand it back; held until handed back
            /// or the plugin unloads. Whether the plugin holds it now.
            CombatControl = combat_control(bool) -> bool,
                permission: None, window: false, since: (1, 0);

            // ── G. Items, equipment, loot ───────────────────────────────
            /// Everything the character owns, by name then id: worn, wielded,
            /// in the main pack and inside every pack. An item the server has
            /// listed in a pack but not described yet is included, in the
            /// pack whose list holds it, with no name, class `Unknown` and no
            /// header until its description arrives; until then it cannot be
            /// sold, moved or used (those calls refuse it or send nothing).
            ItemsOwned = items_owned(()) -> Vec<$crate::items::InventoryItem>,
                permission: None, window: false, since: (1, 0);
            /// Where the item commands stand (busy, completions).
            ItemsState = items_state(()) -> $crate::items::ItemState,
                permission: None, window: false, since: (1, 0);
            /// The server has listed the character's inventory and every
            /// nested pack, and no inventory move or request is pending: only
            /// then does an item missing from the owned list mean it is no
            /// longer owned. Listed, not described: an item may still lack
            /// its description. False outside the world.
            ItemsInventoryComplete = items_inventory_complete(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// The properties of an item the character owns.
            ItemsProperties = items_properties(u32) -> Option<$crate::world::ItemProperties>,
                permission: None, window: false, since: (1, 0);
            /// Use an owned item (or walk to and use a world object).
            ItemsUse = items_use(u32) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Use an owned item on a target: (item, target).
            ItemsApply = items_apply((u32, u32)) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Put an owned item into an owned container.
            ItemsMove = items_move($crate::items::MoveRequest) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// The same move, joining a stack when the flag is set: the
            /// container's own items first, then each pack inside it, are
            /// searched for a stack of the same thing with room for all
            /// that moves, and the first is joined with a merge (no slot
            /// needed). With no such stack, or the flag clear, it is
            /// `items_move`.
            ItemsMoveJoinStack = items_move_join_stack(($crate::items::MoveRequest, bool))
                -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Merge two owned stacks.
            ItemsMerge = items_merge($crate::items::MergeRequest) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Drop an owned item.
            ItemsDrop = items_drop($crate::items::ItemAmount) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Give an owned item to someone.
            ItemsGive = items_give($crate::items::ItemAmount) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Salvage owned items with a tinkering tool.
            ItemsSalvage = items_salvage($crate::items::SalvageRequest)
                -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Sell an owned item to the open vendor.
            ItemsSell = items_sell($crate::items::ItemAmount) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Let go of an item the client lists but the server no longer
            /// has, as a server delete would take it (nothing is sent):
            /// only a carried, unworn item (an empty pack) the server
            /// refused to appraise within the last 30 seconds, and nothing
            /// else since. Completed once it is gone; busy while an
            /// appraisal of it is awaited or an item request is in flight.
            ItemsForgetStale = items_forget_stale(u32) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// An equip the client started is still in flight (an item
            /// being moved aside for it, or the wield itself), and nothing
            /// else: a pending item request does not count.
            EquipmentBusy = equipment_busy(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Everything the character owns that can be worn or wielded:
            /// equipped first, then by name, then by id.
            EquipmentOwned = equipment_owned(()) -> Vec<$crate::items::EquipmentItem>,
                permission: None, window: false, since: (1, 0);
            /// Every known item's worn location, in the table's order.
            EquipmentPlacements = equipment_placements(())
                -> Vec<$crate::items::EquipmentPlacement>,
                permission: None, window: false, since: (1, 0);
            /// Wear or wield an owned item (slot bits, 0 to choose).
            EquipmentEquip = equipment_equip($crate::items::EquipRequest)
                -> $crate::items::EquipmentResult,
                permission: None, window: false, since: (1, 0);
            /// Wield an item in the off hand.
            EquipmentEquipSecondary = equipment_equip_secondary(u32)
                -> $crate::items::EquipmentResult,
                permission: None, window: false, since: (1, 0);
            /// Where looting stands.
            LootState = loot_state(()) -> $crate::items::LootState,
                permission: None, window: false, since: (1, 0);
            /// The corpses within so many metres, nearest first.
            LootCorpses = loot_corpses(f32) -> Vec<$crate::items::LootContainer>,
                permission: None, window: false, since: (1, 0);
            /// What the open container holds (packs inside included).
            LootContents = loot_contents(()) -> Vec<$crate::items::InventoryItem>,
                permission: None, window: false, since: (1, 0);
            /// The properties of an item in the open container.
            LootProperties = loot_properties(u32) -> Option<$crate::world::ItemProperties>,
                permission: None, window: false, since: (1, 0);
            /// Open a corpse or chest (walking there first when needed).
            LootOpen = loot_open(u32) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Close the open container.
            LootClose = loot_close(u32) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Ask for the appraisal of a corpse, an item in the open
            /// container, or an owned item.
            LootIdentify = loot_identify(u32) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Pick an item out of the open container.
            LootPickup = loot_pickup($crate::items::PickupRequest) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 0);
            /// Put an item into the open container at a placement (API
            /// 1.5), with its window's checks: the whole stack (amount 0)
            /// or that many split off. The item is the character's own, or
            /// one of the open container's own loose items (a reorder; the
            /// placement counts its loose items, the moved one taken out
            /// first); the container is the open one. Completed, with
            /// nothing sent, when the item is already there.
            LootPut = loot_put($crate::items::MoveRequest) -> $crate::items::ItemResult,
                permission: None, window: false, since: (1, 5);
            /// Register a loot classifier other plugins can ask (scoped as
            /// `<plugin id>/<id>`); the plugin's handle for it.
            LootClassifierRegister = loot_classifier_register(
                $crate::items::ClassifierRegistration) -> Result<u32, String>
                = Err("loot classifiers are unavailable".to_owned()),
                permission: None, window: false, since: (1, 0);
            /// Remove one of the plugin's classifiers.
            LootClassifierUnregister = loot_classifier_unregister(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Every registered classifier, by name then id.
            LootClassifiers = loot_classifiers(()) -> Vec<$crate::items::ClassifierInfo>,
                permission: None, window: false, since: (1, 0);
            /// Ask a classifier (with a stored profile when one is named).
            LootClassify = loot_classify($crate::items::ClassifyRequest)
                -> Option<$crate::items::LootClassification>,
                permission: None, window: false, since: (1, 0);
            /// Ask a classifier whether it needs the item appraised first.
            LootNeedsIdentification = loot_needs_identification(
                $crate::items::ClassifyRequest) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Tell a classifier an item was looted; whether it heard.
            LootNotifyLooted = loot_notify_looted($crate::items::LootedNotice) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Tell a classifier an item went away; whether it heard.
            LootNotifyRemoved = loot_notify_removed($crate::items::RemovedNotice) -> bool,
                permission: None, window: false, since: (1, 0);

            // ── H. Trade and vendor ─────────────────────────────────────
            /// The secure trade window.
            TradeState = trade_state(()) -> $crate::trade::TradeState,
                permission: None, window: false, since: (1, 0);
            /// Offer an item in the open trade.
            TradeAdd = trade_add(u32) -> $crate::trade::TradeResult,
                permission: None, window: false, since: (1, 0);
            /// Accept the trade as it stands.
            TradeAccept = trade_accept(()) -> $crate::trade::TradeResult,
                permission: None, window: false, since: (1, 0);
            /// Withdraw the acceptance.
            TradeDecline = trade_decline(()) -> $crate::trade::TradeResult,
                permission: None, window: false, since: (1, 0);
            /// Clear both sides.
            TradeReset = trade_reset(()) -> $crate::trade::TradeResult,
                permission: None, window: false, since: (1, 0);
            /// End the trade.
            TradeEnd = trade_end(()) -> $crate::trade::TradeResult,
                permission: None, window: false, since: (1, 0);
            /// The open vendor and the plugin's buy and sell lists.
            VendorState = vendor_state(()) -> $crate::trade::VendorState,
                permission: None, window: false, since: (1, 0);
            /// The properties of an item the vendor lists.
            VendorProperties = vendor_properties(u32) -> Option<$crate::world::ItemProperties>,
                permission: None, window: false, since: (1, 0);
            /// Add (listing, count) to the buy list.
            VendorAddBuy = vendor_add_buy((u32, i32)) -> $crate::trade::VendorResult,
                permission: None, window: false, since: (1, 0);
            /// Add an owned item to the sell list; one listed but not yet
            /// described is refused as an invalid item.
            VendorAddSell = vendor_add_sell(u32) -> $crate::trade::VendorResult,
                permission: None, window: false, since: (1, 0);
            /// Take a listing off the buy list.
            VendorRemoveBuy = vendor_remove_buy(u32) -> $crate::trade::VendorResult,
                permission: None, window: false, since: (1, 0);
            /// Take an item off the sell list.
            VendorRemoveSell = vendor_remove_sell(u32) -> $crate::trade::VendorResult,
                permission: None, window: false, since: (1, 0);
            /// Empty the buy list.
            VendorClearBuy = vendor_clear_buy(()) -> $crate::trade::VendorResult,
                permission: None, window: false, since: (1, 0);
            /// Empty the sell list.
            VendorClearSell = vendor_clear_sell(()) -> $crate::trade::VendorResult,
                permission: None, window: false, since: (1, 0);
            /// Buy everything on the buy list.
            VendorBuyAll = vendor_buy_all(()) -> $crate::trade::VendorResult,
                permission: None, window: false, since: (1, 0);
            /// Sell everything on the sell list.
            VendorSellAll = vendor_sell_all(()) -> $crate::trade::VendorResult,
                permission: None, window: false, since: (1, 0);

            // ── I. Navigation ───────────────────────────────────────────
            /// The character's movement now.
            NavigationSnapshot = navigation_snapshot(()) -> $crate::navigation::NavigationSnapshot,
                permission: None, window: false, since: (1, 0);
            /// One object by id, with what is known of its door and lock.
            NavigationObject = navigation_object(u32) -> Option<$crate::navigation::NavigationObject>,
                permission: None, window: false, since: (1, 0);
            /// The object nearest a position with an exact name.
            NavigationFind = navigation_find($crate::navigation::FindObject)
                -> Option<$crate::navigation::NavigationObject>,
                permission: None, window: false, since: (1, 0);
            /// Every object whose position is known, by id.
            NavigationObjects = navigation_objects(()) -> Vec<$crate::navigation::NavigationObject>,
                permission: None, window: false, since: (1, 0);
            /// Room for a body the size of the character this many metres
            /// (above 0, at most 10) straight ahead of where it faces, as
            /// the client places an object entering the world, without
            /// sliding aside; creatures and players there do not count.
            /// Nothing moves.
            NavigationRoomAhead = navigation_room_ahead(f32) -> $crate::navigation::RoomAhead,
                permission: None, window: false, since: (1, 0);
            /// Hold movement keys until replaced or cleared.
            NavigationSetIntent = navigation_set_intent($crate::navigation::MovementIntent)
                -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// Release the held movement keys.
            NavigationClearIntent = navigation_clear_intent(()) -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// Turn on the spot to a compass heading, degrees.
            NavigationFace = navigation_face(f32) -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// Begin a move the client carries out.
            NavigationMove = navigation_move($crate::navigation::MoveRequest)
                -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// End the move on one channel, or every move (none).
            NavigationStopMoving = navigation_stop_moving(Option<$crate::navigation::MoveChannel>)
                -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// Jump with a share of a full charge (above 0, at most 1).
            NavigationJump = navigation_jump(f32) -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// The latest move on each channel and the latest jump.
            NavigationMoveReport = navigation_move_report(()) -> $crate::navigation::MoveReport,
                permission: None, window: false, since: (1, 0);
            /// Walk to an object: (object, arrival metres).
            NavigationGoTo = navigation_go_to((u32, f32)) -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// Walk to a place.
            NavigationGoToPlace = navigation_go_to_place($crate::navigation::GoToPlace)
                -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// Walk onto an object's top: (object, arrival metres).
            NavigationStandOn = navigation_stand_on((u32, f32)) -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// Follow a player: (player, buffer metres).
            NavigationFollow = navigation_follow((u32, f32)) -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// End the walk the plugin started.
            NavigationStopGoTo = navigation_stop_go_to(()) -> $crate::navigation::NavigationStatus,
                permission: None, window: false, since: (1, 0);
            /// The latest walk.
            NavigationGoToReport = navigation_go_to_report(()) -> $crate::navigation::GoToReport,
                permission: None, window: false, since: (1, 0);
            /// Plan a path without moving the character, owning navigation
            /// or touching a walk under way: a handle (never 0); the plan
            /// arrives as `Event::NavigationPlan` to this plugin, at the
            /// earliest after the plugin's call returns. The collision is
            /// captured now; the grid and the search run on a worker. One
            /// planning region: about 320 m outdoors, a whole sealed dungeon
            /// up to 2,048 m.
            NavigationPreviewPath = navigation_preview_path($crate::navigation::PathPreviewRequest)
                -> u32,
                permission: None, window: false, since: (1, 0);
            /// Every cell of the sealed dungeon the character stands in that
            /// it can walk to within `max_meters`, each with the length of
            /// its route, from one search (API 1.5): a handle (never 0), or
            /// 0 when nothing can be searched from (no session, out of the
            /// world, in portal space or in the air, not in a sealed dungeon,
            /// or before the game data walks need is loaded) or `max_meters`
            /// is not above 0. The
            /// answer arrives as `Event::NavigationReach` to this plugin, at
            /// the earliest after the call returns. The search runs on a
            /// worker over the grid walks and previews use.
            NavigationReachableCells = navigation_reachable_cells($crate::navigation::ReachRequest)
                -> u32,
                permission: None, window: false, since: (1, 5);
            /// Have walks wait while the plugin needs the character: a
            /// handle whose reason (initially this one) the plugin updates;
            /// 0 when refused.
            NavigationPause = navigation_pause(String) -> u32,
                permission: None, window: false, since: (1, 0);
            /// Change what a pause needs the character for (empty: nothing
            /// now); false for an unknown handle.
            NavigationPauseSet = navigation_pause_set($crate::navigation::PauseChange) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Remove a pause; false when already gone.
            NavigationPauseRelease = navigation_pause_release(u32) -> bool,
                permission: None, window: false, since: (1, 0);

            // ── M. Peers and the status board ───────────────────────────
            /// The other clients on this computer, by name.
            PeersClients = peers_clients(()) -> Vec<$crate::peers::NetworkClient>,
                permission: None, window: false, since: (1, 0);
            /// Tell the other clients about a cast; false when it makes no
            /// sense (or the character is not in the world).
            PeersAnnounceCast = peers_announce_cast($crate::peers::CastAnnouncement) -> bool,
                permission: None, window: false, since: (1, 0);
            /// The casts other clients announced above a sequence.
            PeersCasts = peers_casts(i64) -> Vec<$crate::peers::PeerCast>,
                permission: None, window: false, since: (1, 0);
            /// The labels this client answers to; false for a label too
            /// long to travel.
            PeersSetTags = peers_set_tags(Vec<String>) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Ask the other clients to run a command line.
            PeersBroadcast = peers_broadcast($crate::peers::Broadcast) -> bool,
                permission: None, window: false, since: (1, 0);
            /// The command lines other clients broadcast to this one above
            /// a sequence (the client runs them itself).
            PeersCommands = peers_commands(i64) -> Vec<$crate::peers::PeerCommand>,
                permission: None, window: false, since: (1, 0);
            /// Take part in the peer features a capability mask names
            /// (OpenAC `PluginPeerCapabilities`: 1 client state, 2 casts,
            /// 4 commands, 8 sending commands) over the clients' peer hub:
            /// nothing is shared until some plugin of the session holds a
            /// lease, and only what the leases name. None for no capability
            /// or an unknown one, or while peer communication is off.
            PeersSubscribe = peers_subscribe(u32) -> Option<u32>,
                permission: None, window: false, since: (1, 0);
            /// Give back a lease `peers_subscribe` handed out (when the
            /// plugin stops sharing); false for a handle the plugin does not
            /// hold. Leases still held are given back when the plugin is
            /// unloaded.
            PeersUnsubscribe = peers_unsubscribe(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Whether this client is registered with the peer hub and has
            /// heard back (false before, and while peer communication is
            /// off): until then an empty peer list proves nothing.
            PeersIsConnected = peers_is_connected(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// What this client tells the others about its own character
            /// right now; none out of the world or before its position is
            /// known.
            PeersCaptureSelf = peers_capture_self(()) -> Option<$crate::peers::NetworkClient>,
                permission: None, window: false, since: (1, 0);
            /// The casts this client announced above a sequence that a
            /// neighbour would still take in (the last 32, dropped when the
            /// character leaves the world), for a relay to pass on.
            PeersOwnCasts = peers_own_casts(i64) -> Vec<$crate::peers::PeerCast>,
                permission: None, window: false, since: (1, 0);
            /// The lines this client broadcast above a sequence that a
            /// neighbour would still take in, for a relay to pass on.
            PeersOwnCommands = peers_own_commands(i64) -> Vec<$crate::peers::PeerCommand>,
                permission: None, window: false, since: (1, 0);
            /// Add or refresh a peer this computer cannot see (keyed by its
            /// character; it goes stale 15 s after the last import); false
            /// for one a note would not be accepted for, this character, or
            /// a 257th live import.
            PeersImportClient = peers_import_client($crate::peers::NetworkClient) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Add a cast an imported peer made; it reads back from
            /// `peers_casts` marked remote under the same rules.
            PeersImportCast = peers_import_cast($crate::peers::RemoteCast) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Add a line an imported peer broadcast; this client runs it as
            /// a neighbour's line when it is aimed at its labels.
            PeersImportCommand = peers_import_command($crate::peers::RemoteBroadcast) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Publish (or, with none, withdraw) a value under a key on the
            /// session's status board.
            StatusPublish = status_publish((String, Option<String>)) -> bool,
                permission: None, window: false, since: (1, 0);
            /// One plugin's value under a key: (plugin id, key).
            StatusRead = status_read((String, String)) -> Option<String>,
                permission: None, window: false, since: (1, 0);
            /// Everything one plugin published, by key.
            StatusCapture = status_capture(String) -> Vec<(String, String)>,
                permission: None, window: false, since: (1, 0);
            /// Hear [`crate::Event::StatusChanged`] when a value of the
            /// plugin with this id changes ([`crate::status`]); false for a
            /// blank id, the plugin's own, or past
            /// [`crate::status::MAX_SUBSCRIPTIONS`] plugins.
            StatusSubscribe = status_subscribe(String) -> bool,
                permission: None, window: false, since: (1, 8);
            /// Stop hearing one plugin's changes; false when not
            /// subscribed to it.
            StatusUnsubscribe = status_unsubscribe(String) -> bool,
                permission: None, window: false, since: (1, 8);

            // ── P. Plugin services (`crate::services`) ──────────────────
            /// Offer a service other plugins can call (scoped as
            /// `<plugin id>/<name>`), answered through the callback export;
            /// the plugin's handle for it.
            ServiceRegister = service_register($crate::services::ServiceRegistration)
                -> Result<u32, $crate::services::ServiceError>
                = Err($crate::services::ServiceError::Refused(
                    "plugin services are unavailable".to_owned())),
                permission: None, window: false, since: (1, 7);
            /// Withdraw one of the plugin's services; false for a handle it
            /// does not hold.
            ServiceUnregister = service_unregister(u32) -> bool,
                permission: None, window: false, since: (1, 7);
            /// Every service on offer, by id.
            Services = services(()) -> Vec<$crate::services::ServiceInfo>,
                permission: None, window: false, since: (1, 7);
            /// Call another plugin's service and wait for its reply.
            ServiceCall = service_call($crate::services::ServiceCall)
                -> Result<Vec<u8>, $crate::services::ServiceError>
                = Err($crate::services::ServiceError::NotFound),
                permission: None, window: false, since: (1, 7);

            // ── N. Storage ──────────────────────────────────────────────
            /// The bytes stored under a key, none when nothing is.
            StorageRead = storage_read($crate::storage::StorageKey)
                -> Result<Option<Vec<u8>>, String> = Ok(None),
                permission: None, window: false, since: (1, 0);
            /// The text stored under a key, decoded as a .NET plugin reads
            /// a file (a UTF-8, UTF-16 or UTF-32 byte order mark is
            /// honoured and dropped; UTF-8 otherwise, bad bytes replaced).
            StorageReadText = storage_read_text($crate::storage::StorageKey)
                -> Result<Option<String>, String> = Ok(None),
                permission: None, window: false, since: (1, 0);
            /// Replace what is stored under a key, atomically (written
            /// beside it, then moved over it).
            StorageWrite = storage_write($crate::storage::StorageWrite)
                -> Result<(), String> = Err("storage is unavailable".to_owned()),
                permission: None, window: false, since: (1, 0);
            /// The keys of the files beneath a prefix (`""`: all), sorted
            /// ignoring case.
            StorageList = storage_list($crate::storage::StorageKey)
                -> Result<Vec<String>, String> = Ok(Vec::new()),
                permission: None, window: false, since: (1, 0);
            /// Remove what is stored under a key; false when nothing was.
            StorageDelete = storage_delete($crate::storage::StorageKey)
                -> Result<bool, String> = Ok(false),
                permission: None, window: false, since: (1, 0);
            /// Create the folder a key prefix names, with its parents;
            /// whether it exists afterwards.
            StorageEnsureFolder = storage_ensure_folder($crate::storage::StorageKey)
                -> Result<bool, String> = Ok(false),
                permission: None, window: false, since: (1, 0);
            /// Where a place is on disk, to show the player (never to open:
            /// keys go through these calls); none without storage.
            StorageDescribe = storage_describe($crate::storage::StoragePlace) -> Option<String>,
                permission: None, window: false, since: (1, 0);
            /// Take a lock other clients on this computer honour, named by
            /// a key (held until released or the plugin stops): its handle;
            /// an error while another client holds it.
            StorageLock = storage_lock($crate::storage::StorageKey)
                -> Result<u32, String> = Err("storage is unavailable".to_owned()),
                permission: None, window: false, since: (1, 0);
            /// Release a lock; false for an unknown handle.
            StorageUnlock = storage_unlock(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// The data files under a folder in every layer, the player's
            /// own over the package's (the plugin's data folder, then its
            /// own folder): each relative path once, sorted ignoring case
            /// (the resource catalog).
            ResourcesListDataFiles = resources_list_data_files(String)
                -> Result<Vec<String>, String> = Ok(Vec::new()),
                permission: None, window: false, since: (1, 0);

            // ── Net ─────────────────────────────────────────────────────
            /// Send an HTTP request to a host the manifest lists or the
            /// player approved: its handle; the reply arrives as an event.
            NetHttpRequest = net_http_request($crate::net::HttpRequest)
                -> Result<u32, String> = Err("the network is unavailable".to_owned()),
                permission: Some($crate::Permission::Network), window: false, since: (1, 0);
            /// Open a TCP connection (TLS optional) to an allowed host: its
            /// handle; connected or failed arrives as an event.
            NetTcpConnect = net_tcp_connect($crate::net::TcpConnect)
                -> Result<u32, String> = Err("the network is unavailable".to_owned()),
                permission: Some($crate::Permission::Network), window: false, since: (1, 0);
            /// Open a TCP connection to an allowed host as a WebSocket's is
            /// opened (.NET `ClientWebSocket`): through the system proxy's
            /// tunnel for `wss` (TLS) or `ws`, direct when no proxy applies.
            /// `net_tcp_connect` always goes direct, as a .NET socket does.
            NetTcpConnectProxied = net_tcp_connect_proxied($crate::net::TcpConnect)
                -> Result<u32, String> = Err("the network is unavailable".to_owned()),
                permission: Some($crate::Permission::Network), window: false, since: (1, 0);
            /// Listen for TCP connections on a port of this computer only
            /// (loopback): the listener's handle.
            NetTcpListen = net_tcp_listen(u32)
                -> Result<u32, String> = Err("the network is unavailable".to_owned()),
                permission: Some($crate::Permission::Listen), window: false, since: (1, 0);
            /// Queue bytes on one of the plugin's sockets: how many.
            NetSend = net_send((u32, Vec<u8>)) -> Result<u32, String> = Err("no such socket".to_owned()),
                permission: None, window: false, since: (1, 0);
            /// Take up to this many waiting bytes from a socket.
            NetReceive = net_receive((u32, u32)) -> Vec<u8>,
                permission: None, window: false, since: (1, 0);
            /// Close one of the plugin's sockets or listeners (or forget a
            /// request). A socket first writes what was queued on it, for up
            /// to 10 s, then ends its sending half; so does every socket of a
            /// plugin that stops.
            NetClose = net_close(u32) -> bool,
                permission: None, window: false, since: (1, 0);

            // ── L. The client window ────────────────────────────────────
            /// Put text on the system clipboard; false with no window.
            ClientSetClipboard = client_set_clipboard(String) -> bool,
                permission: Some($crate::Permission::Clipboard), window: true, since: (1, 0);
            /// Minimize the window; false with no window.
            ClientMinimize = client_minimize(()) -> bool,
                permission: None, window: true, since: (1, 0);
            /// Restore the minimized window; false with no window.
            ClientRestore = client_restore(()) -> bool,
                permission: None, window: true, since: (1, 0);
            /// The window is minimized (false with no window).
            ClientIsMinimized = client_is_minimized(()) -> bool,
                permission: None, window: true, since: (1, 0);

            // ── J. Social and session ───────────────────────────────────
            /// The character's fellowship.
            FellowshipState = fellowship_state(()) -> $crate::social::FellowshipState,
                permission: None, window: false, since: (1, 0);
            /// The other fellows the client can see, with their distance.
            FellowshipMembers = fellowship_members(()) -> Vec<$crate::social::FellowMember>,
                permission: None, window: false, since: (1, 0);
            /// The fellows the client can see, the character included (at
            /// distance 0).
            FellowshipRoster = fellowship_roster(()) -> Vec<$crate::social::FellowMember>,
                permission: None, window: false, since: (1, 0);
            /// Create a fellowship.
            FellowshipCreate = fellowship_create($crate::social::FellowshipCreate)
                -> $crate::social::FellowshipStatus,
                permission: None, window: false, since: (1, 0);
            /// Recruit a player.
            FellowshipRecruit = fellowship_recruit(u32) -> $crate::social::FellowshipStatus,
                permission: None, window: false, since: (1, 0);
            /// Dismiss a fellow.
            FellowshipDismiss = fellowship_dismiss(u32) -> $crate::social::FellowshipStatus,
                permission: None, window: false, since: (1, 0);
            /// Leave the fellowship (true: disband it).
            FellowshipQuit = fellowship_quit(bool) -> $crate::social::FellowshipStatus,
                permission: None, window: false, since: (1, 0);
            /// Hand the leadership to a fellow.
            FellowshipAssignLeader = fellowship_assign_leader(u32) -> $crate::social::FellowshipStatus,
                permission: None, window: false, since: (1, 0);
            /// Open (true) or close the fellowship to recruiting by anyone.
            FellowshipSetOpen = fellowship_set_open(bool) -> $crate::social::FellowshipStatus,
                permission: None, window: false, since: (1, 0);
            /// Ask for (true) or stop the fellows' live vitals.
            FellowshipRequestVitals = fellowship_request_vitals(bool) -> $crate::social::FellowshipStatus,
                permission: None, window: false, since: (1, 0);
            /// The character's allegiance.
            AllegianceState = allegiance_state(()) -> $crate::social::AllegianceState,
                permission: None, window: false, since: (1, 0);
            /// Swear to a patron standing there.
            AllegianceSwear = allegiance_swear(u32) -> $crate::social::AllegianceResult,
                permission: None, window: false, since: (1, 0);
            /// Break from someone in the allegiance.
            AllegianceBreak = allegiance_break(u32) -> $crate::social::AllegianceResult,
                permission: None, window: false, since: (1, 0);
            /// The account's characters and the next login.
            LoginState = login_state(()) -> $crate::social::LoginState,
                permission: None, window: false, since: (1, 0);
            /// Enter this character at the next return to the list; false
            /// for one that cannot enter.
            LoginSetNext = login_set_next(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// No character is entered automatically.
            LoginClearNext = login_clear_next(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Log the character out to the character list.
            LoginLogout = login_logout(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// The latest recall and the destinations known.
            RecallState = recall_state(()) -> $crate::social::RecallState,
                permission: None, window: false, since: (1, 0);
            /// Recall.
            RecallGo = recall_go($crate::social::RecallKind) -> $crate::social::RecallResult,
                permission: None, window: false, since: (1, 0);
            /// The selection.
            SelectionState = selection_state(()) -> $crate::social::SelectionState,
                permission: None, window: false, since: (1, 0);
            /// Select an object (0 clears); false when nothing changed.
            SelectionSelect = selection_select(u32) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Clear the selection; false when nothing was selected.
            SelectionClear = selection_clear(()) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Step the selection; false when nothing changed.
            SelectionExecute = selection_execute($crate::social::SelectionAction) -> bool,
                permission: None, window: false, since: (1, 0);
            /// Answer the server's open question: (context id, accept);
            /// false when no such question is open.
            DialogAnswer = dialog_answer((u32, bool)) -> bool,
                permission: None, window: false, since: (1, 0);
            /// What holds the character busy.
            RecoveryBusyState = recovery_busy_state(()) -> $crate::social::BusyState,
                permission: None, window: false, since: (1, 0);
            /// Free one busy reference (or the unanswered description).
            RecoveryClearOneBusy = recovery_clear_one_busy(()) -> $crate::social::RecoveryResult,
                permission: None, window: false, since: (1, 0);
        }
    };
}

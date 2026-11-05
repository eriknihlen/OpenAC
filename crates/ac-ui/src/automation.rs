//! Hands-free checks of the live interface: a probe that lists the shown
//! element tree (type, dat id, event id, name, rectangle, item, slot,
//! source) and finds elements by dat id or by the item they show, and a
//! script runner that plays a file of commands against the window (clicks,
//! drags, hovers, waits, assertions on the item table, chat commands, key
//! actions, screenshots and world checkpoints), one command at a time, a
//! few per frame, stopping at the first failure with the line it failed on.
//!
//! The runner never touches a window itself: it asks an
//! [`AutomationTarget`], which the window implements.

use std::fmt::Write as _;

use ac_core::items::{ClientObjectTable, EquipMask};

use crate::event::ItemDragSource;
use crate::input::InputAction;
use crate::root::UiRoot;
use crate::tree::ElementId;
use crate::widgets::ItemSlot;

// ── The probe ─────────────────────────────────────────────────────────────

/// One element of the probe's walk.
#[derive(Debug, Clone, PartialEq)]
pub struct ProbeElement {
    /// Its position in the walk.
    pub index: usize,
    /// Its depth under the root.
    pub depth: usize,
    /// Its path: each step's type and child index, with the dat id.
    pub path: String,
    /// The widget's type.
    pub type_name: String,
    /// The element's name, if any.
    pub name: Option<String>,
    /// The event id.
    pub event_id: u32,
    /// The dat element id.
    pub dat_element_id: u32,
    /// Its rectangle on the canvas: left, top, width, height.
    pub rect: (f32, f32, f32, f32),
    /// It and every parent are visible.
    pub visible: bool,
    /// It takes input.
    pub enabled: bool,
    /// The item an item cell shows (0: none).
    pub item_id: u32,
    /// An item cell's slot (−1: not a cell).
    pub slot_index: i32,
    /// An item cell's source.
    pub source_kind: Option<ItemDragSource>,
}

impl ProbeElement {
    /// The centre, canvas pixels.
    #[must_use]
    pub fn center(&self) -> (i32, i32) {
        let (x, y, w, h) = self.rect;
        ((x + w * 0.5).round() as i32, (y + h * 0.5).round() as i32)
    }

    fn has_area(&self) -> bool {
        self.rect.2 > 0.0 && self.rect.3 > 0.0
    }
}

/// The element tree under the root (only what shows when `visible_only`).
#[must_use]
pub fn snapshot(ui: &UiRoot, visible_only: bool) -> Vec<ProbeElement> {
    let mut rows = Vec::new();
    walk(
        ui,
        ui.root(),
        "root".to_owned(),
        0,
        true,
        visible_only,
        &mut rows,
    );
    rows
}

fn walk(
    ui: &UiRoot,
    id: ElementId,
    path: String,
    depth: usize,
    ancestors_visible: bool,
    visible_only: bool,
    rows: &mut Vec<ProbeElement>,
) {
    let Some(e) = ui.element(id) else {
        return;
    };
    let visible = ancestors_visible && e.visible();
    if visible_only && !visible {
        return;
    }
    let (x, y) = ui.screen_position(id);
    let (item_id, slot_index, source_kind) = ui.widget::<ItemSlot>(id).map_or((0, -1, None), |s| {
        (s.object_id, s.slot_index, Some(s.source_kind))
    });
    rows.push(ProbeElement {
        index: rows.len(),
        depth,
        path: path.clone(),
        type_name: type_name(ui, id),
        name: e.name.clone(),
        event_id: ui.event_id(id),
        dat_element_id: e.dat_element_id,
        rect: (x, y, e.width, e.height),
        visible,
        enabled: e.enabled,
        item_id,
        slot_index,
        source_kind,
    });
    for (i, child) in ui.children(id).into_iter().enumerate() {
        let dat = ui.element(child).map_or(0, |c| c.dat_element_id);
        let mut child_path = format!("{path}/{}[{i}]", type_name(ui, child));
        if dat != 0 {
            let _ = write!(child_path, "#0x{dat:08X}");
        }
        walk(
            ui,
            child,
            child_path,
            depth + 1,
            visible,
            visible_only,
            rows,
        );
    }
}

fn type_name(ui: &UiRoot, id: ElementId) -> String {
    ui.widget_dyn(id)
        .map_or_else(|| "Element".to_owned(), |w| w.type_name().to_owned())
}

fn f(v: f32) -> String {
    let s = format!("{v:.2}");
    let s = s.trim_end_matches('0').trim_end_matches('.');
    if s == "-0" {
        "0".to_owned()
    } else {
        s.to_owned()
    }
}

/// The walk as text, one line per element (the probe's `dump`).
#[must_use]
pub fn dump_text(ui: &UiRoot, visible_only: bool) -> String {
    let mut out = String::new();
    for e in snapshot(ui, visible_only) {
        let _ = write!(out, "[{}] {}{}", e.index, "  ".repeat(e.depth), e.type_name);
        if e.dat_element_id != 0 {
            let _ = write!(out, " dat=0x{:08X}", e.dat_element_id);
        }
        if e.event_id != 0 {
            let _ = write!(out, " event=0x{:08X}", e.event_id);
        }
        if let Some(name) = e.name.as_deref().filter(|n| !n.is_empty()) {
            let _ = write!(out, " name={name}");
        }
        let (x, y, w, h) = e.rect;
        let _ = write!(out, " rect=({},{},{},{})", f(x), f(y), f(w), f(h));
        if !e.visible {
            out.push_str(" hidden");
        }
        if !e.enabled {
            out.push_str(" disabled");
        }
        if e.item_id != 0 {
            let _ = write!(out, " item=0x{:08X} slot={}", e.item_id, e.slot_index);
            if let Some(source) = e.source_kind {
                let _ = write!(out, " source={source:?}");
            }
        }
        let _ = writeln!(out, " path={}", e.path);
    }
    out
}

/// The first shown element with dat id `dat_id` that has an area.
#[must_use]
pub fn find_by_dat_element_id(ui: &UiRoot, dat_id: u32) -> Option<ProbeElement> {
    snapshot(ui, true)
        .into_iter()
        .find(|e| e.dat_element_id == dat_id && e.has_area())
}

/// The first shown cell showing `item` (from `source`, if given) with an
/// area.
#[must_use]
pub fn find_by_item_id(
    ui: &UiRoot,
    item: u32,
    source: Option<ItemDragSource>,
) -> Option<ProbeElement> {
    if item == 0 {
        return None;
    }
    // Item lists lay their cells out on the frame; the walk reads where
    // they are now.
    snapshot(ui, true).into_iter().find(|e| {
        e.item_id == item && source.is_none_or(|s| e.source_kind == Some(s)) && e.has_area()
    })
}

/// Canvas pixels to window pixels.
#[must_use]
pub fn canvas_to_window(ui: &UiRoot, (x, y): (i32, i32)) -> (i32, i32) {
    let (sx, sy) = ui.canvas_scale();
    if (sx, sy) == (1.0, 1.0) {
        (x, y)
    } else {
        (
            (x as f32 * sx).round() as i32,
            (y as f32 * sy).round() as i32,
        )
    }
}

/// An assertion on the item table: the equip location, the container or
/// the slot of `item`.
///
/// # Errors
/// What differs, worded as the probe words it.
pub fn assert_item(
    objects: &ClientObjectTable,
    item: u32,
    equipped: Option<EquipMask>,
    container: Option<u32>,
    slot: Option<i32>,
) -> Result<String, String> {
    let Some(o) = objects.get(item) else {
        return Err(format!("item 0x{item:08X} missing from object table"));
    };
    if let Some(e) = equipped
        && o.currently_equipped_location != e
    {
        return Err(format!(
            "item 0x{item:08X} equip expected 0x{:08X}, got 0x{:08X}",
            e.bits(),
            o.currently_equipped_location.bits()
        ));
    }
    if let Some(c) = container
        && o.container_id != c
    {
        return Err(format!(
            "item 0x{item:08X} container expected 0x{c:08X}, got 0x{:08X}",
            o.container_id
        ));
    }
    if let Some(s) = slot
        && o.container_slot != s
    {
        return Err(format!(
            "item 0x{item:08X} slot expected {s}, got {}",
            o.container_slot
        ));
    }
    Ok(format!("item 0x{item:08X} matched object-table assertion"))
}

// ── The script ────────────────────────────────────────────────────────────

/// A world checkpoint the runtime was asked to write.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CheckpointStatus {
    /// Not written yet.
    Pending,
    /// Written.
    Succeeded,
    /// Writing failed.
    Failed,
    /// Dropped before it was written.
    Cancelled,
}

/// What a script drives. Everything not implemented answers that it is
/// unavailable, which stops the script at that line.
pub trait AutomationTarget {
    /// The interface.
    fn ui(&self) -> &UiRoot;
    /// The item table (for `assert item`).
    fn objects(&self) -> Option<&ClientObjectTable>;
    /// Move the pointer to window pixel (`x`, `y`) over the interface.
    fn mouse_move(&mut self, x: i32, y: i32);
    /// Press (or let go) the left button at window pixel (`x`, `y`).
    fn mouse_button(&mut self, x: i32, y: i32, pressed: bool);
    /// Send a chat line or client command.
    ///
    /// # Errors
    /// Nothing can take it.
    fn submit_command(&mut self, _text: &str) -> Result<(), String> {
        Err("command submission is unavailable".into())
    }
    /// Press a key-map action.
    ///
    /// # Errors
    /// Unavailable, or the action was refused.
    fn press_input(&mut self, action: InputAction) -> Result<(), String> {
        Err(format!("input press injection is unavailable ({action:?})"))
    }
    /// Hold or let go of a key-map action.
    ///
    /// # Errors
    /// Unavailable, or the action was refused.
    fn set_input_held(&mut self, action: InputAction, _held: bool) -> Result<(), String> {
        Err(format!("input held injection is unavailable ({action:?})"))
    }
    /// Turn the mouse-look camera by (`dx`, `dy`) pixels.
    ///
    /// # Errors
    /// Unavailable.
    fn mouse_look(&mut self, _dx: f32, _dy: f32) -> Result<(), String> {
        Err("mouselook injection is unavailable".into())
    }
    /// The world is ready (the reveal finished); `None`: no world here.
    fn world_ready(&self) -> Option<bool> {
        None
    }
    /// The normal world view was drawn; `None`: no world here.
    fn world_visible(&self) -> Option<bool> {
        None
    }
    /// How many portal arrivals finished; `None`: no world here.
    fn portal_materializations(&self) -> Option<u32> {
        None
    }
    /// The framebuffer's size; `None`: unknown.
    fn framebuffer(&self) -> Option<(u32, u32)> {
        None
    }
    /// Ask for a checkpoint named `name`; its sequence.
    ///
    /// # Errors
    /// Unavailable or a bad name.
    fn request_checkpoint(&mut self, _name: &str) -> Result<u64, String> {
        Err("world lifecycle automation is unavailable".into())
    }
    /// How checkpoint `sequence` went (and why it failed).
    fn checkpoint_status(&self, _sequence: u64) -> (CheckpointStatus, Option<String>) {
        (
            CheckpointStatus::Failed,
            Some("world lifecycle automation is unavailable".into()),
        )
    }
    /// Drop a checkpoint not written yet.
    fn cancel_checkpoint(&mut self, _sequence: u64) {}
    /// Ask for a screenshot named `name`.
    ///
    /// # Errors
    /// Unavailable or a bad name.
    fn request_screenshot(&mut self, _name: &str) -> Result<(), String> {
        Err("world lifecycle automation is unavailable".into())
    }
    /// The screenshot named `name` was written.
    fn screenshot_complete(&self, _name: &str) -> bool {
        false
    }
    /// Whether the signal file `name` exists.
    ///
    /// # Errors
    /// Unavailable or a bad name.
    fn signal_published(&self, _name: &str) -> Result<bool, String> {
        Err("automation signals need an artifact directory".into())
    }
    /// Turn Potato Mode on or off.
    ///
    /// # Errors
    /// Unavailable.
    fn set_potato_mode(&mut self, _on: bool) -> Result<(), String> {
        Err("potato-mode automation is unavailable".into())
    }
    /// Turn UI Only on or off.
    ///
    /// # Errors
    /// Unavailable.
    fn set_ui_only(&mut self, _on: bool) -> Result<(), String> {
        Err("ui-only automation is unavailable".into())
    }
    /// Turn UI Only in Background on or off.
    ///
    /// # Errors
    /// Unavailable.
    fn set_ui_only_when_unfocused(&mut self, _on: bool) -> Result<(), String> {
        Err("ui-only automation is unavailable".into())
    }
    /// Report the window's focus as `focused`.
    ///
    /// # Errors
    /// Unavailable.
    fn set_window_focused(&mut self, _focused: bool) -> Result<(), String> {
        Err("window-focus automation is unavailable".into())
    }
    /// Resize the framebuffer.
    ///
    /// # Errors
    /// Unavailable.
    fn resize(&mut self, _width: u32, _height: u32) -> Result<(), String> {
        Err("framebuffer resize automation is unavailable".into())
    }
    /// Close the client.
    ///
    /// # Errors
    /// Unavailable.
    fn close_client(&mut self) -> Result<(), String> {
        Err("client-close automation is unavailable".into())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct Command {
    line: usize,
    text: String,
    parts: Vec<String>,
}

/// A script file played against an [`AutomationTarget`]. See the module
/// documentation.
#[derive(Debug, Default)]
pub struct ScriptRunner {
    commands: Vec<Command>,
    load_error: Option<String>,
    dump_on_start: bool,
    index: usize,
    active: Option<usize>,
    elapsed_ms: f64,
    compensation_ms: f64,
    started: bool,
    completed: bool,
    held: Vec<InputAction>,
    checkpoint: Option<(usize, u64)>,
}

/// The most commands run in one frame.
pub const COMMANDS_PER_TICK: usize = 8;

impl ScriptRunner {
    /// A runner over the script `text` (`#` and `//` start comments).
    #[must_use]
    pub fn parse(text: &str, dump_on_start: bool) -> Self {
        let commands = text
            .lines()
            .enumerate()
            .filter_map(|(i, raw)| {
                let line = strip_comment(raw).trim();
                (!line.is_empty()).then(|| Command {
                    line: i + 1,
                    text: line.to_owned(),
                    parts: line.split_whitespace().map(str::to_owned).collect(),
                })
            })
            .collect();
        Self {
            commands,
            dump_on_start,
            ..Self::default()
        }
    }

    /// A runner over the script file at `path`; a file that cannot be read
    /// is reported on the first tick.
    #[must_use]
    pub fn load(path: &std::path::Path, dump_on_start: bool) -> Self {
        match std::fs::read_to_string(path) {
            Ok(text) => Self::parse(&text, dump_on_start),
            Err(e) => Self {
                load_error: Some(format!(
                    "failed to load UI probe script '{}': {e}",
                    path.display()
                )),
                dump_on_start,
                ..Self::default()
            },
        }
    }

    /// The script ended (finished or stopped).
    #[must_use]
    pub fn completed(&self) -> bool {
        self.completed
    }

    /// One frame of `dt_seconds`: run commands until one waits (at most
    /// [`COMMANDS_PER_TICK`]); what it says goes to `log`.
    pub fn tick(
        &mut self,
        dt_seconds: f64,
        target: &mut dyn AutomationTarget,
        log: &mut Vec<String>,
    ) {
        if self.completed {
            return;
        }
        // Sub-millisecond frames add up exactly (compensated summation); a
        // delta that is not a finite positive time, or overflows, is ignored.
        if dt_seconds.is_finite() && dt_seconds > 0.0 && self.active == Some(self.index) {
            let add = dt_seconds * 1000.0;
            let adjusted = add - self.compensation_ms;
            let next = self.elapsed_ms + adjusted;
            let next_compensation = (next - self.elapsed_ms) - adjusted;
            if add.is_finite() && next.is_finite() && next_compensation.is_finite() {
                self.elapsed_ms = next;
                self.compensation_ms = next_compensation;
            }
        }
        if !self.started {
            self.started = true;
            if let Some(error) = self.load_error.take() {
                log.push(error);
                self.completed = true;
                return;
            }
            if self.dump_on_start {
                log.push(dump_text(target.ui(), true));
            }
            if self.commands.is_empty() {
                self.completed = true;
                return;
            }
            log.push(format!(
                "running {} UI probe command(s)",
                self.commands.len()
            ));
        }
        let mut run = 0;
        while !self.completed && self.index < self.commands.len() && run < COMMANDS_PER_TICK {
            run += 1;
            if self.active != Some(self.index) {
                self.active = Some(self.index);
                self.elapsed_ms = 0.0;
                self.compensation_ms = 0.0;
            }
            let command = self.commands[self.index].clone();
            if !self.execute(&command, target, log) {
                break;
            }
            self.index += 1;
            self.active = None;
            if command.parts[0].eq_ignore_ascii_case("resize") {
                break;
            }
        }
        if self.index >= self.commands.len() && !self.completed {
            self.release_held(target);
            self.completed = true;
            log.push("UI probe script complete".to_owned());
        }
    }

    /// Stop: a checkpoint not written is cancelled, held actions let go.
    pub fn dispose(&mut self, target: &mut dyn AutomationTarget) {
        if let Some((_, sequence)) = self.checkpoint.take() {
            target.cancel_checkpoint(sequence);
        }
        self.release_held(target);
        self.completed = true;
    }

    fn release_held(&mut self, target: &mut dyn AutomationTarget) {
        for action in std::mem::take(&mut self.held) {
            let _ = target.set_input_held(action, false);
        }
    }

    fn stop(
        &mut self,
        command: &Command,
        message: &str,
        target: &mut dyn AutomationTarget,
        log: &mut Vec<String>,
    ) -> bool {
        log.push(format!(
            "line {}: {message}; command: {}",
            command.line, command.text
        ));
        self.release_held(target);
        self.completed = true;
        false
    }

    fn elapsed(&self, ms: u64) -> bool {
        let wanted = ms as f64;
        self.elapsed_ms.is_finite()
            && (self.elapsed_ms >= wanted || wanted - self.elapsed_ms <= (wanted * 1e-12).max(1e-9))
    }

    fn exceeded(&self, ms: u64) -> bool {
        let wanted = ms as f64;
        self.elapsed_ms.is_finite()
            && self.elapsed_ms > wanted
            && self.elapsed_ms - wanted > (wanted * 1e-12).max(1e-9)
    }

    fn wait(
        &mut self,
        command: &Command,
        timeout_ms: u64,
        label: &str,
        target: &mut dyn AutomationTarget,
        log: &mut Vec<String>,
    ) -> bool {
        if !self.exceeded(timeout_ms) {
            return false;
        }
        self.stop(
            command,
            &format!("timed out waiting for {label}"),
            target,
            log,
        )
    }

    #[allow(clippy::too_many_lines)]
    fn execute(
        &mut self,
        c: &Command,
        target: &mut dyn AutomationTarget,
        log: &mut Vec<String>,
    ) -> bool {
        let p: Vec<&str> = c.parts.iter().map(String::as_str).collect();
        let verb = p[0].to_ascii_lowercase();
        let result: Result<bool, String> = match verb.as_str() {
            "dump" => {
                log.push(dump_text(target.ui(), true));
                Ok(true)
            }
            "click" => click(&p, target),
            "hover" => hover(&p, target),
            "mousemove" => match (
                p.len(),
                p.get(1).and_then(|v| int(v)),
                p.get(2).and_then(|v| int(v)),
            ) {
                (3, Some(x), Some(y)) => {
                    target.mouse_move(x, y);
                    Ok(true)
                }
                _ => Err("usage: mousemove <x> <y>".into()),
            },
            "doubleclick" => double_click(&p, target),
            "drag" => drag(&p, target),
            "wait" => return self.do_wait(c, &p, target, log),
            "sleep" => match p.get(1).and_then(|v| v.parse::<u64>().ok()) {
                Some(ms) => Ok(self.elapsed(ms)),
                None => Err("usage: sleep <milliseconds> | wait ms <milliseconds>".into()),
            },
            "assert" => do_assert(&p, target),
            "command" => {
                let text = c.text[p[0].len()..].trim();
                if text.is_empty() {
                    Err("usage: command <chat-or-client-command>".into())
                } else {
                    target.submit_command(text).map(|()| true)
                }
            }
            "input" => self.do_input(&p, target),
            "mouselook" => match (
                p.len(),
                p.get(1).and_then(|v| v.parse::<f32>().ok()),
                p.get(2).and_then(|v| v.parse::<f32>().ok()),
            ) {
                (3, Some(dx), Some(dy)) => target.mouse_look(dx, dy).map(|()| true),
                _ => Err("usage: mouselook <dx> <dy>".into()),
            },
            "checkpoint" => return self.do_checkpoint(c, &p, target, log),
            "potato" => on_off(&p, 1).map_or_else(
                || Err("usage: potato on|off".into()),
                |on| target.set_potato_mode(on).map(|()| true),
            ),
            "uionly" => {
                if p.len() == 3 && p[1].eq_ignore_ascii_case("background") {
                    on_off(&p, 2).map_or_else(
                        || Err("usage: uionly on|off | uionly background on|off".into()),
                        |on| target.set_ui_only_when_unfocused(on).map(|()| true),
                    )
                } else if p.len() == 2 {
                    on_off(&p, 1).map_or_else(
                        || Err("usage: uionly on|off | uionly background on|off".into()),
                        |on| target.set_ui_only(on).map(|()| true),
                    )
                } else {
                    Err("usage: uionly on|off | uionly background on|off".into())
                }
            }
            "focus" => on_off(&p, 1).filter(|_| p.len() == 2).map_or_else(
                || Err("usage: focus on|off".into()),
                |on| target.set_window_focused(on).map(|()| true),
            ),
            "resize" => match (
                p.len(),
                p.get(1).and_then(|v| v.parse::<u32>().ok()),
                p.get(2).and_then(|v| v.parse::<u32>().ok()),
            ) {
                (3, Some(w), Some(h)) if w > 0 && h > 0 => {
                    if !(320..=8192).contains(&w) || !(240..=8192).contains(&h) {
                        Err(
                            "automation framebuffer size must be within 320x240 and 8192x8192"
                                .into(),
                        )
                    } else {
                        target.resize(w, h).map(|()| true)
                    }
                }
                _ => Err("usage: resize <width> <height>".into()),
            },
            "renderpack" => Err("render packs are not part of this client".into()),
            "screenshot" => {
                if !(2..=3).contains(&p.len()) {
                    Err("usage: screenshot <name> [timeoutMs]".into())
                } else {
                    let name = p[1];
                    match target.request_screenshot(name) {
                        Err(e) => Err(e),
                        Ok(()) if target.screenshot_complete(name) => Ok(true),
                        Ok(()) => {
                            return self.wait(
                                c,
                                timeout(&p, 2, 10_000),
                                &format!("screenshot '{name}'"),
                                target,
                                log,
                            );
                        }
                    }
                }
            }
            "close-client" => {
                if p.len() == 1 {
                    target.close_client().map(|()| true)
                } else {
                    Err("usage: close-client".into())
                }
            }
            _ => Err(format!("unknown command '{}'", p[0])),
        };
        match result {
            Ok(done) => done,
            Err(message) => self.stop(c, &message, target, log),
        }
    }

    fn do_wait(
        &mut self,
        c: &Command,
        p: &[&str],
        target: &mut dyn AutomationTarget,
        log: &mut Vec<String>,
    ) -> bool {
        const USAGE: &str = "usage: wait item|element|ms|world-ready|world-visible|materialized|framebuffer|signal ...";
        let Some(kind) = p.get(1).map(|k| k.to_ascii_lowercase()) else {
            return self.stop(c, USAGE, target, log);
        };
        match kind.as_str() {
            "item" => {
                let Some(item) = p.get(2).and_then(|v| uint(v)) else {
                    return self.stop(
                        c,
                        "usage: wait item <guid> [source] [timeoutMs]",
                        target,
                        log,
                    );
                };
                if find_by_item_id(target.ui(), item, source(p, 3)).is_some() {
                    return true;
                }
                self.wait(
                    c,
                    timeout(p, 3, 10_000),
                    &format!("item 0x{item:08X}"),
                    target,
                    log,
                )
            }
            "element" => {
                let Some(id) = p.get(2).and_then(|v| uint(v)) else {
                    return self.stop(c, "usage: wait element <datId> [timeoutMs]", target, log);
                };
                if find_by_dat_element_id(target.ui(), id).is_some() {
                    return true;
                }
                self.wait(
                    c,
                    timeout(p, 3, 10_000),
                    &format!("element 0x{id:08X}"),
                    target,
                    log,
                )
            }
            "ms" => match p.get(2).and_then(|v| v.parse::<u64>().ok()) {
                Some(ms) => self.elapsed(ms),
                None => self.stop(
                    c,
                    "usage: sleep <milliseconds> | wait ms <milliseconds>",
                    target,
                    log,
                ),
            },
            "world-ready" => match target.world_ready() {
                None => self.stop(c, "world lifecycle automation is unavailable", target, log),
                Some(true) => true,
                Some(false) => self.wait(c, timeout(p, 2, 60_000), "world readiness", target, log),
            },
            "world-visible" => match target.world_visible() {
                None => self.stop(c, "world lifecycle automation is unavailable", target, log),
                Some(true) => true,
                Some(false) => self.wait(
                    c,
                    timeout(p, 2, 60_000),
                    "normal world viewport",
                    target,
                    log,
                ),
            },
            "materialized" => {
                let Some(n) = p
                    .get(2)
                    .and_then(|v| v.parse::<u32>().ok())
                    .filter(|n| *n > 0)
                else {
                    return self.stop(
                        c,
                        "usage: wait materialized <occurrence> [timeoutMs]",
                        target,
                        log,
                    );
                };
                match target.portal_materializations() {
                    None => self.stop(c, "world lifecycle automation is unavailable", target, log),
                    Some(count) if count >= n => true,
                    Some(_) => self.wait(
                        c,
                        timeout(p, 3, 60_000),
                        &format!("portal materialization {n}"),
                        target,
                        log,
                    ),
                }
            }
            "framebuffer" => {
                let (Some(w), Some(h)) = (
                    p.get(2)
                        .and_then(|v| v.parse::<u32>().ok())
                        .filter(|v| *v > 0),
                    p.get(3)
                        .and_then(|v| v.parse::<u32>().ok())
                        .filter(|v| *v > 0),
                ) else {
                    return self.stop(
                        c,
                        "usage: wait framebuffer <width> <height> [timeoutMs]",
                        target,
                        log,
                    );
                };
                match target.framebuffer() {
                    None => self.stop(
                        c,
                        "framebuffer resize automation is unavailable",
                        target,
                        log,
                    ),
                    Some(size) if size == (w, h) => true,
                    Some(_) => self.wait(
                        c,
                        timeout(p, 4, 30_000),
                        &format!("framebuffer {w}x{h}"),
                        target,
                        log,
                    ),
                }
            }
            "signal" => {
                let Some(name) = p.get(2) else {
                    return self.stop(c, "usage: wait signal <name> [timeoutMs]", target, log);
                };
                match target.signal_published(name) {
                    Err(e) => self.stop(c, &e, target, log),
                    Ok(true) => true,
                    Ok(false) => self.wait(
                        c,
                        timeout(p, 3, 120_000),
                        &format!("automation signal '{name}'"),
                        target,
                        log,
                    ),
                }
            }
            "render-pack" | "render-pack-samples" => {
                self.stop(c, "render packs are not part of this client", target, log)
            }
            _ => self.stop(c, USAGE, target, log),
        }
    }

    fn do_input(&mut self, p: &[&str], target: &mut dyn AutomationTarget) -> Result<bool, String> {
        if p.len() != 3 {
            return Err("usage: input press|down|up <InputAction>".into());
        }
        let Some(action) = InputAction::from_name(p[2])
            .or_else(|| {
                InputAction::ALL
                    .iter()
                    .copied()
                    .find(|a| a.name().eq_ignore_ascii_case(p[2]))
            })
            .filter(|a| *a != InputAction::None)
        else {
            return Err(format!("unknown input action '{}'", p[2]));
        };
        match p[1].to_ascii_lowercase().as_str() {
            "press" => target
                .press_input(action)
                .map(|()| true)
                .map_err(|e| format!("input press {action:?} was captured: {e}")),
            "down" => {
                if self.held.contains(&action) {
                    return Err(format!("input {action:?} is already down"));
                }
                target
                    .set_input_held(action, true)
                    .map_err(|e| format!("input down {action:?} was captured: {e}"))?;
                self.held.push(action);
                Ok(true)
            }
            "up" => {
                let Some(i) = self.held.iter().position(|a| *a == action) else {
                    return Err(format!("input {action:?} is not down"));
                };
                target
                    .set_input_held(action, false)
                    .map_err(|e| format!("input up {action:?} failed: {e}"))?;
                self.held.remove(i);
                Ok(true)
            }
            _ => Err("usage: input press|down|up <InputAction>".into()),
        }
    }

    fn do_checkpoint(
        &mut self,
        c: &Command,
        p: &[&str],
        target: &mut dyn AutomationTarget,
        log: &mut Vec<String>,
    ) -> bool {
        if p.len() != 2 {
            return self.stop(c, "usage: checkpoint <name>", target, log);
        }
        let sequence = match self.checkpoint {
            Some((index, sequence)) if index == self.index => sequence,
            Some(_) => {
                return self.stop(
                    c,
                    "checkpoint acknowledgement belongs to a different script command",
                    target,
                    log,
                );
            }
            None => match target.request_checkpoint(p[1]) {
                Ok(sequence) => {
                    self.checkpoint = Some((self.index, sequence));
                    sequence
                }
                Err(e) => return self.stop(c, &e, target, log),
            },
        };
        match target.checkpoint_status(sequence) {
            (CheckpointStatus::Pending, _) => false,
            (CheckpointStatus::Succeeded, _) => {
                self.checkpoint = None;
                true
            }
            (status, error) => {
                self.checkpoint = None;
                let message =
                    error.unwrap_or_else(|| format!("checkpoint '{}' ended with {status:?}", p[1]));
                self.stop(c, &message, target, log)
            }
        }
    }
}

fn strip_comment(line: &str) -> &str {
    let hash = line.find('#');
    let slashes = line.find("//");
    let cut = match (hash, slashes) {
        (Some(a), Some(b)) => Some(a.min(b)),
        (a, b) => a.or(b),
    };
    cut.map_or(line, |c| &line[..c])
}

fn uint(value: &str) -> Option<u32> {
    match value
        .strip_prefix("0x")
        .or_else(|| value.strip_prefix("0X"))
    {
        Some(hex) => u32::from_str_radix(hex, 16).ok(),
        None => value.parse().ok(),
    }
}

fn int(value: &str) -> Option<i32> {
    value.parse().ok()
}

fn on_off(p: &[&str], at: usize) -> Option<bool> {
    match p.get(at).map(|v| v.to_ascii_lowercase()).as_deref() {
        Some("on") => Some(true),
        Some("off") => Some(false),
        _ => None,
    }
}

/// The first item source named among `p[start..]`.
fn source(p: &[&str], start: usize) -> Option<ItemDragSource> {
    p.iter()
        .skip(start)
        .find_map(|v| match v.to_ascii_lowercase().as_str() {
            "inventory" | "pack" | "backpack" => Some(ItemDragSource::Inventory),
            "shortcut" | "shortcutbar" | "toolbar" => Some(ItemDragSource::ShortcutBar),
            "equipment" | "equip" | "paperdoll" => Some(ItemDragSource::Equipment),
            "ground" => Some(ItemDragSource::Ground),
            "salvage" => Some(ItemDragSource::Salvage),
            "vendor" => Some(ItemDragSource::Vendor),
            _ => None,
        })
}

/// The first number among `p[start..]`, else `default`.
fn timeout(p: &[&str], start: usize, default: u64) -> u64 {
    p.iter()
        .skip(start)
        .find_map(|v| v.parse::<u64>().ok())
        .unwrap_or(default)
}

fn click_at(target: &mut dyn AutomationTarget, canvas: (i32, i32)) {
    let (x, y) = canvas_to_window(target.ui(), canvas);
    target.mouse_move(x, y);
    target.mouse_button(x, y, true);
    target.mouse_button(x, y, false);
}

fn drag_at(target: &mut dyn AutomationTarget, from: (i32, i32), to: (i32, i32)) {
    let (sx, sy) = canvas_to_window(target.ui(), from);
    let (ex, ey) = canvas_to_window(target.ui(), to);
    target.mouse_move(sx, sy);
    target.mouse_button(sx, sy, true);
    target.mouse_move(sx + 5, sy + 5);
    target.mouse_move(ex, ey);
    target.mouse_button(ex, ey, false);
}

fn element(target: &dyn AutomationTarget, id: u32) -> Result<ProbeElement, String> {
    find_by_dat_element_id(target.ui(), id).ok_or_else(|| format!("element 0x{id:08X} not found"))
}

fn item(
    target: &dyn AutomationTarget,
    id: u32,
    source: Option<ItemDragSource>,
    what: &str,
) -> Result<ProbeElement, String> {
    find_by_item_id(target.ui(), id, source)
        .ok_or_else(|| format!("{what}item 0x{id:08X} not found"))
}

fn click(p: &[&str], target: &mut dyn AutomationTarget) -> Result<bool, String> {
    const USAGE: &str =
        "usage: click element <datId> | click item <guid> [source] | click at <x> <y>";
    if p.len() < 3 {
        return Err(USAGE.into());
    }
    match p[1].to_ascii_lowercase().as_str() {
        "element" => {
            let id = uint(p[2]).ok_or_else(|| format!("bad element id '{}'", p[2]))?;
            let e = element(target, id)?;
            click_at(target, e.center());
            Ok(true)
        }
        "item" => {
            let id = uint(p[2]).ok_or_else(|| format!("bad item guid '{}'", p[2]))?;
            let e = item(target, id, source(p, 3), "")?;
            click_at(target, e.center());
            Ok(true)
        }
        "at" => match (p.get(2).and_then(|v| int(v)), p.get(3).and_then(|v| int(v))) {
            (Some(x), Some(y)) => {
                click_at(target, (x, y));
                Ok(true)
            }
            _ => Err("usage: click at <x> <y>".into()),
        },
        _ => Err(USAGE.into()),
    }
}

fn hover(p: &[&str], target: &mut dyn AutomationTarget) -> Result<bool, String> {
    if p.len() < 3 || !p[1].eq_ignore_ascii_case("element") {
        return Err("usage: hover element <datId>".into());
    }
    let id = uint(p[2]).ok_or_else(|| format!("bad element id '{}'", p[2]))?;
    let e = element(target, id)?;
    let (x, y) = canvas_to_window(target.ui(), e.center());
    target.mouse_move(x, y);
    Ok(true)
}

fn double_click(p: &[&str], target: &mut dyn AutomationTarget) -> Result<bool, String> {
    if p.len() < 3 || !p[1].eq_ignore_ascii_case("item") {
        return Err("usage: doubleclick item <guid> [source]".into());
    }
    let id = uint(p[2]).ok_or_else(|| format!("bad item guid '{}'", p[2]))?;
    let e = item(target, id, source(p, 3), "")?;
    click_at(target, e.center());
    click_at(target, e.center());
    Ok(true)
}

fn drag(p: &[&str], target: &mut dyn AutomationTarget) -> Result<bool, String> {
    if p.len() >= 6 && p[1].eq_ignore_ascii_case("at") {
        let v: Vec<Option<i32>> = p[2..6].iter().map(|v| int(v)).collect();
        return match v.as_slice() {
            [Some(x1), Some(y1), Some(x2), Some(y2)] => {
                drag_at(target, (*x1, *y1), (*x2, *y2));
                Ok(true)
            }
            _ => Err("usage: drag at <x1> <y1> <x2> <y2>".into()),
        };
    }
    if p.len() < 5 || !p[1].eq_ignore_ascii_case("item") {
        return Err("usage: drag item <guid> element <datId> | drag item <guid> item <guid> | drag item <guid> outside <x> <y> | drag at <x1> <y1> <x2> <y2>".into());
    }
    let id = uint(p[2]).ok_or_else(|| format!("bad item guid '{}'", p[2]))?;
    match p[3].to_ascii_lowercase().as_str() {
        "element" => {
            let dat = uint(p[4]).ok_or_else(|| format!("bad element id '{}'", p[4]))?;
            let from = item(target, id, source(p, 5), "drag source ")?;
            let to = find_by_dat_element_id(target.ui(), dat)
                .ok_or_else(|| format!("drop target element 0x{dat:08X} not found"))?;
            drag_at(target, from.center(), to.center());
            Ok(true)
        }
        "item" => {
            let other = uint(p[4]).ok_or_else(|| format!("bad target item guid '{}'", p[4]))?;
            let from = item(target, id, source(p, 5), "drag source ")?;
            let to = item(target, other, None, "drop target ")?;
            drag_at(target, from.center(), to.center());
            Ok(true)
        }
        "outside" => {
            let (Some(x), Some(y)) = (p.get(4).and_then(|v| int(v)), p.get(5).and_then(|v| int(v)))
            else {
                return Err("usage: drag item <guid> outside <x> <y> [source]".into());
            };
            let from = item(target, id, source(p, 6), "drag source ")?;
            drag_at(target, from.center(), (x, y));
            Ok(true)
        }
        _ => Err("usage: drag item <guid> element/item/outside ...".into()),
    }
}

fn do_assert(p: &[&str], target: &mut dyn AutomationTarget) -> Result<bool, String> {
    const USAGE: &str = "usage: assert item <guid> equip|container|slot <value>";
    if p.len() < 5 || !p[1].eq_ignore_ascii_case("item") {
        return Err(USAGE.into());
    }
    let id = uint(p[2]).ok_or_else(|| format!("bad item guid '{}'", p[2]))?;
    let objects = target.objects().ok_or("the item table is unavailable")?;
    match p[3].to_ascii_lowercase().as_str() {
        "equip" => {
            let mask = uint(p[4]).ok_or_else(|| format!("bad equip mask '{}'", p[4]))?;
            assert_item(objects, id, Some(EquipMask(mask)), None, None)
        }
        "container" => {
            let c = uint(p[4]).ok_or_else(|| format!("bad container id '{}'", p[4]))?;
            assert_item(objects, id, None, Some(c), None)
        }
        "slot" => {
            let s = int(p[4]).ok_or_else(|| format!("bad slot '{}'", p[4]))?;
            assert_item(objects, id, None, None, Some(s))
        }
        _ => return Err(USAGE.into()),
    }
    .map(|_| true)
}

//! Replays of what a page received from a real session, drawn to PNG.
//!
//! A capture is the stream frames a page passed to `ui_stream`, as records
//! `[u32 milliseconds since the first frame][u32 length][frame bytes]`
//! (little-endian), with a `.json` beside it (same name) holding the
//! relay's `session` message and the `ui_init` values. The replay drives
//! the module as the page did: the headers, `ui_init` with the capture's
//! canvas, the focus, `ui_session`, then frames every 16 ms with each stream
//! frame handed in at its recorded time (the first at the page's time
//! zero) and every dat request answered from the installed dats. An
//! optional script injects input between frames, after the whole capture
//! or, when it has `until` steps, while the capture plays. At the end the
//! last picture is drawn as a page draws it and written as a PNG, with a
//! record of what every input call returned, every command frame sent
//! (heartbeats counted, not listed) and every log line.
//!
//! Captures hold a real session's data and are never part of the
//! repository. Run, with the installed dats present:
//!
//! ```text
//! OPENAC_UI_CAPTURE=<a capture .bin, or a folder of them>
//! OPENAC_UI_REPLAY_OUT=<a folder for the PNGs and logs>   (default: <temp>/openac-ui-replay)
//! OPENAC_UI_REPLAY_SCRIPT=<a script file>                   (optional)
//! OPENAC_UI_REPLAY_CONTROL=1                                (optional: the page has control)
//! cargo test -p ac-ui-web --test replay -- --ignored --nocapture
//! ```
//!
//! Without `OPENAC_UI_CAPTURE` the replay says it was skipped and passes.
//!
//! The script, one step a line (`#` starts a comment; positions are
//! canvas pixels; `B` is the page's button number, 0 left, 1 middle,
//! 2 right):
//!
//! ```text
//! move X Y            down B X Y          up B X Y
//! click X Y           (move, down, a frame, up)
//! wheel LINES X Y     key down CODE       key up CODE
//! key CODE            (down, then up)     text TEXT
//! frames N            wait MS             (frames every 16 ms)
//! until MS            (frames until the capture's time MS)
//! control on|off      png NAME            probe
//! dump [WINDOW]       (the shown elements of the windows whose name
//!                      contains WINDOW, default the plugin windows)
//! ```

mod harness;

use std::fmt::Write as _;
use std::path::{Path, PathBuf};

use ac_remote_wire::SessionCommand;
use ac_runtime::movement::{MovementCommand, MovementKeys};
use ac_ui_web::host::{Modifiers, Panels, Session};
use harness::Page;

/// A recorded attach.
struct Capture {
    name: String,
    frames: Vec<(u32, Vec<u8>)>,
    width: u32,
    height: u32,
    scale: f32,
    generation: u64,
    permissions: u32,
}

/// The number after `"key":` in `json`.
fn json_number(json: &str, key: &str) -> Option<f64> {
    let at = json.find(&format!("\"{key}\""))?;
    let rest = json[at + key.len() + 2..].trim_start().strip_prefix(':')?;
    let rest = rest.trim_start();
    let end = rest
        .find(|c: char| !(c.is_ascii_digit() || c == '.' || c == '-'))
        .unwrap_or(rest.len());
    rest[..end].parse().ok()
}

impl Capture {
    fn load(bin: &Path) -> Self {
        let bytes = std::fs::read(bin).expect("the capture");
        let mut frames = Vec::new();
        let mut at = 0;
        while at + 8 <= bytes.len() {
            let w = harness::words(&bytes[at..at + 8]);
            let len = w[1] as usize;
            frames.push((w[0], bytes[at + 8..at + 8 + len].to_vec()));
            at += 8 + len;
        }
        assert_eq!(at, bytes.len(), "the capture ends on a record");
        let json = std::fs::read_to_string(bin.with_extension("json")).unwrap_or_default();
        let init = json.find("\"ui_init\"").map_or("", |at| &json[at..]);
        let number =
            |text: &str, key: &str, default: f64| json_number(text, key).unwrap_or(default);
        // Permission bits: 0 control, 1 log off, 2 reconnect.
        let mut permissions = 0;
        if json.contains("\"control\": true") || json.contains("\"control\":true") {
            permissions |= 1;
        }
        if json.contains("\"log_off\"") {
            permissions |= 2;
        }
        if json.contains("\"reconnect\"") {
            permissions |= 4;
        }
        Self {
            name: bin
                .file_stem()
                .map_or_else(|| "capture".into(), |s| s.to_string_lossy().into_owned()),
            frames,
            width: number(init, "width", 1024.0) as u32,
            height: number(init, "height", 768.0) as u32,
            scale: number(init, "scale", 1.0) as f32,
            generation: number(&json, "generation", 1.0) as u64,
            permissions,
        }
    }
}

/// The page, the capture's canvas and what happened, line by line.
struct Replay {
    page: Page,
    width: u32,
    height: u32,
    generation: u64,
    permissions: u32,
    out: PathBuf,
    name: String,
    record: Vec<String>,
    /// Heartbeats (empty movement keys) not written to the record.
    heartbeats: usize,
    /// The capture's frames, and the next one to hand in.
    stream: Vec<(u32, Vec<u8>)>,
    next: usize,
}

impl Replay {
    /// The page built and the canvas handed in, as a page starts; the
    /// capture's frames are handed in at their times by the frames that
    /// follow.
    fn start(capture: &Capture, out: &Path) -> Self {
        let mut page = Page::open();
        page.preload();
        let (w, h, s) = (capture.width, capture.height, capture.scale);
        page.panels().init(w, h, s, 0.0);
        page.panels().focus(true);
        let mut replay = Self {
            page,
            width: w,
            height: h,
            generation: capture.generation,
            permissions: capture.permissions,
            out: out.to_path_buf(),
            name: capture.name.clone(),
            record: Vec::new(),
            heartbeats: 0,
            stream: capture.frames.clone(),
            next: 0,
        };
        replay.control(false);
        replay
    }

    /// Hand in the capture's frames whose time has come.
    fn feed(&mut self) {
        while let Some((ms, frame)) = self.stream.get(self.next) {
            if f64::from(*ms) > self.page.now {
                break;
            }
            let (frame, now) = (frame.clone(), self.page.now);
            self.next += 1;
            let kinds = stream_kinds(&frame);
            self.note(format!(
                "stream frame {} ({} bytes): {kinds}",
                self.next,
                frame.len()
            ));
            if let Err(error) = self.page.panels().stream(&frame, now) {
                self.note(format!("stream frame cannot be read: {error}"));
            }
        }
    }

    fn note(&mut self, line: String) {
        self.record.push(format!("{:>8.0} {line}", self.page.now));
    }

    fn control(&mut self, on: bool) {
        let session = Session::from_words(self.generation, u32::from(on), self.permissions);
        self.page.panels().session(session);
        self.note(format!("session control {on}"));
    }

    /// One frame: what it sent and logged goes to the record. Returns the
    /// dat requests it made.
    fn frame(&mut self) -> usize {
        self.feed();
        let (requests, _) = self.page.frame();
        if (1..=8).contains(&requests) {
            let asked: Vec<String> = self
                .page
                .last_requests
                .iter()
                .map(|(source, id)| format!("{source}:{id:#010x}"))
                .collect();
            self.note(format!("asked for {}", asked.join(" ")));
        }
        let heartbeat = format!(
            "{:?}",
            SessionCommand::Movement(MovementCommand::Keys(MovementKeys::default()))
        );
        for (generation, command) in self.page.sent_stamped() {
            let shown = format!("{command:?}");
            if shown == heartbeat {
                self.heartbeats += 1;
                continue;
            }
            self.note(format!("sent (generation {generation}) {shown}"));
        }
        let log = std::mem::take(&mut self.page.log);
        for line in log.lines() {
            self.note(format!("log {line}"));
        }
        requests
    }

    /// Frames for `ms` milliseconds.
    fn wait(&mut self, ms: f64) {
        let until = self.page.now + ms;
        while self.page.now < until {
            self.frame();
        }
    }

    /// Frames until the capture's time `ms` (its first frame is at the
    /// page's time zero).
    fn until(&mut self, ms: f64) {
        while self.page.now < ms {
            self.frame();
        }
    }

    /// The rest of the capture's frames at their times, then frames until
    /// nothing more is asked for.
    fn play(&mut self) {
        while self.next < self.stream.len() {
            self.frame();
        }
        for _ in 0..400 {
            if self.frame() == 0 && self.page.panels().ui().is_some() {
                break;
            }
        }
        self.wait(500.0);
    }

    fn png(&mut self, name: &str) -> PathBuf {
        let path = self.out.join(format!("{}-{name}.png", self.name));
        self.page.write_png(self.width, self.height, &path);
        self.note(format!("png {}", path.display()));
        path
    }

    fn probe(&mut self) {
        let lines = self
            .page
            .panels()
            .ui()
            .map(ac_ui_window::ui::WindowUi::probe)
            .unwrap_or_default();
        for line in lines {
            self.note(line);
        }
    }

    /// Every shown element of the windows whose name contains `filter`:
    /// its type, rectangle and, for the plugin controls, its text.
    fn dump(&mut self, filter: &str) {
        let Some(ui) = self.page.panels().ui() else {
            return;
        };
        let root = &ui.host().root;
        let mut lines = Vec::new();
        for window in root.windows() {
            if !window.name.contains(filter) || !root.is_window_visible(&window.name) {
                continue;
            }
            lines.push(format!("dump window {}", window.name));
            let mut stack = vec![(window.frame, 0)];
            while let Some((id, depth)) = stack.pop() {
                let Some(e) = root.element(id) else {
                    continue;
                };
                if !e.visible {
                    continue;
                }
                let (x, y) = root.screen_position(id);
                let kind = root.widget_dyn(id).map_or("Element", |w| w.type_name());
                let kind = kind.rsplit("::").next().unwrap_or(kind);
                lines.push(format!(
                    "dump {}{kind} {} {} {} {} {}",
                    "  ".repeat(depth),
                    x as i32,
                    y as i32,
                    e.width as i32,
                    e.height as i32,
                    element_text(root, id)
                ));
                let mut children = root.children(id);
                children.reverse();
                stack.extend(children.into_iter().map(|c| (c, depth + 1)));
            }
        }
        for line in lines {
            self.note(line);
        }
    }

    fn pointer_move(&mut self, x: f32, y: f32) {
        let now = self.page.now;
        let used = self.page.panels().pointer_move(x, y, now);
        self.note(format!("move {x} {y} -> {used}"));
    }

    fn button(&mut self, button: u32, down: bool, x: f32, y: f32) {
        let now = self.page.now;
        let used =
            self.page
                .panels()
                .pointer_button(button, down, (x, y), Modifiers::default(), now);
        let what = if down { "down" } else { "up" };
        self.note(format!("{what} {button} {x} {y} -> {used}"));
    }

    fn key(&mut self, down: bool, code: &str) {
        let now = self.page.now;
        let used = self
            .page
            .panels()
            .key(down, code, Modifiers::default(), false, now);
        let what = if down { "down" } else { "up" };
        self.note(format!("key {what} {code} -> {used}"));
    }

    fn text(&mut self, text: &str) {
        let used = self.page.panels().text(text);
        self.note(format!("text {text:?} -> {used}"));
    }

    fn wheel(&mut self, lines: f32, x: f32, y: f32) {
        let used = self
            .page
            .panels()
            .wheel(lines, (x, y), Modifiers::default());
        self.note(format!("wheel {lines} {x} {y} -> {used}"));
    }

    /// One script line.
    fn step(&mut self, line: &str) {
        let line = line.split('#').next().unwrap_or("").trim();
        let mut parts = line.split_whitespace();
        let Some(verb) = parts.next() else {
            return;
        };
        let rest: Vec<&str> = parts.collect();
        let f = |i: usize| {
            rest.get(i)
                .and_then(|s| s.parse::<f32>().ok())
                .unwrap_or(0.0)
        };
        match verb {
            "move" => self.pointer_move(f(0), f(1)),
            "down" => self.button(f(0) as u32, true, f(1), f(2)),
            "up" => self.button(f(0) as u32, false, f(1), f(2)),
            "click" => {
                self.pointer_move(f(0), f(1));
                self.button(0, true, f(0), f(1));
                self.frame();
                self.button(0, false, f(0), f(1));
            }
            "wheel" => self.wheel(f(0), f(1), f(2)),
            "key" => match rest.as_slice() {
                ["down", code] => self.key(true, code),
                ["up", code] => self.key(false, code),
                [code] => {
                    self.key(true, code);
                    self.key(false, code);
                }
                _ => self.note(format!("script: cannot read {line:?}")),
            },
            "text" => self.text(line["text".len()..].trim_start()),
            "frames" => {
                for _ in 0..f(0) as usize {
                    self.frame();
                }
            }
            "wait" => self.wait(f64::from(f(0))),
            "until" => self.until(f64::from(f(0))),
            "control" => self.control(rest.first() == Some(&"on")),
            "png" => {
                let name = rest.first().copied().unwrap_or("script");
                self.png(name);
            }
            "probe" => self.probe(),
            "dump" => self.dump(rest.first().copied().unwrap_or("plugin:")),
            _ => self.note(format!("script: cannot read {line:?}")),
        }
    }

    /// The record written beside the PNGs.
    fn write_record(&self) -> PathBuf {
        let path = self.out.join(format!("{}.log", self.name));
        let mut text = String::new();
        for line in &self.record {
            let _ = writeln!(text, "{line}");
        }
        let _ = writeln!(text, "({} heartbeats not listed)", self.heartbeats);
        std::fs::write(&path, text).expect("the record written");
        path
    }
}

/// The kinds of the events in a stream frame (each event's name and, for
/// the interface's events, the inner one's), for the record.
fn stream_kinds(frame: &[u8]) -> String {
    let cx = ac_remote_wire::DecodeContext::new(ac_clock::Instant::now(), None, None);
    let Ok(events) = ac_remote_wire::decode_batch(frame, &cx) else {
        return "(not decoded here)".into();
    };
    let names: Vec<String> = events
        .iter()
        .map(|e| {
            let text = format!("{e:?}");
            let cut = |s: &str| {
                s.find(['(', ' ', '{'])
                    .map_or_else(|| s.to_owned(), |i| s[..i].to_owned())
            };
            let outer = cut(&text);
            match text.strip_prefix(&format!("{outer}(")) {
                Some(inner) if outer == "Interface" => format!("{outer}::{}", cut(inner)),
                _ => outer,
            }
        })
        .collect();
    names.join(", ")
}

/// What a plugin control shows, for finding it by its text.
fn element_text(root: &ac_ui::root::UiRoot, id: ac_ui::tree::ElementId) -> String {
    use ac_ui::plugin::MarkupField;
    use ac_ui::plugin::controls::MarkupCheckbox;
    use ac_ui::plugin::widgets::{MarkupButton, MarkupLabel, MarkupToggle};
    if let Some(w) = root.widget::<MarkupCheckbox>(id) {
        return format!("checkbox {:?} {:?}", w.text, w.state);
    }
    if let Some(w) = root.widget::<MarkupToggle>(id) {
        return format!("toggle {:?} {}", w.text, w.checked);
    }
    if let Some(w) = root.widget::<MarkupButton>(id) {
        return format!("button {:?} tab {:?}", w.text, w.tab_selected);
    }
    if let Some(w) = root.widget::<MarkupLabel>(id) {
        return format!("label {:?}", w.text);
    }
    if let Some(w) = root.widget::<MarkupField>(id) {
        return format!("field {:?}", w.inner.text());
    }
    String::new()
}

/// The captures named by the environment, or `None` (skipped).
fn captures() -> Option<Vec<PathBuf>> {
    let path = PathBuf::from(std::env::var_os("OPENAC_UI_CAPTURE")?);
    if path.is_dir() {
        let mut found: Vec<PathBuf> = std::fs::read_dir(&path)
            .expect("the capture folder")
            .filter_map(Result::ok)
            .map(|e| e.path())
            .filter(|p| p.extension().is_some_and(|e| e == "bin"))
            .collect();
        found.sort();
        Some(found)
    } else {
        Some(vec![path])
    }
}

fn out_dir() -> PathBuf {
    std::env::var_os("OPENAC_UI_REPLAY_OUT").map_or_else(
        || std::env::temp_dir().join("openac-ui-replay"),
        PathBuf::from,
    )
}

#[test]
#[ignore = "replays a private capture over the installed dats; see the file's documentation"]
fn a_capture_replays_and_its_last_picture_is_drawn() {
    let Some(captures) = captures() else {
        println!("skipped: OPENAC_UI_CAPTURE is not set (a capture .bin or a folder of them)");
        return;
    };
    let out = out_dir();
    std::fs::create_dir_all(&out).expect("the output folder");
    let control = std::env::var_os("OPENAC_UI_REPLAY_CONTROL").is_some_and(|v| v == "1");
    let script = std::env::var_os("OPENAC_UI_REPLAY_SCRIPT")
        .map(|p| std::fs::read_to_string(p).expect("the script"));
    for bin in captures {
        let capture = Capture::load(&bin);
        let mut replay = Replay::start(&capture, &out);
        if control {
            replay.control(true);
        }
        let script = script.as_deref().unwrap_or("");
        // The script starts at the end of the capture unless it says when.
        if !script.lines().any(|l| l.trim_start().starts_with("until")) {
            replay.play();
            replay.png("played");
            replay.probe();
        }
        for line in script.lines() {
            replay.step(line);
        }
        replay.play();
        let png = replay.png("end");
        let record = replay.write_record();
        println!(
            "{}: {} frames, {} pictures; {}; {}",
            capture.name,
            capture.frames.len(),
            replay.page.pictures,
            png.display(),
            record.display()
        );
        assert!(replay.page.pictures > 0, "a picture was drawn");
    }
}

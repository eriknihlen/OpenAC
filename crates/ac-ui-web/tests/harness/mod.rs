//! A page for the tests: the module driven as a browser page drives it,
//! over the installed dats. Every header and every file it asks for is
//! answered from the dat files (exactly what a dat reader returns for the
//! id, from the dat it named); what it hands back is kept as a page keeps
//! it: the textures it uploaded and has not released, the latest picture,
//! the command frames, the log and the pointer's shape.
//!
//! Needs the dats in `Documents\Asheron's Call` under the user's profile.

#![allow(dead_code)]

pub mod raster;

use std::collections::{BTreeMap, HashMap};

use ac_clock::Instant;
use ac_dat::{DatCollection, DatSource};
use ac_remote_wire::{DecodeContext, SessionCommand};
use ac_ui_web::client::ClientPanels;
use ac_ui_web::host::{Module, Panels};
use ac_ui_web::output::kind;
use ac_ui_web::page::{self, HEADER_BYTES, HEADER_ID};

pub fn words(bytes: &[u8]) -> Vec<u32> {
    bytes
        .chunks_exact(4)
        .map(|w| u32::from_le_bytes([w[0], w[1], w[2], w[3]]))
        .collect()
}

/// What the page answered, by dat: files found, their bytes, files not
/// found.
#[derive(Debug, Default, Clone, Copy)]
pub struct Answered {
    pub files: usize,
    pub bytes: u64,
    pub missing: usize,
}

/// A texture the page holds: its first level as straight RGBA (an A8
/// texture as white with that alpha).
#[derive(Debug, Clone)]
pub struct Held {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
    /// Levels the upload carried.
    pub levels: u32,
}

/// A page: the module, the installed dats it answers from, and what it
/// holds of the module's outputs.
pub struct Page {
    pub module: Module<ClientPanels>,
    pub dats: DatCollection,
    pub dir: std::path::PathBuf,
    /// The page hands in the high-resolution dat (when the install has one).
    pub high_res: bool,
    pub now: f64,
    pub answered: BTreeMap<u32, Answered>,
    /// The textures uploaded and not released, by kind and id.
    pub textures: HashMap<(u32, u32), Held>,
    /// The last picture: vertices (32 bytes each) and runs.
    pub vertices: Vec<u8>,
    pub runs: Vec<[u32; 5]>,
    /// Pictures received so far.
    pub pictures: usize,
    /// Command frames taken since the last look.
    pub commands: Vec<Vec<u8>>,
    /// The pointer's shape as last handed over.
    pub cursor: Option<[u32; 5]>,
    /// The dat requests of the last frame, as (source, id).
    pub last_requests: Vec<(u32, u32)>,
    pub log: String,
}

impl Page {
    pub fn open() -> Self {
        Self::with_high_res(true)
    }

    pub fn with_high_res(high_res: bool) -> Self {
        let dir = ac_dat::default_dat_dir().expect("a user profile");
        let dats = DatCollection::open(&dir).expect("the installed dats");
        Self {
            module: Module::new(ClientPanels::new()),
            dats,
            dir,
            high_res,
            now: 0.0,
            answered: BTreeMap::new(),
            textures: HashMap::new(),
            vertices: Vec::new(),
            runs: Vec::new(),
            pictures: 0,
            commands: Vec::new(),
            cursor: None,
            last_requests: Vec::new(),
            log: String::new(),
        }
    }

    /// The panels.
    pub fn panels(&mut self) -> &mut ClientPanels {
        self.module.panels()
    }

    /// The first bytes of dat `source`'s file, `None` when the install has
    /// none.
    pub fn header(&self, source: u32) -> Option<Vec<u8>> {
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

    pub fn answer(&mut self, source: u32, id: u32) {
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

    /// Hand in the headers before the canvas, as a page does with the
    /// preload list.
    pub fn preload(&mut self) {
        for (source, id) in self.module.panels().dat_preload() {
            self.answer(source, id);
        }
    }

    /// One frame at `self.now + step`: take everything, answer every
    /// request. Returns how many requests it made and whether a picture
    /// came.
    pub fn frame_after(&mut self, step: f64) -> (usize, bool) {
        self.now += step;
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
            self.pictures += 1;
            self.vertices = self.module.take(kind::VERTICES).to_vec();
            self.runs = words(self.module.take(kind::RUNS))
                .chunks_exact(5)
                .map(|r| [r[0], r[1], r[2], r[3], r[4]])
                .collect();
            for run in &self.runs {
                if run[0] != 0 {
                    assert!(
                        self.textures.contains_key(&(run[0], run[1])),
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
        let cursor = words(self.module.take(kind::CURSOR));
        if let [a, b, c, d, e] = cursor[..] {
            self.cursor = Some([a, b, c, d, e]);
        }
        for k in [
            kind::STORAGE_WRITES,
            kind::CLIPBOARD_COPY,
            kind::CLIPBOARD_PASTE,
        ] {
            self.module.take(k);
        }
        let requests = words(self.module.take(kind::DAT_REQUESTS));
        self.last_requests.clear();
        for pair in requests.chunks_exact(2) {
            self.last_requests.push((pair[0], pair[1]));
            self.answer(pair[0], pair[1]);
        }
        (requests.len() / 2, picture)
    }

    /// One frame 16 ms after the last.
    pub fn frame(&mut self) -> (usize, bool) {
        self.frame_after(16.0)
    }

    fn read_uploads(&mut self, bytes: &[u8]) {
        let mut at = 0;
        while at < bytes.len() {
            let head = words(&bytes[at..at + 24]);
            let (texture_kind, id, format, levels) = (head[0], head[1], head[2], head[5]);
            at += 24;
            let mut first = None;
            for _ in 0..levels {
                let size = words(&bytes[at..at + 8]);
                let (w, h) = (size[0], size[1]);
                let per_texel = if format == 0 { 4 } else { 1 };
                at += 8;
                let len = (w * h * per_texel) as usize;
                let texels = &bytes[at..at + len];
                at += len;
                if first.is_none() {
                    let rgba = if format == 0 {
                        texels.to_vec()
                    } else {
                        texels.iter().flat_map(|&a| [255, 255, 255, a]).collect()
                    };
                    first = Some(Held {
                        width: w,
                        height: h,
                        rgba,
                        levels,
                    });
                }
            }
            if let Some(held) = first {
                self.textures.insert((texture_kind, id), held);
            }
        }
        assert_eq!(at, bytes.len(), "uploads end on a record");
    }

    /// Frames until a frame makes no request and the panels are built;
    /// returns the frames run.
    pub fn settle(&mut self, limit: usize) -> usize {
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

    /// The commands sent since the last look, with the generation each was
    /// stamped with.
    pub fn sent_stamped(&mut self) -> Vec<(u64, SessionCommand)> {
        let cx = DecodeContext::new(Instant::now(), None, None);
        std::mem::take(&mut self.commands)
            .iter()
            .map(
                |frame| match ac_remote_wire::decode_command(frame, &cx).unwrap() {
                    SessionCommand::AtGeneration {
                        generation,
                        command,
                    } => (generation.0, *command),
                    other => panic!("not stamped: {other:?}"),
                },
            )
            .collect()
    }

    /// The last picture drawn over a dark fill, straight RGBA rows.
    pub fn render(&self, width: u32, height: u32) -> Vec<u8> {
        raster::render(
            width,
            height,
            &raster::vertices(&self.vertices),
            &self.runs,
            &self.textures,
        )
    }

    /// The last picture as a PNG at `path`.
    pub fn write_png(&self, width: u32, height: u32, path: &std::path::Path) {
        let rgba = self.render(width, height);
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir).expect("the output folder");
        }
        std::fs::write(path, ac_bits::png::encode_rgba(width, height, &rgba))
            .expect("the PNG written");
    }
}

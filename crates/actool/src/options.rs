//! Startup options: command-line flags and environment variables, parsed
//! into one struct. Every option is listed in `docs/launch-options.md`.

use std::ffi::OsString;
use std::path::PathBuf;

/// Environment variable naming the dat directory.
pub const DAT_DIR_ENV: &str = "OPENAC_DAT_DIR";

/// What to run.
#[derive(Debug, Clone, PartialEq)]
pub enum Command {
    /// Print usage.
    Help,
    /// Header fields and entry counts of every dat.
    DatInfo,
    /// A landblock's terrain and, when present, its objects.
    DatLandblock(u32),
    /// A setup's fields.
    DatSetup(u32),
    /// A motion table's stances, cycles, modifiers and links.
    DatMotionTable(u32),
    /// An interior cell and the shape it uses.
    DatEnvCell(u32),
    /// A region's land, calendar, sky, sound, scenery and terrain.
    DatRegion(u32),
    /// A model's fields, polygons and BSP trees.
    DatGfxObj(u32),
    /// One line per UI layout in the local dat.
    DatLayoutIndex,
    /// A UI layout's element tree: geometry, states, media, properties.
    DatLayout(u32),
    /// A font's metrics and glyphs; with an output base, its atlases and a
    /// sample line as PNG files.
    DatFont {
        /// Font id.
        id: u32,
        /// Base path of the PNG files to write (`<base>-fg.png`, ...).
        out: Option<PathBuf>,
    },
    /// A render surface's edge columns as RGB hex.
    DatEdges(u32),
    /// Compare two screenshots (PNG); write a JSON report.
    ImageCompare {
        /// The reference.
        expected: PathBuf,
        /// The screenshot under test.
        actual: PathBuf,
        /// The report.
        out: PathBuf,
        /// Channel tolerance.
        tolerance: u8,
        /// Allowed fraction of differing pixels.
        max_fraction: f64,
        /// Pixels with alpha in this PNG are not compared.
        mask: Option<PathBuf>,
    },
    /// Crop and zoom a PNG.
    ImageCrop {
        /// Input.
        input: PathBuf,
        /// Region and zoom: x, y, w, h, zoom.
        region: [i64; 5],
        /// Output PNG.
        out: PathBuf,
    },
    /// Print a block of a PNG's pixels.
    ImageProbe {
        /// Input.
        input: PathBuf,
        /// x0, y0, x1, y1.
        block: [i64; 4],
    },
    /// Summarise a frame history around checkpoints and portal arrivals.
    FramesSummarize {
        /// The frame history CSV.
        frames: PathBuf,
        /// Checkpoints, one JSON object per line.
        checkpoints: PathBuf,
        /// A log with `materialized destination='...'` lines.
        markers: PathBuf,
        /// The JSON report.
        out: PathBuf,
    },
    /// A texture decoded to a PNG file.
    DatTexture {
        /// Render surface, surface texture or surface id.
        id: u32,
        /// PNG file to write.
        out: PathBuf,
    },
    /// Render surfaces side by side, zoomed, in one PNG.
    DatSprites {
        /// Render surface ids, left to right.
        ids: Vec<u32>,
        /// PNG file to write.
        out: PathBuf,
    },
    /// The vitals window's three meters and their sprites.
    DatVitalsBars,
    /// Every layout holding a vitals meter.
    DatVitalsLayouts,
    /// The selected-object health bar at five fills, in one PNG.
    DatMockSelbar {
        /// PNG file to write.
        out: PathBuf,
    },
}

/// Everything the tool needs to run.
#[derive(Debug, Clone, PartialEq)]
pub struct Options {
    /// Directory holding the dat files.
    pub dat_dir: PathBuf,
    /// What to run.
    pub command: Command,
}

/// Usage text.
pub const USAGE: &str = "\
usage: actool [--dat-dir <path>] <command>

commands:
  dat info                 header fields and entry counts of every dat
  dat landblock <hex id>   a landblock (e.g. A9B4FFFF) and its objects
  dat setup <hex id>       a setup (e.g. 02000001)
  dat motiontable <hex id> a motion table (e.g. 09000001)
  dat envcell <hex id>     an interior cell (e.g. A9B40100) and its shape
  dat region <hex id>      a region (e.g. 13000000)
  dat gfxobj <hex id>      a model (e.g. 01000C1E): vertices, polygons, BSP trees
  dat layout [<hex id>]    every UI layout, one line each; with an id (e.g.
                           21000006) the element tree with states and properties
  dat font <hex id> [--out <base>]
                           a font (e.g. 40000000): metrics and glyphs; --out
                           writes <base>-fg.png, <base>-bg.png, <base>-sample-6x.png
  dat texture <hex id> --out <file.png>
                           decode a texture to PNG: a render surface (06...),
                           a surface texture (05...) or a surface (08...)
  dat edges <hex id>       a render surface's edge columns as RGB hex
  dat sprites <hex id,hex id,...> --out <file.png>
                           render surfaces side by side on a grey strip, 10x
  dat vitals-bars          the vitals meters (health, stamina, mana) and their sprites
  dat vitals-layouts       every UI layout holding a vitals meter
  dat mock-selbar --out <file.png>
                           the selected-object health bar at 100, 90, 70, 50 and 0 %, 8x
  image compare <expected.png> <actual.png> <out.json> [tolerance] [fraction] [mask.png]
                           compare two screenshots; JSON report, exit 1 on failure
  image crop <in.png> <x> <y> <w> <h> <zoom> <out.png>
                           a region of a PNG, zoomed
  image probe <in.png> <x0> <y0> <x1> <y1>
                           a block of pixels as RGB hex
  frames summarize <history.csv> <checkpoints.jsonl> <markers.log> <out.json>
                           summarise a frame history (OPENAC_FRAME_HISTORY)
  migrate-classic [--vtank <dir>] [--ub <dir>] [--root <openac-root>] [--overwrite] [--dry-run]
                           copy a classic VirindiTank and UtilityBelt setup to
                           where MossTank reads it (<root>/vtank/mosstank)

options:
  --dat-dir <path>   dat directory; else $OPENAC_DAT_DIR; else
                     Documents\\Asheron's Call under the user profile
  --out <path>       output file of `dat texture`, `dat sprites` and
                     `dat mock-selbar`, file base of `dat font`
  -h, --help         this text";

impl Options {
    /// Parse the arguments (without the program name), reading the
    /// environment through `env` so tests can supply their own.
    pub fn parse(
        args: impl IntoIterator<Item = OsString>,
        env: impl Fn(&str) -> Option<OsString>,
    ) -> Result<Self, String> {
        let mut dat_dir = None;
        let mut out = None;
        let mut words = Vec::new();
        let mut args = args.into_iter();
        while let Some(arg) = args.next() {
            match arg.to_str() {
                Some("--dat-dir") => {
                    let value = args.next().ok_or("--dat-dir needs a path")?;
                    dat_dir = Some(PathBuf::from(value));
                }
                Some("--out") => {
                    let value = args.next().ok_or("--out needs a path")?;
                    out = Some(PathBuf::from(value));
                }
                Some("-h" | "--help") => {
                    words.clear();
                    words.push("help".to_owned());
                    break;
                }
                Some(flag) if flag.starts_with('-') => {
                    return Err(format!("unknown option {flag}"));
                }
                Some(word) => words.push(word.to_owned()),
                None => return Err(format!("argument is not valid text: {arg:?}")),
            }
        }

        let command = match words.iter().map(String::as_str).collect::<Vec<_>>()[..] {
            [] | ["help"] => Command::Help,
            ["dat", "info"] => Command::DatInfo,
            ["dat", "landblock", id] => {
                let id = parse_hex_id(id)?;
                if id & 0xFFFF != 0xFFFF {
                    return Err(format!(
                        "{id:08X} is not a landblock id (they end in FFFF, e.g. A9B4FFFF)"
                    ));
                }
                Command::DatLandblock(id)
            }
            ["dat", "setup", id] => Command::DatSetup(parse_hex_id(id)?),
            ["dat", "motiontable", id] => Command::DatMotionTable(parse_hex_id(id)?),
            ["dat", "envcell", id] => {
                let id = parse_hex_id(id)?;
                if !(0x0100..=0xFFFD).contains(&(id & 0xFFFF)) {
                    return Err(format!(
                        "{id:08X} is not an interior cell id (the low 16 bits run from 0100 to FFFD, e.g. A9B40100)"
                    ));
                }
                Command::DatEnvCell(id)
            }
            ["dat", "region", id] => Command::DatRegion(parse_hex_id(id)?),
            ["dat", "gfxobj", id] => Command::DatGfxObj(parse_hex_id(id)?),
            ["dat", "layout"] => Command::DatLayoutIndex,
            ["dat", "layout", id] => Command::DatLayout(parse_hex_id(id)?),
            ["dat", "font", id] => Command::DatFont {
                id: parse_hex_id(id)?,
                out: out.take(),
            },
            ["dat", "edges", id] => Command::DatEdges(parse_hex_id(id)?),
            ["dat", "vitals-bars"] => Command::DatVitalsBars,
            ["dat", "vitals-layouts"] => Command::DatVitalsLayouts,
            ["dat", "mock-selbar"] => Command::DatMockSelbar {
                out: out.take().ok_or("dat mock-selbar needs --out <file.png>")?,
            },
            ["image", "compare", expected, actual, report, ref rest @ ..] if rest.len() <= 3 => {
                Command::ImageCompare {
                    expected: PathBuf::from(expected),
                    actual: PathBuf::from(actual),
                    out: PathBuf::from(report),
                    tolerance: rest
                        .first()
                        .map_or(Ok(2), |t| t.parse::<u8>())
                        .map_err(|_| "the tolerance is 0 to 255".to_owned())?,
                    max_fraction: rest
                        .get(1)
                        .map_or(Ok(0.001), |t| t.parse::<f64>())
                        .map_err(|_| "the allowed fraction is a number".to_owned())?,
                    mask: rest.get(2).map(PathBuf::from),
                }
            }
            ["image", "crop", input, x, y, w, h, zoom, output] => Command::ImageCrop {
                input: PathBuf::from(input),
                region: [
                    parse_int(x)?,
                    parse_int(y)?,
                    parse_int(w)?,
                    parse_int(h)?,
                    parse_int(zoom)?,
                ],
                out: PathBuf::from(output),
            },
            ["image", "probe", input, x0, y0, x1, y1] => Command::ImageProbe {
                input: PathBuf::from(input),
                block: [
                    parse_int(x0)?,
                    parse_int(y0)?,
                    parse_int(x1)?,
                    parse_int(y1)?,
                ],
            },
            ["frames", "summarize", frames, checkpoints, markers, report] => {
                Command::FramesSummarize {
                    frames: PathBuf::from(frames),
                    checkpoints: PathBuf::from(checkpoints),
                    markers: PathBuf::from(markers),
                    out: PathBuf::from(report),
                }
            }
            ["dat", "texture", id] => Command::DatTexture {
                id: parse_hex_id(id)?,
                out: out.take().ok_or("dat texture needs --out <file.png>")?,
            },
            ["dat", "sprites", ids] => Command::DatSprites {
                ids: ids
                    .split(',')
                    .map(str::trim)
                    .filter(|s| !s.is_empty())
                    .map(parse_hex_id)
                    .collect::<Result<Vec<_>, _>>()?,
                out: out.take().ok_or("dat sprites needs --out <file.png>")?,
            },
            _ => return Err(format!("unknown command: {}", words.join(" "))),
        };
        if out.is_some() && command != Command::Help {
            return Err(
                "--out only applies to dat texture, dat sprites, dat mock-selbar and dat font"
                    .to_owned(),
            );
        }

        let needs_dats = !matches!(
            command,
            Command::ImageCompare { .. }
                | Command::ImageCrop { .. }
                | Command::ImageProbe { .. }
                | Command::FramesSummarize { .. }
        );
        let dat_dir = match dat_dir.or_else(|| env(DAT_DIR_ENV).map(PathBuf::from)) {
            Some(dir) => dir,
            None if !needs_dats => PathBuf::new(),
            None => ac_dat::default_dat_dir()
                .ok_or("no dat directory: pass --dat-dir or set OPENAC_DAT_DIR")?,
        };
        Ok(Self { dat_dir, command })
    }
}

/// A whole number.
fn parse_int(text: &str) -> Result<i64, String> {
    text.parse::<i64>()
        .map_err(|_| format!("not a whole number: {text}"))
}

/// A file id in hex, with or without a `0x` prefix.
fn parse_hex_id(text: &str) -> Result<u32, String> {
    let digits = text
        .strip_prefix("0x")
        .or_else(|| text.strip_prefix("0X"))
        .unwrap_or(text);
    if digits.is_empty() || digits.len() > 8 {
        return Err(format!("not a hex id: {text}"));
    }
    u32::from_str_radix(digits, 16).map_err(|_| format!("not a hex id: {text}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(args: &[&str], env_dir: Option<&str>) -> Result<Options, String> {
        Options::parse(args.iter().map(OsString::from), |name| {
            assert_eq!(name, DAT_DIR_ENV);
            env_dir.map(OsString::from)
        })
    }

    #[test]
    fn commands() {
        assert_eq!(
            parse(&["dat", "info"], Some("d")).unwrap().command,
            Command::DatInfo
        );
        assert_eq!(
            parse(&["dat", "landblock", "A9B4FFFF"], Some("d"))
                .unwrap()
                .command,
            Command::DatLandblock(0xA9B4_FFFF)
        );
        assert_eq!(
            parse(&["dat", "setup", "0x02000001"], Some("d"))
                .unwrap()
                .command,
            Command::DatSetup(0x0200_0001)
        );
        assert_eq!(
            parse(&["dat", "motiontable", "09000001"], Some("d"))
                .unwrap()
                .command,
            Command::DatMotionTable(0x0900_0001)
        );
        assert_eq!(
            parse(&["dat", "envcell", "A9B40100"], Some("d"))
                .unwrap()
                .command,
            Command::DatEnvCell(0xA9B4_0100)
        );
        assert_eq!(
            parse(&["dat", "region", "13000000"], Some("d"))
                .unwrap()
                .command,
            Command::DatRegion(0x1300_0000)
        );
        assert_eq!(parse(&[], Some("d")).unwrap().command, Command::Help);
        assert_eq!(
            parse(&["dat", "info", "--help"], Some("d"))
                .unwrap()
                .command,
            Command::Help
        );
    }

    #[test]
    fn model_and_texture_commands() {
        assert_eq!(
            parse(&["dat", "gfxobj", "01000C1E"], Some("d"))
                .unwrap()
                .command,
            Command::DatGfxObj(0x0100_0C1E)
        );
        assert_eq!(
            parse(&["dat", "texture", "0600378B", "--out", "t.png"], Some("d"))
                .unwrap()
                .command,
            Command::DatTexture {
                id: 0x0600_378B,
                out: PathBuf::from("t.png")
            }
        );
        assert_eq!(
            parse(&["--out", "t.png", "dat", "texture", "08000001"], Some("d"))
                .unwrap()
                .command,
            Command::DatTexture {
                id: 0x0800_0001,
                out: PathBuf::from("t.png")
            }
        );
        assert!(parse(&["dat", "texture", "0600378B"], Some("d")).is_err());
        assert!(parse(&["dat", "texture", "0600378B", "--out"], Some("d")).is_err());
        assert!(parse(&["dat", "gfxobj", "01000001", "--out", "x"], Some("d")).is_err());
        assert_eq!(
            parse(&["dat", "texture", "1", "--out", "x", "--help"], Some("d"))
                .unwrap()
                .command,
            Command::Help
        );
    }

    #[test]
    fn ui_commands() {
        assert_eq!(
            parse(&["dat", "layout"], Some("d")).unwrap().command,
            Command::DatLayoutIndex
        );
        assert_eq!(
            parse(&["dat", "layout", "21000006"], Some("d"))
                .unwrap()
                .command,
            Command::DatLayout(0x2100_0006)
        );
        assert_eq!(
            parse(&["dat", "font", "40000000"], Some("d"))
                .unwrap()
                .command,
            Command::DatFont {
                id: 0x4000_0000,
                out: None
            }
        );
        assert_eq!(
            parse(&["dat", "font", "40000000", "--out", "f"], Some("d"))
                .unwrap()
                .command,
            Command::DatFont {
                id: 0x4000_0000,
                out: Some(PathBuf::from("f"))
            }
        );
        assert!(parse(&["dat", "layout", "x"], Some("d")).is_err());
        assert!(parse(&["dat", "layout", "21000006", "--out", "f"], Some("d")).is_err());
    }

    #[test]
    fn dat_dir_flag_beats_env() {
        let o = parse(&["--dat-dir", "flag", "dat", "info"], Some("env")).unwrap();
        assert_eq!(o.dat_dir, PathBuf::from("flag"));
        let o = parse(&["dat", "info", "--dat-dir", "flag"], Some("env")).unwrap();
        assert_eq!(o.dat_dir, PathBuf::from("flag"));
        let o = parse(&["dat", "info"], Some("env")).unwrap();
        assert_eq!(o.dat_dir, PathBuf::from("env"));
    }

    #[test]
    fn bad_input_is_reported() {
        assert!(parse(&["dat", "landblock", "A9B40100"], Some("d")).is_err());
        assert!(parse(&["dat", "setup", "xyz"], Some("d")).is_err());
        assert!(parse(&["dat", "setup", "123456789"], Some("d")).is_err());
        assert!(parse(&["dat", "setup"], Some("d")).is_err());
        assert!(parse(&["dat", "envcell", "A9B4FFFF"], Some("d")).is_err());
        assert!(parse(&["dat", "envcell", "A9B400FF"], Some("d")).is_err());
        assert!(parse(&["dat", "motiontable"], Some("d")).is_err());
        assert!(parse(&["dat", "region", "x"], Some("d")).is_err());
        assert!(parse(&["dat", "info", "--verbose"], Some("d")).is_err());
        assert!(parse(&["--dat-dir"], Some("d")).is_err());
        assert!(parse(&["frobnicate"], Some("d")).is_err());
    }
}

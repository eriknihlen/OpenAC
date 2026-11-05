//! The UI commands: `dat layout` and `dat font`.

use std::collections::BTreeMap;
use std::error::Error;
use std::path::Path;

use ac_bits::png;
use ac_dat::DatCollection;
use ac_dat::objects::{
    ElementDesc, Font, FontCharDesc, LayoutDesc, MASTER_PROPERTY_ID, MasterProperty, Media,
    Property, PropertyValue, RenderSurface, StateDesc, ui_state_name,
};
use ac_dat::texture::{self, DecodeOptions, DecodedTexture};

use crate::dat::f;

type Result = core::result::Result<(), Box<dyn Error>>;

fn master(dats: &DatCollection) -> core::result::Result<MasterProperty, Box<dyn Error>> {
    Ok(dats
        .get(MASTER_PROPERTY_ID)?
        .ok_or("no master property table 39000001 in the portal dat")?)
}

/// Short name of an element type, as OpenAC's layout index prints it.
fn type_name(t: u32) -> String {
    match t {
        0 => "Text0".to_owned(),
        1 => "Button".to_owned(),
        2 => "Dragbar".to_owned(),
        3 => "Field".to_owned(),
        5 => "ListBox".to_owned(),
        6 => "Menu".to_owned(),
        7 => "Meter".to_owned(),
        8 => "Panel".to_owned(),
        9 => "Resizebar".to_owned(),
        0xB => "Scrollbar".to_owned(),
        0xC => "Text".to_owned(),
        0xD => "Viewport".to_owned(),
        0xE => "Browser".to_owned(),
        0x10 => "ColorPicker".to_owned(),
        0x11 => "GroupBox".to_owned(),
        0x12 => "Proto".to_owned(),
        0x1000_0041 => "gmMainChatUI".to_owned(),
        0x1000_0040 => "gmFloatyChatUI".to_owned(),
        0x1000_0050 => "gmFloatyMainChatUI".to_owned(),
        0x1000_0042 => "gmChatOptionsUI".to_owned(),
        0x1000_0009 => "gmVitalsUI".to_owned(),
        _ => format!("0x{t:X}"),
    }
}

fn count_types(e: &ElementDesc, histogram: &mut BTreeMap<u32, usize>) -> usize {
    *histogram.entry(e.element_type).or_insert(0) += 1;
    1 + e
        .children
        .iter()
        .map(|(_, c)| count_types(c, histogram))
        .sum::<usize>()
}

/// The layout's root: its single top element, or the largest of several.
fn root(l: &LayoutDesc) -> Option<&ElementDesc> {
    let mut best: Option<&ElementDesc> = None;
    for (_, e) in &l.elements {
        let area = |e: &ElementDesc| u64::from(e.width as u32) * u64::from(e.height as u32);
        if best.is_none_or(|b| area(e) > area(b)) {
            best = Some(e);
        }
    }
    best
}

fn index_line(id: u32, l: &LayoutDesc) -> Option<String> {
    let root = root(l)?;
    let mut histogram = BTreeMap::new();
    let count = count_types(root, &mut histogram);
    let h: Vec<String> = histogram
        .iter()
        .map(|(t, n)| format!("{}x{n}", type_name(*t)))
        .collect();
    Some(format!(
        "  0x{id:08X}  root=0x{:08X} type=0x{:08X}({})  {}x{}  n={count}  [{}]",
        root.element_id,
        root.element_type,
        type_name(root.element_type),
        root.width,
        root.height,
        h.join(" ")
    ))
}

/// `actool dat layout`: one line per layout in the local dat.
pub fn layout_index(dir: &Path) -> Result {
    let dats = DatCollection::open(dir)?;
    let master = master(&dats)?;
    println!(
        "=== All LayoutDescs (id : root element type : size : #elements : type histogram) ==="
    );
    let mut total = 0;
    for entry in dats.local.entries() {
        let entry = entry?;
        if entry.id >> 24 != 0x21 {
            continue;
        }
        let Some(l) = LayoutDesc::read(&dats.local, &master, entry.id)? else {
            continue;
        };
        total += 1;
        if let Some(line) = index_line(entry.id, &l) {
            println!("{line}");
        }
    }
    println!();
    println!("{total} LayoutDescs.");
    Ok(())
}

/// `actool dat layout <id>`: the index line, then every element with its
/// geometry, states, media and properties.
pub fn layout(dir: &Path, id: u32) -> Result {
    let dats = DatCollection::open(dir)?;
    let master = master(&dats)?;
    let l = LayoutDesc::read(&dats.local, &master, id)?
        .ok_or_else(|| format!("no LayoutDesc {id:08X} in the local dat"))?;
    println!("== LayoutDesc {:#010X}  {}x{}", l.id, l.width, l.height);
    if let Some(line) = index_line(id, &l) {
        println!("{line}");
    }
    for (_, e) in &l.elements {
        print_element(e, 1);
    }
    Ok(())
}

fn print_element(e: &ElementDesc, depth: usize) {
    let pad = "  ".repeat(depth);
    let base = if e.base_element != 0 {
        format!(" base=0x{:08X}/0x{:08X}", e.base_element, e.base_layout_id)
    } else {
        String::new()
    };
    println!(
        "{pad}0x{:08X} {} @{},{} {}x{} z={} order={} edges={}{}{}{} default={}{base}",
        e.element_id,
        type_name(e.element_type),
        e.x,
        e.y,
        e.width,
        e.height,
        e.z_level,
        e.read_order,
        e.left_edge,
        e.top_edge,
        e.right_edge,
        e.bottom_edge,
        ui_state_name(e.default_state),
    );
    print_state(&pad, "(direct)", &e.state_desc);
    for (k, s) in &e.states {
        print_state(&pad, &ui_state_name(*k), s);
    }
    for (_, c) in &e.children {
        print_element(c, depth + 1);
    }
}

fn print_state(pad: &str, name: &str, s: &StateDesc) {
    if s.properties.is_empty() && s.media.is_empty() && !s.pass_to_children {
        return;
    }
    let pass = if s.pass_to_children {
        " pass-to-children"
    } else {
        ""
    };
    println!(
        "{pad}  state {name} flags={:#X}{pass}",
        s.incorporation_flags.0
    );
    for m in &s.media {
        println!("{pad}    media {}", media(&m.media));
    }
    for (k, p) in &s.properties {
        println!("{pad}    prop 0x{k:X} = {}", property(p));
    }
}

fn media(m: &Media) -> String {
    match m {
        Media::Movie {
            file_name,
            stretch_to_full_screen,
        } => format!("movie {file_name:?} stretch={stretch_to_full_screen}"),
        Media::Alpha { file } => format!("alpha 0x{file:08X}"),
        Media::Animation {
            duration,
            draw_mode,
            frames,
        } => format!(
            "animation {}s mode={} frames {:08X?}",
            f(*duration),
            draw_mode.0,
            frames
        ),
        Media::Cursor {
            file,
            x_hotspot,
            y_hotspot,
        } => format!("cursor 0x{file:08X} hotspot {x_hotspot},{y_hotspot}"),
        Media::Image { file, draw_mode } => format!("image 0x{file:08X} mode={}", draw_mode.0),
        Media::Jump {
            jump_item_index,
            probability,
        } => format!("jump {jump_item_index} p={}", f(*probability)),
        Media::Message { id, probability } => format!("message 0x{id:X} p={}", f(*probability)),
        Media::Pause {
            min_duration,
            max_duration,
        } => format!("pause {}..{}s", f(*min_duration), f(*max_duration)),
        Media::Sound { file, sound } => format!("sound 0x{file:08X} type={sound}"),
        Media::State {
            state_id,
            probability,
        } => format!("state {} p={}", ui_state_name(*state_id), f(*probability)),
        Media::Fade {
            start_alpha,
            end_alpha,
            duration,
        } => format!(
            "fade {}->{} over {}s",
            f(*start_alpha),
            f(*end_alpha),
            f(*duration)
        ),
    }
}

fn property(p: &Property) -> String {
    match &p.value {
        PropertyValue::Enum(v) => format!("enum {v}"),
        PropertyValue::Bool(v) => format!("bool {v}"),
        PropertyValue::DataId(v) => format!("did 0x{v:08X}"),
        PropertyValue::Float(v) => format!("float {}", f(*v)),
        PropertyValue::Integer(v) => format!("int {v}"),
        PropertyValue::StringInfo(s) => format!(
            "string table=0x{:08X} id=0x{:08X} token={}",
            s.table_id, s.string_id, s.token
        ),
        PropertyValue::Color(c) => format!(
            "color {:02X}{:02X}{:02X}{:02X} (ARGB)",
            c.alpha, c.red, c.green, c.blue
        ),
        PropertyValue::Array(items) => {
            let items: Vec<String> = items.iter().map(property).collect();
            format!("[{}]", items.join(", "))
        }
        PropertyValue::Struct(members) => {
            let members: Vec<String> = members
                .iter()
                .map(|(k, v)| format!("0x{k:X}: {}", property(v)))
                .collect();
            format!("{{{}}}", members.join(", "))
        }
        PropertyValue::Vector(v) => format!("vector {} {} {}", f(v.x), f(v.y), f(v.z)),
        PropertyValue::Bitfield32(v) => format!("bits32 {v:#010X}"),
        PropertyValue::Bitfield64(v) => format!("bits64 {v:#018X}"),
        PropertyValue::InstanceId(v) => format!("iid 0x{v:08X}"),
    }
}

/// Sample text drawn by `dat font --out`.
const SAMPLE: &str = "Chat  Send  12345  ghpqy";

/// `actool dat font <id> [--out <base>]`: metrics and glyphs; with `--out`,
/// the atlases (alpha as grey, 4x) and a 6x sample line drawn the way the UI
/// draws text (outline pass from the background atlas, then the fill).
pub fn font(dir: &Path, id: u32, out: Option<&Path>) -> Result {
    let dats = DatCollection::open(dir)?;
    let font: Font = dats
        .get(id)?
        .ok_or_else(|| format!("no Font {id:08X} in the portal dat"))?;
    println!(
        "Font {:#010X}: fg=0x{:08X} bg=0x{:08X} max height {} max width {} baseline {} border {},{} glyphs {}",
        font.id,
        font.foreground_surface_data_id,
        font.background_surface_data_id,
        font.max_char_height,
        font.max_char_width,
        font.baseline_offset,
        font.num_horizontal_border_pixels,
        font.num_vertical_border_pixels,
        font.char_descs.len()
    );
    for g in &font.char_descs {
        let c = char::from_u32(u32::from(g.unicode)).filter(|c| !c.is_control());
        println!(
            "  U+{:04X} {:?} atlas {},{} {}x{} before {} after {} down {} advance {}",
            g.unicode,
            c.unwrap_or('?'),
            g.offset_x,
            g.offset_y,
            g.width,
            g.height,
            g.horizontal_offset_before,
            g.horizontal_offset_after,
            g.vertical_offset_before,
            g.advance()
        );
    }
    let Some(base) = out else {
        return Ok(());
    };
    let fg = decode(&dats, font.foreground_surface_data_id)?;
    let bg = if font.background_surface_data_id != 0 {
        Some(decode(&dats, font.background_surface_data_id)?)
    } else {
        None
    };
    println!(
        "  fg atlas {}x{}{}",
        fg.width,
        fg.height,
        bg.as_ref().map_or(" (no bg atlas)".to_owned(), |b| format!(
            "   bg atlas {}x{}",
            b.width, b.height
        ))
    );
    write(&with_suffix(base, "-fg.png"), &alpha_as_grey(&fg, 4))?;
    if let Some(bg) = &bg {
        write(&with_suffix(base, "-bg.png"), &alpha_as_grey(bg, 4))?;
    }
    let sample = render_sample(&font, &fg, bg.as_ref());
    write(&with_suffix(base, "-sample-6x.png"), &scale(&sample, 6))?;
    Ok(())
}

/// The gap around and between the sprites of a sheet, pixels before the
/// zoom.
const SHEET_PAD: u32 = 6;
/// A sheet's zoom.
const SHEET_ZOOM: u32 = 10;
/// A sheet's background.
const SHEET_BACKGROUND: [u8; 4] = [64, 64, 72, 255];

/// The vitals window's meters: health, stamina, mana.
const VITALS_METERS: [(u32, &str); 3] = [
    (0x1000_00E6, "HEALTH"),
    (0x1000_00EC, "STAMINA"),
    (0x1000_00EE, "MANA"),
];

fn find_element(e: &ElementDesc, id: u32) -> Option<&ElementDesc> {
    if e.element_id == id {
        return Some(e);
    }
    e.children.iter().find_map(|(_, c)| find_element(c, id))
}

fn layout_element(l: &LayoutDesc, id: u32) -> Option<&ElementDesc> {
    l.elements.iter().find_map(|(_, e)| find_element(e, id))
}

/// Every layout of the local dat, in id order.
fn layouts(dats: &DatCollection) -> core::result::Result<Vec<(u32, LayoutDesc)>, Box<dyn Error>> {
    let master = master(dats)?;
    let mut all = Vec::new();
    for entry in dats.local.entries() {
        let entry = entry?;
        if entry.id >> 24 != 0x21 {
            continue;
        }
        if let Some(l) = LayoutDesc::read(&dats.local, &master, entry.id)? {
            all.push((entry.id, l));
        }
    }
    Ok(all)
}

/// The images of `e` and its subtree: (role, render surface, draw mode),
/// the direct state first, then each named state, children indented.
fn element_sprites(e: &ElementDesc, depth: usize, out: &mut Vec<(String, u32, String)>) {
    let indent = "  ".repeat(depth);
    let mut state = |name: &str, s: &StateDesc| {
        for m in &s.media {
            if let Media::Image { file, draw_mode } = &m.media
                && *file != 0
            {
                out.push((
                    format!("{indent}elem_0x{:08X}.{name}", e.element_id),
                    *file,
                    draw_mode_name(*draw_mode).to_owned(),
                ));
            }
        }
    };
    state("DirectState", &e.state_desc);
    for (k, s) in &e.states {
        state(&ui_state_name(*k), s);
    }
    for (_, c) in &e.children {
        element_sprites(c, depth + 1, out);
    }
}

fn draw_mode_name(mode: ac_dat::objects::DrawModeType) -> &'static str {
    match mode.0 {
        0 => "Undefined",
        1 => "Normal",
        2 => "Overlay",
        3 => "Alphablend",
        _ => "?",
    }
}

/// `dat vitals-bars`: the first layout holding the health meter, then each
/// meter's images (OpenAC `AcDream.Cli dump-vitals-bars`).
pub fn vitals_bars(dir: &Path) -> Result {
    let dats = DatCollection::open(dir)?;
    println!("Scanning LayoutDescs for vitals window (element 0x100000E6 = Health meter)...");
    let all = layouts(&dats)?;
    let Some((id, layout)) = all
        .iter()
        .find(|(_, l)| layout_element(l, VITALS_METERS[0].0).is_some())
    else {
        return Err("no LayoutDesc contains element 0x100000E6 (Health meter)".into());
    };
    println!("Found vitals layout: 0x{id:08X}");
    println!();
    for (element, name) in VITALS_METERS {
        println!("{name} meter (element 0x{element:08X}) in layout 0x{id:08X}:");
        let Some(meter) = layout_element(layout, element) else {
            println!("  <element not found>");
            continue;
        };
        let mut sprites = Vec::new();
        element_sprites(meter, 0, &mut sprites);
        if sprites.is_empty() {
            println!("  <no sprites found in sub-tree>");
        }
        for (role, file, mode) in sprites {
            println!("  {role:<35} 0x{file:08X}  ({mode})");
        }
        println!();
    }
    Ok(())
}

/// `dat vitals-layouts`: every layout holding any vitals meter, with its
/// size (the search OpenAC's `dump-vitals-layout` prints before its dump;
/// the dump itself is `dat layout <id>`).
pub fn vitals_layouts(dir: &Path) -> Result {
    let dats = DatCollection::open(dir)?;
    println!("=== LayoutDescs containing a vitals meter element (0x100000E6/EC/EE) ===");
    for (id, l) in layouts(&dats)? {
        if VITALS_METERS
            .iter()
            .any(|(e, _)| layout_element(&l, *e).is_some())
        {
            println!("  0x{id:08X}  {}x{}", l.width, l.height);
        }
    }
    Ok(())
}

/// The selected-object health bar's back and fill images.
const SELBAR_BACK: u32 = 0x0600_193E;
const SELBAR_FILL: u32 = 0x0600_193F;
/// The bar element's width, pixels.
const SELBAR_WIDTH: u32 = 140;
/// Pixels between the rows.
const SELBAR_GAP: u32 = 4;
/// The fills drawn, top to bottom.
const SELBAR_FILLS: [f32; 5] = [1.0, 0.9, 0.7, 0.5, 0.0];
/// The canvas behind the rows.
const SELBAR_BACKGROUND: [u8; 4] = [20, 20, 24, 255];
/// The zoom of the written picture.
const SELBAR_ZOOM: u32 = 8;

/// `dat mock-selbar`: the selected-object health bar at five fills, one
/// row each (OpenAC `AcDream.Cli mock-selbar`).
pub fn mock_selbar(dir: &Path, out: &Path) -> Result {
    let dats = DatCollection::open(dir)?;
    let back = decode(&dats, SELBAR_BACK)?;
    let fill = decode(&dats, SELBAR_FILL)?;
    write(out, &selbar(&back, &fill))?;
    println!("selbar composite, rows = health 1.0 / 0.9 / 0.7 / 0.5 / 0.0");
    Ok(())
}

/// The composite: per fill a row of the back image cropped to the bar's
/// width, the fill image cropped to the filled part over it, blended over
/// the dark canvas, the whole zoomed 8 times.
fn selbar(back: &DecodedTexture, fill: &DecodedTexture) -> DecodedTexture {
    let height = back.height.min(fill.height);
    let row = height + SELBAR_GAP;
    let mut canvas = DecodedTexture {
        width: SELBAR_WIDTH,
        height: row * SELBAR_FILLS.len() as u32,
        rgba: SELBAR_BACKGROUND.repeat((SELBAR_WIDTH * row * SELBAR_FILLS.len() as u32) as usize),
    };
    for (i, fraction) in SELBAR_FILLS.iter().enumerate() {
        let y = i as u32 * row;
        over(&mut canvas, back, SELBAR_WIDTH.min(back.width), height, y);
        let filled = (SELBAR_WIDTH as f32 * fraction).round_ties_even() as u32;
        if filled > 0 {
            over(&mut canvas, fill, filled.min(fill.width), height, y);
        }
    }
    scale(&canvas, SELBAR_ZOOM)
}

/// The top-left `w` x `h` of `src` blended over `dst` at (0, `y`).
fn over(dst: &mut DecodedTexture, src: &DecodedTexture, w: u32, h: u32, y: u32) {
    for sy in 0..h {
        for sx in 0..w {
            let s = ((sy * src.width + sx) * 4) as usize;
            let d = (((y + sy) * dst.width + sx) * 4) as usize;
            let a = f32::from(src.rgba[s + 3]) / 255.0;
            for k in 0..3 {
                let v = f32::from(src.rgba[s + k]) * a + f32::from(dst.rgba[d + k]) * (1.0 - a);
                dst.rgba[d + k] = (v + 0.5) as u8;
            }
        }
    }
}

/// `dat sprites`: render surfaces left to right on a grey strip, each
/// blended over it at its own size, the strip zoomed (OpenAC
/// `AcDream.Cli dump-sprite-sheet`).
pub fn sprites(dir: &Path, ids: &[u32], out: &Path) -> Result {
    if ids.is_empty() {
        return Err("no render surface ids".into());
    }
    let dats = DatCollection::open(dir)?;
    let images = ids
        .iter()
        .map(|&id| decode(&dats, id))
        .collect::<core::result::Result<Vec<_>, _>>()?;
    write(out, &sprite_sheet(&images))?;
    let order: Vec<String> = ids
        .iter()
        .zip(&images)
        .map(|(id, im)| format!("0x{id:08X}={}x{}", im.width, im.height))
        .collect();
    println!("order (left to right): {}", order.join("  "));
    Ok(())
}

/// The sheet of `images`: [`SHEET_PAD`] around and between them on
/// [`SHEET_BACKGROUND`], zoomed [`SHEET_ZOOM`] times.
fn sprite_sheet(images: &[DecodedTexture]) -> DecodedTexture {
    let width = SHEET_PAD + images.iter().map(|i| i.width + SHEET_PAD).sum::<u32>();
    let height = images.iter().map(|i| i.height).max().unwrap_or(0) + 2 * SHEET_PAD;
    let mut sheet = DecodedTexture {
        width,
        height,
        rgba: SHEET_BACKGROUND.repeat((width * height) as usize),
    };
    let mut x = SHEET_PAD;
    for image in images {
        for (i, px) in image.rgba.chunks_exact(4).enumerate() {
            let (ix, iy) = (i as u32 % image.width, i as u32 / image.width);
            let at = (((SHEET_PAD + iy) * width + x + ix) * 4) as usize;
            let a = f32::from(px[3]) / 255.0;
            for (dst, &src) in sheet.rgba[at..at + 3].iter_mut().zip(&px[..3]) {
                *dst = (f32::from(src) * a + f32::from(*dst) * (1.0 - a)).round() as u8;
            }
        }
        x += image.width + SHEET_PAD;
    }
    scale(&sheet, SHEET_ZOOM)
}

fn decode(dats: &DatCollection, id: u32) -> core::result::Result<DecodedTexture, Box<dyn Error>> {
    let rs: RenderSurface = dats
        .get(id)?
        .ok_or_else(|| format!("no RenderSurface {id:08X}"))?;
    Ok(texture::decode(&rs, None, DecodeOptions::default())?)
}

fn with_suffix(base: &Path, suffix: &str) -> std::path::PathBuf {
    let mut name = base.as_os_str().to_owned();
    name.push(suffix);
    name.into()
}

fn write(path: &Path, image: &DecodedTexture) -> Result {
    std::fs::write(
        path,
        png::encode_rgba(image.width, image.height, &image.rgba),
    )
    .map_err(|e| format!("cannot write {}: {e}", path.display()))?;
    println!("wrote {}", path.display());
    Ok(())
}

fn alpha_as_grey(t: &DecodedTexture, zoom: u32) -> DecodedTexture {
    let mut grey = DecodedTexture {
        width: t.width,
        height: t.height,
        rgba: Vec::with_capacity(t.rgba.len()),
    };
    for px in t.rgba.chunks_exact(4) {
        grey.rgba.extend_from_slice(&[px[3], px[3], px[3], 255]);
    }
    scale(&grey, zoom)
}

fn scale(t: &DecodedTexture, zoom: u32) -> DecodedTexture {
    let (w, h) = (t.width * zoom, t.height * zoom);
    let mut rgba = Vec::with_capacity((w * h * 4) as usize);
    for y in 0..h {
        for x in 0..w {
            let i = (((y / zoom) * t.width + x / zoom) * 4) as usize;
            rgba.extend_from_slice(&t.rgba[i..i + 4]);
        }
    }
    DecodedTexture {
        width: w,
        height: h,
        rgba,
    }
}

/// One line of [`SAMPLE`]: white fill over a black outline on a dark panel,
/// the pen advancing by each glyph's advance and the line baseline snapped
/// to a whole pixel once.
fn render_sample(font: &Font, fg: &DecodedTexture, bg: Option<&DecodedTexture>) -> DecodedTexture {
    let glyph = |c: char| -> Option<&FontCharDesc> {
        font.char_descs
            .iter()
            .find(|g| u32::from(g.unicode) == u32::from(c))
    };
    let mut pen = 0i32;
    let mut max_x = 0i32;
    for c in SAMPLE.chars() {
        if let Some(g) = glyph(c) {
            max_x = max_x.max(pen + i32::from(g.horizontal_offset_before) + i32::from(g.width));
            pen += g.advance();
        }
    }
    let line = i32::try_from(font.max_char_height).unwrap_or(8).max(8);
    let width = (max_x.max(pen) + 4).max(8) as u32;
    let height = (line + 6) as u32;
    let mut out = DecodedTexture {
        width,
        height,
        rgba: [28u8, 28, 32, 255].repeat((width * height) as usize),
    };
    let border = (
        i32::try_from(font.num_horizontal_border_pixels).unwrap_or(0),
        i32::try_from(font.num_vertical_border_pixels).unwrap_or(0),
    );
    let base_y = 3;
    let mut pen = 2;
    for c in SAMPLE.chars() {
        let Some(g) = glyph(c) else { continue };
        let gx = pen + i32::from(g.horizontal_offset_before);
        let gy = base_y + i32::from(g.vertical_offset_before);
        if g.width > 0 && g.height > 0 {
            if let Some(bg) = bg {
                let rect = (
                    i32::from(g.offset_x) - border.0,
                    i32::from(g.offset_y) - border.1,
                    i32::from(g.width) + 2 * border.0,
                    i32::from(g.height) + 2 * border.1,
                );
                blit(&mut out, bg, rect, gx - border.0, gy - border.1, [0, 0, 0]);
            }
            let rect = (
                i32::from(g.offset_x),
                i32::from(g.offset_y),
                i32::from(g.width),
                i32::from(g.height),
            );
            blit(&mut out, fg, rect, gx, gy, [255, 255, 255]);
        }
        pen += g.advance();
    }
    out
}

/// Blend `tint` into `dst` at (`dx`, `dy`) by the atlas alpha in `src`.
fn blit(
    dst: &mut DecodedTexture,
    atlas: &DecodedTexture,
    (sx, sy, w, h): (i32, i32, i32, i32),
    dx: i32,
    dy: i32,
    tint: [u8; 3],
) {
    for y in 0..h {
        for x in 0..w {
            let (ax, ay) = (sx + x, sy + y);
            let (px, py) = (dx + x, dy + y);
            if ax < 0 || ay < 0 || px < 0 || py < 0 {
                continue;
            }
            let (ax, ay, px, py) = (ax as u32, ay as u32, px as u32, py as u32);
            if ax >= atlas.width || ay >= atlas.height || px >= dst.width || py >= dst.height {
                continue;
            }
            let coverage =
                f32::from(atlas.rgba[((ay * atlas.width + ax) * 4 + 3) as usize]) / 255.0;
            if coverage <= 0.0 {
                continue;
            }
            let i = ((py * dst.width + px) * 4) as usize;
            for (k, t) in tint.iter().enumerate() {
                let under = f32::from(dst.rgba[i + k]);
                dst.rgba[i + k] = (f32::from(*t) * coverage + under * (1.0 - coverage)) as u8;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn solid(width: u32, height: u32, px: [u8; 4]) -> DecodedTexture {
        DecodedTexture {
            width,
            height,
            rgba: px.repeat((width * height) as usize),
        }
    }

    /// OpenAC `mock-selbar`: rows of the bar's height plus 4, the back
    /// cropped to 140, the fill to 140 x the fraction (126, 98, 70, 0 at
    /// 0.9 .. 0), blended over (20, 20, 24), all 8 times.
    #[test]
    fn the_selbar_mock_stacks_five_fills_of_the_bar() {
        let back = solid(200, 3, [0, 0, 255, 255]);
        let fill = solid(200, 5, [255, 0, 0, 128]);
        let image = selbar(&back, &fill);
        assert_eq!((image.width, image.height), (140 * 8, (3 + 4) * 5 * 8));
        let at = |x: u32, y: u32| {
            let i = ((y * 8 * image.width + x * 8) * 4) as usize;
            [image.rgba[i], image.rgba[i + 1], image.rgba[i + 2]]
        };
        // Half-transparent red over blue: 128/255 of each.
        let mixed = [128, 0, 127];
        assert_eq!(at(0, 0), mixed);
        assert_eq!(at(139, 0), mixed);
        // Row two (0.9): filled to 126.
        assert_eq!(at(125, 7), mixed);
        assert_eq!(at(126, 7), [0, 0, 255]);
        // Row five (0.0): the back only; the gap shows the canvas.
        assert_eq!(at(0, 28), [0, 0, 255]);
        assert_eq!(at(0, 31), [20, 20, 24]);
    }

    /// The sheet's geometry as OpenAC's `dump-sprite-sheet` lays it out:
    /// 6 px around and between the sprites, the tallest sets the height,
    /// everything 10 times.
    #[test]
    fn a_sheet_spaces_its_sprites_on_grey_and_zooms_ten_times() {
        let sheet = sprite_sheet(&[solid(2, 3, [255, 0, 0, 255]), solid(4, 1, [0, 0, 255, 0])]);
        assert_eq!(sheet.width, (6 + 2 + 6 + 4 + 6) * 10);
        assert_eq!(sheet.height, (3 + 12) * 10);
        let at = |x: u32, y: u32| {
            let i = ((y * 10 * sheet.width + x * 10) * 4) as usize;
            [
                sheet.rgba[i],
                sheet.rgba[i + 1],
                sheet.rgba[i + 2],
                sheet.rgba[i + 3],
            ]
        };
        assert_eq!(at(0, 0), SHEET_BACKGROUND);
        assert_eq!(at(6, 6), [255, 0, 0, 255]);
        assert_eq!(at(7, 8), [255, 0, 0, 255]);
        assert_eq!(at(8, 6), SHEET_BACKGROUND);
        // A transparent sprite leaves the grey showing.
        assert_eq!(at(14, 6), SHEET_BACKGROUND);
    }
}

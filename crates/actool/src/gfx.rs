//! The model and texture commands: `dat gfxobj` and `dat texture`.

use std::error::Error;
use std::path::Path;

use ac_dat::DatCollection;
use ac_dat::objects::{
    BspNode, BspTree, GfxObj, GfxObjFlags, Palette, Polygon, RenderSurface, Surface, SurfaceBase,
    SurfaceTexture, SurfaceType,
};
use ac_dat::texture::{self, DecodeOptions, DecodedTexture};

use crate::dat::{f, sphere, v};
use ac_bits::png;

type Result = core::result::Result<(), Box<dyn Error>>;

/// `actool dat gfxobj <id>`.
pub fn gfxobj(dir: &Path, id: u32) -> Result {
    let dats = DatCollection::open(dir)?;
    let g: GfxObj = dats
        .get(id)?
        .ok_or_else(|| format!("no GfxObj {id:08X} in the portal dats"))?;
    let mut flag_names = Vec::new();
    for (bit, name) in [
        (GfxObjFlags::HAS_PHYSICS, "HasPhysics"),
        (GfxObjFlags::HAS_DRAWING, "HasDrawing"),
        (GfxObjFlags::UNKNOWN, "Unknown"),
        (GfxObjFlags::HAS_DID_DEGRADE, "HasDIDDegrade"),
    ] {
        if g.flags.contains(bit) {
            flag_names.push(name);
        }
    }
    println!("== GfxObj {:#010X}", g.id);
    println!("  flags {:#010X} {}", g.flags.0, flag_names.join(" "));
    let surfaces: Vec<String> = g.surfaces.iter().map(|s| format!("{s:#010X}")).collect();
    println!("  surfaces {}: {}", g.surfaces.len(), surfaces.join(" "));
    println!("  sort center {}", v(g.sort_center));
    println!("  degrade {:#010X}", g.did_degrade);
    println!(
        "  vertices {} (type {})",
        g.vertices.vertices.len(),
        g.vertices.vertex_type
    );
    for (key, vx) in &g.vertices.vertices {
        let uvs: Vec<String> = vx
            .uvs
            .iter()
            .map(|uv| format!("{}/{}", f(uv.u), f(uv.v)))
            .collect();
        println!(
            "    [{key:3}] origin={} normal={} uvs [{}]",
            v(vx.origin),
            v(vx.normal),
            uvs.join(" ")
        );
    }
    print_polygons("polygons", &g.polygons);
    print_polygons("physics polygons", &g.physics_polygons);
    if let Some(t) = &g.physics_bsp {
        print_tree("physics BSP", t);
    }
    if let Some(t) = &g.drawing_bsp {
        print_tree("drawing BSP", t);
    }
    Ok(())
}

fn print_polygons(label: &str, polygons: &[(u16, Polygon)]) {
    println!("  {label} {}", polygons.len());
    for (key, p) in polygons {
        println!(
            "    [{key:3}] stippling {:#04X} sides {} surfaces {}/{} vertices {:?} pos uv {:?} neg uv {:?}",
            p.stippling.0,
            p.sides_type.0,
            p.pos_surface,
            p.neg_surface,
            p.vertex_ids,
            p.pos_uv_indices,
            p.neg_uv_indices
        );
    }
}

fn print_tree(label: &str, t: &BspTree) {
    println!(
        "  {label}: {} nodes ({:?} tree), root first",
        t.nodes.len(),
        t.kind
    );
    let child = |c: Option<usize>| c.map_or_else(|| "-".to_owned(), |i| i.to_string());
    for (i, node) in t.nodes.iter().enumerate() {
        match node {
            BspNode::Split(s) => println!(
                "    [{i}] {} plane {} d={} +{} -{} sphere {} polys {:?}",
                String::from_utf8_lossy(&s.tag.text()),
                v(s.plane.normal),
                f(s.plane.distance),
                child(s.positive),
                child(s.negative),
                sphere(&s.sphere),
                s.polygons
            ),
            BspNode::Portal(p) => {
                let portals: Vec<String> = p
                    .portals
                    .iter()
                    .map(|r| format!("{}:{}", r.poly_id, r.portal_index))
                    .collect();
                println!(
                    "    [{i}] PORT plane {} d={} +{} -{} sphere {} polys {:?} portals [{}]",
                    v(p.plane.normal),
                    f(p.plane.distance),
                    p.positive,
                    p.negative,
                    sphere(&p.sphere),
                    p.polygons,
                    portals.join(" ")
                );
            }
            BspNode::Leaf(l) => println!(
                "    [{i}] LEAF {} solid {} sphere {} polys {:?}",
                l.index,
                l.solid,
                sphere(&l.sphere),
                l.polygons
            ),
        }
    }
}

/// `actool dat texture <id> --out <file.png>`.
/// `actool dat edges <id>`: a render surface's four first and four last
/// columns as RGB hex, top to bottom (seams between tiles).
pub fn edges(dir: &Path, id: u32) -> Result {
    let dats = DatCollection::open(dir)?;
    let d = render_surface(&dats, id, DecodeOptions::default())?;
    for line in edge_lines(&d) {
        println!("{line}");
    }
    Ok(())
}

fn edge_lines(d: &DecodedTexture) -> Vec<String> {
    let w = d.width as i64;
    let mut out = Vec::new();
    for cx in [0, 1, 2, 3, w - 4, w - 3, w - 2, w - 1] {
        if cx < 0 || cx >= w {
            continue;
        }
        let mut line = format!("x={cx:3}: ");
        for y in 0..d.height as i64 {
            let i = ((y * w + cx) * 4) as usize;
            line.push_str(&format!(
                "{:02X}{:02X}{:02X} ",
                d.rgba[i],
                d.rgba[i + 1],
                d.rgba[i + 2]
            ));
        }
        out.push(line);
    }
    out
}

pub fn texture(dir: &Path, id: u32, out: &Path) -> Result {
    let dats = DatCollection::open(dir)?;
    let decoded = match id >> 24 {
        0x06 | 0x07 => render_surface(&dats, id, DecodeOptions::default())?,
        0x05 => {
            let t: SurfaceTexture = dats
                .get(id)?
                .ok_or_else(|| format!("no SurfaceTexture {id:08X}"))?;
            println!(
                "SurfaceTexture {id:#010X}: type {} textures {:08X?}",
                t.texture_type.0, t.textures
            );
            let first = *t.textures.first().ok_or("the surface texture is empty")?;
            render_surface(&dats, first, DecodeOptions::default())?
        }
        0x08 => {
            let s: Surface = dats
                .get(id)?
                .ok_or_else(|| format!("no Surface {id:08X}"))?;
            println!(
                "Surface {id:#010X}: type {:#010X} translucency {} luminosity {} diffuse {}",
                s.surface_type.0,
                f(s.translucency),
                f(s.luminosity),
                f(s.diffuse)
            );
            match s.base {
                SurfaceBase::Solid(c) => {
                    println!(
                        "  solid colour {:02X}{:02X}{:02X}{:02X} (ARGB)",
                        c.alpha, c.red, c.green, c.blue
                    );
                    texture::solid_color(c, s.translucency)
                }
                SurfaceBase::Texture { texture, palette } => {
                    println!("  texture {texture:#010X} palette {palette:#010X}");
                    let options = DecodeOptions {
                        clip_map: s.surface_type.contains(SurfaceType::BASE1_CLIP_MAP),
                        additive: s.surface_type.contains(SurfaceType::ADDITIVE),
                    };
                    let t: SurfaceTexture = dats
                        .get(texture)?
                        .ok_or_else(|| format!("no SurfaceTexture {texture:08X}"))?;
                    let first = *t.textures.first().ok_or("the surface texture is empty")?;
                    render_surface(&dats, first, options)?
                }
            }
        }
        _ => {
            return Err(format!(
                "{id:08X} is not a render surface (06/07), surface texture (05) or surface (08)"
            )
            .into());
        }
    };
    std::fs::write(
        out,
        png::encode_rgba(decoded.width, decoded.height, &decoded.rgba),
    )
    .map_err(|e| format!("cannot write {}: {e}", out.display()))?;
    println!(
        "wrote {}x{} to {}",
        decoded.width,
        decoded.height,
        out.display()
    );
    Ok(())
}

fn render_surface(
    dats: &DatCollection,
    id: u32,
    options: DecodeOptions,
) -> core::result::Result<DecodedTexture, Box<dyn Error>> {
    let rs: RenderSurface = dats
        .get(id)?
        .ok_or_else(|| format!("no RenderSurface {id:08X} in the portal or highres dat"))?;
    println!(
        "RenderSurface {id:#010X}: {} ({:#X}) {}x{} data {} bytes palette {:#010X} clip map {} additive {}",
        rs.format.name(),
        rs.format.0,
        rs.width,
        rs.height,
        rs.data.len(),
        rs.default_palette_id,
        options.clip_map,
        options.additive
    );
    let palette: Option<Palette> = if rs.default_palette_id != 0 {
        dats.get(rs.default_palette_id)?
    } else {
        None
    };
    Ok(texture::decode(&rs, palette.as_ref(), options)?)
}

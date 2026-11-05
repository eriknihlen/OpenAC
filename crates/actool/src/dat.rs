//! The `dat` commands. Floats print as their shortest decimal form followed
//! by their bit pattern, so output can be compared exactly.

use std::error::Error;
use std::path::Path;

use ac_dat::objects::{
    BspNode, BspTree, CellPortal, EnvCell, EnvCellFlags, Environment, LandBlock, LandBlockInfo,
    MotionData, MotionDataFlags, MotionTable, Polygon, Region, Setup, SetupFlags,
};
use ac_dat::types::{ColorArgb, Frame, Quaternion, Sphere, Vector3};
use ac_dat::{DatCollection, DatFile};

type Result = core::result::Result<(), Box<dyn Error>>;

pub(crate) fn f(value: f32) -> String {
    format!("{value}({:#010X})", value.to_bits())
}

pub(crate) fn v(value: Vector3) -> String {
    format!("[{} {} {}]", f(value.x), f(value.y), f(value.z))
}

fn q(value: Quaternion) -> String {
    format!(
        "[w={} x={} y={} z={}]",
        f(value.w),
        f(value.x),
        f(value.y),
        f(value.z)
    )
}

fn frame(value: &Frame) -> String {
    format!("origin={} orient={}", v(value.origin), q(value.orientation))
}

pub(crate) fn sphere(value: &Sphere) -> String {
    format!("origin={} r={}", v(value.origin), f(value.radius))
}

/// `actool dat info`.
pub fn info(dir: &Path) -> Result {
    let dats = DatCollection::open(dir)?;
    for (name, dat) in dats.all() {
        print_info(name, dat)?;
    }
    Ok(())
}

fn print_info(name: &str, dat: &DatFile) -> Result {
    let h = dat.header();
    println!("== {name}: {}", dat.path().display());
    println!(
        "  version '{}'  magic {:#010X}  block size {}  file size {} (on disk {})",
        h.version,
        h.magic,
        h.block_size,
        h.file_size,
        dat.file_len()
    );
    println!(
        "  type {:?} ({})  subset {}",
        h.file_type,
        h.file_type.raw(),
        h.subset
    );
    println!(
        "  free blocks {}  first {:#010X}  last {:#010X}",
        h.free_block_count, h.first_free_block, h.last_free_block
    );
    println!(
        "  root {:#010X}  master map {:#010X}",
        h.root_block, h.master_map_id
    );
    println!(
        "  lru new {} old {} use {}",
        h.new_lru, h.old_lru, h.use_lru
    );
    println!(
        "  engine {}  game {}  major {}  minor {}",
        h.engine_version,
        h.game_version,
        h.major_version_text(),
        h.minor_version
    );
    let transactions: String = h.transactions[..16]
        .iter()
        .map(|b| format!("{b:02X}"))
        .collect();
    println!("  transactions {transactions}...");
    let it = dat.iteration();
    let runs: Vec<String> = it
        .iterations
        .iter()
        .map(|(start, count)| format!("{start}:{count}"))
        .collect();
    println!(
        "  iteration {}  runs {}",
        it.current_iteration,
        runs.join(",")
    );
    let mut count = 0u64;
    let mut compressed = 0u64;
    for entry in dat.entries() {
        let entry = entry?;
        count += 1;
        compressed += u64::from(entry.is_compressed());
    }
    println!("  entries {count}  compressed {compressed}");
    Ok(())
}

/// `actool dat landblock <id>`.
pub fn landblock(dir: &Path, id: u32) -> Result {
    let dats = DatCollection::open(dir)?;
    let lb: LandBlock = dats
        .get(id)?
        .ok_or_else(|| format!("no landblock {id:08X} in the cell dat"))?;
    println!(
        "== LandBlock {:#010X}  has objects {}",
        lb.id, lb.has_objects
    );
    println!("  terrain words (row y = 0..8 down, column x = 0..8 across):");
    for y in 0..9 {
        let row: Vec<String> = (0..9)
            .map(|x| format!("{:04X}", lb.terrain[x * 9 + y].0))
            .collect();
        println!("    {}", row.join(" "));
    }
    println!("  heights:");
    for y in 0..9 {
        let row: Vec<String> = (0..9)
            .map(|x| format!("{:3}", lb.height[x * 9 + y]))
            .collect();
        println!("    {}", row.join(" "));
    }
    println!(
        "  height sum {}  terrain sum {}",
        lb.height.iter().map(|&h| u32::from(h)).sum::<u32>(),
        lb.terrain.iter().map(|t| u32::from(t.0)).sum::<u32>()
    );

    let info_id = (id & 0xFFFF_0000) | 0xFFFE;
    let Some(info) = dats.get::<LandBlockInfo>(info_id)? else {
        println!("== no LandBlockInfo {info_id:#010X}");
        return Ok(());
    };
    println!(
        "== LandBlockInfo {:#010X}  cells {}  objects {}  buildings {}  restrictions {}",
        info.id,
        info.num_cells,
        info.objects.len(),
        info.buildings.len(),
        info.restriction_table.len()
    );
    for (i, o) in info.objects.iter().enumerate() {
        println!("  obj[{i}] {:#010X} {}", o.id, frame(&o.frame));
    }
    for (i, b) in info.buildings.iter().enumerate() {
        println!(
            "  bld[{i}] model {:#010X} leaves {} portals {} {}",
            b.model_id,
            b.num_leaves,
            b.portals.len(),
            frame(&b.frame)
        );
        for p in &b.portals {
            let stabs: Vec<String> = p.stab_list.iter().map(|s| format!("{s:04X}")).collect();
            println!(
                "    portal flags {} other cell {:04X} other portal {} stabs [{}]",
                p.flags,
                p.other_cell_id,
                p.other_portal_id,
                stabs.join(",")
            );
        }
    }
    for (key, value) in &info.restriction_table {
        println!("  restriction {key:#010X} = {value:#010X}");
    }
    Ok(())
}

/// `actool dat setup <id>`.
pub fn setup(dir: &Path, id: u32) -> Result {
    let dats = DatCollection::open(dir)?;
    let s: Setup = dats
        .get(id)?
        .ok_or_else(|| format!("no setup {id:08X} in the portal dats"))?;
    let mut flag_names = Vec::new();
    for (bit, name) in [
        (SetupFlags::HAS_PARENT, "HasParent"),
        (SetupFlags::HAS_DEFAULT_SCALE, "HasDefaultScale"),
        (SetupFlags::ALLOW_FREE_HEADING, "AllowFreeHeading"),
        (SetupFlags::HAS_PHYSICS_BSP, "HasPhysicsBSP"),
    ] {
        if s.flags.contains(bit) {
            flag_names.push(name);
        }
    }
    println!("== Setup {:#010X}", s.id);
    println!("  flags {:#010X} {}", s.flags.0, flag_names.join(" "));
    println!("  parts {}", s.parts.len());
    for (i, part) in s.parts.iter().enumerate() {
        let parent = s
            .parent_index
            .get(i)
            .map_or_else(String::new, |p| format!(" parent {p:#X}"));
        let scale = s
            .default_scale
            .get(i)
            .map_or_else(String::new, |sc| format!(" scale {}", v(*sc)));
        println!("    [{i:2}] {part:#010X}{parent}{scale}");
    }
    println!("  holding locations {}", s.holding_locations.len());
    for (key, loc) in &s.holding_locations {
        println!("    {key}: part {} {}", loc.part_id, frame(&loc.frame));
    }
    println!("  connection points {}", s.connection_points.len());
    for (key, loc) in &s.connection_points {
        println!("    {key}: part {} {}", loc.part_id, frame(&loc.frame));
    }
    println!("  placement frames {}", s.placement_frames.len());
    for (key, pf) in &s.placement_frames {
        println!(
            "    placement {key}: {} frames, {} hooks",
            pf.frames.len(),
            pf.hooks.len()
        );
        for (i, fr) in pf.frames.iter().enumerate() {
            println!("      [{i:2}] {}", frame(fr));
        }
        for hook in &pf.hooks {
            println!("      hook dir {:#X} {:?}", hook.direction, hook.data);
        }
    }
    println!("  cyl spheres {}", s.cyl_spheres.len());
    for c in &s.cyl_spheres {
        println!(
            "    origin={} r={} h={}",
            v(c.origin),
            f(c.radius),
            f(c.height)
        );
    }
    println!("  spheres {}", s.spheres.len());
    for sp in &s.spheres {
        println!("    {}", sphere(sp));
    }
    println!(
        "  height {}  radius {}  step up {}  step down {}",
        f(s.height),
        f(s.radius),
        f(s.step_up_height),
        f(s.step_down_height)
    );
    println!("  sorting sphere {}", sphere(&s.sorting_sphere));
    println!("  selection sphere {}", sphere(&s.selection_sphere));
    println!("  lights {}", s.lights.len());
    for (key, light) in &s.lights {
        let c = light.color;
        println!(
            "    {key}: {} color {:02X}{:02X}{:02X}{:02X} intensity {} falloff {} cone {}",
            frame(&light.view_space_location),
            c.alpha,
            c.red,
            c.green,
            c.blue,
            f(light.intensity),
            f(light.falloff),
            f(light.cone_angle)
        );
    }
    println!(
        "  defaults animation {:#010X} script {:#010X} motion table {:#010X} sound table {:#010X} script table {:#010X}",
        s.default_animation,
        s.default_script,
        s.default_motion_table,
        s.default_sound_table,
        s.default_script_table
    );
    Ok(())
}

fn d(value: f64) -> String {
    format!("{value}({:#018X})", value.to_bits())
}

fn color(c: ColorArgb) -> String {
    format!("{:02X}{:02X}{:02X}{:02X}", c.alpha, c.red, c.green, c.blue)
}

fn motion(m: &MotionData) -> String {
    let mut out = format!("bitfield {:#04X} flags {:#04X}", m.bitfield, m.flags.0);
    if m.flags.contains(MotionDataFlags::HAS_VELOCITY) {
        out += &format!(" velocity {}", v(m.velocity));
    }
    if m.flags.contains(MotionDataFlags::HAS_OMEGA) {
        out += &format!(" omega {}", v(m.omega));
    }
    for a in &m.anims {
        out += &format!(
            "\n        anim {:#010X} frames {}..{} rate {}",
            a.anim_id,
            a.low_frame,
            a.high_frame,
            f(a.framerate)
        );
    }
    out
}

/// `actool dat motiontable <id>`.
pub fn motion_table(dir: &Path, id: u32) -> Result {
    let dats = DatCollection::open(dir)?;
    let t: MotionTable = dats
        .get(id)?
        .ok_or_else(|| format!("no motion table {id:08X} in the portal dats"))?;
    println!(
        "== MotionTable {:#010X}  default stance {:#010X}",
        t.id, t.default_style
    );
    println!("  stance defaults {}", t.style_defaults.len());
    for (stance, motion) in &t.style_defaults {
        println!("    {stance:#010X} -> {motion:#010X}");
    }
    println!("  cycles {}", t.cycles.len());
    for (key, m) in &t.cycles {
        println!("    {key:#010X} {}", motion(m));
    }
    println!("  modifiers {}", t.modifiers.len());
    for (key, m) in &t.modifiers {
        println!("    {key:#010X} {}", motion(m));
    }
    println!("  links {}", t.links.len());
    for (key, targets) in &t.links {
        println!("    from {key:#010X}: {} targets", targets.len());
        for (to, m) in targets {
            println!("      to {to:#010X} {}", motion(m));
        }
    }
    Ok(())
}

fn portal_line(p: &CellPortal) -> String {
    let other = if p.flags & CellPortal::NO_OTHER_CELL != 0 {
        "outside".to_owned()
    } else {
        format!("cell {:04X} portal {}", p.other_cell_id, p.other_portal_id)
    };
    format!("flags {:#06X} polygon {} -> {other}", p.flags, p.polygon_id)
}

fn polygon_line(key: u16, p: &Polygon) -> String {
    format!(
        "[{key:3}] stippling {:#04X} sides {} surfaces {}/{} vertices {:?} uv+ {:?} uv- {:?}",
        p.stippling.0,
        p.sides_type.0,
        p.pos_surface,
        p.neg_surface,
        p.vertex_ids,
        p.pos_uv_indices,
        p.neg_uv_indices
    )
}

/// (nodes, leaves, depth) of a tree.
fn tree_shape(tree: &BspTree) -> (usize, usize, usize) {
    let leaves = tree
        .nodes
        .iter()
        .filter(|n| matches!(n, BspNode::Leaf(_)))
        .count();
    let mut depth = 0;
    let mut stack = if tree.nodes.is_empty() {
        Vec::new()
    } else {
        vec![(0, 1)]
    };
    while let Some((index, level)) = stack.pop() {
        depth = depth.max(level);
        if let Some(node) = tree.nodes.get(index) {
            stack.extend(node.children().map(|child| (child, level + 1)));
        }
    }
    (tree.nodes.len(), leaves, depth)
}

/// `actool dat envcell <id>`.
pub fn env_cell(dir: &Path, id: u32) -> Result {
    let dats = DatCollection::open(dir)?;
    let c: EnvCell = dats
        .get(id)?
        .ok_or_else(|| format!("no interior cell {id:08X} in the cell dat"))?;
    let mut flag_names = Vec::new();
    for (bit, name) in [
        (EnvCellFlags::SEEN_OUTSIDE, "SeenOutside"),
        (EnvCellFlags::HAS_STATIC_OBJS, "HasStaticObjs"),
        (EnvCellFlags::HAS_RESTRICTION_OBJ, "HasRestrictionObj"),
    ] {
        if c.flags.contains(bit) {
            flag_names.push(name);
        }
    }
    println!("== EnvCell {:#010X}  stored id {:#010X}", c.id, c.cell_id);
    println!("  flags {:#010X} {}", c.flags.0, flag_names.join(" "));
    println!(
        "  environment {:#010X} cell structure {}",
        c.environment_file_id(),
        c.cell_structure
    );
    println!("  position {}", frame(&c.position));
    let surfaces: Vec<String> = c.surface_file_ids().map(|s| format!("{s:08X}")).collect();
    println!("  surfaces {}: {}", surfaces.len(), surfaces.join(","));
    println!("  portals {}", c.cell_portals.len());
    for p in &c.cell_portals {
        println!("    {}", portal_line(p));
    }
    let visible: Vec<String> = c.visible_cells.iter().map(|v| format!("{v:04X}")).collect();
    println!("  visible cells {}: {}", visible.len(), visible.join(","));
    println!("  static objects {}", c.static_objects.len());
    for s in &c.static_objects {
        println!("    {:#010X} {}", s.id, frame(&s.frame));
    }
    if c.flags.contains(EnvCellFlags::HAS_RESTRICTION_OBJ) {
        println!("  restriction object {:#010X}", c.restriction_obj);
    }

    let env_id = c.environment_file_id();
    let Some(env) = dats.get::<Environment>(env_id)? else {
        println!("== no Environment {env_id:#010X}");
        return Ok(());
    };
    let Some(cs) = env.cell(u32::from(c.cell_structure)) else {
        println!(
            "== Environment {env_id:#010X} has no cell structure {}",
            c.cell_structure
        );
        return Ok(());
    };
    println!(
        "== Environment {:#010X} cell structure {} (of {})",
        env.id,
        c.cell_structure,
        env.cells.len()
    );
    println!(
        "  vertices {} (type {})",
        cs.vertex_array.vertices.len(),
        cs.vertex_array.vertex_type
    );
    for (key, vx) in &cs.vertex_array.vertices {
        let uvs: Vec<String> = vx
            .uvs
            .iter()
            .map(|uv| format!("({} {})", f(uv.u), f(uv.v)))
            .collect();
        println!(
            "    [{key:3}] {} normal {} uv {}",
            v(vx.origin),
            v(vx.normal),
            uvs.join(" ")
        );
    }
    println!("  polygons {}", cs.polygons.len());
    for (key, p) in &cs.polygons {
        println!("    {}", polygon_line(*key, p));
    }
    println!("  portal polygons {:?}", cs.portals);
    println!("  physics polygons {}", cs.physics_polygons.len());
    for (key, p) in &cs.physics_polygons {
        println!("    {}", polygon_line(*key, p));
    }
    let mut trees = vec![("cell", &cs.cell_bsp), ("physics", &cs.physics_bsp)];
    if let Some(t) = &cs.drawing_bsp {
        trees.push(("drawing", t));
    }
    for (name, tree) in trees {
        let (nodes, leaves, depth) = tree_shape(tree);
        println!("  {name} tree: {nodes} nodes, {leaves} leaves, depth {depth}");
    }
    if cs.drawing_bsp.is_none() {
        println!("  no drawing tree");
    }
    Ok(())
}

/// `actool dat region <id>`.
pub fn region(dir: &Path, id: u32) -> Result {
    let dats = DatCollection::open(dir)?;
    let g: Region = dats
        .get(id)?
        .ok_or_else(|| format!("no region {id:08X} in the portal dats"))?;
    println!(
        "== Region {:#010X} '{}'  number {} version {} parts {:#06X}",
        g.id, g.name, g.region_number, g.version, g.parts_mask.0
    );
    let l = &g.land_defs;
    println!(
        "  land {}x{} blocks, square {} m, {} squares per block, {} vertices per square",
        l.num_block_length,
        l.num_block_width,
        f(l.square_length),
        l.lblock_length,
        l.vertex_per_cell
    );
    println!(
        "  max object height {}  sky height {}  road width {}",
        f(l.max_obj_height),
        f(l.sky_height),
        f(l.road_width)
    );
    for (row, chunk) in l.land_height_table.chunks(16).enumerate() {
        let heights: Vec<String> = chunk.iter().map(|h| format!("{h}")).collect();
        println!("    heights[{:3}..] {}", row * 16, heights.join(" "));
    }
    let t = &g.game_time;
    println!(
        "  calendar: zero time {} year {} '{}' day {} s, {} days per year",
        d(t.zero_time_of_year),
        t.zero_year,
        t.year_spec,
        f(t.day_length),
        t.days_per_year
    );
    for tod in &t.times_of_day {
        println!(
            "    time {} {}{}",
            f(tod.start),
            tod.name,
            if tod.is_night { " (night)" } else { "" }
        );
    }
    println!("    weekdays {}", t.days_of_week.join(", "));
    for s in &t.seasons {
        println!("    season from day {} {}", s.start, s.name);
    }
    if let Some(sky) = &g.sky_info {
        println!(
            "  sky: tick {} light tick {}, {} kinds of day",
            d(sky.tick_size),
            d(sky.light_tick_size),
            sky.day_groups.len()
        );
        for group in &sky.day_groups {
            println!(
                "    day '{}' chance {}: {} sky objects, {} times",
                group.day_name,
                f(group.chance_of_occur),
                group.sky_objects.len(),
                group.sky_time.len()
            );
            for o in &group.sky_objects {
                println!(
                    "      object {:#010X} script {:#010X} time {}..{} angle {}..{} tex {} {} props {:#X}",
                    o.default_gfx_object_id,
                    o.default_pes_object_id,
                    f(o.begin_time),
                    f(o.end_time),
                    f(o.begin_angle),
                    f(o.end_angle),
                    f(o.tex_velocity_x),
                    f(o.tex_velocity_y),
                    o.properties
                );
            }
            for s in &group.sky_time {
                println!(
                    "      from {} sun {} heading {} pitch {} color {} ambient {} color {} fog {}..{} color {} mode {} overrides {}",
                    f(s.begin),
                    f(s.dir_bright),
                    f(s.dir_heading),
                    f(s.dir_pitch),
                    color(s.dir_color),
                    f(s.amb_bright),
                    color(s.amb_color),
                    f(s.min_world_fog),
                    f(s.max_world_fog),
                    color(s.world_fog_color),
                    s.world_fog,
                    s.sky_obj_replace.len()
                );
            }
        }
    }
    if let Some(sound) = &g.sound_info {
        println!("  ambient sound groups {}", sound.stb_desc.len());
        for (i, stb) in sound.stb_desc.iter().enumerate() {
            println!(
                "    [{i:2}] table {:#010X}: {} sounds",
                stb.stb_id,
                stb.ambient_sounds.len()
            );
        }
    }
    if let Some(scene) = &g.scene_info {
        println!("  scene types {}", scene.scene_types.len());
        for (i, st) in scene.scene_types.iter().enumerate() {
            let scenes: Vec<String> = st.scenes.iter().map(|s| format!("{s:08X}")).collect();
            println!(
                "    [{i:2}] sound group {} scenes {}",
                st.stb_index,
                scenes.join(",")
            );
        }
    }
    let terrain = &g.terrain_info;
    println!("  terrain types {}", terrain.terrain_types.len());
    for (i, tt) in terrain.terrain_types.iter().enumerate() {
        println!(
            "    [{i:2}] {} color {} scene types {:?}",
            tt.terrain_name,
            color(tt.terrain_color),
            tt.scene_types
        );
    }
    let tm = &terrain.land_surfaces.tex_merge;
    println!(
        "  terrain textures: base size {}, {} corner maps, {} side maps, {} road maps",
        tm.base_tex_size,
        tm.corner_terrain_maps.len(),
        tm.side_terrain_maps.len(),
        tm.road_maps.len()
    );
    for desc in &tm.terrain_desc {
        let x = desc.terrain_tex;
        println!(
            "    type {:2} texture {:#010X} tiling {} detail {:#010X} tiling {}",
            desc.terrain_type, x.texture_id, x.tex_tiling, x.detail_texture_id, x.detail_tex_tiling
        );
    }
    if let Some(m) = &g.region_misc {
        println!(
            "  map {:#010X}  test map {:#010X} size {}  clear cell {:#010X} monster {:#010X}",
            m.game_map_id,
            m.autotest_map_id,
            m.autotest_map_size,
            m.clear_cell_id,
            m.clear_monster_id
        );
    }
    Ok(())
}

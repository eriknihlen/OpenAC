use ac_bits::Reader;

use super::DatObject;
use super::read::{self, bool8, compressed_string, hash_table_count, keyed, list};
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::Frame;

type Result<T> = core::result::Result<T, DecodeError>;

/// File id of the character creation data.
pub const CHAR_GEN_ID: u32 = 0x0E00_0002;

/// Base id of models.
const GFX_OBJ_BASE: u32 = 0x0100_0000;
/// Base id of palettes.
const PALETTE_BASE: u32 = 0x0400_0000;
/// Base id of textures.
const SURFACE_TEXTURE_BASE: u32 = 0x0500_0000;

/// Character creation data: starting towns and every heritage's options.
/// File id `0x0E000002`.
#[derive(Debug, Clone, PartialEq)]
pub struct CharGen {
    /// File id.
    pub id: u32,
    /// The data id stored in the object (the same id).
    pub data_id: u32,
    /// Starting towns.
    pub starting_areas: Vec<StartingArea>,
    /// Heritages keyed by heritage number, in file order.
    pub heritage_groups: Vec<(u32, HeritageGroupCg)>,
}

impl DatObject for CharGen {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let data_id = r.u32()?;
        let count = u64::from(r.compressed_u32()?);
        let starting_areas = list(r, count, StartingArea::unpack)?;
        let count = hash_table_count(r)?;
        let heritage_groups = keyed(r, "heritages", count, read::u32, HeritageGroupCg::unpack)?;
        Ok(Self {
            id,
            data_id,
            starting_areas,
            heritage_groups,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// A position: cell id and frame within it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Position {
    /// Cell id.
    pub cell_id: u32,
    /// Position and orientation within the cell's landblock.
    pub frame: Frame,
}

impl Position {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            cell_id: r.u32()?,
            frame: Frame::unpack(r)?,
        })
    }
}

/// A starting town and its arrival points.
#[derive(Debug, Clone, PartialEq)]
pub struct StartingArea {
    /// Name.
    pub name: String,
    /// Arrival points.
    pub locations: Vec<Position>,
}

impl StartingArea {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let name = compressed_string(r)?;
        let count = u64::from(r.compressed_u32()?);
        Ok(Self {
            name,
            locations: list(r, count, Position::unpack)?,
        })
    }
}

/// One heritage's creation options.
#[derive(Debug, Clone, PartialEq)]
pub struct HeritageGroupCg {
    /// Name.
    pub name: String,
    /// Icon id.
    pub icon_id: u32,
    /// Setup id of the character.
    pub setup_id: u32,
    /// Setup id of the creation screen's backdrop.
    pub environment_setup_id: u32,
    /// Attribute points to spend.
    pub attribute_credits: u32,
    /// Skill credits to spend.
    pub skill_credits: u32,
    /// Starting area indices offered first.
    pub primary_start_areas: Vec<i32>,
    /// Other starting area indices.
    pub secondary_start_areas: Vec<i32>,
    /// Skills with heritage-specific costs.
    pub skills: Vec<SkillCg>,
    /// Profession templates.
    pub templates: Vec<TemplateCg>,
    /// Options per sex, keyed by sex number, in file order.
    pub genders: Vec<(i32, SexCg)>,
}

impl HeritageGroupCg {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let name = compressed_string(r)?;
        let icon_id = r.u32()?;
        let setup_id = r.u32()?;
        let environment_setup_id = r.u32()?;
        let attribute_credits = r.u32()?;
        let skill_credits = r.u32()?;
        let count = u64::from(r.compressed_u32()?);
        let primary_start_areas = list(r, count, read::i32)?;
        let count = u64::from(r.compressed_u32()?);
        let secondary_start_areas = list(r, count, read::i32)?;
        let count = u64::from(r.compressed_u32()?);
        let skills = list(r, count, SkillCg::unpack)?;
        let count = u64::from(r.compressed_u32()?);
        let templates = list(r, count, TemplateCg::unpack)?;
        let count = hash_table_count(r)?;
        let genders = keyed(r, "genders", count, read::i32, SexCg::unpack)?;
        Ok(Self {
            name,
            icon_id,
            setup_id,
            environment_setup_id,
            attribute_credits,
            skill_credits,
            primary_start_areas,
            secondary_start_areas,
            skills,
            templates,
            genders,
        })
    }
}

/// A skill and its costs for one heritage.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SkillCg {
    /// Skill id.
    pub id: i32,
    /// Credits to train.
    pub normal_cost: i32,
    /// Credits to specialise.
    pub primary_cost: i32,
}

impl SkillCg {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            id: r.i32()?,
            normal_cost: r.i32()?,
            primary_cost: r.i32()?,
        })
    }
}

/// A profession template: attributes and skills.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TemplateCg {
    /// Name.
    pub name: String,
    /// Icon id.
    pub icon_id: u32,
    /// Title id.
    pub title: u32,
    /// Strength.
    pub strength: i32,
    /// Endurance.
    pub endurance: i32,
    /// Coordination.
    pub coordination: i32,
    /// Quickness.
    pub quickness: i32,
    /// Focus.
    pub focus: i32,
    /// Self.
    pub self_: i32,
    /// Trained skill ids.
    pub normal_skills: Vec<i32>,
    /// Specialised skill ids.
    pub primary_skills: Vec<i32>,
}

impl TemplateCg {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let name = compressed_string(r)?;
        let icon_id = r.u32()?;
        let title = r.u32()?;
        let strength = r.i32()?;
        let endurance = r.i32()?;
        let coordination = r.i32()?;
        let quickness = r.i32()?;
        let focus = r.i32()?;
        let self_ = r.i32()?;
        let count = u64::from(r.compressed_u32()?);
        let normal_skills = list(r, count, read::i32)?;
        let count = u64::from(r.compressed_u32()?);
        let primary_skills = list(r, count, read::i32)?;
        Ok(Self {
            name,
            icon_id,
            title,
            strength,
            endurance,
            coordination,
            quickness,
            focus,
            self_,
            normal_skills,
            primary_skills,
        })
    }
}

/// One sex's appearance options and default tables.
#[derive(Debug, Clone, PartialEq)]
pub struct SexCg {
    /// Name.
    pub name: String,
    /// Scale, percent.
    pub scale: u32,
    /// Setup id.
    pub setup_id: u32,
    /// Sound table id.
    pub sound_table: u32,
    /// Icon id.
    pub icon_id: u32,
    /// Base palette id.
    pub base_palette: u32,
    /// Skin palette set id.
    pub skin_pal_set: u32,
    /// Physics script table id.
    pub physics_table: u32,
    /// Motion table id.
    pub motion_table: u32,
    /// Combat table id.
    pub combat_table: u32,
    /// Base appearance.
    pub base_obj_desc: ObjDesc,
    /// Hair colour palette set ids.
    pub hair_colors: Vec<u32>,
    /// Hair styles.
    pub hair_styles: Vec<HairStyleCg>,
    /// Eye colour palette ids.
    pub eye_colors: Vec<u32>,
    /// Eye textures.
    pub eye_strips: Vec<EyeStripCg>,
    /// Nose textures.
    pub nose_strips: Vec<FaceStripCg>,
    /// Mouth textures.
    pub mouth_strips: Vec<FaceStripCg>,
    /// Hats.
    pub headgears: Vec<GearCg>,
    /// Shirts.
    pub shirts: Vec<GearCg>,
    /// Trousers.
    pub pants: Vec<GearCg>,
    /// Shoes.
    pub footwear: Vec<GearCg>,
    /// Clothing colour ids.
    pub clothing_colors: Vec<u32>,
}

impl SexCg {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let name = compressed_string(r)?;
        let scale = r.u32()?;
        let setup_id = r.u32()?;
        let sound_table = r.u32()?;
        let icon_id = r.u32()?;
        let base_palette = r.u32()?;
        let skin_pal_set = r.u32()?;
        let physics_table = r.u32()?;
        let motion_table = r.u32()?;
        let combat_table = r.u32()?;
        let base_obj_desc = ObjDesc::unpack(r)?;
        let count = |r: &mut Reader<'_>| -> Result<u64> { Ok(u64::from(r.compressed_u32()?)) };
        let n = count(r)?;
        let hair_colors = list(r, n, read::u32)?;
        let n = count(r)?;
        let hair_styles = list(r, n, HairStyleCg::unpack)?;
        let n = count(r)?;
        let eye_colors = list(r, n, read::u32)?;
        let n = count(r)?;
        let eye_strips = list(r, n, EyeStripCg::unpack)?;
        let n = count(r)?;
        let nose_strips = list(r, n, FaceStripCg::unpack)?;
        let n = count(r)?;
        let mouth_strips = list(r, n, FaceStripCg::unpack)?;
        let n = count(r)?;
        let headgears = list(r, n, GearCg::unpack)?;
        let n = count(r)?;
        let shirts = list(r, n, GearCg::unpack)?;
        let n = count(r)?;
        let pants = list(r, n, GearCg::unpack)?;
        let n = count(r)?;
        let footwear = list(r, n, GearCg::unpack)?;
        let n = count(r)?;
        let clothing_colors = list(r, n, read::u32)?;
        Ok(Self {
            name,
            scale,
            setup_id,
            sound_table,
            icon_id,
            base_palette,
            skin_pal_set,
            physics_table,
            motion_table,
            combat_table,
            base_obj_desc,
            hair_colors,
            hair_styles,
            eye_colors,
            eye_strips,
            nose_strips,
            mouth_strips,
            headgears,
            shirts,
            pants,
            footwear,
            clothing_colors,
        })
    }
}

/// A hair style.
#[derive(Debug, Clone, PartialEq)]
pub struct HairStyleCg {
    /// Icon id.
    pub icon_id: u32,
    /// Whether the style is bald.
    pub bald: bool,
    /// Setup id that replaces the character's setup (0 for none).
    pub alternate_setup: u32,
    /// Appearance changes.
    pub obj_desc: ObjDesc,
}

impl HairStyleCg {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            icon_id: r.u32()?,
            bald: bool8(r)?,
            alternate_setup: r.u32()?,
            obj_desc: ObjDesc::unpack(r)?,
        })
    }
}

/// An eye texture, with and without hair.
#[derive(Debug, Clone, PartialEq)]
pub struct EyeStripCg {
    /// Icon id.
    pub icon_id: u32,
    /// Icon id with a bald head.
    pub bald_icon_id: u32,
    /// Appearance changes.
    pub obj_desc: ObjDesc,
    /// Appearance changes with a bald head.
    pub bald_obj_desc: ObjDesc,
}

impl EyeStripCg {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            icon_id: r.u32()?,
            bald_icon_id: r.u32()?,
            obj_desc: ObjDesc::unpack(r)?,
            bald_obj_desc: ObjDesc::unpack(r)?,
        })
    }
}

/// A nose or mouth texture.
#[derive(Debug, Clone, PartialEq)]
pub struct FaceStripCg {
    /// Icon id.
    pub icon_id: u32,
    /// Appearance changes.
    pub obj_desc: ObjDesc,
}

impl FaceStripCg {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            icon_id: r.u32()?,
            obj_desc: ObjDesc::unpack(r)?,
        })
    }
}

/// A starting clothing item.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GearCg {
    /// Name.
    pub name: String,
    /// Clothing table id.
    pub clothing_table: u32,
    /// Server object class id.
    pub weenie_default: u32,
}

impl GearCg {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            name: compressed_string(r)?,
            clothing_table: r.u32()?,
            weenie_default: r.u32()?,
        })
    }
}

/// Appearance changes: palette swaps, texture swaps and part swaps.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ObjDesc {
    /// Format version; always [`ObjDesc::VERSION`].
    pub version: u8,
    /// Base palette id; zero when there are no palette swaps.
    pub palette_id: u32,
    /// Palette range swaps.
    pub sub_palettes: Vec<SubPalette>,
    /// Texture swaps.
    pub texture_changes: Vec<TextureMapChange>,
    /// Part model swaps.
    pub anim_part_changes: Vec<AnimationPartChange>,
}

impl ObjDesc {
    /// The only format version; any other is refused.
    pub const VERSION: u8 = 0x11;

    /// Padding to 4 bytes from the object start, the version and three
    /// counts, the lists, then padding again.
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        r.align(4)?;
        let version = r.u8()?;
        if version != Self::VERSION {
            return Err(DecodeError::Unsupported("appearance description version"));
        }
        let num_sub_palettes = r.u8()?;
        let num_texture_changes = r.u8()?;
        let num_anim_part_changes = r.u8()?;
        let palette_id = if num_sub_palettes > 0 {
            r.data_id_of_known_type(PALETTE_BASE)?
        } else {
            0
        };
        let sub_palettes = list(r, u64::from(num_sub_palettes), SubPalette::unpack)?;
        let texture_changes = list(r, u64::from(num_texture_changes), TextureMapChange::unpack)?;
        let anim_part_changes = list(
            r,
            u64::from(num_anim_part_changes),
            AnimationPartChange::unpack,
        )?;
        r.align(4)?;
        Ok(Self {
            version,
            palette_id,
            sub_palettes,
            texture_changes,
            anim_part_changes,
        })
    }
}

/// A palette range swap.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SubPalette {
    /// Palette id.
    pub sub_id: u32,
    /// First colour replaced, as stored (in units of 8 colours).
    pub offset: u8,
    /// Colours replaced, as stored (in units of 8 colours).
    pub num_colors: u8,
}

impl SubPalette {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            sub_id: r.data_id_of_known_type(PALETTE_BASE)?,
            offset: r.u8()?,
            num_colors: r.u8()?,
        })
    }
}

/// A texture swap on one part.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TextureMapChange {
    /// Part index.
    pub part_index: u8,
    /// Texture replaced.
    pub old_texture: u32,
    /// Replacement texture.
    pub new_texture: u32,
}

impl TextureMapChange {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            part_index: r.u8()?,
            old_texture: r.data_id_of_known_type(SURFACE_TEXTURE_BASE)?,
            new_texture: r.data_id_of_known_type(SURFACE_TEXTURE_BASE)?,
        })
    }
}

/// A model swap on one part.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AnimationPartChange {
    /// Part index.
    pub part_index: u8,
    /// Replacement model id.
    pub part_id: u32,
}

impl AnimationPartChange {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            part_index: r.u8()?,
            part_id: r.data_id_of_known_type(GFX_OBJ_BASE)?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn text(w: &mut Writer, s: &str) {
        w.compressed_u32(s.len() as u32).unwrap();
        w.bytes(s.as_bytes());
    }

    /// An appearance with one swap of each kind (or none).
    fn obj_desc(w: &mut Writer, full: bool) {
        w.align(4);
        w.u8(17);
        let n = u8::from(full);
        w.u8(n);
        w.u8(n);
        w.u8(n);
        if full {
            w.u16(0x007E); // palette 0x0400007E
            w.u16(0x0BEF); // sub palette
            w.u8(3);
            w.u8(2);
            w.u8(4); // texture change on part 4
            w.u16(0x1371);
            w.u16(0x8001); // long form: 0x05011234
            w.u16(0x1234);
            w.u8(9); // part change on part 9
            w.u16(0x004E);
        }
        w.align(4);
    }

    fn gear(w: &mut Writer, name: &str) {
        text(w, name);
        w.u32(0x1000_0001);
        w.u32(130);
    }

    fn sex(w: &mut Writer) {
        text(w, "Male");
        for v in [
            100u32,
            0x0200_0001,
            0x2000_0001,
            0x0600_04DA,
            0x0400_007E,
            0x0F00_0013,
            0x3400_0004,
            0x0900_0001,
            0x3000_0000,
        ] {
            w.u32(v);
        }
        obj_desc(w, true);
        w.u8(1);
        w.u32(0x0F00_0B50);
        w.u8(1); // hair styles
        w.u32(0x0600_0F54);
        w.u8(1);
        w.u32(0);
        obj_desc(w, false);
        w.u8(0); // eye colours
        w.u8(1); // eye strips
        w.u32(0);
        w.u32(0);
        obj_desc(w, true);
        obj_desc(w, false);
        w.u8(1); // noses
        w.u32(5);
        obj_desc(w, false);
        w.u8(0); // mouths
        w.u8(0); // hats
        w.u8(1); // shirts
        gear(w, "Shirt");
        w.u8(0); // pants
        w.u8(0); // shoes
        w.u8(1);
        w.u32(9);
    }

    fn sample() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(CHAR_GEN_ID);
        w.u32(CHAR_GEN_ID);
        w.u8(1);
        text(&mut w, "Holtburg");
        w.u8(1);
        w.u32(0x8602_01AD);
        for v in [12.32f32, 0.0, 0.005, 0.34, 0.0, 0.0, 0.94] {
            w.f32(v);
        }
        w.u8(1); // heritage table bucket index
        w.u8(1); // one heritage
        w.u32(1);
        text(&mut w, "Aluvian");
        for v in [0x0600_04C6u32, 0x0200_0054, 0x0200_05A1, 330, 52] {
            w.u32(v);
        }
        w.u8(1);
        w.i32(0);
        w.u8(3);
        for a in [1, 2, 3] {
            w.i32(a);
        }
        w.u8(1);
        w.i32(14);
        w.i32(0);
        w.i32(2);
        w.u8(1); // templates
        text(&mut w, "Adventurer");
        w.u32(0x0600_1152);
        w.u32(1);
        for a in [10, 10, 10, 10, 10, 10] {
            w.i32(a);
        }
        w.u8(1);
        w.i32(6);
        w.u8(0);
        w.u8(1); // gender table bucket index
        w.u8(1);
        w.i32(1);
        sex(&mut w);
        w.into_vec()
    }

    #[test]
    fn unpacks_creation_data() {
        let bytes = sample();
        let r = &mut Reader::new(&bytes);
        let c = CharGen::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(c.starting_areas[0].name, "Holtburg");
        assert_eq!(c.starting_areas[0].locations[0].cell_id, 0x8602_01AD);
        let (key, h) = &c.heritage_groups[0];
        assert_eq!((*key, h.name.as_str()), (1, "Aluvian"));
        assert_eq!(h.secondary_start_areas, vec![1, 2, 3]);
        assert_eq!(
            h.skills[0],
            SkillCg {
                id: 14,
                normal_cost: 0,
                primary_cost: 2
            }
        );
        assert_eq!(h.templates[0].self_, 10);
        assert_eq!(h.templates[0].normal_skills, vec![6]);
        let (sex_key, s) = &h.genders[0];
        assert_eq!((*sex_key, s.name.as_str()), (1, "Male"));
        assert_eq!(s.combat_table, 0x3000_0000);
        let od = &s.base_obj_desc;
        assert_eq!(od.version, ObjDesc::VERSION);
        assert_eq!(od.palette_id, 0x0400_007E);
        assert_eq!(
            od.sub_palettes[0],
            SubPalette {
                sub_id: 0x0400_0BEF,
                offset: 3,
                num_colors: 2
            }
        );
        assert_eq!(
            od.texture_changes[0],
            TextureMapChange {
                part_index: 4,
                old_texture: 0x0500_1371,
                new_texture: 0x0501_1234
            }
        );
        assert_eq!(
            od.anim_part_changes[0],
            AnimationPartChange {
                part_index: 9,
                part_id: 0x0100_004E
            }
        );
        assert!(s.hair_styles[0].bald);
        assert_eq!(
            s.hair_styles[0].obj_desc,
            ObjDesc {
                version: 17,
                ..ObjDesc::default()
            }
        );
        assert_eq!(s.eye_strips[0].obj_desc.texture_changes.len(), 1);
        assert_eq!(s.nose_strips[0].icon_id, 5);
        assert_eq!(s.shirts[0].name, "Shirt");
        assert_eq!(s.clothing_colors, vec![9]);
        for len in 0..bytes.len() {
            assert!(
                CharGen::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }

    #[test]
    fn appearance_of_another_version_is_refused() {
        let mut w = Writer::new();
        obj_desc(&mut w, false);
        let mut bytes = w.into_vec();
        assert!(ObjDesc::unpack(&mut Reader::new(&bytes)).is_ok());
        bytes[0] = 0x12;
        assert_eq!(
            ObjDesc::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::Unsupported("appearance description version"))
        );
    }
}

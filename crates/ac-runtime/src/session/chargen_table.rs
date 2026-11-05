//! The portal dat's character-generation table, projected into the plain
//! creation options character creation reads: starting towns, heritages
//! with their genders, templates and skill costs, and the skill table's
//! costs and details as the fallback for skills a heritage does not price.

use std::collections::HashMap;

use ac_core::chargen::{
    ChargenAnimPartChange, ChargenAttributeValues, ChargenEyeStrip, ChargenFaceStrip,
    ChargenGearOption, ChargenGenderOptions, ChargenHairStyle, ChargenHeritageOptions,
    ChargenObjDesc, ChargenOptions, ChargenPosition, ChargenSkillCost, ChargenSkillDetail,
    ChargenStarterArea, ChargenSubPalette, ChargenTemplate, ChargenTextureChange,
};
use ac_core::player::SkillBase;
use ac_dat::objects::{CharGen, GearCg, HeritageGroupCg, ObjDesc, SexCg, StartingArea, TemplateCg};

/// Project the table. `skills` is the skill table by skill id (empty when
/// the dat has none): every skill in it becomes a global cost and detail.
#[must_use]
pub fn project_chargen(table: &CharGen, skills: &HashMap<u32, SkillBase>) -> ChargenOptions {
    let starter_areas = table
        .starting_areas
        .iter()
        .enumerate()
        .map(|(index, area)| starter_area(index, area))
        .collect();
    let heritages_by_id = table
        .heritage_groups
        .iter()
        .map(|(id, heritage)| (*id, project_heritage(*id, heritage)))
        .collect();
    let mut global_skill_costs_by_skill_id = HashMap::with_capacity(skills.len());
    let mut global_skill_details_by_skill_id = HashMap::with_capacity(skills.len());
    for (&skill_id, skill) in skills {
        global_skill_costs_by_skill_id.insert(
            skill_id,
            ChargenSkillCost {
                skill_id,
                normal_cost: skill.trained_cost,
                primary_cost: skill.specialized_cost,
            },
        );
        global_skill_details_by_skill_id.insert(
            skill_id,
            ChargenSkillDetail {
                skill_id,
                name: skill.name.clone(),
                min_level: skill.min_level,
                description: skill.description.clone(),
                formula: skill.formula,
            },
        );
    }
    ChargenOptions {
        starter_areas,
        heritages_by_id,
        global_skill_costs_by_skill_id,
        global_skill_details_by_skill_id,
    }
}

fn starter_area(index: usize, area: &StartingArea) -> ChargenStarterArea {
    ChargenStarterArea {
        index: i32::try_from(index).unwrap_or(i32::MAX),
        name: area.name.clone(),
        locations: area
            .locations
            .iter()
            .map(|p| {
                let o = p.frame.origin;
                let q = p.frame.orientation;
                ChargenPosition {
                    cell_id: p.cell_id,
                    origin: [o.x, o.y, o.z],
                    rotation_wxyz: [q.w, q.x, q.y, q.z],
                }
            })
            .collect(),
    }
}

fn project_heritage(heritage_id: u32, cg: &HeritageGroupCg) -> ChargenHeritageOptions {
    let skill_costs_by_skill_id = cg
        .skills
        .iter()
        .map(|skill| {
            // Skill ids are small positive numbers; the dat stores them signed.
            let skill_id = skill.id.cast_unsigned();
            (
                skill_id,
                ChargenSkillCost {
                    skill_id,
                    normal_cost: skill.normal_cost,
                    primary_cost: skill.primary_cost,
                },
            )
        })
        .collect();
    ChargenHeritageOptions {
        heritage_id,
        name: cg.name.clone(),
        icon_id: cg.icon_id,
        setup_id: cg.setup_id,
        environment_setup_id: cg.environment_setup_id,
        attribute_credits: cg.attribute_credits,
        skill_credits: cg.skill_credits,
        primary_start_area_indices: cg.primary_start_areas.clone(),
        secondary_start_area_indices: cg.secondary_start_areas.clone(),
        skill_costs_by_skill_id,
        templates: cg.templates.iter().map(project_template).collect(),
        genders_by_key: cg
            .genders
            .iter()
            .map(|(key, sex)| (*key, project_gender(*key, sex)))
            .collect(),
    }
}

fn project_template(template: &TemplateCg) -> ChargenTemplate {
    let skills = |ids: &[i32]| ids.iter().map(|id| id.cast_unsigned()).collect();
    ChargenTemplate {
        name: template.name.clone(),
        icon_id: template.icon_id,
        title_string_id: template.title,
        attributes: ChargenAttributeValues {
            strength: template.strength,
            endurance: template.endurance,
            coordination: template.coordination,
            quickness: template.quickness,
            focus: template.focus,
            self_: template.self_,
        },
        normal_skills: skills(&template.normal_skills),
        primary_skills: skills(&template.primary_skills),
    }
}

fn project_gender(gender_key: i32, sex: &SexCg) -> ChargenGenderOptions {
    ChargenGenderOptions {
        gender_key,
        name: sex.name.clone(),
        scale: sex.scale,
        setup_id: sex.setup_id,
        sound_table_id: sex.sound_table,
        icon_id: sex.icon_id,
        base_palette_id: sex.base_palette,
        skin_pal_set_id: sex.skin_pal_set,
        physics_table_id: sex.physics_table,
        motion_table_id: sex.motion_table,
        combat_table_id: sex.combat_table,
        base_obj_desc: obj_desc(&sex.base_obj_desc),
        hair_colors: sex.hair_colors.clone(),
        hair_styles: sex
            .hair_styles
            .iter()
            .map(|hair| ChargenHairStyle {
                icon_id: hair.icon_id,
                bald: hair.bald,
                alternate_setup: hair.alternate_setup,
                obj_desc: obj_desc(&hair.obj_desc),
            })
            .collect(),
        eye_colors: sex.eye_colors.clone(),
        eye_strips: sex
            .eye_strips
            .iter()
            .map(|eye| ChargenEyeStrip {
                icon_id: eye.icon_id,
                bald_icon_id: eye.bald_icon_id,
                obj_desc: obj_desc(&eye.obj_desc),
                bald_obj_desc: obj_desc(&eye.bald_obj_desc),
            })
            .collect(),
        nose_strips: sex
            .nose_strips
            .iter()
            .map(|s| ChargenFaceStrip {
                icon_id: s.icon_id,
                obj_desc: obj_desc(&s.obj_desc),
            })
            .collect(),
        mouth_strips: sex
            .mouth_strips
            .iter()
            .map(|s| ChargenFaceStrip {
                icon_id: s.icon_id,
                obj_desc: obj_desc(&s.obj_desc),
            })
            .collect(),
        headgears: gear(&sex.headgears),
        shirts: gear(&sex.shirts),
        pants: gear(&sex.pants),
        footwear: gear(&sex.footwear),
        clothing_colors: sex.clothing_colors.clone(),
    }
}

fn gear(list: &[GearCg]) -> Vec<ChargenGearOption> {
    list.iter()
        .map(|g| ChargenGearOption {
            name: g.name.clone(),
            clothing_table_id: g.clothing_table,
            weenie_default_id: g.weenie_default,
        })
        .collect()
}

fn obj_desc(desc: &ObjDesc) -> ChargenObjDesc {
    ChargenObjDesc {
        palette_id: desc.palette_id,
        sub_palettes: desc
            .sub_palettes
            .iter()
            .map(|s| ChargenSubPalette {
                sub_palette_id: s.sub_id,
                offset: s.offset,
                num_colors: s.num_colors,
            })
            .collect(),
        texture_changes: desc
            .texture_changes
            .iter()
            .map(|t| ChargenTextureChange {
                part_index: t.part_index,
                old_texture_id: t.old_texture,
                new_texture_id: t.new_texture,
            })
            .collect(),
        anim_part_changes: desc
            .anim_part_changes
            .iter()
            .map(|a| ChargenAnimPartChange {
                part_index: a.part_index,
                part_id: a.part_id,
            })
            .collect(),
    }
}

#[cfg(test)]
mod tests {
    //! Installed-dat conformance: OpenAC
    //! `tests/AcDream.Content.Tests/CharGen/ChargenTableReaderInstalledDatTests.cs`.
    use ac_core::chargen::ChargenSkillAdvancementSet;
    use ac_core::chargen::attributes::{are_all_within_range, is_fully_spent};

    use crate::gameplay::GameTables;

    fn options() -> std::sync::Arc<ac_core::chargen::ChargenOptions> {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = ac_dat::DatCollection::open(dir).expect("open the retail dats");
        std::sync::Arc::clone(GameTables::load(&dats).unwrap().chargen())
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn thirteen_heritages_with_the_retail_names_and_start_areas() {
        let options = options();
        assert_eq!(options.heritages_by_id.len(), 13);
        assert!(!options.starter_areas.is_empty());
        let name = |id: u32| options.heritages_by_id[&id].name.clone();
        assert_eq!(name(1), "Aluvian");
        assert_eq!(name(2), "Gharu'ndim");
        assert_eq!(name(3), "Sho");
        assert_eq!(name(4), "Viamontian");
        assert!(options.heritages_by_id[&12].is_olthoi());
        assert!(options.heritages_by_id[&13].is_olthoi());
        let mut checked = 0;
        for heritage in options.heritages_by_id.values() {
            assert!(
                heritage
                    .genders_by_key
                    .values()
                    .any(|g| g.has_any_appearance_options())
            );
            for index in heritage
                .primary_start_area_indices
                .iter()
                .chain(&heritage.secondary_start_area_indices)
            {
                checked += 1;
                let area = options.try_get_starter_area(*index).unwrap();
                assert!(!area.name.is_empty());
            }
        }
        assert!(checked > 0);
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn every_template_fits_its_budget_and_one_spends_it_exactly() {
        let options = options();
        let mut any_full = false;
        let mut templates = 0;
        for heritage in options.heritages_by_id.values() {
            for template in &heritage.templates {
                templates += 1;
                assert!(are_all_within_range(&template.attributes));
                assert!(template.attributes.total() <= i64::from(heritage.attribute_credits));
                any_full |= is_fully_spent(heritage.attribute_credits, &template.attributes);
            }
        }
        assert!(templates > 0);
        assert!(any_full);
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn skill_costs_fall_back_to_the_skill_table_and_sixteen_skills_have_none() {
        let options = options();
        assert_eq!(options.global_skill_costs_by_skill_id.len(), 38);
        assert_eq!(options.global_skill_details_by_skill_id.len(), 38);
        let untrained = options
            .global_skill_details_by_skill_id
            .values()
            .filter(|d| d.min_level <= 1)
            .count();
        assert_eq!(untrained, 23);
        let expected: Vec<u32> = vec![1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13, 17, 25, 26, 42, 53];
        for heritage in options.heritages_by_id.values() {
            assert_eq!(heritage.skill_costs_by_skill_id.len(), 1);
            let uncostable: Vec<u32> = (1..ChargenSkillAdvancementSet::SLOT_COUNT as u32)
                .filter(|id| {
                    !heritage.skill_costs_by_skill_id.contains_key(id)
                        && !options.global_skill_costs_by_skill_id.contains_key(id)
                })
                .collect();
            assert_eq!(uncostable, expected, "{}", heritage.name);
        }
        let aluvian = &options.heritages_by_id[&1];
        for (id, cost) in &aluvian.skill_costs_by_skill_id {
            assert_eq!(*id, cost.skill_id);
            assert!((1..=54).contains(id));
            assert!(cost.normal_cost >= 0 && cost.primary_cost >= 0);
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn every_gender_offers_every_appearance_list() {
        let options = options();
        for heritage in options.heritages_by_id.values() {
            for gender in heritage.genders_by_key.values() {
                for (list, count) in [
                    ("hair styles", gender.hair_styles.len()),
                    ("eye strips", gender.eye_strips.len()),
                    ("nose strips", gender.nose_strips.len()),
                    ("mouth strips", gender.mouth_strips.len()),
                    ("headgear", gender.headgears.len()),
                    ("shirts", gender.shirts.len()),
                    ("pants", gender.pants.len()),
                    ("footwear", gender.footwear.len()),
                    ("hair colors", gender.hair_colors.len()),
                    ("eye colors", gender.eye_colors.len()),
                    ("clothing colors", gender.clothing_colors.len()),
                ] {
                    assert!(count > 0, "{}/{}: {list}", heritage.name, gender.name);
                }
            }
        }
    }
}

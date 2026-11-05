//! OpenAC's prepared-content recipe migrations: what an older package needs
//! to match the current recipe.

/// What content work a migration needs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ContentWorkKind {
    /// Nothing.
    None,
    /// A layered update package.
    Overlay,
    /// A full rebuild.
    FullRebuild,
    /// A verification pass.
    Verify,
}

/// One resolved migration.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ContentMigrationPlan {
    /// The recipe the package has.
    pub from_recipe_version: u32,
    /// The recipe the client needs.
    pub target_recipe_version: u32,
    /// The work.
    pub kind: ContentWorkKind,
    /// Why, every step's reason joined by `; `.
    pub reason: String,
    /// DAT ids an overlay rebuilds.
    pub dat_ids: Vec<u32>,
    /// Landblocks an overlay rebuilds.
    pub landblocks: Vec<u8>,
}

const STEPS: [(u32, &str); 9] = [
    (2, "prepared EnvCell identity changed"),
    (3, "render-pass translucency moved into prepared meshes"),
    (4, "flat collision and EnvCell topology were added"),
    (5, "solid-colour positive mesh faces must be regenerated"),
    (
        6,
        "the optimized pak v2 texture catalog and compression format require one full rebuild",
    ),
    (
        7,
        "GfxObj portal admission now uses the authored DrawingBSP sphere",
    ),
    (8, "exact CellStruct surface-index subset construction"),
    (9, "authored surface opacity in prepared mesh batches"),
    (
        10,
        "exact resolved SetSurface state in prepared mesh batches",
    ),
];

/// The migration from one recipe to a newer one: every published step on
/// the way (all of them full rebuilds today), combined.
pub fn resolve_content_migration(from: u32, target: u32) -> Result<ContentMigrationPlan, String> {
    if from == 0 || target == 0 {
        return Err("Content recipe versions must be positive.".into());
    }
    if from == target {
        return Ok(ContentMigrationPlan {
            from_recipe_version: from,
            target_recipe_version: target,
            kind: ContentWorkKind::None,
            reason: "Prepared content already matches this client.".into(),
            dat_ids: Vec::new(),
            landblocks: Vec::new(),
        });
    }
    if from > target {
        return Err(format!(
            "Prepared content recipe {from} is newer than this launcher's recipe {target}."
        ));
    }
    let mut reasons = Vec::new();
    for step in from + 1..=target {
        let reason = STEPS
            .iter()
            .find(|(version, _)| *version == step)
            .map(|(_, reason)| *reason)
            .ok_or_else(|| {
                format!(
                    "No prepared-content migration is published for recipe {} to {step}.",
                    step - 1
                )
            })?;
        reasons.push(reason);
    }
    Ok(ContentMigrationPlan {
        from_recipe_version: from,
        target_recipe_version: target,
        kind: ContentWorkKind::FullRebuild,
        reason: reasons.join("; "),
        dat_ids: Vec::new(),
        landblocks: Vec::new(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from OpenAC tests/.../Installation/ContentMigrationCatalogTests.cs.

    #[test]
    fn single_steps_are_full_rebuilds_with_their_reason() {
        for (from, word) in [
            (6, "DrawingBSP"),
            (7, "CellStruct"),
            (8, "surface opacity"),
            (9, "SetSurface"),
        ] {
            let plan = resolve_content_migration(from, from + 1).unwrap();
            assert_eq!(plan.kind, ContentWorkKind::FullRebuild);
            assert_eq!(plan.from_recipe_version, from);
            assert_eq!(plan.target_recipe_version, from + 1);
            assert!(
                plan.reason.to_lowercase().contains(&word.to_lowercase()),
                "{from}"
            );
            assert!(plan.dat_ids.is_empty() && plan.landblocks.is_empty());
        }
    }

    #[test]
    fn older_recipes_collapse_to_one_full_rebuild() {
        for (target, words) in [
            (7, ["pak v2", "DrawingBSP"]),
            (8, ["DrawingBSP", "CellStruct"]),
            (9, ["CellStruct", "surface opacity"]),
            (10, ["surface opacity", "SetSurface"]),
        ] {
            let plan = resolve_content_migration(1, target).unwrap();
            assert_eq!(plan.kind, ContentWorkKind::FullRebuild);
            assert_eq!(plan.target_recipe_version, target);
            for word in words {
                assert!(plan.reason.to_lowercase().contains(&word.to_lowercase()));
            }
        }
    }

    #[test]
    fn equal_newer_and_unpublished_recipes() {
        assert_eq!(
            resolve_content_migration(10, 10).unwrap().kind,
            ContentWorkKind::None
        );
        assert!(resolve_content_migration(11, 10).is_err());
        assert!(resolve_content_migration(10, 11).is_err());
        assert!(resolve_content_migration(0, 10).is_err());
    }
}

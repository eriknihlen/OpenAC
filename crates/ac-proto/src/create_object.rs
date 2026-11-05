use ac_bits::Reader;

use crate::{ModelData, Opcode, PhysicsDesc, PublicWeenieDesc, Result, expect_opcode};

/// A complete object description: the server creating an object
/// ([`Opcode::CreateObject`]) or re-sending the description of one that
/// already exists ([`Opcode::UpdateObject`]). Both carry the same body.
#[derive(Debug, Clone, PartialEq)]
pub struct CreateObject {
    /// The object.
    pub guid: u32,
    /// Appearance changes.
    pub model: ModelData,
    /// Physics fields and update counters.
    pub physics: PhysicsDesc,
    /// Public description; absent when the body ends after the physics
    /// block (fewer than 4 bytes left).
    pub weenie: Option<PublicWeenieDesc>,
}

impl CreateObject {
    /// Parse a create-object body.
    pub fn parse(body: &[u8]) -> Result<Self> {
        Self::parse_as(body, Opcode::CreateObject)
    }

    /// Parse an update-object body: a re-sent description for an object
    /// that already exists, which refreshes it rather than spawning it.
    pub fn parse_update(body: &[u8]) -> Result<Self> {
        Self::parse_as(body, Opcode::UpdateObject)
    }

    fn parse_as(body: &[u8], opcode: Opcode) -> Result<Self> {
        let mut r = Reader::new(body);
        expect_opcode(&mut r, opcode)?;
        let guid = r.u32()?;
        let model = ModelData::read(&mut r)?;
        let physics = PhysicsDesc::read(&mut r)?;
        let weenie = if r.remaining() < 4 {
            None
        } else {
            Some(PublicWeenieDesc::read(&mut r)?)
        };
        Ok(Self {
            guid,
            model,
            physics,
            weenie,
        })
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/CreateObjectTests.cs`,
    //! `ProjectileVfxPacketFixtures.cs` (the CreateObject fixtures) and the
    //! message-level part of `WorldSessionUpdateObjectTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::model_data::tests::EMPTY as EMPTY_MODEL;
    use crate::physics_desc::*;
    use crate::test_util::assert_every_prefix_fails;
    use crate::{
        HouseGuest, MovementData, PhysicsMovement, PhysicsTimestamps, Position, ProtoError,
        Quaternion, Vector3,
    };

    const CREATURE: u32 = 0x10;
    const MISC: u32 = 0x80;
    const MELEE_WEAPON: u32 = 0x1;
    const MISSILE_WEAPON: u32 = 0x100;
    const CONTAINER: u32 = 0x200;
    const JEWELRY: u32 = 0x8;
    const CASTER: u32 = 0x8000;
    const PORTAL: u32 = 0x1_0000;

    /// The OpenAC test builder `BuildMinimalCreateObjectWithWeenieHeader`:
    /// empty appearance, a physics block with optional placement and
    /// parent, then the description with the requested optional fields.
    #[derive(Default)]
    struct Desc {
        guid: u32,
        name: &'static str,
        item_type: u32,
        physics_state: u32,
        object_description_flags: u32,
        weenie_flags: u32,
        weenie_flags2: u32,
        icon_id: u32,
        ui_effects: u32,
        value: u32,
        useability: u32,
        use_radius: f32,
        target_type: u32,
        icon_overlay_id: u32,
        icon_underlay_id: u32,
        structure: u16,
        max_structure: u16,
        stack_size: u16,
        burden: u16,
        weenie_class_id: Option<u32>,
        max_stack_size: u16,
        items_capacity: i8,
        containers_capacity: i8,
        container: u32,
        wielder: u32,
        valid_locations: u32,
        currently_wielded_location: u32,
        priority: u32,
        workmanship: f32,
        plural_name: &'static str,
        radar_blip_color: u8,
        radar_behavior: u8,
        combat_use: u8,
        ammo_type: u16,
        movement_seq: u16,
        placement_id: Option<u32>,
        parent: Option<(u32, u32)>,
        position_seq: u16,
        instance_seq: u16,
        material_type: u32,
        cooldown_id: u32,
        cooldown_duration: f64,
        pet_owner_id: u32,
        spell_id: u16,
        hook_item_types: u32,
        hook_type: u16,
        house_owner_id: u32,
        monarch_id: u32,
        house_open: bool,
        house_monarch_id: u32,
        house_guests: &'static [(u32, u32)],
    }

    impl Desc {
        fn new(guid: u32, name: &'static str, item_type: u32) -> Self {
            Self {
                guid,
                name,
                item_type,
                ..Self::default()
            }
        }

        fn build(&self) -> Vec<u8> {
            let mut w = Writer::new();
            w.u32(0xF745);
            w.u32(self.guid);
            w.bytes(&EMPTY_MODEL);

            let mut flags = 0;
            if self.placement_id.is_some() {
                flags |= PD_ANIMATION_FRAME;
            }
            if self.parent.is_some() {
                flags |= PD_PARENT;
            }
            w.u32(flags);
            w.u32(self.physics_state);
            if let Some(p) = self.placement_id {
                w.u32(p);
            }
            if let Some((guid, location)) = self.parent {
                w.u32(guid);
                w.u32(location);
            }
            for i in 0..9 {
                w.u16(match i {
                    0 => self.position_seq,
                    1 => self.movement_seq,
                    8 => self.instance_seq,
                    _ => 0,
                });
            }
            w.align(4);

            let wf = self.weenie_flags;
            let wf2 = self.weenie_flags2;
            w.u32(wf);
            w.string16l(self.name).unwrap();
            w.packed_dword(self.weenie_class_id.unwrap_or(0x1234))
                .unwrap();
            w.packed_dword(self.icon_id).unwrap();
            w.u32(self.item_type);
            w.u32(self.object_description_flags);
            w.align(4);
            if self.object_description_flags & 0x0400_0000 != 0 {
                w.u32(wf2);
            }
            if wf & 0x1 != 0 {
                w.string16l(self.plural_name).unwrap();
            }
            if wf & 0x2 != 0 {
                w.i8(self.items_capacity);
            }
            if wf & 0x4 != 0 {
                w.i8(self.containers_capacity);
            }
            if wf & 0x100 != 0 {
                w.u16(self.ammo_type);
            }
            if wf & 0x8 != 0 {
                w.u32(self.value);
            }
            if wf & 0x10 != 0 {
                w.u32(self.useability);
            }
            if wf & 0x20 != 0 {
                w.f32(self.use_radius);
            }
            if wf & 0x8_0000 != 0 {
                w.u32(self.target_type);
            }
            if wf & 0x80 != 0 {
                w.u32(self.ui_effects);
            }
            if wf & 0x200 != 0 {
                w.u8(self.combat_use);
            }
            if wf & 0x400 != 0 {
                w.u16(self.structure);
            }
            if wf & 0x800 != 0 {
                w.u16(self.max_structure);
            }
            if wf & 0x1000 != 0 {
                w.u16(self.stack_size);
            }
            if wf & 0x2000 != 0 {
                w.u16(self.max_stack_size);
            }
            if wf & 0x4000 != 0 {
                w.u32(self.container);
            }
            if wf & 0x8000 != 0 {
                w.u32(self.wielder);
            }
            if wf & 0x1_0000 != 0 {
                w.u32(self.valid_locations);
            }
            if wf & 0x2_0000 != 0 {
                w.u32(self.currently_wielded_location);
            }
            if wf & 0x4_0000 != 0 {
                w.u32(self.priority);
            }
            if wf & 0x10_0000 != 0 {
                w.u8(self.radar_blip_color);
            }
            if wf & 0x80_0000 != 0 {
                w.u8(self.radar_behavior);
            }
            if wf & 0x0800_0000 != 0 {
                w.u16(0);
            }
            if wf & 0x0100_0000 != 0 {
                w.f32(self.workmanship);
            }
            if wf & 0x20_0000 != 0 {
                w.u16(self.burden);
            }
            if wf & 0x40_0000 != 0 {
                w.u16(self.spell_id);
            }
            if wf & 0x0200_0000 != 0 {
                w.u32(self.house_owner_id);
            }
            if wf & 0x0400_0000 != 0 {
                w.u32(0x1000_0002);
                w.u32(u32::from(self.house_open));
                w.u32(self.house_monarch_id);
                w.u32(self.house_guests.len() as u32);
                for &(guest, permission) in self.house_guests {
                    w.u32(guest);
                    w.u32(permission);
                }
            }
            if wf & 0x2000_0000 != 0 {
                w.u32(self.hook_item_types);
            }
            if wf & 0x40 != 0 {
                w.u32(self.monarch_id);
            }
            if wf & 0x1000_0000 != 0 {
                w.u16(self.hook_type);
            }
            if wf & 0x4000_0000 != 0 {
                w.packed_dword(self.icon_overlay_id).unwrap();
            }
            if wf2 & 0x1 != 0 {
                w.packed_dword(self.icon_underlay_id).unwrap();
            }
            if wf & 0x8000_0000 != 0 {
                w.u32(self.material_type);
            }
            if wf2 & 0x2 != 0 {
                w.u32(self.cooldown_id);
            }
            if wf2 & 0x4 != 0 {
                w.f64(self.cooldown_duration);
            }
            if wf2 & 0x8 != 0 {
                w.u32(self.pet_owner_id);
            }
            w.align(4);
            w.into_vec()
        }

        /// Build, check every truncation of the description fails or (for
        /// the 1-3 bytes after the physics block) yields no description,
        /// and parse.
        fn parse(&self) -> (CreateObject, PublicWeenieDesc) {
            let body = self.build();
            let parsed = CreateObject::parse(&body).unwrap();
            assert_truncations(&body);
            let weenie = parsed.weenie.clone().expect("description present");
            (parsed, weenie)
        }
    }

    /// Every prefix that cuts into a field fails. A body that ends 0-3
    /// bytes after the physics block parses with no description; one that
    /// only loses the description's trailing padding parses in full.
    fn assert_truncations(body: &[u8]) {
        let full = CreateObject::parse(body).unwrap();
        let physics_end = body.len() - weenie_len(body);
        let consumed = consumed_len(body);
        for len in 0..body.len() {
            let result = CreateObject::parse(&body[..len]);
            if (physics_end..physics_end + 4).contains(&len) {
                let parsed = result.unwrap_or_else(|e| panic!("length {len}: {e}"));
                assert_eq!(parsed.weenie, None);
                // Debug form, so a NaN field compares equal to itself.
                assert_eq!(
                    format!("{:?}", parsed.physics),
                    format!("{:?}", full.physics)
                );
            } else if len >= consumed {
                assert_eq!(
                    format!("{result:?}"),
                    format!("{:?}", Ok::<_, ProtoError>(&full)),
                    "length {len}"
                );
            } else {
                assert!(result.is_err(), "prefix of length {len} parsed");
            }
        }
    }

    /// Bytes from the start of the description to the end of the body.
    fn weenie_len(body: &[u8]) -> usize {
        let mut r = Reader::new(body);
        r.skip(8).unwrap();
        ModelData::read(&mut r).unwrap();
        PhysicsDesc::read(&mut r).unwrap();
        r.remaining()
    }

    /// Bytes the parser reads: everything but trailing padding.
    fn consumed_len(body: &[u8]) -> usize {
        let mut r = Reader::new(body);
        r.skip(8).unwrap();
        ModelData::read(&mut r).unwrap();
        PhysicsDesc::read(&mut r).unwrap();
        if r.remaining() >= 4 {
            PublicWeenieDesc::read(&mut r).unwrap();
        }
        r.position()
    }

    #[test]
    fn weenie_header_prefix_returns_name_and_item_type() {
        let (p, w) = Desc::new(0x5000_0002, "Drudge", CREATURE).parse();
        assert_eq!(p.guid, 0x5000_0002);
        assert_eq!(w.name, "Drudge");
        assert_eq!(w.item_type, CREATURE);
    }

    #[test]
    fn physics_state_parsed() {
        let (p, _) = Desc {
            physics_state: 0x14,
            ..Desc::new(0x5000_0003, "GhostNpc", CREATURE)
        }
        .parse();
        assert_eq!(p.physics.state, 0x14);
    }

    #[test]
    fn object_description_flags_player_killer_bits() {
        let (_, w) = Desc {
            object_description_flags: 0x8 | 0x20,
            ..Desc::new(0x5000_0004, "+PkPlayer", CREATURE)
        }
        .parse();
        assert_eq!(w.object_description_flags, 0x28);
    }

    #[test]
    fn object_description_flags_pk_lite_bit() {
        let (_, w) = Desc {
            object_description_flags: 0x8 | 0x0200_0000,
            ..Desc::new(0x5000_0005, "+PklPlayer", CREATURE)
        }
        .parse();
        assert_eq!(w.object_description_flags, 0x0200_0008);
    }

    #[test]
    fn no_weenie_flags_leaves_useability_absent() {
        let (_, w) = Desc::new(0x5000_0006, "Holtburg Sign", 0x8000).parse();
        assert_eq!(w.useability, None);
        assert_eq!(w.use_radius, None);
    }

    #[test]
    fn usable_flag_reads_useability() {
        let (_, w) = Desc {
            weenie_flags: 0x10,
            useability: 0x20,
            ..Desc::new(0x5000_0007, "Tirenia", CREATURE)
        }
        .parse();
        assert_eq!(w.useability, Some(0x20));
        let (_, w) = Desc {
            weenie_flags: 0x10,
            useability: 0x01,
            ..Desc::new(0x7A9B_3001, "Holtburg", MISC)
        }
        .parse();
        assert_eq!(w.useability, Some(0x01));
    }

    #[test]
    fn value_usable_and_use_radius_read_in_order() {
        let (_, w) = Desc {
            weenie_flags: 0x8 | 0x10 | 0x20,
            value: 0x1234_5678,
            useability: 0x20,
            use_radius: 2.5,
            ..Desc::new(0x5000_0008, "PriceyDoor", MISC)
        }
        .parse();
        assert_eq!(w.value, Some(0x1234_5678));
        assert_eq!(w.useability, Some(0x20));
        assert_eq!(w.use_radius, Some(2.5));
    }

    #[test]
    fn target_type() {
        let (_, w) = Desc {
            weenie_flags: 0x0008_0000,
            target_type: CREATURE,
            ..Desc::new(0x5000_0009, "Healing Kit", MISC)
        }
        .parse();
        assert_eq!(w.target_type, Some(CREATURE));
    }

    #[test]
    fn combat_use_byte() {
        let (_, w) = Desc {
            weenie_flags: 0x200,
            combat_use: 2,
            ..Desc::new(0x5000_000D, "Sword", MELEE_WEAPON)
        }
        .parse();
        assert_eq!(w.combat_use, Some(2));
    }

    #[test]
    fn ammo_type() {
        let (_, w) = Desc {
            weenie_flags: 0x100,
            ammo_type: 1,
            ..Desc::new(0x5000_000E, "Bow", MISSILE_WEAPON)
        }
        .parse();
        assert_eq!(w.ammo_type, Some(1));
    }

    #[test]
    fn parented_child_placement_parent_and_position_sequence() {
        // Placement LeftHand = 0x0E (Placement enum), parent location
        // LeftHand = 0x08 (the parent location enum).
        let (p, _) = Desc {
            placement_id: Some(0x0E),
            parent: Some((0x5000_0001, 0x08)),
            position_seq: 0x3456,
            instance_seq: 0x789A,
            ..Desc::new(0x6000_0002, "Bow", MISSILE_WEAPON)
        }
        .parse();
        assert_eq!(p.physics.animation_frame, Some(0x0E));
        assert_eq!(
            p.physics.parent,
            Some(PhysicsAttachment {
                guid: 0x5000_0001,
                location_id: 0x08
            })
        );
        assert_eq!(p.physics.timestamps.position, 0x3456);
        assert_eq!(p.physics.timestamps.instance, 0x789A);
    }

    #[test]
    fn radar_fields() {
        let (_, w) = Desc::new(0x5000_000A, "Unspecified Radar Object", MISC).parse();
        assert_eq!((w.radar_blip_color, w.radar_behavior), (None, None));

        let (_, w) = Desc {
            weenie_flags: 0x0010_0000 | 0x0080_0000 | 0x0100_0000,
            radar_blip_color: 4,
            radar_behavior: 4,
            workmanship: 6.25,
            ..Desc::new(0x5000_000B, "Radar Portal", PORTAL)
        }
        .parse();
        assert_eq!(w.radar_blip_color, Some(4));
        assert_eq!(w.radar_behavior, Some(4));
        assert_eq!(w.workmanship, Some(6.25));

        let (_, w) = Desc {
            weenie_flags: 0x0010_0000 | 0x0080_0000,
            ..Desc::new(0x5000_000C, "Explicit Radar Defaults", MISC)
        }
        .parse();
        assert_eq!((w.radar_blip_color, w.radar_behavior), (Some(0), Some(0)));
    }

    #[test]
    fn icon_id_gets_its_type() {
        let (_, w) = Desc {
            icon_id: 0x1234,
            ..Desc::new(0x5000_0009, "SwordIcon", MELEE_WEAPON)
        }
        .parse();
        assert_eq!(w.icon_id, 0x0600_1234);
    }

    #[test]
    fn icon_overlay_from_the_tail() {
        let (_, w) = Desc {
            weenie_flags: 0x4000_0000,
            icon_overlay_id: 0x1ABC,
            ..Desc::new(0x5000_000A, "EnchantedSword", MELEE_WEAPON)
        }
        .parse();
        assert_eq!(w.icon_overlay_id, Some(0x0600_1ABC));
        assert_eq!(w.icon_underlay_id, None);
    }

    #[test]
    fn icon_overlay_and_underlay() {
        let (_, w) = Desc {
            object_description_flags: 0x0400_0000,
            weenie_flags: 0x4000_0000,
            weenie_flags2: 0x1,
            icon_overlay_id: 0x5678,
            icon_underlay_id: 0x9ABC,
            ..Desc::new(0x5000_000B, "MagicRing", JEWELRY)
        }
        .parse();
        assert_eq!(w.header_flags2, Some(0x1));
        assert_eq!(w.icon_overlay_id, Some(0x0600_5678));
        assert_eq!(w.icon_underlay_id, Some(0x0600_9ABC));
    }

    #[test]
    fn no_overlay_bits_common_case() {
        let (_, w) = Desc::new(0x5000_000C, "CommonDrudge", CREATURE).parse();
        assert_eq!(w.name, "CommonDrudge");
        assert_eq!(w.icon_overlay_id, None);
        assert_eq!(w.icon_underlay_id, None);
        assert_eq!(w.useability, None);
        assert_eq!(w.header_flags2, None);
    }

    #[test]
    fn intermediate_fields_before_icon_overlay() {
        let (_, w) = Desc {
            weenie_flags: 0x4000_0000 | 0x0020_0000 | 0x1000 | 0x800 | 0x400,
            structure: 50,
            max_structure: 100,
            stack_size: 1,
            burden: 300,
            icon_overlay_id: 0x2222,
            ..Desc::new(0x5000_000D, "FancySword", MELEE_WEAPON)
        }
        .parse();
        assert_eq!(w.structure, Some(50));
        assert_eq!(w.max_structure, Some(100));
        assert_eq!(w.stack_size, Some(1));
        assert_eq!(w.burden, Some(300));
        assert_eq!(w.icon_overlay_id, Some(0x0600_2222));
    }

    #[test]
    fn empty_house_restrictions_then_icon_overlay() {
        let (_, w) = Desc {
            object_description_flags: 0x0400_0000,
            weenie_flags: 0x0400_0000 | 0x4000_0000,
            weenie_flags2: 0x1,
            icon_overlay_id: 0x3333,
            icon_underlay_id: 0x4444,
            ..Desc::new(0x5000_000E, "HousePortal", PORTAL)
        }
        .parse();
        let h = w.house_restrictions.unwrap();
        assert_eq!(h.version, 0x1000_0002);
        assert!(!h.open_to_public());
        assert_eq!(h.monarch_id, 0);
        assert!(h.guests.is_empty());
        assert_eq!(w.icon_overlay_id, Some(0x0600_3333));
        assert_eq!(w.icon_underlay_id, Some(0x0600_4444));
    }

    #[test]
    fn house_restrictions_with_guests() {
        let (_, w) = Desc {
            weenie_flags: 0x0400_0000 | 0x2000_0000 | 0x40 | 0x1000_0000 | 0x4000_0000,
            house_monarch_id: 0x5000_0500,
            house_guests: &[(0x5000_0001, 0), (0x5000_0002, 1)],
            hook_item_types: 0x1,
            hook_type: 2,
            icon_overlay_id: 0x5555,
            ..Desc::new(0x5000_000F, "HouseCottage", PORTAL)
        }
        .parse();
        let h = w.house_restrictions.unwrap();
        assert!(!h.open_to_public());
        assert_eq!(h.monarch_id, 0x5000_0500);
        assert_eq!(
            h.guests,
            [
                HouseGuest {
                    guest_id: 0x5000_0001,
                    permission: 0
                },
                HouseGuest {
                    guest_id: 0x5000_0002,
                    permission: 1
                }
            ]
        );
        assert_eq!(w.hook_item_types, Some(0x1));
        assert_eq!(w.hook_type, Some(2));
        assert_eq!(w.icon_overlay_id, Some(0x0600_5555));
    }

    #[test]
    fn house_guest_count_beyond_the_body_is_an_error() {
        let mut body = Desc {
            weenie_flags: 0x0400_0000,
            ..Desc::new(0x5000_000F, "HouseCottage", PORTAL)
        }
        .build();
        let count_at = body.len() - 4;
        body[count_at..].copy_from_slice(&0x0100_0001u32.to_le_bytes());
        // The high byte is not part of the count: one guest, no bytes.
        assert_eq!(
            CreateObject::parse(&body),
            Err(ProtoError::CountExceedsPayload {
                what: "house guests",
                count: 1
            })
        );
    }

    #[test]
    fn house_owner_and_monarch() {
        let (_, w) = Desc {
            weenie_flags: 0x0200_0000 | 0x40,
            house_owner_id: 0x5000_0042,
            monarch_id: 0x5000_0777,
            ..Desc::new(0x5000_0012, "HouseDoor", PORTAL)
        }
        .parse();
        assert_eq!(w.house_owner_id, Some(0x5000_0042));
        assert_eq!(w.monarch_id, Some(0x5000_0777));
    }

    #[test]
    fn ui_effects() {
        let (_, w) = Desc {
            weenie_flags: 0x80,
            ui_effects: 0x1,
            ..Desc::new(0x5000_0010, "MagicWand", CASTER)
        }
        .parse();
        assert_eq!(w.ui_effects, Some(0x1));

        let (_, w) = Desc {
            weenie_flags: 0x80 | 0x4000_0000,
            ui_effects: 0x4,
            icon_overlay_id: 0x1ABC,
            ..Desc::new(0x5000_0011, "GlowSword", MELEE_WEAPON)
        }
        .parse();
        assert_eq!(w.ui_effects, Some(0x4));
        assert_eq!(w.icon_overlay_id, Some(0x0600_1ABC));

        let (_, w) = Desc::new(0x5000_0012, "PlainRock", MISC).parse();
        assert_eq!(w.ui_effects, None);
    }

    #[test]
    fn weenie_class_id() {
        let (_, w) = Desc {
            weenie_class_id: Some(0xABCD),
            ..Desc::new(0x5000_0020, "Sword", MELEE_WEAPON)
        }
        .parse();
        assert_eq!(w.weenie_class_id, 0xABCD);
    }

    #[test]
    fn full_item_fields() {
        let (_, w) = Desc {
            weenie_flags: 0x8
                | 0x1000
                | 0x2000
                | 0x0020_0000
                | 0x2
                | 0x4
                | 0x4000
                | 0x8000
                | 0x1_0000
                | 0x2_0000
                | 0x4_0000
                | 0x400
                | 0x800
                | 0x0100_0000,
            value: 250,
            stack_size: 7,
            max_stack_size: 100,
            burden: 42,
            items_capacity: 24,
            containers_capacity: 7,
            container: 0x5000_0099,
            wielder: 0x5000_009A,
            valid_locations: 0x0200_0000,
            currently_wielded_location: 0x0200_0000,
            priority: 8,
            structure: 5,
            max_structure: 10,
            workmanship: 7.5,
            ..Desc::new(0x5000_0021, "Pack", CONTAINER)
        }
        .parse();
        assert_eq!(w.value, Some(250));
        assert_eq!(w.stack_size, Some(7));
        assert_eq!(w.max_stack_size, Some(100));
        assert_eq!(w.burden, Some(42));
        assert_eq!(w.items_capacity, Some(24));
        assert_eq!(w.containers_capacity, Some(7));
        assert_eq!(w.container_id, Some(0x5000_0099));
        assert_eq!(w.wielder_id, Some(0x5000_009A));
        assert_eq!(w.valid_locations, Some(0x0200_0000));
        assert_eq!(w.currently_wielded_location, Some(0x0200_0000));
        assert_eq!(w.priority, Some(8));
        assert_eq!(w.structure, Some(5));
        assert_eq!(w.max_structure, Some(10));
        assert_eq!(w.workmanship, Some(7.5));
    }

    #[test]
    fn capacity_bytes_are_signed() {
        let (_, w) = Desc {
            weenie_class_id: Some(28249),
            weenie_flags: 0x2 | 0x4,
            items_capacity: -1,
            containers_capacity: -1,
            ..Desc::new(0x5000_0029, "Black Phyntos Hive", CREATURE)
        }
        .parse();
        assert_eq!(w.weenie_class_id, 28249);
        assert_eq!(w.items_capacity, Some(-1));
        assert_eq!(w.containers_capacity, Some(-1));
    }

    #[test]
    fn hook_identity_fields_do_not_misalign_the_overlay() {
        let (_, w) = Desc {
            weenie_flags: 0x2000_0000 | 0x1000_0000 | 0x4000_0000,
            hook_item_types: CONTAINER,
            hook_type: 4,
            icon_overlay_id: 0x4567,
            ..Desc::new(0x5000_0027, "Black Phyntos Hive", CONTAINER)
        }
        .parse();
        assert_eq!(w.hook_item_types, Some(CONTAINER));
        assert_eq!(w.hook_type, Some(4));
        assert_eq!(w.icon_overlay_id, Some(0x0600_4567));

        let (_, w) = Desc::new(0x5000_0028, "Ordinary Chest", CONTAINER).parse();
        assert_eq!((w.hook_item_types, w.hook_type), (None, None));
    }

    #[test]
    fn movement_sequence_from_the_counters() {
        let (p, _) = Desc {
            movement_seq: 0x9000,
            ..Desc::new(0x5000_0030, "Runner", 0x10)
        }
        .parse();
        assert_eq!(p.physics.timestamps.movement, 0x9000);
    }

    #[test]
    fn mid_tail_fields_still_reach_icon_overlay() {
        let (_, w) = Desc {
            weenie_flags: 0x1000 | 0x4000 | 0x4000_0000,
            stack_size: 1,
            container: 0x5000_00F0,
            icon_overlay_id: 0x4321,
            ..Desc::new(0x5000_0022, "Ring", JEWELRY)
        }
        .parse();
        assert_eq!(w.icon_overlay_id, Some(0x0600_4321));
        assert_eq!(w.container_id, Some(0x5000_00F0));
    }

    #[test]
    fn plural_name() {
        let (_, w) = Desc {
            weenie_flags: 0x1 | 0x1000,
            stack_size: 2,
            plural_name: "Pyreal Scarabs",
            ..Desc::new(0x5000_0023, "Pyreal Scarab", MISC)
        }
        .parse();
        assert_eq!(w.plural_name.as_deref(), Some("Pyreal Scarabs"));
        assert_eq!(w.stack_size, Some(2));
    }

    #[test]
    fn second_header_pet_owner_after_preceding_fields() {
        let (_, w) = Desc {
            object_description_flags: 0x0400_0000,
            weenie_flags: 0x8000_0000,
            weenie_flags2: 0xE,
            material_type: 7,
            cooldown_id: 11,
            cooldown_duration: 12.5,
            pet_owner_id: 0x5000_0001,
            ..Desc::new(0x5000_0024, "Combat Pet", CREATURE)
        }
        .parse();
        assert_eq!(w.material_type, Some(7));
        assert_eq!(w.cooldown_id, Some(11));
        assert_eq!(w.cooldown_duration, Some(12.5));
        assert_eq!(w.pet_owner_id, Some(0x5000_0001));
    }

    #[test]
    fn absent_cooldown_fields() {
        let (_, w) = Desc {
            object_description_flags: 0x0400_0000,
            ..Desc::new(0x5000_0026, "Ordinary Stone", MISC)
        }
        .parse();
        assert_eq!(w.header_flags2, Some(0));
        assert_eq!((w.cooldown_id, w.cooldown_duration), (None, None));
    }

    #[test]
    fn spell_id() {
        let (_, w) = Desc {
            weenie_flags: 0x40_0000,
            spell_id: 0x0A6E,
            ..Desc::new(0x5000_0025, "Orb", CASTER)
        }
        .parse();
        assert_eq!(w.spell_id, Some(0x0A6E));
    }

    #[test]
    fn physics_script_is_read_not_skipped() {
        let (_, w) = Desc {
            weenie_flags: 0x0800_0000 | 0x0100_0000,
            workmanship: 3.0,
            ..Desc::new(0x5000_0031, "Scripted", MISC)
        }
        .parse();
        assert_eq!(w.physics_script, Some(0));
        assert_eq!(w.workmanship, Some(3.0));
    }

    #[test]
    fn wrong_opcode_is_an_error() {
        let mut body = Desc::new(1, "x", MISC).build();
        body[..4].copy_from_slice(&0xF7DBu32.to_le_bytes());
        assert!(matches!(
            CreateObject::parse(&body),
            Err(ProtoError::WrongOpcode {
                expected: 0xF745,
                actual: 0xF7DB
            })
        ));
        assert!(CreateObject::parse_update(&body).is_ok());
    }

    // --- ProjectileVfxPacketFixtures.cs ---

    fn golden_projectile() -> Vec<u8> {
        let mut w = Writer::new();
        let flags = PD_SETUP
            | PD_MOTION_TABLE
            | PD_VELOCITY
            | PD_ACCELERATION
            | PD_OMEGA
            | PD_PARENT
            | PD_CHILDREN
            | PD_OBJ_SCALE
            | PD_FRICTION
            | PD_ELASTICITY
            | PD_SOUND_TABLE
            | PD_PHYSICS_SCRIPT_TABLE
            | PD_DEFAULT_SCRIPT
            | PD_DEFAULT_SCRIPT_INTENSITY
            | PD_POSITION
            | PD_TRANSLUCENCY;
        w.u32(0xF745);
        w.u32(0x5000_0042);
        w.bytes(&EMPTY_MODEL);
        w.u32(flags);
        w.u32(0x0000_0748);
        w.u32(0x7F01_0123);
        for v in [12.5, 25.0, 3.75, 1.0, 0.0, 0.0, 0.0] {
            w.f32(v);
        }
        w.u32(0x0900_0001);
        w.u32(0x2000_0014);
        w.u32(0x3400_0005);
        w.u32(0x0200_040D);
        w.u32(0x5000_0010);
        w.u32(3);
        w.u32(1);
        w.u32(0x5000_0011);
        w.u32(4);
        for v in [
            1.25, 0.125, 0.05, 0.25, 10.0, 20.0, 30.0, 1.0, 2.0, 3.0, 0.1, 0.2, 0.3,
        ] {
            w.f32(v);
        }
        w.u32(0xA5A5_A5A5);
        w.u32(0x7FC0_1234);
        for i in 0..9u16 {
            w.u16(0xFFF8u16.wrapping_add(i));
        }
        w.align(4);
        w.u32(0);
        w.string16l("Golden Projectile").unwrap();
        w.packed_dword(7277).unwrap();
        w.packed_dword(0).unwrap();
        w.u32(0x100);
        w.u32(0);
        w.align(4);
        w.into_vec()
    }

    fn branch_fixture(flags: u32, guid: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xF745);
        w.u32(guid);
        w.bytes(&EMPTY_MODEL);
        w.u32(flags);
        w.u32(0);
        if flags & PD_MOVEMENT != 0 || flags & PD_ANIMATION_FRAME != 0 {
            w.u32(0);
        }
        if flags & PD_PHYSICS_SCRIPT_TABLE != 0 {
            w.u32(0);
        }
        for i in 0..9u16 {
            w.u16(0x1100 + i);
        }
        w.align(4);
        w.u32(0);
        w.string16l("Physics Branch Fixture").unwrap();
        w.packed_dword(7277).unwrap();
        w.packed_dword(0).unwrap();
        w.u32(0x100);
        w.u32(0);
        w.align(4);
        w.into_vec()
    }

    #[test]
    fn golden_projectile_physics_fields_stay_aligned() {
        let body = golden_projectile();
        let p = CreateObject::parse(&body).unwrap();
        let ph = &p.physics;
        assert_eq!(p.guid, 0x5000_0042);
        assert_eq!(p.weenie.as_ref().unwrap().name, "Golden Projectile");
        assert_eq!(p.weenie.as_ref().unwrap().weenie_class_id, 7277);
        assert_eq!(ph.state, 0x0000_0748);
        assert_eq!(
            ph.position,
            Some(Position {
                cell_id: 0x7F01_0123,
                origin: Vector3 {
                    x: 12.5,
                    y: 25.0,
                    z: 3.75
                },
                orientation: Quaternion::IDENTITY,
            })
        );
        assert_eq!(ph.setup_id, Some(0x0200_040D));
        assert_eq!(ph.motion_table_id, Some(0x0900_0001));
        assert_eq!(ph.sound_table_id, Some(0x2000_0014));
        assert_eq!(ph.physics_script_table_id, Some(0x3400_0005));
        assert_eq!(
            ph.parent,
            Some(PhysicsAttachment {
                guid: 0x5000_0010,
                location_id: 3
            })
        );
        assert_eq!(
            ph.children.as_deref(),
            Some(
                &[PhysicsAttachment {
                    guid: 0x5000_0011,
                    location_id: 4
                }][..]
            )
        );
        assert_eq!(ph.scale, Some(1.25));
        assert_eq!(ph.friction, Some(0.125));
        assert_eq!(ph.elasticity, Some(0.05));
        assert_eq!(ph.translucency, Some(0.25));
        assert_eq!(
            ph.velocity,
            Some(Vector3 {
                x: 10.0,
                y: 20.0,
                z: 30.0
            })
        );
        assert_eq!(
            ph.acceleration,
            Some(Vector3 {
                x: 1.0,
                y: 2.0,
                z: 3.0
            })
        );
        assert_eq!(
            ph.omega,
            Some(Vector3 {
                x: 0.1,
                y: 0.2,
                z: 0.3
            })
        );
        assert_eq!(ph.default_script, Some(0xA5A5_A5A5));
        assert_eq!(
            ph.default_script_intensity.map(f32::to_bits),
            Some(0x7FC0_1234)
        );
        assert_eq!(
            ph.timestamps,
            PhysicsTimestamps {
                position: 0xFFF8,
                movement: 0xFFF9,
                state: 0xFFFA,
                vector: 0xFFFB,
                teleport: 0xFFFC,
                server_controlled_move: 0xFFFD,
                force_position: 0xFFFE,
                obj_desc: 0xFFFF,
                instance: 0,
            }
        );
    }

    #[test]
    fn golden_projectile_rejects_every_truncation_inside_the_physics_block() {
        let body = golden_projectile();
        for len in 0..168 {
            assert!(CreateObject::parse(&body[..len]).is_err(), "length {len}");
        }
        let p = CreateObject::parse(&body[..168]).unwrap();
        assert_eq!(p.weenie, None);
        assert_truncations(&body);
    }

    #[test]
    fn movement_and_animation_frame_fixtures_stay_aligned() {
        let movement = CreateObject::parse(&branch_fixture(PD_MOVEMENT, 0x5000_0051)).unwrap();
        let animation =
            CreateObject::parse(&branch_fixture(PD_ANIMATION_FRAME, 0x5000_0052)).unwrap();
        assert_eq!(movement.guid, 0x5000_0051);
        assert_eq!(animation.guid, 0x5000_0052);
        assert_eq!(movement.physics.animation_frame, None);
        assert_eq!(movement.physics.movement, Some(PhysicsMovement::Empty));
        assert_eq!(animation.physics.animation_frame, Some(0));
        assert_eq!(animation.physics.movement, None);
        assert_eq!(movement.physics.timestamps.instance, 0x1108);
        assert_eq!(animation.physics.timestamps.instance, 0x1108);
    }

    #[test]
    fn physics_script_table_absent_versus_present_zero() {
        let present = branch_fixture(PD_PHYSICS_SCRIPT_TABLE, 0x5000_0053);
        let absent = branch_fixture(0, 0x5000_0054);
        assert_eq!(present.len(), absent.len() + 4);
        assert_eq!(
            CreateObject::parse(&present)
                .unwrap()
                .physics
                .physics_script_table_id,
            Some(0)
        );
        assert_eq!(
            CreateObject::parse(&absent)
                .unwrap()
                .physics
                .physics_script_table_id,
            None
        );
        assert_truncations(&present);
    }

    // --- Movement blob (new; same reader as the motion update) ---

    fn with_movement(blob: &[u8], autonomous: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xF745);
        w.u32(0x5000_0060);
        w.bytes(&EMPTY_MODEL);
        w.u32(PD_MOVEMENT | PD_ANIMATION_FRAME);
        w.u32(0);
        w.u32(blob.len() as u32);
        w.bytes(blob);
        w.u32(autonomous);
        for _ in 0..9 {
            w.u16(0);
        }
        w.align(4);
        w.into_vec()
    }

    #[test]
    fn movement_blob_is_parsed_and_kept() {
        // Interpreted state: run forward at 1.5, stance 0x3D, then the
        // padding the server writes.
        let mut m = Writer::new();
        m.u8(0);
        m.u8(0);
        m.u16(0x003D);
        m.u32(0x2 | 0x4);
        m.u16(0x0007);
        m.f32(1.5);
        m.align(4);
        let blob = m.into_vec();
        let body = with_movement(&blob, 1);
        let p = CreateObject::parse(&body).unwrap();
        let Some(PhysicsMovement::Present {
            raw,
            data,
            is_autonomous,
        }) = &p.physics.movement
        else {
            panic!("expected movement");
        };
        assert_eq!(raw, &blob);
        assert!(is_autonomous);
        assert_eq!(data.stance(), 0x003D);
        let s = data.interpreted().unwrap();
        assert_eq!((s.forward_command, s.forward_speed), (Some(7), Some(1.5)));
        // Animation frame is not read when movement is present.
        assert_eq!(p.physics.animation_frame, None);
        assert_eq!(
            MovementData::read(&mut Reader::new(&blob)).as_ref(),
            Ok(data)
        );
        assert_truncations(&body);
    }

    #[test]
    fn movement_length_beyond_the_body_is_an_error() {
        let mut body = with_movement(&[0, 0, 0x3D, 0, 0, 0, 0, 0], 0);
        body[20..24].copy_from_slice(&0x1000u32.to_le_bytes());
        assert_eq!(
            CreateObject::parse(&body),
            Err(ProtoError::CountExceedsPayload {
                what: "movement bytes",
                count: 0x1000
            })
        );
    }

    #[test]
    fn malformed_movement_blob_is_an_error() {
        // A move-to-position header with no path in a 4-byte blob.
        let body = with_movement(&[7, 0, 0x3D, 0], 0);
        assert!(CreateObject::parse(&body).is_err());
    }

    #[test]
    fn children_count_is_checked() {
        let mut w = Writer::new();
        w.u32(0xF745);
        w.u32(1);
        w.bytes(&EMPTY_MODEL);
        w.u32(PD_CHILDREN);
        w.u32(0);
        w.i32(-1);
        assert_eq!(
            CreateObject::parse(w.as_slice()),
            Err(ProtoError::CountOutOfRange {
                what: "physics children",
                count: u32::MAX,
                max: MAX_CHILDREN
            })
        );
        let mut w = Writer::new();
        w.u32(0xF745);
        w.u32(1);
        w.bytes(&EMPTY_MODEL);
        w.u32(PD_CHILDREN);
        w.u32(0);
        w.i32(1025);
        assert!(matches!(
            CreateObject::parse(w.as_slice()),
            Err(ProtoError::CountOutOfRange { count: 1025, .. })
        ));
    }

    // --- WorldSessionUpdateObjectTests.cs (message part) ---

    /// `BuildDescription`: empty appearance, empty physics, then the
    /// description with an optional icon underlay behind the second flag
    /// word.
    fn description(opcode: u32, name: &str, icon_underlay_id: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(opcode);
        w.u32(0x5000_0042);
        w.bytes(&EMPTY_MODEL);
        w.u32(0);
        w.u32(0);
        for _ in 0..9 {
            w.u16(0);
        }
        w.align(4);
        let object_description_flags = if icon_underlay_id != 0 {
            0x0400_0000
        } else {
            0
        };
        let flags2 = if icon_underlay_id != 0 { 1 } else { 0 };
        w.u32(0);
        w.string16l(name).unwrap();
        w.packed_dword(0x1234).unwrap();
        w.packed_dword(0).unwrap();
        w.u32(0);
        w.u32(object_description_flags);
        w.align(4);
        if object_description_flags != 0 {
            w.u32(flags2);
        }
        if flags2 & 1 != 0 {
            w.packed_dword(icon_underlay_id).unwrap();
        }
        w.align(4);
        w.into_vec()
    }

    #[test]
    fn update_object_carries_the_same_description() {
        let body = description(0xF7DB, "Sword", 0x0600_1234);
        let parsed = CreateObject::parse_update(&body).unwrap();
        assert_eq!(parsed.guid, 0x5000_0042);
        assert_eq!(
            parsed.weenie.as_ref().unwrap().icon_underlay_id,
            Some(0x0600_1234)
        );
        assert!(matches!(
            CreateObject::parse(&body),
            Err(ProtoError::WrongOpcode { .. })
        ));
        let create = description(0xF745, "Sword", 0x0600_1234);
        assert_eq!(CreateObject::parse(&create), Ok(parsed));
        assert!(CreateObject::parse_update(&create).is_err());
        assert_every_prefix_fails(&body[..body.len() - weenie_len(&body)], |b| {
            CreateObject::parse_update(b)
        });
    }
}

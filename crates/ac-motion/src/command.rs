//! Motion command classes and the wire catalogs.
//!
//! The wire carries only a command's low 16 bits; the receiver restores the
//! full 32-bit command (class bits included) from a catalog. Two numberings
//! exist: the one the server and the dat files use, and an older client
//! table that numbers every command from `0x115` on three lower. The live
//! client uses the first ([`reconstruct_full_command`]); the older table is
//! kept for conformance work only.

use crate::motion_command;

/// Stance (style) commands have bit 31 set.
pub const STYLE_BIT: u32 = 0x8000_0000;
/// Looping states (substates) have bit 30 set.
pub const SUBSTATE_BIT: u32 = 0x4000_0000;
/// Modifiers (overlays on a cycle) have bit 29 set.
pub const MODIFIER_BIT: u32 = 0x2000_0000;
/// Actions (one-shot motions) have bit 28 set.
pub const ACTION_BIT: u32 = 0x1000_0000;

/// The reference name of a full command.
#[must_use]
pub fn command_name(command: u32) -> Option<&'static str> {
    motion_command::ALL
        .iter()
        .find(|&&(_, value)| value == command)
        .map(|&(name, _)| name)
}

/// Restores full motion commands from their 16-bit wire form.
pub trait MotionCommandCatalog {
    /// The full command for `wire_command`, or 0 when none matches.
    fn reconstruct_full_command(&self, wire_command: u16) -> u32;
}

/// The server's numbering (the same as the dat files'). When several
/// commands share the low 16 bits, the lowest class byte wins.
#[derive(Debug, Clone, Copy, Default)]
pub struct AceModernCommandCatalog;

impl MotionCommandCatalog for AceModernCommandCatalog {
    fn reconstruct_full_command(&self, wire_command: u16) -> u32 {
        if wire_command == 0 {
            return 0;
        }
        let mut best: Option<u32> = None;
        for &(_, full) in &motion_command::ALL {
            if full & 0xFFFF != u32::from(wire_command) {
                continue;
            }
            best = Some(match best {
                Some(b) => resolve_class_priority(&[b, full]),
                None => full,
            });
        }
        best.unwrap_or(0)
    }
}

/// Of commands sharing the low 16 bits, the one with the lowest class byte
/// (the first such in `candidates`); 0 for an empty list.
#[must_use]
pub fn resolve_class_priority(candidates: &[u32]) -> u32 {
    let Some((&first, rest)) = candidates.split_first() else {
        return 0;
    };
    let mut best = first;
    for &candidate in rest {
        if (candidate >> 24) < (best >> 24) {
            best = candidate;
        }
    }
    best
}

/// The older client's own command table, indexed by wire value.
#[derive(Debug, Clone, Copy, Default)]
pub struct Retail2013CommandCatalog;

impl MotionCommandCatalog for Retail2013CommandCatalog {
    fn reconstruct_full_command(&self, wire_command: u16) -> u32 {
        RETAIL_2013_COMMAND_IDS
            .get(usize::from(wire_command))
            .copied()
            .unwrap_or(0)
    }
}

/// The live mapping from wire commands to full commands (the server's
/// numbering).
#[must_use]
pub fn reconstruct_full_command(wire_command: u16) -> u32 {
    AceModernCommandCatalog.reconstruct_full_command(wire_command)
}

/// The older client's table, one entry per wire value `0..0x198`.
const RETAIL_2013_COMMAND_IDS: [u32; 0x198] = [
    0x80000000, 0x85000001, 0x85000002, 0x41000003, 0x40000004, 0x45000005, 0x45000006, 0x44000007,
    0x40000008, 0x40000009, 0x4000000A, 0x4000000B, 0x4000000C, 0x6500000D, 0x6500000E, 0x6500000F,
    0x65000010, 0x40000011, 0x41000012, 0x41000013, 0x41000014, 0x40000015, 0x40000016, 0x40000017,
    0x40000018, 0x40000019, 0x4000001A, 0x4000001B, 0x4000001C, 0x4000001D, 0x4000001E, 0x4000001F,
    0x40000020, 0x40000021, 0x40000022, 0x40000023, 0x40000024, 0x40000025, 0x40000026, 0x40000027,
    0x40000028, 0x40000029, 0x4000002A, 0x4000002B, 0x4000002C, 0x4000002D, 0x4000002E, 0x4000002F,
    0x40000030, 0x40000031, 0x40000032, 0x40000033, 0x40000034, 0x40000035, 0x40000036, 0x40000037,
    0x40000038, 0x40000039, 0x2000003A, 0x2500003B, 0x8000003C, 0x8000003D, 0x8000003E, 0x8000003F,
    0x80000040, 0x80000041, 0x80000042, 0x80000043, 0x80000044, 0x80000045, 0x80000046, 0x80000047,
    0x80000048, 0x80000049, 0x1000004A, 0x1000004B, 0x1300004C, 0x1000004D, 0x1000004E, 0x1000004F,
    0x10000050, 0x10000051, 0x10000052, 0x10000053, 0x10000054, 0x10000055, 0x10000056, 0x10000057,
    0x10000058, 0x10000059, 0x1000005A, 0x1000005B, 0x1000005C, 0x1000005D, 0x1000005E, 0x1000005F,
    0x10000060, 0x10000061, 0x10000062, 0x10000063, 0x10000064, 0x10000065, 0x10000066, 0x10000067,
    0x10000068, 0x10000069, 0x1000006A, 0x1000006B, 0x1000006C, 0x1000006D, 0x1000006E, 0x1000006F,
    0x10000070, 0x10000071, 0x10000072, 0x10000073, 0x10000074, 0x10000075, 0x10000076, 0x10000077,
    0x10000078, 0x13000079, 0x1300007A, 0x1300007B, 0x1300007C, 0x1300007D, 0x1300007E, 0x1300007F,
    0x13000080, 0x13000081, 0x13000082, 0x13000083, 0x13000084, 0x13000085, 0x13000086, 0x13000087,
    0x13000088, 0x13000089, 0x1300008A, 0x1300008B, 0x1300008C, 0x1300008D, 0x1300008E, 0x1300008F,
    0x13000090, 0x13000091, 0x13000092, 0x13000093, 0x13000094, 0x13000095, 0x13000096, 0x13000097,
    0x13000098, 0x13000099, 0x1300009A, 0x1200009B, 0x1000009C, 0x1000009D, 0x1000009E, 0x1000009F,
    0x100000A0, 0x100000A1, 0x080000A2, 0x090000A3, 0x090000A4, 0x090000A5, 0x090000A6, 0x090000A7,
    0x090000A8, 0x080000A9, 0x090000AA, 0x090000AB, 0x090000AC, 0x090000AD, 0x090000AE, 0x090000AF,
    0x090000B0, 0x090000B1, 0x0D0000B2, 0x0D0000B3, 0x0D0000B4, 0x080000B5, 0x080000B6, 0x080000B7,
    0x090000B8, 0x090000B9, 0x0D0000BA, 0x0D0000BB, 0x0D0000BC, 0x0D0000BD, 0x0D0000BE, 0x0D0000BF,
    0x090000C0, 0x0C0000C1, 0x090000C2, 0x090000C3, 0x090000C4, 0x0D0000C5, 0x090000C6, 0x090000C7,
    0x090000C8, 0x090000C9, 0x130000CA, 0x130000CB, 0x130000CC, 0x100000CD, 0x100000CE, 0x100000CF,
    0x100000D0, 0x100000D1, 0x100000D2, 0x400000D3, 0x120000D4, 0x090000D5, 0x090000D6, 0x090000D7,
    0x090000D8, 0x090000D9, 0x090000DA, 0x090000DB, 0x090000DC, 0x090000DD, 0x090000DE, 0x120000DF,
    0x400000E0, 0x400000E1, 0x100000E2, 0x100000E3, 0x400000E4, 0x400000E5, 0x400000E6, 0x090000E7,
    0x800000E8, 0x800000E9, 0x430000EA, 0x430000EB, 0x430000EC, 0x430000ED, 0x430000EE, 0x430000EF,
    0x430000F0, 0x430000F1, 0x430000F2, 0x430000F3, 0x430000F4, 0x430000F5, 0x430000F6, 0x430000F7,
    0x430000F8, 0x420000F9, 0x430000FA, 0x430000FB, 0x430000FC, 0x430000FD, 0x090000FE, 0x090000FF,
    0x09000100, 0x09000101, 0x09000102, 0x09000103, 0x09000104, 0x09000105, 0x09000106, 0x09000107,
    0x09000108, 0x09000109, 0x0900010A, 0x0900010B, 0x0900010C, 0x0900010D, 0x1000010E, 0x0900010F,
    0x09000110, 0x09000111, 0x09000112, 0x09000113, 0x09000114, 0x43000115, 0x13000116, 0x43000117,
    0x43000118, 0x43000119, 0x0900011A, 0x1000011B, 0x1000011C, 0x1000011D, 0x1000011E, 0x1000011F,
    0x10000120, 0x10000121, 0x10000122, 0x10000123, 0x10000124, 0x10000125, 0x10000126, 0x10000127,
    0x10000128, 0x10000129, 0x1000012A, 0x1000012B, 0x1000012C, 0x1000012D, 0x1000012E, 0x1000012F,
    0x10000130, 0x10000131, 0x13000132, 0x40000133, 0x40000134, 0x40000135, 0x40000136, 0x10000137,
    0x80000138, 0x80000139, 0x4300013A, 0x4300013B, 0x4300013C, 0x4300013D, 0x4300013E, 0x4300013F,
    0x43000140, 0x43000141, 0x43000142, 0x43000143, 0x43000144, 0x43000145, 0x43000146, 0x13000147,
    0x13000148, 0x13000149, 0x1300014A, 0x1300014B, 0x1300014C, 0x1300014D, 0x1300014E, 0x1300014F,
    0x10000150, 0x09000151, 0x09000152, 0x09000153, 0x09000154, 0x09000155, 0x09000156, 0x09000157,
    0x09000158, 0x09000159, 0x0900015A, 0x0900015B, 0x0900015C, 0x0900015D, 0x0900015E, 0x0900015F,
    0x09000160, 0x09000161, 0x10000162, 0x10000163, 0x10000164, 0x09000165, 0x09000166, 0x09000167,
    0x09000168, 0x09000169, 0x0900016A, 0x0900016B, 0x0900016C, 0x0900016D, 0x1000016E, 0x1000016F,
    0x10000170, 0x10000171, 0x10000172, 0x10000173, 0x10000174, 0x10000175, 0x10000176, 0x10000177,
    0x10000178, 0x10000179, 0x1000017A, 0x1000017B, 0x1000017C, 0x1000017D, 0x1000017E, 0x1000017F,
    0x10000180, 0x10000181, 0x10000182, 0x10000183, 0x10000184, 0x10000185, 0x10000186, 0x10000187,
    0x10000188, 0x10000189, 0x1000018A, 0x1000018B, 0x1000018C, 0x1000018D, 0x1000018E, 0x1000018F,
    0x10000190, 0x10000191, 0x10000192, 0x10000193, 0x10000194, 0x10000195, 0x10000196, 0x10000197,
];

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `MotionCommandCatalogTests` and
    //! `MotionCommandResolverTests`; pins above `0x115` checked against the
    //! server's motion command numbering.
    use super::*;

    #[test]
    fn the_command_table_has_every_reference_value() {
        assert_eq!(motion_command::ALL.len(), 409);
        let mut values: Vec<u32> = motion_command::ALL.iter().map(|&(_, v)| v).collect();
        values.sort_unstable();
        values.dedup();
        assert_eq!(values.len(), 409, "no duplicate values");
    }

    #[test]
    fn commands_from_0x115_use_the_server_numbering() {
        // Older-client values are three lower from SnowAngelState on.
        assert_eq!(motion_command::SNOW_ANGEL_STATE, 0x4300_0118);
        assert_eq!(motion_command::ATLATL_COMBAT, 0x8000_013B);
        assert_eq!(motion_command::THROWN_SHIELD_COMBAT, 0x8000_013C);
        assert_eq!(motion_command::AT_EASE_STATE, 0x4300_0149);
        assert_eq!(motion_command::HELPER, 0x1300_0135);
        assert_eq!(motion_command::LIFESTONE_RECALL, 0x1000_0153);
        assert_eq!(motion_command::HOUSE_RECALL, 0x1000_013A);
        assert_eq!(command_name(0x4500_0005), Some("WalkForward"));
        assert_eq!(command_name(0x1234_5678), None);
    }

    #[test]
    fn server_catalog_resolves_shifted_recalls_and_actions() {
        for (wire, expected) in [
            (0x0153, 0x1000_0153),
            (0x0166, 0x1000_0166),
            (0x0171, 0x1000_0171),
            (0x0173, 0x1000_0173),
        ] {
            assert_eq!(reconstruct_full_command(wire), expected, "{wire:#x}");
        }
    }

    #[test]
    fn server_catalog_matches_the_resolver_matrix() {
        for (wire, expected) in [
            (0x0003, 0x4100_0003),
            (0x0005, 0x4500_0005),
            (0x0007, 0x4400_0007),
            (0x0006, 0x4500_0006),
            (0x000D, 0x6500_000D),
            (0x000E, 0x6500_000E),
            (0x000F, 0x6500_000F),
            (0x0015, 0x4000_0015),
            (0x0011, 0x4000_0011),
            (0x0012, 0x4100_0012),
            (0x0013, 0x4100_0013),
            (0x0014, 0x4100_0014),
            (0x0057, 0x1000_0057),
            (0x0058, 0x1000_0058),
            (0x005B, 0x1000_005B),
            (0x0061, 0x1000_0061),
            (0x004B, 0x1000_004B),
            (0x0050, 0x1000_0050),
            (0x0087, 0x1300_0087),
            (0x0080, 0x1300_0080),
            (0x007D, 0x1300_007D),
        ] {
            assert_eq!(reconstruct_full_command(wire), expected, "{wire:#x}");
            assert_eq!(
                AceModernCommandCatalog.reconstruct_full_command(wire),
                expected
            );
        }
        assert_eq!(reconstruct_full_command(0), 0);
        assert_eq!(reconstruct_full_command(0xFFFF), 0);
    }

    #[test]
    fn older_catalog_resolves_unshifted_values() {
        let c = Retail2013CommandCatalog;
        for (wire, expected) in [
            (0x0150, 0x1000_0150),
            (0x0163, 0x1000_0163),
            (0x016E, 0x1000_016E),
            (0x0170, 0x1000_0170),
            (0x0000, 0x8000_0000),
            (0x0003, 0x4100_0003),
            (0x0005, 0x4500_0005),
            (0x0007, 0x4400_0007),
            (0x000D, 0x6500_000D),
            (0x0153, 0x0900_0153),
            (0x0197, 0x1000_0197),
        ] {
            assert_eq!(c.reconstruct_full_command(wire), expected, "{wire:#x}");
        }
        assert_eq!(c.reconstruct_full_command(0x0198), 0);
        assert_eq!(c.reconstruct_full_command(0xFFFF), 0);
    }

    #[test]
    fn lowest_class_byte_wins_whatever_the_order() {
        assert_eq!(
            resolve_class_priority(&[0x8000_0042, 0x2000_0042, 0x1000_0042]),
            0x1000_0042
        );
        let forward = [0x8000_0099, 0x4100_0099, 0x1000_0099];
        let reversed = [0x1000_0099, 0x4100_0099, 0x8000_0099];
        assert_eq!(
            resolve_class_priority(&forward),
            resolve_class_priority(&reversed)
        );
        assert_eq!(resolve_class_priority(&forward), 0x1000_0099);
        assert_eq!(resolve_class_priority(&[]), 0);
    }
}

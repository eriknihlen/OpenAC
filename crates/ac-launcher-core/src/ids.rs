//! Unique names for temporary files, staging folders and sessions: 32
//! lowercase hex digits, the shape of a GUID written without separators
//! (OpenAC's `Guid.ToString("N")`).

use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

static COUNTER: AtomicU64 = AtomicU64::new(0);

fn mixed() -> u64 {
    // Each `RandomState` carries keys seeded from the operating system's
    // random source; hashing the time, the process and a counter under them
    // gives names that do not repeat within or across processes.
    let mut hasher = RandomState::new().build_hasher();
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|since| since.as_nanos())
        .unwrap_or_default();
    hasher.write_u128(nanos);
    hasher.write_u32(std::process::id());
    hasher.write_u64(COUNTER.fetch_add(1, Ordering::Relaxed));
    hasher.finish()
}

/// 32 lowercase hex digits, different on every call. Not for secrets.
pub fn unique_hex32() -> String {
    format!("{:016x}{:016x}", mixed(), mixed())
}

/// True when `text` is exactly 32 lowercase hex digits.
pub fn is_hex32(text: &str) -> bool {
    text.len() == 32
        && text
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_are_hex_and_distinct() {
        let first = unique_hex32();
        let second = unique_hex32();
        assert!(is_hex32(&first) && is_hex32(&second));
        assert_ne!(first, second);
        assert!(!is_hex32("ABCDEF0123456789abcdef0123456789"));
    }
}

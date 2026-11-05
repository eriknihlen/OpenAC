//! Random characters from the operating system's generator, for the
//! Digest client nonce (.NET `RandomNumberGenerator.GetString`).

/// `length` characters drawn uniformly from `alphabet` (at most 256
/// characters) with the system's cryptographic generator.
pub(crate) fn string(alphabet: &[u8], length: usize) -> Result<String, String> {
    debug_assert!(!alphabet.is_empty() && alphabet.len() <= 256);
    // Bytes at or past the largest multiple of the alphabet's size are
    // drawn again, so every character is equally likely.
    let limit = 256 - 256 % alphabet.len();
    let mut out = String::with_capacity(length);
    while out.len() < length {
        let mut bytes = [0u8; 64];
        fill(&mut bytes)?;
        for byte in bytes {
            if usize::from(byte) < limit && out.len() < length {
                out.push(char::from(alphabet[usize::from(byte) % alphabet.len()]));
            }
        }
    }
    Ok(out)
}

#[cfg(windows)]
#[allow(unsafe_code)]
fn fill(bytes: &mut [u8]) -> Result<(), String> {
    use windows_sys::Win32::Security::Cryptography::{
        BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom,
    };
    let length = u32::try_from(bytes.len()).map_err(|_| "too many random bytes".to_owned())?;
    // SAFETY: the buffer is valid for `length` bytes; no algorithm handle is
    // needed with the system-preferred generator.
    let status = unsafe {
        BCryptGenRandom(
            std::ptr::null_mut(),
            bytes.as_mut_ptr(),
            length,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        )
    };
    if status == 0 {
        Ok(())
    } else {
        Err(format!("the system random generator failed ({status:#x})"))
    }
}

#[cfg(not(windows))]
fn fill(bytes: &mut [u8]) -> Result<(), String> {
    use std::io::Read;
    std::fs::File::open("/dev/urandom")
        .and_then(|mut file| file.read_exact(bytes))
        .map_err(|error| format!("the system random generator failed: {error}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn random_strings_use_the_alphabet_and_differ() {
        let alphabet = b"0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        let a = string(alphabet, 16).unwrap();
        let b = string(alphabet, 16).unwrap();
        assert_eq!(a.len(), 16);
        assert!(a.bytes().all(|c| alphabet.contains(&c)));
        assert_ne!(a, b);
    }
}

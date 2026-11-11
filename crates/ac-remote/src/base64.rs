//! Base64 with the standard alphabet and padding (RFC 4648 section 4), only
//! for the WebSocket opening handshake's key and accept values.

/// `data` as base64 text.
pub(crate) fn encode(data: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let b0 = chunk[0];
        let b1 = chunk.get(1).copied().unwrap_or(0);
        let b2 = chunk.get(2).copied().unwrap_or(0);
        let group = u32::from(b0) << 16 | u32::from(b1) << 8 | u32::from(b2);
        let sextet = |shift: u32| char::from(ALPHABET[(group >> shift & 0x3F) as usize]);
        out.push(sextet(18));
        out.push(sextet(12));
        out.push(if chunk.len() > 1 { sextet(6) } else { '=' });
        out.push(if chunk.len() > 2 { sextet(0) } else { '=' });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::encode;

    // RFC 4648 section 10.
    #[test]
    fn standard_vectors() {
        for (input, output) in [
            ("", ""),
            ("f", "Zg=="),
            ("fo", "Zm8="),
            ("foo", "Zm9v"),
            ("foob", "Zm9vYg=="),
            ("fooba", "Zm9vYmE="),
            ("foobar", "Zm9vYmFy"),
        ] {
            assert_eq!(encode(input.as_bytes()), output, "{input:?}");
        }
    }

    #[test]
    fn every_alphabet_character_appears() {
        let bytes: Vec<u8> = (0..=255u8).collect();
        let text = encode(&bytes);
        assert_eq!(text.len(), 344);
        assert!(text.starts_with("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8g"));
        assert!(text.ends_with("+/w=="));
    }
}

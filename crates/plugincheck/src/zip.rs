//! A small zip writer for release packages: deflated entries (stored when
//! deflate does not help), UTF-8 names, one fixed timestamp so the same
//! folder always packs to the same bytes, no extra fields, no data
//! descriptors. What the launcher's extractor reads.

use ac_launcher_core::updates::zip::crc32;

/// The DOS date of every entry: 1980-01-01, the format's epoch.
const DOS_DATE: u16 = (1 << 5) | 1;
/// The DOS time of every entry: midnight.
const DOS_TIME: u16 = 0;
/// UTF-8 names.
const FLAG_UTF8: u16 = 0x0800;
/// Version 2.0: deflate.
const VERSION: u16 = 20;

/// Why an archive cannot be written.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ZipTooLarge;

/// Writes `entries` (`/`-separated relative path, bytes) as a zip archive.
///
/// # Errors
/// More than 65 535 entries, or a size or offset past 4 GiB (no Zip64).
pub fn write(entries: &[(String, Vec<u8>)]) -> Result<Vec<u8>, ZipTooLarge> {
    let count = u16::try_from(entries.len()).map_err(|_| ZipTooLarge)?;
    let mut out = Vec::new();
    let mut central = Vec::new();
    for (name, content) in entries {
        let deflated = miniz_oxide::deflate::compress_to_vec(content, 9);
        let (method, data) = if deflated.len() < content.len() {
            (8u16, deflated)
        } else {
            (0u16, content.clone())
        };
        let crc = crc32(content);
        let offset = u32::try_from(out.len()).map_err(|_| ZipTooLarge)?;
        let compressed = u32::try_from(data.len()).map_err(|_| ZipTooLarge)?;
        let size = u32::try_from(content.len()).map_err(|_| ZipTooLarge)?;
        let name_length = u16::try_from(name.len()).map_err(|_| ZipTooLarge)?;

        out.extend_from_slice(&0x0403_4b50u32.to_le_bytes());
        out.extend_from_slice(&VERSION.to_le_bytes());
        out.extend_from_slice(&FLAG_UTF8.to_le_bytes());
        out.extend_from_slice(&method.to_le_bytes());
        out.extend_from_slice(&DOS_TIME.to_le_bytes());
        out.extend_from_slice(&DOS_DATE.to_le_bytes());
        out.extend_from_slice(&crc.to_le_bytes());
        out.extend_from_slice(&compressed.to_le_bytes());
        out.extend_from_slice(&size.to_le_bytes());
        out.extend_from_slice(&name_length.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(name.as_bytes());
        out.extend_from_slice(&data);

        central.extend_from_slice(&0x0201_4b50u32.to_le_bytes());
        central.extend_from_slice(&VERSION.to_le_bytes());
        central.extend_from_slice(&VERSION.to_le_bytes());
        central.extend_from_slice(&FLAG_UTF8.to_le_bytes());
        central.extend_from_slice(&method.to_le_bytes());
        central.extend_from_slice(&DOS_TIME.to_le_bytes());
        central.extend_from_slice(&DOS_DATE.to_le_bytes());
        central.extend_from_slice(&crc.to_le_bytes());
        central.extend_from_slice(&compressed.to_le_bytes());
        central.extend_from_slice(&size.to_le_bytes());
        central.extend_from_slice(&name_length.to_le_bytes());
        // Extra field, comment, disk number, internal and external attributes.
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u32.to_le_bytes());
        central.extend_from_slice(&offset.to_le_bytes());
        central.extend_from_slice(name.as_bytes());
    }
    let central_offset = u32::try_from(out.len()).map_err(|_| ZipTooLarge)?;
    let central_length = u32::try_from(central.len()).map_err(|_| ZipTooLarge)?;
    out.extend_from_slice(&central);
    out.extend_from_slice(&0x0605_4b50u32.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out.extend_from_slice(&count.to_le_bytes());
    out.extend_from_slice(&count.to_le_bytes());
    out.extend_from_slice(&central_length.to_le_bytes());
    out.extend_from_slice(&central_offset.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    Ok(out)
}

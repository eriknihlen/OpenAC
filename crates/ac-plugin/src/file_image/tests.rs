//! Ported from OpenAC `tests/AcDream.Core.Tests/Plugins/PluginImageFileTests.cs`
//! (fixtures from `tests/Fixtures/plugin-icons/PngTestData.cs`: `Sized`, and
//! `JpegTestData.Red`, a real 8 x 4 baseline JPEG, solid red).

use std::path::{Path, PathBuf};

use super::*;

/// A folder of its own under the system's temporary folder, removed after.
pub(crate) struct TemporaryDirectory(pub(crate) PathBuf);

impl TemporaryDirectory {
    pub(crate) fn new(tag: &str) -> Self {
        use std::sync::atomic::{AtomicU32, Ordering};
        static NEXT: AtomicU32 = AtomicU32::new(0);
        let path = std::env::temp_dir().join(format!(
            "ac-plugin-file-image-{tag}-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        let _ = std::fs::remove_dir_all(&path);
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }

    pub(crate) fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TemporaryDirectory {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// `PngTestData.Sized`: a valid PNG of any size (RGBA rows, bytes
/// `(x + y) % 256`).
pub(crate) fn png(width: u32, height: u32) -> Vec<u8> {
    let stride = width as usize * 4;
    let mut rgba = vec![0u8; stride * height as usize];
    for y in 0..height as usize {
        for x in 0..stride {
            rgba[y * stride + x] = ((x + y) % 256) as u8;
        }
    }
    ac_bits::png::encode_rgba(width, height, &rgba)
}

/// `JpegTestData`: its size.
pub(crate) const JPEG_WIDTH: u32 = 8;
pub(crate) const JPEG_HEIGHT: u32 = 4;

/// `JpegTestData.Red`.
pub(crate) fn red_jpeg() -> Vec<u8> {
    base64(
        "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAACKADAAQAAAABAAAABAAAAAD/7QA4UGhvdG9zaG9wIDMuMAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgABAAIAwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMAAgICAgICAwICAwUDAwMFBgUFBQUGCAYGBgYGCAoICAgICAgKCgoKCgoKCgwMDAwMDA4ODg4ODw8PDw8PDw8PD//bAEMBAgICBAQEBwQEBxALCQsQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEP/dAAQAAf/aAAwDAQACEQMRAD8A+X6KKK/Ez/UA/9k=",
    )
}

fn base64(text: &str) -> Vec<u8> {
    let value = |c: u8| -> u32 {
        match c {
            b'A'..=b'Z' => u32::from(c - b'A'),
            b'a'..=b'z' => u32::from(c - b'a') + 26,
            b'0'..=b'9' => u32::from(c - b'0') + 52,
            b'+' => 62,
            b'/' => 63,
            _ => panic!("not base64"),
        }
    };
    let digits: Vec<u8> = text.bytes().filter(|c| *c != b'=').collect();
    let mut out = Vec::new();
    for group in digits.chunks(4) {
        let mut n = 0u32;
        for (i, c) in group.iter().enumerate() {
            n |= value(*c) << (18 - 6 * i);
        }
        let bytes = n.to_be_bytes();
        out.extend_from_slice(&bytes[1..group.len()]);
    }
    out
}

fn write(directory: &TemporaryDirectory, name: &str, bytes: &[u8]) -> PathBuf {
    let path = directory.path().join(name);
    std::fs::write(&path, bytes).unwrap();
    path
}

// ResolvePathAcceptsAPngOrJpegInsideTheFolder.
#[test]
fn resolve_path_accepts_a_png_or_jpeg_inside_the_folder() {
    for relative in [
        "icons/sword.png",
        "icons\\sword.png",
        "icons/shield.JPG",
        "icons/helm.jpeg",
    ] {
        let directory = TemporaryDirectory::new("accept");
        std::fs::create_dir_all(directory.path().join("icons")).unwrap();
        let name = relative.replace('\\', "/");
        let name = name.rsplit('/').next().unwrap();
        std::fs::write(directory.path().join("icons").join(name), [1]).unwrap();

        let full = resolve_image_path(directory.path(), relative)
            .unwrap_or_else(|reason| panic!("{relative}: {reason}"));
        assert!(
            full.to_string_lossy()
                .to_ascii_lowercase()
                .ends_with(&name.to_ascii_lowercase()),
            "{}",
            full.display()
        );
    }
}

// ResolvePathRejectsAnythingElse (drive-relative, UNC and device paths
// pinned as outside the folder).
#[test]
fn resolve_path_rejects_anything_else() {
    for (relative, named) in [
        ("../outside.png", "points outside"),
        ("/etc/icon.png", "points outside"),
        ("icons/loot.svg", "not a .png or .jpg"),
        ("icons/loot.gif", "not a .png or .jpg"),
        ("missing.png", "does not exist"),
        ("C:icon.png", "points outside"),
        ("\\\\server\\share\\a.png", "points outside"),
        ("//?/C:/a.png", "points outside"),
        // Not in OpenAC's set: an absolute path of this system, a drive
        // with a separator, and a `..` that climbs back out mid-path.
        ("C:/Windows/a.png", "points outside"),
        ("icons/../../outside.png", "points outside"),
        ("", "points outside"),
    ] {
        let outer = TemporaryDirectory::new("reject");
        let folder = outer.path().join("plugin");
        std::fs::create_dir_all(folder.join("icons")).unwrap();
        std::fs::write(outer.path().join("outside.png"), png(4, 4)).unwrap();
        std::fs::write(folder.join("icons").join("loot.svg"), [1]).unwrap();
        std::fs::write(folder.join("icons").join("loot.gif"), [1]).unwrap();

        let reason = resolve_image_path(&folder, relative).unwrap_err();
        assert!(reason.contains(named), "{relative}: {reason}");
    }
    // An absolute path to a real file, spelled the system's way.
    let outer = TemporaryDirectory::new("absolute");
    let folder = outer.path().join("plugin");
    std::fs::create_dir_all(&folder).unwrap();
    let outside = outer.path().join("outside.png");
    std::fs::write(&outside, png(4, 4)).unwrap();
    let reason = resolve_image_path(&folder, &outside.to_string_lossy()).unwrap_err();
    assert!(reason.contains("points outside"), "{reason}");
}

// A folder named like an image is not a file to resolve.
#[test]
fn resolve_path_refuses_a_folder_named_like_an_image() {
    let directory = TemporaryDirectory::new("folder-name");
    std::fs::create_dir_all(directory.path().join("art.png")).unwrap();
    let reason = resolve_image_path(directory.path(), "art.png").unwrap_err();
    assert!(reason.contains("does not exist"), "{reason}");
}

// NormalizeGivesEverySpellingOfAFileOneSpelling.
#[test]
fn normalize_gives_every_spelling_of_a_file_one_spelling() {
    for (written, normalized) in [
        ("icons/sword.png", "icons/sword.png"),
        ("./icons/sword.png", "icons/sword.png"),
        ("icons//./sword.png", "icons/sword.png"),
        ("icons\\sword.png", "icons/sword.png"),
        ("art/../icons/sword.png", "icons/sword.png"),
        ("../sword.png", "../sword.png"),
        ("/etc/sword.png", "/etc/sword.png"),
        ("C:sword.png", "C:sword.png"),
    ] {
        assert_eq!(normalize(written), normalized, "{written}");
    }
}

// LoadsAPngOfAnySizeUpToTheLimit.
#[test]
fn loads_a_png_of_any_size_up_to_the_limit() {
    let directory = TemporaryDirectory::new("png");
    let path = write(&directory, "wide.png", &png(48, 16));
    let image = load_image(&path).unwrap();
    assert_eq!((image.width, image.height), (48, 16));
    assert_eq!(image.rgba.len(), 48 * 16 * 4);
}

// LoadsAJpegAsOpaqueRgba.
#[test]
fn loads_a_jpeg_as_opaque_rgba() {
    let directory = TemporaryDirectory::new("jpeg");
    let path = write(&directory, "red.jpg", &red_jpeg());
    let image = load_image(&path).unwrap();
    assert_eq!((image.width, image.height), (JPEG_WIDTH, JPEG_HEIGHT));
    assert_eq!(image.rgba[3], 255);
    assert!(
        image.rgba[0] > 150 && image.rgba[1] < 100,
        "the first pixel should be red: {:?}",
        &image.rgba[..4]
    );
}

// RefusesAnImageWiderThanTheLimitBeforeDecodingIt.
#[test]
fn refuses_an_image_wider_than_the_limit_before_decoding_it() {
    // A header that claims 100000 x 1 over data that is not an image: a
    // decode would fail, so only the check of the header's word refuses it
    // for its size.
    let directory = TemporaryDirectory::new("big");
    let mut bytes = vec![137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13];
    bytes.extend_from_slice(b"IHDR");
    bytes.extend_from_slice(&100_000u32.to_be_bytes());
    bytes.extend_from_slice(&1u32.to_be_bytes());
    bytes.extend_from_slice(&[8, 6, 0, 0, 0, 0, 0, 0, 0]);
    bytes.extend_from_slice(&[0x5A; 4096]);
    let path = write(&directory, "big.png", &bytes);
    let reason = load_image(&path).unwrap_err();
    assert!(reason.contains("512"), "{reason}");
    assert!(!reason.contains("could not be decoded"), "{reason}");
}

// RefusesAFileLargerThanTheByteLimit.
#[test]
fn refuses_a_file_larger_than_the_byte_limit() {
    let directory = TemporaryDirectory::new("huge");
    let path = write(&directory, "huge.png", &vec![0u8; MAXIMUM_BYTES + 1]);
    let reason = load_image(&path).unwrap_err();
    assert!(reason.contains("1 MiB"), "{reason}");
}

// RefusesAFileThatIsNeitherPngNorJpegWhateverItsName.
#[test]
fn refuses_a_file_that_is_neither_png_nor_jpeg_whatever_its_name() {
    let directory = TemporaryDirectory::new("fake");
    let path = write(&directory, "fake.png", b"GIF89a not really");
    let reason = load_image(&path).unwrap_err();
    assert!(reason.contains("not a PNG or JPEG"), "{reason}");
}

// RefusesAPngWhosePixelsDoNotDecode.
#[test]
fn refuses_a_png_whose_pixels_do_not_decode() {
    let directory = TemporaryDirectory::new("cut");
    let mut bytes = png(4, 4);
    bytes.truncate(bytes.len() - 20);
    let path = write(&directory, "cut.png", &bytes);
    let reason = load_image(&path).unwrap_err();
    assert!(reason.contains("could not be decoded"), "{reason}");
}

// RefusesAMissingFileWithoutThrowing.
#[test]
fn refuses_a_missing_file_without_throwing() {
    let directory = TemporaryDirectory::new("gone");
    let reason = load_image(&directory.path().join("gone.png")).unwrap_err();
    assert!(reason.contains("could not be read"), "{reason}");
}

// RefusesAnImageWhoseHeaderDoesNotSayItsSize.
#[test]
fn refuses_an_image_whose_header_does_not_say_its_size() {
    let png_without_header: &[u8] = &[
        137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, b'n', b'o', b'p', b'e', 1, 2, 3, 4,
    ];
    let jpeg_without_frame: &[u8] = &[0xFF, 0xD8, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    for bytes in [png_without_header, jpeg_without_frame] {
        let directory = TemporaryDirectory::new("headless");
        let path = write(&directory, "headless.png", bytes);
        let reason = load_image(&path).unwrap_err();
        assert!(reason.contains("header that does not say"), "{reason}");
    }
}

// RefusesAFolderNamedLikeAnImage.
#[test]
fn refuses_a_folder_named_like_an_image() {
    let directory = TemporaryDirectory::new("dir");
    let path = directory.path().join("folder.png");
    std::fs::create_dir_all(&path).unwrap();
    let reason = load_image(&path).unwrap_err();
    assert!(reason.contains("not a regular file"), "{reason}");
}

// The read stops one byte past its limit however long the file is.
#[test]
fn a_read_stops_one_byte_past_its_limit() {
    let directory = TemporaryDirectory::new("limit");
    let path = write(&directory, "long.bin", &[7u8; 100]);
    assert_eq!(read_regular_file(&path, 11).unwrap().len(), 11);
    assert_eq!(read_regular_file(&path, 1000).unwrap().len(), 100);
    let empty = write(&directory, "empty.bin", &[]);
    assert!(read_regular_file(&empty, 10).unwrap().is_empty());
}

// RefusesAPipeNamedLikeAnImageWithoutWaitingOnIt: opened without waiting,
// then refused by the read (a pipe has no offsets to read at).
#[cfg(unix)]
#[test]
fn refuses_a_pipe_named_like_an_image_without_waiting_on_it() {
    let directory = TemporaryDirectory::new("fifo");
    let path = directory.path().join("pipe.png");
    let status = std::process::Command::new("mkfifo")
        .arg(&path)
        .status()
        .unwrap();
    assert!(status.success());
    let (sender, receiver) = std::sync::mpsc::channel();
    let reading = path.clone();
    std::thread::spawn(move || {
        let _ = sender.send(load_image(&reading));
    });
    let outcome = receiver.recv_timeout(std::time::Duration::from_secs(10));
    if outcome.is_err() {
        // Let a reader stuck on the pipe go: a writer opens and closes it.
        drop(std::fs::OpenOptions::new().write(true).open(&path));
    }
    let reason = outcome
        .expect("reading a pipe should not wait for a writer")
        .unwrap_err();
    assert!(reason.contains("could not be read"), "{reason}");
}

// Links: every link on the way to a file is followed, so none can lead a
// plugin's markup out of its own folder. Windows needs extra rights to make
// links, so these run where links can be made freely.

#[cfg(unix)]
fn link_test_folders() -> (TemporaryDirectory, PathBuf) {
    let outer = TemporaryDirectory::new("links");
    let folder = outer.path().join("plugin");
    std::fs::create_dir_all(folder.join("icons")).unwrap();
    std::fs::write(outer.path().join("outside.png"), png(4, 4)).unwrap();
    (outer, folder)
}

// ResolvePathRefusesAFileLinkThatPointsOutsideTheFolder.
#[cfg(unix)]
#[test]
fn resolve_path_refuses_a_file_link_that_points_outside_the_folder() {
    let (outer, folder) = link_test_folders();
    std::os::unix::fs::symlink(
        outer.path().join("outside.png"),
        folder.join("icons/sword.png"),
    )
    .unwrap();
    let reason = resolve_image_path(&folder, "icons/sword.png").unwrap_err();
    assert!(reason.contains("points outside"), "{reason}");
}

// ResolvePathRefusesAFolderLinkThatPointsOutsideTheFolder.
#[cfg(unix)]
#[test]
fn resolve_path_refuses_a_folder_link_that_points_outside_the_folder() {
    let (outer, folder) = link_test_folders();
    let elsewhere = outer.path().join("elsewhere");
    std::fs::create_dir_all(&elsewhere).unwrap();
    std::fs::write(elsewhere.join("sword.png"), png(4, 4)).unwrap();
    std::os::unix::fs::symlink(&elsewhere, folder.join("linked")).unwrap();
    let reason = resolve_image_path(&folder, "linked/sword.png").unwrap_err();
    assert!(reason.contains("points outside"), "{reason}");
}

// ResolvePathFollowsALinkToALinkToWhereItReallyEnds.
#[cfg(unix)]
#[test]
fn resolve_path_follows_a_link_to_a_link_to_where_it_really_ends() {
    let (outer, folder) = link_test_folders();
    let inner = folder.join("inner.png");
    std::os::unix::fs::symlink(outer.path().join("outside.png"), &inner).unwrap();
    std::os::unix::fs::symlink(&inner, folder.join("icons/sword.png")).unwrap();
    let reason = resolve_image_path(&folder, "icons/sword.png").unwrap_err();
    assert!(reason.contains("points outside"), "{reason}");
}

// ResolvePathAcceptsALinkThatStaysInsideTheFolder.
#[cfg(unix)]
#[test]
fn resolve_path_accepts_a_link_that_stays_inside_the_folder() {
    let (_outer, folder) = link_test_folders();
    let real = folder.join("art").join("sword.png");
    std::fs::create_dir_all(real.parent().unwrap()).unwrap();
    std::fs::write(&real, png(4, 4)).unwrap();
    std::os::unix::fs::symlink(
        Path::new("..").join("art").join("sword.png"),
        folder.join("icons/sword.png"),
    )
    .unwrap();
    let full = resolve_image_path(&folder, "icons/sword.png").unwrap();
    assert!(
        full.ends_with(Path::new("art").join("sword.png")),
        "{}",
        full.display()
    );
}

// ResolvePathServesAFolderThatIsItselfALink.
#[cfg(unix)]
#[test]
fn resolve_path_serves_a_folder_that_is_itself_a_link() {
    let (outer, folder) = link_test_folders();
    std::fs::write(folder.join("icons/sword.png"), png(4, 4)).unwrap();
    let alias = outer.path().join("alias");
    std::os::unix::fs::symlink(&folder, &alias).unwrap();
    resolve_image_path(&alias, "icons/sword.png").unwrap();
}

// The textual check on its own (OpenAC's containment of the full path): a
// `..` folds into the part before it, the folder's own parts included, and
// only what that comes to decides.
#[test]
fn the_textual_check_folds_dots_as_the_full_path_reads() {
    let root = Path::new(if cfg!(windows) {
        r"C:\games\plugin"
    } else {
        "/games/plugin"
    });
    let inside = root.join("icons").join("a.png");
    assert_eq!(
        lexical_candidate(root, "icons/./a.png"),
        Some(inside.clone())
    );
    assert_eq!(
        lexical_candidate(root, "icons//a.png"),
        Some(inside.clone())
    );
    assert_eq!(
        lexical_candidate(root, "art/../icons/a.png"),
        Some(inside.clone())
    );
    // Out of the folder and back in by its own name is inside.
    assert_eq!(
        lexical_candidate(root, "../plugin/icons/a.png"),
        Some(inside)
    );
    for outside in [
        "../outside.png",
        "icons/../../outside.png",
        "../plugin2/a.png",
        "../../../../../../../../a.png",
        "icons/..",
        "..",
        ".",
        "",
    ] {
        assert_eq!(lexical_candidate(root, outside), None, "{outside}");
    }
    if cfg!(windows) {
        // A drive replaces the whole path.
        assert_eq!(lexical_candidate(root, "icons/C:/a.png"), None);
        assert_eq!(lexical_candidate(root, "D:a.png"), None);
    }
}

/// A junction (a folder link Windows lets anyone make) at `link` to
/// `target`; false when this system did not allow one.
#[cfg(windows)]
fn junction(link: &Path, target: &Path) -> bool {
    std::process::Command::new("cmd")
        .args(["/C", "mklink", "/J"])
        .arg(link)
        .arg(target)
        .output()
        .is_ok_and(|o| o.status.success())
}

// ResolvePathRefusesAFolderLinkThatPointsOutsideTheFolder, on Windows: a
// junction inside the folder to a folder outside it. The path is inside
// as written; only following the link shows where it ends.
#[cfg(windows)]
#[test]
fn resolve_path_refuses_a_junction_that_points_outside_the_folder() {
    let outer = TemporaryDirectory::new("junction-out");
    let folder = outer.path().join("plugin");
    let elsewhere = outer.path().join("elsewhere");
    std::fs::create_dir_all(&folder).unwrap();
    std::fs::create_dir_all(&elsewhere).unwrap();
    std::fs::write(elsewhere.join("sword.png"), png(4, 4)).unwrap();
    if !junction(&folder.join("linked"), &elsewhere) {
        eprintln!("skipped: this system did not let the test make a junction");
        return;
    }
    let reason = resolve_image_path(&folder, "linked/sword.png").unwrap_err();
    assert!(reason.contains("points outside"), "{reason}");
}

// ResolvePathAcceptsALinkThatStaysInsideTheFolder, on Windows.
#[cfg(windows)]
#[test]
fn resolve_path_accepts_a_junction_that_stays_inside_the_folder() {
    let outer = TemporaryDirectory::new("junction-in");
    let folder = outer.path().join("plugin");
    let art = folder.join("art");
    std::fs::create_dir_all(&art).unwrap();
    std::fs::write(art.join("sword.png"), png(4, 4)).unwrap();
    if !junction(&folder.join("linked"), &art) {
        eprintln!("skipped: this system did not let the test make a junction");
        return;
    }
    let full = resolve_image_path(&folder, "linked/sword.png").unwrap();
    assert!(
        full.ends_with(Path::new("art").join("sword.png")),
        "{}",
        full.display()
    );
}

// ResolvePathRefusesAFileLinkThatPointsOutsideTheFolder, on Windows, where
// making a file link needs the right to (developer mode or an
// administrator); skipped when it is not granted.
#[cfg(windows)]
#[test]
fn resolve_path_refuses_a_file_link_that_points_outside_the_folder_on_windows() {
    let outer = TemporaryDirectory::new("symlink-out");
    let folder = outer.path().join("plugin");
    std::fs::create_dir_all(folder.join("icons")).unwrap();
    let outside = outer.path().join("outside.png");
    std::fs::write(&outside, png(4, 4)).unwrap();
    if let Err(e) =
        std::os::windows::fs::symlink_file(&outside, folder.join("icons").join("sword.png"))
    {
        eprintln!("skipped: this system did not let the test make a file link: {e}");
        return;
    }
    let reason = resolve_image_path(&folder, "icons/sword.png").unwrap_err();
    assert!(reason.contains("points outside"), "{reason}");
}

// ── A window's own dock icon ─────────────────────────────────────────────

fn dock_icon_folder() -> TemporaryDirectory {
    let root = TemporaryDirectory::new("dock-icon");
    std::fs::create_dir_all(root.path().join("icons")).unwrap();
    root
}

#[test]
fn a_dock_icon_of_64_pixels_is_read_by_any_spelling() {
    let root = dock_icon_folder();
    std::fs::write(root.path().join("icons/a.png"), png(64, 64)).unwrap();
    for spelling in [
        "icons/a.png",
        "./icons//a.png",
        r"icons\a.png",
        "icons/../icons/a.PNG",
    ] {
        // A file system that tells letter case apart has no `a.PNG`.
        let read = load_dock_icon(root.path(), spelling);
        if spelling.ends_with("PNG") && matches!(read, Ok(None)) {
            continue;
        }
        let icon = read.unwrap().unwrap_or_else(|| panic!("{spelling}"));
        assert_eq!((icon.width, icon.height), (64, 64), "{spelling}");
    }
}

#[test]
fn a_missing_dock_icon_is_not_an_error() {
    let root = dock_icon_folder();
    assert_eq!(load_dock_icon(root.path(), "icons/gone.png"), Ok(None));
}

#[test]
fn a_dock_icon_is_refused_for_each_rule_it_breaks() {
    let root = dock_icon_folder();
    let write = |name: &str, bytes: &[u8]| std::fs::write(root.path().join(name), bytes).unwrap();
    write("small.png", &png(32, 32));
    write("big.png", &vec![0u8; ICON_MAXIMUM_BYTES + 1]);
    write("text.png", b"this is not a PNG");
    write("a.jpg", &red_jpeg());
    write("a.svg", b"<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
    for (path, reason) in [
        ("small.png", "must be exactly 64x64"),
        ("big.png", "is larger than"),
        ("text.png", "is not a PNG"),
        ("a.jpg", "is not a .png file"),
        ("a.svg", "is not a .png file"),
        ("../a.png", OUTSIDE),
        ("icons/../../a.png", OUTSIDE),
        ("/etc/a.png", OUTSIDE),
        (r"C:\a.png", OUTSIDE),
        ("", OUTSIDE),
    ] {
        let refused = load_dock_icon(root.path(), path).unwrap_err();
        assert!(refused.contains(reason), "{path}: {refused}");
    }
}

#[cfg(unix)]
#[test]
fn a_dock_icon_cannot_be_a_link_out_of_the_folder_or_a_pipe() {
    let root = dock_icon_folder();
    let outside = TemporaryDirectory::new("dock-icon-outside");
    std::fs::write(outside.path().join("a.png"), png(64, 64)).unwrap();
    std::os::unix::fs::symlink(outside.path().join("a.png"), root.path().join("link.png")).unwrap();
    assert_eq!(
        load_dock_icon(root.path(), "link.png").unwrap_err(),
        OUTSIDE
    );

    // Refused for what it read, at once: nothing ever writes to this pipe.
    let pipe = root.path().join("pipe.png");
    let made = std::process::Command::new("mkfifo")
        .arg(&pipe)
        .status()
        .unwrap();
    assert!(made.success());
    assert!(load_dock_icon(root.path(), "pipe.png").is_err());
}

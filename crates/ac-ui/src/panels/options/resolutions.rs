//! The resolutions the display menu offers: the monitor's modes of the
//! modern aspect families (no legacy 4:3 or 5:4, nothing narrower than
//! 1280 or larger than the desktop; the desktop itself always), and in a
//! window also the standard ladder sizes that fit.

/// The standard sizes, offered when nothing better is known.
pub const AVAILABLE_RESOLUTIONS: [&str; 6] = [
    "1280x720",
    "1366x768",
    "1600x900",
    "1920x1080",
    "2560x1440",
    "3840x2160",
];

/// 16:9, 16:10, 21:9 and 32:9, as width over height.
const MODERN_ASPECTS: [f32; 4] = [16.0 / 9.0, 16.0 / 10.0, 21.0 / 9.0, 32.0 / 9.0];

fn parse(spec: &str) -> Option<(i32, i32)> {
    let (w, h) = spec.split_once('x')?;
    let w = w.parse::<i32>().ok()?;
    let h = h.parse::<i32>().ok()?;
    (w > 0 && h > 0).then_some((w, h))
}

fn names(mut modes: Vec<(i32, i32)>) -> Vec<String> {
    modes.sort_unstable();
    modes.dedup();
    modes.into_iter().map(|(w, h)| format!("{w}x{h}")).collect()
}

/// The monitor's `modes` worth offering on a `desktop`-sized screen,
/// ascending by width then height.
#[must_use]
pub fn curate(modes: &[(i32, i32)], desktop: (i32, i32)) -> Vec<String> {
    let mut keep = Vec::new();
    for &(w, h) in modes {
        if w <= 0 || h <= 0 || w > desktop.0 || h > desktop.1 {
            continue;
        }
        if (w, h) == desktop {
            keep.push((w, h));
            continue;
        }
        if w < 1280 {
            continue;
        }
        let aspect = w as f32 / h as f32;
        if MODERN_ASPECTS
            .iter()
            .any(|family| (aspect - family).abs() <= family * 0.025)
        {
            keep.push((w, h));
        }
    }
    names(keep)
}

/// The windowed offering: the curated modes plus the standard sizes that
/// fit the desktop.
#[must_use]
pub fn windowed_offering(curated: &[String], desktop: (i32, i32)) -> Vec<String> {
    let mut keep: Vec<(i32, i32)> = curated.iter().filter_map(|s| parse(s)).collect();
    keep.extend(
        AVAILABLE_RESOLUTIONS
            .iter()
            .filter_map(|s| parse(s))
            .filter(|&(w, h)| w <= desktop.0 && h <= desktop.1),
    );
    names(keep)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/Rendering/DisplayModeCatalogTests.cs`.
    use super::*;

    const DESKTOP: (i32, i32) = (2560, 1440);

    #[test]
    fn legacy_formats_go_and_modern_families_stay() {
        let modes = [
            (640, 480),
            (800, 600),
            (1024, 768),
            (1280, 1024),
            (1280, 720),
            (1366, 768),
            (1600, 900),
            (1920, 1080),
            (1920, 1200),
            (2560, 1440),
        ];
        assert_eq!(
            curate(&modes, DESKTOP),
            [
                "1280x720",
                "1366x768",
                "1600x900",
                "1920x1080",
                "1920x1200",
                "2560x1440"
            ]
        );
    }

    #[test]
    fn modes_larger_than_the_desktop_go_and_duplicates_collapse() {
        assert_eq!(
            curate(&[(1920, 1080), (2560, 1440), (3840, 2160)], DESKTOP),
            ["1920x1080", "2560x1440"]
        );
        assert_eq!(
            curate(
                &[
                    (2560, 1440),
                    (1920, 1080),
                    (1920, 1080),
                    (1920, 1080),
                    (1280, 720)
                ],
                DESKTOP
            ),
            ["1280x720", "1920x1080", "2560x1440"]
        );
    }

    #[test]
    fn ultrawide_families_stay_and_the_desktop_always_does() {
        assert_eq!(
            curate(&[(2560, 1080), (3440, 1440), (3840, 1080)], (3840, 1600)),
            ["2560x1080", "3440x1440", "3840x1080"]
        );
        let c = curate(&[(1920, 1080), (1920, 1440)], (1920, 1440));
        assert!(c.contains(&"1920x1440".to_owned()));
        assert!(c.contains(&"1920x1080".to_owned()));
        assert_eq!(curate(&[(1024, 576), (1280, 720)], DESKTOP), ["1280x720"]);
    }

    #[test]
    fn the_standard_ladder_passes_its_own_rule() {
        let parsed: Vec<(i32, i32)> = AVAILABLE_RESOLUTIONS
            .iter()
            .filter_map(|s| parse(s))
            .collect();
        assert_eq!(curate(&parsed, (3840, 2160)), AVAILABLE_RESOLUTIONS);
    }

    #[test]
    fn a_starved_mode_list_gains_the_ladder_sizes_that_fit() {
        assert_eq!(
            windowed_offering(
                &["1920x1080".to_owned(), "2056x1290".to_owned()],
                (2056, 1290)
            ),
            ["1280x720", "1366x768", "1600x900", "1920x1080", "2056x1290"]
        );
    }
}

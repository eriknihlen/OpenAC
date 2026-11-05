//! Performance and image tools: summarise a frame history around its
//! checkpoints and portal arrivals, compare two screenshots, crop and zoom
//! one, print a block of its pixels.

use std::path::Path;

use ac_host::json::{self, Member, Node, Value};

/// One frame of a frame history CSV (`OPENAC_FRAME_HISTORY`).
#[derive(Debug, Clone, Copy, PartialEq)]
struct FrameRow {
    frame: i64,
    /// 100-nanosecond ticks since 1970.
    ticks: i64,
    cpu_us: i64,
    gpu_us: i64,
    alloc_bytes: i64,
    update_us: i64,
    upload_us: i64,
    imgui_us: i64,
    pacing_us: i64,
}

const TICKS_PER_SECOND: i64 = 10_000_000;

fn days_from_civil(y: i64, m: i64, d: i64) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let mp = (m + 9) % 12;
    let doy = (153 * mp + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

fn civil_from_days(z: i64) -> (i64, i64, i64) {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    (if m <= 2 { y + 1 } else { y }, m, d)
}

/// An ISO 8601 UTC instant (`2026-07-24T10:00:01.0000000Z`, the fraction
/// and the `Z` optional) as ticks since 1970.
fn parse_utc(text: &str) -> Result<i64, String> {
    let bad = || format!("not a UTC timestamp: {text}");
    let t = text.trim().trim_end_matches('Z').trim_end_matches("+00:00");
    let (date, time) = t.split_once('T').ok_or_else(bad)?;
    let mut d = date.split('-').map(str::parse::<i64>);
    let (y, mo, da) = (
        d.next().and_then(Result::ok).ok_or_else(bad)?,
        d.next().and_then(Result::ok).ok_or_else(bad)?,
        d.next().and_then(Result::ok).ok_or_else(bad)?,
    );
    let (hms, fraction) = time.split_once('.').unwrap_or((time, ""));
    let mut h = hms.split(':').map(str::parse::<i64>);
    let (hh, mm, ss) = (
        h.next().and_then(Result::ok).ok_or_else(bad)?,
        h.next().and_then(Result::ok).ok_or_else(bad)?,
        h.next().and_then(Result::ok).ok_or_else(bad)?,
    );
    let mut frac_ticks = 0i64;
    for (i, c) in fraction.chars().take(7).enumerate() {
        let digit = c.to_digit(10).ok_or_else(bad)?;
        frac_ticks += i64::from(digit) * 10i64.pow(6 - i as u32);
    }
    let seconds = days_from_civil(y, mo, da) * 86_400 + hh * 3600 + mm * 60 + ss;
    Ok(seconds * TICKS_PER_SECOND + frac_ticks)
}

/// Ticks since 1970 as `YYYY-MM-DDTHH:MM:SS.fffffffZ`.
fn format_utc(ticks: i64) -> String {
    let seconds = ticks.div_euclid(TICKS_PER_SECOND);
    let frac = ticks.rem_euclid(TICKS_PER_SECOND);
    let (days, rem) = (seconds.div_euclid(86_400), seconds.rem_euclid(86_400));
    let (y, m, d) = civil_from_days(days);
    format!(
        "{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}.{frac:07}Z",
        rem / 3600,
        rem % 3600 / 60,
        rem % 60
    )
}

fn read_frames(text: &str) -> Result<Vec<FrameRow>, String> {
    let mut lines = text.lines();
    let header: Vec<&str> = lines
        .next()
        .ok_or("Frame-history CSV is empty.")?
        .split(',')
        .collect();
    let column = |name: &str| {
        header
            .iter()
            .position(|h| *h == name)
            .ok_or_else(|| format!("Frame-history CSV is missing '{name}'."))
    };
    let columns = [
        "frame",
        "timestamp_utc",
        "cpu_us",
        "gpu_us",
        "alloc_bytes",
        "update_us",
        "upload_us",
        "imgui_us",
        "pacing_us",
    ]
    .map(column);
    let mut idx = [0usize; 9];
    for (i, c) in columns.into_iter().enumerate() {
        idx[i] = c?;
    }
    let mut rows = Vec::new();
    for (n, line) in lines.enumerate() {
        if line.trim().is_empty() {
            continue;
        }
        let values: Vec<&str> = line.split(',').collect();
        let row = (|| -> Result<FrameRow, String> {
            let get = |i: usize| values.get(idx[i]).copied().ok_or("missing column");
            let int = |i: usize| -> Result<i64, String> {
                get(i)?.trim().parse::<i64>().map_err(|e| e.to_string())
            };
            Ok(FrameRow {
                frame: int(0)?,
                ticks: parse_utc(get(1)?)?,
                cpu_us: int(2)?,
                gpu_us: int(3)?,
                alloc_bytes: int(4)?,
                update_us: int(5)?,
                upload_us: int(6)?,
                imgui_us: int(7)?,
                pacing_us: int(8)?,
            })
        })()
        .map_err(|e| format!("Invalid frame-history row {}: {e}", n + 2))?;
        rows.push(row);
    }
    Ok(rows)
}

/// A checkpoint: name, instant, the allocation rate since the previous.
type Checkpoint = (String, i64, Option<f64>);

fn read_checkpoints(text: &str) -> Result<Vec<Checkpoint>, String> {
    let mut out = Vec::new();
    let mut previous: Option<(i64, i64)> = None;
    for line in text.lines().filter(|l| !l.trim().is_empty()) {
        let node = json::parse(line.as_bytes()).map_err(|e| e.to_string())?;
        let member = |node: &Node, name: &str| -> Result<Node, String> {
            json::object_members(node)
                .map_err(|e| e.to_string())?
                .iter()
                .find(|m| m.name == name)
                .map(|m| m.value.clone())
                .ok_or_else(|| format!("checkpoint is missing '{name}'"))
        };
        let name = match member(&node, "name")?.value {
            Value::String(s) => s,
            _ => return Err("Checkpoint name is not text.".into()),
        };
        let at = match member(&node, "timestampUtc")?.value {
            Value::String(s) => parse_utc(&s)?,
            _ => return Err("checkpoint timestampUtc is not text".into()),
        };
        let allocated =
            match member(&member(&node, "resources")?, "processTotalAllocatedBytes")?.value {
                Value::Number(n) => n.parse::<i64>().map_err(|e| e.to_string())?,
                _ => return Err("processTotalAllocatedBytes is not a number".into()),
            };
        let rate = previous.and_then(|(p_at, p_bytes)| {
            let seconds = (at - p_at) as f64 / TICKS_PER_SECOND as f64;
            (seconds > 0.0).then(|| (allocated - p_bytes).max(0) as f64 / seconds)
        });
        out.push((name, at, rate));
        previous = Some((at, allocated));
    }
    Ok(out)
}

fn read_markers(text: &str) -> Result<Vec<(i64, String)>, String> {
    let mut out = Vec::new();
    for line in text.lines() {
        let Some((stamp, rest)) = line.split_once(' ') else {
            continue;
        };
        let Some(rest) = rest.strip_prefix("materialized destination='") else {
            continue;
        };
        let Some((destination, _)) = rest.split_once('\'') else {
            continue;
        };
        if stamp.chars().any(char::is_whitespace) {
            continue;
        }
        out.push((parse_utc(stamp)?, destination.to_owned()));
    }
    Ok(out)
}

fn percentile(sorted: &[i64], p: f64) -> i64 {
    let index = (sorted.len() as f64 * p).ceil() as i64 - 1;
    sorted[index.clamp(0, sorted.len() as i64 - 1) as usize]
}

fn num(v: impl ToString) -> Node {
    Node::made(Value::Number(v.to_string()))
}

fn float(v: f64) -> Node {
    num(if v.fract() == 0.0 && v.abs() < 1e15 {
        format!("{v:.0}")
    } else {
        format!("{v}")
    })
}

fn obj(members: Vec<(&str, Node)>) -> Node {
    Node::made(Value::Object(
        members
            .into_iter()
            .map(|(n, v)| Member::made(n, v.value))
            .collect(),
    ))
}

fn text(s: &str) -> Node {
    Node::made(Value::String(s.to_owned()))
}

fn metric(values: impl Iterator<Item = i64>) -> Node {
    let mut v: Vec<i64> = values.collect();
    v.sort_unstable();
    if v.is_empty() {
        return obj(vec![
            ("count", num(0)),
            ("p50", num(0)),
            ("p95", num(0)),
            ("p99", num(0)),
            ("p999", num(0)),
            ("maximum", num(0)),
            ("mean", num(0)),
        ]);
    }
    let mean = v.iter().map(|&x| x as f64).sum::<f64>() / v.len() as f64;
    obj(vec![
        ("count", num(v.len())),
        ("p50", num(percentile(&v, 0.50))),
        ("p95", num(percentile(&v, 0.95))),
        ("p99", num(percentile(&v, 0.99))),
        ("p999", num(percentile(&v, 0.999))),
        ("maximum", num(v[v.len() - 1])),
        ("mean", float(mean)),
    ])
}

fn window(rows: &[&FrameRow]) -> Node {
    let mut rows = rows.to_vec();
    rows.sort_by_key(|r| r.ticks);
    let duration = if rows.len() < 2 {
        0.0
    } else {
        (rows[rows.len() - 1].ticks - rows[0].ticks) as f64 / TICKS_PER_SECOND as f64
    };
    let cpu: Vec<f64> = rows.iter().map(|r| r.cpu_us as f64).collect();
    let sd = if cpu.is_empty() {
        0.0
    } else {
        let mean = cpu.iter().sum::<f64>() / cpu.len() as f64;
        (cpu.iter().map(|c| (c - mean).powi(2)).sum::<f64>() / cpu.len() as f64).sqrt()
    };
    obj(vec![
        ("frameCount", num(rows.len())),
        ("durationSeconds", float(duration)),
        ("cpuUs", metric(rows.iter().map(|r| r.cpu_us))),
        (
            "gpuUs",
            metric(rows.iter().filter(|r| r.gpu_us >= 0).map(|r| r.gpu_us)),
        ),
        (
            "allocationBytes",
            metric(rows.iter().map(|r| r.alloc_bytes)),
        ),
        ("updateUs", metric(rows.iter().map(|r| r.update_us))),
        ("uploadUs", metric(rows.iter().map(|r| r.upload_us))),
        ("imGuiUs", metric(rows.iter().map(|r| r.imgui_us))),
        ("pacingUs", metric(rows.iter().map(|r| r.pacing_us))),
        (
            "totalPositiveAllocationBytes",
            num(rows.iter().map(|r| r.alloc_bytes.max(0)).sum::<i64>()),
        ),
        ("cpuStandardDeviationUs", float(sd)),
    ])
}

/// The frame-history report as JSON (OpenAC's `FrameHistoryReport`).
pub fn summarize(
    source_name: &str,
    frames_csv: &str,
    checkpoints_jsonl: &str,
    markers_log: &str,
) -> Result<String, String> {
    let frames = read_frames(frames_csv)?;
    let checkpoints = read_checkpoints(checkpoints_jsonl)?;
    let portals = read_markers(markers_log)?;
    let all: Vec<&FrameRow> = frames.iter().collect();
    let within = |from: i64, to: i64| -> Vec<&FrameRow> {
        frames
            .iter()
            .filter(|f| f.ticks >= from && f.ticks <= to)
            .collect()
    };
    let five = 5 * TICKS_PER_SECOND;
    let checkpoint_windows = checkpoints
        .iter()
        .map(|(name, at, rate)| {
            obj(vec![
                ("name", text(name)),
                ("startUtc", text(&format_utc(at - five))),
                ("endUtc", text(&format_utc(*at))),
                ("metrics", window(&within(at - five, *at))),
                (
                    "processAllocationRateBytesPerSecond",
                    rate.map_or(Node::made(Value::Null), float),
                ),
            ])
        })
        .collect();
    let portal_windows = portals
        .iter()
        .map(|(at, destination)| {
            let rows = within(at - 2 * five, at + five);
            let mut worst = rows.clone();
            worst.sort_by_key(|f| std::cmp::Reverse(f.cpu_us));
            let worst = worst
                .iter()
                .take(20)
                .map(|f| {
                    let relative = ((f.ticks - at) as f64 / 10_000.0 * 1000.0).round() / 1000.0;
                    obj(vec![
                        ("frame", num(f.frame)),
                        ("relativeToMaterializationMs", float(relative)),
                        ("cpuUs", num(f.cpu_us)),
                        ("gpuUs", num(f.gpu_us)),
                        ("allocationBytes", num(f.alloc_bytes)),
                        ("updateUs", num(f.update_us)),
                        ("uploadUs", num(f.upload_us)),
                        ("pacingUs", num(f.pacing_us)),
                    ])
                })
                .collect();
            obj(vec![
                ("destination", text(destination)),
                ("materializedUtc", text(&format_utc(*at))),
                ("metrics", window(&rows)),
                ("worstFrames", Node::made(Value::Array(worst))),
            ])
        })
        .collect();
    let utc =
        |f: Option<&FrameRow>| f.map_or(Node::made(Value::Null), |f| text(&format_utc(f.ticks)));
    let report = obj(vec![
        ("sourceFrameHistory", text(source_name)),
        ("frameCount", num(frames.len())),
        ("firstFrameUtc", utc(frames.first())),
        ("lastFrameUtc", utc(frames.last())),
        ("route", window(&all)),
        (
            "checkpointWindows",
            Node::made(Value::Array(checkpoint_windows)),
        ),
        ("portalWindows", Node::made(Value::Array(portal_windows))),
    ]);
    let mut out = String::new();
    json::write_value(&mut out, &report, 0);
    Ok(out)
}

/// An RGBA image.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Image {
    /// Width.
    pub width: u32,
    /// Height.
    pub height: u32,
    /// Pixels, four bytes each, rows top first.
    pub rgba: Vec<u8>,
}

/// Read a PNG.
pub fn read_png(path: &Path) -> Result<Image, String> {
    let bytes = std::fs::read(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let icon = ac_launcher_core::window::icon::decode_png(&bytes)
        .ok_or_else(|| format!("{}: not a PNG this tool reads", path.display()))?;
    Ok(Image {
        width: icon.width,
        height: icon.height,
        rgba: icon.rgba.to_vec(),
    })
}

/// How two screenshots differ.
#[derive(Debug, Clone, PartialEq)]
pub struct Comparison {
    /// Within the allowed fraction.
    pub passed: bool,
    /// Pixels compared.
    pub compared_pixels: u64,
    /// Pixels the mask excluded.
    pub masked_pixels: u64,
    /// Pixels beyond the channel tolerance.
    pub different_pixels: u64,
    /// The report as JSON.
    pub json: String,
}

/// Compare `actual` with `expected` pixel by pixel: a pixel differs when any
/// channel is more than `tolerance` apart; a mask pixel with alpha excludes
/// its pixel (OpenAC `PerformanceTools.CompareImages`).
pub fn compare(
    expected: &Image,
    actual: &Image,
    names: (&str, &str, Option<&str>),
    tolerance: u8,
    max_different_fraction: f64,
    mask: Option<&Image>,
) -> Result<Comparison, String> {
    if !(0.0..=1.0).contains(&max_different_fraction) {
        return Err("the allowed fraction runs from 0 to 1".into());
    }
    let report = |passed: bool,
                  compared: u64,
                  masked: u64,
                  different: u64,
                  fraction: f64,
                  max_delta: u32,
                  mean: f64,
                  rms: f64| {
        let node = obj(vec![
            ("passed", Node::made(Value::Bool(passed))),
            ("width", num(actual.width)),
            ("height", num(actual.height)),
            ("expectedWidth", num(expected.width)),
            ("expectedHeight", num(expected.height)),
            ("channelTolerance", num(tolerance)),
            ("maxDifferentPixelFraction", float(max_different_fraction)),
            ("comparedPixels", num(compared)),
            ("maskedPixels", num(masked)),
            ("differentPixels", num(different)),
            ("differentPixelFraction", float(fraction)),
            ("maximumChannelDelta", num(max_delta)),
            ("meanAbsoluteChannelDelta", float(mean)),
            ("rootMeanSquareChannelDelta", float(rms)),
            ("expectedPath", text(names.0)),
            ("actualPath", text(names.1)),
            ("maskPath", names.2.map_or(Node::made(Value::Null), text)),
        ]);
        let mut out = String::new();
        json::write_value(&mut out, &node, 0);
        Comparison {
            passed,
            compared_pixels: compared,
            masked_pixels: masked,
            different_pixels: different,
            json: out,
        }
    };
    if (expected.width, expected.height) != (actual.width, actual.height) {
        return Ok(report(false, 0, 0, 0, 1.0, 255, 0.0, 0.0));
    }
    if let Some(m) = mask
        && (m.width, m.height) != (expected.width, expected.height)
    {
        return Err("Screenshot mask dimensions must match the compared images.".into());
    }
    let (mut compared, mut masked, mut different) = (0u64, 0u64, 0u64);
    let (mut abs_sum, mut sq_sum, mut max_delta) = (0u64, 0u64, 0u32);
    for i in 0..(expected.width * expected.height) as usize {
        if mask.is_some_and(|m| m.rgba[i * 4 + 3] != 0) {
            masked += 1;
            continue;
        }
        compared += 1;
        let mut is_different = false;
        for c in 0..4 {
            let d = u32::from(expected.rgba[i * 4 + c].abs_diff(actual.rgba[i * 4 + c]));
            abs_sum += u64::from(d);
            sq_sum += u64::from(d * d);
            max_delta = max_delta.max(d);
            is_different |= d > u32::from(tolerance);
        }
        different += u64::from(is_different);
    }
    if compared == 0 {
        return Err("Screenshot mask excludes every pixel; no comparison is possible.".into());
    }
    let fraction = different as f64 / compared as f64;
    let channels = (compared * 4) as f64;
    Ok(report(
        fraction <= max_different_fraction,
        compared,
        masked,
        different,
        fraction,
        max_delta,
        abs_sum as f64 / channels,
        (sq_sum as f64 / channels).sqrt(),
    ))
}

/// A region of `image` (clamped into it), zoomed `zoom` times by repeating
/// pixels.
#[must_use]
pub fn crop(image: &Image, x: i64, y: i64, w: i64, h: i64, zoom: u32) -> Image {
    let x = x.clamp(0, i64::from(image.width) - 1) as u32;
    let y = y.clamp(0, i64::from(image.height) - 1) as u32;
    let w = w.clamp(1, i64::from(image.width - x)) as u32;
    let h = h.clamp(1, i64::from(image.height - y)) as u32;
    let zoom = zoom.max(1);
    let (ow, oh) = (w * zoom, h * zoom);
    let mut rgba = Vec::with_capacity((ow * oh * 4) as usize);
    for oy in 0..oh {
        for ox in 0..ow {
            let (sx, sy) = (x + ox / zoom, y + oy / zoom);
            let i = ((sy * image.width + sx) * 4) as usize;
            rgba.extend_from_slice(&image.rgba[i..i + 4]);
        }
    }
    Image {
        width: ow,
        height: oh,
        rgba,
    }
}

/// The RGB of a block of pixels as hex, one line per row (clamped into the
/// image).
#[must_use]
pub fn probe(image: &Image, x0: i64, y0: i64, x1: i64, y1: i64) -> Vec<String> {
    let cx = |v: i64| v.clamp(0, i64::from(image.width) - 1) as u32;
    let cy = |v: i64| v.clamp(0, i64::from(image.height) - 1) as u32;
    let (x0, x1, y0, y1) = (cx(x0), cx(x1), cy(y0), cy(y1));
    let mut lines = vec![format!(
        "{}x{}  cols x={x0}..{x1}",
        image.width, image.height
    )];
    for y in y0..=y1 {
        let mut line = format!("y={y:4}: ");
        for x in x0..=x1 {
            let i = ((y * image.width + x) * 4) as usize;
            line.push_str(&format!(
                "{:02X}{:02X}{:02X} ",
                image.rgba[i],
                image.rgba[i + 1],
                image.rgba[i + 2]
            ));
        }
        lines.push(line);
    }
    lines
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Cli.Tests/PerformanceToolsTests.cs`.

    use super::*;

    fn image(width: u32, pixels: &[[u8; 4]]) -> Image {
        Image {
            width,
            height: (pixels.len() as u32) / width,
            rgba: pixels.concat(),
        }
    }

    #[test]
    fn comparing_applies_the_channel_tolerance_and_the_mask() {
        let expected = image(2, &[[10, 20, 30, 255], [40, 50, 60, 255]]);
        let actual = image(2, &[[12, 20, 30, 255], [200, 50, 60, 255]]);
        let mask = image(2, &[[0, 0, 0, 0], [0, 0, 0, 255]]);
        let names = ("expected.png", "actual.png", None);
        let unmasked = compare(&expected, &actual, names, 2, 0.0, None).unwrap();
        assert!(!unmasked.passed);
        assert_eq!(unmasked.different_pixels, 1);
        let masked = compare(&expected, &actual, names, 2, 0.0, Some(&mask)).unwrap();
        assert!(masked.passed);
        assert_eq!(
            (
                masked.compared_pixels,
                masked.masked_pixels,
                masked.different_pixels
            ),
            (1, 1, 0)
        );
    }

    #[test]
    fn a_fully_masked_comparison_is_refused() {
        let one = image(1, &[[0, 0, 0, 0]]);
        let mask = image(1, &[[0, 0, 0, 255]]);
        let error = compare(&one, &one, ("e", "a", None), 2, 0.001, Some(&mask)).unwrap_err();
        assert!(error.contains("excludes every pixel"));
    }

    #[test]
    fn a_size_mismatch_fails_with_a_report() {
        let one = image(1, &[[0; 4]]);
        let two = image(2, &[[0; 4], [0; 4]]);
        let c = compare(
            &one,
            &two,
            ("expected.png", "actual.png", None),
            2,
            0.001,
            None,
        )
        .unwrap();
        assert!(!c.passed);
        let node = json::parse(c.json.as_bytes()).unwrap();
        let members = json::object_members(&node).unwrap();
        let get = |n: &str| {
            members
                .iter()
                .find(|m| m.name == n)
                .unwrap()
                .value
                .value
                .clone()
        };
        assert_eq!(get("passed"), Value::Bool(false));
        assert_eq!(get("expectedPath"), Value::String("expected.png".into()));
        assert_eq!(get("actualPath"), Value::String("actual.png".into()));
    }

    #[test]
    fn the_summary_holds_the_route_checkpoint_and_portal_windows() {
        let frames = "frame,timestamp_ms,timestamp_utc,cpu_us,gpu_us,alloc_bytes,update_us,upload_us,imgui_us,pacing_us\n\
            0,0.000,2026-07-24T10:00:00.0000000Z,1000,500,100,100,10,0,0\n\
            1,1000.000,2026-07-24T10:00:01.0000000Z,2000,600,200,200,20,0,0\n\
            2,2000.000,2026-07-24T10:00:02.0000000Z,9000,700,300,300,30,0,0\n";
        let checkpoints = "{\"name\":\"baseline\",\"timestampUtc\":\"2026-07-24T10:00:01Z\",\"resources\":{\"processTotalAllocatedBytes\":1000}}\n\
            {\"name\":\"return\",\"timestampUtc\":\"2026-07-24T10:00:02Z\",\"resources\":{\"processTotalAllocatedBytes\":5000}}\n\n";
        let markers = "2026-07-24T10:00:01.0000000Z materialized destination='Arwic' occurrence=1 elapsed=1.0s";
        let out = summarize("frames.csv", frames, checkpoints, markers).unwrap();
        let root = json::parse(out.as_bytes()).unwrap();
        let get = |node: &Node, name: &str| -> Node {
            json::object_members(node)
                .unwrap()
                .iter()
                .find(|m| m.name == name)
                .unwrap()
                .value
                .clone()
        };
        assert_eq!(get(&root, "frameCount").value, Value::Number("3".into()));
        let route = get(&root, "route");
        assert_eq!(
            get(&get(&route, "cpuUs"), "p99").value,
            Value::Number("9000".into())
        );
        let windows = get(&root, "checkpointWindows");
        let windows = json::array_items(&windows).unwrap();
        assert_eq!(windows.len(), 2);
        assert_eq!(
            get(&windows[1], "processAllocationRateBytesPerSecond").value,
            Value::Number("4000".into())
        );
        let portals = get(&root, "portalWindows");
        let portal = &json::array_items(&portals).unwrap()[0];
        assert_eq!(
            get(portal, "destination").value,
            Value::String("Arwic".into())
        );
        let worst = get(portal, "worstFrames");
        assert_eq!(
            get(&json::array_items(&worst).unwrap()[0], "cpuUs").value,
            Value::Number("9000".into())
        );
    }

    #[test]
    fn utc_timestamps_read_and_write_round_trip() {
        let t = parse_utc("2026-07-24T10:00:01.1234567Z").unwrap();
        assert_eq!(format_utc(t), "2026-07-24T10:00:01.1234567Z");
        assert_eq!(parse_utc("1970-01-01T00:00:00Z"), Ok(0));
    }

    #[test]
    fn crop_and_probe_clamp_into_the_image() {
        let img = image(
            2,
            &[
                [1, 2, 3, 255],
                [4, 5, 6, 255],
                [7, 8, 9, 255],
                [10, 11, 12, 255],
            ],
        );
        let c = crop(&img, 1, 0, 9, 9, 2);
        assert_eq!((c.width, c.height), (2, 4));
        assert_eq!(&c.rgba[..4], &[4, 5, 6, 255]);
        let lines = probe(&img, -5, 0, 9, 1);
        assert_eq!(lines[1], "y=   0: 010203 040506 ");
        assert_eq!(lines.len(), 3);
    }
}

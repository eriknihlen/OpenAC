//! Colours as a markup `<color>` picker handles them (OpenAC
//! `MarkupColorValue`, `MarkupColorRamps`, `MarkupRecentColors`): packed
//! `0xAARRGGBB` (the byte order of the `#AARRGGBB` literal), their text,
//! and hue, saturation and value, the hue in degrees `[0, 360)` and the
//! others in `[0, 1]`.

use crate::control_state::ControlState;

/// Opaque white: what a colour that does not read shows as.
pub const WHITE: u32 = 0xFFFF_FFFF;

/// `#AARRGGBB` in upper case, or `#RRGGBB` without the alpha.
#[must_use]
pub fn format(argb: u32, with_alpha: bool) -> String {
    if with_alpha {
        format!("#{argb:08X}")
    } else {
        format!("#{:06X}", argb & 0x00FF_FFFF)
    }
}

/// What the hex field takes: an optional `#`, then 6 or 8 hex digits, any
/// case, surrounding spaces ignored; six digits get `alpha_for_rgb`.
#[must_use]
pub fn parse(text: &str, alpha_for_rgb: u8) -> Option<u32> {
    let mut digits = text.trim();
    if let Some(rest) = digits.strip_prefix('#') {
        digits = rest;
    }
    if !matches!(digits.len(), 6 | 8) || !digits.chars().all(|c| c.is_ascii_hexdigit()) {
        return None;
    }
    let value = u32::from_str_radix(digits, 16).ok()?;
    Some(if digits.len() == 6 {
        u32::from(alpha_for_rgb) << 24 | value
    } else {
        value
    })
}

/// Exactly `#` and 8 hex digits, as a bound or stored colour string.
#[must_use]
pub fn parse_strict(text: &str) -> Option<u32> {
    if text.len() == 9 && text.starts_with('#') {
        parse(text, 0xFF)
    } else {
        None
    }
}

/// The alpha byte.
#[must_use]
pub const fn alpha(argb: u32) -> u8 {
    (argb >> 24) as u8
}

/// The colour with another alpha.
#[must_use]
pub const fn with_alpha(argb: u32, alpha: u8) -> u32 {
    (argb & 0x00FF_FFFF) | (alpha as u32) << 24
}

/// Packs the bytes.
#[must_use]
pub const fn pack(a: u8, r: u8, g: u8, b: u8) -> u32 {
    (a as u32) << 24 | (r as u32) << 16 | (g as u32) << 8 | b as u32
}

/// A 0..=1 channel as a byte, rounded half away from zero.
#[must_use]
pub fn to_byte(channel: f32) -> u8 {
    (channel.clamp(0.0, 1.0) * 255.0).round() as u8
}

/// Red, green, blue, alpha as 0..=1.
#[must_use]
pub fn to_rgba(argb: u32) -> [f32; 4] {
    [
        ((argb >> 16) & 0xFF) as f32 / 255.0,
        ((argb >> 8) & 0xFF) as f32 / 255.0,
        (argb & 0xFF) as f32 / 255.0,
        (argb >> 24) as f32 / 255.0,
    ]
}

/// Packs 0..=1 red, green, blue, alpha.
#[must_use]
pub fn from_rgba(c: [f32; 4]) -> u32 {
    pack(to_byte(c[3]), to_byte(c[0]), to_byte(c[1]), to_byte(c[2]))
}

/// The colour's hue, saturation and value; grey has hue 0.
#[must_use]
#[allow(clippy::float_cmp)]
pub fn to_hsv(argb: u32) -> (f32, f32, f32) {
    let r = ((argb >> 16) & 0xFF) as f32 / 255.0;
    let g = ((argb >> 8) & 0xFF) as f32 / 255.0;
    let b = (argb & 0xFF) as f32 / 255.0;
    let max = r.max(g.max(b));
    let min = r.min(g.min(b));
    let delta = max - min;
    let s = if max <= 0.0 { 0.0 } else { delta / max };
    let mut h = if delta <= 0.0 {
        0.0
    } else if max == r {
        60.0 * ((g - b) / delta)
    } else if max == g {
        60.0 * ((b - r) / delta + 2.0)
    } else {
        60.0 * ((r - g) / delta + 4.0)
    };
    if h < 0.0 {
        h += 360.0;
    }
    (h, s, max)
}

/// The colour at a hue (any degrees, wrapped), saturation and value, with
/// `alpha`.
#[must_use]
pub fn from_hsv(h: f32, s: f32, v: f32, alpha: u8) -> u32 {
    let h = ((h % 360.0) + 360.0) % 360.0;
    let s = s.clamp(0.0, 1.0);
    let v = v.clamp(0.0, 1.0);
    let c = v * s;
    let x = c * (1.0 - ((h / 60.0) % 2.0 - 1.0).abs());
    let m = v - c;
    let (r, g, b) = match (h / 60.0) as i32 {
        0 => (c, x, 0.0),
        1 => (x, c, 0.0),
        2 => (0.0, c, x),
        3 => (0.0, x, c),
        4 => (x, 0.0, c),
        _ => (c, 0.0, x),
    };
    pack(alpha, to_byte(r + m), to_byte(g + m), to_byte(b + m))
}

/// The picker's gradients, straight-alpha RGBA8 rows top to bottom (OpenAC
/// `MarkupColorRamps`). The square is a fill of the pure hue with
/// [`white_to_clear`] and then [`clear_to_black`] over it.
pub mod ramps {
    use super::{from_hsv, to_byte};

    fn ramp(i: usize, count: usize, falling: bool) -> u8 {
        let t = if count > 1 {
            i as f32 / (count - 1) as f32
        } else {
            0.0
        };
        to_byte(if falling { 1.0 - t } else { t })
    }

    fn fill(width: usize, height: usize, pixel: impl Fn(usize, usize) -> [u8; 4]) -> Vec<u8> {
        let mut rgba = Vec::with_capacity(width * height * 4);
        for y in 0..height {
            for x in 0..width {
                rgba.extend_from_slice(&pixel(x, y));
            }
        }
        rgba
    }

    /// White, opaque at the left, clear at the right.
    #[must_use]
    pub fn white_to_clear(width: usize, height: usize) -> Vec<u8> {
        fill(width, height, |x, _| [255, 255, 255, ramp(x, width, true)])
    }

    /// Black, clear at the top, opaque at the bottom.
    #[must_use]
    pub fn clear_to_black(width: usize, height: usize) -> Vec<u8> {
        fill(width, height, |_, y| [0, 0, 0, ramp(y, height, false)])
    }

    /// The hues top to bottom, red at the top, opaque.
    #[must_use]
    pub fn hue(width: usize, height: usize) -> Vec<u8> {
        fill(width, height, |_, y| {
            let c = from_hsv(360.0 * y as f32 / height as f32, 1.0, 1.0, 0xFF);
            [(c >> 16) as u8, (c >> 8) as u8, c as u8, 255]
        })
    }

    /// White, opaque at the top, clear at the bottom; drawn tinted.
    #[must_use]
    pub fn alpha_ramp(width: usize, height: usize) -> Vec<u8> {
        fill(width, height, |_, y| [255, 255, 255, ramp(y, height, true)])
    }
}

/// The colours the player picked last, newest first, at most
/// [`Self::CAPACITY`], shared by every picker over the same state (OpenAC
/// `MarkupRecentColors`). With a state the list is remembered as
/// comma-separated `#AARRGGBB` text under the control `color`, kind
/// `recent`; the stored text is the truth, re-read on every use.
#[derive(Debug, Clone, Default)]
pub struct RecentColors {
    read: bool,
    text: Option<String>,
    colors: Vec<u32>,
}

impl RecentColors {
    /// The most colours kept.
    pub const CAPACITY: usize = 8;
    /// The control name the list is stored under.
    pub const CONTROL: &'static str = "color";
    /// The kind it is stored under.
    pub const KIND: &'static str = "recent";

    /// The list now (re-read from `state` when its text changed).
    pub fn colors(&mut self, state: Option<&mut (dyn ControlState + '_)>) -> &[u32] {
        self.refresh(state);
        &self.colors
    }

    /// Puts a picked colour first (moving it when it is already there).
    pub fn add(&mut self, argb: u32, state: Option<&mut (dyn ControlState + '_)>) {
        let mut state = state;
        self.refresh(state.as_mut().map(|s| &mut **s as &mut dyn ControlState));
        self.colors.retain(|c| *c != argb);
        self.colors.insert(0, argb);
        self.colors.truncate(Self::CAPACITY);
        if let Some(state) = state {
            let text = encode(&self.colors);
            state.set(Self::CONTROL, Self::KIND, &text);
            self.text = Some(text);
        }
    }

    fn refresh(&mut self, state: Option<&mut (dyn ControlState + '_)>) {
        let Some(state) = state else {
            return;
        };
        let text = state.get(Self::CONTROL, Self::KIND);
        if self.read && text == self.text {
            return;
        }
        self.read = true;
        self.colors = decode(text.as_deref());
        self.text = text;
    }
}

/// The stored text of a list.
#[must_use]
pub fn encode(colors: &[u32]) -> String {
    colors
        .iter()
        .map(|c| format(*c, true))
        .collect::<Vec<_>>()
        .join(",")
}

/// A stored list: unreadable entries skipped, repeats dropped, at most
/// [`RecentColors::CAPACITY`].
#[must_use]
pub fn decode(text: Option<&str>) -> Vec<u32> {
    let mut colors = Vec::new();
    let Some(text) = text.filter(|t| !t.is_empty()) else {
        return colors;
    };
    for part in text.split(',') {
        if let Some(c) = parse_strict(part)
            && !colors.contains(&c)
        {
            colors.push(c);
        }
        if colors.len() == RecentColors::CAPACITY {
            break;
        }
    }
    colors
}

#[cfg(test)]
mod tests;

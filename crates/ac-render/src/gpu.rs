//! Choosing the GPU, checking it can run the renderer, and deciding what a
//! GPU failure means.
//!
//! Pure policy: the window feeds it what wgpu reports and acts on the
//! answer, so every rule is tested without a GPU.

use std::fmt::Write as _;
use std::path::Path;

/// One adapter wgpu offers, reduced to what the choice needs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdapterCandidate {
    /// Position in wgpu's enumeration.
    pub index: usize,
    /// The adapter's name as the driver reports it.
    pub name: String,
    /// Discrete, integrated, virtual, software or other.
    pub device_type: wgpu::DeviceType,
    /// The graphics API it runs on.
    pub backend: wgpu::Backend,
}

/// The adapter to use and why, for the log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdapterChoice {
    /// Index into the candidates' enumeration.
    pub index: usize,
    /// How it was chosen.
    pub reason: String,
}

/// The environment variable that picks an adapter by index or name.
pub const ADAPTER_OVERRIDE_VAR: &str = "OPENAC_GPU_ADAPTER";

/// Lower is better: discrete, integrated, virtual, software, then anything
/// else.
#[must_use]
pub fn preference_rank(device_type: wgpu::DeviceType) -> u8 {
    match device_type {
        wgpu::DeviceType::DiscreteGpu => 0,
        wgpu::DeviceType::IntegratedGpu => 1,
        wgpu::DeviceType::VirtualGpu => 2,
        wgpu::DeviceType::Cpu => 3,
        wgpu::DeviceType::Other => 4,
    }
}

/// The best candidate: by device type, then enumeration order. `None`
/// without candidates.
#[must_use]
pub fn rank(candidates: &[AdapterCandidate]) -> Option<&AdapterCandidate> {
    candidates
        .iter()
        .min_by_key(|c| (preference_rank(c.device_type), c.index))
}

/// Choose an adapter. An override that is all decimal digits picks that
/// enumeration index; anything else picks the first adapter whose name
/// contains it (ignoring case). An override that matches nothing falls
/// back to the automatic choice, and the reason says so.
#[must_use]
pub fn choose_adapter(
    candidates: &[AdapterCandidate],
    adapter_override: Option<&str>,
) -> Option<AdapterChoice> {
    let automatic = rank(candidates)?;
    let describe =
        |c: &AdapterCandidate| format!("'{}' ({:?}, {:?})", c.name, c.device_type, c.backend);
    if let Some(wanted) = adapter_override.map(str::trim).filter(|w| !w.is_empty()) {
        let found = if is_decimal_index(wanted) {
            wanted
                .parse::<usize>()
                .ok()
                .and_then(|i| candidates.iter().find(|c| c.index == i))
                .map(|c| {
                    (
                        c,
                        format!(
                            "{ADAPTER_OVERRIDE_VAR}={wanted} selected adapter index {}.",
                            c.index
                        ),
                    )
                })
        } else {
            let lower = wanted.to_lowercase();
            candidates
                .iter()
                .find(|c| c.name.to_lowercase().contains(&lower))
                .map(|c| {
                    (
                        c,
                        format!(
                            "{ADAPTER_OVERRIDE_VAR}={wanted} matched adapter name '{}'.",
                            c.name
                        ),
                    )
                })
        };
        return Some(match found {
            Some((c, reason)) => AdapterChoice {
                index: c.index,
                reason,
            },
            None => AdapterChoice {
                index: automatic.index,
                reason: format!(
                    "{ADAPTER_OVERRIDE_VAR}={wanted} matched no adapter; fell back to the automatic choice {}.",
                    describe(automatic)
                ),
            },
        });
    }
    Some(AdapterChoice {
        index: automatic.index,
        reason: format!(
            "automatic: {} ranked first of {} adapter(s) that can present.",
            describe(automatic),
            candidates.len()
        ),
    })
}

/// An override that is digits and nothing else names an index.
#[must_use]
pub fn is_decimal_index(value: &str) -> bool {
    !value.is_empty() && value.bytes().all(|b| b.is_ascii_digit())
}

/// The environment variable that pretends one required capability is
/// missing, to exercise the refusal path.
pub const FORCE_UNSUPPORTED_VAR: &str = "OPENAC_GPU_FORCE_UNSUPPORTED";

/// The file the capability report is written to.
pub const REPORT_FILE_NAME: &str = "graphical-capabilities-wgpu.json";

/// What the renderer found out about the chosen adapter and whether it can
/// run on it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CapabilityRecord {
    /// Operating system and processor, e.g. `windows-x86_64`.
    pub platform: String,
    /// The adapter's name.
    pub device_name: String,
    /// Discrete, integrated, ...
    pub device_type: String,
    /// The graphics API.
    pub backend: String,
    /// Driver name and version as the driver reports them.
    pub driver: String,
    /// PCI vendor and device ids.
    pub vendor_device: (u32, u32),
    /// Every limit the renderer needs, with the adapter's value: (name,
    /// adapter, required).
    pub failed_limits: Vec<(String, u64, u64)>,
    /// The surface offers no format to present in.
    pub presents: bool,
    /// The adapter can time GPU work (optional; the frame profiler uses it).
    pub timestamps: bool,
    /// The required capability [`FORCE_UNSUPPORTED_VAR`] removed, if any.
    pub forced_unsupported: Option<String>,
    /// Why the renderer cannot run here; empty when it can.
    pub failures: Vec<String>,
}

impl CapabilityRecord {
    /// True when nothing is missing.
    #[must_use]
    pub fn is_supported(&self) -> bool {
        self.failures.is_empty()
    }
}

/// The current platform as `os-arch`.
#[must_use]
pub fn platform_name() -> String {
    format!("{}-{}", std::env::consts::OS, std::env::consts::ARCH)
}

/// Check an adapter against what the renderer needs: every WebGPU default
/// limit (the device is requested with them) and a surface it can present
/// to. `forced` removes one required limit by name to test the refusal.
#[must_use]
pub fn check_capabilities(
    info: &wgpu::AdapterInfo,
    limits: &wgpu::Limits,
    features: wgpu::Features,
    presents: bool,
    forced: Option<&str>,
) -> CapabilityRecord {
    let required = wgpu::Limits::default();
    let mut failed_limits = Vec::new();
    required.check_limits_with_fail_fn(limits, false, |name, needed, allowed| {
        failed_limits.push((name.to_owned(), allowed, needed));
    });
    let mut failures: Vec<String> = failed_limits
        .iter()
        .map(|(name, adapter, needed)| {
            format!("{name} is required to be at least {needed}; the adapter has {adapter}")
        })
        .collect();
    if !presents {
        failures.push("presenting to the window is required; the surface offers no format".into());
    }
    let forced = forced.map(str::trim).filter(|f| !f.is_empty());
    if let Some(name) = forced {
        let known = forceable_names().contains(&name);
        failures.push(if known {
            format!("{name} is required (forced unsupported by {FORCE_UNSUPPORTED_VAR})")
        } else {
            format!("{FORCE_UNSUPPORTED_VAR}={name}, which is not a required capability")
        });
    }
    CapabilityRecord {
        platform: platform_name(),
        device_name: info.name.clone(),
        device_type: format!("{:?}", info.device_type),
        backend: format!("{:?}", info.backend),
        driver: format!("{} {}", info.driver, info.driver_info)
            .trim()
            .to_owned(),
        vendor_device: (info.vendor, info.device),
        failed_limits,
        presents,
        timestamps: features.contains(wgpu::Features::TIMESTAMP_QUERY),
        forced_unsupported: forced.map(str::to_owned),
        failures,
    }
}

/// The names [`FORCE_UNSUPPORTED_VAR`] accepts: every limit the WebGPU
/// defaults ask more of than the downlevel tiers do (the ones a real
/// adapter can fall short of).
fn forceable_names() -> Vec<&'static str> {
    let mut names = Vec::new();
    let required = wgpu::Limits::default();
    for tier in [
        wgpu::Limits::downlevel_defaults(),
        wgpu::Limits::downlevel_webgl2_defaults(),
    ] {
        required.check_limits_with_fail_fn(&tier, false, |n, _, _| names.push(n));
    }
    names.sort_unstable();
    names.dedup();
    names
}

/// The message an unsupported adapter is refused with: the platform, the
/// device, each failure and where the full report is.
#[must_use]
pub fn unsupported_message(record: &CapabilityRecord, report_path: &Path) -> String {
    let mut text = format!(
        "openAC's renderer is unsupported by the selected GPU.\nPlatform: {}, {} ({}), {}, {}\n",
        record.platform, record.device_name, record.device_type, record.backend, record.driver
    );
    for failure in &record.failures {
        let _ = writeln!(text, " - {failure}");
    }
    let _ = write!(
        text,
        "Try another adapter with {ADAPTER_OVERRIDE_VAR}=<index or name>, or update the graphics driver.\nFull capability report: {}",
        report_path.display()
    );
    text
}

/// The capability report as JSON, for a bug report.
#[must_use]
pub fn report_json(record: &CapabilityRecord, choice_reason: &str) -> String {
    let s = |text: &str| json_string(text);
    let mut out = String::from("{\n");
    let _ = writeln!(out, "  \"schemaVersion\": 1,");
    let _ = writeln!(out, "  \"platform\": {},", s(&record.platform));
    let _ = writeln!(out, "  \"deviceName\": {},", s(&record.device_name));
    let _ = writeln!(out, "  \"deviceType\": {},", s(&record.device_type));
    let _ = writeln!(out, "  \"backend\": {},", s(&record.backend));
    let _ = writeln!(out, "  \"driver\": {},", s(&record.driver));
    let _ = writeln!(out, "  \"vendorId\": {},", record.vendor_device.0);
    let _ = writeln!(out, "  \"deviceId\": {},", record.vendor_device.1);
    let _ = writeln!(out, "  \"selection\": {},", s(choice_reason));
    let _ = writeln!(out, "  \"presents\": {},", record.presents);
    let _ = writeln!(out, "  \"timestampQueries\": {},", record.timestamps);
    let _ = writeln!(
        out,
        "  \"forcedUnsupported\": {},",
        record
            .forced_unsupported
            .as_deref()
            .map_or_else(|| "null".to_owned(), s)
    );
    let failures: Vec<String> = record.failures.iter().map(|f| s(f)).collect();
    let _ = writeln!(out, "  \"supported\": {},", record.is_supported());
    let _ = writeln!(out, "  \"failures\": [{}]", failures.join(", "));
    out.push_str("}\n");
    out
}

/// `text` as a JSON string literal.
#[must_use]
pub fn json_string(text: &str) -> String {
    let mut out = String::with_capacity(text.len() + 2);
    out.push('"');
    for c in text.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if u32::from(c) < 0x20 => {
                let _ = write!(out, "\\u{:04x}", u32::from(c));
            }
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

/// Something that went wrong on the GPU.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum GpuFault {
    /// The device is gone (driver reset, GPU removed).
    DeviceLost(String),
    /// GPU or host memory ran out.
    OutOfMemory,
    /// The window's surface is gone.
    SurfaceLost,
    /// A command was invalid (a bug in what was submitted).
    Validation(String),
    /// Any other failure the driver reports.
    Internal(String),
}

impl GpuFault {
    /// A wgpu error raised outside an error scope.
    #[must_use]
    pub fn from_wgpu(error: &wgpu::Error) -> Self {
        match error {
            wgpu::Error::OutOfMemory { .. } => Self::OutOfMemory,
            wgpu::Error::Validation { description, .. } => Self::Validation(description.clone()),
            wgpu::Error::Internal { description, .. } => Self::Internal(description.clone()),
        }
    }
}

impl std::fmt::Display for GpuFault {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::DeviceLost(why) => write!(f, "the GPU device was lost: {why}"),
            Self::OutOfMemory => f.write_str("the GPU ran out of memory"),
            Self::SurfaceLost => f.write_str("the window surface was lost"),
            Self::Validation(what) => write!(f, "GPU validation error: {what}"),
            Self::Internal(what) => write!(f, "GPU internal error: {what}"),
        }
    }
}

/// Whether a fault ends the window: a lost device, exhausted memory or a
/// lost surface cannot be drawn past, so the window closes (the session
/// keeps running and a new window can attach); a validation or internal
/// error spoils one frame and drawing continues.
#[must_use]
pub fn is_fatal(fault: &GpuFault) -> bool {
    matches!(
        fault,
        GpuFault::DeviceLost(_) | GpuFault::OutOfMemory | GpuFault::SurfaceLost
    )
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/Gpu/Vk/VulkanDeviceSelectionTests.cs`,
    //! `VulkanCapabilityGateTests.cs` and `VulkanRenderFailurePolicyTests.cs`.
    use super::*;
    use wgpu::DeviceType as T;

    fn device(index: usize, name: &str, device_type: T) -> AdapterCandidate {
        AdapterCandidate {
            index,
            name: name.to_owned(),
            device_type,
            backend: wgpu::Backend::Vulkan,
        }
    }

    #[test]
    fn discrete_beats_integrated_beats_virtual_beats_cpu_beats_other() {
        let ranks: Vec<u8> = [
            T::DiscreteGpu,
            T::IntegratedGpu,
            T::VirtualGpu,
            T::Cpu,
            T::Other,
        ]
        .into_iter()
        .map(preference_rank)
        .collect();
        assert!(ranks.windows(2).all(|w| w[0] < w[1]));
    }

    #[test]
    fn the_discrete_gpu_wins_over_an_integrated_one() {
        let candidates = [
            device(0, "Intel Iris Xe", T::IntegratedGpu),
            device(1, "AMD Radeon RX 9070 XT", T::DiscreteGpu),
        ];
        let choice = choose_adapter(&candidates, None).unwrap();
        assert_eq!(choice.index, 1);
        assert!(choice.reason.contains("automatic"));
    }

    #[test]
    fn identical_devices_are_broken_by_enumeration_index() {
        let mut candidates = vec![
            device(0, "Twin", T::DiscreteGpu),
            device(1, "Twin", T::DiscreteGpu),
        ];
        assert_eq!(choose_adapter(&candidates, None).unwrap().index, 0);
        candidates.reverse();
        assert_eq!(choose_adapter(&candidates, None).unwrap().index, 0);
    }

    #[test]
    fn software_devices_are_last_but_still_selectable() {
        let candidates = [device(0, "llvmpipe", T::Cpu)];
        assert_eq!(choose_adapter(&candidates, None).unwrap().index, 0);
    }

    #[test]
    fn no_adapter_gives_no_choice() {
        assert_eq!(choose_adapter(&[], None), None);
    }

    #[test]
    fn the_override_matches_a_decimal_index() {
        let candidates = [
            device(0, "AMD Radeon RX 9070 XT", T::DiscreteGpu),
            device(1, "Intel Iris Xe", T::IntegratedGpu),
        ];
        let choice = choose_adapter(&candidates, Some("1")).unwrap();
        assert_eq!(choice.index, 1);
        assert!(choice.reason.contains("OPENAC_GPU_ADAPTER=1"));
    }

    #[test]
    fn the_override_matches_a_name_substring_ignoring_case() {
        let candidates = [
            device(0, "AMD Radeon RX 9070 XT", T::DiscreteGpu),
            device(1, "Intel Iris Xe", T::IntegratedGpu),
        ];
        for wanted in ["iris", "IRIS", "Intel Iris Xe"] {
            assert_eq!(
                choose_adapter(&candidates, Some(wanted)).unwrap().index,
                1,
                "{wanted}"
            );
        }
    }

    #[test]
    fn an_override_matching_nothing_falls_back_and_says_so() {
        let candidates = [
            device(0, "Intel Iris Xe", T::IntegratedGpu),
            device(1, "AMD Radeon RX 9070 XT", T::DiscreteGpu),
        ];
        for wanted in ["7", "nvidia"] {
            let choice = choose_adapter(&candidates, Some(wanted)).unwrap();
            assert_eq!(choice.index, 1);
            assert!(
                choice.reason.contains("matched no adapter"),
                "{}",
                choice.reason
            );
        }
        assert!(is_decimal_index("12"));
        assert!(!is_decimal_index("1a"));
        assert!(!is_decimal_index(""));
    }

    fn info() -> wgpu::AdapterInfo {
        wgpu::AdapterInfo {
            name: "AMD Radeon RX 9070 XT".into(),
            vendor: 0x1002,
            device: 0x7550,
            driver: "AMD proprietary driver".into(),
            driver_info: "25.9.1".into(),
            ..wgpu::AdapterInfo::new(T::DiscreteGpu, wgpu::Backend::Vulkan)
        }
    }

    #[test]
    fn a_complete_adapter_is_accepted() {
        let record = check_capabilities(
            &info(),
            &wgpu::Limits::default(),
            wgpu::Features::TIMESTAMP_QUERY,
            true,
            None,
        );
        assert!(record.is_supported(), "{:?}", record.failures);
        assert!(record.timestamps);
    }

    #[test]
    fn a_limit_below_the_requirement_is_rejected_by_name() {
        let limits = wgpu::Limits {
            max_texture_array_layers: 64,
            ..wgpu::Limits::default()
        };
        let record = check_capabilities(&info(), &limits, wgpu::Features::empty(), true, None);
        assert!(!record.is_supported());
        assert!(
            record
                .failures
                .iter()
                .any(|f| f.contains("max_texture_array_layers"))
        );
        assert!(!record.timestamps);
    }

    #[test]
    fn an_adapter_that_cannot_present_is_rejected() {
        let record = check_capabilities(
            &info(),
            &wgpu::Limits::default(),
            wgpu::Features::empty(),
            false,
            None,
        );
        assert!(record.failures.iter().any(|f| f.contains("presenting")));
    }

    #[test]
    fn the_forced_unsupported_knob_rejects_the_named_requirement() {
        let record = check_capabilities(
            &info(),
            &wgpu::Limits::default(),
            wgpu::Features::empty(),
            true,
            Some("max_texture_dimension_2d"),
        );
        assert!(!record.is_supported());
        assert_eq!(
            record.forced_unsupported.as_deref(),
            Some("max_texture_dimension_2d")
        );
        assert!(
            record
                .failures
                .iter()
                .any(|f| f.contains("max_texture_dimension_2d is required"))
        );
        let unset = check_capabilities(
            &info(),
            &wgpu::Limits::default(),
            wgpu::Features::empty(),
            true,
            Some("  "),
        );
        assert!(unset.is_supported());
        assert_eq!(unset.forced_unsupported, None);
    }

    #[test]
    fn the_forced_unsupported_knob_fails_on_an_unknown_name() {
        let record = check_capabilities(
            &info(),
            &wgpu::Limits::default(),
            wgpu::Features::empty(),
            true,
            Some("TeapotShading"),
        );
        assert!(
            record
                .failures
                .iter()
                .any(|f| f.contains("which is not a required capability"))
        );
    }

    #[test]
    fn the_message_names_platform_device_failures_and_report() {
        let record = check_capabilities(
            &info(),
            &wgpu::Limits::default(),
            wgpu::Features::empty(),
            true,
            Some("max_storage_buffers_per_shader_stage"),
        );
        let message =
            unsupported_message(&record, Path::new("diag/graphical-capabilities-wgpu.json"));
        assert!(message.contains(&platform_name()));
        assert!(message.contains("AMD Radeon RX 9070 XT"));
        assert!(message.contains("max_storage_buffers_per_shader_stage is required"));
        assert!(message.contains("graphical-capabilities-wgpu.json"));
        assert!(message.contains(ADAPTER_OVERRIDE_VAR));
    }

    #[test]
    fn the_report_carries_readable_identifying_fields() {
        let record = check_capabilities(
            &info(),
            &wgpu::Limits::default(),
            wgpu::Features::empty(),
            true,
            None,
        );
        let json = report_json(&record, "automatic: \"x\"");
        assert!(json.contains("\"deviceName\": \"AMD Radeon RX 9070 XT\""));
        assert!(json.contains("\"deviceType\": \"DiscreteGpu\""));
        assert!(json.contains("\"backend\": \"Vulkan\""));
        assert!(json.contains("\"selection\": \"automatic: \\\"x\\\"\""));
        assert!(json.contains("\"supported\": true"));
        assert_eq!(REPORT_FILE_NAME, "graphical-capabilities-wgpu.json");
    }

    #[test]
    fn lost_device_memory_and_surface_are_fatal() {
        assert!(is_fatal(&GpuFault::DeviceLost("reset".into())));
        assert!(is_fatal(&GpuFault::OutOfMemory));
        assert!(is_fatal(&GpuFault::SurfaceLost));
    }

    #[test]
    fn validation_and_internal_errors_are_not_fatal() {
        assert!(!is_fatal(&GpuFault::Validation("bad draw".into())));
        assert!(!is_fatal(&GpuFault::Internal("pipeline".into())));
    }
}

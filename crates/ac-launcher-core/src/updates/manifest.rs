//! The release manifest: which client and launcher payloads a release has,
//! per platform, with their size and SHA-256, read strictly.

use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::Duration;

use ac_host_core::json::{self as host_json, Node, ObjectReader};

use super::UpdateError;
use super::payload::is_valid_rid;
use super::version::LauncherVersion;
use crate::cancel::CancelToken;
use crate::net::{self, HttpTransport};
use crate::url::WebUrl;

/// Largest manifest accepted.
pub const MAXIMUM_MANIFEST_BYTES: u64 = 1024 * 1024;

/// Largest payload a manifest may name.
pub const MAXIMUM_ARTIFACT_BYTES: u64 = 4 * 1024 * 1024 * 1024;

/// Where the launcher looks for releases by default.
pub const PRODUCTION_MANIFEST_URI: &str =
    "https://github.com/eriknihlen/openac-rust/releases/latest/download/manifest.json";

/// The schema version this reader understands.
pub const CURRENT_MANIFEST_SCHEMA_VERSION: i32 = 1;

/// The launcher fingerprint's file, beside `manifest.json` in a release.
pub const LAUNCHER_FINGERPRINT_FILE_NAME: &str = "launcher-fingerprint.json";

/// The fingerprint schema version this reader understands.
pub const CURRENT_FINGERPRINT_SCHEMA_VERSION: i32 = 1;

/// How long the optional fingerprint may take before a check goes on
/// without it.
pub const DEFAULT_FINGERPRINT_TIMEOUT: Duration = Duration::from_secs(5);

/// The fingerprint of a release's launcher: one SHA-256 over what the
/// launcher is built from, published beside the manifest. It changes only
/// when the launcher does, so a launcher carrying the same fingerprint is
/// that release's launcher whatever its version says.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherFingerprintDocument {
    /// The release it belongs to.
    pub version: LauncherVersion,
    /// The SHA-256, lowercase hex.
    pub fingerprint: String,
}

impl LauncherFingerprintDocument {
    /// Reads one strictly: schema version 1, a strict SemVer version, a
    /// SHA-256, nothing else.
    pub fn parse(utf8: &[u8]) -> Result<Self, UpdateError> {
        let invalid = |error: host_json::JsonError| {
            UpdateError::failed(format!("The launcher fingerprint is invalid: {error}"))
        };
        let refused = || UpdateError::failed("The launcher fingerprint is invalid.");
        let root = host_json::parse(utf8).map_err(invalid)?;
        if crate::json::depth(&root) > 16 || host_json::is_null(&root) {
            return Err(refused());
        }
        let mut object = ObjectReader::new(&root).map_err(invalid)?;
        let schema_version = match object.take("schemaVersion") {
            Some(node) => host_json::int32(node).map_err(invalid)?,
            None => 0,
        };
        let version = optional_string(&mut object, "version").map_err(invalid)?;
        let fingerprint = optional_string(&mut object, "fingerprint").map_err(invalid)?;
        object.finish().map_err(invalid)?;
        let version = version.as_deref().and_then(LauncherVersion::try_parse);
        match (version, fingerprint) {
            (Some(version), Some(fingerprint))
                if schema_version == CURRENT_FINGERPRINT_SCHEMA_VERSION
                    && is_sha256(&fingerprint) =>
            {
                Ok(Self {
                    version,
                    fingerprint: fingerprint.to_ascii_lowercase(),
                })
            }
            _ => Err(refused()),
        }
    }
}

/// One payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseArtifact {
    /// Where to download it.
    pub url: WebUrl,
    /// SHA-256, lowercase hex.
    pub sha256: String,
    /// Size in bytes.
    pub size: u64,
}

/// A release.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseManifest {
    /// The release version.
    pub version: LauncherVersion,
    /// The oldest launcher that may install its client.
    pub minimum_launcher_version: LauncherVersion,
    /// Client payloads by runtime identifier.
    pub clients: BTreeMap<String, ReleaseArtifact>,
    /// Launcher payloads by runtime identifier.
    pub launchers: BTreeMap<String, ReleaseArtifact>,
}

impl ReleaseManifest {
    /// The client payload for `rid`.
    pub fn require_client(&self, rid: &str) -> Result<&ReleaseArtifact, UpdateError> {
        self.clients.get(rid).ok_or_else(|| {
            UpdateError::failed(format!(
                "Release {} has no client payload for RID '{rid}'.",
                self.version
            ))
        })
    }

    /// The launcher payload for `rid`.
    pub fn require_launcher(&self, rid: &str) -> Result<&ReleaseArtifact, UpdateError> {
        self.launchers.get(rid).ok_or_else(|| {
            UpdateError::failed(format!(
                "Release {} has no launcher payload for RID '{rid}'.",
                self.version
            ))
        })
    }

    /// Reads a manifest. Unknown or repeated members, a depth over 16,
    /// another schema version, versions that are not strict SemVer, a
    /// minimum above the release, invalid platform names, non-HTTPS URLs
    /// (loopback HTTP only when allowed), bad digests and sizes outside
    /// 1..=4 GiB are all refused.
    pub fn parse(utf8: &[u8], allow_loopback_http_artifacts: bool) -> Result<Self, UpdateError> {
        let invalid = |error: host_json::JsonError| {
            UpdateError::failed(format!("The release manifest is invalid: {error}"))
        };
        let root = host_json::parse(utf8).map_err(invalid)?;
        if crate::json::depth(&root) > 16 {
            return Err(UpdateError::failed(
                "The release manifest is invalid: nested too deeply.",
            ));
        }
        if host_json::is_null(&root) {
            return Err(UpdateError::failed("The release manifest is empty."));
        }
        let mut object = ObjectReader::new(&root).map_err(invalid)?;
        let schema_version = match object.take("schemaVersion") {
            Some(node) => host_json::int32(node).map_err(invalid)?,
            None => 0,
        };
        let version = optional_string(&mut object, "version").map_err(invalid)?;
        let minimum = optional_string(&mut object, "minimumLauncherVersion").map_err(invalid)?;
        let clients = object.take("clients");
        let launchers = object.take("launchers");
        object.finish().map_err(invalid)?;

        if schema_version != CURRENT_MANIFEST_SCHEMA_VERSION {
            return Err(UpdateError::failed(format!(
                "Release manifest schema version {schema_version} is not supported."
            )));
        }
        let version = parse_version(
            version.ok_or_else(|| UpdateError::failed("The release version is missing."))?,
        )?;
        let minimum = parse_version(
            minimum
                .ok_or_else(|| UpdateError::failed("The minimum launcher version is missing."))?,
        )?;
        if minimum > version {
            return Err(UpdateError::failed(
                "The minimum launcher version cannot exceed the release version.",
            ));
        }
        let clients = validate_artifacts(clients, "clients", allow_loopback_http_artifacts)?;
        let launchers = validate_artifacts(launchers, "launchers", allow_loopback_http_artifacts)?;
        Ok(Self {
            version,
            minimum_launcher_version: minimum,
            clients,
            launchers,
        })
    }
}

fn parse_version(text: String) -> Result<LauncherVersion, UpdateError> {
    LauncherVersion::try_parse(&text).ok_or_else(|| {
        UpdateError::failed(format!(
            "The release manifest is invalid: '{text}' is not a strict SemVer 2.0 version."
        ))
    })
}

fn optional_string(
    object: &mut ObjectReader<'_>,
    name: &str,
) -> Result<Option<String>, host_json::JsonError> {
    match object.take(name) {
        Some(node) => host_json::string_or_null(node),
        None => Ok(None),
    }
}

/// 64 hex digits of either case.
pub fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn validate_artifacts(
    node: Option<&Node>,
    field: &str,
    allow_loopback_http: bool,
) -> Result<BTreeMap<String, ReleaseArtifact>, UpdateError> {
    let invalid = |error: host_json::JsonError| {
        UpdateError::failed(format!("The release manifest is invalid: {error}"))
    };
    let members = match node {
        Some(node) if !host_json::is_null(node) => {
            host_json::object_members(node).map_err(invalid)?
        }
        _ => &[],
    };
    if members.is_empty() {
        return Err(UpdateError::failed(format!(
            "Manifest field '{field}' must not be empty."
        )));
    }
    let mut result = BTreeMap::new();
    for member in members {
        let rid = &member.name;
        if !is_valid_rid(rid) {
            return Err(UpdateError::failed(format!(
                "Manifest field '{field}' contains invalid RID '{rid}'."
            )));
        }
        if host_json::is_null(&member.value) {
            return Err(UpdateError::failed(format!(
                "Manifest payload '{field}.{rid}' is null."
            )));
        }
        let mut artifact = ObjectReader::new(&member.value).map_err(invalid)?;
        let url = optional_string(&mut artifact, "url").map_err(invalid)?;
        let sha256 = optional_string(&mut artifact, "sha256").map_err(invalid)?;
        let size = match artifact.take("size") {
            Some(node) => crate::json::int64(node).map_err(invalid)?,
            None => 0,
        };
        artifact.finish().map_err(invalid)?;
        let url = url.as_deref().and_then(WebUrl::parse).ok_or_else(|| {
            UpdateError::failed(format!(
                "Manifest payload '{field}.{rid}' has an invalid URL."
            ))
        })?;
        net::require_transport(
            &url,
            &format!("{field}.{rid} artifact"),
            allow_loopback_http,
        )?;
        let sha256 = sha256.filter(|value| is_sha256(value)).ok_or_else(|| {
            UpdateError::failed(format!(
                "Manifest payload '{field}.{rid}' has an invalid SHA-256 digest."
            ))
        })?;
        let size = u64::try_from(size)
            .ok()
            .filter(|&size| size > 0 && size <= MAXIMUM_ARTIFACT_BYTES)
            .ok_or_else(|| {
                UpdateError::failed(format!(
                    "Manifest payload '{field}.{rid}' has an invalid size."
                ))
            })?;
        result.insert(
            rid.clone(),
            ReleaseArtifact {
                url,
                sha256: sha256.to_ascii_lowercase(),
                size,
            },
        );
    }
    Ok(result)
}

/// Where release manifests come from (a fake in tests).
pub trait ReleaseManifestSource: Send + Sync {
    /// Fetches and checks the current manifest.
    fn fetch(&self, cancel: &CancelToken) -> Result<ReleaseManifest, UpdateError>;

    /// The launcher fingerprint published beside the manifest, or `None`
    /// when the release has none or it cannot be read (the launcher then
    /// compares versions). Only the caller's cancellation is an error.
    fn fetch_launcher_fingerprint(
        &self,
        _cancel: &CancelToken,
    ) -> Result<Option<LauncherFingerprintDocument>, UpdateError> {
        Ok(None)
    }
}

/// Fetches the manifest over HTTPS through the host's transport, following
/// only checked redirects and reading at most 1 MiB.
pub struct ReleaseManifestClient {
    transport: Arc<dyn HttpTransport>,
    manifest_uri: WebUrl,
    allow_loopback_http: bool,
    timeout: Duration,
    fingerprint_timeout: Duration,
}

impl std::fmt::Debug for ReleaseManifestClient {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ReleaseManifestClient")
            .field("manifest_uri", &self.manifest_uri)
            .field("allow_loopback_http", &self.allow_loopback_http)
            .finish_non_exhaustive()
    }
}

impl ReleaseManifestClient {
    /// The production feed's address, the launcher's default.
    pub fn production_manifest_uri() -> WebUrl {
        WebUrl::parse(PRODUCTION_MANIFEST_URI)
            .unwrap_or_else(|| unreachable!("the production URI is valid"))
    }

    /// The client for the feed the launcher was started with: the
    /// production feed, or a process-local override
    /// ([`Self::local_update_feed_override`]) for any other address.
    pub fn for_feed(
        transport: Arc<dyn HttpTransport>,
        manifest_uri: &WebUrl,
    ) -> Result<Self, UpdateError> {
        if *manifest_uri == Self::production_manifest_uri() {
            Ok(Self::production(transport))
        } else {
            Self::local_update_feed_override(transport, &manifest_uri.to_string())
        }
    }

    /// The production feed.
    pub fn production(transport: Arc<dyn HttpTransport>) -> Self {
        Self {
            transport,
            manifest_uri: Self::production_manifest_uri(),
            allow_loopback_http: false,
            timeout: Duration::from_secs(15),
            fingerprint_timeout: DEFAULT_FINGERPRINT_TIMEOUT,
        }
    }

    /// A process-local feed (`--update-manifest-uri`): HTTPS, or HTTP to
    /// this machine for fixtures; no user information, query or fragment.
    pub fn local_update_feed_override(
        transport: Arc<dyn HttpTransport>,
        manifest_uri: &str,
    ) -> Result<Self, UpdateError> {
        let uri = WebUrl::parse(manifest_uri)
            .ok_or_else(|| UpdateError::failed("The manifest URI is not an absolute URI."))?;
        if !uri.user_info().is_empty() || !uri.query().is_empty() || !uri.fragment().is_empty() {
            return Err(UpdateError::failed(
                "A process-local manifest URI cannot contain user information, a query, or a \
                 fragment.",
            ));
        }
        let allow_loopback_http = uri.is_http() && uri.is_loopback();
        if !uri.is_https() && !allow_loopback_http {
            return Err(UpdateError::failed(
                "A process-local manifest URI must use HTTPS (loopback HTTP is fixture-only).",
            ));
        }
        Ok(Self {
            transport,
            manifest_uri: uri,
            allow_loopback_http,
            timeout: Duration::from_secs(15),
            fingerprint_timeout: DEFAULT_FINGERPRINT_TIMEOUT,
        })
    }

    /// A client for any URI, with loopback HTTP allowed or not (transport
    /// tests).
    pub fn for_transport_test(
        transport: Arc<dyn HttpTransport>,
        manifest_uri: WebUrl,
        allow_loopback_http: bool,
    ) -> Result<Self, UpdateError> {
        net::require_transport(&manifest_uri, "manifest", allow_loopback_http)?;
        Ok(Self {
            transport,
            manifest_uri,
            allow_loopback_http,
            timeout: Duration::from_secs(15),
            fingerprint_timeout: DEFAULT_FINGERPRINT_TIMEOUT,
        })
    }

    /// The feed this client reads.
    pub fn manifest_uri(&self) -> &WebUrl {
        &self.manifest_uri
    }

    /// Replaces how long the optional fingerprint may take (tests).
    #[must_use]
    pub fn with_fingerprint_timeout(mut self, timeout: Duration) -> Self {
        self.fingerprint_timeout = timeout;
        self
    }

    /// Reads `start` (following checked redirects, at most 1 MiB) within
    /// `budget` overall, each request also within the client's timeout.
    /// `description` names it in errors ("release manifest").
    fn fetch_bytes(
        &self,
        start: WebUrl,
        description: &str,
        budget: Option<Duration>,
        cancel: &CancelToken,
    ) -> Result<Vec<u8>, UpdateError> {
        let wrap = |error: UpdateError| match error {
            UpdateError::Transport(net::HttpError::Cancelled) => UpdateError::Cancelled,
            UpdateError::Transport(transport) => UpdateError::failed(format!(
                "The {description} could not be loaded: {transport}"
            )),
            other => other,
        };
        let deadline = budget.map(|budget| std::time::Instant::now() + budget);
        let redirect = format!("{description} redirect");
        let mut current = start;
        let mut visited = std::collections::HashSet::new();
        let mut redirects = 0;
        loop {
            net::require_transport(&current, &redirect, self.allow_loopback_http)?;
            if !visited.insert(current.to_string()) {
                return Err(UpdateError::failed(format!(
                    "The {description} redirect chain contains a loop."
                )));
            }
            cancel.check()?;
            let timeout = match deadline {
                None => self.timeout,
                Some(deadline) => {
                    let left = deadline.saturating_duration_since(std::time::Instant::now());
                    if left.is_zero() {
                        return Err(wrap(UpdateError::Transport(net::HttpError::TimedOut)));
                    }
                    left.min(self.timeout)
                }
            };
            let mut response = self
                .transport
                .get(
                    &net::HttpRequest {
                        url: current.clone(),
                        timeout: Some(timeout),
                    },
                    cancel,
                )
                .map_err(|error| wrap(UpdateError::Transport(error)))?;
            if matches!(response.status, 301 | 302 | 303 | 307 | 308) {
                if redirects >= net::MAXIMUM_REDIRECTS {
                    return Err(UpdateError::failed(format!(
                        "The {description} exceeded {} redirects.",
                        net::MAXIMUM_REDIRECTS
                    )));
                }
                let location = response.location.as_deref().ok_or_else(|| {
                    UpdateError::failed(format!(
                        "The {description} redirect has no Location header."
                    ))
                })?;
                let next = current.join(location).ok_or_else(|| {
                    UpdateError::failed(format!(
                        "The {description} redirect Location is not a URI."
                    ))
                })?;
                net::require_transport(&next, &redirect, self.allow_loopback_http)?;
                current = next;
                redirects += 1;
                continue;
            }
            if !(200..300).contains(&response.status) {
                return Err(UpdateError::failed(format!(
                    "The {description} could not be loaded: Response status code does not \
                     indicate success: {}.",
                    response.status
                )));
            }
            return net::read_bounded(
                &mut response,
                MAXIMUM_MANIFEST_BYTES,
                &format!("The {description} is larger than {MAXIMUM_MANIFEST_BYTES} bytes."),
                cancel,
            )
            .map_err(wrap);
        }
    }
}

impl ReleaseManifestSource for ReleaseManifestClient {
    fn fetch(&self, cancel: &CancelToken) -> Result<ReleaseManifest, UpdateError> {
        let bytes =
            self.fetch_bytes(self.manifest_uri.clone(), "release manifest", None, cancel)?;
        ReleaseManifest::parse(&bytes, self.allow_loopback_http)
    }

    fn fetch_launcher_fingerprint(
        &self,
        cancel: &CancelToken,
    ) -> Result<Option<LauncherFingerprintDocument>, UpdateError> {
        let Some(uri) = self.manifest_uri.join(LAUNCHER_FINGERPRINT_FILE_NAME) else {
            return Ok(None);
        };
        let fetched = self.fetch_bytes(
            uri,
            "launcher fingerprint",
            Some(self.fingerprint_timeout),
            cancel,
        );
        match fetched.and_then(|bytes| LauncherFingerprintDocument::parse(&bytes)) {
            Ok(document) => Ok(Some(document)),
            // The file is optional: a release from before fingerprints has
            // none, and one that is slow, unreachable or unreadable leaves
            // the check to compare versions. Stopping still stops.
            Err(_) if cancel.is_cancelled() => Err(UpdateError::Cancelled),
            Err(_) => Ok(None),
        }
    }
}

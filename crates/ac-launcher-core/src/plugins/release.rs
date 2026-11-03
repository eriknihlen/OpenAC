//! Plugin releases on GitHub: fetching small release documents (catching
//! the tag a `latest` redirect resolves to, which the curated list uses),
//! reading the releases Atom feed for this client's releases (`wasm-v`, and
//! `wasm-<plugin id>-v` in a repository that publishes several plugins), and
//! resolving a repository (and plugin id) to the release to offer.

use std::sync::Arc;

use super::manifest::{InstallRules, PluginManifest, WASM_TAG_PREFIX, plugin_tag_prefix};
use super::record_store::PluginReleaseChannel;
use super::rules::github;
use crate::cancel::CancelToken;
use crate::net::{self, HttpTransport};
use crate::updates::{LauncherVersion, UpdateError};
use crate::url::WebUrl;

/// Largest small document (list, `plugin.json`, `.sha256`).
pub const MAXIMUM_DOCUMENT_BYTES: u64 = 256 * 1024;

/// Largest releases feed.
pub const FEED_MAXIMUM_BYTES: u64 = 1024 * 1024;

/// How many candidates naming another plugin a channel skips before it
/// gives up, bounding the `plugin.json` fetches one resolution makes.
pub const MAXIMUM_REFUSED_CANDIDATES: usize = 3;

/// A fetched document and, when a `latest/download` redirect named one, the
/// release tag.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginReleaseDocument {
    /// The bytes.
    pub content: Vec<u8>,
    /// The tag.
    pub tag: Option<String>,
}

/// How a fetch went.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PluginReleaseFetchResult {
    /// Fetched.
    Success(PluginReleaseDocument),
    /// GitHub is rate limiting (HTTP 429).
    RateLimited,
    /// Unreachable, not found, or redirected to another repository.
    Unavailable,
}

/// Fetches plugin release documents through the host's transport.
#[derive(Clone)]
pub struct PluginReleaseClient {
    transport: Arc<dyn HttpTransport>,
}

impl std::fmt::Debug for PluginReleaseClient {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("PluginReleaseClient")
    }
}

/// The tag in a redirect shaped `/<owner>/<repo>/releases/download/<tag>/...`:
/// `Ok(Some)` for the asked-for repository, `Ok(None)` for another shape,
/// `Err` for a release of another repository.
fn match_tag(target: &WebUrl, owner: &str, repo: &str) -> Result<Option<String>, ()> {
    let segments = target.segments();
    if segments.len() < 5 || segments[2] != "releases" || segments[3] != "download" {
        return Ok(None);
    }
    if !segments[0].eq_ignore_ascii_case(owner) || !segments[1].eq_ignore_ascii_case(repo) {
        return Err(());
    }
    Ok(Some(segments[4].to_owned()))
}

impl PluginReleaseClient {
    /// A client over `transport`.
    pub fn new(transport: Arc<dyn HttpTransport>) -> Self {
        Self { transport }
    }

    /// Fetches a document of at most `maximum_bytes`. Transport failures,
    /// non-200 answers and redirects to another repository are
    /// `Unavailable`; 429 is `RateLimited`; a non-HTTPS hop or an oversized
    /// body is an error.
    pub fn fetch_document(
        &self,
        url: &WebUrl,
        maximum_bytes: u64,
        cancel: &CancelToken,
    ) -> Result<PluginReleaseFetchResult, UpdateError> {
        net::require_secure_or_loopback(url, "plugin release")?;
        let segments = url.segments();
        let (owner, repo) = if segments.len() >= 2 {
            (segments[0].to_owned(), segments[1].to_owned())
        } else {
            (String::new(), String::new())
        };
        let mut tag: Option<String> = None;
        let mut redirected_elsewhere = false;
        let response = net::send_with_validated_redirects(
            self.transport.as_ref(),
            url,
            "plugin release",
            None,
            |next| match match_tag(next, &owner, &repo) {
                Ok(Some(matched)) => {
                    tag.get_or_insert(matched);
                }
                Ok(None) => {}
                Err(()) => redirected_elsewhere = true,
            },
            cancel,
        );
        let mut response = match response {
            Ok(response) => response,
            Err(UpdateError::Transport(_)) => return Ok(PluginReleaseFetchResult::Unavailable),
            Err(error) => return Err(error),
        };
        if redirected_elsewhere {
            return Ok(PluginReleaseFetchResult::Unavailable);
        }
        if response.status == 429 {
            return Ok(PluginReleaseFetchResult::RateLimited);
        }
        if response.status != 200 {
            return Ok(PluginReleaseFetchResult::Unavailable);
        }
        let too_large =
            format!("The plugin release document is larger than {maximum_bytes} bytes.");
        match net::read_bounded(&mut response, maximum_bytes, &too_large, cancel) {
            Ok(content) => Ok(PluginReleaseFetchResult::Success(PluginReleaseDocument {
                content,
                tag,
            })),
            Err(UpdateError::Transport(_)) => Ok(PluginReleaseFetchResult::Unavailable),
            Err(error) => Err(error),
        }
    }
}

// --- The releases feed ----------------------------------------------------------

const ATOM: &str = "http://www.w3.org/2005/Atom";

/// A WebAssembly release found on a feed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginFeedRelease {
    /// Its tag (`wasm-v` or `wasm-<plugin id>-v`, then the version).
    pub tag: String,
    /// Its version.
    pub version: LauncherVersion,
}

/// The feed could not be read.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("{0}")]
pub struct PluginReleaseFeedError(pub String);

/// The release tag a feed link names, when it is this repository's.
fn match_release_link(href: &str, repo: &str) -> Option<String> {
    let url = WebUrl::parse(href)?;
    if !url.is_https()
        || !url.host().eq_ignore_ascii_case("github.com")
        || url.port() != Some(443)
        || !url.query().is_empty()
        || !url.fragment().is_empty()
    {
        return None;
    }
    let segments = url.segments();
    let (owner, name) = repo.split_once('/')?;
    if segments.len() != 5
        || !segments[0].eq_ignore_ascii_case(owner)
        || !segments[1].eq_ignore_ascii_case(name)
        || segments[2] != "releases"
        || segments[3] != "tag"
    {
        return None;
    }
    Some(segments[4].to_owned())
}

/// The release `tag` names when it is `prefix` followed by a strict SemVer
/// version with no build metadata.
fn parse_release(tag: &str, prefix: &str) -> Option<PluginFeedRelease> {
    let version_text = tag.strip_prefix(prefix)?;
    if version_text.contains('+') {
        return None;
    }
    let version = LauncherVersion::try_parse(version_text)?;
    Some(PluginFeedRelease {
        tag: tag.to_owned(),
        version,
    })
}

/// Every WebAssembly release on a repository's releases feed. Only a `link`
/// that is a direct child of an `entry` counts, naming
/// `https://github.com/<owner>/<repo>/releases/tag/wasm-v<version>` for this
/// repository with no query, fragment or build metadata; any other tag (an
/// OpenAC C# release's `v<version>` and a plugin-named
/// `wasm-<plugin id>-v<version>` among them) is not counted here.
/// Malformed XML or a DTD is an error.
pub fn wasm_releases(
    content: &[u8],
    repo: &str,
) -> Result<Vec<PluginFeedRelease>, PluginReleaseFeedError> {
    Ok(feed_tags(content, repo)?
        .iter()
        .filter_map(|tag| parse_release(tag, WASM_TAG_PREFIX))
        .collect())
}

/// The releases among `tags` that may be plugin `id`'s: its own
/// `wasm-<id>-v<version>` tags, matched by exact prefix, and the repository's
/// `wasm-v<version>` tags (a plugin's `plugin.json` decides whose they are).
pub fn plugin_releases(tags: &[String], id: &str) -> Vec<PluginFeedRelease> {
    let prefix = plugin_tag_prefix(id);
    tags.iter()
        .filter_map(|tag| {
            parse_release(tag, &prefix).or_else(|| parse_release(tag, WASM_TAG_PREFIX))
        })
        .collect()
}

/// Every release tag a repository's releases feed links to, by the link
/// rules of [`wasm_releases`], before any tag form is read.
pub fn feed_tags(content: &[u8], repo: &str) -> Result<Vec<String>, PluginReleaseFeedError> {
    let text = std::str::from_utf8(content)
        .map_err(|_| PluginReleaseFeedError("the releases feed is not UTF-8".into()))?;
    // roxmltree refuses a DTD unless asked to allow one.
    let document = roxmltree::Document::parse(text)
        .map_err(|_| PluginReleaseFeedError("the releases feed is not well-formed XML".into()))?;
    let feed = document.root_element();
    if feed.tag_name().name() != "feed" || feed.tag_name().namespace() != Some(ATOM) {
        return Err(PluginReleaseFeedError(
            "the releases feed has no <feed> root".into(),
        ));
    }
    let mut tags = Vec::new();
    for entry in feed
        .children()
        .filter(|node| node.is_element() && node.has_tag_name((ATOM, "entry")))
    {
        for link in entry
            .children()
            .filter(|node| node.is_element() && node.has_tag_name((ATOM, "link")))
        {
            if let Some(tag) = link
                .attribute("href")
                .and_then(|href| match_release_link(href, repo))
            {
                tags.push(tag);
            }
        }
    }
    Ok(tags)
}

fn highest(releases: &[PluginFeedRelease], pre_release: bool) -> Option<&PluginFeedRelease> {
    releases
        .iter()
        .filter(|release| release.version.is_pre_release() == pre_release)
        .fold(
            None,
            |best: Option<&PluginFeedRelease>, candidate| match best {
                Some(current) if current.version >= candidate.version => Some(current),
                _ => Some(candidate),
            },
        )
}

/// The highest-precedence WebAssembly pre-release on a releases feed.
pub fn highest_prerelease(
    content: &[u8],
    repo: &str,
) -> Result<Option<PluginFeedRelease>, PluginReleaseFeedError> {
    Ok(highest(&wasm_releases(content, repo)?, true).cloned())
}

/// The highest-precedence WebAssembly stable release on a releases feed.
pub fn highest_stable(
    content: &[u8],
    repo: &str,
) -> Result<Option<PluginFeedRelease>, PluginReleaseFeedError> {
    Ok(highest(&wasm_releases(content, repo)?, false).cloned())
}

// --- Resolving a repository ----------------------------------------------------

/// A resolved release.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginReleaseResolution {
    /// Its tag.
    pub tag: String,
    /// Its `plugin.json` bytes.
    pub manifest_bytes: Vec<u8>,
    /// Its parsed manifest.
    pub manifest: PluginManifest,
}

/// How resolving went.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PluginReleaseResolveResult {
    /// A release to offer.
    Success(Box<PluginReleaseResolution>),
    /// GitHub is rate limiting.
    RateLimited,
    /// Unreachable, not found, or no release for this client.
    Unavailable,
    /// The release is unusable; the text says why.
    Invalid(String),
    /// The repository's newest release for this client is a pre-release
    /// and it has no stable one (not offered on stable).
    Prerelease(String),
}

impl PluginReleaseResolveResult {
    /// The resolution, when successful.
    pub fn resolution(&self) -> Option<&PluginReleaseResolution> {
        match self {
            Self::Success(resolution) => Some(resolution.as_ref()),
            _ => None,
        }
    }
}

/// Both candidates of one pass: the stable release (always) and the highest
/// pre-release (only on the beta channel).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginReleaseCandidates {
    /// The stable release.
    pub stable: PluginReleaseResolveResult,
    /// The beta candidate, when asked for and found.
    pub beta: Option<PluginReleaseResolveResult>,
}

impl PluginReleaseCandidates {
    /// The release a channel offers: stable for stable; for beta, the
    /// higher by precedence of the two, and a failed stable never hides a
    /// beta that resolved.
    pub fn for_channel(&self, channel: PluginReleaseChannel) -> &PluginReleaseResolveResult {
        if channel != PluginReleaseChannel::Beta {
            return &self.stable;
        }
        let Some(beta) = &self.beta else {
            return &self.stable;
        };
        let (Some(stable), Some(beta_resolution)) = (self.stable.resolution(), beta.resolution())
        else {
            return if self.stable.resolution().is_none() {
                beta
            } else {
                &self.stable
            };
        };
        let stable_version = LauncherVersion::try_parse(&stable.manifest.version);
        let beta_version = LauncherVersion::try_parse(&beta_resolution.manifest.version);
        match (stable_version, beta_version) {
            (Some(stable_version), Some(beta_version)) if beta_version > stable_version => beta,
            _ => &self.stable,
        }
    }
}

/// Resolves repositories to releases. Both channels come from one fetch of
/// the repository's releases feed: the highest stable release, and on beta
/// the highest pre-release. Without a plugin id only `wasm-v<version>` tags
/// count; with one, its `wasm-<id>-v<version>` tags count too, and a
/// candidate whose `plugin.json` names another plugin is skipped for the
/// next highest. A repository's `releases/latest` belongs to OpenAC's C#
/// launcher and is never read.
#[derive(Debug, Clone)]
pub struct PluginReleaseResolver {
    client: PluginReleaseClient,
}

impl PluginReleaseResolver {
    /// A resolver over `client`.
    pub fn new(client: PluginReleaseClient) -> Self {
        Self { client }
    }

    /// Resolves the stable release and, on beta, the highest pre-release
    /// too, from the repository's `wasm-v` tags.
    pub fn resolve_candidates(
        &self,
        repo: &str,
        channel: PluginReleaseChannel,
        cancel: &CancelToken,
    ) -> Result<PluginReleaseCandidates, UpdateError> {
        self.candidates(repo, None, channel, cancel)
    }

    /// Resolves plugin `id`'s stable release and, on beta, its highest
    /// pre-release too.
    pub fn resolve_plugin_candidates(
        &self,
        repo: &str,
        id: &str,
        channel: PluginReleaseChannel,
        cancel: &CancelToken,
    ) -> Result<PluginReleaseCandidates, UpdateError> {
        self.candidates(repo, Some(id), channel, cancel)
    }

    /// The release the channel offers for plugin `id`.
    pub fn resolve_plugin(
        &self,
        repo: &str,
        id: &str,
        channel: PluginReleaseChannel,
        cancel: &CancelToken,
    ) -> Result<PluginReleaseResolveResult, UpdateError> {
        Ok(self
            .resolve_plugin_candidates(repo, id, channel, cancel)?
            .for_channel(channel)
            .clone())
    }

    fn candidates(
        &self,
        repo: &str,
        id: Option<&str>,
        channel: PluginReleaseChannel,
        cancel: &CancelToken,
    ) -> Result<PluginReleaseCandidates, UpdateError> {
        let feed_url = github::releases_feed(repo).ok_or_else(|| {
            UpdateError::failed(format!("'{repo}' is not an 'owner/name' repository."))
        })?;
        let only = |stable| Ok(PluginReleaseCandidates { stable, beta: None });
        let feed = match self
            .client
            .fetch_document(&feed_url, FEED_MAXIMUM_BYTES, cancel)
        {
            Ok(PluginReleaseFetchResult::Success(document)) => document,
            Ok(PluginReleaseFetchResult::RateLimited) => {
                return only(PluginReleaseResolveResult::RateLimited);
            }
            Ok(PluginReleaseFetchResult::Unavailable) => {
                return only(PluginReleaseResolveResult::Unavailable);
            }
            Err(UpdateError::Cancelled) => return Err(UpdateError::Cancelled),
            // An oversized feed or a redirect off HTTPS: no release can be read from it.
            Err(_) => return only(PluginReleaseResolveResult::Unavailable),
        };
        let tags = match feed_tags(&feed.content, repo) {
            Ok(tags) => tags,
            Err(error) => return only(PluginReleaseResolveResult::Invalid(error.to_string())),
        };
        let releases = match id {
            Some(id) => plugin_releases(&tags, id),
            None => tags
                .iter()
                .filter_map(|tag| parse_release(tag, WASM_TAG_PREFIX))
                .collect(),
        };
        let stable = match self.first_release(repo, id, &releases, false, cancel)? {
            Some(result) => result,
            None => match highest(&releases, true) {
                Some(pre_release) => PluginReleaseResolveResult::Prerelease(format!(
                    "the latest release of '{repo}' is a pre-release ({})",
                    pre_release.version.value()
                )),
                None => PluginReleaseResolveResult::Unavailable,
            },
        };
        let beta = if channel == PluginReleaseChannel::Beta {
            // A beta that cannot be read is simply not offered.
            match self.first_release(repo, id, &releases, true, cancel) {
                Ok(Some(result @ PluginReleaseResolveResult::Success(_))) => Some(result),
                Ok(_) => None,
                Err(UpdateError::Cancelled) => return Err(UpdateError::Cancelled),
                Err(_) => None,
            }
        } else {
            None
        };
        Ok(PluginReleaseCandidates { stable, beta })
    }

    /// The highest stable release (or pre-release) of `releases` whose
    /// `plugin.json` is plugin `id`'s, skipping at most
    /// [`MAXIMUM_REFUSED_CANDIDATES`] that name another plugin; `None` when
    /// none is found. On a tie a `wasm-<id>-v` tag is tried before `wasm-v`.
    fn first_release(
        &self,
        repo: &str,
        id: Option<&str>,
        releases: &[PluginFeedRelease],
        pre_release: bool,
        cancel: &CancelToken,
    ) -> Result<Option<PluginReleaseResolveResult>, UpdateError> {
        let mut candidates: Vec<&PluginFeedRelease> = releases
            .iter()
            .filter(|release| release.version.is_pre_release() == pre_release)
            .collect();
        candidates.sort_by(|a, b| {
            b.version.cmp(&a.version).then_with(|| {
                a.tag
                    .starts_with(WASM_TAG_PREFIX)
                    .cmp(&b.tag.starts_with(WASM_TAG_PREFIX))
            })
        });
        candidates.dedup_by(|a, b| a.tag == b.tag);
        let mut refused = 0;
        for candidate in candidates {
            match self.resolve_tag(repo, &candidate.tag, id, cancel)? {
                TagOutcome::Resolved(result) => return Ok(Some(result)),
                TagOutcome::AnotherPlugin => {
                    refused += 1;
                    if refused == MAXIMUM_REFUSED_CANDIDATES {
                        break;
                    }
                }
            }
        }
        Ok(None)
    }

    /// The release the channel offers.
    pub fn resolve(
        &self,
        repo: &str,
        channel: PluginReleaseChannel,
        cancel: &CancelToken,
    ) -> Result<PluginReleaseResolveResult, UpdateError> {
        Ok(self
            .resolve_candidates(repo, channel, cancel)?
            .for_channel(channel)
            .clone())
    }

    /// The release tagged `tag`: its `plugin.json`, read by the client's
    /// parser, naming plugin `id` (when given) and the tag's version.
    fn resolve_tag(
        &self,
        repo: &str,
        tag: &str,
        id: Option<&str>,
        cancel: &CancelToken,
    ) -> Result<TagOutcome, UpdateError> {
        let resolved = |result| Ok(TagOutcome::Resolved(result));
        let Some(url) = github::tagged_asset(repo, tag, "plugin.json") else {
            return resolved(PluginReleaseResolveResult::Unavailable);
        };
        let document = match self
            .client
            .fetch_document(&url, MAXIMUM_DOCUMENT_BYTES, cancel)?
        {
            PluginReleaseFetchResult::Success(document) => document,
            PluginReleaseFetchResult::RateLimited => {
                return resolved(PluginReleaseResolveResult::RateLimited);
            }
            PluginReleaseFetchResult::Unavailable => {
                return resolved(PluginReleaseResolveResult::Unavailable);
            }
        };
        let content = document
            .content
            .strip_prefix(b"\xEF\xBB\xBF")
            .unwrap_or(&document.content);
        let manifest = match PluginManifest::parse(&String::from_utf8_lossy(content)) {
            Ok(manifest) => manifest,
            Err(error) => return resolved(PluginReleaseResolveResult::Invalid(error.to_string())),
        };
        if id.is_some_and(|id| !manifest.id.eq_ignore_ascii_case(id)) {
            return Ok(TagOutcome::AnotherPlugin);
        }
        if !manifest.matches_tag(tag) {
            return resolved(PluginReleaseResolveResult::Invalid(format!(
                "the release tag does not match plugin version {}",
                manifest.version
            )));
        }
        resolved(PluginReleaseResolveResult::Success(Box::new(
            PluginReleaseResolution {
                tag: tag.to_owned(),
                manifest_bytes: document.content,
                manifest,
            },
        )))
    }
}

/// What one tag's `plugin.json` gave.
enum TagOutcome {
    /// A result for the plugin asked for.
    Resolved(PluginReleaseResolveResult),
    /// The release is another plugin's.
    AnotherPlugin,
}

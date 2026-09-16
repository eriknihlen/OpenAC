# Plugin manifest

Every plugin ships a `plugin.json` at the root of its folder. The host reads it before it loads
any code.

| Field | Required | Meaning |
|---|---|---|
| `id` | required | Identity, non-empty. |
| `displayName` | required | Shown to the player. |
| `version` | required | The plugin's own version, free-form. |
| `entryDll` | required | The assembly the host loads. |
| `apiVersion` | required | Must be within the range `AcDream.Plugin.Abstractions` supports. |
| `kinds` | optional; absent = `gameplay` | `gameplay`, `renderPack`, or both. |
| `minHostVersion` | optional | Lowest client version, `MAJOR.MINOR.PATCH`, inclusive. |
| `maxHostVersion` | optional | Highest client version, inclusive. |
| `skipHostVersions` | optional | Exact client versions with a known breakage. |
| `hosts` | optional; absent = both | Non-empty array of `graphical`, `headless`. |

A property name must be unique, case-insensitively, within every object in the document, including objects nested inside arrays; a repeat fails to parse.

A host version is compared on its version core: `0.1.7+3a71d75` and `0.2.0-beta.1` compare as
`0.1.7` and `0.2.0`. `<Version>` in `Directory.Build.props` is bumped only at release, so a build
from `main` reports the last release number: a plugin needing unreleased abstractions sets
`minHostVersion` to the next release.

## When a plugin is incompatible

A plugin that fails the version or host check follows the same rule an unsupported `kinds` value
already does: the player asked for it by id, or they didn't.

- Listed in the session's plugin allow-list: the session status reports `pluginFailed` with the
  reason, and no code loads.
- Not listed, or no allow-list is configured: the plugin is skipped silently.

## Duplicate ids

If more than one folder under the configured plugin roots declares the same `id`, once each has
passed its own `kinds` and compatibility check, the session reports `pluginFailed` for that id and
loads no copy.

## Publishing for the launcher

The launcher installs plugins from GitHub releases (`shaneedwards/openac-plugins` lists them for
now). A plugin author who wants a plugin installable through the launcher follows a stricter
contract than the fields above:

| Field | Launcher install |
|---|---|
| `id` | must match `^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$`, for example `edwards.buffbot` |
| `version` | SemVer 2.0, equal to the release tag minus a leading `v` |
| `minHostVersion` | required |
| `hosts` | required, non-empty |

**Release, one per version:**

- Public repository. Releases are not drafts or prereleases. The release GitHub marks *latest*
  must be the highest version; the launcher refuses to install an older one.
- **Tag:** `v<version>`, for example `v1.2.0`.
- **Three assets, exact names, and an optional fourth:**
  - `plugin.json`, byte-identical to the one at the root of the zip
  - `<id>-<version>.zip`, with `plugin.json` at the zip root (not inside a folder) plus the entry
    DLL and its dependencies
  - `<id>-<version>.zip.sha256`, the output of `shasum -a 256 <zip>` (hash, optional whitespace
    and file name)
  - `icon.png`, byte-identical to the zip's copy, only if the zip has one

**Icon:** a plugin may ship one `icon.png` at the root of its zip, PNG only, exactly 64x64, at
most 64 KiB, not animated. No icon is fine, but an icon that breaks a rule refuses the whole
install. `icon.jpg` and `icon.jpeg` are never accepted at the zip root, in any case; an icon in a
subfolder is untouched by this rule.

**Managed code only, by allowlist.** Every file in the zip must end in one of: `.dll`, `.pdb`,
`.json`, `.xml`, `.txt`, `.md`, `.png`, `.jpg`, `.jpeg`, `.ttf`, `.otf`. A `runtimes/` folder is
rejected.

**Caps:** zip at most 64 MiB. Extraction: 2,000 entries, 64 MiB per entry, 256 MiB total,
compression ratio 200.

**Recommended:** enable immutable releases on the repository.

The launcher never loads, reflects over, or runs a downloaded file. It unzips, verifies the
`.sha256`, and stages the plugin disabled; enabling it is a separate, explicit choice the player
makes on the Plugins tab.

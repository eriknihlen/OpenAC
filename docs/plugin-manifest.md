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

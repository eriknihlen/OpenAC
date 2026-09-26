# OpenAC launcher

The launcher groups accounts by username. Expand an account to see its servers.
Resize the window to fit your desktop; the account list scrolls independently
of the launch controls.

Choose **Character select** or a known character, then **Graphical** or
**Headless**. Headless sessions need a named character. Check server rows and
choose **Play selected** to start all eligible selections. Each row starts a
separate supervised session. A failed launch is reported without preventing
the other selected rows from starting. Already active accounts cannot start
again on the same server. **Cancel** stops pending starts; use the row's
**Stop** action to close an active session gracefully.

## Accounts and servers

The bottom editors run inside the launcher on Windows and Linux. Saves are
validated and atomic; invalid entries do not partially change your profiles.
Close active sessions before saving profile edits.

**Edit Servers** has two fields per row: **Server name** and **Address:port**.
For example, enter `Local` and `127.0.0.1:9000`, or `Example` and
`game.example.org:9000`. Include the port; for IPv6, use `[::1]:9000`.
Use **Add server** or **Remove** to change the list, then **Save**.

**Edit accounts** has separate **Username** and **Password** fields. Passwords
are masked and may be empty. Use **Add account** or **Remove** to change the
list. Enter values directly; no separators or quoting are needed.

Every user lists every configured server, including servers added later. Users
can be added before any servers and survive removing all servers. Passwords are
stored locally. Older conflicting passwords remain in Edit accounts until you
choose one password per username.

Removing a server removes its saved characters. Keep server names unchanged
to retain their character settings. If profiles change while an editor is open,
reopen the editor before saving.

A row's **…** action always opens the same dialog: which installed plugins load
for that row (see **Plugins** below). With a named character chosen it edits that
character, including its one-command-per-line logon commands. With **Character
select** chosen it edits every character on the account at once. **Logon commands** in the bottom bar provides the complete
structured command list for bulk editing.

## Plugins

The **Plugins** tab lists what is installed and what is available to install
from the curated list. **Discover** shows plugins not yet installed, except
any the curated list blocks, and only once each one's release has been
checked and found usable; a row appears as its check finishes, and if GitHub
is rate limiting or unreachable, a line says so instead of a shorter list.
**Install** downloads and unzips one, but never runs it. **Installed** shows
what is on disk, with a source badge (**Listed**
or **Unlisted** for a launcher-managed plugin, **Direct install** or
**Bundled** otherwise), **Update** for plugins the launcher itself installed,
and **Remove** for those plus a Direct install. Removing a plugin also
unticks it for every character that had it enabled, so reinstalling it always
starts from none. **Refresh list** reloads
both lists; the launcher also checks once at startup, without delaying the
window. **Add from URL** adds a plugin from a `https://github.com/owner/name`
repository not on the list. Right after a curated-list release publishes,
GitHub's "latest" link can keep serving the previous release for under a
minute; wait a moment and press **Refresh list** again.

A launcher-managed plugin (Listed or Unlisted) has a **Beta updates** toggle
on its Installed card. On, its update check also offers the newest
pre-release on the repository's release feed, if it is newer than the latest
stable release; installing one shows a **beta** tag. A beta player still gets
a stable release once one passes the beta. Turning the toggle off never
downgrades an installed beta; it just stops offering pre-releases until a
stable release passes it. The release feed can lag a new release by up to a
minute.

Installing or updating a plugin does not need the game closed. The launcher
writes the new files into the plugin's folder, leaving the plugin's own
`files` folder (its saved settings) where it is, and every running client and
headless session that has the plugin loaded switches to the new version about
a second later. If the new version needs a newer client, the running one keeps
the old version and says so in chat; the update takes effect after updating
the client. Removing a plugin, and client or launcher updates, still wait until
every session is closed.

The **Show beta plugins** checkbox, in Launcher settings behind the gear icon
on the tab row, is off by default and covers Discover and Add from URL
instead: on, a plugin with no stable release yet can be found and installed,
landing on the beta channel with the same pre-release notice; off, only its
stable releases are offered.

Every row shows its compatibility with the installed client: compatible and
which version, graphical-only or headless-only when the plugin restricts
itself to one host, the incompatibility reason, or that no client is
installed yet.

Every install and update dialog shows a short notice: plugins are made by
third parties, not OpenAC, and installing one is the player's choice and
responsibility; an unlisted plugin adds that it is not on the curated list.
The plugin is downloaded and unzipped, never run automatically, and stays
disabled until the player chooses to enable it.

Installing never enables a plugin. Choose **None** to install without
enabling anything, **All characters**, or **Choose** to pick specific
characters; an update carries no such choice, since it can only affect a
plugin already enabled where it was chosen before. A character's own **…**
action opens a checklist of installed plugins compatible with its launch
mode; only checked plugins load, and a blank list loads nothing at all. A row set to **Character select** picks its character inside
the client, after the plugin list is already fixed, so it loads only the plugins
every character on that account has enabled; its **…** action ticks a plugin for
all of them in one step. Existing profiles are not migrated: anyone who relied on a
plugin loading by default must tick it once.

A blocked plugin (listed as unsafe by the curated list) shows a red
"Blocked: <reason>" badge, cannot be installed or updated to, and is filtered
out of every character's list at launch, with a status line saying so. A
blocked plugin not yet installed does not appear in Discover at all.

A plugin folder unzipped by hand into the plugins directory, with no matching
install record, is a **Direct install**. It is checked against every install
rule that does not need a GitHub release: no links or reparse points, regular
files only, safe paths, the size and count limits, the allowed file types, the
manifest, and the icon rules; Finder and Explorer metadata files
(`.DS_Store`, `._*`, `Thumbs.db`, `desktop.ini`) are ignored rather than
refused. A folder that fails shows a red "Refused: <reason>" badge, is never
offered to a character, and is left out of every session's plugin list even
if a character had it enabled before it broke. A second copy of an already
installed id, in any plugin folder, is flagged "Duplicate" on every copy and
loaded by neither, because the client itself refuses to load a duplicated id.
Passing or refused, a Direct install can be removed like any other. This
checking is advisory, not a security boundary: anyone who can write the
plugins folder can change a plugin's files after it passes, and a
launcher-managed plugin is never re-checked once installed.

`--plugin-list-uri <https-uri>` overrides the curated list for testing, the
same way `--update-manifest-uri` overrides the update feed.

## Installation and updates

First setup opens when required. **Installation & updates** always provides
setup, file verification and a manual update check. Available updates appear
above the accounts; **Review update** opens the existing verified updater.
Close active sessions before installing. Long content preparation retains its
progress and cancellation controls. Content and client compatibility checks
continue to gate launching.

A release's client is installed only by a launcher at least as new as the
release; the launcher always updates itself first and then offers the client.

### Coming from 0.1.16 or earlier

OpenAC now keeps everything in one install folder (Windows
`%LOCALAPPDATA%\OpenAC`, macOS `~/Library/Application Support/OpenAC`, Linux
`~/.local/share/openac`), shown under Settings with Open and Move… buttons.
Earlier versions spread their files over per-user `acdream` folders
(`%APPDATA%\acdream` and `%LOCALAPPDATA%\acdream` on Windows).

Updating from such a version moves nothing. The old launcher updates itself as
usual, and the new one then starts as a new installation: first setup runs
again (point it at your Asheron's Call folder; it prepares the game content and
downloads the client), and launcher settings, account profiles and plugins
start fresh, so add your accounts and install your plugins again. The first
setup form says so and names the old folders. They are left exactly as they
were and are no longer used; delete them once you no longer need anything in
them.

If you started the old launcher with `--data-dir` (or `ACDREAM_DATA_DIR`), the
update itself still finishes in that folder, but the new launcher will not use
that folder: `--data-dir` now names the whole install folder, and one that
holds an earlier version's files (`launcher-update`, `pak`, `install.json`,
`install.verification.json` or `crash-reports` at its top) is refused with a
message saying so. Start the launcher with `--root-dir <new empty folder>`
instead, or without the option to use the default folder.

## Testing a pre-release

Pre-releases are development builds. The launcher never finds one by itself:
it reads the update feed through GitHub's "latest release" link, and that link
skips pre-releases, so an ordinary install is only ever offered releases meant
for everyone. Two ways to run one are supported.

**Point a launcher at it.** `--update-manifest-uri <https-uri>` replaces the
update feed for that run, the same way `--plugin-list-uri` replaces the curated
plugin list. Give it the `manifest.json` attached to the pre-release:

```
acdream-launcher --update-manifest-uri https://github.com/eriknihlen/OpenAC/releases/download/v0.1.13-dev.1/manifest.json
```

That launcher then offers the pre-release's client, and the pre-release's
launcher as well when it is newer than the one running. The option is not
saved: start the launcher without it and it is back on the ordinary feed. Add
`--root-dir <new empty folder>` to keep the test install away from your real
one. (`--config-dir`, `--data-dir` and `--cache-dir`, all three together, still
work, but `--data-dir` names the install folder itself, so give it a new folder
too; a folder an earlier version used is refused.)

**Build one from source.** `tools/run-launcher-trial.ps1` publishes the client
and launcher from a checkout, installs the client into a scratch directory and
starts the launcher against it, touching nothing you already have installed.

A pre-release's `launcher-*.zip` can also be downloaded and unzipped by hand.

Afterwards, a hand-installed pre-release launcher keeps polling the ordinary
feed and offers an update only when what it finds there is strictly newer than
what is installed. A pre-release sorts above the release before it and below
the release of the same number, so a launcher on `0.1.13-dev.1` is not offered
`0.1.12` and will not go back to it. It stays where it is until `0.1.13` is
released, then updates to that like any other. The client it installed follows
the same rule.

## Server status

The launcher checks each configured endpoint every 30 seconds and on demand.
A green dot means the game endpoint answered; a red dot means no response
within the timeout (offline or unreachable). Gray means not checked yet.
Status does not prevent launching. Player counts come from TreeStats, matched
by server name or an unambiguous hostname label such as coldeve in play.coldeve.ac. Missing
counts are shown as unavailable and failed refreshes mark cached counts stale.
These external population counts are separate from endpoint reachability.

Failed launches stay visible on their account/server row. Play refreshes when
the reconnect delay expires, even if no further session event arrives.

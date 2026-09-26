# OpenAC launcher

The launcher lists each account on each server as its own card: the account
name, its profile tags, the server and how many characters it has, and the
plugins its characters start with. Resize the window to fit your desktop; the
account list scrolls independently of the launch controls.

Choose **Character select** or a known character, then **Graphical** or
**Headless**. Headless sessions need a named character. Tick accounts and
choose **Play selected** to start all eligible selections. Each row starts a
separate supervised session. A failed launch is reported without preventing
the other selected rows from starting. Already active accounts cannot start
again on the same server. **Cancel** stops pending starts; use the row's
**Stop** action to close an active session gracefully.

**Profiles** tag accounts, such as `Main`, `Bots` or `Mules`. The chips above
the list (**All** and one per tag) show only the accounts with that tag, and
**Play selected** then starts only the ticked accounts the filter shows.

A row's **Options ▾** menu has **Logon commands…**, **Plugins for this
character…** (with a character chosen), **Console** (for a running headless
session), **Open logs folder** and **Remove character** (a later character
refresh brings a removed character back; it asks first).

## Accounts and servers

The editors run inside the launcher. Saves are validated and atomic; invalid
entries do not partially change your profiles. If profiles change while an
editor is open, reopen the editor before saving.

**Accounts** is one plain text, grouped by server:

```
#Coldeve
Name=notan3,Password=secret,Profiles=Main;Bots
Name=notan4,Password=other

#sawato
Name=testaccount,Password=testpassword
```

A `#` line names a server (matched by its name, ignoring case); every account
line below it belongs to that server. `Profiles=` is optional; separate tags
with `;`. Put double quotes around a value that contains a comma or a quote,
and write a quote as `\"`. A server's section is the whole truth for that
server: an account left out of it is removed with its saved characters. A
server left out of the text is left as it is. Changing an account's name is
removing it and adding a new one: a different name is a different login, with
its own characters, so its characters, plugins and logon commands are not
carried over. When a save would remove an account that has any of those, the
editor lists them first and saves only when you press **Remove and save**. Passwords are stored and shown
in plain text on this computer; they stay hidden, and the text read-only,
until **Show passwords** is ticked. **Copy all** copies the text, passwords
included; **Paste all** replaces it (nothing is saved until **Save**). A
wrong line is reported by its number and nothing is saved. Accounts can be
edited while sessions run, except that a running account cannot be removed.

**Add server** adds your own server (name, host and port) or one from the list
of known public servers that [TreeStats](https://treestats.net/) publishes,
with its type, player count, description and links. Search by name, address or
description, or narrow the list by type; a server you already have (same name,
or same host and port) shows **Added**. The list is saved each time it loads,
so the dialog also opens offline with the last copy; with no copy at all, only
your own server can be added. A new server has no accounts until you add them
under its `#` line in **Accounts**.

**Edit servers** has two fields per row: **Server name** and **Address:port**.
For example, enter `Local` and `127.0.0.1:9000`, or `Example` and
`game.example.org:9000`. Include the port; for IPv6, use `[::1]:9000`.
Removing a server removes its saved characters. Keep server names unchanged
to retain their character settings. Close that server's sessions first.

**Logon commands** are one plain text too, holding every server and account:

```
#Coldeve
##notan3
/vt start
##notan
#sawato
##testaccount
/vt nav load bore_circuit1
```

`#` starts a server, `##` an account on it, and every other non-empty line is
a command, run in order after any character on that account logs in. An
account with no lines runs no commands. A command that itself starts with `#`
is written with a backslash in front, `\#…`; a line starting with a backslash
is always a command, taken without that backslash (so a command starting with
a backslash is written `\\…`). The same rules as Accounts apply: a
listed server's section is the whole truth, a server left out is left alone,
and a wrong line (an unknown server or account, a command before any `##`) is
reported by number and nothing is saved. Commands can be edited while sessions
run; they take effect at the next login.

**Plugins for this account** (**Edit plugins** on the account's card) is the
list every character on the account starts with, the character screen
included. A character can still differ: **Options ▾ › Plugins for this
character…** either follows the account (**Use the account's plugins**) or
keeps a list of its own, filtered to the plugins that support its launch mode.

### Profiles written by an earlier launcher

An earlier launcher kept plugins and logon commands on each character and one
user list shared by every server, in `launcher-profiles.json` in the settings
folder. This launcher keeps its profiles in `launcher-profiles.v2.json`. The
first time it starts without that file it reads `launcher-profiles.json` once
and converts it, keeping a byte-for-byte copy as
`launcher-profiles.v1-backup.json`. It never writes `launcher-profiles.json`,
so an older launcher started afterwards still finds its own profiles and works
as before; changes made there are not seen by this launcher, and changes made
here are not seen by the older one. The conversion:

- each account's plugins are every plugin its characters had, in the order
  first seen; a character whose own list differed, in its plugins or in their
  order, keeps it as its own list;
- each account's logon commands are its characters' commands when they were
  all the same, otherwise the first character's, and a one-time notice lists
  what the other characters had;
- every server keeps exactly the accounts it showed before.

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
unticks it for every account and character that had it enabled, so reinstalling
it always starts from none. **Refresh list** reloads
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

Limits worth knowing:

- A client or headless session started from a release before this one does
  not reload plugins; it keeps the old version until it is restarted.
- If the launcher stops in the middle of an update (a crash or a power cut),
  the next launcher start with no session running puts the previous version
  back, or finishes the update if its `plugin.json` was already in place.
- A file in the plugin's folder that something holds open (outside `files`),
  such as a log file a running plugin writes beside its code, can stop the
  update; the launcher then puts the previous version back and says why.
  Close the session and update again.

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

Installing never enables a plugin by itself. Choose **None** to install without
enabling anything, **All accounts**, or **Choose accounts** to add it to
specific accounts' plugin lists; an update carries no such choice, since it can
only affect a plugin already enabled where it was chosen before. Only the
plugins on an account's list (or a character's own list) load, and a blank
list loads nothing at all.

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

The top right shows the installed **Client** and **Launcher** versions and
whether your **Plugins** have updates. **Check for updates** beside them checks
the client, the launcher and the installed plugins at once; the launcher also
checks at startup and every 20 minutes while it is open. Only the startup check
verifies the installed client; the later ones only read the release feed, so
they never hold up **Play**. The plugin part looks only at installed plugins'
updates and leaves the Plugins tab's messages and Discover list as they are
(**Refresh list** there re-checks everything). A background check only shows
the update banner; the button also opens what it found.

The launcher updates itself only when it changed. Each release publishes a
fingerprint of what the launcher is built from (`launcher-fingerprint.json`,
beside `manifest.json`); a launcher whose own fingerprint matches is that
release's launcher and stays as it is, even though its version number is
older, and installs the new client directly. When the fingerprints differ the
launcher updates itself first and then offers the client, as before. A
launcher built without a fingerprint (a developer build), or a release without
one, compares versions instead, and a launcher from before fingerprints keeps
updating by version, so it always reaches the current launcher. The fingerprint
covers the launcher's own code, the preparation tool's own project, the version
of the prepared-data recipe, the build files and the .NET runtime it carries; a
release that only changes the shared game code leaves the launcher as it is,
because its preparation tool still prepares valid data until the recipe version
goes up.

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

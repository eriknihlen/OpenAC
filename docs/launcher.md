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

## Users and servers

The bottom editors run inside the launcher on Windows and Linux. Saves are
validated and atomic; malformed text does not partially change your profiles.
Close active sessions before saving profile edits.

**Edit Servers** accepts one server per line:

```text
Local | 127.0.0.1 | 9000
Example | game.example.org | 9000
```

**Edit Users** accepts one account per line:

```text
myaccount | mypassword | Local, Example
anotheraccount | anotherpassword
```

Omitting the server list associates the account with every configured server.
Passwords are stored locally. A username with different passwords on different
servers is exported as separate lines, preserving those credentials.
Quote values containing separators or surrounding whitespace using JSON string
escaping, for example `"password|with|separators"`.

Removing a server association removes its saved characters. Keep names unchanged
to retain their character settings. If profiles change while an editor is open,
reopen the editor before saving.

Use a named character row's **…** action for plugins and one-command-per-line
logon commands. **Logon commands** in the bottom bar provides the complete
structured command list for bulk editing.

## Installation and updates

First setup opens when required. **Installation & updates** always provides
setup, file verification and a manual update check. Available updates appear
above the accounts; **Review update** opens the existing verified updater.
Close active sessions before installing. Long content preparation retains its
progress and cancellation controls. Content and client compatibility checks
continue to gate launching.

## Server status

The launcher checks each configured endpoint every 30 seconds and on demand.
**Online** means the game endpoint answered a status probe. **No response**
means its availability is unknown; it does not prevent launching.
Player counts come from TreeStats, matched by configured server name. Missing
counts are shown as unavailable and failed refreshes mark cached counts stale.
These external population counts are separate from endpoint reachability.

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
A green dot means the game endpoint answered; a red dot means no response
within the timeout (offline or unreachable). Gray means not checked yet.
Status does not prevent launching. Player counts come from TreeStats, matched
by server name or an unambiguous hostname label such as coldeve in play.coldeve.ac. Missing
counts are shown as unavailable and failed refreshes mark cached counts stale.
These external population counts are separate from endpoint reachability.

Failed launches stay visible on their account/server row. Play refreshes when
the reconnect delay expires, even if no further session event arrives.

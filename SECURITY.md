# Security policy

OpenAC is a game client that talks to a game server over UDP and reads data
files you supply. If you find a way to make it execute code, read files it
should not, or misbehave against a server in a way that affects other players,
please report it privately.

## Reporting

Open a private report at
<https://github.com/eriknihlen/OpenAC/security/advisories/new>. Include the
version (from the launcher), the steps, and what you observed. You will get
an acknowledgement within a week.

Please do not open a public issue for a security problem until a fix has
shipped.

## Scope

In scope: the client, the launcher and updater, the headless host, and the
plugin loader. Out of scope: the game servers you connect to, and the retail
data files.

# Security policy

openac-rust is a game client that talks to a game server over UDP, reads data
files you supply, and runs WebAssembly plugins in a sandbox. If you find a way
to make it execute code, read or write files it should not, let a plugin
escape its sandbox or reach beyond the permissions it declared, or misbehave
against a server in a way that affects other players, please report it
privately.

## Reporting

Open a private report at
<https://github.com/eriknihlen/openac-rust/security/advisories/new>. Include
the version (from the launcher, or the tag you built from), the steps, and
what you observed. You will get an acknowledgement within a week.

Please do not open a public issue for a security problem until a fix has
shipped.

## Scope

In scope: the client (`openac`, graphical and headless), the launcher and its
updater, the plugin host and its WebAssembly sandbox (permissions, memory
caps, call deadlines, storage scoping, network access), the plugin installer
and `plugincheck`. Out of scope: the game servers you connect to, the
original game's data files, and third-party plugins themselves (report those
to their authors).

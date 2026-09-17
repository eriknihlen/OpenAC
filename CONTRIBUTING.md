# Contributing to OpenAC

Thanks for your interest. OpenAC exists to be worked on together: pull
requests, bug reports, questions, plugins, and platform ports are all wanted,
and nobody needs to ask before starting. OpenAC is MIT-licensed: you may fork
it, ship it, or build something else on it without asking either.

## Ground rules

- **Be kind.** The [code of conduct](CODE_OF_CONDUCT.md) applies everywhere
  the project talks.
- **One topic per pull request.** A fix and an unrelated refactor are two PRs.
- **Green build, green tests.** CI runs on every PR. Warnings are errors.
- **The behavior is retail.** OpenAC exists to reproduce what the original
  client did, in a codebase that stays readable. If you change how the client behaves in
  the world, say in the PR what the original client does and how you know.

## Getting set up

See [docs/building-and-running.md](docs/building-and-running.md). Short
version:

```bash
git clone https://github.com/eriknihlen/OpenAC.git
cd OpenAC
dotnet build AcDream.slnx -c Release
dotnet test AcDream.slnx -c Release --no-build --filter "Lane!=InstalledDat&Lane!=PreparedPackage&Lane!=Live&Lane!=Manual&Lane!=Timing&Lane!=Windows&Lane!=Linux&Lane!=MacOS&Lane!=Unix&Lane!=Vulkan&Lane!=SystemFont&Purpose!=Diagnostic&Status!=KnownFailure"
```

That filter is the portable gate: it skips tests that need your retail data
files, a prepared package, a live server, a GPU, or a specific OS. Run the
lanes you can; CI runs the rest.

## Where things live

`docs/architecture.md` maps the projects. In one line: `AcDream.Core` and
`AcDream.Core.Net` hold gameplay and protocol logic, `AcDream.Runtime` owns the
game state, `AcDream.App` draws it with Vulkan and hosts the retail UI,
`AcDream.Headless` runs the same runtime without a window, and
`AcDream.Plugin.Abstractions` is the surface plugins target.

## Faithfulness and provenance

Game-specific behavior (physics, movement, animation, the wire protocol, UI
layout) reproduces what the original client observably did. The maintainers
verify changes against the original client's behavior; a contributor who
cannot should describe the observed behavior instead and mark the PR as
needing verification. Please do not add comments that describe the original
client's internals; describe what the code does.

Do not paste code from other Asheron's Call projects. ACEmulator and
holtburger are AGPL-3.0 and ACViewer is GPL-3.0; their code cannot be
included in an MIT project. Reading them to understand a message layout is
fine; copying is not.

## AI-assisted contributions

This project is built with heavy AI assistance and welcomes contributions made
the same way. The bar is the same for every change regardless of how it was
written: it builds with warnings as errors, the tests pass, the behavior matches
the original game where that applies, and you can explain what it does and why.
You do not have to have read every generated line; you do have to understand
what the change does.

## Reporting bugs

Use the bug template. The most useful report has: the version, what you did,
what you expected, what happened, and the client log from `logs/` if there is
one. A screenshot of the original client doing it right is gold.

## Plugins

Plugins target `AcDream.Plugin.Abstractions` only and never import `AcDream.App`,
`AcDream.Runtime` or `AcDream.Core`. `docs/plugin-development.md` is the
guide for plugin authors; `docs/plugin-api.md` describes the surfaces and
`docs/plugin-ui-markup.md` the panel markup. Plugins belong in their own
repositories; `AcDream.Plugins.MossTank` in `src/` is the bundled example.

### Changing the plugin API

The contract is what external plugins compile against, so changes to it
follow stricter rules than the rest of the client:

- **Additive.** Add a member with a default implementation that returns an
  inert value (`false`, `Unavailable`, an empty list). Do not rename or
  remove a public member; a plugin built against the previous contract must
  keep compiling and loading. If a name must change, keep the old one as a
  forwarding default and say so in the summary.
- **Documented.** `AcDream.Plugin.Abstractions` generates its XML
  documentation with warnings as errors: an undocumented public member fails
  the build. Write the summary in plain terms, and say when the member
  returns false or unavailable.
- **One implementation per operation.** A new capability is implemented once,
  in Runtime where it concerns game state, and bound by both hosts. If the
  headless host cannot provide it, bind an explicit refusal there; do not
  leave the member silently unbound.
- **Tested through the binding.** A unit test on the surface class plus a
  host-level test (graphical or headless) that reaches the member through
  `IPluginHost`, so a panel or command cannot be dead while the layout and
  the logic both pass.
- **Not owned by one plugin.** Describe capabilities generically; nothing in
  the contract names a particular plugin, and the host never constructs a
  named plugin.

Mention the API change in your pull request description so the next
contract revision can note it.

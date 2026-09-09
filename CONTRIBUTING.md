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
dotnet test AcDream.slnx -c Release --no-build --filter "Lane!=InstalledDat&Lane!=PreparedPackage&Lane!=Live&Lane!=Manual&Lane!=Timing&Lane!=Windows&Lane!=Linux&Lane!=Vulkan&Lane!=SystemFont&Purpose!=Diagnostic&Status!=KnownFailure"
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

Plugins target `AcDream.Plugin.Abstractions` only and never import `AcDream.App`.
`docs/plugin-ui-markup.md` documents the panel markup. `AcDream.Plugins.MossTank`
in `src/` is a complete working example.

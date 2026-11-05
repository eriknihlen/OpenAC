## What this changes

## Original behavior
If this changes what the client does in the world: what does the original
client (or OpenAC) do, and how do you know?

## Checklist
- [ ] `scripts/check.sh` (or `scripts/check.ps1`) passes: fmt, clippy with `-D warnings`, the WebAssembly build, tests
- [ ] Ran the real-data tests (`--include-ignored`) if this touches data files, rendering or the interface
- [ ] One topic per PR
- [ ] If `ac-plugin-abi` changed: the layout fingerprint is updated and the PR says plugins must be rebuilt
- [ ] New options are documented in `docs/launch-options.md`
- [ ] No code copied from ACEmulator, ACViewer, or holtburger (AGPL/GPL)
- [ ] No comments describing the original client's internals

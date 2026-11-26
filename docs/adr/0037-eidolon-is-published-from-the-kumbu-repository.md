# 0037. Eidolon is published from the Kumbu² repository, under the LGPL

- **Date:** 2026-11-25
- **Status:** Proposed
- **Decided by:** proposed by Claude; Chris decides where the library lives
- **Spec:** [Eidolon as a library](../superpowers/specs/2026-11-25-eidolon-library-design.md)

## Context

0.8.0 takes the Eidolon core out of Kumbu² as a library for Chatsundere ([ADR 0002](0002-eidolon-core-as-a-host-agnostic-package.md) built it to be taken out). The server's sync API and the engine change together; the Obsidian plugin vendors the engine's sync part until there is a package to depend on.

## Decision

`packages/eidolon-core` stays in this repository and is published to npm from it, as `@second-circuit/eidolon`, under LGPL-3.0-or-later, with its own semantic version (0.x until a second host runs on it), an explicit list of exported entry points, and JavaScript with type declarations as what is published. The plugin depends on the published package instead of vendoring.

## Why

- A protocol change stays one pull request with server and engine tested together, as it is now.
- The LGPL lets other programs link the library whatever their licence, while changes to it stay free; every commit being the maintainer's, the copyright holder can relicense alone.
- An explicit surface is what a semantic version can promise; today every file is importable.

Lost: a repository of its own (visibly independent, its own issues, but every protocol change across two repositories and two CIs); keeping it private and vendored (Chatsundere would vendor too, a second copy to keep in step).

## Consequences

- A tag `eidolon-vX.Y.Z` and a workflow publish it; the npm organisation and its token are the maintainer's to set up.
- The package's files carry an LGPL header while the rest of the repository stays AGPL; the boundary test keeps AGPL and GPL code of others out of it.
- The plugin's version changes when it moves to the package.

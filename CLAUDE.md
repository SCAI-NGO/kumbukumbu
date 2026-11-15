# Kumbu² — notes for Claude

Kumbu² is a Markdown second brain served over MCP: a C# (.NET 10) server in `src/`, a React/Vite web app in `web/`, MongoDB for storage. `README.md` describes the product and its configuration; `docs/concept.md` and `docs/security-architecture.md` the design.

## Working with the maintainer

- Talk with the maintainer in **German**; everything that lands in the repository stays in British English.
- Interactive, together, the human in the loop. Where a requirement is ambiguous or several approaches are valid, ask, with your recommendation first; where the next step plainly follows, take it and say that you did.
- Short replies: the result first, at most one question, and that one last.
- Decisions come with their reasons: write the reason into the spec, next to the decision.
- Before fixing a bug, list every possible root cause (infrastructure, configuration and the layers between included), ranked by likelihood; then work through them in that order, verifying each.
- Merges, tags, deployments and deletions only on an explicit yes. Merge only when every check is green; rerun a flaky job rather than merging past it, and say so.
- Reference code as `path:line`.

## How we build

- What do we really need? Cut or freeze before adding. Solve the actual problem: no abstractions, helpers or wrappers for needs that may never come, no error handling for cases that can't occur, clarity over cleverness.
- Security first: validate at the system's boundaries, never trust input.
- Tests where the scope warrants them, written first for features and fixes: xUnit for C#, Vitest for the web app. No tests for trivial code.
- A new functional block of the UI gets a UX review before its spec; its findings are discussed one by one.
- Search with `rg`, not `grep`; navigate with the LSP where there is one.
- With the superpowers skills: execute plans subagent-driven, without asking. For work that isn't quick, offer an overnighter once, right after the spec is approved.

## Language and style

- Code, comments, docs, commit messages, PR descriptions: **British English** (colour, behaviour, initialise, licence as the noun).
- Match the surrounding code: its comment density, naming and idiom. Comments only where the reasoning isn't obvious. Source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later` where their siblings do.
- The repository names no customer or employer: not in code, docs, specs, test data or examples (use neutral words such as "Work"). The one organisation it may name, where needed, is its owner: Second Circuit – Association for digital freedom of thought. Deployment-specific settings go into `.env` or a `compose.override.yaml`, never into the repository.

## Build and test

```sh
dotnet test --solution Kumbu.slnx          # server and core; 4 tests skip without the model and a TensorX key
pnpm -C web install --frozen-lockfile      # first, or after a pull: stale node_modules make tests and build fail
pnpm -C web test --run
pnpm -C web build
```

The search tests that need the real embedding model run with `KUMBU_MODEL_DIR` pointing at a folder holding `arctic-m/` (the server downloads it into its `models` volume on the first start).

## Git and pull requests

- A branch per change, a pull request, squash merge. Commit messages imperative and free-form ("Let the admin choose the default embedding provider"), no Conventional Commits prefix.
- CI (`.github/workflows/ci.yml`) and the image build (`docker.yml`) run on every pull request. A push to `master` publishes `:master` and `:sha-…` images; only a version tag publishes `:latest`, which deployments follow.
- Specs go to `docs/superpowers/specs/YYYY-MM-DD-<topic>-design.md`, plans to `docs/superpowers/plans/`.

## Overnighters

Cloud runs (routines on Anthropic's servers) use the current Opus model (now `claude-opus-5-5`). Security audits are the one exception: they use the current Mythos model (now `claude-mythos-5-2`), our specialist for defensive security work, and stay blue team: reviewing and testing our own code, never exploits or live systems. As newer versions come out, the runs move to them, and the split stays the same.

## Releasing

Only when the maintainer asks for a release.

1. Set the version in `version.txt`, `src/Kumbu.Server/Kumbu.Server.csproj` (`<Version>`) and `web/package.json`, and add the release notes to `CHANGELOG.md` (built from the pull requests since the last release).
2. Pull request "Release vX.Y.Z", squash merge once CI is green.
3. Annotated tag on the merge commit: `git tag -a vX.Y.Z -m "Kumbu² X.Y.Z"`, then `git push origin vX.Y.Z`. The tag's image build is the release; there are no GitHub release objects.

A pre-release, for trying a build on a server pinned to it before the release: an annotated tag `vX.Y.Z-pre.N` (`-m "Kumbu² X.Y.Z-pre.N"`) on a `master` commit, without a release pull request; the version comes from the tag, `:latest` stays where it is, and `:prerelease` (which staging follows, as it moves on every version tag) moves to it.

The Obsidian plugin is versioned on its own; it only changes version when its code changes.

## Dependabot

Pull requests for NuGet packages of `src/Kumbu.Server` fail CI with `NU1004`: Dependabot updates the server's `packages.lock.json` but not the test project's. On the branch, merge `master`, run `dotnet restore Kumbu.slnx --force-evaluate`, commit the lock files, run the full test suite.

Images are signed with cosign 3 (Sigstore bundle format); the README's `cosign verify` needs cosign 3 or newer.

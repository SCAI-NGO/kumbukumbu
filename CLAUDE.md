# Kumbu² — notes for Claude

Kumbu² is a Markdown second brain served over MCP: a C# (.NET 10) server in `src/`, a React/Vite web app in `web/`, MongoDB for storage. `README.md` describes the product and its configuration; `docs/concept.md` and `docs/security-architecture.md` the design.

## Language and style

- Code, comments, docs, commit messages, PR descriptions: **British English** (colour, behaviour, initialise, licence as the noun).
- Match the surrounding code: its comment density, naming and idiom. Comments only where the reasoning isn't obvious. Source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later` where their siblings do.
- The repository names no organisation, customer or employer: not in code, docs, specs, test data or examples (use neutral words such as "Work"). Deployment-specific settings go into `.env` or a `compose.override.yaml`, never into the repository.

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

## Releasing

Only when the maintainer asks for a release.

1. Set the version in `version.txt`, `src/Kumbu.Server/Kumbu.Server.csproj` (`<Version>`) and `web/package.json`.
2. Pull request "Release vX.Y.Z", squash merge once CI is green.
3. Annotated tag on the merge commit: `git tag -a vX.Y.Z -m "Kumbu² X.Y.Z"`, then `git push origin vX.Y.Z`. The tag's image build is the release; there are no GitHub release objects.

The Obsidian plugin is versioned on its own; it only changes version when its code changes.

## Dependabot

Pull requests for NuGet packages of `src/Kumbu.Server` fail CI with `NU1004`: Dependabot updates the server's `packages.lock.json` but not the test project's. On the branch, merge `master`, run `dotnet restore Kumbu.slnx --force-evaluate`, commit the lock files, run the full test suite.

Images are signed with cosign 3 (Sigstore bundle format); the README's `cosign verify` needs cosign 3 or newer.

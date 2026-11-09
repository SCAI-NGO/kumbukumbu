# Overnighter brief: WebDAV access, storage quota, access tabs

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React web app, on MongoDB. A new user asked on her first day for the one thing that would make it feel like home: mounting Kumbu² as a drive in Dolphin, Windows Explorer or Finder and dragging files in. Tonight's work is that, together with what it needs before it can be handed out: a storage quota (with `df`, `du` and emptying the trash) and an access page with three tabs. It touches credentials, a new way in and every write path, so care counts for more than speed. An honest partial result is a good result.

Nobody is available to answer questions during this run. That is intended: the spec has been reviewed by Chris, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it (everything you need from it is repeated here)
3. Spec: `docs/superpowers/specs/2026-11-07-webdav-design.md`, reviewed and approved by Chris. Please don't edit it.
4. For context, as needed: `docs/security-architecture.md` (§3 slots, §5.6 what is in the clear, §6 API keys), `docs/superpowers/specs/2026-11-05-publish-design.md` §3.7–3.8 (who may remove a published note, "May publish"), `README.md`

## Where you run

- Branch `feature/webdav`. The session starts on the default branch, so first run `git fetch origin feature/webdav && git checkout feature/webdav`. Everything you push to that branch survives, everything else does not.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages, `litmus`) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new NuGet or npm package. WebDAV is implemented by hand (spec §7); `System.Xml` is part of the framework.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", please check whether the setup script's daemon is still running, and start it again if you can.
  - If Docker can't work at all, use an external replica-set MongoDB: install `mongod`, start it as a single-node replica set, and set `KUMBU_TEST_MONGO` (see `ExternalMongo` in `TestImages.cs`).
  - An earlier run met a hard file limit below what Testcontainers' `mongo` asks for. What worked was a container of your own with `--ulimit nofile=20000:20000`, named `kumbu-test-mongo`, as a replica set, and `KUMBU_TEST_MONGO` pointing at it.
  - Give every full run a fresh container, with `docker rm -fv kumbu-test-mongo` first: mongod runs out of file handles over many runs, and without `-v` anonymous volumes fill the disk.
  - If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- The 4 skipped tests need the real embedding model and a TensorX key. Neither is available here, and skipping them is expected. Running as root can skip 2 more.
- **litmus** (the WebDAV compliance suite) is in Debian/Ubuntu as the package `litmus`; otherwise build it from its source release. For the CI job (spec §7) use the runner's package manager, not a new project dependency.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool and say so in the report.

## Open points for the run

An independent review of spec and brief against the code found these. All are **decide**: settle them with the rules below, log each in the report. Where a suggestion is given, it is Chris' preferred reading.

1. **Parked content must not run on the app's origin.** Parked files hold any bytes under any name, and Basic credentials can be primed by a top-level navigation. Every `GET` under `/dav/` goes through `FileContentPolicy` (`src/Kumbu.Server/Api/Security.cs` ~70); parked content goes out as `application/octet-stream` with `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`. The OAuth CORS middleware never answers `/dav`.
2. **`dav_shares` needs what `api_keys` has**, copied from `src/Kumbu.Server/Accounts/ApiKeys.cs`: the writer label sealed under a key derived from the token (a request holding only the token can't open the user key; see `writer`, ~374 and ~591), the meta with vaults and rights (~587), resealing in `UserKeyRotation.cs` (~127, ~261), an expiry sweep as `ExpiredKeySweep`, and account deletion (`Deletion.cs` ~69). Spec §4 lists the fields in the clear; whatever else the key's design needs is sealed the same way.
3. **Member shares** touch more than the spec names: the history label from the membership's sealed `writers` (`Sharing/Memberships.cs` ~406–434), the owner's member panel and `MemberCredentialKinds` (~679, ~937), slot queries filtering on `Kind == ApiKey` (`VaultCatalog.Rekey.cs` ~545, ~556, ~586; `Memberships.cs` ~585; `VaultCatalog.cs` ~788), and the caller counting as human (`RequestCaller` `_human`, ~31). Find every place a member's API key is handled and handle a share there the same way.
4. **A revoked or expired share's retries** would use up the IP's budget in `WrongKeyLimiter` (20 per minute; a revoked API key counts as wrong today), and with it the MCP keys from the same IP. A token that matches a known share which is revoked or expired gives its permit back, as `CallerMiddleware` does for connection URLs (`ConnectionUrlAsync`), and gets 401.
5. **Routing**: `/dav` without a trailing slash redirects or answers as `/dav/`; the prefix is matched case-insensitively and fails closed (`GuardedPrefixes` in `CallerMiddleware.cs` ~210 covers only `mcp` and `api`: give `/dav` its own branch there, leaving the existing rules for `/mcp` and `/api` as they are). `PROPFIND` and `OPTIONS` on `/` must not fall into `MapFallbackToFile` (`Program.cs` ~291): `OPTIONS /` answers the DAV headers, `PROPFIND /` gets 405 or a redirect to `/dav/`.
6. **Protocol corners**: `DELETE` answers 204, also in spec §3.6 case 6 (the client is told it worked; the note reappears). `MKCOL`, `DELETE`, `PUT`, `MOVE` on `/dav/` itself or on a vault's own folder: 403. A folder `MOVE`d onto a parking name: 403 (only files are held). `COPY` of a parked file onto a real path: as a `PUT` of its bytes there. A parked file `MOVE`d onto a real path whose checks fail (UTF-8, size, format, quota): the status that `PUT` would get, the parked file stays. Extensions are matched case-insensitively (`.MD` is a note). Chunked bodies are limited as declared ones are, by counting.
7. **More temp patterns**: vim's `4913` probe file and emacs' `#name#` (emacs' `.#name` is covered by the dot rule).
8. **Quota details**: a per-user override of `0` means "no writes that add data", distinct from unlimited (store unlimited as −1 as spec §4 says). The first usage count after a start runs in the background, never on a write's path; until it is known, writes are allowed. Shares can be created while `KUMBU_DAV_ENABLED` is off (the tab says they work once it is on). `df`'s note and attachment counts are current entries, without the trash.
9. **The Obsidian plugin under a full quota** is in another repository. The sync endpoints refuse with 507 and the neutral text; record in `docs/superpowers/open-items.md` that the plugin must treat 507 as final for that file and tell the user, rather than retry.
10. **README notes**: CRLF becoming LF changes a `.md`'s size, so rclone may report "sizes differ" after uploading a CRLF file; say so with the remedy (`--ignore-size` or LF files).
11. **The litmus job** may create its user and share without the web app: the bootstrap admin (`src/Kumbu.Server/Cli/BootstrapAdmin.cs`), sign-in and password confirmation through the REST API, and plain HTTP in the Development environment (spec §3.3). Point it at `/dav/<vault>/.litmus/` (spec §7).

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-07-webdav.md`.
2. It starts with **Task 0: baseline**: run the full suites before any change and record the exact counts and the exact failures, against the known state below.
   Known state on `master` (`6a2afd9`): web 94 test files, 1691 tests, all passing, and `pnpm -C web build` green (measured on the planning machine). **.NET: unknown locally** (the planning machine has no .NET 10 SDK); Task 0 establishes it. CI run 42382451518 on `6a2afd9` failed in `dotnet test`, while the identical tree passed as the pull request's head `ead2e30`, so a flaky test is likely: if Task 0 shows a failure, run that test class alone three times, record whether it is flaky, and carry on; a test that fails every time on an untouched `master` is a known failure for the report, not something to fix. The image signing failure in the Docker workflow on `6a2afd9` happens only on pushes to `master` and is not this run's concern.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. **Order by value**, so that a short night still leaves something whole: (1) shares, slots and Basic authentication; (2) read-only WebDAV (`OPTIONS`, `PROPFIND`, `GET`/`HEAD`); (3) writing (`PUT`, `MKCOL`, `DELETE`, `COPY`, `MOVE` with the link rewrite), parking, locks, atomic saves and the published-note cases; (4) the quota on every write path, emptying the trash, `df`/`du`; (5) the web app: tabs, share dialog, connect page, quota display, admin override, trash; (6) litmus in CI, documentation, security review. Put a phase gate after each.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by a fresh subagent.
- Subagents never merge, push or switch branches. You commit and push.
- Commit and push after every task. Commit messages imperative and free-form ("Let a WebDAV share mount a vault"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- The test filter is Microsoft.Testing.Platform's: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`, repeatable. `FullyQualifiedName~` doesn't work here, and a glob in mid-name selects nothing. Core: `--project tests/Kumbu.Core.Tests`. Web: `pnpm -C web test --run <file>`.
- Full verification at each phase gate, as CI does (`.github/workflows/ci.yml`): `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`; `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`.
- **The web's CI gate is `pnpm -C web build`:** it runs the type check, which `pnpm test` does not.
- Only `dotnet build`, `dotnet test`, `pnpm test`, `pnpm build` and `litmus` output count as evidence.
- Time-dependent behaviour (the 60-second windows, parking TTL, lock timeouts, expiry, the usage recount) goes through the `TimeProvider` the code already injects, so tests don't sleep.
- **If the night runs short**, stop at the last green phase gate rather than half-way through a task, push, and say in the report which tasks are left.
- **Style:** British English in code, comments, docs, commits (colour, behaviour, initialise, licence as the noun). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **`web/src/api/schema.d.ts` is edited by hand here**, as generated output would have it.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples, test data. Use Alice, Bob, Carol, `Work`, `Personal`, `kumbu.example.org`.
- New environment variables go into `README.md` (what, format, example), `.env.example` and `compose.yaml`, as the existing `KUMBU_*` ones do.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. A server updated to this version behaves as before until its admin sets `KUMBU_QUOTA_DEFAULT` or someone creates a share.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture: a share is built like an API key (`src/Kumbu.Server/Accounts/ApiKeys.cs`), writes go through the existing writers, rights are per vault and nothing finer (spec §3.1); no new dependencies.
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling. When unsure, the reading that gives a share less is the right one.
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, do not guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the PR description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please do not merge, tag or deploy, and don't change the version: rollout is something Chris does himself. One draft pull request is the result.
- No secrets in logs, assertions or the report. **A share's password never reaches a log, a URL or a response other than the one that creates it**, not even at debug level.
- Specs outside this topic stay as they are; if one looks wrong, mention it in the report.
- **Every write that adds data passes the quota**, whichever channel it comes from (spec §3.10); removing, moving and emptying the trash always work, also over the quota. A member learns nothing about the owner's quota beyond "This vault is out of space."
- **No agent can delete permanently**: emptying the trash is the owner's session in the web app, with the password, and nothing else.
- **Client junk never reaches MongoDB** (spec §3.8), and an unsupported file fails visibly (415), never silently.
- **Security boundary: touched, deliberately.** A new credential kind (`dav_shares`, `dav` slots under an HKDF of the token), HTTP Basic in `CallerMiddleware`, an XML parser on untrusted input, path handling for a new protocol, and a hook in every write path (the quota). A late task has a fresh subagent review the whole branch against spec §5 and spec §2's anti-goals; every finding goes in the report, fixed or not, and the pull request says a security review applies. **Not touched:** the encryption scheme and the existing slot kinds' derivations, sessions and their password window (only used), OAuth, pairing, `CallerMiddleware`'s existing rules for `/mcp` and `/api`, and the publish feature's rules (a share is a credential without "May publish", spec §3.6 cases 6 and 7 aside).
- At the end: a row with the pull request's link in `docs/superpowers/open-items.md`, and in the report the hand-test checklist from spec §7 with numbered steps, where to look and what passes. Don't change anything else outside the plan's files.

## Out of scope

- Everything spec §2 lists as a non-goal: a link index, pending link adjustments, a graph; rights finer than a vault; real locking; Obsidian vaults over WebDAV; new stored file types; per-writer accounting.
- Changing the Obsidian plugin (another repository).
- Merging `master` into the branch: if `origin/master` moves during the night, the pull request will show whether it conflicts.

## The report

First open a draft pull request to `master` (not merged), titled "WebDAV access, storage quota and access tabs", whose body lists the decisions made without Chris and says that a security review applies, and ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Then, as the last commit, `docs/superpowers/overnighter/2026-11-07-webdav-report.md`, with:

- the environment
- the plan review findings and what you did with them
- Task 0 planned vs. measured
- for each task the commit and the real test output
- every mutation individually
- the litmus result, with the expected-failure list and why each entry is on it
- the security review's findings and what became of each
- the decisions made without Chris
- deviations, surprises, tasks left if any, open points for Chris
- the hand-test checklist
- the pull request's link, together with a row linking it in `docs/superpowers/open-items.md`

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!

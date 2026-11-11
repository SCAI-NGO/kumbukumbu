# Security audit of 0.4.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An independent security audit of `v0.3.2..v0.4.0-pre.5` (`9864ff7`), every finding proven and rated on the spec's one scale, the Low and Medium findings that need no decision fixed, F11 fixed, and two reports.

**Architecture:** Seven audit areas, each reviewed by its own fresh subagent against a threat model; every Medium-or-higher finding and every Low then checked by a second fresh subagent that tries to disprove it (a Low may only be rated up). Confidential findings go to the private repository before any fix starts. Fixes follow one procedure per finding: red test, green, every mutation, spec-compliance and code-quality reviews, and for a Medium the fix review of spec §6 before it is pushed.

**Tech Stack:** C# (.NET 10, xUnit on Microsoft.Testing.Platform, Testcontainers' MongoDB), React/Vite with Vitest, the PDF sidecar in TypeScript (`pdf/`).

**Spec:** `docs/superpowers/specs/2026-11-10-security-audit-0-4-0-design.md` (with the brief `docs/superpowers/overnighter/2026-11-10-security-audit-brief.md`).

## Global Constraints

- Scope is the diff `v0.3.2..v0.4.0-pre.5` and the existing code it calls into where a check happens (spec §2).
- Severity per spec §3: impact times exploitability; one sentence each; when in doubt, the higher level, with the doubt written down.
- Nothing of a Medium-or-higher finding (how to exploit it, a proof, the exact place) on a public branch, commit message, test name, comment or pull request (spec §5). Its record, proof test, fix tasks, test output, mutations and reviews live in `SCAI-NGO/kumbukumbu-security`, branch `audit/0-4-0`, under `audits/` only.
- No red proof test of a Medium or higher is ever committed to the public branch; it lives in a working copy outside the public tree, then in `audits/2026-11-10-0.4.0/` of the private repository.
- Fix only Low and Medium findings within spec §6's limits. Not touched: `src/Kumbu.Server/Auth/CallerMiddleware.cs`, `src/Kumbu.Server/Accounts/Sessions.cs`, `src/Kumbu.Server/Accounts/DavTokens.cs`, `src/Kumbu.Server/Accounts/DiscreetVaults.cs`, `src/Kumbu.Server/Api/LateSave.cs`, `src/Kumbu.Server/Sharing/`, anything that derives keys or slots, the CSP's policy, the version. When unsure whether a fix is inside the limits, it isn't.
- No new npm or NuGet dependency. No dependency upgrade (a vulnerable package is a finding only).
- Never test against a real host. Everything runs against the test server, the test MongoDB and stubs.
- British English; SPDX header on new source files; no organisation, customer or employer named (Alice, Bob, Dana, `Home`, `Work`, `Tagebuch`).
- Commits imperative, free-form, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Stage files by name only. Before every public push: `git diff origin/audit/0-4-0-security --stat`.
- Push only to `audit/0-4-0-security` (public) and `audit/0-4-0` (private). Never force-push; never merge, tag, deploy or change the version.
- Subagents may commit locally in a fix task; they never push, merge or switch branches. The controller pushes.
- A Medium fix is the only unpushed commit while it waits for its fix review: nothing else is committed or pushed on the public branch until that review has passed (then it is pushed) or failed (then it is dropped with `git reset --hard HEAD~1`).
- If the private repository can't be reached (can't clone, can't push, or can't open its pull request): the confidential findings go in full into the run's final message only, the public report and pull request name only their ids and severities and that this happened, and nothing of Medium or higher is fixed. Lows and F11 may still be fixed.
- Phase order: (1) audit, (2) adversarial checks, (3) confidential findings pushed and the private pull request opened, (4) F11, (5) fixes, (6) reports and pull requests. Nothing of phases 4 and 5 before phase 3 is pushed; in phases 1–3 the only public commit is this plan.
- If time runs short: stop fixing after the last green gate; priority audit and confidential findings and reports, then F11, then Medium fixes, then Low fixes.

## Review Focus

1. A fix that narrows what a caller sees also narrows the owner's own view: every filter fix gets a test that the owner (or the session, for discreet vaults) still sees what it saw before.
2. A fix to a 404 or error text changes the text for vaults outside discreet mode: every text fix gets a test that a user without a PIN or discreet vaults sees exactly today's text (brief rule 3: outside discreet mode nothing may differ).
3. F11 on a cold start: the vaults query not yet loaded must not flash the path-bearing text or heading; F11's tests include the loading state.
4. F11 in a vault the list names: today's text stays, including the moved-to sentence of `web/src/note/ChangedElsewhere.tsx`.
5. A proof test that passes for the wrong reason (a 404 because the fixture is wrong): every proof test also asserts the positive control (the same request succeeds for the caller who should succeed).

---

## The finding record (used by Tasks 1–9)

Every finding, wherever it is written, has these fields (spec §5): id, title, area, severity with one sentence for impact and one for exploitability, the attacker (from the list below), the proof (a test's code with its run output, or a precise trace with file and line), the adversarial verdict and final severity, a proposed fix and the tests that should prove it, whether it needs a decision from Chris, and whether it lies inside spec §6's limits.

Ids: confidential findings `S-01`, `S-02`, … (Medium and higher); public findings `L-01`, `L-02`, … (Low, after their adversarial check). An unproven suspicion is recorded as such, with what would prove it, and gets no id.

The attackers (spec §2): someone without an account; a user with an account but no access to the vault; a member with a lower role; a holder of a WebDAV share, an API key, a device key or a connection limited to some vaults; a holder of a published link or a skill link; a malicious skill marketplace or GitHub repository; a malicious file (PDF, image, ZIP, Markdown); a person looking over the shoulder of a user with discreet vaults.

The classes (spec §4 step 2): authentication and authorisation (every new endpoint, credential kind, filter); path handling and traversal; injection (process arguments, XML, Markdown to HTML, MongoDB queries); SSRF and outbound requests; resource exhaustion (archives, images, PDFs, XML, large tables, PROPFIND depth); race conditions; information leaks (existence of vaults and notes, timing, error texts, logs); cryptography (only new slots or derivations); CSRF and origin checks; the CSP.

What is no finding: a behaviour an earlier spec decided on purpose, unless it breaks a promise of `docs/security-architecture.md` or the spec itself (then: a finding with "needs a decision from Chris", unfixed); a finding the security reviews in the reports of #152 (`docs/superpowers/overnighter/2026-11-07-webdav-report.md`), #153 (`2026-11-08-skills-report.md`) and #179 (`2026-11-09-discreet-vaults-report.md`) list as accepted; an item of `docs/superpowers/open-items.md`. One they list as fixed but which isn't, is a finding.

---

### Task 0: Baseline

**Files:** none changed.

- [ ] **Step 1: Environment.** `git fetch origin audit/0-4-0-security && git checkout audit/0-4-0-security`; clone `https://github.com/SCAI-NGO/kumbukumbu-security` beside it if it isn't there; `docker info` answers (restart the setup script's daemon if it says "Cannot connect"). .NET SDK per `global.json` (10.0.100, roll forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`, Node 26, pnpm 10.32.1 (`npm install -g pnpm@10.32.1`), then `pnpm -C web install --frozen-lockfile`. The tokenizer:
  ```sh
  mkdir -p "$HOME/.cache/kumbu"
  curl -fsSL --retry 3 --retry-all-errors -o "$HOME/.cache/kumbu/tokenizer.json" https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v2.0/resolve/95c2741480856aa9666782eb4afe11959938017f/tokenizer.json
  echo "f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec  $HOME/.cache/kumbu/tokenizer.json" | sha256sum -c -
  export KUMBU_TOKENIZER="$HOME/.cache/kumbu/tokenizer.json"
  ```
  Record each version and everything installed.

- [ ] **Step 2: Build.** Run: `dotnet restore --locked-mode && dotnet build --configuration Release --no-restore`. Expected: `0 Error(s)`.

- [ ] **Step 3: Core tests.** Run: `dotnet test --project tests/Kumbu.Core.Tests --configuration Release --no-build`. Expected: 2153 total, 3 skipped, 0 failed.

- [ ] **Step 4: Server tests, four groups**, each with a fresh `kumbu-test-mongo` (`docker rm -fv kumbu-test-mongo; docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all; sleep 5; docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate()'`, `KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true"`):
  ```sh
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Api --filter-namespace Kumbu.Server.Tests.Mcp --filter-namespace Kumbu.Server.Tests.Auth --filter-namespace Kumbu.Server.Tests.Net
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Accounts --filter-namespace Kumbu.Server.Tests.Sharing --filter-namespace Kumbu.Server.Tests.Welcome
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Vaults --filter-namespace Kumbu.Server.Tests.Skills --filter-namespace Kumbu.Server.Tests.Search --filter-namespace Kumbu.Server.Tests.Metrics
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-not-namespace Kumbu.Server.Tests.Api --filter-not-namespace Kumbu.Server.Tests.Mcp --filter-not-namespace Kumbu.Server.Tests.Auth --filter-not-namespace Kumbu.Server.Tests.Net --filter-not-namespace Kumbu.Server.Tests.Accounts --filter-not-namespace Kumbu.Server.Tests.Sharing --filter-not-namespace Kumbu.Server.Tests.Welcome --filter-not-namespace Kumbu.Server.Tests.Vaults --filter-not-namespace Kumbu.Server.Tests.Skills --filter-not-namespace Kumbu.Server.Tests.Search --filter-not-namespace Kumbu.Server.Tests.Metrics
  ```
  Expected: together 3521 total, 7 skipped (9 when running as root), 0 failed. A failure in `AccountApiTests`, `RekeyTransitionTests`, `PairingTests` or a sync-hub class: run that class alone three times (`-- --filter-class "*ClassName"`), record pass/fail of each, and carry on.

- [ ] **Step 5: Web.** Run: `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`. Expected: 128 files, 2226 tests passed; build green.

- [ ] **Step 6: PDF sidecar.** Run: `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`. Expected: all passing, build green.

- [ ] **Step 7: Record** planned vs. measured counts, exact failures, and the time Task 0 took, for the report. No commit.

### Task 0a: Plan review

- [ ] **Step 1:** A fresh subagent gets the spec, the brief and this plan (not the controller's conclusions) and lists concrete issues.
- [ ] **Step 2:** Fold them in; record each and what was done with it for the report.
- [ ] **Step 3:** `git add docs/superpowers/plans/2026-11-10-security-audit.md`, commit "Plan the security audit of 0.4.0", check `git diff origin/audit/0-4-0-security --stat` names only the plan, push. This is the only public commit of phases 1–3.

---

### Tasks 1–7: one audit per area

Each area task is done by one fresh subagent in its own git worktree of the public repository, made by the controller outside the public tree (`git worktree add --detach /home/user/wt/audit-<area> HEAD`, removed with `git worktree remove --force` after the hand-back), so that its proof tests never touch the public working tree; with its own MongoDB container on its own port (`docker run -d --name kumbu-audit-<area> --ulimit nofile=20000:20000 -p <port>:27017 mongo:7.0 --replSet rs0 --bind_ip_all`, `rs.initiate()`, `KUMBU_TEST_MONGO=mongodb://127.0.0.1:<port>/?replicaSet=rs0&directConnection=true`). The subagent gets this plan's "finding record" section, the spec, `docs/security-architecture.md`, the area's design spec and earlier security review, and the area's files; not the other areas' results.

Every area task has the same steps:

- [ ] **Step 1: Threat model.** Write down the area's assets, entry points (every new endpoint, MCP tool, shell command, page, background job), the attackers above who reach each, and what the design promises (security-architecture section and design-spec section, by heading).
- [ ] **Step 2: Review** every entry point against the classes, reading the code `git diff v0.3.2..9864ff7 -- <area paths>` and the code it calls where a check happens.
- [ ] **Step 3: Prove** each candidate: a test in the worktree (`tests/Kumbu.Server.Tests/<Namespace>/`, `tests/Kumbu.Core.Tests/…`, `web/src/…/*.test.tsx`, `pdf/src/…`) that fails because of the weakness and asserts a positive control; run it with the area's exact command (`dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*<Class>"`, `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*<Class>"`, `pnpm -C web test --run <file>`, `pnpm -C pdf test`). Where a test isn't possible, a trace with file and line for every step.
- [ ] **Step 4: Rate** per spec §3 and fill in the finding record (the proof test's full code and its run output included).
- [ ] **Step 5: Hand back** the records, the suspicions left unproven, and the list of what was checked and found sound. The found-sound lists go to the private record only (a gap in one area's list would point at a confidential finding); the public report gives per area the same method and the classes reviewed. The subagent commits nothing and pushes nothing; the worktree's proof tests are copied by the controller into the private clone only.

#### Task 1: Area E, discreet vaults (first: sessions and a PIN)

**Files to audit:** `src/Kumbu.Server/Accounts/DiscreetVaults.cs`, `src/Kumbu.Server/Accounts/Sessions.cs`, `src/Kumbu.Server/Api/LateSave.cs`, every filter of `docs/superpowers/specs/2026-11-09-discreet-vaults-design.md` §3.4 (find them with `git diff v0.3.2..9864ff7 --stat -- src` and `rg -n "Discreet|discreet" src/Kumbu.Server`), `web/src/discreet/`, and the shell, routes and stores that read `useDiscreet`.
**Design promises:** the discreet-vaults design, its pre.3 and pre.4 follow-ups (`2026-11-09-pre3-findings-design.md`, `2026-11-10-pre4-findings-design.md`), security-architecture §4 and §6.
**Earlier review:** `docs/superpowers/overnighter/2026-11-09-discreet-vaults-report.md`.
**Focus:** every list and count outside discreet mode that could name or betray a hidden vault (vaults, keys, connections, shares, members, invitations, search, history, trash, metrics, logs, errors, timing); the PIN's rate limit and its state across sessions and restarts; the reveal's end and the late save; what a second session or tab sees; what stays on screen or in browser storage after hiding.
**Decided, not a finding by itself:** agents, WebDAV and sync see discreet vaults; the PIN hides, it doesn't encrypt; the late save takes one save.

#### Task 2: Area A, WebDAV and the storage quota

**Files to audit:** `src/Kumbu.Server/Dav/`, `src/Kumbu.Server/Accounts/DavShares.cs`, `src/Kumbu.Server/Accounts/DavTokens.cs`, `src/Kumbu.Server/Accounts/StorageQuota.cs`, `src/Kumbu.Server/Auth/CallerMiddleware.cs` (the share's path), and every write path's quota hook (`rg -n "StorageQuota|Quota" src`).
**Design promises:** `docs/superpowers/specs/2026-11-07-webdav-design.md`, security-architecture §6 "WebDAV shares".
**Earlier review:** `docs/superpowers/overnighter/2026-11-07-webdav-report.md`.
**Focus:** a share reaching vaults, paths or rights outside its slots; HTTP Basic over plain HTTP; the share used outside `/dav/`; XML parsing (DTDs, entities, size, depth); PROPFIND `Depth: infinity`; `MOVE`/`COPY` destinations across vaults or outside the share; `LOCK`; deletion for good; quota bypass and races between concurrent writes; the share's name and password in logs.

#### Task 3: Area B, skills

**Files to audit:** `src/Kumbu.Server/Skills/` (the `/k` page, `GitHubClient.cs`, marketplaces, updates, `SkillZip.cs`, `SkillCopier.cs`), `src/Kumbu.Core/Skills/`, `src/Kumbu.Server/Mcp/HandshakeInstructions.cs`, cross-vault copy and move, `web/src/skills/`, `web/src/skillpage/`.
**Design promises:** `docs/superpowers/specs/2026-11-08-skills-design.md`, security-architecture §4 "Skills".
**Earlier review:** `docs/superpowers/overnighter/2026-11-08-skills-report.md`.
**Focus:** the anonymous `/k` page and its cookie (what a skill link reveals beyond the skill, guessing, CSRF); outbound requests (SSRF through marketplace URLs, redirects, hosts other than GitHub's, size and time limits); archives (traversal, links, bombs, number of entries); a skill's text reaching the handshake instructions (prompt injection into every connection); copy and move across vaults with lower roles.

#### Task 4: Area C, ingesting attachments

**Files to audit:** `src/Kumbu.Core/Sh/Commands/PdfImages.cs`, `src/Kumbu.Core/Sh/Commands/PdfCommand.cs`, `src/Kumbu.Core/Attachments/`, `src/Kumbu.Server/Attachments/` (`PdfSidecar.cs`, `ExtractionGate.cs`, `AttachmentPixels.cs`), `pdf/src/`, the system skill.
**Design promises:** `docs/superpowers/specs/2026-11-08-ingest-attachments-design.md`, security-architecture §7 "The PDF sidecar".
**Earlier review:** `docs/superpowers/overnighter/2026-11-08-ingest-attachments-report.md`.
**Focus:** process arguments and output paths in the sidecar (`pdfimages`, `pdftoppm`); output written into vaults (names, counts, sizes, quota); decompression and image bombs; the sidecar's answer trusted by the server; the gate's concurrency.

#### Task 5: Area F, web app rendering

**Files to audit:** the remark plugins (highlight, comments, footnotes), KaTeX, the reader page and `web/s.html`, `web/src/note/`, the CSP (`rg -n "Content-Security-Policy" src`).
**Design promises:** `docs/superpowers/specs/2026-11-09-obsidian-parity-quick-wins-design.md`, security-architecture §7.
**Earlier review:** `docs/superpowers/overnighter/2026-11-09-obsidian-parity-quick-wins-report.md`.
**Focus:** raw HTML or `javascript:`/`data:` URLs through the new syntax (footnote ids and labels, highlight and comment spans, KaTeX's `\href`, `\url`, `\htmlClass`, `trust`, `maxSize`, `maxExpand`); id clobbering; the reader page with a hostile published note; copy-as-Markdown on the reader page; what the CSP still allows.

#### Task 6: Area G, cross-cutting

**Files to audit:** dependencies (`dotnet list package --vulnerable --include-transitive`, `pnpm -C web audit`, `pnpm -C pdf audit`), `src/Kumbu.Server/Metrics/KumbuMetrics.cs`, every new log line (`git diff v0.3.2..9864ff7 -- src | rg "Log(Information|Warning|Error|Debug)"`), error texts, response headers on every new route, `KumbuOptions.cs` defaults.
**Focus:** secrets, vault or note names in logs and metrics labels; versions in headers or errors; defaults that are unsafe on a fresh install; a vulnerable package whose code Kumbu² reaches (rated by reachability; no upgrade).

#### Task 7: Area D, search and structural chunking

**Files to audit:** `src/Kumbu.Core/Search/`.
**Design promises:** `docs/superpowers/specs/2026-11-09-structural-chunking-design.md`, security-architecture §5.5.
**Earlier review:** `docs/superpowers/overnighter/2026-11-09-structural-chunking-report.md`.
**Focus:** resource exhaustion in the chunker (deep headings, huge tables, pathological Markdown); results or snippets crossing vaults or a credential's reach; snippets of hidden vaults.

---

### Task 8: Adversarial checks

- [ ] **Step 1:** For each Medium-or-higher finding, a fresh subagent gets the claim (title, severity, attacker, the proof test's code or the trace) and the code, not the auditor's reasoning, and tries to disprove it: runs the proof test itself in its own worktree, looks for a check elsewhere that stops it, a design decision that allows it, an earlier review that accepted it. Answer: confirmed, disproved (why), or re-rated (to what, why).
- [ ] **Step 2:** For each Low, the same, but the verdict may only keep it or rate it up.
- [ ] **Step 3:** Record each verdict and final severity in the finding record. A Low rated up becomes confidential (an `S-` id).

### Task 9: Confidential findings to the private repository

**Files:**
- Create (private): `audits/2026-11-10-0.4.0.md`, `audits/2026-11-10-0.4.0/<proof test files>`

- [ ] **Step 1:** In `kumbukumbu-security`, create `audit/0-4-0` from `master` (check it out if it exists).
- [ ] **Step 2:** Write every confidential finding's record, with its adversarial verdict, the proof tests as files that drop into `tests/…` or `web/src/…`, and per finding inside §6's limits its fix task (files with line anchors, the behaviour test with its assertions, the exact commands with expected fail and pass, the mutations). If there is none: "none", and why that is trusted.
- [ ] **Step 3:** Commit, `git push -u origin audit/0-4-0`, open the pull request "Security audit of 0.4.0" to `master`. If any of clone, push or pull request fails: the Global Constraints' rule for an unreachable private repository applies (findings in full in the final message only; no Medium fixed; what was pushed stays).

### Task 10: F11, the 404 of a note in a vault the list doesn't name

**Files:**
- Modify: `web/src/discreet/errorText.ts:15-18` (`useErrorTextFor`)
- Modify: `web/src/note/NoteView.tsx:88-89` (reuse its `vaults`/`vault`; `vault === undefined` means unnamed) and `:361` (the `<h1>{titleOf(path)}</h1>` made from the path)
- Modify: `web/src/shell/MobileShell.tsx:317` (`<TopBar path={gone ? null : path} …>`; `TopBar.tsx:56-57` shows `crumbOf(path)` and `titleOf(path)`)
- Test: `web/src/discreet/errorText.test.tsx`, `web/src/note/NoteView.test.tsx`, a shell-level test in the pattern of `web/src/discreet/offlineEnd.test.tsx:515`

**Interfaces:**
- Consumes: `useVaults()` from `web/src/api/notes.ts:132` (the vaults query the app already has; no new request; its `select: withoutDiscreet` drops discreet vaults while the tab isn't revealed), `vaultOf` from `web/src/api/paths`, `NO_SUCH_NOTE` from `web/src/discreet/errorText.ts:9`.
- Produces: one rule, `unnamed = !vaults.data || !vaults.data.some(v => v.name === vaultOf(path))` (a list still loading or failed counts as unnamed), used in all three places: `useErrorTextFor(path)` returns `NO_SUCH_NOTE` for a 404 when `discreet || unnamed`; the note view shows no path heading for a 404 when unnamed; the shell passes `path={gone || (unnamed && the note answered 404) ? null : path}` to the top bar. In a named vault, the heading during the note's own loading and every text stay as today (nothing differs for users without a PIN).
- Intended reach: the hook is also used by `web/src/note/MarkdownView.tsx:140` (embeds) and `web/src/note/HistorySheet.tsx:49,97,173,225`; they get the same rule. The checkpoint route (`web/src/routes/Checkpoint.tsx:89`, `CheckpointNote.tsx:38`) uses raw `errorText`: check whether a 404 there names a vault outside the list and record the decision.

- [ ] **Step 1: Write the failing tests.** In `errorText.test.tsx`, give `text()` a wrapper with a fresh `QueryClient`; seed `queryClient.setQueryData(["vaults"], [{ name: "Home" }])` for the resolved case, leave it unseeded for the loading case; update the existing `/Work/Fehlt.md` test (line 26) to seed `[{ name: "Work" }]`. New tests: with `[Home]`, a 404 for `/Tagebuch/Heute.md` reads `"There is no such note (if it was removed, it is in the trash)."`; a 404 for `/Home/a.md` reads the server's text; a 500 for `/Tagebuch/Heute.md` reads today's text; unseeded (loading), the 404 for `/Tagebuch/Heute.md` is `NO_SUCH_NOTE`. In `NoteView.test.tsx`: a 404 for `/Tagebuch/Heute.md` with `/api/vaults` answering `[Home]` renders no `Heute` heading and the `NO_SUCH_NOTE` text; the same with the vaults response held back; for `/Home/a.md` the heading `a` stays. Shell level: for `/Tagebuch/Heute.md` answering 404, neither `Tagebuch` nor `Heute` appears anywhere in `document.body`.
- [ ] **Step 2: Run** `pnpm -C web test --run src/discreet/errorText.test.tsx src/note/NoteView.test.tsx <the shell test file>`. Expected: the new tests FAIL (the path is named, the heading and top bar shown); the updated existing ones pass.
- [ ] **Step 3: Implement** the rule of Interfaces in the three places.
- [ ] **Step 4: Run** the same command, then `pnpm -C web test --run src/note/MarkdownView.test.tsx src/note/HistorySheet.test.tsx src/note/ChangedElsewhere.test.tsx`, then `pnpm -C web test --run && pnpm -C web build`. Expected: all pass, build green.
- [ ] **Step 5: Mutations**, each alone, restored with `git checkout -- <file>`: (a) drop the `unnamed` term in the hook → the Tagebuch text test fails; (b) drop `!vaults.data` → the loading tests fail; (c) invert the membership check → the Home test fails; (d) drop the heading guard in `NoteView.tsx` → the heading test fails; (e) drop the top-bar guard in `MobileShell.tsx` → the shell test fails. Record each failing output.
- [ ] **Step 6: Reviews** by two fresh subagents (spec compliance against spec §6's F11 paragraph and brief open point 6; code quality).
- [ ] **Step 7: Commit** the changed files by name with message "Say a note is missing without its path when its vault isn't in the list" and "F11" in the body; check `git diff origin/audit/0-4-0-security --stat`; push.

### Task 11: The fix procedure, per finding

Applied to each finding that is Low or Medium, needs no decision, lies inside spec §6's limits and is small. Low fix tasks are added to this plan below once the Low has passed Task 8; Medium fix tasks live only in the private repository (Task 9 Step 2).

A Medium fix is the only unpushed commit while it waits for its fix review (Global Constraints). If the private repository was unreachable (Task 9 Step 3), no Medium is fixed.

- [ ] **Step 1:** A fresh subagent writes the behaviour test (what the code must do; for a Medium, nothing about how it could be abused), runs it, sees it FAIL.
- [ ] **Step 2:** It makes the smallest change, runs the test, sees it PASS, then commits locally (Low: "L-NN" in the body; Medium: a neutral message).
- [ ] **Step 3:** It breaks the fix once per mutation, runs the test, sees it fail, restores with `git checkout -- <file>`.
- [ ] **Step 4:** The full gate (server fix) or the web lines (web-only fix). Expected: the Task 0 counts plus the new tests, 0 failed.
- [ ] **Step 5:** Spec-compliance review and code-quality review, each by a fresh subagent. For a Medium also the fix review of spec §6: a fresh subagent with the confidential finding, the diff and the code answers whether it closes the weakness for every attacker, opens a new one, or changes what a user sees beyond the fault.
- [ ] **Step 6:** Low: push. Medium: push only after the fix review confirms; otherwise `git reset --hard HEAD~1` (never pushed), the finding stays unfixed, the verdict goes to the private record. Update the private record with commit, test output, mutations and reviews.

### Task 12: Final gate

- [ ] **Step 1:** Run `dotnet restore --locked-mode && dotnet build --configuration Release --no-restore`, Task 0 Step 3's core command, Task 0 Step 4's four server commands (each with a fresh `kumbu-test-mongo`), `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`, `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`. Expected: Task 0's measured counts plus the new tests, 0 failed (a known flaky class rerun alone three times and recorded).

### Task 13: Reports and pull requests

**Files:**
- Create: `docs/superpowers/overnighter/2026-11-10-security-audit-report.md` (the last commit)
- Modify: `docs/superpowers/open-items.md` (a row with the public pull request's link; a row by finding id only for each question that needs a change of scope)
- Modify (private): `audits/2026-11-10-0.4.0.md`

- [ ] **Step 1:** Update the private record with every fix's commit, output, mutations and review; push; update the private pull request.
- [ ] **Step 2:** Open the public pull request "Security audit of 0.4.0: fixes" from `audit/0-4-0-security` to `master`, with the counts per severity (not per area), the Low findings and their fixes, F11, the confidential findings by id, severity and fixed-or-not only (no area, commit or test name; Medium fixes not listed as fixes), the decisions made without Chris (none that reveal a confidential finding), the security boundary, ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- [ ] **Step 3:** Write the public report (the brief's list: environment, plan review, Task 0 planned vs. measured, method per area, counts, Lows in full, each Low fix and F11 with commit, test output and every mutation, decisions, deviations, open points, the hand-test checklist with numbered steps, both pull requests' links), add the open-items row, check `git diff origin/audit/0-4-0-security --stat`, commit, push.
- [ ] **Step 4:** Send the `PushNotification`.
- [ ] **Step 5:** Do not merge, tag or deploy either pull request.

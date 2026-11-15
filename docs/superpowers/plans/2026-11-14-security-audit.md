# Security review of 0.5.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An independent, defensive security review of `v0.4.0..v0.5.0-pre.1` (`8f3f4b55`), every weakness shown by a behaviour test or a code trace and rated on the spec's one scale, and two reports. Nothing is fixed.

**Architecture:** Seven review areas, each reviewed by its own fresh subagent against the area's trust boundaries and promises, plus one check of whether the 0.4.0 findings' fixes still hold. Every Medium-or-higher finding and every Low then goes to a second fresh subagent that gets the claim and the code, not the first reviewer's reasoning. Confidential findings go to the private repository; the public report carries counts, the Low findings and ids.

**Tech Stack:** C# (.NET 10, xUnit on Microsoft.Testing.Platform, MongoDB 7 as a single-node replica set), React/Vite with Vitest, the PDF sidecar in TypeScript (`pdf/`), Playwright (`web/e2e/`, read only).

**Spec:** `docs/superpowers/specs/2026-11-14-security-audit-0-5-0-design.md` (with the brief `docs/superpowers/overnighter/2026-11-14-security-audit-brief.md`, and from the 0.4.0 audit `docs/superpowers/specs/2026-11-10-security-audit-0-4-0-design.md` §3, §4 steps 1 to 4, and §5's publication rule).

## Global Constraints

- **Blue team only** (spec §1). Code review and defensive tests of our own code. Every test asserts the behaviour the code should have. Inputs are the ordinary untrusted inputs of a unit test: the standard sanitiser test strings and bounded oversized or nested inputs. No exploit development, nothing obfuscated, no work on getting past a filter, no chaining of steps, nothing against a live system.
- **No fixes** (spec §5). No product code changes, public or private; no version change; no dependency added or upgraded. The only commits: on the public branch this plan, `docs/superpowers/open-items.md` and the report; in the private repository, `audits/`.
- **Scope** (spec §2): the diff `v0.4.0..v0.5.0-pre.1` and the existing code it calls into where a check happens; code untouched since 0.4.0 only where a new feature reaches it in a new way. `web/e2e/` and `.dockerignore` at the branch head (`git diff 8f3f4b55..HEAD`), only for leaking secrets or reaching the image. Out of scope: spec §8 in full.
- **Severity** (0.4.0 spec §3, read with this spec's terms): impact times reachability, one sentence each; when in doubt between two levels, the higher one, with the doubt written down.
- **Publication rule** (spec §4, §9): nothing of a Medium-or-higher weakness (the area, the place, the way it shows, a test) on a public branch, in a commit message, in a pull request or on the Factory card. This plan names no finding. When in doubt whether a detail is safe to publish, it isn't.
- **Worktrees.** Every reviewer works in its own worktree of `v0.5.0-pre.1` outside the public clone (`git worktree add /home/user/audit-run/<area> v0.5.0-pre.1`), writes its tests and notes only there, and never commits. The controller copies what belongs in the private repository to `audits/2026-11-14-0.5.0/`.
- **Never test against a real host.** Everything runs against the in-process test server, the test MongoDB and stubs. Read-only advisory lookups are allowed (`pnpm audit`, `dotnet list package --vulnerable --include-transitive`, GitHub's advisory database if those fail).
- **Ids:** `S5-NN` for Medium and higher, `L5-NN` for Low, given by the controller after the second review. A 0.4.0 finding still open, or whose fix doesn't hold, keeps its 0.4.0 id and is reported in the private file only.
- **No finding:** a decision an earlier spec made on purpose, unless it breaks a promise of `docs/security-architecture.md` (then: a finding with "needs a decision from Chris"); a point the earlier per-diff reviews list as accepted; an item of `docs/superpowers/open-items.md`. A point they list as fixed that isn't fixed is a finding.
- **Subagents** never commit, merge, push or switch branches. The controller commits and pushes, stages files by name, and runs `git diff origin/audit/0-5-0-security --stat` before every public push.
- Push only to `audit/0-5-0-security` (public) and `audit/0-5-0` (private). Never force-push; never merge, tag or deploy.
- British English; no organisation, customer or employer named (Alice, Bob, Dana, `Home`, `Work`).
- Commit messages imperative and free-form, ending with `Co-Authored-By: Claude Mythos 5.2 <noreply@anthropic.com>`.
- **Deadline:** both reports written and pushed by 2026-11-15 06:00 UTC. If time runs short, stop reviewing and write what there is, with an honest account of the areas not finished.
- **The plan stays as committed.** After Task 0a this file isn't edited or committed again until phase 4 (no ticked boxes, no scope notes); progress lives on the Factory card and in private notes.
- No secrets in logs, assertions or the reports (test passwords and keys made by fixtures are fine). Tools installed for the run are noted in the report. If superpowers skills, the board connector or `PushNotification` aren't available, the run emulates or skips them and says so in the report.
- **The Factory card** carries phases only, a log line at least every hour, and ids, severities and counts only at the end of a phase; never an area, a file or a detail.

## Review Focus

The review itself is the product, so these are the ways a review goes wrong:

1. **A test that passes or fails for the wrong reason.** Every test that shows a weakness also asserts a positive control: the same request succeeds for the caller who should succeed, or the same parser handles an ordinary input of the same size in time.
2. **A design decision reported as a finding, or a finding waved through as a decision.** Each candidate is checked against the area's spec, its report's security review and `docs/security-architecture.md` before it is rated.
3. **A Low that is really a Medium described in public.** The second review may only confirm a Low, rate it up, or refute it; a Low it rates up moves to the private file before anything is written in public.
4. **A finding tied to an area by timing or by gaps.** Per-area "found sound" lists go to the private file only; the public report gives the method per area.
5. **Timings taken under load or from a Debug build.** Resource findings are measured in Release, alone, with the control beside them.

---

## The finding record (Tasks 1 to 10)

Each finding (spec §4): id, title, area, severity with one sentence on impact and one on reachability, the trust boundary it crosses (spec §2's list), the evidence (a test's code with its run output and its positive control, or a precise trace with file and line), the second review's verdict and the final severity, a proposed fix with the tests that should prove it, and whether it needs a decision from Chris. A suspicion that couldn't be shown is recorded as a suspicion, with what would show it, and gets no id.

The classes every reviewer goes through (0.4.0 spec §4 step 2): authentication and authorisation (every new endpoint, credential kind and filter); path handling and traversal; injection (process arguments, XML, Markdown to HTML, MongoDB queries); SSRF and outbound requests; resource exhaustion; race conditions; information leaks (existence of vaults and notes, timing, error texts, logs); cryptography (only where new slots or derivations were added); CSRF and origin checks; the CSP.

The trust boundaries (spec §2): someone without an account who holds an AI link or a published link; a user with an account but no access to the vault; a member with a lower role; a holder of an API key, a WebDAV share, a device key or a connection limited to some vaults; a writer whose Markdown, frontmatter or Mermaid source others open; a person looking over the shoulder of a user with discreet vaults.

---

### Task 0: Setup and the baseline

**Files:** none changed.

Target: about 15 minutes; an overrun is noted in the report.

- [ ] **Step 1: The Factory card.** Read the board's rules with `sh board /Factory`. Add the card with the `board` tool: vault `Factory`, title "Security review 0.5.0", column `Running`. Its text: the repository and branch, the brief's path, the trigger `trig_01ToeBk9kmgaq4JQsehYnhKG`, the session's link, a `## Steps` checklist of exactly "Task 0", "Phase 1: area reviews (7)", "Phase 2: second reviews", "Phase 3: confidential findings", "Phase 4: public report", and a `## Log`. If the connector isn't available, say so in the report and carry on without the board.
- [ ] **Step 2: The branches.** `git fetch origin audit/0-5-0-security && git checkout audit/0-5-0-security`, `git fetch origin --tags`; in `/home/user/kumbukumbu-security`, `git fetch origin` and `git checkout -b audit/0-5-0 origin/master` (or check the branch out if it exists). The reviewers' worktrees under `/home/user/audit-run/` are added, or reused if they exist.
- [ ] **Step 3: The tools.** .NET SDK per `global.json` with `DOTNET_CLI_TELEMETRY_OPTOUT=1`; Node 26 in `/opt/node26` from the nodejs.org tarball; `npm install -g pnpm@10.32.1`; `pnpm -C web install --frozen-lockfile`; `dockerd` started if it isn't running; the test MongoDB as the brief gives it (`mongo:7.0`, `--replSet rs0`, `--ulimit nofile=20000:20000`, port 27017), with `KUMBU_TEST_MONGO` and `TESTCONTAINERS_RYUK_DISABLED=true`.
- [ ] **Step 4: Build.** `dotnet restore --locked-mode && dotnet build --configuration Release --no-restore`. Expected: 0 errors.
- [ ] **Step 5: The areas' test classes,** one class at a time (`dotnet test --project tests/… --configuration Release --no-build -- --filter-class "*ClassName"`), counts recorded for the report:
  - core (`tests/Kumbu.Core.Tests`): `FrontmatterTests`, `CalendarLayoutTests`, `CalendarReaderTests`, `CalendarServiceTests`, `CalendarTimeTests`, `EntryReaderTests`, `EntryStatesTests`, `ReadingCostTests`, `UpcomingTests`, `WhatsOnTests`, `ZonesTests`, `AliasResolutionTests`, `MentionsTests`, `NoteAliasesTests`, `AliasMoveTests`, `NoteMoverTests`, `NoteWriterTests`, `BlocksTests`, `CatTests`, `CatEmbedsTests`, `LinkCommandsTests`, `AliasLinkCommandsTests`, `CheckpointTests`, `CalendarCommandTests`, `FlagServiceTests`, `FlagTagsTests`, `FlagsTests`;
  - server (`tests/Kumbu.Server.Tests`): `ConsoleApiTests`, `CalendarApiTests`, `CalendarDiscreetTests`, `CalendarSettingsTests`, `CalendarWriteApiTests`, `HiddenVaultTests`, `FlagApiTests`, `RecentApiTests`, `AiLinkApiTests`, `AiLinkStoreTests`, `AliasPublishTests`, `ReaderApiTests`, `NoteApiTests`, `HistoryApiTests`, `HttpMetricsTests`, `SkillUpdateCheckTests`;
  - web: `pnpm -C web test --run src/reader src/calendar src/flags src/recent src/discreet src/note src/shell src/upload src/api`.

  Expected: every class green. A class from the brief's flaky list that fails is run alone three times and recorded.
- [ ] **Step 6: The Factory card** ticked for Task 0, with a log line.

### Task 0a: The plan's review

- [ ] **Step 1:** A fresh subagent gets the spec, the brief and this plan (not the controller's conclusions) and reviews the plan against them.
- [ ] **Step 2:** Its findings are folded in and recorded for the report.
- [ ] **Step 3:** Commit and push the plan: `git add docs/superpowers/plans/2026-11-14-security-audit.md`, message "Plan the security review of 0.5.0". This is the only public commit until phase 4.

---

## Phase 1: the area reviews

Tasks 1 to 8 share one procedure. Each reviewer is a fresh subagent in its own worktree (with its own `pnpm -C web install --frozen-lockfile` and Release build first) and, where it runs server tests, its own MongoDB container on its own port. Reviewers read in parallel; each runs its server test classes one at a time and never a whole suite, because the machine's memory is shared.

- [ ] **Step 1: Read** the spec's §1 to §3, the area's design spec and the security review in its report, and the parts of `docs/security-architecture.md` the task names.
- [ ] **Step 2: Model** the area: what is protected, the entry points new since 0.4.0 (every endpoint, command, page, background job), which of the spec's trust boundaries each one touches, and what the design promises.
- [ ] **Step 3: Review** every entry point against the classes above, reading `git diff v0.4.0..v0.5.0-pre.1` for the area's files and the checks the new code calls into.
- [ ] **Step 4: Show** each candidate by a behaviour test with a positive control (xUnit or Vitest, run locally, Release for timings) or by a precise trace with file and line. Tests assert the behaviour the code should have.
- [ ] **Step 5: Report** to the controller, in the finding record's fields: findings, suspicions with what would show them, and what was checked and found sound. Nothing is committed.

The order, by how much a weakness would matter (brief, open point 2): A, F, E, D, G, B, C. Reviewers run in parallel as the machine allows, started in that order.

### Task 1: Area A, the AI link

**Scope:** `src/Kumbu.Server/Publishing/` (`AiLinkApi.cs`, `AiLinks.cs`, `PublicationStore.cs`, `ReaderApi.cs`), `web/src/reader/` (the reader page's handling of the link), `web/s.html`, the rate limits in `src/Kumbu.Server/Api/Security.cs`, `Program.cs`'s routing and registrations of the new paths. Specs: `2026-11-11-ai-link-design.md`, `2026-11-11-ai-link-report.md`; security architecture "Published notes" (§3) and §7.

**Boundaries and what to check:** someone without an account who holds an AI link or a published link reads that copy and nothing more. The token (its entropy, how it is stored and compared), what the anonymous answers carry (headers, caching, content type, anything beyond the copy), what happens when the link is removed, the owner is deactivated or publishing is switched off, the rate limits on the anonymous path, who may create, list and remove a link (role, vault-limited credentials), and what the answers tell about notes and vaults that exist.

### Task 2: Area F, discreet vaults across the new views

**Scope:** every view and answer new since 0.4.0 that speaks of more than one vault: Recent changes (`Api/RecentApi.cs`, `web/src/recent/`), What's on and the calendar (`Api/CalendarApi.cs`, `web/src/calendar/`, the badges and `useBadge.ts`, the OS app-icon badge), the tree's new fields (`Api/NoteApi.cs`), the flag filter's counts (`Api/FlagApi.cs`, `web/src/flags/`), the AI link, mentions and aliases across vaults; `web/src/discreet/`, `Vaults/VaultCatalog.cs`, `web/src/routes/SignIn.tsx` (what is cleared at sign-out), `web/src/state/editor.ts`, `web/src/shell/` (`TreeDrawer.tsx`, `VaultSwitcher.tsx`), `web/src/router.tsx`. Specs: the discreet-vaults design and report of 2026-11-09, each feature's spec; security architecture §2 and the discreet-vault parts of §4 and §6.

**Boundaries and what to check:** a person looking over the shoulder of a user with discreet vaults sees nothing of a hidden vault. For each new endpoint that names a vault: a hidden vault answers as a name the user never had. For each answer about all vaults: nothing of a hidden one (entries, counts, names, badges, errors). In the web app: what stays on screen, in caches, in the URL, in toasts and in the badge when the reveal ends, the session ends, or another session hides a vault. The decisions on purpose (agents, WebDAV and sync see discreet vaults; the PIN hides, it doesn't encrypt) are no findings.

### Task 3: Area E, flags, the explorer, Recent changes, Take photo

**Scope:** `Api/FlagApi.cs`, `Api/RecentApi.cs`, `Api/NoteApi.cs` (the tree), `src/Kumbu.Core/Tags/`, `web/src/flags/`, `web/src/recent/`, `web/src/shell/`, `web/src/upload/`, `web/src/api/` (`notes.ts`, `paths.ts`), `web/src/components/Menu.tsx`. Specs: `2026-11-14-flags-explorer-photo-design.md`, `2026-11-14-flags-explorer-report.md`; security architecture §4 and §6 (API keys, shares, connections).

**Boundaries and what to check:** a user without access sees nothing of the vault; a read-only member changes nothing; a credential limited to some vaults reaches only those, at its right. Every new endpoint and write against each role and credential kind; the flag write's path into the note (frontmatter and tag edits on untrusted note text, concurrent writes, version checks); what Recent changes and the tree tell about writers, notes and vaults the caller has no right to; the photo's name and upload path; request-forgery and origin checks on the writes. The decisions on purpose (the flag endpoints work for keys and connections; Recent changes shows writers as Activity does) are no findings.

### Task 4: Area D, the calendar

**Scope:** `src/Kumbu.Core/Calendar/`, `src/Kumbu.Core/Board/` (the frontmatter writes), `src/Kumbu.Core/Sh/Commands/CalendarCommand.cs`, `src/Kumbu.Server/Api/CalendarApi.cs`, `Accounts/CalendarSettings.cs`, `Accounts/AccountCallerZone.cs`, `Accounts/UserDocuments.cs` (how the calendar's settings are stored and sealed: the cryptography class applies), `Api/BoardApi.cs`, the calendar's registrations in `Program.cs`, `web/src/calendar/`. Specs: `2026-11-12-calendar-design.md`, `2026-11-13-calendar-report.md`; security architecture §4.

**Boundaries and what to check:** the calendar's writes (close, move, new entry, settings) against each role and credential kind; the frontmatter writer on untrusted YAML (what it keeps, what it can be made to overwrite, its cost on bounded oversized or nested input); the reading cost across many notes; time zones and dates from the request; what entries of vaults without access, or of the trash and archive, can show; request-forgery and origin checks; the points the calendar report lists as fixed.

### Task 5: Area G, cross-cutting

**Scope and what to check:**
- **The console's read-only promise for every command** (`Api/ConsoleApi.cs`, `src/Kumbu.Core/Sh/`, with `Shell.cs`'s read-only path for checkpoints, `CommandArgs.cs`, `Manual.cs`, `TagsCommand.cs` and `web/src/console/paths.ts`): list every command and every option that writes, and check each against the console's refusal, however the line is written (options first, `--`, quoting, pipes), as the calendar run's finding suggests.
- **Resource exhaustion in the new parsers:** YAML frontmatter, aliases, mentions, block references, with bounded oversized or deeply nested inputs up to the note size limit, in Release, each beside an ordinary input of the same size. (Mermaid sources are area B's.)
- **Dependencies and the build:** `dotnet list package --vulnerable --include-transitive`, `pnpm -C web audit`, `pnpm -C pdf audit`, including #191's minor updates (`pdf/package.json`, `web/package.json`, `Kumbu.Server.csproj`, `PdfAnalyses.cs`); each advisory rated by whether Kumbu² reaches the vulnerable code. The diff's changes to `Dockerfile`, `pdf/Dockerfile` (base-image digests) and `.github/workflows/*.yml` (action pins, what the new `:prerelease` tag publishes).
- **Logs, metrics, error texts:** every log line, metric label (`Metrics/KumbuMetrics.cs`) and error text added since 0.4.0, for secrets and for vault and note names.
- **Skills:** the diff of `src/Kumbu.Server/Skills/` (`SkillsApi.cs`, `SkillUpdatesApi.cs`, `SkillUpdates.cs`, `SkillUpdateTaker.cs`) and `web/src/skills/Installed.tsx`.
- **At the branch head:** `.dockerignore` and `web/e2e/` (`git diff 8f3f4b55..HEAD`), only for whether the suite can leak secrets or reach the image.

Specs: the read-only console plan and spec, security architecture §7.

### Task 6: Area B, rendering

**Scope:** `web/src/reader/mermaidRender.ts`, `MermaidDiagram.tsx` (the Mermaid path, `diagramAlt`, the source refusals), the rest of `web/src/reader/`, all of `web/src/note/` in the diff (`MarkdownView.tsx`, `NoteView.tsx`, `MarkdownEditor.tsx`, `AmbiguousLinkDialog.tsx`, `blocks.ts`, `wikiLinks.ts`, embeds, `UnlinkedMentions.tsx`, `BacklinksList.tsx`), `web/vite.config.ts` and `web/package.json` (how Mermaid is loaded), both CSPs (read, not changed). Specs: `2026-11-12-mermaid-and-parity-design.md` and its report; security architecture §7.

**Boundaries and what to check:** a writer's Markdown, frontmatter or Mermaid source can't act in the reader's session or make it send requests, in the note view, in embeds and on the anonymous reader page. The standard sanitiser test strings through every new renderer, each test asserting that the output is neutralised; Mermaid's configuration (security level, HTML labels, links, click handlers, themes and init directives from the source); what the refusals refuse and what bounds a source's size, nesting and render time; whether the CSPs still hold what §7 says with Mermaid on the page; embeds of embeds (depth and cycles).

### Task 7: Area C, linking

**Scope:** `src/Kumbu.Core/Links/`, `src/Kumbu.Core/Sections/`, `src/Kumbu.Core/Notes/` (the diff), `src/Kumbu.Core/Sh/Commands/` (`Cat.cs`, `Backlinks.cs`, `LinksCommand.cs`). Specs: `2026-11-12-aliases-design.md` and report, `2026-11-12-mermaid-and-parity-design.md` (embeds in `cat`, block references).

**Boundaries and what to check:** what an embed in `cat`, a block reference, an alias or an unlinked mention can pull in from a vault or note the caller has no right to (a limited key or connection, another member's vault, the trash, the archive, an earlier version or checkpoint); embed depth, cycles and output size; alias collisions and what a writer in a shared vault can make another member's link resolve to; what a move or an alias rewrite changes in notes the mover can't write; published copies and aliases.

### Task 8: The 0.4.0 findings at `v0.5.0-pre.1`

**Scope:** the private repository's `audits/2026-11-10-0.4.0.md` and its tests: the findings from `origin/audit/0-4-0`, the fix records and fixes from `origin/fixes/0-4-0`. For each 0.4.0 finding: is its fix in `v0.5.0-pre.1`, and does it still hold where 0.5.0's code touches the same place? The tests of `audits/2026-11-10-0.4.0/` are run in the reviewer's worktree where they still apply. A finding still open, or whose fix doesn't hold, is reported under its 0.4.0 id for the private file only; this task's results never appear in public beyond "the 0.4.0 findings were checked".

- [ ] **After Tasks 1 to 8:** the Factory card ticked for phase 1, with the candidates' count only.

---

## Phase 2: the second reviews

### Task 9: The independent second review

- [ ] **Step 1:** For every Medium-or-higher candidate and every Low, a second fresh subagent gets the claim (title, place, the behaviour claimed, the test if there is one) and the code in its own worktree, not the first reviewer's reasoning. It checks the claim against the code, runs or rewrites the test, and returns: confirmed, rated up, or refuted, with the severity and its two sentences. A Low may be confirmed, rated up or refuted, never rated down to anything else.
- [ ] **Step 2:** The controller settles each finding: where the two reviews differ on the level and either records a doubt towards the higher one, the higher one stands. Ids are given now: `S5-NN` and `L5-NN`. A Medium-or-higher finding refuted or rated down stays in the private file only, with the verdict. A Low rated up gets an `S5` id and goes to the private file only. A refuted Low gets no id and appears in the public report only in a count of refuted candidates.
- [ ] **Step 3: A High or Critical finding, as soon as its second review confirms it** (spec §6, not waiting for the phase's end): `audits/2026-11-14-0.5.0.md` created if it isn't there yet, the finding written into it in full, `audit/0-5-0` pushed and the private pull request opened now (Task 10 then completes it); `PushNotification` "Security review 0.5.0: a High (or Critical) finding, S5-NN, details in the private pull request"; the card moved to **Waiting for Chris** with the id, the severity and "Fix S5-NN now? Recommendation: yes, details in the private pull request"; then the review carries on.
- [ ] **Step 4:** The Factory card ticked for phase 2, with ids, severities and counts.

---

## Phase 3: the confidential findings

### Task 10: The private file and pull request

**Files (private repository, branch `audit/0-5-0`):**
- Create: `audits/2026-11-14-0.5.0.md`
- Create: `audits/2026-11-14-0.5.0/` (each test as a file that drops into `tests/…` or `web/src/…`, the folder named under its finding)

- [ ] **Step 1:** Write the file: how the findings were made; a summary table; each finding of Medium or higher in the finding record's fields; the 0.4.0 findings still open under their 0.4.0 ids; the Medium-or-higher candidates refuted or rated down, with the verdicts; an appendix of what each area checked and found sound. If nothing is Medium or higher: "none", and why that can be trusted.
- [ ] **Step 2:** Copy the tests from the reviewers' worktrees; check that the file names no secret.
- [ ] **Step 3:** Commit (files staged by name), `git push -u origin audit/0-5-0`, open the pull request "Security review of 0.5.0" from `audit/0-5-0` to `master`. Do not merge.
- [ ] **Step 4:** If the private repository can't be reached: nothing confidential is written anywhere public; the findings go in full into the session's final message, and the public report and pull request say only that this happened, with ids and severities.
- [ ] **Step 5:** The Factory card ticked for phase 3.

---

## Phase 4: the public report

### Task 11: The report, the open items and the pull request

**Files (public branch):**
- Create: `docs/superpowers/overnighter/2026-11-14-security-audit-report.md`
- Modify: `docs/superpowers/open-items.md` (a row with the public pull request's link and the findings by id only, with no detail of a weakness; and for a scope question without an id, "Security review 0.5.0: a scope question, see the private file")

- [ ] **Step 1:** Write the report: the environment (and what was installed); the plan review's findings and what was done with them; Task 0 with its counts; what was reviewed per area and how (the method, not what was found); the counts per severity (not per area); every Low finding in full; each confidential finding by id and severity only; the decisions made without Chris, each with the options seen and why this one; deviations, surprises and open points for Chris; both pull requests' links (the private one by its URL only).
- [ ] **Step 2:** Read the report and the open-items rows once more against the publication rule: no area, file, test name or timing that ties a confidential finding to a place.
- [ ] **Step 3:** (a) Open the public pull request "Security review of 0.5.0" from `audit/0-5-0-security` to `master` on the plan's commit, which is already pushed, with a first description that carries no findings; its link goes into the report and the row. (b) Commit the report and `open-items.md` together as the last commit (files staged by name; `git diff origin/audit/0-5-0-security --stat` before the push), and push. (c) Set the final description: counts per severity, the Low findings in full, the confidential findings by id and severity, **Decisions made without Chris**, ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- [ ] **Step 4:** Move the Factory card to Done with the public pull request's link; send a short `PushNotification`.

### Task 12: Do not merge

- [ ] Neither pull request is merged; nothing is tagged or deployed; no product code and no version changed. Check `git diff v0.5.0-pre.1..HEAD --stat -- src web pdf` shows only what #200 brought.

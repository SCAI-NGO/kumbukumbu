# Overnighter report: the security review of 0.7.0

Run of 2026-11-26 on the brief `docs/superpowers/overnighter/2026-11-26-security-audit-brief.md` and the spec `docs/superpowers/specs/2026-11-26-security-audit-0-7-0-design.md`; plan `docs/superpowers/plans/2026-11-26-security-audit.md`. Branch `audit/0-7-0-security`. The code reviewed is `master` at `2fee5ad2` against `v0.6.0`. Nothing was fixed, merged, tagged or deployed; no product code and no version changed.

This report is public. Findings of Medium or higher are named here only by id and severity; their details are in the private repository's pull request (link at the end). Sixteen Low findings are also named by id only, because describing them here would point towards a confidential finding or because a review wrote a doubt towards a higher rating (decision 6).

## 1. Environment

- A cloud container with 4 cores and 15 GB of memory, shared by every agent of the run.
- .NET SDK 10.0.404 (`global.json`: 10.0.100, roll forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `MSBUILDDISABLENODEREUSE=1`, `-nodeReuse:false`.
- Node 26.13.1 from `/opt/node26` and pnpm 10.32.1 were there. **Nothing was installed**, and no dependency was added to the project. The PDF sidecar was not installed; only its advisory lookup ran.
- Docker 29.9.3; `dockerd` was not running and was started. Test MongoDB `mongo:9` as a single-node replica set, as the brief gives it, with the capped log. One container served every reviewer; `TESTCONTAINERS_RYUK_DISABLED=true`. It was never restarted.
- **One heavy run at a time:** every build, test run and measurement of every agent took its turn under one file lock, with a 20-minute limit per command. In Task 0 the compiler's build server (`VBCSCompiler`), left behind by the first build, inherited the lock's file and held the lock; it was stopped, and from then on every heavy run went through a small wrapper that takes the lock with `flock -o`, which closes the lock's file before the command starts, and sets `UseSharedCompilation=false`. No process held the lock by accident after that. This is worth carrying into the next brief beside `MSBUILDDISABLENODEREUSE=1`.
- The superpowers skills were available. The plan follows the shape of `superpowers:writing-plans`; the reviews were run subagent-driven with the plain Agent tool: one fresh agent per first review, per second review, per further review, for the severity check and for the last check of this report. Each agent that ran a test did so in its own worktree of `2fee5ad2` outside the public clone (`/home/user/audit-scratch/<name>`), none of which was committed from, and each removed when its report was in the private clone.
- **The Factory board connector was not available** in the session (no `Kumbu_Factory` tool): there is no card. The pull requests and the notifications are the signal instead.
- Nothing ran against a real host. Every request went to the in-process test server, a process on the loopback interface inside the container, the test MongoDB or a fake; no AI provider and no model host was contacted. Outbound requests were the package restores, the read-only advisory lookups (`dotnet list package --vulnerable --include-transitive`, `pnpm audit` at the root, `pnpm -C pdf audit --ignore-workspace`), all of which worked behind the proxy, and reading this repository's pull requests.
- **No Playwright run of the product was made** (the plan allowed two). Where the plan had a browser in mind, tests in jsdom with the web app's own code were enough to show or refute the point. What a real browser would still add is said in the private file.

## 2. The plan and its review

The plan was reviewed by a fresh agent that got the spec, the brief and the plan, not the controller's conclusions. It found that all 244 product files of the diff fall under an area and that every file and test class the plan names exists at `2fee5ad2`, that the plan gives nothing away, and it raised twelve points. All were folded in before the plan was committed:

1. The rule for rating the browser's findings (spec §3) was missing: added.
2. Two rules of spec §4 on where a finding goes were missing: added.
3. Area I lacked the re-check of the earlier review's suspicions and the place of the 0.6.0 findings: added.
4. Splitting area A between two reviewers left twelve files and the crossings without one owner each: each assigned; the split is logged as a deviation (decision 1).
5. The draft pull request stood under the last phase: moved to straight after the plan's commit, with "until phase 5 the only public commit is the plan".
6. The baseline left some classes for the reviewers to name: named.
7. Details of a browser run, the faked clock and the sidecar were missing: added.
8. Smaller rules (the commit trailer, the form of a scope question, the Agent-tool fallback, the plan review itself): added.
9. A build per worktree for every reviewer would cost an hour of the lock: a reviewer builds only once it has a test to run.
10. Browser runs can hold the lock for an hour: at most two, decided by the controller.
11. The web baseline's larger folders: run once the lock is idle.
12. The severity check's budget: what happens if it doesn't reach a finding.

The plan was the only public commit before this report.

## 3. Task 0

| Step | Result on `2fee5ad2` |
|---|---|
| Restore (locked) and Release build | 0 errors, about one minute |
| Core, four classes (`NoteWriterTests`, `SimilarTests`, `SkillsCommandTests`, `SkillDirectoryTests`) | 191 tests, 0 skipped, 0 failed |
| Core, the namespaces `Graph`, `Search`, `Links` | 833 tests: 764 passed, 69 skipped (bound to the tokenizer), 0 failed |
| Server, seven classes (`MirrorSlotsTests`, `UserKeyRotationTests`, `CallerTests`, `BundleTests`, `PublicationsTests`, `SearchIndexTests`, `EncryptedNoteStoreTests`) | 260 tests: 259 passed, 1 skipped (`SearchIndexTests`, which needs the model), 0 failed |
| Server, eight `Api` classes (`GraphApiTests`, `HistoryApiTests`, `PasswordWindowTests`, `SimilarApiTests`, `StaticFilesTests`, `SyncHubReviewTests`, `SyncSocketTests`, `ConsoleApiTests`) | 295 tests, 0 skipped, 0 failed |
| Server, the namespace `Discreet` | 146 tests, 0 skipped, 0 failed |
| The Eidolon core (`pnpm -C packages/eidolon-core test`, with the boundary check) and its `build` | 32 files, 730 tests passed; the build passed |
| Web, `src/offline` and `sw` | 122 tests passed |
| Web, `src/graph`, `src/note`, `src/published`, `src/routes`, `src/search`, `src/shell`, `src/update`, `src/api` | 137 files, 2,286 tests passed |

No test failed, so none had to be re-run alone. Two batches first reported "zero tests ran": `--filter-class` and `--filter-namespace` don't combine in one call. They were run again split, as the table shows. The set-up, the build and the whole baseline took about 25 minutes, beside the first reviews. The full suites were not run: nothing was changed, and CI was green on `2fee5ad2`.

## 4. What was reviewed, and how

Nine areas, as the spec's §2 has them, A split in two:

- **A**, the offline copy in the browser: **A1** the sealed store, its keys and wraps, the KDF, the version counter, unlocking, the idle lock, sign-out and several users in one browser; **A2** reading, writing and syncing through the copy, the words search, attachments from the copy.
- **B**, the server side of copies: mirror slots, contacts, the wipe signals, the devices list, removing a copy, deactivation.
- **C**, the service worker and the shell.
- **D**, similarity, Similar notes and synthesis.
- **E**, the Published window and Compare.
- **F**, discreet vaults across the new views.
- **G**, resource use.
- **H**, cross-cutting: the security architecture against the code, dependencies and advisories, the Eidolon core's boundary, the tests' fast KDF, the console's read-only promise for `similar`, logs, metrics and error texts, the workflows, and a sweep of the diff for files no area covered (none).
- **I**, the earlier findings at this commit.

For each, one fresh reviewer:

1. **Read** the area's feature specs, the pull requests' descriptions, the ADRs and the parts of `docs/security-architecture.md` and of the offline design that make its promises, and the points earlier reviews had recorded.
2. **Modelled** the area: every entry point new or changed since `v0.6.0`, which of the spec's trust boundaries each touches, and what is promised.
3. **Reviewed every entry point** against the classes of the 0.4.0 spec §4 step 2.
4. **Showed each candidate** by a behaviour test that asserts what the code should do, beside a positive control, or by a trace with file and line. Measurements were taken in Release, beside a control, with the load average stated.
5. **Checked the crossings of features** it owned (the brief's open point 3) and said "checked" or "not reached" for each. All nine crossings were checked by their owners; the restore of a backup was reviewed against "Backups" and not run, as the brief says.

Every candidate then went to a **second fresh reviewer** who got the claim (title, place, the behaviour claimed, the test) and the code, not the first reviewer's reasoning or report, and who reproduced or rewrote the test, tried to disprove the claim and rated it independently. A Low could only be confirmed, rated up or refuted. Where a second reviewer was the first to show a point, or where a rating carried a doubt towards the next level, a **further reviewer** got the claim and the code only. Two more first reviewers then took **what the first round's reviewers said they had not reached** (decision 3), and their candidates had a second review like the others.

**The per-PR reviews.** The spec expects a security review in each pull request's description. None of the 41 squash commits carries such a section, and the descriptions on GitHub have none either; they record deferrals and fixes. One reviewer went through all of them and checked each point recorded as fixed or deferred at `2fee5ad2`. Nothing therefore counted as accepted by a per-PR review; what counted as decided were the ADRs and the specs.

**The severity check** of spec §3 was run as the plan describes it; its results are in the private file.

What each area checked and found sound is recorded in the private file, not here: a public list would show the confidential findings by its gaps. The same holds for area I's results.

**The check of this report against the publication rule.** Before the push, a fresh reviewer got this report, the pull request's text, the commit message and the row for the open items, together with the private file's summary, and read them as someone who wants to learn where the unfixed weaknesses are. What it raised and what was changed is in §8.

## 5. Counts per severity

| Severity | 0.7.0's findings | Older than 0.7.0, counted apart |
|---|---|---|
| Critical | 0 | 0 |
| High | 1 | 1 |
| Medium | 14 | 0 |
| Low | 21 | 1 |

No candidate was refuted by its second review; one suspicion handed from one reviewer to another was refuted by a test. Eight findings were rated up by one level by a later review, and for two a further review settled the rating. The second and further reviews corrected facts in most findings; the private file follows the corrected facts.

For each of the two Highs, spec §6 was used as soon as a review had rated it High: written in full into the private file, pushed, and a notification sent with the id only. By the release policy (spec §1) a High is fixed at once and Medium and up before 0.7.0. Which findings need a decision from Chris, and which lie in code older than 0.7.0, is in the private file.

Six Lows are described in §6. The other findings, by id and severity only:

| Id | Severity |
|---|---|
| S7-07 | Medium (doubt: High) |
| S7-08 | Medium (doubt: High) |
| S7-09 | Medium |
| S7-10 | Medium (doubt: Low) |
| S7-11 | Medium |
| S7-12 | Medium (doubt: Low) |
| S7-13 | Medium (doubt: Low) |
| S7-14 | Medium (doubt: Low) |
| S7-15 | Medium (doubt: Low) |
| S7-16 | Medium (doubt: Low) |
| S7-17 | Medium (doubt: Low) |
| S7-18 | Medium (doubt: Low) |
| S7-19 | High |
| S7-20 | Medium (doubt: Low) |
| S7-21 | High (doubt: Medium) |
| S7-22 | Medium (doubt: Low) |
| L7-10 | Low (doubt: Medium) |
| L7-11 | Low |
| L7-12 | Low (doubt: Medium) |
| L7-14 | Low |
| L7-15 | Low (doubt: Medium) |
| L7-17 | Low (doubt: Medium) |
| L7-20 | Low |
| L7-22 | Low (doubt: Medium) |
| L7-24 | Low |
| L7-25 | Low |
| L7-26 | Low |
| L7-27 | Low (doubt: Medium) |
| L7-28 | Low |
| L7-29 | Low |
| L7-30 | Low |
| L7-31 | Low (doubt: Medium) |

The ids go on from the last ones of the version's earlier scoped review, as the spec's §4 says.

## 6. The Low findings described in public

Each was confirmed by its second review. None was fixed.

### L7-13: A read-only member's unsaved text reaches his copy and the wire through the engine

- **Area:** A. **Severity: Low.** *Impact:* a conflict copy with his own text is written into a read-only member's offline copy, and an upload is sent for it; Kumbu² refuses it, so nothing reaches the vault. The copy then counts a change waiting that can never upload, and it is tried again every 15 minutes. *Reachability:* the copy's writer refuses him, and the Write button is off for a read-only vault, so it needs unsaved text in an open editor of a vault whose role was lowered while he wrote.
- **Trust boundary:** "a read-only member … writes nothing, not even into the copy" (spec §2). The server stays the gate.
- **Place:** `web/src/note/MarkdownEditor.tsx:116` and `web/src/offline/writing.ts:141-158` (the draft is registered whatever the vault's right); `packages/eidolon-core/src/store/vault.ts:482` and `:504`; `packages/eidolon-core/src/engine/engine.ts:842-873`.
- **Evidence:** a test of the Eidolon core that fails today: with a draft, the copy holds the note and a conflict copy, one upload is sent and one change waits; the control without a draft passes.
- **Second review:** confirmed with corrections (the conditions above; the refused change stays waiting). Low.
- **Proposed fix:** no draft for a vault the copy holds as read-only, so the download takes the path and the editor is offered the new text as online. The failing test is the proof.
- **Needs a decision from Chris:** no.

### L7-16: A `not-related` entry with `..` names a note of another vault

- **Area:** D. **Severity: Low.** *Impact:* a note can take itself out of `similar --unlinked` for a note in another vault of the reader, and a note in a shared vault can so hide one of the reader's own notes from that list; against "their `not-related` property hides suggestions in their vault only". Nothing is revealed: the entry is only parsed, so it tells nobody whether a path exists. *Reachability:* a writer of a shared vault who guesses the reader's name for the other vault and the note's path; only `sh similar --unlinked` over more than one vault is touched (Similar notes and the graph ask one vault).
- **Place:** `src/Kumbu.Core/Links/SuggestionService.cs:52-61` (the entry is joined to the vault's name and parsed; `src/Kumbu.Core/Paths/VaultPath.cs:62-70` resolves `..`), read at `SuggestionService.cs:43-47`.
- **Evidence:** two core tests that fail today, each beside a same-vault control that passes.
- **Second review:** confirmed, with the second direction above. Low.
- **Proposed fix:** drop an entry unless the parsed path's vault is the note's own. The two tests are the proof.
- **Needs a decision from Chris:** no.

### L7-18: Marking a note's published version raises the revision of a note written once without recording its creation

- **Area:** E. **Severity: Low.** *Impact:* no access and no disclosure, and never a wrong name. A note that had been written exactly once reads "created on or before …" for good after it is published; a note stored before lines were recorded, and still at its first revision, loses who wrote its lines. *Reachability:* only the vault's owner publishes, but it happens on every ordinary publication of a once-written note, and a bundle marks every note it copies.
- **Place:** `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs:211`; the readers of "revision 1" are `src/Kumbu.Core/Notes/NoteCreation.cs:10` and `src/Kumbu.Core/Notes/Authorship.cs:57`. A move, which raises the revision the same way, records both (`src/Kumbu.Core/Notes/NoteMover.cs:266-268`). ADR 0025 accepts the raised revision and says nothing of the creation or the line record.
- **Evidence:** two server tests that fail today, each beside a positive control.
- **Second review:** confirmed, with the scope of the second half narrowed as above. Low.
- **Proposed fix:** let the mark carry what a move carries: the creation and the line record.
- **Needs a decision from Chris:** no.

### L7-19: The security architecture doesn't state the browser's promises, and some of its 0.7.0 sentences don't hold

- **Area:** H. **Severity: Low**, documentation. No promise is missing that is kept nowhere else: each stands in the offline design §3a and §4 and in the ADRs. *Impact:* someone who reads `docs/security-architecture.md` to learn what a found laptop gives away gets the server's half of the offline mode.
- **The gap:** the "Offline mirror" entry in §6 still says it covers the server's part and "grows with" the browser's; it didn't. The row "Lost or stolen laptop" in §2 has no word of the offline copy, though the offline design §4.5 asks for it.
- **Sentences that don't hold as written:** "It is a background request" follows the sentence on renaming and reads as said of it; the background request is the contact. About ten line references in the entry have moved by a few lines, and those of the row "Version and updates" in §7 by more.
- **Second review:** confirmed with corrections. Low.
- **Proposed fix, the text's outline** (headings and a line each; the text itself is the fix run's):
  - §2, "Lost or stolen laptop": the offline copy in both columns.
  - §6, "Offline mirror", first paragraph: the entry covers the server and the browser.
  - *The copy in the browser*: one copy per user and browser, only where the user turned it on.
  - *What is sealed, and what the header keeps in the clear.*
  - *The password wrap*: Argon2id and its parameters, the limits on a header's parameters, no lockout offline, where it runs.
  - *Records can't be moved or rolled back unnoticed*: the additional data and the version counter of ADR 0007.
  - *The key's life in the tab, and the idle lock.*
  - *Several people in one browser* (ADRs 0029, 0033).
  - *Discreet vaults are never kept* (ADR 0013).
  - *A password change keeps the other devices' copies* (ADR 0008), and removing another device's copy (ADR 0009).
  - *The service worker checks what it serves* (ADR 0021), in §7.
  - *What it can't do.*
- **Needs a decision from Chris:** no.

### L7-21: The Eidolon core's boundary check can be passed

- **Area:** H. **Severity: Low**, hardening. *Impact:* none today: `packages/eidolon-core/src` holds no such line. A later change could bring DOM types or a file of `web/` into the core with `pnpm build` and `boundary.test.ts` both green.
- **Place:** `packages/eidolon-core/test/boundary.test.ts:25-31` (three patterns, all for quoted specifiers of `import` and `export`), `packages/eidolon-core/tsconfig.json`.
- **Evidence:** an experiment with the core's own compiler settings: a file that uses `document` fails to compile (the control); it compiles once one file starts with a triple-slash `reference lib="dom"` directive, and a second file without the directive then uses `localStorage` too; a template-literal dynamic import and a cast of `globalThis` give the test's patterns nothing to match.
- **Second review:** confirmed. Low.
- **Proposed fix:** refuse, in `src/`, any triple-slash reference, any `declare` outside `globals.d.ts`, `globalThis`, the `@ts-` comments, and an `import(` whose argument isn't a quoted literal; or walk the import and reference nodes with the compiler's API instead of matching text.
- **Needs a decision from Chris:** no.

### L7-23: A reference instance's host name in a test file

- **Area:** H. **Severity: Low**; a question on the repository's rule, not a weakness. `packages/eidolon-core/test/paths.test.ts`, moved in with the sync engine by #280, checks a URL parser with the host name of the maintainer's public reference instance, which a dozen older files under `docs/` also name. Nothing contacts it. `CLAUDE.md` says deployment-specific settings never go into the repository; it forbids naming customers and employers, and this is neither. No customer's or employer's name, no secret and no other deployment's host came with the diff.
- **Second review:** confirmed with that correction.
- **Proposed fix:** `https://kumbu.example` in the test, if Chris reads the rule so.
- **Needs a decision from Chris:** a small one.

## 7. Decisions made without Chris

1. **Area A was split between two first reviewers** (the store and its life; reading, writing and syncing through the copy), a deviation from "one fresh reviewer per area": the area is half the diff. Each file and each crossing had one owner.
2. **The order of the areas** was the brief's: A, B, F, C, then D, E, I, H, G as places freed up, at most four first reviewers at a time. Second and further reviewers ran beside them, so more than four agents were at work at times; the lock kept the heavy runs apart, and the container was never short of memory.
3. **Two further first reviewers** took what the first round's reviewers had written under "not reached", and went through every pull request's description for points recorded as fixed or deferred. The run was well inside its time, and an honest list of what wasn't reached is better short.
4. **Where a later review rated a finding higher than an earlier one, the higher rating stands**, as in the earlier audits. This raised eight findings by one level.
5. **Spec §6 was used twice**, for S7-19 and S7-21, each as soon as a review had rated it High: written in full into the private file, pushed, a notification sent with the id only; the private pull request was opened with the first. Two findings that three reviewers each rated Medium with a written doubt towards High (S7-07, S7-08) did not trigger §6, because no reviewer rated them High; they stand first in the private file's proposed order after the Highs.
6. **A Low stays out of this report** where a review wrote a doubt towards a higher rating, or where describing it would point to a confidential finding (spec §4). That holds for sixteen of the twenty-two; the last check before the push moved two of them there (§8).
7. **Findings in code older than 0.7.0** are reported and counted apart (the brief's open point 10); which they are is in the private file.
8. **The advisory lookups** ran as the brief's open point 7 says, all three behind the proxy without failure; nothing was upgraded. Their results are in the private file. The sidecar's audit read its own lock file (104 dependencies against 477 at the root).
9. **No browser run of the product**, as §1 says: none was needed for a finding to stand.
10. **L7-23 is kept as a Low** although its second review found it to be a question on the repository's rule and not a weakness: it is for Chris to say how the rule reads.
11. **No Factory card** could be made (the connector wasn't there); the first notification says so.

No question came up that could only be answered by changing the spec's scope; `docs/superpowers/open-items.md` gets the one row for this pull request.

## 8. Deviations and open points

- **The publication check** (§4) raised its points in two rounds; all were taken. Two Lows meant for this report were moved to the private file, and some method sentences, a column and some wording were cut back to what the spec, the brief and the plan already say.
- **Not reached**, across the areas, without detail: a real browser for the points the private file names; real devices and storage quotas (out of scope by the spec); timing differences on the new endpoints; some web components were read for their sinks and their diffs only, not line by line; the 0.6.0 audit's own tests were not re-run (its findings were checked by reading and by the product's tests). The private file has the full list per area.
- **Suspicions that could not be shown** are in the private file, each with what would show it.
- **CI** on this pull request: the plan's commit ran green. The report's commit changes docs only; if a check fails on it, it is rerun once, the result is recorded in the pull request, and nothing is changed.
- **For the next brief:** `flock -o` and `UseSharedCompilation=false` (§1); class and namespace filters in separate calls (§3); and the per-PR security reviews the spec builds on don't exist as sections of the pull requests (§4).

## 9. Links

- Public pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/348
- Private pull request, with every finding in full: https://github.com/SCAI-NGO/kumbukumbu-security/pull/14

Neither is to be merged by this run.

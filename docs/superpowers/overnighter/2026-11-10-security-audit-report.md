# Overnighter report: the security audit of 0.4.0

Run of 2026-11-10/11 on the brief `docs/superpowers/overnighter/2026-11-10-security-audit-brief.md` and the spec `docs/superpowers/specs/2026-11-10-security-audit-0-4-0-design.md`; plan `docs/superpowers/plans/2026-11-10-security-audit.md`. Branch `audit/0-4-0-security`, based on `master` at `9864ff7` (`v0.4.0-pre.5`). Nothing was merged, tagged or deployed, and the version is unchanged.

This report is public. Findings of Medium or higher are named here only by id, severity and whether they were fixed; their details are in the private repository's pull request (link at the end).

## 1. Environment

- A cloud container with 4 cores and 15 GB of memory, shared by every agent of the run.
- .NET SDK 10.0.404 (`global.json`: 10.0.100, roll forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Installed by hand:** Node 26.12.1 in `/opt/node26` (the image had Node 22.23.4), and pnpm 10.32.1 (`npm install -g pnpm@10.32.1`).
- arctic-m's tokenizer, fetched as CI does and checked against its SHA-256; `KUMBU_TOKENIZER` set.
- Docker 29.9.2 from the setup script. Test MongoDB `mongo:7.0` as a replica set with `--ulimit nofile=20000:20000` (the highest limit Docker accepts here), a fresh container per server group. `TESTCONTAINERS_RYUK_DISABLED=true`.
- Superpowers skills were available; the work was done subagent-driven: one fresh agent per area audit, per adversarial check, per fix, and per review, each in its own git worktree and, where needed, its own MongoDB on its own port.
- Nothing ran against a real host. Every request went to the in-process test server, the test MongoDB, a locally built PDF sidecar or a stub; Chromium (preinstalled, for Playwright) loaded only `setContent` pages with network requests aborted.

## 2. The plan and its review

The plan was written with `superpowers:writing-plans` and reviewed by a fresh agent that got the spec, the brief and the plan. Its twelve points, all folded in before the plan was committed:

1. F11: the top bar also showed the path (`MobileShell.tsx` passes the path to `TopBar`, which shows crumb and title): added to Task 10.
2. F11: the heading anchor was wrong (`NoteView.tsx:361`, not `:82`): corrected.
3. F11: the existing `errorText` tests needed a query client and seeded lists: written into Step 1.
4. F11: the loading rule had to be the same in every place: one rule, `!vaults.data` or not listed.
5. F11: "not yet loaded" was ambiguous: spelt as `!vaults.data` (a failed list counts too).
6. F11: the hook's reach to embeds and history sheets: named, and their tests run.
7. A Medium fix must be the only unpushed commit while it waits for its review: added as a global constraint.
8. Per-area "found sound" lists would point at confidential findings by their gaps: they go to the private record only; this report gives the method per area.
9. The decisions log may name nothing of a confidential finding: added.
10. The worktree command and the unreachable-repository rule spelt out.
11. Task 0 got its setup steps and the exact commands instead of "as in the brief".
12. Subagents may commit locally; a Task 0a for the plan review and its commit.

The plan was the only public commit of phases 1–3.

## 3. Task 0: planned and measured

| Suite | Planned (brief) | Measured on `9864ff7` |
|---|---|---|
| Build | green | 0 errors |
| Core | 2153 tests, 3 skipped | 2153 total, 3 skipped, 0 failed |
| Server | 3521 tests, 7 skipped, 0 failed | 3521 total, 7 skipped; 2 failed under load, both pass alone (below) |
| Web | 128 files, 2226 tests, build green | 128 files, 2226 passed, build green |
| PDF sidecar | passing | 9 files, 198 tests passed, build green |

- **The second server group** (Accounts, Sharing, Welcome together) killed its MongoDB: "Too many open files", a fatal WiredTiger assertion, at the 20,000-file limit, which Docker here won't raise. I ran that group namespace by namespace instead (Accounts 413, Sharing 353, Welcome 20), as the brief allows, and did the same in every later gate.
- **Two failures under load**, while the area auditors were running on the same cores:
  - `DavShareTests.The_expiry_sweep_retires_expired_shares` (`DavShareTests.cs:485`, `Assert.False`). The class alone, three times on its own MongoDB: 29/29, 29/29, 29/29.
  - `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault` (`HiddenVaultTests.cs:342`). The class alone, three times: 11/11, 11/11, 11/11.
  - Both are flaky under load, not regressions. Neither appeared in the later gates.
- Task 0 took about 35 minutes of wall clock rather than 15, mostly the first attempt's stuck second group and running beside the auditors.

## 4. What was audited, and how

Seven areas, in the brief's order of risk: **E** discreet vaults, **A** WebDAV and the storage quota, **B** skills, **C** ingesting attachments, **D** search and structural chunking, **F** rendering, **G** cross-cutting. For each, one fresh agent in its own worktree:

1. **Built a threat model** from the area's design spec, `docs/security-architecture.md` and the earlier security reviews (#152, #153, #179): the assets, the entry points (every new endpoint, MCP command, page and background job), the attackers of spec §2 who reach them, and what the design promises.
2. **Reviewed every entry point** against the classes of spec §4 step 2:
   - authentication and authorisation;
   - path handling and traversal;
   - injection: process arguments, XML, Markdown to HTML, MongoDB queries;
   - SSRF and outbound requests;
   - resource exhaustion: archives, images, PDFs, XML, large tables, PROPFIND depth, pathological Markdown;
   - races;
   - information leaks: existence, timing, error texts, logs;
   - CSRF and origin checks;
   - the CSP.
3. **Proved each candidate** with a test that fails because of it and asserts a positive control, or with a trace with file and line where no test was possible.

Area by area:

- **E:** a sweep of every endpoint that names a vault (hidden vault against a name never had), a sweep of every answer about all of a user's vaults, and the web app's state across hiding, reveals ending, sessions ending, and other sessions.
- **A:** shares against paths, rights, role changes and revocation; the credential outside `/dav/` and other credentials on it; XML, PROPFIND, MOVE/COPY destinations, locks; the quota on every write path, with concurrency.
- **B:** the anonymous `/k` page and its cookie; outbound requests and the archive guard; the handshake instructions; copy, move, add and updates.
- **C:** the sidecar's routes, limits and pools with hostile PDFs against a local sidecar (`poppler-utils` was not needed); the server's checks on the sidecar's answers; rights and quota of `pdfimages`' writes.
- **D:** time and memory of the chunker, the snippets and the tokenizer on pathological notes up to the 1 MiB limit; what search returns across credentials, members, discreet vaults and the trash.
- **F:** a hostile Markdown corpus through every renderer (note view, embeds, reader page, skill page, install dialog), KaTeX's commands under `trust: false`, footnote ids, the CSP.
- **G:** `dotnet list package --vulnerable --include-transitive`, `pnpm -C web audit`, `pnpm -C pdf audit`; every log line added since 0.3.2; metrics labels; response headers on the new routes; error texts; new defaults of `KumbuOptions`.

Every finding, and two suspicions with something measurable, then went to a **second fresh agent** given the claim and the code but not the auditor's reasoning, who tried to disprove it (spec §4 step 4). For a Low that would be described in public, that check could only keep it or rate it up.

## 5. Counts per severity

| Severity | Count | Fixed in this run |
|---|---|---|
| Critical | 0 | — |
| High | 3 | 0 (never in this run, spec §6) |
| Medium | 10 | 3 |
| Low | 3 | 2 |

One High is pre-existing (code unchanged since 0.3.2, outside the audit's scope) and is reported for Chris with its severity.

The confidential findings, by id:

| Id | Severity | Fixed in this run |
|---|---|---|
| S-01 | High | no |
| S-02 | High | no |
| S-03 | Medium | no |
| S-04 | Medium | no |
| S-05 | Medium | no |
| S-06 | Medium | yes |
| S-07 | Medium | no |
| S-08 | Medium | no |
| S-09 | Medium | yes |
| S-10 | Medium | no |
| S-11 | Medium | no |
| S-12 | Medium | yes |
| S-13 | High (pre-existing) | no |

Details of each, including the fixed ones, are in the confidential findings: S-01 to S-13, the private pull request below.

## 6. The Low findings

### L-01: Web dependencies with known advisories, none reachable in Kumbu²

- **Area:** G.
- **Severity: Low.** *Impact:* in the shipped product none; on a developer's or CI machine at most if a Vitest UI, API or browser-mode server were exposed, which the project never starts. *Exploitability:* every advisory needs a second weakness (a prototype-pollution gadget) or a configuration Kumbu² doesn't use (SSR hydration, a `trust` function, `vitest --ui`/`--api`).
- **Attacker:** a malicious note or link; for the dev tools, someone on the developer's network.
- **Proof (trace):**
  - `pnpm -C web audit`: 9 advisories (3 critical, 5 moderate, 1 low). `pnpm -C pdf audit`: none. `dotnet list package --vulnerable --include-transitive`: none in all four projects.
  - **react-router 6.30.6:** GHSA-wrjc-x8rr-h8h6 (open redirect through a backslash in `<Link>`/`useNavigate`) and GHSA-337j-9hxr-rhxg (`deserializeErrors` in SSR hydration). Every navigation target is a fixed prefix with encoded segments (`web/src/api/paths.ts`) except `next`, which goes through `safeNext` (`web/src/routes/SignIn.tsx:17-26`), tried with `/\evil.com`, `/\t/evil.com`, `/./\evil.com`, `/a/../\evil.com` (all `/`). Note links are plain `<a>`. `createBrowserRouter` gets no hydration data and nothing sets `__staticRouterHydrationData`.
  - **katex 0.16.47** (new in 0.4.0): GHSA-238p-pmpm-9mq7 (a polluted prototype can bypass `trust`). The options set `trust: false` as an own value (`web/src/note/math.ts:73`), and nothing in `web/src` deep-merges or touches `__proto__`.
  - **vitest 2.1.9, @vitest/mocker, tinypool 1.1.1:** GHSA-5xrq-8626-4rwp, GHSA-82fw-gwwq-j7x9, GHSA-qxg6-89mg-rqv8, GHSA-5gmw-xhrv-c9v3, GHSA-85c8-ppgw-ccpr. These are dev dependencies only: the image copies only `web/dist`, and the script is `vitest run`.
  - Only the KaTeX advisory came with 0.4.0; the other eight were in 0.3.2 already.
- **Adversarial check:** confirmed, stays Low.
- **Proposed fix:** upgrade react-router-dom to 7.18 or later (a major version), katex to 0.18.2 or later, and vitest to 4.1.12 or later, in a dependency pull request; then `pnpm -C web audit` shows none.
- **Needs a decision from Chris:** yes (major upgrades, and the router upgrade touches `neutralWindow`). **Fixed:** no (spec §8: no upgrades in this run).

### L-02: A link copy's updates kept reading the link after its owner was deactivated

- **Area:** B.
- **Severity: Low** (rated by its check). *Impact:* someone who had added a copy from a skill link kept reading the link's last snapshot (shown as diff hunks in Review, writable with Take) after the owner was deactivated or publishing was switched off, against security-architecture §4 "Skills" ("the owner's deactivation … end its links as they end note publications; `KUMBU_PUBLISH_ENABLED=false` stops sharing and reading them"). The snapshot cannot change once the owner is deactivated, so it is content the owner had published while active. *Exploitability:* an account, a copy added while the link was live, and the code sealed in the holder's own record; it reaches only that one link.
- **Attacker:** a holder of a skill link.
- **Proof:**
  - **Trace:** `SkillUpdateTaker.UpstreamAsync` (`src/Kumbu.Server/Skills/SkillUpdateTaker.cs:169-180`, used by Review and Take) and `SkillUpdates.LinkHashAsync` (`SkillUpdates.cs:129-140`, behind the update check) called `PublicationStore.OpenSkillAsync` directly. The checks for the switch and the owner were only in `SkillPageApi.OpenCodeAsync`.
  - **Test:** Alice shares, Dave adds, Alice updates the link, Alice is deactivated, and Dave's Review answers 200. The positive controls passed: Review 200 while she is active, `/api/k` 404 after.
- **Adversarial check:** confirmed, stays Low. Note publications are consistent; withdrawal and account deletion were already handled.
- **Fix:** commit `81dd66ae` "End a link copy's updates when the link's owner is deactivated" (L-02 in its body).
  - A new `UserCatalog.IsActiveAsync(userId)` (`Users.Find(u => u.Id == userId && u.Active).AnyAsync`).
  - `UpstreamAsync` throws the existing `SourceGone` and `LinkHashAsync` reports the source gone while the owner isn't active, exactly as after a withdrawal, without `OpenCodeAsync`'s rate limits.
  - `SkillUpdatesApi` and `SkillsApi.CheckUpdatesAsync` pass the catalog in.
  - **Not fixed:** what a copy should show while publishing is switched off. "Source gone" could mislead, since the switch can be turned on again, so that needs a decision (open items).
  - **Test** `SkillUpdateTakeTests.A_link_copy_gets_no_update_while_the_links_owner_is_deactivated`: while Alice is deactivated, Dave's Review and Take answer 404 type `gone` and his check reports `gone`. After she is reactivated, Review offers `examples/post.md` as `added` and the check reports `available`.
  - **Red** (before the fix): `Assert.Equal() Failure: Values differ / Expected: NotFound / Actual: OK` at the Review after deactivation.
  - **Green:** `SkillUpdateTakeTests` 13/13; `Kumbu.Server.Tests.Skills` 340/340; the full gate below.
  - **Mutations:**
    1. Owner check out of `UpstreamAsync` (replaced by a no-op so that it compiles): Review assertion `Expected: NotFound / Actual: OK`.
    2. Out of `LinkHashAsync`: check assertion `Expected: "gone" / Actual: "available"`.
    3. Inverted in both: the deactivated case fails first, `Expected: NotFound / Actual: OK`.
    4. Always gone: the reactivation case fails in `ReviewAsync`, `Expected: OK / Actual: NotFound`.
  - **Reviews:** a spec-compliance review (nothing to fix; asked for Take to be asserted too, done) and a code-quality review (nothing to fix; the `IsActiveAsync` helper, the admin made through `RestClient.NewAsync(..., role: Roles.Admin)`, a shared test setup, done). The skills design §3.10 still names only withdrawn links and vanished repositories as "source gone"; I left it unchanged, since specs outside this topic stay as they are.

### L-03: The security architecture didn't describe the sidecar's extraction surface

- **Area:** C.
- **Severity: Low.** *Impact:* none directly; the security document under-described what untrusted PDFs reach, misleading reviewers and operators. *Exploitability:* none.
- **Proof (trace):**
  - §7 "The PDF sidecar" named four routes and "two workers and a queue of 16".
  - #176 added `POST /images/list`, `/images/image` and `/images/region` (`pdf/src/server.ts:127-152`), a pool of one worker with a queue of 2 under a gate of two jobs shared with the main pool (`server.ts:264-267`, `pdf/src/pool.ts:42-70`), a 30 s extraction limit (`server.ts:25-27`) and the server's one-call-per-account `ExtractionGate`. #176 didn't touch the document.
  - Several anchors in §7 had drifted, among them `Program.cs`'s.
- **Adversarial check:** confirmed, stays Low. None of the inaccuracies hides a weakness: the budget of two heaps still holds because of the gate.
- **Fix:** commit `16ec8538` "Describe the sidecar's extraction routes and limits in the security architecture" (L-03 in its body), `docs/security-architecture.md` §7:
  - the three routes;
  - their pool and the shared gate (at most two jobs, the two heaps the memory is budgeted for);
  - the 30 s extraction limit;
  - the output limits (8 MP, 16,384 px, regions at 300 dpi within them, 15 MiB, 10,000 placements a page);
  - that pdf.js's output is swallowed during extraction;
  - the server's one call per account (a second is refused) and its 95 s wait (a full queue's two jobs and its own, 30 s each, plus 5 s);
  - the connection limit (both pools' capacities plus 16);
  - every anchor refreshed.

  No test: it is documentation. **Review:** one reviewer checked every number and every anchor against the code. Applied: three stale `Program.cs` anchors, the first bullet's anchors, and wording.

## 7. F11 and the fixes

### F11: a 404 for a note in a vault the list doesn't name

- **Commit:** `d6827983` "Say a note is missing without its path when its vault isn't in the list" (F11 in its body).
- **What it does:** every 404 for a note in a vault the user's vault list doesn't name now reads "There is no such note (if it was removed, it is in the trash)." (`NO_SUCH_NOTE`), and shows no heading made from the path, neither in the note view nor in the top bar. A 404 in a vault the list names keeps today's text and heading.
  - The rule comes from the vaults query the app already has, read passively: no new request. A list still loading, or failed, counts as not naming the vault.
  - **Files:**
    - `web/src/discreet/errorText.ts`: `useUnnamedVault(path)`; `useErrorTextFor` returns `NO_SUCH_NOTE` for a 404 when the vault is discreet or unnamed.
    - `web/src/api/notes.ts`: `useVaults({ passive })`, an observer that never fetches by itself; and `useNoteMissing(path)`, which reads the note query's state from the cache, batched as `useIsFetching` is.
    - `web/src/api/error-text.ts`: `isNotFound`.
    - `web/src/note/NoteView.tsx`: no path heading for such a 404.
    - `web/src/shell/MobileShell.tsx`: no path in the top bar for such a 404.
  - Embeds (`MarkdownView`) and the history sheets get the same rule.
- **Tests:**
  - `web/src/discreet/errorText.test.tsx`: a hidden vault after a reload, a named vault, a 500, the list loading, and that nothing is asked of the server.
  - `web/src/note/NoteView.test.tsx`: no heading for an unnamed vault, also while the list loads; the heading `a` stays for `/Home/a.md`.
  - `web/src/shell/MobileShell.test.tsx`: neither `Tagebuch` nor `Heute` anywhere on the page; the crumb and title stay for a vault the list names.
- **Red** (before), `pnpm -C web test --run src/discreet/errorText.test.tsx src/note/NoteView.test.tsx src/shell/MobileShell.test.tsx`: "Test Files 3 failed (3); Tests 5 failed | 104 passed (109)".
  - The text tests: "Expected "There is no such note (if it was removed, it is in the trash)." Received "There is no note at /Tagebuch/Heute.md (if it was removed, it is in the trash).""
  - The view and shell tests: "Unable to find an element with the text: There is no such note (if it was removed, it is in the trash)."
- **Green:** the five files 205 passed; the whole web suite "Test Files 128 passed (128), Tests 2236 passed (2236)"; `pnpm -C web build` "✓ built in 5.17s" (my own run before the push).
- **Mutations**, each alone:
  1. Without the unnamed term: 5 failed, among them the hidden-vault text test ("Received "There is no note at /Tagebuch/Heute.md…"").
  2. Without the loading term: 2 failed (the loading text test, and the view's loading test).
  3. Membership inverted: 8 failed, among them "is the server's sentence for a 404 in a vault the list names" (Expected "There is no note at /Home/a.md.").
  4. Without the heading guard: 3 failed (`expect(element).not.toBeInTheDocument()`; shell "…not to contain 'Heute'").
  5. Without the top-bar guard: 1 failed (the shell test, "…not to contain 'Tagebuch'").
  6. With a plain observer instead of the passive one: "expected "spy" to not be called at all, but actually been called 1 times".
- **Reviews:**
  - **Spec compliance:** meets F11. It asked for a passive vaults reader (the first version's observer refetched the list for every embed and history sheet) and for a shell-level positive control.
  - **Code quality:** must-fix, the cache subscription needed `notifyManager.batchCalls` (it caused a "Cannot update a component while rendering a different component" warning); also asked for `isNotFound` beside `isVaultBusy` and for one rule in `NoteView`.
  - All applied before the push.
- **Decisions:**
  - A list loading or failed counts as unnamed, the spec's wording. A 404 that arrives before the list reads `NO_SUCH_NOTE` without a heading for that moment, for every user.
  - A vault the user lost or never had also gets the neutral sentence.
  - The checkpoint route is not included. A checkpoint note's 404 can only come for a vault whose tree loaded, and the page mounts no vaults query, so including it would add a request. Its tree's 404 says "There is no vault 'X'.", the name from the URL.
  - While the note itself is still loading, the heading and top bar show the path from the address bar, as before.

### The other fixes

The fixes of confidential findings, their tests, gate output, mutations and reviews are recorded in the confidential findings only (spec §5).

Every fix followed the plan's Task 11:
1. a fresh agent wrote the behaviour test, saw it fail, fixed and committed, and ran every mutation;
2. fresh agents reviewed spec compliance and code quality, and for a confidential finding a further one did the fix review of spec §6;
3. then the gate ran, and only then the push.

Some fixes were made and reviewed but dropped before they were pushed, because their review found a visible change beyond the fault or a new problem; they are recorded for Chris.

**Gates.** The final gate is the last full gate, run on this branch's final code (`ed399c77`, the report being documentation), as CI runs it:

| Suite | Result |
|---|---|
| Restore and build | 0 errors |
| Core | 2163 total, 3 skipped, 0 failed |
| Server | 836 + 413 + 353 + 20 + 768 + 1132 = 3522 total, 7 skipped; 1 failed under load (below) |
| Web | 128 files, 2242 tests passed; `pnpm -C web build` "✓ built in 5.23s" |
| PDF sidecar | 9 files, 198 tests passed; build green |

- The one failure: `DavParkingTests.Every_junk_pattern_is_parked_and_never_reaches_mongodb(name: ".gitignore")`, "Dictionaries differ" at `DavParkingTests.cs:115`. It counts every collection's documents, so another test's write between its two counts fails it. The class alone, three times: 51/51, 51/51, 51/51.
- The two earlier full gates of the run were green with no failures: 3521 and 3521 server tests (core 2159 and 2163; web 128 files and 2236 tests).
- The counts grew by the new tests: core +10, server +1, web +16 over Task 0.

## 8. Decisions made without Chris

1. **The second server group run namespace by namespace.** Its MongoDB died at the 20,000-file limit, which Docker here won't raise.
   - Options: keep the group, raise the limit (refused), or split it as the brief allows.
   - Chosen: split it.
2. **When in doubt, the higher level.** Where an auditor or a checker recorded a doubt towards the higher level, I kept the higher one (spec §3). Where a checker rated a finding up, the higher rating stands.
3. **A duplicate of an open item is no new finding.** One candidate turned out to be an open item's limit (the sidecar's per-page memory, 2026-11-08). What the open item lacks is recorded in the private record.
4. **A documented limit is no finding.**
   - One candidate was a limit security-architecture §4 already documents for keys, and the share's row says "As an API key": a writer can push a note's older versions out of its 10-version history.
   - Suggestion, not done: add the same caveat to the share's row in §4.
5. **A pre-existing weakness found on the way is reported, not counted as 0.4.0's.** It is outside the audit's scope (spec §2), so it is reported for Chris with its severity.
6. **L-02 fixes only the deactivated owner.**
   - Options: also the publishing switch, or neither.
   - Chosen: only the owner. "Source gone" could mislead while the switch is off, and the alternative is a new answer, so that part needs a decision.
7. **The skills design §3.10 is not updated** for L-02. Specs outside this topic stay as they are.
8. **F11's rule:**
   - a list loading or failed counts as not naming the vault;
   - the checkpoint route is left out (no request may be added, and its note 404s come only for vaults whose tree loaded);
   - the path still shows while the note loads.
9. **Fixes that weren't small, or that changed something visible beyond the fault, were not made.** Where one had been made, a failed review dropped it unpushed. I did not adjust a fix's scope to get it through.
10. **One reviewer for small changes.** For changes of one rule or one function, spec compliance and code quality went to one reviewer. Every fix of a confidential finding still got its own fix review.

## 9. Deviations, surprises, open points

- **Task 0 took about 35 minutes.** The cause was the second server group's MongoDB, which died at the open-files limit, and running beside seven auditors.
- **Fixes ran in parallel.** To use the night, fixes were implemented in parallel in separate worktrees on local branches and cherry-picked onto the branch one at a time. Only a fix whose reviews had passed was cherry-picked, so a confidential fix was never pushed before its review. The cherry-picked commits keep their messages; their hashes differ from those in the agents' notes.
- **Two agent briefs were stopped by the session's safety classifier while being written.** Each time the work was re-briefed or, for one fix, left to the fix overnighter. The confidential findings say which.
- **The plan named `src/note/HistorySheet.test.tsx`;** the history sheets' tests are in `src/shell/HistorySheets.test.tsx`, which was run instead.
- **Flaky under load**, each passing alone three times:
  - `DavShareTests.The_expiry_sweep_retires_expired_shares`;
  - `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault`;
  - `DavParkingTests.Every_junk_pattern_is_parked_and_never_reaches_mongodb`, which counts all collections;
  - `SkillPage.test.tsx`'s zip tests, under heavy load in one worktree.

  They join the open item on tests that fail under load.
- **Open points for Chris:**
  - L-01's upgrades;
  - L-02's publishing switch;
  - the confidential findings that need a decision or the fix overnighter (by id in `docs/superpowers/open-items.md`).

## 10. Hand-test checklist

On a server running this branch (or a pre-release tag of it), with a user who has a PIN and the vaults `Work` and `Tagebuch`, `Tagebuch` marked discreet.

**F11**

1. Reveal (PIN), open a note in `Tagebuch` (e.g. `Heute`), copy the address bar's real path from the note's menu (or type `/notes/Tagebuch/Heute.md`), hide again.
2. Open a new tab with `/notes/Tagebuch/Heute.md` and reload it. **Pass:** the page says "There is no such note (if it was removed, it is in the trash).", no heading "Heute", and the top bar shows neither "Tagebuch" nor "Heute".
3. In the same tab open `/notes/Work/fehlt.md` (a note that doesn't exist). **Pass:** "There is no note at /Work/fehlt.md (if it was removed, it is in the trash).", with the heading "fehlt" and the top bar naming Work, as before.
4. Delete a note in `Work`, then open its address. **Pass:** today's text, with its moved-to or trash sentence, unchanged.
5. With a user without a PIN or discreet vaults, open a missing note in an own vault. **Pass:** nothing differs from 0.4.0-pre.5.

**L-02**

6. As Alice, share a skill by link; as Dave (another user), open the link and add it; as Alice, change the skill and update the link.
7. As an admin, deactivate Alice. As Dave, open Skills → Installed. **Pass:** the copy shows "source gone"; Review offers nothing.
8. Reactivate Alice. **Pass:** Dave's copy offers the update again.

Hand tests for the fixes of confidential findings are in the confidential findings.

## 11. Pull requests

- Public: https://github.com/SCAI-NGO/kumbukumbu/pull/185
- Private (opens for Chris alone): https://github.com/SCAI-NGO/kumbukumbu-security/pull/1

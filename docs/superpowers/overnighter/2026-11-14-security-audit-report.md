# Overnighter report: the security review of 0.5.0

Run of 2026-11-14 on the brief `docs/superpowers/overnighter/2026-11-14-security-audit-brief.md` and the spec `docs/superpowers/specs/2026-11-14-security-audit-0-5-0-design.md`; plan `docs/superpowers/plans/2026-11-14-security-audit.md`. Branch `audit/0-5-0-security`. The code reviewed is `v0.5.0-pre.1` (`8f3f4b55`) against `v0.4.0`; the end-to-end suite and `.dockerignore` were read at the branch head. Nothing was fixed, merged, tagged or deployed; no product code and no version changed.

This report is public. Findings of Medium or higher are named here only by id and severity; their details are in the private repository's pull request (link at the end). Five Low findings are also named by id only, because describing them here would point towards a confidential finding (decision 9).

## 1. Environment

- A cloud container with 4 cores and 15 GB of memory, shared by every agent of the run.
- .NET SDK 10.0.404 (`global.json`: 10.0.100, roll forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Installed by hand:** Node 26.13.1 in `/opt/node26` from the nodejs.org tarball (the image had Node 22.23.4), and pnpm 10.32.1 (`npm install -g pnpm@10.32.1`). Nothing else was installed; `poppler-utils` was not needed. No dependency was added to the project.
- Docker 29.9.2; `dockerd` was not running and was started. Test MongoDB `mongo:7.0` as a single-node replica set with `--ulimit nofile=20000:20000`: one container for the baseline and one per reviewer that ran server tests, each on its own port. `TESTCONTAINERS_RYUK_DISABLED=true`.
- The superpowers skills were available. The plan was written with `superpowers:writing-plans`. The reviews were run subagent-driven with the plain Agent tool: one fresh agent per area review and per second review, each in its own worktree of `v0.5.0-pre.1` outside the public clone (`/home/user/audit-run/<area>`), none of which was committed from.
- The Factory board connector was available; the card "Security review 0.5.0" carried the phases and a log.
- Nothing ran against a real host. Every request went to the in-process test server, a test MongoDB or a stub; Chromium (preinstalled, for Playwright) loaded only `setContent` pages with every network request aborted. Outbound requests were the read-only advisory lookups (`pnpm audit` against the npm registry, `dotnet list package --vulnerable` against NuGet; both worked), the package restores, and one download of arctic-m's `tokenizer.json` from the URL CI uses, checked against its SHA-256, by the reviewer who re-ran the 0.4.0 audit's tests.

## 2. The plan and its review

The plan was reviewed by a fresh agent that got the spec, the brief and the plan. It found that every file and test class the plan names exists at the tag and that the plan names no finding, and it raised eleven points. All were folded in before the plan was committed, the fifth in part:

1. No step created the Factory card: added as Task 0's first step.
2. The step for a High or Critical finding spoke of a private pull request that only a later task opened: it now creates the file, pushes and opens the pull request at once.
3. Scope gaps: files of the diff that no task covered (the checkpoint's read-only path in `Shell.cs`, `UserDocuments.cs`, `BoardApi.cs`, `SignIn.tsx`, `web/src/api/`, `Installed.tsx`, the Dockerfiles and workflows, and others) were added to their areas.
4. Task 0 left out changed test classes of the areas under review: added, and the classes named one by one.
5. Parallel server test runs against the brief's "one class at a time": each reviewer runs one class at a time and never a whole suite, on its own MongoDB. Not adopted: serialising test runs across all worktrees, which the separate containers made unnecessary.
6. The 0.4.0 findings' source branches were named imprecisely: corrected.
7. The step that opens the public pull request was garbled: rewritten in three parts.
8. The `open-items.md` rows went beyond the brief: ids only.
9. Nothing kept the plan from being touched in public during phases 1 to 3: added as a constraint.
10. What happens to a refuted Low, and to a Low rated up, was not said: added.
11. Items of the brief the plan omitted (no secrets in reports, installed tools noted, what to do without skills or notifications, Task 0's 15 minutes): added.

The plan was the only public commit of phases 1 to 3.

## 3. Task 0

| Step | Result on `v0.5.0-pre.1` (the branch head adds only test code and docs) |
|---|---|
| Restore (locked) and Release build | 0 errors |
| Core, 27 classes of the areas, one at a time | 776 tests, 1 skipped (`ReadingCostTests`), 0 failed |
| Server, 16 classes of the areas, one at a time | 278 tests, 2 skipped, 0 failed |
| Web, the areas' folders (`src/reader`, `calendar`, `flags`, `recent`, `discreet`, `note`, `shell`, `upload`, `api`) | 99 files, 1384 tests passed |

None of the classes on the brief's flaky list failed (`HiddenVaultTests` 11 of 11). Task 0 took about 15 minutes, the setup included. The full suites were not run: nothing was changed, and the full suite was green in CI on #200's merge.

## 4. What was reviewed, and how

Seven areas, as the spec's §2 has them, and one further task:

- **A**, the AI link: the anonymous read path, its token, its ends and its limits.
- **B**, rendering: Mermaid, embeds and block references in the note view, the reader page, both Content Security Policies.
- **C**, linking: embeds in `cat`, block references, unlinked mentions, aliases, moves.
- **D**, the calendar: its reads and writes, the frontmatter writer, settings and time zones.
- **E**, flags, the explorer, Recent changes and Take photo.
- **F**, discreet vaults across every new view.
- **G**, cross-cutting: the console's read-only promise for every command, resource use of the new parsers, dependencies and the build, logs, metrics and error texts, the skills diff, and at the branch head `.dockerignore` and the end-to-end suite.
- **The 0.4.0 findings:** whether each fix of the 0.4.0 audit is in `v0.5.0-pre.1` and still holds where 0.5.0's code touches the same place, with the 0.4.0 audit's own tests re-run where they still apply.

For each, one fresh reviewer in its own worktree:

1. **Read** the area's design spec, the security review in its overnighter report, and the parts of `docs/security-architecture.md` that make its promises; and so knew which points earlier reviews had accepted, and which they had recorded as fixed.
2. **Modelled** the area: the entry points new since 0.4.0 (every endpoint, command, page and background job), which of the spec's trust boundaries each one touches, and what the design promises.
3. **Reviewed every entry point** against the classes of the 0.4.0 spec §4 step 2: authentication and authorisation for every role and credential kind; path handling; injection (Markdown to HTML, YAML, MongoDB queries); outbound requests; resource use on bounded oversized and deeply nested inputs; races; information leaks (existence, error texts, logs); cryptography where a new key was derived; request forgery and origin checks; the CSP.
4. **Showed each candidate** by a behaviour test that asserts what the code should do, beside a positive control (the same request by the caller who should succeed, or an ordinary input of the same size), or by a trace with file and line. Any measurement was taken in Release, beside a control.

How each area went about it:

- **A:** the anonymous answers header by header and byte by byte against what the copy may hold; the token's making, storing and finding; every way a link ends; who may make, list and remove one; the making request's origin check; odd inputs to the Markdown writer.
- **B:** the real Mermaid, bundled with the app's own render module, in a local Chromium page with every request aborted and recorded: the standard sanitiser test strings as diagram text across the diagram types, configuration directives from the source, links and click directives, CSS and external references; the note view's and the embeds' output for the same strings; embeds of embeds.
- **C:** embeds, block references, aliases and mentions against a key limited to one vault, another user's vault, the archive and a read-only key; the resolver's rules by trace.
- **D:** every calendar read and write, over the API and over `sh calendar`, for a user without access, a limited key, a read key, a read-only member and a foreign origin; zone names and far dates; the frontmatter writer on odd YAML; how frontmatter text reaches the web calendar.
- **E:** the flag read and write for every role and credential kind and a foreign origin; a generated-frontmatter property test of what a flag write may change; Recent changes' and the tree's answers for vaults, the trash and the archive; the photo's name.
- **F:** every new endpoint and every new view, for a session with its discreet vaults hidden, against the discreet design's promises.
- **G:** 137 spellings of every write of every shell command against the console, compared by the vault's state, with the same writes over MCP as controls; the new parsers; `dotnet list package --vulnerable --include-transitive`, `pnpm -C web audit`, `pnpm -C pdf audit`; every log line, metric label and error text added since 0.4.0; the workflows' pins and permissions; the end-to-end suite's secrets, traces, screenshots and its reach into the image.

Every candidate, and every suspicion with something measurable, then went to a **second fresh reviewer** who got the claim (title, place, the behaviour claimed, the test) and the code, not the first reviewer's reasoning, and who checked the claim, re-ran or rewrote the test, and rated it independently. A Low could only be confirmed, rated up or refuted. Some findings got a third reviewer or an additional check of their severity (decisions 5, 7 and 8).

What each area checked and found sound is recorded in the private file, not here: a public list would show the confidential findings by its gaps.

## 5. Counts per severity

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 0 |
| Medium | 10 |
| Low | 10 |

No candidate was refuted. One of the Medium findings (S5-10) lies outside the review's scope and is reported for Chris with its severity. For two of the Medium findings (S5-01, S5-02) a doubt towards High is recorded in the private file, with what would settle it (decision 5); for most of the others a doubt towards Low is recorded there. Because no High was confirmed, spec §6 was not used.

| Id | Severity | Needs a decision from Chris |
|---|---|---|
| S5-01 | Medium (doubt: High) | no |
| S5-02 | Medium (doubt: High) | no |
| S5-03 | Medium | no |
| S5-04 | Medium | a small one |
| S5-05 | Medium | no |
| S5-06 | Medium | yes |
| S5-07 | Medium | no |
| S5-08 | Medium | one point |
| S5-09 | Medium | one point |
| S5-10 | Medium (outside the scope) | no |
| L5-06 | Low | yes |
| L5-07 | Low | no |
| L5-08 | Low | no |
| L5-09 | Low | no |
| L5-10 | Low | no |

Details of each are in the confidential findings, the private pull request below. By the release policy (spec §1), Medium and up is fixed before 0.5.0.

**The 0.4.0 findings.** Every fix of the 0.4.0 audit is in `v0.5.0-pre.1` and holds. S-02 and S-11, which `docs/superpowers/open-items.md` and the fixes report still list as open, were closed before `v0.4.0` by #187 and #188; those rows are stale (section 8).

## 6. The Low findings

### L5-01: No bound on live AI-link documents other than the reader limits

- **Area:** A. **Needs a decision from Chris:** yes.
- **Severity: Low.** *Impact:* each press of "Make a link for an AI" stores a document that lives an hour and counts towards nobody's storage; at the server-wide reader limit that is about 1.2 million documents at a time, measured at roughly 0.36 GB of data and 0.2 to 0.3 GB of indexes, gone an hour after the traffic ends. *Reachability:* it needs one valid published link and sustained requests from at least 34 networks with ten addresses each for a full hour; one network alone reaches about 36,000 documents (about 18 MB). The same traffic uses up the server-wide reader allowance, which the limits' design already accepts.
- **Trust boundary:** none is crossed: resource use by someone without an account who holds a published link.
- **Place:** `src/Kumbu.Server/Publishing/ReaderApi.cs` (`MakeAiLinkAsync`), `PublicationStore.cs` (`CreateAiLinkAsync`), `src/Kumbu.Server/Api/Security.cs` (the `ReaderRead` limits: 60 a minute per address, 600 per network, 20,000 per server).
- **Evidence:** a trace of the limits and the lifetime, and a measuring test through the test server and the store: one address gets 59 links and then 429; a document weighs 303 bytes, with about 191 bytes of indexes.
- **Second review:** confirmed, Low. The reader cookie, the copy header and the origin check identify the note, not the reader, so only the three limits bound the making.
- **Earlier reviews:** the AI-link spec §3.5 decides against a cap per reader cookie ("one per note that one reader could use up for all") and §3.4 keeps the links out of the storage figures on the assumption that "the space is surely tiny"; the AI-link review's point 6 says "Making links is bounded by the reader limits". None gives the figure, which is why it is raised.
- **Proposed fix, if wanted:** a making limit of its own, much lower than the read limit (a person presses a button: a few a minute per address, some hundreds per server), or a cap of live links per publication. The cap's cost is the one §3.5 names. **Tests:** the press after the limit answers 429 while a read from the same address still answers 200; the count of live links of one publication never passes the cap.

### L5-02: A calendar removal can delete a line inside another key's multi-line value

- **Area:** D. **Needs a decision from Chris:** no.
- **Severity: Low.** *Impact:* Reopen, Undo, "take out of the calendar" and `--no-task` silently drop lines from another frontmatter key's value and answer success; the earlier text is in the note's history unless the same changeset wrote it. *Reachability:* only in a note where someone who may write it has put a single-quoted value that continues at the start of a line with a line beginning `reason:`, `closed:`, `task:`, `until:`, `upcoming:`, `tz:` or `when:`; nothing crosses a role. *Doubt:* it breaks a written promise, which argues for more than Low; both reviews kept Low because the writer of that shape could change the note anyway.
- **Trust boundary:** a writer's frontmatter is untrusted input.
- **The promise:** `docs/security-architecture.md` §4 says the calendar "changes only its own frontmatter keys and leaves every other byte of the note as it was".
- **Place:** `src/Kumbu.Core/Board/Frontmatter.cs` (`Set` finds a key by the start of a line), `src/Kumbu.Core/Board/FrontmatterWrites.cs:30-38` (for a removal, the read-back check only asks that the key is gone), reached from `src/Kumbu.Core/Calendar/CalendarService.cs`.
- **Evidence:** a core test, "a calendar write leaves every other key's value as it was", for reopen, unset and no-task on `summary: 'first line⏎reason: this line belongs to summary⏎last line'`: the write answers "reopened", and the summary reads back as `first line last line`. The control, a reopen on an ordinary note, passes.
- **Second review:** confirmed, Low. Only the single-quoted form reaches it (the double-quoted and flow forms don't parse at the start of a line, block scalars and comments are untouched); indented lines after the look-alike go with it; lines of the note's body are safe; a set is refused by the read-back check, except a set of the value the key already has when the look-alike line stands before the real key.
- **Earlier reviews:** the calendar review lists frontmatter writes under "No hole found" and speaks only of sets; another review fixed the same kind of fault elsewhere.
- **Proposed fix:** in `FrontmatterWrites.TryOwnedKeys`, read the frontmatter before and after and refuse the write when any key outside the change reads differently or a key appears; that covers the board's writes too. **Tests:** the test above, and the same shape through the board (removing `due`).

### L5-03: Some diagrams hold a `foreignObject`, against the spec's statement

- **Area:** B. **Needs a decision from Chris:** a small one (correct the sentence, or refuse the sources).
- **Severity: Low.** *Impact:* none was shown: no request, no script and no handler in the drawn diagram. *Reachability:* any writer, with maths (`$$…$$`) in a sequence diagram's message, note or actor name or in a class label, and with every `journey` diagram.
- **Trust boundary:** a writer's Mermaid source.
- **The statement:** the Mermaid spec §2.2 says labels are SVG text, with no `foreignObject`; `htmlLabels: false` does not govern these paths.
- **Place:** `web/src/reader/mermaidRender.ts` (the site's configuration).
- **Evidence:** tests that draw `sequenceDiagram` sources with maths with the real Mermaid in a local Chromium page and assert "no `foreignObject`": they fail. What reaches the element is KaTeX's own MathML, made without `trust` and sanitised twice by Mermaid.
- **Second review:** confirmed, Low, and wider than first claimed (a sequence diagram's note and actor name, the class label, and the journey diagram).
- **Earlier reviews:** the Mermaid review's probes checked `foreignObject` for flowcharts only.
- **Proposed fix:** correct the spec's sentence, or refuse `$$` in sources and the journey diagram's `foreignObject` with the existing "can't be drawn" line; and assert on the drawn image in a test with the real Mermaid.

### L5-04: The KaTeX upgrade doesn't reach the renderer, and L-01's reasoning needs a sentence on Mermaid

- **Area:** G. **Needs a decision from Chris:** no.
- **Severity: Low.** *Impact:* none in the product today: the advisory (GHSA-238p-pmpm-9mq7, KaTeX below 0.18.2) still needs a prototype-pollution weakness elsewhere, and `web/src` has no such source. *Reachability:* a note's maths or a Mermaid label with maths, and only together with that second weakness.
- **What is new:** #191 raised the direct `katex` to 0.19.1, as L-01 of the 0.4.0 audit proposed, but `rehype-katex`, `micromark-extension-math` and `mermaid` each still resolve their own `katex` 0.16.47, so the renderer is unchanged and `pnpm -C web audit` still lists the advisory; the direct package supplies only the stylesheet, which is now newer than the markup it styles. And L-01 argued from `trust: false` being set as an own option, which holds for the note view's maths but not for Mermaid's own KaTeX call, new in 0.5.0, which passes no `trust` at all.
- **Evidence:** `pnpm -C web why katex`, `pnpm -C web audit` (nine advisories, the same as L-01's: react-router twice, vitest and its parts six times, KaTeX), `web/pnpm-lock.yaml`. `dotnet list package --vulnerable --include-transitive` and `pnpm -C pdf audit` list nothing.
- **Second review:** confirmed, Low; the same open item as L-01, not a new advisory.
- **Proposed fix:** a pnpm override `katex: ^0.19.1`, then the audit, the maths tests and a look at a formula in the browser; and the Mermaid sentence added to L-01's reasoning.

### L5-05: The end-to-end suite's fallback stack uses predictable paths in the temporary directory

- **Area:** G, at the branch head (#200; not in the tag, never in CI or the image). **Needs a decision from Chris:** no.
- **Severity: Low.** *Impact:* on a machine shared with another local user, that user can stop a developer's end-to-end run and, with a pid file placed first, make `down` end one of the developer's own processes; swapping the throwaway server's files needs a race between `rm -rf` and `mkdir -p`. The throwaway server's log and recovery files are readable by other local users under an ordinary umask. *Reachability:* only with `E2E_STACK=dotnet`, on a developer's multi-user machine with a shared temporary directory.
- **Place:** `web/e2e/stack/dotnet.sh` (`$TMPDIR/kumbu-e2e-dotnet`), `web/e2e/stack/env.sh` (`kumbu-e2e-vaults`).
- **Evidence:** a trace, and the second reviewer's run of `dotnet.sh down` and `up` as an unprivileged user against a directory another user had made: a directory made beforehand is not used (`rm -rf` fails and the script stops), the pid file is.
- **Second review:** confirmed, Low, with the mechanism corrected as above.
- **Proposed fix:** `mktemp -d`, with the path kept in the ignored `e2e/.auth/`, or `mkdir` without `-p` and `chmod 700`.
- **Found sound beside it:** the suite's secrets are random per run, in `e2e/.auth/` (mode 600, ignored by git and by Docker, removed on exit); traces are off; no workflow runs the suite; the image copies only `/app`.

## 7. Decisions made without Chris

1. **Every reviewer at once.** Options: the areas one after another in the brief's order, or in parallel. Chosen: all eight started together, in the brief's order; the machine carried it, at the price of slow first builds and timings under load, which every reviewer therefore took beside a control.
2. **Second reviews as each area reported,** not after the last area. Options: wait for phase 1 to end, or start each second review when its area's report came. Chosen: at once, so that a High would have been confirmed and reported as early as possible; the card's phases were ticked when each was complete.
3. **An eighth task for the 0.4.0 findings.** The spec asks that a 0.4.0 finding still open, or whose fix doesn't hold, be reported; that needed its own reviewer with the private record, kept apart from the areas.
4. **A weakness reported by more than one reviewer is counted once.**
5. **No High.** For two findings one reviewer proposed High; their second reviews and an additional severity check rated them Medium. Options: High by the rule "when in doubt, the higher", with the notification and the board move of spec §6; or Medium with the doubt written down. Chosen: Medium, because spec §6 acts on a High "once the second review confirms it", and none did. The doubt, and what would settle it, are in the private file for Chris to overrule.
6. **Ids.** Every finding of this review has an `S5` or `L5` id of its own; cross-references to earlier findings are in the private file.
7. **Otherwise, the higher level.** Where a later review rated a point up, the higher rating stands.
8. **A third reviewer where a second reviewer was the first to show something.** A suspicion the first reviewer couldn't show, which the second reviewer then showed, had only one reviewer's evidence. Options: count it as reviewed, or have it checked again. Chosen: a third fresh reviewer for two such points; the others stay in the private file.
9. **Five Low findings are not described here.** The brief asks for every Low in full; its first decision rule says that a detail which may give away an unreleased weakness is not published. L5-06 to L5-10 each sit beside a confidential finding, so their descriptions are in the private file only, and this report gives their ids. Options: publish them, leave them out silently, or name them by id. Chosen: by id.
10. **A finding outside the scope gets an id and is counted apart.** S5-10 lies outside the scope (spec §2); it was found on the way and is reported for Chris with its severity.
11. **L5-04 is counted, though its advisory is L-01's.** A known and accepted advisory is no new finding; what is new is that the upgrade made for it doesn't reach the renderer and that its reasoning no longer covers every call.
12. **What is no finding.** Recorded and left: the calendar's web writes carry no base version (the calendar plan's decision 4); a reader on the network of a blocked address sees AI links as expired (the AI-link report's decision 7).
13. **The private material was pushed before the findings file.** The reviewers' tests and reports went to the private branch as soon as phase 2 neared its end, the findings file after it; nothing but the plan went to the public branch before this report.

## 8. Deviations, surprises, open points

- **A loaded machine.** With eight reviewers on four cores the load average stood between 20 and 45 for most of phase 1, and first builds took up to five minutes. Any measurement was taken beside a control and repeated by the second review.
- **A shared scratch directory.** Two reviewers' first build logs overwrote each other; the builds were fine, and later logs went into the worktrees.
- **Two false alarms by reviewers' own tests,** found and corrected by them before reporting (a case-insensitive match on a title, an assertion on literal text). The second reviews also corrected several first reviews' facts.
- **Web tests with the real Mermaid.** jsdom can't render Mermaid, and every Mermaid test in the repository mocks it. The reviewers bundled the app's render module with the real library into a local Chromium page; such a test in the repository would be worth having (open point below).
- **This report was checked against the publication rule** by a fresh agent that got the private file and the draft. It raised 25 points; all but one were taken, which moved one Low (L5-06) from this report to the private file and cut the method descriptions down. Not taken: writing the two pull requests without their URLs, which the brief asks for.
- **Not done:** the full test suites (nothing changed); a check of the base-image digests against the registries; device keys and WebDAV shares against the new flag and calendar endpoints (keys, sessions and connections were tested); timing differences between answers (earlier reviews accepted them).
- **`PushNotification`** was available and was used once, at the end.
- **Open points for Chris:**
  - the ten Medium findings, by the release policy before 0.5.0: details, proposed fixes and tests in the private pull request; S5-06 needs a decision, S5-04, S5-08 and S5-09 a small one each;
  - the doubt towards High on S5-01 and S5-02 (decision 5): the recommendation is to fix these two first in the joint fix run;
  - L5-01 and L5-06 need a decision, L5-03 a small one;
  - `docs/superpowers/open-items.md` and `docs/superpowers/overnighter/2026-11-11-security-fixes-report.md` still list S-02 and S-11 as open, though #187 and #188 closed them before `v0.4.0`; this run didn't change those rows (they are not its own), and the private file records what #188 means for S-11;
  - a test with the real Mermaid in the repository; and two small points that are not security matters, in the private file's "Other points for Chris".

## 9. Pull requests

- Public: https://github.com/SCAI-NGO/kumbukumbu/pull/201
- Private (opens for Chris alone): https://github.com/SCAI-NGO/kumbukumbu-security/pull/3

Neither is merged. The signal at the end of the run is a `PushNotification` and the two pull requests.

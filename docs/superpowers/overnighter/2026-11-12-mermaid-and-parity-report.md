# Overnighter report: Mermaid diagrams and three Obsidian parity items

Date: 2026-11-12/13. Branch `feature/mermaid-parity`, pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/195. Spec `docs/superpowers/specs/2026-11-12-mermaid-and-parity-design.md`, brief `docs/superpowers/overnighter/2026-11-12-mermaid-and-parity-brief.md`, plan `docs/superpowers/plans/2026-11-12-mermaid-and-parity.md`. Not merged, not tagged, version unchanged.

## Summary

All four features are built, reviewed and tested; one part of the spec could not be delivered as written:

- **Mermaid** draws as an image (`<img src="blob:…">`) in the note view, embedded notes and History/checkpoint views that use `MarkdownView`, with Show source, Full size/Fit, alt text from `accTitle`/`accDescr`, a line and the source on any failure, and a redraw on a theme change. **The publish reader page shows the code block instead**: a real Chromium render under its CSP drew the right image but raised 78 `style-src` violations (Mermaid's measuring container and `<style>` in the live document). Per spec §2.3 the CSP stays and the case is in `open-items.md`.
- **Embeds in `cat`**: a standing-alone `cat` appends a marked "embedded notes" block; the note's own text stays byte for byte.
- **Block references `^id`**: one finder per side (`Kumbu.Core.Sections.Blocks`, `web/src/note/blocks.ts`) against one shared cases file (`web/src/note/blocks.cases.json`, 36 cases); `cat "#^id"`, `links`, moves, hidden markers, block embeds, jumps with a brief highlight.
- **Unlinked mentions**: `backlinks --unlinked`, `GET /api/mentions/{vault}/{path}`, a closed "Unlinked mentions" section below "Linked from".
- The security review led to three rounds of hardening on diagrams (refusing sources that would load a resource or put HTML into the live page while Mermaid draws) and to linear-time mention matching; a final Chromium re-probe of 33 hostile sources made no request.

## Environment

- Cloud container, 4 cores, 15 GB RAM, Linux 6.18. .NET SDK 10.0.403 (global.json 10.0.100, roll forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- Installed by hand: Node 26.12.1 into `/opt/node26` (the image had Node 22), pnpm 10.32.1 (`npm install -g`). `dockerd` was not running and was started by hand; `mongo:7.0` pulled on the first run. The tokenizer was fetched and checked as the brief says (`KUMBU_TOKENIZER`). `TESTCONTAINERS_RYUK_DISABLED=true`; the test MongoDB as the brief's `kumbu-test-mongo`, fresh before each server group.
- Playwright and Chromium were preinstalled and used for the CSP probes (never against a real host).
- Superpowers skills were available; the run used `writing-plans` and `subagent-driven-development` (a fresh implementer per task, a task reviewer giving both verdicts — spec compliance and quality — then scoped re-reviews after fix rounds, a security review and a final whole-branch review on the most capable model). Implementers never committed; the controller committed and pushed after each task.
- Task 0 took about 75 minutes, not 15: the full gate itself is long (six server groups), plus the MongoDB image pull. Nothing was off beyond that.

## Plan review

A fresh subagent reviewed the plan against spec and brief before work began. Its findings and what happened to them:

- Mention offsets wrong (13 → 17) — fixed in the plan.
- HelpTests and the manual's 32/34-column layout would break with the new `backlinks` usage — plan updated; later settled by ruling (manual keeps the usage in its column).
- `MarkdownView.test.tsx:550` asserts no `[data-line]` — plan says narrow it to headings (done in Task 6).
- The agent highlight on a diagram would be lost (`hProperties` land on `code`) — plan says read them from the code child (done in Task 2).
- Diagram directives could undo `htmlLabels: false` — plan adds a `secure` key list and `suppressErrorRendering` (done in Task 1).
- Mermaid renders in the live document; the reader probe would likely fail — plan made the fallback concrete (it did fail, fallback applied).
- Stale renders and leaked object URLs — plan adds a cancellation guard and a test (done).
- The new endpoint must join `HiddenVaultTests`, `MarkerTests` and the OpenAPI path list (done in Task 9).
- Own-line `^id` without a blank line, a move test that proves nothing for bare names, a placeholder in the vite rule, an archive hint for `--unlinked`, NFD offsets, the wider "once" rule in `cat` — all folded into the plan.
- Low items (React 18 not 19, vague fixtures, missing manual line for blocks, headings carrying an id, `useDarkScheme`'s fallback wording, blob SVG out of links, the PushNotification step) — folded in or recorded below.

## Task 0: baseline, planned against measured

| | Planned (master at 54e66fd) | Measured on `feature/mermaid-parity` before any change |
|---|---|---|
| Core | 2,266 total, 3 skipped, 0 failed | 2,266 total, 3 skipped, 0 failed |
| Server | 843 + 413 + 353 + 20 + 780 + 1,163 = 3,572, 5 skipped in CI (up to 7 locally) | 843 + 413 + 353 + 20 + 780 (6 skipped) + 1,163 (1 skipped) = 3,572, 7 skipped, 0 failed |
| Web | 131 files, 2,361 tests, build green | 131 files, 2,361 tests, build green |
| PDF sidecar | 9 files, 198 tests, build green | 9 files, 198 tests, build green |

No flaky test failed in the baseline.

## Tasks: commits, test output and mutations

The implementers' and reviewers' reports (`task-N-report.md` and the others named below) lived in the run's git-ignored workspace and are not committed; their numbers and test output are quoted here as they recorded them.

### Task 3: Core block finder and `cat "<path>#^id"`
- Commit: `e88a0ba Find block ids and let cat print a block`.
- Review: clean, 0 fix rounds. Deferred minors: paragraph split at any list-item line; indented code after heading; loose lists cut at blank lines; heading text starting with `^` cannot be addressed. Concern: `Sections.Blocks` vs internal `Search.Blocks` name clash, aliased in ChunkerTests; cat help lacks `#^id` (Task 7 added it).
- RED: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*BlocksTests"` (tests, no implementation): `BlocksTests.cs(25,21): error CS0103: The name 'Blocks' does not exist in the current context`; `error CS0246: The type or namespace name 'Block' could not be found`.
- GREEN: `*BlocksTests` total: 42 succeeded: 42; `*CatTests` 17/17; `*SectionCommandsTests` 18/18; `*SectionAddressTests` 23/23; `*HelpTests` 25/25; `*ChunkerTests` 179/179; whole Core `total: 2312  failed: 0  succeeded: 2309  skipped: 3`; Server tests build `Build succeeded, 0 warnings`.

| Mutation | Failed test(s) | Result |
|---|---|---|
| (a) code lines not skipped (`new bool[lines.Count]`) | first run only `after code`; brief's `inside code` case did not fail | Survivor: case strengthened to `"```md\nThe pump needs a controller. ^code\n\nMore.\n```\n"`; rerun: `inside code` and `after code` FAIL |
| (b) ordinal compare | `ignoring case` | FAIL |
| (c) last block wins (`LastOrDefault`) | `the first of two wins` | FAIL |
| (d) no space needed before `^` | `no space before the caret`, `a quote, id right below` | FAIL |
| (e, extra) `ReadAddressAsync` never takes the block branch | the three new CatTests | FAIL |
| (f, extra) indented code not detected | `indented code` | FAIL |

### Task 4: links and moves with block references
- Commit: `548a50a Resolve block references in links`.
- Review: clean, 0 fix rounds. Deferred minors: directory mode splits every note eagerly; `Blocks.Find` reparses per link; no test for block link to ambiguous/missing note or with `--version` (both added in the final fix wave, item 7).
- RED: `--filter-class "*LinkCommandsTests"`: total 54, failed 6 (all new except `A_heading_subpath_prints_as_before`), e.g. "Not found: 1: [[Plan#^controller]] → /Work/Pump/Plan.md#^cont...". `*NoteMoverTests` 22/22 passed already (no mover change needed).
- GREEN: LinkCommandsTests 54/0 failed, NoteMoverTests 22/0, HelpTests 25/0; `dotnet test --project tests/Kumbu.Core.Tests` (Debug): total 2320, failed 0, skipped 3.

| Mutation | Failed test(s) | Result |
|---|---|---|
| (a) ignore `%5E` | `A_percent_encoded_caret_counts` and four other block tests | FAIL (a finer first attempt did not compile, discarded) |
| (b) print path without `#^id` | `Links_to_a_block_name_it`, `A_percent_encoded_caret_counts` | FAIL |
| (c) skip the directory check (`broken ||` removed) | `A_directory_lists_missing_blocks_among_the_broken` | FAIL |

### Task 1: Mermaid component (reader folder)
- Commits: `5811854 Add a Mermaid diagram component drawn as an image`; fix `6729951 Turn Mermaid's drawing into well-formed SVG and fall back when the image fails`.
- Review: Needs fixes (Important: HTML-serialised SVG not XML: `&nbsp;`, no img onError); 1 fix round, then clean. Deferred minors: no test for the no-svg path; `wide` not reset on redraw (fixed in round 1); bare `<br>` in SVG content would cut the image; init-directive test can't fail for the real reason (mocked), covered by Task 11 probe.
- RED: `pnpm -C web test --run src/reader/MermaidDiagram`: `Error: Failed to resolve import "./MermaidDiagram" ...`; `Test Files  2 failed (2)`; `Tests  no tests`.
- GREEN (first): `pnpm -C web test --run src/reader`: `Test Files  5 passed (5)`, `Tests  36 passed (36)`. Whole suite `Test Files 133 passed (133), Tests 2376 passed (2376)`; build passed (8.01s).
- GREEN (after fix round): `src/reader` `Tests  39 passed (39)` (MermaidDiagram.test.tsx 17); whole suite `Test Files 133 passed (133)`, `Tests 2379 passed (2379)`; build `built in 7.76s`.
- Build probe (throwaway): mermaidRender chunk 679.20 kB (gzip 166.31 kB), cynefin 690.9 kB; first-load chunks without mermaid; >600 kB warning accepted.

| Mutation | Failed test(s) | Result |
|---|---|---|
| (a) `securityLevel: "loose"` | `an init directive cannot loosen the security level`, `passes the theme` | FAIL (2) |
| (b) revoke effect a no-op | `revokes the image's URL when it goes`, `passes the theme` | FAIL (2) |
| (c) no queue (`const run = draw(source, dark)`) | `renders diagrams one after another` | FAIL (1) |
| (d) `return { svg, alt }` (no naturalSize) | `draws the diagram as an image with its alt text` | FAIL (1) |
| (e) `"katex"` added to the isolation set | none (36 passed) | Survivor, accepted: the test pins what is imported, not what is allowed; restored |
| Fix round: `root.outerHTML` instead of `XMLSerializer` | `gives the image well-formed XML ...` | FAIL (1) |
| Fix round: `onError` removed | `an image that can't be shown ...` | FAIL (1) |
| Fix round: `setFull(false)` removed | `a new image starts fitted` | FAIL (1) |

### Task 7: embedded notes appended to `cat`
- Commits: `974b901 Append the notes a note embeds to cat`; fix `2881a05 Keep cat's appendix within the output limit and take embeds in lists and quotes`.
- Review: Needs fixes (1 Important: budget near output limit; 5 findings in all); 1 fix round, then clean. Deferred minors: outline mode skips attachment existence check; 200-char notice margin could be exceeded; `text\n![[x]]` continuation appended though inline in the web.
- RED: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*CatEmbedsTests"`: `Expected: ···"``\n\n![[Ruling#Decision]]\n\n--- embedded notes: show"···  Actual: ···"``\n\n![[Ruling#Decision]]\n"`; `total: 20  failed: 14` (6 negative tests passed as they pin today's behaviour).
- GREEN: `*CatEmbedsTests` 20/20; `*CatTests` 17/17; `*ShellTests` 27/27; `*HelpTests` 25/25; `*SkillsCommandTests` 37/37; `*PipelineTests` 9/9; Core `total 2340, failed 0, succeeded 2337, skipped 3`.
- Fix-round RED (committed `Cat.cs` swapped in under new tests): `total: 24  failed: 4`. GREEN: `*CatEmbedsTests` 25/25, CatTests 17/17, ShellTests 27/27, HelpTests 25/25, LinkCommandsTests 54/54; Core `total 2345, failed 0, succeeded 2342, skipped 3`.

| Mutation | Failed test(s) | Result |
|---|---|---|
| (a) append in a pipeline (`args.Count == 1` removed) | `No_appendix_in_a_pipeline` | FAIL (1 of 20) |
| (b) self filter removed | `Each_target_once_and_never_the_note_itself` | FAIL |
| (c) no 200 margin before the cut line | `A_long_appendix_is_cut_at_an_embed` | FAIL |
| (d) `marks.NoticeAsync([path], ct)` | `An_embedded_note_written_by_another_user_is_marked` | FAIL |
| (e) line number `embed.Link.Line - range.From + 1` | `A_section_appends_only_its_embeds` | FAIL |
| Fix: lookahead dropped (`+ after` removed) | at first none | Survivor: `An_embed_is_added_only_with_room_for_the_next_cut_line` added; it fails under this mutation |
| Fix: up-front header + cut check removed | `A_cut_line_with_a_long_target_is_measured_whole`, `A_note_just_under_the_limit_...` | FAIL |
| Fix: version check removed | `A_note_changed_between_the_reads_gets_no_block` | FAIL |
| Fix: prefix ignored (`start = 0`) | `Embeds_as_list_items_and_in_quotes_are_appended` | FAIL |

### Task 8: unlinked mentions in Core and `backlinks --unlinked`
- Commit: `a3b1446 List unlinked mentions with backlinks --unlinked`.
- Review: clean, 0 fix rounds. Rulings: `Backlinks.Usage` stays `backlinks [--archive] <path>`; manual keeps its column and names `--unlinked` in the summary (no ArchiveHiddenTests exemption). Deferred minors: NFD combining mark after a title; missing blank lines (fixed in final wave item 9); O(n²) on unmatched `[` (fixed in security fixes); double split; weak second vault-isolation assertion.
- RED: `MentionsTests.cs(10,13): error CS0103: The name 'Mentions' does not exist in the current context`.
- GREEN: first run MentionsTests 23/23, HelpTests 25/25, LinkCommandsTests 2 failures (mistakes in expectations: `_archive` sorts before `Diary`; shell prepends notices), then 64/64; full Core first time 1 failure (`ArchiveHiddenTests.Help_rg_and_semsearch_name_archive`, column 34). After the ruling: ArchiveHiddenTests 25/25, LinkCommandsTests 64/64, MentionsTests 23/23, HelpTests 25/25 (intermediate: 24 passed, 1 failed on `The_readers_that_hide_the_archive_take_archive` at HelpTests.cs:61, resolved by option 1); whole Core 2378 total, 0 failed, 2375 passed, 3 skipped.

| Mutation (class) | Failed test(s) | Result |
|---|---|---|
| (a) `OrdinalIgnoreCase` to `Ordinal` (MentionsTests) | 7 failed: `A_plain_mention_counts`, `Whole_words_only` (`the plan.`, `PLAN`, `a (plan) here`), `A_line_with_a_link...`, `Two_mentions...`, `Titles_with_spaces` | FAIL |
| (b) word-boundary checks removed (MentionsTests) | 3 failed: `Whole_words_only` (`planning ahead`, `an airplane`, `plan2 is out`) | FAIL |
| (c) Markdown link masking off (MentionsTests) | 2 failed: `see [the plan](x.md)`, `see ![the plan](x.png)` | FAIL (external `[x](https://a.example/plan)` still masked by the bare-URL regex) |
| (d) note itself included (LinkCommandsTests) | 8 failed incl. `The_note_itself_doesnt_count`, `Backlinks_unlinked_lists_plain_mentions` | FAIL |

### Task 9: the mentions endpoint
- Commit: `5b0580a Answer unlinked mentions over the API`.
- Review: clean, 0 fix rounds. Minors: duplicated existence check; member test only Read role. Ruling: `schema.d.ts` gets only the new blocks (committed file already stale vs master: `ai-links`, order, SkillView defaults); open item.
- RED: NoteApiTests `failed: 3` (openapi list, `A_member_reads_mentions_of_a_shared_vault`, `Mentions_lists_plain_mentions_with_their_marks`); MarkerTests `total: 8  failed: 1` (`/api/mentions/garten/kohl2.md: NotFound`); HiddenVaultTests `total: 11  failed: 0`. The two 404 tests and the HiddenVault entry passed before the route existed (unmapped route is also 404); mutation (a) shows they test the handler.
- GREEN: NoteApiTests `total: 31  failed: 0  succeeded: 31`; HiddenVaultTests `total: 11  failed: 0`; MarkerTests `total: 8   failed: 0`; `pnpm -C web build`: `✓ built`. `git diff --stat web/src/api/schema.d.ts`: 89 insertions, 0 deletions.

| Mutation | Failed test(s) | Result |
|---|---|---|
| (a) `MissingAsync` existence check removed | `Mentions_of_a_missing_note_is_404`, `Mentions_of_another_users_vault_is_404` (Expected NotFound, Actual OK) | FAIL (2) |
| (b) `m.Line` instead of `m.Line + 1` | `Mentions_lists_plain_mentions_with_their_marks` (Expected 1, Actual 0) | FAIL |

### Task 2: Mermaid in the note view
- Commit: `7b46d9b Draw Mermaid diagrams in the note view`.
- Review: clean, 0 fix rounds. Minors: import order (fixed in final wave); stale `textOf` comment (fixed).
- RED: `pnpm -C web test --run src/theme/dark.test.ts src/note/MarkdownView.mermaid.test.tsx`: `Failed to resolve import "./dark"`; four failures (`draws a mermaid block as an image`, `redraws on a change of data-scheme`, embedded, highlight); `Test Files  2 failed (2)   Tests  4 failed | 1 passed (5)`.
- GREEN: `Test Files  2 passed (2)   Tests  6 passed (6)`; `src/note` `Test Files 22 passed (22), Tests 468 passed (468)`; whole suite `Test Files 135 passed (135), Tests 2385 passed (2385)`; build `built in 15.39s`. First build failed in tsc (`TS2345 '"language-mermaid"' is not assignable to parameter of type 'never'`), fixed.

| Mutation | Failed test(s) | Result |
|---|---|---|
| (a) `dark={false}` always | `redraws on a change of data-scheme` (1 failed, 4 passed) | FAIL (run on first and final version) |
| (b) match `language-mermai` | first version: none (5 passed, substring `includes`) | Survivor: check changed to exact class token; re-run on final code: 4 failed (image, redraw, embedded, highlight), "loads nothing" passes |
| (c) static `import * as statically from "./mermaidRender"` in MermaidDiagram.tsx | `loads nothing for a note without a mermaid block` (1 failed, 4 passed) | FAIL |

### Task 11: Mermaid on the reader page, under its CSP
- Commit: `b19272d Keep Mermaid blocks as code on the reader page, whose CSP refuses Mermaid's inline styles`.
- Review: clean, 0 fix rounds. Probe failed, fallback applied (code block); open-items row added. Minors: fixed sleep in reader test (fixed in final wave); comment on mock factory.
- RED (drawing version): `pnpm -C web test --run src/reader`: three failed (`draws a mermaid block as an image`, `Copy as Markdown keeps the mermaid source`, `follows the system's dark preference`), each at `findByRole("img")`.
- GREEN (drawing version): 43 of 43 passed in `src/reader`. Fallback: 41 of 41 passed in `src/reader`. Whole suite `136 of 136 files and 2395 of 2395 tests`; `pnpm -C web build` passed (first run failed in tsc on another task's `UnlinkedMentions.tsx`).
- Probe numbers: section 3.

| Mutation / check | Failed test(s) | Result |
|---|---|---|
| (a) `dark` always `false` (drawing version) | `follows the system's dark preference` (other 21 passed) | FAIL; restored 22 of 22 |
| Fallback test against the drawing `Reader.tsx` | `shows a mermaid block as code` (1 failed, 19 passed) | FAIL, so the test catches a reader that draws |
| (b) block-id mutation | not run (controller ruling: Task 11 runs before Task 5) | not applicable |

### Task 10: unlinked mentions in the web app
- Commit: `df0ee4c Show unlinked mentions below Linked from`.
- Review: clean, 0 fix rounds. TDD order deviation (code first; red obtained by moving the file aside). Minors, taken in the final wave: no test for closing on path change; order test doesn't assert order; contrived destructure; retry shows no loading state.
- RED (file moved aside): `pnpm -C web test --run src/note/UnlinkedMentions.test.tsx`: file failed to load, "Tests no tests", ELIFECYCLE.
- GREEN: run 1: 7 passed, 1 failed (test data mark at 20 instead of 21), then `8 passed (8)`; `src/note`: `23 files, 476 tests passed`; whole suite (before the `Number()` fix) `136 files, 2397 tests passed`; build first run failed type check, then `built in 15.81s`.

| Mutation | Failed test(s) | Result |
|---|---|---|
| (a) `useMentions(path, true)` | `is closed and asks nothing until opened` (1 failed, 7 passed) | FAIL |
| (b) navigate without the state | `opens the note at the line` | FAIL |
| (c) `if (sources.length === 0) return null` | `shows even when nothing links here` | FAIL |

### Task 12: README
- Commit: `8487dbf Document Mermaid, block references and unlinked mentions`.
- Review: none by a reviewer seat; the controller read the 3-line diff. README only (`cat`, `links`/`backlinks`, web feature paragraph). No test run needed (tests' README mentions are skill READMEs). No mutations.

| Mutation | Failed test(s) | Result |
|---|---|---|
| none recorded | n/a | n/a |

### Task 5: block ids in the web app
- Commits: `917817e Hide block ids and embed blocks in the note view`; fix `1da8285 Read Windows line ends as the Core does, and hide an id after a hard break`.
- Review: Needs fixes (Important: CRLF divergence in fence/heading regexes); 1 fix round, then clean. Deferred minors: lazy continuation markers hidden though they name nothing; `\s`/`\d` Unicode gaps (fixed in final wave item 6); outline shows `^id` in heading text.
- RED: `pnpm test --run src/reader/blockIds.test.ts src/note/blocks.test.ts src/note/wikiLinks.test.ts src/note/MarkdownView.blocks.test.tsx`: `Failed to resolve import "./blocks"`, `"./blockIds"`; `Tests 11 failed | 5 passed (16)`. Reader red: `Tests 1 failed | 21 passed (22)`.
- GREEN: `Tests 55 passed (55)`; Reader 22 passed; `src/reader` `Tests 52 passed (52)`; Core `BlocksTests` Passed, total 44, failed 0; whole suite `Test Files 139 passed, Tests 2449 passed`; build `built in 15.22s`.
- Fix round: three CRLF cases red on web `3 failed | 32 passed`, Core `total: 47, failed: 0`; green web `35 passed`, Core 47; covering tests `10 files, 115 tests passed`; whole suite `139 files, 2455 tests passed`; build `built in 16.13s`.

| Mutation | Failed test(s) | Result |
|---|---|---|
| (a) own-line paragraph filter removed (blockIds.ts) | `remarkHideBlockIds > hides an id on its own line`, `Block ids in the note view > hides block markers in the note view` (2 failed, 10 passed) | FAIL |
| (b) fence lines counted as not code in `codeLines` | `findBlock > every shared case: inside code` (1 failed, 29 passed) | FAIL |
| (c) `decodeURIComponent` removed from `subpathBlock` | `subpathBlock > reads [c](Plan.md#%5Econtroller) as controller` (1 failed, 12 passed) | FAIL |
| Reader: plugin reverted in `Reader.tsx` | `Block ids on the reader page > hides block ids` (1 failed, 21 passed) | FAIL (red check) |

### Task 6: open a note at a block or a line
- Commits: `05c2bc0 Open a note at a block or a line, with a brief highlight`; fixes `7f85d9c Jump to the block with the greatest line at or above the target, and keep the block through an ambiguous link` (fix rounds 1 and 2 in one commit).
- Review: Needs fixes (Important: DOM order vs greatest data-line; footnotes); 2 fix rounds, then clean. Deferred minors: `[[#^id]]` inside an embedded note stays plain; jump before embeds load can drift (fixed in final wave item 1).
- RED: `pnpm -C web test --run src/note/NoteView.jump.test.tsx src/note/MarkdownView.blocks.test.tsx`: `Tests  4 failed | 5 passed (9)` (e.g. `a link to a block carries the block in its state`, `an unknown block opens at the top with a notice`).
- GREEN: `Tests 10 passed (10)`; `src/note src/search`: `Test Files 28 passed (28) / Tests 553 passed (553)`; whole suite `Test Files 140 passed (140) / Tests 2461 passed (2461)`; build first failed `TS18048: 'link' is possibly 'undefined'`, then `✓ built in 18.33s`.
- Fix round 1: red `expected [ <p data-line="3" …> ] to deeply equal [ <p data-line="7" …> ]` and same for the quote; two task files 13/13; build `built in 20.18s`; whole suite had flaky `commentRanges.test.tsx` failures in the first two runs (load average 10 on 4 cores), third run `Test Files 140 passed (140) / Tests 2464 passed (2464)`.
- Fix round 2: two task files 14/14; `src/note` `26 files, 534/534`; no whole-suite run.

| Mutation | Failed test(s) | Result |
|---|---|---|
| (a) `unflash` doesn't remove `k-flash` | `a link to a block scrolls to it and highlights it` (1 failed) | FAIL |
| (b) first `data-line` >= line (`.find(... >= line)`) | `a line opens there` (1 failed) | FAIL |
| (c) `useLine` returns the line when `nested` | `nothing inside an embed carries data-line` (1 failed) | FAIL |
| (d, extra) self link without a block | `a link to a block of the note itself opens it there` (1 failed) | FAIL |
| (e) last in DOM order (no greatest-line check) | quote test (footnote test still passes) | FAIL |
| (f) `>=` (last on equal lines) | quote test | FAIL |
| (g) dialog without `state={stateOf?.(path)}` | ambiguous-link test | FAIL |
| (h) `[data-footnotes]` exclusion dropped | none (all pass) | Survivor: the greatest-line rule covers it alone; exclusion later dropped in fix round 2 with test `a line inside a footnote's definition opens at the footnote` (red with exclusion: `expected [ <h1 data-line="1" …> ] to deeply equal [ <li data-line="3" …> ]`) |
| Round 2: last in DOM order, re-checked | three tests: footnote above the line, line inside the footnote, quote | FAIL (3) |

### Security fixes
- Commits: `b711091 Find mentions in linear time and refuse diagrams that would load images`; `e2c38cf Refuse diagrams whose styles would load a resource`; `cadb8e6 Refuse HTML in diagrams and check masked spans by binary search`.
- Review: security review (Q1-Q4 NO-ISSUE; findings 1-4); fix, scoped re-review of 7f85d9c..e2c38cf found open HTML sinks and mentions `masked.Any` quadratic; round 3 then a browser re-probe (section 3). Open points recorded: forged appendix header, trailing-newline ambiguity, `NoteLink.Parse` quadratic (pre-existing), Mermaid temp DOM.
- Part 1 (mentions CPU): RED `Actual: 00:02:14.4067853`, total 24, failed 1; GREEN total 24, succeeded 24, 3.9 s. Equivalence scratch: "2000000 lines agree".
- Part 2 (image shapes): RED `7 failed | 18 passed`; GREEN `25 passed`.
- Part 3 (diagramAlt): RED `expected 1723.99 to be less than 200`; equivalence 3,000,000 random sources, 0 differ.
- Final runs part 1-3: Core `total 2384, succeeded 2381, skipped 3, failed 0`; `src/reader` `6 files, 61 tests passed`; build 13.7 s.
- Follow-up CSS url() (e2c38cf): RED `6 failed | 26 passed (32)`; GREEN 32 passed; `src/reader` `6 files, 68 tests passed`; second follow-up (drop `\s*`): `69/69`.
- Round 3 (cadb8e6): Mentions RED both cases failed at 17.8 s and 18.1 s, GREEN 26/26 (class 1.4 s); equivalence "3000000 queries agree". Mermaid RED `8 failed | 36 passed`, GREEN `44/44`; final `dotnet ... "*MentionsTests"`: 26/26; `src/reader`: `6 files, 80 tests passed`; build 14.8 s.

| Mutation | Failed test(s) | Result |
|---|---|---|
| Old `Mentions.cs` put back (part 1) | `Unclosed_brackets_take_linear_time (1m 52s 744ms)`, `Actual: 00:01:52.68` | FAIL |
| Delete `if (loadsImage(source)) throw` | 6 failed, 19 passed (all six refusal cases) | FAIL |
| Old regex lines back in `diagramAlt` | `expected 1759.24 to be less than 200` (1 failed) | FAIL |
| CSS follow-up: check line deleted | same 6 failed, 26 passed | FAIL |
| CSS follow-up 2: `\s*` put back | the state label `image (1) and element (x)` draws case (1 failed, 32 passed) | FAIL |
| Round 3: old `Mentions.cs` (`masked.Any`) put back | both `Many_masked_spans_take_linear_time` cases failed again (17.2 s and 17.3 s) | FAIL |
| Round 3: reference decoding and HTML rule removed (CSS decoding only) | the same 8 failed | FAIL |
| Cancellation check in `NoteLinks.MentionsAsync` | no dedicated test (no test calls `MentionsAsync` directly) | Not covered, recorded |

### Final fix wave
- Commit: `c403b08 Keep a jump on its block while content loads, and close the review's smaller gaps`.
- Review: scoped re-review clean (10/10 addressed). Rulings recorded: web mentions leave the archive out; cat appends only `![[...]]` embeds; checkpoint views ignore NoteAt. Watch: MentionsTests timing test failed once under load in the fixer's run, "check in gate 2".
- Item 1 (jump drift): RED both `TypeError: resized is not a function`; GREEN 11/11.
- Item 2 (stored text): RED `expected [] to deeply equal [ <p data-line="3"> ]`; GREEN 11/11.
- Item 3 (folded callout): RED `expect(element).toHaveAttribute("open")`; GREEN 11/11.
- Item 4 (manual `links` row): HelpTests 25/25, ArchiveHiddenTests 25/25.
- Item 5 (`subpathBlock` angle brackets): RED 2 failed, 13 passed; GREEN 15/15.
- Item 6 (ASCII classes): RED `*BlocksTests` 2 failed of 49 (Arabic case `Tuple (2, 2)` where `(1, 2)` expected; next-line case `Tuple (1, 1)` where `null` expected), web `38/38` already; GREEN 49/49 and 38/38. Deviation: `[ \t\v\f\r]` rather than `[ \t]` (the `\r` is needed for Windows line ends); Arabic case uses a different note from the brief's, which could not fail.
- Item 7 (links tests): no red (pin existing behaviour), 66/66 first run.
- Item 8 (UnlinkedMentions tests): component unchanged (TanStack Query v5 `isPending` already covers retry); each mutation `1 failed, 9 passed`.
- Item 9 (cosmetics): includes `Reader.test.tsx` fixed sleep replaced by `vi.dynamicImportSettled()`; load counter made a plain hoisted counter.
- Item 10 (spec §1 example, `MarkdownView.example.test.tsx`): 1/1, no red.
- Final: Core first run `2 failed, 2385 passed, 3 skipped` (the two `Many_masked_spans_take_linear_time` cases, "Plan`a`" 18.9 s, "Plan[a](b)" 21.8 s, against a 2 s limit, while another agent edited Mentions.cs); `*MentionsTests` alone twice 26/26; whole Core again `2387 passed, 0 failed, 3 skipped`. Web `141 files, 2492 tests passed`; build `built in 14.1 s`.

| Item: mutation | Failed test(s) | Result |
|---|---|---|
| 1: observer callback a no-op | "the block is centred again when the note above it grows" (1 failed, 10 passed) | FAIL |
| 1: user-input listeners removed | "but not once the user scrolls" (1 failed, 10 passed) | FAIL |
| 2: back to `findBlock(shown.body, ...)` | "a block a comment hides opens at the block above it, without a notice" (1 failed, 10 passed) | FAIL |
| 3: `closest("details")` line removed | "a jump into a folded callout unfolds it" (1 failed, 10 passed) | FAIL |
| 4: summary at column 35 | `ArchiveHiddenTests.Help_rg_and_semsearch_name_archive` (24/25) | FAIL |
| 5: `<([^>\s]*)>` (no spaces in brackets) | both new angle-bracket cases | FAIL |
| 6: `\d{1,9}` back in Blocks.cs | the Arabic case (48/49) | FAIL |
| 6: `\u0085` added to JS `inline` class | the next-line case (1 failed, 37 passed) | FAIL |
| 7: block link not Self/Note returns "not found: no block ^id" in `DescribeAsync` | `A_block_link_to_an_ambiguous_or_missing_note_prints_as_before` (65/66) | FAIL |
| 7: `ReadVersionAsync` line removed | `Links_of_a_version_find_blocks_in_the_notes_as_they_are_now` (65/66) | FAIL |
| 8: `open = openFor !== null` | "closes again when the note changes" (1 failed, 9 passed) | FAIL |
| 8: "Looking for mentions …" line deleted | the retry test (1 failed, 9 passed) | FAIL |
| 8: BacklinksList renders mentions before "Linked from" | the order test (1 failed, 9 passed) | FAIL |
| 9: Reader loads `./mermaidRender` by dynamic import from a `useEffect` | before: passed the whole file (survivor, counter reset by `restoreAllMocks`); after the counter change: `shows a mermaid block as code` (1 failed, 21 passed) | Survivor fixed, then FAIL; restored 22/22 |
| 10: `mermaidCode(node)` replaced with null | fails ("Unable to find role="img"") | FAIL |
| 10: `remarkHideBlockIds` dropped from plugins | fails (paragraph text carries the marker) | FAIL |

## Mutations, overview

The per-task mutation tables are in the section above, under each task, so that each mutation sits with its RED/GREEN evidence. Summary of those that did not fail, and what was done:

| Task | Mutation | Outcome and action |
|---|---|---|
| 3 | (a) code lines not skipped | `inside code` case did not fail at first; case strengthened, then failed |
| 1 | (e) `"katex"` added to the isolation set | no test fails; accepted (test pins imports, not the allowed list) |
| 2 | (b) match `language-mermai` | survived (substring `includes`); check changed to exact class token, then 4 failed |
| 7 | lookahead dropped | survived; `An_embed_is_added_only_with_room_for_the_next_cut_line` added, then failed |
| 6 | (h) `[data-footnotes]` exclusion dropped | all pass (guards back each other up); exclusion removed in round 2 with a new test |
| 10 | TDD order (not a mutation) | red obtained by moving the file aside |
| 11 | (b) block-id mutation | skipped by ruling |
| Security | cancellation check in `MentionsAsync` | no test; recorded |
| Final | Reader dynamic-import mutation | passed the full file; counter changed, then failed |
| 12 | none | README only |

## Probe results

### Reader page CSP probe (Task 11, `task-11-report.md`)
- Setup: Node `http` server on 127.0.0.1 serving a `vite build` of the working tree; `GET /s` with `Security.ReaderPagePolicy` or `Security.ContentSecurityPolicy` copied verbatim; Playwright Chromium; `securitypolicyviolation` events collected; SVG Blob captured. Scripts and screenshots in the session scratchpad `csp-probe/` (`probe.js`, `run-drawn/`, `run-fallback/`).
- Spec flowchart (`flowchart LR\n  Sensor --> Controller --> Pump`) under `ReaderPagePolicy`, drawing build: FAIL. Light 375, light 1280, dark 375, dark 1280 each: `img` drawn, alt "Diagram (flowchart)", natural 466×66 (shown 335×47 at 375, 466×66 at 1280), 78 violations in every run. Breakdown of the 78: line 310 `style-src-elem` ×1; line 3 `style-src-attr` ×29 and `style-src-elem` ×1; line 312 (our `asImage` DOMParser) `style-src-attr` ×29 and `style-src-elem` ×1; line 128 `style-src-attr` ×17. No page errors, no dialogs, nothing left in `body`, no `foreignObject` in the Blob.
- Fallback build under `ReaderPagePolicy`: 4 runs, code block visible, 0 violations, 0 console messages, no request for any Mermaid chunk.
- Hostile sources (click, label, init), drawing build: app policy: `img` 208×66 / 307×66 / 307×84, 0 violations, no dialog, no `/x` request; reader policy: `img`, 60 style violations each, no dialog, no `/x` request. In all: no `img[src=x]`, no `[onerror]`, no `foreignObject`, `onerror` or `<script>` in the Blob, no SVG in the page. The init directive did not take effect (`htmlLabels` stayed false; label shown as literal text).
- Verdict: image passes, CSP check fails (78 violations), so the fallback applies (reader page shows code); open-items row added. Light-preference diagram on the always-dark reader page has barely visible edges (moot with the fallback). Controller viewed `reader-dark-375.png`: three boxes, labels, readable.

### CSS url() probe (`css-url-probe.md`)
- Setup: scratch copy of `web/` built with the "drawn" Reader (repo's unchanged `MermaidDiagram.tsx` and `mermaidRender.ts`), Mermaid 11.17.2 strict, app `ContentSecurityPolicy` verbatim; every request recorded in the browser and on the server; positive control (`background-image:url(/probe-control)` on a div) seen by both. Scripts `probe3.js`, `origin.js`.
- Result: YES, a same-origin GET goes out during render, 1 to 4 ms after the blob was made, from Mermaid's transient inline SVG, for: stateDiagram-v2 and stateDiagram (`style` with fill, stroke, filter, background-image, mask-image; `classDef k fill:url(...)` with `class S k` / `S:::k`), classDiagram (`style A fill:url(...),background-image:url(...)`, `classDef` + `class A:::k`; `cssClass "B" k` and stroke-only `classDef` gave no request), block-beta (`style a fill:url(...)`, `classDef` + `class b k`), C4Context (`UpdateElementStyle(a, $bgColor="url(/probe-c4)")`).
- Not requested: flowchart/graph (grammar refuses `(`: "Parse error on line 3"; `url%28..%29` and `url\28..\29` stay literal; `#40;` and `\(` refused), `%%{init}%%` themeCSS/themeVariables/fontFamily and front-matter `config:` (blocked by `secure`), `cursor:url` in state diagrams (lazy), erDiagram, mindmap, quadrant, pie themeVariables, gantt/flowchart `click href`, markdown label image, sequence note span, kanban.
- Origin: the final SVG as a blob `<img>` under the same CSP makes no request; the same SVG inlined in the DOM does, so the fetch comes from the render-time SVG in the live document. Requests carry the viewer's cookies. Image refusal confirmed: `A@{ img: "/probe-img", label: "x" }` and sequence `properties A: {"icon":...}` show "This diagram can't be drawn: images in diagrams aren't loaded." and request nothing.
- Re-probe after fixes (HEAD cadb8e6, `probe3.js` with `c4.json`): no `/probe-*` request in any of 33 sources (control still seen).
  - Refused "images in diagrams aren't loaded.": state style and classDef, class classDef and style, block style, C4 UpdateElementStyle, entity forms `url&#40;/..)`, `url&#x28;`, `url#40;..#41;`, CSS escape `\75rl(`, `A@{ img: "/probe-img" }`, kanban with an img tag.
  - Refused "HTML in diagrams isn't drawn.": sequence `$$x$$ <img src="/probe-katex">`, architecture `service s(server)["<img ...>"]` and `service s "<img ...>" [S]`, gitGraph (id and tag), mindmap root img, timeline, xychart, journey, flowchart label, erDiagram label, treemap.
  - Drawn, no request: flowchart `Sensor --> Controller --> Pump`, sequence `Hello<br/>there`, class `<<interface>>`, state, pie, sequence `$$\href{/probe-href}{x}$$` and `$$\includegraphics{/probe-incl}$$`, mindmap `::icon(/probe-icon)` (text only), flowchart markdown-label image, sankey, radar.
- Earlier HTML-sink finding (ledger, security re-review of 7f85d9c..e2c38cf): KaTeX in sequence, architecture iconText and event-modelling put DOMPurify-kept `<img>` into the live page; closed by refusing tag-like HTML except `<br>` in round 3. That finding is from the reviewer, not a separate probe number: request counts for it were not recorded beyond the re-probe above.

## Final gate

Full gate at c403b08 (the brief's commands, a fresh MongoDB before each server group):

| | Baseline | Final |
|---|---|---|
| Core | 2,266 / 3 skipped / 0 failed | 2,390 / 3 skipped / 0 failed |
| Server (Api, Mcp, Auth, Net) | 843 | 847 |
| Server (Accounts) | 413 | 413 |
| Server (Sharing) | 353 | 353 |
| Server (Welcome) | 20 | 20 |
| Server (Vaults, Skills, Search, Metrics) | 780 (6 skipped) | 780 (6 skipped) |
| Server (the rest) | 1,163 (1 skipped) | 1,163 (1 skipped) |
| Web | 131 files, 2,361 tests | 141 files, 2,503 tests |
| Web build | green | green; warning for the lazy `mermaidRender` (681.54 kB) and Mermaid's `cynefin` chunk, accepted; `main` and `s` import only `blockIds`, `markdown` and `vendor` statically |
| PDF sidecar | 9 files, 198 tests, build green | 9 files, 198 tests, build green |

All 0 failed. The WebDAV `litmus` job runs on the pull request only (nothing in `src/Kumbu.Server/Dav/` changed).

## Security review

A fresh subagent got spec §1, §2.2, §3 and §5.1 and the diff (not the controller's reasoning) and answered the brief's four questions:

1. **Can a diagram's source run anything in the page or reach the DOM as markup?** No. `securityLevel: "strict"`, `htmlLabels: false` and a widened `secure` list (a `%%{init}%%` directive or front matter can't undo them), `click` callbacks never bound, the SVG normalised in an inert `DOMParser` document and shown only as a blob `<img>` outside any link; error line, alt text and source reach React as text. Note: Mermaid itself puts its (sanitised) SVG into the live document while measuring — that is Mermaid's, and the reader page doesn't run it at all.
2. **Did either CSP change?** No (`Security.cs`, `index.html`, `s.html` untouched).
3. **Can the appendix, block lookups or mentions read outside the note's vault or past the caller's permission?** No: everything resolves through `NoteLinks` within the note's vault and reads through the caller's scoped store (the same DI registration as `backlinks`); `--checkpoint` and `--version` get no appendix; the mentions endpoint has backlinks' existence check and is in `HiddenVaultTests`, `MarkerTests` and the OpenAPI list.
4. **Does `cat`'s own note text stay byte for byte?** Yes: the output is the stored text (CRLF, no final newline, near the output limit, sections, blocks), then the block.

What it found besides, and what was done:

| Finding | Done |
|---|---|
| Quadratic masking in unlinked mentions (a line of `[` took ~90 s); later a second quadratic in the span check (~15–20 s for a 1 MiB note); no cancellation check | Fixed: one-pass closers and a binary search over sorted spans, `ct` checked per note; timing tests (80,000 spans in 1.4 s for the class) |
| Mermaid image shapes `A@{ img: "/api/…" }` fire a same-origin GET with the viewer's cookies while rendering | Fixed: refused before Mermaid sees the source — "This diagram can't be drawn: images in diagrams aren't loaded." |
| `diagramAlt`'s regexes polynomial on whitespace | Fixed: line-based |
| CSS `url()` in `style`/`classDef`/`:::`/C4 styles (state, class, block, C4 diagrams) fetch during render — confirmed in Chromium | Fixed: sources whose decoded text holds a resource-loading CSS function (`url(`, `image-set(`, `image(`, `cross-fade(`, `element(`, `src(`) are refused with the same line |
| HTML sinks in Mermaid (KaTeX in sequence diagrams, architecture icon text, …) put DOMPurify-kept `<img src="/api/…">` into the live page; entity-encoded `url&#40;` | Fixed: HTML, CSS and Mermaid entity escapes decoded first; anything tag-like other than `<br>` refused — "This diagram can't be drawn: HTML in diagrams isn't drawn." (`<<interface>>` annotations still draw) |
| The appendix header can be forged by text in a note | Recorded (the spec fixes the header's wording); open point below |
| An agent can't tell whether a note ended with a newline before the appendix | Recorded; open point below |
| `NoteLink.Parse` (links, backlinks, the web) has the same quadratic as the old mention masking | Pre-existing, out of scope; open point below |

A final Chromium re-probe at cadb8e6 under the app's CSP: 33 hostile sources (every route above, encoded variants, KaTeX `\href`/`\includegraphics`, mindmap icons, kanban, gitGraph, timeline, xychart, journey, ER, treemap) made no request; the spec's flowchart, a sequence diagram with `<br/>`, a class diagram with `<<interface>>`, a state diagram and a pie still draw.

## Decisions made without Chris

Each with the options seen and why this one (the rules: breaks nothing; true to the application; no new hole; true to the spec).

1. **Block finders' shape (open point 1).** Options: one shared grammar generated for both sides; one finder per side with a shared cases file; the web asking the server. Chose a finder per side (`Kumbu.Core.Sections.Blocks`, `web/src/note/blocks.ts`) held to `web/src/note/blocks.cases.json` (36 cases, read by `BlocksTests` and `blocks.test.ts`), as `comments.cases.json` does. Rules: an id `[A-Za-z0-9-]+` after a space at the end of a paragraph's, item's, quote's or heading's last line; on its own line after exactly one blank line it names the list, table, quote, callout or paragraph above; right below a paragraph without a blank line it names the paragraph; not in code (fenced or indented), not in front matter; first wins; ids compared ignoring case; ASCII digits and whitespace only on both sides. What Obsidian does with a repeated id was not found out, so the spec's "first wins" holds.
2. **Wide diagrams (open point 2).** Lightbox or scrolling: the app's lightbox is a route for vault attachments and can't take a blob, so the image is fitted to the column and a **Full size / Fit** button (shown only when the diagram is wider) lets it scroll in its frame.
3. **API for unlinked mentions (open point 3).** A parameter on `/api/backlinks` would change that answer's type in the OpenAPI document; chose `GET /api/mentions/{vault}/{path}?archive=` beside it, returning `MentionView { path, line, text, marks[{start,length}] }`. `schema.d.ts` was generated against a locally running server; the committed file was already stale against master (e.g. `/api/s/ai-links` missing, other order), so only the generator's output for the new path and schemas was spliced in by script, keeping the diff to this work (the full generated file was compared byte for byte by the reviewer). A full regeneration is an open point.
4. **Mermaid under the reader page's CSP (open point 4).** Found out with a real Chromium render: right image, 78 violations → the reader page shows the code block; `open-items.md` has the row; no CSP changed.
5. **`cat`'s appendix: which embeds.** "On a line of its own, as the web app shows them": a line of nothing but `![[…]]` embeds, also as a list item or in a quote (the web shows those as embedded notes). `![](note.md)` images of notes are not appended (spec says `![[…]]`). Each (target, subpath) once — two sections of one note are two embeds.
6. **`cat`'s appendix: honesty.** If the note changed between `cat`'s read and the re-read used for line numbers, no appendix; a note too long alone lists only the embed lines; the appendix is cut at an embed boundary within the output limit, and dropped entirely when not even its header and cut line fit.
7. **`backlinks` usage line.** The manual pins every summary at column 34 and the help list to the manual: `backlinks [--archive] <path>` stays there, `--unlinked` is named in the manual's summary, and `help backlinks` opens with `backlinks [--archive] [--unlinked] <path>`.
8. **`backlinks --unlinked` and the archive.** As `backlinks`: archived notes are left out, with the same kind of hint when only archived notes mention it. The web's section leaves the archive out too (spec §5.1 "unless asked for"); "Linked from" keeps its existing behaviour.
9. **Headings carrying an id** (`## Decision ^d`): `#Decision` addresses and the outline keep the text as today (spec: `#Heading` behaves as today), so `![[Ruling#Decision]]` doesn't find such a heading; recorded as an open point.
10. **Navigation to a block or line.** React Router location state (`NoteAt = { block } | { line }`), not the URL: a hash or query would put note details into the address bar (the reason the app already keeps footnote jumps out of it). The jump goes to the element with the greatest `data-line` at or above the target (the outermost on ties), opens a folded callout, flashes 1.6 s (a static outline with reduced motion), and re-centres while diagrams and embeds above it load until the reader scrolls. An unknown id: "There is no block ^id in this note." Block ids resolve on the stored text. Checkpoint views ignore the state.
11. **Diagram safety beyond the spec.** Refusing sources with resource-loading CSS functions, image shapes or HTML (except `<br>`) — the spec's "never anything from the diagram running in the page" and the app's rule that a note never makes the browser fetch anything; the cost is that such diagrams show as code with a one-line reason.
12. **Theme.** `useDarkScheme` reads `scheme.light` of `<html data-scheme>` via a `MutationObserver` (no theme store exists); absent or unknown → light.
13. **Order of work.** Tasks ran 3, 4, 1, 7, 8, 9, 2, 11, 10, 12, 5, 6 rather than the plan's order (Core tasks in parallel with web ones on disjoint files; the reader-page probe early because it was the first real render). No task depended on a later one's interface.

## Deviations, surprises and open points for Chris

- **The reader page shows Mermaid as code**, not as a picture (spec §1's last example bullet and §2.1). Recorded in `open-items.md`; a later attempt has to avoid Mermaid's live-document measuring and the HTML parse of its output.
- **The `mermaidRender` chunk is 679 kB** (gzip 166 kB) and Mermaid's own `cynefin` chunk 691 kB, over `chunkSizeWarningLimit` (600 kB): the build prints the warning; both are lazy, neither the `main` nor the `s` entry imports them statically. The limit's comment ("pdf.js … is the one chunk over") is now inaccurate.
- The reader page's own CSS root is always the dark `kumbu` scheme; were diagrams ever drawn there, `dark` should follow the page, not `prefers-color-scheme`.
- **Forgeable appendix header**: text in a note (or in an embedded note by another member) can contain the header line. A random per-call marker would close it but changes the spec's wording.
- **Trailing newline**: `abc` and `abc\n` both print `abc\n\n--- embedded notes …`.
- **Pre-existing quadratic link parsing** in `NoteLink.Parse`/`MarkdownLink` (links, backlinks, the web view): measured 82 s on a 160k-character line of `[`. Worth an issue.
- **`web/src/api/schema.d.ts` is stale** against master beyond this diff; a full regeneration is its own change.
- Smaller known limits, each recorded in the ledger and below in open items where they matter: the outline shows `^id` in heading text; a `^id` line run on under a list item or quote is hidden though it names nothing; loose lists are cut at blank lines and a paragraph interrupted by any list-marker line (both sides alike); `[[#^id]]` inside an embedded note stays plain text; a wall-clock bound in `MentionsTests.Many_masked_spans_take_linear_time` failed once under heavy parallel load (passed alone and in the gate); `commentRanges.test.tsx › reads comments in linear time` failed twice under load (unchanged code; passes alone).
- No changelog entry (no "Unreleased" heading); Welcome notes untouched; `docs/concept.md` has no feature list; README gained three lines.
- No spec outside this topic looked wrong.

## Hand-test checklist

Runnable on staging (or locally through Playwright). Set up: a vault `Work` with the spec's example — `/Work/Pump/Plan.md`, `/Work/Pump/Ruling.md` (with `## Decision`), `/Work/Diary/2026-11-12.md` ("Talked about the plan with Bob."), `/Work/Pump/Tasks.md` ("- see [[Plan#^controller]]").

Web app, light theme (Settings → a light scheme), laptop width:
1. Open `Plan`. Passes: the flowchart shows as a picture (Sensor → Controller → Pump) in light colours; "The pump needs a new controller." shows without `^controller`; the embed frame "Ruling › Decision" shows the Decision section.
2. Under the diagram, press **Show source**. Passes: the button says Hide source (`aria-expanded="true"`) and the code block shows the source; press again to hide.
3. Inspect the diagram image (devtools). Passes: an `<img>` with a `blob:` source and alt "Diagram (flowchart)"; no `<svg>` from the diagram in the page.
4. Switch to a dark scheme with the note open. Passes: the diagram redraws in dark colours without a reload.
5. Edit `Plan` to add a second block `\`\`\`mermaid\nflowchart LR\n  A -->\n\`\`\``. Passes: "This diagram can't be drawn: …" above the code block; the rest of the note shows normally.
6. Add a block with `stateDiagram-v2\n  [*] --> S\n  style S fill:url(/x)`. Passes: "This diagram can't be drawn: images in diagrams aren't loaded." and the code; the network panel shows no request to `/x`.
7. Open `Tasks`, tap "Plan" in "see Plan › ^controller". Passes: `Plan` opens, scrolls to "The pump needs a new controller." and highlights it briefly; the address bar has no hash.
8. Change the link to `[[Plan#^contoller]]`, tap it. Passes: `Plan` opens at the top with "There is no block ^contoller in this note."
9. Back in `Plan`, below "Linked from · 1" (Tasks): **Unlinked mentions**, closed, no count. Open it. Passes: `2026-11-12` with "Talked about the plan with Bob.", "plan" marked, folder `Work/Diary`; tapping opens the diary scrolled to that line, highlighted briefly.
10. In Write mode, `Plan` shows `^controller` and the Mermaid source as written.

Phone width (375 px, dark theme):
11. Repeat 1, 7 and 9. Passes: the diagram fits the column (no horizontal page scroll); the mentions button is at least 48 px high and reachable by keyboard/screen reader; with a wide diagram (e.g. ten nodes in a row) **Full size** appears and the image scrolls within its frame.
12. With "Reduce motion" on in the OS, repeat 7. Passes: the block gets a static outline for a moment, no fade.

Reader page of a published note:
13. Publish `Plan` (⋯ → Publish…), open the link in a private window. Passes: the page shows the Mermaid block as code (not a picture — see deviations), "The pump needs a new controller." without `^controller`; the console shows no CSP violation.
14. **Copy as Markdown**, **Link for your AI** → fetch it. Passes: both contain the ```` ```mermaid ```` source and `^controller` as written.

Agents (`sh` through MCP, or the console for the reading commands):
15. `cat /Work/Pump/Plan.md`. Passes: the note exactly as stored, then a blank line, `--- embedded notes: shown for reading, not part of this note's text (which ends above) ---`, `[line 10: ![[Ruling#Decision]] → /Work/Pump/Ruling.md#Decision]` and the Decision section; the version token is Plan's.
16. `cat /Work/Pump/Plan.md | wc -l`. Passes: counts the note only.
17. `cat "/Work/Pump/Plan.md#^controller"`. Passes: `The pump needs a new controller. ^controller`. `cat "/Work/Pump/Plan.md#^contoller"`: `cat: /Work/Pump/Plan.md#^contoller: no such block (…)`.
18. `links /Work/Pump/Tasks.md`. Passes: `1: [[Plan#^controller]] → /Work/Pump/Plan.md#^controller`; with `^contoller`: `→ not found: no block ^contoller in /Work/Pump/Plan.md`; `links /Work` lists that line among the broken.
19. `backlinks --unlinked /Work/Pump/Plan.md`. Passes: `/Work/Diary/2026-11-12.md:1:Talked about the plan with Bob.`; `backlinks /Work/Pump/Plan.md` is as before.
20. `mv /Work/Pump/Plan.md /Work/Old/Plan.md`, then `cat /Work/Pump/Tasks.md`. Passes: the link still carries `#^controller` (path form updated where `mv` rewrites); move it back afterwards.
21. In a vault shared with Bob (write role), Bob edits `Ruling`; Alice runs `cat /Garten/plan.md`-style on a note embedding it. Passes: the notice names the embedded note as last written by another user.

## The pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/195 (opened as a draft, marked ready after this report was pushed; not merged).

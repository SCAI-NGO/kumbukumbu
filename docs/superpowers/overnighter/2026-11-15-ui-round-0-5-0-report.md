# Report: the UI round of 0.5.0

Run of 2026-11-15, from 05:51 to about 08:15 UTC, on branch `feature/ui-round-0-5-0`. Spec: [`2026-11-15-ui-round-0-5-0-design.md`](../specs/2026-11-15-ui-round-0-5-0-design.md) (not edited). Brief: [`2026-11-15-ui-round-0-5-0-brief.md`](2026-11-15-ui-round-0-5-0-brief.md). Plan: [`2026-11-15-ui-round-0-5-0.md`](../plans/2026-11-15-ui-round-0-5-0.md).

**State:** every section of the spec is built and tested: §2.1 (the toolbar), §2.2 (the hint, Move to properties, + Add property), §2.3 (all four calendar changes); §2.4 needs nothing. Nothing of the priority list had to be left out. The pull request is open and not merged: https://github.com/SCAI-NGO/kumbukumbu/pull/204.

## Environment

- **Machine:** 4 CPUs, 15 GB RAM, Linux 6.18, the clock in UTC. No restart of the worker.
- **Node:** 22 was installed; I put Node 26.13.1 into `/opt/node26` from the nodejs.org tarball and pnpm 10.32.1 with `npm install -g`, as the last runs did. Nothing was added to the project.
- **.NET:** the SDK was there (`global.json`, roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Docker:** `dockerd` was already running this time (Docker 29.9.2, Compose 5.6.1). The e2e stack ran behind the proxy with `E2E_PROXY_CA=/root/.ccr/ca-bundle.crt`, as the UI tests' report describes; nothing to fix.
- **Superpowers:** available. I used `writing-plans` and `subagent-driven-development`: a fresh implementer per group of tasks, then a fresh reviewer per group (spec compliance and code quality), the findings sent back to the implementer, the lead committing nothing but the plan and cherry-picks and pushing after every task. Parallel work as the brief allows: Task 8 and Tasks 9–10 were built in their own worktrees and cherry-picked; §2.1 and §2.3.2 (both in `TreeDrawer.tsx`) never ran at the same time.
- **The e2e suite's screenshots:** each run rewrites `docs/superpowers/overnighter/2026-11-14-ui-tests-0.5.0/*.png` (`web/e2e/support/test.ts` line 12), which hold the UI tests run's evidence. I restored them with `git checkout --` after every run and committed none.
- **The clock on the board:** four log lines on the Factory card (06:20 to 07:15) carried estimated times that ran ahead of the clock; a correction line says so.

## The plan and its review

The plan was reviewed by a fresh subagent that had the spec, the brief and the plan. Its ten findings and what I did with them (all folded into the plan's section "The plan review's findings, folded in", commit `bbe7598`):

1. **To-do switch re-sent `when`.** `EntryView.when` is in the caller's zone, so sending it back could rewrite the line; the request is now exactly `{ path, task }`, and the tests assert the real URL (POST `/api/vaults/{vault}/calendar/set`). Done.
2. **The back link would survive a reload** (browsers keep `history.state`). The state carries a per-page-load id; another load's state shows no link. Also: a `/calendar/<vault>` origin is accepted only for the note's own vault. Done.
3. **The toolbar doesn't fit 375 px with the plan's padding.** Budget set (301 px, slimmer + New note, no gaps between 48 px icons), width 0 means "show everything" in jsdom, a live-resize test; the e2e example measures the row. Done.
4. **Values that wouldn't read back, the BOM, escapes in the box.** Both writes read their result back and refuse otherwise; a BOM stays first; `unquote` undoes `\\` and `\"`. Done.
5. **A detection row contradicted the rule** (`Remember: buy milk` is a plain key). Replaced by `Buy milk: today`. Done.
6. **No calendar refresh, no settle.** `editorSettled` before and `afterEntryWrite` after Move and Add. Done.
7. **MarkdownView hook-up details** (`hr` without a node, memo deps, the embed mutation that couldn't fail, Move shown while the vault list loads). All taken; the embed mutation kept, as the implementer found a way it does fail (an embedded note's lines).
8. **Splitting `FolderButton`** (which button keeps `ref`, `data-tree-row`, `aria-current`; the alignment). Taken; the alignment was then fixed properly after the UX check (below).
9. **What the plan left out** (the board card, per-task reviews, `dotnet build` per phase, run lines, `calendar.spec.ts:63`, light and dark in both widths, the board in `Work`, the badge test, the PR's sections, the checklist). All taken.
10. **Security:** the back link's vault check (taken, see 2), and `mayChange` (asks `maintain` for `Board/Board.md`) for Move and Add (taken).

## Task 0: planned against measured

Planned 15 minutes; measured about 12 (05:52 to 06:04 for the web gate and the build, the e2e baseline from 05:57 to 06:01 in the background).

- Web gate: **168 files, 3132 tests passed** (147.8 s); the build passed.
- End to end (`e2e:0.5.0`): **parallel 105 passed, 5 skipped; serial 45 passed**, no failure, no flake.
- `commentRanges.test.tsx` and `SkillPage.test.tsx` passed in the full run; nothing to repeat.

## The tasks: commits and test output

Counts are as the test runner printed them; "red" is the run after the new tests and before the change.

| Task | Commit(s) | Red | Green |
|---|---|---|---|
| 1 ‹ Calendar / ‹ What's on | `bc7a096` | backLink: module missing; NoteView "the way back" 3 failed; What's on and Calendar 4 failed | backLink 19/19; the four files 110/110; neighbours 53/53 |
| 2 Calendar in the phone's dock | `732b1b3` | MobileShell 2 failed; Badge 9 failed | MobileShell, Badge, DockedSidebar 94/94 |
| 3 Make it a to-do / Just an appointment | `85c6f32` | DateDialog 5 failed; MobileShell 1 failed | DateDialog, MobileShell, TreeDrawerFolders 125/125 |
| 4 The folders open their views | `827e400` | 6 failed, 35 passed | 41/41; neighbours 179/179 |
| §2.3 review fixes | `53a17de`, `d63c909`, `a0f8a3b`, `0cd748b`, `010928e` | hideAgain (gate), backLink/TagFilter 3, folders 1 | `src/discreet src/calendar src/shell` and NoteView: 56 files, 755 passed |
| 5 The toolbar row | `f1ea1c2` | TreeTools and Badge 15 failed, 21 passed | 36/36 |
| 6 The cut by width | `65d387f` | toolbarCut missing; TreeTools 2 failed | 36/36; `src/shell` and Badge 428 passed |
| 7 The chips | `26f2403` | 6 failed | 51/51; full suite 171 files, 3318 passed |
| §2.1 review fixes | `2afd255` | 2 failed, 37 passed | 39/39; `src/shell`, Badge, `src/recent` 453 passed |
| 8 Frontmatter text functions | `5d86919`, `4c1a675` (cherry-picked from `d602835`, `1ce1a97`) | module missing; then 14 failed, 103 passed | 82/82; then 5 files, 156 passed |
| 9 The hint and Move to properties | `c958e8e` | 8 failed, 135 passed | 143/143; `src/note` and MobileShell 786 passed |
| 10 + Add property | `bed9608` | 12 failed, 138 passed | 150/150; neighbours 1014 passed |
| §2.2 review fixes | `6f4fd8a` | 9 failed (see the mutations) | 32 files, 850 passed |
| 11 End to end | `1a3c68b` | first run: 1 failed in each phase | parallel 105 passed, 5 skipped; serial 49 passed |
| UX fixes | `12809d5`, `fa37a1d`, `a67e849`, `5ce4357`, `8ba4a89`, `bd75888`, `be656ec` | 3, 1, 1, 1, 3, 1, 1 failed | 82 files, 1406 passed |

Phase gates: after §2.3 the full suite found one test left behind (`hideAgain.test.tsx` still looked for the badge on the top bar's button; fixed in `53a17de`); after §2.2: **171 files, 3354 tests passed**, the build passed, `dotnet build --configuration Release` 0 warnings, 0 errors, `git diff origin/master --stat -- src tests` empty. The final gate is below.

## Mutations, each on its own

Each was applied alone, the named test file run, then restored with `git checkout -- <file>` (the first UX fix's two from a scratch copy, as it wasn't committed yet).

**Task 1:** `cameFromOf` accepting any address → 5 failed, among them "refuses another address" and "reads a calendar back only for a note of its own vault". `withCameFrom` dropping the old state → "keeps the state it joins" and NoteView's "…still opens at the line it was opened at". The link always shown → "shows no way back for a note opened any other way".
**Task 1 fix (tag):** BackLink without state → "takes What's on's tag back with it…"; `useTagFilter` ignoring `initial` → "back on What's on with the tag it took along…"; What's on not passing its tag → "a note opened from What's on, narrowed, takes the tag along…".
**Task 2:** no badge on the dock's button → "sits between Files and Search, carries What's on's count…"; the button at desktop widths → "isn't in the dock at desktop widths"; the top bar's badge back → Badge's "the badge sits on What's on…" and the dock test.
**Task 3:** offered for a closed to-do → "a closed to-do, which would lose its closed and its reason"; `clearUntil: true` sent → both request tests; the `canWrite` check dropped → "a note one may not write".
**Task 4:** the root check dropped → "a Calendar folder further down is an ordinary folder"; Board without a board → "a Board folder without a board is an ordinary folder"; the chevron calling `open` → "the chevron opens and closes the folder, and goes nowhere". **Fix:** the description only the count, or none → "says the folder's count and its mark…".
**Task 5:** New note without write → "offers no New note and no Upload to someone who only reads" and the empty tree's "says only that to someone who reads"; the header's ⋯ kept → "is one row directly above the folders…" and the docked sidebar's test; Sort's label fixed → both "the tree's order" tests.
**Task 6:** ⋯ dropped when narrow → "then cuts from the right… New note and ⋯ always stay" and "moves Upload, Sort and Recent changes to the top of ⋯…"; the drop order reversed → 9 failed, among them the 400 px and 360 px rows. **Fix:** the effect's deps back to `[open]` → "cuts the row again when New note comes…"; the `id` filter removed → the "once each" check; focus only to the opener → the focus check.
**Task 7:** the pressed-chip rule removed → "a pressed chip stays shown while it is pressed…"; all eight chips → 3 failed, among them "shows no chips where no note carries a flag".
**Task 8:** fence skip dropped → the three fence rows; lines joined with `\n` → five CRLF rows; the `exists` check dropped → "refuses a key the frontmatter has, naming the first" (and Add's "refuses a name that is there"); `yes` plain → `yamlValue > "yes"` (the row was added when this mutation first survived); the "line before is blank or ATX" rule dropped → "a setext heading over prose" and "a block right after the frontmatter's closing line"; Add's read-back dropped → "refuses a value that wouldn't read back". **Survived:** line 0 allowed as an opening (79 passed): the frontmatter is skipped before, so that rule can't change a result; the real-frontmatter row guards the skip instead (scanning from line 0 fails three rows). **Fix:** repeated keys → "a key twice"; the HTML skip → three HTML rows; the quote rule → three rows, the flow-map rule → one; `commented`'s quote check → two; the control-character check → four rows.
**Task 9:** the button without write → "without write, says where properties go and offers nothing" and "offers the move only once the vault list says…"; a save on a refusal → "refuses a key the properties have already…" and "moves nothing when the properties at the top can't be read…"; the hint in nested views → "is never shown inside an embedded note, whose lines are its own".
**Task 10:** the value always plain → "writes a value YAML would take for something else in quotes"; the menu item without write → "isn't offered where the box shows, nor without write" and two read-only menu tests; appending after the closing `---` → "writes name: value as the properties' last line…" and the quoting and conflict tests.
**§2.2 fixes:** `afterEntryWrite` dropped → both "asks the calendar again after…"; the settle check skipped → both "writes nothing while the editor's text isn't saved…"; the pre-settle note used → "moves in the text as the editor's save left it, on that version"; `mayWrite` at any level but read → "the board's own note, below maintain, offers neither…"; the menu item gated on `write` → "isn't offered on the board's own note below maintain"; `frontmatterUnreadable` ignoring the parse → two rows; the read-back wording swapped → "says why when the moved keys wouldn't read back".
**UX fixes:** `overflow-y-auto` removed → "a menu taller than the room on its side scrolls within that room"; `scrollIntoView` removed → "the arrow keys bring the item they reach into view"; `min-h-11` back → "every item is at least 48 px high, in a section too"; the chip's container rule removed → "lets Search's mode give way before its word…"; the chevron button's `pl-1.5` removed → "the chevron button holds the chevron and the folder icon…"; the renaming check dropped → "offers no Change it in Write mode while the note is being renamed…"; the edge back to `border-line` → "draws the hint's buttons with the accent's text colour…"; the icons swapped → "shows Sort in ⋯ with the icon the row's Sort has…".

## The UX check

A fresh subagent ran it with Playwright against the e2e stack on `localhost:8080` (never a real host), at 1280 × 800 and at 375 × 812 with touch, in light and dark, over §2.1 to §2.3 and the §3 example (which passed on the phone in both schemes). Its findings and what became of them:

1. **The vault's ⋯ ran off the bottom of the screen** (720–760 px tall, opening at y ≈ 135–200; Export as ZIP… and Delete vault… out of reach). Fixed: every menu is as high as the room on its side, scrolls within it, and opens to the side with more room (`12809d5`).
2. **The dock's Search lost its word** at 375 px (119 px field) while the "hybrid" chip stayed. Fixed: the mode gives way first (`a67e849`).
3. **The Calendar and Board rows sat 12 px right of the other folders.** Fixed: the chevron button holds the chevron and the folder icon, so icon and name line up (`5ce4357`).
4. **The docked sidebar's default width (318 px) cuts the row as the phone does**: Upload, Sort and Recent changes are in ⋯ until about 450 px. **Recorded for Chris** (open points): it follows the spec's rule ("whatever fits at 48 px stays"); widening the default sidebar is a decision outside this spec.
5. **Menu items were 44 px high**, the moved ones and the new note-menu items included. Fixed: every menu item is at least 48 px (`fa37a1d`).
6. **The refusal "`aliases` is already in the properties: change it there." left the person in Read mode.** Fixed: with write, it adds **Change it in Write mode**, which does what the top bar's Write does (`8ba4a89`).
7. **Move to properties' edge was 1.1:1 (dark) and 1.2:1 (light).** Fixed with the accent text colour, at least 4.5:1 in every scheme (`bd75888`).
8. **Small ones.** Sort's icon in ⋯ differed from the row's: fixed (`be656ec`). Recorded, not changed: the dock's Calendar is named "Calendar" (the spec) with the tooltip "What's on" (where it goes); "+ Add property" stands just below the box rather than inside its border; the dialog's fields are labelled Name and Value, as the spec says.

Checked and fine, by the UX check: the row's order and names at every width, every row target 48 × 48 with its tooltip, the live cut, one vault menu; Sort's label; the chips (only Red and Blue in use, wrapping with all eight, a pressed one staying); a reader (Bob) gets no New note, no Upload, the hint without its button; the hint above its block, Move writing the expected bytes, no hint for a rule over prose; Add from the box and the menu, `yes` quoted, both refusals worded as the spec; ‹ Calendar and ‹ What's on 48 px high, leading back, absent when the note is opened directly; the folder taps and chevrons with the spec's names; the dock's Calendar 48 × 48 with the badge; the to-do switch keeping every other byte, not offered for a closed to-do or a day note; nothing scrolling sideways.

## End to end

The first run on the branch (before Task 11) found one test the spec alters (`flags #13`, the old Name / Last changed switch, rewritten around Sort) and one flake (`flags #2`: Playwright reported the note menu's Flag item "outside of the viewport" once; it passed in the next run and in 4 repeated `@flags` runs; 1 in 6). `explorer.spec.ts`, `recent.spec.ts`, `calendar.spec.ts` (line 63) and `smoke.spec.ts` #6 were updated to the toolbar; `whats-on.spec.ts` already looked for the badge on the dock (Task 2). The new tag `@properties` is in `e2e:0.5.0`; `web/e2e/properties/ui-round.spec.ts` runs the §3 example in light and dark in `serial` (1280) and `serial-phone` (375, touch): 4 tests.

The two final runs, each from a fresh stack (`down`, build, `up`, setup, tests, `down`), after the UX fixes:

The final gate before them: the web gate **171 files, 3365 tests passed** (161 s), the build passed, `dotnet build --configuration Release` 0 warnings, 0 errors, `git diff origin/master --stat -- src tests` empty.

| Run | Parallel (setup, desktop, phone) | Serial (serial, serial-phone) |
|---|---|---|
| 1 | 105 passed, 5 skipped (58.8 s) | 49 passed (2.4 min) |
| 2 | 105 passed, 5 skipped (1.1 min) | 49 passed (2.4 min) |

Both runs identical: no failure, no flake, no retry. The 5 skipped are the suite's own (as at Task 0); the serial phase's 49 are the baseline's 45 and the 4 new tests of the §3 example.

## Decisions made without Chris

Each by the brief's rules: breaks nothing, true to the spirit, no new hole, true to the spec.

1. **The order** (open point 1): §2.3, §2.1, §2.2, as suggested; Task 8 (new files only) and Tasks 9–10 in worktrees, in parallel with §2.3 and §2.1; never §2.1 and §2.3.2 at once.
2. **Frontmatter in the client** (2): pure functions in `web/src/note/frontmatterText.ts` (`findPropertyBlocks`, `moveToProperties`, `addProperty`, `yamlValue`) with table tests over CRLF, no final newline, an empty frontmatter, a body starting with a blank line, a BOM; saved through `useSaveNote` after `editorSettled`. Both writes read their result back with `parseProperties` and refuse what wouldn't read, so the box never turns into raw text by our hand.
3. **"Looks like frontmatter"**, stricter than the spec's words where they leave room ("when in doubt, no hint"): the opening `---` must follow a blank line or an ATX heading (after prose it is a setext heading, which the spec excludes); keys must be plain keys (letters, digits, `-`, `_`, starting with a letter: the same rule as Add's names), so prose such as "Buy milk: today" gets no hint; no blank line, repeated key, unclosed quote or unreadable flow map inside; fences and HTML blocks (`<!--`, `<pre>`, `<script>`, `<style>`, `<textarea>`) are skipped like code. A block directly after the real frontmatter's closing line gets no hint (rare, and the line before it isn't blank).
4. **Escapes in the box:** `unquote` in `properties.ts` now undoes `\\` and `\"` inside double quotes, and a `#` inside a complete quoted value is no comment. Both are YAML's own rules; before, such values showed with their backslashes or turned the box into raw text. Other readers (`board/cardLine.ts`) only read.
5. **Add's values:** line breaks and control characters are refused ("That value can't be written as a property here."); the spec says the value is text, and a line break would need block text, which the box can't read. Non-ASCII letters (`Zoë`) are quoted, the cautious reading of "letters".
6. **The web types** (3): written by hand as `web/src/calendar/api.ts` does; `schema.d.ts` not regenerated.
7. **Existing tests** (4): changed to the new behaviour, none loosened (the reviewers compared old and new assertions).
8. **A conflict outside the editor** (6): nothing more is written; "This note changed elsewhere; try again."; the note shown is the newer one (the hook puts it in the cache). An unsaved editor says the calendar's `UNSAVED` sentence.
9. **Frontmatter without a box** (7): the note's ⋯ offers Add property… with write whenever no box with keys is shown; Add and Move are refused with the reason when the frontmatter can't be read.
10. **The chevron as its own button** (8): for root `Calendar/` and `Board/` only; after the UX check the chevron button holds the chevron and the folder icon (48 × 48), so the rows line up with the others; those two rows are 48 px high where the others are 44.
11. **Chips when no note carries a flag** (9): the group is hidden, "Any flag" included, unless one is pressed.
12. **Navigation state** (10): `cameFrom` is merged into the state (`withCameFrom`), never replacing a jump to a block, heading or line; it carries a per-page-load id, so a reload or a state from elsewhere shows no link, and a calendar origin is accepted only for the note's own vault. While discreet vaults are shown, What's on keeps its tag out of the address; the back link carries it in the router's state, which `neutralWindow.ts` keeps in memory, not in `history.state`, while any discreet vault is known.
13. **Wording that follows** (11): the empty tree says "No notes yet. Make the first with + New note above." (with write) or "No notes yet." (without).
14. **Search mode** (12): Sort and Recent changes drop out; Search the contents… stays.
15. **The e2e tag** (13): a new tag `@properties`, added to `e2e:0.5.0`.
16. **The to-do switch's write:** exactly `{ path, task }`, so the server keeps `when`, `until` and `upcoming` as they are.
17. **The dock's Calendar** only below the desktop breakpoint (the dock shows at every width; the spec keeps the desktop unchanged).
18. **Narrow toolbar:** the three rarer tools go one at a time (Upload, Sort, Recent changes) before Find, Board and Calendar; an item moved into ⋯ replaces the menu's own item of the same action (by an `id`), so nothing is in the menu twice; the moved Sort says the spec's sentence.
19. **Menus** (UX check): every menu item is at least 48 px high, and every menu fits the room on its side and scrolls; this changes all menus a little, in line with the 48 px rule.
20. **Change it in Write mode** (UX check): added after the spec's refusal sentence, with write, so the person is led to where the change is made.

## Deviations, surprises and open points for Chris

- **The docked sidebar's default width** (318 px) folds Upload, Sort and Recent changes into ⋯ on the laptop until the sidebar is widened to about 450 px. That is the spec's rule applied; if the laptop should show the full row by default, the default width is the lever. In `open-items.md`.
- **Board cards and `maintain`:** spec §6 says "a board card wants `maintain`". `mayChange` (`web/src/api/notes.ts` line 45), which every write of the web app follows, asks `maintain` only for `Board/Board.md` and the `Board` folder itself; a card (`Board/x.md`) can be changed at `write`, in the editor as today. Move and Add follow `mayChange`, so they are offered where the editor is; I didn't make them stricter than the editor. If cards should want `maintain`, that is a change to `mayChange` (and the server's rule), outside this round.
- **Notes opened in Write mode from the calendar** (+ Today, + This week, a new entry) show ‹ Calendar once back in Read mode (the header's back link is a Read-mode element, as the dock is).
- **The flaky `flags #2`** in the first e2e run (1 in 6), not reproduced since.
- **`Settings.test.tsx` › "deletes the account…"** failed once under the full suite's load in an implementer's run and passed alone and in every gate since.
- **Nothing in `src/` or `tests/` changed** (`git diff origin/master --stat -- src tests` empty); no new package.
- No spec outside this topic looked wrong.

## Hand-test checklist

On staging, signed in as yourself, in a vault of your own (say `Work`) with a board, a few flagged notes and one dated entry later today. "Phone" means the phone, or a 375 px window with touch; "laptop" the docked sidebar.

**§2.1 The toolbar**
1. Laptop: under the vault's name, one row above the folders: **+ New note**, then icons, then ⋯. Nothing beside the vault's name. Passes if every icon shows its name on hover.
2. Laptop: widen the sidebar by dragging its edge. Upload, Sort and Recent changes join the row as it grows, and leave it as it narrows; ⋯ is always there.
3. Laptop, narrow sidebar: open ⋯. Upload, Sort and Recent changes are its first three items, each once; the menu fits the screen and scrolls if it is long; Export as ZIP… and Delete vault… can be reached.
4. Tap **+ New note**: a new note in the vault's root opens. Tap Upload (row or ⋯): the upload goes to the root.
5. Tap Sort: the order switches between name and last changed; its tooltip says "Sorted by name. Sort by last changed" (or the reverse). Reload: the order is kept.
6. Tap Calendar: the vault's calendar opens; the icon carries the vault's What's on count. Tap Board: the board opens (only shown when the vault has one). Tap Recent changes: Recent changes opens.
7. Tap Find: the search row as before; only the flags some note of this vault carries are chips, wrapping onto a second row if needed, never scrolling sideways. Press a chip, then remove that flag from its last note: the chip stays while pressed. Search the contents… is still there; Sort and Recent changes are not.
8. Phone: open the drawer. The row shows + New note, Calendar, Board, Find and ⋯, nothing cut off; Upload, Sort and Recent changes are at the top of ⋯.
9. In a vault shared with you read-only: no + New note and no Upload, in the row or in ⋯.

**§2.2 Properties**
10. Make a note `# aliastest`, then below it `---`, `aliases: [kumbu]`, `---`; tap Done. In Read mode, right above the block: "Properties go at the very top of a note." and **Move to properties**.
11. Tap it. The note starts with `---` / `aliases: [kumbu]` / `---`, then `# aliastest`; the properties box shows `aliases`; nothing else changed (check in Write mode).
12. Write the same block below the title again and Move: "`aliases` is already in the properties: change it there." with **Change it in Write mode**, which opens Write mode; nothing was moved.
13. A note with a horizontal rule, a heading underlined with `---` under a sentence, or the block inside a code fence: no hint.
14. In the properties box, tap **+ Add property**, enter `owner` / `Alice`: the frontmatter ends with `owner: Alice`; nothing else changed. Add `owner` again: "`owner` is already in the properties: change it in the note." Add `done` / `yes`: it is written as `done: "yes"`.
15. A note without frontmatter: its ⋯ has **Add property…**; adding creates the frontmatter at the very top.
16. In a vault you may only read: the hint has no button; no + Add property, no Add property….

**§2.3 The calendar**
17. Open the vault's calendar on some week other than this one, with a tag filter; open an entry. The note's header shows **‹ Calendar** above the calendar line; tap it: the same week, the same filter. Same from the month view.
18. From What's on, open an entry: **‹ What's on**; tap it: back where you were. Reload the note, or open it from the tree: no back link.
19. Laptop: in the tree, tap the folder name **Calendar**: the calendar opens. Tap the chevron in front of it: the folder opens and closes in the tree. Same for **Board** (opens the board). A folder called Calendar deeper down behaves as any folder.
20. Phone, Read mode: the dock shows Files, **Calendar**, Search, ⋯, +. Calendar carries What's on's count (red when something is overdue) and opens What's on; the top bar's file tree button carries no count; Search still shows its word.
21. A dated entry without a to-do: its ⋯ (in the note and in the tree) has **Make it a to-do**; tap it: the calendar line shows the checkbox. On an open to-do: **Just an appointment**; on a closed one, neither. Not on board cards or day notes.

## The pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/204

# Overnighter report: Obsidian parity quick wins

Pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/178 (draft, not merged). All five sections of the spec are in; nothing is left. Plan: `docs/superpowers/plans/2026-11-09-obsidian-parity-quick-wins.md`.

## 1. Environment

- Cloud container, 4 cores, 15 GB. .NET SDK 10.0.403 (global.json rolls forward from 10.0.100), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Installed by hand:** Node 26.13.0 into `/opt/node26` (the image had Node 22), pnpm 10.32.1 with `npm install -g`.
- **Docker:** the setup script's daemon wasn't running (a stale pid file); `dockerd` started by hand. Its open-file limit is a hard 20000, and it can't be raised in the container (`prlimit` refuses).
- **Test MongoDB:** an own `mongo:7.0` container, `--ulimit nofile=20000:20000`, replica set `rs0`, `KUMBU_TEST_MONGO` pointing at it, `TESTCONTAINERS_RYUK_DISABLED=true`. With 20000 files mongod died after about five minutes (WiredTiger `EMFILE`, `fassert`). What worked: `--wiredTigerEngineConfigString "file_manager=(close_idle_time=15,close_scan_interval=5,close_handle_minimum=250)"`, so WiredTiger closes idle file handles early. A fresh container for every full run.
- superpowers skills were available; the work ran as subagent-driven development: a fresh implementer per task, then a spec-compliance and a code-quality review by fresh subagents, then fixes. Tasks 3 and 5 ran in git worktrees beside the branch and were cherry-picked onto it, so two agents never committed into the same tree.

## 2. Plan review

A fresh subagent reviewed the plan against spec, brief and code: 20 findings, 4 blockers, all folded in except one, decided against (decision 20). The blockers:

1. The `mark` component must test `dataFind !== undefined`: the first find match is `0`.
2. rehype-katex typesets any `code.language-math`, so fenced ```` ```math ```` blocks would turn into maths: they get another class.
3. A stale "renamed" record would have pulled the next new `Untitled.md` to the old note's new name: navigation became a callback the shell registers, used once.
4. The `$` placeholder had to be restored in every field (URLs, titles, `meta`, the maths nodes' `hChildren`), not only in text.

Among the others: expectations corrected (`==#tag==` makes no tag chip, as on the server; `recentPaths` positions); the heading checked before any network call; footnote ids unique in every view, not only nested ones; Copy link checking only the form it emits; the hand-test checklist and the notification in Task 6.

## 3. Task 0: planned against measured

| | Planned (planning machine) | Measured |
|---|---|---|
| .NET | 5333 total, 5328 passed, 1 failed, 4 skipped | First run: invalid (mongod died of `EMFILE`, 36 failures). Second run, 16 min 53 s: 5333 total, 5321 passed, **6 failed**, 6 skipped |
| web tests | 115 files, 1863 passed | 115 files, 1863 passed (130 s) |
| web build | green | green; `main` 233.58 kB (gzip 67.13), `markdown` 159.33 kB (48.48) |

The six failures of the second run: `DotSegmentTests.A_dot_segment_is_refused_over_http_rather_than_resolved` (4 cases) and `DotSegmentTests.The_same_url_without_dot_segments_goes_on_to_the_endpoint` ("Failed to bind to address http://127.0.0.1:5000: address already in use"), and `PresenceTests.An_off_is_never_lost_in_a_burst_of_hints` (`NullReferenceException` at `SyncSockets.cs:75`). The second run overlapped the tail of the invalid first one, which held port 5000. Both classes alone, three times: 21 of 21 passed each time. So: environmental (the overlap) and, for the presence test, timing under load; not known failures. Six skipped instead of four: the two that skip when run as root, as the brief says.

## 4. The tasks

The full gate after each task: `pnpm -C web test --run` and `pnpm -C web build` (with the type check). Counts as the tools printed them.

| Commit | What | Gate |
|---|---|---|
| `bd91eb4` | The plan | |
| `810e1a9` | §3 Copy file path, Copy link, Copy markdown link | 116 files, 1882 tests passed; build green; `main` 236.29 kB |
| `4f8af83` | §3 review fixes: names counted once per tree (not per tree row), one kind check, a second copy shown for its full time, a live region that stays | 118 files, 1938 passed; `main` 237.32 kB |
| `3967cb2`, `b107b8d` | §2 the first heading names a new note; a test made able to fail | 117 files, 1936 passed |
| `6bab4d1` | §2 review fixes: a note gone meanwhile left alone, renames queued, the failure tied to its note, session checked again, 15 s timeout, timers cancellable, the follow callback's owner checked, Rename hidden while renaming, `exact: true` | 118 files, 1945 passed |
| `fcbfea3` | §4 `==highlight==` and `%%comments%%` | 118 files, 1973 passed; `main` 240.08 kB |
| `1070091` | §4 review fixes: comments before highlights, one linear pass, no `%%` counted in code in the outline, bare URLs and escapes left alone, `==` dropped from heading text | 119 files, 2021 passed; `main` 245.19 kB |
| `ed16f3d` | §5 maths with KaTeX | 119 files, 2013 passed; `main` 243.44 kB, `markdown` 159.71 kB, `math` 277.39 kB + 24.61 kB CSS |
| `98f89c6`, `eae7030` | §5 review fixes: `$` in wiki links and tags never maths, pairing and fence search linear, fences inside quotes, reference links; a timing test for many formulas | 120 files, 2035 passed; `main` 246.21 kB |
| `84e01e9`, `9c58ecc` | §6 footnotes; review fixes: focus, other fragment links as before, `↩︎` | 120 files, 2042 passed |

One full web run during §5's fixes had a single failure in `src/search/SearchSheet.test.tsx`, which this work doesn't touch; it passed alone (22 of 22) and in the next full run. A Task 2 test, "names the note on Read …", failed now and then under load before `6bab4d1` fixed its race (it read an element a re-render had replaced).

**§6:** before the change, "renders the reference and its definition with a back link" and "keeps the generated Footnotes heading hidden" passed already. The jump (no `preventDefault`, no scroll), unique ids across two views and in an embed failed, and were fixed.

## 5. Mutations

Each applied alone after its commit, the tests run, the mutation restored with `git checkout --`.

| Section | Mutation | Caught by |
|---|---|---|
| §3 | uniqueness without ignoring case | "links by the path inside the vault when the name exists twice, ignoring case and NFC" |
| §3 | the `# \| ^ [ ]` check removed | "leaves Copy link out for a name a wikilink can't hold" and two more |
| §3 | the space not encoded | "writes the Markdown link as the agents do" and two more |
| §3 | `encodeURIComponent` instead | "encodes the destination as MarkdownLink.Encode does, non-ASCII raw" |
| §3 | an attachment given the note items | "offers an attachment its file path only" (unit and tree) |
| §3 | no message on a failed copy | "says so when a copy fails" (shell and drawer) |
| §3 | the timer not re-armed by a second copy | `useCopied.test.tsx` "… shows a second copy for its full time" |
| §2 | `isUntitled` for any name | "leaves a named note alone" (unit and integration) |
| §2 | the `unresolved()` check dropped | survived at first (the saved text had no heading); after `b107b8d`: "sends no move while text is unsaved" |
| §2 | no StrictMode guard | "moves once under StrictMode" |
| §2 | `follow` without the URL check | "names it when another note opens, and stays on that note" |
| §2 | Write not blocked while renaming | "doesn't enter Write while the move is in flight" (each of the three guards alone too) |
| §2 | presence check removed | "skips while someone else edits the note" |
| §2 | collision suffix removed | "counts on past names that are taken" |
| §2 | board check removed | "leaves a board's cards alone" |
| §2 | `content: ""` again | "starts a new note with `# `", "opens a second new note", the shell's "Untitled 2" test |
| §2 | still-there check removed | "leaves a note alone that was moved by hand from Write mode" |
| §2 | global skip instead of the queue | "names two untitled notes left one after the other" |
| §2 | session check before the move removed | "stops when the session ends while it looks at the folder" |
| §2 | Rename shown while renaming (⋯ and tree) | "offers no Rename or move for the note while it is being named" |
| §2 | `exact: false` | seven tests, among them "names the note on Read" |
| §2 | no timeout | "gives up after 15 s" |
| §4 | flanking check removed | "leaves comparisons and code as written" |
| §4 | plugins before the wiki links | "holds emphasis, links and tags inside one highlight" |
| §4 | comments after find | survived at first; caught after a test that `%%gelben%% gelben` numbers the visible match 0 |
| §4 | `mark` testing `dataFind`'s truthiness | "finds no hidden text, and marks a match inside a highlight as found" |
| §4 | block comment pass removed | three tests (several blocks, table, unclosed) |
| §4 | outline ignoring comments | the outline's `%%` test |
| §4 | highlights before comments | "hides comments before it looks for highlights" |
| §4 | the old rescanning loop | "matches a long line of openers without a closer in linear time" (25 s) |
| §4 | code spans not blanked in the outline | "counts no %% in inline or indented code" |
| §4 | empty items kept inside a comment | "drops an empty item inside a comment, and keeps one outside" |
| §4 | bare URLs not opaque | "leaves %% and == in a bare URL alone" |
| §4 | escapes ignored | "keeps escaped delimiters as written" |
| §5 | digit rule removed | prepareMath `$x$5` |
| §5 | blank-line rule removed | prepareMath `$a\n\nb$`; "keeps currency as text" |
| §5 | inline / fenced code not masked | prepareMath `` `$x$` ``, `~~~\n$x$ and $5\n~~~` |
| §5 | `file.value` not restored | "keeps a link's address, and finds links whose text holds a $" |
| §5 | `hChildren` not restored | "hands KaTeX the formula with its $ signs" |
| §5 | link stop removed | the link test; prepareMath `[$5 plan](/a.md) and $y$` |
| §5 | `remarkMathBlocks` removed | the agent-highlight test and the code test |
| §5 | ```` ```math ```` guard removed | "leaves maths-like text in code alone" |
| §5 | `strict` back to its default | the console-spy test |
| §5 | `trust: true` | the same test (an `a` for `\href` appears) |
| §5 | one-line `$$` plugin removed | the one-line `$$x^2$$` test |
| §5 | `[[…]]` not skipped / tags not inert | prepareMath `[[A$B]] and [[C$D]]`, `#a$b and #c$d`; "never takes a $ in a wiki link or a tag for maths" |
| §5 | closer cursor restarting per opener | survived at first; caught after `eae7030` ("many formulas in one paragraph", 7.5 s mutated) |
| §5 | failed fence searches forgotten | "unclosed $$ fences on every line" (85 s mutated) |
| §5 | blockquote depth ignored | the two `> ```` cases |
| §6 | no `preventDefault` | the jump test and the embed test |
| §6 | one prefix for every view | the two-views ids test and the embed test |
| §6 | `h2` spread before its class | "keeps the generated Footnotes heading hidden" |
| §6 | target not focused | the jump test ("… takes the focus along") |
| §6 | every `#` link intercepted | "leaves other fragment links as they were" |

## 6. Bundle

| Chunk | Before (`c4b36f2`) | After |
|---|---|---|
| `main` | 233.58 kB (gzip 67.13) | 247.58 kB (gzip 72.29); 4.4 kB of the growth is the maths pre-pass and loader, the rest §2–§4 and §6 |
| `markdown` | 159.33 kB | 159.71 kB |
| `vendor` | 261.33 kB | 261.33 kB |
| `math` (new, loaded only for a note with maths) | – | 277.39 kB (gzip 83.70) + `math-*.css` 24.61 kB |

Checks on the final build: no `data:font` in any CSS (the one `data:font` string in the build is pdf.js's own runtime code); no `katex` in `main-*.js` or `markdown-*.js`; `index.html` preloads `index`, `vendor`, `markdown` and the CSS, no maths chunk; the KaTeX fonts are files under `/assets/`, so the CSP (`default-src 'self'`) admits them.

## 7. Final gate

As CI runs it, against a fresh MongoDB: `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`: **total 5333, failed 0, succeeded 5327, skipped 6** (15 min 59 s). `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`: **120 files, 2042 tests passed**, build green.

## 8. What I could look at

The app's dev server with Playwright and a mocked API (no server: its data is the mock's), at 390 px wide, in Kumbu (dark) and Catppuccin Latte (light): Alice's shopping list with a wide `aligned` formula, a malformed formula and a footnote. The highlight reads well in both; the comment is hidden; `$3` is text; the formulas take the text colour; the wide formula scrolls inside its box (572 px in 346 px) and the page doesn't scroll sideways; the malformed one shows its source in the warning colour, dotted; the ⋯ menu shows the three items after History; **Copy link** put `[[Einkaufsliste]]` on the clipboard. Not looked at in a browser: the rename against a real server, the tree's menu, the footnote jump on a phone, other schemes than those two. These are in the hand test below.

## 9. Decisions made without Chris

As in the pull request's description: each with the options seen where there were several, and why.

#### §2, the rename

1. **Which names count:** `Untitled.md` and `Untitled <n>.md` for n ≥ 1, ignoring case, whatever made the note (the app, Obsidian, a link). A heading that cleans to an untitled name doesn't rename. *Options:* also `Untitled draft.md`-like names. *Why:* any other name may be an intention.
2. **The trigger is the editor going away with everything saved**, deferred by a tick and dropped if an editor for the note mounts again (React StrictMode). Read, opening another note and leaving the note view all unmount the editor, so one mechanism covers the three and fires once. *Options:* hooks in `endWrite`, the router blocker and the shell separately. *Why:* one place, and it runs after the flush, so the move never races a save.
3. **While the move is in flight** Write mode and "Rename or move" are off for that note; renames of several notes run one after another, each reading the tree afresh; both requests give up after 15 s with a message. *Why:* a save to the old path would make a new `Untitled.md`, and a manual rename racing the automatic one must win.
4. **Skipped silently:** a note someone else is editing (presence, your other tabs too), a note the caller can't write, a note gone or moved meanwhile, a signed-out session, a board card. *Why:* breaks nothing; the next time Write mode ends catches it.
5. **The URL follows only if the note is still shown** (through a callback the shell registers); leaving to Settings updates the recent notes and doesn't pull the user back.
6. **A failed move** shows "Couldn't name the note from its heading: …" while that note is open; it is not retried by itself, and the next Write session tries again.
7. **The automatic move is exact** (`exact: true`), so a folder named like the target can't swallow the note; the dialog keeps `exact: false`.
8. **The name** is the heading as it reads (emphasis, links, `==`, `$`, inline `%%` comments removed), cleaned as the spec says, trailing dots and spaces dropped; collisions get ` 2`, ` 3`, … ignoring case, folders count as taken.
9. **The caret goes after `# `** only when the note's text is exactly `# `; other notes open as before.
10. **A new note left without a title keeps `# `** on the server. *Option:* write it back to empty. *Why not:* an extra write the user didn't make; listed as an open point.

#### §3, the copy items

11. **Place:** after History in the ⋯ menu, first in the tree's menu (which read-only members now have too).
12. **Copy link:** the bare name when exactly one note in the tree (archive left out, NFC, ignoring case) has it, else the path inside the vault; the form chosen is checked, and if it would hold `# | ^ [ ]` the item is left out. While the tree isn't loaded, the path form. Known gap: a note named like an attachment (`x.png.md`) resolves to the attachment first on the server.
13. **Copy markdown link** encodes exactly as the server's `MarkdownLink.Encode` (non-ASCII raw) and escapes `\ [ ] ( )` in the text.
14. **"Copied"** is an always-present `aria-live="polite"` region, so a second "Copied" is announced too (a `role="status"` wrapper would have clashed with the existing status lines).
15. **The shell reads the note's vault tree** so Copy link gives the same form before the drawer was first opened (one more tree request on a phone). *Option:* read it when the item is chosen. *Why not:* an `await` inside the tap can cost iOS's clipboard permission.

#### §4, highlights and comments

16. **The colour** is a warm yellow per scheme (darker and muted on dark schemes), each held to 4.5:1 for the text on it.
17. **Never inside code, bare URLs or after a backslash** (`\==x==` stays text); `====x====` stays text; an unclosed `%%` hides the rest of the note, as the server's tag reader reads it; a task whose text is only a comment is hidden with its checkbox.
18. **Comments run before highlights**, both after links and tags and before find, so hidden text is never found or highlighted.

#### §5, maths

19. **Currency** is kept out of remark-math by a pre-pass that swaps an unpairable `$` for a placeholder of the same length (U+FDD0) and puts it back right after parsing, instead of a backslash: line numbers, offsets (links, tags, the agent highlight, find) and code stay exactly as written.
20. **Not converted: `\( … \)` and inline `\[ … \]`**, against the brief's suggestion. Both are CommonMark escapes (`\[TODO\]`), so converting them would change notes that render fine today. Only `\[` and `\]` alone on their lines become `$$`. Listed as an open point.
21. **A `$` inside a wiki link or a `#tag`** is never maths; a link destination, a reference link and a wiki link end a pair.
22. **rehype-katex has no `throwOnError` option:** it tries with errors on and falls back to KaTeX's own rendering of the source in `--warning`, dotted, its message in the tooltip.
23. **Fenced ```` ```math ```` blocks stay code** (rehype-katex would otherwise typeset them).

#### §6, footnotes

24. **Every view gets its own id prefix** (`useId`), the note's, an embed's, the history's and a checkpoint's, including the footnotes heading's id.
25. **Only footnote links are intercepted;** other fragment links behave as before. The target takes the focus; the back link is `↩︎` in text presentation.

## 10. Deviations, surprises, open points for Chris

- **Not converted: `\( … \)` and inline `\[ … \]`** (decision 20): they are CommonMark escapes, and notes use `\[TODO\]`. If agents write them, a conversion limited to contents that look like LaTeX could follow. Recorded in `open-items.md`.
- **A new note abandoned untitled keeps `# `** on the server (decision 10). Obsidian, WebDAV and agents see an empty heading. Writing it back to empty would be one more write.
- **Copy link and a note named like an attachment** (`x.png.md`): the server resolves `[[x.png]]` to the attachment first. Not fixed (server side).
- **Timing tests:** §4 and §5 have tests that bound long inputs at 1.5 s (linear against quadratic, 7 to 85 s when mutated). Under a heavily loaded CI runner they could take longer; widen the bound if one ever fails.
- **Tables and `$`:** pairing doesn't split a table row by cell, so in `| $5 | $x$ |` the formula may stay text. Nothing breaks.
- **Test MongoDB in a container with a hard file limit:** the WiredTiger setting above made the full suite pass; it may help the CI runner too (see the open item of 2026-11-07).
- **My own slip, corrected:** restoring a mutation with `git checkout --` on a file with uncommitted changes threw away a fix once; it was written again before its commit.
- The worktrees of Tasks 3 and 5 are local to the container and not pushed.

## 11. Hand test (staging, partly through Playwright)

Each step: what to do, where to look, what passes. Phone width means 390 px.

1. **Alice's shopping list.** In `/Home/Garden` (any vault) tap **New note** in the dock. *Look:* the tree and the editor. *Passes:* `Untitled.md` appears, opens in Write mode with `# ` and the caret after it.
2. Type `Einkaufsliste`, a blank line, `- ==Tomaten== %%die gelben%%`, a blank line, `Price per kilo: $3`, a blank line, `Bed area: $A = \pi r^2$`, then tap **Read** at once. *Look:* tree, URL, note. *Passes:* the note is `/Home/Garden/Einkaufsliste.md` in the tree and the URL; "Tomaten" in yellow; "die gelben" not shown; "$3" plain text; the area formula typeset; reloading shows all the text.
3. ⋯ → **Copy link**, paste into another note, open the link. *Passes:* `[[Einkaufsliste]]`, "Copied" above the dock, the link leads back. ⋯ → **Copy file path**: `/Home/Garden/Einkaufsliste.md`. ⋯ → **Copy markdown link**: `[Einkaufsliste](/Garden/Einkaufsliste.md)`.
4. **A manual name is left alone.** Rename the note to `Markt` (⋯ → Rename or move), Write, change the heading to `# Einkaufsliste Samstag`, Read. *Passes:* the file stays `Markt.md`.
5. **An Obsidian-made note.** In Obsidian (synced) create `Untitled 1.md` with `# Beet plan`; in the web app open it, Write, Read. *Passes:* it becomes `Beet plan.md`, in Obsidian too after sync.
6. **Other triggers.** A new note with `# Saatgut`, then open another note from the tree (not Read). *Passes:* `Saatgut.md` in the tree, the other note open. Again with `# Kompost`, then go to Settings. *Passes:* back in the notes, `Kompost.md` is the recent note.
7. **A collision.** A new note with `# Einkaufsliste` in the same folder. *Passes:* `Einkaufsliste 2.md`.
8. **Someone editing.** Bob has `Untitled.md` open in Write mode; Alice opens it, writes `# Bobs Liste`, Read. *Passes:* no rename (the name stays) while Bob edits.
9. **The tree's menu.** In the drawer, ⋯ beside a note: **Copy file path**, **Copy link**, **Copy markdown link** first; beside an image: **Copy file path** only. *Passes:* "Copied" shows inside the drawer.
10. **A read-only member.** As Carol with read access to the vault: ⋯ of a note and the tree's ⋯ show the three copy items, no Rename or Delete; copying works.
11. **Maths at phone width in dark mode.** Settings → a dark scheme; a note with `$$` on a line, `\begin{aligned} f(x) &= \sum_{n=0}^{\infty} \frac{x^n}{n!} + \int_0^1 \sqrt{1+t^2}\,dt \\ g &= 1 \end{aligned}`, `$$` on a line; and `$\frac{1}{$`. *Passes:* the formula readable in the text colour, scrolls sideways inside its box, the page doesn't; the malformed one shows its source in the warning colour with a dotted underline, the rest of the note renders; no font errors in the browser console (CSP).
12. **Currency and code.** `costs $5 and $10`, `` `$x$` ``, a ```` ```math ```` block. *Passes:* all as written, no maths; the browser's network tab shows no `math-*.js` for a note with currency only.
13. **Footnotes.** `Watering twice a day.[^1]` and `[^1]: Unless it rains.` *Passes:* a small accent "1"; tapping it scrolls the note (not the page) to the definition and the URL doesn't change; "↩︎" jumps back. In a note embedding this one (`![[…]]`) the same works inside the embed.
14. **Highlights in the rest of the app.** Find in note (⋯ → Find in note) for "Tomaten": the match in the accent colour, distinct from the yellow; "gelben" finds nothing. Agent highlight ("Show") on a changed maths block still tints it.

# Overnighter brief: Obsidian parity quick wins

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. Tonight's work is small, web-only and close to the users' hands: a new note is named from its first heading, three copy items (file path, Obsidian link, Markdown link), `==highlight==` and `%%comments%%`, maths with KaTeX, and a check of footnotes. Several users already prefer Kumbu² to Obsidian for agentic work and speed; these are the everyday gaps they still notice. With this pull request 0.4.0 is feature complete, so care and polish count for more than breadth.

Nobody is available to answer questions during this run. That is intended: the spec has been reviewed by Chris, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it (everything you need from it is repeated here)
3. Spec: `docs/superpowers/specs/2026-11-09-obsidian-parity-quick-wins-design.md`, reviewed and approved by Chris. Please don't edit it.
4. For context, as needed:
   - `web/src/note/MarkdownView.tsx` with its remark plugins (`callouts.ts`, `wikiLinks.ts`, `../tags/tags.ts`, `findInNote.ts`): where §4 and §5 plug in
   - `web/src/api/notes.ts` (`useCreateUntitled` `:161`, `useMove` `:288`), `web/src/api/paths.ts` (`untitledName` `:120`), `web/src/shell/MobileShell.tsx` (`newNote` `:248`, `noteActions` `:294`), `web/src/shell/TreeDrawer.tsx` (`:217`), `web/src/state/editor.ts` (mode, `moved`), `web/src/note/NoteView.tsx`, `web/src/note/useAutosave.ts`, `web/src/shell/MoveDialog.tsx` (how a manual rename is done and how its failure is shown)
   - `src/Kumbu.Core/Links/LinkResolver.cs`: how the server resolves `[[name]]`, which decides what "unique" means for **Copy link**
   - `web/src/note/CheckpointNote.tsx` (`copyForAi`) and `web/src/shell/ConnectObsidianDialog.tsx` (`CopyButton`): how the app copies today

## Where you run

- Branch `feature/obsidian-parity-quick-wins`, based on `master` at `c4b36f2`. The session starts on the default branch, so first run `git fetch origin feature/obsidian-parity-quick-wins && git checkout feature/obsidian-parity-quick-wins`. Everything you push to that branch survives, everything else does not. Don't merge `master` into it.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is allowed only for §5:** `remark-math` (^6), `rehype-katex` (^7) and `katex` (^0.16) in `web/`, with `pnpm -C web add` so `pnpm-lock.yaml` updates; nothing else.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). This work is web-only, so the .NET suite only has to stay as it is; run it at Task 0 and at the final gate. The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", check whether the setup script's daemon is still running, and start it again if you can.
  - If Docker can't work at all, use an external replica-set MongoDB: install `mongod`, start it as a single-node replica set, and set `KUMBU_TEST_MONGO` (see `ExternalMongo` in `TestImages.cs`).
  - Earlier runs met a hard file limit below what Testcontainers' `mongo` asks for. What worked was a container of your own with `--ulimit nofile=20000:20000`, named `kumbu-test-mongo`, as a replica set, and `KUMBU_TEST_MONGO` pointing at it. Give every full run a fresh one (`docker rm -fv kumbu-test-mongo` first). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- The 4 skipped tests need the real embedding model and a TensorX key; skipping them is expected. Running as root can skip 2 more.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool and say so in the report.

## What Chris' other project taught us about KaTeX

The spec points at `../chatsundere`, Chris' other project. **You can't read it from here**, so this is what matters from it (its message-rendering spec and `preprocess-math.ts`):

- Stack: `remark-math` ^6 + `rehype-katex` ^7 + `katex` ^0.16, plus `katex/dist/katex.min.css`, `throwOnError: false`.
- **remark-math has no anti-currency rule**, whatever comments in chatsundere say. Verified with remark-math 6.0.0 / micromark-extension-math 3.1.0: `costs $5 and $10` gives inlineMath `5 and `, `$ x $` is inlineMath, and inline maths spans newlines inside a paragraph. A post-pass can't repair that (the `$` is gone). So apply the Obsidian/Pandoc rule **before parsing**, in the same code-masked pre-pass as below: an opening `$` must be followed by a non-space, a closing `$` must follow a non-space and must not be followed by a digit, no pair spans a blank line; escape (`\$`) every `$` that can't pair. The pre-pass keeps the line count.
- **`$$` is a display block only when both `$$` stand at a line boundary**; otherwise it is parsed as inline maths, which may not contain newlines. Notes write `$$` on lines of their own, as Obsidian does. A one-line `$$x$$` parses as inlineMath: a paragraph whose only child is a `$$`-delimited inlineMath becomes display maths, as in Obsidian. Test a one-line `$$x$$` (display), `$$` blocks over several lines (display), and `\begin{aligned} … \\ … \end{aligned}` inside one.
- **`\( … \)` and `\[ … \]`**: chatsundere converts them before parsing, because models write them. Agents write many Kumbu² notes too, so this is worth having here: a pure function run on the source before ReactMarkdown, (1) masking fenced code blocks (```` ``` ````, `~~~`) and inline code spans first and restoring them afterwards, so nothing inside code is rewritten; (2) `\[ … \]` → `$$ … $$`, with multi-line inner content emitted as a true block fence `\n\n$$\n<content>\n$$\n\n` and single-line content kept as `$$<content>$$`; (3) `\( … \)` → `$<trimmed>$` (trimmed because of the whitespace rule); (4) a negative look-behind on `\` so `\\[5pt]` (a LaTeX line break with spacing inside `aligned`) is never taken for `\[`. Its tests there: `\(x\)` → `$x$`, `\( x \)` → `$x$`, single-line `\[x\]` → `$$x$$`, multi-line `\[ A\\B \]` → block fence, maths-like text in code untouched, `\\[5pt]` not rewritten. The spec allows this ("unless it costs nothing"); do it, unless it breaks something below. Mind that the preprocessed source must not shift the line numbers `data-line`, the agent highlight and find-in-note rely on (`remarkChanged`, `remarkFind` in `MarkdownView.tsx`): if a multi-line rewrite would, keep the line count (for example, don't add lines, or only convert where no lines are added) and test it.
- **Display maths scrolls inside itself:** `.katex-display { overflow-x: auto; overflow-y: hidden; margin: 0.6em 0; padding: 0.25em 0 }`, so a wide formula never makes the page scroll sideways. Check at phone width.
- **Bundle:** katex is about 260 kB; chatsundere had to split it into its own chunk. Here the spec wants it loaded only when a note has maths (dynamic import), so the first load doesn't grow. **Trap:** `web/vite.config.ts:60-67` `manualChunks` puts every `remark-|micromark|mdast-|hast-|unist-` module into the eagerly loaded `markdown` chunk, dynamic imports included; exclude the maths packages (remark-math, micromark-extension-math, mdast-util-math, rehype-katex and its own hast utilities) from that group. Compare the sizes of the main **and** the `markdown` chunk in `pnpm -C web build` before and after and put them in the report.
- **Fonts and the CSP:** `KaTeX_Size3-Regular.woff2` (3.6 kB) is under Vite's default `assetsInlineLimit` (4096), so Vite inlines it as `data:`, and the CSP (`Security.cs:59`, no `font-src`, so `default-src 'self'`) refuses it: some delimiters break in production only. Never inline fonts (`build.assetsInlineLimit` as a function returning false for `.woff2`/`.woff`/`.ttf`) and check the build output for `data:font`.
- **Options:** `throwOnError: false`, `trust: false`, and `strict: "ignore"`: the default `strict: "warn"` writes `console.warn` lines that quote the formula, and note contents never reach a log.
- KaTeX's colour follows the text colour; check light and dark mode.

## Open points for the run

From the review of spec and brief against the code (anchors checked at `f8764a5`). All are **decide**: settle them with the rules below and log each in the report. Where a reading is given, it is the recommended one. The KaTeX points are in the section above.

**§2, the rename**

1. **Which names count:** `Untitled.md` and `Untitled <n>.md` for any n ≥ 1 (the app makes 2, 3, …; Obsidian makes 1, 2, …), ignoring case. Nothing else (`Untitled draft.md` is a name). A link-created `Untitled.md` (from `[[Untitled]]` through `useCreateNote`) is renamed too and its link rewritten by the move: consistent with the name rule.
2. **Where the rename runs.** Opening another note or leaving the note view with nothing unsaved doesn't hit the blocker (`MarkdownEditor.tsx:154-176`): the editor unmounts with its text. Take the heading from the text the editor last saved (`s.saved` after `flush`), handed out at unmount or in `beforeRead`, not from a possibly stale note cache. Don't hang `useEditor.moved()` and the navigate on `move.mutate(…, { onSuccess })`: TanStack drops per-call callbacks after unmount (leaving to Settings), and `recentPaths` would keep the old path and 404. Use an awaited call with the store update unconditional, mirroring `MoveDialog.tsx:67-80`: `beforeRead` first, then `moved(from, to)`, then `navigate(…, { replace: true })` only if `currentPath === from`.
3. **No Write mode while the move is in flight.** An autosave PUT to the old path would recreate `Untitled.md` there (`MoveDialog.tsx:66` says so), losing text "during" the rename. `/api/move` takes no version: what matters is that the text is flushed first and no save goes out to the old path afterwards. Two triggers in a row (Read, then navigate) move once; a failed move is not retried by the second trigger.
4. **Other editors present.** If presence (`useSyncStatus` editors, `PresenceLine.tsx`) shows anyone else editing the note, another person or the user's own other tab, skip the rename: renaming under them makes their editor "gone". The next time someone leaves Write mode catches it.
5. **Read-only notes:** `MobileShell.tsx:222` drops to Read for those; the rename needs write permission and doesn't run.
6. **The `# ` start.** `startsWithH1` (`NoteView.tsx:64`) matches a bare `# `, which would hide the file title (`:356`) and show an empty H1, a blank page. Treat an empty H1 as no H1 there. Programmatic `focus()` (`MarkdownEditor.tsx:105`) doesn't put the caret after `# ` in Chrome: set `setSelectionRange(len, len)` explicitly. Link-created notes (`useCreateNote`) and board cards keep their empty content. (There is no welcome flow in `web/src` to worry about.)
7. **Finding the heading:** in the body with the front matter split off (reuse NoteView's `parts`), skipping fenced code and `%%` blocks; ATX only (don't reuse `startsWithH1`, which also takes setext). Besides the spec's cleaning, drop trailing dots and spaces (WebDAV and Windows clients choke on them).

**§3, the copy items**

8. **Copy link's uniqueness doesn't depend on a source note:** `LinkResolver.Find` prefers the source's folder, but the link is pasted somewhere unknown. Rule: the bare name is unique when exactly one note in the vault's tree outside `_archive/` has it, comparing case-insensitively after NFC; otherwise the path inside the vault without `.md`, the server's own fallback (`LinkResolver.LinkText`, used at `NoteMover.cs:357`). Leave attachments out of the tree's notes. A name containing `# | ^ [ ]` would make a broken wikilink: use the path form if that fixes it, else leave **Copy link** out for such a note, and record it.
9. **Copy markdown link's encoding** mirrors the server's `MarkdownLink.Encode` (`src/Kumbu.Core/Links/MarkdownLink.cs:40-64`): encode only control characters, the space and `` %#?()<>[]^|\`"{} ``; non-ASCII stays raw (`Gemüse`, not `%C3%BC`, so no `encodeURIComponent`).
10. **"Copied":** the menu closes on select. Show a transient `role="status"` confirmation where the menu was opened, after the precedent `Lightbox.tsx:131-138` / `:244`; for the tree that is inside the drawer, which on a phone is an overlay (`TreeDrawer.tsx:397`) and would hide a message at `MobileShell.tsx:278`. A failed copy says so in the same place.
11. `FileMenu` now always has items, also for read-only members (its `return null` at `:225` no longer triggers): expected, test it. The Dock's ⋯ menu exists only in Read mode (`MobileShell.tsx:283`): leave that as it is.

**§4, highlight and comments**

12. **Plugin order and positions.** `remarkWikiLinks` matches by `line` plus raw text and needs `position` on text nodes (`wikiLinks.ts:94`, `:103`); `remarkTags` needs `position.offset`; `callouts.ts:79` keeps positions on the parts it splits. Run both new plugins **after** `remarkWikiLinks` and `remarkTags` and **before** `remarkFind` (so find doesn't count hidden text), keep positions on split nodes, and work across sibling phrasing nodes (`==a *b* [[c]]==`), plus a block-level pass for `%%` blocks over several paragraphs. Test `==[[link]]==` and `==#tag==`.
13. **`==` in comparisons:** `a == b and c == d` stays text: flanking as for emphasis, no whitespace just inside either `==`.
14. **The `mark` component** (`MarkdownView.tsx:343`) maps every `mark` to find's style today; branch on `dataFind`. The highlight colour, decided by Chris: **a warm yellow like Obsidian's**, the classic highlighter colour, readable in light and dark mode (light: a yellow background under the normal text colour; dark: a darker, muted yellow, text still readable); find-in-note keeps the accent. Add it as a theme token next to the existing ones if none fits.
15. **Hidden headings:** a heading inside a `%%` block is left out of the outline (`outline.ts`) and its `data-line` jumps.

**§5 and §6**

16. **Agent highlight on maths blocks:** check that `remarkChanged`'s `hProperties` survive rehype-katex replacing a `math` block's `pre`/`code` (wrap the output in a `div` that carries them).
17. **History and checkpoints** use `MarkdownView` too (`HistorySheet.tsx:126`, `CheckpointNote.tsx`) and get §4 and §5 with it: intended, no flag.
18. **Footnotes in embeds:** a nested `MarkdownView` (`MarkdownView.tsx:161`) would repeat `user-content-fn-1` ids; give each nested view its own `clobberPrefix` (`remarkRehypeOptions`). Handle fragment clicks yourself (`preventDefault`, `scrollIntoView` inside the note's scroller) so the URL stays clean. The generated `<h2 class="sr-only">Footnotes</h2>` passes through the `h2` component (`:337`); test that it stays hidden.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-09-obsidian-parity-quick-wins.md`.
2. It starts with **Task 0: baseline**: run the full suites before any change and record the exact counts and the exact failures, against the known state below.
   Known state on `master` at `c4b36f2`, measured on the planning machine on 2026-11-09 with `dotnet test --solution Kumbu.slnx`, `pnpm -C web test --run` and `pnpm -C web build`: **.NET total 5333, succeeded 5328, failed 1, skipped 4** (13 min 60 s; the failing test's name wasn't captured on the planning machine, most likely one of the classes below under load: identify it in Task 0); **web 115 files, 1863 tests, all passing, build green**.
   Tests that failed now and then under load in earlier runs (`AttachmentMetricsTests.Turned_pages_count_for_pdf`, `RekeyTransitionTests`, `PairingTests`, and the sync-hub test): if Task 0 shows a failure, run that test class alone three times, record whether it is flaky, and carry on; a test that fails every time on the untouched branch is a known failure for the report, not something to fix.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. **Order by value**, so a short night still leaves something whole: (1) §3 the copy items; (2) §2 the heading names the note; (3) §4 highlight and comments; (4) §5 KaTeX; (5) §6 footnotes. Each is its own commit, as the spec asks; put a gate (web tests and build) after each.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by a fresh subagent.
- Subagents never merge, push or switch branches. You commit and push.
- Commit and push after every task. Commit messages imperative and free-form ("Name a new note from its first heading"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- Web tests, one file: `pnpm -C web test --run <file>`. The whole web suite: `pnpm -C web test --run`. **The web's CI gate is `pnpm -C web build`:** it runs the type check, which `pnpm test` does not.
- Full verification at the final gate, as CI does (`.github/workflows/ci.yml`): `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`. The litmus job (WebDAV) is not needed: this work doesn't touch WebDAV.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- If you can, look at the result in a browser (`pnpm -C web dev` against a local server, or the existing component tests with Testing Library if a server is out of reach) at phone width and in light and dark mode; say in the report what you could and couldn't look at.
- **If the night runs short**, stop at the last green gate rather than half-way through a task, push, and say in the report which sections are left.
- **Style:** British English in code, comments, docs, commits, user-visible text (colour, behaviour, initialise, licence as the noun). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`. Menu labels exactly as the spec writes them: **Copy file path**, **Copy link**, **Copy markdown link**.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples, test data. Use Alice, Bob, Carol, `Work`, `Home`, a vegetable garden.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. A note that isn't "Untitled …" is never renamed; notes without `==`, `%%`, `$` or footnotes render exactly as before; agent highlights, find-in-note, task ticking, embeds and callouts keep working.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture: renames through `useMove`, menus through the existing `MenuItem` lists, rendering as remark/rehype plugins next to the existing ones; no new dependencies beyond the three of §5. Where Obsidian's and Kumbu²'s behaviour differ, Kumbu²'s semantics come first (Chris: for the users, Obsidian is little more than the front end until Kumbu² can do everything itself).
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling; KaTeX stays `trust: false`.
4. **True to the spec.** Chris' guiding wish: **a manual name is an intention**. Where a choice trades automatic renaming against leaving a name alone, leave it alone.

If a question can only be answered by changing the spec's scope, do not guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the pull request description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please do not merge, tag or deploy, and don't change the version: rollout is something Chris does himself. One pull request is the result.
- No secrets in logs, assertions or the report; note contents never reach a log.
- **Security boundary: not touched.** Web app only. No change to the server, its API or CSP (`src/Kumbu.Server/Api/Security.cs`), encryption, accounts, sharing and roles, publishing, WebDAV, sync. The publish reader (`web/src/reader/`) and the skill pages keep their renderers. If something seems to need a server change, leave it out and record it in `docs/superpowers/open-items.md`.
- At the end: a row with the pull request's link in `docs/superpowers/open-items.md`, and in the report a hand-test checklist with numbered steps, where to look and what passes. Chris tests it on staging, partly through Playwright, so include the end-to-end example of the spec (Alice's shopping list), an Obsidian-made `Untitled 1.md` being named, a manually named note left alone, the three copy items in the ⋯ menu and the tree, a read-only member copying, maths at phone width in dark mode, and footnotes. Don't change anything else outside the plan's files.

## Out of scope

- Everything spec §1 lists as a non-goal: the publish reader, skill pages and install dialog, renaming in the Obsidian plugin, WebDAV or agents, Mermaid, anything server-side.
- The other items of the parity list (embeds in `cat`, block references, unlinked mentions, `aliases`, Graph, Canvas, Properties, Themes).
- Changing the Obsidian plugin (another repository).

## The report

First open a pull request against `master` titled "Obsidian parity: the quick wins for 0.4.0", whose body lists the decisions made without Chris, says that the security boundary is not touched, and ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Then, as the last commit, `docs/superpowers/overnighter/2026-11-09-obsidian-parity-quick-wins-report.md`, with:

- the environment
- the plan review findings and what you did with them
- Task 0 planned vs. measured
- for each task the commit and the real test output
- every mutation individually
- the main chunk's size before and after KaTeX
- the decisions made without Chris
- deviations, surprises, sections left if any, open points for Chris
- the hand-test checklist
- the pull request's link, together with a row linking it in `docs/superpowers/open-items.md`

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!

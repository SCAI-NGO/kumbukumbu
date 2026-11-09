# Obsidian parity quick wins Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close five everyday Obsidian gaps in the web app's note view for 0.4.0: copy items, a new note named from its first heading, `==highlight==` and `%%comments%%`, maths with KaTeX, and working footnotes.

**Architecture:** Web only (`web/`). Rendering stays remark/rehype plugins next to the existing ones in `web/src/note/MarkdownView.tsx`; KaTeX is a dynamically imported module, loaded only when a note has maths. The rename uses the existing `POST /api/move` through a helper shared with `useMove`, triggered once when the editor of an "Untitled …" note unmounts with everything saved.

**Tech Stack:** React 18, react-markdown 10, remark-gfm 4, TanStack Query 5, zustand, Vitest + Testing Library; new for Task 4 only: `remark-math` ^6, `rehype-katex` ^7, `katex` ^0.16.

**Spec:** `docs/superpowers/specs/2026-11-09-obsidian-parity-quick-wins-design.md`; the brief `docs/superpowers/overnighter/2026-11-09-obsidian-parity-quick-wins-brief.md` (its "Open points" 1–18 and its KaTeX section are requirements of this plan too).

## Global Constraints

- British English in code, comments, docs, commit messages and user-visible text. New TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- Menu labels exactly: **Copy file path**, **Copy link**, **Copy markdown link**.
- No new dependency except `remark-math` ^6, `rehype-katex` ^7, `katex` ^0.16, added with `pnpm -C web add` (Task 4 only).
- No change to the server, its API or CSP (`src/Kumbu.Server/Api/Security.cs`), the publish reader (`web/src/reader/`), skill pages, WebDAV, sync.
- A note whose name is not "Untitled …" is never renamed. Notes without `==`, `%%`, `$` or footnotes render exactly as before.
- KaTeX options: `throwOnError: false`, `trust: false`, `strict: "ignore"`. Fonts never inlined as `data:`.
- Note contents never reach a log (no `console.*` with note text).
- Commit messages imperative, free-form, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Subagents never push, merge or switch branches.
- Gate after every task: `pnpm -C web test --run` (all pass) and `pnpm -C web build` (green; this runs the type check).
- Test data uses neutral names: Alice, Bob, Carol, `Work`, `Home`, a vegetable garden.

## Plan review

Reviewed by a fresh subagent against spec, brief and code (20 findings, 4 blockers); all folded in below, except the full `\( … \)` / inline `\[ … \]` conversion, decided against (Task 4).

## Review Focus

1. **Text typed in the last 800 ms before Read** must land in the renamed note (save first, then move). Pinned in Task 2, test "keeps text typed just before Read".
2. **A note named by hand** (`Markt.md`) whose heading changes is never moved. Pinned in Task 2, test "leaves a named note alone".
3. **`$` as currency** (`costs $5 and $10`, `$3` alone) stays text, with no visible backslash. Pinned in Task 4, `prepareMath` tests and the MarkdownView test "keeps currency as text".
4. **`a == b and c == d`** and `==` inside code stay text. Pinned in Task 3.
5. **A read-only member** gets the three copy items in the ⋯ menu and the tree, and nothing that writes. Pinned in Task 1.

---

## File map

| File | Task | Responsibility |
|---|---|---|
| `web/src/shell/copyItems.ts` (new) | 1 | the three strings: `filePathText`, `wikiLinkText`, `markdownLinkText`; `copyItems(...)` → `MenuItem[]` |
| `web/src/shell/useCopied.tsx` (new) | 1 | clipboard write + transient `role="status"` line |
| `web/src/shell/MobileShell.tsx`, `TreeDrawer.tsx` | 1, 2 | menu items; Task 2: follow a rename, show its failure, block Write while renaming |
| `web/src/note/nameFromHeading.ts` (new) | 2 | pure naming rules + `renameFromHeading` orchestration |
| `web/src/api/notes.ts` | 2 | `moveNote` shared by `useMove`; new notes start with `# ` |
| `web/src/state/editor.ts` | 2 | `renaming`, `renamed`, `renameProblem` |
| `web/src/note/MarkdownEditor.tsx`, `useAutosave.ts`, `NoteView.tsx` | 2 | trigger at unmount; caret after `# `; empty H1 is no H1 |
| `web/src/note/highlights.ts` (new) | 3 | `remarkHighlights`, `remarkComments` |
| `web/src/note/outline.ts` | 3 | headings inside `%%` blocks left out |
| `web/src/index.css`, `web/src/theme/*.test.ts` | 3 | `--highlight` token |
| `web/src/note/prepareMath.ts` (new) | 4 | the code-masked pre-pass, `remarkRestoreDollars` |
| `web/src/note/math.ts`, `math.css` (new) | 4 | lazily loaded: remark-math, rehype-katex, display/wrap plugins, KaTeX CSS |
| `web/vite.config.ts` | 4 | maths chunk outside `markdown`, fonts never inlined |
| `web/src/note/MarkdownView.tsx` | 3, 4, 5 | plugin order, `mark` branch, footnote links, `clobberPrefix` |

---

### Task 0: Baseline

**Files:** none changed.

- [ ] **Step 1:** Start a fresh MongoDB: `docker rm -fv kumbu-test-mongo; docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all`, then with `KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true"` run `dotnet test --solution Kumbu.slnx`. Expected (planning machine): total 5333, succeeded 5328, failed 1, skipped 4. Record the exact counts and the failing test's name.
- [ ] **Step 2:** If a test fails, run its class alone three times (`dotnet test --solution Kumbu.slnx -- --filter-class "<Namespace.Class>"`, or the runner's equivalent); record whether it is flaky.
- [ ] **Step 3:** `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`. Expected: 115 files, 1863 tests passing, build green. Record the sizes of `main-*.js` and `markdown-*.js`.

---

### Task 1: Copy file path, Copy link, Copy markdown link (spec §3)

**Files:**
- Create: `web/src/shell/copyItems.ts`, `web/src/shell/useCopied.tsx`
- Test: `web/src/shell/copyItems.test.ts`, `web/src/shell/MobileShell.test.tsx` (add), `web/src/shell/TreeDrawerAttachments.test.tsx` (add)
- Modify: `web/src/shell/MobileShell.tsx:283-301` (noteActions), `web/src/shell/TreeDrawer.tsx:200-230` (FileMenu) and its panel (status line beside `marking.error`, `:540-544`)

**Interfaces:**
- Produces:
  - `filePathText(path: string): string` → the path as is (`/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md`).
  - `wikiLinkText(path: string, root: TreeFolder | undefined): string | null`
  - `markdownLinkText(path: string): string`
  - `copyItems(path: string, root: TreeFolder | undefined, note: boolean, copy: (text: string) => void): MenuItem[]`
  - `useCopied(): { copy(text: string): void; status: ReactNode }` — `status` is a `<p role="status">` reading `Copied` or `Couldn't copy. Copy it by hand.` for 3 s (`useTimeout` from `note/ChangedElsewhere.tsx`), else null.

- [ ] **Step 1: Write the failing unit tests** in `copyItems.test.ts`:

```ts
// A TreeFolder for the vault of the first path; `.png`/`.pdf` entries get kind "image"/"pdf", folders are made as needed.
const tree = (paths: string[]): TreeFolder => buildTree(paths);
it("copies the path with the vault", () => expect(filePathText("/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md")).toBe("/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md"));
it("links by the bare name when no other note has it", () =>
  expect(wikiLinkText("/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md", tree(["/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md", "/SWE/Obsidian Parity.png"]))).toBe("[[Obsidian Parity]]"));
it("links by the path inside the vault when the name exists twice, ignoring case and NFC", () =>
  expect(wikiLinkText("/SWE/Kumbu/Gemüse.md", tree(["/SWE/Kumbu/Gemüse.md", "/SWE/Home/GEMU\u0308SE.md"]))).toBe("[[Kumbu/Gemüse]]"));
it("leaves Copy link out for a name a wikilink can't hold", () => expect(wikiLinkText("/SWE/a#b.md", tree(["/SWE/a#b.md"]))).toBeNull()); // also `|`, `^`, `[`, `]`
it("checks only the form it emits", () => expect(wikiLinkText("/SWE/C#/plan.md", tree(["/SWE/C#/plan.md"]))).toBe("[[plan]]"));
it("falls back to the path form while the tree isn't loaded", () => expect(wikiLinkText("/SWE/Kumbu/plan.md", undefined)).toBe("[[Kumbu/plan]]"));
it("writes the Markdown link as the agents do", () =>
  expect(markdownLinkText("/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md")).toBe("[Obsidian Parity](/Kumbu/Obsidian%20Parity/Obsidian%20Parity.md)"));
it("encodes the destination as MarkdownLink.Encode does, non-ASCII raw", () =>
  expect(markdownLinkText("/W/Beet (2)/Gemüse #1.md")).toBe("[Gemüse #1](/Beet%20%282%29/Gemüse%20%231.md)"));
it("escapes brackets and parentheses in the text", () =>
  expect(markdownLinkText("/W/a [b] (c).md")).toBe("[a \\[b\\] \\(c\\)](/a%20%5Bb%5D%20%28c%29.md)"));
```

Plus: `copyItems(path, root, false, copy)` (an attachment) has the one label `Copy file path`; for a note the labels are, in order, `Copy file path`, `Copy link`, `Copy markdown link`.

- [ ] **Step 2:** `pnpm -C web test --run src/shell/copyItems.test.ts` — FAIL (module missing).
- [ ] **Step 3: Implement `copyItems.ts`.** Uniqueness: count every note (entry kind `note`, i.e. not `image`/`pdf`/`text`) in the whole tree whose name without `.md` equals the note's, both `.normalize("NFC").toLowerCase()`; the tree already leaves `_archive/` out. Exactly one → `[[name]]`, else `[[<inVault path without .md>]]` (the server's `LinkResolver.LinkText`). The form chosen is checked: any of `# | ^ [ ]` in it → try the path form (only useful if the bare name was the problem, so in practice → `null`, item omitted). No tree yet (`root` undefined) → the path form. Known gap, recorded not fixed: a note named like an attachment (`x.png.md`) resolves to the attachment first on the server. Encoding: per segment after the vault, percent-encode (UTF-8 bytes, upper-case hex) control characters and any of `` %#?()<>[]^|\`"{} `` and the space; everything else raw (mirrors `src/Kumbu.Core/Links/MarkdownLink.cs:40-64`). Link text: `titleOf(path)` with `\`, `[`, `]`, `(`, `)` escaped by a backslash. Icons: `Copy` (path), `Brackets` (link), `Link` (markdown link).
- [ ] **Step 4:** same command — PASS.
- [ ] **Step 5: Write the failing UI tests.** In `MobileShell.test.tsx`: the ⋯ menu ("Note actions") of a note lists `Copy file path`, `Copy link`, `Copy markdown link` after `History`; choosing `Copy link` writes `[[garden]]` (stub `navigator.clipboard.writeText` with `vi.fn().mockResolvedValue(undefined)`) and shows `role="status"` "Copied"; a rejected write shows "Couldn't copy. Copy it by hand."; for a vault with `level: "read"` the three items are there and `Rename or move` / `Delete note` are not. In `TreeDrawerAttachments.test.tsx`: an attachment row's menu has `Copy file path` and no `Copy link`; a note row of a read-only vault now has a menu (it had none: `FileMenu`'s `return null` at `:225`) with the three items; the "Copied" status is inside the drawer dialog.
- [ ] **Step 6:** `pnpm -C web test --run src/shell/MobileShell.test.tsx src/shell/TreeDrawerAttachments.test.tsx` — the new tests FAIL.
- [ ] **Step 7: Wire it.** `MobileShell`: `const copied = useCopied()`, `useTree(path && vaultOf(path))`, insert `...copyItems(path, tree.data?.root, true, copied.copy)` after the History item; render `copied.status` with the alerts above the dock (`:278`), as `<p role="status" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">`. `TreeDrawer`: `FileMenu` gets `root` and `copy` props and puts `copyItems(...)` first; the drawer's `useCopied()` status goes beside `marking.error` in the panel (`TreeDrawer.tsx:~559`, inside the dialog on a phone). Leave the Dock's Read-only rule (`MobileShell.tsx:283`) as it is.
- [ ] **Step 8:** same command — PASS. Gate: `pnpm -C web test --run && pnpm -C web build`.
- [ ] **Step 9: Mutations** (each alone; watch a test fail; restore with `git checkout -- <file>`): uniqueness ignores case off (`toLowerCase` removed) · the `# | ^ [ ]` check removed · the space no longer encoded · `ü` encoded with `encodeURIComponent` · `copyItems` returns the note items for an attachment · the status never shown on failure.
- [ ] **Step 10: Commit** "Copy a note's file path, its link and its Markdown link".

---

### Task 2: The first heading names a new note (spec §2)

**Files:**
- Create: `web/src/note/nameFromHeading.ts`; Test: `web/src/note/nameFromHeading.test.ts`
- Modify: `web/src/api/notes.ts:161-181` (`content: "# "`), `:288-300` (`moveNote`), `web/src/state/editor.ts`, `web/src/note/useAutosave.ts` (a `savedText()` getter), `web/src/note/MarkdownEditor.tsx:103-106` (focus, caret) and its effects, `web/src/note/NoteView.tsx:64-67` (`startsWithH1`), `web/src/shell/MobileShell.tsx` (follow, failure, Write blocked), `web/src/shell/ModeToggle.tsx` (none: `writable` comes from the shell)
- Test: `web/src/note/MarkdownEditor.test.tsx` (add a `describe("Named from its heading")`), `web/src/note/NoteView.test.tsx` (startsWithH1)

**Interfaces:**
- Produces (in `nameFromHeading.ts`):
  - `isUntitled(path: string): boolean` — the file name matches `/^untitled(?: [1-9]\d*)?\.md$/i`.
  - `headingName(content: string): string | null` — the cleaned name (no `.md`) from the first ATX level-1 heading of `splitFrontmatter(content).body`.
  - `cleanName(text: string): string | null`
  - `renameTarget(path: string, content: string, root: TreeFolder): string | null`
  - `renameFromHeading(queryClient: QueryClient, path: string, saved: string): Promise<void>`
- Produces (in `notes.ts`): `moveNote(queryClient: QueryClient, from: string, to: string): Promise<MoveResult>` — the `POST /api/move` call plus what `useMove`'s `onSuccess` does today; `useMove` uses it.
- Produces (in `editor.ts` state): `renaming: string | null`, `renameProblem: string | null`, `follow: ((from: string, to: string) => void) | null` (registered by the shell, as `beforeRead` is), setters `setRenaming`, `setRenameProblem`, `setFollow`. `setMode("write")` and `openNote(p, "write")` don't enter Write mode while `renaming === currentPath` / `renaming === p`. `openNote` of another path and `setMode("write")` clear `renameProblem`. `reset` clears them.

- [ ] **Step 1: Failing unit tests** (`nameFromHeading.test.ts`):
  - `isUntitled`: true for `/W/Untitled.md`, `/W/untitled 2.md`, `/W/Untitled 1.md`, `/W/UNTITLED 12.md`; false for `/W/Untitled draft.md`, `/W/Untitled 0.md`, `/W/Untitled2.md`, `/W/Markt.md`.
  - `headingName`: `"# Einkaufsliste\n\n- x"` → `Einkaufsliste`; `"---\ntitle: a\n---\n# Plan"` → `Plan`; `"Intro\n\n# Later"` → `Later`; `"# "` → null; `"#\n# B"` → null (the first H1 is empty); `"Title\n==="` → null (setext); `"## Sub"` → null; ```` "```\n# code\n```\n# Real" ```` → `Real`; `"%%\n# hidden\n%%\n# Shown"` → `Shown`; `"# A %%note%% B"` → `A B`; `"# **Bold** [[Gemüse|Beet]] #"` → `Bold Beet`; `"   # Indented"` → `Indented`; `"    # code"` → null.
  - `cleanName`: `'a/b\\c:d*e?f"g<h>i|j#k^l[m]n'` → `abcdefghijklmn`; `"  many   spaces  "` → `many spaces`; `"...hidden"` → `hidden`; `"ends with dots..."` → `ends with dots`; `"?#"` → null; a 120-character text of words → cut at the last space at or before 100 characters; one 120-character word → its first 100 characters; NFC applied.
  - `headingName("# ==Tomaten== $x$")` → `Tomaten x` (`==` and `$` dropped before cleaning).
  - `renameTarget`: Untitled with `# Einkaufsliste` in `/Home/Garden` → `/Home/Garden/Einkaufsliste.md`; with `einkaufsliste.md` already there → `/Home/Garden/Einkaufsliste 2.md` (and ` 3` after ` 2`); a folder named `Einkaufsliste.md` counts as taken; a named note → null; no heading → null; heading `Untitled` → null; `/Work/Board/Untitled.md` when `/Work/Board` holds `Board.md` → null (without `Board.md` → renamed).
- [ ] **Step 2:** `pnpm -C web test --run src/note/nameFromHeading.test.ts` — FAIL.
- [ ] **Step 3: Implement the pure part.** `headingName` scans lines: a fence (```` ``` ````/`~~~`, same rules as `outline.ts`) is skipped; outside fences, `%%…%%` segments are removed with a state that spans lines (an unclosed `%%` hides the rest, as `findTags` does); the first remaining line matching `/^ {0,3}#(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/` is the heading; its text goes through `plain` from `outline.ts` (export it as `headingText`) and `cleanName`. `cleanName`: NFC, remove ``/ \ : * ? " < > | # ^ [ ]``, collapse whitespace, trim, strip leading dots and spaces, cut to 100 code points (at the last space if there is one in them), strip trailing dots and spaces; empty → null. `renameTarget`: also null when the cleaned name is itself an untitled name; collision as `untitledName` (`paths.ts:120`), ignoring case, excluding the note itself.
- [ ] **Step 4:** same command — PASS.
- [ ] **Step 5: Failing integration tests** in `MarkdownEditor.test.tsx`, with `renderApp` and `fakeApi` (`src/test/app.tsx`); record `POST /api/move` bodies; the tree holds `/Home/Garden/Untitled.md` (`content: "# "`):
  1. "starts a new note with `# ` and the caret after it": New note from the dock → the PUT body is `{ content: "# " }`; the textarea's `selectionStart === selectionEnd === 2`.
  2. "names the note on Read": type `Einkaufsliste` after `# `, wait 800 ms, click Read → one move `{ from: "/Home/Garden/Untitled.md", to: "/Home/Garden/Einkaufsliste.md", keepLinks: false, exact: false }` after the last PUT; the URL becomes `/notes/Home/Garden/Einkaufsliste.md`; `# ==Tomaten== $x$` names the file `Tomaten x.md` (headingName strips `==` and `$`).
  3. "keeps text typed just before Read": type, click Read at once (no wait) → the PUT carrying the whole text comes before the move.
  4. "names it when another note opens" (tree click), and "when leaving to Settings": the move happens; the renamed path is in `recentPaths` (after a tree click at `[1]`, after Settings at `[0]`) and the old one is gone; the URL stays where the user went.
  5. "leaves a named note alone": `/Home/Garden/Markt.md` with `# Einkaufsliste Samstag`, Write then Read → no move.
  6. "renames an Obsidian `Untitled 1.md`".
  7. "skips while someone else edits": `useSyncStatus.setState({ editors: { [presenceKey("Home", "Garden/Untitled.md")]: ["Bob"] } })` → no move.
  8. "says so when the move fails, once": move answers 409 → a `role="alert"` naming the failure; the note stays at its path; no second move after navigating on.
  9. "doesn't enter Write while the move is in flight": hold the move's answer; the Write button is disabled; no PUT to the old path afterwards.
  10. "two new notes in a row": name the first, New note again → the second `Untitled.md` opens and stays open (no redirect to the first one's new name).
  11. "sends nothing for an Untitled note without a heading": open and leave Write on `# ` → no tree GET, no move (the existing MobileShell tests' cleanup must stay quiet).
  12. "moves once under StrictMode": render inside `<StrictMode>` (or mount, unmount, remount the editor in one act) → no move until Write really ends.
  In `NoteView.test.tsx`: `startsWithH1("# ")`, `startsWithH1("#\n\ntext")` are false; `startsWithH1("# A")` stays true; a note `# ` shows the file title.
- [ ] **Step 6:** `pnpm -C web test --run src/note/MarkdownEditor.test.tsx src/note/NoteView.test.tsx` — new tests FAIL.
- [ ] **Step 7: Implement the orchestration.**
  - `renameFromHeading(queryClient, path, saved)`: return unless `isUntitled(path)` and `headingName(saved)` is non-null (before any network call); unless signed in (`useSession`); unless `mayChange(levelOf(vault), path)` for the vault in `queryClient.getQueryData(["vaults"])` and no `refused` for the path; unless presence for `presenceKey(vaultOf(path), inVault(path))` is empty; unless no rename is in flight. Then `setRenaming(path)`; `fetchQuery` the tree (`["tree", vault]`, `staleTime: 0`); `renameTarget`; `moveNote`; `moved(path, to)`, then in the same continuation `useEditor.getState().follow?.(path, to)` (as `MoveDialog.tsx:72-76` does). On error: `setRenameProblem(\`Couldn't name the note from its heading: ${errorText(e)}\`)`. Finally `setRenaming(null)`. No retry.
  - `MarkdownEditor`: a module-level `Map<string, number>` of mounted editors per path. Mount effect increments; cleanup decrements, captures `!unresolved() && !goneRef.current ? savedText() : null` (`gone` read through a ref), and `setTimeout(…, 0)` calls `renameFromHeading` only if no editor for the path is mounted again by then (StrictMode remounts at once). The focus layout effect also calls `setSelectionRange(len, len)` when the text is exactly `# `.
  - `MobileShell`: `writable={!readOnlyNote && renaming !== path}`; registers `follow` in an effect (cleared on unmount): it navigates `noteUrl(to)` with `replace: true` only when the shell's current URL path (a ref kept in step with `path`) is `from`; unmounted (Settings) there is no follow, so the user is never pulled back and nothing stale remains; `renameProblem` shows as `role="alert"` with the other alerts.
  - `startsWithH1`: an ATX H1 counts only with text after `#` (closing `#`s don't count).
- [ ] **Step 8:** same command — PASS. Gate: `pnpm -C web test --run && pnpm -C web build`.
- [ ] **Step 9: Mutations:** `isUntitled` matching any name · the unmount trigger renaming with unsaved text (drop the `unresolved()` check) · no StrictMode guard (drop the remount check) · `follow` navigating without the URL check · Write not blocked while renaming · presence check removed · the collision suffix removed · the board check removed · `content: ""` again.
- [ ] **Step 10: Commit** "Name a new note from its first heading".

---

### Task 3: `==highlight==` and `%%comments%%` (spec §4)

**Files:**
- Create: `web/src/note/highlights.ts`; Test: `web/src/note/highlights.test.ts`
- Modify: `web/src/note/MarkdownView.tsx` (plugin list `:420-423`, `mark` component `:343-345`), `web/src/note/outline.ts` (comments), `web/src/index.css` (token), `web/src/theme/contrast.test.ts` (highlight contrast)
- Test: `web/src/note/MarkdownView.test.tsx`, `web/src/note/outline.test.ts`

**Interfaces:**
- Produces: `remarkHighlights(): Transformer` → `highlight` nodes, `data: { hName: "mark", hProperties: { dataHighlight: true } }`; `remarkComments(): Transformer` removes comment text and blocks. Order in `MarkdownView`: `remarkGfm, remarkCallouts, remarkTags, remarkWikiLinks, remarkHighlights, remarkComments, remarkFind, remarkChanged`.
- Produces: CSS token `--highlight` (`--color-highlight` in `@theme inline`), one value for dark schemes, one for light.

- [ ] **Step 1: Failing tests** (`MarkdownView.test.tsx`, via `show()`):
  - `- ==Tomaten== %%die gelben%%` → a `mark` "Tomaten" with `data-highlight`, and "die gelben" not in the document.
  - `a == b and c == d` → no `mark`, text as written; `` `==x==` `` and a fenced block with `==x==` and `%%y%%` stay code.
  - `==a *b* [[c]]==` with the link answered → one `mark` holding the emphasis and the link; `==[[c]]==` → the link inside a `mark`; `==#tag==` → a `mark` holding plain `#tag` (no chip: a tag needs whitespace before `#`, as on the server); `==a #tag==` → the chip inside a `mark`.
  - `==a\nb==` → no `mark` (not across lines).
  - `> [!note] T\n> ==x== %%y%%` → highlight and hidden comment inside the callout.
  - `%%\n# Hidden\n\nmore\n%%\nShown` → "Hidden" and "more" absent, "Shown" present; an unclosed `%%` hides the rest.
  - Find (`find={{ query: "gelben", current: 0 }}`) on the shopping line → no `mark[data-find]`; find on "Tomaten" → a find mark (`data-find="0"`, the first match) inside the highlight, with the accent class, not the highlight class.
  - Agent highlight (`highlight={{ lines: new Set([1]), writer: "Bob" }}`) on `==x==` still sets `data-changed`.
  - `outline.test.ts`: `outline("%%\n# Hidden\n%%\n# Shown")` → only `Shown` at line 4; `# A %%c%%` → text `A`.
  - `contrast.test.ts`: every scheme's `text` on its `highlight` ≥ 4.5:1.
- [ ] **Step 2:** `pnpm -C web test --run src/note/MarkdownView.test.tsx src/note/outline.test.ts src/theme` — FAIL.
- [ ] **Step 3: Implement.** Inline pass over each parent's phrasing children: delimiters only in `text` nodes; `==` opens when the next character is not whitespace and the one before is not `=`, closes when the one before is not whitespace and the next is not `=`; opener and closer are siblings in the same parent with no `\n` or `break` between them. Split text nodes keep a `position` (start line plus the newlines before the part, as `callouts.ts:79`). Comments: pairs of `%%` among siblings remove the range (newlines allowed); an opener with no closer in its paragraph starts a block comment that removes the following blocks of the same parent up to the block holding the closer (text after the closer stays); none → to the end. `mark` component: `node?.properties.dataFind !== undefined` → today's classes (the first match is `0`, falsy); `dataHighlight` → `rounded-[3px] bg-highlight px-0.5 text-text`. Token: follow the project's pattern: a `highlight` entry in `Tokens` (`web/src/theme/schemes.ts`) for every scheme, repeated as `--highlight` in each `[data-scheme]` block of `index.css` (`apply.test.ts` keeps them in step), `--color-highlight` in `@theme inline`; a warm yellow per scheme (dark schemes a darker muted yellow around `#5c4a0c`, light ones around `#fff09a`), held by `contrast.test.ts` to 4.5:1 for `text` on it. `outline`: skip lines inside `%%` (state across lines, outside fences); `plain` drops inline `%%…%%`.
- [ ] **Step 4:** same command — PASS. Gate: `pnpm -C web test --run && pnpm -C web build`.
- [ ] **Step 5: Mutations:** whitespace-flanking check removed · plugins before `remarkWikiLinks` · comments after `remarkFind` · `mark` branching on the truthiness of `dataFind` · the block comment pass removed · `outline` ignoring comments.
- [ ] **Step 6: Commit** "Render ==highlights== and hide %%comments%% in the note view".

---

### Task 4: Maths with KaTeX (spec §5)

**Files:**
- `pnpm -C web add remark-math@^6 rehype-katex@^7 katex@^0.16`
- Create: `web/src/note/prepareMath.ts` (+ `prepareMath.test.ts`), `web/src/note/math.ts`, `web/src/note/math.css`
- Modify: `web/src/note/MarkdownView.tsx`, `web/vite.config.ts:60-67` and `build`
- Test: `web/src/note/MarkdownView.test.tsx` (`describe("Maths")`)

**Interfaces:**
- Produces: `prepareMath(source: string): { source: string; maths: boolean }` — same length and same line count as the input; runs of `$` are tokens paired by equal length (`$x$ in $$y$$`); a `$` that can't pair (and an unclosed `$$` fence line) becomes U+FDD0; `$$` fence lines are recognised inside containers too (after `>` markers and list indentation); the bodies of `$$` blocks, fenced code, inline code, link destinations (`](…)`) and autolinks (`<…>`, bare `https://…`) are never changed; `\[` and `\]` standing alone on their lines become `$$`. Decided against the brief's full `\( … \)` / inline `\[ … \]` conversion: both are CommonMark escapes (`\[TODO\]`), so converting them would change notes that render fine today (rule 1); recorded as a decision and an open item. If the input already contains U+FDD0, it is returned unchanged with `maths` from a plain `$` test.
- Produces: `remarkRestoreDollars({ original }: { original: string })` — first transform: every U+FDD0 back to `$` in every string field of every node (`value`, `url`, `title`, `alt`, `meta`, and the text nodes in `data.hChildren` of `math`/`inlineMath`), and `file.value = original` (positions stay valid, the lengths are equal).
- Produces (`math.ts`, loaded by `import("./math")`): `remarkPlugins: PluggableList` (remark-math, `remarkDisplayParagraph`, `remarkMathBlocks`) and `rehypePlugins: PluggableList` (`[rehypeKatex, { trust: false, strict: "ignore", errorColor: "var(--warning)" }]`; rehype-katex's options type has no `throwOnError`: it tries with errors on and falls back to KaTeX's `throwOnError: false` rendering itself, which the report notes); imports `katex/dist/katex.min.css` then `./math.css`.

- [ ] **Step 1: Failing `prepareMath` tests:** `costs $5 and $10` → both `$` hidden, `maths: false`; `$3` alone hidden; `$x$` kept, `maths: true`; `$ x $` hidden; `$x$5` → closing refused (digit after) so both hidden; `$a\n\nb$` hidden (blank line); `$a\nb$` kept; `` `$x$` `` and ```` ```\n$x$\n``` ```` unchanged and `maths: false`; `$$\nx\n$$` kept, `maths: true`; `$$x$$` on one line kept; an unclosed `$$` line hidden; `\[\nA\\\\B\n\]` → `$$\nA\\\\B\n$$`; `\\[5pt]` and an inline `\[x\]` unchanged; an already escaped `\$` unchanged; `$x$ in $$y$$` both kept; `> $$\n> x\n> $$` kept, `maths: true`; `[a](https://x/?p=$5)` unchanged; `<https://x/$5>` unchanged; output length and line count equal the input's for every case (one `it.each`).
- [ ] **Step 2:** `pnpm -C web test --run src/note/prepareMath.test.ts` — FAIL; implement; PASS.
- [ ] **Step 3: Failing MarkdownView tests** (`await screen.findBy…`, the module loads asynchronously): `Bed area: $A = \pi r^2$` → a `.katex` element, "Bed area:" text; `Price per kilo: $3` → text `Price per kilo: $3`, no backslash, no `.katex`; `costs $5 and $10` → as written; `$$\n\\begin{aligned} a &= b \\\\ c &= d \\end{aligned}\n$$` → `.katex-display`; one-line `$$x^2$$` alone in a paragraph → `.katex-display`; `$x$ in $$y$$ text` → two inline `.katex`, no display; `` `$x$` `` → `code` with `$x$`; a fenced ```` ```math ```` block in a note that also has `$x$` stays a code block (no `.katex` inside `pre`); `[a](https://x/?p=$5)` keeps its href; `$$\na$b\n$$` reaches KaTeX with `$`; `$\frac{1}{$` → `.katex-error` showing its source, no thrown error; `\href{javascript:alert(1)}{x}` inside `$…$` → no `a` element; `console.warn`/`console.error` spies untouched by any maths case; agent highlight on a maths block's line → an element with `data-changed` containing `.katex-display`; a `[link](/Work/plan.md)` after `$5` on the same line still resolves (wikiLinks' raw slice); a note without `$` renders the same HTML as before Task 4 (snapshot of one rich note taken before the change).
- [ ] **Step 4:** run — FAIL.
- [ ] **Step 5: Implement.** `MarkdownView`: `const prepared = useMemo(() => prepareMath(source), [source])`; a module-level loader `loadMath()` (cached promise) and `useMath(prepared.maths)` (state set when loaded; a failed load leaves maths as text); plugins `remarkGfm, ...math.remarkPlugins[0] (remark-math), [remarkRestoreDollars, { original: source }], remarkCallouts, …, remarkFind, remarkDisplayParagraph, remarkMathBlocks, remarkChanged` and `rehypePlugins={math?.rehypePlugins}`. `remarkDisplayParagraph`: a paragraph whose only child is an `inlineMath` whose source (`original.slice(start.offset, end.offset)`) starts with `$$` becomes a `math` node with the paragraph's position, its `data` as `mdast-util-math` builds a flow `math` (`hName: "pre"`, `hChildren: [code.language-math.math-display]`). `remarkMathBlocks`: every `math` node gets `hName: "div"`, `hProperties: { className: ["k-math"] }`, `hChildren: [pre > code.language-math.math-display]` (rehype-katex replaces the `pre` only for `language-math`), so `remarkChanged`'s properties land on the `div` and survive rehype-katex replacing the `pre`. `remarkMathBlocks` also gives fenced `code` nodes with `lang: "math"` `data.hProperties.className = ["language-math-source"]`, so rehype-katex (which renders any `code.language-math`) leaves them as code. `math.css`: `.katex-display { overflow-x: auto; overflow-y: hidden; margin: 0.6em 0; padding: 0.25em 0 }`, `.katex-error { text-decoration: underline dotted; cursor: help }`.
- [ ] **Step 6: Bundle.** `vite.config.ts`: the `markdown` group excludes `remark-math`, `micromark-extension-math`, `mdast-util-math`, `rehype-katex`, `katex` and the hast utilities only rehype-katex uses (check with the build which ones: `hast-util-from-html-isomorphic`, `hast-util-from-dom`, `hast-util-from-html`, `hast-util-to-text`, `hast-util-is-element`, `parse5`, …), so they land in the lazily loaded chunk; `build.assetsInlineLimit: (file) => (/\.(woff2?|ttf)$/.test(file) ? false : undefined)`. Verify: `pnpm -C web build`; `grep -l "data:font" dist/assets/*` finds nothing; `dist/index.html` preloads no maths chunk; `grep -l katex dist/assets/main-*.js dist/assets/markdown-*.js` finds nothing; record main and markdown sizes against Task 0's.
- [ ] **Step 7:** tests PASS. Gate: `pnpm -C web test --run && pnpm -C web build`.
- [ ] **Step 8: Mutations:** the digit rule removed · the blank-line rule removed · code masking removed · `file.value` not restored · `remarkMathBlocks` removed (the agent highlight test fails) · the ```` ```math ```` guard removed · `strict` back to default (the console spy fails) · the paragraph-to-display plugin removed.
- [ ] **Step 9: Commit** "Typeset maths with KaTeX, loaded only for notes that have it".

---

### Task 5: Footnotes (spec §6)

**Files:**
- Modify: `web/src/note/MarkdownView.tsx` (`a` component `:375-385`, `remarkRehypeOptions`, embed nesting `:161`), `web/src/index.css` (footnote look, if needed)
- Test: `web/src/note/MarkdownView.test.tsx` (`describe("Footnotes")`)

**Interfaces:**
- Consumes: `MarkdownView`'s `nested` prop. Produces: a `clobberPrefix` from `useId()` (stripped to `[a-z0-9-]`) for every MarkdownView, nested or not (History and checkpoints render top-level views too); the fragment lookup stays inside the view's own root.

- [ ] **Step 1: Failing tests:** `Text[^1]\n\n[^1]: The note.` → a `sup` link `1` to `#user-content-fn-1`, a `section[data-footnotes]` with the definition and a back link; clicking the reference calls `scrollIntoView` (stub `Element.prototype.scrollIntoView`) on the definition's `li`, `preventDefault`s (the memory router's location is unchanged); clicking the back link scrolls to the reference; the `h2` "Footnotes" keeps `sr-only`; two views (a note and its embed) produce no duplicate ids (`document.querySelectorAll("[id]")` ids unique).
- [ ] **Step 2:** run `pnpm -C web test --run src/note/MarkdownView.test.tsx` — FAIL where something is missing (record which already passed: that is §6's "test that says so").
- [ ] **Step 3: Implement.** `a`: an `href` starting with `#` → `onClick` finds `CSS.escape`d id inside `event.currentTarget.closest("article") ?? document`, `preventDefault`, `scrollIntoView({ block: "center" })`, focus the target if focusable. Every view passes `remarkRehypeOptions={{ clobberPrefix }}` with `user-content-<useId stripped to [a-z0-9-]>-`; the click handler looks the id up inside the view's root `div` (a ref on it), not the document. Footnote look: the section `mt-8 border-t border-line pt-3 text-[14px] text-muted`, references in `text-accent-text`.
- [ ] **Step 4:** PASS. Gate: `pnpm -C web test --run && pnpm -C web build`.
- [ ] **Step 5: Mutations:** no `preventDefault` · one fixed prefix for every view · the h2 losing `sr-only` (spread before the className).
- [ ] **Step 6: Commit** "Let footnotes jump within the note".

---

### Task 6: Final gate, pull request, report

- [ ] **Step 1:** Fresh MongoDB as in Task 0; then CI's steps: `dotnet restore --locked-mode`, `dotnet build --configuration Release --no-restore`, `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`. Record counts.
- [ ] **Step 2:** Look at the app if possible (`pnpm -C web dev` against a local server, else component tests) at 390 px wide, light and dark: Alice's shopping list, maths, footnotes, the menus.
- [ ] **Step 3:** Push; open the pull request "Obsidian parity: the quick wins for 0.4.0" (decisions made without Chris, security boundary not touched, ending `🤖 Generated with [Claude Code](https://claude.com/claude-code)`).
- [ ] **Step 4:** Last commit: the report (environment, plan review findings and what was done, Task 0 planned vs measured, per task commit and real test output, every mutation, main and markdown chunk sizes before/after, decisions made without Chris, deviations and open points, and a numbered hand-test checklist covering: Alice's shopping list end to end, an Obsidian-made `Untitled 1.md` being named, a manually named note left alone, the three copy items in the ⋯ menu and the tree, a read-only member copying, maths at phone width in dark mode, footnotes) `docs/superpowers/overnighter/2026-11-09-obsidian-parity-quick-wins-report.md` and a row in `docs/superpowers/open-items.md` with the pull request's link. Then `PushNotification` with the pull request link. **Do not merge, tag, deploy or change the version.**

# Mermaid and parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw Mermaid diagrams as images in every read view, append embedded notes to a standing-alone `cat`, resolve block references `^id` on both sides, and list unlinked mentions for agents and in the web app.

**Architecture:** Mermaid lives in `web/src/reader/` (the reader page's isolation allows nothing else), loaded lazily as KaTeX is, rendered one diagram at a time to an SVG string that reaches the page only as `<img src="blob:…">`. Block ids get one finder per side (`Kumbu.Core.Sections.Blocks`, `web/src/note/blocks.ts`) held to one shared cases file. `cat`'s appendix is added in `Shell`, where standing alone is decided. Unlinked mentions are a Core finder used by `backlinks --unlinked` and by a new endpoint `GET /api/mentions/{vault}/{**path}`.

**Tech Stack:** C# / .NET 10 (xUnit, Microsoft.Testing.Platform), React 19 + Vite + Vitest + Testing Library, `mermaid` 11.x (the only new dependency).

**Spec:** `docs/superpowers/specs/2026-11-12-mermaid-and-parity-design.md`; brief: `docs/superpowers/overnighter/2026-11-12-mermaid-and-parity-brief.md`. Executors read both.

## Global Constraints

- British English in code, comments, docs, commits and every user-visible string. Match surrounding naming, idiom and comment density; comments only where the reasoning isn't obvious.
- New `.ts`, `.tsx` and `.cs` files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- No new dependency except `mermaid` in `web/package.json`: `"mermaid": "^11.17.2"` (caret, as the neighbours), lockfile committed.
- No organisation, customer or employer names; test data uses Alice, Bob, Dana, `Work`, `Home`, `Tagebuch`.
- Neither CSP in `src/Kumbu.Server/Api/Security.cs` changes. Nothing in `Auth/`, `Crypto/`, sharing, roles, discreet vaults, publishing semantics, `FairCopies.cs`, WebDAV, sync or OAuth changes.
- Mermaid: `securityLevel: "strict"`, default `maxTextSize`/`maxEdges`, `htmlLabels: false` (root and `flowchart`), `fontFamily` a system stack; the SVG reaches the DOM only as an `<img>` of a Blob object URL, revoked when the image goes or changes. No note text ever reaches `dangerouslySetInnerHTML`.
- `web/src/reader/*` may import only `react`, `react-dom/client`, `react-markdown`, `remark-gfm`, `mermaid` and files in `web/src/reader/` (and `../index.css`). Nothing there imports `web/src/theme/*`.
- `cat`, `links`, `backlinks` answer byte for byte as before wherever the spec adds nothing. The note's own text in `cat`'s output stays byte for byte what is stored.
- Line numbers shown to agents are 1-based in the whole note (also for `cat "<path>#section"`).
- Commits: imperative, free-form, no Conventional Commits prefix, ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Stage files by name. Subagents never commit, push, merge or switch branches: the controller does.
- Every task: red test first, then green, then each mutation listed in the task (break the code, run the named test, watch it fail, restore with `git checkout -- <file>`), reporting each mutation's result.
- Environment for every command: `. /tmp/claude-0/-home-user-kumbukumbu/683227b5-6b43-57a0-b0cf-a8478c46b103/scratchpad/env.sh` (Node 26, `KUMBU_TOKENIZER`, `KUMBU_TEST_MONGO`). Server tests need the MongoDB container: `bash /tmp/claude-0/-home-user-kumbukumbu/683227b5-6b43-57a0-b0cf-a8478c46b103/scratchpad/mongo.sh`.
- Commands: core class `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ClassName"`; server class likewise with `tests/Kumbu.Server.Tests`; web file `pnpm -C web test --run <file>`; web gate `pnpm -C web build` (type check included).

## Review Focus

1. **A Mermaid source that tries to inject markup or script** (`click A call alert(1)`, `<img onerror>` in a label, `%%{init: {"securityLevel":"loose"}}%%`): must draw as an image or fail to the source with the error line, never run or reach the DOM. → Task 1 test "an init directive cannot loosen the security level" (the component passes `securityLevel: "strict"` on every `initialize` and the render result is only ever used as Blob content).
2. **A note whose `^id` sits on a line inside a fenced code block or front matter**: not a block. → shared cases in Task 3 (`"inside code"`, `"in front matter"`), run by both finders.
3. **`cat` on a note that embeds itself, or embeds the same note twice, or embeds a note in another folder by bare name**: itself never, each target once, resolution as `links`. → Task 7 tests.
4. **A title that is a common word ("Plan") inside a URL or inline code or a link's text**: no mention. → Task 8 test "mentions in code, links, URLs and front matter don't count".
5. **Theme switched while a diagram is shown, and several diagrams in one note**: redraw with the new theme; render one after another. → Task 2 tests "redraws on a change of data-scheme" and Task 1 "renders diagrams one after another".

---

### Task 0: Baseline

**Files:** none changed.

- [ ] **Step 1:** Run the full gate (`bash /tmp/claude-0/-home-user-kumbukumbu/683227b5-6b43-57a0-b0cf-a8478c46b103/scratchpad/gate.sh <logdir>`, which runs the brief's gate line by line with a fresh MongoDB before each server group) on `feature/mermaid-parity` before any change.
- [ ] **Step 2:** Record total, skipped and failed for core, each of the six server groups, web (files and tests), web build, PDF sidecar, against the brief's known state (core 2,266/3 skipped; server 843+413+353+20+780+1,163 = 3,572; web 131 files/2,361 tests; PDF 9 files/198 tests). A failure in a known-flaky class: run the class alone three times and record.

### Task 1: The Mermaid component (reader folder)

**Files:**
- Modify: `web/package.json`, `web/pnpm-lock.yaml` (add `"mermaid": "^11.17.2"` with `pnpm -C web add mermaid@^11.17.2`)
- Create: `web/src/reader/mermaidRender.ts`, `web/src/reader/MermaidDiagram.tsx`
- Modify: `web/src/reader/isolation.test.ts:7` (allowed set gains `"mermaid"` only)
- Modify: `web/vite.config.ts:67-71` (mermaid and its dependencies return `undefined` from `manualChunks`, as KaTeX's do; leave `chunkSizeWarningLimit` alone)
- Test: `web/src/reader/MermaidDiagram.test.tsx`

**Interfaces:**
- Produces `web/src/reader/mermaidRender.ts` (only ever loaded by `import("./mermaidRender")`):
  - `export async function renderDiagram(source: string, dark: boolean): Promise<{ svg: string; alt: string }>` — throws `Error` whose `message` is Mermaid's first message line when the source can't be drawn. Calls, in a module-level queue (one render at a time, in call order): `mermaid.initialize(config(dark))`, `await mermaid.parse(source)`, `await mermaid.render(id, source)`; removes any element Mermaid left in `document` for that id on failure. Sets `width`/`height` on the root `<svg>` from its `viewBox` (and drops `style="max-width…"`) so the image has its natural size.
  - `export function diagramAlt(source: string): string` — `accTitle` and `accDescr` (single-line `accDescr: …` or block `accDescr { … }`) joined with ". " when present; else `Diagram (<type>)`: the first keyword of the first non-blank, non-`%%` line, `graph` → `flowchart`, a trailing `Diagram` and `-v2`/`-beta` removed, lower-cased (`sequenceDiagram` → `sequence`, `stateDiagram-v2` → `state`).
  - config: `{ startOnLoad: false, securityLevel: "strict", theme: dark ? "dark" : "default", htmlLabels: false, flowchart: { htmlLabels: false }, fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif' }`.
- Produces `web/src/reader/MermaidDiagram.tsx`: `export default function MermaidDiagram({ source, dark }: { source: string; dark: boolean })` — and nothing that imports `mermaid` statically.

Behaviour of `MermaidDiagram`:
- On mount and on a change of `source` or `dark`: `import("./mermaidRender")` (memoised module promise; a failed load is forgotten so a later mount retries), then `renderDiagram`. Until drawn: the code block (see below) is not shown; a placeholder `<p>` "Drawing the diagram …" in muted text.
- Drawn: `<figure>` holding a frame `div` (`overflow-x-auto`) with `<img src={blob URL} alt={alt}>` (`max-w-full h-auto`, i.e. natural width, scaled down never up), the Blob `new Blob([svg], { type: "image/svg+xml" })`; the URL revoked on unmount and before a new one replaces it. Under it a row of buttons (min-h-11): **Show source** / **Hide source** (`aria-expanded`, `aria-controls` the code block's id) and, only when the image's natural width is larger than the frame's client width, **Full size** / **Fit** (`aria-pressed`): full size removes `max-w-full` so the frame scrolls (decision: scrolling within the frame; the app's lightbox is a route for vault attachments and can't take a blob).
- Parse error: `<p role="note">This diagram can't be drawn: {message}</p>` above the code block, shown open. Load failure: `<p role="note">Diagrams can't be drawn right now.</p>` above the code block. Nothing is thrown past the component.
- The code block is `<pre><code className="language-mermaid">{source}</code></pre>` with the same classes as `Reader.tsx`'s `pre` (the component's own copy; no import from the note folder).

- [ ] **Step 1: Write the failing tests** in `web/src/reader/MermaidDiagram.test.tsx`, with `vi.mock("mermaid", () => ({ default: { initialize: vi.fn(), parse: vi.fn(), render: vi.fn() } }))` and `URL.createObjectURL`/`revokeObjectURL` stubbed:
  - `draws the diagram as an image with its alt text`: source `"flowchart LR\n  Sensor --> Controller --> Pump"`, render resolves `{ svg: '<svg viewBox="0 0 640 120" width="100%" style="max-width: 640px;"><g/></svg>' }`; expect `getByRole("img", { name: "Diagram (flowchart)" })` with `src` `blob:…`, and the Blob's text (read from the `createObjectURL` mock's argument) contains `width="640"` and `height="120"` and no `width="100%"`.
  - `uses accTitle and accDescr as the alt text`: `"flowchart LR\n accTitle: Pump chain\n accDescr: Sensor to pump\n A-->B"` → alt `Pump chain. Sensor to pump`.
  - `passes the theme`: `dark: true` → `initialize` last called with `expect.objectContaining({ theme: "dark", securityLevel: "strict", htmlLabels: false, flowchart: expect.objectContaining({ htmlLabels: false }) })`; rerender with `dark: false` → `theme: "default"`, a second `render` call, the first URL revoked.
  - `an init directive cannot loosen the security level`: source `'%%{init: {"securityLevel": "loose"}}%%\nflowchart LR\n A-->B'` → every `initialize` call has `securityLevel: "strict"`; no element other than the `img` was added to the container (`container.querySelectorAll("svg").length === 0`).
  - `shows the source under Show source`: button `Show source` with `aria-expanded="false"`; click → `"true"`, the `code` with the source visible.
  - `a parse error shows the source and the line`: `parse` rejects `new Error("Parse error on line 2:\n...")` → text `This diagram can't be drawn: Parse error on line 2:` and the source in a `code`; no `img`.
  - `a failed load says diagrams can't be drawn right now`: `vi.doMock` makes `./mermaidRender` reject (separate file `MermaidDiagram.loadFailure.test.tsx`, its own module registry) → `Diagrams can't be drawn right now.` and the code block.
  - `renders diagrams one after another`: two diagrams; the first `render` is held by a deferred promise; expect `render` called once until it resolves, then twice.
  - `revokes the image's URL when it goes`: unmount → `revokeObjectURL` called with the URL.
- [ ] **Step 2:** `pnpm -C web test --run src/reader/MermaidDiagram.test.tsx` — FAIL (module missing).
- [ ] **Step 3:** Add the dependency, implement `mermaidRender.ts` and `MermaidDiagram.tsx`, extend `isolation.test.ts`'s set with `"mermaid"` (nothing else), and in `vite.config.ts` return `undefined` from `manualChunks` for `/node_modules/.pnpm/(mermaid|@mermaid-js|…its dependencies that would otherwise match the markdown or vendor regexes)@/` with a comment in the KaTeX comment's style.
- [ ] **Step 4:** `pnpm -C web test --run src/reader` — PASS; `pnpm -C web build` — PASS; then check the output: `grep -l mermaid web/dist/assets/*.js` lists only lazy chunks, and neither the `main` nor the `s` entry chunk (`web/dist/index.html`'s and `web/dist/s.html`'s module scripts and their static `import`s, read with `grep -o 'from"./[^"]*"' …`) reaches one of them statically. Record the chunk-size warning for the mermaid chunk (accepted).
- [ ] **Step 5: Mutations**, each run with the Step 4 test command: (a) `securityLevel: "loose"` in the config → the security test fails; (b) drop the `revokeObjectURL` on unmount → the revoke test fails; (c) render without the queue (call `render` directly) → the one-after-another test fails; (d) skip the width/height fix → the first test fails; (e) add `"katex"` to the isolation set → no test fails, so restore and note that the isolation test pins only what is imported (expected).
- [ ] **Step 6: Commit** `web/package.json web/pnpm-lock.yaml web/vite.config.ts web/src/reader/isolation.test.ts web/src/reader/mermaidRender.ts web/src/reader/MermaidDiagram.tsx web/src/reader/MermaidDiagram.test.tsx web/src/reader/MermaidDiagram.loadFailure.test.tsx` — "Add a Mermaid diagram component drawn as an image".

### Task 2: Mermaid in the note view

**Files:**
- Create: `web/src/theme/dark.ts`
- Modify: `web/src/note/MarkdownView.tsx` (`pre` component, ~line 448)
- Test: `web/src/theme/dark.test.ts`, `web/src/note/MarkdownView.mermaid.test.tsx`

**Interfaces:**
- Consumes: `MermaidDiagram` from `../reader/MermaidDiagram` (Task 1).
- Produces: `export function useDarkScheme(): boolean` in `web/src/theme/dark.ts` — `!scheme.light` of the scheme named by `document.documentElement.dataset.scheme` (`schemes` from `./schemes`; unknown or absent: `!matchMedia("(prefers-color-scheme: dark)").matches` is not used, the app always sets one — fall back to light), updated by a `MutationObserver` on `<html>`'s `data-scheme` attribute.

- [ ] **Step 1: Failing tests.**
  - `dark.test.ts`: `useDarkScheme follows data-scheme`: set `data-scheme` to a light scheme id from `schemes` → `false`; change the attribute to a dark one inside `act` → `true`.
  - `MarkdownView.mermaid.test.tsx` (mock `../reader/mermaidRender` with `renderDiagram: vi.fn(async () => ({ svg: "<svg viewBox='0 0 10 10'/>", alt: "Diagram (flowchart)" }))`, a module registry of its own, as `MarkdownView.mathLoading.test.tsx` does):
    - `draws a mermaid block as an image`: source `"# Plan\n\n```mermaid\nflowchart LR\n  A --> B\n```\n"` → `findByRole("img", { name: "Diagram (flowchart)" })`.
    - `loads nothing for a note without a mermaid block`: `"```js\nx\n```"` → after 100 ms the mock module was never loaded (a `loaded` spy in the mock factory) and the code shows as a `pre`.
    - `redraws on a change of data-scheme`: start light, `renderDiagram` called with `dark: false`; set `data-scheme` to a dark scheme → called with `(source, true)`.
    - `an embedded note's mermaid block is drawn too`: `nested` view of the same source → image.
    - `keeps the agent's highlight on a diagram`: with `highlight={{ lines: "all", writer: "Claude" }}` the element around the figure has `data-changed`.
- [ ] **Step 2:** `pnpm -C web test --run src/theme/dark.test.ts src/note/MarkdownView.mermaid.test.tsx` — FAIL.
- [ ] **Step 3:** Implement `useDarkScheme`; in `MarkdownView.tsx`'s `pre` component, when its only element child is a `code` whose `className` contains `language-mermaid`, render `<div {...props without children}><MermaidDiagram source={text of the code without its final newline} dark={dark} /></div>`, `dark` from a small component that calls `useDarkScheme()` (the `components` map keeps its identity). Every other `pre` as before.
- [ ] **Step 4:** Step 2's command — PASS; `pnpm -C web test --run src/note` — PASS; `pnpm -C web build` — PASS; check again that `main`'s entry doesn't import the mermaid chunk statically.
- [ ] **Step 5: Mutations:** (a) pass `dark={false}` always → redraw test fails; (b) match `language-mermai` → image test fails; (c) import `./mermaidRender` statically in `MermaidDiagram.tsx` → the loads-nothing test fails.
- [ ] **Step 6: Commit** — "Draw Mermaid diagrams in the note view".

### Task 3: The Core block finder and `cat "<path>#^id"`

**Files:**
- Create: `src/Kumbu.Core/Sections/Blocks.cs`, `web/src/note/blocks.cases.json`
- Modify: `src/Kumbu.Core/Sections/SectionLookup.cs` (add `TryFindBlock`), `src/Kumbu.Core/Sh/Commands/CommandArgs.cs:117-129` (`ReadAddressAsync` takes `#^id`), `tests/Kumbu.Core.Tests/Kumbu.Core.Tests.csproj:23` (link the cases file as `Sections/blocks.cases.json`, as comments.cases.json is)
- Test: `tests/Kumbu.Core.Tests/Sections/BlocksTests.cs`, `tests/Kumbu.Core.Tests/Sh/CatTests.cs`

**Interfaces:**
- Produces `Kumbu.Core.Sections`:
  - `public sealed record Block(string Id, int Start, int End)` — 0-based lines, `End` exclusive, marker line included when the id stands on a line of its own.
  - `public static class Blocks { public static IReadOnlyList<Block> Parse(IReadOnlyList<string> lines); public static Block? Find(IReadOnlyList<string> lines, string id); public static bool TryId(string fragment, out string id); }` — `Find` compares ids ignoring case (`OrdinalIgnoreCase`), first block wins; `TryId("^controller", out id)` is true with `id == "controller"`, also for a fragment percent-encoded as `%5Econtroller` (decode first), false for anything not `^[A-Za-z0-9-]+`.
  - `SectionLookup.TryFindBlock(VaultPath path, string content, string id, [NotNullWhen(true)] out string? text, [NotNullWhen(false)] out string? error)` — `text` = the block's lines joined; `error` = `"{path}#^{id}: no such block (a block id is written as ^{id} at the end of a paragraph, item or heading)"` (no command prefix, as `TryFind`'s).
- Shared cases file `web/src/note/blocks.cases.json`: `[{ "name": string, "note": string, "id": string, "lines": [first, last] | null }]`, lines 1-based inclusive.

Block rules (spec §4.1): skip front matter (`Outline.FrontmatterEnd`) and fenced code (`Outline.CodeLines`), and indented code (4+ spaces or a tab, after a blank line, outside a list). Split the rest into chunks at blank lines and at headings/fences. Inline id: a line ending in ` ^id` (space before `^`, id `[A-Za-z0-9-]+`, trailing whitespace allowed): a heading → that line; a paragraph's last line → the paragraph; a list item's line → that item with its nested lines (until a line indented at or less than the item's marker that starts an item or less-indented text); a quote/callout's last line (`>`) → the whole quote. Own-line id: a line that is only `^id` names the chunk directly above it, with at most one blank line between (a list, table, quote or callout as a whole, or a paragraph), its range from that chunk's first line to the marker line.

- [ ] **Step 1: Failing tests.** Cases (`blocks.cases.json`), at least: `"a paragraph's id at its end"` (`"# Plan\n\nThe pump needs a new controller. ^controller\n"`, `controller`, `[3,3]`); `"a paragraph over lines"`; `"a heading"` (`"## Decision ^decision"`); `"a list item with its nested items"`; `"a list as a whole, id on its own line after a blank"` (`"- a\n- b\n\n^list"` → `[1,4]`); `"a table, id right below"`; `"a quote"`; `"a callout"` (`"> [!note] Title\n> text ^call"`); `"inside code"` (fenced block holding `x ^code` → `null`); `"in front matter"` (`"---\nk: v ^fm\n---\n"` → `null`); `"no space before the caret"` (`"word^x"` → `null`); `"the first of two wins"`; `"ignoring case"` (written `^Controller`, asked `controller`); `"an unknown id"` → `null`; `"indented code"` (`"text\n\n    code ^ind\n"` → `null`). `BlocksTests.Every_shared_case` theory over the file (deserialised like `FairCopyTests.cs:72`). `CatTests`: `A_block_prints_with_its_marker` (`cat "/Work/Pump/Plan.md#^controller"` → `The pump needs a new controller. ^controller`, version token of the whole note), `An_unknown_block_is_an_error` (`cat: /Work/Pump/Plan.md#^contoller: no such block (a block id is written as ^contoller at the end of a paragraph, item or heading)`), `A_percent_encoded_caret_names_a_block` (`#%5Econtroller`).
- [ ] **Step 2:** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*BlocksTests"` and `--filter-class "*CatTests"` — FAIL (compile).
- [ ] **Step 3:** Implement `Blocks`, `TryFindBlock`, and in `ReadAddressAsync`: `if (fragment is not null && Blocks.TryId(fragment, out var id))` → `TryFindBlock`, else as today.
- [ ] **Step 4:** Step 2's commands — PASS; also `--filter-class "*SectionCommandsTests"` and `"*SectionAddressTests"` — PASS (unchanged).
- [ ] **Step 5: Mutations:** (a) don't skip code lines → `inside code` fails; (b) ordinal compare → `ignoring case` fails; (c) last block wins → `the first of two wins` fails; (d) allow no space before `^` → `no space before the caret` fails.
- [ ] **Step 6: Commit** — "Find block ids and let cat print a block".

### Task 4: `links` and moves with block references

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/LinksCommand.cs:76-117` (range for `#^id`; describe block links), its `Help`
- Test: `tests/Kumbu.Core.Tests/Sh/LinkCommandsTests.cs`, `tests/Kumbu.Core.Tests/Notes/NoteMoverTests.cs`

**Interfaces:**
- Consumes: `Blocks.TryId`, `Blocks.Find` (Task 3). A link's block id: `Blocks.TryId(link.Subpath[1..], out id)` when `Subpath` starts with `#` (a Markdown link's subpath is still percent-encoded; `TryId` decodes).

Output (spec §4.2, brief §5): a link with a block id to a note that resolves: `→ /Work/Pump/Plan.md#^controller` when `Blocks.Find` finds it in that note's current content, `→ not found: no block ^controller in /Work/Pump/Plan.md` when not; `[[#^id]]` (kind Self): `→ this note` when the block is in the note, `→ not found: no block ^id in this note` when not. Every other link exactly as today (a `#Heading` subpath prints the path without it). `links <directory>` lists the block-not-found ones among the broken. `links "<path>#^id"` lists the links within the block's lines. Each target note read once per command.

- [ ] **Step 1: Failing tests** in `LinkCommandsTests` with the spec's vault (`/Work/Pump/Plan.md` as in spec §1, `/Work/Pump/Tasks.md` "- see [[Plan#^controller]]", plus lines `[[Plan#^contoller]]`, `[c](Plan.md#%5Econtroller)`, `[[#^here]]` with `^here` present and `[[#^gone]]` absent, `[[Plan#Plan]]`): `Links_to_a_block_name_it`, `A_missing_block_is_not_found`, `A_percent_encoded_caret_counts`, `A_block_of_the_note_itself`, `A_heading_subpath_prints_as_before`, `A_directory_lists_missing_blocks_among_the_broken` (`/Work/Pump/Tasks.md:2: [[Plan#^contoller]] → not found: no block ^contoller in /Work/Pump/Plan.md`), `Links_of_a_block`. `NoteMoverTests.Moving_a_note_keeps_block_subpaths`: moving `/Work/Pump/Plan.md` to `/Work/Old/Plan.md` rewrites `[[Plan#^controller]]` keeping `#^controller` and `[c](/Pump/Plan.md#%5Econtroller)` keeping `#%5Econtroller` (follow the file's existing move tests for the rewritten form).
- [ ] **Step 2:** `--filter-class "*LinkCommandsTests"` and `"*NoteMoverTests"` — the links tests FAIL; the mover test is expected to PASS already (brief: no code change unless it fails).
- [ ] **Step 3:** Implement in `LinksCommand` (a private `DescribeAsync` that consults a per-command `Dictionary<VaultPath, IReadOnlyList<string>?>` of target lines); add one sentence to `Help`: "A link to a block ([[plan#^id]]) leads to the note and its block: 'not found: no block ^id in …' when the note has no such block."
- [ ] **Step 4:** Step 2's commands — PASS; `--filter-class "*HelpTests"` — PASS.
- [ ] **Step 5: Mutations:** (a) ignore `%5E` → percent test fails; (b) print the path without the subpath → first test fails; (c) skip the directory check → directory test fails.
- [ ] **Step 6: Commit** — "Resolve block references in links".

### Task 5: Block ids in the web app: hidden markers and block embeds

**Files:**
- Create: `web/src/reader/blockIds.ts`, `web/src/note/blocks.ts`
- Modify: `web/src/note/MarkdownView.tsx` (plugin list; `EmbeddedNote` takes a block), `web/src/note/wikiLinks.ts:153-162` (`embedHeading` unchanged; add `subpathBlock`)
- Test: `web/src/reader/blockIds.test.ts`, `web/src/note/blocks.test.ts`, `web/src/note/MarkdownView.blocks.test.tsx`

**Interfaces:**
- Produces `web/src/reader/blockIds.ts`: `export const blockIdAtEnd: RegExp` (`/[ \t]\^([A-Za-z0-9-]+)[ \t]*$/`), `export const blockIdLine: RegExp` (`/^[ \t]*\^([A-Za-z0-9-]+)[ \t]*$/`), `export function remarkHideBlockIds(): (tree) => void` — removes a trailing ` ^id` from the last text node of a paragraph, heading, list item's paragraph or table cell row end, and removes a paragraph that is only `^id`; leaves code and inline code alone.
- Produces `web/src/note/blocks.ts`: `export function findBlock(source: string, id: string): { start: number; end: number } | null` (1-based, inclusive, lines of `source`; same rules as Core's `Blocks`, see Task 3) and `export function blockSource(source: string, block: { start: number; end: number }): string` (lines outside blanked, as `section()` in `outline.ts` does, so line numbers hold).
- Produces in `wikiLinks.ts`: `export function subpathBlock(raw: string): string | null` — the block id of a wikilink's or Markdown link's raw (`[[Plan#^controller|x]]`, `[c](Plan.md#%5Econtroller)` → `controller`), else null.

- [ ] **Step 1: Failing tests.** `blocks.test.ts`: `every shared case` over `./blocks.cases.json` (Task 3's file; `[first,last]` ↔ `{start,end}`, `null` ↔ `null`). `blockIds.test.ts`: rendering with `react-markdown` + `remarkGfm` + `remarkHideBlockIds`: `"The pump needs a new controller. ^controller"` → text `The pump needs a new controller.`; `"- a ^x\n- b"` → `a` without marker; `"- a\n\n^list"` → no `^list` text; `` "`x ^y`" `` keeps `x ^y`; `"```\nx ^y\n```"` keeps it; `"## Decision ^d"` → heading `Decision`. `wikiLinks.test.ts` additions for `subpathBlock`. `MarkdownView.blocks.test.tsx`: `hides block markers in the note view`; `an embed of a block shows that block` (links answer `[{ line: 1, raw: "![[Plan#^controller]]", kind: "note", path: "/Work/Pump/Plan.md", target: "Plan", candidates: [] }]`, `path="/Work/Pump/Tasks.md"`, `useNote` answered with Plan's text via the test utilities the other MarkdownView tests use) → the embed frame `Embedded: Plan › ^controller` holds `The pump needs a new controller.` and not `# Plan`; `an embed of a missing block says so` → `This note has no block ^contoller.`
- [ ] **Step 2:** `pnpm -C web test --run src/reader/blockIds.test.ts src/note/blocks.test.ts src/note/wikiLinks.test.ts src/note/MarkdownView.blocks.test.tsx` — FAIL.
- [ ] **Step 3:** Implement; put `remarkHideBlockIds` in MarkdownView's plugin list after `remarkWikiLinks` and before `remarkFind`; `WikiLink` passes `block={subpathBlock(link.raw)}` to `EmbeddedNote`, which shows `blockSource(body, found)` or the missing-block line; its title `Plan › ^controller`.
- [ ] **Step 4:** Step 2's command — PASS; `pnpm -C web test --run src/note src/reader` — PASS; `pnpm -C web build` — PASS.
- [ ] **Step 5: Mutations:** (a) drop the own-line paragraph removal → `^list` test fails; (b) `findBlock` ignores code → `inside code` case fails; (c) `subpathBlock` without `%5E` → its test fails.
- [ ] **Step 6: Commit** — "Hide block ids and embed blocks in the note view".

### Task 6: Jumps to a block or a line in the note view

**Files:**
- Modify: `web/src/api/paths.ts:106` (add `NoteAt`), `web/src/note/MarkdownView.tsx` (`data-line` on blocks; block links carry state), `web/src/note/NoteView.tsx:216-237` (read the state; scroll, highlight, notice), `web/src/index.css` (`.k-flash`)
- Test: `web/src/note/NoteView.jump.test.tsx`, `web/src/note/MarkdownView.blocks.test.tsx`

**Interfaces:**
- Consumes: `findBlock` (Task 5), `subpathBlock` (Task 5).
- Produces: `export type NoteAt = { block: string } | { line: number }` in `paths.ts` — router location state for `noteUrl(path)`: `navigate(noteUrl(p), { state: { line: 7 } satisfies NoteAt })`, `<Link to={noteUrl(p)} state={{ block: "controller" } satisfies NoteAt}>`.
- `data-line` (1-based source start line, from the hast node's position) on `p`, `li`, `blockquote`, `table`'s wrapper, `pre`, the callout `aside`/`details`, the embeds' `div`, headings — only when not `nested` (the existing `useLine` hook).

Behaviour: NoteView, once `note.data` is shown in Read mode, takes `location.state` as `NoteAt` once per `location.key`: for `block`, `findBlock(shown.body, id)` → line; unknown → notice `There is no block ^{id} in this note.` (`role="status"`, the existing notice box), view at the top. For `line` (or the found line): the element with the greatest `data-line` ≤ line inside the article (not inside `.k-embed`), `scrollIntoView({ block: "center" })`, class `k-flash` for 1.6 s. `.k-flash`: a background fade (`@keyframes`, accent at 25 % to transparent); under `@media (prefers-reduced-motion: reduce)` a static `outline` for the same time, no animation. The note's `jump` (outline) keeps working (it now finds headings among more `data-line`s: keep its selector to headings, `h1…h6[data-line]`). The search `scrollTarget` effect keeps selecting headings only (`h1…h6[data-line]`), so its behaviour is unchanged.

`WikiLink` (MarkdownView): `case "note"` with `subpathBlock(link.raw)` → `state={{ block }}`; `self` with a block → a `Link` to `noteUrl(path)` with that state (only from the note shown, not a checkpoint).

- [ ] **Step 1: Failing tests** (`NoteView.jump.test.tsx`, following how existing NoteView tests render with a query client and router; `Element.prototype.scrollIntoView = vi.fn()`):
  - `a link to a block scrolls to it and highlights it`: open `/Work/Pump/Plan.md` with state `{ block: "controller" }` → `scrollIntoView` called on the `p` holding `The pump needs a new controller.`, which has class `k-flash`; after 1.6 s (fake timers) it hasn't.
  - `an unknown block opens at the top with a notice`: state `{ block: "contoller" }` → status `There is no block ^contoller in this note.`; `scrollIntoView` not called.
  - `a line opens there`: state `{ line: 10 }` on a note whose line 10 is a paragraph → that `p` scrolled to and flashed.
  - `nothing inside an embed carries data-line` (MarkdownView with `nested`).
  - `MarkdownView.blocks.test.tsx`: `a link to a block carries the block in its state` (render inside a `MemoryRouter` with a route that prints `useLocation().state`; click `[[Plan#^controller]]` → state `{ block: "controller" }`).
- [ ] **Step 2:** `pnpm -C web test --run src/note/NoteView.jump.test.tsx src/note/MarkdownView.blocks.test.tsx` — FAIL.
- [ ] **Step 3:** Implement as above.
- [ ] **Step 4:** Step 2's command — PASS; `pnpm -C web test --run src/note src/search` — PASS; `pnpm -C web build` — PASS.
- [ ] **Step 5: Mutations:** (a) don't remove `k-flash` → timer assertion fails; (b) use the first `data-line` ≥ line → line test fails; (c) put `data-line` on nested blocks → nested test fails.
- [ ] **Step 6: Commit** — "Open a note at a block or a line, with a brief highlight".

### Task 7: Embedded notes appended to `cat`

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/Cat.cs` (add `AppendEmbedsAsync`; `Help`), `src/Kumbu.Core/Sh/Shell.cs:134` (call it), `src/Kumbu.Core/Sh/Manual.cs:11` (one line)
- Test: `tests/Kumbu.Core.Tests/Sh/CatEmbedsTests.cs`

**Interfaces:**
- Consumes: `Blocks.TryId`, `SectionLookup.TryFindBlock` (Task 3), `NoteLinks.OfAsync`, `WriterMarks.NoticeAsync`, `OutputLimit.MaxChars`.
- Produces: `public async Task<ShellResult> AppendEmbedsAsync(string arg, ShellResult read, ShellResult shown, CancellationToken ct)` on `Cat` — `read` is the result before `OutlineIfTooLong`, `shown` after it. In `Shell.RunAsync` line 134: `if (stages.Count == 1 && command is Cat cat && args.Count == 1) { var read = result; result = Cat.OutlineIfTooLong(args[0], result); result = await cat.AppendEmbedsAsync(args[0], read, result, ct); }`. Not called in `RunInCheckpointAsync`, nor by `SkillsCommand`. Errors, `--help`, attachments and text files of skills come back unchanged.

Appendix (spec §3, brief §5): embeds = `WikiLink`s with `Embed` that are the only content of their line (one or more embeds, whitespace between), within the printed range (whole note, section `Heading.Line..ContentEnd`, or block `Start..End`), outside code (WikiLink.Parse skips it), resolved by `NoteLinks.OfAsync`; skip those resolving to the note itself (and `Self`); each `(target, subpath)` once in order of first appearance. Output = `shown.Output` + (`"\n"` if it ends with a line break, else `"\n\n"`) + `--- embedded notes: shown for reading, not part of this note's text (which ends above) ---` + per embed `\n[line {Line+1}: {Raw} → {where}]` + `\n{text}` where:
- note, no subpath: where = path, text = the note's content without its final line break;
- `#Heading`: where = `path#Heading` (as written), text = the section (`SectionLookup.TryFind`), missing → where = `not found: no section #Heading in {path}`, no text;
- `#^id`: where = `path#^id`, text = the block; missing → `not found: no block ^id in {path}`;
- attachment: where = path, text = the one-line description `cat` gives (`AttachmentText.Line`/`PdfLine` as `DescribeAsync`);
- not found: `not found`; ambiguous: `ambiguous: a, b` (as `links`).
Too long: when `shown` is the outline (the note alone too long), each embed is the `[line …]` line only. Otherwise, add embeds while the whole output stays within `OutputLimit.MaxChars - 200`; the first that doesn't fit and everything after it become `\n[embedded notes cut here; read the rest with cat "{that embed's target with its subpath}"]`. Notice: `marks.NoticeAsync([note, ..embedded note paths])` replaces the note's own. `Version` unchanged.

- [ ] **Step 1: Failing tests** (`CatEmbedsTests`, through `TestShell`/`Shell.RunAsync` with an in-memory store holding the spec's example: `/Work/Pump/Plan.md` as in spec §1, `/Work/Pump/Ruling.md` "# Ruling\n\nIntro\n\n## Decision\n\nWe buy the controller.\n\n## Later\n\nx\n"):
  - `Cat_appends_the_embedded_section`: `cat /Work/Pump/Plan.md` → exactly Plan's content + `"\n--- embedded notes: shown for reading, not part of this note's text (which ends above) ---\n[line 10: ![[Ruling#Decision]] → /Work/Pump/Ruling.md#Decision]\n## Decision\n\nWe buy the controller."` (the embed is on line 10 of the spec's example; the spec's "line 9" is an illustration), version token of Plan.
  - `The_note_text_is_byte_for_byte`: `result.Output.StartsWith(content, Ordinal)`.
  - `A_missing_embed_says_not_found` (`![[Missing]]` → `[line N: ![[Missing]] → not found]`), `An_ambiguous_embed_names_the_candidates`, `A_block_embed_shows_the_block`, `A_missing_section_says_so`, `An_attachment_embed_gets_its_description`.
  - `Each_target_once_and_never_the_note_itself` (`![[Plan]]` inside Plan, `![[Ruling]]` twice).
  - `One_level_deep`: Ruling's own `![[Other]]` stays as written in the appendix text, Other isn't appended.
  - `An_embed_within_a_sentence_is_not_appended` (`see ![[Ruling]] here`), `An_embed_in_code_is_not_appended`.
  - `A_section_appends_only_its_embeds` (`cat "/Work/Pump/Plan.md#Plan"` vs an embed in another section), with line numbers counted in the whole note.
  - `No_appendix_in_a_pipeline` (`cat /Work/Pump/Plan.md | wc -l` equals the note's count), `No_appendix_with_version`, `No_appendix_with_checkpoint`, `No_appendix_for_a_note_without_embeds` (output equals today's exactly).
  - `A_long_appendix_is_cut_at_an_embed` (embedded note of 20,000 chars after one small one → the small one whole, then `[embedded notes cut here; read the rest with cat "/Work/Pump/Big.md"]`).
  - `A_note_too_long_alone_lists_only_the_embed_lines` (outline footer present, then the header and `[line …]` lines, no embedded text).
  - `An_embedded_note_written_by_another_user_is_marked` (shared vault fixture `SharedVaults.SharedGarten`, as `CatTests.A_note_last_written_by_another_user_is_marked`).
  - `Skills_cat_gets_no_appendix` (via `skills cat` if a fixture exists; else assert `SkillsCommand` calls `Cat.RunAsync` only, by a skill note with an embed).
- [ ] **Step 2:** `--filter-class "*CatEmbedsTests"` — FAIL.
- [ ] **Step 3:** Implement; `Help` gains, after the outline paragraph: "Standing alone, a note that embeds notes (![[plan]] on a line of its own) is followed by a block 'embedded notes', with each embedded note, section or block as it is now, one level deep: shown for reading, not part of the note's text, which ends above it. Write back only the note's own text. No block in a pipeline or with --version." `Manual.cs:11`'s `cat` line keeps its width; add in the "Long notes" area: "Embeds: cat appends the notes a note embeds, below a marked line; they are not part of its text."
- [ ] **Step 4:** Step 2's command — PASS; `--filter-class "*CatTests"`, `"*ShellTests"`, `"*HelpTests"`, `"*SkillsCommandTests"`, `"*PipelineTests"` — PASS.
- [ ] **Step 5: Mutations:** (a) append in a pipeline → pipeline test fails; (b) drop the self filter → once-test fails; (c) cut mid-embed (no boundary) → cut test fails; (d) use `marks.NoticeAsync([note])` only → shared-vault test fails; (e) count lines from the section start → section test fails.
- [ ] **Step 6: Commit** — "Append the notes a note embeds to cat".

### Task 8: Unlinked mentions in Core and `backlinks --unlinked`

**Files:**
- Create: `src/Kumbu.Core/Links/Mentions.cs`
- Modify: `src/Kumbu.Core/Links/NoteLinks.cs` (add `MentionsAsync`), `src/Kumbu.Core/Sh/Commands/Backlinks.cs` (option `unlinked`; usage, help), `src/Kumbu.Core/Sh/Manual.cs:20` (usage line `backlinks [--archive] [--unlinked] <path>`)
- Test: `tests/Kumbu.Core.Tests/Links/MentionsTests.cs`, `tests/Kumbu.Core.Tests/Sh/LinkCommandsTests.cs`

**Interfaces:**
- Produces `Kumbu.Core.Links`:
  - `public sealed record Mention(VaultPath Source, int Line, string Text, IReadOnlyList<(int Start, int Length)> Marks)` — `Line` 0-based, `Marks` UTF-16 offsets in `Text`.
  - `public static class Mentions { public static IReadOnlyList<(int Line, IReadOnlyList<(int Start, int Length)> Marks)> In(string content, string title); }` — per source line with at least one mention.
  - `NoteLinks.MentionsAsync(INoteReader notes, VaultPath path, bool archive, CancellationToken ct): Task<IReadOnlyList<Mention>>` — reads `ReadNotesUnderAsync(vault)` as `ToAsync`; sources ordered by `TextOrder.Default`, lines ascending; the note itself excluded; `_archive/` sources excluded unless `ArchiveFolder.Shown(path, archive)`; empty for an attachment or a title shorter than 3 characters (title: file name without `.md`, NFC).

What counts (spec §5.1): ignoring case (`OrdinalIgnoreCase` on NFC text), whole word (no `char.IsLetterOrDigit` right before or after), outside front matter, fenced code, indented code, inline code spans (`NoteLink.CodeSpans`), wikilinks (whole `[[…]]`), Markdown links and images of any destination (`[text](…)`, the whole construct, external ones too), autolinks `<…>` and bare URLs (`\bhttps?://\S+`). One entry per line.

- [ ] **Step 1: Failing tests.** `MentionsTests`: `A_plain_mention_counts` (`"Talked about the plan with Bob."`, `Plan` → line 0, mark `(13, 4)`); `Whole_words_only` (`planning`, `airplane`, `plan2` no; `plan.` and `(plan)` yes); `Mentions_in_code_links_urls_and_front_matter_dont_count` (inline code, fenced, indented, `[[Plan]]`, `[the plan](x.md)`, `[x](https://a.example/plan)`, `https://a.example/plan`, `---\ntitle: plan\n---`); `A_line_with_a_link_and_a_plain_mention_counts` (`"[[Plan]] and the plan"` → one entry, one mark); `Two_mentions_on_a_line_are_one_entry_with_two_marks`; `Titles_under_three_characters_have_none` (`AB`); `Titles_with_spaces` (`Meeting Notes`). `LinkCommandsTests`: `Backlinks_unlinked_lists_plain_mentions` (spec vault; `backlinks --unlinked /Work/Pump/Plan.md` → `/Work/Diary/2026-11-12.md:1:Talked about the plan with Bob.`; Tasks.md's `[[Plan#^controller]]` line not listed); `Backlinks_unlinked_none` → `(no unlinked mentions)`; `Backlinks_unlinked_leaves_the_archive_out_unless_asked` (`--archive --unlinked`); `Backlinks_unlinked_stays_in_the_vault` (a note in `/Home` saying "plan" isn't listed); `Backlinks_unlinked_of_an_attachment` → `(no unlinked mentions)`; `Backlinks_without_unlinked_is_unchanged` (existing outputs).
- [ ] **Step 2:** `--filter-class "*MentionsTests"`, `"*LinkCommandsTests"` — FAIL.
- [ ] **Step 3:** Implement; Backlinks `Spec` gains `new(null, "unlinked")`, `Usage` `backlinks [--archive] [--unlinked] <path>`; `Help` gains `--unlinked   list the lines that name the note without linking to it (its title as a whole word, ignoring case, outside code and links), as path:line:text`. Output lines `{source}:{line+1}:{text}`.
- [ ] **Step 4:** Step 2's commands — PASS; `"*HelpTests"` — PASS.
- [ ] **Step 5: Mutations:** (a) case-sensitive → plain test fails; (b) no word boundary → whole-words fails; (c) don't mask Markdown links → code/links test fails; (d) include the note itself → a test with a self-mention fails (add `The_note_itself_doesnt_count` to Step 1 if missing).
- [ ] **Step 6: Commit** — "List unlinked mentions with backlinks --unlinked".

### Task 9: The mentions endpoint

**Files:**
- Modify: `src/Kumbu.Server/Api/NoteApi.cs:172` (records), `:237` (route), near `:759` (handler); `web/src/api/schema.d.ts` (generated)
- Test: the server test class that covers `BacklinksAsync` (find it with `grep -rln "api/backlinks" tests/Kumbu.Server.Tests`), new tests there

**Interfaces:**
- Consumes: `NoteLinks.MentionsAsync` (Task 8).
- Produces: `GET /api/mentions/{vault}/{**path}?archive=false` → `MentionView[]`; `public sealed record MentionView(string Path, int Line, string Text, MentionMarkView[] Marks)` (`Line` 1-based), `public sealed record MentionMarkView(int Start, int Length)`; `.Errors(400, 401, 404, 503)` as the backlinks route; 404 for a missing note exactly as `BacklinksAsync`; doc comment in the neighbours' style. Decision: an endpoint beside backlinks, not a parameter (a parameter would change the backlinks answer's type in the OpenAPI document).

- [ ] **Step 1: Failing tests:** `Mentions_lists_plain_mentions_with_their_marks` (spec vault → `[{ path: "/Work/Diary/2026-11-12.md", line: 1, text: "Talked about the plan with Bob.", marks: [{ start: 13, length: 4 }] }]`); `Mentions_of_a_missing_note_is_404`; `Mentions_of_another_users_vault_is_404` (as the neighbouring backlinks permission test does it); `A_member_reads_mentions_of_a_shared_vault` (if the class has the shared-vault fixture; else in `Sharing` tests beside the member's backlinks test).
- [ ] **Step 2:** start MongoDB; `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*<ThatClass>"` — FAIL.
- [ ] **Step 3:** Implement. Regenerate `schema.d.ts`: run the server locally against the test MongoDB (`dotnet run --project src/Kumbu.Server` with the env the README names for a dev run, on port 8083), then `KUMBU_OPENAPI_URL=http://localhost:8083/api/openapi.json pnpm -C web gen:api`; `git diff web/src/api/schema.d.ts` shows only the new path and schemas. If the server can't start, record why and type the call in `web/src/api/notes.ts` as its neighbours do (Task 10).
- [ ] **Step 4:** Step 2's command — PASS; the OpenAPI snapshot test if one exists (`grep -rln openapi tests/Kumbu.Server.Tests`) — PASS or updated as its file says; `pnpm -C web build` — PASS.
- [ ] **Step 5: Mutations:** (a) skip the existence check → 404 test fails; (b) `Line` 0-based → first test fails.
- [ ] **Step 6: Commit** — "Answer unlinked mentions over the API".

### Task 10: Unlinked mentions in the web app

**Files:**
- Create: `web/src/note/UnlinkedMentions.tsx`
- Modify: `web/src/api/notes.ts:248` (`useMentions`), `web/src/note/BacklinksList.tsx` (always render; the mentions section below)
- Test: `web/src/note/UnlinkedMentions.test.tsx`, `web/src/note/BacklinksList.test.tsx` (if it exists; else in the new file)

**Interfaces:**
- Consumes: `GET /api/mentions/{vault}/{path}` (Task 9), `NoteAt` (Task 6).
- Produces: `export const useMentions = (path: string, enabled: boolean)` — `useQuery({ queryKey: ["mentions", path], queryFn: …, enabled })`; `export default function UnlinkedMentions({ path }: { path: string })`.

Behaviour (spec §5.3): `BacklinksList` returns its "Linked from" section only when it has sources (as today) and always renders `<UnlinkedMentions path>` after it (with the top border and spacing of the section when there is no "Linked from"). A disclosure button `Unlinked mentions` (no count, `aria-expanded`, `aria-controls`, `min-h-12`, same heading look as "Linked from"); closed by default, asks only when first opened. Open: loading `Looking for mentions …`; error `Couldn't look for mentions.` with a button `Try again` (`refetch`); none `No unlinked mentions.`; entries as buttons like the backlinks rows: title, the line with each mark as `<mark>` (from `marks`, text as React text nodes, never HTML), the folder (`crumbOf`); a tap: `navigate(noteUrl(m.path), { state: { line: m.line } satisfies NoteAt })`. Closed again on a change of `path`.

- [ ] **Step 1: Failing tests:** `is closed and asks nothing until opened` (fetch mock not called; button `aria-expanded="false"`); `lists the mentions with the title marked` (answer from Task 9's example → text `2026-11-12`, a `mark` with `plan`, folder crumb); `opens the note at the line` (click → navigated to `noteUrl` with state `{ line: 1 }`); `says when there are none`; `a failed request offers a retry` (first call 500, `Try again` → second call); `shows even when nothing links here` (BacklinksList with empty backlinks renders the mentions button); `a mention's text is never markup` (text `<b>plan</b>` shows literally).
- [ ] **Step 2:** `pnpm -C web test --run src/note/UnlinkedMentions.test.tsx` — FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Step 2's command — PASS; `pnpm -C web test --run src/note` — PASS; `pnpm -C web build` — PASS.
- [ ] **Step 5: Mutations:** (a) `enabled: true` always → closed test fails; (b) drop the state → line test fails; (c) hide when no backlinks → shows-even test fails.
- [ ] **Step 6: Commit** — "Show unlinked mentions below Linked from".

### Task 11: Mermaid and block ids on the reader page, under its CSP

**Files:**
- Modify: `web/src/reader/Reader.tsx` (`pre` component; `remarkHideBlockIds`; dark from `prefers-color-scheme`)
- Test: `web/src/reader/Reader.test.tsx`; a probe outside the repository: `/tmp/claude-0/-home-user-kumbukumbu/683227b5-6b43-57a0-b0cf-a8478c46b103/scratchpad/csp-probe/`

**Interfaces:**
- Consumes: `MermaidDiagram`, `remarkHideBlockIds` (Tasks 1, 5).
- Produces: in `Reader.tsx`, `function usePrefersDark(): boolean` (`matchMedia("(prefers-color-scheme: dark)")` and its `change` event).

- [ ] **Step 1: Failing tests** in `Reader.test.tsx` (mock `./mermaidRender` as in Task 2): `draws a mermaid block as an image`; `hides block ids`; `Copy as Markdown keeps the mermaid source and the block ids` (clipboard text contains ```` ```mermaid ```` and `^controller`); `follows the system's dark preference` (`matchMedia` mocked dark → `renderDiagram(source, true)`).
- [ ] **Step 2:** `pnpm -C web test --run src/reader` — FAIL.
- [ ] **Step 3:** Implement; the `pre` component as Task 2's, `dark` from `usePrefersDark`.
- [ ] **Step 4:** Step 2's command — PASS; `pnpm -C web build` — PASS.
- [ ] **Step 5: CSP probe.** Serve `web/dist` with a small Node `http` server in the scratch folder that sends `s.html` with `Content-Security-Policy` copied verbatim from `Security.ReaderPagePolicy`, serves `/assets/*`, and answers `POST /api/s` with a `ReaderCopy` JSON (see `readerApi.ts`) whose `markdown` is the spec's flowchart; Playwright (Chromium) opens `/s#CODE`, collects `console` messages and `securitypolicyviolation` events, waits for the `img`, screenshots light and dark (`colorScheme`) at 375 px and 1280 px, and reads the image's `naturalWidth`. Pass only if the image shows three boxes with the labels Sensor, Controller, Pump in the theme's colours (look at the screenshots) **and** no CSP violation was reported. Record the result in the task's report. If it fails: the reader page shows the code block instead (a `canDraw` prop or the reader not using `MermaidDiagram`), and a row goes into `docs/superpowers/open-items.md`; the CSP is never changed. Repeat the probe for the app's policy on `index.html` only if cheap; the note view must work regardless.
- [ ] **Step 6: Mutations:** (a) `dark` always false → dark test fails; (b) drop `remarkHideBlockIds` → hide test fails.
- [ ] **Step 7: Commit** — "Draw Mermaid diagrams on the reader page".

### Task 12: Documentation

**Files:**
- Modify: `README.md` (only if it lists what the web app shows: add Mermaid diagrams, block references, unlinked mentions in its style), `docs/concept.md` (only if it lists features). Not `CHANGELOG.md` (no "Unreleased" heading), not the Welcome notes.
- [ ] **Step 1:** `grep -n "KaTeX\|maths\|callout\|backlink" README.md docs/concept.md` to find such lists; edit only there.
- [ ] **Step 2:** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"` and `"*ManualTests"` if present — PASS.
- [ ] **Step 3: Commit** — "Document Mermaid, block references and unlinked mentions".

### Task 13: Security review, final gate, pull request, report

- [ ] **Step 1:** A fresh subagent gets spec §1, §2.2, §3, §5.1 and `git diff master...HEAD`, not the controller's reasoning, and answers the brief's four questions. Fix what it confirms (each fix red/green as above); record the rest.
- [ ] **Step 2:** Full gate (Task 0's script), counts against Task 0.
- [ ] **Step 3:** Push; open the pull request from `feature/mermaid-parity` to `master` as a draft, titled "Draw Mermaid diagrams, embed notes in cat, resolve block references and list unlinked mentions", description with **Decisions made without Chris**, ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- [ ] **Step 4:** Write `docs/superpowers/overnighter/2026-11-12-mermaid-and-parity-report.md` (the brief's sections and the hand-test checklist) and the `open-items.md` rows (the PR's link, "Copy link to block", the "Link" button on unlinked mentions); commit, push, mark the pull request ready.
- [ ] **Step 5:** Do not merge, tag, deploy or change the version.

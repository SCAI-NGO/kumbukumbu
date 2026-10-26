# Markdown Links Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every Markdown link to a note, `[text](target)` in any form Obsidian reads, counts as a link wherever wikilinks count (`links`, `backlinks`, `rm`'s and `write`'s hints, the web app, the checkpoint view, `mv`); `mv` rewrites the ones it has to touch to the absolute form `/Folder/Note%20name.md`; agents are told to write that form.

**Architecture:** A new `MarkdownLink` record beside `WikiLink`, both deriving from an abstract `NoteLink` (line, start, raw, decoded target, subpath); `NoteLink.Parse` returns both kinds in source order and replaces `WikiLink.Parse` at every place that resolves links. `LinkResolver` needs no new kind, only Obsidian's order for paths with a folder (relative to the source's folder, then exact in the vault, then a path suffix). `NoteMover.Rewrites` decides *whether* to touch a link exactly as today (the same test for both kinds, so the sync's `rewriteLinks: false` skips both); only *what to write* differs per kind. The web app maps remark's `link` nodes to the API's `LinkView`s by line, raw source text and occurrence, as it maps wikilinks.

**Tech Stack:** .NET 10, xUnit (Core tests with `InMemoryNoteStore`, server tests with Testcontainers MongoDB); React 18, remark/react-markdown, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-26-markdown-links-design.md` (binding).

## Where and how this runs

- Branch `markdown-links` off `master` 6c13cbc; one pull request, never merged by the builder.
- Commands: `dotnet test tests/Kumbu.Core.Tests --filter "FullyQualifiedName~<Class>"`, `dotnet test`, `pnpm -C web test`, `pnpm -C web build`. The server's lint is the build (`TreatWarningsAsErrors`).
- No REST shape changes: `LinkView` stays as it is (`raw` is the link as written, `target` the decoded path), so `web/src/api/schema.d.ts` is not regenerated.
- Every commit message imperative, free-form, ending with:
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01QQATz5y8fF72SRR68e5igp
  ```

## Global Constraints

- Not a note link: a target with a scheme (`^[A-Za-z][A-Za-z0-9+.-]*:` — `https:`, `mailto:`, `obsidian:` …) or starting with `//`; images (`![…](…)`); reference-style links (`[t][ref]`, not parsed); anything in fenced code blocks or inline code. Frontmatter counts, as for wikilinks.
- A target that resolves to no note and looks like a file (`report.pdf`) is `LinkKind.Attachment`, exactly as for wikilinks; one that looks like a note is `NotFound`.
- Decoding: CommonMark backslash escapes, then percent-decoding (`%20` → space); `<…>` angle brackets taken off. `Meeting%20Notes.md` and `<Meeting Notes.md>` resolve to the same note. The fragment starts at the first `#` of the raw destination (so `%23` stays part of the name).
- `mv` writes a touched Markdown link as `/` + the note's path inside its vault, each segment percent-encoded per the encoding table below, `.md` kept. Link text, `#heading`/`#^block` (raw), angle brackets and title (`"title"`) stay byte for byte; only the path changes.
- Encoding table (what `mv` writes): space → `%20`, and `%` `#` `?` `(` `)` `<` `>` `[` `]` `^` `|` `\` `` ` `` `"` `{` `}` and control characters percent-encoded (UTF-8); letters including non-ASCII (`ü`), digits and other punctuation stay as they are.
- The sync's moves (`keepLinks: true` → `rewriteLinks: false`) touch no Markdown link, as they touch no wikilink.
- Wikilinks keep today's `mv` behaviour (form kept, `NewTarget`); no link changes form except a Markdown link `mv` has to touch.
- Don't touch the write path beyond what links need, nor encryption, nor the sync protocol. No version bump.
- British English in code, comments and copy; SPDX header `// SPDX-License-Identifier: AGPL-3.0-or-later` on every new file; the repo's comment density (why, not what).

## Rulings (the builder's, within the spec)

1. **Resolution order for a path with a folder** (`Work/plan.md`, `sub/plan`), for both kinds: a note at that path relative to the source's folder (how Obsidian's "Relative path to file" writes links below the note without `./`), else the exact vault path, else a path suffix with today's ambiguity handling. Bare names (`plan`) resolve exactly as today. Applying it to wikilinks too keeps one resolver and matches Obsidian, which resolves both forms with the same function; it only turns some ambiguous results into the note Obsidian picks.
2. **Without `.md`**: `[Plan](plan)` resolves like `[[plan]]` (Obsidian appends `.md` when resolving).
3. **Non-ASCII** stays unencoded in what `mv` writes: Obsidian and every CommonMark reader read it raw, and it stays readable.
4. **The web app**: a Markdown link to a note renders like a wikilink (a link to the note, the ambiguous dialog, "create" for a missing one, "not in this checkpoint"); a non-external Markdown link the API's answer doesn't have stays plain text, as wikilinks do; external links stay `<a>` as today. Creating a missing note from a link resolves `/…` from the vault's root and `./`, `../` from the note's folder.
5. **The replace hint** (`replace`'s preview of occurrences inside links) reads Markdown links too; its wording stays.

## Review Focus

1. **A link with parentheses or a title** (`[t](Plan%20(v2).md)`, `[t](<a b.md> "title")`, `[t](plan.md 'x')`): the parser must take the whole destination and keep the title; `mv` must change only the path. Tests in Task 1 (`ParsesDestinationsWithParenthesesAnglesAndTitles`) and Task 3 (`MvKeepsTextHeadingAngleBracketsAndTitle`).
2. **Several links on one line, mixed kinds** (`[[a]] [b](b.md) [[c]]`): order by position, and `Apply` must replace each at its own place. Tests in Task 1 (`ParseReturnsBothKindsInSourceOrder`) and Task 3 (`MvRewritesMixedLinksOnOneLine`).
3. **Escapes and brackets in the link text** (`[a \] b](x.md)`, `[see [this]](x.md)`, `\[not](x.md)`): nested brackets are text; an escaped opening bracket is no link. Test in Task 1 (`LinkTextWithBracketsAndEscapes`).
4. **A Markdown link inside a wikilink's alias or the other way round** (`[[plan|see (x)]]`, `[[x]](y.md)`): never two links from one span. Test in Task 1 (`NoMarkdownLinkInsideAWikilink`).
5. **A note name needing encoding after a move** (`Plan #2 (draft).md`, `Bücher/Ü.md`): `mv` writes `/Work/Plan%20%232%20%28draft%29.md`, `/Work/Bücher/Ü.md`, and the result resolves back to the note. Test in Task 3 (`MvEncodesWhatNeedsIt`).

---

### Task 1: Parse Markdown links and resolve them as Obsidian does

**Files:**
- Create: `src/Kumbu.Core/Links/NoteLink.cs`, `src/Kumbu.Core/Links/MarkdownLink.cs`
- Modify: `src/Kumbu.Core/Links/WikiLink.cs` (derive from `NoteLink`; move `CodeSpans` to `NoteLink` as `internal static`)
- Modify: `src/Kumbu.Core/Links/LinkResolver.cs` (Ruling 1)
- Test: `tests/Kumbu.Core.Tests/Links/MarkdownLinkTests.cs` (new), `tests/Kumbu.Core.Tests/Links/LinkResolverTests.cs`

**Interfaces:**
- Produces:
  - `public abstract record NoteLink(int Line, int Start, string Raw, string Target, string Subpath)` with `public static IReadOnlyList<NoteLink> Parse(string content)`: wikilinks and Markdown links, ordered by `(Line, Start)`. `Target` is what `LinkResolver.Resolve` takes (for Markdown: decoded path, "" for `#Heading`).
  - `public sealed partial record WikiLink(int Line, int Start, string Raw, bool Embed, string Target, string Subpath, string Alias) : NoteLink(Line, Start, Raw, Target, Subpath)` — otherwise unchanged; `WikiLink.Parse` stays (the welcome-vault test uses it).
  - `public sealed partial record MarkdownLink(int Line, int Start, string Raw, string Target, string Subpath, ...) : NoteLink(...)` keeping the exact source text of each part (text `[…]`, destination as written with or without `<>`, raw subpath, the rest up to and including `)`); `public static IReadOnlyList<MarkdownLink> Parse(string content)`; `public string WithPath(VaultPath note)` (the same link with the path replaced by `Encode(note)`); `public static string Encode(VaultPath note)` (`/Folder/Note%20name.md`).

- [ ] **Step 1: Write the failing tests** in `MarkdownLinkTests`:
  - `ParsesEveryFormOfTarget` (Theory, line → expected `Target`, `Subpath`): `[a](/Work/plan.md)` → `/Work/plan.md`,""; `[a](Work/plan.md)` → `Work/plan.md`; `[a](./sub/plan.md)`; `[a](../plan.md)`; `[a](plan.md)`; `[a](plan)` → `plan`; `[a](Meeting%20Notes.md)` → `Meeting Notes.md`; `[a](<Meeting Notes.md>)` → `Meeting Notes.md`; `[a](plan.md#Goals)` → `plan.md`,`#Goals`; `[a](plan.md#Goals%20and%20ways)` → `plan.md`,`#Goals%20and%20ways`; `[a](plan.md#^block)` → `plan.md`,`#^block`; `[a](#Heading)` → "",`#Heading`; `[a](Plan%20%232.md)` → `Plan #2.md`.
  - `ParsesDestinationsWithParenthesesAnglesAndTitles`: `[t](Plan%20(v2).md)` → target `Plan (v2).md`, Raw the whole link; `[t](<a b.md> "title")` → `a b.md`; `[t](plan.md 'x')` → `plan.md`; `[t](plan.md (x))` → `plan.md`.
  - `NotNoteLinks` (Theory, `Assert.Empty(NoteLink.Parse(text))`): `[a](https://x.org)`, `[a](mailto:a@b.c)`, `[a](obsidian://open?file=x)`, `[a](//cdn.x/y.md)`, `![a](plan.md)`, `[a][ref]`, `` `[a](plan.md)` ``, `"```\n[a](plan.md)\n```\n"`, `[a]()`, `\[a](plan.md)`.
  - `LinkTextWithBracketsAndEscapes`: `[see [this]](x.md)` and `[a \] b](x.md)` each parse to one link with target `x.md`.
  - `NoMarkdownLinkInsideAWikilink`: `[[plan|see (x)]]` gives one `WikiLink`; `[[x]](y.md)` gives one `WikiLink`, no `MarkdownLink`.
  - `ParseReturnsBothKindsInSourceOrder`: `"[[a]] [b](b.md) [[c]]\n[d](d.md)"` → types and targets in order `a`,`b.md`,`c`,`d.md`; lines 0,0,0,1.
  - `FrontmatterCounts`: `"---\nsee: \"[x](x.md)\"\n---\n"` → one link.
  - `WithPathChangesOnlyThePath`: parse `[My *text*](<old name.md#Goals> "t")`, `WithPath(P("/W/Work/New name.md"))` → `[My *text*](</Work/New%20name.md#Goals> "t")`; `[a](../x.md#^b)` → `[a](/Work/New%20name.md#^b)`.
  - `EncodeFollowsTheTable` (Theory): `/W/Work/Plan #2 (draft).md` → `/Work/Plan%20%232%20%28draft%29.md`; `/W/Bücher/Ü.md` → `/Bücher/Ü.md`; `/W/a%b^c.md` → `/a%25b%5Ec.md`.
- In `LinkResolverTests` add (Ruling 1), with notes `/W/Work/plan.md`, `/W/x/Work/plan.md`, `/W/x/sub/plan.md`, `/W/sub/plan.md` in a separate resolver: from `/W/inbox.md`, `Work/plan` → `/W/Work/plan.md` (exact wins over the suffix match in `/W/x`); from `/W/x/note.md`, `sub/plan.md` → `/W/x/sub/plan.md` (relative to the folder wins); from `/W/x/note.md`, `Work/plan` → `/W/x/Work/plan.md`; the existing theory rows still pass.

- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Core.Tests --filter "FullyQualifiedName~Links"` — expected: FAIL (types missing, new resolver rows wrong).

- [ ] **Step 3: Implement** `NoteLink`, `MarkdownLink`, the `WikiLink` change and the resolver rule. The parser is a hand-written scanner per line (no regex for the destination: parentheses must balance, `<…>` may hold spaces), skipping code lines (`Outline.CodeLines`), code spans and wikilink spans; `[` preceded by `!` or `\` starts no link. Decode with backslash-unescape then `Uri.UnescapeDataString` on the path part only.

- [ ] **Step 4: Run** the same filter, then `dotnet test tests/Kumbu.Core.Tests` — expected: PASS.

- [ ] **Step 5: Commit** `Parse Markdown links beside wikilinks and resolve folder paths as Obsidian does`.

### Task 2: Count Markdown links in links, backlinks, rm, write and the checkpoint view

**Files:**
- Modify: `src/Kumbu.Core/Links/NoteLinks.cs` (`ResolvedLink(NoteLink Link, …)`, `NoteLink.Parse`)
- Modify: `src/Kumbu.Core/Sh/Commands/LinksCommand.cs` (directory check), `src/Kumbu.Core/Notes/NoteWriter.cs` (rm count, `NewlyAmbiguousAsync`), `src/Kumbu.Core/Notes/Checkpoints.cs` (broken-link check in `PlanAsync`), `src/Kumbu.Core/Notes/Replacer.cs` (Ruling 5: `NoteLink.Parse(note.Content)` and `NoteLink.Parse(raw)`)
- Modify: `src/Kumbu.Server/Api/NoteApi.cs` (only if the type change needs it; the `LinkView` comment)
- Test: `tests/Kumbu.Core.Tests/Sh/LinkCommandsTests.cs`, `tests/Kumbu.Server.Tests/Api/CheckpointReadApiTests.cs`

**Interfaces:**
- Consumes: `NoteLink.Parse`, `NoteLink.Target/Line/Raw` (Task 1).
- Produces: `ResolvedLink(NoteLink Link, LinkResolution Resolution)`.

- [ ] **Step 1: Write the failing tests** in `LinkCommandsTests` (own store in each test so the shared fixtures stay as they are):
  - `MarkdownLinksAreLinks`: note `/Demo/md.md` = `"[k](/projects/kumbu.md) [m](Meeting%20Notes.md) [w](<welcome.md#Welcome>)\n[p](plan.md) [n](nope.md) [r](report.pdf) [h](#Top) [x](https://x.org)\n"`; `links /Demo/md.md` →
    ```
    1: [k](/projects/kumbu.md) → /Demo/projects/kumbu.md
    1: [m](Meeting%20Notes.md) → /Demo/Meeting Notes.md
    1: [w](<welcome.md#Welcome>) → /Demo/welcome.md
    2: [p](plan.md) → ambiguous: /Demo/a/plan.md, /Demo/b/plan.md
    2: [n](nope.md) → not found
    2: [r](report.pdf) → attachment (not a note)
    2: [h](#Top) → this note
    ```
  - `BacklinksFindMarkdownLinks`: with that note, `backlinks /Demo/projects/kumbu.md` includes `/Demo/md.md:1:[k](/projects/kumbu.md) …`.
  - `RmCountsMarkdownLinks`: `rm /Demo/projects/kumbu.md` with that note names `/Demo/md.md` among the notes whose links now lead nowhere.
  - `LinksOfADirectoryListsBrokenMarkdownLinks`: `links /Demo` contains `/Demo/md.md:2: [n](nope.md) → not found`.
- In `CheckpointReadApiTests`: `Markdown_links_resolve_inside_the_checkpoint_even_after_a_move`, as `Links_resolve_inside_the_checkpoint_even_after_a_move` with `a.md` = `"[b](p/b.md) [c](/c.md)\n"`: kinds `note` (path `/Arbeit/p/b.md`) and `not_found` (target `/c.md`).

- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Core.Tests --filter "FullyQualifiedName~LinkCommandsTests"` — expected: FAIL.

- [ ] **Step 3: Replace** `WikiLink.Parse` by `NoteLink.Parse` at the listed places (not in `NoteMover`, which is Task 3, nor in the welcome-vault test).

- [ ] **Step 4: Run** `dotnet test` — expected: PASS (all, including the server tests).

- [ ] **Step 5: Commit** `Count Markdown links in links, backlinks and the hints`.

### Task 3: Keep Markdown links working through mv

**Files:**
- Modify: `src/Kumbu.Core/Notes/NoteMover.cs` (`Rewrites`, `Apply`: `List<(NoteLink Link, string Replacement)>`; `NoteLink.Parse`; the replacement per kind)
- Modify: `src/Kumbu.Core/Notes/Checkpoints.cs` (`RestoreWork.Rewrites` type)
- Test: `tests/Kumbu.Core.Tests/Sh/LinkCommandsTests.cs`, `tests/Kumbu.Core.Tests/Sh/FolderMoveTests.cs`, `tests/Kumbu.Server.Tests/Api/SyncApiTests.cs`

**Interfaces:**
- Consumes: `MarkdownLink.WithPath`, `MarkdownLink.Encode` (Task 1).
- Produces: `NoteMover.Rewrites(...) : Dictionary<VaultPath, List<(NoteLink Link, string Replacement)>>`, `NoteMover.Apply(string, List<(NoteLink Link, string Replacement)>)`.

- [ ] **Step 1: Write the failing tests** (in `LinkCommandsTests` unless stated):
  - `MvRewritesMarkdownLinksToTheMovedNoteAbsolute`: `/Demo/md.md` = `"[k](projects/kumbu.md) and [K](kumbu#Goal)\n"`; `mv /Demo/projects/kumbu.md /Demo/archive/kumbu-v1.md` → `/Demo/md.md` is `"[k](/archive/kumbu-v1.md) and [K](/archive/kumbu-v1.md#Goal)\n"`; the output names `/Demo/md.md`.
  - `MvRewritesARelativeLinkInTheMovedNote`: `/Demo/projects/rel.md` = `"[k](./kumbu.md) [w](../welcome.md)\n"`; `mv /Demo/projects/rel.md /Demo/deep/er/rel.md` → `"[k](/projects/kumbu.md) [w](/welcome.md)\n"`.
  - `MvLeavesAMarkdownLinkItDoesNotHaveToTouch`: `/Demo/md.md` = `"[m](Meeting%20Notes.md) [x](https://x.org/kumbu.md) [n](nope.md)\n"`; after `mv /Demo/projects/kumbu.md /Demo/k2.md` the note is byte for byte unchanged.
  - `MvKeepsTextHeadingAngleBracketsAndTitle`: `"[My *k*](<projects/kumbu.md#Goal> \"t\")\n"` → `"[My *k*](</archive/k.md#Goal> \"t\")\n"` after `mv /Demo/projects/kumbu.md /Demo/archive/k.md`.
  - `MvRewritesMixedLinksOnOneLine`: `"[[kumbu]] [k](kumbu.md) [[kumbu#Goal]]\n"` → `"[[k3]] [k](/k3.md) [[k3#Goal]]\n"` after `mv /Demo/projects/kumbu.md /Demo/k3.md`.
  - `MvEncodesWhatNeedsIt`: `"[k](kumbu.md)\n"`; `mv /Demo/projects/kumbu.md "/Demo/Plan #2 (draft).md"` → `"[k](/Plan%20%232%20%28draft%29.md)\n"`, and `links` of that note resolves to `/Demo/Plan #2 (draft).md`.
  - `KeepLinksLeavesMarkdownLinksAlone`: `mv --keep-links` leaves `"[k](projects/kumbu.md)\n"` as it is.
  - In `FolderMoveTests`: `AFolderMoveRewritesMarkdownLinksAbsoluteAndKeepsInnerRelativeOnes`: `/W/p/a.md` = `"[b](./b.md) [o](../o.md)\n"`, `/W/p/b.md`, `/W/o.md`, `/W/x.md` = `"[a](p/a.md)\n"`; `mv /W/p/ /W/q/` → `/W/q/a.md` is `"[b](./b.md) [o](../o.md)\n"` (both still reach the same notes) and `/W/x.md` is `"[a](/q/a.md)\n"`. (Adapt vault name and helpers to that file's fixtures.)
  - In `SyncApiTests`: `A_sync_move_leaves_markdown_links_alone`, as the existing `keepLinks = true` test with a linking note `"[s](source.md)\n"` that stays unchanged.

- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Core.Tests --filter "FullyQualifiedName~LinkCommandsTests|FullyQualifiedName~FolderMoveTests"` — expected: FAIL.

- [ ] **Step 3: Implement**: `Rewrites` iterates `NoteLink.Parse`; the test whether to touch a link is today's (`was` a note, `now` not the meant note); the replacement is `w.WithTarget(NewTarget(…))` for a `WikiLink` (unchanged) and `m.WithPath(meant)` for a `MarkdownLink`. Update the `Mv` help text: Markdown links that have to change are written as `/Folder/Note%20name.md` (one sentence).

- [ ] **Step 4: Run** `dotnet test` — expected: PASS.

- [ ] **Step 5: Commit** `Rewrite Markdown links absolute when mv has to touch them`.

### Task 4: Tell agents to write absolute Markdown links

**Files:**
- Modify: `src/Kumbu.Core/Sh/Manual.cs` (`ToolDescription`: the `links` line; the "Follow a [[wikilink]]" line; `WriteDescription`: one line), `src/Kumbu.Core/Sh/Commands/LinksCommand.cs` (`Summary`, `Help`)
- Test: `tests/Kumbu.Core.Tests/Sh/HelpTests.cs` (if it pins texts), `tests/Kumbu.Core.Tests/Sh/ShellTests.cs`

- [ ] **Step 1: Write the failing test** `TheToolDescriptionRecommendsAbsoluteMarkdownLinks`: `Manual.ToolDescription` contains `[text](/Folder/Note.md)` and `%20`; `Manual.WriteDescription` contains `[text](/Folder/Note.md)`.
- [ ] **Step 2: Run** it — expected: FAIL.
- [ ] **Step 3: Change the texts.** `ToolDescription`, instead of the "Follow a [[wikilink]]" line:
  `Links: every form is read, [[wikilinks]] and Markdown links; follow one with 'links <path>'. Write new links as [text](/Folder/Note.md), absolute in the vault, %20 for spaces ([notes](/Work/Meeting%20Notes.md)): unambiguous, survives moving the note it stands in, standard Markdown every tool reads.`
  The `links` line: `the links of a note ([[wikilinks]], [text](note.md)) and where they lead`. `WriteDescription`: `Link to other notes as [text](/Folder/Note.md) (absolute in the vault, %20 for spaces).` `LinksCommand` help names Markdown links and their forms (`/`, `./`, `../`, a path or name, `%20` or `<…>`).
- [ ] **Step 4: Run** `dotnet test tests/Kumbu.Core.Tests` — expected: PASS (fix any test pinning the old texts).
- [ ] **Step 5: Commit** `Recommend absolute Markdown links to agents`.

### Task 5: Open notes from Markdown links in the web app

**Files:**
- Modify: `web/src/note/wikiLinks.ts` (`remarkWikiLinks` also maps `link` nodes; `createdPath`), `web/src/note/MarkdownView.tsx` (only if needed), `web/src/note/NoteView.tsx` (`createMissing` uses `createdPath`)
- Test: `web/src/note/MarkdownView.test.tsx`, `web/src/note/wikiLinks.test.ts` (new, for `createdPath`), the checkpoint note test (`web/src/note/CheckpointNote.test.tsx` or where checkpoint links are tested)

**Interfaces:**
- Consumes: `LinkView` from the API (`raw` = `[text](dest)` as written, `target` = decoded path).
- Produces: `export function createdPath(notePath: string, target: string): string` — `/V/a/n.md` + `new` → `/V/a/new.md`; + `/Work/new.md` → `/V/Work/new.md`; + `./x/new.md` → `/V/a/x/new.md`; + `../new` → `/V/new.md`; + `Meeting Notes.md` → `/V/a/Meeting Notes.md`.

- [ ] **Step 1: Write the failing tests** in `MarkdownView.test.tsx` (a `mdLink(line, raw, kind, target, path?, candidates?)` helper beside `link`):
  - `opens the note a Markdown link resolves to`: `"See [the plan](/Work/plan.md) and [x](https://x.org)."` with `mdLink(1, "[the plan](/Work/plan.md)", "note", "/Work/plan.md", "/Work/projects/plan.md")` → link "the plan" has `href` `/notes/Work/projects/plan.md`; link "x" keeps `href` `https://x.org` and `target="_blank"`.
  - `matches Markdown links by line and occurrence`: two `[p](plan.md)` on one line, answers `note` and `not_found` → first is a link, second a create button.
  - `asks which note an ambiguous Markdown link means`: `ambiguous` with two candidates → button with "2 notes", the dialog lists both.
  - `creates the note a missing Markdown link names`: `not_found`, target `/Work/new.md` → click calls `onCreateMissing("/Work/new.md")`.
  - `keeps a Markdown link the answer doesn't have as plain text`: `[p](plan.md)` with no answers → no link role, text "p".
  - In the checkpoint note test: a Markdown link answered `note` opens the checkpoint URL (`checkpointLink`), `not_found` shows "not in this checkpoint".
  - In `wikiLinks.test.ts`: `createdPath` rows from the Interfaces block.
- [ ] **Step 2: Run** `pnpm -C web test` — expected: FAIL.
- [ ] **Step 3: Implement.** The plugin takes the vfile (`(tree, file)`) and, for a `link` node whose `url` has no scheme and doesn't start with `//`, takes its raw text `String(file.value).slice(start.offset, end.offset)`, finds the answer by `line\u0000raw` and occurrence (as wikilinks), and replaces the node by a `wikiLink` node (`hName: "span"`, `dataWikiLink: index`) keeping its children; `WikiLink` in `MarkdownView.tsx` then renders it unchanged. `NoteView.createMissing` creates `createdPath(path, target)`.
- [ ] **Step 4: Run** `pnpm -C web test && pnpm -C web build` — expected: PASS.
- [ ] **Step 5: Commit** `Open notes from Markdown links in the web app`.

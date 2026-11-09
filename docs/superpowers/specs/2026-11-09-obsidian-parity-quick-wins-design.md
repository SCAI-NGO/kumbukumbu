# Obsidian parity: the quick wins for 0.4.0

Date: 2026-11-09. Status: approved by the maintainer.

Several of the current users will care a lot about Obsidian parity. Kumbu² already beats Obsidian for some of them (agentic features, a web app far quicker than the Obsidian app), so closing the everyday gaps makes it the place they work in. The maintainer keeps the whole list in Kumbu² (`/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md`); this spec takes its quick wins for 0.4.0. Bigger items (Mermaid, embeds in `cat`, block references, unlinked mentions, `aliases`, Graph, Canvas) wait for 0.5.0 and 0.6.0, so that 0.4.0 doesn't overload and 0.5.0 keeps something visible.

**With this work 0.4.0 is feature complete.** What is left of the week until Monday 2026-11-16 goes to tests, polish and whatever turns up. Why: both flagship features are in, and the maintainer wants 0.4.0 out rather than growing.

The work is built on one branch, one pull request, each section its own commit.

## 1. Goals, non-goals, anti-goals

Goals: §2 to §6, each testable on its own.

Non-goals:

- The publish reader (`web/src/reader/Reader.tsx`), the skill pages and the install dialog keep their own renderer as it is. The new rendering of §4 and §5 is for the note view (`web/src/note/MarkdownView.tsx`) only.
- Renaming in the Obsidian plugin, over WebDAV or through agents (§2). Only the web app renames, though it renames any "Untitled …" note it edits, wherever that note came from.
- Mermaid, which is a flagship of 0.5.0.
- Anything server-side. No API changes; the rename of §2 uses the existing move (`useMove`, `web/src/api/notes.ts:288`), which rewrites links.

Anti-goals, which make this a failure even if it works:

- **A note renamed the user had named.** Once a note's name is no longer "Untitled …", nothing in §2 ever renames it again.
- **Text lost on the rename.** Whatever was typed before, during and after the rename is in the note afterwards.
- **A rename per keystroke.** Every rename reaches the Obsidian sync clients and WebDAV and rewrites links, so §2 renames once, not while the heading is being typed.
- **Maths that breaks the page.** A malformed formula shows as its source, never as an error that takes the note down.

## 2. The first heading names a new note

Today "New note" creates `Untitled.md` (`Untitled 2.md`, … : `untitledName`, `web/src/api/paths.ts:120`) through `useCreateUntitled` (`web/src/api/notes.ts:161`) and opens it in Write mode (`web/src/shell/MobileShell.tsx:248`). The maintainer then renames it by hand every time, which annoys him as a user. Obsidian names the file from the title.

Decided by the maintainer:

- **The file follows the first `#` heading only while its name is still "Untitled …"** (`Untitled.md`, `Untitled 2.md`, …). Once it has another name, a changed heading leaves it alone. Why: a manual rename carries an intention. "Ideen für das neue Gemüsebeet" may live in `neues Gemüsebeet.md` by choice, while "Einkaufsliste" is plainly `Einkaufsliste.md`; and every rename reaches sync clients and WebDAV and rewrites links.
- Only the web app does it, and **by the name alone, whatever made the note**: an `Untitled.md` from Obsidian (which names new notes `Untitled.md`, `Untitled 1.md`, …), WebDAV, an agent or from before 0.4.0 is renamed too when someone leaves its Write mode in the web app. Why: for the users, Obsidian has become little more than the front end for Kumbu² until Kumbu² can do everything itself, so Kumbu²'s semantics and behaviour come first; and an "Untitled" carries no intention. Both of the app's patterns (`Untitled 2.md`) and Obsidian's (`Untitled 1.md`) count.

Claude's calls, for the maintainer to check:

- A new note's editor starts with `# ` and the cursor after it, so typing the title is the first thing. The content is created that way (`content: "# "`), not only shown.
- The rename happens when the user leaves the note's Write mode: switching to Read, opening another note, or leaving the note view. Not while typing, not on each autosave. The autosave of what was typed has landed before the move.
- The name is the text of the first level-1 heading (`# `, ATX only), trimmed. Nothing happens if there is no such heading or it is empty. Characters Obsidian and common file systems refuse (`/ \ : * ? " < > | # ^ [ ]`) are removed, runs of whitespace become one space, a leading dot is dropped, and the name is cut to 100 characters at a word boundary where possible. If nothing is left, nothing happens.
- If a note of that name exists in the folder (ignoring case, as `untitledName` does), the name gets ` 2`, ` 3`, … like the untitled names.
- Notes in a board's folder (`/<vault>/Board` holding `Board.md`) are cards, named by the board (`notes.ts:173`); they are left alone.
- A failed move leaves the note as it is, under its untitled name, and says so the way other failed moves do. It is not retried on its own.
- Another tab or device that has the note open follows the move the way it follows any move today.

## 3. Copy file path, Copy link, Copy markdown link

The maintainer wants to copy a note's path without typing it, and the agents and `sh` speak in paths. Three items, in the note's ⋯ menu (`noteActions`, `MobileShell.tsx:294`) and in the note's context menu in the tree (`web/src/shell/TreeDrawer.tsx:217`), in this order, all available at the read level (no write permission needed):

| Item | Copies | Example |
|---|---|---|
| **Copy file path** | the path with the vault, as `sh` and the agents use it | `/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md` |
| **Copy link** | an Obsidian wikilink | `[[Obsidian Parity]]` |
| **Copy markdown link** | a Markdown link as the agents write them: the path inside the vault, without the vault's name, `%20` for spaces | `[Obsidian Parity](/Kumbu/Obsidian%20Parity/Obsidian%20Parity.md)` |

Decided by the maintainer: the three names and what they copy. Why: "copy path" is a file path, not a link; the link comes in Obsidian's form and in Markdown's.

Claude's calls:

- **Copy link** uses Obsidian's default, the shortest form that is unique in the vault: the bare name (without `.md`) where no other note in the vault has it, else the path inside the vault without `.md` (`[[Kumbu/Obsidian Parity/Obsidian Parity]]`). The server's link resolution (`src/Kumbu.Core/Links/LinkResolver.cs`) must lead that link back to this note; uniqueness is judged the way it judges it.
- The text of the Markdown link is the note's name without `.md`. Characters that would break the link (`[`, `]`, `(`, `)` and those percent-encoding needs) are escaped or encoded.
- Attachments in the tree get **Copy file path** only.
- A short confirmation ("Copied") as elsewhere in the app; a failed copy says so.

## 4. `==highlight==` and `%%comments%%`

Both are everyday Obsidian syntax and show as raw text today.

- `==text==` renders as `<mark>`, styled from the theme's accent (light and dark). Not inside code; not across lines.
- `%%text%%`, inline or as a block over several lines, is hidden in the reading view. It stays in the source and in Write mode. Not inside code.
- Both are remark plugins next to the existing ones (`remarkCallouts`, `remarkTags`, `remarkWikiLinks` in `MarkdownView.tsx`), so they also apply inside embedded notes and callouts. The agent highlight (`remarkChanged`) and find-in-note (`remarkFind`) keep working on such text.

## 5. Maths with KaTeX

`$…$` inline and `$$…$$` as a block render as maths in the note view.

- Chatsundere (`../chatsundere`, the maintainer's other project) has already solved many of the formatting problems: read its spec `superpowers/specs/2026-06-01-message-rendering-design.md` and `apps/user-client` (its `preprocess-math.ts`) first, and take over what fits: `remark-math` + `rehype-katex` + `katex`, `throwOnError: false`, the handling of whitespace-bounded inline maths, and leaving maths-like text inside code alone. Chatsundere's `\( … \)` and `\[ … \]` conversion is for model output; Obsidian notes use `$` only, so it is left out unless it costs nothing.
- **Currency stays text.** "costs $5 and $10" must not turn into maths; remark-math's rule for that is kept.
- KaTeX and its CSS and fonts load only when a note has maths (a dynamic import), so the app's first load doesn't grow. Fonts come from the app's own origin; the app's CSP (`src/Kumbu.Server/Api/Security.cs:59`) is not loosened.
- A formula KaTeX can't parse shows as its source, marked as such.

## 6. Footnotes

remark-gfm parses footnotes already. Check in the note view that `[^1]` and its definition render, that the reference and the back link jump to each other inside the note's scroller (not the window, and not changing the URL in a way the router takes for navigation), and that they look like the rest of the theme. Fix what doesn't. If everything already works, this section is a test that says so.

## 7. Tests

Vitest, as the surrounding code has them:

- §2: the name from a heading (sanitising, cut, empty, collision, board folder), renamed on leaving Write mode only while "Untitled …", not renamed once named, text typed just before leaving survives.
- §3: the three strings for a unique name, a name that exists twice in the vault, spaces and brackets; the items appear for read-only members; attachments get the path only.
- §4: highlight and comment, inline and block, not inside code, inside a callout.
- §5: inline, block, currency stays text, maths inside code stays code, malformed formula shows its source.
- §6: footnote reference and back link.

`pnpm -C web test --run` and `pnpm -C web build` pass; `dotnet test` is untouched but runs in CI.

## 8. Hand test

The maintainer's hand tests for 0.4.0 get these items, with steps, where to look and what passes; the browser parts can run through Playwright on staging.

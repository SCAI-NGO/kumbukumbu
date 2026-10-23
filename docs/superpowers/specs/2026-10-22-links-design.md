# Links, Backlinks and `mv` — Design (step 4b)

**Date:** 2026-10-22
**Scope:** The link half of `docs/concept.md` §12 step 4: wikilinks, link resolution, backlinks, `mv` with link rewriting (the link normaliser is the part of `mv` that writes links). Also the feedback from [notes-test-phase4a.md](../../notes-test-phase4a.md), done first in its own commit.
**Status:** "Content is never rewritten automatically" and "`mv` with link rewriting off for the Obsidian plugin" settled with Chris (concept §4, [plugin spec](../../obsidian-plugin-design-spec.md) §3). The rest written by Claude while Chris was away, for review with the PR.

## 1. Intent

- An agent follows a `[[wikilink]]` in one call (`links <path>`), instead of `rg --files | rg -i name` (the workaround since phase 2).
- An agent sees what points to a note (`backlinks <path>`), e.g. before changing or removing it.
- An agent renames or moves a note (`mv`), and every link that pointed to it still does afterwards.
- The Obsidian plugin can move a note without the server touching links (`mv --keep-links`), since Obsidian rewrites them itself.

Out of scope: Markdown links `[text](path.md)` (wikilinks are what Obsidian writes by default; see §3), moving directories, links between vaults, tags, a stored link index.

## 2. Decisions

| Topic | Decision | Why |
|---|---|---|
| What is a link | `[[target]]`, `[[target#Heading]]`, `[[target#^block]]`, `[[target\|alias]]` (also `\|` escaped as in tables), and embeds `![[target]]`. Not inside fenced code or inline code spans. Links in YAML frontmatter count. | Concept §4; Obsidian counts links in properties. |
| Notes only | A target that resolves to no note and has another extension (`image.png`, `doc.pdf`) is an attachment link: shown as such by `links`, never rewritten. | Kumbu² holds no attachments (concept §4). |
| Resolution | Within the source note's vault, case-insensitive, Unicode-normalised: `./` and `../` relative to the source's folder; otherwise the note whose vault path ends with the link path (`kumbu` → `projects/kumbu.md`, `projects/kumbu` → the same). Several candidates: the one in the source's own folder wins; otherwise the link is **ambiguous** and reported with its candidates, never guessed. | Close to Obsidian's rules (shortest unique name, path where ambiguous). Where Obsidian would quietly pick one, an agent is better served by the candidates. |
| When links resolve | At read time, from the notes of the vault. **No stored edge index.** | Concept §4 (decided Oct 22): writes are verbatim, links resolve when read. Parsing a vault of a few thousand notes takes milliseconds, and nothing can go stale. With encryption (step 5) this is the unlocked vault in RAM anyway (concept §7). An index can come later if a vault gets big. |
| `links <path>` | Every link of a note with its line and where it leads: a path, `not found`, `ambiguous: …`, `attachment`, or `this note` for `[[#Heading]]`. | Answers phase 1 observation 1 directly. |
| `backlinks <path>` | `source:line:text` per linking line, like `rg`, sorted in human order. Only the note's own vault (concept §4: no existence leaks across vaults). | The `rg` layout is the one models already read fluently. |
| `mv <src> <dst>` | One note at a time, within one vault. `dst` may be a directory (ending in `/`, or an existing one): the note keeps its name. The target is checked like a new note (`NoteNames`), so a badly named note can be renamed to a good name but never the other way round. An existing note is never overwritten. History moves with the note. | Concept §4. No silent overwrite, consistent with `write`. |
| Which links `mv` rewrites | Every link in the vault whose resolution the move would change: links to the moved note, and links that resolved to *another* note but would now hit the moved one (a name collision). Each is rewritten to its old target in the **most specific form**, the vault path without `.md` (`[[projects/kumbu-v2]]`), keeping heading, alias and the `!` of embeds. Everything else in the note stays byte for byte. | Concept §4 ("maximally specific link form"). Checking every link's resolution before and after is the only way to catch collisions. |
| How `mv` writes | Move first (insert at the target with the revision check, then delete the source with it; undone if the source changed), then one compare-and-swap write per note with rewritten links, through the same rules as `edit` (history, coalescing). A note that changes in between is re-read and retried. | Atomic per document, like every write since step 3; no transactions. |
| `mv --keep-links` | Moves without rewriting any link. | For the Obsidian plugin (plugin spec §3): Obsidian rewrites links itself and uploads them as edits. |

## 3. Known limitations

- Markdown links (`[text](projects/kumbu.md)`) are neither resolved nor rewritten. Obsidian creates wikilinks by default; Markdown links can follow if real vaults use them.
- Until the seeder goes away, a note moved with `mv` comes back at its old path on the next start if its file is still on disk (the seeder sees a missing note that is not in the trash).

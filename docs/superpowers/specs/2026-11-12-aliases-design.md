# `aliases` in the front matter

Date: 2026-11-12. Status: approved by the maintainer.

A note can carry other names in its front matter, as in Obsidian:

```markdown
---
aliases: [Kumbu, K²]
---
# Kumbukumbu
```

`[[Kumbu]]` and `[[K²]]` then lead to `Kumbukumbu.md`, wherever the links stand in the vault. This is the last 0.5.0 item of the parity list (kept in Kumbu² at `/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md`). Why now: it is small and well bounded, Obsidian vaults carried over into Kumbu² already use it (their alias links show as "not found" today), and it makes unlinked mentions (#195) find a note by its short names too. It is one overnighter, one branch, one pull request.

The Mermaid spec of the same day (`2026-11-12-mermaid-and-parity-design.md`) lists "`aliases` as mention names" among its non-goals for 0.6.0; this spec supersedes that line, and §4 brings them in.

## 1. Goals, non-goals, anti-goals

Goals: §2 to §6, each testable on its own.

Non-goals:

- **Aliases in the web app's link completion** (`[[` in the editor suggesting `Kumbu` for `Kumbukumbu`). It needs the aliases in the tree answer or a new endpoint; a follow-up.
- **Editing aliases in the web app** (a properties editor). They are front matter, written as today.
- **A hint when a write changes a note's aliases** and so makes links ambiguous or moves them. Only creating a note gets a hint (§3.4).
- Aliases across vaults, Markdown links by alias (`[x](Kumbu.md)` is a path and stays one), the deprecated singular `alias:` key, changes to the Obsidian plugin, WebDAV or sync.

Anti-goals, which make this a failure even if it works:

- **A link that silently leads somewhere else.** An alias never takes a link away from a note or attachment its name matches today; where two notes share an alias, the link is ambiguous, never a guess.
- **An `mv` that rewrites alias links into paths.** `[[Kumbu]]` stays `[[Kumbu]]` when `Kumbukumbu.md` moves: the alias moves with the note.
- **Aliases that leak.** An alias is read only from notes in the same vault, and only from notes the reader may read (a publication: only from the notes it publishes).

### An example from end to end

Alice's vault `Work` holds `/Work/Projects/Kumbukumbu.md` with `aliases: [Kumbu, K²]`, no note named `Kumbu` or `K²`, and `/Work/Diary/2026-11-12.md` says:

```markdown
Talked with Bob about [[Kumbu]] and the [[K²#Roadmap|roadmap]].
Kumbu needs a calendar.
```

- In the web app, both links in the diary are links; tapping `Kumbu` opens Kumbukumbu, tapping `roadmap` opens it at its Roadmap section.
- `links /Work/Diary/2026-11-12.md` prints
  `1: [[Kumbu]] → /Work/Projects/Kumbukumbu.md (alias)` and
  `1: [[K²#Roadmap|roadmap]] → /Work/Projects/Kumbukumbu.md (alias)`.
- `backlinks /Work/Projects/Kumbukumbu.md` lists the diary's line 1. In the web app, "Linked from" lists the diary.
- `backlinks --unlinked /Work/Projects/Kumbukumbu.md` gives `/Work/Diary/2026-11-12.md:2:Kumbu needs a calendar.`, "Kumbu" marked; the web app's Unlinked mentions list the same line. Line 1 isn't listed: its "Kumbu" stands inside links (§4).
- `mv /Work/Projects/Kumbukumbu.md /Work/Done/Kumbukumbu.md`: the diary is not rewritten; both links still lead to the moved note.
- Bob, in the same vault, adds `/Work/Ideas/Kumbu.md`. From then on `[[Kumbu]]` leads to Bob's note, because a note's name beats an alias (as in Obsidian). The `write` answer says so (§3.4): `[[Kumbu]] in /Work/Diary/2026-11-12.md now leads to /Work/Ideas/Kumbu.md (before: /Work/Projects/Kumbukumbu.md by its alias)`.
- Carol writes `/Work/People/Kumbu Team.md` with `aliases: [K²]`. `links` on the diary then says `[[K²#Roadmap|roadmap]] → ambiguous: /Work/People/Kumbu Team.md, /Work/Projects/Kumbukumbu.md`, and the web app shows it as an ambiguous link, as any other.
- Alice publishes the diary: in the reader page, `[[Kumbu]]` behaves as any link to a note the publication holds or doesn't.

### Security boundary

Not touched: authentication, sessions, encryption and keys, accounts, sharing and roles, discreet vaults' semantics, publishing's semantics, WebDAV, sync, the CSPs. Aliases come only from the front matter of notes in the vault the resolver already works on, and only from the notes it is already given; no new reading path, no new endpoint, no new stored data. Front matter is parsed with the existing YAML reader (`Kumbu.Core.Board.Frontmatter`), whose failure is "no front matter values", never an exception reaching a request.

## 2. What an alias is

- The key `aliases` at the top level of a note's YAML front matter. Its value is a list (`[a, b]` or a block list) or a single text (`aliases: Kumbu`). A scalar item that isn't text (`2026`, `true`) counts by its text as written; maps, nested lists and nulls are skipped.
- Each alias is trimmed and Unicode-normalised (NFC), compared ignoring case, as note names are. Empty values are skipped, and so is a value that can't be a link's name: one containing `/`, `#`, `|`, `[`, `]`, `^` or a line break. Duplicates count once.
- A note whose front matter doesn't parse has no aliases; nothing else about it changes.
- Notes only; attachments have no front matter and no aliases.

## 3. Links

### 3.1 Which links

A wikilink (`[[…]]`, embeds `![[…]]` included) whose target is a bare name: no `/`, not `./` or `../`. Its heading or block subpath and its display text work as with any other link. Markdown links never resolve by alias. Links in front matter (Obsidian's links in properties) count, as they do today.

### 3.2 Order

1. The existing rules, unchanged: an attachment by its name, then a note by its name, with the same-folder and archive preferences as today. If they find a note, an attachment, or an ambiguity, that is the answer.
2. Only if they find nothing: the notes whose aliases contain the name. One candidate: that note. Several: the archive preference as for names (for a source outside the archive, an archived note counts only if no living one matches), then the source's own folder if exactly one candidate is there; otherwise ambiguous, with every candidate.
3. Still nothing: as today (not found, or an attachment that isn't there).

A note's alias that equals its own name changes nothing. `[[Kumbu]]` inside `Kumbukumbu.md` itself leads to that note.

### 3.3 Where it applies

Everywhere links are resolved, through the one resolver (`src/Kumbu.Core/Links/LinkResolver.cs`), so they agree: `links` (on a note and on a folder's broken and ambiguous links), `backlinks`, the REST answer the web app draws links from, embeds in the web app and in `cat`, block references, `rm`'s count of linking notes, `replace`'s link warnings, `mv`'s rewrites, and the reader page of a publication. A resolution by alias is marked as such (e.g. a flag on `LinkResolution`), so `links` can add ` (alias)` after the target; the REST answer may carry the flag too, the web app needs nothing new from it.

### 3.4 Moving and creating notes

- **`mv`** of a note with aliases: links that led to it by alias lead to it afterwards by the same alias, so they are not rewritten. Links that led to it by name are rewritten as today. A move that makes an alias link ambiguous or lose its note (another note's name now matches) is reported like the name collisions today.
- **Creating a note** (`write`, upload of a note, the web app): the existing hint about links that are now ambiguous also lists links that led to another note by alias and now lead to the new note by its name, in the form of the example (§1).

## 4. Unlinked mentions

A note's unlinked mentions (`backlinks --unlinked`, the web app's Unlinked mentions section) look for its title and each of its aliases, by the existing rules (whole word, ignoring case, at least three characters, outside front matter, code and links). A line that mentions several of them is listed once, with every match marked.

## 5. Tests

xUnit in `tests/Kumbu.Core.Tests` for §2 to §4: the parsing cases of §2 (list, block list, single text, number, nested, invalid YAML, forbidden characters, duplicates, NFD input), every step of §3.2 including name-beats-alias, archive and same-folder preference and ambiguity, the callers of §3.3 (at least `links`, `backlinks`, `mv` keeping alias links, the creation hint, a publication resolving only from its own notes), and §4. A web test only if the web app's code changes.

## 6. Documentation

`help links` (`src/Kumbu.Core/Sh/Manual.cs`) gets one sentence on aliases; the README, where it lists what Kumbu² reads of Obsidian's Markdown, one line. The parity list in Kumbu² is the maintainer's to tick.

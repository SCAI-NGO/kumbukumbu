# Tags — Design

**Date:** 2026-10-28 · **Status:** agreed in chat, written for review · **Release:** v2.5 (0.2.5)

## Intent

Notes carry tags the way Obsidian writes them: `tags` in the frontmatter and `#tags` in the text. Kumbu² should read them as Obsidian does, since the aim is parity with Obsidian for text, and let people and agents use them in three ways: see which tags a vault has, list the notes with a tag, and narrow a search to notes with a tag. The last one is the big win: "what did we discuss about modelling capabilities in #research?" asks `semsearch` for a topic that comes up in many projects, and the tag keeps the results to the one meant.

The Kanban design left this out on purpose ("a general tag search over all notes: a feature of its own, later, which then covers cards too"). Cards are notes, so they are covered without anything of their own.

## Goals

- Tags are read from the frontmatter (`tags`) and from the text (`#tag`), by Obsidian's rules (below).
- Nested tags as in Obsidian: `#project/kumbu` is a tag of its own, and asking for `#project` finds it too.
- Tags match ignoring case, as in Obsidian.
- `tags` in `sh`: the tags below a path with their counts, or the notes with one tag.
- `--tag` on `rg` and `semsearch`: only notes with that tag (several: all of them).
- `board --tag` follows the same rules (nested, ignoring case).
- The web app shows a note's tags; a tag opens the list of notes with it; the search takes a tag filter.
- Everything works on a checkpoint as on today's notes (`--checkpoint`).

## Non-goals

- Writing tags: renaming a tag across notes, adding or removing one. `replace` changes text in whole notes, frontmatter included, so a rename is `replace '#old' '#new'` plus the frontmatter's spelling; a tool of its own is a later topic if it is missed.
- Tags on attachments (images, PDFs): they have no text to carry one.
- A tag index in MongoDB: tags are read from the notes an unlocked vault already holds in memory, as `rg`, `links` and `backlinks` do.
- Tag colours, a tag tree view or a tag pane as in Obsidian's sidebar.

## Anti-goals (it failed if…)

- …a heading (`# Title`), a URL's fragment (`https://example.org/page#part`), a colour (`#fff` inside code), or a number (`#123`, an issue reference) is taken for a tag.
- …Kumbu² and Obsidian disagree about the tags of an ordinary note.
- …a tag filter silently searches everything because the tag is misspelt: a tag no note carries says so.
- …tags change a note's text in any way.

## What is a tag (Obsidian's rules)

### In the frontmatter

- The key `tags`. The older `tag` was deprecated in Obsidian 1.4 and is no longer read since 1.9 (Obsidian's help, "Properties", "Deprecated properties"), so Kumbu² doesn't read it either.
- A YAML list (`tags: [a, b]` or a block list with `- a`), or a string of tags separated by commas or spaces (`tags: a, b` or `tags: a b`), as older notes have it.
- A leading `#` in a value is dropped (`tags: ["#a"]`); a value that isn't a tag by the rules below (`2026`) is left out.
- A frontmatter that doesn't parse yields no tags and no error: the note is still read for its inline tags. The parser of `Kumbu.Core/Board/Frontmatter.cs` is reused.

### In the text

- `#` followed by at least one tag character, at the start of a line or after whitespace. `word#tag` is no tag; `##tag` and `# heading` are none.
- Tag characters: Unicode letters and digits, `_`, `-` and `/`, and, as Obsidian's help says, "commonly accepted Unicode characters, including emojis and other symbols" (`#🚀launch`). The tag ends at whitespace, at ASCII punctuation other than `_`, `-` and `/`, and at the general punctuation of Unicode (`…`, `—`), so `#tag.`, `#tag,` and `#tag's` are `tag`.
- At least one character that isn't a digit: `#123` and `#2026` are no tags, `#v2` and `#2026-plan` are.
- Not inside fenced code blocks (``` and ~~~), indented code blocks (outside a list: there the same indent is a nested item), inline code, HTML comments (`<!-- -->`), Obsidian comments (`%% %%`), wikilinks or HTML tags (`<span style="color: #fff">`), nor in the frontmatter (read as above).
- A trailing `/` is dropped (`#project/` is `project`).
- Checked against Obsidian's help vault ("Tags", "Properties") and its API's `getAllTags` and `parseFrontMatterTags`; the cases are the rows of `TagReaderTests`. Changed after that check: the `tag` key isn't read, and emoji and symbols are tag characters.

### Matching

- Ignoring case: `#Research` and `#research` are the same tag. A listing shows the spelling used most often (ties: the first in path order).
- Nested: asking for `project` finds `project` and `project/…`; asking for `project/kumbu` finds that and `project/kumbu/…`, never `project`.
- Asked for with or without `#`: `--tag research` and `--tag '#research'` are the same.

## Tools for agents

### `tags`, a new reader in `sh`

```
tags [path]
tags <tag> [path]
```

- Without a tag: the tags of the notes below the path (default: every vault), one per line, `#tag (count)`, the count being notes, sorted by count, then by name. Nested tags are listed in full (`#project/kumbu (4)`); a parent that never stands alone isn't invented.
- With a tag: the notes that carry it (nested included), as `path:line:text` like `rg`, the line where the tag stands (for a frontmatter tag, the line of its key or list item), so `| head`, `| wc -l` and `| rg` work as everywhere.
- Takes `--checkpoint`; appears in `help checkpoints`.
- A tag no note carries: "no note below /Work carries #reserch; `tags /Work` lists the tags".

### `--tag` on `rg` and `semsearch`

- `rg --tag research -i capabilit /Work`, `semsearch --tag research modelling capabilities`.
- Repeatable: `--tag a --tag b` means notes with both.
- Narrows the notes before searching; for `semsearch` the ranking is the same as without, over fewer notes.
- An unknown tag fails with the sentence above instead of returning nothing.

### Manual and instructions

- `help tags`, the command list, and one line in the manual on when to use a tag filter.
- One clause in the server's `initialize` instructions: notes may carry `#tags`; `tags` lists them, `--tag` narrows `rg` and `semsearch`.
- No new MCP tool: reading tags is `sh`, writing them is `edit`, `write` and `replace`.

## Server

- A tag extractor in `Kumbu.Core` (pure, unit-tested): note text in, tags with their line numbers out.
- The working set keeps a note's extracted tags with its version, computed on first use, so a search over a vault doesn't parse every note each time. The plan measures a vault of 2,000 notes with and without that cache and keeps the cache only if it's needed.
- REST, both readable with an API key that may read the vault, as the other readers are:
  - `GET /api/tags?path=/Work` → `[{ tag, count }]`.
  - `GET /api/tags/notes?tag=research&path=/Work` → the notes with their lines.
  - `GET /api/search` takes `tag` (repeatable) as `semsearch` does.
- `GET /api/search/earlier` (sessions only) takes no tag filter in this round: earlier versions carry the tags they had then, which is a question of its own.

## Web app

- **Note view:** inline `#tags` in the rendered Markdown are links styled as chips; the frontmatter's tags show as chips under the title. A tag opens the search sheet filtered by it.
- **Search sheet:** a tag filter as a chip above the results (`#research ×`). Typing `#research capabilities` sets the chip and searches for the rest; a tag alone lists the notes that carry it. The scope (this vault or all) is the sheet's as today.
- **Tag list:** in the search sheet, with an empty query, "Tags" lists the vault's tags with counts; tapping one sets the filter.
- Phone first, as everywhere: chips wrap, the sheet's layout stays as it is.

## Testing

- The extractor, table-driven: every rule above with a case that is a tag and one that isn't (heading, URL fragment, code block, inline code, comments, `#123`, `word#tag`, Unicode `#Grüße`, nested, trailing `/`, frontmatter list, flow list, comma string, space string, the `tag` key not read, `#` in a value, broken frontmatter).
- `tags` with and without a tag, nested and case, unknown tag, `--checkpoint`, in a pipe.
- `rg --tag` and `semsearch --tag`: narrowing, several tags, unknown tag.
- `board --tag` with a nested tag.
- REST: the three endpoints, an API key with and without read rights on the vault.
- Web: chips in the note view, a tag opens the filtered search, `#tag words` in the search box, the tag list.

## Changes elsewhere

- `docs/concept.md`: tags as part of v2.5 in the roadmap and the Kanban paragraph ("tag search … later" becomes built).
- `docs/superpowers/specs/2026-10-27-kanban-design.md`: its non-goal points to this design.
- README: tags in the list of what agents and the web app can do.
- `CONCEPTS.md`: a short section in the search chapter on tags as a filter, and why they are read rather than indexed.

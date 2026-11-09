# Structural chunking: sections that stay whole, tables included

Date: 2026-11-09. Status: approved by the maintainer on 2026-11-09. Target: 0.4.0 (release planned for Sunday 2026-11-15). Builds on `master` with ingesting attachments (#176, the cherry-pick of #174).

## 1. Why

Ingesting attachments (`2026-11-08-ingest-attachments-design.md`) turns PDFs into long Markdown notes with headings, lists and **tables**. Search has to find what these notes hold. Today it can miss parts of them without saying so:

- `Chunker` (`src/Kumbu.Core/Search/Chunker.cs`) splits a section once it reaches 2,000 characters, assuming about 4 characters per token. German, tables, code and columns of numbers use more tokens per character. The tokenizer then cuts silently at 512 tokens (`UnigramTokenizer.Encode`), and the end of the chunk is never embedded.
- A PDF page is embedded as one chunk whose text is cut at 2,000 characters (`SearchIndex`, `PdfAnalyses.SearchTextAsync`). Whatever follows on a dense page is invisible to search by meaning.
- The parts of a long section don't overlap, and a table is split wherever the character count runs out, so rows get separated from their header.
- A single line over 4,000 characters is cut.
- `semsearch` shows one line per hit. The AI has to guess from that line and then `cat` blindly.

The maintainer wants ingesting attachments to ship "completely rounded" in 0.4.0: what goes in comes out again through search. Markdown is our central format and well structured, so chunking should follow that structure.

## 2. Goals, non-goals, anti-goals

**Goals**

- Chunks follow the Markdown structure: headings first, then blocks (paragraphs, lists, tables, code). Things that belong together stay together.
- Length is measured in **meaningful words** (§3.1), and the model's real token limit is a hard ceiling. Nothing is cut off silently.
- Short sections under one heading are merged into one chunk instead of becoming many tiny chunks.
- Tables stay whole while that makes sense. A table too long for one chunk is split between rows, and every part carries the header row.
- Each chunk has a **pre-roll and post-roll**: surrounding text for context. It is shown, never embedded.
- PDF pages are split into several chunks by word count instead of being cut off, with a fixed-size pre-roll and post-roll.
- `semsearch` shows each hit's whole core text by default. `-C` adds the pre-roll and post-roll, and `-l` gives a compact list.
- Automated tests prove the whole path: text anywhere in a long note, a dense PDF page or a long table is found again.

**Non-goals**

- A different embedding model, a different search ranking (cosine plus word bonus, BM25), or a change to `rg`'s regex semantics.
- Chunking that understands content (sentences, topics) beyond the Markdown structure.
- One chunk per table row. The hybrid search already finds single codes and names through BM25 (the maintainer: `GL_INVALID_OPERATION` and `157452` are found either way), and single rows would lose their context.
- PDF chunks that span pages. The page stays the unit you can address (`pdf path 7`).

**Anti-goals** (failure even if it technically works)

- Any text that is in a note or on a page but cannot be reached by search by meaning, unless the user is told.
- Pre-roll or post-roll in the embedded text. It would blur what a chunk is about (maintainer's decision, §3.4).
- A long table whose parts can't be understood on their own because the header is missing.
- 0.4.0 slipping. If something here threatens Sunday, it goes to a follow-up, and the core (word lengths, token ceiling, tables, PDF splitting) goes first.

## 3. Decisions taken

### 3.1 Length in meaningful words (maintainer)

For the chunking algorithm, length is the number of **words**: runs of Unicode letters and digits. Punctuation, special characters and Markdown markup (`#`, `|`, `-`, `*`, backticks, link syntax) don't count. In the maintainer's words: "grep for only letters, digits, spaces and line breaks, then `wc`". A table's pipes and separator rows therefore weigh nothing, and its cell contents count.

### 3.2 The token limit is a hard ceiling (maintainer, after Claude pointed it out)

The embedding model reads at most 512 tokens. Word counts decide where the structure is split. Every chunk's embedding text (context plus core) is then checked against the real tokenizer and made smaller if it would go over. Nothing is ever truncated silently.

*Claude's call:* the check uses the local arctic tokenizer (`UnigramTokenizer`) for every provider. For a remote provider it is an approximation, but chunks then stay within a size every current model reads.

**Where the tokenizer comes from (maintainer, 2026-11-09):** download it. Today `tokenizer.json` (about 17 MB) only arrives with the model download (`ModelFiles`), so it is missing with `KUMBU_SEARCH=false` and an external provider, and before the first download has finished. From now on:

- The server downloads the tokenizer on its own, pinned by SHA-256, **whenever any embedding provider is active**, internal or external, whether or not the internal model is used (maintainer, 2026-11-09). A server with no provider at all needs no tokenizer: without search by meaning, the fallback bound below suffices, and such a server keeps working offline as the README says.
- Tests and CI point the server at a file fetched beforehand, through an environment variable. This is for tests and CI only, not a new setting for admins. Test hosts never download.
- CI fetches the same file, pinned by the same hash, and caches it, so the token-ceiling tests run on every pull request instead of skipping.
- Until the tokenizer is there, a conservative bound without a tokenizer applies (*Claude's call*, e.g. a word and character bound that keeps even dense text under 512 tokens). With a provider active, the indexer **waits for the tokenizer** (as it waits for the model today) and uses the fallback only after the download has failed. That way notes aren't chunked twice on every start, and an external provider isn't sent the same text twice.
- The chunking's scheme version (§4) includes which bound produced the boundaries: tokenizer by hash, or the fallback. When that changes, notes are chunked anew instead of their chunk keys drifting silently.
- Search-time chunking (`SectionSearch`, `EarlierSearch`, and through them `NoteApi` and `Semsearch`) uses the same bound as the stored chunks, so the boundaries are identical on both sides. Hits are matched back to chunks by start line (`SectionSearch.Locate`). A note stored under another scheme counts as pending until it is re-chunked, rather than having its hits attached to the wrong chunks.

Why a download and not the file in the repository: 17 MB doesn't belong in Git. A separate repository for all downloaded files may come later. The maintainer will decide that after 0.5.0.

### 3.3 Short sections are merged under their heading (maintainer)

Headings remain the frame. A section whose own text is short is merged with its neighbouring sections that share the same parent heading, while the result stays within the target length. Merging never crosses into another parent section.

### 3.4 Pre-roll and post-roll: context, not search (maintainer)

Every chunk has a **core**, which is embedded, ranked and counted, and a **pre-roll** and **post-roll**, which are only there to put the core in context when a hit is shown. They are **not embedded**. In the maintainer's words: "they serve to contextualise the content, not the search."

How far the rolls reach (maintainer's idea, worked out by Claude): go up to the next higher heading level, so the rolls are the rest of the enclosing section before and after the core, unless that is too long. If it is too long, take the neighbouring blocks up to a fixed word budget on each side, ending at block boundaries.

### 3.5 Tables stay together while sensible (maintainer)

A GFM table is one block and is never split inside a row. If a table fits the maximum, it stays whole in one chunk. If it doesn't (the maintainer's example: someone pastes all of OpenGL's error codes), it is split into groups of whole rows. Every group's core carries the header row and the separator row, so it can be read on its own. The line range still points at the rows it really covers.

### 3.6 PDF pages are split by word count (maintainer)

A page's text has no Markdown structure. It is split into chunks by word count, at paragraph or line breaks where possible. The rolls are **semi-static**: a fixed word budget before and after, snapped to the nearest line break. Chunks don't cross page boundaries, and the context stays `file name > pN`.

### 3.7 What `semsearch` prints (Claude's recommendation, accepted by the maintainer)

The maintainer asked what helps the AI most. Claude's answer:

- **Default:** per hit, the header line as today (`path#Address  lines a-b  score s`, or `path pN` for a PDF page), then the **whole core text**, indented. The AI can judge a hit without a follow-up `cat`. Ten hits are a few thousand tokens. In the maintainer's words, "not a 500 kB website".
- **`-C`:** additionally prints the pre-roll and post-roll around each core, marked so they can't be mistaken for the core. The name follows `rg -C`.
- **`-l`:** compact, one line per hit (address, lines, score), for an overview of many hits.

- **The output limit (maintainer, 2026-11-09):** `sh` cuts every output at `OutputLimit.MaxChars` (24,000 characters). Whole cores are printed while they fit in that budget, with room kept for the footer. The remaining hits fall back to the one-line form, with a note that `-n` or `cat` shows more. The footer is always printed, as `pdf` keeps its room with `Reserve`.
- `-C` takes no value. A number right after it is refused with the usage line, so that `semsearch -C 3 query` doesn't quietly search for "3 query".

The footer, the notices and `-n`, `--words`, `--meaning` and `--tag` stay as they are. `help semsearch` is rewritten accordingly.

### 3.8 Ships in 0.4.0 (maintainer)

Together with ingesting attachments, so that the feature is complete when it ships.

### 3.9 Example, end to end

A note `/Work/Pump/Manual.md`:

```markdown
# Pump P-200
## Safety
Wear gloves. Disconnect power before opening the housing.
## Error codes
| Code | Meaning | Remedy |
|------|---------|--------|
| E01  | Dry run | Fill the inlet and restart |
… 240 more rows …
| E241 | Sensor drift | Recalibrate in the service menu |
## Maintenance
### Weekly
Check the seal for drips.
### Monthly
Clean the filter.
```

What chunking gives (word counts illustrative):

- *Safety* is short, and so are *Weekly* and *Monthly*. *Safety* stands alone, because *Error codes* follows it and is too big to merge with. *Weekly* and *Monthly* share the parent *Maintenance* and are merged into one chunk with the address `#Pump P-200#Maintenance` and lines 250-254.
- *Error codes* is far over the maximum. It is split into groups of whole rows, about 300 words each. Every group's core starts with the header row `| Code | Meaning | Remedy |` and the separator row, and its line range covers only its own rows. The context is `Manual > Pump P-200 > Error codes` for every group.
- The rolls of the second table group are the last rows of the first group and the first rows of the third (the enclosing section is too long to take whole). They are shown, not embedded.

`semsearch sensor drift recalibrate /Work` then prints the last group:

```
1. /Work/Pump/Manual.md#Pump P-200#Error codes  lines 230-247  score 0.71
   | Code | Meaning | Remedy |
   |------|---------|--------|
   | E224 | … |
   …
   | E241 | Sensor drift | Recalibrate in the service menu |
(ranked by words and meaning; cat "path#Section" reads one)
```

With `-C`, rows before line 230 and the *Maintenance* heading after line 247 appear around the core, marked as context. With `-l`, only the first line is printed. A PDF hit prints as `/Work/Pump/P-200.pdf p12  score 0.64`, followed by the core of page 12's best chunk.

### 3.10 Security boundary

Not touched: encryption (embeddings, including the changed chunk records, stay inside the sealed payload as today), accounts, sessions, sharing and roles, OAuth, pairing, WebDAV, Obsidian sync, the export, publishing, the PDF sidecar. The one new outside access is the tokenizer download, pinned and verified exactly as the model files are (`ModelFiles`).

## 4. Claude's calls (the maintainer can override them)

- **Thresholds, as starting values in one place:** merge sections while under a target of about **250 words**. A core holds at most about **350 words**, and the token ceiling applies on top. Rolls get a budget of about **120 words** each side. A chunk under about **40 words** is merged with a neighbour where the structure allows. The builder tunes these against the tests' fixtures and real ingested notes, and writes down the values chosen.
- **Blocks:** paragraph, list, table, fenced code, block quote, and the text before the first heading. A list too long for one chunk is split between items, and fenced code between lines. A single line over the maximum (extracted text without line breaks) is split at word boundaries, so it is no longer cut.
- **Address of a merged chunk:** the address of the deepest heading that all merged sections share, plus the exact line range. `cat path#Address` may then return more than the core, and the line range is what's exact.
- **Re-indexing:** the chunking gets a **scheme version** that counts towards whether stored embeddings are current (`SearchIndex.IsCurrent` compares only the note's version today). After the update every note and PDF is chunked anew. Vectors are reused by text hash where a chunk's embedding text is unchanged, so only new texts are embedded. The `Pending` count in `semsearch`'s footer covers the time this takes.
- **PDF chunk keys:** a page can now have several chunks. Their key becomes page plus position on the page. Today `EmbeddedChunk.StartLine` holds the page number, and `SemanticHit` carries only `StartLine`. A hit still prints as `path pN`.
- **One hit per PDF page:** the page stays the unit, as `WithPages` keeps it today (best cosine per page). The core shown is the page's best-ranking chunk, and `-C` shows the rolls around that chunk. A page's other chunks don't take up `-n` slots.

## 5. Automated tests

The maintainer: ingesting attachments processes data end to end, so it **must be tested automatically**, not only by hand.

- **Runs in CI:** every test below except the end-to-end one runs on every pull request, with the downloaded tokenizer (§3.2).
- **Chunker:** no line is lost (every non-blank line of a note is in exactly one core). Tables stay whole when they fit. A large table splits between rows with the header in every part. Short sibling sections are merged and never across parent sections. No embedding text goes over 512 tokens, even for German prose, a dense table, code, or a single line of 10,000 characters. Rolls are never part of the embedding text.
- **PDF:** a dense page yields several chunks covering all its text, and rolls stay within the page.
- **`semsearch`:** the default prints whole cores, `-C` prints the rolls distinguishably, `-l` prints one line per hit, and `help semsearch` documents all three.
- **Re-indexing:** a scheme version change re-chunks stored notes, and unchanged texts keep their vectors.
- **End to end** (with the real model, skipped without `KUMBU_MODEL_DIR` like the existing search tests): a fixture PDF with a dense page and a long table goes through extraction and indexing. Then `semsearch` finds (a) a sentence from the end of the dense page, (b) a row near the end of the long table together with its header in the core, and (c) the same content after it has been written into a note, as the ingest skill does.

## 6. Left to the builder

- How Markdown blocks are recognised. The existing `Outline` and `Lines` helpers already know headings, frontmatter and fenced code, and tables follow GFM.
- The exact markers `-C` uses for the rolls, and how a PDF hit shows its core and rolls.
- Whether `rg` (`SectionSearch.Exact`) keeps grouping by these chunks or by plain sections. Its output must not get worse for a single match.
- Tuning the thresholds in §4.

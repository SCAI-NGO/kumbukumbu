# Structural chunking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace search's character-based chunking with chunking that follows the Markdown structure, bounded by the model's real 512-token limit, split PDF pages instead of cutting them, and let `semsearch` show each hit's whole core.

**Architecture:** A `ChunkBound` in `Kumbu.Core/Search` says how long a chunk may be (arctic-m's tokenizer, or a character fallback) and carries the scheme id. `Chunker.Split(note, bound)` builds chunks from sections and blocks; `Chunker.SplitPage` splits a PDF page. The server's new `ChunkTokenizer` (hosted service) downloads and verifies the tokenizer like `ModelFiles`, and `SearchIndex` exposes its bound to everyone who chunks (`ISearchIndex.Bound`). Stored embeddings gain a scheme and a chunk part, so old ones count as pending and are re-chunked.

**Tech Stack:** C# / .NET 10, xUnit v3 on Microsoft.Testing.Platform, MongoDB (Testcontainers or `KUMBU_TEST_MONGO`), GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-09-structural-chunking-design.md` (approved; not edited). Brief: `docs/superpowers/overnighter/2026-11-09-structural-chunking-brief.md`.

## Global Constraints

- British English in code, comments, docs, commit messages. Match surrounding naming and comment density. New C#/TS files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- No new NuGet or npm package. No organisation, customer or employer names anywhere (use Alice, Bob, `Work`, a pump manual, wild flowers).
- Commit messages imperative, free-form, no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- The tokenizer is arctic-m's `tokenizer.json` at revision `95c2741480856aa9666782eb4afe11959938017f`, 17,083,009 bytes, SHA-256 `f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec` — exactly `ModelFiles.Get("arctic-m").Files[0]`. It is verified by size and SHA-256 before use.
- Tests find a pre-fetched tokenizer through the environment variable `KUMBU_TOKENIZER` (a path to the file). Test hosts never download. Without the variable, tests that need the tokenizer skip with `Assert.SkipUnless(..., "KUMBU_TOKENIZER (arctic-m's tokenizer.json) not set")`.
- Thresholds live in one place, `Chunker`: `TargetWords = 250`, `MaxWords = 350`, `RollWords = 120`, `MinWords = 40`. Token budget: 512 tokens including `<s>` and `</s>`. Fallback: an embedding text of at most 500 characters (measured worst case 0.943 tokens per character, punctuation and symbols; see Task 1).
- Words = runs of Unicode letters and digits (`[\p{L}\p{N}]+`); punctuation and Markdown markup count nothing.
- Pre-roll and post-roll are never part of `EmbeddingText`.
- Untouched: encryption (embeddings stay inside the sealed payload), accounts, sessions, sharing, OAuth, pairing, WebDAV, Obsidian sync, export, publishing, `CallerMiddleware`, the sidecar. Don't fix the three known flaky tests.
- Don't merge `master` or `ingest-attachments`, don't tag, don't change the version. Subagents never push or switch branches.
- Test filter: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ClassName"` (server: `--project tests/Kumbu.Server.Tests`). `FullyQualifiedName~` doesn't work.

## Review Focus

1. **A single line of 10,000 characters without spaces** (a hex dump, base64): it must be split into pieces each within the token budget, every character in some core, and the pieces must have distinct keys (same start line, different `Part`). Test in Task 2.
2. **A very long heading path** (deep nesting with long headings): the context alone could eat the budget. The context is shortened to a quarter of the budget by words; the core still fits. Test in Task 2.
3. **Notes stored under the old scheme while the new index catches up**: they count as pending, the footer says so, and `--words` still finds them; PDFs of the old scheme stay findable by page. Test in Task 3.
4. **Duplicate start lines across PDF chunks and note pieces in `SectionSearch.Locate`**: the dictionary by `Key` must not throw on equal start lines with different parts. Test in Task 2 (note) and Task 4 (PDF).
5. **`semsearch -C 3 query`**: refused with the usage line, not a search for "3 query". Test in Task 5.

---

### Task 0: Baseline

**Files:** none changed.

- [ ] **Step 1:** Install what is missing (Node 26, pnpm 10.32.1), start Docker or a replica-set `mongod` as the brief says (`mongo:7.0` from `tests/Kumbu.Server.Tests/TestImages.cs`), fetch the tokenizer into a scratch folder and check its SHA-256.
- [ ] **Step 2:** Run, as CI does: `dotnet restore --locked-mode && dotnet build --configuration Release --no-restore && dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`; `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`.
  Expected (Chris' machine): .NET total 5333, succeeded 5327, failed 2, skipped 4; the failures among the known flaky three. A flaky one: run its class alone three times and record the result.
- [ ] **Step 3:** Record counts and failures in the report's Task 0 section.

**Phase gate 0:** baseline recorded.

---

### Task 1: The token bound, the tokenizer download and CI

**Files:**
- Modify: `src/Kumbu.Core/Search/UnigramTokenizer.cs:110-123` (add `Count`)
- Create: `src/Kumbu.Core/Search/ChunkBound.cs`
- Modify: `src/Kumbu.Core/Search/ISearchIndex.cs:37-43` (default member `Bound`)
- Create: `src/Kumbu.Server/Search/ChunkTokenizer.cs`
- Modify: `src/Kumbu.Server/Search/ModelFiles.cs` (a `ChunkTokenizer` file constant)
- Modify: `src/Kumbu.Server/Search/SearchIndex.cs:37-45,107-110` (optional `ChunkTokenizer`, wait for it, expose `Bound`)
- Modify: `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (`Bound => index?.Bound ?? ChunkBound.Fallback`)
- Modify: `src/Kumbu.Server/Program.cs:85-95` (register `ChunkTokenizer` as singleton and hosted service, before `SearchIndex`)
- Modify: `tests/Kumbu.Server.Tests/KumbuAppFactory.cs:65-75` (the tokenizer's HTTP client refuses every request)
- Modify: `.github/workflows/ci.yml` (build job: cached, hash-checked tokenizer; `KUMBU_TOKENIZER`)
- Test: `tests/Kumbu.Core.Tests/Search/ChunkBoundTests.cs`, `tests/Kumbu.Server.Tests/Search/ChunkTokenizerTests.cs`, `tests/Kumbu.Core.Tests/TestTokenizer.cs` (helper)

**Interfaces:**
- Produces (Core):
  - `public int UnigramTokenizer.Count(string text)` — the number of ids `Encode` would give without truncation, `<s>` and `</s>` included.
  - `public sealed class ChunkBound` with
    - `public const int MaxTokens = 512;` `public const int FallbackMaxChars = 500;`
    - `public static ChunkBound Fallback { get; }` — `Id == "fallback"`: a provider is active but the tokenizer couldn't be had.
    - `public static ChunkBound Unbounded { get; }` — `Id == "words"`, `Cost` 0, `Budget` `int.MaxValue`, `Fits` always true: no provider is active, nothing is embedded, so only the word limits apply (a server without search by meaning keeps chunks of about 250–350 words).
    - `public static ChunkBound Of(UnigramTokenizer tokenizer, string sha256)` — `Id == "tok-" + sha256[..12]`.
    - `public string Id { get; }`, `public string Scheme => $"{Chunker.SchemeVersion}/{Id}"` (Task 2 adds `Chunker.SchemeVersion = 2`; in Task 1 use a private const `2` in `ChunkBound` that Task 2 moves).
    - `public int Budget { get; }` — `MaxTokens - 2` with a tokenizer, `FallbackMaxChars + 1` for the fallback.
    - `public int Cost(string piece)` — tokens of the piece without `<s>`/`</s>` (memoised by string in a `ConcurrentDictionary<string, int>`, cleared at 100,000 entries), or `piece.Length + 1` for the fallback. Pieces are lines or space-separated parts of a line; costs add up exactly for arctic-m because it splits words at whitespace and a line break is its own grapheme.
    - `public bool Fits(string embeddingText)` — `tokenizer.Count(text) <= 512`, or `text.Length <= 500` for the fallback. The independent check tests use.
  - `ISearchIndex`: `ChunkBound Bound => ChunkBound.Fallback;` (default interface member, so fakes keep compiling).
- Produces (Server):
  - `public sealed class ChunkTokenizer(IOptions<KumbuOptions> options, IHttpClientFactory http, ILogger<ChunkTokenizer> log) : BackgroundService`
    - `public const string HttpClientName = nameof(ChunkTokenizer);`
    - `public ChunkBound Bound { get; }` — until settled: `Fallback`; settled: the tokenizer bound if a verified file is there or was downloaded; `Unbounded` when not `Needed` and no verified file is there; `Fallback` when `Needed` and the download failed.
    - `public bool Settled { get; }`, `public Task WhenSettledAsync(CancellationToken ct)` — completes once loaded or once it is clear there is none.
    - A verified file already in place is loaded even when not `Needed` (no download then). Downloads go through its own part file `tokenizer.json.chunking.part`, so it never races `OnnxEmbedder`'s `tokenizer.json.part`; both end with an atomic `File.Move(…, overwrite: true)` of a verified file.
    - `public static bool Needed(KumbuOptions o) => o.Search.Enabled || o.Embeddings.Providers.Count > 0;`
    - File: `Path.Combine(ModelFiles.Directory(o.Search.ModelPath, ModelFiles.Default), ModelFiles.Tokenizer)` — the same place the arctic-m download keeps it, so an existing verified file is reused and never downloaded twice.
  - `SearchIndex(VaultCatalog, EmbeddingProviders, ILogger<SearchIndex>, PdfAnalyses? analyses = null, ChunkTokenizer? tokenizer = null)`; `public ChunkBound Bound => _tokenizer?.Bound ?? ChunkBound.Fallback;`. `ExecuteAsync` awaits `_tokenizer.WhenSettledAsync` once, then runs `CatchUp` over every open vault (`VaultCatalog.OpenVaults`, a new `internal IReadOnlyList<UnlockedVault>` over `_open.Values`), because a vault unlocked before that was compared with the unsettled bound; then starts the per-provider loops.
  - `EncryptedNoteStore.SearchAsync`: while a provider is active and the tokenizer hasn't settled (`index.TokenizerSettled == false`), returns `SemanticState.Preparing` with the reason "search by meaning is getting ready (the tokenizer is loading)" instead of comparing schemes.

- [ ] **Step 1: Write the failing tests** in `ChunkBoundTests`:
  - `Count_is_Encode_without_the_cut`: with the real tokenizer (skip without `KUMBU_TOKENIZER`), for a German paragraph repeated to 3,000 characters, `tokenizer.Count(text) > 512` and `tokenizer.Encode(text, int.MaxValue).Count == tokenizer.Count(text)`, and for a short text `Count == Encode(text).Count`.
  - `Costs_of_lines_add_up_to_the_count`: for each fixture (German prose, a numeric table, code, a URL list — the measurement fixtures below), `2 + lines.Sum(bound.Cost) == tokenizer.Count(string.Join('\n', lines))`.
  - `The_fallback_counts_characters`: `ChunkBound.Fallback.Fits(new string('x', 500))` is true, 501 false; `Fallback.Budget == 501`; `Fallback.Cost("abc") == 4`; `Fallback.Id == "fallback"`.
  - `The_scheme_names_the_bound`: `ChunkBound.Of(tok, "f1cc44ad7faaeec4…").Scheme == "2/tok-f1cc44ad7faa"`, `Fallback.Scheme == "2/fallback"`.
  - `The_fallback_keeps_dense_text_under_the_limit` (real tokenizer): for 500-character prefixes of each dense fixture (numeric table, base64, punctuation, symbols "→∑≈€§¶•✓★♣", hex), `tokenizer.Count(prefix) <= 512`.
  `TestTokenizer.Load()` returns the tokenizer from `KUMBU_TOKENIZER` (cached), or null; tests call `Assert.SkipUnless(TestTokenizer.Load() is not null, "KUMBU_TOKENIZER (arctic-m's tokenizer.json) not set")`.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ChunkBoundTests"` → FAIL (types missing).
- [ ] **Step 3: Implement** `UnigramTokenizer.Count` (share the loop with `Encode`, no truncation) and `ChunkBound`. Comment the fallback with the measured numbers (from the run: punctuation 0.874, symbols 0.943, mixed "aé1|-_.ßäöü§→" 0.881, base64 0.781, hex 0.605, numeric table 0.636 tokens per character; German prose 0.26, English 0.21).
- [ ] **Step 4: Run** the same command → PASS (with `KUMBU_TOKENIZER` set; without it the tokenizer tests skip).
- [ ] **Step 5: Write the failing server tests** in `ChunkTokenizerTests` (no MongoDB needed; a temp `ModelPath`, a fake `IHttpClientFactory` whose handler serves given bytes and counts requests):
  - `Without_a_provider_nothing_is_downloaded`: `Search.Enabled = false`, no providers → settled, `Bound == ChunkBound.Fallback`, 0 requests.
  - `An_external_provider_gets_the_tokenizer`: `Search.Enabled = false`, one provider configured, handler serves the file from `KUMBU_TOKENIZER` (skip without) → settled, `Bound.Id == "tok-f1cc44ad7faa"`, 1 request to `https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v2.0/resolve/95c2741480856aa9666782eb4afe11959938017f/tokenizer.json`, the file is in `<ModelPath>/arctic-m/tokenizer.json`.
  - `A_file_already_there_is_checked_and_used`: the file copied into `<ModelPath>/arctic-m/` beforehand → 0 requests, tokenizer bound (skip without `KUMBU_TOKENIZER`).
  - `A_download_that_does_not_match_falls_back`: handler serves 17,083,009 zero bytes → settled, `Bound == ChunkBound.Fallback`, no `tokenizer.json` and no `.part` left.
  - `A_failed_download_falls_back`: handler returns 503 → settled, fallback.
  - `Without_a_provider_an_existing_file_is_still_used`: `Search.Enabled = false`, no providers, verified file in place → tokenizer bound, 0 requests; without the file → `Unbounded`.
  - `Its_part_file_is_its_own`: during a download (a handler that blocks until released) the file `tokenizer.json.chunking.part` exists and `tokenizer.json.part` doesn't.
  - `A_file_that_does_not_match_is_replaced`: a wrong file of the right size in place, handler serves the real bytes → tokenizer bound, file now matches.
- [ ] **Step 6: Run** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ChunkTokenizerTests"` → FAIL.
- [ ] **Step 7: Implement** `ChunkTokenizer`: follow `OnnxEmbedder.DownloadAsync` (`.part`, `IncrementalHash`, size and hash check, `File.Move`) and `ModelFiles.MatchesAsync`; no retry loop — a failure logs a warning ("search chunks by a character bound until the next start") and settles. Register it in `Program.cs`; `KumbuAppFactory` adds `services.AddHttpClient(ChunkTokenizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new NoDownload())` (a handler that throws `HttpRequestException`), sets `Kumbu:Search:ModelPath` to a per-factory temp folder and, when `KUMBU_TOKENIZER` is set, copies the file into its `arctic-m/` — so in CI the app-factory tests chunk with the tokenizer bound, locally without it `Unbounded`. `SearchIndex` takes it as an optional constructor parameter and waits for it; `EncryptedNoteStore` implements `Bound`.
- [ ] **Step 8: Run** both filters → PASS; then `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*SearchIndexTests"` → PASS (unchanged behaviour).
- [ ] **Step 9: CI.** In `.github/workflows/ci.yml`'s `build` job, before `dotnet test`: an `actions/cache` step pinned by commit SHA like the other actions (look the current release's commit up with `git ls-remote https://github.com/actions/cache` — don't guess), `path: ~/.cache/kumbu`, `key: tokenizer-f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec`; a `run` step that downloads on a cache miss with `curl -fsSL -o "$HOME/.cache/kumbu/tokenizer.json" <url>`, then always checks `echo "f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec  $HOME/.cache/kumbu/tokenizer.json" | sha256sum -c -` and writes `echo "KUMBU_TOKENIZER=$HOME/.cache/kumbu/tokenizer.json" >> "$GITHUB_ENV"`. Validate the YAML with `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml'))"`.
- [ ] **Step 10: Mutations**, each alone, watch a test fail, restore with `git checkout -- <file>`: (a) `Count` truncates at 512; (b) `Cost` returns `Count(piece)` (with specials); (c) skip the hash check in `ChunkTokenizer`; (d) `Needed` ignores external providers; (e) fallback limit 600.
- [ ] **Step 11: Commit** (the implementer), then the controller pushes; a spec-compliance review and a code-quality review follow, each by a fresh subagent, and their findings are fixed in a follow-up commit before the phase gate. The same holds for every task below. "Bound chunks by arctic-m's tokenizer, downloaded and verified like the model".

**Phase gate 1:** full verification as CI does (brief: restore locked, build Release, test Release; web and pdf suites).

---

### Task 2: The structural chunker for notes

**Files:**
- Modify: `src/Kumbu.Core/Search/Chunker.cs` (rewrite; keep the old algorithm as `Sections`)
- Create: `src/Kumbu.Core/Search/Blocks.cs` (Markdown blocks of a section)
- Modify: `src/Kumbu.Core/Search/SectionSearch.cs:59,107-126,182-205,217-223` (`Split(note, index.Bound)`, `Exact` on `Sections`, key with `Part`)
- Modify: `src/Kumbu.Core/Search/EarlierSearch.cs:43` (`Chunker.Sections`)
- Modify: `src/Kumbu.Server/Search/SearchIndex.cs:181` (`Chunker.Split(note, Bound)`)
- Modify: `tests/Kumbu.Core.Tests/Search/SearchTests.cs:15-37` (the two chunker tests move to `ChunkerTests` and are rewritten to the new rules)
- Test: `tests/Kumbu.Core.Tests/Search/ChunkerTests.cs`, fixtures `tests/Kumbu.Core.Tests/Search/ChunkerFixtures.cs` (generated strings: long German section, a 240-row error-code table of a pump, nested headings with many short sections, fenced code, a 10,000-character line, a 10,000-character line without spaces)

**Interfaces:**
- Consumes: `ChunkBound` (Task 1).
- Produces:
  - `Chunk` gains `public int Part { get; init; }` (0 unless one line is split into pieces: then 0, 1, … in order) and `Key => (Path, StartLine, Part)` of type `(VaultPath, int, int)`. `StartLine`/`EndLine` are the lines the core covers; a table part's repeated header rows lie before `StartLine`.
  - `public static class Chunker`: `SchemeVersion = 2`, `TargetWords = 250`, `MaxWords = 350`, `RollWords = 120`, `MinWords = 40`; `MaxChars` stays for `Sections`.
  - `public static List<Chunk> Split(Note note, ChunkBound bound)` — the new chunks.
  - `public static List<Chunk> Sections(Note note)` — today's `Split`, unchanged, for `rg`-style grouping (`SectionSearch.Exact`, `EarlierSearch`).
  - `public static int Words(string text)`.
  - `internal static class Blocks` with `record Block(BlockKind Kind, int Start, int End)` and `IReadOnlyList<Block> Parse(IReadOnlyList<string> lines, int start, int end, bool[] code)`; `BlockKind { Heading, Paragraph, List, Table, Code, Quote }`.

Algorithm (the part the tests don't determine):
1. Sections as today: the text before the first heading (address "", after frontmatter) and each heading's own lines up to the next heading of any level; blank lines trimmed at both ends.
2. Blocks within a section: the heading line; then runs of non-blank lines, with fenced code one block across blank lines (`Outline.CodeLines`), a GFM table starting at a line with `|` followed by a separator row (`^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$`) and running while lines contain `|`, a list starting at a list marker (`^\s*([-*+]|\d+[.)])\s`), a quote at `>`; a paragraph otherwise. A run that changes kind mid-way (a paragraph line followed directly by a table) starts a new block there.
3. A section within `MaxWords` and the budget is one unit. A longer section is packed greedily into parts of whole blocks up to `MaxWords` and the budget; a block that alone doesn't fit is split into pieces: a table into groups of whole rows, each part's text starting with the header and separator rows; a list between items (a line at the list's first indentation with a marker); code, paragraphs and quotes between lines; a single line that doesn't fit, at spaces (same `StartLine`, `Part` 0, 1, …), and a run without spaces at the longest prefix with `Cost(context) + Cost(prefix) ≤ Budget` (binary search; the token count isn't strictly monotonic, so the result is checked with `Fits(EmbeddingText)` and shortened by one character while it doesn't fit). A single table row over the budget is split like a line. When the context plus the header and separator rows leave no room for one row, the table's parts go without the repeated header.
4. The budget check of a candidate: `bound.Cost(context) + Σ bound.Cost(line) ≤ bound.Budget`, over every line of the candidate's text (header rows included). Words: `Words` of the text.
5. Merging: only **adjacent** sections that each stayed one unit merge; a split section ends the group, and a section that has child sections always starts a new group (so a heading-only parent stays with its children). Greedily, a group takes the next section while (a) the group's words plus the next's stay within `TargetWords`, or one of them is under `MinWords` and the sum within `MaxWords`; (b) the budget holds; (c) the structure allows: let H be the deepest heading that is the ancestor-or-self of every section in the group (the root for none); every section after the first has H as its parent, and the first is H itself or has H as its parent. A merged chunk's address is H's address ("" at the root), its context the title and H's heading path, its lines from the first section's first line to the last section's last.
6. Context: `title > heading path` as today. If `bound.Cost(context) > bound.Budget / 4`, keep its leading words up to that.

- [ ] **Step 1: Write the failing tests** in `ChunkerTests` (each runs with `ChunkBound.Fallback` and, where marked, with the tokenizer bound via `TestTokenizer`, skipping without it):
  - `Every_line_is_in_exactly_one_core` (both bounds, every fixture): the line ranges `[StartLine, EndLine)` of the chunks with `Part == 0` don't overlap; every non-blank line after the frontmatter is inside one; for pieces of one line, their texts concatenated with single spaces equal the trimmed line (whitespace runs aside), and for the no-space line, concatenated without separator.
  - `No_embedding_text_goes_over_the_budget` (both bounds, every fixture): `bound.Fits(c.EmbeddingText)` for all, and with the tokenizer `tok.Count(c.EmbeddingText) <= 512`.
  - `A_table_that_fits_stays_whole`: a 10-row table in a section → one chunk whose text contains all rows.
  - `A_long_table_is_split_between_rows_with_its_header` (tokenizer bound): the 240-row table → several chunks, each text's first non-heading lines are the header row and the separator row, each row in exactly one chunk's range, no row cut, `Address == "#Pump P-200#Error codes"`, context `Manual > Pump P-200 > Error codes`, and the second part's `StartLine` is the line of its own first row.
  - `Short_siblings_merge_under_their_parent`: the spec §3.9 note (`# Pump P-200`, `## Safety`, `## Error codes` with the 240-row table, `## Maintenance`, `### Weekly`, `### Monthly`) → the heading-only `# Pump P-200` and *Safety* form one chunk with address `#Pump P-200` and lines 1-3 (1-based, the heading to Safety's last line); *Error codes* is split; `## Maintenance`, *Weekly* and *Monthly* form one chunk with address `#Pump P-200#Maintenance`, lines from `## Maintenance` to the last line. (The spec's example has *Safety* alone; with its heading-only parent before it, the rule of §3.3 puts the two together — noted in the report.)
  - `A_split_section_ends_a_group`: a short section, a split one, a short one, all siblings → the two short ones aren't merged across the split one; no ranges overlap.
  - `A_table_whose_header_leaves_no_room_goes_without_it` (fallback bound): a table whose header and separator rows are 480 characters → every chunk fits, every row covered.
  - `Merging_never_crosses_a_parent`: `# A\n## A1\nx\n## A2\ny\n# B\n## B1\nz` → no chunk spans from A's subtree into B's.
  - `Small_sections_merge_up_to_the_target`: 30 sections of 20 words under one parent → chunks of ≤ 250 words, more than one chunk, fewer than 30.
  - `A_long_German_section_splits_between_paragraphs` (tokenizer bound): every chunk starts at a block boundary.
  - `Fenced_code_splits_between_lines_only`: no chunk starts inside a code line; a code block that fits stays whole.
  - `A_line_of_10000_characters_is_split_at_spaces` and `A_run_without_spaces_is_split_within_the_budget` (both bounds): several chunks, same `StartLine`, `Part` 0..n−1, distinct keys.
  - `A_long_heading_path_leaves_room_for_the_core`: nesting 6 levels deep with 80-word headings → every chunk fits, every line covered.
  - `The_text_before_the_first_heading_is_a_chunk` and the old `ChunksAreSectionsWithTheirHeadingPath` expectations (for that small note the result is one merged chunk with address "" — rewrite the assertion to the new rule and say so in a comment).
  - `Sections_is_the_old_split`: the two old `SearchTests` chunker tests, now against `Chunker.Sections`.
  - `SearchTests.Snippet_of_a_table_part_keeps_its_header_row`: `Snippet` skips the first line only when it is a heading.
  - `NoteApi` (web search, `SearchApiTests` or the class `rg -l "api/search" tests/Kumbu.Server.Tests` names): `Pieces_of_one_line_list_once` — results are unique by (path, start line), keeping the best (the web app keys rows by path, address and start line, `web/src/search/SearchSheet.tsx:135`).
  - `Locate_finds_a_piece_by_its_part` (in `SearchTests`, through `SectionSearch.RankAsync` with the existing `FakeIndex`): two hits on the same start line with parts 0 and 1 (needs `SemanticHit.Part`, see Task 3 — in this task construct hits with Part 0 only and assert `RankAsync` doesn't throw on a note whose long line was split).
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ChunkerTests"` → FAIL.
- [ ] **Step 3: Implement** `Blocks`, then `Chunker.Split`, `Sections`, `Words`; switch `SectionSearch.RankAsync` to `Chunker.Split(note, index.Bound)` (once per call: read `index.Bound` once), `Exact` and `EarlierSearch` to `Chunker.Sections`, `SearchIndex.IndexAsync` to `Chunker.Split(note, Bound)`. `RankByMeaning`/`Locate` key by `(Path, StartLine, Part)`, the dictionary built with `TryAdd`; a hit carries part 0 until Task 3. `Snippet` skips the first line only if it is a heading. `NoteApi`'s search keeps the first result per (path, start line).
- [ ] **Step 4: Run** `dotnet test --project tests/Kumbu.Core.Tests` → PASS, and `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*SearchIndexTests" --filter-class "*SemsearchTests" --filter-class "*SearchApiTests"` (whatever classes `rg -l "semsearch|SearchView" tests/Kumbu.Server.Tests` names) → PASS; the classes: `rg -l "semsearch|SectionSearch|api/search|SearchView" tests/Kumbu.Server.Tests` lists them, run each with its own `--filter-class`. A server test that asserted old chunk boundaries is updated on purpose, with the reason in the commit message.
- [ ] **Step 5: Measure search-time cost** (brief 3a): a scratch program (not committed) builds 1,000 notes from the fixtures (mixed sizes, 2–20 KB), times `SectionSearch.RankAsync` (hybrid, with a fake index giving a cosine per chunk) on the parent commit vs this one, and `Chunker.Sections` (the old split) vs `Chunker.Split` with the fallback and with the tokenizer bound, cold and warm (memo filled), median of 5. If warm `Split` with the tokenizer exceeds 3× the old split, tokenise only lines whose candidate is near the budget (skip `Cost` while `Σ(line.Length + 1) + context.Length ≤ Budget / 2` for the tokenizer, a bound measured on the fixtures) and measure again; if a warm search is still over 3× the old one, cache each note's chunks in a `ConditionalWeakTable<string, …>` keyed by the content string, checked against path and bound id. Numbers go to the report.
- [ ] **Step 6: Mutations**, each alone: (a) drop the header rows from table parts; (b) allow merging across parents (skip rule 5c); (c) ignore the budget in packing (words only); (d) leave out the last piece of a split line; (e) count `|` and `-` as words; (f) give all pieces of a line `Part = 0`.
- [ ] **Step 7: Commit** "Chunk notes by their structure: sections, blocks and whole tables within the token budget".

**Phase gate 2:** full verification.

---

### Task 3: Scheme version, chunk parts and re-indexing

**Files:**
- Modify: `src/Kumbu.Server/Vaults/UnlockedVault.cs:265-280,642-649` (payload and records)
- Modify: `src/Kumbu.Core/Search/ISearchIndex.cs:19` (`SemanticHit.Part`)
- Modify: `src/Kumbu.Server/Search/SearchIndex.cs:100-105,181,224-271` (`IsCurrent` with scheme, store scheme and parts)
- Modify: `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs:604-636` (pending by scheme, parts in hits)
- Modify: `src/Kumbu.Core/Search/SectionSearch.cs` (`Locate` uses the hit's part)
- Modify: `README.md` (one line: after the update, vaults on an external provider send their text once more)
- Test: `tests/Kumbu.Server.Tests/Search/SearchIndexTests.cs`, `tests/Kumbu.Server.Tests/Vaults/` (the class that tests sealing of embeddings: `rg -l "SealEmbeddings|OpenEmbeddings" tests`)

**Interfaces:**
- Consumes: `ChunkBound.Scheme` (Task 1), `Chunk.Part`, `Chunker.Split` (Task 2).
- Produces:
  - `EmbeddedChunk(int StartLine, string Hash, float[] Vector, int Part = 0)`; `NoteEmbeddings(string Model, string Version, IReadOnlyList<EmbeddedChunk> Chunks, string? Scheme = null)`.
  - Payload: `EmbeddedChunkPayload(int StartLine, string Hash, string Vector, int Part = 0)`, `EmbeddingsPayload(string Path, string Version, List<EmbeddedChunkPayload> Chunks, string? Model = null, string? Scheme = null)`, no attributes: System.Text.Json fills a missing constructor parameter with its default, so old payloads open with `Scheme == null`, `Part == 0` (the round-trip test proves it).
  - `SemanticHit(VaultPath Path, int StartLine, double Score) { public int Part { get; init; } }`.
  - `SearchIndex.IsCurrent` also requires `embeddings.Scheme == Bound.Scheme`; `EmbedAsync` takes `(int StartLine, int Part, string Text)` chunks and stores `Bound.Scheme`; its "unchanged" check compares `(StartLine, Part, Hash)`.
  - `EncryptedNoteStore.SearchAsync`: a note whose embeddings have another scheme counts as pending, adds no hits, is enqueued (as a wrong vector length is, `EncryptedNoteStore.cs:615`) and is listed in `SemanticResult.StaleNotes` (`IReadOnlyList<VaultPath>`, caller paths, init property, default empty). A PDF of the current version but another scheme keeps its hits (by page, part 0), is enqueued, and doesn't count as pending (the footer's "semsearch --words finds them" isn't true of PDFs).
  - `SectionSearch.RankAsync`: the chunks of `StaleNotes` are ranked by words and fused with the meaning list by `FuseByRank`, as `WordsOnlyVaults` are, so notes being re-chunked stay findable in a hybrid search. Update `SemanticResult.Pending`'s doc comment.

- [ ] **Step 1: Write the failing tests:**
  - sealing class: `Embeddings_keep_their_scheme_and_parts` (seal and open round trip keeps `Scheme` and `Part`); `Embeddings_before_the_scheme_open_without_one` (a payload JSON without `Scheme`/`Part` opens with null and 0).
  - `SearchIndexTests`: `A_note_of_an_older_scheme_is_chunked_anew` (store embeddings with `Scheme = null` for a note at its current version, catch up → re-indexed, `Scheme == index.Bound.Scheme`); `Unchanged_texts_keep_their_vectors_across_schemes` (a counting fake driver: after the scheme change only chunks whose embedding text changed are embedded; a short note whose single chunk text is unchanged embeds nothing); `Older_scheme_notes_are_pending_until_re_indexed` (`SearchAsync` before the catch-up reports `Pending == n`, no hits for them and their paths in `StaleNotes`, after `IdleAsync` 0); `A_PDF_of_an_older_scheme_stays_findable_by_page` (hits for its pages, not counted as pending, re-indexed after `IdleAsync`); `A_note_stored_under_the_fallback_is_re_chunked_once_the_tokenizer_settles` (stored with `2/fallback`, the vault unlocked before the tokenizer settles → after settling it is re-indexed under the tokenizer scheme). In `SearchTests`: `Stale_notes_are_ranked_by_words_meanwhile` (`FakeIndex` with `StaleNotes = [p]` → a word match in `p` is listed).
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*SearchIndexTests"` (and the sealing class) → FAIL.
- [ ] **Step 3: Implement** the records, payload, `IsCurrent`, `EmbedAsync`, `SearchAsync`, `Locate` (`(hit.Path, hit.StartLine, hit.Part)` first, then the chunk containing the start line); README line under the search section.
- [ ] **Step 4: Run** the same filters plus `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*SearchTests"` → PASS; add `Locate_finds_a_piece_by_its_part` with two parts now.
- [ ] **Step 5: Mutations**, each alone: (a) `IsCurrent` ignores the scheme; (b) `SearchAsync` adds hits of old-scheme notes; (c) old-scheme PDFs dropped; (d) `Part` not sealed; (e) "unchanged" check ignores `Part`.
- [ ] **Step 6: Commit** "Re-chunk stored embeddings through a scheme version, keeping vectors of unchanged texts".

**Phase gate 3:** full verification.

---

### Task 4: PDF pages split by words

**Files:**
- Modify: `src/Kumbu.Core/Search/Chunker.cs` (`SplitPage`)
- Modify: `src/Kumbu.Server/Attachments/PdfAnalyses.cs:160-218` (`SearchTextAsync` without `maxChars`)
- Modify: `src/Kumbu.Server/Search/SearchIndex.cs:195-216` (pages into chunks with parts)
- Modify: `src/Kumbu.Core/Search/SectionSearch.cs:168-182` (`WithPages`: best chunk per page, keeps its part)
- Modify: `tests/Kumbu.Server.Tests/Attachments/PdfAnalysisStoreTests.cs:313` (call without the cut; assert a page longer than 2,000 characters comes whole)
- Test: `tests/Kumbu.Core.Tests/Search/ChunkerTests.cs` (page tests), `tests/Kumbu.Server.Tests/Search/SearchIndexTests.cs` (PDF tests)

**Interfaces:**
- Consumes: `ChunkBound`, `Chunker` thresholds.
- Produces:
  - `public sealed record PageChunk(int Part, string Core, string PreRoll, string PostRoll)`; `public static IReadOnlyList<PageChunk> Chunker.SplitPage(string pdfTitle, int page, string text, ChunkBound bound)` — context `"{pdfTitle} > p{page}"`; embedding text `context + "\n" + Core`; lines packed greedily within `MaxWords` and the budget, cutting at the last blank line of the candidate when it lies in its second half, else at the line; a line that doesn't fit split as in Task 2. Rolls: up to `RollWords` words of whole lines before and after, within the page, at most 1,000 characters each side.
  - `PageChunk.EmbeddingText(string pdfTitle, int page)` or an equivalent helper used by indexing.
  - `SectionHit.PagePart` (`int?`, set with `Page`).

- [ ] **Step 1: Write the failing tests:**
  - `A_dense_page_yields_several_chunks_covering_its_text` (both bounds): a 9,000-character page of German prose and numbers → ≥ 3 chunks; the cores joined with a space, whitespace runs collapsed, equal the page's text with whitespace runs collapsed (also for a page without line breaks); each `EmbeddingText` fits the budget.
  - `Rolls_stay_within_the_page`: the first chunk's `PreRoll` and the last chunk's `PostRoll` are empty; a middle chunk's rolls are text of the same page, ≤ 120 words each, never part of `Core`.
  - `A_short_page_is_one_chunk`.
  - `SearchIndexTests.A_dense_PDF_page_is_embedded_in_parts`: through `FakePdfSidecar` an analysis with a 9,000-character page → embeddings with `StartLine == page` and parts 0..n−1, and a hit on the last part prints as that page.
  - `SearchTests.One_hit_per_PDF_page_with_its_best_part`: `FakeIndex` hits (page 3, part 0, 0.4) and (page 3, part 2, 0.7) → one `SectionHit` with `Page == 3`, `PagePart == 2`, score 0.7.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ChunkerTests" --filter-class "*SearchTests"` → FAIL.
- [ ] **Step 3: Implement** `SplitPage`, `SearchTextAsync(vault, blobId, ct)` (drop the cut and its `cut` array; the doc comment says each page's whole text is held), `IndexPdfAsync` using `SplitPage(title, page, text, Bound)`, `WithPages` keeping the best part.
- [ ] **Step 4: Run** the Core filters and `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*SearchIndexTests" --filter-class "*PdfAnalysisStoreTests"` → PASS.
- [ ] **Step 5: Mutations**, each alone: (a) keep the 2,000-character cut; (b) rolls cross into the core; (c) drop the last chunk of a page; (d) `WithPages` keeps the first part instead of the best.
- [ ] **Step 6: Commit** "Split PDF pages into chunks by words instead of cutting them".

**Phase gate 4:** full verification.

---

### Task 5: Rolls and what `semsearch` prints

**Files:**
- Modify: `src/Kumbu.Core/Search/Chunker.cs` (`Rolls`)
- Modify: `src/Kumbu.Core/Sh/Commands/Semsearch.cs` (default, `-C`, `-l`, budget, help)
- Modify: `tests/Kumbu.Core.Tests/Sh/ArchiveHiddenTests.cs:307,314` and every test asserting the usage line (`rg -n "semsearch \[" tests`)
- Modify: `web/src/console/paths.ts:64` only if needed (`-C` and `-l` take no value, so `values` stays `["-n", "--max", "--tag"]`); `web/src/console/console-fixture.json` if `ConsoleFixtureTests` asks for it
- Test: `tests/Kumbu.Core.Tests/Sh/SemsearchTests.cs` (or the existing semsearch test class: `rg -l "new Semsearch" tests`), `tests/Kumbu.Core.Tests/Search/ChunkerTests.cs`

**Interfaces:**
- Consumes: `Chunker.Split`, `SplitPage`, `PageChunk`, `SectionHit.PagePart`, `ISearchIndex.Bound`.
- Produces:
  - `public static (string Before, string After) Chunker.Rolls(Note note, Chunk chunk)`: the enclosing section is the parent of the chunk's address heading (the note for none); before = its lines from its start to the chunk's start (for a table part: before the repeated header); after = from the chunk's end to its end; each taken whole if within `RollWords` and 1,000 characters, else the nearest whole blocks, then lines, up to the budget; blank lines trimmed.
  - `semsearch` options: `new('C', "context")`, `new('l', "list")`. Usage: `semsearch [-n N] [-C|-l] [--words|--meaning] [--tag T] [--archive] <query> [path]`.
  - Output, default: header line as today (`N. path#Address  lines a-b  score s`, `N. path pN  score s`), then every line of the core indented by three spaces. `-C`: the rolls' lines around the core, each prefixed `   ┊ `. `-l`: header lines only. Budget: `OutputLimit.MaxChars - 1_000`; a hit whose core no longer fits prints its header line only, and before the footer the line `(hits K-N without their text, to fit the output; cat "path#Section" or pdf path N reads one)`. `-C` and `-l` exclude each other (`semsearch: -C and -l exclude each other`). A number right after `-C` (`-C 3`, `-C3`) → `ShellResult.Error` with the usage line, checked on the raw `args` before `Options.TryParse` (an argument `-C` followed by an all-digit argument, or `-C` followed by digits in one argument, before any `--`).
  - A PDF hit's core: `SplitPage(title, page, text, index.Bound)[PagePart]` of the page's text from `pixels.PageTextAsync`; a part beyond the count falls back to part 0.

- [ ] **Step 1: Write the failing tests:**
  - `ChunkerTests.Rolls_are_the_rest_of_the_enclosing_section` and `Rolls_of_a_long_table_part_are_neighbouring_rows` (the second part's before ends with the first part's last row, its after starts with the third part's first row, each ≤ 120 words) and `Rolls_are_never_embedded` (no roll line is in `EmbeddingText`, except lines that also occur in the core such as the header).
  - Semsearch: `Prints_the_whole_core_of_each_hit`; `With_C_it_prints_the_rolls_marked` (every roll line starts with `   ┊ `, core lines don't); `With_l_it_prints_one_line_per_hit`; `C_takes_no_number` (`-C 3 pump` and `-C3 pump` → error containing the usage line); `C_and_l_exclude_each_other`; `Cores_that_do_not_fit_fall_back_to_one_line` (30 hits of 2,000-character cores with `-n 30` → output ≤ `OutputLimit.MaxChars`, the footer present, the "without their text" line names the right hit numbers); `A_PDF_hit_prints_its_best_part` (`FakePixels` with a 9,000-character page; hit part 2 → the core of part 2); `Help_documents_C_and_l`.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*SemsearchTests" --filter-class "*ChunkerTests"` → FAIL.
- [ ] **Step 3: Implement** `Rolls`, the options, the output and the help in `Manual.cs`'s voice (short, literal). Help text (replace the paragraph from "Prints the best sections" to "pdf path 7 reads it"):
  ```
  Prints the best sections (-n, default 10): path#Section with their lines and score, then the
  section's whole text. A short section is often merged with its neighbours under their parent
  heading: path#Section then names that parent, and the lines are exact. Scores are comparable
  within one search, not across searches. cat "path#Section" reads the whole section.
  -C        also print the text around each hit, its lines marked with ┊; context, not part of the match
  -l        one line per hit: path, lines and score
  ```
  and keep the PDF, archive and tag sentences. Update the usage-line tests on purpose.
- [ ] **Step 4: Run** the filters, `dotnet test --project tests/Kumbu.Core.Tests`, `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ConsoleFixtureTests"`, `pnpm -C web test --run src/console` → PASS.
- [ ] **Step 5: Mutations**, each alone: (a) print the snippet instead of the core; (b) no `┊` on roll lines; (c) no budget (print all cores); (d) accept `-C 3`; (e) PDF hit shows part 0 always.
- [ ] **Step 6: Commit** "Let semsearch show each hit's whole text, with -C for its context and -l for a list".

**Phase gate 5:** full verification.

---

### Task 6: End to end, README and help texts

**Files:**
- Test: `tests/Kumbu.Server.Tests/Search/StructuralChunkingEndToEndTests.cs`
- Modify: `README.md` (search section: chunks follow the structure; the tokenizer download, 17 MB, with any provider active; `KUMBU_TOKENIZER` is for tests and CI only — in the contributor/test part)
- Modify: `docs/concept.md` §6 (one paragraph: structure, words, token ceiling, rolls)
- Modify: `src/Kumbu.Core/Sh/Manual.cs` (only if the overview line of `semsearch` needs it)

**Interfaces:** consumes everything above.

- [ ] **Step 1: Write the tests:**
  - With the app factory's hashing fake embedder (runs in CI): `A_row_near_the_end_of_a_long_table_is_found_with_its_header` (write `/Work/Pump/Manual.md` with the 240-row table, wait for the index, `semsearch sensor drift recalibrate /Work` → first hit's core contains `| Code | Meaning | Remedy |` and `E241`); `A_sentence_at_the_end_of_a_dense_page_is_found` (a PDF through `FakePdfSidecar` with a 9,000-character page whose last paragraph names wild flowers → `semsearch` finds `pN` and prints that paragraph); `The_same_content_written_into_a_note_is_found` (the page's text as a note, found by its last sentence).
  - With the real model (`KUMBU_MODEL_DIR`, skipped otherwise, like `SearchIndexTests.The_real_model_ranks_by_meaning`): the same three by meaning, with a paraphrased query.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*StructuralChunkingEndToEndTests"` → PASS (the real-model tests skip here).
- [ ] **Step 3:** README and concept §6 text; check that no organisation names slipped in (`rg -i` over the diff for anything but Alice, Bob, Work, pump, flowers).
- [ ] **Step 4: Mutation:** break the header rows of table parts again → `A_row_near_the_end…` fails; restore.
- [ ] **Step 5: Commit** "Test structural chunking end to end and document it".

**Phase gate 6:** full verification.

---

### Task 7: Pull request and report

- [ ] Push `feature/structural-chunking`. Open a **draft** pull request, base `master`, title "Structural chunking: sections that stay whole, tables included"; body: depends on #176 (and shows its commit until merged), the decisions made without Chris, the tokenizer download as the only new outside access, ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- [ ] Add a row with the pull request's link to `docs/superpowers/open-items.md`.
- [ ] Last commit: `docs/superpowers/overnighter/2026-11-09-structural-chunking-report.md` as the brief lists.
- [ ] **Do not merge, tag or deploy.** Notify via `PushNotification`.

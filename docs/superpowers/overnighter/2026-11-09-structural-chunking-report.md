# Overnighter report: structural chunking

Run of 2026-11-09, from the brief `2026-11-09-structural-chunking-brief.md`. The result is the draft pull request **https://github.com/SCAI-NGO/kumbukumbu/pull/177**, "Structural chunking: sections that stay whole, tables included", with base `master`. I didn't merge, tag or deploy it, and I didn't change the version.

All six tasks of the plan are done, along with a fix wave from the final whole-branch review. No task is left.

## Environment

- **Machine:** a cloud container with 4 cores, 15 GB of RAM and no swap, running Linux 6.18.
- **.NET:** SDK 10.0.403, rolled forward from `global.json`'s 10.0.100.
- **Node and pnpm:**
  - **Node:** 22 was installed, so I installed **Node 26.13.0** by hand into `/opt/node26`, from nodejs.org with its SHA-256 checked.
  - **pnpm:** **10.32.1**, installed with `npm install -g`.
- **Docker:**
  - **Daemon:** it wasn't running at the start, so I started `dockerd` by hand.
  - **MongoDB:** a container of my own, `mongo:7.0` as a single-node replica set, reached through `KUMBU_TEST_MONGO`.
  - **Open-file limit:** the hard limit is 20,000 open files. The first baseline run crashed mongod mid-run with "Too many open files" (a WiredTiger panic). The cure was `--wiredTigerEngineConfigString "file_manager=(close_idle_time=15,close_scan_interval=5,close_handle_minimum=200)"`, which keeps idle file handles closed. After that, every full run passed through.
  - **Fresh container:** each full verification started a new container (`docker rm -fv` first).
- **huggingface.co was reachable.**
  - **Tokenizer:** arctic-m's `tokenizer.json` was fetched once; 17,083,009 bytes, with its SHA-256 `f1cc44ad…a803ec` checked. Every run used it through `KUMBU_TOKENIZER`.
  - **Model:** for the end-to-end tests, the Task 6 implementer also downloaded arctic-m's model and checked its SHA-256. The real-model tests then ran here too; see "Tests with the real model".
- **Nothing was added to the project:** no NuGet package and no npm package.
- **Process:**
  - The superpowers skills were available, and I used `writing-plans` and `subagent-driven-development`.
  - Every task had a fresh implementer subagent, then a spec-compliance reviewer and a code-quality reviewer, each a fresh subagent.
  - Each fix round got a scoped re-review, and the branch got a final whole-branch review and one fix wave after it.
  - Implementers committed. I pushed after every task and fix round.
- **Phase gates:** each gate ran in its own detached worktree (`/tmp/claude-0/gate-wt`) at the task's commit, so a running implementer couldn't change the code under test.

## Plan review

A fresh subagent reviewed the plan against the spec, the brief and the code. It made 15 findings, and I worked all of them into the plan before Task 1 (commit `0a25012`).

The main ones:

1. **Two downloads of one file at once.** `ChunkTokenizer` and `OnnxEmbedder` both write `arctic-m/tokenizer.json`. Fix: the tokenizer gets its own part file, `tokenizer.json.chunking.part`, and both downloads end in an atomic move of a verified file.
2. **A scheme that goes stale.** A vault unlocked before the tokenizer settles was compared against the wrong bound, and fallback-scheme notes would never be re-chunked. Fixes:
   - after settling, the indexer catches up every open vault;
   - a search re-queues stale notes;
   - while a provider is active but the tokenizer hasn't settled, search reports "getting ready" instead of comparing schemes.
3. **Stale PDFs counted as pending.** The footer's "semsearch --words finds them" would be false for PDFs. Fix: they keep their page hits and aren't counted.
4. **Notes vanishing from hybrid search while they are re-indexed.** Fix: stale notes are ranked by words and fused by rank, like `WordsOnlyVaults`.
5. **Gaps in the merging rule.** Fixes: only adjacent sections merge, a split section ends a group, and a section with children starts a new one. The expectations for the §3.9 example are spelled out.
6. **The no-space cut ignored the context.** Fix: a binary search on context plus prefix, then a check with `Fits`.
7. **Tables.** Fixes: a single row over the budget is split like a line, and a header with no room for a row is dropped.
8. **The fallback reached too far.** Servers with no provider would get 500-character chunks for nothing. Fixes:
   - `Unbounded` (words only) when no provider is active;
   - an existing verified file is used even when it isn't needed;
   - the app factory copies `KUMBU_TOKENIZER` in, so CI's server tests run with the tokenizer bound.
9. **JSON attributes on positional records were wrong.** Fix: no attributes; defaults fill missing fields.
10. **Duplicate React keys** in the web search for the pieces of one line. Fix: the API de-duplicates by path and start line.
11. **CI step quoting** (`~` inside quotes, `env:` not expanded). Fix: `$HOME` and `$GITHUB_ENV`.
12. **`-C 3` / `-C3`.** Fix: the raw arguments are checked before option parsing.
13. **The PDF coverage test assumed line breaks.** Fix: it compares with whitespace normalised.
14. **Process.** Fixes: push after every task, and two reviewers per task. The brief's search timing (3a) is measured on `RankAsync`, not only on `Split`.
15. **Smaller items:** `TryAdd` for `byKey`, the `Pending` doc comment, and `Snippet` on table parts.

Its scheme id `tok-f1cc44ad7fae` was a typo of mine in the plan. `sha256[..12]` is `tok-f1cc44ad7faa`, which I corrected in `959a270`.

## Task 0: baseline, planned against measured

Baseline at `0a25012`, which is `9e919a7` plus the plan; only docs differ.

| | Chris' machine (planned) | Measured here |
|---|---|---|
| .NET total | 5333 | 5333 |
| succeeded | 5327 | 5326 |
| failed | 2 (known flaky) | 1 |
| skipped | 4 | 6 |
| web | not measured | 115 files, 1863 passed; build ok |
| pdf | not measured | 9 files, 198 passed; build ok |

- **Skips:** the two extra skips come from running as root, as the brief expects: `SeedScannerTests.Skips_unreadable_files_with_a_warning` and `ModelFilesTests.AReadOnlyModelFolderTurnsSearchByMeaningOffInsteadOfStoppingTheServer`.
- **The one failure:** `RekeyTransitionTests.A_run_cancelled_in_its_transaction_completes_the_transition(point: "inside-finish")`, after 1 min 01 s. That run had a subagent building beside it. The class alone, run 3 times on the base: 12/12, 12/12 and 12/12. It isn't one of the three known flaky tests, but it behaves like them under load.
- **Timing:** Task 0 took a little over an hour, not 15 minutes. That includes starting Docker, installing Node 26, and one aborted run while mongod ran out of file handles.

## Tasks, commits and test output

Every gate is the full CI sequence:
1. `dotnet restore --locked-mode`;
2. `dotnet build --configuration Release --no-restore`;
3. `dotnet test --configuration Release --no-build`, with `KUMBU_TOKENIZER` set;
4. the web app: install, `pnpm test --run` and `pnpm build`;
5. the PDF sidecar: install, `pnpm test` and `pnpm build`.

| Task | Commits | Gate result (.NET total / succeeded / failed / skipped; web; pdf) |
|---|---|---|
| 1 Token bound, tokenizer download, CI | `4d23085`, fix `126f06f` | 5354 / 5348 / 0 / 6; 1863 ok, build ok; 198 ok, build ok |
| 2 Structural chunker | `dc045ea`, `05eed24`, fixes `bcd6b38` and `7036640` | 5438 / 5431 / 0 / 7; 1863 ok, build ok; 198 ok, build ok |
| 3 Scheme version, parts, re-indexing | `346c243` | 5448 / 5440 / 1 / 7 (`MembershipApiTests.Last_use_is_the_use_on_this_vault_not_the_keys_global_one`, known flaky; class alone 3×: 26/26 each); 1863 ok; 198 ok |
| 4 PDF pages split by words | `88744b9`, fix `a1947d7` | 5466 / 5458 / 1 / 7 (the same known flaky test); 1863 ok, build ok; 198 ok, build ok |
| 5 Rolls and semsearch output | `312ea36`, fix `96e9bfb` | gate invalid: a process was OOM-killed while the real-model tests ran beside it (total 4613); superseded by the next gate |
| 6 End to end, README, docs | `1f6a149`, fix `4d2e5d2` | 5514 / 5503 / 1 / 10 (`PairingTests.A_code_redeemed_twice_at_once_makes_one_key`, which ran beside the fix-wave implementer; class alone 3×: 55/55 each); 1863 ok, build ok; 198 ok, build ok |
| Final review fixes | `729ed4d`, `9bffdb3`, `ba0cda1`, `6323ef4`, `32bc9a0` | 5524 / 5514 / 0 / 10; 1863 ok, build ok; 198 ok, build ok |

- **Skipped tests:** 10 at the end: the 4 of the base, 2 because of root, 3 new real-model end-to-end tests, and 1 arctic-l count test.
- **Every task's implementer report** has the RED and GREEN runs: compile failures or stubs for RED, then the focused classes green with and without `KUMBU_TOKENIZER`.
- **The reviews' findings and fixes:**
  - Task 1: download cap, and test hosts kept offline.
  - Task 2: header per table part, a cache per note, Blocks GFM checks, and then cheap exact counting with a warm-up.
  - Task 4: a long PDF name crashed indexing.
  - Task 5: the writer notice outside the budget, and page text identical for indexing and display.
  - Task 6: wording only.
  - Task 3: its reviews found nothing Important.

### Tests with the real model

The Task 6 implementer downloaded arctic-m, and I ran these with `KUMBU_MODEL_DIR`:
- `UnigramTokenizerTests`: 11 passed, 2 skipped (arctic-l), including `MatchesHuggingFaceOnTheRealVocabulary`.
- `SearchIndexTests` plus `StructuralChunkingEndToEndTests`: 35/35 passed. That includes `The_real_model_ranks_by_meaning` and the three real-model end-to-end tests: a paraphrased query finds the end of a dense page, a table row with its header, and the same content as a note.

## Decisions made without Chris

Each was settled by the brief's rules: breaks nothing, true to the application, no new security hole, true to the spec.

1. **Which tokenizer bounds the chunks: always arctic-m's**, whichever model or provider embeds.
   - The alternative was the active model's own tokenizer, which is arctic-l's for arctic-l.
   - Fixed boundaries don't move when the admin switches models.
   - A file the arctic-m model download already holds is reused.
2. **How the bound reaches Core:** `ChunkBound` in `Kumbu.Core/Search`, read from `ISearchIndex.Bound`. That is a default interface member returning `Fallback`, so the test fakes keep compiling.
   - The alternatives were a static set at start-up (global state, which parallel tests share) or a parameter threaded through every caller.
   - Every search-time caller now chunks with the bound the index uses, so `Locate` matches by start line and part.
3. **The fallback bound: an embedding text of at most 500 characters**, applied only after a failed download while a provider is active.
   - The alternative was a combined word-and-character limit, which failed on punctuation and symbol runs (measurements below).
   - With no provider active, the bound is `Unbounded` (words only), because nothing is embedded. Such servers keep chunks of about 250 to 350 words.
3a. **Search-time cost.**
   - Counting is exact and cheap: a memo of piece counts per word, an ASCII fast path in the normaliser (checked against the precompiled map at load), a trie Viterbi that doesn't allocate, and a cheap upper bound tried before counting.
   - Each note's chunks are cached by content string, path and bound.
   - A vault's notes are split in the background once it is unlocked and the tokenizer has settled.
   - The numbers are below. A cold search on 12 MB of distinct text is 2.45× the old code's, against a 2× target that I accepted missing, because the warm-up covers it. A cached search is faster than before.
   - A probe compared the old and new `Encode` on 470,000 random texts across scripts, emoji, combining marks and control characters: 0 differences.
4. **Scheme version:** `NoteEmbeddings.Scheme` (`"2/tok-f1cc44ad7faa"`, `"2/fallback"`, `"2/words"`) is sealed in the payload; null means stored before this change.
   - **Notes of another scheme:** they count as pending, add no hits (none attach to wrong chunks), are queued again, and are ranked by words meanwhile. The alternative, mapping their hits by start line, is what the spec rules out.
   - **PDFs of another scheme:** they keep their page hits (part 0) until they are re-indexed and aren't counted as pending, because "semsearch --words finds them" isn't true of PDFs.
   - **Vectors** are reused by text hash.
   - **README:** a line says that after the update every note and PDF is chunked once more, and that vaults on an external provider send their text once more.
5. **PDF chunk key:** a new `EmbeddedChunk.Part` and `SemanticHit.Part`, while `StartLine` keeps the page number.
   - The alternative was encoding page and part into `StartLine` arithmetically.
   - Old data opens with `Part` 0.
   - One splitter, `Chunker.SplitPage`, serves both indexing and display, on the same uncut text read through a shared helper. A test checks both readers agree even with segments out of page order.
   - `SearchTextAsync`'s 2,000-character cut is gone.
6. **Merged chunks and `cat`:** a merged chunk's address is the deepest shared heading, or `""` for top-level siblings. The help says the address may name the parent and that the line range is exact.
   - **Visible change:** top-level siblings merge under the note, so `semsearch` prints `/Demo/gate.md  lines 1-4`.
   - **The §3.9 example:** *Safety* merges with its heading-only parent `# Pump P-200`. The spec's example has *Safety* alone; the §3.3 rule, and the same example's *Maintenance* case, put a heading-only parent together with its children.
7. **`rg` stays on plain sections** (`Chunker.Sections`, the old split unchanged), and so does the search of earlier versions. Grouping a single match by a merged chunk would make the output vaguer.
8. **The web search keeps its API.** Results are unique per path and start line, because `SearchSheet.tsx` keys rows that way, so a long line's pieces show once, as the best piece. `schema.d.ts` needed no change.
9. **Thresholds** are the spec's starting values: 250, 350, 120 and 40 words.
   - Small sections can go past 250 words only when one side is under 40 words, with a one-step lookahead.
   - The token ceiling usually binds first for German (2.3 tokens per word) and for dense tables (5 tokens per word).
10. **Download robustness, after the final review:**
    - an idle timeout of 60 s and an overall deadline of 10 minutes per attempt;
    - 3 attempts, 30 s and 2 min apart, before the fallback;
    - no retry within the run after that; the next start tries again.
    - A note whose indexing keeps failing (not because the provider is down) isn't re-queued by every search until it changes, the scheme changes, or the vault is unlocked again.
    - In the worst case, indexing waits about 32 minutes before it falls back, and search says "getting ready" meanwhile.
11. **`semsearch` strings beyond the plan:**
    - the singular "(hit K without its text, …)";
    - "(hits K-N left out, to fit the output)", when even header lines don't fit;
    - for `-C 3`: "semsearch: -C takes no number; to search for one, put it after --: semsearch -C -- 2025 budget (<usage>)".

Left open, as the brief asks for scope questions: none. Nothing needed a change to the spec's scope.

## Token counts behind the thresholds and the fallback

Measured with arctic-m's `tokenizer.json` (`UnigramTokenizer.Encode` without the cut, `<s>`/`</s>` included):

| Text | Characters | Words | Tokens | Tokens/word | Tokens/char |
|---|---|---|---|---|---|
| German pump prose | 1,943 | 216 | 506 | 2.34 | 0.260 |
| English prose | 1,904 | 315 | 407 | 1.29 | 0.214 |
| Numeric table (hex, decimals) | 2,461 | 304 | 1,564 | 5.14 | 0.636 |
| C-like code | 2,720 | 520 | 1,414 | 2.72 | 0.520 |
| URL list | 3,198 | 520 | 1,762 | 3.39 | 0.551 |
| Hex, no spaces | 2,400 | 1 | 1,452 | | 0.605 |
| Numbers | 2,749 | 400 | 1,118 | 2.79 | 0.407 |
| Random letters | 1,000 | 132 | 536 | 4.06 | 0.536 |
| Base64 | 1,000 | 22 | 781 | 35.5 | 0.781 |
| ASCII punctuation | 1,000 | 0 | 874 | | 0.874 |
| Mixed "aé1\|-_.ßäöü§→" | 1,000 | 256 | 881 | 3.44 | 0.881 |
| Symbols "→∑≈€§¶•✓★♣" | 300 | 0 | 283 | | **0.943** |
| CJK | 400 | 1 | 249 | | 0.623 |
| Greek | 1,000 | 119 | 695 | 5.84 | 0.695 |
| Emoji | 600 (300 code points) | 0 | 298 | | 0.497 |

The worst case is **0.943 tokens per character**, so 500 characters stay at about 474 tokens; the comment in `ChunkBound` records it. Runs of rare punctuation reach 1.0 per character, giving 502 tokens: still under 512, but a thin margin.

A word bound can't work as a fallback: tables run at 5 tokens per word, and base64 at 35.

## Search-time cost

`SectionSearch.RankAsync`, hybrid, over 1,000 notes (2–20 KB each, 11.8 MB), with a fake index giving a cosine per chunk. Release build, median of 5. The parent is the commit before the chunker (`126f06f`).

"Cold" means fresh content strings and a freshly loaded tokenizer, with empty memos.

| Corpus | Parent old split | Parent search | Split now (fallback / tokenizer cold / memo warm / cached) | Search now (fallback / tokenizer cold / memo warm / cached) |
|---|---|---|---|---|
| fixtures (1,851 distinct lines) | 137 | 877 | 407 / 527 / 485 / 0 | 938 / **1,025** / 982 / **474** |
| distinct (159,211 distinct lines) | 192 | 663 | 284 / 1,179 / 1,109 / 0 | 1,026 / **1,623** / 1,603 / **421** |

All values are in ms.

- **Fixtures:** a cold search is 1.17× the parent's, a cached one 0.54×.
- **Distinct corpus:** a cold search is 2.45× the parent's (down from 8.5×: 5,361 ms in round 1); a cached one 0.64×.
- **The 2× target is not met for a cold search on the distinct corpus.** Even with the fallback bound, a fully uncached search is 1.55× the parent's, from the work of the new chunker itself.
- **Splitting 12 MB of distinct text with the tokenizer now takes about 1.1 s**, the cost of warming one such vault in the background. A search after the warm-up costs 421 ms.

## Mutations, each applied alone and restored

### Task 1
Each was applied alone and restored with `git checkout -- <file>`. All were killed.

| Mutation | Killed by |
|---|---|
| (a) `Count` truncates at 512 (`Encode(text, 512)`) | `ChunkBoundTests.Count_is_Encode_without_the_cut`: Assert.True() Failure (Count > 512 false) |
| (b) `Cost` returns `Count(piece)`, specials included | `Costs_of_lines_add_up_to_the_count`, all 4 fixtures, e.g. Urls: Expected 176, Actual 184 |
| (c) hash check skipped in `ChunkTokenizer` (size only) | `ChunkTokenizerTests.A_download_that_does_not_match_falls_back`: Assert.False(File.Exists(tokenizer.json)) failed. The zero file was moved into place, and only failing to load it kept the fallback. |
| (d) `Needed` ignores external providers | 6 ChunkTokenizerTests, e.g. `A_failed_download_falls_back`: Expected fallback, Actual words |
| (e) fallback limit 600 | `The_fallback_counts_characters` (Fits(501) true) and `The_fallback_keeps_dense_text_under_the_limit` ("punctuation: 602 tokens") |
| extra: `SearchAsync` without the Preparing check | `SearchIndexTests.Until_the_tokenizer_settles…`: Expected (Preparing, "…tokenizer is loading"), Actual (Ready, null) |
| extra: `SearchIndex` doesn't wait for the tokenizer | same test: `Embedded` Expected 0, Actual 1 |

### Task 2
Each was applied alone to `Chunker.cs`, with `--filter-class "*ChunkerTests" --filter-class "*SearchTests"` and the tokenizer. The file was restored with `git checkout` after each.

| Mutation | Killed by | Message |
|---|---|---|
| (a) no header rows in table parts (`repeat = -1`) | `A_long_table_is_split_between_rows_with_its_header` | Assert.Equal() Failure: Strings differ |
| (b) skip rule 5c (`Common` always allowed) | **survived at first**: `Merging_never_crosses_a_parent` only had parents with children. Added two notes (`05eed24`); now killed by `Merging_never_crosses_a_parent` | Expected [("#A",0,3),("#B",3,5)] Actual [("",0,5)] |
| (c) words only in packing (no budget) | `No_embedding_text_goes_over_the_budget(PumpManual, fallback)`, `A_long_heading_path_leaves_room_for_the_core(fallback)`, `A_table_whose_header_leaves_no_room_goes_without_it`, `A_run_without_spaces_is_split_within_the_budget` (both) | Assert.All() Failure: 9 out of 11 items … |
| (d) last piece of a split line dropped | `A_line_of_10000_characters_is_split_at_spaces` (fallback, unbounded), `A_run_without_spaces_is_split_within_the_budget` (both) | Assert.Equal() Failure: Strings differ |
| (e) `\|` and `-` count as word characters | `Words_are_runs_of_letters_and_digits` | Assert.Equal() Failure: Values differ |
| (f) all pieces `Part = 0` (`part++ * 0`, since a plain `0` doesn't compile under warnings-as-errors) | `Locate_finds_a_piece_by_its_part` (Assert.Single() … 10 items), `A_run_without_spaces_is_split_within_the_budget` (Expected [0,1,2,…] Actual [0,0,0,…]) | |

Re-run without the tokenizer, after fix round 1:

All killed:

| Mutation | Killed by |
|---|---|
| (a) a row's new part without the header (`TryOpen(-1,…)` in place of `TryOpen(header,…)`) | `A_wide_row…` and `A_long_table_is_split_between_rows_with_its_header(fallback)` (Strings differ) |
| (b) no rule 5c | `Merging_never_crosses_a_parent` |
| (c) words only in packing | `A_run_without_spaces_is_split_within_the_budget(fallback)`, `A_table_whose_header_leaves_no_room_goes_without_it`, `A_long_heading_path_leaves_room_for_the_core(fallback)`, `Header_rows_that_fit_with_no_row_end_the_part_before` |
| (d) last piece dropped | `A_run_without_spaces_is_split_within_the_budget(fallback)`, `A_run_without_spaces_holds_at_most_the_maximum_of_words(fallback, unbounded)` |
| (e) `\|` and `-` as word characters | `Words_are_runs_of_letters_and_digits` |
| (f) all pieces part 0 | `Locate_finds_a_piece_by_its_part`, `A_run_without_spaces_…` |
| (g) new: cache ignores path and bound | `The_chunks_of_a_content_are_reused_for_the_same_path_and_bound_only`, `A_line_of_10000_characters_is_split_at_spaces(fallback)`, `A_long_heading_path_leaves_room_for_the_core(fallback)` |
| (i) new: no-space cut without the words limit | `A_run_without_spaces_holds_at_most_the_maximum_of_words(unbounded)` |

Fix round 2:

All were applied alone and killed, with the tokenizer:

| Mutation | Killed by |
|---|---|
| (j) no joining of unknowns across words | `Count_is…(tiny-without-mark)` |
| (k) the ASCII fast path without the next-character check | `Count_is…(arctic-m)` (normalisations differ) |
| (l) ceiling +1 instead of +3 | `Count_is…` (tiny, tiny-without-mark, arctic-m) |
| (n) counting every unknown piece in `CountWord` | `Count_is…` (all three) |
| (o) snippets ignoring fenced code | `Snippet_of_a_table_part_keeps_its_header_row` |
| (q) ceiling check with 200 units of slack | `Small_sections_merge_up_to_the_target(fallback)`, `A_table_whose_header_leaves_no_room_goes_without_it`, `Header_rows_that_fit_with_no_row_end_the_part_before` |
| (r) non-ASCII characters' ceiling 1 instead of the longest replacement | **survived at first**; killed after adding the runs of "ﷺ" and "㎏㎒ﬃ" to the texts, by `Count_is…(arctic-m)` |
| warm-up removed | `The_notes_of_an_unlocked_vault_are_split_ahead_of_the_first_search` (see 3) |

### Task 3
- (a) `IsCurrent` ignores the scheme: killed by `A_note_of_an_older_scheme_is_chunked_anew` (expected "2/fallback", actual null). `Older_scheme_notes_are_pending_until_re_indexed` and `Unchanged_texts_keep_their_vectors_across_schemes` fail too.
- (b) `SearchAsync` adds the hits of old-scheme notes: killed by `Older_scheme_notes_are_pending_until_re_indexed` ("Assert.Single() Failure: The collection contained 3 items").
- (c) Old-scheme PDFs dropped: killed by `A_PDF_of_an_older_scheme_stays_findable_by_page` (output "(nothing found for 'quokkas smile visitors' …").
- (d) `Part` not sealed: killed by `Embeddings_keep_their_scheme_and_parts` (expected `(3, 2, "h2")`, actual `(3, 0, "h2")`).
- (e) The "unchanged" check ignores `Part`: killed by `Pages_stored_with_other_parts_are_stored_anew` (parts stay 7).

### Task 4
- (a) Keep the 2,000-character cut (truncate each page in `SearchTextAsync`). Killed by:
  - `PdfAnalysisStoreTests.The_text_for_search_holds_every_page_whole`: "Collections differ at index 0 … Actual: aaaa…�" (it even cuts inside the surrogate pair);
  - `PdfSearchTests.A_dense_PDF_page_is_embedded_in_parts`: "1 parts" (PdfSearchTests.cs:93).
- (b) The rolls cross into the core (pre-roll starts at the core's first line). Killed by `Rolls_stay_within_the_page` (fallback, unbounded), "Strings differ, Expected: """, and by `A_short_page_is_one_chunk`. A variant that crosses for middle chunks only is killed by `Rolls_stay_within_the_page`: "Assert.Contains() Failure: Sub-string not found".
- (c) Drop the last chunk of a page (`cores.SkipLast(1)`). Killed by:
  - `A_dense_page_yields_several_chunks_covering_its_text` (both variants): "Strings differ ↓ (pos 8735)";
  - `Rolls_stay_within_the_page`;
  - `A_short_page_is_one_chunk`: "Assert.Single() … empty";
  - `PdfSearchTests.A_dense_PDF_page_is_embedded_in_parts`: "Collections differ".
- (d) `WithPages` keeps the first part instead of the best. Killed by `SearchTests.One_hit_per_PDF_page_with_its_best_part`: "Expected [(3, 2, 0.7), (5, 0, 0.6)], Actual [(3, 0, 0.7), (5, 0, 0.6)]".

All four mutations were killed.

### Task 5
- (a) The snippet instead of the core. Killed by `Prints_the_whole_core_of_each_hit` ("Strings differ"), and also by With_C and Cores_that_do_not_fit.
- (b) No ┊ on roll lines. Killed by `With_C_it_prints_the_rolls_marked` and `A_PDF_hit_prints_its_best_part` ("Strings differ").
- (c) No budget (`if (size > room)` → `if (size < 0)`). Killed by `Cores_that_do_not_fit_fall_back_to_one_line`: "Strings differ" on the footer line, since the shell's OutputLimit cut it off.
- (d) `-C 3` accepted (check disabled). Killed by `C_takes_no_number` (all 3 cases: Assert.True / Sub-string not found).
- (e) The PDF hit always shows part 0. Killed by `A_PDF_hit_prints_its_best_part` ("Strings differ").
- Extra (f): the repeated table header not excluded from rolls. Killed by `Rolls_of_a_long_table_part_are_neighbouring_rows(fallback)` and `Rolls_are_never_embedded(PumpManual, fallback)`.

(The first tries of (c) and (d) used `if (false)`, which doesn't compile under warnings-as-errors, CS0162; they were redone with non-constant conditions.)

Fix round:

- M1, the notice not subtracted from the room: killed by `The_notice_above_the_hits_counts_towards_the_limit` ("24042 characters").
- M2, PartRecord back to `Segments[0]`/`Segments[^1]`: killed by `PdfAnalysisStoreTests.A_page_reads_as_the_text_for_search_has_it_also_with_segments_out_of_page_order` ("Collections differ at index 0").
- M3, the splitter not setting RepeatedHeader: killed by `Rolls_of_a_long_table_part_are_neighbouring_rows(fallback)` and `A_long_table_is_split_between_rows_with_its_header(fallback)`.
- M4, resolving the address heading only when it has no index: killed by `Rolls_find_a_heading_that_shares_its_text_with_a_sibling`.
- M5, PixelsUnavailableException not caught: killed by `A_page_whose_text_cant_be_read_says_so`.
- M6, a null page text not treated as unreadable: killed by `A_page_whose_text_cant_be_read_says_so` ("Strings differ").

### Task 6
- **Change:** in `ChunkSplitter.FeedTable`, `if (TryOpen(header, r, r + 1)) bare = false;` became `if (TryOpen(-1, r, r + 1)) ...`, so table parts no longer repeat the header rows.
- **Result:** killed by `A_row_near_the_end_of_a_long_table_is_found_with_its_header` with `Assert.Contains() Failure: Sub-string not found … Not found: "\n   | Code | Meaning | Remedy |\n   |------|-------"…`.
- **Restore:** `git checkout -- src/Kumbu.Core/Search/ChunkSplitter.cs`. I ran the mutation before the commit, on the same code.

### Final review fix wave
#### 1. Stalled tokenizer download

- Change (`src/Kumbu.Server/Search/ChunkTokenizer.cs`): `DownloadAsync` gets a linked CTS with a total limit (`DownloadLimit`, 10 min) and a per-read idle CTS linked to it (`IdleLimit`, 60 s, reset with `CancelAfter` after each read, also covering the headers and `ReadAsStreamAsync`). A cancellation that isn't the stopping token becomes a `TimeoutException` ("stalled" / "took too long"), which counts as a failed attempt. The existing `finally` deletes the part file. Both limits are internal settable properties, like the other test-only hooks. Pinning, the size cap, the SHA-256 check before the move and the own part file are unchanged.
- Tests (`ChunkTokenizerTests`): `A_stalled_download_is_given_up` (16 bytes, then a read that never returns; IdleLimit 200 ms → Fallback, 3 requests, folder empty), `A_download_that_trickles_is_given_up` (1 byte per 20 ms; DownloadLimit 300 ms), `A_slow_download_that_keeps_coming_is_not_given_up` (the real file, 1 MB per read every 40 ms, IdleLimit 250 ms → bound; needs KUMBU_TOKENIZER).
- RED, with the original source plus stub properties: the stalled and trickling tests failed after 30 s (the test's settle timeout; the download hung).
- Mutations killed: reads on `ct` instead of `idle.Token` → stalled + trickling fail; no `total.CancelAfter` → trickling fails; no idle reset per read → slow-download fails ("Strings differ": fallback instead of the tokenizer).

#### 2. Retries before settling on the fallback

- Change: `DownloadWithRetriesAsync`, 3 attempts with pauses `RetryDelays` = [30 s, 2 min] (internal settable). A stop during a pause ends it as before. The class comment and the README line now say the download is tried 3 times before the character bound applies.
- Tests: `A_passing_failure_is_tried_again` (503, 503, then the file → tokenizer bound, 3 requests; needs KUMBU_TOKENIZER), `Each_attempt_waits_its_pause_first` (pause of 10 min → 1 request, not settled). The failure tests now expect 3 requests (`A_failed_download_falls_back`, `A_download_that_does_not_match_falls_back`, `With_the_internal_model…`, and `A_download_longer_than_the_file_is_cut_off`, which now checks each of its 3 streams for the cut-off). The tests' `Create` sets the pauses to zero.
- Test hosts: `NoDownload.Register` now registers the `ChunkTokenizer` with `RetryDelays = []`. Without that, every host with a provider and no tokenizer waited 2.5 min before search was ready. Without KUMBU_TOKENIZER, `EmbeddingApiTests.A_search_through_a_real_driver_leaves_no_http_client_lines_in_the_log` failed this way until the change.
- RED: with the original source, 8 tests failed (3 requests expected but 1 made; the passing failure got the fallback; the pause test was settled).
- Mutations killed: no retry (`attempt < 0`) → 6+ tests fail, including `A_failed_download_falls_back` and `Each_attempt_waits_its_pause_first`; no pause → `Each_attempt_waits_its_pause_first` ("collection contained 2 items").

#### 3. Searches re-sending a note that always fails

- Change (`SearchIndex`): `_failed` maps (vault id, path) to (version, scheme) of the last failed attempt. `RunAsync`'s generic catch, which leaves out provider-down errors, calls `NoteFailed`. A run that doesn't throw removes the entry, and `CatchUp` clears the vault's entries. `Requeue(vault, paths)` enqueues only the paths where `HasFailed` isn't true for the current version and scheme. `EncryptedNoteStore.SearchAsync`'s three re-queue sites (other vector length for a note, other vector length for a PDF, other scheme) now call `Requeue`. Writes and catch-up still call `Enqueue`.
- Tests (`SearchIndexTests`): `A_note_that_fails_is_not_sent_again_by_every_search`. It uses `FakeEmbedder.Refuses` (a new hook that throws for matching texts and counts them) on a note made stale under another scheme with unknown hashes. The first search → 1 refusal; three more searches → `Queued` stays 0 and there are no more refusals; a write → tried again (2 refusals), then searches leave it again. `Failures_are_forgotten_on_an_unlock_and_with_another_version`: `CatchUp` clears the entry, and a new version no longer counts as failed.
- RED and mutation: `Requeue` without the filter (the old behaviour) → the first test fails ("Values differ"). No clear in CatchUp → the second test fails. A version-blind `HasFailed` → the second test fails. No `NoteFailed` in the catch → the first test fails (`HasFailed` false).

#### 4. Locate scanning every chunk for PDF hits

- Change (`SectionSearch`): `RankByMeaning` groups the chunks by path once. `Locate` returns null for a PDF path first, and its fallback looks only at the hit's own note's chunks.
- Test (`SearchTests.A_hit_maps_only_to_its_own_note`): a PDF hit and a hit for a note that isn't searched → no hits; with pages, only the page. A note chunk under the PDF's own path (no store gives one; it isolates the PDF skip) → no hits. The existing Locate and page tests pass unchanged.
- RED: none possible. The fix is for cost, and before it a PDF hit already matched no chunk because note paths differ. The test guards the behaviour.
- Mutations killed: fallback over all chunks without the path → the test fails ("not empty"); no PDF skip → the test fails via the PDF-named note.

#### 5. Code parts read as headings

- Change: `Outline.Fences(lines)` returns the code flags plus, for each line, the fence open before it; `CodeLines` delegates to it. `Chunk.Fence` (string?, init) holds the note's fence a chunk starts inside of. `ChunkSplitter` sets it at all three creation sites from the note's lines at the chunk's StartLine, and never for table parts. `SectionSearch.Body` (used by `IsHeadingOnly` and both `Snippet`s) prepends the fence before working out code lines.
- Test (`SearchTests.Every_part_of_a_long_code_block_of_comments_is_listed`): a python block of 150 "# kettle step …" lines → more than 2 chunks, some without a fence. A words search for "kettle" lists every chunk's start line, `Snippet(c)` contains the comment lines, and `Snippet(c, "kettle")` starts with one.
- RED and mutation: `Body` working out code from the chunk text again → fails ("Collections differ": the parts were dropped). No `Fence` at the packed-part site → fails.

#### 6. Token ceiling check at index time

- Change: `EmbedAsync` counts the texts it is about to send that fail `Bound.Fits` (`internal static Oversized`). If any do, it logs a warning with the vault id, count, path id and scheme, no content, and still embeds.
- Test: `Texts_over_the_bound_are_counted_and_normal_notes_stay_quiet`. `Oversized` counts 1 of 2 for Fallback and 0 for Unbounded, and a normal note in several chunks logs no warning (CapturedLog through a new optional `log` parameter of the tests' `StartAsync`).
- RED: none for the log. The chunker never produces an oversized text, so no indexing path reaches the warning; the brief made this test optional.
- Mutations killed: inverted `Fits` → fails; `over >= 0` → fails ("Filter matched"). Not killable: removing the check entirely. A test for it would need a seam to inject an oversized chunk, which I didn't add.

#### 7. Chunker.MaxChars comment

Now says it is the limit of `Sections` only (rg, EarlierSearch), and that embedded chunks are bounded by their ChunkBound. Comment only, no test.

## Deviations and surprises

- **The tokenizer rewrite.**
  - **What changed:** the Viterbi, the normaliser and counting were changed for speed, all inside `UnigramTokenizer`. That is beyond the plan's intent, and it was needed because the first cold search took 5 s per 12 MB.
  - **Why it is safe:** `Encode` gives the same ids. Evidence: the Hugging Face reference test for arctic-m passes, and the probe over 470,000 texts found 0 differences.
- **PDF analysis parts:** each stored part's first and last page are now its lowest and highest page, not those of its first and last segment, so a part with segments out of page order is found for every page it holds.
  - The stored shape is unchanged. Analyses stored before this change were written in page order, so their values don't change.
- **`CatchUp` waits for the tokenizer.** It returns early until the tokenizer has settled, and the indexer catches up every open vault afterwards. That avoids queueing every note and PDF at each start.
- **New test-only hooks,** in the style of `TextPartRead`: `ArrangeSegments` in `PdfAnalyses`, and `DownloadLimit`, `IdleLimit` and `RetryDelays` in `ChunkTokenizer`. All are internal and none comes from configuration.
- **Gates sharing the machine:**
  - The gate of Task 5 is invalid: a process was killed for lack of memory while the real-model tests ran beside it.
  - The unrelated failures in gates 3, 4 and 6 each passed 3 times in their class alone.
  - On CI, with a quiet machine, I expect the known flaky tests only.
- **CLAUDE.md's test count is stale.** It says "4 tests skip without the model and a TensorX key"; now 3 real-model end-to-end tests and 1 arctic-l count test skip as well. I didn't touch CLAUDE.md.

## Open points for Chris

1. **Visible output change:** top-level sibling sections merge under the note (address `""`), and *Safety* merges with `# Pump P-200`. If you'd rather keep top-level sections apart, it is one rule in `ChunkSplitter`.
2. **A cold search on a large vault** right after unlock (within about 1 s per 12 MB) costs 2.45× what it did before. After that, search is faster.
3. **Memory:**
   - **Tokenizer:** it is loaded twice when the internal model is arctic-m, once by `ChunkTokenizer` and once by `OnnxEmbedder`. Each copy is a trie of about 14 MB, plus a word memo of up to 200k words and a segment memo of up to 50k segments.
   - **Chunk cache:** it holds about the notes' size again.
   - **PDF indexing:** `SearchTextAsync` holds a PDF's whole text while the PDF is indexed.
4. **The README names 0.4.0** for the re-indexing line. Please check it at release.
5. **The fallback** is 500 characters. A server that ends up on the fallback makes about 4 times as many chunks, and once the tokenizer arrives at a later start, everything is chunked and sent to an external provider again. The retries make this rare.
6. **Deferred minor findings,** each triaged by the final review as fine to leave:
   - `-C` can repeat a neighbouring hit's core;
   - the middle pieces of a long line show the whole line's neighbours as rolls;
   - a page's last chunk can be small;
   - a skill's `SKILL.md` loses meaning hits while it is chunked anew;
   - the log line "notes and PDFs to chunk anew" doesn't tell notes from PDFs;
   - removing the warning for an embedding text over the bound is caught by no test, since the chunker never produces one.
7. **#176 was merged during the run.** This branch still carries `9e919a7` (its cherry-pick) and needs a rebase onto `master`, or a merge of `master`, before it can be merged. Per the brief I did neither.

## Hand-test checklist

Use a server with the internal model (arctic-m) unless a step says otherwise.

1. **Update an existing server and watch re-indexing.**
   - Start the new image on a vault indexed by 0.3.x. Within a few seconds the log says "Embedding model loaded", and the tokenizer is verified without downloading.
   - Run `semsearch pump` right away.
   - **Pass:** the footer says "N notes aren't indexed yet …", and N goes down on each repeat until the footer reads "(ranked by words and meaning; …)". Meanwhile the hits still include those notes, ranked by words.
2. **A long ingested note with a big table.**
   - Ingest a PDF manual with a table of 200+ rows (the ingest skill, or write the note by hand). Then `semsearch <words of a row near the end> /Vault`.
   - **Pass:** hit 1 is `path#…#<table heading>  lines a-b`, its core starts with the table's header and separator rows, then its own rows including the one searched for, and `lines a-b` covers only those rows (`cat` with `tail +a | head` shows them).
3. **A dense PDF page searched by its last paragraph.**
   - Upload a PDF with a page of more than 4,000 characters, wait for its analysis, then `semsearch <its last paragraph in other words> /Vault/file.pdf`.
   - **Pass:** `file.pdf pN  score …`, then the last part of the page, not its start.
4. **Default output, `-C`, `-l`.** Run `semsearch seal drips` and the same with `-C` and with `-l`.
   - **Pass, default:** each hit is a header line, then its whole core indented by three spaces.
   - **Pass, `-C`:** the same, plus lines before and after marked `   ┊ `.
   - **Pass, `-l`:** one line per hit.
   - **Refusals:** `semsearch -C 3 pump` and `semsearch -C -l pump` are refused, the first with the usage line.
   - **Limit:** `semsearch -n 100 the` stays under the output limit with its footer last, and later hits show as "(hits K-N without their text, …)".
5. **`help semsearch`.** **Pass:** it documents `-C` and `-l`, and says that a short section may be merged under its parent and that the lines are exact.
6. **A server with `KUMBU_SEARCH=false` and an external provider.**
   - Configure one external provider, search off, and an empty `models` volume.
   - **Pass:** the log shows "Downloading the tokenizer (17 MB) into …" and then "Chunks are bounded by the tokenizer in …", and `models/arctic-m/tokenizer.json` is there, 17,083,009 bytes.
   - **Pass:** search by meaning works and, after indexing, the footer shows no pending notes.
7. **A server with no provider at all** (`KUMBU_SEARCH=false`, no external providers). **Pass:** no "Downloading the tokenizer" line in the log, and `semsearch --words` works.
8. **A blocked download.**
   - Same as step 6, with huggingface.co blocked or a proxy that stalls.
   - **Pass:** search says "getting ready (the tokenizer is loading)", the log shows "Downloading the tokenizer failed … trying again" twice and then "The tokenizer isn't available …; search chunks by a character bound until the next start". Within about 32 minutes at worst, search by meaning then works with smaller chunks.
9. **The web app's search.** Search for words from the big table and from a merged section.
   - **Pass:** the result list shows hits with their address and lines, no duplicate rows, and opening a hit goes to the right note.
10. **`rg`.** Run `rg -n 'E241' /Vault`. **Pass:** the same output as before this change, with plain sections and the exact line.

## Pull request

- Draft: https://github.com/SCAI-NGO/kumbukumbu/pull/177
- A row in `docs/superpowers/open-items.md` links it.
- I'm watching the pull request for CI results and review comments.

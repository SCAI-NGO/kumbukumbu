# Search Tuning (step 7b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Score fusion instead of RRF, a configurable embedding model (arctic-m or arctic-l) with re-embedding on a switch, the phase 7 fixes, server instructions, and the search evaluation in the repository.

**Architecture:** `Semsearch` (Kumbu.Core) ranks by cosine plus a word bonus over every chunk that has current embeddings. `ModelFiles` becomes a catalogue of `EmbeddingModel` records; `OnnxEmbedder` loads the configured one from `ModelPath/<key>/`. Each note's sealed embeddings record their model key, and `SearchIndex`/`EncryptedNoteStore.SearchAsync` treat other models' embeddings as missing.

**Tech Stack:** C# / .NET 10, xUnit v3, ONNX Runtime, MongoDB, ModelContextProtocol C# SDK; Python with uv for the evaluation.

**Spec:** `docs/superpowers/specs/2026-10-24-search-tuning-design.md` (and `docs/notes-test-phase7.md` for the measurements).

## Global Constraints

- British English in code, comments and docs; SPDX header `// SPDX-License-Identifier: AGPL-3.0-or-later` (Python: `# SPDX-License-Identifier: AGPL-3.0-or-later`) on new files.
- Bonus weight `0.1`, list length for the meaning hits unchanged where it isn't replaced; default model key `arctic-m`.
- arctic-l: `Snowflake/snowflake-arctic-embed-l-v2.0` at `ac6544c8a46e00af67e330e85a9028c66b8cfd9a`; `tokenizer.json` 17083074 bytes `39feb9863a378165ab9c5c689047203d789422966c0c58721c5309fd039a8edc`; `onnx/model_fp16.onnx` 1134153607 bytes `0b6846bcf4c0d9e53fdfc70d6dc1bbe3bb40fd205ebb7f0365da5b07064ddccc`; 1024 dimensions; prompt `query: `.
- No free model ids: only keys from the catalogue.
- Commit messages imperative, free-form, ending with the session's attribution lines.
- `dotnet test` green after every task.

## Review Focus

1. A vault whose embeddings are half old model, half new (switch mid-catch-up): search must never compare a query vector with a vector of the other model or of another dimension; those notes count as pending. Test in Task 3.
2. Switching back (`arctic-l` → `arctic-m`) re-embeds again and doesn't resurrect stale vectors by text hash from the other model. Test in Task 3.
3. A step 7 deployment starts with files directly in `ModelPath`: no second 613 MB download, and a partly downloaded `.part` file left from step 7 isn't moved as if complete. Test in Task 2.
4. A query made only of stop words or single letters (`a`, `der die das`): the cosine alone ranks, no division by zero. Test in Task 4.
5. A folder whose moves all happened at the same instant but by two writers, or a real folder move plus an unrelated single move at the same time: one row for the folder move, one for the single move. Test in Task 5.

---

### Task 1: Tokenizer reads arctic-l's tokenizer.json

**Files:**
- Modify: `src/Kumbu.Core/Search/UnigramTokenizer.cs`
- Create: `tests/Kumbu.Core.Tests/Search/tokenizer-reference-arctic-l.json` (copy to output like the existing reference file; see the test csproj)
- Modify: `tests/Kumbu.Core.Tests/Search/UnigramTokenizerTests.cs`, `tests/Kumbu.Core.Tests/Kumbu.Core.Tests.csproj`

**Interfaces:**
- Produces: `UnigramTokenizer.Load(Stream)` accepts both normaliser forms (`Precompiled`, or `Sequence` of `Precompiled` and `Replace` with a `Regex` pattern) and both pre-tokeniser forms (`Sequence[WhitespaceSplit, Metaspace]`, or `Metaspace` alone). Unknown forms throw `InvalidDataException` naming the type.

- [ ] **Step 1: Generate the reference ids.** In the scratchpad evaluation env (`uv run python`), with `tokenizers` and arctic-l's `tokenizer.json` (download it from the pinned revision), encode the 20 texts of `tokenizer-reference.json` plus `"  leading and trailing  "`, `"a  b   c"`, `"tab\there\nnewline"`, `"query: Warum heizen wir nicht mit Erdwärme?"`, and write `[{"text":…,"ids":[…]}]` to `tokenizer-reference-arctic-l.json`.
- [ ] **Step 2: Write failing tests.** `MatchesHuggingFaceOnArcticL` (like `MatchesHuggingFaceOnTheRealVocabulary`, reading `KUMBU_MODEL_DIR/arctic-l/tokenizer.json`, skipping without it), and point the existing real-vocabulary test at `KUMBU_MODEL_DIR/arctic-m/tokenizer.json`. Tiny-vocabulary tests: `ReplaceCollapsesRunsOfSpaces` and `MetaspaceAloneKeepsTrailingSpaceMark`, built by loading a minimal `tokenizer.json` string (a `Stream` over JSON with the tiny vocabulary, `Replace` normaliser, `Metaspace` pre-tokeniser), asserting the ids Hugging Face gives for the same tiny config (derive them by running the tiny JSON through `tokenizers` in Step 1 and pasting them).
- [ ] **Step 3: Run** `dotnet test --filter UnigramTokenizerTests`; expected: the new tests fail (arctic-l tests skip without the model dir).
- [ ] **Step 4: Implement** the two forms in `Load` and `Encode`: the `Replace` regex applied after the precompiled map; for `Metaspace` alone, replace spaces with `▁`, prepend one `▁`, and split before each `▁` (Hugging Face's `split: true`, `MergedWithNext` behaviour) instead of splitting on whitespace. Match the reference ids exactly.
- [ ] **Step 5: Run** `KUMBU_MODEL_DIR=<dir with arctic-m/ and arctic-l/ tokenizer.json> dotnet test --filter UnigramTokenizerTests`; expected: all pass, none skipped.
- [ ] **Step 6: Commit** `Read arctic-l's tokenizer: Replace normaliser and Metaspace alone`.

### Task 2: A catalogue of models and the option to choose one

**Files:**
- Modify: `src/Kumbu.Server/Search/IEmbedder.cs` (`ModelFiles` → catalogue), `src/Kumbu.Server/Search/OnnxEmbedder.cs`, `src/Kumbu.Server/KumbuOptions.cs`, `src/Kumbu.Server/KumbuOptionsValidator.cs`, `compose.yaml`, `.env.example`, `README.md`
- Test: `tests/Kumbu.Server.Tests/Startup/KumbuOptionsValidatorTests.cs`, `tests/Kumbu.Server.Tests/Search/ModelFilesTests.cs` (create)

**Interfaces:**
- Produces:
  - `public sealed record EmbeddingModel(string Key, string Repository, string Revision, string QueryPrompt, int Dimensions, IReadOnlyList<ModelFile> Files)` and `public sealed record ModelFile(string Remote, string Local, long Size, string Sha256)`; `EmbeddingModel.Url(ModelFile)`.
  - `public static class ModelFiles { public const string Default = "arctic-m"; public static IReadOnlyDictionary<string, EmbeddingModel> Catalogue; public static EmbeddingModel Get(string key); public static string Directory(string modelPath, string key); public static void AdoptStep7Layout(string modelPath); }` with local names `tokenizer.json` and `model_fp16.onnx` for both.
  - `SearchOptions.Model` (string, default `"arctic-m"`); env `Kumbu__Search__Model`, compose `KUMBU_SEARCH_MODEL`.
  - `IEmbedder.Model` (string key of the loaded/configured model).

- [ ] **Step 1: Failing tests.** `KumbuOptionsValidatorTests.UnknownSearchModelFails` (`Search.Model = "gpt"` → failure message contains `arctic-m` and `arctic-l`). `ModelFilesTests.CatalogueHoldsBothArcticModels` (keys, dimensions 768/1024, revisions, sizes and SHA-256 values from Global Constraints and the current `ModelFiles`). `ModelFilesTests.Step7FilesAreMovedIntoTheModelFolder`: a temp dir with `tokenizer.json` and `model_fp16.onnx` of the arctic-m sizes (sparse files via `FileStream.SetLength`) → after `AdoptStep7Layout`, both are in `arctic-m/`, none left at the root. `ModelFilesTests.PartFilesAndWrongSizesStay`: `model_fp16.onnx.part`, and a `model_fp16.onnx` of the wrong size → not moved.
- [ ] **Step 2: Run** the tests; expected FAIL (types missing).
- [ ] **Step 3: Implement** the catalogue, the option, the validator check, and in `OnnxEmbedder`: `AdoptStep7Layout` before downloading, download and load from `ModelFiles.Directory(o.ModelPath, o.Model)`, query prompt and dimensions from the model record. Output tensor: use `sentence_embedding` when the session's outputs have it, otherwise the first token of `last_hidden_state`; check the vector length equals `Dimensions` on the first run (throw `InvalidDataException` otherwise). Log `Embedding model loaded ({Key}: {Repository} at {Revision[..8]})`. Compose: `Kumbu__Search__Model: ${KUMBU_SEARCH_MODEL:-arctic-m}`; `.env.example` and README document `KUMBU_SEARCH_MODEL` (keys, sizes 613 MB / 1.13 GB, memory about 1 GB / 2 GB, ~26 / ~49 ms per section, switching re-embeds every note when its vault is next unlocked).
- [ ] **Step 4: Run** `dotnet test`; expected PASS.
- [ ] **Step 5: Commit** `Make the embedding model configurable: arctic-m or arctic-l`.

### Task 3: Embeddings record their model; a switch re-embeds

**Files:**
- Modify: `src/Kumbu.Server/Vaults/UnlockedVault.cs` (payload and `NoteEmbeddings`), `src/Kumbu.Server/Search/SearchIndex.cs`, `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (`SearchAsync`)
- Test: `tests/Kumbu.Server.Tests/Search/SearchIndexTests.cs`, `tests/Kumbu.Server.Tests/Search/FakeEmbedder.cs`

**Interfaces:**
- Consumes: `IEmbedder.Model` (Task 2).
- Produces: `public sealed record NoteEmbeddings(string Model, string Version, IReadOnlyList<EmbeddedChunk> Chunks)`; `EmbeddingsPayload` gains `string? Model` (null when read = `"arctic-m"`). `FakeEmbedder(SemanticState state = Ready, string model = "arctic-m", int dimensions = …)`.

- [ ] **Step 1: Failing tests** in `SearchIndexTests`: `SwitchingTheModelReEmbedsOnUnlock` (index with fake `arctic-m`, restart with fake `arctic-l`: `Embedded` equals the chunk count, stored payloads say `arctic-l`); `SearchSkipsVectorsOfAnotherModelAndCountsThemPending` (before the catch-up finishes, or with the embedder not ready for indexing: hits contain no note with old-model embeddings, `Pending` counts them; the two fakes use different dimensions so a mix would throw); `SwitchingBackDoesNotReuseVectorsOfTheOtherModel` (m → l → m: the second m run embeds again; reuse by hash takes only same-model vectors); `EmbeddingsWithoutAModelAreArcticM` (a payload written without `Model` opens as `arctic-m`, so a step 7 index isn't re-embedded under arctic-m).
- [ ] **Step 2: Run**; expected FAIL.
- [ ] **Step 3: Implement**: `SealEmbeddings`/`OpenEmbeddings` carry the model; `CatchUp` queues notes whose embeddings' `Model != _embedder.Model` or version differs; `IndexAsync` treats other-model embeddings as absent and builds `known` from same-model chunks only; `SearchAsync` skips `e.Model != embedder.Model` and counts the note pending. On a catch-up that queues notes for another model, log `Vault {Name}: {Count} notes to embed with {New} (were {Old})`.
- [ ] **Step 4: Run** `dotnet test`; expected PASS.
- [ ] **Step 5: Commit** `Record each note's embedding model and re-embed after a switch`.

### Task 4: Score fusion and the semsearch output

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/Semsearch.cs`, `src/Kumbu.Core/Search/Ranking.cs` (`QueryTerms`; RRF removed if nothing else uses it), `src/Kumbu.Core/Search/ISearchIndex.cs` (doc comments)
- Test: `tests/Kumbu.Core.Tests/Search/SearchTests.cs`

**Interfaces:**
- Consumes: `ISearchIndex.SearchAsync(scope, query, limit, ct)`: called with `limit: int.MaxValue` so every chunk with current embeddings gets its cosine.
- Produces: `public static class Fusion { public const double WordBonus = 0.1; public static double Score(double cosine, IReadOnlyList<string> queryTerms, IReadOnlySet<string> chunkTerms); }` in `src/Kumbu.Core/Search/Ranking.cs`.

- [ ] **Step 1: Failing tests** in `SearchTests` (fake index as the existing tests use): `WordBonusLiftsASectionContainingTheQueryTerms` (two chunks with cosines 0.40 and 0.35; the second contains both query terms → it ranks first: 0.35 + 0.1 > 0.40); `NoQueryTermsRanksByCosineAlone` (query `der die das` → order by cosine); `ScoresArePrintedWithTwoDecimals` (`score 0.45` on the result line; no score with `--words`); `HeadingOnlySectionsAreLeftOut` (a chunk whose text is `## Februar 2026` with the best cosine isn't listed, the next one is first); `PendingNotesAreNamedWithWords` (footer: `… 1 note isn't indexed yet and isn't ranked by meaning; semsearch --words finds it …`); `ASingleDigitStaysAQueryTerm` (`Bm25.QueryTerms("topic 3")` = `["topic", "3"]`; `"a b"` → empty). Remove or rewrite the RRF tests that no longer apply.
- [ ] **Step 2: Run** `dotnet test --filter SearchTests`; expected FAIL.
- [ ] **Step 3: Implement**: hybrid = for each chunk with a semantic hit, `Fusion.Score(hit.Score, terms, Bm25.Terms(chunk.EmbeddingText) as set)`; sort descending; `--meaning` = cosine; `--words` = BM25 as now. Score on the result line: `{n}. {path}{address}  lines {a}-{b}  score {s:0.00}` (invariant culture). Heading-only: `chunk.Text` has one line and it is a heading. Update `Help` (the two rankings are combined by adding a bonus for the query's words to the meaning score; scores are comparable within one search, not across searches) and the class comment.
- [ ] **Step 4: Run** `dotnet test`; expected PASS.
- [ ] **Step 5: Commit** `Rank by meaning plus a word bonus instead of RRF; show scores`.

### Task 5: History rows for moves

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/History.cs` (`WritesBelowAsync`)
- Test: `tests/Kumbu.Core.Tests/Sh/VersionCommandsTests.cs`

- [ ] **Step 1: Failing tests**: `AFolderMoveIsOneRow` (`mv /W/a/ /W/b/` with two notes → one row `/W/a/ → /W/b/  2 notes`, writer `dev (mv)`); `ARestoresMovesAreOneRowEach` (two `MoveRecord`s by `dev (restore)` at the same instant, `/W/elsewhere/x2.md → /W/r/x.md` and `/W/r/sub/z.md → /W/r/y.md` → two rows, writer `dev (restore)`, no `(mv)` added); `AFolderMoveAndASingleMoveAtOneInstant` (three records at one time: two from `/W/a/` to `/W/b/` keeping their names, one `/W/c.md → /W/d.md` → two rows).
- [ ] **Step 2: Run**; expected FAIL.
- [ ] **Step 3: Implement**: within each `(MovedBy, MovedAt)` group, gather the records that form a folder move (same source folder and target folder, same path below them; the source folder is the common folder of the sources, the target the common folder of the targets, and every record's relative path matches) into one row; every other record gets its own row. Writer label: `MovedBy` if it already contains `" ("`, otherwise `MovedBy + " (mv)"`.
- [ ] **Step 4: Run** `dotnet test`; expected PASS.
- [ ] **Step 5: Commit** `Show a restore's moves as single rows in folder history`.

### Task 6: Server instructions, the manual line, and the flaky secrets

**Files:**
- Modify: `src/Kumbu.Server/Program.cs`, `src/Kumbu.Core/Sh/Manual.cs`, `tests/Kumbu.Server.Tests/Search/SearchIndexTests.cs:59-69`, `tests/Kumbu.Server.Tests/Vaults/EncryptedNoteStoreTests.cs:~70-80`
- Test: `tests/Kumbu.Server.Tests/Mcp/McpClientTests.cs`

**Interfaces:**
- Produces: `public static class ServerInstructions { public const string Text = …; }` in `src/Kumbu.Core/Sh/Manual.cs` (next to the manual), set as `o.ServerInstructions` in `AddMcpServer`.

- [ ] **Step 1: Failing test** `InitializeReturnsTheInstructions` in `McpClientTests` (the client's `ServerInstructions` after connecting equals `ServerInstructions.Text` and contains `semsearch` and `rg`).
- [ ] **Step 2: Run**; expected FAIL.
- [ ] **Step 3: Implement**: the text exactly as in spec §3 (obs. 6); in the manual's command list area add the line `Which search: rg for exact words, names or a regex; semsearch for a question or topic in your own words, or in another language.` Replace the secrets in both sealed-storage tests with strings that can't occur in Base64 by chance (`Acme-Übernahme-Geheimprojekt`, `Geheimakte Übernahme`) and keep the note content consistent with them.
- [ ] **Step 4: Run** `dotnet test` three times; expected PASS each time.
- [ ] **Step 5: Commit** `Tell clients what Kumbu² holds; make the sealed-storage tests deterministic`.

### Task 7: The evaluation in the repository

**Files:**
- Create: `tools/search-eval/pyproject.toml`, `tools/search-eval/README.md`, `tools/search-eval/queries.py`, `tools/search-eval/run_eval.py`, `tools/search-eval/corpus/**` (39 notes), `tools/search-eval/.gitignore` (`.venv/`, `results*.json`)
- Source: the scratchpad evaluation (`eval/make_corpus.py`, `eval/queries.py`, `eval/run_eval.py`, `eval/corpus/`) — the controller passes its path.

- [ ] **Step 1: Copy** the corpus as files (not the generator) and the queries; add SPDX headers to the Python files.
- [ ] **Step 2: Rewrite the fusion in `run_eval.py`** to mirror Task 4 (cosine + 0.1 × share of query terms; digits kept in query terms; heading-only chunks are ranked but would be hidden in the output, so the metrics count them as they are ranked) and keep RRF as a reported variant `rrf (step 7)` for comparison. Models: `arctic-m` and `arctic-l` through ONNX from `--model-dir` (expects `<dir>/<key>/tokenizer.json` and `model_fp16.onnx`, the server's layout), plus `--compare` for the sentence-transformers models of the phase 7 notes (with `config_kwargs` `use_memory_efficient_attention=False, unpad_inputs=False` for the GTE-based arctic-m). Output: a table per model (R@1, R@3, MRR, meaning and hybrid, R@1 per category) and `results.json`.
- [ ] **Step 3: README**: what it measures, the caveat (37 queries, written by Claude), how to run (`uv run run_eval.py --model-dir <ModelPath>`; getting the model files from the server's `models` volume with `docker compose cp`), and "change this when you change `Chunker.cs`, `Ranking.cs` or the fusion in `Semsearch.cs`".
- [ ] **Step 4: Run** it against arctic-m from the server's volume; expected: meaning R@1 0.81, hybrid R@1 0.84 (the phase 7 numbers).
- [ ] **Step 5: Commit** `Add the search evaluation under tools/search-eval`.

### Task 8: Concept and CONCEPTS

**Files:**
- Modify: `docs/concept.md` §6 (RRF → score fusion, with the measurement; the model as a choice of two), `CONCEPTS.md` chapter on search (step 7's chapter: the fusion and why RRF lost; the model catalogue and re-embedding with encryption at rest), `README.md` (semsearch line mentions scores)

- [ ] **Step 1: Edit** the three documents; British English, the house style of the surrounding chapters (explain the why, Git analogies where they help).
- [ ] **Step 2: Commit** `Describe score fusion and the choice of model`.

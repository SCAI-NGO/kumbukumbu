# Embeddable properties: the data side — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A note's `summary` and `description` are embedded beside its sections, and a query note's `meaning:` ranks its rows by how close that property is to a text, on the server; offline it fails with `meaning-offline`.

**Architecture:** The search index stores one vector per text property in the note's sealed `NoteEmbeddings` blob. The query engines read `meaning:` from the frontmatter and take the vectors as an input of a run (`MeaningVectors`), so the cases give vectors and need no model. `query execute` gets them from a new `ISearchIndex.MeaningAsync`, which embeds the text once with the vault's provider.

**Tech Stack:** C# (.NET 10, xUnit, MongoDB), TypeScript (Vitest), YAML conformance cases.

**Spec:** `docs/superpowers/specs/2026-11-25-embeddable-properties-design.md` (decisions 1–8, §4, §5 steps 1–3). ADRs 0035, 0036. Decision 9 (similar notes and the graph) is not part of this plan.

## Global Constraints

- The properties are exactly `summary` and `description`. A value counts only when it is a text, as `Kumbu.Core.Queries.Properties` reads the note, and is not empty after trimming. Lists, numbers, dates and objects don't count.
- The text embedded is the note's title, `"\n"`, then the value, cut to the chunk bound as a section's core is cut. The title is the same note name a chunk's context starts with (`ChunkSplitter`'s `_title`).
- The hash is the same as a chunk's: the SHA-256 hex prefix of 32 characters (`SearchIndex.Hash`). Vectors are reused by hash within the vault and model, as a chunk's are.
- Property vectors live in the same sealed blob under the same context, `embeddings:{vaultId}:{pathId}`. A blob without the list reads as having none.
- `meaning:` is a map with exactly these keys:
  - `property`: `summary` or `description`.
  - `text`: a plain YAML scalar naming a declared parameter, or a quoted scalar that is the literal text.
  - `min`: optional, a number from -1 to 1.
  - `as`: optional, a name in the parameter-name shape `^[\p{L}_][\p{L}\p{N}_]*$`, default `closeness`.
  - Anything else gives the error code `meaning`.
- The text to embed is at most 2,000 characters; over that, or empty after trimming, the code is `param-value`.
- Error codes: `meaning`, `meaning-unavailable` (the provider fails or times out: 5 s, one retry), `meaning-offline` (TS, raised before any note is read).
- A score is the cosine of the two vectors as a number. It is null for a row without a vector. With `min`, a row below `min` or without a vector is dropped before the base's filters. The score shadows the row's own property named by `as`. `this` keeps its own properties.
- A run embeds one text; nothing else reaches the provider. A shared vault whose provider the member hasn't consented to (`ProviderConsented`) is never sent the text: `meaning-unavailable`.
- Don't touch the sync code: `packages/eidolon-core/src/{engine,api,live}` and `test/fakes`.
- Every source file starts with `// SPDX-License-Identifier: AGPL-3.0-or-later`. Write in British English. Add comments only where the reasoning isn't obvious.
- Behaviour goes into the conformance cases (`tests/query-conformance/`, format in its README); unit tests only cover what one engine has inside. Never change a case's expected values to fit an engine.
- Checks before each commit:
  - `dotnet test --project tests/Kumbu.Core.Tests -- --filter-namespace Kumbu.Core.Tests.Queries --filter-namespace Kumbu.Core.Tests.Sh`
  - for Task 1 and Task 3 also `dotnet test --project tests/Kumbu.Server.Tests` (the Search and Vaults tests at least)
  - `pnpm -C packages/eidolon-core test`
  - `pnpm -C packages/eidolon-core build`
- Commit messages are imperative and free-form, and end with the session's attribution lines.

## Review Focus

1. A note whose `summary` is a list, a number or blank has no vector, and its score is null. Task 1 tests storage; Task 2 has a case.
2. When only the body changes, the summary's vector is reused and no request is made for it. When only the summary changes, only the property text goes to the provider. Task 1.
3. A blob stored before this change (no property list) is brought up to date once at catch-up. Its chunk vectors are reused, so only the property texts are embedded. Task 1.
4. A stale vector is not used. If a note's summary was edited and not yet re-embedded, its stored vector's hash no longer matches the current text, so the note scores null rather than by the old summary. Task 3.
5. A shared vault whose member hasn't consented to its provider gives `meaning-unavailable`, and the provider gets no request. Task 3.

---

### Task 1: Store property vectors with the note's embeddings

**Files:**
- Create: `src/Kumbu.Core/Search/PropertyTexts.cs`.
- Modify:
  - `src/Kumbu.Server/Vaults/UnlockedVault.cs` (`EmbeddedChunk`/`NoteEmbeddings` records, `SealEmbeddings`, `OpenEmbeddings`, `EmbeddingsPayload`);
  - `src/Kumbu.Server/Search/SearchIndex.cs` (`IsCurrent`, `IndexAsync`, `EmbedAsync`, `Hash` visibility);
  - `docs/security-architecture.md` §5.5 ("What leaves the server" names the two properties' values).
- Test: `tests/Kumbu.Server.Tests/Search/SearchIndexTests.cs`, `tests/Kumbu.Core.Tests/Search/PropertyTextsTests.cs`.

**Interfaces:**
- Produces:
  - `public static class PropertyTexts`, with the constant `Names = ["summary", "description"]` and `public static IReadOnlyList<(string Name, string Text)> Of(VaultPath path, string content, ChunkBound bound)`. It returns the texts to embed, in `Names` order, and only for properties whose value is a non-blank `TextValue`.
  - `public sealed record EmbeddedProperty(string Name, string Hash, float[] Vector)`.
  - `NoteEmbeddings` gains a last optional parameter `IReadOnlyList<EmbeddedProperty>? Properties = null`.
  - `SearchIndex.Hash` becomes `internal static string Hash(string text)`.

- [ ] **Step 1: Write the failing tests**
  - `PropertyTextsTests`:
    - `Of_gives_title_and_value_for_text_summary_and_description`: for `---\nsummary: Offline sync conflicts\ndescription: How two copies meet\n---\n# X`, it gives `[("summary", "<title>\nOffline sync conflicts"), ("description", "<title>\nHow two copies meet")]`.
    - `Of_skips_lists_numbers_and_blank`: `summary: [a, b]`, `description: "  "` and `summary: 42` each give `[]`.
    - `Of_cuts_a_long_value_to_the_bound`: a 5,000-character value under `ChunkBound.Fallback` gives a text that `bound.Fits`.
  - `SearchIndexTests`, following the existing setup with a fake driver that counts the texts it is asked for:
    - `A_note_with_a_summary_stores_its_vector`: the stored embeddings have one `EmbeddedProperty("summary", …)`, and reopening the sealed blob gives the same.
    - `Editing_the_body_reuses_the_summary_vector`: no new embedding request contains the summary text.
    - `Editing_the_summary_embeds_only_the_summary`: the only text requested is the property text.
    - `A_blob_without_properties_is_caught_up_once`: store a `NoteEmbeddings` with `Properties: null` at the current version and run `CatchUp`. The note is re-indexed, only the property text is requested, and a second `CatchUp` queues nothing.
    - `Moving_a_note_keeps_its_property_vectors`: use the existing move path in the vault tests.
- [ ] **Step 2: Run them and see them fail.**
  - Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class *PropertyTextsTests`.
  - Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class *SearchIndexTests`.
  - Both fail: `PropertyTexts` doesn't compile, and the new SearchIndex tests fail.
- [ ] **Step 3: Implement.**
  - `PropertyTexts.Of` reads `Properties.Read(content)` and cuts like `ChunkSplitter` cuts a core. Reuse its helper; don't add a second one.
  - In `EmbedAsync`, take the property texts beside the chunks:
    - Hash them, and reuse known vectors from both the chunks' and the properties' hashes.
    - Request only the missing ones, in the same batches.
    - Store `Properties`.
    - The early return ("nothing changed") also compares the property hashes.
  - `IsCurrent` treats a note's embeddings with `Properties == null` as not current. A PDF's embeddings are exempt and stored with `Properties: []`.
  - The payload field is optional, so older blobs open.
- [ ] **Step 4: Run them and see them pass.** Run the same two commands, then the full `Kumbu.Server.Tests` Search and Vaults namespaces.
- [ ] **Step 5: Update §5.5 of `docs/security-architecture.md`, then commit.** §5.5 must say that the values of `summary` and `description` go to the vault's provider as its sections do.

### Task 2: `meaning:` in both query engines

**Files:**
- Modify:
  - C#: `src/Kumbu.Core/Queries/Query.cs`.
  - TS: `packages/eidolon-core/src/query/query.ts` and `packages/eidolon-core/src/query/index.ts` (exports).
  - The runners: `tests/Kumbu.Core.Tests/Queries/ConformanceTests.cs` and `packages/eidolon-core/test/query.conformance.test.ts`.
  - `tests/query-conformance/README.md` (format).
- Create: cases in `tests/query-conformance/queries/`: `meaning-ranks-by-closeness.yaml`, `meaning-min-and-missing-vectors.yaml`, `meaning-as-shadows-a-property.yaml`, `meaning-errors.yaml`, `meaning-text-limits.yaml`.

**Interfaces:**
- Produces (C#, and the same shape in TS: `QueryMeaning`, `MeaningVectors { query: Float32Array | number[]; byPath: ReadonlyMap<string, …> }`, `query.meaning`, `query.meaningText(args)`):
  - `public sealed record QueryMeaning(string Property, string? Param, string? Text, double? Min, string As)`. Exactly one of `Param` and `Text` is set.
  - `public sealed record MeaningVectors(float[] Query, IReadOnlyDictionary<string, float[]> ByPath)`, keyed by `QueryNote.Path`.
  - `Query.Meaning` is a `QueryMeaning?`, read in `Query.Read`. It throws `QueryError("meaning", …)` on a bad map.
  - `public string MeaningText(IReadOnlyDictionary<string, IReadOnlyList<string>> arguments)` returns the text to embed: the parameter's argument or default, or the literal. It throws `param-value` when the text is over 2,000 characters or blank, and `missing-param` / `unknown-param` as `Bind` does.
  - `Run(string? view, IReadOnlyDictionary<string, IReadOnlyList<string>> arguments, IEnumerable<QueryNote> notes, DateTime now, CancellationToken ct = default, MeaningVectors? meaning = null)`.
    - On a query with `Meaning` and `meaning == null`, C# throws `meaning-unavailable`.
    - In TS, `run` on a query with `meaning` always throws `meaning-offline`, before it reads a note.
  - `Check` works as before. It never needs vectors.

- [ ] **Step 1: Extend the format.** In `tests/query-conformance/README.md`, define two optional fields for `queries/` cases:
  - `vectors:` holds `query: [numbers]` and `notes: { <path>: [numbers] }`, the property vector of each note that has one.
  - `offline:` holds `{ error: <code> }`, the result the offline (TS) engine must give instead of the case's `expect`.

  Then teach both runners these fields. The C# runner passes `vectors` as `MeaningVectors`. The TS runner checks `offline` when it is present, and otherwise `expect`.
- [ ] **Step 2: Write the cases (`source: spec`), red on both engines.**
  - `meaning-ranks-by-closeness`: three notes with vectors `[1,0]`, `[0.6,0.8]` and `[0,1]`, and query `[1,0]`. Sort is `closeness DESC`, with `order: [file.basename, closeness]`. Rows come in that order with closeness `1`, `0.6` and `0`. `offline: {error: meaning-offline}`.
  - `meaning-min-and-missing-vectors`: with `min: 0.5`, the `0` note and a note without a vector are dropped. Without `min`, the note without a vector is a row with closeness null.
  - `meaning-as-shadows-a-property`: with `as: score`, a note's own `score: 99` reads as its cosine. `this.score` stays the query note's own value.
  - `meaning-errors`, one per §4 rule, each with expected `error: meaning`:
    - `meaning` is not a map;
    - an unknown key;
    - `property: tags`;
    - `text` names no declared parameter;
    - `min: 2`;
    - `min: high`;
    - `as: "1x"`.
  - `meaning-text-limits`: a 2,001-character argument gives `param-value`, and so does a blank argument. A literal `text: "offline sync"` runs.
  - Error cases fail at `Read`. They need no `offline` field, because both engines give the same code.
- [ ] **Step 3: See them fail.** Run the conformance tests of both engines (`--filter-class *ConformanceTests`, and `pnpm -C packages/eidolon-core test query.conformance`).
- [ ] **Step 4: Implement in C#, then TS.**
  - Parse `meaning:` from the frontmatter's YAML tree, as `Parameters` does: plain or quoted by `YamlScalarNode.Style`, and in TS by the `yaml` scalar's type.
  - In `Run`:
    - Wrap each note once per run into a `QueryNote` with the property `As` set to `NumberValue(cosine)` or `NullValue`. Keep one instance per file, because the Kept-field cache keys by reference.
    - With `Min`, drop rows below it or without a vector.
    - Cosine is `dot / (|a|·|b|)`, null when either norm is 0 or the lengths differ.
- [ ] **Step 5: See them pass, run the global checks, and commit.**

### Task 3: `query` reaches meaning on the server

**Files:**
- Modify:
  - `src/Kumbu.Core/Search/ISearchIndex.cs`;
  - `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs`;
  - `src/Kumbu.Core/Sh/Commands/QueryCommand.cs` (execute, inspect, check, `help query`);
  - `src/Kumbu.Core/Skills/System/create-query/SKILL.md`;
  - `README.md` (the queries section);
  - `packages/eidolon-core/README.md` (the "Queries" section mentions `meaning-offline`).
- Test: `tests/Kumbu.Core.Tests/Sh/QueryCommandTests.cs`, `tests/Kumbu.Server.Tests/Search/MeaningTests.cs` (new).

**Interfaces:**
- Consumes:
  - from Task 1: `PropertyTexts.Of`, `EmbeddedProperty`, `SearchIndex.Hash`;
  - from Task 2: `Query.Meaning`, `Query.MeaningText`, `Run(..., MeaningVectors?)`.
- Produces:
  - `public sealed record MeaningResult(SemanticState State, float[]? Query, IReadOnlyDictionary<VaultPath, float[]> Vectors, string? Reason)`.
  - `ISearchIndex.MeaningAsync(VaultPath vault, string property, string text, CancellationToken ct)`. Its default implementation returns `Off` with "search by meaning is off on this server".

- [ ] **Step 1: Write the failing tests.**
  - `MeaningTests`, with the server fixtures and the fake driver:
    - `Embeds_the_text_once_and_returns_current_property_vectors`.
    - `A_stale_property_vector_is_left_out`: the summary was edited and not yet re-indexed, so the stored hash ≠ the hash of `PropertyTexts.Of(current content)`.
    - `An_unconsented_shared_vault_sends_nothing`: the result is `Off`, and the driver gets no request.
    - `A_provider_that_times_out_twice_is_unavailable`: the driver hangs, then gives up after 5 s and one retry. Use the test clock or a short injectable timeout, as the existing search tests do.
  - `QueryCommandTests`:
    - `Execute_ranks_by_meaning` uses a fake `ISearchIndex`.
    - `Execute_without_a_provider_is_meaning_unavailable`.
    - `Inspect_shows_meaning` prints property, text, min and as.
    - `Check_warns_when_as_shadows_a_property_below_the_query_folder`: the warning names the note, and the query still checks.
    - `A_meaning_text_over_2000_is_param_value`.
- [ ] **Step 2: See them fail.**
- [ ] **Step 3: Implement.**
  - `EncryptedNoteStore.MeaningAsync`:
    - Resolve the one vault.
    - Apply `ProviderConsented` and the owner-away rule as `SearchAsync` does.
    - Embed the text with `EmbedQueryAsync`, wrapped in a 5 s timeout and one retry.
    - Return each note's `EmbeddedProperty` vector of the vault's current model, but only when its hash equals the hash of the current text. A vector of another length is left out.
  - `QueryCommand.Execute`:
    - When `query.Meaning` is set, compute `MeaningText` first, so `param-value` comes before any request.
    - Call `MeaningAsync` on the query's vault.
    - Map vault paths to `QueryNote.Path`.
    - Pass the vectors to `Run`. A state other than `Ready` is `meaning-unavailable` with the reason.
  - `query check`: warn when a note in the query's folder or below has a property named `as`.
  - `help query`, the skill and both READMEs:
    - Explain `meaning:`.
    - The skill teaches decision 10: suggest a new summary for one a person wrote, don't overwrite it.
- [ ] **Step 4: See them pass, run the global checks and the Server Search tests, and commit.**

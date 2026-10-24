# Search by Words and Meaning, and the Phase 6b Fixes — Design (step 7)

**Date:** 2026-10-24
**Scope:** `docs/concept.md` §12, step 7: first the decisions and fixes from [notes-test-phase6b.md](../../notes-test-phase6b.md), then embeddings and hybrid search (concept §6).
**Status:**
- Settled with Chris on 2026-10-24:
  - the three phase 6b design questions (in the notes)
  - one PR for the fixes and search
  - the model downloaded on the first start
- The rest is written by Claude for review with the PR.

## 1. The phase 6b fixes

| Obs. | Decision / fix |
|---|---|
| 1 | **A folder restore moves moved notes back** (Chris: option b).<br>A checkpoint path with no note now, whose move records lead (since the checkpoint, up to 10 hops) to a note that exists and isn't in the checkpoint at its own path, is moved back. The note keeps its history and gets its content then if that differs.<br>Links outside the scope that the move rewrote follow it back (the same rewrite as `mv`, from the new path to the checkpoint path). Notes inside the scope get their content then anyway.<br>The preview lists `moved back  new → old` and the links it rewrites, plus the links outside the scope that would lead nowhere afterwards (to a note going to the trash).<br>Everything is one transaction. `diff --checkpoint` on a folder shows such a note as `moved`. |
| 2 | **`replace` warns about links that would lead to another note** (Chris: warn): a third group, `would lead to another note: … to X (now Y)`. The groups are named `would lead nowhere or be ambiguous` and `would lead to another note`; a link that still leads to the same note is only counted. |
| 3 | **The preview names the notes named after the old text** (Chris: in): `Named after "Hecate": …/Hecate.md, …/Hecate Retrospektive.md`, with `Read them before applying …; to rename one, mv it first`. "Named after" means the old text is a whole word in the note's name; the whole vault is checked, not just the path. |
| 4 | `cat --checkpoint` on a note created since says `is not in checkpoint 'x' (created since)`. The hint without a path repeats the command with its arguments and names a vault that has the checkpoint. |
| 5 | `history <folder>` has a row per note that went to the trash: `rm`, or a restore (`dev (restore)  /Ideas/t6b/delta.md → trash`). |
| 6 | `rmcp` takes the vault or a path the checkpoint covers; otherwise `checkpoint 'x' covers …, not …; give its path or the vault`. |
| 7 | Wording: `now ambiguous: [[alpha]] in … (2 notes)`. Both messages of a failed folder restore say `changed or appeared since the preview`. |

Out of scope, as decided: contextual changes (a sentence that depends on a name without containing it) are the plugin package's job, not the tools'.

## 2. Search

| Topic | Decision | Why |
|---|---|---|
| Command | `semsearch [-n N] [--words\|--meaning] <query> [path]` in `sh`. The query is the operands before a trailing path; quoting is optional. Output: rank, `path#Section`, lines, and the start of the section's text (160 characters), then a footer saying how it ranked. | `rg` stays plain regex (concept §6). The address is what `cat` takes next. |
| Chunks | A section's own text, from its heading to the next heading of any level, so subsections are their own chunks; the text before the first heading (frontmatter excluded) is one chunk too. Over 2000 characters (about 512 tokens), a chunk is split between paragraphs. The embedded text is the context (note name, heading path) and the chunk. | Concept §6. A chunk is something `cat` can show. |
| Lexical | BM25 (`k1` 1.2, `b` 0.75) over chunk and context. Terms are Unicode letters and digits, NFKC, lowercase. Query terms drop English and German stop words and single characters. | Found in the live test: without stop words, "animals on a farm" ranked every note with "a" and "on" first by words. RRF counts ranks, so that buried the right note, which only matched by meaning. |
| Semantic | Dot product of unit vectors, over every chunk of the caller's vaults below the path, in memory. | Concept §6: no vector database; thousands of sections take milliseconds. |
| Fusion | Reciprocal Rank Fusion, `k` 60, top 50 of each list. | Concept §6. |
| Model | `Snowflake/snowflake-arctic-embed-m-v2.0`, ONNX fp16 (613 MB), pinned to revision `95c2741…`, Apache-2.0. Query prompt `query: `, documents without one. CLS pooling as exported (`sentence_embedding`), normalised. | Concept §6 (16-bit). Measured on CPU: fp16 takes about 22 ms per section. int8 is 8 ms, but its vectors agree with fp16 only at a cosine of 0.90 to 0.97, too far. |
| Delivery | Downloaded on the first start (Chris) into `Kumbu:Search:ModelPath` (`/models`, the `models` volume). Each file's size and SHA-256 are checked, and it is written as `.part` and renamed. If the download fails, the server retries every 10 minutes. `Kumbu:Search:Enabled=false` (`KUMBU_SEARCH=false`) switches it off. | The image stays small; after the first start everything is offline. |
| Tokenizer | Our own C# implementation of the model's `tokenizer.json`: the precompiled normaliser (darts-clone trie, per grapheme as Hugging Face does), WhitespaceSplit + Metaspace, Unigram Viterbi (unknown characters at the lowest score minus 10, consecutive `<unk>` fused), `<s> … </s>`, 512 tokens at most. | No native tokenizer dependency. `tokenizer-reference.json` holds ids from Hugging Face's `tokenizers` for 20 texts (umlauts, NFKC cases, emoji, CJK, Greek, Cyrillic, Arabic, code); the test matches all of them. |
| Runtime | `Microsoft.ML.OnnxRuntime` (CPU), half the cores by default (`Kumbu:Search:Threads`), batches of 8 sorted by length. | Leaves cores for requests while indexing. |
| Storage | `embeddings`: one sealed document per note under its path id (unique index), with the note's version and per chunk the start line, a hash of its text and the vector as 16-bit floats. The context binds it to the path id. Loaded into the working set on unlock and counted in its memory size. | Like notes (chapter 6). Half-floats halve the storage; search doesn't need more precision. |
| Indexing | A background worker with a queue (each note once). Every committed write queues its paths; an unlock or create of a vault queues every note whose version has no embeddings, and every embedding whose note is gone. Only chunks whose text hash is new to the vault are embedded. Until the model is ready the queue waits. | A write is never slowed by embedding. Nothing is missed across locks or restarts. |
| Pending | A search counts notes below the path whose current version isn't indexed yet and says so; they still count by words. | Honest about a lagging index. |
| Access | `semsearch` goes through the request's store, so it sees only the vaults the credential opens. | As every read. |

## 3. Testing

- **Core:**
  - chunks (frontmatter, levels, a long section split between paragraphs)
  - BM25 (rare terms, stop words)
  - RRF
  - `semsearch` with a fake index (fusion, fallback with the reason, `--meaning` when not ready, pending, errors)
  - the tokenizer on a tiny vocabulary, and on the real one against Hugging Face's ids (with `KUMBU_MODEL_DIR`)
  - the 6b fixes: moved back with links, `moved` in diff, links that would break outside, the third replace group, notes named after the text, small messages, trash rows
- **Server**, with a stand-in embedder (words hashed into vectors):
  - sections found
  - embeddings sealed, and nothing readable
  - a restart without embedding again
  - only changed sections re-embedded
  - removed notes leave
  - waiting while the model loads
  - another token finds nothing
  - the real model with `KUMBU_MODEL_DIR`: meaning across languages, unit vectors
- **MCP:** `semsearch` end to end.
- **Live in the sandbox:**
  - The runtime image: published on the host, since the sandbox's proxy certificate isn't trusted in the build container; the Dockerfile itself is unchanged apart from `/models`.
  - The full Compose stack with `mongo:7.0`.
  - The model download on an empty folder, with the checksums.
  - Queries in English and German (a German question finds the English note, an English one the German note).
  - The 6b fixes over MCP.

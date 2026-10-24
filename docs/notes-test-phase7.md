# Test notes: phase 7 (search by words and meaning, and the phase 6b fixes)

Date: 2026-10-24. Hands-on test of build order step 7 (PR #15, merged as `e4de344`): `semsearch` with BM25, embeddings (`snowflake-arctic-embed-m-v2.0`, ONNX fp16) and Reciprocal Rank Fusion, the background indexer, and the fixes for the phase 6b observations (issues #10–#14). Setup as in [phase 1](notes-test-phase1.md): stack on `127.0.0.1:8083`, rebuilt by Chris with `./update.sh`, vaults `Demo`, `Ideas` and `Technik`. `dotnet test`: 726 passed, 2 skipped (the real-model tests without `KUMBU_MODEL_DIR`); one run of four had a flaky failure (observation 7).

The live `tools/list` lists `semsearch <query> [path]  ranked search by words and meaning; finds the best sections`; `help semsearch` matches the spec.

## Results

### Model download and indexing

| Check | Result |
|---|---|
| First start | `tokenizer.json` and `model_fp16.onnx` (613 MB) downloaded into the `models` volume with progress in the log, then `Embedding model loaded (… at 95c27414)` |
| Memory | Server 1.0–1.2 GB with the model loaded (was about 200 MB) |
| A new note | Searchable within a second |
| 15 notes of 21 sections written at once | `15 notes aren't indexed yet and count by words only`, counting down by about one note a second (about 55 ms per section on half the cores) |
| Restart | Nothing embedded again; the first query after the start had no pending notes |
| `KUMBU_SEARCH=false` | By words, with `(ranked by words only: search by meaning is switched off on this server …)`; `--meaning` refused with the reason |
| Storage | `embeddings`: `_id`, `pid`, `vault`, `blob`, unique index on `vault` + `pid`; no plain text in a dump (note text, paths, names searched). 70 notes take 1.1 MB |
| Errors | Missing query, `-n 0`, `--words --meaning`, a missing path: each refused with the reason |

### Search quality, by hand

| Query | Result |
|---|---|
| „wer kümmert sich um den Lasttest" | The two meetings, the todo list and `ben.md` first |
| „who designed the rule language" (English, notes German) | `anna.md` first |
| „Zukunftsvisionen der Vergangenheit" (German, note English) | `retrofuturism.md` first and second |
| „why did we choose this heat pump", „warum haben wir uns für die Luft-Wasser-Wärmepumpe entschieden" | The decision of 2026-03-12 first, in an 862-line note |
| „Durchfluss 1204" | The one entry with that number first |
| „Warum heizen wir am Standort Nord nicht mit Erdwärme?" | **Not in the top 3** (observation 1); empty month headings among them (observation 2) |
| „topic 3" over notes `bulk0` … `bulk29` | `bulk1` first: the `3` is dropped (observation 4) |
| Every query | Ten results, the tail unrelated (observation 3) |

Every call took under 100 ms round trip. The section address in the results (`…#Projekt-Logbuch Wärmepumpe Standort Nord#März 2026#2026-03-12 Entscheidung Wärmepumpe`) works with `cat`, and so does the short form `#2026-03-12 Entscheidung Wärmepumpe`.

To rule out a pipeline bug behind the Erdwärme miss, the same model files ran in Python with Hugging Face's `tokenizers` and the official `onnxruntime`: the same order (a site entry at a cosine of 0.199, the decision at 0.147). The pipeline is right; the model doesn't connect „Erdwärme" with „Sole-Wasser … Bohrgenehmigung".

### The phase 6b fixes (issues #10–#14)

| Issue | Result |
|---|---|
| #10 folder restore and moves | A note moved within the folder and one moved out of it: the preview lists both as `moved back  new → old`, `diff --checkpoint` as `moved`. After the apply both notes are back with their history (an edit made after the move stays in it), and all four links in `/Ideas/r10out.md` are back in their original form (`[[y]]`, `[[r10/y\|Why]]`, `[[x#X]]`, `[[/r10/x]]`). But `history <folder>` shows the two moves as one wrong row (observation 5) |
| #11 links to another note | `Hermes` → `Merkur` with a note `Merkur.md` present: `3 links would lead to another note: [[Hermes]] → [[Merkur]]: to /Ideas/r11/Merkur.md (now /Ideas/r11/Hermes.md)`, separate from `1 link would lead nowhere or be ambiguous` |
| #12 notes named after the text | `Named after "Hermes": /Ideas/r11/Hermes Retro.md, /Ideas/r11/Hermes.md`, `Read them before applying: a sentence can depend on the name without containing it. To rename one, mv it first.` Excerpts now show each occurrence with its context |
| #13 small fixes | `is not in checkpoint 'r10cp' (created since)`; the hint without a path repeats the command (`rg --checkpoint r10cp Orion /Ideas`); `rmcp` with a path outside the checkpoint refused (`covers /Ideas/r10/, not …; give its path or the vault`); `now ambiguous: [[y]] in /Ideas/r10out.md (2 notes)`; both messages of a failed restore say `changed or appeared since the preview`; `Unlocked vault Technik (1 note)` |
| #14 trash rows | `dev  /Ideas/r10/new.md → trash` in `history <folder>` |

## Evaluation of the embedding model

Asked for by Chris after the Erdwärme miss: is the model the problem, and would another one be better?

**Setup** (scripts in the scratchpad, not in the repository; observation 8):

- Corpus: 39 notes, 238 sections. The seed vaults, the Hecate fixture from phase 6, and 24 notes written for the evaluation: a mixed second brain in German and English (recipes, travel, insurance, tax, car, health, garden; incidents, ADRs, a retro, a customer, onboarding; Rust, Kubernetes, photography, statistics). With deliberate distractors: a private heating note next to the heat pump log, two `Gitarre` notes in different senses.
- 40 queries, each with the section that answers it: exact terms (6), paraphrases in the same language (11), across languages (8), world knowledge (7, e.g. „Erdwärme" for brine-water, „Puddingtörtchen" for Pastéis de Belém, „Hochwasser" for Elementarschaden), vague (5), and 3 with no answer.
- Chunking, context, BM25, stop words and RRF exactly as `Chunker.cs` and `Ranking.cs`. The current model in its fp16 ONNX form as the server runs it; the others through `sentence-transformers` on the CPU.
- Measured: R@1 (the right section first), R@3, MRR, by meaning only and as Kumbu²'s hybrid.

**Caveat:** 37 queries with an answer means one query is 2.7 points. The corpus and the queries were written by Claude, who knew the models' weak spot from the Erdwärme case. The numbers show directions and large differences, not small ones.

### Models (with context, as Kumbu² chunks)

| Model | Size | Meaning R@1 | R@3 | MRR | Hybrid R@1 | ms per section |
|---|---|---|---|---|---|---|
| **arctic-embed-m-v2.0 (current)** | 305M | 0.81 | 0.92 | 0.88 | 0.70 | 26 |
| arctic-embed-l-v2.0 | 568M | **0.89** | **0.97** | **0.92** | 0.76 | 49 |
| bge-m3 | 568M | **0.89** | 0.95 | **0.92** | 0.76 | 48 |
| multilingual-e5-large-instruct | 560M | 0.86 | 0.89 | 0.89 | 0.68 | 151 |
| multilingual-e5-base | 278M | 0.84 | 0.86 | 0.87 | 0.73 | 14 |
| Qwen3-Embedding-0.6B | 600M | 0.76 | 0.92 | 0.84 | 0.68 | 34 |

(The fp32 control of the current model failed in its Hugging Face code with an index error; not needed for the comparison.)

### Variants of chunking

| Model | with context (Kumbu²) | without empty headings | without empty headings and context |
|---|---|---|---|
| current | 0.81 | 0.78 | 0.76 |
| arctic-l-v2 | 0.89 | 0.89 | 0.84 |
| bge-m3 | 0.89 | 0.86 | 0.81 |

(Meaning R@1.) **The context helps** for every model but Qwen3: keep it. Dropping heading-only sections (32 of 238) doesn't help the ranking; they are noise in the output, not in the ranking of the right section (observation 2).

### Fusion

| Variant (current model) | R@1 | R@3 | MRR |
|---|---|---|---|
| Meaning only | 0.81 | 0.92 | 0.88 |
| Words only (BM25) | 0.49 | 0.62 | 0.56 |
| **RRF k 60 (Kumbu² today)** | **0.70** | 0.89 | 0.81 |
| RRF with German question words as stop words | 0.70 | 0.89 | 0.81 |
| RRF with meaning weighted twice | 0.76 | 0.89 | 0.84 |
| **Cosine plus 0.1 × share of query terms in the section** | **0.84** | 0.92 | **0.89** |

With arctic-l-v2: meaning 0.89, RRF 0.76, cosine plus bonus 0.89.

**Why RRF loses here:** it counts ranks only, so a weak list weighs as much as a strong one. BM25 nearly always returns *some* list: „Warum ist die Datenbank im September ausgefallen?" matches `September` in the heat pump log and `Datenbank` in the budget, and a section that is mediocre in both lists beats the one that is first by meaning and absent from BM25 (the incident note is English). Of the 7 queries the hybrid made worse, 6 are across languages or need world knowledge, exactly where words can't help. The bonus of 0.1 was set once beforehand, not tuned on this set.

### Thresholds

The best cosine of a query with an answer: lowest 0.23, 10th percentile 0.28, median 0.48 (current model). Of a query with none: 0.18, 0.26, 0.42. **No absolute threshold separates them**; „Kubernetes autoscaling configuration" (no answer) scores 0.42 against the probes note. Cutting the list by a fixed score would drop right answers before it drops wrong ones.

## Blind run: two questions in other words

Date: 2026-10-24, 15:16 to 15:18 UTC. Setup as in phase 6b (`mktemp -d /tmp/notes.XXXXXX`, a start script with `--mcp-config <0600 file> --strict-mcp-config`, `/mcp` checked; token file deleted afterwards). Fixture: `/Technik/Projekte/Wärmepumpe Standort Nord.md` as seeded (862 lines, about 13,700 tokens, beyond the output limit).

**Prompt (Chris):** „Warum heizen wir am Standort Nord eigentlich nicht mit Erdwärme? Und ist das mit dem Lärm an der Grundstücksgrenze inzwischen geklärt?" Neither „Erdwärme" nor „Lärm" occurs in the notes; the answers are „Variante A (Sole-Wasser) verworfen: Bohrgenehmigung nicht vor 2027 realistisch" and „Schallmessung an der Grundstücksgrenze steht noch aus" (still open, last on 2026-10-20).

### What the agent did

1. Searched the empty working directory and its memory, then answered that it had no information and asked where to look (observation 6). Chris: „schau bitte im kumbu :-)".
2. Loaded the Kumbu² tools and ran **`rg -i -l "erdwärme|geotherm|wärmepumpe|lärm|schall|grundstücksgrenze"`**: its own synonyms. It found the log and `/Ideas/search/ctx/heizung.md`, a copy of the decision I had left there from the evaluation; the agent recognised it as a copy and cited the log.
3. `cat` of the log (outline, as it is too long), `cat` of the decision section, `rg -n -i "lärm|schall|dB|grundstücksgrenze|nachbar|erdwärme|sole|bohr"`, `head`, and the October section.
4. Answered both questions correctly, with the line numbers, and noticed the log entries dated after today (an artefact of the synthetic fixture) and another open point relevant for commissioning (the foundation's structural approval). It offered to write an overview.
5. After Chris's yes: one `edit` adding a section „Offene Punkte (Stand 2026-10-24)" with a table and an Obsidian warning callout on the dates, then checked its own table against the log and fixed two dates with two more edits.

**It never used `semsearch`.** 2 minutes, 14 Kumbu² calls.

### Result

| Check | Result |
|---|---|
| Erdwärme | Correct: the permit for drilling, not technology or cost; the chosen variant and the rejected hybrid named too |
| Noise | Correct: open since 2026-01-10, last on 2026-10-20; „Geräusche weg" correctly attributed to the hydraulics |
| The table it wrote | All four rows right (first and last mention of each open point, checked against the seed file) |
| History | One version from the agent's changeset on top of the seed |

### What this means

- **An agent finds synonyms `rg` can use better than the model finds meaning.** `sole|bohr` is the world knowledge the embedding model lacked. On 40 notes `rg` with a few alternatives is enough; `semsearch` earns its place where `rg` returns too much or where the agent can't guess the words (large vaults, other languages). The manual says what `semsearch` does, not when to prefer it.
- The tool output (outline for the long note, section addresses, line numbers) carried the run, as in phase 4b.

## Observations from the agent's side

1. **Search by meaning misses what needs world knowledge.** „Erdwärme" for brine-water and drilling, „when is the heat pump supposed to start operating" for „Inbetriebnahme geplant" (rank 23 and 110 for the current and the large model: the decision section covers five topics). The larger multilingual models do better overall (R@1 0.89 against 0.81, R@3 0.97 against 0.92) at twice the time per section and about twice the memory (568M parameters). Design question 2.
2. **Heading-only sections clutter the results.** `## Februar 2026`, `# Another alpha`, `# Meeting Notes` (one line each, 13 % of all sections in the evaluation corpus) show up in the top 3 of unrelated queries. They don't push the right section down (the evaluation shows no gain from dropping them), but they waste result lines and look like hits. Suggest: don't list a section whose text is its heading only; its subsections are listed anyway.
3. **The list is always filled to ten, with no sign of where relevance ends.** The evaluation shows that no absolute score threshold works. Suggest instead: print the score (or a coarse `strong`/`weak` from the gap to the first result) and stop the list where the score drops sharply, so an agent sees „three hits and noise" rather than „ten hits". The UI spec wants a score bar and a reason too (`concepts/MOBILE_EDITOR_v1.md` §4).
4. **Single characters are dropped from queries, digits included.** „topic 3", „KW 8", „Schritt 7", „Variante B" lose their distinguishing part. Suggest: keep single digits (and possibly single capital letters next to a word). The German stop words also lack question words (`warum`, `wann`, `wer`, `welche`, `keine`); adding them changed nothing measurable, so low priority.
5. **`history <folder>` merges the moves of a restore into one wrong row.** A folder restore that moved `/Ideas/elsewhere/x2.md` back to `/Ideas/r10/x.md` and `/Ideas/r10/sub/z.md` back to `/Ideas/r10/y.md` shows `dev (restore) (mv)  /Ideas/ → /Ideas/r10/  2 notes`: a folder move that never happened, from the common prefix of the sources, and a writer label with two parentheses. Expected: one row per note move, `dev (restore)`.
6. **An agent doesn't know that Kumbu² holds the user's notes until told.** In this run and in phase 6b the agent first looked in the working directory and answered „no information", then searched Kumbu² at the user's hint. The tools were deferred, so the agent saw only their names. MCP has a place for this: the `instructions` the server returns on `initialize`, which clients show to the model (the other servers in these sessions use it). Suggest a short one: „Kumbu² holds the user's notes (vaults of Markdown). For questions about their projects, decisions, people or past notes, search here first: `sh` with `semsearch` or `rg`."
7. **A flaky test.** `SearchIndexTests.Embeddings_are_sealed_and_survive_a_restart_without_embedding_again` failed once in four runs: it checks that `Acme` doesn't occur in the dump, case-insensitively, and found it in random Base64 (`…ieHoacmesje…`). A four-letter word in about 3 KB of Base64 occurs by chance in roughly one run of 300; `EncryptedNoteStoreTests` line 79 checks the same way. Suggest secrets that can't occur by chance (`Acme-Übernahme-Geheimprojekt`), or checking the decoded blob only.
8. **The evaluation should live in the repository.** Every later change to chunking, fusion or the model needs the same measurement. Suggest `tools/search-eval/` (corpus, queries, script; run with `uv`), not part of `dotnet test`.

## Design questions for Chris

1. **Fusion (observations 1, 3).** Replace RRF with the cosine plus a word bonus, which measured best for both the current and the large model? It also gives a score to print. The concept (§6) says RRF; the measurement says it hurts here.
2. **Model (observation 1).** Stay with arctic-m-v2 (305M, 26 ms per section, about 1 GB of server memory), or switch to arctic-l-v2 or bge-m3 (568M, about 49 ms, roughly twice the memory; +8 points R@1, +5 R@3)? A switch re-embeds every note once. arctic-l-v2 is the same family (same prompt, an ONNX export to check) and was best on R@3.
   **Decided by Chris (Oct 24, 2026): configurable, both variants offered.** The deployer chooses. Proposal for the design, to settle in the next phase:
   - A fixed list of known models (`arctic-m`, `arctic-l`), each with a pinned revision, SHA-256 per file, prompt, pooling and dimension; chosen with `Kumbu:Search:Model` / `KUMBU_SEARCH_MODEL`. No free Hugging Face ids: the checksums keep a foreign model out, and the evaluation covers exactly what runs. Only the chosen model is downloaded.
   - Each embedding records its model. After a switch, embeddings of another model count as missing: the indexer re-embeds them in the background when a vault is unlocked, the same path that today embeds notes without embeddings. **Encryption at rest needs nothing new**: embedding needs the plain text, so it happens only in an open vault, as now; a locked vault is re-embedded when it is next opened, and until then nobody can search it anyway.
   - Vectors of two models are never mixed (different spaces, 768 and 1024 dimensions). Sections not yet re-embedded count as pending and are found by words only, with the existing `N notes aren't indexed yet` line.
   - Cost: about 49 ms per section with arctic-l on half the cores, so a vault of 10,000 sections takes about 8 minutes; the old vectors are dropped as the new ones arrive.
3. **The evaluation in the repository (observation 8)?**
4. **`instructions` on `initialize` (observation 6)?**

## Suggested next step

1. Chris's decisions on the four questions above.
2. A short follow-up to step 7: fusion and model as decided, observations 2–7, the evaluation in the repository; then step 8 (users, admin, invites, API keys).
3. Housekeeping: the test notes in `/Ideas` (`t6b/`, `r10/`, `r11/`, `search/`, …), 7 checkpoints, and the agent's new section in the heat pump log; a `down -v` before the next phase resets everything (it removes the `models` volume too, so the next start downloads the model again).

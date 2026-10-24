# Test notes: phase 7b (search tuning, a choice of model, the phase 7 fixes)

Date: 2026-10-24. Built and tested locally while Chris was away (branch `step-7b`, 13 commits from `9f74ff3` to `b975b3a`), from the plan `docs/superpowers/plans/2026-10-24-search-tuning.md` and the spec `docs/superpowers/specs/2026-10-24-search-tuning-design.md`. Each of the 8 tasks was implemented by a subagent and reviewed by another, then the whole branch by a final reviewer; its findings were fixed in one pass and re-reviewed. `dotnet test` with `KUMBU_MODEL_DIR` (both models): 752 passed, none skipped.

The stack was rebuilt from the branch with `docker compose up -d --build` on the existing data (vaults `Demo`, `Ideas`, `Technik` with the phase 7 test notes and embeddings).

## Results

### Upgrade from step 7

| Check | Result |
|---|---|
| Model files of step 7 directly in `/models` | Moved into `/models/arctic-m/` on the first start; no download |
| Embeddings of step 7 (no model recorded) | Read as `arctic-m`; nothing re-embedded |
| `initialize` | Returns the `instructions` exactly as in the spec |

### semsearch with arctic-m

| Query | Result |
|---|---|
| „who designed the rule language" | `anna.md` first, `score 0.32`, then `0.23`, `0.22` |
| `--words Durchfluss 1204` | The entry with 1204 first; no scores on the lines |
| „topic 3" over `bulk0` … `bulk29` | `bulk3` first (`0.59`), then the other notes about topic 3: the digit counts now (was `bulk1` in phase 7) |
| „Warum heizen wir am Standort Nord nicht mit Erdwärme?" | Still not in the top 4: the model's limit, as measured in phase 7 |

### Switching to arctic-l and back

| Step | Result |
|---|---|
| `KUMBU_SEARCH_MODEL=arctic-l`, `docker compose up -d` | `tokenizer.json` (17 MB) and `model_fp16.onnx` (1134 MB) downloaded into `/models/arctic-l`, checksums checked, model loaded 17 s after the start |
| Catch-up | `Vault Demo: 13 notes to embed with arctic-l (were arctic-m)`, Ideas 69, Technik 1 |
| A search before anything is re-embedded | `(ranked by words only: 69 notes aren't indexed yet)`: words rank while no section has a vector of the new model |
| During the catch-up | `… 63 notes aren't indexed yet and aren't ranked by meaning; semsearch --words finds them`, counting down to none in about a minute (83 notes) |
| Erdwärme with arctic-l | The decision of 2026-03-12 third (`0.44`, `0.43`, `0.43`): the close scores show the model is unsure |
| „Zukunftsvisionen der Vergangenheit" | `retrofuturism.md` first and second (`0.46`), then `0.24`: a clear drop |
| Memory | 3.0 GB with arctic-l, against 1.0–1.2 GB with arctic-m |
| Back to arctic-m | `… notes to embed with arctic-m (were arctic-l)`, all re-embedded, results as before |
| The volume afterwards | Both models kept: `/models/arctic-l` 1.1 GB, `/models/arctic-m` 602 MB (observation 2) |

### The phase 7 fixes

| Obs. | Result |
|---|---|
| 2 (heading-only sections) | Not listed any more; the ranking is unchanged |
| 3 (where relevance ends) | Every hybrid or meaning result shows its score |
| 4 (single digits) | Kept, see „topic 3" |
| 5 (history rows) | `mv /Ideas/proj/ /Ideas/archive/proj/` shows `dev (mv)  /Ideas/proj/ → /Ideas/archive/proj/  2 notes`; a restore's moves are single rows (tests) |
| 6 (instructions) | See the blind run |
| 7 (flaky tests) | The secrets are long and contain an umlaut; three full runs green |
| 8 (evaluation) | `tools/search-eval/`: arctic-m meaning 0.81, hybrid 0.84 (RRF of step 7: 0.70); arctic-l 0.89 and 0.89 (RRF 0.76) |

## Blind run: does the agent find Kumbu² by itself?

Headless this time (`claude -p` in an empty `mktemp` directory, `--mcp-config <0600 file> --strict-mcp-config --permission-mode default --allowedTools mcp__kumbu`; token file deleted afterwards), since Chris was away. Nobody had to answer a question.

**Prompt:** „Wer hat sich eigentlich die Regelsprache für unser Gateway ausgedacht?" No word about Kumbu², notes or where to look; the notes say „Regel-DSL", „Gate-Service", „Janus".

**What the agent did:** loaded `mcp__kumbu__sh` and ran **`semsearch wer hat die Regelsprache für das Gateway entworfen`**, then answered. Two Kumbu² calls in all (the tool search and one `semsearch`), three turns.

**Result:** correct („Anna … Hat die Regel-DSL von Janus entworfen", with `anna.md`, the architecture section of `Janus.md` and the retrospective as sources), and it offered to search further if another gateway was meant. The session's prompt contained the server's instructions („Kumbu² holds the user's notes …").

**What this means:** in phase 6b and 7 the agent first searched the empty working directory and answered „no information" until told „in kumbu". The instructions fixed that, and the agent reached for `semsearch` first, for a question in words that are not in the notes.

## Observations from the agent's side

1. **Switching models costs a minute of weaker search, and the output says so honestly.** Right after the switch, words rank alone; then the hybrid covers more notes each second. A deployer switching on a large vault should know it: about 49 ms per section with arctic-l on half the cores.
2. **The old model stays in the volume.** After switching back, `/models/arctic-l` (1.1 GB) remains. That makes a second switch instant, but it is disk space nobody asked to keep. Suggest: a line in the README (`docker compose exec kumbu rm -r /models/arctic-l` frees it), or remove other models' folders on start. I'd document it rather than delete automatically.
3. **`1 notes to embed`.** The catch-up log line has no singular.
4. **The scores are useful, also when they are close.** `0.44 / 0.43 / 0.43` for the Erdwärme question says „not sure" better than any wording could; `0.46 / 0.46 / 0.24` says „two hits, then nothing".

## Decisions taken on Chris's behalf (while he was away)

Recorded in the plan workspace as rulings; listed here so they can be undone:

1. **Fallback to words when nothing has a vector yet** (found by the final review): after a model switch, `semsearch` printed „nothing found" even for words in the notes. It now ranks by words and says so. The spec only said that pending sections don't join the hybrid list. If unwanted: drop the fallback, and the footer's `--words` hint remains.
2. **No `last_hidden_state` fallback in the loader:** both models export `sentence_embedding`; the loader requires it and checks the dimension. A future model without it fails loudly.
3. **Test names follow the file's existing style** (one task had mixed PascalCase into a snake_case file).
4. **The evaluation's expected numbers were accepted within ±0.03**; they came out exact.
5. **Cheap minors of the reviews fixed in the final pass:** dead code in `Semsearch`, the last short test secrets, the heading-only check using `Outline`'s heading rule, a read-only model folder no longer stopping the server (search by meaning is then off, with the reason).
6. **One documentation fix verified by me instead of a second review round:** the three unanswerable queries' best cosines (0.18, 0.26, 0.42), which `notes-test-phase7.md` had phrased as if they were percentiles; corrected there too.

## Suggested next step

1. Chris reviews and merges the PR for step 7b.
2. Observations 2 and 3 when convenient (README line, singular).
3. Build order step 8 with GitHub login (concept §8, direction of 2026-10-24), aiming for a live demo on Tuesday as a nice-to-have.

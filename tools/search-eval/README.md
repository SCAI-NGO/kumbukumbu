# Search evaluation

Measures how well `semsearch` finds the right section of a note, for the embedding models the server can run. It exists
so that a change to the chunking, the ranking or the model is judged by numbers, not by a feeling from a few searches.

## What it measures

`corpus/` holds 39 Markdown notes (German and English, with umlauts, in the folder layout of a real vault).
`queries.py` holds 40 queries; for 37 of them it names the section that answers it (the "gold" section), and the last
three have no answer at all. The queries come in categories:

| Category | Kind of query |
|---|---|
| L | exact terms, names, numbers (what `rg` would find too) |
| P | a paraphrase in the same language |
| C | a question in the other language than the note |
| W | needs world knowledge (the note says it differently) |
| V | vague, or a similar section nearby |
| N | nothing answers it (not counted in the metrics) |

For each model it prints, for the 37 queries with an answer:

- **R@1, R@3**: the share of queries whose gold section is the first, or among the first three, results.
- **MRR**: the mean of 1 / rank of the gold section; it also rewards rank 2 over rank 5.
- **R@1 per category**, to see where a model or a fusion is weak.

Three rankings are compared: `meaning` (cosine similarity only, `semsearch --meaning`), `hybrid` (what `semsearch`
does: cosine plus 0.1 x the share of the query's words in the section) and `rrf (step 7)`, the rank fusion of
BM25 and meaning that `semsearch` used before step 7b, kept to see what the change brought.

Heading-only sections are ranked like any other: the server ranks them too and only hides them in its output, so a
gold hit on one counts as it is ranked.

## The caveat

37 queries are few: one query is 2.7 percentage points of R@1, so differences of a point or two mean nothing. The
corpus and the queries were written by Claude, not taken from real use, and they were written while looking at what
the search found, so they favour what it already does. Read the numbers as a regression test and a rough comparison
between models, not as a measure of quality in your vault.

## Running it

The models are the server's own files. Copy them out of the `models` volume of a running server (layout
`<ModelPath>/<key>/tokenizer.json` and `model_fp16.onnx`):

```sh
mkdir models
docker compose cp kumbu:/models/arctic-m models/arctic-m
docker compose cp kumbu:/models/arctic-l models/arctic-l   # only if the server has downloaded it
```

Then, from this folder (needs [uv](https://docs.astral.sh/uv/) and Python 3.12 or later):

```sh
uv run run_eval.py --model-dir models
uv run run_eval.py --model-dir models --models arctic-m    # one model only
uv run --extra compare run_eval.py --model-dir models --compare   # plus other models (downloads them, needs torch)
```

`--compare` takes names (`--compare bge-m3 e5`) to run only some: arctic-m and arctic-l as fp32 through
sentence-transformers, bge-m3, multilingual-e5-large-instruct, multilingual-e5-base and qwen3-embedding-0.6b.
Embedding the 238 sections takes about 6 s (arctic-m) or 15 s (arctic-l) on a CPU.

An external provider with the OpenAI embeddings API runs as the server's `openai-compatible` driver calls it. The key
is read from a file (`--openai-key-file`, or `$KUMBU_TEST_TENSORX_KEY_FILE`), never given on the command line:

```sh
KUMBU_TEST_TENSORX_KEY_FILE=~/keys/.tensorx-api-key uv run run_eval.py --model-dir models --models arctic-l \
  --openai-url https://api.tensorx.ai/v1 --openai-model qwen/qwen3-embedding-8b --openai-variants 1024 1024+prompt \
  --mixed arctic-l "qwen3-embedding-8b@1024 +prompt"
```

Each variant is the dimensions to ask for (`0`: the model's own), `+prompt` puts Qwen3-Embedding's query instruction
before the queries. The provider's vectors are kept in `cache/` (not committed: it holds the corpus' vectors), so a
second run sends only what is new. `--mixed A B` splits the corpus into vaults (its top-level folders), embeds every
other one with B and the rest with A, and ranks across them as the server does; `docs/notes-search-external.md`
has the results of 25 October 2026.

The tables go to the terminal; `results.json` has the same numbers plus the rank of the gold section for every query
(`results*.json` is not committed).

## Reference baseline

On 24 October 2026, with the fp16 ONNX models of the server's catalogue, 238 sections:

| Model | Ranking | R@1 | R@3 | MRR |
|---|---|---|---|---|
| arctic-m | meaning | 0.81 | 0.92 | 0.88 |
| arctic-m | hybrid | 0.84 | 0.92 | 0.89 |
| arctic-m | rrf (step 7) | 0.70 | 0.89 | 0.81 |
| arctic-l | meaning | 0.89 | 0.97 | 0.92 |
| arctic-l | hybrid | 0.89 | 0.97 | 0.92 |
| arctic-l | rrf (step 7) | 0.76 | 0.92 | 0.84 |

If a change moves these by more than a few points, find out why before accepting it.

## When to change it

This tool carries its own copy of the logic it measures. Change `run_eval.py` when you change:

- `Chunker.cs`: `chunks_of` mirrors how notes are split into sections and what text is embedded (title and heading
  path, then the section).
- `Ranking.cs`: the stop words, `query_terms`, `WORD_BONUS` and `fusion_scores` mirror `Bm25` and `Fusion`.
- the fusion in `Semsearch.cs`, or the models in `ModelFiles` (the query prompt `query: ` and the output
  `sentence_embedding`).

Add queries to `queries.py` when a real search goes wrong; keep the gold section as `(path, heading)`, or
`("*", text)` for every section containing the text.

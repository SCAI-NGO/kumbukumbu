# Search with an external embedding provider: evaluation notes

25 October 2026. Track T2 of the overnight round: can a vault that opts in to an external provider search as well as
one on the internal model, and does cross-vault search still rank sensibly when vaults use different models?

## Setup

- The search evaluation of step 7b (`tools/search-eval`, its README explains the corpus and the metrics): 39 notes,
  238 sections, 40 queries, 37 of them with a gold section.
- Internal models: arctic-m and arctic-l, the fp16 ONNX files of the server's catalogue.
- External: TensorX (`https://api.tensorx.ai/v1`), model `qwen/qwen3-embedding-8b`, through the same request the
  server's `openai-compatible` driver sends (`run_eval.py --openai-url …`). Variants: the model's full 4096 values
  and 1024 (`dimensions`), each with and without an instruction before queries.

```sh
cd tools/search-eval
KUMBU_TEST_TENSORX_KEY_FILE=~/keys/.tensorx-api-key uv run run_eval.py --model-dir <models> \
  --openai-url https://api.tensorx.ai/v1 --openai-variants 4096 4096+prompt 1024 1024+prompt \
  --mixed arctic-l "qwen3-embedding-8b@1024 +prompt"
```

The query instruction is Qwen3-Embedding's documented form, worded for notes:
`Instruct: Given a search query, retrieve the note sections that answer it\nQuery: ` (the driver's `QueryPrompt`).

## Results

| Model | Ranking | R@1 | R@3 | MRR | C | L | P | V | W |
|---|---|---|---|---|---|---|---|---|---|
| arctic-m | hybrid | 0.84 | 0.92 | 0.89 | 0.62 | 1.00 | 0.82 | 1.00 | 0.86 |
| arctic-l | hybrid | 0.89 | 0.97 | 0.92 | 0.88 | 1.00 | 0.91 | 0.80 | 0.86 |
| qwen3-8b @4096, no prompt | hybrid | 0.62 | 0.86 | 0.75 | 0.50 | 0.67 | 0.73 | 0.60 | 0.57 |
| qwen3-8b @4096 + prompt | hybrid | **0.92** | 0.95 | **0.94** | 0.75 | 1.00 | 0.91 | 1.00 | 1.00 |
| qwen3-8b @1024, no prompt | hybrid | 0.68 | 0.84 | 0.77 | 0.62 | 0.67 | 0.73 | 0.60 | 0.71 |
| qwen3-8b @1024 + prompt | hybrid | 0.89 | 0.95 | 0.93 | 0.75 | 0.83 | 0.91 | 1.00 | 1.00 |

Meaning alone (no word bonus): arctic-l 0.89 / 0.97 / 0.92, qwen3 @4096 + prompt 0.89 / 0.92 / 0.92, @1024 + prompt
0.89 / 0.92 / 0.92. Best cosine of a query with an answer, median, against the highest cosine of a query without one:
arctic-l 0.478 / 0.396, qwen3 @1024 + prompt 0.633 / 0.468, qwen3 @1024 without prompt 0.628 / 0.658.

What this says, with the README's caveat (37 queries; one query is 2.7 points of R@1):

- **The query instruction is not optional for Qwen3-Embedding.** Without it, R@1 falls by a quarter, and a query
  without an answer scores higher than a typical one with an answer. Configure `QueryPrompt` (the README's TensorX
  example does).
- **With it, qwen3-embedding-8b is as good as arctic-l, not better.** 0.92 against 0.89 R@1 is one query. It is
  stronger on world knowledge and vague queries (W, V: every one), weaker across languages (C: 0.75 against 0.88) and
  on exact terms at 1024 (L). For a German/English vault, arctic-l on the server is the equal of an 8B model in the cloud.
- **1024 values are enough.** One query lost against 4096, at a quarter of the memory per section in the working set
  (2 KB against 8 KB as half floats) and a quarter of the sealed embeddings in MongoDB. The README suggests `Dimensions: 1024`.
- Speed: 238 sections took 10 s (1024 values) to 19 s (4096) over the network, 41 to 81 ms per section in batches of
  32 (rate limit: 60 requests a minute), about what arctic-l takes on a CPU (82 ms); a query adds one round trip to a
  search (0.28 to 0.38 s in five tries from Germany).

## Cross-vault search with two models

A user's vaults may use different models: say "Arbeit" stays internal and "Privat" opted in to TensorX. A search over
all vaults then has cosines from two models in one list. How the server combines them
(`EncryptedNoteStore.SearchAsync`, `SectionSearch.RankByMeaning`): the query is embedded once per provider in scope,
each vault's sections get the cosine with *their* model's query vector, and the hybrid score is that cosine plus the
word bonus (0.1 × the share of the query's words in the section), all sorted together. The raw cosines of two models
are not on one scale: qwen3's run higher (median best 0.63 against 0.48).

`--mixed A B` measures what that costs: each top-level folder of the corpus is a vault, every other one embedded by B,
the rest by A, and the other way round. "standardised" maps each model's cosines per query to the two models' common
mean and spread before the bonus, the obvious correction.

| A (internal) | B (TensorX) | Vaults on B | raw R@1 / R@3 / MRR | standardised |
|---|---|---|---|---|
| arctic-l | qwen3 @1024 + prompt | Demo, Lernen, Technik | 0.86 / 0.95 / 0.91 | 0.84 / 0.95 / 0.89 |
| arctic-l | qwen3 @1024 + prompt | Arbeit, Ideas, Privat | 0.86 / 0.89 / 0.89 | 0.86 / 0.92 / 0.90 |
| arctic-m | qwen3 @1024 + prompt | Demo, Lernen, Technik | 0.84 / 0.89 / 0.87 | 0.81 / 0.92 / 0.88 |
| arctic-m | qwen3 @1024 + prompt | Arbeit, Ideas, Privat | 0.84 / 0.89 / 0.88 | 0.86 / 0.92 / 0.90 |

Mixed, R@1 lands between the two models alone (0.84 to 0.89), a query or two below the better one; the top hit came
from qwen3's vaults for 14 or 15 queries where 13 had their answer there (27 for 25 the other way): a slight pull
towards the model whose cosines run higher, worth about one query. Standardising doesn't do better than that noise
(±1 query either way), and it needs every section's cosine of both models per query to compute. **Decision: the server
compares raw cosines across vaults**; the cost is stated here and in the README. If real use shows one vault's model crowding out another's, standardising per provider in
`EncryptedNoteStore.SearchAsync` is the place, and this experiment the test for it.

## The integration test

`OpenAiCompatibleDriverTests.TensorX_ranks_by_meaning` runs the driver against TensorX with the README's settings
when `KUMBU_TEST_TENSORX_KEY_FILE` names a key file (skipped otherwise): German farm animals against an English
query, 1024 values, unit vectors, the key in no log line. It passed on 25 October 2026.

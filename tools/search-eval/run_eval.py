# SPDX-License-Identifier: AGPL-3.0-or-later
"""Evaluates Kumbu²'s search on the corpus in ./corpus with the queries in queries.py.

Chunking, query terms, stop words and the fusion mirror src/Kumbu.Core/Search (Chunker.cs, Ranking.cs) and
src/Kumbu.Core/Sh/Commands/Semsearch.cs. Change this file when you change those.

    uv run run_eval.py --model-dir <ModelPath>                 # arctic-m and arctic-l, as the server runs them
    uv run --extra compare run_eval.py --model-dir <ModelPath> --compare   # plus other models, for comparison
"""
import argparse
import json
import math
import os
import pathlib
import re
import sys
import time
import unicodedata

import numpy as np

HERE = pathlib.Path(__file__).parent
sys.path.insert(0, str(HERE))
from queries import QUERIES  # noqa: E402

# --- chunking (Chunker.cs) -------------------------------------------------------------------------
HEADING = re.compile(r"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")
MAX_CHARS = 2000  # Chunker.MaxChars


def chunks_of(rel, text):
    """Sections of a note, split like Chunker.Split. Heading-only sections stay in: the server ranks them too (it only hides them in the output)."""
    lines = text.split("\n")
    start = 0
    if lines and lines[0].strip() == "---":
        for i in range(1, len(lines)):
            if lines[i].strip() == "---":
                start = i + 1
                break
    heads, fence = [], False
    for i in range(start, len(lines)):
        if lines[i].lstrip().startswith("```"):
            fence = not fence
            continue
        m = None if fence else HEADING.match(lines[i])
        if m:
            heads.append((i, len(m.group(1)), (m.group(2) or "").strip()))
    title = pathlib.PurePath(rel).name[:-3]
    out = []

    def emit(heading, ctx, a, b):
        while b > a and not lines[b - 1].strip():
            b -= 1
        if a >= b:
            return
        text = "\n".join(lines[a:b])[:MAX_CHARS * 2]  # a single huge line is cut
        out.append({"path": rel, "heading": heading, "text": text, "context": ctx, "lines": (a, b)})

    def add(heading, chain, a, b):
        """Chunker.Add: a section over MAX_CHARS is split at the last blank line before the limit, else at the line."""
        while b > a and not lines[b - 1].strip():
            b -= 1
        while a < b and not lines[a].strip():
            a += 1
        if a >= b:
            return
        ctx = title if heading is None else title + " > " + " > ".join(chain)
        part, size, last_break = a, 0, -1
        for i in range(a, b):
            if size > 0 and size + len(lines[i]) + 1 > MAX_CHARS:
                cut = last_break if last_break > part else i
                emit(heading or "", ctx, part, cut)
                part = cut
                while part < b and not lines[part].strip():
                    part += 1
                size = sum(len(l) + 1 for l in lines[part:i])
                last_break = -1
            if not lines[i].strip():
                last_break = i
            size += len(lines[i]) + 1
        if part < b:
            emit(heading or "", ctx, part, b)

    add(None, [], start, heads[0][0] if heads else len(lines))
    stack = []
    for k, (line, level, name) in enumerate(heads):
        while stack and stack[-1][0] >= level:
            stack.pop()
        stack.append((level, name))
        end = heads[k + 1][0] if k + 1 < len(heads) else len(lines)
        add(name, [n for _, n in stack], line, end)
    return out


# --- words and fusion (Ranking.cs, Semsearch.cs) ---------------------------------------------------
STOP = set("""a an and are as at be but by can do does for from has have how i if in into is it its my of on or our so that the
their then there these this to was we were what when where which who why will with you your aber als am an auch auf aus bei
bin bis da das dass dem den der des die doch du ein eine einem einen einer es für hat ich ihr im in ist ja kann mit nach
nicht noch nur oder sich sie sind so um und uns von vor war was wie wir wo zu zum zur""".split())
WORD = re.compile(r"[^\W_]+")
WORD_BONUS = 0.1


def terms(t):
    return [w.lower() for w in WORD.findall(unicodedata.normalize("NFKC", t))]


def query_terms(query):
    """No stop words, no single letters; a single digit stays ("topic 3")."""
    return list(dict.fromkeys(t for t in terms(query) if (len(t) > 1 or t.isdigit()) and t not in STOP))


def fusion_scores(cosines, qterms, chunk_terms):
    """Cosine plus 0.1 x the share of the query's terms the chunk contains (Fusion.Score)."""
    if not qterms:
        return cosines
    bonus = np.array([sum(t in ct for t in qterms) / len(qterms) for ct in chunk_terms])
    return cosines + WORD_BONUS * bonus


def bm25(docs, query):
    q = query_terms(query)
    if not q:
        return []
    tf = [{} for _ in docs]
    for i, d in enumerate(docs):
        for t in terms(d):
            tf[i][t] = tf[i].get(t, 0) + 1
    lens = [sum(x.values()) for x in tf]
    avg = max(1, sum(lens) / len(lens))
    idf = {t: math.log(1 + (len(docs) - sum(t in x for x in tf) + 0.5) / (sum(t in x for x in tf) + 0.5)) for t in q}
    scores = []
    for i, x in enumerate(tf):
        s = sum(idf[t] * x[t] * 2.2 / (x[t] + 1.2 * (0.25 + 0.75 * lens[i] / avg)) for t in q if t in x)
        if s > 0:
            scores.append((s, i))
    return [i for _, i in sorted(scores, key=lambda p: (-p[0], p[1]))]


def rrf(lists, k=60):
    """The fusion before step 7b, kept to compare against."""
    score, first, n = {}, {}, 0
    for lst in lists:
        for r, i in enumerate(lst):
            score[i] = score.get(i, 0) + 1 / (k + r + 1)
            if i not in first:
                first[i] = n
                n += 1
    return sorted(score, key=lambda i: (-score[i], first[i]))


# --- models ----------------------------------------------------------------------------------------
class OnnxModel:
    """The server's setup: fp16 ONNX export, sentence_embedding output, 'query: ' prompt for queries."""

    def __init__(self, d):
        import onnxruntime as ort
        from tokenizers import Tokenizer
        self.tok = Tokenizer.from_file(str(d / "tokenizer.json"))
        self.tok.enable_truncation(512)
        self.session = ort.InferenceSession(str(d / "model_fp16.onnx"), providers=["CPUExecutionProvider"])

    def _run(self, texts):
        out = []
        for t in texts:
            ids = np.array([self.tok.encode(t).ids], dtype=np.int64)
            v = self.session.run(["sentence_embedding"], {"input_ids": ids, "attention_mask": np.ones_like(ids)})[0][0]
            out.append(v.astype(np.float32))
        v = np.array(out)
        return v / np.linalg.norm(v, axis=1, keepdims=True)

    def docs(self, texts):
        return self._run(texts)

    def queries(self, texts):
        return self._run(["query: " + t for t in texts])


class SentenceTransformerModel:
    def __init__(self, name, query_prompt=None, prompt_name=None, **kw):
        from sentence_transformers import SentenceTransformer
        self.m = SentenceTransformer(name, device="cpu", trust_remote_code=True, **kw)
        self.m.max_seq_length = 512
        self.query_prompt, self.prompt_name = query_prompt, prompt_name

    def docs(self, texts):
        return self.m.encode(texts, batch_size=8, normalize_embeddings=True, convert_to_numpy=True)

    def queries(self, texts):
        if self.prompt_name:
            return self.m.encode(texts, prompt_name=self.prompt_name, normalize_embeddings=True, convert_to_numpy=True)
        return self.m.encode([(self.query_prompt or "") + t for t in texts], normalize_embeddings=True, convert_to_numpy=True)


class OpenAICompatibleModel:
    """An external provider as the openai-compatible driver calls it: POST {base}/embeddings, batches of 32, the
    query prompt before queries only. The key is read from a file and sent as a bearer token, nothing else."""

    def __init__(self, base_url, model, key_file, dimensions=None, query_prompt="", cache=None):
        self.url = base_url.rstrip("/") + "/embeddings"
        self.model, self.dimensions, self.query_prompt = model, dimensions, query_prompt
        self.key = pathlib.Path(key_file).expanduser().read_text(encoding="utf-8").strip() if key_file else None
        # Vectors by text, so that a second run (another query prompt, the mixed experiment) doesn't send the corpus again.
        self.cache_path = cache
        self.cache = json.loads(cache.read_text(encoding="utf-8")) if cache and cache.exists() else {}

    def _post(self, texts):
        import urllib.error
        import urllib.request
        body = {"model": self.model, "input": texts, "encoding_format": "float"}
        if self.dimensions:
            body["dimensions"] = self.dimensions
        headers = {"Content-Type": "application/json"}
        if self.key:
            headers["Authorization"] = "Bearer " + self.key
        for attempt in range(5):
            request = urllib.request.Request(self.url, data=json.dumps(body).encode(), headers=headers)
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    data = sorted(json.load(response)["data"], key=lambda d: d["index"])
                    return [d["embedding"] for d in data]
            except urllib.error.HTTPError as e:
                if e.code not in (408, 429, 500, 502, 503, 504) or attempt == 4:
                    raise RuntimeError(f"{self.url} answered {e.code}") from None
                time.sleep(float(e.headers.get("Retry-After") or 2 ** attempt))

    def _run(self, texts):
        missing = list(dict.fromkeys(t for t in texts if t not in self.cache))
        for i in range(0, len(missing), 32):
            batch = missing[i:i + 32]
            self.cache.update(zip(batch, self._post(batch)))
        if self.cache_path and missing:
            self.cache_path.write_text(json.dumps(self.cache), encoding="utf-8")
        v = np.array([self.cache[t] for t in texts], dtype=np.float32)
        return v / np.linalg.norm(v, axis=1, keepdims=True)

    def docs(self, texts):
        return self._run(texts)

    def queries(self, texts):
        return self._run([self.query_prompt + t for t in texts])


QWEN3_PROMPT = "Instruct: Given a search query, retrieve the note sections that answer it\nQuery: "


# The GTE-based arctic v2 models need these on CPU without xformers (model card).
NO_XFORMERS = {"use_memory_efficient_attention": False, "unpad_inputs": False}
E5_INSTRUCT = "Instruct: Given a question or topic, retrieve the note sections that answer it\nQuery: "
COMPARE = {
    "arctic-m (fp32)": lambda: SentenceTransformerModel("Snowflake/snowflake-arctic-embed-m-v2.0", prompt_name="query", config_kwargs=NO_XFORMERS),
    "arctic-l (fp32)": lambda: SentenceTransformerModel("Snowflake/snowflake-arctic-embed-l-v2.0", prompt_name="query"),
    "bge-m3": lambda: SentenceTransformerModel("BAAI/bge-m3"),
    "multilingual-e5-large-instruct": lambda: SentenceTransformerModel("intfloat/multilingual-e5-large-instruct", query_prompt=E5_INSTRUCT),
    "multilingual-e5-base": lambda: SentenceTransformerModel("intfloat/multilingual-e5-base", query_prompt="query: "),
    "qwen3-embedding-0.6b": lambda: SentenceTransformerModel("Qwen/Qwen3-Embedding-0.6B", prompt_name="query"),
}


# --- metrics ---------------------------------------------------------------------------------------
def is_gold(c, gold):
    for p, h in gold:
        if p == "*" and h in c["text"]:
            return True
        if p == c["path"] and h == c["heading"]:
            return True
    return False


def ranks_of(chunks, ranked_lists):
    return [next((r + 1 for r, i in enumerate(ranked) if is_gold(chunks[i], gold)), None)
            for (_, _, gold), ranked in zip(QUERIES, ranked_lists)]


def summary(ranks):
    """Over the queries with an answer; the N queries (nothing answers them) have no gold section."""
    pos = [(cat, r) for (cat, _, _), r in zip(QUERIES, ranks) if cat != "N"]
    at = lambda k: sum(1 for _, r in pos if r and r <= k) / len(pos)
    by = {}
    for cat, r in pos:
        by.setdefault(cat, []).append(1 if r == 1 else 0)
    return {"R@1": at(1), "R@3": at(3), "R@10": at(10), "MRR": sum(1 / r for _, r in pos if r) / len(pos),
            "R@1 by cat": {c: sum(v) / len(v) for c, v in sorted(by.items())}}


def evaluate(model, notes):
    chunks = [c for rel, t in notes.items() for c in chunks_of(rel, t)]
    texts = [c["context"] + "\n" + c["text"] for c in chunks]  # EmbeddingText
    chunk_terms = [set(terms(t)) for t in texts]
    t0 = time.time()
    dv = model.docs(texts)
    ms_per_chunk = (time.time() - t0) / len(texts) * 1000
    sims = model.queries([q for _, q, _ in QUERIES]) @ dv.T

    meaning = [list(np.argsort(-s, kind="stable")) for s in sims]
    hybrid = [list(np.argsort(-fusion_scores(s, query_terms(q), chunk_terms), kind="stable"))
              for s, (_, q, _) in zip(sims, QUERIES)]
    lexical = [bm25(texts, q) for _, q, _ in QUERIES]
    rrf_lists = [rrf([m[:50], w[:50]]) for m, w in zip(meaning, lexical)]

    variants = {"meaning": meaning, "hybrid": hybrid, "rrf (step 7)": rrf_lists}
    top1 = [float(s.max()) for s in sims]
    return {
        "chunks": len(chunks), "ms_per_chunk": round(ms_per_chunk, 1),
        "variants": {name: {**summary(ranks_of(chunks, lists)), "ranks": ranks_of(chunks, lists)} for name, lists in variants.items()},
        "best_cosine_with_answer": [t for t, (c, _, _) in zip(top1, QUERIES) if c != "N"],
        "best_cosine_without_answer": [t for t, (c, _, _) in zip(top1, QUERIES) if c == "N"],
    }


def similarities(model, notes):
    """The corpus' chunks and, per query, the cosine of every chunk, by one model."""
    chunks = [c for rel, t in notes.items() for c in chunks_of(rel, t)]
    texts = [c["context"] + "\n" + c["text"] for c in chunks]
    return chunks, texts, model.queries([q for _, q, _ in QUERIES]) @ model.docs(texts).T


def evaluate_mixed(sims_a, sims_b, chunks, texts):
    """
    Cross-vault search with two models: each top-level folder of the corpus is a vault, every other one embedded by
    model B, the rest by A (and the other way round). The server ranks every vault's chunks by their own model's
    cosine plus the word bonus, in one list ("raw"). "standardised" maps each model's cosines, per query, to the mean
    and spread the two models have together before the bonus, so that a model whose cosines run higher doesn't take
    the top places for its vaults alone.
    """
    vaults = sorted({c["path"].split("/")[0] for c in chunks})
    chunk_terms = [set(terms(t)) for t in texts]
    out = {}
    for flip in (False, True):
        b_vaults = set(vaults[1 - flip::2])
        in_b = np.array([c["path"].split("/")[0] in b_vaults for c in chunks])
        label = "B: " + ", ".join(sorted(b_vaults))
        variants = {"raw": [], "standardised": []}
        top_from_b, gold_in_b = 0, 0
        for qi, (cat, q, gold) in enumerate(QUERIES):
            cos = np.where(in_b, sims_b[qi], sims_a[qi])
            bonus = fusion_scores(np.zeros(len(chunks)), query_terms(q), chunk_terms)
            variants["raw"].append(list(np.argsort(-(cos + bonus), kind="stable")))
            mu = {g: cos[in_b == g].mean() for g in (False, True)}
            sd = {g: cos[in_b == g].std() for g in (False, True)}
            mu_ref, sd_ref = (mu[False] + mu[True]) / 2, (sd[False] + sd[True]) / 2
            std = np.where(in_b, (cos - mu[True]) / sd[True], (cos - mu[False]) / sd[False]) * sd_ref + mu_ref
            variants["standardised"].append(list(np.argsort(-(std + bonus), kind="stable")))
            if cat != "N":
                top_from_b += bool(in_b[variants["raw"][-1][0]])
                gold_in_b += any(is_gold(chunks[i], gold) for i in np.nonzero(in_b)[0])
        out[label] = {name: summary(ranks_of(chunks, lists)) for name, lists in variants.items()}
        out[label]["top_from_b"], out[label]["gold_in_b"] = top_from_b, gold_in_b
    return out


def print_mixed(a, b, res):
    print(f"\nmixed: A = {a}, B = {b}")
    for label, r in res.items():
        print(f"  {label}  (top hit in B's vaults for {r['top_from_b']} queries, gold there for {r['gold_in_b']})")
        for v in ("raw", "standardised"):
            s = r[v]
            print(f"    {v:14} R@1 {s['R@1']:.2f}  R@3 {s['R@3']:.2f}  MRR {s['MRR']:.2f}")


def print_table(name, res):
    cats = sorted({c for c, _, _ in QUERIES if c != "N"})
    print(f"\n{name}  ({res['chunks']} chunks, {res['ms_per_chunk']} ms/chunk)")
    print(f"  {'':14} {'R@1':>5} {'R@3':>5} {'MRR':>5}  " + " ".join(f"{c:>4}" for c in cats) + "   (R@1 per category)")
    for v, s in res["variants"].items():
        print(f"  {v:14} {s['R@1']:5.2f} {s['R@3']:5.2f} {s['MRR']:5.2f}  " + " ".join(f"{s['R@1 by cat'][c]:4.2f}" for c in cats))
    pos, neg = res["best_cosine_with_answer"], res["best_cosine_without_answer"]
    print(f"  best cosine: median {np.median(pos):.3f} with an answer, max {max(neg):.3f} without one")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model-dir", type=pathlib.Path, required=True, help="the server's ModelPath: <dir>/<key>/tokenizer.json and model_fp16.onnx")
    ap.add_argument("--models", nargs="+", default=["arctic-m", "arctic-l"], help="catalogue keys to run (default: both)")
    ap.add_argument("--compare", nargs="*", metavar="NAME", help=f"also run sentence-transformers models; no names = all of: {', '.join(COMPARE)}")
    ap.add_argument("--corpus", type=pathlib.Path, default=HERE / "corpus")
    ap.add_argument("--out", type=pathlib.Path, default=HERE / "results.json")
    ap.add_argument("--openai-url", help="an external provider with the OpenAI embeddings API, e.g. https://api.tensorx.ai/v1")
    ap.add_argument("--openai-model", default="qwen/qwen3-embedding-8b")
    ap.add_argument("--openai-key-file", default=os.environ.get("KUMBU_TEST_TENSORX_KEY_FILE"),
                    help="file with the provider's API key (default: $KUMBU_TEST_TENSORX_KEY_FILE)")
    ap.add_argument("--openai-variants", nargs="+", default=["4096", "1024", "1024+prompt"],
                    help="dimensions to ask for, '+prompt' to put the Qwen3 instruction before queries (default: 4096 1024 1024+prompt)")
    ap.add_argument("--mixed", nargs=2, metavar=("A", "B"), help="two runs to search a corpus split into vaults of both with")
    ap.add_argument("--cache-dir", type=pathlib.Path, default=HERE / "cache", help="where an external provider's vectors are kept between runs")
    args = ap.parse_args()

    notes = {p.relative_to(args.corpus).as_posix(): p.read_text(encoding="utf-8") for p in sorted(args.corpus.rglob("*.md"))}
    runs = {key: (lambda key=key: OnnxModel(args.model_dir / key)) for key in args.models}
    if args.compare is not None:
        runs |= {k: v for k, v in COMPARE.items() if not args.compare or any(w in k for w in args.compare)}
    if args.openai_url:
        args.cache_dir.mkdir(exist_ok=True)
        for variant in args.openai_variants:
            dims, _, prompt = variant.partition("+")
            name = f"{args.openai_model.split('/')[-1]}@{dims}" + (" +prompt" if prompt else "")
            cache = args.cache_dir / f"{args.openai_model.replace('/', '_')}@{dims}.json"
            runs[name] = (lambda dims=dims, prompt=prompt, cache=cache: OpenAICompatibleModel(
                args.openai_url, args.openai_model, args.openai_key_file, int(dims) or None, QWEN3_PROMPT if prompt else "", cache))

    results, sims = {}, {}
    for name, make in runs.items():
        try:
            model = make()
        except Exception as e:
            print(f"{name}: failed to load: {e!r}"[:300])
            continue
        results[name] = evaluate(model, notes)
        print_table(name, results[name])
        if args.mixed and name in args.mixed:
            sims[name] = similarities(model, notes)
        del model
    if args.mixed and all(m in sims for m in args.mixed):
        a, b = args.mixed
        chunks, texts, sims_a = sims[a]
        results["mixed"] = {"A": a, "B": b, **evaluate_mixed(sims_a, sims[b][2], chunks, texts)}
        print_mixed(a, b, {k: v for k, v in results["mixed"].items() if k not in ("A", "B")})
    args.out.write_text(json.dumps(results, indent=1, default=float), encoding="utf-8")
    print(f"\nwritten: {args.out}")


if __name__ == "__main__":
    main()

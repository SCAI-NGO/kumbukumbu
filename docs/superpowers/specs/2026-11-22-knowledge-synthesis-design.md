# Knowledge synthesis (`similar`, `synthesize-knowledge`) — design

Date: 2026-11-22. Status: draft for Chris's review; every decision in §3 is *(Claude's call)*, made overnight on the basis of the pre-analysis, and open to change. Size: M (an index method, one new `sh` command, three filters, a system skill). Release: 0.7.0, beside offline mode (Chris, 2026-11-22); none of this touches offline code.

Sources:
- the pre-analysis "Operation Fish Oil" (2026-11-22, `/SWE/Kumbu/Ideas/`): Swanson's ABC model mapped to the commands, the gaps, a draft of the skill;
- the ideas *Link Prediction* (2026-11-17) and *Semantic walk for agents* (2026-11-17), where `similar` was first sketched;
- the search index: `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (`SearchAsync`), `src/Kumbu.Core/Search/ISearchIndex.cs`.

## 1. Why

A person asks her agent: "what can we learn from everything we have already written about X?" The answer she hopes for is not a summary of X but what Don Swanson found in 1986: two bodies of notes that never cite each other (fish oil lowers blood viscosity; Raynaud's patients have high blood viscosity) and together say something neither says alone. Swanson called it undiscovered public knowledge. In a second brain the silos are vaults, folders, projects and time: the note from March that the note from November never heard of.

Every step can be done by hand with the commands of 0.6.0, and the pre-analysis did so. What is missing is what Arrowsmith added to Swanson: the machine proposing the candidates, so the model reads twenty sections instead of two hundred. The embeddings Kumbu² already keeps can do that, by place: "what is close in meaning to *this note*, and far from it in links?"

## 2. Goals, non-goals, anti-goals

Goals:
- `similar <note>` ranks the sections closest in meaning to a note or a section, one per note, each with its **link distance** to the note, from the stored embeddings; `--unlinked` leaves out what the note already links or is linked from (open discovery, Swanson's steps 4–6).
- `similar --between <a> <c>` ranks the sections close to *both* notes: the semantic B list of closed discovery (Arrowsmith's question).
- `--outside <path>` on `similar` and `semsearch`: leave out a silo, A's own folder first of all.
- `--without-tag T` on `similar`, `semsearch` and `graph`: leave out the agent's own earlier hypotheses, so the vault doesn't read its own output back as evidence.
- A system skill `synthesize-knowledge` that walks an agent through bridge and explore runs and writes one cited report note.

Non-goals (frozen until a real run shows the need, as the pre-analysis recommends):
- `similar --gap` (a serendipity feed without a starting point), `terms` (Arrowsmith's word statistics), `graph --islands`, `graph --rank`.
- PDF pages as `similar` hits or sources: notes only for now; `semsearch` still finds pages.
- Link prediction in the web app (0.8.0); this is its agent face and shares its core.
- A store for hypotheses: a note with front matter is the store.

Anti-goals:
- **Writing links into A or C.** Similar is not the same as relevant; the skill proposes, the user decides.
- **A query leaving the server for `similar`.** It compares stored vectors only, so it never sends text to an embedding provider, also not to a vault's external one, and costs no one's BYOK budget.
- **A finding that is the vault's own output.** Hypotheses are tagged and can be left out.
- **Comparing vectors of two models.** Their cosines don't share a space; `similar` compares only within the source's model.

## 3. Decisions

1. **One new command, `similar`, rather than a mode of `semsearch`.** *Because* its input is a place (a note or section), not words, and its output carries what `semsearch` doesn't have: link distances. Its options mirror `semsearch` where they mean the same (`-n`, `--archive`, `--outside`, `--without-tag`).
2. **One hit per note, its best section.** *Because* link distance belongs to a note, and a long note would otherwise fill the list with its sections; `cat "path#Section"` reads the one shown, `outline` the rest.
3. **Similarity of two notes is the best pair of their chunks** (the highest cosine of any chunk of the source with any chunk of the candidate), not a mean vector. *Because* a note about five things has a mean vector about none of them; Swanson's bridges are single mechanisms, which live in single sections. A source section (`note.md#Heading`) uses only that section's chunks.
4. **`--between a c` scores a section by the lower of its two similarities** (to `a` and to `c`). *Because* a bridge must be close to both; the minimum is the usual ABC score (the weakest link) and needs no tuning. Both distances are shown.
5. **Link distance** is the shortest path through notes in either direction, as `graph --path` walks (attachments and links to nothing are never passed through), printed as `1 step`, `N steps`, `not connected` or `other vault` (links never cross vaults). `--without-tag` and the archive rule apply to the walk too. *Because* a hypothesis note links A and C; walked through, it would make every finding look known.
6. **`--unlinked` leaves out distance 1 only.** *Because* distance 2 (a common neighbour) is exactly where many good bridges sit; the agent sees the distance and judges.
7. **`--outside` leaves out notes (and on `semsearch` PDF pages) at or below a path; repeatable, at most 50 like the graph's filters.** **`--without-tag` leaves out notes carrying the tag, nested ones included; repeatable, a note carrying any of them goes.** PDFs carry no tags, so they stay. An unknown tag is no error for `--without-tag` (leaving out nothing is the right answer).
8. **Pending and other-model notes are counted in the footer, never guessed.** A source note without current embeddings is an error that says so, and points to `semsearch` for its topic meanwhile.
9. **The skill ships as a system skill** in `src/Kumbu.Core/Skills/System/synthesize-knowledge/`, with a hand-test list. The pre-analysis suggests trying it as a user skill first; the PR proposes both ways (§7), Chris decides.

## 4. The index: stored vectors

`ISearchIndex` gains one method with a default, so every fake and `NoSearchIndex` keep working:

```csharp
Task<StoredVectors> VectorsAsync(VaultPath scope, CancellationToken ct);
// StoredVectors(SemanticState State, IReadOnlyList<ChunkVector> Chunks, int Pending, string? Reason)
// ChunkVector(VaultPath Path, int StartLine, int Part, string Space, float[] Vector)
```

`EncryptedNoteStore` answers it from the vault's working set, as `SearchAsync` reads it: per note below the scope, the chunks embedded by the vault's current model under the current chunking scheme; `Space` is the model's key, so the command compares only within one. Notes without such embeddings count as pending; a vault whose provider isn't configured is left out with a reason. The vectors are the stored arrays, handed out by reference and never written to. No consent check is needed: nothing is embedded, nothing leaves.

The ranking lives in `Kumbu.Core.Search.SimilarSearch.RunAsync` (a `SimilarQuery` in, a `SimilarResult` out), not in the command, so that an HTTP endpoint can call it: link prediction's Similar tab and the graph's suggested links build on it; the command only parses and prints. Candidates a caller leaves out (a silo, a tag, a person's "not related") come in as a predicate over notes.

Cost: one dot product per pair of a source chunk and a candidate chunk. A 2,000-note vault holds some 10,000 chunks; a source note of five chunks is 50,000 dot products of 768 floats, well under a tenth of a second. `--between` doubles it.

## 5. The command

```
similar <note>[#section] [scope] [-n N] [--unlinked] [--outside P]... [--without-tag T]... [--archive]
similar --between <a>[#section] <c>[#section] [scope] [-n N] [--outside P]... [--without-tag T]... [--archive]
```

Output, one hit per note, the best first:

```
similar to /Research/Fish oil.md, below every vault: 3 notes
1. /Health/Cold hands.md#Triggers  lines 4-12  score 0.77  not connected
   Vasospasm after cold or stress; blood flow to the fingers drops…
2. /Research/Viscosity.md#Measured  lines 10-18  score 0.74  2 steps
   …
(by the stored embeddings, nothing sent to an embedding provider; distance counts links either way; cat "path#Section" reads one)
```

With `--between`: `score 0.71 (0.74 to A, 0.71 to C)  2 steps from A, not connected to C`, and the heading names both notes. The snippet is `semsearch`'s (`SectionSearch.Snippet`). The scope defaults to every vault; the source may lie outside it. Headings with nothing below them don't count, as in `semsearch`.

## 6. The filters on `semsearch` and `graph`

- `semsearch --outside P` drops the notes at or below P before ranking, and the PDF pages there from the hits.
- `semsearch --without-tag T` drops the notes carrying T before ranking; PDF pages stay (they carry no tags), unlike `--tag`, which leaves them out.
- `graph --without-tag T` (every mode): `GraphFilter.WithoutTags`, a note carrying any of them isn't kept, as `--tag` keeps only those that carry all. The two ends of `graph --path` stay, as with `--tag`.

## 7. The skill

`synthesize-knowledge` (system), two modes, one report note per run, the pre-analysis's draft tightened to the commands as built:

- **Bridge** (closed discovery): "how could X relate to Y?" `similar --between`, `graph --path`.
- **Explore** (open discovery): "what can we learn from what we wrote about X?", "what am I missing about X?" `similar --unlinked --outside <A's silo>`, `semsearch --outside` per B term.

Both leave out earlier findings with `--without-tag synthesis/hypothesis`, cap the run at five hypotheses, say for each why it could be wrong, and write one report note tagged `#synthesis/hypothesis` with `source: synthesized` and a review date, the inference in an `[!inferred]` callout, each B link linked to the section it stands in. They never change A or C.

How it lands, for Chris to pick in the PR:
- **(a) as in the PR, a system skill now** (recommended): the commands it needs ship in the same change, and a system skill is updated with the server, so a run that teaches us something is fixed in one place.
- **(b) a user skill first**: drop the skill's commit, put its `SKILL.md` into `/Skills/synthesize-knowledge/` on Chris's instance, run the hand tests two or three times, then freeze it with what was learned.

The hand-test list goes to `/SWE/Kumbu/Tests/` once the release is chosen.

## 8. Tests

- Core (`SimilarTests`, xUnit, fake index with chosen vectors): ranking by best chunk pair, one hit per note, the source left out, `#section` as source, `--unlinked`, link distances (1, 2, not connected, other vault, through a tagged note left out), `--between` by the minimum, `--outside`, `--without-tag`, the archive, other-model vectors skipped and counted, a source not indexed, the index off.
- `semsearch --outside` and `--without-tag`, `graph --without-tag`: a test each.
- Server: `VectorsAsync` returns the chunks of the current model and scheme and counts the rest as pending.
- The shell's command lists (`KumbuMetrics.KnownCommands`, `ConsoleApi.Allowed`, the console's completion) include `similar`; their parity tests guard it.

# Embeddable properties: finding notes by what their summary means

Date: 2026-11-25. Status: decided for 0.8.0 on `release/0.8.0`, with the maintainer's go-ahead to work 0.8.0 ahead on its own (2026-11-25); the data side is built from this spec, the UI comes with the query UI. Builds on [Queries: the data side](2026-11-24-queries-data-side-design.md). Source: the maintainer's note "Queries 2026-11-23", section "Summary properties: the Ferrari variant".

## 1. Why

Search by meaning embeds a note's sections: it finds what happens to be in the text. A curated summary finds what the note is *about*, the old trick of the abstract. Write a good `summary:` into a note's frontmatter, and the note can be found by that meaning, filtered by its other properties and ranked by how close it is: `query execute related --param topic="offline sync conflicts"` gives a knowledge base you ask in prose. Skills and memory files already work this way (their `description` is what an agent reads to decide what to open); embeddable properties make it a tool for every note.

## 2. What it is today

- The chunker starts after the frontmatter (`src/Kumbu.Core/Search/Chunker.cs`, `ChunkSplitter.cs`): no property is embedded.
- A note's vectors are one sealed `embeddings` document per path (`NoteEmbeddings`: model, version, scheme, chunks of start line, part, hash and a float16 vector; `src/Kumbu.Server/Vaults/UnlockedVault.cs`), in memory while the vault is unlocked. There is no vector per note.
- Every committed write queues the note once (`SearchIndex`), and only chunks whose hash changed are embedded; vectors are reused by hash within the vault and model.
- Similar notes rank by best chunk (`SimilarSearch`), the graph's similarity edges by the symmetric mean of best-match cosines over all chunk pairs (`PairSimilarity.Mutual`).

## 3. Decisions

1. **Which properties: `summary` and `description`, in every vault** ([ADR 0035](../../adr/0035-summary-and-description-are-embedded-in-every-vault.md)). Their text values are embedded besides the sections. *Why:* the two names people and agents already use for "what this is about" (`description` is the field of skills and memory files); a fixed pair needs no setting, no UI and no format before anyone asks for a third. A per-vault list comes with the query UI if people want other names; the stored format below already holds any name. *Lost:* a marker in `.obsidian/types.json` (Obsidian rewrites the file and drops keys it doesn't know); a Kumbu² setting now (a UI and a sealed field for a need not yet seen).
2. **Only text, one vector per property.** A property is embedded when its value is a text (as the note's properties read it, `src/Kumbu.Core/Queries/Properties.cs`), not empty after trimming. A list, a number, a date or an object is not. The text embedded is the note's title, a line break, and the value: the same shape as a chunk (context, then core), so the vector lives in the same space as the sections'. A value longer than one chunk's bound is cut there (the model's 512 tokens), as a section's core is.
3. **Stored with the note's embeddings, sealed as they are.** `NoteEmbeddings` gains a list of property vectors (name, hash, vector), in the same sealed blob under the same context (`embeddings:{vaultId}:{pathId}`). A blob without the list reads as "none". *Why:* one place per note, the same seal, the same lifetime (trash, move and re-key carry it without new code); no second collection whose size would show which notes have a summary. A scheme change is not needed: an older server reading a newer blob is not supported anyway (no downgrades).
4. **Embedded when the value changes, no debounce.** The note is queued as today; a property is embedded only when its hash (the same SHA-256 prefix as a chunk's) changed, and a vector is reused by hash within the vault and model as a chunk's is. *Why:* the queue already holds a note once however often it is written, and an unchanged summary costs nothing. An agent rewriting a summary ten times a minute costs at most ten small requests, the same as editing a section; measure before adding a debounce.
5. **Same rule as the text for where it goes.** A vault's property values go to the vault's embedding provider exactly as its sections do: internal by default, the owner's choice otherwise, a discreet vault never on a personal key. No separate rule for properties. *Why:* a frontmatter value is note content; one rule is one the owner can know. *Changes:* `docs/security-architecture.md` §5.5 "What leaves the server" names the two properties' values too.
6. **Meaning enters a query through the frontmatter, not the base** ([ADR 0036](../../adr/0036-meaning-enters-a-query-through-its-frontmatter.md)). A query note says `meaning:` beside `params:`:

   ```yaml
   meaning:
     property: summary      # summary or description
     text: topic            # a parameter's name, or a text in quotes
     min: 0.5               # optional: leave out the notes less close than this (cosine, -1 to 1)
     as: closeness          # optional: the name the score has in the base; "closeness" when left out
   ```

   Before the base runs, the text is embedded once (the vault's provider, as a search query is), each note's vector for the property is compared with it, and every note gets its score as a property named by `as` (null for a note without that property's vector). Notes under `min`, or without a vector when `min` is given, are left out. The base then filters, sorts and shows the score as any property: `sort: [{property: closeness, direction: DESC}]`, `order: [file.basename, closeness]`. *Why:* the base block stays pure Bases (spec 2026-11-24, decision 5): in Obsidian an unknown function fails the whole base, an unknown property is only empty. The same query note renders in Obsidian, unranked and without the scores. *Lost:* a function `similar(summary, this.topic)` in the language (fails in Obsidian; would make the evaluator wait on a provider per row); ranking without a score anyone can see (a person can't tell why a note came first).
7. **The score name must not hide a property.** `as` follows the rule of a parameter's name, and a note's own property of that name is shadowed for the run; `query check` warns when a note below the query's folder has it. *Why:* a silent overwrite would show a wrong value; a warning, not an error, because notes elsewhere may use the word for other things.
8. **Offline, meaning has no provider.** The offline engine (`packages/eidolon-core/src/query`) reads `meaning:` and fails with the code `meaning-offline` before any note is read. *Why:* the browser copy holds no vectors and no model; an honest error beats a list that silently isn't ranked.
9. **Similar notes and the graph use the summary when both notes have one** — a follow-up step, after the data side. Two notes that both have a `summary` vector are compared by the cosine of those two vectors (one dot product instead of chunks × chunks); otherwise as today. *Why:* cheaper and closer to what the notes are about. It is a second step because it changes results people already see, and needs a look at real vaults first (does a pair of summaries rank as well as the chunks?); until measured it stays off.
10. **People's summaries are theirs.** An agent asked to keep summaries current suggests a new summary for one a person wrote (Who wrote what tells), it doesn't overwrite it. That is guidance in the `create-query` skill, not a rule the server enforces. *Why:* the server can't tell a good overwrite from a bad one; the person can.

## 4. Errors and limits

- `meaning:` that isn't a map of `property`, `text`, `min`, `as`: the code `meaning`, with what is wrong. `property` other than `summary` or `description`, `text` neither a declared parameter nor a quoted text, `min` not a number from -1 to 1, `as` not a name: `meaning`.
- The provider failing or timing out (5 s, one retry, as a search query): `meaning-unavailable`, the query doesn't run. *Why:* a list without the meaning it was asked for would mislead.
- The text to embed is at most 2,000 characters (it is anyone's input; a summary-length question needs no more): over it, `param-value`.
- A run embeds one text; nothing else reaches the provider.

## 5. Steps

1. Server: embed `summary` and `description` (decisions 1–5), with the stored list in `NoteEmbeddings`; xUnit tests for what is embedded, re-embedding only on change, sealing and moving with the note.
2. Queries: `meaning:` in the C# engine (decisions 6–7 and §4), held to new conformance cases in `tests/query-conformance/queries/` (vectors given in the case, no model needed); `query inspect` shows it, `help query` explains it, the skill `create-query` teaches it.
3. Offline: the TypeScript engine reads `meaning:` and gives `meaning-offline` (decision 8); the same cases mark it so.
4. Later, measured first: decision 9.

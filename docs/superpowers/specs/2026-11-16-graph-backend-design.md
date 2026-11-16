# The graph backend

Date: 2026-11-16. Status: approved by the maintainer on 2026-11-16; builder check done (its answers marked *(Claude's call, builder check)*).

The graph of a vault's notes: a sealed, persisted index of what each note links to, resolved in memory into a graph, and the queries on it. It serves Publish 2.0 now (its own spec, built on the bundle query, §6.7) and the graph view in 0.7.0, which is then a front-end topic on a backend whose schema is settled. `links`, `backlinks`, the link rewrite on `mv` and the backlink count on `rm` move onto it in this version.

**Why the split into backend now and view later** (maintainer, 2026-11-15): a rock-solid backend first, which we are convinced holds, then the view on top of it. Test-first with thorough coverage and given the time it needs: the backend is the foundation, not a helper for a front-end feature. On 2026-11-16 the maintainer widened it: the view's queries are built and tested now too, their contract taken from a UX review of the graph view held before this spec (§3), so that the schema is certain when 0.7.0 starts.

**Who asked.** A power user with about 1,600 notes after two weeks: the graph view is what still keeps him in Obsidian. Publish 2.0 needs to follow a note's links to a depth.

The working notes, with every decision as it was taken, are in Kumbu² at `/SWE/Kumbu/Ideas/Graph backend 2026-11-16.md`. Each decision carries its reason; Claude's own calls are marked *(Claude's call)*.

## 1. Goals, non-goals, anti-goals

Goals: §4 to §9.

Non-goals:

- **The graph view.** 0.7.0, on these queries; no layout, positions or rendering on the server, ever (the client runs the force layout).
- **The time-lapse** of Obsidian's graph. Frozen (§3, decision 5); the creation time it would need is recorded from now on (§5).
- **The graph at a checkpoint** or any earlier state. Computed on request if it is ever needed; the index holds the current state only.
- **Links across vaults.** They don't exist (`LinkResolver.cs:60`); soft links are a backlog item.
- **Unlinked mentions** (`backlinks --unlinked`) stay a full scan: they search prose, not links.
- **A delta log** for live updates (§3, decision 4).

Anti-goals, which make this a failure even if it works:

- **Two truths.** `backlinks`, `links`, `mv`'s rewrite and the graph never disagree about where a link leads.
- **A stale graph.** After any committed write, the next query sees its effect; there is no "eventually".
- **Plaintext links in the database.** The index is sealed like everything else of a vault: a database dump shows neither who links to whom nor the tags.
- **A graph that shows what its reader can't read.** A hidden discreet vault leaves no trace, not even a ghost node.
- **An agent flooded.** No query hands an agent more than its output budget; a hub never drowns the answer.

## 2. What we found

- No link index exists. `links`, `backlinks`, `mv`'s rewrite (`NoteMover.cs:324`), `rm`'s backlink count (`NoteWriter.cs:410`) and the hints on create (`NoteWriter.cs:488`) read every note of the vault, build a `LinkResolver` and parse every note again, on every call (`NoteLinks.cs:23`, `:34`). It is cheap only because an unlocked vault lies decrypted in memory (`UnlockedVault.cs:92`).
- Measured on synthetic vaults (~1.5 KB and 12 links per note, Release build):

  | | 1,600 notes | 16,000 notes |
  |---|---|---|
  | parse links and aliases | ~300 ms | ~1.5 s |
  | resolve every link | ~20 ms | ~150 ms |
  | decrypt every note (comparison) | ~7 ms | ~70 ms |

  Parsing is the cost; resolving is cheap. Every `backlinks` pays the ~300 ms today.
- Every write goes through one place, `EncryptedNoteStore.UnderGateAsync` (`EncryptedNoteStore.cs:1016`): vault gate, Mongo transaction, commit, `apply()` to the working set, then the search index's `Enqueue` and `NotesChanged`. The index hangs off the same place.

## 3. Decisions

1. **Persist the parsed links, resolve in memory** (approach A). One sealed document per note with the raw links, the aliases, the tags and `when` (§4), written right after the note's commit (§4.1). On unlock the resolved graph is built from them (~20 ms for 1,600 notes); every write keeps it current. *Maintainer:* a user has 1,600 notes after two weeks, and the 0.7.0 view must be fast; parsing on every unlock would cost seconds as vaults grow.
   - **Not B, the resolved edges persisted.** Where `[[X]]` leads depends on the whole vault: a new note `X.md` or a new alias redirects or makes ambiguous links in other notes, so stored edges need invalidation logic that will eventually be wrong. The maintainer first had B in mind; the interplay with aliases decided it.
   - **Not C, memory only:** the load times above.
2. **Tags and `when` go into the index.** *Maintainer:* grouping and tag nodes in the view would otherwise need every note parsed again, and adding them later means a new scheme version and a full rebuild. `when` because in the calendar it marks a dated note (a meeting), which is not a day note: the view filters the two apart, and a later time-lapse gets a real date.
3. **Every perspective is covered** (checked on the maintainer's request): from a note to a depth in either direction, a folder, a set of notes, a vault or several, orphans, hubs, unresolved targets, attachments, ambiguous links, the shortest paths between two notes. Rights are per vault (`concept.md` §3), so whoever reads a vault may see its whole graph; Publish's filter belongs in its query, not the index.
4. **`links`, `backlinks`, `mv`'s rewrite, `rm`'s count and the hints on create move onto the index.** *Maintainer:* one truth, so `backlinks` never disagrees with the graph; and the index is exercised by every existing test and by daily use before 0.7.0 relies on it. Today's scan stays, in the tests only, as the oracle (§10).
5. **The view's queries are built and tested now** (§6), from the UX review's findings, decided one by one:
   1. **Ghost nodes are real nodes.** A diary links `[[Anna]]` a hundred times and never writes the note; in the graph she is the centre. Identity in §4.3. Obsidian also shows unresolved links as grey nodes keyed by link text; whether it folds case is undocumented. *Maintainer:* Kumbu²'s semantics come first and the resolver folds case already; "we're a Linux-oriented shop" and would rather treat Anna and anna as two files, but that would confuse users badly, so the compromise holds.
   2. **The global graph comes at once and compact**, no paging below ~10,000 notes; hubs are not capped there (that would falsify the overview), only in the local graph and for agents. **Day notes are a node kind** so the view can hide them in one click: in diary vaults they are the dominant noise. *Maintainer:* the front matter is a second rail for correcting the recognition later.
   3. **Agents:** a hub cap with the command that goes on, a hard output budget, and the shortest paths between two notes. New, not Obsidian parity: "how is X connected to Y?" is a typical agent question that takes many `links` calls today. *Maintainer on "not connected":* six degrees of separation; what isn't connected within six steps most likely has nothing to do with each other, or the user should tend the links there; it's a signal.
   4. **Live without a delta log.** The sync sockets already nudge on every change (`SyncHub.cs:17`); the graph carries a version as ETag and the client diffs by path. A delta log is a source of bugs of its own, needed only for far larger vaults. *Maintainer:* an agent writing 50 notes a minute is an extreme case, a theoretical constraint.
   5. **The time-lapse is frozen; the creation time is recorded from now on** (§5). Kumbu² doesn't know when a note came into being (history keeps 10 states, `NoteWriter.cs:27`). *Maintainer:* the power user's import was deliberate and his vault grows, so a time-lapse showing the import as one burst can be explained; and "unbedingt ab 0.6.0 mitschreiben": unlike the index, it can never be derived later.
   6. **Phone:** the local graph starts at depth 1 with the hub cap; nothing extra.
   7. **Discreet and several vaults:** a graph over several vaults takes exactly the vaults `ls /` would list for the same credential and session (§8).
   8. **Accessibility:** a list of neighbours comes from the local graph's query; nothing extra.

## 4. The index

### 4.1 The stored extract

A new collection, `links`, one `SealedDocument` per note (`Documents.cs:176`): `_id`, `vault`, `pid` (the note's keyed path hash, as in `notes`), `blob`; the note's version token (`VersionToken.Of` of its content) sealed inside the payload names the state it was made from. The sealed payload, the **extract**:

| Field | What |
|---|---|
| `scheme` | the extract's scheme version (§4.5) |
| `links` | every `NoteLink.Parse` result: kind (wiki or Markdown), embed, target, subpath, line, start; the alias text of a wikilink is not needed and not stored |
| `aliases` | `NoteAliases.Of` |
| `tags` | the note's tags as `TagReader` reads them, front matter and inline |
| `when` | the note's calendar time as `EntryReader` reads it from the front matter (start, and `until` if present), or none |

The payload also holds the note's path and that version token; its associated data binds it to the path id, as for embeddings: `links:{vault}:{pid}` *(Claude's call, while planning: the document is upserted by path id like an embedding, `UnlockedVault.cs:265`)*. The extract is derived data: anything in it can be rebuilt from the note.

**A cache, never a source of truth** *(Claude's call, while planning; amended 2026-11-16)*. The graph in memory never trusts a stored extract whose version token differs from the note's: it parses the note instead. Not the revision: a note removed and made again at its path starts at revision 1 again, so a revision would let the old note's extract pass *(Claude's call, while building Task 8)*. So the extract is not written inside the note's transaction but right after the commit, still under the vault's gate (where `UnderGateAsync` enqueues the search index today, `EncryptedNoteStore.cs:1053`); a crash between the two leaves a stale extract, which the next unlock parses anew. Why: correctness then rests on one comparison of revisions instead of on every write path's transaction, and no write path has to know about extracts. It is written after every operation that writes a note's content or path: create, write, edit, replace, mv, archive, rm (the extract is removed with the note; trashed notes are not in the graph), restore from the trash (made anew), restore of a version, checkpoint restore, the seeder, sync and WebDAV, which all go through `EncryptedNoteStore`. Emptying the trash touches no extract. Re-keying reseals the collection with the rest (`VaultCatalog.Rekey.cs:46`); deleting a vault or an account deletes it (`Deletion.cs`).

Attachments get no extract: they link to nothing. They are nodes when a note links to them (§4.3).

### 4.2 On unlock

`VaultCatalog.LoadAsync` reads the extracts with the notes. A note whose extract is missing, has an older `scheme`, or was made from another state of the note (its version token differs) gets one parsed in memory at once, so the graph is complete when the vault opens; writing those extracts back happens after the load, not inside it *(Claude's call: the first unlock after the upgrade parses every note once, ~300 ms for 1,600 notes, and the user gets the vault without waiting for 1,600 writes)*. An extract that doesn't open (damaged) is treated as missing; the note is reported as damaged only by the note's own document, as today.

### 4.3 The graph in memory

Built from the extracts and the vault's attachment records, held in the working set as one immutable snapshot, replaced whole on every change under the vault gate, so a reader always sees one consistent state.

**Nodes**, each with a kind:

| Kind | What | Attributes |
|---|---|---|
| `note` | a note | path, title (the name without `.md`), tags, `when`, archived (below `_archive/`); changed (last write) and created (§5) are read from the working set by the node query only (§6.3) |
| `day` | a note whose name is a day or a week (`EntryReader.IsDayOrWeekName`, as the calendar recognises them) | as `note` |
| `attachment` | an attachment that a note links to or embeds | path, title (the name with its extension) |
| `ghost` | a link target that resolves to nothing | its key (below), title |

Degree in and out are counted on the snapshot, over its edges.

**Ghost identity.** The unresolved target, without `.md` and without its subpath, compared as the resolver compares (case-insensitive, Unicode normalised to NFC as the resolver does). A bare name is one ghost for the whole vault: every `[[Anna]]` and `[[anna]]` from any note meets in the ghost "Anna" (its title is the spelling of the first link in path order, *Claude's call*). A target with a folder (`[[People/Anna]]`, `[x](/People/Anna.md)`) is a ghost of its own, keyed by that path, since it means something else. Once a note `Anna.md` exists, the ghost is gone and its edges lead to the note: this falls out of resolving, nothing extra. A link to a file that isn't there (`[[report.docx]]`) is a ghost too, with its extension in the key.

**Edges**, from a note to a node, one per pair (source, target), carrying:

- the lines of the links behind it, in order (for `backlinks` and for a list view);
- `embed` if any of them is an embed;
- `alias` if it was found through an alias (`LinkResolution.ViaAlias`);
- `ambiguous` if the link matches several notes and none is preferred (`LinkKind.Ambiguous`): then one edge goes to each candidate, all marked ambiguous, as `backlinks` lists them under "maybe" today.

`[[#Heading]]` within the note itself (`LinkKind.Self`) makes no edge. A link to a heading or block of another note is an edge to that note; the subpath stays with the line, not the edge. Web links make no edge. Links inside fenced or inline code don't count, as today; links in the front matter do, as today.

### 4.4 Keeping it current

One rule, correct by construction *(Claude's call; simplified while planning, 2026-11-16)*: the snapshot belongs to one state of the working set's notes and attachments. On the first read after either changed, every link of the vault is resolved again from the extracts held in memory (~20 ms for 1,600 notes, ~150 ms for 16,000); only notes whose extract is missing or from another state of the note are parsed. No partial update, and no cleverness about which other notes a new name might affect: that is exactly the invalidation logic decision 1 rejects. Every change to the working set (a write, `RefreshAsync`, a load) is covered without touching it, since they all replace the immutable `Notes` or `Attachments`. Resolving only the changed notes' links is an optimisation for later, if measurement on large vaults asks for it.

**The graph version.** A counter per vault in the snapshot, raised only when the nodes, edges or their attributes differ from the previous snapshot; a typo in prose leaves it unchanged. Times are not attributes of the snapshot: every write changes `changed`, so with them in it the version would rise on every save and the ETag would guarantee nothing. They come with the node query instead *(Claude's call, builder check)*. It is published together with an epoch drawn at random when the vault is loaded, so a version never repeats across restarts or evictions: ETag `"{epoch}.{version}"`; several vaults combine their pairs in the order of the request.

### 4.5 The scheme version

`scheme` names the extract's format and the parsers that made it, as the chunks do (`ChunkBound.Scheme`). Any change to `NoteLink.Parse`, `WikiLink`, `MarkdownLink`, `NoteAliases`, the tag reader or the reading of `when` that changes what they return for some text raises it; a test pins the current value next to a fixture that would change under such a change, so nobody forgets *(Claude's call)*. On unlock, older extracts are made anew (§4.2).

## 5. The creation time

A new field on `StoredNote`: `CreatedAt`, with a flag `CreatedAtLeast`.

- Set at revision 1 to the write's time, for every way a note comes into being (create, write of a new path, sync, WebDAV, seeder, checkpoint restore of a note that no longer exists).
- Kept through every later write, `mv` (`NoteMover` uses `with`, which keeps it), archive, restore of a version, and restore from the trash.
- Notes written before this version have no `CreatedAt`. Until their next real write, readers derive it in memory from the oldest state their history still knows (`History` is newest first; the last entry's `WrittenAt`, or `LastWriteAt` if the history is empty), with "at the latest" set; that write stores the value derived just before it, with `CreatedAtLeast`, and it is kept from then on. No background rewrite of note documents: it would raise every note's revision, race with the user's writes ("changed meanwhile"), make every extract stale and count as a change for sync and history *(Claude's call, builder check)*.
- Shown nowhere new in this version except as the node attribute `created`, with its "at the latest" mark.

Why in the note and not in the extract: the extract is derived and may be thrown away and rebuilt; the creation time can't be derived from anything.

## 6. The queries

Pure functions on the snapshot, in `Kumbu.Core` (a `Graph` namespace) so they are tested without a database; the server supplies the snapshot *(Claude's call)*. Every query takes the shared filter:

| Filter | Default | |
|---|---|---|
| `path` | the vault | only nodes below these folders (several allowed) |
| `tag` | none | only notes with all these tags (nested tags as `rg --tag` matches them) |
| `days` | shown | hide `day` nodes |
| `attachments` | shown | hide `attachment` nodes |
| `ghosts` | shown | hide `ghost` nodes |
| `archive` | hidden | show notes below `_archive/`, as `--archive` elsewhere |

A filter removes nodes and their edges; it never changes resolution. The start note of a local graph is always shown, whatever the filter. `path` and `tag` select **notes** (day notes are notes); an attachment or ghost stays when a note that stays links to it, wherever the attachment lies, so `graph --unresolved /Work` lists the ghosts that notes below `/Work` link to *(Claude's call, builder check)*.

1. **Local graph.** A note, a depth (1 to 3, default 1), a direction (`out`, `in`, `both`, default `both`), a hub cap (default 20). Breadth-first from the note; each node appears once, at its first depth. A node with more neighbours than the cap shows the cap's worth, ordered by degree, then by path, and a count of the rest. Returns nodes, edges between returned nodes, and for each capped node the number left out.
2. **Global graph.** One or more vaults, the filter. Every node and edge, uncapped. Compact: nodes as a list; edges as pairs of indices into it with their flags.
3. **Node.** One node: its attributes, degree in and out, for a note its `changed`, `created` and "at the latest" mark (§5), and the first paragraph (the first block after the front matter that isn't a heading, cut at about 300 characters on a word boundary) for the hover preview.
4. **Orphans.** Notes with no edge in either direction, below a path; attachments and ghosts never are orphans.
5. **Hubs.** The notes with the highest degree (in plus out), top N (default 20), with their degrees.
6. **Unresolved.** The ghost nodes with the notes and lines that link to each, ordered by how many notes link to them.
7. **Bundle.** A note, a depth, and a predicate on notes: outgoing breadth-first, following only into notes the predicate admits, never crossing a vault. Returns the notes reached, the attachments they link to or embed (the images a publication needs), and the edges between them *(answered from the purpose, builder check)*. For Publish 2.0 and the SPA export; the predicate (unpublishable notes, what the publisher may publish) belongs to Publish 2.0's spec. Not exposed over `sh` or REST in this version.
8. **Paths.** Two notes, a direction (`both` by default, `out` follows links as written). The shortest paths between them, at most 5, of length at most 6; says whether there are more of the same length. If none: whether the two are **not connected at all** (no path of any length: separate islands) or **farther than 6 steps**. Breadth-first from both ends; linear in nodes and edges.

Ghost nodes are passed through but never walked beyond (they have no outgoing edges anyway). Attachments neither.

## 7. Where the queries are reached

### 7.1 `sh graph`

| Command | Query |
|---|---|
| `graph <note> [--depth N] [--out \| --in] [--cap N]` | local graph |
| `graph --path <note> <note> [--out]` | paths |
| `graph --orphans [path]` | orphans |
| `graph --hubs [path] [-n N]` | hubs |
| `graph --unresolved [path]` | unresolved |

All take `--tag`, `--archive`, `--no-days`, `--no-attachments`, `--no-ghosts`; `--path` and the other modes are exclusive. Paths are written as everywhere in `sh`. Reading requires the right to read the vault, nothing more.

The local graph prints a heading line, then one line per edge, grouped by depth, a ghost as its link text with "(no note)":

```
/Work/Plan.md, depth 2, both directions: 14 notes, 1 attachment, 2 without a note
1  /Work/Plan.md -> /Work/Budget.md
1  /Work/Plan.md -> /Work/Pump/diagram.png (embed)
1  /Work/Daily/2026-11-12.md -> /Work/Plan.md
2  /Work/Budget.md -> Anna (no note)
2  /Work/Daily.md -> /Work/Plan.md
   +37 more linking to /Work/Daily.md: graph /Work/Daily.md --in
```

Paths print each path on one line (`/Work/A.md -> /Work/C.md <- /Work/B.md`, the arrows showing each link's own direction). The exact wording of the other modes is the builder's, in the same style. Output is capped by `sh`'s budget: whatever is cut ends with one line naming how to narrow it (a smaller depth, `--path`, `--tag`, or the node to go on from). `help graph` and the `sh` tool description (`Manual.cs`) name the command with an example each.

### 7.2 REST

For the web app, in 0.7.0; built and tested now, under `/api/graph` beside `/api/links` (`NoteApi.cs:252`):

- `GET /api/graph?vault=…&vault=…&<filter>`: the global graph. `ETag` and `If-None-Match` → `304` as `SyncApi` does (`SyncApi.cs:152`).
- `GET /api/graph/local?path=…&depth=…&dir=…&cap=…&<filter>`: the local graph, with the same ETag (the vault's).
- `GET /api/graph/node?path=…`: one node.
- `GET /api/graph/paths?from=…&to=…&dir=…`: paths.

JSON shapes are the builder's, with this constraint: nodes as a list of objects (`path`, `title`, `kind`, `tags`, `when`, `archived`, `in`, `out`; the times only in `/api/graph/node`, §4.4), edges as `[from, to, flags]` with indices into that list, so that 1,600 notes and 20,000 edges stay around 200 to 300 KB before compression. Parameters are validated at the boundary: depth 1 to 3, cap 1 to 100, at most as many vaults as the caller has, paths parsed with `VaultPath.TryParse`; anything else is a `400`.

### 7.3 Live

No new message. The sync socket's existing `changed` nudge (`SyncHub.cs:17`) is the signal; the client asks again with the ETag. Nothing to build on the server beyond the ETag.

## 8. Rights and discreet vaults

- Every query needs read access to the vault(s), checked as for `cat`; a cached unlocked vault is used only for a credential valid for it (`concept.md` §7).
- A graph over several vaults takes exactly the vaults the caller would see in `ls /`: API keys and connections by their slots, a web session through `DiscreetFilter` (`Credentials.cs:31`), so a hidden discreet vault is absent, with no node, no ghost and no count.
- The bundle never leaves its vault; Publish 2.0 adds its own predicate on top.
- Nothing about the graph reaches logs or metrics beyond counts and timings.

## 9. Moving the existing callers

- `NoteLinks.OfAsync` resolves a note's links from the snapshot (the note's own current content is still parsed when a version other than the current one is asked for, as `links --version` does).
- `NoteLinks.ToAsync` (`backlinks`) reads the in-edges, lines included, in the same order and with the same "maybe" lines as today.
- `NoteMover`'s rewrite, `NoteWriter.RemoveAsync`'s count and `LinkHintsAsync` take the edges from the snapshot instead of scanning.
- `INoteReader` gains one method that returns the snapshot of a vault *(Claude's call: one seam, so `Kumbu.Core` stays free of the server; `InMemoryNoteStore` in the tests builds its snapshot the same way)*.
- Outputs of `links`, `backlinks`, `mv` and `rm` don't change by a character; the existing tests stay as they are and must pass unchanged. One exception, where one truth wins: `rm`'s count and the hints on create resolve today without the vault's attachments (`NoteWriter.cs:410`, `:488`) while the rest resolves with them; on the snapshot all resolve with them, so in rare cases (a missing `x.png` beside a note `x.png.md`) those two may answer differently, and a test that pins the old answer changes *(Claude's call, builder check)*.

## 10. Tests

Test-first, for every part. Beyond the unit tests of each query:

- **The oracle.** Today's scan (`NoteLinks.ToAsync`'s loop and `OfAsync` as they are now) moves into the test project as the reference. A property test generates vaults (notes in folders, wikilinks and Markdown links, aliases, embeds, headings and blocks, links in code, duplicate names in several folders, archived notes, attachments, case variants, missing targets) and checks that the snapshot's edges equal the oracle's, link for link. It runs again after random sequences of operations (create, edit, mv of notes and folders, rm, restore, alias changes, attachment uploads), with the snapshot kept current incrementally and compared to one built from scratch and to the oracle. *(Claude's call: a seeded generator in xUnit, no new dependency; the builder may choose a property-testing library if one is already in the solution.)*
- **Persistence**, against MongoDB as the other store tests do: an extract is written after the note's commit; a stale or missing one never changes the graph (it is parsed anew); sealed (the blob doesn't contain a link target in plain text), resealed by re-keying, removed with the vault; a vault unlocked with missing, stale-`scheme` or wrong-version extracts gets a complete graph and writes them back.
- **The version**: unchanged by prose edits, raised by a link added, a tag changed, a note created elsewhere that redirects a link; never repeating across a reload.
- **The creation time**: set on each way of creation, kept through mv, archive, restores; the "at the latest" fill for old notes.
- **Rights**: a hidden discreet vault absent from a graph over several vaults; a key without the vault refused.
- **Budget**: the local graph of a hub with 400 backlinks and depth 3 stays within the output budget with the line that says how to go on.
- **Load time**, measured not unit-tested: building the snapshot from extracts for 1,600 synthetic notes stays well under 50 ms on the CI machine; noted in the pull request.

## 11. Documentation

- `docs/concept.md` §7: the working set's link graph now has a sealed, persisted extract behind it.
- `docs/security-architecture.md`: the new collection in the list of sealed collections and in re-keying; the graph's rights (§8).
- `README.md`: `graph` in the list of commands.
- `help graph` and `Manual.cs`.

## 12. Left to the builder

- The exact JSON of the REST responses within §7.2's constraint, and the wording of `sh graph`'s other modes.
- How the snapshot is laid out in memory (adjacency lists, index maps), as long as queries stay linear and the snapshot immutable.
- How the write-back of extracts after an upgrade is scheduled (§4.2).
- Naming in code.

# Graph backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A sealed, persisted link extract per note, resolved in memory into a graph of each vault, the eight graph queries on it, `sh graph`, `/api/graph`, the creation time of notes, and `links`/`backlinks`/`mv`/`rm`/create hints moved onto the graph.

**Architecture:** Parsing (`LinkExtract`), the graph (`LinkGraph`) and its queries are pure code in `Kumbu.Core/Graph`, reached through one new `INoteReader.GraphAsync`. The server keeps extracts in the unlocked vault's working set, rebuilds the graph lazily on the first read after `Notes` or `Attachments` changed, and persists extracts as a cache in a sealed `links` collection, written after each commit and trusted only at the note's revision.

**Tech Stack:** C# / .NET 10, xUnit v3 on Microsoft.Testing.Platform, MongoDB (Testcontainers in `Kumbu.Server.Tests`).

**Spec:** `docs/superpowers/specs/2026-11-16-graph-backend-design.md` (read it whole before any task; section numbers below refer to it).

## Global Constraints

- British English in code, comments, docs and commit messages; source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- Test-first for every task: the failing test is committed with the code that makes it pass, in the same commit.
- No new NuGet dependency.
- No customer or employer named anywhere; test data uses neutral vaults (`W`, `Work`).
- Outputs of `links`, `backlinks`, `mv` and `rm` don't change by a character; existing tests pass unchanged, except the one case spec §9 names (attachments now resolved for `rm`'s count and the create hints).
- Nothing about a vault's graph in plaintext in MongoDB, logs or metrics (counts and timings only).
- Limits: local depth 1–3 (default 1), hub cap 1–100 (default 20), paths at most 5 of length at most 6, hubs top N default 20, first paragraph ~300 characters cut on a word boundary.
- Commit messages imperative and free-form, ending with the session's attribution lines.
- Run tests by project and filter: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-namespace "Kumbu.Core.Tests.Graph"`; server tests need Docker for MongoDB, as the existing ones do.

## Review Focus

1. **A shared vault renamed by a member** (`VaultAccess.Renamed`): every graph path the member sees is in her name for the vault, never the stored one; tested in Task 8.
2. **A note that doesn't open (damaged) or an extract that doesn't open:** the graph is built without it and nothing throws; tested in Task 9.
3. **Case and Unicode variants of one target** (`[[Anna]]`, `[[anna]]`, `[[Aná]]` in NFD): one ghost, and one note once it exists; tested in Task 3.
4. **A hub with hundreds of backlinks at depth 3 through `sh graph`:** stays within `OutputLimit.MaxChars` with the line saying how to go on, never `OutputLimit`'s generic footer; tested in Task 10.
5. **A hidden discreet vault in a web session:** absent from `/api/graph` over several vaults, and a request naming it explicitly is a `404` like any unknown vault; tested in Task 11.

---

### Task 1: The creation time of notes

**Files:**
- Modify: `src/Kumbu.Core/Notes/StoredNote.cs:93-106`
- Create: `src/Kumbu.Core/Notes/NoteCreation.cs`
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs:38-58` (`Next`)
- Modify: `src/Kumbu.Server/Vaults/UnlockedVault.cs:600-614` (`NotePayload`) and where `NotePayload` is built from a `StoredNote`
- Test: `tests/Kumbu.Core.Tests/Notes/NoteCreationTests.cs`, `tests/Kumbu.Server.Tests/Vaults/SealedPayloads.cs` (extend)

**Interfaces:**
- Produces: `StoredNote(..., DateTime? CreatedAt = null, bool CreatedAtLeast = false)` (two new optional parameters, last); `public static (DateTime At, bool AtLeast) NoteCreation.Of(StoredNote note)`.

Rule (spec §5): a note at `Revision == 1` was created at its `LastWriteAt`, so no creation site needs to change. `Of` returns `(CreatedAt, CreatedAtLeast)` when `CreatedAt` is set; else `(LastWriteAt, false)` at revision 1; else `(History[^1].WrittenAt, true)`, or `(LastWriteAt, true)` with an empty history. `NoteWriter.Next` stores `Of(current)` into `CreatedAt`/`CreatedAtLeast` of the next state. `mv` (`NoteMover.cs:272`, `with`) and trash/restore keep the fields as they are.

- [ ] **Step 1: Write the failing tests** in `NoteCreationTests`:
  - `New_note_was_created_at_its_first_write`: `Of(new StoredNote(p, "x", 1, "a", t1, []))` is `(t1, false)`.
  - `Next_keeps_the_creation_of_revision_one`: `Of(NoteWriter.Next(rev1 at t1, "y", …, now: t2))` is `(t1, false)` and its `CreatedAt == t1`.
  - `Old_note_takes_its_oldest_history_state_at_the_latest`: revision 7 without `CreatedAt`, history newest first `[t6, t3]`: `Of` is `(t3, true)`; after `Next` the stored `CreatedAt == t3`, `CreatedAtLeast == true`, and stays so through two more `Next`.
  - `Old_note_without_history_is_at_the_latest_its_last_write`: revision 4, empty history, `LastWriteAt t4`: `(t4, true)`.
  - In `SealedPayloads`: a note with `CreatedAt` and `CreatedAtLeast` round-trips through seal and open; a payload sealed without them opens with `CreatedAt == null`.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "Kumbu.Core.Tests.Notes.NoteCreationTests"`; expected: compile failure (`NoteCreation` missing).
- [ ] **Step 3: Implement** the two record parameters, `NoteCreation.Of`, the line in `Next`, and two optional payload fields `created` and `createdLeast` (`JsonIgnore(WhenWritingNull)`, like `msg`), so notes sealed before stay byte-identical when they don't carry them.
- [ ] **Step 4: Run** both test projects' `Notes`/`Vaults` namespaces; expected: PASS, and the whole `Kumbu.Core.Tests` project green.
- [ ] **Step 5: Commit** "Record when a note came into being".

### Task 2: The link extract

**Files:**
- Create: `src/Kumbu.Core/Graph/LinkExtract.cs`
- Test: `tests/Kumbu.Core.Tests/Graph/LinkExtractTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record LinkExtract(IReadOnlyList<NoteLink> Links, IReadOnlyList<string> Aliases, IReadOnlyList<string> Tags,
      CalendarTime? When, CalendarTime? Until)
  {
      public const int SchemeVersion = 1;
      public static LinkExtract Of(VaultPath path, string content);
  }
  ```

`Links` is `NoteLink.Parse(content)`; `Aliases` is `NoteAliases.Of(content)`; `Tags` the distinct tag names of `TagReader.Of(content)` in first-use order, as `sh tags` names them; `When`/`Until` from `EntryReader.Read(new Note(path, content), board: null)`, but none when the note's name is a day or week and its front matter sets no `when` (the node kind `day` carries that, spec §4.3).

- [ ] **Step 1: Write the failing tests:**
  - `Takes_links_aliases_tags_and_when`: a note with front matter `aliases: [Pumpe]`, `when: 2026-11-20`, `until: 2026-11-21`, body `#project/kumbu see [[Plan]] and ![x](/img/a.png)` gives two links (`Plan`, `/img/a.png` with `Embed`), `Aliases == ["Pumpe"]`, `Tags == ["project/kumbu"]`, `When`/`Until` those dates.
  - `Ignores_links_in_code`: a link inside a fence and inside backticks is not in `Links`.
  - `Day_note_without_front_matter_has_no_when`: `/W/2026-11-12.md` with body only: `When == null`.
  - `Scheme_is_pinned`: `Assert.Equal(1, LinkExtract.SchemeVersion)` beside a fixture text whose extract is asserted field by field (links with line and start, aliases, tags, when), with a comment: a change to any parser that changes this fixture's extract raises `SchemeVersion` and this test together (spec §4.5).
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-namespace "Kumbu.Core.Tests.Graph"`; expected: compile failure.
- [ ] **Step 3: Implement** `LinkExtract` and `Of`.
- [ ] **Step 4: Run** the same command; expected: PASS.
- [ ] **Step 5: Commit** "Extract a note's links, aliases, tags and time in one pass".

### Task 3: The graph, built from extracts

**Files:**
- Create: `src/Kumbu.Core/Graph/LinkGraph.cs` (graph, nodes, edges), `src/Kumbu.Core/Graph/GhostKey.cs`
- Test: `tests/Kumbu.Core.Tests/Graph/LinkGraphTests.cs`

**Interfaces:**
- Consumes: `LinkExtract` (Task 2), `LinkResolver`, `LinkResolution`, `ResolvedLink`.
- Produces:
  ```csharp
  public enum NodeKind { Note, Day, Attachment, Ghost }
  [Flags] public enum EdgeFlags { None = 0, Embed = 1, Alias = 2, Ambiguous = 4 }
  public sealed record GraphNode(string Key, VaultPath? Path, string Title, NodeKind Kind,
      IReadOnlyList<string> Tags, CalendarTime? When, CalendarTime? Until, bool Archived);
  public sealed record GraphEdge(int From, int To, EdgeFlags Flags, IReadOnlyList<int> Lines);   // Lines 0-based, ascending
  public sealed class LinkGraph
  {
      public static LinkGraph Build(string vault, IReadOnlyDictionary<VaultPath, LinkExtract> notes, IReadOnlyCollection<VaultPath> attachments);
      public string Vault { get; }
      public IReadOnlyList<GraphNode> Nodes { get; }
      public IReadOnlyList<GraphEdge> Edges { get; }
      public int? IndexOf(VaultPath path);                       // a note or attachment node
      public IReadOnlyList<int> OutEdges(int node);              // edge indices, by target path order
      public IReadOnlyList<int> InEdges(int node);               // edge indices, by source path order
      public IReadOnlyList<ResolvedLink> LinksOf(VaultPath note); // every link of the note with its resolution, in source order
      public IReadOnlyDictionary<VaultPath, LinkExtract> Extracts { get; }
      public IReadOnlyCollection<VaultPath> Attachments { get; }
      public string? Version { get; init; }                      // the ETag a store sets (Task 8); null elsewhere
      public bool SameAs(LinkGraph other);                       // same nodes, edges, flags, lines and attributes
      public LinkGraph Rebased(Func<VaultPath, VaultPath> map);  // every path through map (a renamed vault)
  }
  public static class GhostKey { public static string Of(string target); }
  ```

Rules, all from spec §4.3: node keys are the path's `Value` for notes and attachments and `"?" + GhostKey.Of(target)` for ghosts; `Day` by `EntryReader.IsDayOrWeekName`; `Archived` for paths with an `_archive` segment as `ArchiveFolder` defines it; attachments are nodes only when linked; one resolver per graph over all notes, attachments and aliases; one edge per (source, target) with the lines of every link behind it; ambiguous links make one edge per candidate, flagged; `Self` makes no edge; `Attachment` resolutions without a target and `NotFound` make ghost edges; a ghost's title is the target as first written in path order (source path, then line). `GhostKey.Of`: trim, drop the subpath (`#…`), drop a trailing `.md` (case-insensitive), normalise to NFC, then `ToUpperInvariant` — the same folding `LinkResolver` uses; keep any folder part.

- [ ] **Step 1: Write the failing tests** (each a vault built in the test from a few notes through `LinkExtract.Of`):
  - `Note_link_makes_one_edge_with_its_lines`: A links B twice (lines 2 and 5): one edge A→B, `Lines == [2, 5]`.
  - `Embed_and_alias_are_flags`: A embeds `![[B]]`, links `[[Pumpe]]` (B's alias) on another line: one edge, `Embed | Alias`.
  - `Ambiguous_link_points_to_each_candidate`: `[[Plan]]` from `/W/x/A.md` with `/W/y/Plan.md` and `/W/z/Plan.md`: two edges, both `Ambiguous`.
  - `Self_link_makes_no_edge`: `[[#Heading]]` gives no edge.
  - `Ghosts_meet_by_folded_name`: `[[Anna]]` in A, `[[anna]]` in B, `[[Aná]]` written in NFD as `Ana` + combining acute vs `[[Aná]]` in NFC in C: one ghost `Anna` with two in-edges, and one ghost for the accented pair.
  - `Ghost_with_folder_is_its_own`: `[[People/Anna]]` and `[[Anna]]`: two ghosts.
  - `Ghost_becomes_note_when_it_exists`: rebuilt with `/W/Anna.md` added, both edges lead to the note and no ghost remains.
  - `Day_and_attachment_kinds`: `/W/2026-11-12.md` is `Day`; a linked `/W/img/a.png` is `Attachment`; an unlinked attachment is no node.
  - `Missing_file_is_a_ghost_with_extension`: `[[report.docx]]` with no such attachment: ghost key `?REPORT.DOCX`.
  - `SameAs_ignores_order_of_construction_but_not_a_line`: two builds from the same extracts are `SameAs`; moving a link to another line is not.
  - `Rebased_maps_every_path`: rebased with `W` → `Shared`, every node path and `LinksOf` key starts with `/Shared/`.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "Kumbu.Core.Tests.Graph.LinkGraphTests"`; expected: compile failure.
- [ ] **Step 3: Implement** `LinkGraph.Build`, `GhostKey.Of` and the accessors; adjacency as arrays of edge indices built once in `Build` (immutable afterwards).
- [ ] **Step 4: Run** the same command; expected: PASS.
- [ ] **Step 5: Commit** "Build a vault's link graph from its notes' extracts".

### Task 4: The oracle

**Files:**
- Create: `tests/Kumbu.Core.Tests/Graph/ScanOracle.cs` (today's `NoteLinks.OfAsync`/`ToAsync` scan, copied verbatim as it is on `master` before Task 5, as a static helper over a list of `Note` and attachment paths)
- Create: `tests/Kumbu.Core.Tests/Graph/VaultGenerator.cs` (a seeded generator)
- Test: `tests/Kumbu.Core.Tests/Graph/OracleTests.cs`

**Interfaces:**
- Produces: `ScanOracle.Links(IReadOnlyList<Note> notes, IReadOnlyList<VaultPath> attachments, VaultPath note) -> IReadOnlyList<ResolvedLink>`; `ScanOracle.Backlinks(…, VaultPath target) -> IReadOnlyList<Backlink>`; `VaultGenerator.Vault(int seed, int notes) -> (List<Note> Notes, List<VaultPath> Attachments)`; `VaultGenerator.Step(int seed, …)` returning one random operation (create, edit, mv of a note or folder, rm, restore, alias change, attachment add or remove) applied to a copy.

The generator covers what spec §10 lists: folders, wikilinks and Markdown links, aliases, embeds, headings and blocks, links in code, the same name in several folders, archived notes, attachments, case variants, missing targets.

- [ ] **Step 1: Write the tests:**
  - `Graph_matches_the_scan` (`[Theory]` over seeds 1–200, 60 notes each): for every note, `graph.LinksOf(n)` equals `ScanOracle.Links` resolution for resolution; for every note and attachment, the in-edges flattened to (source, line, ambiguous) equal `ScanOracle.Backlinks`.
  - `Graph_after_operations_matches_a_fresh_build_and_the_scan` (seeds 1–50, 30 random steps each): after each step, `LinkGraph.Build` over the new state is `SameAs` a build from the extracts carried forward (extracts of untouched notes reused, changed ones re-extracted) and matches the oracle as above.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "Kumbu.Core.Tests.Graph.OracleTests"`; expected: PASS (the graph of Task 3 already exists; a failure here is a real defect in Task 3 to fix before going on).
- [ ] **Step 3: Commit** "Check the link graph against today's scan for generated vaults".

### Task 5: `INoteReader.GraphAsync` and the callers moved onto it

**Files:**
- Modify: `src/Kumbu.Core/Notes/INoteReader.cs` (default method), `tests/Kumbu.Core.Tests/Fakes/InMemoryNoteStore.cs` (keeps the default)
- Modify: `src/Kumbu.Core/Links/NoteLinks.cs:23-57` (`OfAsync`, `ToAsync`)
- Modify: `src/Kumbu.Core/Notes/NoteMover.cs:324` (`Rewrites` takes the graph's extracts instead of parsing), its caller
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs:410-418` (`rm`'s count from `InEdges`), `:488-…` (`LinkHintsAsync` from the graph's extracts and name sets)
- Test: `tests/Kumbu.Core.Tests/Graph/GraphReaderTests.cs`; every existing test under `tests/Kumbu.Core.Tests/{Links,Notes,Sh}` unchanged

**Interfaces:**
- Consumes: `LinkGraph` (Task 3).
- Produces: `Task<LinkGraph> INoteReader.GraphAsync(VaultPath vault, CancellationToken ct)`, default implementation: `LinkGraph.Build` over `ReadNotesUnderAsync(vault)` and the vault's attachments (`StoreAttachments.PathsUnderAsync`). `CheckpointView` and the fakes inherit it.

`OfAsync(notes, path, content, ct)`: when `content` is the note's current content, the graph's `LinksOf(path)`; for another version (`links --version`), parse `content` and resolve with a resolver over the graph's name sets. `ToAsync`: the target's in-edges, exact lines first then the ambiguous ones, each line's text from the source's content, in today's order. `Rewrites` gets `IReadOnlyDictionary<VaultPath, LinkExtract>` for the links and keeps its before/after resolvers (the "after" state is hypothetical, so it can't come from the graph).

- [ ] **Step 1: Write the failing test** `GraphReaderTests.Backlinks_parse_no_note`: an `INoteReader` wrapper counting `ReadNotesUnderAsync` and returning a prebuilt graph from `GraphAsync` (overriding the default): `NoteLinks.ToAsync` on it calls `ReadNotesUnderAsync` at most once (for line texts) and never parses through `NoteLink.Parse` (asserted by a graph whose extracts differ from the content: the answer follows the graph).
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "Kumbu.Core.Tests.Graph.GraphReaderTests"`; expected: FAIL.
- [ ] **Step 3: Implement** the default method and move the four callers.
- [ ] **Step 4: Run** the whole `Kumbu.Core.Tests` project; expected: PASS with no existing test changed. If a test of `rm`'s count or the create hints fails only because attachments now resolve (spec §9's exception), change that test's expectation and say so in the commit message; any other failure is a defect.
- [ ] **Step 5: Commit** "Answer links, backlinks, mv and rm from the link graph".

### Task 6: Filter, local graph, global graph, node

**Files:**
- Create: `src/Kumbu.Core/Graph/GraphFilter.cs`, `src/Kumbu.Core/Graph/GraphQueries.cs`
- Test: `tests/Kumbu.Core.Tests/Graph/GraphQueryTests.cs`

**Interfaces:**
- Consumes: `LinkGraph`, `NoteCreation.Of` (Task 1).
- Produces:
  ```csharp
  public enum GraphDirection { Out, In, Both }
  public sealed record GraphFilter(IReadOnlyList<VaultPath> Paths, IReadOnlyList<string> Tags,
      bool Days = true, bool Attachments = true, bool Ghosts = true, bool Archive = false)
  { public static GraphFilter All { get; } }
  public sealed record GraphSelection(IReadOnlyList<int> Nodes, IReadOnlyList<int> Edges);
  public sealed record LocalGraph(IReadOnlyList<(int Node, int Depth)> Nodes, IReadOnlyList<int> Edges, IReadOnlyDictionary<int, int> LeftOut);
  public sealed record NodeDetail(GraphNode Node, int In, int Out, DateTime? Changed, DateTime? Created, bool CreatedAtLeast, string? FirstParagraph);
  public static partial class GraphQueries
  {
      public static GraphSelection Global(LinkGraph g, GraphFilter f);
      public static LocalGraph Local(LinkGraph g, VaultPath note, int depth, GraphDirection dir, int cap, GraphFilter f);
      public static NodeDetail Node(LinkGraph g, int node, StoredNote? note);
  }
  ```

Filter semantics (spec §6): `Paths` and `Tags` select notes (day notes are notes; tags match as `rg --tag` matches nested tags); an attachment or ghost stays when a kept note links to it; `Days`, `Attachments`, `Ghosts`, `Archive` hide their kinds; a filter never changes resolution. `Local`: breadth-first, each node once at its first depth, the start always shown; a node with more neighbours than `cap` keeps `cap` of them ordered by degree (in plus out) descending, then path, and `LeftOut[node]` counts the rest; ghosts and attachments are never walked beyond; edges returned are those between returned nodes. `Node`: degree in and out; times from `NoteCreation.Of(note)` and `note.LastWriteAt`; first paragraph: the first block after the front matter that is not a heading, cut at about 300 characters on a word boundary.

- [ ] **Step 1: Write the failing tests:** `Global_keeps_every_edge_uncapped` (a hub with 400 in-edges: all 400), `Filter_by_path_keeps_linked_ghosts_and_attachments`, `Filter_by_tag_includes_day_notes`, `Archive_hidden_by_default`, `Local_depth_two_each_node_once`, `Local_direction_in_only`, `Local_caps_a_hub_by_degree_then_path` (hub with 30 neighbours, cap 20: the 20 of highest degree, `LeftOut[hub] == 10`), `Local_start_shown_despite_filter`, `Local_does_not_walk_through_ghosts`, `Node_reads_degree_times_and_first_paragraph` (front matter, a heading, then a 400-character paragraph: cut under ~300 characters at a space, `CreatedAtLeast` passed through).
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "Kumbu.Core.Tests.Graph.GraphQueryTests"`; expected: compile failure.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same; expected: PASS.
- [ ] **Step 5: Commit** "Query a vault's graph: global, local with a hub cap, and one node".

### Task 7: Orphans, hubs, unresolved, bundle, paths

**Files:**
- Modify: `src/Kumbu.Core/Graph/GraphQueries.cs`
- Create: `src/Kumbu.Core/Graph/GraphPaths.cs`
- Test: `tests/Kumbu.Core.Tests/Graph/GraphQueryTests.cs` (extend), `tests/Kumbu.Core.Tests/Graph/GraphPathsTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static IReadOnlyList<int> Orphans(LinkGraph g, GraphFilter f);
  public static IReadOnlyList<(int Node, int In, int Out)> Hubs(LinkGraph g, GraphFilter f, int top);
  public static IReadOnlyList<(int Ghost, IReadOnlyList<(VaultPath Source, int Line)> From)> Unresolved(LinkGraph g, GraphFilter f);
  public static GraphSelection Bundle(LinkGraph g, VaultPath note, int depth, Func<VaultPath, bool> admit);
  public enum Disconnection { None, NotConnected, FartherThanLimit }
  public sealed record PathsResult(IReadOnlyList<IReadOnlyList<int>> Paths, bool More, Disconnection Why);  // each path: edge indices from start to end
  public static PathsResult Paths(LinkGraph g, VaultPath from, VaultPath to, GraphDirection dir);  // Both or Out
  ```

`Orphans`: notes with no edge either way (attachments and ghosts never). `Hubs`: notes by in plus out, then path. `Unresolved`: ghosts by number of distinct source notes, descending, then key. `Bundle`: outgoing breadth-first from `note` to `depth`, entering only notes `admit` accepts, adding the attachments reached notes link to or embed, edges between the selected nodes. `Paths`: bidirectional breadth-first over notes only, at most 5 shortest paths, length at most 6, `More` when further paths of the same length exist; with none found, `NotConnected` when a full search from `from` never reaches `to`, else `FartherThanLimit`; `dir: Out` follows edges as written.

- [ ] **Step 1: Write the failing tests:** `Orphans_are_notes_only`, `Hubs_ordered_by_degree_then_path`, `Unresolved_ordered_by_sources`, `Bundle_stops_at_depth_and_at_refused_notes` (a refused note is neither in the result nor walked through), `Bundle_takes_embedded_attachments`; in `GraphPathsTests`: `Direct_link_is_one_step`, `Both_directions_by_default` (A→C←B is a path A–B), `Out_follows_links_as_written` (same vault: no path), `At_most_five_paths_with_more` (A and B joined through 8 day notes: 5 paths, `More`), `Seven_steps_are_farther_than_the_limit`, `Separate_islands_are_not_connected`.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-namespace "Kumbu.Core.Tests.Graph"`; expected: compile failure.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same; expected: PASS.
- [ ] **Step 5: Commit** "Find orphans, hubs, unresolved links, bundles and paths in the graph".

### Task 8: The graph in the server's working set, with its version

**Files:**
- Modify: `src/Kumbu.Server/Vaults/UnlockedVault.cs` (extracts and the lazy graph)
- Modify: `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (`GraphAsync` override)
- Test: `tests/Kumbu.Server.Tests/Vaults/GraphStoreTests.cs`

**Interfaces:**
- Consumes: `LinkExtract`, `LinkGraph`.
- Produces: in `UnlockedVault`: `internal ImmutableDictionary<VaultPath, (long Rev, LinkExtract Extract)> Extracts { get; set; }`; `public LinkGraph Graph()`: under a lock of its own, returns the cached graph if `Notes` and `Attachments` are the same objects it was built from, else builds a new one from `Extracts` (parsing and storing only notes whose `Rev` differs from the note's `Revision` or that are missing), raises a version counter when the new graph is not `SameAs` the old, and sets `Version = $"{epoch}.{counter}"`, `epoch` a random 64-bit hex value drawn when the object is created. `EncryptedNoteStore.GraphAsync(vault)`: the access's `UnlockedVault.Graph()`, `Rebased(access.ToCaller)` when `access.Renamed`, keeping `Version`.

No change to any write path: they all replace `Notes` or `Attachments` (spec §4.4).

- [ ] **Step 1: Write the failing tests** (Mongo fixture as `EncryptedNoteStoreTests`): `Graph_follows_a_write` (write A linking B; `GraphAsync` shows the edge; write B; still there; edit A to drop the link; gone), `Version_unchanged_by_prose`, `Version_raised_by_a_new_link_a_tag_and_a_redirecting_note`, `Version_never_repeats_after_a_reload` (lock and unlock: the ETag differs though the graph is the same), `Renamed_shared_vault_shows_the_members_names` (Review Focus 1, with the setup `RenamedOwnerTests` uses), `Second_read_without_change_builds_nothing` (the same `LinkGraph` instance).
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "Kumbu.Server.Tests.Vaults.GraphStoreTests"`; expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same and the server's `Vaults` and `Notes` namespaces; expected: PASS.
- [ ] **Step 5: Commit** "Keep a vault's link graph in its working set, versioned".

### Task 9: Persisting the extracts

**Files:**
- Modify: `src/Kumbu.Server/Vaults/VaultCatalog.cs` (`Links` collection beside `Embeddings` at `:64`, its index as at `:1731`, `ContentCollections` at `:757`, loading in `LoadAsync` at `:1471`)
- Modify: `src/Kumbu.Server/Vaults/VaultCatalog.Rekey.cs:46` and `:728` (the collection resealed)
- Modify: `src/Kumbu.Server/Vaults/UnlockedVault.cs` (`SealExtract`, `OpenExtract`, `ExtractPayload`, and the rekey's `Resealed` case beside embeddings at `:418`)
- Modify: `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs:1052` (after `apply()`: write the extracts of `paths`)
- Test: `tests/Kumbu.Server.Tests/Vaults/LinkExtractStoreTests.cs`, `tests/Kumbu.Server.Tests/Vaults/RekeyTests.cs` (extend)

**Interfaces:**
- Produces: `byte[] UnlockedVault.SealExtract(VaultPath path, long rev, LinkExtract extract)` with context `links:{Id}:{base64(PathId(path))}`; `(VaultPath Path, long Rev, int Scheme, LinkExtract Extract) UnlockedVault.OpenExtract(SealedDocument doc)` (path id checked as `OpenEmbeddings` does); `Task VaultCatalog.WriteExtractsAsync(UnlockedVault vault, IReadOnlyList<VaultPath> paths)`: for each note path now in `vault.Notes`, upsert its extract at its revision (from `Extracts`, parsing if needed), for each path no longer a note, delete the document; failures logged and swallowed (the extract is a cache).

Load: open every `links` document of the vault; keep those whose `Scheme == LinkExtract.SchemeVersion` and whose `Rev` matches the loaded note's revision into `Extracts`; after `Unlocked?.Invoke`, write back (fire and forget, under the vault's gate, in batches) the extracts of notes that had none or a stale one, and delete documents of paths that are no notes. A `links` document that doesn't open is ignored (logged once per vault), never marks a note damaged. The payload: `ExtractPayload(string Path, long Rev, int Scheme, …)` with the links as small records (kind, embed, target, subpath, line, start, raw), `CalendarTime` as its canonical text.

- [ ] **Step 1: Write the failing tests:** `Extract_is_written_after_the_commit_sealed` (the blob contains neither the target text nor the tag in UTF-8), `Unlock_uses_stored_extracts_without_parsing` (a counter on parsing, exposed for tests, stays 0 for a vault whose extracts are current), `Stale_rev_or_scheme_is_parsed_and_written_back`, `Missing_extract_never_changes_the_graph` (delete the documents, reload: the same graph, `SameAs`), `Damaged_extract_is_ignored` and `Damaged_note_is_left_out` (Review Focus 2), `Removed_note_removes_its_extract`; in `RekeyTests`: the `links` documents open under the new key after re-keying; vault deletion leaves no `links` document.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "Kumbu.Server.Tests.Vaults.LinkExtractStoreTests"`; expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same, `RekeyTests`, and the whole `Kumbu.Server.Tests` `Vaults` namespace; expected: PASS. Then measure once: build the graph from stored extracts for 1,600 synthetic notes on this machine and note the time for the pull request (spec §10, target well under 50 ms).
- [ ] **Step 5: Commit** "Persist the link extracts, sealed, as a cache of the graph".

### Task 10: `sh graph`

**Files:**
- Create: `src/Kumbu.Core/Sh/Commands/GraphCommand.cs`
- Modify: `src/Kumbu.Core/Sh/Shell.cs:85` (register beside `Backlinks`), `src/Kumbu.Core/Sh/Manual.cs:20` (one line under `backlinks`)
- Test: `tests/Kumbu.Core.Tests/Sh/GraphCommandTests.cs`

**Interfaces:**
- Consumes: `INoteReader.GraphAsync`, `GraphQueries`, `GraphPaths`.
- Produces: `public sealed class GraphCommand(INoteReader notes) : ICommand`, `Name == "graph"`, `Usage == "graph <note> [--depth N] [--out|--in] [--cap N] | --path <a> <b> [--out] | --orphans|--hubs [-n N]|--unresolved [path]"`.

Options and output exactly as spec §7.1, including the local graph's example (heading line with counts, `depth  source -> target`, `(embed)`, `Name (no note)`, the indented `+N more linking to …: graph … --in` line). Paths print one per line with each arrow in its link's own direction. Shared options `--tag`, `--archive`, `--no-days`, `--no-attachments`, `--no-ghosts`. Errors in the style of `backlinks` (unknown note, depth outside 1–3, cap outside 1–100, two modes at once). The command budgets itself: it stops adding lines before `OutputLimit.MaxChars` and ends with one line naming how to narrow it (a smaller `--depth`, `--tag`, or the node to go on from), so `OutputLimit`'s generic footer never appears.

- [ ] **Step 1: Write the failing tests:** `Local_graph_prints_as_the_spec_shows` (the spec's example vault rebuilt, output compared whole), `Path_prints_arrows_in_link_direction`, `Not_connected_and_farther_than_six_are_told_apart`, `Orphans_hubs_unresolved_list`, `Hub_at_depth_three_stays_in_budget` (Review Focus 4: 400 backlinks into a hub, depth 3; output length ≤ `OutputLimit.MaxChars`, last line names how to narrow, no "[truncated"), `Bad_depth_and_two_modes_are_errors`, `Help_names_every_mode`.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "Kumbu.Core.Tests.Sh.GraphCommandTests"`; expected: compile failure.
- [ ] **Step 3: Implement**, register, add the `Manual.cs` line and `help graph` text with an example per mode.
- [ ] **Step 4: Run** the whole `Kumbu.Core.Tests` project (the manual's tests check every command is listed); expected: PASS.
- [ ] **Step 5: Commit** "Add sh graph: local graphs, paths, orphans, hubs and unresolved links".

### Task 11: `/api/graph`

**Files:**
- Create: `src/Kumbu.Server/Api/GraphApi.cs` (`MapGraphApi`), registered in `src/Kumbu.Server/Program.cs:321` beside `MapNoteApi`
- Test: `tests/Kumbu.Server.Tests/Api/GraphApiTests.cs`

**Interfaces:**
- Consumes: `INoteStore` (the request's store), `GraphQueries`, `GraphPaths`, `INoteStore.GetAsync` for the node's times.
- Produces: `GET /api/graph?vault=…&vault=…&<filter>`, `GET /api/graph/local?path&depth&dir&cap&<filter>`, `GET /api/graph/node?path`, `GET /api/graph/paths?from&to&dir`; filter parameters `path` (repeatable), `tag` (repeatable), `days`, `attachments`, `ghosts`, `archive` (booleans).

JSON within spec §7.2: `{ "nodes": [{ "path", "title", "kind", "tags", "when", "until", "archived", "in", "out" }], "edges": [[from, to, flags]] }`, `kind` in lower case, `flags` the `EdgeFlags` integer, ghosts with `path: null` and their title; local adds `"depth"` per node and `"leftOut": { "<index>": n }`; node answers `NodeDetail` with times in ISO 8601 UTC. The global and local graph send `ETag` (the vaults' `Version`s joined with `,` in request order, quoted) and answer `304` to a matching `If-None-Match`, as `SyncApi.cs:152-158` does. Validation at the boundary: vault names the caller has (else `404` as for an unknown vault), depth 1–3, cap 1–100, `dir` in `out|in|both`, paths through `ApiPaths.Parse`; anything else `400`. Errors declared with `.Errors(…)` like `NoteApi`.

- [ ] **Step 1: Write the failing tests:** `Global_graph_of_two_vaults_with_etag_and_304`, `Local_graph_validates_depth_and_cap` (0 and 4, 0 and 101: `400`), `Node_has_times_and_first_paragraph`, `Paths_between_two_notes`, `Discreet_vault_hidden_from_a_session` (Review Focus 5: the session that hides it gets a graph without it and `404` when naming it; with the setup of `tests/Kumbu.Server.Tests/Discreet`), `Key_without_the_vault_is_refused`, `Prose_edit_keeps_the_etag`.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "Kumbu.Server.Tests.Api.GraphApiTests"`; expected: FAIL (404 on the routes).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same and the OpenAPI test if one checks the document; expected: PASS.
- [ ] **Step 5: Commit** "Serve the graph to the web app under /api/graph".

### Task 12: Documentation and the whole suite

**Files:**
- Modify: `docs/concept.md` §7 (the working set's link graph has a sealed, persisted extract behind it, a cache trusted at the note's revision), `docs/security-architecture.md` (the `links` collection among the sealed collections and in re-keying; the graph's rights as spec §8), `README.md` (`graph` in the list of commands)

- [ ] **Step 1: Write** the three changes, matching each document's style and density.
- [ ] **Step 2: Run** the full suite as `CLAUDE.md` gives it: `dotnet test --solution Kumbu.slnx`; expected: all pass, the 4 known skips only. Then `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build` (unchanged web; must stay green).
- [ ] **Step 3: Commit** "Document the graph backend".

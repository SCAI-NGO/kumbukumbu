# Publish 2.0 (bundles) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A publication carries its start note and the notes it reaches by outgoing links, to a depth the publisher chooses, behind one secret link.

**Architecture:**
- Today's single-note publication becomes a bundle of depth 0; there is no second kind.
- The walk is `GraphQueries.Bundle`. Its predicate is the hard blacklist: marks, skill places, and the publisher's one-off exclusions.
- The copy stays one sealed blob. `PublishedCopy` gains the bundle's other notes and the edges between them.
- Inside the copy, a link to a bundle note is written as `#kn-<id>` (and `#kn-<id>/<subpath>`). The reader resolves it.
- When a note leaves the copy, its links in the other notes become plain text again. `FairCopies.Unlink` does this rewrite, and it knows what is code.

**Tech Stack:** C# / .NET 10, MongoDB, xUnit; React 19, TanStack Query, react-markdown, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-18-publish-bundles-design.md`

## Global Constraints

- **Depth:** 0 to 5, default 0. The red warning appears from 50 notes on (the start note included). The hard limit is 500 notes.
- **Size:** `Publications.CopyLimit` (64 MB) counts for the whole bundle's images.
  - A bundle with more than the start note is refused when its images are estimated above the limit.
  - A single note keeps today's behaviour: an image that doesn't fit becomes plain text.
- **Unticked notes** (`without`) and marked notes are never entered and never walked through.
- **What was seen is what goes:** `POST` and `PUT` carry `expected`, the sorted list of note paths the preview showed. If the publish-time walk differs, the server publishes nothing and answers `409` with the new preview.
- **Unchanged:** only the owner publishes, with "May publish". There is a fair copy for every note, and links never cross a vault.
- **Language:** British English. SPDX headers where the siblings have them. Comments match the surrounding density.

## Review Focus

1. **A note reachable two ways,** once through a marked note and once directly. It must be taken, at its shortest admitted depth. *Test in Task 2.*
2. **A link to a heading inside a bundle note,** written as a Markdown link with spaces (`[x](Other%20note.md#Big%20idea)`). It must land on the heading. *Tests in Task 1 and Task 6.*
3. **A note inside a bundle is moved** (renamed, not into a mark). It must stay in the bundle, and the bar must follow it. *Test in Task 4.*
4. **Code that contains `](#kn-3)`.** `FairCopies.Unlink` must leave it byte for byte. *Test in Task 1.*
5. **An existing single-note copy stored before this change.** It must still open in the reader, and update as a depth-0 bundle. *Test in Task 3.*

---

### Task 1: Links between the notes of a copy (Core)

**Files:**
- Modify: `src/Kumbu.Core/Publishing/FairCopies.cs`
- Test: `tests/Kumbu.Core.Tests/Publishing/FairCopyTests.cs`

**Interfaces:**
- Produces:
  - `FairCopies.Make(VaultPath note, string content, Func<string, VaultPath?> attachment, Func<VaultPath, bool> mayCopy, string? ownOrigin = null, Func<string, int?>? noteLink = null)`. `noteLink` receives a vault link's target without its `#subpath` and returns the bundle id of the note, or null. Null means plain text, as today.
  - `public const string NoteLinkPrefix = "#kn-";`
  - `FairCopies.Unlink(string markdown, Func<int, bool> keep) -> string`. A note link or note embed whose id `keep` refuses becomes its text. Code is left untouched.
  - `FairCopies.NoteIds(string markdown) -> IReadOnlySet<int>`: the ids the copy links to, outside code.

- [ ] **Step 1: Write the failing tests**
  - `A_wikilink_to_a_bundle_note_links_inside_the_copy`: `[[Plan]]` with `noteLink("Plan") = 3` gives `[Plan](#kn-3)`.
  - `A_link_to_a_heading_or_block_keeps_its_subpath`:
    - `[[Plan#Big idea|the idea]]` gives `[the idea](#kn-3/Big%20idea)`;
    - `[x](Plan.md#^b1)` gives `[x](#kn-3/%5Eb1)`;
    - `[x](Other%20note.md#Big%20idea)` with `noteLink("Other%20note.md") = 4` gives `[x](#kn-4/Big%20idea)`. The subpath is unescaped, then encoded with `Uri.EscapeDataString`.
  - `An_embed_of_a_bundle_note_stays_an_embed`: `![[Plan]]` gives `![Plan](#kn-3)`.
  - `A_note_not_in_the_bundle_stays_plain_text`: `noteLink` returns null, so the output is unchanged from today.
  - `Unlink_turns_links_to_dropped_notes_into_their_text`: `[a](#kn-3) [b](#kn-4/x) ![c](#kn-3)` with `keep = id != 3` gives `a [b](#kn-4/x) c`. Escaped brackets in the text (`[a \] b](#kn-3)`) become `a ] b`.
  - `Unlink_leaves_code_alone`: a fence and a code span that contain `[a](#kn-3)` stay byte for byte.
  - `NoteIds_reads_ids_outside_code`.

- [ ] **Step 2: Run, expect FAIL**

  Run: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*FairCopyTests"`

  Expected: compile errors or failing tests.

- [ ] **Step 3: Implement**
  - In `Copier`, a vault link (`Kind.Vault`, not an embed) whose `noteLink` gives an id keeps its text and gets the destination `#kn-<id>[/<sub>]`, both brackets kept.
  - In `WikiText`, the same for a wikilink. An embed with an id gives `![shown](#kn-<id>)`, kept.
  - `Unlink` and `NoteIds` use `CodeLines` and `CodeSpans` to skip code, with the regex `!?\[((?:\\.|[^\\\]])*)\]\(#kn-(\d+)(?:/[^)\s]*)?\)`.
  - A user's own `(#kn-3)` outside code is already plain text, because it is `Kind.Vault` and no note resolves from it. So outside code only our links match.

- [ ] **Step 4: Run, expect PASS** (the whole `FairCopyTests` class).

- [ ] **Step 5: Commit** `Link the notes of a published copy to each other`

### Task 2: The walk and its preview (server)

**Files:**
- Create: `src/Kumbu.Server/Publishing/Bundles.cs`
- Modify: `src/Kumbu.Server/Publishing/Publications.cs`
- Test: `tests/Kumbu.Server.Tests/Publishing/BundleTests.cs` (new)

**Interfaces:**
- Produces:
  - `public sealed record BundleNoteLine(VaultPath Path, string Title, int Depth);`
  - `public sealed record BundleLeftOut(VaultPath Path, string Reason);`, where `Reason` is one of `BundleReasons.Unpublishable = "unpublishable"`, `Behind = "behind"`, `Without = "without"`, `Pdf = "pdf"`, `TooLarge = "too-large"`.
  - `public sealed record BundlePreview(IReadOnlyList<BundleNoteLine> Notes, IReadOnlyList<BundleLeftOut> LeftOut, long Bytes, bool TooMany, bool TooLarge);`. `Notes` is in walk order, the start first. `TooMany` is true for more than 500 notes; `TooLarge` for an image estimate above `CopyLimit` when there is more than one note.
  - `Publications.PreviewAsync(VaultAccess access, EncryptedNoteStore notes, VaultPath note, int depth, IReadOnlyList<VaultPath> without, CancellationToken ct) -> Task<BundlePreview>`. It checks as publishing does (`Check(access, publishing: true)`, skill place, the start note not marked) and writes nothing.
  - `internal const int MaxDepth = 5, NoteLimit = 500;`
- Walk:
  - The graph is `notes.GraphAsync(root)`.
  - `admit(p)` means: not `Covered(marks, stored(p))`, not `InSkillPlace`, and not in `without`.
  - The walk is `GraphQueries.Bundle(g, note, depth, admit)`.
  - `LeftOut`: the notes the same walk reaches with `admit = _ => true` that were not taken. Their reason is `unpublishable` (covered or in a skill place), `without`, or `behind`.
  - Embeds that would become plain text, PDFs among them, are listed by their attachment path with `pdf` or `unpublishable`.
  - `Bytes` is the sum of the image attachments' stored sizes, each counted once.
  - The depth is each note's first BFS depth.

- [ ] **Step 1: Write the failing tests** (in Mongo, as `PublicationsTests` builds Alice):
  - `Depth_0_is_the_note_alone`.
  - `Depth_2_takes_links_of_links_in_walk_order`: A→B→C→D with depth 2 gives A, B, C.
  - `A_marked_note_is_neither_taken_nor_walked_through`: A→M(marked)→X gives X left out as `behind`, and M as `unpublishable`.
  - `A_note_reached_another_way_is_taken`: A→M→X and A→X, so X is taken at depth 1.
  - `A_marked_folder_blocks_its_notes`.
  - `Without_blocks_like_a_mark`, with the reason `without`.
  - `Nothing_crosses_a_vault`.
  - `A_pdf_embed_is_listed`.
  - `More_than_500_notes_is_too_many` (`NoteLimit` lowered through an `internal init` for the test).
  - `Members_and_keys_without_May_publish_are_refused`.
  - `Depth_outside_0_to_5_is_invalid`: a `PublishRefusedException` with `Invalid`.

- [ ] **Step 2: Run, expect FAIL**

  Run: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*BundleTests"` (with `KUMBU_TEST_MONGO`)

- [ ] **Step 3: Implement** `PreviewAsync` and the walk helper, in `Bundles.cs` as a `partial` of `Publications`.

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit** `Walk a note's links for a publication, with marks as a hard blacklist`

### Task 3: Publish and update a bundle

**Files:**
- Modify:
  - `src/Kumbu.Server/Publishing/PublishingRecord.cs` (`PublicationEntry`, `PublishedCopy`)
  - `src/Kumbu.Server/Publishing/Publications.cs`
  - `src/Kumbu.Server/Publishing/PublicationApi.cs`
  - `src/Kumbu.Server/Sharing/VaultEvents.cs` (the activity sentences)
- Test: `tests/Kumbu.Server.Tests/Publishing/BundleTests.cs`

**Interfaces:**
- Produces:
  - `PublicationEntry` gains `int? Depth = null`, `IReadOnlyList<string>? Without = null` (stored paths) and `IReadOnlyList<BundleMember>? Members = null`, where `BundleMember(int Id, string Note, string Version)` covers every note but the start. All are optional, so stored records read as depth 0.
  - `PublishedCopy` gains `IReadOnlyList<PublishedBundleNote>? Notes = null` and `IReadOnlyList<int[]>? Edges = null`, where `PublishedBundleNote(int Id, string Title, string Markdown, int Depth, IReadOnlyList<int> Files)`. The start note has id 0 and stays in `Title`/`Markdown`. `Edges` are `[from, to]` pairs of ids, kept for a later graph and not served. Old copies deserialise with nulls.
  - `Publications.PublishAsync(..., int depth = 0, IReadOnlyList<VaultPath>? without = null, IReadOnlyList<VaultPath>? expected = null)`, and `UpdateAsync` the same.
    - `expected` null means walk and publish in one step (used by `sh`).
    - When `expected` is given and the walk's sorted paths differ, it throws `BundleChangedException(BundlePreview Now)`.
  - `PublishedNote` gains `IReadOnlyList<VaultPath> Notes` and `IReadOnlyList<VaultPath> Added`, `Removed` (empty after a first publish).
  - The API:
    - `PublishRequest(string? Password = null, int Depth = 0, string[]? Without = null, string[]? Expected = null)`;
    - `UpdateRequest(int? Depth, string[]? Without, string[]? Expected)` as the optional body of `PUT`. A missing depth keeps the entry's.
    - `GET /api/publication-previews/{vault}/{**path}?depth=&without=` answers `PublicationPreviewView`.
    - `BundleChangedException` maps to `409` with `{ preview }`.
    - Every path parameter is parsed with `ApiPaths.Parse(vault, p, note: true)`; anything else is a `400`.
  - The log entries keep their kinds. A new optional field `Count` on `PublishingLogEntry` (`int? Count = null`, `int? Added`, `int? Removed`) feeds the sentences "published *Start* with 12 notes" and "updated *Start*: +2, −1". Single notes keep today's sentences.
- Copy making:
  - `MakeAsync` runs per note with `noteLink = target => resolver.Resolve(source, target) is { Kind: LinkKind.Note, Target: t } && ids.TryGetValue(t, out var id) ? id : null`.
  - Files are numbered across the bundle; one file used by several notes is copied once.
  - Each `PublishedBundleNote.Files` lists the indices that note uses.
  - The ids follow walk order (start = 0).

- [ ] **Step 1: Write the failing tests**
  - `Publishing_depth_1_carries_the_linked_notes_with_links_inside_the_copy`: open the copy with `Store.OpenCopyAsync`. Expect two notes, the link `#kn-1`, and the edges `[[0,1]]`.
  - `An_expected_set_that_differs_publishes_nothing_and_gives_the_new_preview`.
  - `Update_keeps_the_depth_and_without_and_reports_added_and_removed`.
  - `Update_with_a_new_depth_changes_it`.
  - `The_activity_says_how_many_notes`.
  - `A_copy_stored_before_bundles_still_opens_and_updates_as_depth_0`: write a record entry and a copy without the new fields, as JSON.
  - `Older_is_true_when_any_note_of_the_bundle_changed`.
  - `A_bundle_over_the_image_limit_is_refused` (`CopyLimit` lowered).

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement** the record fields, `MakeBundleAsync` (the per-note `MakeAsync` plus shared file numbering), `expected` checking inside the attempt loop, the API endpoints and the event sentences.

- [ ] **Step 4: Run** `BundleTests`, `PublicationsTests`, `PublicationApiTests` and `PublicationStoreTests`. Expect PASS.

- [ ] **Step 5: Commit** `Publish and update a note with its linked notes`

### Task 4: Notes leave the copy at once

**Files:**
- Modify:
  - `src/Kumbu.Server/Publishing/Publications.cs` (`SetUnpublishableAsync`, `FollowAsync`)
  - `src/Kumbu.Server/Publishing/PublicationStore.cs` (`DeleteFilesAsync`)
- Test: `tests/Kumbu.Server.Tests/Publishing/BundleTests.cs`

**Interfaces:**
- Produces:
  - `PublicationStore.DeleteFilesAsync(IClientSessionHandle session, ObjectId publication, IReadOnlyCollection<int> indices, CancellationToken ct)`.
  - `Bundles.Pruned(PublishedCopy copy, IReadOnlySet<int> dropped) -> (PublishedCopy Copy, IReadOnlySet<int> Gone, IReadOnlySet<int> FreedFiles)`. `Gone` holds the dropped ids plus the ids no longer reachable from 0 over the stored edges. Their links become plain text with `FairCopies.Unlink`. `FreedFiles` are the file indices that only gone notes used.
- Rules:
  - For an entry with `Members`, a member that a batch trashes, that becomes newly covered, or that lands in a skill place is dropped.
  - Members that move follow (`Note` rewritten).
  - The start note keeps today's rule: withdraw.
  - The prune happens in the same transaction. It opens the copy with the entry's key (`OpenCopyAsync`), replaces it (`ReplaceCopyAsync`, kept files only) and deletes the freed files.
  - The log adds an `updated` entry with `Removed`, and the reason `unpublishable` or `removed`.
  - The prune is done by whoever changes the note. A share's or member's batch that trashes a member note prunes it, because that only reduces what is out. Only the start note keeps the owner-only rule.

- [ ] **Step 1: Write the failing tests**
  - `Marking_a_bundle_note_takes_it_out_and_its_links_become_text`: the reader copy has no note 1, and note 0's `[B](#kn-1)` is `B`.
  - `Notes_reached_only_through_it_leave_too`: A→B→C with B marked gives A alone.
  - `A_note_reached_another_way_stays`.
  - `Removing_a_bundle_note_takes_it_out`.
  - `Moving_a_bundle_note_into_a_marked_folder_takes_it_out`.
  - `Renaming_a_bundle_note_keeps_it_in_the_bundle`.
  - `Its_images_go_with_it_unless_another_note_uses_them`.
  - `Marking_the_start_note_still_withdraws_everything`.

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run** the `Publishing`, `Dav` and `Notes` namespaces. Expect PASS.

- [ ] **Step 5: Commit** `Take a note out of every copy it is in once it is marked, moved under a mark or removed`

### Task 5: The bar, the lists and the reader endpoint

**Files:**
- Modify:
  - `src/Kumbu.Server/Publishing/Publications.cs` (`ListAsync`, `StateAsync`)
  - `src/Kumbu.Server/Publishing/PublicationApi.cs`
  - `src/Kumbu.Server/Publishing/ReaderApi.cs`
  - `src/Kumbu.Server/Publishing/AiLinkApi.cs`
- Test: `ReaderApiTests.cs`, `AiLinkApiTests.cs`, `BundleTests.cs`

**Interfaces:**
- Produces:
  - `PublicationState` gains `int Notes = 1`. `VaultPublicationsView` gains `BundledView[] Bundled`, where `BundledView(string Path, string Start)` holds every member note with its start note. For a member, only the members' paths appear, with `Start`, as today's member rule.
  - `PublicationView` gains `int Notes`.
  - `GET /api/publications/{vault}/{path}` for a member note answers `404`, as today. The web app reads `bundled` instead.
  - `ReaderView` gains `ReaderNoteView[]? Notes`, where `ReaderNoteView(int Id, string Title, string Markdown, int Depth)` is in walk order and includes 0. Edges are never served.
  - The AI link's Markdown for a bundle:
    - `# <start title>`, then the start note;
    - then `---` and `# <title> {#n<id>}` with each note;
    - a link `(#kn-<id>/…)` becomes `(#n<id>)`.
  - The reader page's `copyMarkdown` does the same (Task 7).

- [ ] **Step 1: Write the failing tests**
  - `The_reader_gets_every_note_of_the_bundle_in_walk_order_and_no_edges`: the serialised JSON has no `edges` key.
  - `A_member_sees_bundled_notes_with_their_start`.
  - `The_vault_list_names_bundled_notes`.
  - `The_ai_link_text_carries_the_whole_bundle`.

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit** `Serve a bundle to its readers and name its notes in the vault's list`

### Task 6: `sh publish` with depth

**Files:**
- Modify:
  - `src/Kumbu.Core/Publishing/IPublisher.cs`
  - `src/Kumbu.Core/Sh/Commands/PublishCommand.cs`
  - `src/Kumbu.Server/Publishing/ShellPublisher.cs`
  - `src/Kumbu.Core/Sh/Manual.cs`
- Test: `tests/Kumbu.Server.Tests/Publishing/PublishShellTests.cs`, plus Core tests if the command has a fake publisher there.

**Interfaces:**
- Produces:
  - `IPublisher.AddAsync(VaultPath note, int depth, IReadOnlyList<VaultPath> without, bool dryRun, CancellationToken ct) -> Task<PublishOutcome>`, and `UpdateAsync(VaultPath note, int? depth, IReadOnlyList<VaultPath>? without, CancellationToken ct)`.
  - `PublishOutcome` gains `IReadOnlyList<VaultPath> Notes`, `IReadOnlyList<(VaultPath Path, string Reason)> LeftOut`, `IReadOnlyList<VaultPath> Added`, `Removed` and `bool DryRun`. `PublicationLine` gains `int Notes`.
  - The options are `--depth N` (0–5), `--without <path>` (repeatable) and `--dry-run` (`add` only).
- Output of `publish add /Work/Plan.md --depth 1`:
  ```
  published /Work/Plan.md with 3 notes as a copy of <time>
  link (shown once): <link>
  notes:
    /Work/Plan.md
    /Work/Budget.md
    /Work/People.md
  left out:
    /Work/Salaries.md  marked unpublishable
  ```
  - `--dry-run` prints `would publish …` and no link.
  - `ls` adds `  notes: 3` when there is more than one note.
  - `update` prints `added:` and `removed:` lines.
  - The reasons read as words: `marked unpublishable`, `only reached through a note that stays out`, `taken out by you`, `a PDF, not published`.
- The tool description in `Manual.cs` tells agents to show the list to the person before they publish more than the start note.

- [ ] **Step 1: Write the failing tests**:
  - `publish_add_with_depth_lists_notes_and_left_out`;
  - `publish_add_dry_run_publishes_nothing`;
  - `publish_update_lists_added_and_removed`;
  - `a_depth_of_6_is_invalid`;
  - `publish_ls_names_the_note_count`.

- [ ] **Step 2: Run, expect FAIL**

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run** the `Publishing` and `Mcp` namespaces and the Core `Sh` tests. Expect PASS.

- [ ] **Step 5: Commit** `Let sh publish take a depth, leave notes out and try it dry`

### Task 7: The web app

**Files:**
- Modify:
  - `web/src/api/schema.d.ts` (regenerated: `pnpm -C web gen:api` against a running server, or edited by hand to match, mirroring the generated style)
  - `web/src/api/publications.ts`
  - `web/src/shell/PublishDialog.tsx`
  - `web/src/note/PublicationBar.tsx`
  - `web/src/reader/Reader.tsx`
  - `web/src/reader/readerApi.ts`
- Test: `web/src/shell/PublishDialog.test.tsx` (new or existing), `web/src/note/PublicationBar.test.tsx`, `web/src/reader/Reader.test.tsx`

**Interfaces:**
- Consumes: the Task 3 and Task 5 endpoints.
- Produces:
  - `usePublicationPreview(path: string, depth: number, without: string[])`;
  - `usePublish(path)` with `{ password?, depth, without, expected }`;
  - `useUpdatePublication(path)` with the optional `{ depth, without, expected }`;
  - `useBundledIn(path) -> string | null` (the start note).
- **Dialog:**
  - a number field "How many links deep", 0–5, with the count beside it ("This note only" / "+N notes");
  - the list of notes with checkboxes (the start note not untickable);
  - the left-out list with reasons;
  - from 50 notes on, the count and the line `That's a lot of notes. Check the list before you publish.` in the warning colour (`text-warning`), with `role="status"`;
  - the button `Publish N notes`.
  - On a `409`, the dialog shows the new list and `Something changed in the vault: check the list again.`
  - Depth 0 keeps today's sentence and flow, with no list.
- **Update copy:** first asks for a preview with the entry's depth and `without`. When the set differs from the current one, it shows the dialog's list before updating. Otherwise it updates directly, as today.
- **Bar:** a member note of a bundle shows `Published with <Start title>` with a link to the start note. A note in several bundles lists them. It turns yellow (older) as today, from the bundle's state.
- **Reader:**
  - The start note first, and a `Contents` button that opens a list of titles in walk order.
  - A link `#kn-<id>[/sub]` navigates within the copy (to a heading via the slug, or to a block id). An id that isn't in `notes` is its text.
  - `![x](#kn-<id>)` renders the embedded note's Markdown inline, one level only: an embed inside it is a link.
  - The address becomes `#/<id>` after the code is read, so a reload with the cookie opens that note.
  - `copyMarkdown` carries the whole bundle, as the AI text does (Task 5).

- [ ] **Step 1: Write the failing Vitest tests**:
  - the dialog: depth changes the count, the 50 warning, unticking sends `without`, the 409 shows the new list;
  - the bar: "Published with";
  - the reader: Contents, in-copy navigation, an unknown id as text, the inline embed, `copyMarkdown` of a bundle, reload on `#/2`.

- [ ] **Step 2: Run, expect FAIL**

  Run: `pnpm -C web test --run src/shell src/note src/reader`

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run** `pnpm -C web test --run` and `pnpm -C web build`. Expect PASS.

- [ ] **Step 5: Commit** `Choose a depth and see the list before publishing, and read a bundle as a small site`

### Task 8: Docs and the full gates

**Files:**
- Modify:
  - `README.md` (the publish section)
  - `docs/security-architecture.md` (the publishing guarantees)
  - `docs/superpowers/specs/2026-11-18-publish-bundles-design.md` ("Left to the builder" answered)

- [ ] **Step 1:** Update the docs: depth, the list, the hard blacklist, the limits, `expected`/`409`, and the reader.

- [ ] **Step 2:** Run the full gates:
  - `dotnet test --solution Kumbu.slnx` (by namespace if the shared mongod stalls);
  - `pnpm -C web test --run`;
  - `pnpm -C web build`.

  All must be green.

- [ ] **Step 3: Commit** `Document Publish 2.0`

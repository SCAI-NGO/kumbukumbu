# Overnighter report: publish v1, one note behind a link

Date: 2026-11-05/06. Brief: [2026-11-05-publish-brief.md](2026-11-05-publish-brief.md). Spec: [2026-11-05-publish-design.md](../specs/2026-11-05-publish-design.md). Plan: [2026-11-05-publish.md](../plans/2026-11-05-publish.md).

**Pull request:** https://github.com/SCAI-NGO/kumbukumbu/pull/150 (draft, not merged, no tag, no version change).

**In short.** Every task of the plan, 0 to 17, is done: the fair copy, image metadata, share codes, "May publish" in server, REST and web app, storage, publishing, marks and history, the removal hook, `sh publish`, the owner's REST API, the anonymous reader endpoint and page, the web app's dialog, bar, marks and overview, the end-to-end test, the documents and the security review. Each task went through red, green, commit, its mutations one by one, and a spec-compliance and a code-quality review by fresh subagents; every finding of those reviews was fixed or is named below. The fair copy needed four review rounds before a reviewer found nothing that leaves through the reader's own renderer. Task 16's security review found no Critical or High issue; its two Medium and three Low findings are fixed, its four Info findings are open points. The final verification is in [Final verification](#final-verification).

## The run

### Environment

- Cloud container, 4 cores, 15 GB RAM, Linux.
- .NET SDK 10.0.403 (`global.json`: 10.0.100, roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT=1` was already set.
- **Installed by hand:** Node v26.13.0 from nodejs.org's tarball into `/opt` (the container had 22.23.4); pnpm 10.32.1 with `npm install -g pnpm@10.32.1` (it had 10.28.0). No dependency was added to the project.
- Docker 29.9.2. A restart of the session's worker process killed `dockerd` once; I started it again by hand (`nohup dockerd &`).
- **MongoDB:** a container of my own, `kumbu-test-mongo` (`mongo:7.0`, `--ulimit nofile=20000:20000`, replica set `rs0`, the plan's `file_manager` setting), with `KUMBU_TEST_MONGO=mongodb://127.0.0.1:27017/?directConnection=true` and `TESTCONTAINERS_RYUK_DISABLED=true`. Phase gates used a second, fresh container on port 27018 (`kumbu-gate-mongo`, mongod itself on 27018, so the replica set's member name matches), so they didn't disturb workers running tests.
- **Branches:** the session's harness gave `claude/overnighter-publish-brief-qb0piu` as the branch to develop on; the brief names `feature/publish`. Both stood at `da884bc`. I worked on the harness's branch and pushed every commit to both, so both hold the same history; the pull request is from `feature/publish`, as the brief says.
- **How the work ran:** each task by a fresh subagent in a git worktree of its own, then two fresh reviewers; the controller cherry-picked the finished commits onto the branch, resolved the few conflicts (noted per task), built, ran the affected classes and pushed. Independent tasks ran in parallel.

### Task 0: planned against measured

| | Planned | Measured |
|---|---|---|
| Setup and first build and tests | 15 min | about 20 min: 5 for Node, pnpm and the Mongo container, 1 for restore and the Release build, 11 for `dotnet test`, the rest web and sidecar |
| `dotnet test` | total 3727, 3723 succeeded, 4 skipped, 0 failed | total 3727, **3721** succeeded, **6** skipped, 0 failed: the 4 expected plus the 2 that skip as root (`SeedScannerTests.Skips_unreadable_files_with_a_warning`, `ModelFilesTests.AReadOnlyModelFolderTurnsSearchByMeaningOffInsteadOfStoppingTheServer`) |
| Web | 88 files, 1585 tests | 88 files, 1585 tests |
| Sidecar | 8 files, 146 tests | 8 files, 146 tests |

A first `dotnet test` attempt was cut off by a time limit I had set too short while three workers ran in parallel; it had shown the known flaky `AccountApiTests.Recovery_revokes_every_api_key_and_replaces_the_used_recovery_key` (Conflict for OK), which passes alone. The numbers above are from the rerun on a fresh container.

**Learnt on the way:** some server tests bind `127.0.0.1:5000` (`DotSegmentTests`), so two full `dotnet test` runs in two worktrees at once collide and fail by the hundred. From then on only one full run ran at a time; workers ran filtered classes.

## Per task

Commits are the branch's (`git log e8be7ad..HEAD`). "Red" and "green" are the task command's summary lines; mutations are listed one by one with the test that caught them.

### Task 1: the fair copy (spec §3.3)

- **Commits:** `76d8636` "Make a fair copy of a note for publishing"; after reviews `5012145`, `550289d`, `c0af4be`, `78dd2e6`, `8ebb82d`, `b7867ca`.
- **Red/green:** red was a compile error for `FairCopies`, then 27 of 27 failing against a throwing stub; green 27/27; Core 1547 total, 1545 succeeded, 2 skipped. After the four review rounds `FairCopyTests` has 64 tests, and the Core suite is 1602 total, 1600 succeeded, 2 skipped.
- **Mutations (a)–(i)**, all caught: (a) frontmatter kept → `Frontmatter_goes`, `The_example_of_spec_6`; (b) inline code as text → `Code_stays_as_written`; (c) a tag's `#` kept → `A_tag_in_running_text_keeps_its_word`; (d) the whole target of `[[Projects/Routing master]]` → `Every_link_form_becomes_its_text`, `No_path_leaves`; (e) reference definitions left → `No_path_leaves`; (f) Files not de-duplicated → `An_image_it_may_copy_is_embedded_by_number`; (g) raw HTML kept → `Raw_html_goes_its_text_stays`, `No_path_leaves`; (h) an `obsidian://` autolink kept → `No_path_leaves`; (i) `ownOrigin` by string prefix → `Links_to_the_server_itself_are_vault_links`.
- **Review rounds.** Each round's reviewer found more ways out, the later ones by running the result through the reader's own renderer (react-markdown with remark-gfm and `skipHtml`, a small harness in the scratchpad):
  1. **Round 1** (spec and quality, High): links and HTML tags over two lines, quoted `>` in attributes, definitions inside quotes and lists, bare URLs into the server, a stack overflow from deeply nested links. The scanner became linear and one level deep, inline content is read per paragraph, with an HTML-tag scanner of its own and a safety net. Mutation 7a (spaces in another scheme's autolink) survived because the safety net gave the same output; strengthened in `550289d`.
  2. **Round 2** (High): a fence on a list or quote line made the rest of the note "code", which left verbatim; odd wikilink shapes left `[[` and the path; `x<y` ate the rest of a line; 1 MB of `[a](` took 16 s. Fixed in `c0af4be`; the timing test now compares against plain text of the same length (`78dd2e6`), because an absolute limit failed under load at the first phase gate.
  3. **Round 3** (High): a fence in a container outlived the container in the renderer's reading; a definition label over two lines; a lone `\r`; full-width, soft-hyphen and zero-width spellings of the server's host; a percent-encoded own URL in another site's query; comments protected by code spans read per line; an `[file:](x)` input that took 357 s at 1 MiB. Fixed in `8ebb82d`: a fence inside a container is filtered as text, fence-shaped text lines are escaped, raw HTML blocks hold no code, hosts compare after IDNA and NFKC folding, queries are decoded, GFM's table pipes are read first. `b7867ca` strengthens a test after the `IdnHost` mutation survived.
  4. Against the renderer, none of 153 cases (the reviewers' and the implementer's own) renders an `href` to a vault path or to the server; the security review's 47 further cases agree.
- **Changes outside the new files:** `Outline.CodeLines` no longer takes a backtick fence whose info string holds a backtick as a fence (CommonMark §4.5); every caller agrees now, the server's tag, link and section tests pass, and `OutlineTests` has a case for it. `MarkdownLink.Decode` and `TagReader.IndentedCode` became internal.
- **Choices:** a backslash escapes only `#` and backticks for the filter; a leftover `<` is written `\<`; reference definitions stay only for clean web links; the server's own host is matched on the host alone, any scheme or port.

### Task 2: image metadata (spec §3.4)

- **Commits:** `10c3f78` "Strip images' metadata, keeping their orientation"; after reviews `ee4e527`.
- **Red/green:** compile error, then 8/8 failing against a stub; green 8/8 (with `ImageHeaderTests` 27/27). After the review fixes 16 tests, with `ImageHeaderTests` 35/35.
- **Mutations (a)–(f)**, all caught: (a) APP13 kept and (e) bytes after EOI kept → `Jpeg_loses_exif_xmp_iptc_and_comments`; (b) only Exif APP1 dropped → the three `Jpeg_*` tests; (c) `VP8X` flags left → `Webp_loses_exif_and_xmp_and_their_flags`; (d) orientation dropped → `Jpeg_keeps_its_orientation`, `Png_loses_text_and_exif_chunks`, the WebP test; (f) a wrong CRC on `eXIf` → `Png_loses_text_and_exif_chunks`.
- **Review fixes** (spec review: six Medium; quality review: one Medium), 18 more mutations, all caught: only listed JPEG markers are kept; JFIF cut to its fixed part without thumbnail, JFXX dropped, Adobe APP14 cut to 12 bytes; a JPEG needs a frame and a scan; ICC profiles keep their colours but lose date, maker, model, creator and profile id from the header, in all three formats (a PNG `iCCP` is inflated with the BCL's `ZLibStream`, capped at 16 MiB, its name set to `ICC profile`); animated WebP frames keep only image chunks, `VP8X` must be exact, first, with reserved bits zeroed; GIF keeps only graphic control and a bare loop extension; PNG CRCs are checked, a second `IHDR` refused. A multi-scan JPEG test pins that nothing rides between scans.
- **Choices:** the orientation comes from the last Exif before the first scan, as `ImageHeader` reads it; only orientations 2 to 8 are kept; WebP keeps one only with a `VP8X`; ICC `desc` and `cprt` tags are kept (open point).

### Task 3: the share code and its keys (spec §4)

- **Commits:** `8f60601` "Add share codes and their derivations"; `a34b03f` after review.
- **Red/green:** compile error, then 6/6 failing; green 6/6; `PairingTests` 54/54 after `Alphabet` became internal.
- **Mutations:** (a) 19 bytes → five tests; (b) one label → `Lookup_and_reader_key_differ_and_are_stable`; (c) no length check → `Parse_reads_as_pairing_codes_do`; (d) `/s/` → `Link_puts_the_code_in_the_fragment`.
- **Review fix:** `Parse` refuses more than 64 characters before normalising (anyone may send one to the reader endpoint; mutation caught), the code's characters are cleared after use, a spacing slip.
- **Phase gate 1:** see [Phase gates](#phase-gates).

### Task 4a: "May publish" sealed into keys and connections (spec §3.8)

- **Commits:** `46763db` "Seal a May publish right into keys and connections"; `19450b6` after review.
- **Red/green:** compile errors on the new members only; green 222/222 (`ApiKeyTests`, `ConnectionTests`, `UserKeyRotationTests`, `PairingTests`, `TokenTests`, `RefreshTests`); neighbouring classes 477/477, the protected `MembershipCheckTests` and `BoundMembershipWriteTests` among them. After review 224/224.
- **Mutations (a)–(f)**, all caught: (a) every owner credential may publish → six tests, `A_slot_sealed_before_publishing_may_not_publish` among them; (b) without `membership is null &&` → `A_member_derived_slot_with_a_planted_publish_flag_may_not_publish`; (c) rotation loses `publish` → `A_rotation_keeps_may_publish` (key and connection, each run); (d) `WrapConnectionSlots` and (e) `ConnectionSlotsFromUserAsync` drop it → the first-token and refresh tests; (f) a vault outside `vaults` accepted → the refusal tests.
- **Deviation:** the implementer wrote code before tests and then reverted `src` to show an honest red; the plan's line anchors for `:238` and the others are in `VaultCatalog.cs`, not `OAuthTokens.cs`.
- **Beyond the plan:** a device key refuses "May publish"; a member-derived connection slot is never sealed with it; `OAuthTokens.SameRights` compares it, so a change during consent is refused.
- **Review fix:** `Publish` is built from the parsed rights; a shared vault is refused for connections too (test and mutation); a legacy connection slot opens as "may not".

### Task 4b: "May publish" in the REST API

- **Commits:** `f14edf9` "Show and take May publish in the REST API"; `7f705aa` after review (mine).
- **Red/green:** 7 of 83 failing; green 83/83; related 409/409; `pnpm -C web build` passes.
- **Mutations:** (a) the views without `publish` (key and connection view, each) → `The_key_view_lists_may_publish`, `The_connection_view_lists_may_publish`; (b) a foreign name as an empty set → the 400 tests; (c) the consent view without it → `The_consent_view_shows_may_publish`; extra, the read-only cap removed → the same.
- **Review fix:** `"publish": [null]` answered 400, not 500 (red without the fix, green with).

### Task 5: where publications are stored (spec §4)

- **Commits:** `6fa99a8` "Store publications as mini vaults beside their vault's sealed record"; `dfeb5f3` after review.
- **Red/green:** 23 of 43 failing (9 store tests on a stub, 14 `DeletionTests` on the longer list); green 43/43; related 186/186; after review 46/46.
- **Mutations (a)–(e)**, all caught: (a) the record's context without the revision → `A_record_opens_only_where_it_was_sealed`; (b) `publishing` out of `RekeyOrder` → `A_rekey_keeps_the_record_and_the_copy`; (c) `ContentCollections` without the copies → the deletion tests; (d) the file context without the index → `Files_open_only_at_their_index`; (e) the copy context without the lookup → `A_copy_opens_only_under_its_own_lookup`, a test added because the plan's couldn't catch it (two publications never share a key).
- **Deviations:** the test class uses `[Collection("mongo")]` with a catalog built by hand, as `RekeyTests` does; `DeletionTests`' filler documents gained `lookup`, `publication` and `n`, as the new unique indexes require.
- **Review fix:** `ReplaceCopyAsync` checks that the key is the reader slot's; tests for the blob condition and for a record moved to another document of its vault (three mutations, caught).

### Task 6a: publishing, updating and withdrawing one note

- **Commit:** `5328f2b` "Let the owner publish, update and withdraw one note"; review fixes in `15f0674` and `25d0601` (written by Task 6b's worker, as both edit `Publications.cs`).
- **Red/green:** 15 of 15 failing on a stub; green 15/15; related 485/485.
- **Mutations (a)–(f)**, all caught: (a) owner check skipped → `A_member_never_publishes`; (b) `MayPublish` skipped → the key, other-vault, device and dev-token tests; (c) `Covered` without the `/` → `A_mark_covers_only_its_own_folder`; (d) Older ignores attachments → `Older_follows_the_note_and_its_attachments`; (e) a member's state with `SnapshotAt` → `A_member_sees_the_path_alone`; (f) withdrawing refused while off → `Switched_off_refuses_publishing_and_allows_withdrawing`. Extra: the device mask removed → `A_device_key_is_refused`.
- **Deviations:** `Publications` is internal (its constructor takes the internal store) and has no `IMongoDatabase` or logger (warnings are errors, unused parameters fail the build); Update records the updater as "by"; a device's vaults never may publish, also with a planted flag.
- **Review fixes** (one Medium): `ToString` of every record holding a code, link or key leaves them out; listing at the vault root lists everything; withdrawing what is neither published nor a note is "no such note"; the switch is checked before the owner; a copy takes at most 64 MiB of images; a parallel-publish test (its mutation survived first and was strengthened in `25d0601`); a duplicate lookup is a conflict without the driver's message, which quoted the lookup id.
- **Bisect:** `15f0674` and `25d0601` don't compile on their own (the shell's call of the changed `WithdrawAsync`); `639b301` fixes it. The squash merge hides it; I didn't rewrite the history, as the brief asks.

### Task 6b: unpublishable marks and the owner's history

- **Commits:** `ce958d7` "Mark notes and folders unpublishable, and record publications in the owner's history"; `639b301` (mine, the shell's call); `1b72edd` after review.
- **Red/green:** 10 of 39 failing; green 39/39; related 287/287; after review 180/180.
- **Mutations:** (a) a mark doesn't withdraw → five tests; (b) a marked attachment leaves the publication → `Marking_an_attachment_a_copy_holds_withdraws_the_whole_publication`; (c) lifting removes the folder's mark → `Lifting_a_mark_allows_publishing_again`; (d) the kinds in `VaultWide` → `Members_see_no_publication_event`; (e) the event outside the transaction → `An_aborted_publish_leaves_no_event`, added for it ("after the commit" can't be told apart once the transaction aborted).
- **Deviations:** `SetUnpublishableAsync` and `WithdrawAsync` take the note store (for the existence check); a mark on nothing is NotFound "`{path}: no such note, file or folder`"; setting and lifting are idempotent.
- **Review fixes:** "You" only for the owner's own session; events of one instant keep their order; a test for every sentence; the owner's key without the right is refused on marks.

### Task 7: removing and moving published notes

- **Commits:** `701d1c5` "Withdraw a publication with its note, and refuse whoever may not" (cherry-picked with one conflict in `ApiErrors.cs`, both catches kept); `7ae9701` after review.
- **Red/green:** 14 of 16 failing; green 51/51 (`PublishedNoteRemovalTests`, `DeletionTests`); after the cherry-pick 80/80 with the shell, REST and marks classes; after review 83/83, related store, trash, move, checkpoint and sync classes 605 (604 succeeded, 1 skipped).
- **Mutations (a)–(g)**, all caught: (a) device not allowed → `The_owners_device_removes_it`; (b) member allowed → `A_member_is_refused`, `A_members_device_gets_a_final_refusal`; (c) folder mark doesn't follow → `A_folder_move_keeps_the_link_and_the_mark`; (d) no right check on moves into a mark → the same and the attachment test; (e) and (g) `CloseInAsync` keeps copies or record → `Deleting_the_account_withdraws_its_publications_at_once`; (f) `TryTrashAsync` skips the hook → `A_single_trash_meets_the_rule_too`, added because nothing in the server calls `TryTrashAsync`.
- **Deviations:** `Publications` is internal, so the store has an internal constructor and DI a factory; `Deletion` makes its own `PublicationStore`; `FollowAsync` returns `Task`; the account-deletion test lives in `PublishedNoteRemovalTests`; a change of the record during a batch is a write conflict, retried.
- **Review fixes** (both reviewers: a folder mark followed onto a folder that already existed; and a Medium leak: a trashed note restored after its folder moved came back unmarked): a folder mark follows only a real rename; the trash counts as staying, and then both the old and the new folder stay marked; a note's own mark follows it (test); a store built without `Publications` refuses to trash or move in a vault that has a publishing record (refusing everywhere would break the hand-built stores of the protected `NoNamesTests`); a checkpoint plan records its failure.
- **Phase gate 2:** see [Phase gates](#phase-gates) (run after Tasks 8–13b had landed too).

### Task 8: `sh publish`

- **Commits:** `274565d` "Add sh publish"; after review `cb09cad`, `0a2947d`.
- **Red/green:** Core 20 of 62 failing, server 13 of 82, web 2 of 156; green 62/62, 82/82, 156/156; Core suite 1593 (1591, 2 skipped); web 90 files, 1595 tests.
- **Mutations:** (a) the console lets `publish add` through → every row of `The_console_refuses_publishing`; (b) `ls` prints a link → `Ls_never_prints_a_link`; (b′) extra, the shell publisher puts the real link into `ls` → four `PublishShellTests` and `The_console_lists_publications`; (c) the refusal loses its code → `A_refusal_keeps_its_code`; (d) `KnownCommands` without `publish` → `Known_commands_are_the_shells`, `Every_shell_command_is_classified`.
- **Choices:** `publish update` also prints the "not copied" line; the console refuses `publish --help` (`help publish` works); `ConsolePage.test.tsx` follows the new help sentence.
- **Review fixes** (two Medium): the MCP library logs whole messages at Trace, which would carry the link of `publish add`: it is filtered to Information (test with Trace on); vaults being re-keyed are skipped at the root and busy when named; a member is recorded with no unscoped reference; one constant for the PDF sentence; the refusal codes are tested through the scoped shell; `PublishOutcome.ToString` leaves the link out. One mutation, a member's writer reference, can't be observed (a member is refused before anything is written) and the code fails closed.

### Task 9: the owner's REST API

- **Commits:** `4636ce2` "Give the owner publications in the REST API", `506339f`; `2846a0d` after review.
- **Red/green:** 12 of 12 failing; green with `PasswordWindowTests` 82/82; related 222/222; web build passes. After review 83/83.
- **Mutations (a)–(g)**, all caught: (a) and (b) a member gets `SnapshotAt` or `Link` → `The_owners_note_view_holds_the_link_and_a_members_doesnt` (its failure message printed the link; changed in `506339f`); (c) publish without window or password → `Publishing_without_a_password_needs_the_open_window`; (d) update without the window → `Update_needs_the_window_and_withdrawal_doesnt`; (e) the overview with a shared vault → `The_overview_lists_her_vaults_only`; (f) DELETE refused while off → `Switched_off_withdrawing_and_marks_still_work`; (g) no `PasswordGivenAsync` → `A_password_given_opens_the_window`.
- **Review fixes:** the switch and a non-owner are refused before any password is asked for or checked; no assertion prints a code or link on failure; REST's 409 names "Update copy", not the shell's command.

### Task 10: the anonymous reader endpoint

- **Commits:** `ef4347b` "Let anyone with the link read the copy, and nothing else" (cherry-picked with conflicts: the routes beside `MapPublicationApi`, `schema.d.ts` merged from both tasks, one test call); after review `6778fea`, `6d7d770`; the security fixes below.
- **Red/green:** 16 of 37 failing on stubs; green 37/37 (`ReaderApiTests`, `StaticFilesTests`, `HttpMetricsTests`); related 302/302 and 98/98; after review 65/65.
- **Mutations (a)–(h)**, all caught: (a) cookie path `/` → three tests; (b) 410 for a withdrawn code → `A_wrong_and_a_withdrawn_code_answer_alike` and two more; (c) no `Active` check → `Deactivation_hides_and_reactivation_shows`; (d) no `ReaderFailed` → `Wrong_codes_are_limited_per_network_and_server`, `The_code_reaches_no_log`; (e) no `X-Robots-Tag` on a 404 and (f) `Referrer-Policy` left to `TryAdd` → `Every_reader_answer_carries_its_headers` (and `StaticFilesTests`); (g) a `Max-Age` → `A_right_code_reads_the_copy_and_sets_the_reader_cookie`; (h) reader routes in the metrics → `Readers_are_not_counted`.
- **Choices:** a bad file index with a live cookie is the same 404, uncounted, cookie kept; the off answer keeps the cookie; `/api/s`'s JSON also carries `ReaderPagePolicy`.
- **Review fixes** (two Medium): file reads have their own allowance (600 a minute per address; a note with many images loaded never fully before); a reader's view carries an opaque copy id (a third HKDF label, `kumbu/v1/publication-copy`) and a file read needs it in `X-Kumbu-Copy`, so a second tab's cookie can't put another note's images into this one; copy reads are also limited per network and server-wide; `POST /api/s` takes at most 1 KiB; a file read opens the copy once; `HEAD /s` answers as `GET`; stronger tests (session touch with the clock set back, `/`'s own headers, `snapshotAt` in UTC, the lookup's Base64 in the log check). 16 mutations, all caught.
- **Accepted limit:** a browser holds one reader cookie. A reload of tab A, after tab B opened another link, shows B's note.

### Task 11: the reader page

- **Commits:** `109c4de` "Add the reader page, an entry of its own", `545c7c1`; `8625dd1` after review.
- **Red/green:** 9 of 9 failing on stubs; green 9/9; web 90 files, 1594 tests; build passes. After review 14/14, web 1599.
- **Mutations:** (a) no `replaceState` → "posts the code from the fragment and removes it"; (b) `skipHtml` off **survived** (react-markdown then shows raw HTML as text, so no element appears); strengthened in `545c7c1` (no `onerror`, no `<a ` in the text), then caught; (c) `img` for any `src` → two tests; (d) `../api/client` imported → "imports nothing of the app".
- **The build's output** (Task 11's check):
  - `rg -o '<script[^>]+src="[^"]+"' web/dist/index.html`:
    ```
    <script type="module" crossorigin src="/assets/main-CMAxVYrL.js"
    ```
  - `rg -o '(src|href)="[^"]+"' web/dist/s.html`:
    ```
    href="/favicon.svg"
    src="/assets/s-Dr3byTMf.js"
    href="/assets/index-Cs_9rygP.js"
    href="/assets/vendor-CDQSN613.js"
    href="/assets/markdown-CNRNHItN.js"
    href="/assets/index-DzsbGqQG.css"
    ```
  - Nothing in `s.html` is the app's entry. `index-Cs_9rygP.js` is Vite's modulepreload polyfill (711 bytes, no imports), the CSS is the shared stylesheet, and `vendor` and `markdown` are the accepted library chunks.
- **Review fixes** (three Medium): only the latest answer is applied, "Read" waits while one runs; the Markdown components keep their identity (images no longer reload on a render); the wrong-code line is an alert tied to the field; headings h4–h6; an image that fails shows its alt; a StrictMode test; the isolation test reads the folder recursively. Ten mutations, all caught.
- **Beyond the plan's copy:** "Loading …" and "The note couldn't be loaded; try again later." for other answers.

### Task 12: "May publish" in the web app

- **Commits:** `5247403` "Offer May publish beside a key's or connection's right"; `7b3569e` (part) and `ea4bcee` (mine) after review.
- **Red/green:** 7 of 73 failing; green 73/73; build passes.
- **Mutations:** (a) the box for shared vaults → "offers May publish only for owned vaults"; (b) not sent → three tests; (c) consent leaves it out → "shows May publish on the consent page when set"; extra: the change-rights dialog starting empty → "edits May publish of a connection"; `publishEnabled` ignored → "hides May publish when publishing is off".
- **Review fixes:** the hint is the checkbox's description for screen readers; editing a connection while publishing is off sends its right back (test; mutation caught).

### Task 13a: the Publish dialog and the bar above a note

- **Commits:** `441b7d2` "Let the owner publish from the note, and show its state above it"; `7b3569e` after review.
- **Red/green:** 2 of 51 failing plus the two missing modules; green 64/64; web 92 files, 1616 tests; build passes.
- **Mutations (a)–(e)**, all caught; (a) and (b) needed tests beyond the plan's ("shows a member no times even if the server sent them", three `MobileShell` tests), because the plan's member fixture had no times and the dialog tests can't see the menu.
- **Review fixes** (one Medium): the owner's publication query (which holds the link) is dropped from the cache when nothing shows it; the password and the link don't stay in the mutation cache; the green dot has a `--success` token in every colour scheme, held to 3:1 contrast; the yellow state never reads "up to date"; the bar's error lines are announced.

### Task 13b: marks in the menus and the tree, and the overview

- **Commits:** `c2dd2fe` "Mark notes and folders unpublishable in the web app, and list the published notes", `7798f35`; `9d6fcbe` after review.
- **Red/green:** 8 of 41 failing; green 41/41, with `Settings` and `MobileShell` 125/125; after review web 94 files, 1691 tests.
- **Mutations:** (a) members get the entries → "offers no publishing and no marks to members"; (b) "Publish…" not disabled under a mark → "offers Publish only where no mark covers the note"; (c) marks hidden while off **survived** (the open note's menu filled the same cache), strengthened in `7798f35`, then caught; (d) no mark in the tree → four tests.
- **Review fixes** (two Medium): moves, deletions and restores refresh the marks and the published state; marking something that covers a live publication asks first ("Marking it unpublishable withdraws its published link: whoever has it can't read the note any more."); the delete dialog learns "published" from the list that carries no link; "Publish…" waits for the marks.

### Task 14: end to end, spec §6

- **Commit:** `6562f57` "Test spec §6 end to end".
- **Green at once:** 1/1, one long fact over steps 1–7. No earlier task needed a fix.
- **Mutation:** the fair copy keeps `[[…]]` → `EndToEndTests.Spec_section_6` and 9 of 64 `FairCopyTests` fail.

### Task 15: the documents

- **Commits:** `2892811` "Describe publishing in the security architecture, the concept and the README"; `a2f0e7a` "Bring the publishing documents in line with what was built" (after everything was built); `28a387f` "Describe the security review's fixes in the documents".
- `docs/security-architecture.md`: §3 (key hierarchy, the publication key's two slots, the re-key), §2's threat table (someone holding a link), prompt injection, §4 (the reader, the owner's prerogative, "May publish", who may remove a published note), §5.6 (the three collections), §6 (deleting), §7 (the reader endpoint, its limits, CSP, cookie, headers, no reading statistics). Line references into every file this branch changed were re-checked (about 280; about 130 set by hand, many of which were already off at `e8be7ad`). References into files the branch didn't touch were left as they were.
- `docs/concept.md`, `README.md` ("Publishing a note", `sh publish`, `KUMBU_PUBLISH_ENABLED`), `.env.example`, `compose.yaml` (`Kumbu__Publish__Enabled`).
- **Found while writing:** a re-key changes no publication's code or key (see open points).

### Task 16: the security review

A fresh subagent reviewed the whole diff against spec §2's anti-goals and §5, `docs/security-architecture.md` and the hard rules, without my conclusions. See [The security review](#the-security-review).

**Hard-rule tests:** `MembershipCheckTests`, `BoundMembershipWriteTests`, `PinTests`, `NoNamesTests`, `MarkerTests`: total 106, 106 succeeded, 0 failed, 0 skipped. `git diff e8be7ad...HEAD --stat` on their five files shows nothing.

## Phase gates

| Gate | At | Restore, build | `dotnet test` | Web | Sidecar |
|---|---|---|---|---|---|
| After Task 3 (with 4a) | `550289d` | 0, 0 (0 warnings) | 3798 total, 3790 succeeded, 6 skipped, **2 failed**: `Deep_nesting_finishes_quickly(20000)` (4.9 s against a 3 s limit under load; made robust in Task 1's third round) and the known flaky `AccountApiTests.Recovery_revokes_…` (passes alone, 1/1) | 88 files, 1585; build 0 | 8 files, 146; build 0 |
| After Tasks 7, 10 and 13b | `701d1c5` (.NET) | 0, 0 (0 warnings) | 3968 total, 3962 succeeded, 6 skipped, 0 failed | 3 failed in `TreeDrawerPublishing.test.tsx`, because I cherry-picked Task 13b's review fix into the tree while the run read it; rerun at `9d6fcbe`: 94 files, 1691 passed; build 0 | 8 files, 146; build 0 |

A first attempt at the first gate failed before any test ran: the gate's own MongoDB mapped port 27018 to 27017 inside, so the replica set's member name didn't match the node. Fixed by running mongod on 27018 inside too.

## Final verification

In a clean `git worktree` at `28a387f`, on a fresh MongoDB, as CI does:

| Step | Exit | Result |
|---|---|---|
| `dotnet restore --locked-mode` | 0 | |
| `dotnet build --configuration Release --no-restore` | 0 | 0 warnings, 0 errors |
| `dotnet test --configuration Release --no-build` | 0 | total **3989**, succeeded 3983, failed **0**, skipped 6 (the 4 expected and the 2 root skips) |
| `pnpm -C web install --frozen-lockfile && pnpm -C web test --run` | 0 | **94** test files, **1691** tests passed |
| `pnpm -C web build` | 0 | |
| `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test` | 0 | 8 test files, **146** tests passed |
| `pnpm -C pdf build` | 0 | |

Against the baseline: .NET 3727 → 3989 (+262), web 1585 → 1691 (+106) in 88 → 94 files, sidecar unchanged at 146.

## Departures and calls

Beyond the per-task deviations above:

- **The branch:** see [Environment](#environment).
- **Stricter than the plan where the reviews showed a way out**, each by *forum internum*: the fair copy (four rounds), image metadata (thumbnails, unknown segments, ICC headers, WebP frames, GIF extensions), file names out of the reader's JSON, marks carried along when anyone but the owner's session moves something out from under one, a question before a mark withdraws a link.
- **Additions the plan doesn't name:** a copy id and `X-Kumbu-Copy` for file reads; reader limits per network and server-wide for copy and file reads; a 1 KiB body limit; a 64 MiB limit on a copy's images; the publishing log capped at its newest 500 entries (the full history is in the vault's events); the MCP library's log filter; `HEAD /s`.
- **Surprises:**
  - `ToString` of C# records prints every field, codes included; overridden on every type that holds a code, link or key.
  - The MongoDB driver's duplicate-key message quotes the key, here the lookup id; the store rethrows without it.
  - Some server tests bind a real port; parallel full runs fail by the hundred.
  - Several mutations survived first and led to stronger tests (Task 1 7a and `IdnHost`, Task 6a's parallel publish, Task 8's re-key check, Task 11's `skipHtml`, Task 13b's switched-off marks).
- **Web tests under load:** in full parallel runs on 4 cores a few tests outside this work timed out once (`GalleryPage` "orders by date newest first grouped by day", `ShareDialog` "keeps the secret in sessionStorage and never in the URL after sign-in", which also failed on the base, `NoteView` "renders the note…", `MobileShell` "closes the drawer when Settings is followed…"); each passes alone.

## The security review

No Critical or High finding. Each finding, and what became of it:

| # | Severity | Finding | What became of it |
|---|---|---|---|
| 1 | Medium (growth confirmed) | The vault's publishing record grew with every update (about 206 bytes each); after about 81,000 updates its 16 MiB document would be full, and then withdrawing, marks and every trash or move in the vault would fail | **Fixed** (`fd73f8e`): the log keeps its newest 500 entries; the full history is in the vault's events. Mutations A–C caught |
| 2 | Medium (reasoned) | Image reads on the anonymous route had only a per-address limit | **Fixed** (`7d4550d`): 6,000 a minute per network, 100,000 server-wide. Mutations D–F caught |
| 3 | Low (confirmed) | A key with write and "May publish" could move a note out of a marked folder and then publish it | **Fixed** (`3f32a2e`): when anyone but the owner's session moves a note or file out from under a mark, it keeps a mark of its own. Mutations K–M caught |
| 4 | Low (reasoned) | A request without a cookie used up the server-wide read allowance, so a few networks could shut all readers out | **Fixed** (`cfcc7b1`): it gets the identical 404 at once and takes no allowance. Mutations G, H caught |
| 5 | Low (reasoned, renderer) | The reader's JSON carried each image's vault file name, which the page never shows | **Fixed** (`24c05f3`): no name stored or sent; an older copy's stored name is ignored. Mutations I, J caught |
| 6 | Info (confirmed) | A key with `read` and "May publish" can publish and withdraw, while an OAuth read-only grant drops the right | **Kept** as the plan says ("May publish goes with any right, read included"); open point |
| 7 | Info | Links to the app under another host name than `Kumbu:PublicUrl` (an old domain, an internal name) stay web links, with the vault and path in them | Open point |
| 8 | Info (confirmed) | Code blocks keep `[[…]]` and paths as written (the spec's "code stays as written"); 47 other forms were stripped as the renderer reads them | Open point (a sentence in the dialog?) |
| 9 | Info | ICC `desc` and `cprt` tags are kept; a copy is bound to its lookup, not to a revision, and the clear `vault` field is unbound (someone with database write access could restore an older copy) | Open point; the second half is the rollback the security architecture already accepts |

**The review's answers to its eight questions, in one line each:** (1) nothing beyond findings 5, 7 and 8 leaves; (2) no member, device, dev token or key without the right can publish, update or withdraw, and none learns a code or who published; (3) no half-finished edit reaches a reader; (4) the page and endpoint can't be turned against the web app (own entry, strict CSP, no raw HTML, cookie on `/api/s` only, Origin check, `kumbu_session` ignored); (5) a preview without the fragment gets a generic page; (6) wrong, withdrawn and deactivated answers are identical but for a small time difference only a link holder can see, which doesn't matter; the limits held per network for copies, not for files (finding 2) and could shut readers out (finding 4); no code, lookup id or cookie reaches a log; (7) labels distinct, contexts bound, randomness from `RandomNumberGenerator`, the record bound to its revision, the re-key reseals the record, every deletion path removes copy and files; (8) "May publish" appears nowhere it wasn't granted; with the switch off nothing publishes, updates or reads, withdrawing and marks work; reader routes stay out of the metrics.

## The hand test

For `/SWE/Kumbu/Tests/Test list 0.3.2-pre.1.md` under "Added later" (the run doesn't write to Kumbu). A pre-release image of this branch, or a local `docker compose up --build`, with `Kumbu:PublicUrl` set. Alice is you, with a vault `Work`; Bob a second account.

1. **Publish on a desktop.** Make `Work/Routing/Questions.md` with spec §6's note: frontmatter `status: draft`, a heading `# Questions on routing #routing`, a line with `[[Routing master]]`, a line with `[Router docs](https://example.org/router)`, a `%% … %%` comment and `![[flow.jpg]]`. Upload `flow.jpg`, a phone photo **with GPS** (check with an EXIF viewer that it has it). Open the note's ⋯ menu → **Publish…**. **Passes:** the dialog says what happens, asks for your password unless you gave it within the last minutes, then shows a link `https://<host>/s#XXXX-XXXX-…` (8 groups of 4) with **Copy link**; the clipboard holds exactly the link.
2. **Publish on a phone.** The same with another note on the phone's browser. **Passes:** the dialog fits the screen, Copy link works, the bar above the note turns green.
3. **Read in a private window.** Paste the link. **Passes:** the address bar shows `/s` without the code at once; the page shows "Questions on routing", "Snapshot of <date>, <time>" in your time zone, "See Routing master." as plain text, "Router docs" as a working link (opens in a new tab), the image, and nothing of the frontmatter, `#routing`, the comment, your name or `Work`. Reload: the same copy, without the code. Close the window, open it again without the code (`/s`): it asks for the code; a wrong code says "This code doesn't open a published note."
4. **The image's metadata.** On the reader page, save the image (right-click → Save image). Open it in an EXIF viewer. **Passes:** no GPS, no camera make or model, no date; the image stands the right way up.
5. **A link preview.** Paste the link into a chat service that shows previews (for example a message to yourself). **Passes:** the preview shows at most "Kumbu² · a published note", nothing of the note.
6. **The yellow bar.** Add a line to the note and save. **Passes:** the bar turns yellow, "Published copy is older than this note. Published <date, time> · changed <time>"; the private window, reloaded, still shows the old text and the old snapshot time, with no hint of a change. Click **Update copy** (password only if the window closed): the same link now shows the new line and a later time.
7. **Unpublish.** In the bar, **Unpublish** → it asks "Withdraw the link? …" → **Unpublish**. **Passes:** the bar goes; the private window, reloaded, asks for the code; the old link opened anew says the code doesn't open a note. Your vault's activity (the history view of `Work`, `GET /api/vaults/Work/activity`) shows the publication, the updates and the withdrawal, each once.
8. **An agent through claude.ai, without "May publish".** Settings → Connections (or API keys): connect claude.ai with `write` on `Work` and **without** "May publish". Ask it to run `sh publish add /Work/Routing/Questions.md`. **Passes:** refused, naming the missing right ("this key may not publish in Work; its owner can allow it …"). Ask it to `rm` a published note: refused, naming the publication. `sh publish ls /Work` lists the note with its snapshot time and "older: no/yes", **without** any link.
9. **The same agent with "May publish".** Change the connection's rights: tick **May publish** for `Work` (password asked if the window is closed). Ask it to `publish add` another note. **Passes:** it prints the link once (it passes through the conversation, as accepted); `publish ls` never shows it again; `publish update` and `publish rm` work; your history names the agent ("“Claude” published …"). Tick "May publish" off again afterwards.
10. **Obsidian deleting a published note.** With the Kumbu plugin connected to `Work`, publish a note, then delete it in Obsidian. **Passes:** the delete syncs (your own device may withdraw); the link reads nothing; the history says "… was withdrawn: … removed the note." Then rename a published note in Obsidian (a rename the plugin sends as a move): **passes** if the link still reads. If the plugin turns the rename into delete plus create, the publication is withdrawn, which is expected; note which it does.
11. **A member's Obsidian device.** Share `Work` with Bob (write). With Bob's Obsidian device, delete a note Alice published. **Passes:** the delete is refused and the note stays; watch whether the plugin retries for ever (it gets a 403 with code `published`; see open points).
12. **Bob's view in the web app.** Sign in as Bob, open the published note. **Passes:** the bar says exactly "Published by the vault's owner", no colour, no times, no buttons; the menu offers no Publish…, no marks; Bob can edit the note, and the reader's copy doesn't change. Bob's `sh publish ls /Work` (with his key) gives the path alone.
13. **Marks.** As Alice, in the tree: ⋯ on `Work/Private` → **Mark unpublishable**. **Passes:** an eye-off mark on the folder; Publish… on a note inside is disabled as "Publish… (unpublishable)". Mark a folder holding a published note: it asks first ("Marking it unpublishable withdraws its published link …"), and on yes the link dies. Move a published note into the marked folder: the publication is withdrawn, the history says why. Move the folder `Work/Private` to `Work/Archive/Private` (a whole rename): the mark follows. **Allow publishing** lifts it.
14. **The overview.** Settings → **Published notes**. **Passes:** every published note of your vaults with "Snapshot of …", "older than the note" where so, and "by you" or "by “Claude”"; no link.
15. **The switch.** Set `KUMBU_PUBLISH_ENABLED=false`, restart. **Passes:** the reader page says "Published notes can't be read on this server."; Publish… and Update copy are gone; the bar reads "Published. Publishing is switched off on this server, so the link reads nothing until it is on again." with **Unpublish** alone, which works; marks can be set and lifted. Set it back to `true`, restart: the links that weren't withdrawn read again.
16. **Two links in one browser.** Open link A in a tab, then link B in another tab, then reload tab A. **Expected (accepted limit):** tab A shows note B; its images are B's. Opening link A again shows A.
17. **Deactivation.** As admin, deactivate Alice. **Passes:** her links read as a wrong code. Reactivate: they read again.

## Open points for Chris

- **The Obsidian plugin should treat the refusal code `published` as final** (decision 21): a member's device that deletes the owner's published note gets a 403 with `code: "published"` and must not retry for ever. The plugin is another repository and was out of scope.
- **Decision 20 departs from spec §3.13's "the web app hides the publish UI":** with the switch off, Unpublish and the marks stay, on purpose (they only reduce what is out).
- **A re-key changes no publication's code or key.** Whoever opened the old record with an old vault key keeps the links until each note is withdrawn and published again. Documented in `docs/security-architecture.md`; a re-key could also re-issue codes, at the cost of every link handed out.
- **One reader cookie per browser** (Task 10): a reload of a tab can show the note opened last in another tab. A per-publication cookie would fix it, at the cost of a path or name per publication.
- **A key with `read` plus "May publish" may publish** (security review 6), while an OAuth read-only grant drops the right. The plan wants "any right"; make the two the same either way?
- **Links to the app under other host names** stay web links (security review 7): an old domain or an internal name would carry the vault and path out. Strip URLs shaped like the app's note routes on any host, or list kept web links in the dialog?
- **Code keeps links as written** (security review 8): a sentence in the Publish dialog ("Code blocks are copied as they are") would tell the owner.
- **ICC `desc` and `cprt` tags** are kept (they normally name the colour space; a hand-made profile could name a person).
- **"by you" in the overview** compares the publisher's label with the username. That is safe today because key and connection names equal to the username are refused (`WriterNames.Check`), but it leans on that rule; the server could send the writer's kind instead.
- **`KeepOneAsync` in a re-key** keeps the higher revision of two publishing records of one vault and deletes the other, which would orphan live copies; it can't happen normally (unique index).
- **The decisions you may want otherwise:** PDFs not published (1); the tag rule (2); the title (3); Update recording the updater as "by" (Task 6a); marks carried along on moves by anyone but your session (security review 3), which can leave marks in places you didn't mark yourself.
- **Specs outside this topic:** none looked wrong.
- **Not run here:** a browser against a running server; Task 11's page was tested in jsdom and its endpoint by the server's tests, together only in the hand test above.

## Tools installed

Node v26.13.0 (nodejs.org tarball into `/opt`), pnpm 10.32.1 (`npm install -g`). Docker's daemon restarted by hand once. No dependency added to the project.

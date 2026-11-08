# Overnighter report: WebDAV access, storage quota and access tabs

Date: 2026-11-07/08. Brief: [2026-11-07-webdav-brief.md](2026-11-07-webdav-brief.md). Spec: [2026-11-07-webdav-design.md](../specs/2026-11-07-webdav-design.md). Plan: [2026-11-07-webdav.md](../plans/2026-11-07-webdav.md).

**Pull request:** https://github.com/SCAI-NGO/kumbukumbu/pull/152 (draft, not merged, no tag, no version change).

**In short.** Every task of the plan, 0 to 21, is done; no task is left. Phases 1 to 6 each ended with a green gate. Each task went through red, green, commit and its mutations one by one, then a spec review and a code-quality review by fresh subagents; Tasks 18 and 19 each had one combined review instead. Eleven tasks needed one fix round, none needed a second. The security review of the whole branch found 1 High, 4 Medium, 3 Low and 2 Info findings: the High, the four Mediums, one Low and both Infos are fixed with tests, two Lows are accepted and documented. The final whole-branch review found no Critical issue and 3 Important ones, and all three are dealt with. The last full verification (Gate 7, at `8f2a3ae`) is green: .NET 4407 tests, 4401 passed, 0 failed, 6 skipped; web 100 files, 1732 tests; web build green; pdf 8 files, 146 tests, build green. litmus exits 0: its 21 failures are exactly the expected ones. The hand test in §10 has not been run, because no client was available here.

## 1. Environment

- Cloud container, Linux. .NET SDK **10.0.403** (`global.json` rolls forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Installed for the run** (nothing added to the project):
  - Node **26.13.0** into `/opt/node26`, put first on `PATH`;
  - pnpm **10.32.1** there;
  - **litmus 0.13** via `apt` (Task 18), the package CI's job installs too.
- **Docker 29.9.2** from the environment's setup script.
- **MongoDB** ran in a container of its own, `kumbu-test-mongo` (`mongo:7.0`, replica set `rs0`), with `KUMBU_TEST_MONGO="mongodb://localhost:27017/?replicaSet=rs0&directConnection=true"` and `TESTCONTAINERS_RYUK_DISABLED=true`.
  - Its file limit is `--ulimit nofile=20000:20000`. The daemon refused anything higher.
  - The first baseline run crashed mongod with `EMFILE` (too many open files). Since then the container starts with WiredTiger's `file_manager=(close_idle_time=15)`, so idle files are closed.
  - A helper script in the scratchpad recreates the container (`docker rm -fv` first), and every gate began with a fresh one.
- **Skills:** the superpowers skills were available. The run used **subagent-driven development**:
  - a fresh implementer subagent per task (red test, green, commit, every mutation on its own);
  - then a spec-compliance review and a code-quality review, each by another fresh subagent;
  - for a fix round, a fresh implementer and a re-review of the fix diff.
  - **Exception:** Task 18 (litmus in CI) and Task 19 (documentation) each had **one combined review** (spec and quality together). Both diffs were small and lay outside the product code. This departs from "two reviewers", and the ledger notes it.
- The security review (Task 20) and the final whole-branch review were fresh subagents on the most capable model, read-only.
- Subagents never pushed, merged or switched branches; the controller did the gates and the pushes.

## 2. The plan review

A fresh subagent reviewed the plan against the spec and the brief. It was not given the controller's conclusions. It found **2 blockers, 15 should-fix and 4 nits**. Commit `055827f` "Fold the plan review into the WebDAV plan" (181 lines added, 81 removed in the plan) folded them into the plan, on top of the first draft `8d3942f`. The two blockers:

1. **Tasks 9 to 21 did not stand alone.** They leaned on earlier tasks ("as in Task N", missing file anchors and commands), against the brief's rule that every task stands alone. The plan now gives each of them exact files, commands and expected outcomes.
2. **Parked content on the app's origin was not addressed** (brief, open point 1). The plan now puts every `GET` under `/dav/` through `FileContentPolicy`, sends parked bytes as `application/octet-stream` with `Content-Disposition: attachment` and `nosniff`, and keeps the OAuth CORS middleware off `/dav`. Tasks 6 and 7 built and tested this.

All should-fix items and nits went into the plan, except four that the controller ruled otherwise. Each is in the ledger and in §8:

- **#4, the label namespace.** Shares keep a namespace of their own. A share's writer label is always marked `(dav)`, so it never equals a key's or a connection's name, and key and connection creation stay untouched.
- **#20, `Known` for a valid token with another share's username.** It is `Known = false` and counts as a wrong guess (spec §3.2: "refused like a wrong token").
- **#13, parking in a read-only vault.** A parking write there gets 403, the reading that gives a share less.
- **Publication writes and the quota.** The quota never refuses them, since they copy what is already stored, but they count in usage.

## 3. Task 0: planned against measured

| | Planned (brief, known state `6a2afd9`) | Measured |
|---|---|---|
| Time | about 15 minutes | Much longer. Two .NET runs were void, the second started at 16:07 UTC, and the clean third ran from 16:15 to 16:25 UTC (9 min 39 s) |
| .NET | unknown locally; expected all passing, 4 skipped (model, TensorX), maybe 2 more as root | total **3989**, succeeded **3982**, failed **1**, skipped **6** |
| Web | 94 files, 1691 tests, build green | 94 files, 1691 tests passed, build green |
| pdf | not stated | 8 files, 146 tests passed, build green |

**The six skips**: the 4 expected ones (`UnigramTokenizerTests.MatchesHuggingFaceOnArcticL` and `…OnTheRealVocabulary`, `OpenAiCompatibleDriverTests.TensorX_ranks_by_meaning`, `SearchIndexTests.The_real_model_ranks_by_meaning`), plus 2 that skip as root (`SeedScannerTests.Skips_unreadable_files_with_a_warning` and `ModelFilesTests.AReadOnlyModelFolderTurnsSearchByMeaningOffInsteadOfStoppingTheServer`).

**The void first runs.**

- **Run 1.** mongod crashed with `EMFILE` part-way through. From then on, tests failed with server-selection timeouts ("A timeout occurred after 30003ms selecting a server … State : "Disconnected"") and query errors on `blobsTree`, in `DeletionTests`, `PasswordWindowTests`, `NoticeTests`, `FileApiTests` and others. The ledger records 5 failures, all from the crash; the log ends without a summary. The fix was `close_idle_time` in the container (§1).
- **Run 2.** It overlapped with another run. Kestrel-based tests failed with "Failed to bind to address http://127.0.0.1:5000: address already in use" (`DotSegmentTests`), and `RekeyTransitionTests` timed out. Not usable either.
- **Run 3** (clean) is the row above.

**The one failure in run 3, and its three solo runs.** The failure was `Kumbu.Server.Tests.Metrics.AttachmentMetricsTests.Turned_pages_count_for_pdf`: "Assert.Equal() Failure: Values differ / Expected: 1 / Actual: 0" (`AttachmentMetricsTests.cs:237`). The log shows "Storing the analysis of a PDF of vault … failed: MongoException" at that moment. The run shared its mongod with the Task 1 implementer's tests. Run alone with `--filter-class "*AttachmentMetricsTests"` three times, the class passed 13 of 13 each time. **Flaky**, not a known failure. It never failed again in any gate.

The brief's CI run 42382451518 on `6a2afd9` may have met the same test; this run can't tell.

## 4. Per task

Counts are from the implementers' reports and the gate logs. "Focused" means the classes the brief named, run with `--filter-class`. Every red run failed for the reason expected. The mutations are in §5.

### Phase 1: shares, slots, Basic authentication

#### Task 1: `dav` slots in the catalog. Commit `2702840`.

- **What it adds.**
  - `SlotDocument.Dav` and a separate `share` field (`ShareId`), which no API-key query can match.
  - `VaultCatalog`: a private `RightSlotKind` for keys and shares. The share's KEK is `kumbu/v1/dav-slot-kek` and its context `slot:{vaultId}:dav:{shareId}`; the API key's bytes are unchanged.
  - `DavSlotsAsync`, `OpenForDavShareAsync` (always `MayPublish = false`) and `DeleteDavSlotsAsync`.
  - `DavShareCredential`.
- **Red.** `DavSlotTests` (9 tests) did not compile: "error CS1061: 'VaultCatalog' does not contain a definition for 'DavSlotsAsync'", and "error CS0117: 'SlotDocument' does not contain a definition for 'Dav'".
- **Green.**
  - `DavSlotTests`: "failed: 0 / succeeded: 9 / Test run summary: Passed!".
  - `ApiKeyTests` and `DerivedCredentialTests`: 52 passed, 0 failed.
- **Review.** Spec ✅ (sonnet), quality Approved (opus). No fix round.

#### Task 2: `DavShares`, `/api/dav-shares`. Commit `dd77c4a`.

- **What it adds.**
  - `DavTokens`: 32 random bytes as 52 Crockford characters, normalised as pairing codes are.
  - `DavShares`, built like `ApiKeys`: create, list, revoke and resolve. It has lookup and writer seals, a sealed meta, a `sharesCreated` counter, the expiry sweep, rotation resealing and account deletion. Revocation runs on deactivation, on a password change with revoke, on recovery and on "Re-encrypt my vaults".
  - `GET`/`POST /api/dav-shares` (POST needs the password window) and `DELETE /api/dav-shares/{id}`.
- **Red.** "error CS0246: The type or namespace name 'DavShares' could not be found".
- **Green.**
  - `DavShareTests` (29) and `DavShareApiTests` (5): total 34, failed 0.
  - With the neighbouring classes (`EncryptedNoteStoreTests` … `ApiKeyTests`): total 327, failed 0.
  - A second set (`SessionTests` … `OwnerInactiveTests`): total 338, failed 0.
- **Review.** Quality Approved; spec and security ✅. No fix round.
- **Carried to Task 3.**
  - Writer ids `dav:{id}`.
  - Re-keying's share revocation in a try of its own.

#### Task 3: a member's shares. Commits `bb66693`, fix `7c9f83a`.

- **What it adds.**
  - The writer id `dav:{id}` for the membership lookup.
  - A share loses its vault through vault deletion, membership cleanup and re-keying, and is revoked when it has no vault left.
  - On re-keying, the owner's shares are revoked whole.
  - The member panel lists a share as kind `share`, "Garden PC (dav)".
  - The re-key preview lists shares (`GET /api/vaults/{vault}/access` gains `shares`).
  - "Re-encrypt my vaults" revokes shares in a try of their own.
- **Red** (`DerivedShareTests`): 7 failed, 1 passed. For example "Assert.NotNull() Failure: Value of type 'Nullable<DateTime>' does not have a value", and "Expected: [("Claude","key"), ("Garden PC (dav)","share")] Actual: [("Claude","key")]".
- **Green.**
  - `DerivedShareTests` 9/9.
  - 14 related classes: 251 passed, 0 failed.
  - `Vaults.RekeyTests`: 43 passed.
  - 9 more classes: 193 passed.
  - Release build: 0 warnings, 0 errors.
- **Review.** Spec and security ✅. Quality: Approved with 2 Important findings, both duplication: the share revocation copied the key's body, and the distinct-`ShareId` query was repeated five times.
- **Fix round 1** (`7c9f83a`).
  - One generic `SlotCredential<TDoc>` revocation body and one `ShareIdsAsync`.
  - `RekeyVaultView.revokedShares`.
  - The log text corrected.
  - 11 classes: 220 passed, 0 failed.

#### Task 4: Basic authentication on `/dav`. Commits `78ff920`, fix `4cd9dc3`.

- **What it adds.**
  - **Caller.** `CallerKind.DavShare`; the writer is `Work PC (dav)`, kind Agent, with a person's changeset.
  - **`CallerMiddleware`.** A `/dav` branch of its own, matched case-insensitively, before `NeedsCaller`. It answers in this order:
    1. `Cache-Control: private` on every answer, and any `Set-Cookie` removed;
    2. 404 when WebDAV is off;
    3. 403 "WebDAV needs HTTPS.";
    4. `OPTIONS` passes without credentials;
    5. Basic per RFC 7617, otherwise 401 with `WWW-Authenticate: Basic realm="Kumbu²", charset="UTF-8"`;
    6. the limiter, with the permit given back for a known share.
  - **`OPTIONS /` and `PROPFIND /`.**
  - **The HTTP metrics' method guard** admits the seven WebDAV methods.
  - **A Kestrel response-header encoding selector**, Latin-1 for the realm.
  - **`KUMBU_DAV_ENABLED`**, in the README, `.env.example` and `compose.yaml`.
- **Red.** `DavAuthTests`: total 22, 19 failed, 3 passed. Typical failures:
  - "Expected: NotImplemented / Actual: MethodNotAllowed" (the metrics guard);
  - "Expected: OK / Actual: Unauthorized" (OPTIONS);
  - `NotImplementedException` in the caller test.
- **On the way to green.**
  - Kestrel sent the realm as `Kumbu?`, hence the selector.
  - The Kestrel test clashed on port 5000 and was moved to a free port.
- **Green.** 12 classes: 145 total, 0 failed.
- **Review.** Spec and security ✅; quality Approved with minors to do before Task 5.
- **Fix round 1** (`4cd9dc3`).
  - `DavAssert`, and the WebDAV methods defined once in the test client.
  - A deterministic `NewIp()`.
  - A test, `Other_headers_go_out_over_kestrel_as_before`, pins that every other header keeps its ASCII check under the selector.
  - `OPTIONS /` is mapped only when WebDAV is on.
  - Results: `DavAuthTests` total 25, failed 0. With `LabelGuardTests`, `CallerTests`, `StaticFilesTests` and `DotSegmentTests`: total 89, failed 0.

**Gate 1** at `4cd9dc3` (17:33–17:45 UTC): green. Release build 0 warnings. .NET total 4066, succeeded 4060, failed 0, skipped 6. Web 94 files, 1691 tests; build green. pdf 8 files, 146 tests; build green.

### Phase 2: read-only WebDAV

#### Task 5: protocol skeleton, `OPTIONS`, `PROPFIND`. Commits `7bf94d0`, fix `7ccb511`.

- **What it adds.**
  - `DavEndpoints`: 12 methods, and a static `OPTIONS` that touches no store and no caller.
  - `DavPaths`: raw target parsing; `%2F` gives 400; everything goes through `ApiPaths.Parse`.
  - `DavXml`: 64 KB counted (chunked too); `DtdProcessing.Prohibit`; no resolver.
  - `DavTree`: no folder markers in listings.
  - `Depth` 0 and 1 work; `infinity` or a missing `Depth` gets 403 `propfind-finite-depth`.
  - `/dav` without a slash gets 308 to `/dav/`.
- **Red.** `DavPropfindTests`: total 19, failed 19. For example "Expected: MultiStatus Actual: NotImplemented", and "Expected start: "HTTP/1.1 400"".
- **Green.**
  - `DavPropfindTests` 19/19.
  - With `DavAuthTests`, `DotSegmentTests` and `DavShare*`: 92/92.
- **Review.** Spec and security ✅. Quality: Approved with 2 Important findings: a property seam was missing, and the method list existed twice.
- **Fix round 1** (`7ccb511`).
  - A per-request `DavProperties`, the `DavMethod` constants and `DavException` in a file of its own.
  - `<D:prop/>` with no children gets 400.
  - `Through` rejects 5xx.
  - More 404 cases.
  - `DavPropfindTests`, `DavAuthTests` and `DotSegmentTests`: total 52, failed 0.
- **Incident.** This fix round's implementer committed on a **detached HEAD** (§9).

#### Task 6: `GET`, `HEAD`, `PROPPATCH`. Commit `86b629e`.

- **What it adds.**
  - Every `/dav` endpoint carries `FileContentMetadata`, so every answer has the file-content CSP `sandbox; default-src 'none'; frame-ancestors 'none'`.
  - One `ServeAsync`, which writes ETag, Last-Modified, `If-None-Match` → 304 and `Content-Disposition` from `FileApi.Disposition`.
  - Notes go out as `text/markdown; charset=utf-8`.
  - `FileApi.OpenAsync` was extracted from `FileApi` so DAV reuses it.
  - `PROPPATCH` answers 207, every property 200, and stores nothing.
- **Red.** All 8 `DavReadTests` and `A_doctype_in_the_body_is_refused` failed: "Expected: OK/MethodNotAllowed/NotModified/MultiStatus/BadRequest Actual: NotImplemented".
- **Green.** `DavReadTests`, `DavPropfindTests`, `FileApiTests` and `DavAuthTests`: total 71, failed 0. Build without warnings.
- **Review.** Quality Approved; spec and security ✅. No fix round.

**Gate 2** at `86b629e` (18:16–18:28 UTC): green. .NET total 4087, succeeded 4081, failed 0, skipped 6. Web 94/1691; build green. pdf 8/146; build green.

### Phase 3: writing

#### Task 7: parking and locks. Commits `e3f8f28`, `354a77d`, fix `d2af862`.

- **What it adds.**
  - **`DavRules`.** The hold window is 60 s, parking lasts 10 min, the cap is 64 MiB per share and the lock default 600 s. Parking patterns come from spec §3.8 plus vim's `4913` and emacs' `#*#` (open point 7). A dot-folder above a name parks it too.
  - **`DavParking`.** Kept per share, vault and path; it expires lazily and evicts the oldest files.
  - **`DavLocks`.** Fake locks, kept per share.
  - **`DavCaller`, `DavDestination`** (moved forward from Task 9 by a ruling), **`DavBody`** (counts the bytes as they arrive), and **`DavWrites`** with the shared protocol checks (403, 409, 405, 415, 502, 412).
  - **Parked content.** It goes out as `application/octet-stream`, `Content-Disposition: attachment`.
- **Red.** `DavDestinationTests` did not compile (CS0103). Without it, the run gave "total: 51, failed: 39, succeeded: 12". Every new test failed with "Expected: Created/OK/Forbidden/BadRequest, Actual: NotImplemented".
- **Green.**
  - `DavParkingTests`, `DavLockTests` and `DavDestinationTests`: 59/59, then 71/71 with the unit theory of `354a77d`.
  - All six Dav classes: **117 total, 0 failed**.
  - Build: 0 warnings.
- **Review.** Both reviews: Needs fixes.
  - Quality: a lock's `Owner` element was shared across responses (a race), and COPY or MOVE onto an ancestor lost the source while answering 204.
  - Security: the parking cost undercounted entries (memory DoS), and lock owners were XElement trees (memory).
- **Fix round 1** (`d2af862`).
  - **The new tests failed first** ("total: 57, failed: 7"), for example "Assert.Null() Failure … Actual: <note xmlns="DAV:">changed</note>", and "Expected: Forbidden, Actual: NoContent".
  - **Lock owners.** The owner is serialised once; an owner over 4096 characters is left out.
  - **Ancestors.** Copying or moving onto a folder above the source gets 403.
  - **Cost and cap.** Each entry costs 512 bytes, with a cap of 10,000 entries per share.
  - **No capture.** A parked folder no longer captures a store folder of its name.
  - **Order of checks.** Room is reserved before a move or copy, and a copy never evicts its own source.
  - **Green:** six Dav classes, total 124, failed 0.

#### Task 8: `PUT` and `MKCOL` into the store. Commits `8b43b60`, `3ced6b9`, fix `bb73412`, `e31a284`.

- **What it adds.**
  - **`PUT` of a `.md`.** Its checks run in this order:
    1. rights, before the body;
    2. 1 MiB, counted (413);
    3. strict UTF-8 (415), with a BOM kept;
    4. `If-Match` / `If-None-Match` (412);
    5. then `NoteWriter`, which turns CRLF into LF. The answer carries the ETag.
  - **`PUT` of an attachment.**
    - Rights, then 15 MB (413), then the format and `ImageHeader` (415).
    - An empty `PUT` parks a placeholder, which the next content `PUT` replaces.
    - Any other format gets 415 with `AttachmentFormat.UnsupportedMessage`.
  - **`MKCOL`** goes through `MakeFolderAsync`.
- **Red.** `DavPutTests`: 16 of 23 failed, each "Expected: <status> / Actual: NotImplemented". The other 7 cover Task 7's protocol checks.
- **Green.**
  - `DavPutTests`: total 23, failed 0.
  - With 10 neighbouring classes: total 208, failed 0.
- **Review.** Both: Needs fixes.
  - Spec: rewriting `.MD` to `.md` broke read-after-write.
  - Quality: the compare-and-swap retry was untested and duplicated.
- **Fix round 1** (`bb73412`, `e31a284`).
  - **`.MD` resolution.** A `.MD` name resolves to its `.md` note on every method, in one place (`DavTree.ResolveAsync`). PROPFIND echoes the asked href.
  - **The retry.** It is folded into `WriteOnceMoreIfLostAsync` and keyed on `Kind == Conflict`. That needed a Core change: NoteWriter's "already exists" results now carry `Kind = Conflict` (ruling in §8).
  - **Tests.** A `RacingCaller` test seam covers the retry, with no production hook. The 413 texts give the limits.
  - **Red:** for example GET `Plan.MD` "Expected: OK, Actual: NotFound", and "Expected: "A note can have at most 1 MiB." Actual: "…1048576 bytes (1 MiB).""
  - **Green:** 11 classes, total 222, failed 0. Core: total 1602, failed 0.
  - **Incident:** a lost edit during the mutations (§9).

#### Task 9: `DELETE` and `COPY`. Commits `3f31c90`, `cffff1d`.

- **What it adds.**
  - **A new Core `TreeWriter`** for `rm -r` and copy batches. A folder goes to the trash in one batch.
  - **Copy.** Every check runs before the target is trashed, the copy rewrites no links, and copies share the source's blob.
  - **`DavHolds.RecordRemoval`.**
  - **COPY into and out of parking:** a parked file copied onto a real path is a `PUT` of its bytes.
- **Red.** 20 failed with "Expected: NoContent/Created/... Actual: NotImplemented"; 10 passed (Task 7's protocol checks).
- **Green.**
  - `DavDeleteCopyTests` 30/30.
  - With the Put, Parking, Lock and Destination classes: 142/142.
  - The Read, Propfind and Auth classes with `PublishedNoteRemovalTests`: 69/69.
- **Review.** Quality Approved; spec and data safety ✅. No fix round.

#### Task 10: `MOVE` and atomic saves (spec §3.6 cases 1–5). Commits `60d587e`, fix `ea3b694`.

- **What it adds.**
  - **Case 1:** a move to a free path goes through `NoteMover`, links rewritten, one batch.
  - **Case 2:** a parked file onto a note is that note's content write.
  - **Case 3:** a removal by the same share within 60 s is restored from the trash.
  - **Case 4:** held.
  - **Case 5:** with `Overwrite: T`, the target is trashed and the move made in one batch (`NoteMover.MoveOverAsync`).
- **Red** (`DavMoveTests`, with a stub): 21 failed, 8 passed, each failure "Actual: NotImplemented".
- **Green.**
  - `DavMoveTests` 29/29, three runs in a row.
  - All Dav classes: 188/188.
  - Core: 1600 passed, 2 skipped.
  - The classes that use `NoteMover`: 281/281.
- **Review.** Both: Needs fixes.
  - Security: a timer ended holds with a cached credential and rights, and an ending ignored the version.
  - Quality: the one-batch test couldn't see two batches, and cancellation dropped holds.
- **Fix round 1** (`ea3b694`).
  - **No credential outlives its request.** The hosted timer and `IsLiveAsync` were removed. A due hold ends lazily, on the same share's next request, with that request's rights. A hold not ended within the 10 minutes is forgotten and the note stays (ruling in §8).
  - **Version.** The ending removes at the held version.
  - **Cancellation.** Holds that didn't end are put back.
  - **One batch.** The one-batch tests tick the clock.
  - **Red:** 5 of 37 failed, for example "Expected "# v1\n", Actual: null" (the old timer ended another share's hold).
  - **Green:** `DavMoveTests` 38/38. All Dav classes and the NoteMover users: 507/507. Core: 1600 passed, 2 skipped.

#### Task 11: published notes (spec §3.6 cases 6–7). Commits `a82bd02`, fix `1d7ad3a`.

- **What it adds.**
  - **DELETE of a published note** answers 204 and holds the note under no name. After the minute the note reappears, nothing goes to the trash, and the owner's activity records "“Work PC (dav)” tried to remove /Work/Ideas/plan.md; it is published, so it stays.", which comes from a new log kind `kept` and a new event `published-note-kept`.
  - **A folder DELETE** trashes the rest of the folder and keeps the published notes and the folders above them.
- **Red.** 7 failed, 2 passed, for example "Expected: NoContent / Actual: Forbidden".
- **Green.**
  - `DavPublishedTests` 10/10.
  - With `VaultEventTests`: 31/31.
  - 12 related classes: 255/255.
- **Review.** Spec and security ✅. Quality: Needs fixes, because the `Published` branch was untested on its own (mutation 1 survived, §5).
- **Fix round 1** (`1d7ad3a`).
  - Three tests for each ending.
  - Repeated `kept` entries for the same note and writer are folded within an hour.
  - `DavPublishedTests` 14/14; 14 related classes: total 299, failed 0.

**Gate 3** at `1d7ad3a` (20:54–21:05 UTC): green. .NET total 4280, succeeded 4274, failed 0, skipped 6. Web 94/1691; build green. pdf 8/146; build green.

### Phase 4: quota

#### Task 12: the storage quota on every write that adds data. Commits `0a8eb67`, fix `4be8dab`.

- **`StorageQuota`.**
  - `QuotaSize.Parse` reads binary prefixes.
  - The usage cache and the recount run in the background and coalesce.
  - The check reads memory only: a write never waits for MongoDB.
- **The check runs in two places in the store:** `WriteUnderGateAsync` (with `adding`) and `PutBlobAsync`.
- **`NoteBatch.IsMove`** decides by kind, so `rm`, `mv`, `DELETE` and `MOVE` always pass.
- **What a refusal says.**
  - REST and WebDAV: 507 "This vault is out of space.", plus `code: "quota"` for the owner.
  - MCP and `sh`: the owner also gets "See `df`.".
- **RFC 4331 properties.**
- **Settings.**
  - `KUMBU_QUOTA_DEFAULT`.
  - The admin's override (`PATCH /api/admin/users/{id}` `quota`).
  - `GET /api/account/storage`.
  - `schema.d.ts` edited by hand.
- **Red.** Every `QuotaApiTests` test failed except `Parked_bytes_do_not_count`, typically "Expected: InsufficientStorage, Actual: Created". Every `Parse` case threw `NotImplementedException`.
- **Green.**
  - `StorageQuotaTests` and `QuotaApiTests`: 45/45.
  - The full server suite: 2718 passed, 4 skipped, 1 failed. The failure was `PasswordWindowTests.Every_session_only_endpoint_is_guarded_or_deliberately_open`, because the new GET was not listed; it was fixed and rerun green.
  - Core: 1600 passed, 2 skipped.
  - `AccountApiTests.Recovery_revokes_every_api_key_and_replaces_the_used_recovery_key` failed once in a focused rerun, then passed twice and in the full suite. It is a known flaky test.
- **Review.** Spec and coverage ✅. Quality: Needs fixes, because `_running` leaked and a failed count was retried on every write.
- **Fix round 1** (`4be8dab`).
  - Ended tasks are pruned, and failures are retried after 10 minutes.
  - Writes made during a count are kept (`Pending`).
  - **Breaks nothing:** without a default or an override, nothing is looked up at all.
  - A PATCH applies all or nothing.
  - Quota classes: 50/50. With the Admin, AccountApi, PasswordWindow and WelcomeVault classes and every `Dav*Tests` class: **455 passed, 0 failed**.

#### Task 13: emptying the trash. Commit `e15ab06`.

- **What it adds.**
  - `EncryptedNoteStore.PurgeTrashAsync`: one transaction with the owner's event `trash-emptied` (with `count`), through the quota.
  - `POST /api/vaults/{vault}/trash/purge`: session only, owner only, with the password or within the window.
  - Nothing in `sh` or MCP.
- **Red.** 8 failed with "Expected: OK/Forbidden/BadRequest, Actual: NotFound". `No_sh_command_deletes_permanently` passed from the start, as a guard.
- **Green.**
  - `EmptyTrashTests` 9/9.
  - With 5 related classes: 171/171.
  - `pnpm -C web build` green.
- **Review.** Spec and security ✅; quality Approved. No fix round.

#### Task 14: `df` and `du` in `sh`. Commits `58f2fed`, fix `ff9261f`.

- **What it adds.**
  - `df [-h] [-b] [/vault]` and `du [-h] [-b] [-s] [-d N] [path]` in Core, with `IStorageFigures`.
  - `ShellStorageFigures` in the server.
  - Help, the manual, the web console's command table, metrics and README.
- **Red.**
  - Core: failed 20, succeeded 100.
  - Server: "Assert.StartsWith() Failure … Expected start: "Vault "".
- **Green.**
  - Core Df, Du and Help: 120/120. Full Core: 1620 passed, 2 skipped.
  - Server classes: 132 passed.
  - Web `paths.test.ts`: 108 passed; build green.
- **Review.** Quality Approved. Spec and security: Needs fixes.
  - A partial key's Total was misleading.
  - `df` ran one aggregation per call.
- **Fix round 1** (`ff9261f`).
  - With a key that opens only some vaults, the Total line has no percentage and says "your other vaults count against the 2.0 GB limit too".
  - `RecentFigureAsync` counts at most every 30 s per owner and coalesces concurrent counts.
  - **Red:** Core failed 2, succeeded 28. Server: "Expected: 1 Actual: 2".
  - **Green:** Core 1630 passed, 2 skipped. Server classes: 127 passed.

**Gate 4** at `ff9261f` (22:33–22:46 UTC): green. .NET total 4372, succeeded 4366, failed 0, skipped 6. Web 94 files, 1695 tests; build green. pdf 8/146; build green.

### Phase 5: web app

#### Task 15: the Access page with tabs. Commits `2efaf06`, fix `9ec3881`.

- **What it adds.**
  - `/access/keys`, `/access/apps`, `/access/dav`.
  - An ARIA tablist with arrow, Home and End keys.
  - `/keys` and `/access` redirect to `/access/keys`.
  - A reveal has the page to itself.
- **Red.** "Tests 4 failed (4)", for example "Unable to find role="heading" and name "Access"".
- **Green.**
  - Access 5/5.
  - Full web suite: 95 files, 1700 tests.
  - Build green.
- **Review.** Needs fixes: a stale `revealing` hid the tabs.
- **Fix round 1** (`9ec3881`).
  - A `useReveal` hook that reports `false` on unmount.
  - Keyboard navigation with `replace`.
  - **Red:** "Tests 2 failed | 6 passed (8)".
  - **Green:** 95 files, 1703 tests; build green.

#### Task 16: the WebDAV tab, new-share dialog, connect page. Commits `2485332`, fix `820138f`.

- **What it adds.**
  - `DavShares.tsx`, `NewShareDialog.tsx` (no May publish), and `DavConnect.tsx`. The password is shown once, with a leave guard. Instructions for all seven clients, with the username filled in.
  - `RevokeDialog` for shares.
  - The Settings re-key preview lists shares.
  - `schema.d.ts` by hand.
- **Red.**
  - 16 failed, for example "Unable to find role="button" and name "New share"".
  - Settings: 3 failed.
- **Green.**
  - The three files 16/16; Settings 34/34.
  - Full web suite: 98 files, 1720 tests.
  - Build green.
- **Review.** Quality Approved. Spec: Needs fixes, because the re-encrypt texts and invalidations left out shares.
- **Fix round 1** (`820138f`).
  - The texts name WebDAV shares, and `dav-shares` is invalidated.
  - The password-change checkbox now reads "Also revoke all API keys and WebDAV shares, and disconnect apps".
  - Ended shares are folded into "Revoked and expired (N)".
  - **Green:** Settings 35/35, DavShares 6/6, the full suite 98 files with 1721 tests; build green.

#### Task 17: the quota display, admin override, trash. Commit `40b3cb7`.

- **What it adds.**
  - `storage.ts`, and `errorText` for 507 ("Your storage is full (2 GB). Empty the trash or ask your admin for more." for the owner; the server's sentence for a member).
  - In Settings: "Storage: 182 MB of 2 GB".
  - `StorageNotice` from 90 %.
  - The admin's `QuotaControl`, with a meter.
  - `TrashSheet`: "Empty trash…" and "Delete permanently…" for the owner, with the password when the window is closed.
- **Red.** 6 failed, and 2 files did not import, for example "Unable to find an element with the text: Storage: 182 MB of 2 GB".
- **Green.**
  - The 5 files: 73/73.
  - Full web suite: 100 files, 1730 tests.
  - Build green.
- **Review.** Spec and security ✅; quality Approved, with many minors that the final review triaged. No fix round.

**Gate 5** at `40b3cb7` (23:28–23:40 UTC): green. .NET total 4372, succeeded 4366, failed 0, skipped 6. Web 100 files, 1730 tests; build green. pdf 8/146; build green.

### Phase 6: litmus, documentation, review

#### Task 18: litmus in CI. Commits `a904d86`, `a26c9e9`.

- **What it adds.**
  - `tools/litmus/run.sh`. It signs in through the REST API, confirms the password, makes a vault and a share, `MKCOL`s `.litmus/`, and runs `basic copymove props locks`.
  - `tools/litmus/expected-failures.txt`.
  - The `litmus` job in `ci.yml`: `apt-get install litmus`, `mongo:7.0` as a replica set from a step, the bootstrap admin with a masked random password, and the server log on failure.
- **Results.** litmus found no protocol bug, so there is no xUnit test and no server change. §6 has the results.
- **Review.** One combined review (spec and quality): ✅ Approved.

#### Task 19: documentation. Commit `53d818a`.

- **What it adds.**
  - README: new sections "WebDAV" and "Storage and the trash", and the revocation texts.
  - `docs/security-architecture.md`: §1–§7, the `dav` slot row, `dav_shares`, and "No agent can delete permanently".
  - `docs/concept.md`: §4, §8, §9 and §13.
  - The open item for the Obsidian plugin and 507.
- **Review.** One combined review: ✅ Approved. One minor ("per client network") was left, because it matches the README's existing wording for the same limiter.

#### Task 20: the security review, its fixes and follow-ups

The review started at `a26c9e9`, while Task 19's documents were still being written (see §7). The fixes, each with a failing test first:

| Finding | Commit | |
|---|---|---|
| 7 | `c512570` | Fold a share's kept attempts per note |
| 2 | `ecd9cf8` | Refuse a raw `#` in a WebDAV target or Destination |
| 9 | `48ebee4` | Refuse control characters in WebDAV paths and Destination headers |
| 10 (lower-case method) | `028f17e` | Answer a WebDAV method in another case as an unknown one, with 405 |
| 1 | `6429514` | Cap the properties a PROPFIND may name and stream the multistatus |
| 3 | `aa90733` | Refuse a copy into the parking area that exceeds its cap before reading it |
| 4 | `15617b1` | Bound WebDAV memory across shares and the shares a user may hold |
| 5 | `83a3fb5` | Serve the account's storage from the 30-second figure df uses |

The red and green runs, as `Dav.*` grew:

| Finding | Red | Green |
|---|---|---|
| 7 | "Expected: 2 Actual: 4" | `DavPublishedTests` 15/15 |
| 2 | "DELETE /dav/Work/frag/#ment HTTP/1.1 : HTTP/1.1 204 No Content" | `Dav.*` 244/244 |
| 9 | 9 failures, for example "Actual: DavTarget { Path = /Work/.a\0b }" | 253/253 |
| 10 | "options: 500" | 254/254 |
| 1 | "Expected: BadRequest / Actual: MultiStatus" | 256/256 |
| 3 | "Expected: InsufficientStorage / Actual: Created" | 257/257 |
| 4 | parking "Assert.Null() Failure: Value is not null"; locks "Assert.Empty() Failure"; shares "Expected: Conflict Actual: Created" | 294/294 with the share classes |
| 5 | "Expected: 1 Actual: 2" | `QuotaApiTests`, `StorageQuotaTests` and `EmptyTrashTests` 61/61 |

The final run of the fix wave was **355/355 passed**: `Dav.*`, `DavShareApiTests`, `DavShareTests`, `QuotaApiTests`, `StorageQuotaTests`, `EmptyTrashTests` and `PublishingApiTests`.

**Re-review.** All 8 fixes ADDRESSED. It asked for two follow-ups:

- `d4d16f2` "Leave a multistatus cut short by a failure unclosed, so it is never well-formed".
  - Red: "Assert.DoesNotContain() Failure: Sub-string found".
  - The writer's dispose used to close `</D:multistatus>`.
- `1f97a1d` "Evict from the share holding the most when parking is full across shares".
  - Red: "Assert.NotNull() Failure: Value is null" (the victim's file was evicted), and "Assert.All() Failure: 1 out of 10 items".
- After both: `Dav.*` 262/262.

**Gate 6** at `83a3fb5` (00:40–00:52 UTC), before the follow-ups: green. .NET total 4395, succeeded 4389, failed 0, skipped 6. Web 100/1730; build green. pdf 8/146.

#### The final whole-branch review and its fix wave

- **The review.** It covered `6a2afd9..1f97a1d` (49 commits) on the most capable model, read-only. It ran the Release build itself (0 warnings, 0 errors), the web tests (100 files, 1730 passed) and the web build (green). It did not run `dotnet test`, because the shared mongod was in use.
- **Verdict:** "Ready to merge: with fixes". 3 Important findings, 10 minors and 5 deferred minors it calls must-fix (all in §7).
- **The fix wave** was one fresh agent, with 13 commits from `ca20ba5` to `34e27d9`:
  - `ca20ba5` and `34e27d9`: I1, the session's `storageLimited` flag and `StorageQuota.LimitedAsync`.
  - `5921a01`: I2 and M4.
  - `eaf4bc2`: M1.
  - `8af65f6`: M3.
  - `45f9d49`: M6.
  - `93042e4`: M7.
  - `32f5119`: M2.
  - `6e68710`: M5.
  - `113411c`: M8.
  - `ffe1fb2`: M10.
  - `b1a9209`: M9.
  - `bb5c253`: `schema.d.ts` made what `openapi-typescript` generates from the running server.
  - Each item had a red test where it changed behaviour. For example, MOVE onto a placeholder: the file was held and `FindMoveAsync` returned null. M6: "405 against the page's 401". M7: "500". M2: "Error while copying content to a stream".
- **The wave's final runs.**
  - The affected server classes (`Dav*`, `QuotaApiTests`, `StorageQuotaTests`, `DavShareApiTests`, `Publishing.*`, `CallerTests`, `SessionApiTests`): **508/508**.
  - Core Df and Du: 185/185.
  - Web: 100 files, 1732/1732; build green.
- **Re-review.** All items ADDRESSED; no new Critical or Important issue. Two residual minors are in §9.
- **Gate 6b** at `34e27d9` (01:32 UTC): .NET total 4407, **4400 passed, 1 failed**, 6 skipped. The failure was `DavParkingTests.A_30_MB_parked_put_goes_through_kestrel`: "System.IO.IOException : Failed to bind to address http://127.0.0.1:5000: address already in use". It is the new 30 MB test, whose real Kestrel took port 5000 while a parallel Kestrel test held it.
- **The port fix.** `8f2a3ae` "Let the 30 MB WebDAV test take a free port".

**Gate 7** at `8f2a3ae` (01:47–02:00 UTC): **green**.

- Release build: 0 warnings.
- .NET: total **4407**, succeeded **4401**, failed **0**, skipped **6**. The server tests took 10 min 54 s.
- Web: 100 files, 1732 tests; `web build exit 0`.
- pdf: 8 files, 146 tests; `pdf build exit 0`.

litmus at the same head is in §6.

#### Task 21: the pull request and this report

- The draft PR #152 is open, titled "WebDAV access, storage quota and access tabs". Its body lists the decisions made without Chris, says that a security review applies, and ends with the Claude Code line.
- After the PR's first CI run, `d3e5a2d` "Compare litmus failures by name, not by number" (§6).
- This report and the row in `docs/superpowers/open-items.md` are the last commit.

### Phase gates at a glance

| Gate | Head | .NET total / passed / failed / skipped | Web files / tests, build | pdf |
|---|---|---|---|---|
| Task 0 (run 3) | `6a2afd9` | 3989 / 3982 / 1 (flaky) / 6 | 94 / 1691, green | 8 / 146, green |
| 1 | `4cd9dc3` | 4066 / 4060 / 0 / 6 | 94 / 1691, green | 8 / 146, green |
| 2 | `86b629e` | 4087 / 4081 / 0 / 6 | 94 / 1691, green | 8 / 146, green |
| 3 | `1d7ad3a` | 4280 / 4274 / 0 / 6 | 94 / 1691, green | 8 / 146, green |
| 4 | `ff9261f` | 4372 / 4366 / 0 / 6 | 94 / 1695, green | 8 / 146, green |
| 5 | `40b3cb7` | 4372 / 4366 / 0 / 6 | 100 / 1730, green | 8 / 146, green |
| 6 | `83a3fb5` | 4395 / 4389 / 0 / 6 | 100 / 1730, green | 8 / 146, green |
| 6b | `34e27d9` | 4407 / 4400 / 1 (port clash) / 6 | 100 / 1732, green | 8 / 146, green |
| 7 | `8f2a3ae` | 4407 / 4401 / 0 / 6 | 100 / 1732, green | 8 / 146, green |

## 5. Every mutation

Each mutation was made alone on the committed tree. The named tests ran and failed as shown. The file was restored with `git checkout -- <file>` (or from a copy, see §9), and the tests were green again. "Survived" means the tests stayed green. Each survivor led to a stronger test or has a stated reason.

### Task 1 (`DavSlotTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | `DavSlots` built with the API key's KEK label | `A_share_slot_is_sealed_under_the_dav_key_and_bound_to_the_share`, `A_share_slot_with_a_planted_publish_flag_may_not_publish` | AuthenticationTagMismatchException; "Assert.Single() Failure: The collection was empty" (failed 2 / succeeded 7) |
| 2 | `dav` context without the share id | `…_sealed_under_the_dav_key_and_bound_to_the_share`, `A_share_slot_moved_to_another_share_id_opens_nothing`, `…_planted_publish_flag_may_not_publish` | AuthenticationTagMismatchException; "Assert.Empty() Failure: Collection was not empty"; "Assert.Single() Failure" (failed 3) |
| 3 | `OpenForDavShareAsync` without `MayPublish = false` | `A_share_slot_with_a_planted_publish_flag_may_not_publish` | "Assert.False() Failure" (failed 1) |

Tests 2 and 6 were added beyond the brief, because its four tests alone could not catch mutations 1 and 3.

### Task 2 (`DavShareTests`, `DavShareApiTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Skip the username check in `ResolveAsync` | `Resolve_refuses_the_token_with_another_shares_username`, `A_username_with_a_dot_resolves` | "Assert.Null() Failure: Value is not null" (2 of 34) |
| 2 | Drop the `RevokedAt` check | `Resolve_refuses_a_revoked_share`, `Resolve_refuses_a_deactivated_user`, `A_password_change_that_revokes_the_keys_revokes_the_shares`, `A_recovery_revokes_the_shares` | "Assert.Null() Failure: Value is not null" (4 of 34) |
| 3 | Store the token's first 8 characters as `prefix` | `Nothing_of_the_token_is_stored` | "Assert.False() Failure" (1 of 77, with `EncryptedNoteStoreTests`) |
| 4 | Skip the share revocation on deactivation | `Resolve_refuses_a_deactivated_user` | "Assert.True() Failure" (no `revokedAt`) |

### Task 3 (`DerivedShareTests`; each run failed 1, succeeded 8)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Owner's re-key share filter `Dav` → `ApiKey` | `Rekeying_revokes_the_owners_share_whole` | "Assert.NotNull() Failure: Value of type 'Nullable<DateTime>' does not have a value" |
| 2 | Members' re-key share filter `Dav` → `ApiKey` | `Rekeying_takes_the_vault_from_a_members_share_and_revokes_it_if_none_is_left` | same NotNull failure |
| 3 | Role cap dropped in `CredentialAccess` | `A_member_share_opens_at_most_at_the_role` | "Expected: Read Actual: Write" |
| 4 | Share listed as kind `key` | `The_owners_member_panel_lists_the_share` | "Actual: [("Claude","key"), ("Garden PC (dav)","key")]" |
| 5 | Bare share id for the writer-id lookup | `A_member_shares_writes_carry_its_writer_id_and_label_in_the_vault` | "Assert.NotNull() Failure: Value is null" |
| 6 | No share revocation in vault deletion | `Deleting_the_vault_revokes_a_share_left_without_vaults` | NotNull failure |
| 7 | No share revocation in membership cleanup | `Ending_the_membership_ends_the_shares_access` | NotNull failure |
| 8 | Share revocation back inside the keys' try | `Re_encrypting_revokes_a_members_share_even_when_revoking_the_keys_fails` | NotNull failure |
| 9 | `AccessAsync` share ids empty | `The_rekey_preview_lists_shares` | "Expected: 2 Actual: 0" |
| F1 | (fix round) Owner's share ids looked up with `KeyCredentials` | `Rekeying_revokes_the_owners_share_whole` | "Expected: 1 Actual: 0" (`revokedShares`) |
| F2 | Members' share ids empty in `EndMembersCredentialsAsync` | `Rekeying_takes_the_vault_from_a_members_share_…` | NotNull failure |
| F3 | Deletion's share ids empty | `Deleting_the_vault_revokes_a_share_left_without_vaults` | NotNull failure |
| F4 | Cleanup's share ids empty | `Ending_the_membership_ends_the_shares_access` | NotNull failure |
| F5 | `RevokedShares` not copied into the summary | `Rekeying_revokes_the_owners_share_whole` | "Expected: 1 Actual: 0" |
| F6 | Preview share ids empty | `The_rekey_preview_lists_shares` | "Expected: 2 Actual: 0" |

### Task 4 (`DavAuthTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Count a known revoked token as wrong | `A_revoked_share_gets_401_and_does_not_use_up_the_limiter`, `A_rekeyed_vault_is_gone_from_the_share` | "Expected: Unauthorized / Actual: TooManyRequests" |
| 2 | Accept plain HTTP | `Plain_http_gets_403_outside_Development` | "Expected: Forbidden / Actual: NotImplemented" |
| 3 | Let Bearer through on `/dav` | `Bearer_is_not_accepted_on_dav` | "Expected: Unauthorized / Actual: NotImplemented" |
| 4 | Case-sensitive prefix match | `The_prefix_is_matched_case_insensitively` | the challenge was `"Bearer"` where the Basic challenge was expected |
| 5 | (seen in passing) Selector returning null for `WWW-Authenticate` | `The_challenge_goes_out_over_kestrel` | no `0xB2` byte on the wire |
| 6 | (fix round) `OPTIONS /` mapped unconditionally | `Options_on_root_says_nothing_of_DAV_when_it_is_switched_off` | "Expected: Unauthorized / Actual: OK" |

### Task 5 (`DavPropfindTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Allow `Depth: infinity` | `Depth_infinity_and_a_missing_depth_get_403_with_propfind_finite_depth` | "Expected: Forbidden Actual: MultiStatus" |
| 2 | List folder markers | `A_vault_lists_notes_attachments_and_folders_but_no_folder_markers` | "Collections differ at index 1 … Expected: "/dav/Work/Ideas/Routing.md"" |
| 3 | `DtdProcessing.Parse` | `A_doctype_in_the_body_is_refused` | "Expected: BadRequest Actual: MultiStatus" |
| 4 | (extra) Encoded-slash check removed | `Dot_dot_and_encoded_slashes_are_refused` (`a%2Fplan.md`, `a%2fplan.md`) | ""HTTP/1.1 404 Not Found…"" |
| 5 | (extra) Only the declared length checked | `A_body_over_64_KB_is_refused` | "Expected: RequestEntityTooLarge" |

The fix round re-ran mutations 1 to 3 on the new code, and each failed its test again.

### Task 6 (`DavReadTests`, `DavPropfindTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Attachment without the policy metadata and without `Content-Disposition` | `Get_of_an_attachment_has_the_same_headers_as_the_file_api`, `Get_of_a_note_goes_through_the_file_content_policy` | "Expected: "inline; filename="Bild 1.png"…" Actual: """; "Expected: "sandbox; default-src 'none'; …" Actual: "default-src 'self'; …"" |
| 1b | (extra) Only the metadata removed | both tests | the CSP, as above |
| 2 | Note served as `text/html; charset=utf-8` | `Get_of_a_note_goes_through_the_file_content_policy` | "Expected: "text/markdown; charset=utf-8" Actual: "text/html; charset=utf-8"" |

### Task 7 (`DavParkingTests`, `DavLockTests`, `DavDestinationTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Parking keyed by user instead of share | `Parked_files_are_listed_for_their_share_only`, `A_parked_file_can_be_moved_copied_and_deleted_within_its_share` | "Assert.DoesNotContain() Failure: Item found … "/dav/Work/.DS_Store""; "Expected: NotFound, Actual: Created" |
| 2 | Ancestor dot-folder rule dropped | `The_patterns_are_matched_on_the_name_and_on_dot_folders_above_it(".git/config", True)` and `("Ideas/.obsidian/plugins/x/main.js", True)` | "Expected: True, Actual: False" |
| 3 | Never evict | `The_oldest_parked_files_are_evicted_beyond_64_MB` | "Expected: NotFound, Actual: OK" |
| 4 | Parked files served as `text/html` | `A_parked_file_reads_back_as_an_octet_stream_attachment` | "Expected: "application/octet-stream", Actual: "text/html"" |
| 5 | (extra) `Download: false` for parked files | same | "Expected: "attachment; filename=…", Actual: "inline; filename=…"" |
| 6 | (extra) Any host in `Destination` | `DavDestinationTests.Any_other_destination_is_none` | "Expected: null, Actual: DavTarget { Path = /Work/b.md }" |
| 7 | (extra) Lock timeout not capped | `Lock_timeout_is_at_most_what_the_client_asked` | "Expected: "Second-600", Actual: "Second-3600"" |

**Mutation 2 first survived.** Over HTTP, the theory case `.git/config` did not fail, because the store never holds a dot-folder, so a path below a parked folder is parked anyway. Commit `354a77d` added a unit theory for `DavRules.IsParked`, and that theory is what fails above.

### Task 8 (`DavPutTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Decode without throwing | `Put_of_non_utf8_markdown_is_415` | "Expected: UnsupportedMediaType, Actual: Created" |
| 2 | Limit by Content-Length only | `Chunked_put_over_the_limit_is_413_and_stores_nothing` | "Expected: RequestEntityTooLarge, Actual: BadRequest" |
| 3a | Drop DavWrites' structural check | `Board_md_needs_maintain` | **First survived** (NoteWriter refuses `Board.md` itself). After an added assertion (a body over 1 MiB to `Board.md` is 403, rights before body): "Expected: Forbidden, Actual: RequestEntityTooLarge" |
| 3b | Structural check removed in DavWrites and in `ICaller.Refuse` | `Board_md_needs_maintain` | "Expected: Forbidden, Actual: NoContent" |
| 4 | Store an empty PNG as an attachment | `Empty_put_of_a_png_is_parked_and_the_next_put_replaces_it` | "Expected: Created, Actual: UnsupportedMediaType" |
| 5 | (extra) Placeholder not removed after the store write | same | the `Assert.DoesNotContain` failed (the placeholder showed up again) |
| 6 | (extra) Placeholder counted as parked (`itself: true`) | same | "Expected: OK, Actual: NotFound" |
| M5 | (fix round) No retry | the three race tests | "Expected: NoContent, Actual: Conflict" |
| M6 | Retry despite `If-Match` | `A_lost_race_with_if_match_is_412` | "Expected: PreconditionFailed, Actual: NoContent" |
| M7 | Retry on any error | `A_refused_write_is_not_retried` | "Expected: 1, Actual: 2" |
| M8 | No `.MD` resolution in the dispatch | `Put_of_UPPER_MD_is_a_note`, `An_upper_MD_name_over_a_folder_is_405`, `An_upper_MD_name_addresses…` | "Actual: UnsupportedMediaType" |
| M9 | No href echo | `An_upper_MD_name_addresses…` | "Expected: "/dav/Work/Plan.MD" Actual: "/dav/Work/Plan.md"" |
| M10 | Writer's format refusal through the REST mapping | `Put_of_a_png_name_with_text_bytes_is_415`, `A_refused_write_is_not_retried` | "Expected: UnsupportedMediaType, Actual: BadRequest" |
| M11 | Resolution ignoring a name the share sees | `An_upper_MD_name_the_store_has_is_its_own` | **First survived**, because no test had a stored `.MD` name. After adding that test: "Expected: MultiStatus, Actual: NotFound" |

The fix round re-ran mutations 1 to 4 on the new code, and each failed its test again.

### Task 9 (`DavDeleteCopyTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Accept any host | `A_destination_on_another_host_scheme_or_outside_dav_is_502` | "Expected BadGateway, Actual Created" (for `https://evil.example/…` and `http://localhost/…`) |
| 2 | Rewrite links on copy | `Copy_of_a_note_leaves_links_alone` | "Expected "...Ask [[Questions]]" Actual "...Ask [[Archive]]"" |
| 3 | Trash a folder note by note | `Delete_of_a_folder_is_one_batch` | fails at the single-`DeletedAt` assertion (line 111) |
| 4 | (extra) Skip `RecordRemoval` | `Delete_records_the_removal_for_the_share` | fails |
| 5 | (extra) Trash the target before the copy's checks | `A_refused_copy_with_overwrite_t_keeps_the_target` | "Expected Forbidden, Actual NotFound" |

### Task 10 (`DavMoveTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Case 2 as a store move that rewrites and overwrites | `Case2_editor_save_via_tmp_file_is_one_version_and_leaves_links_alone` | "Assert.Single() Failure: The collection was empty" (:159) |
| 2 | Ignore the share id in `RecentRemoval` | `Case3_does_not_restore_another_shares_removal` | "Assert.Empty() Failure: Collection was not empty" (:228) |
| 3 | Ignore the 60 s window | `Case3_after_61_seconds_is_a_new_note` | "Assert.Empty() Failure: Collection was not empty" (:215) |
| 4 | Move instead of hold in case 4 | `Case4_held_note_is_hidden_from_this_share_only` | "Assert.Contains() Failure: Item not found in collection" (:360) |
| 5 | (extra) Holds never expire | `Case4_without_arrival_goes_to_the_trash_after_60_seconds` | "Assert.Null() Failure … Actual: "# v1\n"" (:292) |
| 6 | (extra) No lazy expiry on the share's request | `Case4_a_file_after_60_seconds_is_a_new_note_and_the_old_one_is_in_the_trash` | "Expected: Created, Actual: BadRequest" (:314) |
| 7 | (extra) Case 5 trashes the target in its own batch first | `A_refused_move_with_overwrite_t_keeps_the_target` | "Expected: Forbidden, Actual: NotFound" (:433). **`Case5_…_one_batch` survived**: on a frozen clock two batches look like one |
| R1 | (fix round, ticking clock) Case 5 target in its own batch | `Case5_overwrite_t_trashes_the_target_and_moves_in_one_batch` | "Expected: …48.0140000Z, Actual: …48.0260000Z" (:609); the survivor of mutation 7 is now caught |
| R2 | Case 1 link rewrite in a second batch with its own time | `Case1_rename_rewrites_links_in_one_batch` | "Expected: …58.0300000Z, Actual: …58.0350000Z" (:121). A split that reuses the first batch's note objects still passes: the store records no other per-commit mark |
| R3 | Ending ignores the held version | `Case4_a_note_changed_meanwhile_is_not_trashed` | "Expected "# Owner's\n", Actual: null" (:507) |
| R4 | No put-back of un-ended holds | `An_ending_that_fails_puts_its_holds_back` | "Expected ["a.md~","b.md~"], Actual []" (:551) |
| R5 | TTL never forgets | `Case4_a_hold_past_the_parking_ttl_is_forgotten` | "Assert.Contains() Failure" (:433) |

### Task 11 (`DavPublishedTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Trash a published note after 60 s (the `if (held.Published)` branch removed) | — | **Survived** (10/10). The store itself refuses a share's removal of a published note, the catch records the attempt and the note stays, so the WebDAV flag is defence in depth |
| 1b | The same, plus the store's guard turned off | `Without_arrival_the_published_note_reappears_and_nothing_is_trashed`, `The_owners_activity_records_the_attempt`, and 4 more | "Expected: MultiStatus / Actual: NotFound"; "Actual: "The publication of /Work/Ideas/plan.md was withdra…"" (6 failed) |
| 2 | Allow a move into an unpublishable folder (store guard off) | `Move_of_a_published_note_into_an_unpublishable_folder_is_403_with_the_reason` | "Expected: Forbidden / Actual: Created" |
| 3 | Trash the whole folder (no `keep`) | `Delete_of_a_folder_removes_the_rest_and_keeps_published_notes_and_their_folders` | "Expected: NoContent / Actual: Forbidden" |
| 4 | (extra) Don't keep the folder markers above a kept note | the folder test | "Assert.NotNull() Failure: Value is null" |
| R1 | (fix round) Mutation 1 again | `A_publication_withdrawn_in_the_minute_leaves_the_note_and_records_nothing` | **Now killed**: "Expected: MultiStatus / Actual: NotFound" |
| R2 | No fold | `Repeated_attempts_within_an_hour_are_recorded_once` | "Assert.Single() Failure: The collection contained 3 matching items" |
| R3 | No recording in the `PublishedNoteException` catch | `A_note_published_while_held_is_kept_and_the_attempt_recorded` | "Expected: "“Work PC (dav)” tried to remove …" / Actual: "You published /Work/Ideas/plan.md."" |
| R4 | No `CanWrite` check in `KeptAsync` | `A_share_that_only_reads_at_the_ending_records_nothing` | "Assert.DoesNotContain() Failure: Filter matched in collection" |
| R5 | Trash the whole folder, after the simplification | 8 tests, among them the folder test and `Delete_of_a_published_note_answers_204_and_hides_it` | failed |

### Task 12 (`QuotaApiTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Check the caller's account (`Membership?.Member`) instead of the owner's | `A_member_gets_only_the_neutral_sentence` | "Expected: InsufficientStorage, Actual: Created" |
| 2 | Skip the check in `PutBlobAsync` | `Upload_is_refused_with_507` | "Expected: InsufficientStorage, Actual: Created" (the big file) |
| 3 | Numbers in the member's refusal | `A_member_gets_only_the_neutral_sentence` | Actual `…"This vault is out of space. (1.1 MB of 1.0 MB)"}` |
| 3b | "See `df`." for a member in `sh`/MCP | same | "Expected: This vault is out of space. / Actual: This vault is out of space. See `df`." |
| 4 | Refuse `mv` batches (`AddsData` ignores `IsMove`) | `Rm_mv_dav_delete_and_dav_move_work_over_the_quota` | line 244: "This vault is out of space. See `df`." from `sh mv` |
| R1 | (fix round, each fix undone) No pruning in `Track` | `Ended_background_work_is_let_go` | "5 tasks held" |
| R2a | `FailedLately` dropped | `A_failed_count_or_lookup_waits_ten_minutes` | "Expected 1, Actual 2" |
| R2b | `_lookupFailed` check dropped | same | "Expected 1, Actual 2" |
| R3 | `+ account.Pending` dropped | `A_count_keeps_what_writes_adjusted_while_it_ran` | "Expected 1000, Actual 0" |
| R5 | `MayLimit` always true | `Without_a_default_or_an_override_nothing_is_looked_up` | "Expected 0, Actual 2" |
| R6 | Old PATCH order | `A_patch_with_the_quota_applies_all_or_nothing` | "Expected NoContent, Actual Forbidden" |

**R5's first attempt didn't compile.** `return true;` gave CS0162 (unreachable code, an error under warnings-as-errors), so that run used stale binaries. It was redone with a non-constant condition, the result shown above.

### Task 13 (`EmptyTrashTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Remove `SessionOnlyMetadata` from the route | `An_api_key_a_connection_a_device_and_a_share_are_refused` | "Expected: Forbidden, Actual: BadRequest" |
| 2 | Remove the owner check | `A_member_is_refused` | "Expected: Forbidden, Actual: OK" |
| 3 | Remove `CheckPasswordAsync` | `A_wrong_password_is_refused` | "Expected: BadRequest, Actual: OK" |
| 4 | (extra) No `RequirePasswordWindow` without a password | `Within_the_password_window_no_password_is_needed` | "Expected: Unauthorized, Actual: OK" |

### Task 14 (`DfServerTests`, `DfTests`, `DuTests`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | List shared vaults in df | `A_members_key_sees_only_her_own_vaults_in_df` | "Assert.DoesNotContain() Failure: Sub-string found" (garten) |
| 2 | du smallest first | `Du_lists_children_largest_first_with_a_total`, `Du_dash_d_2_goes_deeper`, `Du_without_a_path_sizes_the_vaults` | failed (3 of 10) |
| 3 | MB without the decimal | `Df_lists_own_vaults_with_totals_and_limit`, `Df_without_a_limit_says_no_limit`, `Df_of_one_vault` | failed |
| R1 | (fix round) Df ignores `AllOwned` | `Df_with_a_key_for_some_vaults_names_the_limit_without_a_percentage` | failed |
| R2 | 30-second reuse dropped | `Two_df_within_30_seconds_count_once` | "Expected: 1 Actual: 2" |

### Task 15 (`Access.test.tsx`)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Drop the `/keys` redirect | "/keys redirects to /access/keys" | "Unable to find role="button" and name "New key"" |
| 2 | Tab from state, not URL | "the tab is in the URL", "the apps tab has the MCP subtitle", "a new connection's address…" | "Unable to find heading "Connections"" (3 failed) |
| 3 | (extra) Never hide the tabs | "a new connection's address has the page to itself…" | "expect(element).not.toBeInTheDocument()" |
| R1 | (fix round) No unmount cleanup in `useReveal` | "leaving in the middle of a reveal…" | "Unable to find role "tablist" and name "Access"" |
| R2 | No `replace: true` on keyboard navigation | "Home and End…" | "expected '/access/dav' to be '/settings'" |

### Task 16 (web)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Show May publish in the share dialog | "NewShareDialog > has no May publish checkbox" | "expect(element).not.toBeInTheDocument()" |
| 2 | Show the password on the row's page | "DavConnect > without the password from the row" | "expect(element).not.toBeInTheDocument()" |
| 3 | (extra) The share's revoke text as the key's | "DavShares > revoke dialog says drives stop working" | failed |
| 4 | (extra) Settings without shares | "… counts a WebDAV share …" | "Unable to find role="list" and name "WebDAV shares"" |
| R1 | (fix round) No `dav-shares` invalidation on re-encryption | "Settings > re-encrypting shows the new recovery key once" | at `toHaveBeenCalledWith({ queryKey: ["dav-shares"] })` |
| R2 | No share counted as ended | "DavShares > tucks revoked and expired shares away, without their buttons" | failed |

### Task 17 (web)

| # | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Empty trash shown to a member | "TrashSheet > offers neither to a member" | "expect(element).not.toBeInTheDocument()" |
| 2 | Warn from 80 % | "StorageNotice > warns the owner from 90 %" | "expect(element).not.toBeInTheDocument()" (the 85 % case showed the notice) |
| 3 | (extra) "Delete permanently" shown to a member | "offers neither to a member" | "Found multiple elements with the role "button" and name /permanently/" |
| 4 | (extra) Member's 507 sentence dropped | "errorText > quota code gets the owner's sentence" | "Expected: This vault is out of space. Received: The server can't be reached right now…" |

### Task 18 (litmus)

| # | Mutation | Result |
|---|---|---|
| 1 | `ProppatchAsync` answers properties with 403 instead of 200 | `run.sh` exit 1: "Unexpected failures: locks/11.owner_modify, locks/26.owner_modify, locks/33.owner_modify, props/26.propmanyns, props/6.propset", and seven `props/…propget` entries "Expected to fail but passed" (props stops after `propset`) |
| 2 | (a scratch copy of the list) A listed failure removed | "Unexpected failures: props/27.propget", exit 1 |
| 3 | (a scratch copy of the list) A bogus entry `basic/3.put_get` added | reported as expected but not failing, exit 1 |
| 4 | Wrong admin password | "POST /api/session failed: {"error":"The username or password is wrong."}", exit 1 |

Task 19 changed only documents, so it has no mutations.

### Security fixes (Task 20)

| Finding | Mutation | Failing test | Output |
|---|---|---|---|
| 1 | Cap check never true | `A_propfind_naming_more_than_the_cap_is_refused_at_either_depth` | "Expected: BadRequest Actual: MultiStatus" |
| 1 | Multistatus buffered, with Content-Length | `A_depth_1_listing_is_streamed` | "Assert.False() Failure" |
| 2 | Target cut at `#` again | `A_raw_hash_in_the_target_or_the_destination_is_refused` | the 204 line |
| 2 | Destination cut at `#` again | `A_destination_with_a_fragment_is_a_400` | "No exception was thrown" ×3 |
| 3 | Both checks off | `Copy_of_a_stored_folder_larger_than_the_parking_cap_is_507_and_parks_nothing` | "Expected: InsufficientStorage Actual: Created" |
| 3 | Plan check off, read guard kept | same | **Survived**. The read guard answers 507 after about 64 MiB, and the test can't tell "refused before reading" from "refused while reading" without a seam that counts reads |
| 4 | Total parking cap `long.MaxValue` | `Parked_bytes_are_capped_across_shares_the_oldest_going_first` | failed |
| 4 | Total lock cap `int.MaxValue` | `Locks_are_capped_across_shares_the_one_expiring_first_going` | failed |
| 4 | `>=` → `>` in the share check | `Shares_beyond_the_limit_are_409_until_one_is_revoked_or_expires` | "Expected: Conflict Actual: Created" |
| 4 | Share count ignoring expiry | same | failed |
| 5 | Back to `FigureAsync` | `Account_storage_counts_at_most_once_in_30_seconds` | "Expected: 1 Actual: 2" |
| 5 | Limit from the cached figure | 2 tests | "Expected: 1048576 Actual: null" |
| 5 | No invalidation | `Emptying_the_trash_shows_in_the_storage_at_once` | failed |
| 7 | Fold against the last Kept entry of all | `Alternating_attempts_on_two_notes_are_recorded_once_per_note` | "Expected: 2 Actual: 4" |
| 9 | Control-character check never true | `Any_other_destination_is_none`, `Move_or_copy_to_a_destination_with_a_control_character_is_502` | the same 9 failures as the red run |
| 10 | `OrdinalIgnoreCase` in the method check | `A_lower_case_method_is_an_unknown_one` | "options: 500" |
| A | `WriteEndDocumentOnClose = true` | `A_listing_cut_short_is_not_well_formed` | "Assert.DoesNotContain() Failure: Sub-string found" |
| B | Writer ranked without the bytes it adds | `A_writer_that_is_the_largest_share_evicts_its_own` | failed |
| B | No ranking (`OrderBy(a => 0)`) | the writer test and `Past_the_total_cap_the_largest_share_loses_its_oldest_file_first` | failed |

### Final fix wave

- **What its report gives.** It records a red run per behavioural item: I1 server and web, I2 MOVE and COPY, M2, M3, M6, M7, M9, and the generator diff for `schema.d.ts`. It lists no separate mutations.
- **One mutation-style check.** The COPY race test was run against the old behaviour (`overwrite: true`) and failed with "Created instead of PreconditionFailed: the note that arrived was trashed".
- **No red run** for M1, M5, M8 and M10: they are refactors, doc comments and comments, shown unchanged by their classes (for example `DavDeleteCopyTests` 31/31, `Dav*` 313/313).

## 6. litmus

**How it ran.**

- **Server.** `8f2a3ae`, Release, `ASPNETCORE_ENVIRONMENT=Development` (plain HTTP on localhost, spec §3.3), a database of its own, search and metrics off.
- **User.** The bootstrap admin's link was redeemed as user `litmus`.
- **`tools/litmus/run.sh`** then signed in, confirmed the password, made a vault `litmus-<epoch>-<pid>` and a share with write on it, `MKCOL`ed `/dav/<vault>/.litmus/`, and ran litmus there.
- **This run** made vault `litmus-1794103251-31353` and share `litmus.vn24`.
- **Why a dot-folder.** litmus's extension-less files then land in the parking area, so litmus tests the protocol layer (spec §7).

**Summaries of the final run** (litmus 0.13):

```
<- summary for `basic': of 16 tests run: 16 passed, 0 failed. 100.0%
<- summary for `copymove': of 13 tests run: 13 passed, 0 failed. 100.0%
<- summary for `props': of 30 tests run: 22 passed, 8 failed. 73.3%
<- summary for `locks': of 41 tests run: 28 passed, 13 failed. 68.3%
litmus: every failure is an expected one (21 listed)
run.sh exit 0
```

**The expected-failure list** (`tools/litmus/expected-failures.txt`), as the final local run used it at `8f2a3ae`. Entries are `suite/number.name`, with litmus 0.13's numbers. Names repeat (props runs `propget` after each step), so the number was part of the key. The list's current form, after `d3e5a2d`, follows the table.

| Entry | litmus's message | Why it is expected |
|---|---|---|
| `props/7.propget` | No value given for property `{http://example.com/neon/litmus/}prop9` | Dead properties are not stored (spec §3.4: PROPPATCH answers 207 with every property set and stores nothing, since Windows and Finder set times and properties of their own, and refusing breaks their copies) |
| `props/10.propget` | same | dead properties are not stored (after a MOVE) |
| `props/12.propget` | same | … (after deleting some) |
| `props/14.propget` | same | … (after replacing some) |
| `props/16.propget` | No value given for property `{}nonamespace` | … (one without a namespace) |
| `props/18.propget` | No value given for property `…}high-unicode` | … (a value outside the BMP) |
| `props/20.propget` | No value given for property `…}removeset` | … (removed and set in one PROPPATCH) |
| `props/27.propget` | No value given for property `{http://example.com/kappa}somename` | … (many namespaces) |
| `locks/9.notowner_modify` | DELETE of locked resource should fail | Locks are not enforced (spec §3.7, fake locking): DELETE of an exclusively locked file succeeds |
| `locks/10.notowner_lock` | LOCK on locked resource | locks are not enforced: a second exclusive LOCK succeeds |
| `locks/12.notowner_modify` | DELETE of locked resource should fail | … (after the owner wrote) |
| `locks/13.notowner_lock` | LOCK on locked resource | … (after the owner wrote) |
| `locks/24.notowner_modify` | DELETE of locked resource should fail | … a file with a shared lock |
| `locks/25.notowner_lock` | LOCK on locked resource | … an exclusive LOCK over a shared one |
| `locks/28.notowner_modify` | DELETE of locked resource should fail | … (two shared locks) |
| `locks/29.notowner_lock` | LOCK on locked resource | … (two shared locks) |
| `locks/34.notowner_modify` | DELETE of locked resource should fail | … DELETE in a locked folder |
| `locks/16.fail_cond_put` | conditional PUT with invalid lock-token should fail: 204 No Content | `If` headers with lock tokens are accepted and ignored (spec §3.7): an unknown token |
| `locks/18.cond_put_corrupt_token` | same | … a malformed token |
| `locks/20.fail_complex_cond_put` | PUT with complex bogus conditional should fail with 412: 204 No Content | … an `If` with only false conditions |
| `locks/22.fail_cond_put_unlocked` | conditional PUT with invalid lock-token should fail: 204 No Content | … the token of an ended lock |

**Not on the list.** The brief's examples "`Depth: infinity` refused" and "cross-collection behaviours refused" are absent. These four suites never hit them (copymove passes whole; props uses Depth 0 and 1), and the list must be exact.

**After the PR's CI run: by name and count, `d3e5a2d`.**

- The runner's litmus is **0.17**, which numbers the props and locks tests one apart from 0.13. So every `suite/number.name` entry mismatched, although the same tests failed.
- Commit `d3e5a2d` "Compare litmus failures by name, not by number" changed the list to `suite/name times`, the test and how often it fails in a run. The 21 failures above are the same, now as 7 entries:

```
props/propget 8                 # dead properties are not stored
locks/notowner_modify 5         # locks are not enforced: DELETE of a locked resource succeeds
locks/notowner_lock 4           # locks are not enforced: a LOCK over a lock succeeds
locks/fail_cond_put 1           # If headers are ignored: a PUT with an unknown lock token succeeds
locks/cond_put_corrupt_token 1  # If headers are ignored: a PUT with a malformed lock token succeeds
locks/fail_complex_cond_put 1   # If headers are ignored: a PUT whose If lists only false conditions succeeds
locks/fail_cond_put_unlocked 1  # If headers are ignored: a PUT with the token of an ended lock succeeds
```

- `run.sh` still prints "every failure is an expected one (21 listed)", because it sums the counts.
- The run in this section is the local one before that change. The CI run of `d3e5a2d` is the PR's to show.

**How `run.sh` judged at `8f2a3ae`** (`set -euo pipefail`):

- It parses each FAIL as `suite/number.name`, attributing it to the last test named on the line.
- It compares the failures with the list (`comm`), both ways:
  - A failure **not on the list** is printed under "Unexpected failures:" and the script exits 1.
  - A listed test that **did not fail** is printed under "Expected to fail but did not (passed, or skipped after an earlier failure; take a fixed one off …)" and the script also exits 1, so the list stays exact and a fix shows as clearly as a regression.
- A suite without a `<- summary` line fails the run too.
- On success it prints "litmus: every failure is an expected one (21 listed)".
- **Since `d3e5a2d`** it counts the failures per `suite/name` and compares them with the listed counts. An unlisted failure, or a test failing more often than listed, is a regression. A listed test failing less often, or not at all, is a fix, or a suite cut short by an earlier failure. Either way it prints "litmus's failures (test, times) differ from …" with `now:` and `expected:` lines and exits 1.

**The `delete_fragment` warning and its fix.**

- **Before.** Task 18's first runs passed `basic/9.delete_fragment` with "WARNING: DELETE removed collection resource with Request-URI including fragment; unsafe".
- **Cause.** Kestrel keeps a raw `#` in the request target, and `DavPaths` cut the raw target at `#`. So `DELETE /dav/Work/frag/#ment` acted on `/dav/Work/frag/` and trashed the folder (security finding 2).
- **Fix.** `ecd9cf8` refuses a raw `#` in the target or in `Destination` with 400 "A WebDAV URL has no fragment: a '#' in a name is sent as %23.".
- **After.** In the final run, `9. delete_fragment ... pass` has no warning, and `basic` issued no warning at all.

The CI job (`litmus` in `.github/workflows/ci.yml`) was simulated locally step by step at Task 18, except `apt-get install` and `docker run mongo` on the runner. Its first real run, the pull request's, met litmus 0.17 and led to `d3e5a2d` (above).

## 7. The security review

A fresh subagent on the most capable model reviewed the branch at `a26c9e9` (Task 19's documents were in flight), read-only. Its scope was `git diff master...HEAD` (38 commits, about 150 files), against spec §2's anti-goals, §3.2–§3.3, §3.8, §3.10–§3.11 and §5, the brief's open points 1–5 and its security boundary. It probed two claims in a scratch directory outside the repository: the Kestrel `#` behaviour and the PROPFIND amplification. A third probe, XML nesting at the 64 KB cap (about 9,000 levels), came out clean.

| # | Severity | Finding | Outcome |
|---|---|---|---|
| 1 | **High** | A PROPFIND naming about 11,000 properties at Depth 1, multiplied by a folder's children, built the whole multistatus in memory. In the reviewer's simulation, 200 resources took about 220 MiB of heap and 33 MiB of body, growing linearly. A read-only share could take the process down for everyone | **Fixed** `6429514`: at most 64 property names (400 above), and the multistatus is streamed one `response` at a time. Follow-up `d4d16f2`: a multistatus cut short is never well-formed |
| 2 | Medium | A raw `#` in the target or `Destination` made a method act on the parent folder (`DELETE …/frag/#ment` trashed `frag/`; litmus's warning) | **Fixed** `ecd9cf8`: 400 for `#` in either |
| 3 | Medium | A COPY of a stored folder into a parked name read the whole folder into memory before the 64 MiB cap | **Fixed** `aa90733`: the copy is planned from the listing first, and 507 comes before any byte is read; there is also a running guard while reading |
| 4 | Medium | No bound across shares on parked bytes and locks, and no limit on shares per user | **Fixed** `15617b1`: 512 MiB parked and 4096 locks server-wide, and 20 active shares per user (409). Follow-up `1f97a1d`: the share holding the most gives way first. Not done (outside the brief): holds keyed by share, and a semaphore for buffered bodies |
| 5 | Medium | `GET /api/account/storage` ran the full aggregation on every call, and the shell called it on every focus | **Fixed** `83a3fb5`: served from the 30-second figure. The final review's I1 then went further (below) |
| 6 | Low | A member could bisect write sizes to learn the owner's remaining space beyond "full" | **Accepted**: spec §3.10 already accepts that a member learns "full"; the channel is bounded by write sizes (1 MiB / 15 MB), and each probe costs the owner space. Documented in the PR (decision 20) and here |
| 7 | Low | The "kept" fold could be bypassed by alternating notes, pushing real entries out of the 500-entry publishing log | **Fixed** `c512570`: folded per note and writer |
| 8 | Low | In-flight Basic checks hold the IP's wrong-key permits, so more than 20 concurrent requests from one address (Explorer, rclone `--transfers`, a Nextcloud host) get 429 | **Accepted, not fixed**: fixing it would change `WrongKeyLimiter`, which `/mcp` shares and the brief puts out of bounds. Documented in the PR (decision 20); the README says nothing about it yet (an open point, §9) |
| 9 | Info | `Destination` could carry NUL and other control characters into parking and holds | **Fixed** `48ebee4`: control characters are refused in targets (400) and in `Destination` (502) |
| 10 | Info | Development corners: Basic over plain HTTP in Development, where the developer exception page shows `Authorization`; a password sent over plain HTTP in production is already exposed before the 403; lower-case `options` ended in a 500 | **Lower-case method fixed** `028f17e` (405, as an unknown method). The other two points are open points (§9) |

**Re-review of the fixes.** All 8 ADDRESSED, and it asked for two follow-ups:

- `WriteEndDocumentOnClose = false` → `d4d16f2`;
- fair eviction across shares → `1f97a1d`.

**The review checked and found sound:**

- authentication on `/dav` and its separation from `/mcp` and `/api`;
- tokens, slots and derivations (the API-key derivation byte-identical to `master`), and "May publish" forced off;
- revocation on every path, and no secret in any log or URL;
- path parsing, the XML settings, and content on the app's origin (sandbox CSP, `nosniff`, octet-stream attachments);
- parking isolation, published notes, quota coverage and member wording;
- emptying the trash, CSRF on the new endpoints, and that sessions, OAuth, pairing and the encryption scheme are untouched.

### The final review's findings

| Item | Finding | Outcome |
|---|---|---|
| I1 (Important) | Every signed-in user's shell ran the full storage count, also on a server without a quota, which breaks rule 1 | **Fixed** `ca20ba5`, `34e27d9`. The session says whether a limit applies (`storageLimited`). The notice asks only then, at most once a minute, not on focus. A server without a default or override reads nothing for it (`StorageQuota.LimitedAsync`) |
| I2 (Important) | A COPY or MOVE onto an empty placeholder parked or held a real file and answered 201/204: a "rename" could end in the trash | **Fixed** `5921a01`: the destination is judged as `PUT` judges it, the file is stored and the placeholder removed |
| I3 (Important) | No full gate or litmus run since Gate 5 | **Done**: Gate 6b, the port fix, Gate 7 green, litmus at `8f2a3ae` (§4, §6) |
| M1 | `DavHolds.RecentRemoval` used only by a test | Removed, `eaf4bc2` |
| M2 | Kestrel's 30 MB default made the parking cap about 28.6 MiB per file | `32f5119`: a request size limit of the parking cap + 64 KiB on `/dav` |
| M3 | The member panel showed "· share" | `8af65f6`: "WebDAV share" |
| M4 | COPY always passed `overwrite: true` (the Overwrite:F race) | `5921a01`: `overwrite: replaced`; a late arrival gets 412, nothing trashed |
| M5 | Misplaced doc comments on `AttachmentText.Size` | `6e68710` |
| M6 | `PROPFIND /` answered 405 with WebDAV off | `45f9d49`: mapped only while it is on |
| M7 | `VaultReadOnlyException` during a write fell through to 500 | `93042e4`: 403 with its sentence |
| M8 | Duplicated refusal texts and parent-path code | `113411c` |
| M9 | Publishing refusals said "this key" to a share and pointed to the old tab names | `b1a9209`: a share's own wording, and "Access → API keys or Apps" |
| M10 | Two Depth parsers easy to confuse | `ffe1fb2`: comments |
| Deferred must-fix | `schema.d.ts` against the generator | `bb5c253`: the generator's output. Two differences were found: an order, and `withdrawnInvitations`/`revokedShares` required with `@default 0` |
| Deferred must-fix | `RecentRemoval`, the doc comments, the placeholder COPY, the Overwrite:F race | as M1, M5, I2, M4 |

The fix wave's re-review: all ADDRESSED, no new Critical or Important. Two residual minors are in §9.

## 8. Decisions made without Chris

The rules, in order: breaks nothing > the app's patterns > no new security hole > the spec. These are the 20 in the pull request's body. Where the body names no alternative, the one weighed is given.

1. **Share slots carry their id in a new `share` field**, not in `apiKey`.
   - Options: reuse `apiKey` with kind `dav`, or a field of its own.
   - Why: no existing API-key query can match a share by accident (it fails closed), and every place a member's key is handled got an explicit share branch.
   - Cost: more code at each place.
2. **A share's writes carry the writer kind `agent`** (not `device`), with a person's changeset.
   - Options: `device` (as Obsidian's) or `agent`.
   - Why: whoever holds the password can use a share, so its writes stay visible as not the user's own hand. That is less for a share.
   - Cost: the agent banner also mentions a share's writes.
3. **Re-keying revokes the owner's shares with a slot on the vault whole**, as it revokes her API keys, rather than taking only that vault. A member's share loses only that vault, and is revoked if none is left.
   - Why: the spec's "as it removes API-key slots", and the existing ruling for keys.
   - Cost: the user makes the share again.
4. **Share labels are unique among the user's own shares only**, not across keys and connections.
   - Options: one namespace (that would change key and connection creation) or a namespace of their own.
   - Why: a share's history label is always marked `(dav)`.
   - Cost: a list may show a key "Work PC" and a share "Work PC".
5. **A valid token with another share's username counts as a wrong token.** Only a revoked or expired share, or an inactive user, with matching token *and* username gives the permit back.
   - Why: spec §3.2 says "refused like a wrong token".
6. **A parking write into a read-only vault gets 403.**
   - Options: let junk through, since it is never stored, or refuse.
   - Why: less for a share.
7. **The quota never refuses a publication write**, since it copies what is already stored, but the write counts in usage.
   - The alternative, refusing them when full like any other write, was not taken, for that reason.
8. **The realm stays `Kumbu²`**, sent as Latin-1 through a Kestrel response-header encoding selector, and a test pins that every other header keeps its ASCII check.
   - Options: an ASCII realm `Kumbu` (the fallback), UTF-8 (clients would show `KumbuÂ²`), or Latin-1.
   - Cost: it relies on Kestrel asking with an empty header name for known headers (undocumented), and HTTP/2 is untested.
9. **The HTTP metrics' method guard admits the seven WebDAV methods** app-wide.
   - Why: without it nothing WebDAV works. The label stays bounded, and everything else still needs a caller.
10. **`.MD` is a note stored as `.md`, and resolved to it on every method** (`DavTree.ResolveAsync`).
    - Options: store verbatim (needs a system-wide `IsNote` change, out of scope); refuse a non-lowercase `.md` (contradicts open point 6); resolve as an alias (chosen).
    - Cost: a case-sensitive syncer (rclone) may see `Plan.md` against a local `Plan.MD`. The README says so.
11. **Core's NoteWriter marks "note already exists" as a conflict** (`Kind = Conflict`), because the WebDAV retry needs to tell a lost race from a refusal.
    - MCP answers are unchanged, but the metrics move: `kumbu_mcp_tool_errors_total{tool="write"}` counts these as `conflict` instead of `other`, and `kumbu_write_conflicts_total{via="mcp"}` now counts them, as the README defines it and REST already did.
    - Cost: a step in a dashboard trend.
12. **A held note ends lazily, on the same share's next request, with that request's rights.** No credential, token or caller is kept in memory, and a hold not ended within the parking time (10 minutes) is forgotten, so the note stays.
    - Options: a timer with the cached credential (rights could outlive revocation or a membership); a timer re-opening it (the token would stay in memory); lazy (chosen: rule 3 over rule 4).
    - **This departs from spec §3.6 case 4's "after 60 seconds, the note goes to the trash"**: a note a client "deleted" by renaming it to a backup name, with the drive never touched again, stays in place.
13. **A published note's attempted removal is recorded in the owner's activity at that lazy ending.** A forgotten hold records nothing.
    - If the owner withdraws the publication during the minute, the note is still not trashed, and nothing is recorded (less for a share; spec silent).
    - Repeated attempts on the same note by the same writer are folded within an hour (and since `c512570` per note), so a share can't push real publications out of the 500-entry publishing log.
14. **The welcome vault is exempt from the quota**, like the seeder: it is sample content the server writes at sign-up.
    - Cost: with a quota of 0, a new account still gets the welcome vault.
15. **The 507 body carries no limit.** The owner's web app reads the limit from `GET /api/account/storage`, which keeps the refusal's surface minimal.
16. **`df` for a key that opens only some of its user's vaults** shows the rows' total without a percentage, and says the other vaults count against the limit too.
    - Options: the account's total (it would reveal the other vaults' size); the rows' total with a percentage (misleading); chosen.
    - Cost: one bit, that other vaults exist.
17. **The RFC 4331 quota properties on `/dav/` are account-wide for every share**, on the root and on owned vaults (spec §3.4), consistent with the 507.
    - As built they appear only when a PROPFIND names them, never in `allprop` (as the RFC says), and only while a limit applies (Task 19 noted this, §9).
18. **`df` and the storage endpoint count at most every 30 seconds per owner**, and concurrent counts coalesce. The session carries a flag saying whether a limit applies, so on a server without a quota the web app counts nothing.
19. **New limits chosen by the run**, each a constant with a comment:
    - at most 64 properties per PROPFIND (400 beyond);
    - 10,000 parked entries per share (each charged 512 bytes on top of its size);
    - 512 MiB parked and 4096 locks server-wide, the largest share giving way first;
    - 20 active shares per user (409 beyond);
    - lock owners up to 4096 characters (a longer one is left out of `lockdiscovery`);
    - 256 locks per share.
20. **Security findings accepted, not fixed.**
    - A member can bisect write sizes to estimate the owner's remaining space (finding 6). The spec already accepts that a member learns "full".
    - Many parallel Basic requests from one address can use up the wrong-key limiter's in-flight permits and get 429s (finding 8). Fixing it would change `WrongKeyLimiter`, which `/mcp` shares and the brief puts out of bounds.

**Other rulings from the ledger, not in the list above:**

- **`DavDestination` was moved forward from Task 9 to Task 7.** Parked COPY and MOVE need it.
- **An unknown method (`BREW`) gets the HTTP metrics' bare 405**, without `Allow` and `Cache-Control: private`, because that pre-existing guard answers before the endpoints. It was accepted rather than changing `HttpMetrics` further. With metrics off, `/dav`'s own 405 with `Allow` answers.
- **The security review's triage.** Findings 1–5, 7 and 9 and the lower-case-method 500 were fixed now, each with a failing test. Finding 6 is a residual, and finding 8 is documented, not fixed (decision 20).
- **Task 18 had one combined review** (spec and quality), because its diff was small and outside the product code. Task 19, the documents, was reviewed the same way. This departs from "two reviewers per task".
- **Calls made by implementers**, recorded in their reports and reviewed:
  - **Task 2.** `Known` is false on success, so the middleware looks at `Share` first. The share label's name hash has its own HKDF label, `kumbu/v1/dav-share-name-id`, so a share and a key named alike don't carry equal hashes in two collections. Shares are also revoked by "Re-encrypt my vaults". The creation transaction conflicts on a new `sharesCreated` counter.
  - **Task 6.** PROPPATCH reports every property 200, protected live ones included, as spec §3.4 says, rather than RFC 4918's 403. A read share may PROPPATCH, since it stores nothing.
  - **Task 7.** A LOCK on an unmapped parked name parks an empty file (201); on a store name it creates nothing (200). LOCK needs write.
  - **Task 8.**
    - An empty `PUT` over a stored attachment writes nothing and answers 204 with the stored ETag.
    - The changeset test uses `ChangesetIdleTests`' pattern, because the brief's "90 s apart are two changesets" is wrong for `HumanIdle` = 2 minutes.
  - **Task 9.** A parked folder copied onto a real path gets 403 ("copy its files one at a time").
  - **Task 12.** The quota check is synchronous and reads the cache only (open point 8). Only the primary admin may set the primary admin's quota.
  - **Task 13.** The purge route checks the password inline (the role change's pattern), not with `PasswordWindowMetadata`, so a given password is always checked. Unknown entries are skipped.

## 9. Deviations, surprises, tasks left, open points

### Deviations from plan and spec

- **The hold ending is lazy** (decision 12): spec §3.6 case 4 says "after 60 seconds".
- **RFC 4331 properties** appear only when named and only while a limit applies. Spec §3.4 lists them without those conditions.
- **Parking.**
  - The patterns include vim's `4913` and emacs' `#*#`, beyond spec §3.8's list (open point 7 asked for them).
  - The spec's "64 MB per share" is 64 MiB, plus a cap of 10,000 entries per share and 512 MiB server-wide.
- **Until the first count after a start,** writes pass unchecked; after a failed count, they pass unchecked for up to 10 minutes. Open point 8 allows it; spec §3.10 says "computed on first need".
- **Account deletion deletes `dav_shares` documents** and their slots rather than marking them revoked, as for keys.
- **Files outside the plan's lists**, each recorded as a decision by its implementer:
  - `HttpMetrics.cs` (the method guard);
  - `FileApi.OpenAsync` extracted;
  - a new Core `TreeWriter`;
  - `NoteMover.MoveOverAsync`;
  - `ConsoleApi`, `KumbuMetrics` and the web console's `paths.ts` for `df`/`du`;
  - `concept.md` §13 (it said WebDAV was "not planned").
- **`schema.d.ts`** was edited by hand, as the brief asked, and at the end compared with the generator and made identical to its output (`bb5c253`).
- **Two tasks had one combined review**, Tasks 18 and 19.
- **Task 0 took far longer than the brief's 15 minutes**: two void runs (§3).

### Surprises and incidents

- **The detached HEAD.**
  - What happened: Task 5's fix-round implementer checked out `7bf94d0` and committed `7ccb511` on a detached HEAD.
  - The fix: the controller fast-forwarded `feature/webdav` to `7ccb511`, so nothing was lost.
  - After that: every later brief said again never to check out a commit (`git show <sha>:<path>` instead). Task 10's fix round used `git archive 60d587e | tar -x` into the scratchpad to see its red run against the old code.
- **Lost edits during mutations.** `git checkout -- <file>` discards uncommitted work.
  - **Task 5's fix round** wiped its uncommitted edits to `DavEndpoints.cs` once. It re-applied them, checked the diff, and from then on restored mutated files from a copy.
  - **Task 8's fix round** lost its uncommitted edits to `DavWrites.cs` the same way, when its mutation script ran `git checkout --`. It re-applied the same edit script, committed, and only then ran the mutations. The green run before the commit confirmed nothing else was lost.
- **Port 5000 clashes**, three times:
  - Task 0's second run (two overlapping runs, `DotSegmentTests`);
  - Task 4's Kestrel test (moved to port 0);
  - Gate 6b, where the new 30 MB test bound port 5000 beside a parallel Kestrel test (fixed `8f2a3ae`).
- **mongod**: `EMFILE` on the first run, solved with `close_idle_time` and a file limit of 20000 (higher refused).
- **Flaky tests.**
  - `AttachmentMetricsTests.Turned_pages_count_for_pdf` failed once while two runs shared mongod.
  - `AccountApiTests.Recovery_revokes_every_api_key_and_replaces_the_used_recovery_key` failed once in Task 12's focused rerun, and in the void baseline run 2; it is known as flaky from earlier runs.
- **HTTP metrics.** The metrics middleware answered 405 to every WebDAV method before routing (decision 9).
- **Kestrel 10** wrote the realm `Kumbu²` as `Kumbu?` (decision 8).
- **Over HTTP, the dot-folder rule can't fail** (Task 7, mutation 2), because the store refuses dot names anyway.
- **The final fix agent stopped its local server with `pkill -f "Kumbu.Server"`**, a pattern that could also hit a test run. None was running then, and Gate 6b/7 ran afterwards.
- **Specs outside this topic.** `docs/security-architecture.md`'s front matter still says version 0.2.0. Task 19 added "After 0.3.2 …" rather than change the version line.

### Tasks left

None. Every task of the plan, 0 to 21, is done; the PR is a draft and nothing is merged, tagged or deployed.

### Open points for Chris

1. **Decision 12**: held notes are never trashed without a later request from the share. It departs from spec §3.6 case 4.
2. **Decision 8**: the realm's Latin-1 encoding relies on undocumented Kestrel behaviour, and HTTP/2 is untested. Fallback: realm `Kumbu`.
3. **Decision 10**: `.MD` names and case-sensitive syncers.
4. **Decision 11**: the MCP metrics shift for write conflicts.
5. **Decision 14**: with a quota of 0, a new account still gets the welcome vault.
6. **Decision 17 as built**: RFC 4331 properties only when named and only with a limit.
7. **Security finding 8**: a Nextcloud host mounting many users' shares from one address can get 429s. The README doesn't say so yet; a sentence under Nextcloud would.
8. **Security finding 10's other two points**:
   - Basic in Development, where the developer exception page shows `Authorization`; make sure no image or compose file sets `Development`.
   - A password sent over plain HTTP in production is already on the wire before the 403; HSTS is the remedy, perhaps with a hint in the WebDAV tab.
9. **The hand test** in §10 has not been run, since no client was available here. Task 17's screens were not tried in a browser either.
10. **The litmus CI job.** Its first run on the PR met litmus 0.17, whose test numbers differ from 0.13's. `d3e5a2d` now compares by name and count. Check that the PR's `litmus` check is green on that commit; this report has no run of the changed script.
11. **The residual minors from the final fix wave's re-review**:
    - `PublishCommandTests`' stub sentence is stale after M9;
    - a failed overrides check is waited on again.
12. **Deferred minors the final review triaged as "can wait"** (the "must fix" ones are done, §7):
    - **Task 1:** `RightSlotKind` branches on `Dav` in three places; 4-character random ids in `DavSlotTests`; the session overload of `DeleteDavSlotsAsync` untested.
    - **Task 2:** copied `Milliseconds`, writer-context and lastUsed throttle; duplicate-id detection by message text; four `RevokeAllAsync` overloads; sweep isolation and concurrency untested; `RevokeAsync` without retry (mirrors ApiKeys); a writer seal that fails to open counts against the limiter (fails closed).
    - **Task 3:** `RevokedShares` init vs positional; `CredentialOf` called twice; no test that an expired share is left out of the preview.
    - **Task 5:** listing performance on big vaults (each child note decrypted for its length); a vault being re-keyed is listed, then 503; NFC of vault names at storage unverified.
    - **Task 6:** duplicated `ContentAsync`/`FindAsync` lookups; `Download: true` untested over HTTP; `Length` from the stored size; `CollectionMethods` via `Except`.
    - **Task 7:** check-then-act races in parking (junk only); redundant store queries; a parked PUT buffered twice; scans under the global parking lock (now bounded per share and in total).
    - **Task 8:** attachments buffered up to 15 MB (as `/api/files`); `MKCOL Notes.MD` resolves to `Notes.md`; `.MD` not tested directly on DELETE, COPY and MOVE.
    - **Task 9:** `TreeWriter` and `NoteMover` checks mirrored; no Core `TreeWriter` tests; an orphan marker blob on conflict; a parked target lost when a copy-into-parking put fails.
    - **Task 10:** `Hidden` builds the whole set per lookup; a put-back `TryAdd` may lose a hold (the note stays); case 3 is two writes; a stored note MOVEd onto a held original gets 400.
    - **Task 11:** a published note's embedded image can be trashed, as on `/api` and `sh`.
    - **Task 12:** N parallel uploads may overshoot by N writes (spec-accepted); `FigureAsync` doesn't clear `FailedAt`; the restart and failure windows.
    - **Task 13:** the password is checked before the owner check (rate-limited, her own password); the inline password check is duplicated; the trash is read from memory (fails safe).
    - **Task 14:** the `_shown` cache never evicts (bounded by the number of users).
    - **Task 16:** `RevokeDialog`'s union props; `DavConnect` close to `KeyReveal`; `lostVaults` shown as a count; the 90-day default expiry.
    - **Task 17:** two size formatters (the trash shows 1536.0 MB for 1.5 GB); the bar rounds while the notice floors; a misplaced JSDoc; a per-entry aria-label that doesn't contain the visible text (WCAG 2.5.3); the `known` cache; a stale notice after writes; `QuotaControl` resync; `PurgeDialog`'s count; the password kept after a failed attempt; test gaps.
    - **Task 18:** `run.sh` with an empty list under pipefail (the list isn't empty); the share password in litmus' argv on a throwaway localhost share; no MongoDB log on failure; litmus unpinned (fails loudly).
    - **Task 20:** a folder delete with N published notes writes N kept entries an hour; the Depth-1 listing holds the entries' metadata.
13. **Task 3**: a member who re-encrypts her account loses her shares on vaults shared with her too, as with keys.
14. **The Obsidian plugin** (another repository) must treat 507 as final for that file and tell the user, per the open item Task 19 added.

## 10. The hand-test checklist (spec §7)

Not run yet. These are the steps for Chris, against a server at `kumbu.example.org` running this branch over https.

### Preparation, once

1. **Make the share.** In the web app, as Alice, open **Access → WebDAV → New share**:
   - Label "Hand test", `Work` with `write`, expiry 30 days. Confirm the password.
   - The connect page shows `https://kumbu.example.org/dav/`, a username of the form `alice.k7qm`, and the 52-character password. Copy the password into the password manager.
   - For each client below, make a share of its own: "Hand test Dolphin", "Hand test Finder" and so on. Then a mistake in one doesn't spoil the next, and each history entry names its client.
2. **Fill `Work`.** It holds `Ideas/Routing.md`, containing `Ask [[Questions]]`, and `Ideas/Questions.md`, containing a line of text.
3. **Prepare local files:**
   - `diagram.png`, a small picture;
   - `Inbox.md`, a short UTF-8 note;
   - `project/`, holding `README.md`, with `git init && git add . && git commit -m x` run in it, so it has a `.git/`;
   - `scan.pdf` of about 20 MB (over the 15 MB limit, under Windows' 50 MB);
   - `budget.xlsx`, a spreadsheet.
4. **Keep three places of the web app open beside the client:**
   - the **tree** of `Work`;
   - the **history** of the note in question (the note's menu → History);
   - the **Trash** and the **Activity** sheets.
   - Also keep the **console** for `df -h`.
5. **For the full-quota step:** as admin, **Admin → Users →** Alice's row: the quota control, "A size of its own", **0** GB, **Set**. 0 means no write that adds data, which is not the same as unlimited. After the step, set it back to **Default**.

**Passes everywhere:**

- The client never shows a vault the share doesn't have.
- Nothing the client leaves behind (`.DS_Store`, `._*`, `Thumbs.db`, `desktop.ini`, `.~lock.*#`, `*.swp`, `*~`, `.goutputstream-*`, `.git/`) ever appears in the web app's tree, search, history or `df`.
- Every write the client makes shows in the history as "Hand test … (dav)".

### KDE Dolphin

| # | Step | Where to look | Passes when |
|---|---|---|---|
| 1 | Mount: type `webdavs://alice.k7qm@kumbu.example.org/dav/` into the location bar and give the password (KWallet may keep it) | Dolphin; Access → WebDAV, the share's row | One folder `Work`; the row's "Last used" is now |
| 2 | List: open `Work/Ideas` | Dolphin | `Routing.md` and `Questions.md`, no folder markers, times are the last writes |
| 3 | Drag `diagram.png` and `Inbox.md` from a local folder into `Work/Ideas` | tree, gallery, `Inbox.md`'s history | Both are there. `Inbox.md` opens with its text; its history has one entry by "Hand test Dolphin (dav)". The picture shows in the gallery |
| 4 | Edit in place: open `Questions.md` in Kate from Dolphin, change a line, save, close | `Questions.md`'s history; `Routing.md`; tree | Exactly one new version by the share. `Routing.md` unchanged. No swap or backup file in the tree |
| 5 | Rename `Questions.md` to `Open questions.md` (F2) | `Routing.md`; `Open questions.md`'s history | `Routing.md` now says `[[Open questions]]`; the move and the rewrite are one batch in the history |
| 6 | Delete `Open questions.md` (Dolphin deletes remote files directly; confirm) | Trash | It is in the Trash with its history; **Restore** brings it back |
| 7 | Drag the folder `project/` into `Work` | tree; Dolphin; `df -h` in the console | Tree: `project/README.md`, no `.git`. Dolphin (this share) may show `.git` for up to 10 minutes. `df`'s note count rises by one only |
| 8 | Drag `scan.pdf` into `Work/Ideas` | Dolphin's error; tree | Dolphin reports an error (413); no `scan.pdf` in the tree |
| 9 | Drag `budget.xlsx` into `Work/Ideas` | Dolphin's error; tree | Error (415, "Kumbu² keeps notes, PDFs and images (PNG, JPEG, GIF, WebP) only"); nothing in the tree |
| 10 | Full quota: with Alice's quota set to 0, drag another PNG in, then rename and delete a note | Dolphin; the web app's shell notice; Settings; `df -h` | The PNG fails (507 "This vault is out of space."). Rename and delete still work. The shell says "Your storage is full (…)". Settings shows the storage of the limit; `df` shows 100 %. Set the quota back to Default |

### GNOME Files

| # | Step | Where to look | Passes when |
|---|---|---|---|
| 1 | Mount: **Other Locations →** "Connect to Server" `davs://alice.k7qm@kumbu.example.org/dav/`, the password ("Remember forever" puts it in the keyring) | Files; Access → WebDAV row | `Work` shown; "Last used" now |
| 2 | List `Work/Ideas` | Files | The two notes, no folder markers |
| 3 | Drag `diagram.png` and `Inbox.md` in | tree, gallery, history | Both stored; history by "Hand test GNOME (dav)" |
| 4 | Edit in place: open `Questions.md` with Text Editor from Files, change, save. It writes `.goutputstream-*` and renames it over the note | `Questions.md`'s history; `Routing.md`; tree | One new version, links untouched, no `.goutputstream-*` anywhere in the web app |
| 5 | Rename to `Open questions.md` (F2) | `Routing.md`; history | Link rewritten, one batch |
| 6 | Delete it (Delete, confirm "permanently": the remote has no local trash) | Trash | In Kumbu²'s Trash; Restore works |
| 7 | Drag `project/` in | tree; Files; `df -h` | `project/README.md` only; `.git` for this share only, gone after 10 minutes |
| 8 | Drag `scan.pdf` in | Files' error; tree | Error (413); nothing stored |
| 9 | Drag `budget.xlsx` in | Files' error; tree | Error (415); nothing stored |
| 10 | Full quota (0): drag a PNG in; rename and delete a note | Files; shell notice; `df -h` | The PNG fails (507); rename and delete work; the notice and `df` say full. Reset the quota |

### macOS Finder

| # | Step | Where to look | Passes when |
|---|---|---|---|
| 1 | Mount: **Go → Connect to Server** (⌘K) `https://kumbu.example.org/dav/`, "Registered User", `alice.k7qm`, the password, "Remember this password in my keychain" | Finder; Access → WebDAV row | `Work` shown; "Last used" now |
| 2 | List `Work/Ideas` | Finder | The two notes. Finder writes `.DS_Store` and `._*`, and they appear in no web app view |
| 3 | Drag `diagram.png` and `Inbox.md` in. Finder writes an empty file first, then LOCK, then the content | tree, gallery, history | Both stored once; one history entry each; no empty placeholder left in the tree |
| 4 | Edit in place: open `Questions.md` in TextEdit (Format → Make Plain Text), change, ⌘S | history; `Routing.md` | One new version; links untouched; no `.sb-*`/temp names in the tree |
| 5 | Rename to `Open questions.md` (Return on the name) | `Routing.md`; history | Link rewritten, one batch |
| 6 | Delete it (⌘⌫, confirm "deleted immediately") | Trash | In Kumbu²'s Trash; Restore works |
| 7 | Drag `project/` in | tree; `df -h` | `project/README.md` only; no `.git`, `.DS_Store` or `._*` in the web app |
| 8 | Drag `scan.pdf` in | Finder's error; tree | Finder reports an error; nothing stored. Finder's empty `scan.pdf` placeholder may show for this share until it is deleted or 10 minutes pass |
| 9 | Drag `budget.xlsx` in | Finder's error; tree | Error; nothing stored |
| 10 | Full quota (0): drag a PNG in; rename and delete a note | Finder; shell notice; `df -h` | The PNG fails (507); rename and delete work; the notice and `df` say full. Reset the quota |

### Windows Explorer

| # | Step | Where to look | Passes when |
|---|---|---|---|
| 1 | Mount: **This PC → Map network drive**, folder `https://kumbu.example.org/dav/`, "Connect using different credentials", `alice.k7qm` and the password. Or in a terminal: `net use K: https://kumbu.example.org/dav/ /user:alice.k7qm *` | Explorer; Access → WebDAV row | `K:` shows `Work`; "Last used" now |
| 2 | List `K:\Work\Ideas` | Explorer | The two notes; `desktop.ini` and `Thumbs.db` appear in no web app view |
| 3 | Drag `diagram.png` and `Inbox.md` in (Explorer: empty file, LOCK, PUT) | tree, gallery, history | Both stored once; no placeholder left |
| 4 | Edit in place: open `Questions.md` in Notepad from `K:`, change, Ctrl+S | history; `Routing.md` | One new version; links untouched. Explorer may need F5 to show the new size |
| 5 | Rename to `Open questions.md` (F2) | `Routing.md`; history | Link rewritten, one batch |
| 6 | Delete it (Del, confirm "permanently delete") | Trash | In Kumbu²'s Trash; Restore works |
| 7 | Drag `project/` in (the `.git` is hidden in Explorer unless shown) | tree; `df -h` | `project/README.md` only |
| 8 | Drag `scan.pdf` in | Explorer's error; tree | Explorer reports an error copying it; nothing stored |
| 9 | Drag `budget.xlsx` in | Explorer's error; tree | Error copying it; nothing stored |
| 10 | Full quota (0): drag a PNG in; rename and delete a note | Explorer; shell notice; `df -h` | The PNG fails (507 shows as an error); rename and delete work. Reset the quota. Then **revoke** the share: the next access to `K:` asks for credentials (401) |

### rclone

| # | Step | Where to look | Passes when |
|---|---|---|---|
| 1 | Mount: add a remote with `rclone config` or by hand: `[kumbu]` with `type = webdav`, `url = https://kumbu.example.org/dav/`, `vendor = other`, `user = alice.k7qm`, and `pass =` the output of `rclone obscure -` (reading the password from stdin). Then `rclone lsd kumbu:` | terminal; Access → WebDAV row | `Work` listed; "Last used" now |
| 2 | List: `rclone ls kumbu:Work/Ideas` | terminal | The two notes with their sizes |
| 3 | `rclone copy diagram.png kumbu:Work/Ideas/` and `rclone copy Inbox.md kumbu:Work/Ideas/` | tree, history; `rclone check` | Stored; `rclone check` reports no differences for LF files |
| 4 | Edit in place: `rclone mount kumbu: ~/kumbu --vfs-cache-mode writes`, open `~/kumbu/Work/Ideas/Questions.md` in an editor, save | history; `Routing.md` | One new version per save; links untouched; no swap files in the web app. A CRLF file may then show "sizes differ" (README: `--ignore-size` or LF) |
| 5 | Rename: `rclone moveto "kumbu:Work/Ideas/Questions.md" "kumbu:Work/Ideas/Open questions.md"` | `Routing.md`; history | Link rewritten, one batch |
| 6 | Delete: `rclone deletefile "kumbu:Work/Ideas/Open questions.md"` | Trash | In the Trash; Restore works |
| 7 | `rclone copy project kumbu:Work/project` | tree; `rclone lsf -R kumbu:Work/project` | Tree: `README.md` only. rclone may list `.git/` for up to 10 minutes; a later `rclone check` reports it missing, which is expected |
| 8 | `rclone copy scan.pdf kumbu:Work/Ideas/` | terminal; tree | rclone reports the 413 ("A file can have at most 15 MB."); nothing stored |
| 9 | `rclone copy budget.xlsx kumbu:Work/Ideas/` | terminal; tree | rclone reports the 415 with the sentence; nothing stored |
| 10 | Full quota (0): `rclone copy` a PNG; then `rclone moveto` and `rclone deletefile` a note | terminal; `df -h` | The copy fails with 507 "This vault is out of space." after rclone's retries; the move and delete work. Reset the quota |

### Nextcloud External storage

Use Alice's own Nextcloud account and a share of her own for it. Never use one admin mount for everyone.

| # | Step | Where to look | Passes when |
|---|---|---|---|
| 1 | Mount: **Personal settings → External storage**: folder name `Kumbu`, backend **WebDAV**, URL `kumbu.example.org`, remote subfolder `/dav/`, secure (https) on, username `alice.k7qm`, the password | Nextcloud's status dot; Access → WebDAV row | The dot turns green; "Last used" now |
| 2 | List: Nextcloud Files → `Kumbu/Work/Ideas` | Nextcloud | The two notes |
| 3 | Upload `diagram.png` and `Inbox.md` there (drag into the browser). Nextcloud may write a `.ocTransferId*.part` and move it onto the name | tree, history | Both stored as themselves, one history entry each; no `.part` anywhere in the web app |
| 4 | Edit in place: open `Questions.md` in Nextcloud's text editor, change, let it save | history; `Routing.md` | One new version per save; links untouched |
| 5 | Rename to `Open questions.md` (⋯ → Rename) | `Routing.md`; history | Link rewritten, one batch |
| 6 | Delete it (⋯ → Delete) | Kumbu²'s Trash | In the Trash; Restore works |
| 7 | Upload the folder `project/` (drag the folder in) | tree | `project/README.md` only |
| 8 | Upload `scan.pdf` | Nextcloud's error; tree | Upload error; nothing stored |
| 9 | Upload `budget.xlsx` | Nextcloud's error; tree | Upload error; nothing stored |
| 10 | Full quota (0): upload a PNG; rename and delete a note | Nextcloud; `df -h` | The upload fails; rename and delete work. Reset the quota |

### Obsidian Remotely Save

Use a scratch Obsidian vault, not one synced with the Kumbu² Sync plugin, and leave the syncing of Obsidian's settings (`.obsidian`) off.

| # | Step | Where to look | Passes when |
|---|---|---|---|
| 1 | Mount: plugin settings, remote **WebDAV**, server address `https://kumbu.example.org/dav/`, username `alice.k7qm`, password, auth type basic, remote base directory `Work`; **Check connectivity** | the plugin's notice; Access → WebDAV row | Connectivity OK; "Last used" now |
| 2 | List: run the first sync (pull) | the Obsidian vault | `Ideas/Routing.md` and `Ideas/Questions.md` arrive |
| 3 | Put `diagram.png` and `Inbox.md` into the Obsidian vault's `Ideas/`, sync | tree, history | Both stored; history by "Hand test Obsidian (dav)" |
| 4 | Edit `Questions.md` in Obsidian, sync | history | One new version per sync |
| 5 | Rename `Questions.md` to `Open questions.md` in Obsidian (Obsidian updates `Routing.md` itself), sync | tree; `Routing.md`; Trash | `Open questions.md` is there with its text. `Routing.md` says `[[Open questions]]`, whether Kumbu² rewrote it (a MOVE) or Obsidian's own edit arrived. If the plugin uploads and deletes instead, the old name is in the Trash |
| 6 | Delete `Open questions.md` in Obsidian, sync | Trash | In the Trash; Restore works |
| 7 | Copy `project/` into the Obsidian vault, sync | tree | `project/README.md` only (whether the plugin skips `.git` or uploads it to parking) |
| 8 | Put `scan.pdf` in, sync | the plugin's error notice; tree | That file fails, the rest syncs; nothing stored |
| 9 | Put `budget.xlsx` in, sync | the plugin's error notice; tree | That file fails; nothing stored |
| 10 | Full quota (0): add a PNG and sync; delete a note and sync | the plugin's notice; Trash; `df -h` | The PNG fails (507); the deletion goes through. Reset the quota |

### Once, with any client

1. **A published note.**
   - Publish `Work/Ideas/Routing.md` in the web app, then delete it from the client. The client is told it worked.
   - **Activity** (after the share's next request, a minute later) says "“Hand test … (dav)” tried to remove /Work/Ideas/Routing.md; it is published, so it stays.", the note shows again, and the Trash doesn't have it.
2. **Emptying the trash.**
   - In the **Trash** sheet, **Empty trash…** with the password.
   - Activity: "Emptied the trash: N entries.". After the next blob sweep, `df -h` shows the space back.
   - No client, key or console command can do this.
3. **Revoke a share** on its row. The client's next request gets 401: the drive asks for credentials or reports an error.

## 11. The pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/152: draft, "WebDAV access, storage quota and access tabs", from `feature/webdav` to `master`, not merged. The row in [open-items.md](../open-items.md) links it.

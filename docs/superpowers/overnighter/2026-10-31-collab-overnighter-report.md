# Overnighter report: vault sharing ("collab", 0.3.0)

Branch `feature/collab`, from merge base `65ace59` to `14fa84a`: 105 commits (the plan, 80 planned tasks, the review fix rounds and the final fix wave). Every commit was pushed to `feature/collab` and mirrored to the session's branch. Nothing was merged, tagged, released or deployed, and no version file was touched.

Sources: the brief, the plan (`docs/superpowers/plans/2026-10-31-collab-plan.md`), and the run's ledger, per-batch reports and reviews, and the final review and final fix report. Where a source gives no number, this report says "not recorded". The pull request link is in section 9.

---

## 1. Summary

**All six phases (0–5) are built, and every phase gate was green.**

| Phase | What it built | Gate |
|---|---|---|
| 0 | Levels with `maintain`, structural files, refusal codes, writer references, `WriterLabels` | green at `2d85c21` |
| 1 | Memberships, member slots, the per-request membership check, per-caller view, aliases | green at `024ba96` |
| 2 | Keypairs, member slots under P-256 public keys, pins | green at `22c4ec3` |
| 3 | Invitations, revoke and leave, roles, events, notices, closing period, the sharing UI | green at `ce55f43` |
| 4 | Re-keying shared vaults, with recovery and "Re-encrypt my vaults" | green at `93a4cb9` |
| 5 | Sockets for sessions, presence, the server side of plugin 0.2.0 | green at `70be44f` |
| Final fix wave | The final review's findings | green at `14fa84a` |

**Final totals** at `14fa84a`, after a fresh test MongoDB (final fix report):

| Run | Result |
|---|---|
| `dotnet test --solution Kumbu.slnx` | 3204 total: **3198 passed, 6 skipped, 0 failed** |
| `pnpm -C web test --run` | **81 files, 1255 tests passed** |
| `pnpm -C web build` | OK |

The 6 skips are the 4 the repository expects (embedding model and TensorX key) plus 2 that skip because the tests run as root (section 2).

**The five hard rules.** Each has its own test class, and the final branch-wide review found that all five hold.

| # | Rule | Dedicated test class | Also pinned by | Final verdict |
|---|---|---|---|---|
| 1 | Membership checked per request before a vault is loaded (§3.1) | `Sharing/MembershipCheckTests` | `Api/SyncSocketTests`, `Api/SyncHubReviewTests` (sockets) | HOLDS |
| 2 | Membership writes bound to vault and caller (§6.0) | `Sharing/BoundMembershipWriteTests` | `AliasApiTests`, `ProviderConsentTests` (both routes since added to the class itself) | HOLDS |
| 3 | Re-keys wrap only to pinned keys (§5.4) | `Sharing/PinTests` (includes a source scan) | `SharedRekeyTests`, `UserKeyRotationTests` | HOLDS |
| 4 | Members' writes store no names (§8.1) | `Sharing/NoNamesTests` | `MarkerTests.Writer_names_are_never_written_into_note_content` | HOLDS |
| 5 | The marker test (§8.2) | `Sharing/MarkerTests` | `WriterViewTests` | HOLDS |

The final review found one cross-cutting gap (I-1: a member's alias written into `Board.md` by `board create`) and one low-likelihood socket race (I-2). The final fix wave fixed both and extended the hard-rule tests to cover them (section 4.7).

**Not done:**

- The Obsidian plugin 0.2.0 (`kumbu-obsidian`) is out of scope by the brief. Only its server side is built: the `plugin` field on pairing redemption and the 409 `plugin-too-old` refusal. Until 0.2.0 ships, shared vaults can't be paired (section 8).
- Release, tag, version numbers, deployment and images were not touched.
- The scoped re-review of the final fix wave found all eight findings addressed and no new Critical or Important breakage; it judged both surviving mutations equivalent (section 5.7).

---

## 2. Environment

| Item | Found | Done |
|---|---|---|
| .NET SDK | 10.0.403, which satisfies `global.json` (10.0.100, `latestFeature`) | nothing |
| `DOTNET_CLI_TELEMETRY_OPTOUT` | `1` | nothing |
| Node | 22.23.4 | **installed Node 26.13.0** from nodejs.org (SHA-256 checked) into `/opt/node26`, symlinked over `/opt/node22/bin/node`, which comes first on `PATH` |
| pnpm | 10.28.0 | **`npm install -g pnpm@10.32.1`** |
| Docker | 29.9.2, `hello-world` ran | see below |

**How MongoDB ran.**

1. **Testcontainers didn't work.** Its `mongo:7.0` container asks for `nofile` 64000, and the host's hard limit is 20000. Docker refused: "error setting rlimit type 7: operation not permitted". The first full run had 1130 failures.
2. **Our own container instead.** We started `mongo:7.0` ourselves with `--ulimit nofile=20000:20000`, `-p 27017:27017` and `--replSet rs0`, ran `rs.initiate`, and pointed the tests at it through `KUMBU_TEST_MONGO=mongodb://localhost:27017/?replicaSet=rs0&directConnection=true` (the `ExternalMongo` path of `TestImages.cs`). The container is named `kumbu-test-mongo`.
3. **mongod hit EMFILE.** One database per test class piles up over many runs. mongod then aborted with WiredTiger "Too many open files": the env notes give about 30,000 WiredTiger files, and the 0.1–0.4 report found about 115,000 files in `/data/db`. Docker restarted the container 5 times. This caused flaky `MongoConnectionException`s and server-selection timeouts in the early full runs.
   - Fix: start mongod with `--wiredTigerEngineConfigString file_manager=(close_idle_time=5,close_scan_interval=2,close_handle_minimum=250)`, and give every full run a fresh container (`/tmp/kb/mongo-reset.sh`, outside the repository).
   - The first clean full run after that was 2822 passed / 6 skipped at `ebdba66`, in 3 min 21 s.
4. **Docker volumes filled the disk.** The reset script first used `docker rm -f` without `-v`, so each reset left an anonymous volume behind: 38 volumes, 26 GB, and a "No space left on device" during the 1.9–1.12 full run. `docker volume prune -f` cleared them, and the script now uses `docker rm -fv`.

Ryuk didn't come into it. No container needed the network proxy's CA.

**The two extra skips.** The run executes as root, so file permissions are not enforced. Two tests skip because of that:

- `SeedScannerTests.Skips_unreadable_files_with_a_warning`
- `ModelFilesTests.AReadOnlyModelFolderTurnsSearchByMeaningOffInsteadOfStoppingTheServer`

The 4 expected skips are `SearchIndexTests.The_real_model_ranks_by_meaning`, the two `UnigramTokenizerTests` and `OpenAiCompatibleDriverTests.TensorX_ranks_by_meaning`. So every dotnet figure in this report is 6 skipped, where the brief expects 4.

**The test filter that works** (Microsoft.Testing.Platform, xUnit v3): `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`. The flag can be repeated, and repeats OR together. `--filter-method "*Name"` works the same way, and so does `--filter-namespace "…"`.

- Combining `--filter-namespace` with `--filter-class`, or `--filter-class` with `--filter-method`, ANDs them. That selected few or no tests.
- A glob in the middle of a name gives "Zero tests ran".

---

## 3. Task 0: planned against measured

| | Briefed | Measured |
|---|---|---|
| `dotnet test --solution Kumbu.slnx` | 2703 passed, 4 skipped, 0 failed | **2701 passed, 6 skipped, 0 failed** (against the external MongoDB) |
| dotnet duration | about 1 min 45 s | **3 min 32 s** |
| `pnpm -C web test --run` | 74 files, 1128 tests | **74 files, 1128 tests passed**, 1 min 11 s |
| `pnpm -C web build` | succeeds | **succeeds**, 16 s |
| Setup plus the first build and test run | about 15 min | **about 25 min** |

- **Why 2 fewer passed and 2 more skipped:** the two root-only skips (section 2).
- **Why setup took longer:** diagnosing the Docker ulimit, and installing Node 26.
- **Why the full suite took longer:** the duration is not explained in the sources. Later full runs took 3 min 21 s (`ebdba66`) and 4 min 20 s (`e25bfde`). At the phase 3 gate the server project alone took 4 min 29 s.

**The plan** was written, committed and pushed (`f3dcac5`) before any code. It has one section per phase of design §12, small tasks with red tests, mutations and files, estimated counts, and 13 planner decisions where the design leaves the call to the builder.

**The plan's estimates** are written against the briefed baseline of 2703/4. Against the measured baseline, every estimated dotnet figure is 2 lower, with 6 skipped; section 4 gives both.

---

## 4. Per phase

**How to read these tables.**

- "Filtered" is the task's own filtered runs as its report gives them.
- "Full suite" is the full `dotnet test --solution Kumbu.slnx`. It was run once per batch, normally before the batch's last commit.
- Every run listed passed unless it says otherwise.
- Commit timestamps are UTC.

### 4.0 Phase 0: groundwork (ended `2d85c21`, 18:26)

| Task | Commit | Filtered (as reported) | Tests added |
|---|---|---|---|
| 0.1 Ordered levels with `maintain` | `8b2aaa1` | KeyRightTests 7/7; neighbours 71/71 | +7 |
| 0.2 Legacy `write` reads as `maintain` | `238fa9b` | 7/7; with OAuth and neighbours 252/252 | +6 |
| 0.3 Own devices pair as `maintain` | `c9b84c4` | 72/72 | +2 |
| 0.4 `ICaller.Level(vault)` | `374b263` | Core CallerTests 6/6; Server Auth.CallerTests 24/24 | +8 |
| *Batch full suite* | | **2724 passed / 6 skipped / 0 failed** | |
| 0.5 `StructuralFiles` and the structural refusal | `c92a414` | Core 1307 passed / 2 skipped | +10 |
| 0.6 `BoardService` names the operation | `4a018a2` | Core Board 105/105; server Board 18/18 | +4 |
| 0.7 Moves and removals of structural files | `026a457` | Core 1320 passed / 2 skipped | +10 |
| 0.8 Whole-or-nothing `restore --checkpoint` | `467e544` | not recorded | +6 |
| 0.9 Refusal codes on REST and MCP | `3905583` | not recorded | +20 |
| *Batch full suite* (before the 0.9 amend) | | **2773 / 6 / 0**; web 74 files / 1128; build OK | |
| 0.5–0.9 fix round | `0f79a4d` | Core 1336 / 2 skipped; 8 server classes 153/153 | +4 |
| 0.10 Writer references and `v` | `4812c00` | server 10/10, core 1/1 | +11 |
| 0.11 Fail closed without a reference | `a2142cd` | 30/30 | +20 |
| 0.12 Changesets keyed by the reference | `076c3b3` | 9/9 | +4 |
| 0.13 `WriterLabels` | `a2171c9` | 25/25; VersionCommands 31/31 | +4 |
| *Batch full suite* | | **2812 passed / 6 skipped / 1 failed** (a dropped MongoDB connection; the test passes alone and in its class, 316/316) | |
| 0.10–0.13 fix round | `ebdba66` | 46/46; full suite first 2821 / 6 / 1 (the same flake), then **2822 / 6 / 0** after the EMFILE fix | +9 |
| 0.14 Web: `self` from the server | `6ba0b15` | touched web files 224 passed; server 1/1 | web +5 |
| 0.15 `maintain` in the dialogs; the `warning` token | `55ced6b` | 524 passed (routes, dialog, theme); web 74 files / 1190 | web +57 (the 51 contrast cases and the dialog tests) |
| 0.16 Security architecture, phase gate | `2d85c21` | docs only | 0 |

| Phase 0 gate | Estimated (briefed baseline) | Estimated here | Measured |
|---|---|---|---|
| dotnet | 2807 passed / 4 skipped | 2805 / 6 | **2823 passed / 6 skipped / 0 failed** |
| web | 74 files / 1191 | | **74 files / 1190** |
| build | OK | | **OK** |

### 4.1 Phase 1: memberships, per-request authorisation, aliases (ended `024ba96`, 21:14)

| Task | Commit | Filtered (as reported) | Tests added |
|---|---|---|---|
| 1.1 `MembershipDocument` and indexes | `6d437be` | 12 | 1.1–1.4: 29 cases (estimated +18) |
| 1.2 Slot kinds `member`, `invite`; `GrantedVia` | `0fa0d81` | 4 | |
| 1.3 `Memberships.GrantAsync` | `527dd3a` | class 17; 33 with neighbours, stable over 3 runs | |
| 1.4 HARD RULE 1 (a): sessions | `5062e65` | 7; OwnershipTests 5 | |
| *Batch full suite* | | **2852 / 6 / 0** | |
| 1.5 Derived slots with `GrantedVia` | `e9129e6` | 5 (6 after 1.6); neighbours 289 | 1.5–1.8: +23 |
| 1.6 HARD RULE 1 (b): keys, connections, sync | `194fb38` | 12; neighbours 285 | |
| 1.7 Access records, per-caller view | `e7cf486` | AliasTests 5; EncryptedNoteStoreTests 39; server project 1533 passed / 4 skipped | |
| 1.8 Effective levels for members | `4e12d3d` | 6; 30 with CallerTests | |
| *Batch full suite* | | **2875 / 6 / 0** | |
| 1.9 Aliases in the name index; rename | `5668ab8` | AliasTests 10; neighbours 285 | 1.9–1.12: +73 |
| 1.10 A member's rotation keeps her aliases | `80386c2` | class 28 | |
| 1.11 HARD RULE 4: no names in members' writes | `40e6939` | NoNamesTests 56; Core 1336; neighbours 225 | |
| 1.12 HARD RULE 5: marker test, member columns | `39d7c1d` | 8; server project 1612 passed / 4 skipped | |
| *Batch full suite* | | **2948 / 6 / 0** | |
| 1.9–1.12 fix round | `0eb175a` | covering classes 207/207 | |
| 1.13 Re-keying refuses a shared vault | `6563745` | 5; neighbours 208 | 1.13–1.16: +15 |
| 1.14 Owner-only embedding provider; pairing refused | `689ace0` | 7; neighbours 306 | |
| 1.15 A member's account deletion | `dcdb63f` | 3; neighbours 252 | |
| 1.16 Security architecture, phase gate | `309def8` | gate below | |
| 1.13–1.16 fix round | `024ba96` | covering classes 230; SharedRekeyRefusalTests 6 | +1 |

| Phase 1 gate | Estimated (briefed) | Estimated here | Measured |
|---|---|---|---|
| dotnet | 2891 / 4 | 2889 / 6 | **2963 / 6 / 0** at `309def8`; **2968 / 6 / 0** after the fix round at `024ba96` |
| web | 74 / 1191 (no web) | | **74 / 1190** (run because `schema.d.ts` was regenerated) |
| build | | | **OK** |

### 4.2 Phase 2: keypairs and pinned member slots (ended `22c4ec3`, 22:06)

| Task | Commit | Filtered (as reported) | Tests added |
|---|---|---|---|
| 2.1 `MemberSlotCrypto` (DHKEM P-256, AES-256-GCM) | `e37ed34` | 9 | 2.1–2.4: +23 (estimated +20) |
| 2.2 A keypair per user, lazily, once | `cccc1f5` | 6; Accounts 321 | |
| 2.3 Member slots under public keys; the stand-in goes | `7c2e9dd` | KeypairTests 9; Sharing 145; UserKeyRotationTests 29 | |
| 2.4 HARD RULE 3: pins | `47700aa` | PinTests 5; 14 with KeypairTests | |
| *Batch full suite* (run straight after the 2.4 commit) | | **2991 / 6 / 0** | |
| 2.5 A new keypair per rotation | `1a94c1c` | 47/47; Sharing 153; Accounts 325 | 2.5–2.7: +6 (estimated +5) |
| 2.6 The keypair inside the grant's transaction | `2a9754d` | Sharing 155 | |
| 2.7 Security architecture, phase gate | `22c4ec3` | gate below | |
| Phase-2 review fixes (landed in the 3.1–3.4 batch) | `e42606a` | UserKeyRotationTests 35; Sharing 176; Accounts 327 | +2 |

| Phase 2 gate | Estimated (briefed) | Estimated here | Measured |
|---|---|---|---|
| dotnet | 2916 / 4 | 2914 / 6 | **2997 / 6 / 0** |
| web | 74 / 1191 (no web) | | not run (web untouched; last measured 74 / 1190) |

### 4.3 Phase 3: invitations, revoke, roles, events, closing, the UI (ended `ce55f43`, 03:19)

| Task | Commit | Filtered (as reported) | Tests added |
|---|---|---|---|
| 3.1 Creating an invitation | `34fa418` | 68/68; Sharing 163 | +9 |
| 3.2 Peek | `1bfeeac` | 161/161 | +5 |
| 3.3 Accepting | `8a4eb9c` | 81/81; Sharing 176 | +8 |
| 3.4 Confirm, decline, withdraw | `c30ef7b` | 86/86 | +5 |
| *Batch full suite* | | **3027 / 6 / 0**; web 74 / 1190; build OK | |
| 3.1–3.4 fix round | `4bf097f` | 117/117; Sharing 187 | +6 |
| 3.5 Revoke and leave, cleanup | `61401ce` | Sharing 192; 113/113; web Connections 19/19 | +5 |
| 3.6 Role changes | `0a844b8` | 78/78 | +6 |
| 3.7 HARD RULE 2 | `e3ae45f` | Sharing 208 | +13 |
| 3.8 The membership sweep | `2d19e96` | not recorded | +4 |
| *Batch full suite* | | **3061 / 6 / 0**; web 74 / 1191; build OK | |
| 3.5–3.8 fix round | `de83e36` | 17/17; Sharing 214; 53/53 | |
| 3.9 The owner's access list | `45fd71a` | Sharing 217; 91/91 | 3.9–3.12 with the fix: +21 |
| 3.10 What members see | `f537d64` | Core 1350 / 2 skipped; Sharing 219; Api 436 | |
| 3.11 Events | `54a7192` | Sharing 224; 87/87 | |
| 3.12 One-time notices | `ee30b6b` | 4/4; Mcp 42; MarkerTests 4 | |
| *Batch full suite* | | before the 3.12 commit **3081 / 6 / 1 failed**: 3.11's new route was missing from `PasswordWindowTests`' list. After the 3.12 commit: **3082 / 6 / 0**. Web 74 / 1191; build OK | |
| 3.9–3.12 fix round | `fce99a4` | 17/17; Sharing 226; Mcp 43 | +3 |
| 3.13 Provider consent | `e4e0bae` | 5/5; Api 436; Sharing 231; Search 56; Mcp 43 | +5 |
| 3.14 Export by members | `a306e96` | 41/41; 91/91 | +5 |
| 3.15 Owner deactivated | `02572a0` | 8/8 (×10 runs); Sharing 239; Accounts 327; Core 1352 | +8 |
| 3.16 The member deletion receipt | `75bca90` | 2/2 | +2 |
| *Batch full suite* | | **3105 / 6 / 0**; web 74 / 1191; build OK (after fixing a test fixture) | |
| 3.13–3.16 fix round | `b72fa6a` | 10/10 | +2 |
| 3.17 The closing period | `ce01af9` | 13/13; Accounts 328; Sharing 254; 167/167 | +14 |
| 3.18 Sharing metrics | `bc4938e` | Metrics 36/36 | +3 |
| 3.19 Attachments for members | `b6dd72c` | 33/33 | +7 |
| 3.17 follow-up (dev token's locked count, schema) | `4e01920` | | |
| 3.18 follow-up (gauges when a database doesn't answer) | `ea0d0c5` | | |
| *Batch full suite* | | first **3128 / 6 / 3 failed** (two real bugs, fixed in the follow-ups, and one `NoNamesTests` flake); then **3131 / 6 / 0**. Web 74 / 1191; build OK | |
| NoNamesTests flake fix | `4f595ee` | 59/59 | |
| 3.20 Web: vault list, "Shared with me" | `b6faeb4` | | web +7 |
| 3.21 Web: share dialog and access list | `c43ed0e` | | web +11 |
| 3.22 Web: the `/share` accept page | `e25bfde` | | web +6 |
| *Batch* | | web **77 files / 1215**; build OK after each commit; dotnet **3131 / 6 / 0** (4 min 20 s, after the last commit) | |
| 3.20–3.22 fix round | `0256a22` | | dotnet +1, web +1 |
| 3.23 Web: member's side, banners, credential dialogs; alias rename route | `c976e94` | | dotnet +3; web +18, +2 files |
| 3.24 Web: gallery and lightbox follow the level | `0ecbba9` | | web +4 |
| 3.25 Security architecture, phase gate | `ce55f43` | gate below | |

| Phase 3 gate | Estimated (briefed) | Estimated here | Measured |
|---|---|---|---|
| dotnet | 3024 / 4 | 3022 / 6 | **3135 / 6 / 0** (3141 total) |
| web | 80 files / 1229 | | **79 files / 1238** |
| build | | | **OK** |

### 4.4 Phase 4: re-keying shared vaults (ended `93a4cb9`, 04:28)

| Task | Commit | Filtered (as reported) | Tests added |
|---|---|---|---|
| 4.1 The start keeps member slots, ends invitations | `bcbe045` | 4/4; Sharing 262; Rekey classes 66 | 4.1–4.4: net +8 (6 refusal tests deleted, 14 added; estimated +14) |
| 4.2 Only the owner's key drives a run | `9da681e` | 6/6; 11 with OwnershipTests | |
| 4.3 The finish wraps to pins (HARD RULE 3) | `ab2ee15` | PinTests 9/9; SharedRekeyTests 9/9; Sharing 264; 106/106 | |
| 4.4 The old-key guard covers member-derived slots | `2e4a3fc` | SharedRekeyTests 11/11 | |
| *Batch full suite* | | **3143 / 6 / 0**; web 79 / 1238; build OK | |
| 4.5 "Re-encrypt my vaults", recovery, deletions during a run | `2c5d30e` | 28/28; Sharing and Accounts 599/599 | +5 |
| 4.6 Events, notices, the re-encrypt offer | `5baf3cf` | 13/13; four namespaces 809/809; AccessList 7/7 | +3; web net +2 |
| 4.7 Security architecture, phase gate | `93a4cb9` | gate below | |

| Phase 4 gate | Estimated (briefed) | Estimated here | Measured |
|---|---|---|---|
| dotnet | 3046 / 4 | 3044 / 6 | **3151 / 6 / 0** |
| web | 80 files / 1232 | | **79 files / 1240** |
| build | | | **OK** |

### 4.5 Phase 5: sockets, presence, plugin 0.2.0 server side (ended `70be44f`, 06:10)

| Task | Commit | Filtered (as reported) | Tests added |
|---|---|---|---|
| 4.5–4.7 fix round | `704a2ff` | not recorded | +2 |
| 5.1 Tickets for sessions; subjects | `ada513f` | 5/5; neighbours 141/141 | |
| 5.2 The 30-second check recomputes vaults | `ac0090f` | 5/5; neighbours 54/54 | |
| 5.3 Synchronous hub notifications | `a78230b` | 5/5; neighbours 212/212 | |
| 5.4 Presence | `2d31e2a` | 9/9; neighbours 138/138 | +7 |
| 5.5 Marker test on the socket (HARD RULE 5) | `d47ee82` | class 6/6 | +2 |
| *Batch full suite* | | **3175 / 6 / 0** (web not run: no web or OpenAPI change) | |
| 5.1–5.5 fix round, and a test follow-up | `d289a26`, `0997970` | 203/203 | +6 |
| 5.6 Web: one socket per tab | `e77cd5e` | | web +6 |
| 5.7 Web: writers and presence in the editor | `1de65d8` | | web +8 |
| 5.8 Plugin 0.2.0: `plugin` on redemption | `fa8fdd7` | PairingApi and OwnerOnly 33/33 | +5 |
| 5.9 Security architecture, final phase gate | `70be44f` | gate below | |

| Phase 5 gate | Estimated (briefed) | Estimated here | Measured |
|---|---|---|---|
| dotnet | 3073 / 4 | 3071 / 6 | **3186 / 6 / 0** (3192 total) |
| web | 82 files / 1246 | | **81 files / 1254** |
| build | | | **OK** |

**Why the dotnet counts ran ahead of the estimates.** Implementers added cases beyond the plan: theories counted per case, extra tests added to catch mutations that would otherwise have survived, and tests from review fix rounds. The largest single source is the 1.11 theory, 18 write paths × 3 credential kinds. Web is one file short of the estimate in phases 3–5: 4.5–4.7 needed no new web file.

### 4.6 Time per phase

The sources don't record a duration per phase. From the commit timestamps:

| Event | Commit timestamp (UTC) |
|---|---|
| Plan committed | 31 Oct, 15:24 |
| Phase 0 ends | 31 Oct, 18:26 |
| Phase 1 ends | 31 Oct, 21:14 |
| Phase 2 ends | 31 Oct, 22:06 |
| Phase 3 ends | 1 Nov, 03:19 |
| Phase 4 ends | 1 Nov, 04:28 |
| Phase 5 ends | 1 Nov, 06:10 |
| Final fix wave ends | 1 Nov, 07:04 |

### 4.7 Final review and final fix wave (`70be44f..14fa84a`)

The final review was read-only, branch-wide and at `70be44f`. It found that all five rules hold, and raised no Critical findings. It raised two Important ones:

- **I-1:** a member's alias is written into `Board.md` by `board create`;
- **I-2:** a stale socket recompute can restore a revoked vault for up to 30 s.

| Item | Commit | Filtered | Tests added |
|---|---|---|---|
| I-1: a new board is titled `# Board`, "Kanban board of this vault." | `948d7b6` | Core 1352 total, 1350 passed / 2 skipped; Marker, NoNames and Board* 88 passed | |
| I-2: latest-wins socket recompute; presence check-then-set under a lock | `26605e5` | SyncHubReview, SyncSocket and Marker 44 passed | |
| `WriterIn`/`ReferenceIn` fail closed without an access record | `77983bd` | 84 passed; server project 1841 passed / 4 skipped | |
| `BoundFilter`: a same-status update for `active` only | `8069dc8` | 41 passed | |
| "the owner" once the owner's account is gone (server and web) | `e4fdba0` | 21 passed; SharedBanner 7 passed | web +1 |
| A write member's device key may write notes but not `Board.md` (verification) | `fcb96a5` | passed first time | |
| Alias rename and provider-seen in HARD RULE 2's own class | `963b547` | BoundMembershipWriteTests 18 passed | |
| Docs: `security-architecture.md` brought in line | `14fa84a` | docs only | |
| *Final totals* | | **3198 / 6 / 0** (3204 total, +18 over 3186); web **81 / 1255**; build OK | |

---

## 5. Mutations

Every mutation is listed here, one per row. "Caught" means at least one test failed; the number of failing tests is given where the report records it. Every survivor is set out in its own subsection at the end, with what was done about it.

### 5.0 Phase 0

| Task | Mutation | Outcome |
|---|---|---|
| 0.1 | `ToText(Maintain)` → `"write"` | caught (1) |
| 0.1 | `Write` and `Maintain` swapped in the enum | caught (1) |
| 0.1 | `Parse` case-insensitive | caught (1) |
| 0.2 | `RightOf` ignores `V` | caught (2) |
| 0.2 | New payloads omit `V` | caught (2) |
| 0.2 | `CanWrite` back to `== Write` | caught |
| 0.2 | Meta reader skips the legacy rule, in `ApiKeys` (4a) and in `Connections` (4b), each run separately | caught (both) |
| 0.3 | Devices back to `Write` | caught |
| 0.4 | `RequestCaller.Level` returns `Owner` for every caller | caught (3 theory cases) |
| 0.4 | `Max` instead of `Min` | caught, by an extra test added for it |
| 0.4 | Default `CanWrite` as `>= Read` | caught (2) |
| 0.5 | `IsStructural` on a hard-coded `"/Board/Board.md"` suffix | caught |
| 0.5 | `mkcp` dropped from `StructuralCommands` | caught |
| 0.5 | Structural check before read-only | caught |
| 0.5 | `< Write` instead of `< Maintain` | caught (6) |
| 0.5 | Extra: Replacer preview check removed; then the apply check removed | caught (each) |
| 0.6 | Rename passes `board update` | caught |
| 0.6 | Create passes `board add` | caught |
| 0.7 | Target check removed | caught |
| 0.7 | Move-map loop removed | caught |
| 0.7 | `rm` of a folder back to plain `Refuse`, in both writers | caught (both cases) |
| 0.7 | Extra: link-rewrite check removed | caught |
| 0.8 | Compare presence only | caught (2) |
| 0.8 | No check in `PreviewWholeAsync` | caught (3 core and the API test) |
| 0.8 | Apply the non-structural part and skip the board | caught (4) |
| 0.9 | `NoteApi.Refuse` drops the code | caught (14) |
| 0.9 | `FileApi` PUT skips `Refuse` for an existing file | **survived at first**; test added, then caught (5.6) |
| 0.9 | `code` serialised when null | caught |
| 0.5–0.9 fix | A: `RefusePlan` removed from `ApplyAsync` | caught |
| 0.5–0.9 fix | B: removed from `PreviewOfAsync` | caught (2) |
| 0.5–0.9 fix | C: the moved notes' current paths left out | caught |
| 0.5–0.9 fix | D: the rewrite paths left out | caught |
| 0.5–0.9 fix | E: the `card` exemption ignored | caught |
| 0.5–0.9 fix | F: the single-note sentence reverted | caught |
| 0.10 | `Read` takes a missing `ref` as legacy whatever `v` says | caught |
| 0.10 | `HistoryPayload` loses `v`/`ref` on rewrap | caught |
| 0.10 | `MovePayload` without `v` | caught |
| 0.11 | `RequireRef` removed from the attachment insert | caught |
| 0.11 | `Seeder` stores `Ref: null` | caught |
| 0.11 | `TrashInAsync` builds its `TrashedNote` without the reference | caught (12 cases) |
| 0.12 | Member changeset key `"member"` without the id | caught |
| 0.12 | `Latest` keys by the label identity | caught |
| 0.12 | Extra: legacy fallback dropped | caught |
| 0.13 | Activity endpoint sends the raw `row.Writer` | caught |
| 0.13 | An unknown reference renders as the owner's | caught |
| 0.13 | `lscp` prints `CreatedBy` directly | caught |
| 0.10–0.13 fix | (a) `RequireStorable` ignores the markers | caught |
| 0.10–0.13 fix | (b) `Keeps` always false | caught |
| 0.10–0.13 fix | (c) API key reference without `key.Id` | caught |
| 0.10–0.13 fix | (d) connection reference without an id | caught |
| 0.14 | `writerText` with only `case "user"` | caught (4) |
| 0.14 | `ChangedElsewhere` falls back to `writer === username` | caught |
| 0.14 | Extra: `lastSeen` compares the name again | caught |
| 0.14 | Extra: `NoteView` `self` from the name | caught |
| 0.14 | Server: version kind set to null | caught |
| 0.15 | `"warning"` removed from `textTokens` | the theme test count drops from 327 to 276 (−51), as the brief expects |
| 0.15 | catppuccin-latte gets mocha's `#fab387` | caught (3) |
| 0.15 | New key dialog starts at maintain | caught (3) |
| 0.15 | New key dialog starts at read | caught (4) |
| 0.15 | Extra: connection rights dialog lowers maintain to write | caught |
| 0.15 | Extra: Maintain segment without the warning class | caught |

### 5.1 Phase 1

| Task | Mutation | Outcome |
|---|---|---|
| 1.1 | Partial filter on `$exists` | caught |
| 1.1 | Allow `revoked → active` | caught |
| 1.2 | `OwnedVaultsAsync` on `Owner` only | caught |
| 1.2 | `Admin.ListUsersAsync` on `Exists(Owner)` only | caught |
| 1.2 | Extra: `MarkOwnersAsync` without its kind filter | caught |
| 1.2 | Extra: `CountLockedAsync` counts `kind in [user, member]` as owned | caught |
| 1.3 | Limit count outside the transaction | caught (3 of 3 runs) |
| 1.3 | `membershipsChanged` write skipped | caught (3) |
| 1.3 | Alias stored in plain | caught (BSON scan) |
| 1.3 | Slot inserted before the transaction | caught, after the failure test was extended |
| 1.4 | No `status == active` filter (mandatory) | caught (4) |
| 1.4 | Check after `LoadAsync`, only when not in `_open` (mandatory) | caught (5) |
| 1.4 | Per-process cache of membership results (mandatory) | caught |
| 1.4 | Missing membership treated as active (mandatory) | caught (5) |
| 1.4 | Extra: no vault binding | caught |
| 1.4 | Extra: no member binding | caught |
| 1.4 | Extra: `RekeyPendingFound` without the kind check | caught |
| 1.5 | Membership update without `status == active` | caught |
| 1.5 | `GrantedVia` not set on key slots | caught (2) |
| 1.5 | `GrantedVia` not set by `WrapConnectionSlots` | caught |
| 1.5 | Pairings passes the full key name | caught |
| 1.6 | Query skipped when the first slot has no `grantedVia` (mandatory) | caught (3) |
| 1.6 | `OpenConnectionSlotsAsync` lets everything through (mandatory) | caught |
| 1.6 | `status == active` dropped in `ActiveMembershipsAsync` (mandatory) | caught (7) |
| 1.6 | `ForConnectionAsync` lets an empty token through (mandatory) | caught |
| 1.6 | Extra: `SameGrant` always true | caught |
| 1.6 | Extra: no role cap | caught |
| 1.7 | Member's name = `Vault.Name` again | caught (5 of 5) |
| 1.7 | `VaultsAsync` keyed by `Vault.Name` | caught (3) |
| 1.7 | Back to `GroupBy().First()` | caught |
| 1.7 | Ticket from `UnlockedVault.Name` | caught (2) |
| 1.7 | Extra: version entries untranslated | caught |
| 1.8 | No `min` | caught (3) |
| 1.8 | Member session = `Owner` | caught (3) |
| 1.8 | `min` over role and `Owner` | caught, by a test added for it |
| 1.9 | Member slot `NameId` under another key | caught (3) |
| 1.9 | Orphan path without the kind filter | caught |
| 1.9 | Extra: rename updates only `nameId` | caught |
| 1.10 | `NameId` not recomputed for member slots | caught |
| 1.10 | Alias key sealed again under the old key | caught (3) |
| 1.10 | Extra: no `alias` guard | caught |
| 1.11 | `WriterIn` returns the key name (mandatory) | caught (55 of 56) |
| 1.11 | Member reference with the credential id (mandatory) | **survived at first**; test strengthened, then caught (36) |
| 1.11 | The trash path uses `caller.Writer` (mandatory) | caught |
| 1.11 | Session writer id = the user id (mandatory) | caught |
| 1.12 | Membership label rendered to a member viewer (mandatory) | caught |
| 1.12 | Device kind passed through for another member (mandatory) | caught |
| 1.12 | Activity row key = the raw group key / = the writer id (mandatory) | caught (both) |
| 1.12 | `lscp` bypasses `WriterLabels` (mandatory) | caught |
| 1.9–1.12 fix | Folder upload label as `caller.Writer` | caught |
| 1.9–1.12 fix | Member's own session back to the username, not "You" | caught |
| 1.13 | Shared check counts only `active` | caught |
| 1.13 | Check moved out of the start's transaction | caught, by the grant-race test added for it |
| 1.13 | Runner pulls the vault from `rekeyPending` after the refusal | caught |
| 1.14 | `IsOwner` replaced by the first vault's membership | caught (3) |
| 1.14 | Pairing refused only for members | caught |
| 1.15 | Membership left in place, slots deleted | caught (2) |
| 1.15 | Selected by `owner == userId` | caught (3) |
| 1.13–1.16 fix | In-transaction check removed while the pre-check stays | caught |

### 5.2 Phase 2

| Task | Mutation | Outcome |
|---|---|---|
| 2.1 | AAD without the algorithm byte | caught (3) |
| 2.1 | Salt without the recipient's public key | caught (3) |
| 2.1 | No `RequireOnCurve` on unwrap's import | caught, but only through the check's own message: OpenSSL also refuses the point |
| 2.1 | `DeriveKeyFromHash` instead of the raw x-coordinate | caught (3) |
| 2.2 | Keypair filter without `publicKey $exists: false` | caught |
| 2.2 | AAD without the public key's hash | caught |
| 2.2 | Keypair created only at account creation | caught (3) |
| 2.2 | Extra: filter without `keyId` | caught |
| 2.3 | `GrantAsync` uses `users.publicKey` without opening the private key | caught |
| 2.3 | Stand-in unwrap kept as a fallback | caught |
| 2.3 | Extra: rotation doesn't reseal the private key | caught (3) |
| 2.4 | Fall back to `users.publicKey` when the pin doesn't open (mandatory) | caught (3) |
| 2.4 | No `member` check (mandatory) | caught |
| 2.4 | No membership-id check (mandatory) | caught |
| 2.4 | Keep the member slot on a bad pin (mandatory) | caught (3) |
| 2.4 | Extra: wrap to `users.publicKey` despite a valid pin | caught |
| 2.4 | Extra: create member slots (upsert) | caught |
| 2.4 | Extra: no `vault` check | caught |
| 2.4 | Extra: a third `MemberSlotCrypto.Wrap` caller | caught (source scan) |
| 2.5 | Keep the old keypair | caught (5) |
| 2.5 | Rewrap without `revision++` | caught (3) |
| 2.5 | `PrepareUserSlotsAsync` rewraps member slots' `Wrapped` | caught (8) |
| 2.5 | Extra: no check that the pin names the old public key | caught |
| 2.5 | Extra: wrap to the old pinned key, not the new | caught (8) |
| 2.6 | `EnsureAsync` outside the grant's transaction | caught |
| Phase-2 fix | Orphaned member slot not deleted on rotation | caught |
| Phase-2 fix | No user-document counter in the grant | caught (stable 3 of 3) |

### 5.3 Phase 3

| Task | Mutation | Outcome |
|---|---|---|
| 3.1 | Pending count outside the transaction | caught, by the concurrency test added for it |
| 3.1 | `inviteLookup` as plain SHA-256 | caught |
| 3.1 | `?s=<secret>` in the link | caught |
| 3.2 | `InviteMetaAsync` calls `LoadAsync` | caught |
| 3.2 | A distinct answer for a used or expired secret | caught |
| 3.2 | An extra server-wide rate-limit partition | caught |
| 3.3 | Claim without `expiresAt > now` | caught at first only by the peek test; a membership-only-expired case was added, then caught by both |
| 3.3 | Claim outside the transaction | caught |
| 3.3 | No user-document counter | caught |
| 3.4 | Confirm without the password window | caught |
| 3.4 | Withdraw leaves the invite slot | caught |
| 3.4 | Extra: `Bound` without the vault | caught |
| 3.1–3.4 fix | Redeemer shown for any `Member` | caught (2) |
| 3.1–3.4 fix | No `status` in the index's partial filter | caught |
| 3.1–3.4 fix | Index not unique | caught |
| 3.1–3.4 fix | Accept without the vault-name check | caught (4) |
| 3.5 | No hub call | caught |
| 3.5 | `RevokeKeysWithoutVaultsAsync` counts only slots without `GrantedVia` | caught |
| 3.5 | Cleanup deletes the tombstone | caught (4) |
| 3.5 | Extra: `RemoveVaultsAsync` without the user filter | caught |
| 3.5 | Extra: the atomic step keeps the pin | **survived at first**; assertion added, then caught |
| 3.6 | Window check on lowering too | caught (2) |
| 3.6 | No window check on raising | caught (2) |
| 3.7 | Revoke filter without `vault` (mandatory) | caught |
| 3.7 | Role filter `_id` only (mandatory) | caught (3) |
| 3.7 | Leave on `_id` and status only (mandatory) | caught |
| 3.7 | Confirm on status `pending\|active` (mandatory) | caught |
| 3.7 | Extra: `BoundFilter` without the scope | caught (7) |
| 3.8 | Lapse counted from `createdAt` | caught |
| 3.8 | Lapse leaves the member slot | caught |
| 3.8 | Extra: no stray-slot removal | caught |
| 3.8 | Extra: no `cleanedAt` guard in the cleanup | **survived alone**; caught together with the sweeper's query unguarded |
| 3.5–3.8 fix | Cleanup without `only:` | caught |
| 3.5–3.8 fix | Sweeper without the hub call | caught |
| 3.5–3.8 fix | Hub without the live-membership guard | **survived at first**; socket assertion added, then caught |
| 3.9 | Last use from the key document | caught |
| 3.9 | `usedAt` written on every request | caught (profiler count) |
| 3.10 | Mark by label, two variants | caught (3 each) |
| 3.10 | `ls` prints the role instead of the effective level | caught (2) |
| 3.11 | A member sees every membership's events | caught |
| 3.11 | A stored `text` field | caught |
| 3.12 | Mark per user instead of per credential | caught |
| 3.12 | Mark before the answer is safe | caught |
| 3.12 | Extra: the owner's accesses included | caught (3) |
| 3.12 | Extra: TTL of 300 days | caught |
| 3.9–3.12 fix | No `Nin` (limit before the marks) | caught (3) |
| 3.9–3.12 fix | Legacy notes counted as not the owner's own | caught |
| 3.9–3.12 fix | Pre-join bound removed in `OfMemberAsync` / in `OfMemberEverywhereAsync` | caught (each) |
| 3.13 | Left out only for scope `/` | caught |
| 3.13 | `provider-seen` filtered on `_id` only | caught |
| 3.13 | Extra: `members` for a member too | caught |
| 3.14 | Files ZIP without the event | caught |
| 3.14 | Member export without the ticket, at the vault export / at the files ZIP | caught (each) |
| 3.14 | (A first, mis-built attempt that refused more, not less) | survived for that reason; replaced by the two above |
| 3.15 | Not in `CheckWritable` (caller/REST check only) | caught |
| 3.15 | Code not passed through `Refuse` | caught (3) |
| 3.15 | `MayIndex` unchanged | caught |
| 3.16 | Tombstone kept (status `left` instead of deleted) | caught |
| 3.16 | Receipt lists vault names | caught |
| 3.13–3.16 fix | Reactivation back to shared vaults only | caught |
| 3.13–3.16 fix | No clear in `OpenForUserAsync` | caught |
| 3.13–3.16 fix | No clear on the key path | caught |
| 3.17 | `closingAt` set after the transaction | caught |
| 3.17 | Closing vaults not left out of the deletion | caught (10) |
| 3.17 | `DeletionList` without the choice | caught |
| 3.17 | Extra: no notice reseal on rotation | caught |
| 3.17 follow-up | Closing-vault filter removed from `CountLockedAsync` | caught |
| 3.18 | Refused outcome labelled with the exception's message | caught (3) |
| 3.18 | Revoked memberships counted as active | caught |
| 3.19 | Replace skips `Refuse` | caught |
| 3.19 | Attachment history bypasses `WriterLabels` | caught |
| 3.20 | Group by `shared` instead of `owner` | caught |
| 3.20 | `mayChange` keyed on `write` only | caught |
| 3.21 | Link kept in the query cache | caught |
| 3.21 | Role PUT through a plain `fetch` past `api` | caught |
| 3.22 | Secret put into the `?next=` URL | caught |
| 3.22 | `sessionStorage` cleared before accept succeeds | caught |
| 3.20–3.22 fix | Server: `ownerUsername` from the display name | caught |
| 3.20–3.22 fix | Web: group by display name only | caught |
| 3.23 | Mixed warning only when every vault is shared | caught (3) |
| 3.23 | Capped text uses the sealed level | caught (4) |
| 3.23 | Provider OK without invalidating the vault list | caught |
| 3.23 | Leave without confirmation | caught |
| 3.23 | Server: alias route without `VaultNames.Check` | caught |
| 3.23 | Server: `RenameAliasAsync`'s membership read not bound to `Member == memberId` | **survived: equivalent mutant** |
| 3.24 | Gallery keyed on the drawer's active vault | caught |
| 3.24 | Lightbox Delete ungated | caught |
| 3.24 | Restore ungated | caught |

The 3.20–3.22 batch's `NoNamesTests` flake fix (`4f595ee`) records no mutation. Its check was the 59/59 run with the strength kept.

### 5.4 Phase 4

| Task | Mutation | Outcome |
|---|---|---|
| 4.1 | Final slot delete `Nin` without `member` | caught (2) |
| 4.1 | Unconfirmed memberships kept | caught |
| 4.1 | "Bob's key revoked whole", exactly as worded | **survived: a no-op** |
| 4.1 | 3b: whole revocation inside `EndMembersCredentialsAsync` | caught |
| 4.1 | 3c: the original order, the owner's query without the filter | caught |
| 4.2 | `RekeyPendingFound` raised for member slots too | caught |
| 4.3 | Finish wraps to `users.publicKey` | caught (3) |
| 4.3 | Finish without the `revision` filter | caught |
| 4.3 | `label` not resealed | caught |
| 4.3 | Finish inserts a member slot for an active membership without one | caught |
| 4.3 | Extra: a pin that doesn't open is accepted | caught |
| 4.3 | Extra: `PinnedKey` without the membership-id check | caught |
| 4.3 | Extra: `PinnedKey` without the member check | caught |
| 4.3 | Extra (the red state): no `FinishRekeyAsync` call | caught (6) |
| 4.4 | `RequireSameVaultKeysAsync` reads only `user` slots | caught |
| 4.4 | Extra: a member slot counts as "same key" without being opened | caught |
| 4.5 | Recovery skips shared vaults | caught |
| 4.5 | 1b: the same in `ReencryptAccountAsync` | caught |
| 4.5 | 1c: a member's rotation adds her member-slot vaults | caught |
| 4.5 | Deletion check only for the vault route | caught |
| 4.5 | 2b: refuse whether or not the vault is shared | caught |
| 4.5 | 2c: only the in-transaction check, without the check before | **survived** |
| 4.6 | Offer ticked by default | caught (2) |
| 4.6 | Leave offer opens at once, with no ask | caught |
| 4.6 | Re-key before the revoke | caught |
| 4.6 | No event recorded at the finish | caught (2) |
| 4.6 | `rekeyEnded` replaced by the current start's ids | caught |
| 4.6 | `LostAsync` returns nothing | caught (2) |
| 4.6 | Lost notices not marked | caught |
| 4.6 | Event recorded for every vault | caught |
| 4.6 | Departures ignore the latest re-keying | caught |
| 4.5–4.7 fix | Drop `if (running.Contains(vaultId)) continue;` | caught |
| 4.5–4.7 fix | `LostAsync`: skip the mark for events with no membership left | caught |

The 4.5–4.7 fix round did not mutate the in-transaction re-decision of an admin's deletion, because no test makes a run start mid-deletion.

### 5.5 Phase 5

| Task | Mutation | Outcome |
|---|---|---|
| 5.1 | Cap per user instead of per subject | caught |
| 5.1 | Session subject = the user id | caught (3) |
| 5.2 | `AliveAsync` touches `lastUsedAt` | caught |
| 5.2 | Vault set from the ticket only | caught (3) |
| 5.3 | Fire and forget in `EndedAsync` | caught |
| 5.3 | Drop only key subjects | caught (2) |
| 5.4 | Presence on the hint channel | caught |
| 5.4 | Rendered once for all receivers | caught (6) |
| 5.4 | Level check `>= Read` | caught (3) |
| 5.5 | Presence editors carry the display name for every receiver (mandatory) | caught (both tests) |
| 5.5 | Hints include the writer (mandatory) | caught (both tests) |
| 5.1–5.5 fix | (a) No recompute at the upgrade | caught |
| 5.1–5.5 fix | (b) Fail open: only log | caught |
| 5.1–5.5 fix | (c) No presence cap | caught |
| 5.1–5.5 fix | (d) No hub call on sign-out | caught |
| 5.6 | Poll keeps running while connected | caught |
| 5.6 | Reconnect reuses the old ticket | caught |
| 5.7 | Branch on the message text instead of the code | caught |
| 5.7 | No `on:false` on leaving Write mode | caught (2) |
| 5.7 | Extra: `writerOf` in place of `shownWriter` | caught (2) |
| 5.8 | Plugin check after consuming the code | caught (3) |
| 5.8 | Ordinal string comparison of versions | caught (2) |
| 5.8 | Check only for members | caught |

### 5.6 Final fix wave

| Item | Mutation | Outcome |
|---|---|---|
| I-1 | `# {alias}` back into the board title | caught (60 of 66) |
| I-1 | `for {alias}` back into the description | caught (60 of 66) |
| I-2 | M1: no lock in `OnFrame` | caught |
| I-2 | M2: no generation check | caught |
| I-2 | M3: no `_ended` check in `TryApply` | **survived** (defence in depth) |
| I-2 | M4: no name kept on apply | survived at first in the class run; test made deterministic, then caught |
| I-2 | M5: presence not cleared under the apply | caught (3) |
| Fail closed | Null fallback put back | caught (1) |
| Fail closed | A member treated as the owner | caught (58) |
| `BoundFilter` | Restriction removed | caught (3) |
| `BoundFilter` | Restricted to `pending` instead | caught (8) |
| Gone owner | `Owner()` back to `Quoted(display ?? "")` | caught |
| Gone owner | `AtStart` made the identity | caught |
| Gone owner | `TheOwner` always with parentheses | caught |
| Gone owner | `EncryptedNoteStore` alone reverted | caught |
| Gone owner | Web `ownerAtStart` made the identity | caught |
| Device on `Board.md` | `Capped` removed in `CredentialAccess` | caught |
| Rule 2 class | Provider-seen filter made id-only | caught |
| Rule 2 class | Alias rename without the `Member == memberId` clause | **survived** |
| Rule 2 class | Alias rename without the member clause and without the slot lookup | **survived** |

`Mcp/Notices.cs`'s gone-owner path has no mutation of its own. It is covered by the `NoticeFor` unit assertions.

### 5.7 Survivors and how each was handled

| Where | Mutation | Handling |
|---|---|---|
| 0.9 | `FileApi` PUT skips `Refuse` for an existing file | `AttachmentWriter` refused again with the code. Added `A_read_key_replacing_a_file_is_refused_before_its_bytes_are_looked_at` (non-image bytes: 403 against the mutant's 400), amended. Now caught. |
| 1.11 | Member reference with the credential id | `WriterRefPayload.From` already drops the id when sealing. The test now also checks the caller's `ReferenceIn` and the in-memory entries; amended. Now caught (36). |
| 3.3 | Claim without `expiresAt > now` | At first caught only by the peek test, because the invite slot's expiry also refused. Added the membership-only-expired case. Caught by both. |
| 3.5 | The atomic step keeps the pin | The cleanup unset it too. Added `Assert.Null(doc.Pin)` after a failed cleanup; amended. Now caught. |
| 3.8 | No `cleanedAt` guard in the cleanup | The sweeper's query also filters on it. Left as redundant protection; caught when both guards go. |
| 3.5–3.8 fix | Hub without the live-membership guard | The retry test was strengthened with a socket assertion. Now caught. (Phase 5.2 later replaced the guard by matching on the ticket's membership.) |
| 3.14 | A mis-built ticket mutation | It refused more, not less, so it couldn't fail the test. Replaced by two correct mutations, both caught. |
| 3.23 | `RenameAliasAsync` not bound to the member | **Equivalent mutant.** The member slot is looked up under the member's own lookup, so only her own slot matches. Kept as defence in depth. |
| 4.1 | "Bob's key revoked whole", as worded | **No-op in this design**: `EndMembersCredentialsAsync` runs first and has deleted Bob's slots. The two variants that do break it (3b, 3c) are caught. |
| 4.5 | Only the in-transaction deletion check | The in-transaction check alone also refuses, and nothing is deleted. The check before saves a live run from being tombstoned and interrupted before the refusal. That cost is untested. Accepted. |
| Final, I-2 M3 | No `_ended` check in `TryApply` | A closed socket sends nothing and leaves the hub list when its loop ends, so nothing observable changes. Judged equivalent. |
| Final, I-2 M4 | No name kept on apply | At first it passed only by timing. The test now waits for Bob's own presence echo. Now caught. |
| Final, rule 2 class | Alias rename without the member clause (with or without the slot lookup) | **Survives by design.** The alias key opens only with the caller's own user key (`OpenAliasKey`), so a foreign call fails with a `CryptographicException` and answers 404. Judged equivalent. |
| 2.1 (not a survivor) | No `RequireOnCurve` on unwrap | Caught only through the check's message, because OpenSSL's `ImportParameters` refuses the point too. Safe on Linux without the check; not provably so on every platform. |

---

## 6. Deviations from design or plan, and the calls made

### 6.1 The controller's rulings (every `Ruling:` line of the ledger)

| # | Ruling | Why | Cost if wrong |
|---|---|---|---|
| R1 | Dispatch tasks in batches of 2–4 adjacent tasks of one phase per implementer, one review per batch | 80 tasks × (implementer + reviewer) doesn't fit one night; each task still gets its own commit, tests and mutations | A review catches less per task |
| R2 | Push every commit to `feature/collab` and mirror it to the session's branch | The brief names `feature/collab`; the harness names the other | One extra branch to delete |
| R3 | The plan's phase-1 stand-in member slot (under the member's user key) accepted, replaced in phase 2 | The design gives phase 1 member slots but keypairs only in phase 2; the branch is never released between phases | One format to remove in 2.3 (removed; a test shows a stand-in slot opens nothing) |
| R4 | The planner's conflicts list resolved by planner decisions 1–13, accepted as rulings. The list: design path/line drift, connections' rights meta, writer kind `user` rename, `mv` target check, pairing as maintain, `RevokeKeysWithoutVaultsAsync` visibility, refusal sentence for sessions, dev-token duplicate names | The plan was written by one agent from the spec | Not stated |
| R5 | Dispatch the next batch while the previous batch's review runs | Wall clock; reviews are read-only and work from a diff file | A fix round lands on top of the next batch's commits (this is what happened, as separate fix commits) |
| R6 | Board card operations (archive or move of a card) stay `write`, even when the board service rewrites a link inside `Board.md` | Concept §7 lists card operations as write; the rewrite is server-made, not a structural edit | A writer can cause a server-made link-path change in `Board.md` |
| R7 | A single-note restore of `Board.md` from a checkpoint answers with the checkpoint sentence | Consistency with §3.3 | None |
| R8 | The member limit counts `pending` + `active` only | The design's "non-revoked" read as "not ended"; counting leavers would block an owner forever | A vault can accumulate more tombstones (harmless) |
| R9 | (Implementer's, accepted) Derived credentials seal a random alias key, not the alias; the alias is read from the member slot on each opening | A rename reaches every credential at once; the server can't rewrite credential slots | One indexed query per member credential request |
| R10 | The design's alias wording is amended by a dated "Amendments during the build" note (design §14), not a silent rewrite | The design is the reviewed source and must match what is built | Chris may prefer a separate deviations document |
| R11 | The two phase-2 review fixes went to the running 3.1–3.4 implementer as a separate commit (`e42606a`) | One writer in the tree; it touched `GrantAsync` anyway | The fix is reviewed in 3.1–3.4's review rather than a scoped re-review (it was reviewed there and found addressed) |
| R12 | HARD RULE 3's source-scan test accepts `AcceptAsync` as a second `MemberSlot` caller | Design §4.2 and §5.4: acceptance is where member slots are created, from the key just unwrapped; the finish never creates them | A second creation path to keep watching |
| R13 | (Implementer's, accepted) Peek and accept at `/api/vault-invitations/{peek,accept}` | `/api/invitations/peek` is taken by account invitations (the two collided: AmbiguousMatch, 500) | The design's route names differ; the web tasks use these |
| R14 | The redeemer's identity is shown only while the membership is pending, with confirm, and redeemed | Design §4.2 | None |
| R15 | The unique `(vault, member)` index covers live memberships (pending, active) only | Tombstones must not block a re-invite; the design's index text was written before declines kept `member` | Duplicate tombstones per person per vault (the owner sees the history of each) |
| R16 | `VaultView` gains `ownerUsername`, and groups key on it | The member already sees the username at peek (§4.2) | The owner's username is visible in the member's vault list (already shown at acceptance) |
| R17 | An admin's deletion of an owner whose shared vault has a re-key run in progress deletes that vault at once, with member notices, whatever the option | No closing period is possible without the key the run holds | Members lose the 7-day window for that vault (they couldn't export anyway) |

### 6.2 The planner's decisions (plan header, accepted under R4)

| # | Decision | Why | Cost if wrong |
|---|---|---|---|
| 1 | Code in `src/Kumbu.Server/Sharing/`, catalog slot methods in `VaultCatalog.Members.cs`; vault invitations as `VaultInvites` methods on `Memberships` | Keeps account invitations' `Invitations.cs` as it is | A rename, if another layout is wanted |
| 2 | `AccessLevel { Read, Write, Maintain, Owner }` in Core; `KeyRight` gains `Maintain` | One ordered level for every caller | — |
| 3 | `ShellResult.Code`; REST `ApiError.code` beside `type` | Machine-readable refusals (§3.5) | — |
| 4 | A member's session gets "you can only read vault X"; keys keep "this key can only read …"; same `code` | A session is not a key | Wording only |
| 5 | The phase-1 stand-in member slot | See R3 | — |
| 6 | Member writer references and `WriterLabels` member columns in phase 1, not 3 | Members can write from phase 1, so hard rules 4 and 5 must already hold | — |
| 7 | Owner-only gaps closed in phase 1: embeddings `isOwner`; pairing a shared vault refused until 5.8 | They open as soon as members reach vaults | — |
| 8 | Writer kinds: `self` for the viewer, `user` for another person | Design §8.2's meaning differs from today's `user` | Clients reading `user` as "me" break (the web app changed in 0.14) |
| 9 | "Last use on this vault": `usedAt` on the derived slot, at most once a minute | Nothing recorded it | One write a minute per slot (3.9 moved it from opening to the vault's actual use) |
| 10 | Connection metas get the same `v: 2` rule as API key metas | Connections seal rights text too, which design §3.3 doesn't name | — |
| 11 | `Vaults.MemberLimit` (default 20, `KUMBU_VAULT_MEMBER_LIMIT`); pending limit 10 a constant | Configurable where an instance may need it | — |
| 12 | Two vault ids under one caller name throw (500, ids only), for every credential including the dev token | The safer reading of design §3.2: fail loudly rather than hide a vault | A database that already has two dev-token vaults of one name fails loudly |
| 13 | `security-architecture.md` §9 left alone until the release | Design §12 | §9's wording was adjusted in the final docs commit (sharing built; plugin 0.2.0 the remaining prerequisite) |

### 6.3 The implementers' main decisions

**Phase 0**

| Decision | Why | Cost if wrong |
|---|---|---|
| Refusal sentences verbatim, with literal `*vault*` asterisks, in shell, MCP and REST `error` | Verbatim from the brief and design | If the asterisks were meant as italics in the design only, one change in `Refusals` |
| Extra structural checks: folder `replace` reaching `Board.md`; `mv` link rewrites in `Board.md`; per-note refusal in `UploadApi` (a writer could upload `Board/Board.md`); the restore check before the note/folder branch | Safer reading of "by any command" | A writer whose `replace` or `mv` touches `Board.md` only by a link gets the somewhat indirect board sentence |
| `SameRights` in `OAuthTokens` also compares the meta's `v` | A legacy `write` (really maintain) and a new `write` would otherwise look alike in a racing consent | Untested line (deferred minor) |
| `WriterRef.Unknown` and `Legacy` are sentinel instances; null means "no reference given" and is refused | With null for both, a v2 note without a ref couldn't be moved, and fail-closed couldn't tell a forgotten path from a stored unknown entry | — |
| The writer reference is an explicit store argument, not a `NoteBatch` property | The compiler finds every call site | About 100 test call sites changed mechanically |
| "Suggest write": new keys and connections still start with no vault, never preselect maintain, plus a hint | Preselecting would hand a new key every vault | An interpretation of the design's "suggest write" |
| The device dialog states the device's level (maintain) rather than offering a choice | The pairing request carries no right; devices pair as maintain | A level choice for devices would need a server change |
| Kumbu's own scheme's warning colour is coral (`#ff9466`), not amber | Kumbu's accent is already amber | Cosmetic (deferred minor) |

**Phase 1**

| Decision | Why | Cost if wrong |
|---|---|---|
| The membership is bound to vault **and** member, not just checked for `active` | Otherwise a slot pointed at someone else's active membership (a database write) reopens a revoked member's vault | — |
| `RekeyPendingFound` fires only for the owner's `user` slot | A member's session would otherwise start the owner's run with the member's key | — |
| The membership id (`via`) is sealed into derived slots' payloads; `SameGrant` requires `grantedVia == via` | A database writer could otherwise unset or repoint `grantedVia` | — |
| The role cap for keys and connections came in 1.6, not 1.8 | 1.6's test needs it | — |
| `MembershipCheckTests` moved to the app collection | One hard-rule class holds both parts (a) and (b) | — |
| Path translation at `EncryptedNoteStore`'s edges (and `SyncApi.VersionsAsync`) | Stored paths carry the owner's vault name; aliases need translation in and out | Links inside note **text** are not translated (open point 8.1) |
| A vault name two vaults share gives no level (a phase-0 test changed) | The store refuses such a request anyway | — |
| Sessions open their vaults lazily, once per request, and have no level before | Opening in the middleware would load every vault on every session request | — |
| `RecordCredentialAsync` and `DeleteOfMemberAsync` are static with `db` | Avoids new constructor parameters on classes tests build by hand | — |
| `WriterIn`/`ReferenceIn` instead of `Writer(vault)`/`Reference(vault)` | C# name clash with the existing properties | — |
| `Writers.Marked`: no tool mark on an empty label | Members' entries would otherwise store " (mv)" | — |
| Writer views: a revoked tombstone renders as "(removed)"; the owner's labels and member credential names sanitised but not quoted; only the owner's display name to members quoted | Matches the brief's "Bob · Claude" | The quoting of member credential names is left to the spec author |
| The renderer works from derived membership keys on `UnlockedVault`, never the vault key | Least privilege for the rendering code | — |
| The changes banner shows only the viewer's own agents | Design: "keeps showing only the viewer's own" | — |
| Activity row keys are an HMAC under the viewer's id; rows without a changeset grouped by reference | Otherwise every member storing `""` would share one row | Raw changeset ids are still sent (deferred minor) |
| Re-keying and pairing a shared vault answer 409 (phase 1) | The refusal is about the vault's state, not the caller's right | — |
| The embedding endpoint's member answer is 404 "You have no vault named …", checked before the provider is validated | A member learns nothing from the provider list | — |
| The 1.15 tests live in `Sharing/MemberDeletionTests` (app collection), not `Accounts/DeletionTests` | The third test needs REST writer views | — |

**Phase 2**

| Decision | Why | Cost if wrong |
|---|---|---|
| `PinnedKey` is synchronous | Opening a pin needs no read | — |
| A damaged pin deletes the membership's **member** slots only, not its `GrantedVia` slots | The design says "a member slot without a valid pin is deleted" | Derived slots outlive a damaged pin until other cleanup |
| The user-private-key AAD includes the public key's hash | The design gives two AAD forms (§5.1 first and third paragraphs); the brief names the hashed one | — |
| The rotation's pin check also requires the pin to name the **old** public key | A valid-looking pin for another key is exactly what an ex-member with database write access would forge | — |
| A new keypair is made on every rotation, even without an old one | Closes H1 (old private key leak) | — |
| `Share.SetRoleAsync`/`SetStatusAsync` (test helpers) raise `revision` | As the design's routes do; otherwise a role change couldn't conflict with a rotation in tests | — |

**Phase 3**

| Decision | Why | Cost if wrong |
|---|---|---|
| **Route names** `/api/vault-invitations/*` | See R13 | — |
| Creating an invitation needs the password in the body **and** the open window | Strict reading of the design | A remembered session with a closed window confirms first |
| Wrong password on invitation creation → 400 | What vault deletion actually answers (the brief said "403/401 as vault deletion") | — |
| Accept is session-only without the password window | The owner's secret is the grant | — |
| Inactive owner or closing → 409 with `code` on peek and accept; every other unusable link the uniform 404 | The secret holder may know why | — |
| `GET /api/vaults/{vault}/memberships/{id}` added | The confirmation dialog needs the redeemer before granting | — |
| Confirm's own answer carries no redeemer either | Strict reading of R14 | — |
| **Unique index on live memberships** (`vault_1_member_1_live`); the old index dropped at start | See R15; recorded as a design §14 amendment | First index change in the codebase; runs once per process |
| One vault-name rule (`Api/VaultNames.Check`) for vault creation, acceptance and alias rename | Aliases and own vault names share one name index | — |
| Revoke and leave answer 204; a cleanup failure after the committed atomic step still 204, finished by the sweep | The atomic step is what revokes | — |
| New connection reason `sharing_ended` | Clearer than reusing `vault_deleted` | — |
| Changing to the same role is a 200 no-op | — | — |
| New `GET /api/vault-events` (a member's own feed across shared vaults) | A revoked member could otherwise never see "removed" | — |
| Extra event kinds `invitation-declined`, `invitation-expired` (owner only); `provider-changed` stores no provider name | Owner's information; the provider name stays sealed | Event fields beyond §8.3's list (`role`, `until`, later `credentials`) |
| Notices only for API keys and connections reaching the vault through a membership; at most 20 per call; 30-day marks | Design §8.4 | Two concurrent calls of one credential can carry the same notice |
| Search consent: `providerSeen` absent counts as not consented; the body names the provider the member saw | Fail closed; an OK for a replaced provider consents to nothing | — |
| `members` counts active memberships only | Pending people see the provider at acceptance | — |
| Member export events recorded before the first byte, one per shared vault | The owner learns of every export that started | A cut-short download still counts |
| Owner inactive: `ReadOnlyCode` through `Refuse`, plus `CheckWritable(vault, access)` in the store for every write | A route that skips the caller's check still refuses | — |
| An owner whose user document is missing counts as inactive | Fail closed | — |
| Indexing halt for an inactive or closing owner is per process | The opening methods mark as they read the flag | Multi-process deployments keep indexing queued work until a member request comes (deferred minor) |
| The deletion receipt fields are the admin's; self-deletion stays 204 | The brief names the admin's receipt | A member deleting her own account never sees the §7.2 sentence (open point) |
| Only vaults with an **active** member get a closing period | A vault with only open invitations has no one to give time to | — |
| `DeleteVaultsAsync` deletes the vaults' memberships | Design §2.1; before, they stayed active pointing at a deleted vault | — |
| The member's `closing-started` text says "The owner deleted their account" | The deleted owner's display name no longer exists | — |
| Alias rename route `PUT /api/memberships/{id}/alias` added (3.23) | The 3.23 brief assumed a route existed; only the catalog method did | — |
| Web: owner names grouped by `ownerUsername` (R16) | Two owners can share a display name | — |
| Web: Write mode stays enabled while the level is unknown; dock and tree hide write actions until known | Disabling on every note load broke focus behaviour and flashed | The server enforces either way |
| Web: the mixed warning counts an own vault whenever it is `shared` | The dialog can't see the members' roles | A false positive for an own vault shared only with readers |
| Web: capped display uses the raw level word ("write, acts as read …") | As the design writes it | — |
| Web: refusal sentences in plain text (no italics) in `title` attributes | A `title` can't hold italics | — |
| Web: the share dialog has no leave guard | A lost link costs one withdrawn invitation | — |

**Phase 4**

| Decision | Why | Cost if wrong |
|---|---|---|
| The phase-1 refusal of shared re-keys stayed behind a switch through 4.1 and 4.2, and went in 4.3 (with `SharedRekeyRefusalTests`, not in 4.1 as the plan says) | 4.3 is the task that makes the whole run correct | — |
| The finish's revision filter is against the revisions the start recorded; a conflicting change makes the run re-read and retry the finish itself, up to 5 times | A filter against its own in-transaction read would add nothing | After 5 attempts the vault stays pending |
| The resumed start runs the memberships' part again (idempotent) | It returns fresh revisions for the finish | — |
| Non-active member slots deleted at the start and the finish | They hold only the old key | — |
| Members' connections lose the vault at the start; the owner's are disconnected whole | §13 L9's connection reading | — |
| A membership whose pin is invalid stays active without a slot, pin unset, logged as damaged | The owner sees it and can revoke it | It counts towards the limit (deferred minor) |
| **Admin deletion during a re-key is not refused**; with R17, the vault is deleted at once | An owner who never signs in again could otherwise block a privacy deletion forever | Members lose the closing period for that vault |
| "Shared", for the deletion refusal, means at least one active membership | Pending ones are withdrawn or declined at the start | — |
| Agent notices for a finished re-key go to the credentials the start took off the vault, kept on the vault (`rekeyEnded`) and moved into the event | The existing notice path can't reach credentials that lost the vault | Credential ids are stored on the event document, which §8.3 doesn't list; the notice can't name the vault |
| `rekey-finished` recorded for any vault ever shared | It ends the leave offer | — |
| The leave offer lives in the owner's access list (`left` on `/access`); the revoke dialog checks the password before revoking | The web app shows no vault events yet; a wrong password revokes nothing | — |

**Phase 5**

| Decision | Why | Cost if wrong |
|---|---|---|
| The session subject is the slot's lookup value, not the secret; `Sessions.AliveAsync` never touches the session | The hub keeps no usable secret; L7 | An idle-ended session's socket can close up to 30 s late |
| Vault sets only shrink or re-level between tickets; a key's socket never rises above its ticket's level | Aliases are sealed and the hub holds no key | A vault gained since the ticket comes with the next ticket (the web app reconnects for it) |
| Presence only to session subjects, not to plugin keys | Fewer receivers of note paths | — |
| The `editors` format: display strings, anonymous ones collapsed into the last entry | §8.2 | — |
| Re-invited members' sockets matched by the ticket's membership id | Replaces the earlier "another active membership" guard | — |
| Fail-closed close code 1013; presence cap 8 notes per connection, new paths beyond 8 ignored | The web app reconnects on every code but 4409 | — |
| Hints don't invalidate an open search or the board | A hint every 500 ms would re-send semantic queries | — |
| A refused save puts the note into read only until the next vault list | The list is then authoritative | Only the editor's own save triggers it |
| **Plugin refusal**: 409 `type: plugin-too-old`, no refusal `code`; a pre-release of 0.2.0 is too old (semver); the check runs before the code is consumed | Refusal codes are reserved for write refusals; the code stays usable | A guesser learns a code exists, but only one they already hold |
| Task 1.14's refusal of creating codes for shared vaults removed | The redemption check replaces it | — |
| The 5.1–5.3 commits were staged out of one hub implementation written to the final design, with red verified per stage | Process deviation, reported | — |

**Final fix wave**

| Decision | Why | Cost if wrong |
|---|---|---|
| **Board title "Board"**: a new board is titled `# Board`, "Kanban board of this vault.", whoever creates it | The owner's stored vault name would need a new store query and would put the owner's name into text every member reads | **A visible change for every user**, not only on shared vaults: the owner's new boards no longer carry the vault name. Belongs in the release notes |
| "the owner" for a gone owner (it rendered as `"someone"`, quoted) | Reads as a made-up name otherwise | — |

### 6.4 Design §14 amendments

All dated 2026-10-31, under "Amendments during the build", with "(amended in §14)" pointers on the slot-table row, §2.3 and §7.3:

- **Alias-key scheme.** The member slot holds a random alias key in `aliasKey`, sealed under `HKDF(userKey, "kumbu/v1/alias", vaultId)` with AAD `alias-key:{vault}:{membershipId}`. The alias is sealed under the alias key with AAD `alias:{vault}:{membershipId}`.
  - Derived slots carry a copy of the alias key, and read the alias from the member slot at each opening.
  - A rename or a rotation touches only the member slot.
  - The owner can't read the alias.
  - `member_notices` copies the alias ciphertext together with its `aliasKey`; a rotation reseals the notice's key.
- **The unique `(vault, member)` index** covers live memberships only.

---

## 7. Surprises

**Environment**

- Testcontainers' `nofile` 64000 was refused (section 2), and the first run had 1130 failures.
- mongod died with "Too many open files" as databases piled up. Until that was fixed, every early full run lost one or more tests to dropped connections or transaction-number mismatches.
- The reset script's leftover volumes filled 26 GB of disk.
- The test filter: namespace with class ANDs, and a mid-name glob runs nothing.

**Process**

- `git checkout -- <file>` restores the committed state, so it silently dropped uncommitted green work during mutations. This happened in 0.10 (`UnlockedVault.cs`), 0.14 (`activity.ts`), 3.3 (`VaultInvites.cs`) and 5.8 (`Pairings.cs`). Each was redone and re-verified. From 3.5 on, mutations ran through a helper that restores from a backup copy.
- In 2.1, mutations piled up because `git checkout` can't restore an untracked file; they were rerun one at a time.
- Many reds were compile errors against new APIs, not behavioural failures (0.10, 1.5, 1.7, 1.11, 2.1, 2.6, 3.10–3.16, 4.1, 4.6). The mutations were used as the red evidence.
- Some full suites ran just after the last commit rather than before it (2.4, 3.20–3.22, 3.23–3.25). No code changed in between.
- The 3.11 commit (`54a7192`) left `PasswordWindowTests.Every_session_only_endpoint_is_guarded_or_deliberately_open` red until 3.12 listed the new route. History was not rewritten.

**Code**

- **A member's vault notes are sealed under paths with the owner's vault name** (`PathId = MAC(path.Value)`). Aliases needed a translation layer at the store's edges.
- **A plain password change keeps the user key.** Only "Re-encrypt my vaults" and recovery rotate it, so tests of "password change with re-encrypt" call both.
- **OpenSSL refuses an off-curve point on import.** So mutation 2.1/3 is caught only through the check's own message.
- **Route collision.** `POST /api/invitations/peek` already existed for account invitations; the design's route gave an AmbiguousMatch 500.
- **OpenAPI schema name collision.** The sharing records `PeekInvitationRequest` and `InvitationPeekView` shared names with the account invitations' records, so `schema.d.ts` had the wrong peek shapes. The records were renamed, and the test now pins it.
- **`usedAt` measured the wrong thing.** It was written whenever a credential's vaults were opened, so a use of a member's own vault moved the shared vault's "last use". It is now written on the vault's actual use.
- **`KumbuAppFactory.IndexedAsync()` waits on the factory's own host, not `PublicApi`'s.** Other tests using that pair may race.
- **`CountLockedAsync` counted closing vaults** (no user slot left) as dev-token vaults that don't open. This was a real bug of 3.17, found by the full suite.
- **Hosts kept from `StartupValidationTests`** (metrics on, MongoDB unreachable) froze the sharing gauges for 30 s per scrape. This was a real weakness of 3.18, fixed with a 2-second limit per database.
- **`NoNamesTests` could flake.** The marker "Bobs" can appear in base64 case-insensitively. The markers now contain `.`, which is outside both base64 alphabets.
- **`from` is a contextual keyword in C#.** `from = cond ? from with {…}` parses as a LINQ query.
- **4.4 needed no production change.** The guard had covered member slots since 2.3.
- **4.1's mutation 3, as worded, can't break anything** in this design.
- **The gone owner rendered as `"someone"`** (quoted), not `""` as the final review expected.
- **jsdom's `WebSocket` had to be removed** in the web test setup, so existing tests run with the socket down and the poll running.
- **Presence state is last-state-wins.** An `off` sent soon after an `on` can replace it before it is sent, so tests wait for the `on`.

---

## 8. Open points for Chris

### 8.1 Open points from the ledger

| # | Open point | Status at `14fa84a` |
|---|---|---|
| 1 | Links inside note text aren't translated to the member's alias. An absolute intra-vault link shows the owner's vault name, and is dead for a member whose alias differs | Open (functional; final review: "can wait") |
| 2 | `board create` wrote the creator's vault name (a member's alias) into `Board.md`'s title, readable by the owner and other members | **Fixed** in `948d7b6` (title "Board"); see 8.2 |
| 3 | Users have no free-text display name, so "Bob Example (username bob)" can show the username twice | Open (product) |
| 4 | A member deleting her own account gets no receipt, so never sees the §7.2 sentence | Open (the design names the admin's receipt; self-deletion stays 204) |
| 5 | The closing banner's wording uses the server's text; design §7.3 only points to §8.4 | Open (wording) |
| 6 | Admin deletion is not refused during a re-key run (the design names the owner only; refusing the admin could block a privacy deletion). With R17, such a vault is deleted at once | Ruled and documented in `security-architecture.md` §3; please confirm |
| 7 | Shared vaults can't be paired until plugin 0.2.0 ships (0.1.x sends no `plugin` field) | **Release prerequisite**; see 8.2 |

### 8.2 The final review's must-fix-before-release items

1. **The alias in `Board.md` (I-1).** Fixed (`948d7b6`). The alias is now forbidden in `NoNamesTests` and `MarkerTests`.
2. **Plugin 0.2.0 shipping decision (for Chris).** This is no code fix. Either ship `kumbu-obsidian` 0.2.0 first or together with 0.3.0, or state in the release notes that devices can't pair to shared vaults yet. Please decide before tagging.
   - A 0.1.x device paired before the vault was shared keeps syncing, and names its device in conflict copies. The security architecture now says so, with the remedy.
3. **Doc drift.** Fixed (`14fa84a`): `security-architecture.md:140` (the alias), `:219` ("phases 1 and 2"), `:528` (line references), and more stale references in §3. The fix report did not re-verify line references outside the sharing and pairing sections.
4. **The owners' default board title** changes from the vault's name to "Board", for every user. This is a visible change, so it belongs in the 0.3.0 release notes. The re-reviewer suggested a better fix, which I didn't build so as not to open a second fix wave: drop the H1 from the board template. The board API already falls back to the caller's own name for the vault when there is no title, so the owner would see her vault name and a member their alias, with nothing stored. Also worth a release-note line: boards created before this change keep the creator's name for the vault as their title; nothing migrates them.

### 8.3 The final review's "recommended before release"

| Item | Status |
|---|---|
| I-2, the socket recompute race; and 5.1–5.5 minor 2 (`OnFrame` check-then-set) | Fixed (`26605e5`) |
| `WriterIn`/`ReferenceIn` fail closed without an access record | Fixed (`77983bd`) |
| Alias and provider-seen in `BoundMembershipWriteTests` | Done (`963b547`; two mutations survive by design, section 5.7) |
| A member-connection row in `NoNamesTests` | **Not done**: no test writes through a member's OAuth connection. Safe by construction, per the review (same `ReferenceIn`; a missing writer id is a 500, never a name) |
| `BoundFilter` lets any status move to itself | Fixed (`8069dc8`) |
| Empty quoted owner name for a closing or deleted owner | Fixed (`e4fdba0`): "the owner" |

### 8.4 Deferred minors worth a decision

None of these has a confidentiality or authorisation effect, per the final review's triage. The rest of the deferred minors (naming, wording, test boilerplate, untested branches) are in the ledger and the review files.

| Item | Source |
|---|---|
| `Deletion.cs:219`: the shared list is read before the transaction, so an acceptance in between loses that member's closing period (a narrow window) | 3.17–3.19 review |
| Indexing stops per process only, for an inactive or closing owner (`Deletion.cs:283`; 3.15 decision 8): matters for multi-process deployments | 3.13–3.16 and 3.17–3.19 reviews |
| No presence frame rate limit: CPU fan-out by a write member | 5.6–5.9 minor 10 |
| Undisposed `PublicKey` handles: a native leak per wrap | 2.1–2.4 review |
| A forged membership stays active without a slot and counts towards the limit; consider an owner event | 4.1–4.4 review |
| A removed member whose cleanup is still pending can learn that a re-key happened: a small information leak, judged tolerable | 4.5–4.7 minor 5 |
| `PreparedRotation` (a public record) holds the member's shared vault keys in memory until collected | 2.5–2.7 concern |
| A refused restore apply burns its plan token | 0.5–0.9 review |
| Raw changeset ids are still sent in activity (`row.Id`) and history (`changeset`): random per changeset, not per person | 1.9–1.12 concern |
| The `CancellationToken.None` post-commit steps have no direct test; neither has a member's OAuth refresh after a revoke; `One_membership_query_per_request` counts `op: query` only | 3.5–3.8 fix, final review |
| The third `RunStartedException` path of an admin's deletion is a 500 | 5.1–5.5 minor 5 |
| Notices (and lost notices) can be told twice when two calls run at once, or on several server processes | 3.12, 4.6 concerns |
| The member panel's rename/leave follow-up runs only while mounted | 3.23–3.25 review |
| The mixed-credential warning is a false positive for an own vault shared only with readers | 3.23 decision |
| Quoting of member credential names in the owner's view ("Bob · Claude" unquoted) | 1.9–1.12 minor 3, left to the spec author |
| Refusal sentences carry literal `*vault*` asterisks | 0.5–0.9 decision |

### 8.5 Specs and plan text that looked wrong

**Design path and line drift** (listed by the planner and resolved in its decisions):

- The design writes `Kumbu.Core/…` for `src/Kumbu.Core/…`.
- Several line references point elsewhere. For example, the design points at `web/src/api/activity.ts:56` for self-detection; the actual comparisons were in `AgentBanner.tsx:51`, `NoteView.tsx:311` and `ChangedElsewhere.tsx:67`.
- Design §3.3 names the API key's rights display copy but not the connection meta, which seals rights text too (decision 10).
- `writerKind: "user"` means something else in the design than in the code (decision 8).

**Contradictions inside the design:**

- **§5.1** gives two AAD forms for the sealed private key (`user-private:{userId}`, and the hashed form). The hashed one was built.
- **§2.1's unique `(vault, member)` index and the tombstone rule contradict each other.** Declined, revoked and left tombstones keep `member`, so a declined person could never be invited again. Amended in §14 (R15).
- **§2.3 and §14 said the owner can't read the alias**, while `board create` wrote it into `Board.md`. Fixed in code; the documents now hold.
- **Route names.** The design's `/api/invitations/peek` collides with the existing account-invitation route (R13).
- **§8.3's event fields** (vault, kind, membership, at) are fewer than the build needed: `role`, `until`, and the re-key's `credentials`.
- **§7.1** names only the owner for deletion during a run (open point 6). **§7.2** names only the admin's receipt (open point 4).

**The brief and the plan:**

- The plan's task 3.1 says a wrong invitation password answers "403/401 as vault deletion". Vault deletion actually answers 400.
- The 3.23 brief assumed an alias rename route existed. Only the catalog method did, so the route was added.
- The plan deletes `SharedRekeyRefusalTests` in 4.1, but its tests stayed true until 4.3.
- The plan says "Skipped stays at 4 throughout"; here it is 6 (section 2).

**Pre-existing stale references in `docs/security-architecture.md`:** `Consent.tsx:65` and `Keys.test.tsx:197` were already out of date before this run; fixed in 0.16.

**Specs outside this topic:** none was recorded as looking wrong.

### 8.6 Anything else the reports flag for the maintainer

- **The final fix wave's scoped re-review:** all eight findings addressed, no new Critical or Important breakage. Two Minors: every new board's page heading now reads "Board" (see 8.2 item 4), and the security architecture credits the alias route's binding to `Memberships.BoundFilter`, while `RenameAliasAsync` in `VaultCatalog.Members.cs` checks the member (behaviour right, attribution wrong).
- **`/tmp/kb/mongo-reset.sh`** is outside the repository. Anyone repeating this setup on a host whose hard file limit is below 64000 needs it; its essentials:

  ```sh
  docker rm -fv kumbu-test-mongo
  docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 \
    --replSet rs0 --bind_ip_all \
    --wiredTigerEngineConfigString "file_manager=(close_idle_time=5,close_scan_interval=2,close_handle_minimum=250)"
  docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate({_id:"rs0",members:[{_id:0,host:"localhost:27017"}]})'
  export KUMBU_TEST_MONGO="mongodb://localhost:27017/?replicaSet=rs0&directConnection=true"
  ```
- **`KumbuAppFactory.IndexedAsync()`** waits on the factory's own host, which may make tests that use `app.PublicApi` race (section 7).

---

## 9. The pull request

PR: (added after the pull request is opened)

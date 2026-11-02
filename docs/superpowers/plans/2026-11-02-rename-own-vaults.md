# Renaming one's own vaults, and three small things: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (or, if it is unavailable, emulate it with the plain Agent tool). One fresh subagent per task, then a spec-compliance review and a code-quality review, each by its own subagent. Subagents do red, green, commit and mutations; they never merge, push or switch branches. The controller pushes. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Owners can rename their own vaults, by a display name sealed in the vault's meta. Three leftovers of the collab test ship with it: "Leave this vault…" in the vault menu, a changeset when restoring from the trash, and a notice for members whose credentials a re-encryption ended.

**Architecture:** The stored name in `VaultMeta.Name` never changes, because every stored path and every `PathId` is built on it. A new `DisplayName` is what callers see. `VaultAccess` already translates paths between a caller's name and the stored name for members; the owner's access now uses `Shown` as its name, which turns that translation on for a renamed vault's owner.

**Tech stack:** .NET 10, minimal APIs, MongoDB transactions, xUnit v3 on Microsoft.Testing.Platform, Testcontainers; React with Vite, TanStack Query, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-02-rename-own-vaults-design.md` ("spec §n"), reviewed and approved. **Where this plan differs from the spec, the plan is right:** see "Corrections to the spec". Do not edit the spec.

**Line anchors** were verified at commit `3eae059` (master `4b84fa4` plus the spec). They may drift as you work; the sense counts.

**Nothing was run for this plan.** The planning machine has no dotnet, node or docker, so no dry run was possible; the baseline comes from CI, and the code below has never been compiled. If a signature here doesn't compile, fix the obvious thing and say so in the report.

## Corrections to the spec

Checked against the code. The spec's decisions stand; these are facts it had wrong or didn't reach.

| # | Spec says | The code says | This plan |
|---|---|---|---|
| 1 | §1.3 lists `Api/ConnectionApi.cs:180`, `OAuth/OAuthTokens.cs:162`, `:205`, `Accounts/ApiKeys.cs:416`, `Accounts/Connections.cs:404` as reading the vault's `meta.Name` | Each `meta` there is a `ConnectionMeta` or `ApiKeyMeta`; `Name` is **the key's or connection's own name**. `Shown` doesn't exist on them | **Do not touch these five.** A key or connection finds the vault's name through `CredentialAccess` |
| 2 | §1.3 lists `Sharing/VaultInvites.cs:140`, `:142` | They read `InviteVaultMeta.Name`, which `VaultCatalog.Members.cs:80` fills | Only `:80` changes; `VaultInvites.cs` needs a test and no edit |
| 3 | §1.3 omits it | `VaultCatalog.cs:1179` and `:1184` build `UnlockedVault` from the meta. Without `DisplayName` there, a reload loses the name. In the two-key period (`:1179`) the placeholder would show the stored name; beside a new vault of that name, `EncryptedNoteStore.VaultsAsync:988` then throws for **every** request of the user | Both set `DisplayName`; pinned in Task 4 |
| 4 | §1.3 omits it | `VaultCatalog.cs:907` (`RecoverAsync`) returns `vault.Name` | Uses `vault.Shown` (Task 5) |
| 5 | §1.4 "call `SyncHub.VaultsChangedAsync`" | It only re-checks access. `Connection.TryApply` (`Sync/SyncHub.cs:598`) keeps the name it holds, and nothing is sent. Open sockets would go on naming the old vault | A new `SyncHub.VaultRenamed`, modelled on `AliasRenamed` (`:238`). It renames and hints; the hint makes tabs refetch `["vaults"]` (`web/src/api/history.ts:29`), and `useSyncSocket` then takes a new ticket by itself. `VaultsChangedAsync` is not called (Task 6) |
| 6 | §1.4 step 2 "update `Meta`" | `SealMeta(provider)` (`UnlockedVault.cs:168`) always writes an explicit provider, which would pin a vault that follows the admin's default | The rename opens the stored meta and changes only `DisplayName` (Task 4); pinned in Task 5 |
| 7 | §1.2 "`SealMeta` must keep `DisplayName`" | `SetEmbeddingProviderAsync` (`VaultCatalog.cs:1047`) writes blind from the cached object. Keeping the field isn't enough: an object from before a `Forget`, a `LoadAsync` that read the meta before the commit and publishes after it, or a `ct` cancelling after the commit all leave an object without the name. The next provider change would seal `DisplayName = null`, so meta and slot `nameId` disagree; beside a new vault under the old name, `EncryptedNoteStore.cs:988` then throws for all her requests | A provider change opens the **stored** meta under the gate and writes `meta with { Embeddings = provider }`, conditional on the bytes; `SealMeta(string)` goes (Task 5). The rename holds `vault.Gate` for its transaction. After it, it sets `DisplayName` on the object held, queues for `_loading`, releases the gate, and once `_loading` is its own, so that a load under way has published, sets `DisplayName` on `Current(id)`; after a failed or cancelled transaction it forgets the current object there instead, with a reason that doesn't hold the name. A stored meta that doesn't open is `VaultBusyException.Just()`, not a 500 (Task 4) |
| 8 | §1.8 "a key rotation keeps the index right" | `UserKeyRotation.cs:369-370` guards a member slot against a rename between prepare and apply (`OldAlias`), but not a user slot. A rename in between would get its `NameId` overwritten with a hash of the old name | `SlotRewrap` gains `OldNameId`, checked on user slots (Task 5) |
| 9 | §1.5 "shows only if the vault has device keys, from `GET /api/vaults/{vault}/access`" | `VaultAccessView.ApiKeys` (`Api/NoteApi.cs:47`) is names only, with no kind | The view gains `deviceKeys` (Task 8) |
| 10 | §1.4 is silent on it | An open pairing code holds the vault's name (`Accounts/Pairings.cs:159`), and a device redeeming it after a rename gets a dead name | The rename calls `pairings.DropAll(userId)`, as `NoteApi.cs:339` does for a re-key (Task 6) |
| 11 | §1.2 "a `Shown` property" | System.Text.Json serialises computed properties | `[JsonIgnore]` on `VaultMeta.Shown` (Task 4) |
| 12 | §1.8 "through both a session and a key" for all operations | Activity and export are session-only (`Api/HistoryApi.cs:143`, `Api/ExportApi.cs:44`) | The key's cell for these is "403" (Task 7) |
| 13 | §3 sets the banner's vault in italics | Notices are plain text from the server (`MemberNoticeView.Text`) | Plain text, reusing `VaultEvents.TextFor`'s existing sentence (`VaultEvents.cs:199`), which is word for word the spec's. The owner comes through the membership at read time, so the notice document still names no owner (Task 3) |
| 14 | §1.9 "detected as today" | Tampering is detected, but `vault:{id}` binds no version: an **older** meta of the vault still opens, today too. Someone with database write access can put back an old provider or name | Not fixed here. `docs/security-architecture.md` says so (Task 10) |
| 15 | §1.4 is silent on it | A ticket holds the vault's name when it is issued (`Api/SyncApi.cs:81`); one issued before a rename and redeemed after would name the old vault | `SyncHub.RunAsync` refreshes owner names on registration (Task 6) |
| 16 | §1.5 "shows only if the vault has device keys" | The access list can fail to load | The sentence also shows on an access error, rather than staying silent about a device that breaks (Task 9) |
| 17 | §1.7 step 1 "The dialog names the device" | §1.5's sentence names none | The dialog uses §1.5's sentence and names no device: "Connected Obsidian devices need to pair again." (Task 9) |
| 18 | §1.5 "the device pairs again" | Pairing again makes a new key (`Accounts/Pairings.cs:155`). The rename revokes nothing, so the old device key keeps `Maintain` on the vault, now under the new name | Unchanged, as the rename is to remove no access. A hand check, and stated in the report (final task) |
| 19 | §1.6 "routes carry the vault's name, so it navigates there" | `TreeDrawer` mounts only in `MobileShell` (`web/src/shell/MobileShell.tsx:246,282`), whose routes are `/` and `/notes/:vault/*` (`web/src/router.tsx:113-117`). The board, gallery, checkpoint and view routes have no vault menu | The dialog follows only an open note; on `/` the active vault changes (Task 9) |

Decisions the maintainer confirmed, final: no password for renaming; members aren't told; the old name is free at once; the re-encryption notice only for members whose credentials ended; devices pair again after a rename.

## Rules for the whole run

- Branch `feature/rename-own-vaults`. **Do not merge.** One **draft** pull request to `master`. No tags, no version change (`version.txt`, `src/Kumbu.Server/Kumbu.Server.csproj`, `web/package.json`), no pushes to `master`.
- British English in code, comments, docs, UI copy and commit messages. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`. Match the surrounding naming, idiom and comment density; comments only where the reasoning isn't obvious.
- The repository names no organisation, customer or employer anywhere, including tests and examples. People: Alice (owner), Bob and Carol (members). Vaults: `Gartenarbeit`, `Garten`, `Beet`, `Work`.
- No secrets in logs, assertions, test data or the report. **The new name must not appear in logs.**
- Commit messages imperative and free-form ("Let owners rename their vaults"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- `web/src/api/schema.d.ts` is edited **by hand**, as generated output would have it. Each task that changes a REST shape says how.
- These test classes hold earlier hard rules, and are never weakened: `MembershipCheckTests`, `BoundMembershipWriteTests`, `PinTests`, `NoNamesTests`, `MarkerTests`.
- Not touched: pins, re-keying's key handling, `_loading`'s use in loads and runs. The rename only queues for `_loading` once, after its transaction, with no transaction inside, and lets the vault's gate go as soon as it has queued: a re-key finish of another vault can hold `_loading` for minutes, and this vault's writers, who never need it, mustn't wait on the gate meanwhile.

## How every task runs

1. **Red:** write the tests the task names; run only them. Expected: non-zero exit, the named tests failing for the reason the task gives. A compile error counts as red only for a member the task creates. "Zero tests ran" is a failure of the command, not red.
2. **Green:** the smallest change that passes; the task's command again. Expected: exit 0, 0 failed.
3. **Commit** locally.
4. **Mutations**, one at a time: make the breakage, run the task's command, see at least one test fail, restore with `git checkout -- <file>`. A mutation that survives means the test is too weak: strengthen it and say so in the report.
5. **Reviews, then push:** spec compliance, then code quality, each by its own subagent; fixes go back through 1 to 4; then the controller runs `git push -u origin feature/rename-own-vaults`.

Test filters are Microsoft.Testing.Platform's: `--filter-class "*Name"`, repeatable, ORed. `FullyQualifiedName~` doesn't work, and a glob in mid-name selects nothing.

**Full verification** at each phase gate, all of it, as CI does. With your own MongoDB, give each run a fresh one (`docker rm -fv kumbu-test-mongo`).

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

`pnpm -C web build` is the web CI gate: it runs the type check, which `pnpm test` does not. A recent type error got through the tests alone. Run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.

Test fixtures: `tests/Kumbu.Server.Tests/KumbuAppFactory.cs` (`[Collection("app")]`, `app.PublicApi`, `app.Database`, `app.IndexedAsync()`, `app.Sidecar`), `tests/Kumbu.Server.Tests/Api/RestClient.cs`, `tests/Kumbu.Server.Tests/Sharing/Share.cs` (`ShareWithAsync`, `VaultIdAsync`, `VaultKeyAsync`, `HoldRekeyAsync`, `SetRekeyStartedAsync`), `tests/Kumbu.Server.Tests/Vaults/VaultFixture.cs` (`VaultWorld`, `[Collection("mongo")]`), `tests/Kumbu.Server.Tests/Api/SyncSockets.cs`, `tests/Kumbu.Server.Tests/OAuth/OAuthFlow.cs`, `tests/Kumbu.Server.Tests/CapturedLog.cs`. Core: `tests/Kumbu.Core.Tests/Fakes/`. Web: `web/src/test/app.tsx`'s `fakeApi`, `json`, `noContent`, `renderApp`.

**A trap in `RestClient` after a rename.** `RestClient.Vault` stays the name it was created with. So `Url`, `CreateAsync`, `ReadAsync`, `PutFileAsync`, `KeyAsync` and `Share.VaultIdAsync` / `VaultKeyAsync` all look under the old name once a vault is renamed; with a new vault of that name they silently hit **the wrong vault**. In tests, take the vault id and the keys **before** the rename, and spell every later path out (`/api/notes/Garten/plan.md`).

---

## Task 0: baseline

Nothing here changes code.

- [ ] Tools: `dotnet --version` satisfies `global.json` (10.0.100, roll forward); `echo $DOTNET_CLI_TELEMETRY_OPTOUT` prints `1`; `node --version` is 26; `pnpm --version` is 10.32.1 (`npm install -g pnpm@10.32.1` if missing); `docker info` works.
- [ ] `git log --oneline -2` shows the spec commit on top of `4b84fa4`; there may be a later commit adding this plan and the brief. Those commits hold documents only.
- [ ] Run the full verification.

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

**Expected, exit 0 for each. Not measured in a dry run:** taken from the CI run of the merged branch (`gh run list --branch feature/0.3.0-part-f-g-fixes --workflow CI --limit 1`, then `gh run view <id> --log`), whose commit has the same tree as `4b84fa4` (checked: both `3b208a5c`).

| Suite | Expected |
|---|---|
| `dotnet test` | **total 3264, succeeded 3260, skipped 4, failed 0** |
| Skipped, Core (2) | `UnigramTokenizerTests.MatchesHuggingFaceOnArcticL`, `UnigramTokenizerTests.MatchesHuggingFaceOnTheRealVocabulary` |
| Skipped, Server (2) | `OpenAiCompatibleDriverTests.TensorX_ranks_by_meaning`, `SearchIndexTests.The_real_model_ranks_by_meaning` |
| Web (`pnpm -C web test --run`) | **84 files, 1345 tests**, all passed |
| Sidecar (`pnpm -C pdf test`) | **6 files, 78 tests**, all passed |
| Builds | succeed; `dotnet build` 0 warnings, 0 errors |

Known failures: **none**. CI's log prints no per-project test counts, only the total, so measure the split once and put it in the report: `dotnet test --project tests/Kumbu.Core.Tests --configuration Release --no-build` and `dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build`. Running as root can skip 2 more; note that. Record anything that differs; a real failure here is yours to report before going on.

- [ ] Time budget: setup plus first build and tests about **15 minutes** (the last run needed about 25). If much longer, note it and carry on if you can. If Docker can't run, use `KUMBU_TEST_MONGO` with a `mongod` 7.0 replica set (`ExternalMongo` in `tests/Kumbu.Server.Tests/TestImages.cs`); the brief has the details and the file-handle workaround.

## Phase A: the small things, independent of the rename

### Task 1: "Leave this vault…" in the vault menu (spec §2)

**Files:** `web/src/shell/TreeDrawer.tsx`: imports `:24`, state near `:340`, `vaultItems` `:405`, dialogs `:481`. **Test:** `web/src/shell/TreeDrawerFolders.test.tsx` (beside `:457`), `web/src/shell/MobileShell.test.tsx:684`.

- [ ] **Red**, in `TreeDrawerFolders.test.tsx`, in the `describe` holding `"the owner's vault menu offers Delete vault…, a member's doesn't"`:
  - `it.each(["read", "write", "maintain"])("a member (%s) has Leave this vault… last in the vault menu", …)`: `items.at(-1)` is `"Leave this vault…"`, and `items` still contains `"Shared with you…"`.
  - `"Leave this vault… opens the dialog and leaves"`: choosing it shows the alertdialog named `Leave Work?`; "Leave vault" sends `POST /api/memberships/m1/leave`.
  - `"the owner's vault menu has no Leave this vault…"`.
  - Change `MobileShell.test.tsx:684` deliberately: the expected list ends `"Export as ZIP…", "Leave this vault…"`.
- [ ] **Run:** `pnpm -C web test --run TreeDrawerFolders MobileShell MemberPanel`. **Expected:** the new tests and `:684` fail because the entry doesn't exist; `MemberPanel` tests pass.
- [ ] **Green.** `:24`: `import MemberPanel, { LeaveVaultDialog } from "./MemberPanel";`. A state `const [leaving, setLeaving] = useState(false);` beside `deletingVault`. After the "Delete vault…" line (`:405`), with the same reasoning, away from the everyday entries:

```tsx
...(active?.membership && activeVault ? [{ label: "Leave this vault…", icon: LogOut, onSelect: () => setLeaving(true) }] : []),
```

  `LogOut` is imported already. In `dialogs`, beside `deletingVault`: `{leaving && activeVault && active?.membership && <LeaveVaultDialog vault={activeVault} membership={active.membership} onClose={() => setLeaving(false)} />}`. The dialog leaves the note by itself. The entry in "Shared with you…" stays.
- [ ] **Run:** `pnpm -C web test --run TreeDrawerFolders MobileShell MemberPanel`. **Expected:** exit 0.
- [ ] **Commit:** "Offer leaving a vault in its menu".
- [ ] **Mutations**, each with `pnpm -C web test --run TreeDrawerFolders MobileShell MemberPanel`: offer the entry to everyone (drop `active?.membership`); render no dialog; move the entry before "Export as ZIP…".

### Task 2: A changeset when restoring from the trash (spec §4)

`history`'s help names a changeset for every write, but a restore from the trash sets it to null. Restoring joins the writer's open changeset.

**Files:** `src/Kumbu.Core/Notes/INoteStore.cs:43`, `src/Kumbu.Core/Notes/NoteBatch.cs:39-40` (comment), `src/Kumbu.Core/Notes/NoteWriter.cs:320`, `src/Kumbu.Core/Attachments/AttachmentWriter.cs:244`, `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs:764,779`, `tests/Kumbu.Core.Tests/Fakes/InMemoryNoteStore.cs:250,260`. **Callers to adapt:** `tests/Kumbu.Server.Tests/Vaults/EncryptedNoteStoreTests.cs:324,357,360`, `tests/Kumbu.Server.Tests/Vaults/RekeyTests.cs:620,677`. **Test:** `tests/Kumbu.Core.Tests/Notes/ChangesetRecordTests.cs`, `tests/Kumbu.Core.Tests/Attachments/AttachmentWriterTests.cs:246`, `tests/Kumbu.Server.Tests/Vaults/EncryptedNoteStoreTests.cs`.

- [ ] **Red.**
  - `ChangesetRecordTests.A_restore_from_the_trash_joins_the_writers_changeset`: create `/Work/a.md` and `/Work/c.md`, advance a minute, remove `c.md`, advance a minute, `_writer.RestoreAsync(P("/Work/c.md"), null, Ct)`. The restored note's `Changeset.Id` equals `a.md`'s.
  - `ChangesetRecordTests.A_restore_after_the_idle_time_starts_a_new_changeset`: advance past `caller.ChangesetIdle` first; the ids differ, and the restored one isn't null.
  - `AttachmentWriterTests.Restore_from_trash_joins_the_writers_changeset`: the same for `/Work/x.png`.
  - Change `AttachmentWriterTests.cs:246` deliberately, `Assert.Null(attachment.Changeset)` becoming `Assert.NotNull`; say so in the commit. Any other test that pinned the null changes the same way.
  - `EncryptedNoteStoreTests.A_restore_seals_the_changeset_it_is_given`: restore with a given `Changeset`, read through a new catalog, and the note's `Changeset` is that one.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ChangesetRecordTests" --filter-class "*AttachmentWriterTests" --filter-class "*NoteWriterTests" --filter-class "*ActivityTests"`. **Expected:** the new tests fail with a null changeset.
- [ ] **Green.** `INoteStore.TryRestoreFromTrashAsync(VaultPath path, string? version, string writer, WriterRef reference, DateTime at, Changeset changeset, CancellationToken ct)`; its doc comment names the parameter. `NoteWriter.cs:320` passes `await ChangesetAsync(path, ct)`. `EncryptedNoteStore.cs:779` and `InMemoryNoteStore.cs:260`: `Changeset = changeset`. `AttachmentWriter.cs:244`: `Changeset = await ChangesetAsync(path, ct)`, and `Changeset =` the same value on the `NoteBatch` at `:246`. `NoteBatch.cs:39-40`: the comment's "null for a restore" goes; it is recorded with a restored attachment's batch too. Adapt the callers listed, passing `new Changeset("c1", T0)` or the test's own time.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ChangesetRecordTests" --filter-class "*AttachmentWriterTests" --filter-class "*NoteWriterTests" --filter-class "*ActivityTests"`, then `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*EncryptedNoteStoreTests" --filter-class "*RekeyTests" --filter-class "*ActivityApiTests" --filter-class "*HistoryApiTests"`. **Expected:** exit 0 for both.
- [ ] **Commit:** "Give a restore from the trash a changeset".
- [ ] **Mutations.** Each with `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ChangesetRecordTests" --filter-class "*AttachmentWriterTests"`: `Changeset = null` in `InMemoryNoteStore.cs:260`, then in `AttachmentWriter.cs:244`. With `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*EncryptedNoteStoreTests"`: `Changeset = null` in `EncryptedNoteStore.cs:779`.

### Task 3: A notice for members after a re-encryption (spec §3)

Only for memberships whose credentials actually ended. A member without keys or apps on the vault has nothing to do and gets no notice; the Settings entry stays for everyone.

**Files:**

- `src/Kumbu.Server/Vaults/Documents.cs:27`: new field beside `RekeyEnded`.
- `src/Kumbu.Server/Vaults/VaultCatalog.Rekey.cs:514-531` (`EndMembersCredentialsAsync`), `:555-582` (`FinishRekeyAsync`).
- `src/Kumbu.Server/Sharing/MemberNotices.cs`: `:31` model, `:65` `TextFor`, `:145` the kinds.
- `src/Kumbu.Server/Sharing/SharingApi.cs:300-306` (`NoticesAsync`).
- `web/src/shell/SharedBanner.tsx:78` (comment only).
- `docs/security-architecture.md:179,339,344`.

**Test:** `tests/Kumbu.Server.Tests/Sharing/SharedRekeyTests.cs` (`[Collection("app")]`, models: `BeforeFinish` `:279`, the crash at `:68`, notices `:643`), `tests/Kumbu.Server.Tests/Accounts/UserKeyRotationTests.cs`, `web/src/shell/SharedBanner.test.tsx:100`.

- [ ] **Red**, in `SharedRekeyTests`, all through `POST /api/vaults/Gartenarbeit/rekey` with `{ password = RestClient.Password }`. Alice's display name "Alice" as `SetDisplayAsync` in `VaultEventTests.cs:160` sets it; Bob's alias `Beet`, Carol's alias `Kräuter`:
  - `A_member_whose_key_ended_gets_a_notice`: Bob has a key on the vault (`ApiKeys.CreateAsync`, as `VaultEventTests.cs:165`), Carol has none. After the re-key `GET /api/member-notices` as Bob: one notice, kind `vault-rekeyed`, vault `Beet`, text exactly `"Alice" re-encrypted Beet; your keys and apps for it were revoked. Give your agents Beet again.`. As Carol: an empty list.
  - `A_member_whose_connection_ended_gets_a_notice`: Bob has only a connection listing the vault (`POST /api/connections`), never authorised, so without a slot on it: he still gets the notice, since `RemoveVaultsAsync` takes it.
  - `A_member_who_left_during_the_period_gets_no_notice`.
  - `The_notice_survives_a_crash_after_the_start`: as `SharedRekeyTests.cs:68`, fault at `after-start`, then resume; the membership list on the vault document lasted, and one notice exists.
  - `A_finish_that_fails_leaves_no_notice_and_its_retry_one`: `Catalog.RekeyFaultAt` throws once at `inside-finish`; no notice until the retry; then exactly one.
  - `The_finish_clears_the_list`: raw vault document has no `rekeyEndedMembers` afterwards.
  - `A_rekey_notice_goes_once_the_membership_ended`: Bob has the notice; after he leaves, `GET /api/member-notices` no longer lists it. The same after Alice revokes him. After Alice deletes the vault for everyone, only "The owner has deleted Beet." remains.
  - `A_notice_can_be_dismissed_and_names_no_owner_in_the_database`: `DELETE /api/member-notices/{id}`, then none; the stored document has no owner field, and the raw BSON doesn't contain "Alice".
- [ ] **Red**, in `UserKeyRotationTests`: `A_rotation_reseals_a_rekey_notice`: after a rotation of Bob's user key, `MemberNotices.ListAsync` with the new key opens the alias.
- [ ] **Red**, in `SharedBanner.test.tsx`: `"shows a re-encryption notice with its Dismiss"`, with a fixture of kind `vault-rekeyed`.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*SharedRekeyTests" --filter-class "*UserKeyRotationTests" --filter-class "*ClosingPeriodTests" --filter-class "*VaultEventTests"` and `pnpm -C web test --run SharedBanner`. **Expected:** the new server tests fail, the notice list being empty. The web test passes at once, as `MemberNotices` shows any kind's `text`: keep it as a pin, and say so.
- [ ] **Green.**
  - `Documents.cs`, beside `RekeyEnded`: `[BsonElement("rekeyEndedMembers"), BsonIgnoreIfNull] public List<string>? RekeyEndedMembers { get; set; }`, with a summary that the finish leaves these memberships a notice, also after a resume.
  - `EndMembersCredentialsAsync`: before the slots go, `var via = await (await Slots.DistinctAsync(s, sl => sl.GrantedVia, derived, cancellationToken: ct)).ToListAsync(ct);`. Project `theirs` to `new { x.Id, x.User }`, keeping the ids for `RemoveVaultsAsync` and `ended`. Add the active memberships of those users on this vault to `via`. Write with `AddToSetEach(v => v.RekeyEndedMembers, …)` when any, beside `:528`.
  - `FinishRekeyAsync`: `.Unset(v => v.RekeyEndedMembers)` beside `:570`; beside the `RekeyFinished` event (`:580`), in the transaction: `await Sharing.MemberNotices.LeaveForRekeyAsync(db, s, id, doc.RekeyEndedMembers ?? [], Now, ct);`.
  - `MemberNotices`: `public const string VaultRekeyed = "vault-rekeyed";`. `LeaveForRekeyAsync` takes those memberships that are still `Active`, and copies the member slot's `Alias` and `AliasKey` as `LeaveForDeletedAsync` does; share the copying between the two. Update the class comment: it now holds two kinds. `PrepareResealAsync` already covers every notice of the user.
  - `TextFor(string kind, string? alias, string owner)`: `VaultDeleted` as today; `VaultRekeyed` is `VaultEvents.TextFor(VaultEvents.RekeyFinished, toOwner: false, owner, alias ?? "a vault shared with you", null, null)`.
  - `SharingApi.NoticesAsync`: for a `VaultRekeyed` notice, read the membership by `n.Notice.Membership`. Leave the notice out of the answer unless that membership is `Active`: once she has left, was removed, or the vault is gone, "give your agents it again" is wrong. Otherwise `WriterLabels.Owner(await memberships.DisplayNameAsync(membership.Owner, ct))`, each owner once; an owner gone gives `WriterLabels.GoneOwner` by itself. A `VaultDeleted` notice is listed as today. The notice document itself still names no owner.
  - `SharedBanner.tsx:78`: the comment says "notices of shared vaults" without "deleted".
  - `docs/security-architecture.md`: `:179` ends with "and a member whose keys or apps ended gets a notice to dismiss (`Sharing/MemberNotices.cs`)". `:339` adds "the ids of the memberships whose credentials those were (`rekeyEndedMembers`, until the finish)". `:344`: "for each member of a vault its owner deleted for everyone, or whose keys and apps a re-encryption ended: … the kind (`vault-deleted`, `vault-rekeyed`) …".
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*SharedRekeyTests" --filter-class "*UserKeyRotationTests" --filter-class "*ClosingPeriodTests" --filter-class "*VaultEventTests"` and `pnpm -C web test --run SharedBanner`. **Expected:** exit 0 for both.
- [ ] **Commit:** "Tell members whose credentials a re-encryption ended".
- [ ] **Mutations**, each with `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*SharedRekeyTests" --filter-class "*UserKeyRotationTests"`: a notice for every active member (use `members` instead of `doc.RekeyEndedMembers`); drop the `Unset`; drop `via` from `theirs` (connections only through slots); drop the `Status == Active` filter; list rekey notices whatever the membership's status; put the notice outside the transaction; leave `PrepareResealAsync`'s coverage out for the new kind (filter on `VaultDeleted`).
- [ ] **Phase gate:** the full verification.

```sh
dotnet restore --locked-mode && dotnet build --configuration Release --no-restore && dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

  **Expected:** exit 0 for each, 0 failed; `dotnet` total above 3264, web above 1345, sidecar still 78. Push. If the rename below gets stuck, this phase stands on its own.

## Phase B: the rename

### Task 4: A new vault under the old name, tested first (spec §1.2, §1.4, §1.7)

The maintainer asked for this case to be secured by TDD, so it comes first and drives the rename into being. The tests go through REST, so they compile today and fail on the missing route: real red, not a compile error.

**Files:**

- `src/Kumbu.Server/Vaults/UnlockedVault.cs`: `:69` `Name`; `:168` `SealMeta`; `:587-589` `VaultMeta`.
- `src/Kumbu.Server/Vaults/VaultAccess.cs`: `:9-13` comment; `:50` `Owned`.
- `src/Kumbu.Server/Vaults/VaultCatalog.Members.cs:390`: `CredentialAccess`'s owner branch.
- `src/Kumbu.Server/Vaults/VaultCatalog.cs`: `:377` `FindForUserAsync`; `:1179`, `:1184` `LoadAsync`; beside `:1038` `SetEmbeddingProviderAsync`, the new `RenameVaultAsync`.
- `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs:999-1000`: the comment "For the owner both are the same objects" is no longer true.
- `src/Kumbu.Server/Sharing/VaultEvents.cs`: `:55` the kind; `:194` the sentence.
- `src/Kumbu.Server/Api/NoteApi.cs`: `:75` request record; `:193` route; beside `:235` handler.

**Do not touch** `Api/ConnectionApi.cs:180`, `OAuth/OAuthTokens.cs:162,205`, `Accounts/ApiKeys.cs:416`, `Accounts/Connections.cs:404`: those are the credential's own name. Do not touch `ToVault`, `ToCaller` or anything computing a `PathId`.

**Test:** new `tests/Kumbu.Server.Tests/Vaults/VaultRenameTests.cs`, `[Collection("app")]`, constructor `(KumbuAppFactory app)`, SPDX header. Remember: `RestClient.Vault` stays `Gartenarbeit`, so take ids and keys before the rename and spell later paths out.

- [ ] **Red.** A helper `RenameAsync(RestClient user, string vault, string name)` sends `PUT /api/vaults/{vault}/name` with `{ name }`; `NamesAsync` as in `tests/Kumbu.Server.Tests/Sharing/AliasApiTests.cs:16`. Alice owns `Gartenarbeit` with `plan.md` "Beete\n" and an API key taken before the rename.
  - `A_new_vault_may_take_the_old_name_and_they_are_two_vaults`: rename to `Garten`: 204. `GET /api/vaults` names `Garten`. `POST /api/vaults { name = "Gartenarbeit" }`: 201. Names are `Garten, Gartenarbeit`. `GET /api/notes/Garten/plan.md`: "Beete\n", its `path` `/Garten/plan.md`. `GET /api/notes/Gartenarbeit/plan.md`: 404. `PUT /api/notes/Gartenarbeit/plan.md { content = "Andere\n" }`: created. Each read gives its own text. Through `catalog.OpenForUserAsync`: two different `Id`s, both with `Vault.Name == "Gartenarbeit"`.
  - `Writes_to_either_stay_there`: a note written to each under the same relative path, a `mv` and an `rm` in the new vault: the renamed vault's tree (`GET /api/vaults/Garten/tree`) is unchanged by them; the other way round likewise. `GET /api/search?q=…&mode=exact` over both names each hit by its own vault's name.
  - `The_key_of_the_renamed_vault_does_not_reach_the_new_one`: with the new vault in place, as the key: `sh ls /` shows `Garten/` and not `Gartenarbeit/`; `cat /Garten/plan.md` works; `cat /Gartenarbeit/plan.md` is an error and doesn't hold "Andere"; REST `GET /api/notes/Gartenarbeit/plan.md` and `GET /api/vaults/Gartenarbeit/versions` as the key: 404.
  - `A_key_of_the_new_vault_does_not_reach_the_renamed_one`: its key sees only `Gartenarbeit/`; `/Garten/plan.md` is not found.
  - `Renaming_back_is_refused_while_the_old_name_is_taken_and_works_once_it_is_free`: 409 with the sentence `You already have a vault named 'Gartenarbeit'.`; both names stay. Delete the new vault (`POST /api/vaults/Gartenarbeit/delete`, `{ password = RestClient.Password, confirm = "Gartenarbeit" }`: `Accounts/Deletion.cs:85` wants the vault's name exactly), rename back: 204; the raw meta, opened with the vault key taken earlier, has no `DisplayName`.
  - `Deleting_the_new_vault_leaves_the_renamed_one` and `Deleting_the_renamed_vault_leaves_the_new_one`: deleted under its shown name (`POST /api/vaults/Gartenarbeit/delete` with `confirm = "Gartenarbeit"`, `POST /api/vaults/Garten/delete` with `confirm = "Garten"`), the right id goes; the other's notes read as before.
  - `The_name_survives_a_reload`: `catalog.Forget(catalog.Current(id)!, new InvalidOperationException("test"))`, then `GET /api/vaults`: still `Garten`; both vaults read as before.
  - `A_renamed_vault_in_its_two_key_period_keeps_its_name_beside_the_new_one`: with the new vault in place, `Share.SetRekeyStartedAsync(app.Database, id, DateTime.UtcNow)`, `Forget`, then `GET /api/vaults`: **200**, names `Garten, Gartenarbeit`, `Garten` pending. Reset in `finally`. The stored name in the placeholder would make `EncryptedNoteStore.VaultsAsync:988` throw for every request of hers.
  - `A_rename_whose_outcome_is_unknown_lets_the_object_go`: `catalog.MetaFaultAt` throws `InvalidOperationException` at `inside-rename`, as for a cancellation after the commit: the request fails, `catalog.Current(id)` is null, the next `GET /api/vaults` reads what is stored, and with `CapturedLog` (`rg -n CapturedLog tests` for how a host gets one) no line holds the name asked for. A refusal as taken lets nothing go: `Current(id)` is the same object.
  - `A_rename_through_an_older_object_tells_the_current_one`: `var old = catalog.Current(id)!;`, `Forget` it, `GET /api/vaults` to load `var current = catalog.Current(id)!;`, then `await catalog.RenameVaultAsync(alice.User.Id, alice.UserKey, old, "Garten", Ct)`: `current.DisplayName` is `Garten`, `catalog.Current(id)` is still that object, and `GET /api/vaults` names `Garten`.
  - `A_meta_that_does_not_open_answers_just_reencrypted`: flip a byte of the raw `meta`, rename: 503 with `VaultBusyException.JustReencrypted`, not 500; `Current(id)` stays; the slot's `nameId` is unchanged. Restore in `finally`.
  - `A_meta_without_a_display_name_is_as_before`: raw meta JSON of a never-renamed vault has only `Name` (and `Embeddings` if chosen). After a rename it holds `DisplayName`, and never `Shown`.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*VaultRenameTests"`. **Expected:** every test fails: the route answers the JSON 404, not 204. `A_meta_without_a_display_name_is_as_before` may fail only at its second half.
- [ ] **Green.**

`UnlockedVault.cs:587-589`:

```csharp
/// <remarks>… DisplayName: what callers see the vault as, once its owner renamed it; Name, in every stored path, never
/// changes after creation.</remarks>
internal sealed record VaultMeta(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Embeddings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName = null)
{
    [JsonIgnore]
    public string Shown => DisplayName ?? Name;
}
```

`UnlockedVault`: `public string? DisplayName { get; set; }` (set under `Gate`) and `public string Shown => DisplayName ?? Name;` beside `:69`. Beside `SealMeta`: `internal VaultMeta OpenMeta(byte[] blob) => Open<VaultMeta>($"vault:{Id}", blob);` and `internal byte[] SealMeta(VaultMeta meta) => Seal($"vault:{Id}", meta);`. For now `SealMeta(string)` writes `new VaultMeta(Name, embeddingProvider, DisplayName)`; Task 5 replaces it.

`VaultAccess.cs:50`: `new(vault, vault.Shown, AccessLevel.Owner, null, IsOwner: true)`. `VaultCatalog.Members.cs:390`: `new(vault, vault.Shown, …)`. Update the comments at `VaultAccess.cs:9-13,40,49` and `EncryptedNoteStore.cs:999-1000`.

`VaultCatalog.cs:1179` and `:1184`: `{ …, DisplayName = meta.DisplayName }`, the placeholder too. `:377`: `string.Equals(meta.Shown, name, StringComparison.Ordinal)`.

`VaultEvents`: `public const string VaultRenamed = "vault-renamed";`, **not** in `VaultWide` or `MemberKinds`, so members aren't told. `TextFor`: `(true, VaultRenamed) => "You renamed this vault.",`.

`VaultCatalog.cs`, after `SetEmbeddingProviderAsync`. Modelled on `RenameAliasAsync` (`VaultCatalog.Members.cs:161-213`):

```csharp
/// <summary>For tests, as <see cref="RekeyFaultAt"/>: a point between reading a vault's meta and writing it.</summary>
internal Func<string, Task>? MetaFaultAt { get; set; }

/// <summary>
/// The owner renames her vault (rename spec §1.4): in one transaction her user slot's <c>nameId</c>, whose unique index
/// refuses a name she already uses (<see cref="VaultNameTakenException"/>), and the meta's <c>DisplayName</c>. The stored
/// name stays, in every path. Back to the stored name, the display name goes. Under the vault's gate, as a provider
/// change is. <see cref="VaultBusyException"/> while the vault is re-keyed.
/// </summary>
internal async Task RenameVaultAsync(string userId, byte[] userKey, UnlockedVault vault, string newName, CancellationToken ct)
{
    await EnsureReadyAsync(ct);
    await RequireNoneRekeyingAsync([vault.Id], ct);
    CheckWritable(vault);
    await vault.Gate.WaitAsync(ct);
    var gateHeld = true;
    try
    {
        CheckWritable(vault);
        var f = Builders<SlotDocument>.Filter;
        string? display = null;
        using var session = await StartSessionAsync(ct);
        try
        {
          await session.WithTransactionAsync(
            async (s, token) =>
            {
                var slot = await Slots.Find(s, f.Eq(x => x.Kind, SlotDocument.User) & f.Eq(x => x.Vault, vault.Id) & f.Eq(x => x.Lookup, UserLookup(userKey, userId))).FirstOrDefaultAsync(token)
                    ?? throw new ArgumentException("You have no such vault.", nameof(vault));
                var doc = await Vaults.Find(s, v => v.Id == vault.Id).FirstOrDefaultAsync(token) ?? throw new VaultDeletedException();
                if (doc.RekeyStartedAt is not null) throw VaultBusyException.Being();
                // The stored meta, not this object's idea of it: a vault that follows the admin's default provider goes on following it.
                var meta = OpenStoredMeta(vault, doc.Meta);
                display = string.Equals(newName, meta.Name, StringComparison.Ordinal) ? null : newName;
                if (MetaFaultAt is { } fault) await fault("inside-rename");
                try
                {
                    var named = await Slots.UpdateOneAsync(
                        s, f.Eq(x => x.Id, slot.Id) & f.Eq(x => x.Wrapped, slot.Wrapped),
                        Builders<SlotDocument>.Update.Set(x => x.NameId, VaultCrypto.Mac(VaultCrypto.Derive(userKey, VaultNameLabel, []), NormaliseVaultName(newName))),
                        cancellationToken: token);
                    if (named.MatchedCount == 0) throw VaultBusyException.Just();
                }
                catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    throw new VaultNameTakenException(newName);
                }

                var sealedMeta = vault.SealMeta(meta with { DisplayName = display });
                var v = Builders<VaultDocument>.Filter;
                var updated = await Vaults.UpdateOneAsync(s, v.Eq(x => x.Id, vault.Id) & v.Eq(x => x.Meta, doc.Meta), Builders<VaultDocument>.Update.Set(x => x.Meta, sealedMeta), cancellationToken: token);
                if (updated.MatchedCount == 0) throw VaultBusyException.Just();
                await Sharing.VaultEvents.RecordAsync(db, s, vault.Id, Sharing.VaultEvents.VaultRenamed, null, Now, null, token);
                return true;
            },
            cancellationToken: ct);
        }
        catch (Exception e) when (e is not (VaultNameTakenException or VaultBusyException or VaultDeletedException or ArgumentException))
        {
            // Failed or cancelled, the commit may have landed all the same: read the vault again rather than guess. A
            // reason of its own, as Forget logs it and this one's message could hold the name.
            await UnderLoadingAsync(() =>
            {
                if (Current(vault.Id) is { } unknown) Forget(unknown, new InvalidOperationException("The outcome of a rename isn't known."));
            });
            throw;
        }

        vault.DisplayName = display;
        // A load that read the meta before the commit has published by the time _loading is ours. A request may hold an
        // older object: the current one must not lose the name either.
        await UnderLoadingAsync(() =>
        {
            if (Current(vault.Id) is { } current) current.DisplayName = display;
        });
    }
    finally
    {
        if (gateHeld) vault.Gate.Release();
    }

    // Queued before the gate goes, which keeps renames in order; not waited for under it. A re-key finish of another
    // vault can hold _loading for minutes, and this vault's writers, who never need it, would wait on the gate meanwhile.
    async Task UnderLoadingAsync(Action action)
    {
        var loading = _loading.WaitAsync(CancellationToken.None);
        vault.Gate.Release();
        gateHeld = false;
        await loading;
        try
        {
            action();
        }
        finally
        {
            _loading.Release();
        }
    }
}

/// <summary>A stored meta as <paramref name="vault"/> opens it. One that doesn't was re-keyed by another process since.</summary>
private static VaultMeta OpenStoredMeta(UnlockedVault vault, byte[] blob)
{
    try
    {
        return vault.OpenMeta(blob);
    }
    catch (CryptographicException)
    {
        throw VaultBusyException.Just();
    }
}
```

`NoteApi.cs`: `public sealed record RenameVaultRequest(string Name);` beside `:75`. Route beside `:193`, so that the schema's order follows it:

```csharp
api.MapPut("/vaults/{vault}/name", RenameVaultAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 409, 429, 503);
```

Handler beside `SetEmbeddingsAsync`:

```csharp
/// <summary>The owner renames her vault (rename spec §1.4): no password, since it is undone by renaming again and
/// removes no access. A member's alias answers 404, as on the other owner-only endpoints.</summary>
private static async Task<NoContent> RenameVaultAsync(
    string vault, RenameVaultRequest body, EncryptedNoteStore store, VaultCatalog catalog, RequestCaller caller, AccountLimits limits, CancellationToken ct)
{
    if (limits.AliasRename(caller.User!.Id) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
    var name = VaultNames.Check(body.Name);
    var root = ApiPaths.Parse(vault, null, note: false);
    var access = (await store.UnlockedVaultsAsync(ct)).FirstOrDefault(v => v.Name == root.Segments[0])
        ?? throw ApiErrors.NotFound($"You have no vault named '{root.Segments[0]}'.");
    if (!access.IsOwner) throw ApiErrors.NotFound($"You have no vault named '{access.Name}'.");
    if (string.Equals(name, access.Name, StringComparison.Ordinal)) return TypedResults.NoContent();
    try
    {
        await catalog.RenameVaultAsync(caller.User.Id, caller.UserKey!, access.Vault, name, ct);
    }
    catch (ArgumentException)
    {
        throw ApiErrors.NotFound($"You have no vault named '{access.Name}'.");
    }

    return TypedResults.NoContent();
}
```

  `VaultNameTakenException` already answers 409 with its sentence (`Api/ApiErrors.cs:74`), and `VaultBusyException` 503. Tasks 5 and 6 add what else the rename does after its commit. Do **not** edit the five credential-name sites.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*VaultRenameTests" --filter-class "*AliasTests" --filter-class "*AliasApiTests" --filter-class "*OwnerOnlyTests" --filter-class "*EmbeddingApiTests" --filter-class "*RekeyTests" --filter-class "*SharedRekeyTests" --filter-class "*UserVaultTests" --filter-class "*NoNamesTests"`. **Expected:** exit 0.
- [ ] **Commit:** "Let owners rename their vaults by a display name".
- [ ] **Mutations**, each with `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*VaultRenameTests" --filter-class "*AliasApiTests" --filter-class "*OwnerOnlyTests" --filter-class "*EmbeddingApiTests"`:
  1. `Owned` uses `vault.Name`.
  2. `CredentialAccess` uses `vault.Name`.
  3. `LoadAsync` drops `DisplayName` at `:1184`.
  4. The same at the placeholder `:1179`.
  5. `FindForUserAsync` compares `meta.Name`.
  6. Skip the slot's `NameId` update.
  7. Skip the meta update.
  8. Always keep a display name (`display = newName`).
  9. Drop `[JsonIgnore]` on `Shown`.
  10. The slot's `NameId` computed from `meta.Name` instead of `newName`.
  11. No `Forget` after an unknown outcome.
  12. `Forget(unknown, e)` with the caught exception's message holding the name.
  13. Drop the update of `Current(id)` after the commit.
  14. `OpenStoredMeta` without the `catch`.

### Task 5: The name index and everything that re-seals the meta (spec §1.2, §1.3, §1.8)

**Files:** `src/Kumbu.Server/Vaults/VaultCatalog.cs:907` (`RecoverAsync`), `:1038-1056` (`SetEmbeddingProviderAsync`), `src/Kumbu.Server/Vaults/UnlockedVault.cs:168` (`SealMeta(string)` goes), `src/Kumbu.Server/Vaults/VaultCatalog.cs` `:1525` (`PrepareUserSlots`, the name index during a rotation), `:1534-1538` (`SlotRewrap`); `src/Kumbu.Server/Accounts/UserKeyRotation.cs:16-21` (`SlotRewrap`), `:369-370` (`ApplyVaultSlotsAsync`); `src/Kumbu.Server/Vaults/VaultCatalog.Members.cs:80` (`InviteMetaAsync`). `Sharing/VaultInvites.cs:140,142` and `Vaults/VaultCatalog.Rekey.cs:569` need a test and **no edit**: the first reads `InviteVaultMeta.Name`, the second serialises the whole `VaultMeta`.

**Test:** `tests/Kumbu.Server.Tests/Accounts/UserKeyRotationTests.cs` (`[Collection("mongo")]`, model `A_rename_between_prepare_and_apply_makes_apply_fail` `:632`, `Rotation()`, `Catalog()`), `tests/Kumbu.Server.Tests/Vaults/VaultRenameTests.cs`, `tests/Kumbu.Server.Tests/Sharing/InvitationApiTests.cs`.

- [ ] **Red**, in `UserKeyRotationTests`:
  - `A_rotation_keeps_a_renamed_vaults_name_in_the_index`: Bob's vault renamed through `Catalog().RenameVaultAsync`, then `Rotation().RotateAsync`. With the new key: `FindForUserAsync(…, "Garten", …)` finds it; `CreateForUserAsync("garten", …)` throws `VaultNameTakenException`; `CreateForUserAsync("Gartenarbeit", …)` works. (Fails today only if `:1525` stays on `meta.Name`.)
  - `A_vault_rename_between_prepare_and_apply_makes_apply_fail`: as `:632`, with `RenameVaultAsync` between `PrepareAsync` and `CommitAsync`: `RotationConflictException`; the rename stands; a second rotation then gives the right index.
- [ ] **Red**, in `VaultRenameTests` (`[Collection("app")]`):
  - `Reencrypting_keeps_the_name`: rename, `POST /api/vaults/Garten/rekey { password }`, then `GET /api/vaults`: `Garten`; after a `Forget` too; `plan.md` reads under `/Garten/`; the meta opens under the new key with `DisplayName == "Garten"` and `Name == "Gartenarbeit"`.
  - `A_provider_change_keeps_the_name`: `PUT /api/vaults/Garten/embeddings`, with a factory that has a second provider as `tests/Kumbu.Server.Tests/Api/EmbeddingApiTests.cs:26`; the raw meta has both, and after a `Forget` the name stays.
  - `A_rename_keeps_the_provider_chosen` and `A_rename_does_not_pin_the_default_provider`: for a vault never chosen, raw meta's `Embeddings` stays null after a rename. This is the reason the rename opens the stored meta.
  - `A_provider_change_through_an_older_object_keeps_the_name`: `var old = catalog.Current(id)!;`, `catalog.Forget(old, new InvalidOperationException("test"))`, rename through REST (a new object loads; `old` is told nothing), then `await catalog.SetEmbeddingProviderAsync(old, "remote", Ct)`. The raw meta has `Embeddings == "remote"` **and** `DisplayName == "Garten"`; `old.DisplayName` is `Garten` now too; `GET /api/vaults` after a `Forget`, with a new vault under the old name beside it, answers 200. **Fails until the provider change reads the stored meta.**
  - `A_provider_change_on_a_meta_that_does_not_open_answers_just_reencrypted`: flip a byte of the raw `meta`, `PUT /api/vaults/Garten/embeddings`: 503, not 500. Restore in `finally`.
  - `A_provider_change_conflicts_with_another_writer`: `catalog.MetaFaultAt` at `before-provider-write` writes `Meta` directly once: the call throws `VaultBusyException`, and what the other wrote stands.
  - `A_concurrent_provider_change_is_kept`: `catalog.MetaFaultAt` at `inside-rename`, once: writes `Meta` directly, outside the session, as `var c = catalog.Current(id)!; c.SealMeta(c.OpenMeta(stored) with { Embeddings = "remote" })`. The rename's write conflicts, its transaction retries from the new bytes, and it answers 204; the raw meta has `Embeddings == "remote"` and `DisplayName == "Garten"`. (A direct write, as the rename holds the gate a real provider change would wait on: it stands for another writer.) Reset the hook in `finally`.
  - `Renaming_during_a_run_answers_being_reencrypted`: with `Share.SetRekeyStartedAsync`, and then with `await using (await alice.HoldRekeyAsync())`: 503 with `VaultBusyException.BeingReencrypted`; the name stays.
  - `Recovery_names_a_vault_by_its_shown_name`: `catalog.RecoverAsync` with a vault made by `CreateAsync` with a recovery key, renamed by updating its meta directly as a test may, then `catalog.Forget(catalog.Current(id)!, new InvalidOperationException("test"))` or a fresh `world.Catalog()`, as `LoadAsync` otherwise returns the cached object: returns the shown name. Keep it small; `RecoverAsync` serves the dev token's seeding (`Notes/Seeder.cs:53`).
- [ ] **Red**, in `InvitationApiTests`: `Peek_at_a_renamed_vault_shows_its_shown_name`: an invitation made **before** the rename and one after: peek answers `Garten` as the vault and as the proposed alias; accepting takes `Garten` as the alias; the invited member's own notes paths are under `/Garten/`.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*UserKeyRotationTests" --filter-class "*VaultRenameTests" --filter-class "*InvitationApiTests" --filter-class "*RekeyTests" --filter-class "*SharedRekeyTests" --filter-class "*EmbeddingApiTests"`. **Expected:** the rotation tests, the peek test, the recovery test and the three provider-write tests fail; those on re-encryption and provider pass at once, being pins of Task 4's work: say so in the report.
- [ ] **Green.**
  - `SetEmbeddingProviderAsync`, under the gate after `CheckWritable`, replacing the write at `:1047`; `SealMeta(string)` goes:

```csharp
var doc = await Vaults.Find(v => v.Id == vault.Id).FirstOrDefaultAsync(ct) ?? throw new VaultDeletedException();
// The stored meta, not this object's: it may be older than a rename, and sealing from it would lose the name.
var meta = OpenStoredMeta(vault, doc.Meta);
if (MetaFaultAt is { } fault) await fault("before-provider-write");
var f = Builders<VaultDocument>.Filter;
var result = await Vaults.UpdateOneAsync(
    f.Eq(v => v.Id, vault.Id) & f.Eq(v => v.Meta, doc.Meta),
    Builders<VaultDocument>.Update.Set(v => v.Meta, vault.SealMeta(meta with { Embeddings = provider })),
    cancellationToken: ct);
if (result.MatchedCount == 0) throw VaultBusyException.Just();
vault.EmbeddingProvider = provider;
vault.DisplayName = meta.DisplayName;
```

    `tests/Kumbu.Server.Tests/Accounts/DeletionTests.cs:326-329` must keep passing: a document gone is `VaultDeletedException`.
  - `VaultCatalog.cs:1525`: `NormaliseVaultName(meta.Shown)`.
  - `SlotRewrap` gains `public byte[]? OldNameId { get; init; }`, documented as `OldAlias` is; `:1534-1538` sets `OldNameId = slot.Kind == SlotDocument.User ? slot.NameId : null`; `UserKeyRotation.cs:370`: `if (slot.OldNameId is { } name) filter &= f.Eq(s => s.NameId, name);`. The rename makes the rotation conflict, as a member's does.
  - `VaultCatalog.Members.cs:80`: `new InviteVaultMeta(meta.Shown, …)`.
  - `VaultCatalog.cs:907`: `names.Add((vault.Shown, added))`.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*UserKeyRotationTests" --filter-class "*VaultRenameTests" --filter-class "*InvitationApiTests" --filter-class "*RekeyTests" --filter-class "*SharedRekeyTests" --filter-class "*EmbeddingApiTests" --filter-class "*DeletionTests"`. **Expected:** exit 0.
- [ ] **Commit:** "Keep a renamed vault's name in the index and through re-sealing".
- [ ] **Mutations**, each with `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*UserKeyRotationTests" --filter-class "*VaultRenameTests" --filter-class "*InvitationApiTests" --filter-class "*SharedRekeyTests"`:
  1. `:1525` on `meta.Name`.
  2. Drop the `OldNameId` filter.
  3. `Members.cs:80` on `meta.Name`.
  4. `:907` on `vault.Name`.
  5. `SetEmbeddingProviderAsync` seals `new VaultMeta(vault.Name, provider, vault.DisplayName)`, from the object.
  6. `FinishRekeyAsync` seals `meta with { DisplayName = null }` (`VaultCatalog.Rekey.cs:569`).
  7. The rename seals `new VaultMeta(meta.Name, vault.EmbeddingProvider, display)` instead of the stored meta's provider.
  8. The rename reads the meta before the transaction.
  9. The provider write without the condition on the bytes.

### Task 6: What the rename does around its commit (spec §1.4)

**Files:** `src/Kumbu.Server/Api/NoteApi.cs` (the handler `RenameVaultAsync`), `src/Kumbu.Server/Sync/SyncHub.cs:236-241` (`AliasRenamed`), `:606-609` (`Connection.Rename`), `:121-122` (`RunAsync`, registration), `src/Kumbu.Server/Api/Security.cs:265-270,344` (comments on the limit). **Test:** `tests/Kumbu.Server.Tests/Vaults/VaultRenameTests.cs`, `tests/Kumbu.Server.Tests/Api/SyncSocketTests.cs` (model `A_renamed_alias_names_the_hints_at_once` `:606`), `tests/Kumbu.Server.Tests/Api/PairingApiTests.cs`, `tests/Kumbu.Server.Tests/Sharing/OwnerOnlyTests.cs`, `tests/Kumbu.Server.Tests/Sharing/VaultEventTests.cs`.

- [ ] **Red.**

| Test | Class | Pins |
|---|---|---|
| `A_member_may_not_rename_the_vault` | `OwnerOnlyTests` | For read, write and maintain: `PUT /api/vaults/garten/name`: 404, `You have no vault named 'garten'.`. Alice's name unchanged |
| `A_key_may_not_rename_the_vault` | `VaultRenameTests` | As the owner's key: 403 |
| `No_password_is_asked` | `VaultRenameTests` | With the password window closed (as `Api/PasswordWindowTests.cs:34-40`): 204 |
| `A_members_alias_is_untouched` | `VaultRenameTests` | Bob's alias `Beet` before and after; his notes under `/Beet/`; `/api/vault-events` as Bob and activity of `Beet` hold nothing of the rename; his key's `sh` output has no notice of it |
| `The_owner_sees_it_in_her_activity` | `VaultEventTests` | `GET /api/vaults/Garten/activity`: events hold "You renamed this vault."; the stored event holds neither name |
| `A_case_only_rename_works` | `VaultRenameTests` | `beet` to `Beet`: 204, names `Beet`, `NameId` unchanged; notes read under `/Beet/`; `/beet/plan.md` is 404, the lookup being exact |
| `Renaming_to_the_same_name_changes_nothing` | `VaultRenameTests` | 204, no event, meta bytes unchanged |
| `A_name_taken_by_an_alias_is_refused` | `VaultRenameTests` | Alice is a member of Bob's vault under `Beet`; renaming to `beet`: 409, name stays |
| `Invalid_names_are_refused` | `VaultRenameTests` | `a/b`, blank, 65 characters: 400 |
| `Renames_are_rate_limited_with_aliases` | `VaultRenameTests` | Past `AccountLimits.AliasRenamesPerUser` on a factory with a low limit: 429 |
| `The_new_name_reaches_no_log` | `VaultRenameTests` | With `CapturedLog` (`rg -n CapturedLog tests` for how a host gets one): a rename to `Zz.Geheim-7`, one refused as taken, and one of the same vault afterwards: no line contains it |
| `A_renamed_vault_names_the_hints_at_once` | `SyncSocketTests` | A session socket and a key's socket of Alice: after the rename each gets `Changed("Garten")`, unprompted; after a write, `Changed("Garten")` again. Bob's socket under `Beet` isn't renamed (`Hub.Held`), and gets no hint from the rename itself |
| `A_ticket_from_before_a_rename_names_the_new_name` | `SyncSocketTests` | `SyncSockets.TicketAsync` for the session and the key, then the rename, then `ConnectAsync` with each: after a write each gets `Changed("Garten")`, never the old name. Bob's ticket from before keeps his alias |
| `A_rename_during_a_recomputation_keeps_the_new_name` | `tests/Kumbu.Server.Tests/Api/SyncHubReviewTests.cs` (model `:120-145`) | `Hub.AfterRecomputeRead` holds a check while the owner renames: `Hub.Held` names `Garten` afterwards |
| `A_rename_drops_open_pairing_codes` | `PairingApiTests` | A code made before: redeem after the rename answers 404. A new code: redeems with `vault == "Garten"`, its key named `Garten – Obsidian …` |
| `An_old_device_gets_404_by_the_old_name` | `PairingApiTests` | A device key from before: `GET /api/vaults/Gartenarbeit/versions` 404 (what the plugin reads as "can't open"), and the same with a new vault of that name; under `Garten` it answers, with paths under `/Garten/` |

- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*VaultRenameTests" --filter-class "*SyncSocketTests" --filter-class "*SyncHubReviewTests" --filter-class "*PairingApiTests" --filter-class "*OwnerOnlyTests" --filter-class "*VaultEventTests"`. **Expected:** the socket and the pairing-code tests fail; the rest pass as pins of Task 4: say which in the report.
- [ ] **Green.**

`SyncHub`, after `AliasRenamed`:

```csharp
/// <summary>An owner renamed her vault (rename spec §1.4): her connections that hold it as its owner name it so from now
/// on, and are told, so that her open tabs ask for the vault list again and take a new ticket.</summary>
public void VaultRenamed(string ownerId, string vaultId, string name)
{
    foreach (var connection in _connections.Keys.Where(c => c.Grant.Subject.User == ownerId && c.Has(vaultId)))
    {
        connection.RenameOwned(vaultId, name);
        connection.Hint(vaultId);
    }
}
```

`Connection`, after `Rename`:

```csharp
public void RenameOwned(string vaultId, string name)
{
    lock (_gate) _vaults = _vaults.ToDictionary(v => v.Key, v => v.Key == vaultId && v.Value.Membership is null ? v.Value with { Name = name } : v.Value, StringComparer.Ordinal);
}
```

`VaultsChangedAsync` isn't for this: it only re-checks access, and `TryApply` keeps the name a connection holds.

`RunAsync`, right after `_connections[connection] = 0;` (`:122`), for a ticket issued before a rename. In this order a rename is caught either way: one that set the name before this read is seen here, and a later one finds the connection registered.

```csharp
foreach (var (vaultId, state) in connection.Vaults)
{
    if (state.Membership is null && catalog.Current(vaultId)?.Shown is { } shown) connection.RenameOwned(vaultId, shown);
}
```

In the handler `RenameVaultAsync`, add `Pairings pairings, SyncHub hub` to its parameters; after the catalog's call, in this order: `pairings.DropAll(caller.User.Id);` (an open code would hand a device the old name) and `hub.VaultRenamed(caller.User.Id, access.Id, name);`. Update the summary above `AliasRenamesPerUser` in `Security.cs:265-270` and of `AliasRename` at `:344`: they count an owner's renames too. **Log nothing of the name.**
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*VaultRenameTests" --filter-class "*SyncSocketTests" --filter-class "*SyncHubReviewTests" --filter-class "*PairingApiTests" --filter-class "*OwnerOnlyTests" --filter-class "*VaultEventTests"`. **Expected:** exit 0.
- [ ] **Commit:** "Tell open tabs of a rename, and drop pairing codes".
- [ ] **Mutations**, each with `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*VaultRenameTests" --filter-class "*SyncSocketTests" --filter-class "*SyncHubReviewTests" --filter-class "*PairingApiTests" --filter-class "*OwnerOnlyTests" --filter-class "*VaultEventTests"`:
  1. Drop the `IsOwner` check.
  2. Drop `SessionOnlyMetadata`.
  3. Drop the limit.
  4. Drop `DropAll`.
  5. Call `VaultsChangedAsync` instead of `VaultRenamed`.
  6. `RenameOwned` without the `Membership is null` condition.
  7. `VaultRenamed` without the `Subject.User` filter.
  8. Add `VaultRenamed` to `VaultWide`.
  9. Log the name at information level.
  10. Drop the refresh in `RunAsync`.

### Task 7: The owner's matrix (spec §1.7, §1.8)

The translation is proven for members, not for owners, and a path that checks `IsOwner` first would be the failure. Every row runs against a renamed vault **with a new vault under the old name beside it**, holding a decoy `plan.md`, so a path that falls through to the stored name doesn't just 404: it lands in the wrong vault, and the test sees that.

**Files:** tests first; `src/` only where one fails. **Test:** new `tests/Kumbu.Server.Tests/Vaults/RenamedOwnerTests.cs`, `[Collection("app")]`, SPDX header.

Fixture `RenamedAsync()`: Alice (`RestClient.NewAsync(Api, vault: "Gartenarbeit")`); before the rename, `plan.md` "Beete\n", `Bilder/a.png` (`tests/Kumbu.Server.Tests/Api/FileApiTests.cs:32` `Png`), a PDF as `tests/Kumbu.Server.Tests/Attachments/` does with `app.Sidecar`, a key `Claude` with `KeyRight.Maintain`, the vault's id. Then rename to `Garten`, create `Gartenarbeit` with the decoy `plan.md` "Andere\n" and its id. Add to `RestClient`, as `ShAsync` beside it: `ToolAsync(string tool, Dictionary<string, object?> args, string bearer)`; `tests/Kumbu.Server.Tests/Api/WriterLabelTests.cs:93` shows the `write` tool's arguments.

After **each** test, `AssertDecoyUntouchedAsync`: the new vault's tree holds only `plan.md`, its content "Andere\n", one version, empty trash, no checkpoints, no moves. Except where a row reads it on purpose, no response body contains "Gartenarbeit".

- [ ] **Red** where they fail; otherwise they are pins. One `[Fact]` per cell; test name `<Who>_<operation>`. Session is REST with the cookie; "Key, REST" is the same request with `bearer: key`.

| Operation | `Session_…` | `KeyRest_…` | `KeySh_…` (`alice.ShAsync(cmd, key)` or `ToolAsync`) | Asserts besides the status |
|---|---|---|---|---|
| `reads` | `GET /api/notes/Garten/plan.md` | same | `cat /Garten/plan.md` | `path` is `/Garten/plan.md`, text "Beete\n" |
| `lists` | `GET /api/vaults`, `GET /api/vaults/Garten/tree` | same | `ls /`, `ls /Garten` | tree root's path `/Garten`, every child under it |
| `writes` | `PUT /api/notes/Garten/neu.md`; again with `version` | same | tool `write` `/Garten/neu.md`; tool `edit` | new note reads under `/Garten/`; a stale version answers 409 naming `/Garten/neu.md` |
| `moves` | `POST /api/move` `/Garten/plan.md` to `/Garten/p/plan.md`; a folder too | same | `mv` | old path's 404 hint names `/Garten/p/plan.md`; `moves` answer in `/Garten/` |
| `removes` | `DELETE /api/notes/Garten/plan.md` | same | `rm /Garten/plan.md` | in `GET /api/vaults/Garten/trash` under `/Garten/` |
| `restores` | `POST /api/restore/Garten/plan.md` after a removal | same | `restore /Garten/plan.md` | reads again; `trash` is empty |
| `searches_by_words` | `GET /api/search?q=Beete&mode=exact&path=/Garten` | same | `rg Beete /Garten` | every hit under `/Garten/`; "Andere" absent |
| `searches_across_vaults` | `GET /api/search?q=plan&mode=exact` | n/a: the key reaches one vault | `rg plan /` | session: hits under both names, each its own text. Key: only `/Garten/` |
| `searches_by_meaning` | `GET /api/search?q=Beete&mode=meaning&path=/Garten` after `app.IndexedAsync()` | same | `semsearch -n 3 Beete` | hits under `/Garten/`; none of the decoy for the key |
| `sees_history` | `GET /api/history/Garten/plan.md`, then `GET /api/notes/Garten/plan.md?version=…` of an earlier one | same | `history /Garten/plan.md`, `cat` of a version | old content; path under `/Garten/` |
| `sees_activity` | `GET /api/vaults/Garten/activity` | **403**, session-only | n/a | rows' paths under `/Garten/`; "You renamed this vault." among events |
| `makes_checkpoints` | `POST /api/vaults/Garten/checkpoints { name = "gfd" }` | same, other name | `mkcp /Garten gfd2` | scope `/Garten` |
| `lists_checkpoints` | `GET /api/vaults/Garten/checkpoints` | same | `lscp /Garten` | holds them; none in the decoy |
| `restores_checkpoints` | `POST …/checkpoints/gfd/preview`, then `…/restore { plan }`, after changing `plan.md` | same | `restore --checkpoint gfd /Garten/plan.md`; `restore --checkpoint gfd /Garten` then with `--plan` | text back; every preview path under `/Garten/` |
| `reads_a_checkpoint` | `GET …/checkpoints/gfd/tree`, `…/notes/plan.md`, `…/files/Bilder/a.png` | same | n/a | paths under `/Garten/` |
| `removes_checkpoints` | `DELETE …/checkpoints/gfd` | same | `rmcp /Garten gfd2` | gone |
| `writes_attachments` | `PUT /api/files/Garten/Bilder/b.png` via `SendContentAsync`; `POST /api/upload/Garten/Bilder` | same | n/a | answer's `path` under `/Garten/` |
| `reads_attachments` | `GET /api/files/Garten/Bilder/a.png`; PDF pixels as `AttachmentPixels` serves them | same | `img /Garten/Bilder/a.png`; `pdf /Garten/x.pdf`; `rg` a word of the PDF | right bytes, not empty |
| `removes_and_restores_attachments` | `DELETE /api/files/…`, `POST /api/restore/…` | same | `rm`, `restore` | blob opens after |
| `uses_the_board` | `POST /api/vaults/Garten/board`, `…/board/cards`, `…/board/move`, `GET …/board` | same | `board /Garten`, tool `board` | title `Garten`; card paths under `/Garten/Board/` |
| `sees_links_and_tags` | `GET /api/links/Garten/…`, `/api/backlinks/…`, `GET /api/tags?…` | same | `links`, `backlinks`, `tags` | paths under `/Garten/` |
| `syncs` | `GET /api/vaults/Garten/versions`, with `since` and `If-None-Match`, `attachments=1` | same | n/a | every path under `/Garten/`; a write appears in the next cursor's list |
| `exports` | ticket, `GET /api/vaults/Garten/export`; `GET …/checkpoints/gfd/export`; `GET /api/files/Garten/zip`; `GET /api/export?vault=Garten&vault=Gartenarbeit` | **403**, session-only | n/a | zip entries under `Garten/`, right text; the two-vault zip has two folders with each its own `plan.md`; file name holds `Garten` |
| `is_told_of_damage` | damaged note: `tests/Kumbu.Server.Tests/Vaults/EncryptedNoteStoreTests.cs` for how to damage one | same | `cat` | message names `/Garten/…` |

- [ ] **Red**, in the same class:
  - `The_worked_example_of_the_spec`: spec §1.7 steps 1 to 9 as one test, with Bob as member under `Beet`, `plan.md`, a board and a checkpoint `gfd`, a key `Claude`, a device key. Step 6 asserts the device's `versions` under `Gartenarbeit` is 404.
  - `The_name_reaches_a_key_and_a_connection_without_touching_their_slots`: raw `slots` of the key and the connection, and `api_keys`/`connections` documents, compared byte for byte before and after the rename; `GET /api/keys` and `GET /api/connections` show `Garten`; the connection's bearer token (flow as `tests/Kumbu.Server.Tests/OAuth/TokenTests.cs:79`) gets `ls /` with `Garten/`.
  - `Stored_documents_do_not_change`: `notes`, `attachments`, `checkpoints`, `trash` and `blobs` documents of the vault byte for byte before and after a rename and back.
  - `A_new_note_after_the_rename_is_stored_under_the_stored_name`: `catalog.Current(id)!.Notes` keys all start with `/Gartenarbeit/`, and the new note's `PathId` is `PathId(/Gartenarbeit/neu.md)`.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*RenamedOwnerTests"`. **Expected before any fix:** most pass, translation being shared with members; every one that fails is a finding. For each: list it in the report, fix it in `src/` by using the access's name or `ToVault`/`ToCaller`, never by a case for owners, and make the test pass. If a fix would need more than about an hour, leave the test skipped with its reason, and report it among the open points.
- [ ] **Commit:** "Pin a renamed vault's operations for its owner".
- [ ] **Mutations**, each with `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*RenamedOwnerTests"`; each must fail several rows:
  1. `VaultAccess.ToVault` returns the path unchanged.
  2. `VaultAccess.ToCaller` returns the path unchanged.
  3. `EncryptedNoteStore.Shown(VaultAccess, Checkpoint)` returns the checkpoint unchanged.
  4. `Stored(VaultAccess, NoteBatch)` returns the batch unchanged.
  5. `VaultAccess.Renamed` becomes `!IsOwner && …`: exactly the failure the spec fears.
  6. `SyncApi.cs:142`: `n.Note.Path` without `ToCaller`.

### Task 8: `deviceKeys` in the access view (spec §1.5)

**Files:** `src/Kumbu.Server/Api/NoteApi.cs:47` (`VaultAccessView`), `:305` (`AccessAsync`), `src/Kumbu.Server/Accounts/Rekeying.cs:143-156` (`AccessAsync`, `VaultAccessList`), `web/src/api/schema.d.ts:8410`. **Test:** `tests/Kumbu.Server.Tests/Sharing/MembershipApiTests.cs`.

- [ ] **Red:** `The_access_view_names_device_keys`: a device key (`alice.KeyAsync("Gartenarbeit – Obsidian Laptop", KeyRight.Maintain, ApiKeyKind.Device)`) and an agent key: `apiKeys` holds both, as today, `deviceKeys` only the first. `No_device_key_is_an_empty_list`. A revoked device key isn't listed.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*MembershipApiTests" --filter-class "*RekeyTests"`. **Expected:** the new tests fail on the missing `deviceKeys`.
- [ ] **Green.** `Rekeying.AccessAsync`: materialise the live keys once, `var live = (await keys.ListAsync(user, userKey, ct)).Where(k => keyIds.Contains(k.Id) && k.RevokedAt is null && !(k.ExpiresAt <= now)).ToList();`; names from it; `DeviceKeys` = those with `Kind == ApiKeyKind.Device`, ordered. `VaultAccessList(string[] ApiKeys, string[] DeviceKeys, string[] Connections, …)`. `VaultAccessView(string[] ApiKeys, string[] DeviceKeys, string[] Connections, MemberAccessView[] Members, DepartureView[] Left)`, with a doc-comment line: `deviceKeys`: those of `apiKeys` that are devices, which lose the vault when it is renamed. `schema.d.ts:8410`, by hand, in the record's order: `deviceKeys: string[];` after `apiKeys: string[];`.
- [ ] **Run:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*MembershipApiTests" --filter-class "*RekeyTests"`, then `pnpm -C web install --frozen-lockfile && pnpm -C web test --run AccessList && pnpm -C web build`. **Expected:** exit 0 for each. If `build` complains of fixtures typed as `VaultAccessView`, add `deviceKeys: []` to them.
- [ ] **Commit:** "Name device keys in a vault's access view".
- [ ] **Mutations**, each with `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*MembershipApiTests"`: `DeviceKeys` lists every key; leave out the revoked filter.
- [ ] **Phase gate:** the full verification.

```sh
dotnet restore --locked-mode && dotnet build --configuration Release --no-restore && dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

  **Expected:** exit 0 for each, 0 failed.

### Task 9: "Rename vault…" in the web app (spec §1.5, §1.6)

**Files:** new `web/src/shell/RenameVaultDialog.tsx` (SPDX header; model `web/src/shell/MemberPanel.tsx:88-114,143-170`, `web/src/shell/DeleteVaultDialog.tsx`), `web/src/api/sharing.ts:128` (beside `useRenameAlias`), `web/src/shell/TreeDrawer.tsx` (import `:25`, state near `:340`, owner's entries after "Connect Obsidian…" near `:395`, dialogs `:481`), `web/src/api/schema.d.ts:2732,8227`. **Test:** new `web/src/shell/RenameVaultDialog.test.tsx` (setup as `web/src/shell/MemberPanel.test.tsx:1-50`, `fakeApi`), `web/src/shell/TreeDrawerFolders.test.tsx:422`.

- [ ] **Red**, in `RenameVaultDialog.test.tsx`, for vault `Gartenarbeit`:
  - `"says what breaks, before the name is changed"`: text "Rename Gartenarbeit", "Members keep their own name for it.", "Agents find it under the new name; paths with the old one stop working.".
  - `"names Obsidian devices only if there are device keys"`: `GET /api/vaults/Gartenarbeit/access` with `deviceKeys: ["Gartenarbeit – Obsidian Laptop"]`: "Connected Obsidian devices need to pair again." shows; with `[]` it doesn't.
  - `"says it also if the list can't be read"`: access answers 503: the sentence shows, rather than staying silent about a device that breaks.
  - `"sends the name, normalised and trimmed"`: `PUT /api/vaults/Gartenarbeit/name` with `{ name: "Garten" }` for ` Garten `.
  - `"goes on showing the same note under the new name"`: `currentPath` `/Gartenarbeit/plan.md`, after success the router is at `/notes/Garten/plan.md`, `activeVault` `Garten`, recent paths moved.
  - `"on a page with no note open, only the active vault changes"`: at `/`, `activeVault` is `Garten` and the router stays.
  - `"forgets the old name's caches and asks for the vaults again"`.
  - `"shows a taken name and stays open"`: a 409 with the server's sentence; nothing moved.
  - `"offers no Rename while the name is unchanged"`: disabled for an equal name or a blank; a case-only change is enabled.
- [ ] **Red**, in `TreeDrawerFolders.test.tsx`: `"the owner's vault menu offers Rename vault…, before Delete vault…"`; `items.at(-1)` stays `"Delete vault…"`. `it.each(["read", "write", "maintain"])("a member (%s) is offered no Rename vault…", …)`.
- [ ] **Run:** `pnpm -C web test --run RenameVaultDialog TreeDrawerFolders MobileShell`. **Expected:** fail on the missing module and entry.
- [ ] **Green.**

`sharing.ts`, beside `useRenameAlias`:

```ts
/** The owner's new name for her vault; her open note, recent notes and drawer move with it. */
export function useRenameVault() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ vault, name }: { vault: string; name: string }) =>
      void (await call(api.PUT("/api/vaults/{vault}/name", { params: { path: { vault } }, body: { name } }))),
    onSuccess: async (_, { vault, name }) => {
      const to = name.normalize("NFC").trim();
      const editor = useEditor.getState();
      editor.moved(`/${vault}/`, `/${to}/`);
      if (useEditor.getState().activeVault === vault) editor.setActiveVault(to);
      queryClient.removeQueries({ predicate: (q) => q.queryKey.some((k) => typeof k === "string" && (k === vault || k.startsWith(`/${vault}/`))) });
      await Promise.all(["vaults", "keys", "connections"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
    },
  });
}
```

`RenameVaultDialog({ vault, onClose })`: `role="dialog"`, `useDialog`, the name in a text field with `autoComplete="off"` and a label "New name", a Cancel and a primary "Rename vault", the lead sentence of the spec, naming no device. The sentence about devices: `access.isError || access.data?.deviceKeys.length`. With `useVaultAccess(vault)`; hold the primary button while `access.isPending`. On success, as `MemberPanel.tsx:105-112`: `const open = useEditor.getState().currentPath;` read before the mutation; `if (open && vaultOf(open) === vault) navigate(noteUrl(movedPath(open, `/${vault}/`, `/${to}/`)), { replace: true });`; The dialog opens only from `TreeDrawer`, which mounts only on `/` and `/notes/:vault/*` (`web/src/router.tsx:113-117`), so an open note is all there is to follow. `movedPath` comes from `../state/editor`. Errors: `<FormError>{errorText(rename.error)}</FormError>`.

`TreeDrawer`: `{ label: "Rename vault…", icon: …, onSelect: () => setRenaming(true) }` in the owner's list; pick an icon from `lucide-react` (`Pencil`). Dialog beside the others.

`schema.d.ts`, by hand, as generated output would have it. After the `"/api/vaults/{vault}/embeddings"` block, before `"/api/embeddings"` (`:2732`), a block `"/api/vaults/{vault}/name"` with the shape of `"/api/memberships/{id}/alias"` (`:7205-7290`): path parameter `vault: string`, `requestBody` of `components["schemas"]["RenameVaultRequest"]`, responses 204, 400, 401, 403, 404, 409, 429, 503 copied from it. After `RenameColumnRequest` (`:8227`): `RenameVaultRequest: { name: string; };`.
- [ ] **Run:** `pnpm -C web test --run RenameVaultDialog TreeDrawerFolders MobileShell && pnpm -C web build`. **Expected:** exit 0 for both; `build` is what type-checks the schema edit.
- [ ] **Commit:** "Offer renaming a vault in its menu".
- [ ] **Mutations**, each with `pnpm -C web test --run RenameVaultDialog TreeDrawerFolders MobileShell`:
  1. Show the device sentence always.
  2. Hide it on an access error.
  3. Don't navigate.
  4. Leave `activeVault` as it was.
  5. Offer the entry to members.
  6. Keep the old name's caches.

### Task 10: The security document, and the security review (spec §1.9)

This work **touches** the vault meta and the slot name index, so `docs/security-architecture.md` is updated and the security review applies.

**Files:** `docs/security-architecture.md`: `:297` (§5.3), `:339` (§5.6 `vaults`), `:575` (§6, "Sharing a vault"), `:609` (rate limits), §4's table near `:219` if it lists what an owner may do. It cites file and line: keep what you touch true.

- [ ] **Change the document.**
  - `:297`, after "Vault names are sealed (…)": "That stored name never changes after creation: every stored path starts with it, and every path's keyed hash is computed over that. A renamed vault keeps it and gets a **display name**, sealed in the same meta under the vault key, bound to `vault:{id}` as the name is (`Vaults/UnlockedVault.cs`, `VaultMeta`). The owner's user slot's keyed name hash follows the display name, so the old name is free at once; a new vault may take it, with an equal stored name under another id and another vault key. This adds no key and no route to a key, and whoever opens the meta could read the stored name anyway. Tampering with the meta in the database is detected, since it no longer opens; an older meta of the same vault still opens, as it always did, so someone with write access to the database can put back an earlier display name or embedding provider. That shows a wrong name or sends text to a provider the owner chose earlier; it reaches no notes."
  - `:575`, a new bullet after "The member's own side": "**The owner's own name.** `PUT /api/vaults/{vault}/name`, session only, owner only (a member gets 404); no password, as it is undone by renaming again and removes no access; 30 a minute, shared with members' renames. In one transaction, refused while the vault is re-keyed: the user slot's name hash and the meta. The event `vault-renamed` holds no name and is the owner's alone; members keep their alias and aren't told. Keys and connections read the meta through the vault, so their slots aren't touched; agents find the vault under the new name, and a device, which asks by the old name, pairs again. Open pairing codes are dropped. The new name reaches no log."
  - `:609`: "renames of vaults shared with her 30/min per user" becomes "renames of vaults, her own and those shared with her, 30/min per user". Add the owner's rename to what answers `503` while re-keyed, beside the member's rename.
  - The rotation's description (§3, "Key rotation and re-keying", near `:162`): a rename between the preparation and the commit makes the rotation conflict, for a user slot as for a member slot.
- [ ] **Security review, by a fresh subagent** given the whole diff (`git diff 4b84fa4...HEAD -- src web/src docs/security-architecture.md`), `docs/security-architecture.md` and the hard-rule list, and not your conclusions. Its questions:
  1. Can a member, a key, a connection or the dev token rename, or learn the stored name?
  2. Can a stored path or stored name leak to a caller by any route, in an error or a log?
  3. Can one vault's request reach another with an equal stored name?
  4. Can a rename lose the name index, the provider or a re-key's state, or conflict a finish forever?
  5. Is the new name logged anywhere?
  Fix what it finds through red and green, and put every finding, fixed or not, in the report. `/security-review`, if your session has it, may serve as this subagent.
- [ ] **Run the hard-rule tests:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*MembershipCheckTests" --filter-class "*BoundMembershipWriteTests" --filter-class "*PinTests" --filter-class "*NoNamesTests" --filter-class "*MarkerTests"`. **Expected:** exit 0, none of them edited (`git diff 4b84fa4...HEAD --stat -- tests/Kumbu.Server.Tests/Sharing/NoNamesTests.cs` and the others show nothing).
- [ ] **Commit:** "Describe renaming vaults in the security architecture".
- [ ] **Phase gate:** the full verification.

```sh
dotnet restore --locked-mode && dotnet build --configuration Release --no-restore && dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

  **Expected:** exit 0 for each, 0 failed; totals above the baseline's 3264 and 1345, sidecar still 78.

## Final task: report and draft pull request

**Do not merge.** No version change, no tag.

The order matters, so that the report and the row hold a real link: push, draft pull request, report and open-items row, push.

- [ ] Push: `git push -u origin feature/rename-own-vaults`.
- [ ] Open a **draft** pull request from `feature/rename-own-vaults` to `master`: `gh pr create --draft --base master --head feature/rename-own-vaults --title "Rename own vaults, and three small things from the collab test"`. The body says what was built, lists the corrections to the spec, and says that the security review applies. It ends with exactly:

```
🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

  **Do not merge.** If no pull request can be opened, use the compare URL for `master...feature/rename-own-vaults` of this repository's `origin` in place of the link below.
- [ ] Write `docs/superpowers/overnighter/2026-11-02-rename-own-vaults-report.md`:
  - the environment;
  - Task 0 planned against measured, with the per-project split CI didn't print;
  - per task the commit and the real test output;
  - every mutation individually;
  - deviations and the calls you made;
  - surprises, first among them every row of Task 7 that failed before a fix;
  - the security review's findings;
  - open points for Chris, with what he must check by hand:
    - a real Obsidian plugin after a rename (the sentence "This key can't open the vault … any more", pairing again);
    - after pairing again, the old device key still holds `Maintain` on the vault, now under the new name, until he revokes it: is that what he wants?
    - a second open tab on the old name's route;
    - a member's open tab seeing the notice;
    - the dialog on a phone;
  - that the old device key isn't revoked by a rename or by pairing again, as above;
  - what has no test: a `LoadAsync` that read the meta before a rename's commit and publishes after it has no hook, and neither has the gate being free while the rename waits for `_loading`; both rest on reading the code;
  - that the Settings entry's sentence on a re-encryption is unchanged, though it says the same to a member without keys (spec §3);
  - the PR link.
- [ ] Add a row to `docs/superpowers/open-items.md`, in its table: `| 2026-11-02 | Renaming own vaults, and three small things: draft pull request from the overnighter run; review, security review's findings, hand checks named in the report | <PR link> |`.
- [ ] Commit the report and the row, and push: `git push origin feature/rename-own-vaults`. **Do not merge.**
- [ ] Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal.

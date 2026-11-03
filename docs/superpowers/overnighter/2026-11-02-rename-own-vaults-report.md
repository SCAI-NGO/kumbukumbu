# Overnighter report: renaming one's own vaults

Run of 2026-11-02, from the brief `2026-11-02-rename-own-vaults-brief.md` and the plan `../plans/2026-11-02-rename-own-vaults.md`. All tasks 0 to 10 are done, reviewed and pushed. The final verification is green (see "Final verification").

**Pull request (draft):** https://github.com/SCAI-NGO/kumbukumbu/pull/136

## Environment

- **Machine:** cloud container, 4 cores, 15 GB RAM, Linux 6.18; I worked as root.
- **.NET:** SDK 10.0.403 (`global.json` asks for 10.0.100, roll forward). `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Node:** the container had Node 22. I installed **Node 26.13.0** by hand from nodejs.org, with its SHA-256 checked against `SHASUMS256.txt`. Then I pointed `/opt/node22/bin/node` at it, since that directory comes first on `PATH`; the original binary is kept as `node22.orig`.
- **pnpm:** 10.28.0 was installed; I installed **10.32.1** with `npm install -g pnpm@10.32.1`.
- **Docker:** 29.9.2, running. **MongoDB:** the brief's workaround, a `mongo:7.0` container `kumbu-test-mongo` with `--ulimit nofile=20000:20000` and the WiredTiger handle settings, started as a single-node replica set. The tests used it via `KUMBU_TEST_MONGO`, with `TESTCONTAINERS_RYUK_DISABLED=true`. Every full run got a fresh container (`docker rm -fv` first).
- **The 3 phase gates and the final verification** each ran in their own `git worktree` on the exact commit being verified. That way the implementers could go on working in the main tree, and the gate saw no half-done edits.
- **Branch:** the session's own configuration made `claude/rename-own-vaults-p9it7z` (from the same `20ec5ba`) the only branch I was allowed to push to without explicit permission. So until about 18:00 everything went there, not to `feature/rename-own-vaults` as the brief says. Chris then asked for `feature/rename-own-vaults` explicitly. I fast-forwarded it (`20ec5ba..f5688dd`, 11 commits, nothing rewritten), and from then on both branches were kept equal after every push. The pull request is from `feature/rename-own-vaults`.

## Task 0: baseline

| | Planned (from CI) | Measured |
|---|---|---|
| Restore + Release build | succeeds, 0 warnings | succeeded, 0 warnings, 0 errors, about 77 s |
| `dotnet test` | 3264 total, 3260 succeeded, 4 skipped, 0 failed | **3264 total, 3258 succeeded, 6 skipped, 0 failed**, 8 min 41 s |
| Core (not in CI's log) | – | 1367: 1365 succeeded, 2 skipped (the two tokenizer tests) |
| Server (not in CI's log) | – | 1897: 1893 succeeded, 4 skipped |
| Web | 84 files, 1345 tests | 84 files, 1345 tests, build succeeded |
| Sidecar | 6 files, 78 tests | 6 files, 78 tests, build succeeded (see below) |

- **The 2 extra skips** are the brief's root case: `SeedScannerTests.Skips_unreadable_files_with_a_warning` and `ModelFilesTests.AReadOnlyModelFolderTurnsSearchByMeaningOffInsteadOfStoppingTheServer`. The 4 expected ones were skipped as listed in the plan.
- **Sidecar:** the first run failed one timing test, `classify > gives a page with very many path pieces one drawing quickly` (511 ms against a limit of 500 ms). It ran while `dotnet test` was loading all 4 cores. Run alone, all 78 passed.
- **Time:** about 27 minutes (14:59 to 15:26), against the plan's 15. The overrun is mine: I ran the first `dotnet test` in the background with a 10-minute limit, which killed it shortly before the end, and it had to run again. The server suite alone takes 9 to 12 minutes here.

## How the work ran

- **Per task:**
  - a fresh implementer subagent did red, green, commit and every mutation (break, run, see it fail, `git checkout -- <file>`);
  - then a spec-compliance review and a code-quality review, each by its own fresh subagent, given the commit and not my conclusions;
  - review findings went back through red, green and mutation, and I pushed.
- **One deviation:** for Task 7 (tests only) and Task 8, one subagent did both reviews, in two separate passes.
- **Subagents** never pushed, merged or switched branches.
- **My own commits:** three very small ones, where a fresh subagent would have cost more than the change. Two only fix wording in comments: `b1d7a95` and `a10112d`. The third, `fe5f303`, fixes the phase gate's failure; its red is the gate's run.

## Per task: commits and test output

Each "red" is the failing run before the change, each "green" the run after it, and the counts come from `dotnet test` or `pnpm test` output.

### Phase A

**Task 1, "Leave this vault…" in the vault menu**
- **Commits:** `d765638`, plus `066111e` from the review.
- **Red:** `TreeDrawerFolders MobileShell MemberPanel` gave 2 files failed, 5 tests failed and 77 passed. The 3 × `a member (…) has Leave this vault… last` tests expected "Leave this vault…" and found "Export as ZIP…". `Leave this vault… opens the dialog and leaves` found no menu item. MobileShell's read-only list had 6 items where 7 were expected.
- **Green:** 3 files, 82 tests; `pnpm -C web build` succeeded. After the review: 82 tests and the build again.

**Task 2, a changeset when restoring from the trash**
- **Commits:** `0de587a`, plus `e7aad26` from the review.
- **Red:**
  - Core: 98 tests, 5 failed, each on "Value is null":
    - `ChangesetRecordTests.A_restore_from_the_trash_joins_the_writers_changeset`
    - `ChangesetRecordTests.A_restore_after_the_idle_time_starts_a_new_changeset`
    - `ChangesetRecordTests.A_checkpoint_restore_joins_no_changeset`
    - `AttachmentWriterTests.Restore_from_trash_joins_the_writers_changeset`
    - `AttachmentWriterTests.Restore_from_trash_brings_back_the_bytes`
  - Server: `EncryptedNoteStoreTests.A_restore_seals_the_changeset_it_is_given` didn't compile (CS1501, the new parameter), which counts as red for a member the task creates.
- **Green:** Core 98 of 98. Server, the four classes `EncryptedNoteStore`, `Rekey`, `ActivityApi` and `HistoryApi`: 129 of 129. All of Core: 1370 (1368 succeeded, 2 skipped).
- **Red of the review fix:**
  - `ChangesetRecordTests.A_write_after_a_restore_from_the_trash_keeps_the_restored_text_in_history`: history was `["c1\n"]` where `["c2\n","c1\n"]` was expected.
  - `AttachmentWriterTests.A_write_after_a_restore_from_trash_keeps_the_restored_state_in_history`
  - `EncryptedNoteStoreTests.A_restore_keeps_the_restored_state_on_top_of_its_history`
- **Green of the fix:** Core 100 of 100, Server 130 of 130, all of Core 1372 (1370 succeeded, 2 skipped).

**Task 3, a notice for members after a re-encryption**
- **Commits:** `86386d9`, plus `2554a30` from the review.
- **Red:** 8 tests failed in `SharedRekeyTests` and `UserKeyRotationTests`, on an empty notice list, on a count of 0, or on `RekeyEndedMembers` being null. They were the key, connection, crash, failed-finish, cleared-list, membership-ended and dismiss tests, plus `A_rotation_reseals_a_rekey_notice`.
  - `A_member_who_left_during_the_period_gets_no_notice` passed at red, which is right for a guard; mutation 4 shows it bites.
  - The web test `shows a re-encryption notice with its Dismiss` passed at once, as the plan said, and is kept as a pin.
- **Green:** the four classes 83 of 83; `SharedBanner` 8 tests; the build succeeded. The five hard-rule classes plus `Rekey` and `Deletion`: 208 of 208.
- **Red of the review fix:**
  - `A_second_re_encryption_keeps_one_notice`: 2 notices where 1 was expected.
  - `A_notice_whose_alias_does_not_open_names_no_vault`: the text was wrong.
  - `A_member_with_a_key_and_a_connection_gets_one_notice` passed at red, as a pin.
- **Green of the fix:** 86 of 86, and `SharedBanner` 8.

**Phase A gate**
- **First run, at `86386d9`:** 3280 total, 1 failed. My log kept only the tail, so its name is lost. Rerunning the same Release binaries for the server alone gave 1908 total, 0 failed. I take it as flaky and name unknown.
- **Second run, at `2554a30`, complete:**
  - build 0 warnings, 0 errors;
  - `dotnet test`: **3283 total, 3277 succeeded, 6 skipped, 0 failed**;
  - web: 84 files, 1351 tests, build succeeded;
  - sidecar: 6 files, 78 tests, build succeeded.

### Phase B

**Task 4, the rename, with a new vault under the old name first**
- **Commits:** `5062818`, plus `e368111` from the review.
- **Red:** 13 of 13 failed. 11 got the route's 404 where 204 was expected; one of them was the 503 test, which also got 404. `A_meta_without_a_display_name_is_as_before` failed only in its second half, as the plan said. The 2 tests that call the catalog directly failed on the compile stubs' `NotImplementedException`.
- **Green:** `VaultRenameTests` 13 of 13. The task's command (9 classes): 184 of 184. With `NoteApiTests` and the hard-rule classes: 85 of 85.
- **Red of the review fix:** `A_bug_inside_the_rename_is_not_answered_as_not_found` got 404 where 500 was expected.
- **Green of the fix:** 39 of 39.

**Task 5, the name index, and everything that re-seals the meta**
- **Commits:** `f4012c5`, plus `bf7c03d` from the review.
- **Red:** 165 total, 7 failed, the ones the plan expected:
  - `UserKeyRotationTests.A_rotation_keeps_a_renamed_vaults_name_in_the_index`: `Garten` not found.
  - `UserKeyRotationTests.A_vault_rename_between_prepare_and_apply_makes_apply_fail`: no conflict.
  - `InvitationApiTests.Peek_at_a_renamed_vault_shows_its_shown_name`: answered `Gartenarbeit`.
  - `Recovery_names_a_vault_by_its_shown_name`
  - `A_provider_change_through_an_older_object_keeps_the_name`: `DisplayName` came back null.
  - `A_provider_change_on_a_meta_that_does_not_open_answers_just_reencrypted`: answered 200.
  - `A_provider_change_conflicts_with_another_writer`
- **Passed at once, as pins of Task 4:** `Reencrypting_keeps_the_name`, `A_provider_change_keeps_the_name`, `A_rename_keeps_the_provider_chosen`, `A_rename_does_not_pin_the_default_provider`, `A_concurrent_provider_change_is_kept` and `Renaming_during_a_run_answers_being_reencrypted`.
- **Green:** 199 of 199. Another 215 across neighbouring classes, with 1 skip, the real model.
- **Red of the review fix:** `A_vault_rename_between_prepare_and_apply_conflicts_on_a_slot_without_a_name_hash`: no conflict.
- **Green of the fix:** 277 of 277 over ten classes.

**Task 6, what the rename does around its commit**
- **Commits:** `f5688dd`, plus `5e4e9c2` and `9a0f41d` from the review.
- **Red:** 127 total, 4 failed:
  - `SyncSocketTests.A_renamed_vault_names_the_hints_at_once`: no hint.
  - `SyncSocketTests.A_ticket_from_before_a_rename_names_the_new_name`: the old name.
  - `SyncHubReviewTests.A_rename_during_a_recomputation_keeps_the_new_name`
  - `PairingApiTests.A_rename_drops_open_pairing_codes`
- **Passed at once, as pins of Task 4:** member 404, key 403, no password, the member's alias untouched, the owner's activity, case only, the same name, a name taken by an alias, invalid names (4 cases), the rate limit, no log, and an old device 404.
- **Green:** 127 of 127.
- **Red of the review fix:**
  - `SyncHubReviewTests.A_rename_during_a_new_connections_refresh_keeps_the_new_name`: the old name came back.
  - `PairingApiTests.A_rename_whose_outcome_is_unknown_drops_open_pairing_codes`: the code was still open.
- **Green of the fix:** 190 of 190.

**Task 7, the owner's matrix**
- **Commits:** `6b59925` and `8db7583`, plus `daa48a3` from the review.
- **Green:** 70 tests, 70 succeeded: the 66 cells plus the 4 extra tests. None is skipped; the fake embedder and the fake sidecar of the test app cover search by meaning and PDFs.

**Task 8, `deviceKeys` in the access view**
- **Commits:** `ccae2e6` and `c4233ef`, plus `b1d7a95` from the review.
- **Red:** 97 total, 3 failed, each a `NullReferenceException` on the missing `deviceKeys`.
- **Green:** `MembershipApiTests` and `RekeyTests` 97 of 97; `AccessList` 11 tests; the web build succeeded.

**Task 9, "Rename vault…" in the web app**
- **Commits:** `8162ad6` and `0e1961d`, plus `518117f` and `f40bf02` from the review.
- **Red:** the module didn't resolve, and 2 menu tests failed in `TreeDrawerFolders`.
- **Green:** 3 files, 91 tests; the build succeeded; the whole web suite 85 files, 1365 tests.
- **Red of the review fix:** 5 failed. They were: the save never ran, no problem message, the URL stayed after the dialog went away, `search` was not invalidated, and old-name refetches showed in the menu flow.
- **Green of the fix:** 93 tests; whole suite 85 files, 1367 tests; build succeeded.

**Phase B gate, at `c4233ef`**
- **Build:** 0 warnings, 0 errors.
- **`dotnet test`:** 3406 total, 3399 succeeded, 6 skipped, **1 failed:** `PasswordWindowTests.Every_session_only_endpoint_is_guarded_or_deliberately_open`, naming `PUT /api/vaults/{vault}/name`.
  - This was a real finding. None of the task commands had that class in its filter.
  - The rename asks for no password, by the maintainer's decision, so the route goes on the list of deliberately open session endpoints with its reason (`fe5f303`). After that, `PasswordWindowTests` passed 68 of 68.
- **Web:** 84 files, 1351 tests, build succeeded. **Sidecar:** 6 files, 78 tests, build succeeded.

**Task 10, the security document and the security review**
- **Commits:** `dd16711` and `a10112d`; from the review, `f653be0` and `0203fb7`.
- **Red of L2:** `SyncSocketTests.A_rename_whose_outcome_is_unknown_tells_her_sockets` expected a `changed` frame and got null.
- **Green:** 181 of 181 over the rename, socket, hub, pairing and password-window classes.
- **Hard-rule classes:** `git diff 4b84fa4...HEAD` shows no change to any of the five, and they are green in the final run.

## Mutations, each individually

Every mutation below was applied alone, run with the task's command, seen failing, and undone with `git checkout -- <file>`.

**Task 1**
1. Offer the entry to everyone: `the owner's vault menu has no Leave this vault…` and `…offers Delete vault…, a member's doesn't` fail.
2. Render no dialog: `Leave this vault… opens the dialog and leaves` fails.
3. Entry before "Export as ZIP…": the 3 `a member (…) … last` tests and MobileShell's read-only list fail.
4. Extra: the plan's own form, with the dialog gated on `activeVault`: `opens the dialog and leaves` fails. That is the reason for deviation 1.
5. From the review: skip `forgetVault` in `useLeave`: the leave test fails on the new `recentPaths` assertion.

**Task 2**
1. `Changeset = null` in `InMemoryNoteStore`: the three trash-restore tests in `ChangesetRecordTests` fail.
2. `Changeset = null` in `AttachmentWriter`: `Restore_from_trash_joins_the_writers_changeset` and `Restore_from_trash_brings_back_the_bytes` fail.
3. `Changeset = null` in `EncryptedNoteStore`: `A_restore_seals_the_changeset_it_is_given` fails.
4. From the review: no history push for notes: `A_write_after_a_restore_from_the_trash_keeps_the_restored_text_in_history` (Core), and `A_restore_keeps_the_restored_state_on_top_of_its_history` and `Trash_moves_the_note_with_its_history_and_restore_brings_it_back` (Server) fail.
5. From the review: no history push for attachments: `A_write_after_a_restore_from_trash_keeps_the_restored_state_in_history` fails.

**Task 3**
1. A notice for every active member: `A_member_whose_key_ended_gets_a_notice`, `…connection_ended…` and `The_notice_survives_a_crash_after_the_start` fail.
2. Drop the `Unset`: `The_finish_clears_the_list` fails.
3. Connections only through slots: `A_member_whose_connection_ended_gets_a_notice` fails.
4. No `Status == Active` when writing: `A_member_who_left_during_the_period_gets_no_notice` fails.
5. List rekey notices whatever the membership's status: `A_rekey_notice_goes_once_the_membership_ended` fails.
6. Notice outside the transaction: `A_finish_that_fails_leaves_no_notice_and_its_retry_one` fails.
7. `PrepareResealAsync` only for `VaultDeleted`: `A_rotation_reseals_a_rekey_notice` fails.
8. From the review: no deletion of the earlier notice: `A_second_re_encryption_keeps_one_notice` fails.

**Task 4**
1. `Owned` on `vault.Name`: 10 tests fail, among them `A_new_vault_may_take_the_old_name…`, `Writes_to_either_stay_there` and `The_name_survives_a_reload`.
2. `CredentialAccess` on `vault.Name`: `The_key_of_the_renamed_vault_does_not_reach_the_new_one` fails.
3. `LoadAsync` without `DisplayName`: `The_name_survives_a_reload` fails.
4. The same for the placeholder: `A_renamed_vault_in_its_two_key_period…` fails.
5. `FindForUserAsync` on `meta.Name`: `Deleting_the_renamed_vault_leaves_the_new_one` fails.
6. No `NameId` update: 10 tests fail.
7. No meta update: 4 tests fail (two-key period, deleting the renamed vault, the meta without a display name, survives a reload).
8. `display = newName` always: `Renaming_back_is_refused…` fails.
9. No `[JsonIgnore]` on `Shown`: `A_meta_without_a_display_name_is_as_before` fails.
10. `NameId` from `meta.Name`: the same 10 as mutation 6.
11. No `Forget` after an unknown outcome: `A_rename_whose_outcome_is_unknown_lets_the_object_go` fails.
12. `Forget(unknown, e)`: the same test fails, on the name in a log line.
13. `Current(id)` not updated: `A_rename_through_an_older_object_tells_the_current_one` fails.
14. `OpenStoredMeta` without its `catch`: `A_meta_that_does_not_open_answers_just_reencrypted` fails (500).
15. From the review: catch every `ArgumentException` in the handler: `A_bug_inside_the_rename_is_not_answered_as_not_found` fails.
16. From the review: the broad filter in the catalog: the same test fails.

**Task 5**
1. The rotation on `meta.Name`: both new rotation tests fail.
2. No `OldNameId` filter: `A_vault_rename_between_prepare_and_apply_makes_apply_fail` fails.
3. `InviteMetaAsync` on `meta.Name`: `Peek_at_a_renamed_vault_shows_its_shown_name` fails.
4. `RecoverAsync` on `vault.Name`: `Recovery_names_a_vault_by_its_shown_name` fails.
5. The provider change seals from the object: `A_provider_change_through_an_older_object_keeps_the_name` fails.
6. The re-key finish drops `DisplayName`: `Reencrypting_keeps_the_name` fails.
7. The rename seals `vault.EmbeddingProvider`: 4 tests fail, among them `A_rename_does_not_pin_the_default_provider`.
8. The rename reads the meta before the transaction: `A_concurrent_provider_change_is_kept` fails. This was the subtler form, with the byte condition kept.
9. The provider write without the byte condition: `A_provider_change_conflicts_with_another_writer` fails.
10. From the review: check `NameId` only when `OldNameId` is set: `…conflicts_on_a_slot_without_a_name_hash` fails.

**Task 6**
1. No `IsOwner` check: `OwnerOnlyTests.A_member_may_not_rename_the_vault` fails (204).
2. No `SessionOnlyMetadata`: `A_key_may_not_rename_the_vault` fails (500).
3. No limit: `Renames_are_rate_limited_with_aliases` fails.
4. No `DropAll`: `A_rename_drops_open_pairing_codes` fails.
5. `VaultsChangedAsync` instead of `VaultRenamed`: `A_renamed_vault_names_the_hints_at_once` and `A_rename_during_a_recomputation_keeps_the_new_name` fail.
6. `RenameOwned` without `Membership is null`: 12 socket tests fail.
7. `VaultRenamed` without the `Subject.User` filter: `A_renamed_vault_names_the_hints_at_once` fails (Bob gets a hint).
8. `VaultRenamed` in `VaultWide`: `A_members_alias_is_untouched` fails.
9. The name logged at information level: `The_new_name_reaches_no_log` fails.
10. No refresh in `RunAsync`: `A_ticket_from_before_a_rename_names_the_new_name` fails.
11. From the review: read the name outside the lock again: `A_rename_during_a_new_connections_refresh_keeps_the_new_name` fails.
12. From the review: no `DropAll` on the unknown path: `A_rename_whose_outcome_is_unknown_drops_open_pairing_codes` fails.
13. From the review: no member guard: 12 tests fail.
14. From the security review (L2): no `hub.VaultRenamed` on the unknown path: `A_rename_whose_outcome_is_unknown_tells_her_sockets` fails.

**Task 7** (each must fail several rows)
1. `ToVault` returns the path unchanged: 37 rows fail.
2. `ToCaller` returns the path unchanged: 61 rows fail. After the review's fixes, 61 of 70 fail.
3. `Shown(VaultAccess, Checkpoint)` returns the checkpoint unchanged: 17 rows fail.
4. `Stored(VaultAccess, NoteBatch)` returns the batch unchanged: 28 rows fail.
5. `Renamed` becomes `!IsOwner && …`: 48 rows fail.
6. `SyncApi.cs:142` without `ToCaller`: **survived the matrix as planned.** Only the state hash uses those paths, and a 304 doesn't show a wrong hash. Now the sync rows recompute the state hash from the listed paths (`6b59925`, `8db7583`), and 3 rows fail.

**Task 8**
1. `DeviceKeys` lists every key: `The_access_view_names_device_keys` and `No_device_key_is_an_empty_list` fail.
2. No revoked filter: **survived at first.** A revocation deletes the key's slots in the same transaction, so `keyIds` already drops the key. Now a key is marked revoked directly in the database, with its slot kept (`c4233ef`), and `A_revoked_device_key_is_not_listed` fails.

**Task 9**
1. Device sentence always: `names Obsidian devices only if there are device keys` fails.
2. Hidden on an access error: `says it also if the list can't be read` fails.
3. No navigation: `goes on showing the same note under the new name` and the menu flow test fail.
4. `activeVault` left as it was: the same two, plus `on a page with no note open, only the active vault changes`, fail.
5. Entry offered to members: 3 × `a member (…) is offered no Rename vault…` and MobileShell's read-only list fail.
6. Old name's caches kept: `forgets the old name's caches and asks for the vaults again` fails.
7. Extra: dialog gated on `activeVault`: **survived at first**, because a rename never empties `activeVault`; the dialog only re-targets to the new name. `0e1961d` asserts that no access list is requested for the new name, and the test now fails.
8. From the review: no flush: `goes on showing the same note…` and `saves the open note's pending text first…` fail.
9. From the review: navigate only in the `mutate` call again: `follows the note also if the dialog went away…` fails.
10. From the review: wait for the refetches before navigating: **survived at first.** `f40bf02` holds back the vault list's answer and expects the new URL meanwhile, and the test now fails.
11. From the review: drop `search` from the refetch list: `forgets the old name's caches…` fails.
12. Extra: remove the old-name queries at once: the menu flow test fails, on old-name refetches.
13. Extra: no cache subscription: the menu flow test fails, on old-name queries left behind.

**Task 10:** no mutations beyond those listed under Task 6 (L2). The phase gate's failure above stands for the `PasswordWindowTests` entry.

## Deviations, and the calls I made

1. **Task 1:**
   - **Dialog state:** the dialog's state holds `{ vault, membership }` from the moment the entry is chosen, not `useState(false)` gated on `activeVault`. Leaving clears `activeVault` before the dialog's success handler runs, so the plan's form unmounts the dialog, and the user stays on a note she can no longer read.
   - **The same trap in the rename:** Task 9's dialog holds its vault the same way.
2. **Task 2, the most important call of the run:**
   - **The conflict:** spec §4 asks a restore from the trash to join the writer's open changeset. The earlier changesets design (`specs/2026-10-24-changesets-checkpoints-design.md` §2) says a restore never joins one, "so the next write never coalesces into it". The code review proved the risk: rm, then restore, then a write within the idle time merged into the restored state, and the restored text was lost from history for good.
   - **My decision, the safer reading:** the restore joins the changeset, as the approved spec asks, and its state is also put on top of its own history. A later write in the same changeset can no longer drop it.
   - **Visible consequence:** right after a restore, `history` shows the restored version twice: as current, by the restorer, and below with its original writer and time. Activity shows the restore as `changed X->X`. A restore of a version looks the same. `docs/concept.md:74` and `README.md:103` now say so.
   - **Not edited:** the earlier spec, as the brief says; it now contradicts this behaviour.
3. **Task 3:**
   - **Where things live:** the kind `vault-rekeyed` sits beside `vault-deleted` on `MemberNoticeDocument`. The read check is a new `MemberNotices.ActiveOwnersAsync`, which also requires the membership's member to be the caller, as an extra safeguard.
   - **Shared code:** `LeaveForDeletedAsync` and `LeaveForRekeyAsync` share one private `LeaveAsync`.
   - **One notice per membership:** a second re-encryption replaces the earlier notice, from the review.
   - **Fallback text:** when the alias doesn't open, the notice reads: `"Alice" re-encrypted a shared vault; your keys and apps for it were revoked. Give your agents that vault again.`
4. **Task 4:**
   - **Direct catalog calls:** two tests call `RenameVaultAsync` directly rather than through REST: the unknown outcome and the older object. Through REST, the host's own unhandled-exception log would print the fault's message, and the log assertion is meant to pin the catalog.
   - **Keys by id:** keys are made by vault id (`KeyForAsync`), because of the `RestClient` trap.
   - **Narrower catch:** the handler answers 404 only for exactly `ArgumentException`, and the catalog counts any subclass as an unknown outcome. That goes a little beyond the review's request, and is the safer reading.
5. **Task 5:** a user slot of a rotation is always conditioned on its name hash, also when it has none: `SlotRewrap.CheckNameId`, from both reviews.
6. **Task 6:**
   - **One guard for members:** the member guard is only inside `RenameOwned`, and the refresh calls it for every vault. Two copies would have made mutation 6 impossible to catch.
   - **Name read under the lock:** after the review, the name is read from the catalog under the connection's lock, for the refresh as for the rename. A rename can no longer be undone by a refresh that read before it.
   - **Unknown outcome:** pairing codes are dropped and the owner's sockets are hinted also when the rename's outcome isn't known.
7. **Task 7:**
   - **Checkpoint scope:** the expected scope is `/Garten/`, with a slash, as for any vault, not the plan's `/Garten`.
   - **Search across vaults:** this uses `q=e`, since `plan` is in no note's text.
   - **Upload:** `POST /api/upload` takes only Markdown and answers with paths relative to the folder, so that cell checks a skipped image and a conflict instead of an absolute path.
   - **Guard:** every request in the matrix fails if a response body or header names `Gartenarbeit`, unless the row reads it on purpose.
8. **Task 9:**
   - **Saving first:** the dialog saves pending edits first, only when the open note is in that vault.
   - **Navigation:** it happens in `useRenameVault`'s own `onSuccess`, so closing the dialog mid-flight can't lose it.
   - **Caches:** the old name's queries are removed once nothing watches them any more (`forgetOnceUnused`). A data router can't wait for `navigate`, and removing them at once made the still-mounted views fetch them again under the dead name.
   - **Field:** `maxLength={64}`.
9. **`PasswordWindowTests`:** the rename is listed among the deliberately open session endpoints, with the spec's reason.
10. **A test-only hook in `RenameVaultAsync`:** `MetaFaultAt("after-rename-commit")`, right after the transaction, so that "committed, but the outcome is unknown" can be tested. In production it is null.

## Surprises

- **The owner's matrix (Task 7): no row failed before a fix.** All 66 cells passed against the renamed vault with the decoy beside it, through a session, a key over REST and a key over MCP. The translation is shared with members and holds for owners without any case of its own. The 7 tests that were red on the first run were all mistakes in my tests (the checkpoint scope's slash, `mv` of a folder without a trailing slash, a search for a word no note holds), not in the server.
- **Mutations the plan's tests didn't catch:** Task 7 mutation 6, Task 8 mutation 2, and two of the web mutations. Each test is now strengthened.
- **The phase gate found what the tasks didn't:** `PasswordWindowTests` covers every session endpoint and was in no task's filter.
- **The new name never reached a log**, even before Task 6. The 409 is answered, not logged, and `Program.cs` filters ASP.NET's request lines to warnings.
- **The plan's C# compiled almost unchanged.** Only a few `using` directives and the null-forgiving operator in a mutation needed help.
- **`RestClient.NewAsync` defaults to the vault name `Arbeit`**, which is not on the brief's list of names. It was there before; I didn't change it.

## Security review

A fresh subagent was given the whole diff (`git diff 4b84fa4...a10112d -- src web/src docs/security-architecture.md`), the security document and the hard rules, but not my conclusions. Its answers:

1. **Can anyone else rename, or learn the stored name?** No. A member, a key, a connection, a device or the dev token can neither rename nor learn the stored name. The route is session only (403 at `Api/Security.cs:146`), a member gets 404, the MCP shell has no vault rename, and every caller-facing name comes from `VaultAccess.Name`.
2. **Can a stored path or name leak?** No. The owner goes through the same translation members did. Export folders, socket frames, pairing codes, notices and errors use the caller's name.
3. **Can a request reach another vault with an equal stored name?** No. Everything is keyed by vault id, and the matrix pins it.
4. **Can a rename lose the index, the provider or a re-key's state?** Not by ordinary use. Someone with database write access can roll the meta back; see L1.
5. **Is the new name logged?** No.
6. **The rest:** notices, `deviceKeys`, the rate limit and the password decision are fine. The document's new citations are accurate.

The five hard-rule classes passed 106 of 106 in its run, and the rename's classes 154 of 154.

**Findings:**

| | Severity | Finding | State |
|---|---|---|---|
| L1 | low | Someone with database write access puts back an older meta of a renamed vault, whose earlier name now belongs to a new vault of the owner's. Then every request of hers fails with 500 (`EncryptedNoteStore.cs:988`), the rename that would fix it too, and so do a user-key rotation and an account recovery (both slots get one name hash; the unique index refuses). The reviewer reproduced the 500s with a probe; the rotation and recovery part rests on reading the code. The only way out is deleting the other vault. | **Not fixed.** The fix needs a design decision; see open points. `docs/security-architecture.md` §5.3 says it now. |
| L2 | low | After a rename whose outcome is unknown but committed, the owner's sockets kept the old name; beside a new vault of that name, her tabs and devices take the wrong vault's `changed` frames (no data moves). | **Fixed** in `f653be0`, with a test. |
| L3 | low | A sync ticket redeemed after a rename keeps the old name if the vault was unloaded from the catalog in between. Tickets live 30 s. | Documented (`0203fb7`); not changed. |
| I1 | info | A device key keeps its label with the old vault name, "Gartenarbeit – Obsidian Laptop", and keeps `maintain` on the renamed vault. Beside a new `Gartenarbeit`, the owner could revoke the wrong key, or forget a live one. | Open point. |
| I2 | info | Two quick renames while `_loading` is held elsewhere can briefly show the earlier name in memory; the database stays right. | Accepted. |
| I3 | info | An OAuth consent posts vault names, not ids. A rename plus a new vault under the old name between showing the consent and submitting it grants the vault of that name. Only her own actions can cause it. | Accepted. |

**Earlier findings from the reviews, related to security, not fixed:**
- **Pairing codes are dropped for all of the owner's vaults** on a rename, not just the renamed one, as the other callers of `DropAll` do.
- **Taken names:** a duplicate key from an orphaned slot of a failed vault creation answers "taken". Vault creation reclaims such orphans after a grace time; the rename doesn't.
- **Rate limit:** the limit is spent before the owner check and before the name check, as with alias renames.

## Open points for Chris

### What needs checking by hand first

1. **A real Obsidian plugin after a rename.** It should stop with "This key can't open the vault … any more", and pair again from "Connect Obsidian…".
2. **The old device key still holds `Maintain`** on the vault after pairing again, now under the new name, until you revoke it. Neither a rename nor a new pairing revokes it, and its label keeps the old name (I1). Is that what you want? The dialog could offer to revoke the vault's device keys.
3. **A second open tab on the old name's route.** It is hinted and asks for the vault list again, but it is still on `/notes/<old name>/…`. Does it land somewhere sensible?
4. **A member's open tab seeing the re-encryption notice**, and its Dismiss.
5. **The rename dialog on a phone.**

### To decide

- **L1, the rolled-back meta.** Options from the review:
  - in `OpenForUserAsync`, check the slot's `nameId` against the meta's shown name, and on a mismatch fall back to the stored name or leave out only that vault;
  - or let the duplicate-name refusal drop only the two vaults concerned, not the whole request, and let the rename resolve by id.
  
  Either way it touches how vaults are listed, so I left it for a design and review.
- **The Settings entry on a re-encryption** keeps its sentence, as spec §3 says, although it tells a member without keys or apps the same as one whose credentials ended. Change it?
- **A trash restore and its history.** It now shows the restored version twice, as current and as the entry below it (deviation 2). Is that acceptable, or should `history` and activity hide the duplicate?
- **The re-encryption notice keeps the alias it copied at the finish.** If the member renames her alias afterwards, it names the old alias. The spec asks for the copy; reading the live slot at list time would avoid it.

### Without a test, resting on reading the code

- **A load that races the rename's commit:** a `LoadAsync` that read the meta before the commit and publishes after it has no hook, so nothing tests it.
- **The gate while the rename waits for `_loading`:** that the gate is free meanwhile has no test either.

### Smaller, found along the way; most of it predates this branch

- **Leaving a vault:** `MobileShell.tsx:201` sets the active vault back to the first vault of the cached list, which is still the one just left, until the list is fetched again. This also happens through "Shared with you…".
- **A provider change made through an older vault object** updates the store and that object, but not the current object's `EmbeddingProvider`, so the search index embeds with the old provider until a reload. Fixing it would have put `_loading` into the provider change, which the brief keeps off limits.
- **An unknown outcome of a provider write** isn't followed by `Forget`. A vault deleted between the read and the write answers "just re-encrypted" (the rename does the same).
- **The alias rename (`useRenameAlias`) has the gaps that Task 9's review closed for the vault rename:** no flush of pending edits, a dialog that can close mid-flight, and no refresh of `search` and `changes`. `DeleteVaultDialog` and `useLeave` probably refetch under the old name the same way.
- **An attachment restored from its history** (not the trash) still has no changeset.
- **Sockets of API keys and devices** keep the old name after a rename of unknown outcome, until their client takes a new ticket.
- **Stale citations in `docs/security-architecture.md`:** about 55 citations into files this branch touched were already wrong before it. They are in `VaultCatalog.cs` past line 1000, and in `UnlockedVault`, `NoteApi`, `Pairings`, `SyncHub`, `Documents` and `Security`. I corrected those I touched and the `VaultCatalog.Rekey.cs` and `UserKeyRotation.cs` ones. The rest needs a pass of its own; examples are the citations at the document's lines 127, 132, 166, 247 and 413.
- **A small character difference:** .NET's `Trim` removes U+0085 and JavaScript's `trim()` doesn't, so a name ending in it could make the dialog navigate to a name the server stored without it.

## Final verification

Run on `0203fb7`, the last code commit, in a worktree of its own, with a fresh MongoDB. Exactly CI's steps:

| Step | Result |
|---|---|
| `dotnet restore --locked-mode` | succeeded |
| `dotnet build --configuration Release --no-restore` | 0 warnings, 0 errors |
| `dotnet test --configuration Release --no-build` | **total 3407, succeeded 3401, skipped 6, failed 0** (9 min 4 s). Core and Server both passed |
| `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build` | **85 files, 1367 tests** passed; build succeeded |
| `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build` | **6 files, 78 tests** passed; build succeeded |
| Hard-rule classes (`MembershipCheckTests`, `BoundMembershipWriteTests`, `PinTests`, `NoNamesTests`, `MarkerTests`) | **106 of 106**; `git diff 4b84fa4...HEAD` shows no change to any of them |

- **Against the baseline:** `dotnet test` gained 143 tests (3264 to 3407), and the web 1 file and 22 tests (1345 to 1367). The sidecar stays at 78, as the plan expected.
- **The 6 skips:** the 4 that need the real model or a TensorX key, and the 2 that skip because the run was as root.
- **After the last code commit:** only this report and the row in `open-items.md` follow; no code changed after `0203fb7`.

## Link

https://github.com/SCAI-NGO/kumbukumbu/pull/136 (draft, from `feature/rename-own-vaults` to `master`). Not merged.

# GDPR Deletion by an Admin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An admin deletes any user (active or not) for a privacy request in one careful step and gets a downloadable receipt and a reply template; the server remembers deleted user ids outside MongoDB and warns, with a one-step repeat, when a backup restore brings one back.

**Architecture:** `Deletion.DeleteUserAsync` drops the "deactivate first" rule and returns a receipt built from counts the server can see without any user's key. The API answers the receipt; the web app shows it, downloads it as text and builds the reply email from it. A small file store (`DeletionList`) appends deleted ids to a file in its own volume; a check compares it with the users collection at start and on the admin page.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, MongoDB driver, xUnit; React + TypeScript, TanStack Query, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-26-demo-round-design.md`, §5 (binding).

## Where and how this runs

- **Start only after the OAuth branch `step-10-oauth` is merged into `master`.** This plan is written against that code: `DeletionSummary(int Vaults, int ApiKeys, long Documents, int Connections)`, `ConnectionRevocation.DeleteAllAsync` inside `Deletion.DeleteAccountAsync`, `UserCatalog`/`Admin` constructors with `Connections`. Symbols are named rather than line numbers.
- Branch `gdpr-deletion` from `master`; one pull request. Chris merges it himself.
- Commands: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~<Class>"`, `pnpm -C web test`, `pnpm -C web build`. After the API change in Task 2, regenerate the web app's types: run the server (`dotnet run --project src/Kumbu.Server`, local MongoDB, port 8083) and `pnpm -C web gen:api`.
- Commit messages imperative and free-form, each ending with the session's attribution lines.
- **If time runs short** (Chris's cut-off is 20:00): Tasks 5 and 6 (the deleted-ids list and the repeat) are dropped **as a unit**; Tasks 1–4 and 7 stand on their own.

## Global Constraints

- Sessions only; admin endpoints keep `SessionOnlyMetadata` and the admin role check. Never for API keys, connections or agents.
- The receipt, the log and the deletion list hold **no content and no vault or note names**: ids, the username, the admin's username, counts, provider labels, times.
- The admin's own password and the target's username typed out are required, as today; the password rate limit (`AccountLimits.PasswordCheck`) stays.
- Never the primary admin; never the admin themselves through the admin endpoint (they use Settings → delete account).
- The deletion stays one MongoDB transaction (`Deletion.DeleteAccountAsync`); the list is written **after** it commits.
- Configuration: `Kumbu:Privacy:Contact` (optional, `KUMBU_PRIVACY_CONTACT`), `Kumbu:Deletions:Path` (optional; Compose sets `/deletions` on a named volume `deletions`).
- Deletion list file: `<Kumbu:Deletions:Path>/deleted-users`, one user id per line, UTF-8, appended, created `0600` in a `0700` directory (as `Seeder.WriteRecoveryKeyAsync` does), flushed to disk.
- English UI copy, British spelling. Exact strings:
  - Button: `Delete for a privacy request…`
  - Dialog title: `Delete <display> for a privacy request?`
  - Dialog text: `Before you go on: make sure the request really comes from this person, and answer within one month (GDPR Art. 12(3)). Their account goes for good, with every vault and note, the history, trash and checkpoints, their API keys and app connections. Nobody can bring it back. Backups are not reached.`
  - Receipt heading: `Deleted for a privacy request`
  - Buttons on the receipt: `Download receipt`, `Copy reply`
  - Restore warning: `A backup restore brought back <n> deleted user(s): <usernames>. Delete them again?` with button `Delete again…`
- Reply template (exact; `<…>` filled from the receipt; `<contact>` is `Kumbu:Privacy:Contact` or the literal `[your privacy contact]`):

  ```
  Subject: Your deletion request

  Hello,

  as you requested, we deleted your Kumbu² account "<username>" and all notes in it on <date, yyyy-MM-dd HH:mm UTC>. This includes every vault, its history, trash and checkpoints, your API keys and your app connections.

  Copies that AI tools or other apps read from your notes are outside Kumbu². Please delete them there, for example in your chat history.

  Our backups are not changed by a deletion; they are kept as described in our privacy notice. If a backup is ever restored, we delete your account again.

  In future, if you still have access, you can export your notes first and delete your account yourself in Settings.

  Kind regards
  <contact>
  ```

## Review Focus

1. **The target is signed in and working while the admin deletes them.** Expected: their next request answers `401`; nothing of theirs is written afterwards. Test in Task 1 (`Deleting_an_active_user_ends_their_session_and_writes`).
2. **An admin deletes themselves through the admin endpoint.** Expected: `403` with a sentence pointing to Settings, nothing deleted. Test in Task 1 (`An_admin_cant_delete_themselves_here`).
3. **The deletion list can't be written** (path missing, read-only volume). Expected: the deletion still succeeds, the receipt says it wasn't recorded, an error is logged; the server starts. Test in Task 5 (`An_unwritable_list_never_blocks_a_deletion`).
4. **The receipt leaks a name.** Expected: neither a vault name nor a note path appears in the receipt view, the downloaded text or the log. Test in Task 2 (`The_receipt_names_no_vault_or_note`).
5. **A malformed list file** (blank lines, CRLF, junk, duplicates). Expected: ignored lines, no crash, duplicates counted once. Test in Task 6 (`A_messy_list_reads_as_its_ids`).

---

### Task 1: One-step deletion of any user, with a summary for the receipt

**Files:**
- Modify: `src/Kumbu.Server/Accounts/Deletion.cs` (`DeletionSummary`, `DeleteUserAsync`, `DeleteAccountAsync`)
- Test: `tests/Kumbu.Server.Tests/Accounts/DeletionTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record DeletionSummary(int Vaults, int ApiKeys, long Documents, int Connections, long Notes);` (`Notes`: documents in the `notes` collection of the deleted vaults, counted by vault id before the transaction; no key needed.)
  - `public sealed record DeletedUser(string Id, string Username, string Display, string DeletedBy, DateTime DeletedAt, DeletionSummary Summary);`
  - `public Task<DeletedUser?> DeleteUserAsync(User actor, byte[] actorKey, string password, string userId, string confirm, CancellationToken ct)` — null if there is no such user.
  - `DeleteOwnAccountAsync` unchanged in signature; it still returns `bool`.

- [ ] **Step 1: Write the failing tests** in `DeletionTests`:

```csharp
[Fact] public async Task An_admin_deletes_an_active_user_in_one_step()
// arrange: admin + active user with a vault and a note; act: DeleteUserAsync(admin, key, Password, user.Id, user.Username)
// assert: result.Username == user.Username; result.DeletedBy == admin.Username; result.Summary.Vaults == 1;
//         result.Summary.Notes == 1; users collection has no document with that id; ContentCollections empty for the vault

[Fact] public async Task An_admin_cant_delete_themselves_here()
// assert: NotPermittedException with message "Delete your own account in Settings."; the admin still exists

[Fact] public async Task Deleting_an_active_user_ends_their_session_and_writes()
// arrange: user signed in (session secret); act: delete; assert: Sessions().ResolveAsync(secret) is null,
//          and a write through the user's unlocked vault afterwards fails (vault tombstoned)
```

Keep the existing tests for the primary admin, the typed confirmation and the wrong password; change `…only_deactivated…` expectations to the new rule.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~DeletionTests"`
Expected: the three new tests FAIL (active user refused; no `DeletedUser` type).

- [ ] **Step 3: Implement.** In `DeleteUserAsync`: refuse `actor.Id == userId` with `NotPermittedException("Delete your own account in Settings.")` after the password check; remove the `target.Active` refusal and `!u.Active` from the transaction filter (keep `Role != PrimaryAdmin`); count notes of the owned vaults before the transaction; return `DeletedUser` with `time.GetUtcNow()` (inject `TimeProvider` into `Deletion` as the other services do). Update the class and method summaries (no more "only a deactivated user"). Log line unchanged in shape, plus the notes count.

- [ ] **Step 4: Run the tests and see them pass** (same command). Expected: PASS, whole class.

- [ ] **Step 5: Commit** `Let an admin delete an active user in one step`

### Task 2: The receipt in the API, with the privacy contact and external providers

**Files:**
- Modify: `src/Kumbu.Server/KumbuOptions.cs` (add `PrivacyOptions Privacy`), `src/Kumbu.Server/Api/AccountApi.cs` (`DeleteUserAsync` endpoint, new records), `compose.yaml` (`Kumbu__Privacy__Contact: ${KUMBU_PRIVACY_CONTACT:-}`), `.env.example`
- Test: `tests/Kumbu.Server.Tests/Api/AccountApiTests.cs`

**Interfaces:**
- Consumes: `DeletedUser` (Task 1); `SearchIndex.Providers` (external flag and label, as `EmbeddingProviderView` uses them).
- Produces:
  - `public sealed class PrivacyOptions { public string? Contact { get; set; } }`
  - `public sealed record DeletionReceiptView(string UserId, string Username, string DeletedBy, DateTime DeletedAt, int Vaults, long Notes, int ApiKeys, int Connections, IReadOnlyList<string> ExternalProviders, string? PrivacyContact, bool Listed);`
    (`Listed` is `false` until Task 5 writes the list; `ExternalProviders` are the labels of every **external** provider configured on the instance, empty if none.)
  - `POST /api/admin/users/{id}/delete` answers `200 DeletionReceiptView` (was `204`); errors unchanged plus `403` for self.

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public async Task An_admin_deletes_an_active_user_and_gets_a_receipt()
// replaces An_admin_deletes_a_user_only_once_deactivated: no deactivation step;
// 200; json["username"] == username; json["vaults"] == 1; json["notes"] == 1; json["deletedBy"] == admin username;
// json["externalProviders"] is an empty array on the test server; json["privacyContact"] is null; mistyped/wrong password as before

[Fact] public async Task The_receipt_names_no_vault_or_note()
// user with vault "Geheimprojekt" and note "/Geheimprojekt/Plan X.md"; the receipt body and the captured log
// contain neither "Geheimprojekt" nor "Plan X"

[Fact] public async Task The_receipt_carries_the_privacy_contact_when_configured()
// factory with Kumbu:Privacy:Contact = "privacy@example.org" → json["privacyContact"] == "privacy@example.org"
```

- [ ] **Step 2: Run and see them fail:** `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~AccountApiTests"` → the three FAIL.
- [ ] **Step 3: Implement** the option (bound like the others, documented with a one-line summary), the view, and the endpoint (`Ok<DeletionReceiptView>`; `NotPermittedException` maps to `403` as elsewhere).
- [ ] **Step 4: Run and see them pass** (same command), then regenerate the web types (`pnpm -C web gen:api`) and run `pnpm -C web build`. Expected: PASS; build succeeds.
- [ ] **Step 5: Commit** `Answer an admin's deletion with a receipt`

### Task 3: The web dialog, the receipt, download and "Copy reply"

**Files:**
- Modify: `web/src/routes/admin/Users.tsx` (`UserItem` buttons, `DeleteDialog`)
- Create: `web/src/routes/admin/DeletionReceipt.tsx` (`DeletionReceipt` component, `receiptText(r)`, `replyText(r)`)
- Test: `web/src/routes/admin/Users.test.tsx`, `web/src/routes/admin/DeletionReceipt.test.tsx`

**Interfaces:**
- Consumes: `DeletionReceiptView` as `components["schemas"]["DeletionReceiptView"]`.
- Produces: `export function receiptText(r: Receipt): string`, `export function replyText(r: Receipt): string`, `export default function DeletionReceipt({ receipt, onClose }: { receipt: Receipt; onClose(): void })`.

- [ ] **Step 1: Write the failing tests:**

```ts
test("an active user offers 'Delete for a privacy request…', the admin's own row doesn't", …);
test("the dialog says the reminder text and needs the password and the username", …); // exact dialog text from Global Constraints
test("after deleting, the receipt shows counts and both buttons", …);
test("receiptText lists time, username, id, admin, counts, providers and the backup sentence, and no vault name", () => {
  const t = receiptText({ userId: "u1", username: "anna", deletedBy: "chris", deletedAt: "2026-10-27T10:15:00Z", vaults: 2, notes: 41,
    apiKeys: 1, connections: 1, externalProviders: ["OpenAI"], privacyContact: null, listed: true });
  expect(t).toContain("2026-10-27 10:15 UTC"); expect(t).toContain("anna"); expect(t).toContain("u1");
  expect(t).toContain("OpenAI: may have received text from vaults that used it; no copy was deleted there");
  expect(t).toContain("Backups are not reached by this deletion.");
});
test("receiptText says 'none configured' without external providers", …);
test("replyText is the template with username, date and the contact placeholder", …); // exact template from Global Constraints
test("Copy reply writes replyText to the clipboard; Download receipt saves kumbu-deletion-<username>-2026-10-27.txt", …);
```

- [ ] **Step 2: Run and see them fail:** `pnpm -C web test -- admin` → FAIL.
- [ ] **Step 3: Implement.** One button `Delete for a privacy request…` for every user who is neither the primary admin nor the viewer (replaces the deactivated-only `Delete`); the dialog uses `ConfirmTyped` with the exact title and text; on success it shows `DeletionReceipt` in place of the dialog (the users list is invalidated). Download as a `text/plain` Blob; clipboard through `navigator.clipboard.writeText`. If `listed` is false, the receipt adds the line `Not recorded in the deletion list (it isn't configured or couldn't be written).`
- [ ] **Step 4: Run and see them pass:** `pnpm -C web test` and `pnpm -C web build`. Expected: PASS.
- [ ] **Step 5: Commit** `Show a deletion receipt and a reply to copy`

### Task 4: Documentation of the deletion and the receipt

**Files:**
- Modify: `README.md` (admin section, configuration list: `Kumbu:Privacy:Contact`/`KUMBU_PRIVACY_CONTACT`), `.env.example`, `docs/security-architecture.md` (deletion section)

- [ ] **Step 1:** README: how an admin handles a privacy request (identity, one month, the button, the receipt kept as a record, the reply), that **deletions don't reach backups**, and the new variable with format and example (`privacy@example.org`). `.env.example`: `KUMBU_PRIVACY_CONTACT=privacy@example.org` commented as optional.
- [ ] **Step 2:** `security-architecture.md`: active users can be deleted in one step; what the receipt holds and doesn't.
- [ ] **Step 3: Verify** no doc shows a real organisation's address.
- [ ] **Step 4: Commit** `Document deletion for privacy requests`

*(Tasks 5 and 6 form one unit and are dropped together if time runs short.)*

### Task 5: The list of deleted user ids, outside the database

**Files:**
- Create: `src/Kumbu.Server/Accounts/DeletionList.cs`
- Modify: `src/Kumbu.Server/KumbuOptions.cs` (`DeletionsOptions Deletions { Path }`), `src/Kumbu.Server/Program.cs` (singleton), `src/Kumbu.Server/Accounts/Deletion.cs` (append after commit, both admin and own deletion), `src/Kumbu.Server/Api/AccountApi.cs` (`Listed`), `compose.yaml` (`Kumbu__Deletions__Path: /deletions`, volume `deletions:/deletions`), `README.md` (variable, volume, "keep this volume out of database restores")
- Test: `tests/Kumbu.Server.Tests/Accounts/DeletionListTests.cs`, `DeletionTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class DeletionList(IOptions<KumbuOptions> options, ILogger<DeletionList> log)`
  - `public bool Configured { get; }`
  - `public Task<bool> AddAsync(string userId, CancellationToken ct)` — false (and an error logged, no exception) when not configured or not writable.
  - `public Task<IReadOnlySet<string>> ReadAsync(CancellationToken ct)` — empty when not configured or the file is missing.
  - `DeletedUser` gains `bool Listed`.

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public async Task Added_ids_read_back()                       // temp dir; AddAsync("a"), AddAsync("b") → {"a","b"}; file mode 0600 on Unix
[Fact] public async Task An_unwritable_list_never_blocks_a_deletion() // Path = a file, not a dir → DeleteUserAsync succeeds, Listed == false, error logged
[Fact] public async Task Without_a_path_nothing_is_written()          // Configured == false; AddAsync → false; no exception
[Fact] public async Task Both_deletions_list_the_id()                 // admin deletion and own deletion each append the user id
```

- [ ] **Step 2: Run and see them fail:** `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~Deletion"` → FAIL.
- [ ] **Step 3: Implement** `DeletionList` (append with `FileMode.Append`, `UnixCreateMode` 0600, directory 0700, `Flush(true)`; one `SemaphoreSlim` so concurrent deletions don't interleave lines). `Deletion` takes it in its constructor and calls `AddAsync` after the transaction returns.
- [ ] **Step 4: Run and see them pass** (same command, plus `AccountApiTests`: `listed` is true when the factory sets a temp path).
- [ ] **Step 5: Commit** `Remember deleted user ids outside the database`

### Task 6: Warn after a backup restore and repeat the deletion

**Files:**
- Create: `src/Kumbu.Server/Accounts/RestoredDeletions.cs` (check + startup hosted service)
- Modify: `src/Kumbu.Server/Api/AccountApi.cs` (two admin endpoints), `src/Kumbu.Server/Accounts/Deletion.cs` (`RepeatAsync`), `src/Kumbu.Server/Program.cs`, `web/src/routes/admin/Users.tsx` (banner + dialog), `README.md` (what to do after a restore)
- Test: `tests/Kumbu.Server.Tests/Accounts/DeletionListTests.cs`, `tests/Kumbu.Server.Tests/Api/AccountApiTests.cs`, `web/src/routes/admin/Users.test.tsx`

**Interfaces:**
- Consumes: `DeletionList.ReadAsync` (Task 5); `Deletion.DeleteAccountAsync` (private, reused).
- Produces:
  - `public sealed record RestoredUserView(string Id, string Username, string Display);`
  - `GET /api/admin/deletions` → `200 { configured: bool, restored: RestoredUserView[] }` (users whose id is on the list and exists in `users`).
  - `POST /api/admin/deletions/repeat` body `{ password }` → `200 DeletionReceiptView[]` (one per user deleted again; the primary admin is never on the list).
  - `public Task<IReadOnlyList<DeletedUser>> RepeatAsync(User actor, byte[] actorKey, string password, CancellationToken ct)`
  - At start, a hosted service logs a warning `"{Count} deleted users are back after a restore; an admin can delete them again on the users page"` (ids only).

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public async Task A_messy_list_reads_as_its_ids()               // "a\r\n\n  b \njunk with spaces\na\n" → {"a","b"}; lines with whitespace inside are ignored
[Fact] public async Task A_restored_user_is_reported_and_deleted_again() // delete user; re-insert their user document (as a restore would);
                                                                        // GET /api/admin/deletions lists them; repeat with the password → 200, one receipt; GET lists none
[Fact] public async Task Repeat_needs_the_admins_password()            // wrong password → 400 "Your password is wrong."
```

```ts
test("the users page shows the restore warning and 'Delete again…' when restored users exist", …); // exact warning text
```

- [ ] **Step 2: Run and see them fail:** `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~Deletion|FullyQualifiedName~AccountApiTests"` and `pnpm -C web test -- admin` → FAIL.
- [ ] **Step 3: Implement.** Ids on the list are valid user ids only (the same shape `UserCatalog` creates); anything else is skipped. The repeat runs each user's `DeleteAccountAsync` on its own (a failure of one doesn't stop the others) and appends nothing new (the ids are listed already). The banner sits at the top of the users page; its dialog asks only for the admin's password (`ConfirmTyped` without a typed word).
- [ ] **Step 4: Run and see them pass** (same commands; then `pnpm -C web gen:api` and `pnpm -C web build`).
- [ ] **Step 5: Commit** `Warn about users a restore brought back and delete them again`

### Task 7: Pull request

- [ ] **Step 1:** Run everything: `dotnet test tests/Kumbu.Server.Tests`, `dotnet test tests/Kumbu.Core.Tests`, `pnpm -C web test`, `pnpm -C web build`. Expected: all PASS.
- [ ] **Step 2:** Manual check on the compose stack (`docker compose -f compose.yaml -f compose.build.yaml up --build`): an admin deletes an active test user; the receipt downloads; "Copy reply" pastes the template; `docker compose exec kumbu cat /deletions/deleted-users` shows the id (if Tasks 5–6 are in).
- [ ] **Step 3:** Open the PR from `gdpr-deletion` to `master`, titled `Delete a user for a privacy request in one step`, the body listing what is in (and, if dropped, that Tasks 5–6 went to a later run), ending with the attribution lines. Chris merges.

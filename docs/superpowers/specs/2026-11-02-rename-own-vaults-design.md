# Renaming one's own vaults, and three small things from the collab test

Date: 2026-11-02. Status: draft for review.

Members can already rename a vault shared with them (their alias, `VaultCatalog.RenameAliasAsync`). Owners can't rename their own. This adds that, and three leftovers from parts F and G of `docs/test-playbook-collab.md`.

Renaming is meant to be rare: not something done three times a day. That is why asking people to pair Obsidian again is acceptable.

## 1. Rename by a display name

### 1.1 Why not a real rename

The stored path of every note and attachment starts with the vault's name, and each document's `PathId` is a MAC over that path (`Vaults/UnlockedVault.cs:143`). A real rename would rewrite every document. Instead the vault keeps its **stored name** for good and gets a **display name** that every caller sees. `VaultAccess` already translates between the two for members' aliases (`Renamed`, `ToVault`, `Vaults/VaultAccess.cs:41-47`).

### 1.2 Data

- `VaultMeta` (`Vaults/UnlockedVault.cs:587`) gains an optional `DisplayName`, left out of the JSON when null, plus a `Shown` property: `DisplayName ?? Name`. Vaults of today have none, so no migration is needed.
- `Name` in the meta never changes after creation.
- Renaming back to the stored name sets `DisplayName` to null.
- `UnlockedVault` gets `DisplayName` and `Shown`. `SealMeta` (`UnlockedVault.cs:168`) must keep `DisplayName`, or a re-encryption or a change of embedding provider would lose the name.

The display name is sealed under the vault key, as `Name` is. It is not a new kind of secret: whoever holds the vault key could read the old name too.

### 1.3 Who sees what

| Caller | Sees the vault as |
|---|---|
| Owner's session | `Shown` |
| Owner's API keys, connections, devices | `Shown`: they read the meta, so one change reaches them all. This is why the name lives in the meta and not in the owner's slot: the server can't rewrite slots sealed under a key's secret |
| Member | Their own alias, unchanged |
| Someone opening an invitation | `Shown`, as the proposed alias |

These read `meta.Name` today and must use `Shown`, each with a test:

- `Api/ConnectionApi.cs:180`
- `Vaults/VaultCatalog.cs:377` (finding a vault by name)
- `Vaults/VaultCatalog.cs:1525` (the name index during a key rotation)
- `Vaults/VaultCatalog.Members.cs:80`
- `OAuth/OAuthTokens.cs:162`, `:205`
- `Sharing/VaultInvites.cs:140`, `:142`
- `Accounts/ApiKeys.cs:416`
- `Accounts/Connections.cs:404`

`VaultAccess.Owned` (`VaultAccess.cs:50`) and the owner's branch of `CredentialAccess` (`VaultCatalog.Members.cs:390`) use `vault.Shown` as the access's `Name`. That switches on the path translation for a renamed vault's owner.

Places that keep the stored name: `ToVault`, and everything that computes a `PathId`.

### 1.4 The operation

`PUT /api/vaults/{vault}/name`, body `{ "name": "…" }`. Session only, owner only (a member gets 404, as on other owner routes). No password: it is undone by renaming again, and removes no access. Rate limited like an alias rename (`AccountLimits.AliasRename`). The name goes through `VaultNames.Check`.

In one transaction, refused while the vault is re-keyed (`RequireNoneRekeyingAsync`):

1. Update the owner's user slot's `NameId`. The unique index on `(lookup, nameId)` refuses a name the owner already uses for a vault or an alias: 409 with `VaultNameTakenException`'s sentence.
2. Update `Meta`, conditional on the bytes read, so a concurrent provider change conflicts instead of being overwritten.
3. Record the event `vault-renamed` for the owner's activity: "You renamed this vault." Events store no names, and members aren't told, since their alias doesn't change.

After the commit: set `DisplayName` on the cached `UnlockedVault`, and call `SyncHub.VaultsChangedAsync` so that the owner's open tabs update.

The old name is free at once. A new vault may take it, with a stored name equal to the renamed vault's: the two differ by id. Changing only the case ("beet" to "Beet") must work, although `NameId` stays the same.

The new name must not appear in logs.

### 1.5 What breaks, and how the dialog says so

Paths under the old name stop working for agents, on their next call they find the vault by `ls /`. Notes that spell out an absolute path to their own vault aren't rewritten, as with a member's alias.

**Obsidian devices lose the vault:** the plugin keeps the name in its settings and asks for it by name, gets 404, and stops with "This key can't open the vault … any more". The device pairs again. The plugin is not changed for this.

The dialog says both, before the name is changed:

> Rename *Gartenarbeit*. Members keep their own name for it. Agents find it under the new name; paths with the old one stop working. Connected Obsidian devices need to pair again.

The sentence about devices shows only if the vault has device keys, from `GET /api/vaults/{vault}/access` (the same list as the re-encrypt dialog).

### 1.6 Web app

"Rename vault…" in the vault menu (`web/src/shell/TreeDrawer.tsx`, in the owner's entries), a dialog with the name and the sentence above. On success, the app goes on showing the same note under the new name: routes carry the vault's name, so it navigates there and invalidates `["vaults"]`. Add `/api/vaults/{vault}/name` to `web/src/api/schema.d.ts` by hand as generated output would have it.

### 1.7 Worked example

Alice owns `Gartenarbeit` with `plan.md`, a board and a checkpoint `gfd`. Bob is a member, with the alias `Beet`. Alice's key `Claude` and an Obsidian device cover it.

1. Alice picks "Rename vault…", enters `Garten`. The dialog names the device. She confirms.
2. Slot `NameId` and meta change in one transaction. Stored notes don't change: `/Gartenarbeit/plan.md` stays stored as it is, with its `PathId`.
3. Alice's tab shows `Garten`, with `plan.md` open. Activity: "You renamed this vault."
4. `Claude` calls `sh ls /`: `Garten/`. `cat /Garten/plan.md` works; `lscp /Garten` lists `gfd`; `board /Garten` shows the board. `cat /Gartenarbeit/plan.md` answers as a vault it doesn't know.
5. Bob sees `Beet`, as before, and his history and agents don't notice.
6. The Obsidian device stops with the sentence above. Alice pairs it again from "Connect Obsidian…".
7. Alice makes a new vault `Gartenarbeit`. It works, and is a different vault.
8. Alice re-encrypts `Garten`. It stays `Garten`.
9. Alice renames it back to `Gartenarbeit`: refused, the name is taken. She deletes the new one and renames again: `DisplayName` is null.

### 1.8 Tests that matter

With a renamed vault, as its owner, through both a session and a key: read, write, `mv`, `rm`, restore from the trash, `rg`, `semsearch`, history, activity, checkpoints (make, list, restore), attachments, board, export. The translation is proven for members, not for owners, and a path that checks `IsOwner` first would be the failure.

Also: a key rotation keeps the index right; re-encryption and a provider change keep the name; the name reaches a key and a connection without touching their slots; a member gets 404 on the route; a member's alias is untouched; case-only rename; a taken name; renaming back; a concurrent provider change.

### 1.9 Security

This changes what the meta holds and updates a slot's `NameId`, so `docs/security-architecture.md` gets a paragraph, and the security review applies. It adds no key and no route to a key. An update of `Meta` by someone with database write access is detected as today, since the meta is sealed with `vault:{id}`.

## 2. "Leave this vault…" in the vault menu

The entry goes last in the vault menu for members (`web/src/shell/TreeDrawer.tsx`, beside the owner's "Delete vault…", with the same reasoning: away from the everyday entries). It opens the existing `LeaveVaultDialog` (`web/src/shell/MemberPanel.tsx`). The entry in "Shared with you…" stays.

## 3. A banner for members after a re-encryption

Today a member learns of it only from Settings, "Shared vaults". Those whose keys or apps lost the vault need to act, and get a notice in the existing `MemberNotices` list (`web/src/shell/SharedBanner.tsx:79`), with Dismiss:

> "Alice" re-encrypted *Beet*; your keys and apps for it were revoked. Give your agents *Beet* again.

- A new `MemberNoticeDocument` kind, written in the finish's transaction beside `RekeyFinished` (`Vaults/VaultCatalog.Rekey.cs:~580`).
- Only for memberships whose credentials actually ended, kept from `EndMembersCredentialsAsync` on the vault document, as `RekeyEnded` is. A member without keys or apps on the vault has nothing to do and gets no notice; the Settings entry stays for everyone.
- The Settings entry's sentence is unchanged for now, though it says the same to a member without keys. Mention it in the report.
- The alias comes as in `LeaveForDeletedAsync`: a copy of the slot's alias ciphertext and alias key, so a notice opens with the member's user key. A key rotation reseals it through `PrepareResealAsync`, already there for that kind of document.

## 4. A changeset when restoring from the trash

`history`'s help names a changeset for every write, but a restore sets it to null: `Vaults/EncryptedNoteStore.cs:779` and `Kumbu.Core/Attachments/AttachmentWriter.cs:244`. `NoteWriter.ChangesetAsync` gives it, as for every other write. Restoring joins the writer's open changeset.

## Out of scope

- Plugin 0.2.0: its own run in its own repository. "Connect Obsidian…" for members follows that run, as a small change here.
- Pins a former member can't forge and `_loading`: 0.3.1 (`docs/superpowers/open-items.md`), design and independent review first.
- A one-time notice to the owner's agents about a rename.
- Any version change or release.

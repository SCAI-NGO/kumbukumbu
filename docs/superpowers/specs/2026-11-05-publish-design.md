# Publish v1: one note behind a link

Date: 2026-11-05. Status: approved by the maintainer on 2026-11-05. Target: 0.3.2.

## 1. Why

Kumbu² is now in daily use at work. Twice on the first day its owner wanted to send a colleague a note, a list of questions and a list of example prompts, and had to copy and paste the text into a chat service instead. The colleague reads the list and answers in the chat; nothing more is needed.

The notes linked to other notes, among them a master note for the sub-project with half-formed thoughts in it. Those must not go along. Sharing the vault is the wrong tool: it gives far more than one page, and to a person who has no account.

**The guiding principle is the *forum internum*:** the right to keep unfinished thoughts to oneself is part of the freedom of thought. Every choice below follows from it. What leaves the vault is a fair copy the owner chose, as it was when they chose it, and nothing else about them.

## 2. Goals, non-goals, anti-goals

**Goals**

- The owner of a vault publishes **one note** as a **snapshot** behind a **secret link**. Anyone with the link reads it, with no account.
- The owner sees when the published copy is older than the note and updates it with one click; the link stays the same.
- The owner withdraws it; the link is dead at once.
- Agents can do the same through `sh publish`, only with a right the owner gives a credential deliberately.
- Notes and folders can be marked **unpublishable**: a hard block, for people and agents alike.

**Non-goals for v1** (v2, with 0.4.0)

- Several notes under one code, and choosing linked notes to take along.
- Open pages with a readable slug, OpenGraph tags, custom domains.
- Expiry dates, short codes for reading aloud, greylisting.
- Live pages that follow every edit.
- Publishing from a vault shared with the caller (members never publish).

**Anti-goals**: it fails, even if it works, if

- anything the owner didn't choose leaves the vault: another note, its title through a path, a tag, a comment, an attachment's metadata, the vault's name, the owner's name;
- a half-finished edit reaches the reader without the owner saying so;
- an agent can publish because of a right the owner gave it for something else;
- a chat service's link preview shows any of the content;
- the reader page is a way into the web app (XSS on the same origin).

## 3. Decisions taken

These were decided in the conversation of 2026-11-05 and are not open.

### 3.1 What a publication is

- One note. Its snapshot is a **copy**: the note's Markdown, filtered (§3.3), with the attachments the note embeds (§3.4).
- **Protected only.** Every publication has a share code; there are no open pages in v1. Content stays encrypted at rest, so "everything in the database is encrypted" stays true for content (§5).
- **Snapshot, not live.** Editing the note does not change the copy. **Update** writes a new copy under the same code.
- One code per publication. Making a new link is withdraw, then publish again.

### 3.2 What the reader sees

- The note's title, its filtered text and its attachments, rendered read-only.
- **"Snapshot of 5 Nov 2026, 14:32"** under the title: the time the copy was made. The reader is **never told whether the note has changed since** (that would disclose that the owner is still thinking about it). No date at all was rejected as obscurantism.
- Nothing else: no owner name or username, no vault name, no path, no other notes, no editing, no comments, no history.

### 3.3 Filtering the copy

Removed before the copy is stored:

- frontmatter;
- `#tags`: a tag standing alone is removed; a tag in running text keeps its word without the `#` (the builder decides the exact rule; the sentence must stay readable);
- Obsidian comments `%%…%%` and HTML comments `<!-- … -->`;
- links to other notes, in every form (`[[Note]]`, `[[Note|text]]`, `[[Note#Heading]]`, `[text](/path.md)`): they become **plain text**, the link's visible text. A bare wikilink's text is the note's title, and **that is accepted**: the owner is responsible for what they call their notes. The path and target never appear;
- embedded notes (`![[Other note]]`): not resolved, treated as a link (plain text);
- raw HTML: not rendered (as in the web app today);
- remote images (`![](https://…)`): become links, so the reader's browser fetches nothing from third parties.

Web links stay links.

### 3.4 Attachments

- Images and PDFs the note embeds are copied with it, encrypted under the publication's key (§5). Nothing else of the vault is reachable from the page.
- **Metadata is stripped**: EXIF, XMP and IPTC from images (GPS position, device, names); the document information dictionary and XMP from PDFs. Today EXIF is only read for orientation (`Attachments/ImageHeader.cs:202-219`); the orientation must be applied or kept so images don't turn.
- If PDF metadata can't be stripped reliably in this release, PDFs are not published in v1: they become plain text like a link, and the publish dialog says so. That is the builder's call, stated in the result.
- An attachment that is unpublishable (§3.6) is not copied: it becomes plain text.
- The vault's record lists each publication's attachment paths (rewritten on `mv`) and their content hashes. A copy counts as **older than this note** when the note's version or any of these attachments has changed. *(Claude's call.)*

### 3.5 Who publishes

- **Only the vault's owner.** Members of a shared vault never publish from it, whatever their role. This is the owner's prerogative and keeps others' notes from being exposed too easily.
- **In the web app**: the owner's session, confirmed with the password the way an invitation is (`web/src/shell/ShareDialog.tsx`), or within the existing password window if one is open.
- **Agents**: an API key or OAuth connection of the owner, with the new per-vault right **"May publish"** (§3.8). Obsidian devices never get it. Not `maintain`: every Obsidian device has `maintain` (`Accounts/Pairings.cs:155-158`), and so does every `write` sealed before 0.3.0 (`Accounts/ApiKeys.cs:46`); tying publishing to it would let all of them publish without the owner ever deciding.
- That the link passes through the model's context when an agent publishes is accepted: anyone who connects an AI to their notes already lets it read them.

### 3.6 Unpublishable

- A mark on a note or a folder. A folder's mark covers everything below it, notes and attachments.
- It is a **hard block**: nobody publishes or updates what it covers, the owner in the web app included. They lift the mark first.
- It lives in the **vault's sealed data**, not in the note (no frontmatter flag): an agent with write access could otherwise remove it and publish. Only the owner's session sets or lifts it, in the web app. Agents can't. It isn't visible in Obsidian; accepted.
- It follows `mv` of the note or folder.
- Marking something already published, or moving a published note into a marked folder, **withdraws** the publication at once. The same holds when an attachment a live copy holds is marked, or moved into a marked folder: the whole publication is withdrawn, not re-sealed without the attachment, as a snapshot changes only when the owner says so. *(Claude's call.)*

### 3.7 Withdrawing

A publication is withdrawn, its copy, attachments and slots deleted, when:

- the owner or an agent with the right withdraws it;
- the note is removed (`rm`, also to the trash, and a folder above it); `restore` does **not** bring the publication back. The note no longer exists, so neither does its publication;
- it becomes unpublishable (§3.6);
- the vault is deleted;
- the owner's account is deleted: its publications are withdrawn **at once**, also those of a shared vault that is still in its closing period (`Accounts/Deletion.cs:340-356`). *(Claude's call: the owner leaving is the owner deciding.)*

When an admin **deactivates** the owner (`Accounts/Admin.cs:62-100`), publications are kept but unreadable, as with the server switch (§3.13), and readable again on reactivation. The reader endpoint checks the owner's state at read time. *(Claude's call: deactivation is reversible.)*

`mv` of the note or a folder above it keeps the publication and its link.

**Who may remove a published note.** Whoever may not publish may not withdraw either, also not by removing the note:

- Removing a published note, or a folder with one below it, takes the owner's session, the owner's credential with "May publish", or **the owner's Obsidian device**. A device is the owner themself in Obsidian (`Accounts/Pairings.cs:155`), and Obsidian turns some renames into delete plus create (`kumbu-obsidian/src/engine/engine.ts:241-249`); refused, the plugin would retry for ever. A device still never publishes.
- Members and the owner's other credentials are refused, with a reason naming the publication, in `Refusals.cs`.
- The same holds for moving a published note into an unpublishable folder, which withdraws it (§3.6).
- **Editing stays free** for everyone with write access: the snapshot doesn't change until someone with the right updates it, so an edit reaches no reader. Locking edits would protect nothing and would lock out Obsidian and agents with plain write access.

### 3.8 The "May publish" right

- A per-vault boolean beside the existing right, on API keys and OAuth connections. Off by default.
- In `RightsPicker` (`web/src/routes/NewKeyDialog.tsx:15-20,45-95`, reused in `routes/Connections.tsx:200` and `routes/NewConnectionDialog.tsx:80`) it is a **checkbox** beside the radio group, shown **only for vaults the user owns**. The consent page (`routes/Consent.tsx:135`) shows it when it is set.
- Setting it, as raising a right does, needs the password.
- On the server it is sealed with the right, in `ApiKeySlotPayload` and in `ApiKeyMeta`/`ConnectionMeta`, so a credential sealed before this release reads as "may not".
- It acts only while the credential's user owns the vault. Credentials never reach `AccessLevel.Owner` (`Notes/AccessLevel.cs:7`); the check is "the user owns the vault (`VaultAccess.IsOwner`) and the credential may publish there".

### 3.9 `sh publish`

The shell's first command with subcommands, branching on its first argument.

| Command | Who | Does |
|---|---|---|
| `publish ls [path]` | anyone who can read the vault | lists publications below the path. The owner's credentials: note path, snapshot time, whether it is older than the note. Members: the note path only (§3.10). **Never the code or link.** |
| `publish add <note>` | owner's credential with "May publish" | publishes, prints the link once |
| `publish update <note>` | the same | new snapshot, same link |
| `publish rm <note>` | the same | withdraws |

- Refusals name why: not the owner, no "May publish", unpublishable, publishing switched off on the server. They go into `Refusals.cs` like the others.
- The web console (`Api/ConsoleApi.cs:39-46`) allows `publish ls` and refuses the rest. `KnownCommands` (`Metrics/KumbuMetrics.cs:522`), `Help.cs` and the tool description (`Sh/Manual.cs:6`) gain it; the tests that compare them with the shell must pass.
- `sh` has no way to mark something unpublishable; `publish ls` doesn't show marks.

### 3.10 What members of the owner's shared vault see

The middle way: members see **that** a note is published (in `publish ls` and the note's bar), **not** when it was published or updated, how often, or by which agent or key. Those details are in the owner's history only.

- A member's bar is neutral: "Published by the vault's owner", no green or yellow, no times, no buttons. Whether the copy is older than the note is the owner's to know.
- This is enforced by the server, not by the cryptography: members' slots hold the vault key too (`ApiKeySlotPayload`, `VaultCatalog.cs:1463`), but only the server uses it, and it answers members with the path alone. That is how writer names are kept from members today (`Api/WriterLabels.cs`). *(Claude's call; a separate owner-only key would change the slot model for little gain.)*

### 3.11 The owner's view in the web app

As in the mockups of 2026-11-05 (`.superpowers/brainstorm/`, not committed):

- The note's menu gains **Publish…**: a dialog that says what happens ("Anyone with the link can read a copy of this note as it is now. Links to other notes become plain text."), asks for the password, then shows the link with **Copy link**.
- A published note shows a bar above it:
  - green: "Published, up to date", with **Unpublish**;
  - yellow: "Published copy is older than this note. Published 5 Nov, 14:32 · changed 16:05", with **Update copy**.
- The link can be copied again from the bar, so the code is kept sealed in the vault (§5).
- An overview of all publications of the owner's vaults (where it lives is the builder's call): note, snapshot time, older or not, published by whom (the owner, or which key or connection).
- Setting and lifting **Unpublishable** on notes and folders, in their menus, with a visible mark in the tree.
- Each publish, update and withdrawal is a vault event in the history (`Sharing/VaultEvents.cs`), owner only (§3.10), and naming the agent where one did it. History is an audit log: nothing hidden, duplicates included.

### 3.12 The reader page

- At `<base>/s#<code>`. The code is **in the fragment**, never in the path or a query string, so it reaches no server log, no referrer and no link preview. A chat service that fetches the link for a preview gets a page that says nothing about the content.
- A small script reads the fragment, posts the code to an anonymous endpoint, removes the fragment from the address bar (`history.replaceState`) and renders the answer.
- Without a fragment, or with a wrong code, the page asks for the code. A wrong code gets the same answer as a withdrawn one.
- After the code is accepted the server sets a **reader cookie** of its own, never `kumbu_session`: HttpOnly, Secure, SameSite=Strict, its path limited to the reader endpoint, so a reload works without the fragment. It grants that one publication.
- **The page must not be a way into the web app.** It runs on the web app's origin, where the owner's session cookie lives (`kumbu_session`, path `/`). So: its own minimal entry that doesn't load the authenticated app, a strict CSP (no inline script, no third-party sources, `img-src 'self' blob: data:`, `frame-ancestors 'none'`), rendering the Markdown without raw HTML and with dangerous URL schemes dropped, as `MarkdownView.tsx` does. Whether it reuses `MarkdownView` or the server renders sanitised HTML is the builder's call; Markdown is stored, never HTML (§5).
- `X-Robots-Tag: noindex, nofollow` and a `noindex` meta tag; `Referrer-Policy: no-referrer`; `Cache-Control: no-store`.
- The reader endpoint is rate-limited per network and server-wide, as `/api/pairing/redeem` is (`Security.cs:360-374`), against load; 160-bit codes make guessing hopeless anyway.
- **No reading statistics**: no count, no last-read time, no log of who read. Nothing about readers is stored.

### 3.13 Switching it off

`Kumbu:Publish:Enabled` in `KumbuOptions` (environment `KUMBU_PUBLISH_ENABLED`), default `true`. Off, publishing and the reader endpoint both refuse, existing publications are kept but unreadable, and the web app hides the publish UI. Documented in the README and `.env.example`.

## 4. Data and keys

The design in `/SWE/Kumbu/Ideas` (2026-11-04), narrowed to protected pages:

- **Share code**: generated by the server, never chosen. 160 bits as 32 Crockford Base32 characters in groups of four (`K7QM-4XDA-…`). Normalised as pairing codes are (`Accounts/Pairings.cs:192-214`: case, hyphens and spaces don't matter, I/L read as 1, O as 0).
- **Derivation**: the normalised code goes through HKDF twice, with different labels: a **lookup id**, which is all the database stores to find the publication, and a **reader key**. No per-publication salt (the publication is found only through the code).
- **Publication key**: random per publication, in two slots: one sealed under the reader key (for readers), one under a key derived from the vault key (so that **Update** works without the code). A mini vault in the existing slot model (`Crypto/VaultCrypto.cs`).
- **The copy** (filtered Markdown, title, snapshot time, attachments) is sealed under the publication key. It does not name its user in the clear. It **does** carry the vault id in the clear, as every other vault document does: vault deletion (`ContentCollections`), account deletion, the closing period and the admin's storage figures (`Accounts/Admin.cs:181`) run without the vault unlocked and find documents by it, and the reader endpoint needs it to check the owner's state. A dump thus shows that a vault has publications, not what they hold. *(Claude's call; a deviation from the idea of 2026-11-04, which wanted no vault in the clear.)*
- **The vault's record** of its publications, sealed under the vault key, the way checkpoints are (`UnlockedVault.cs` ~300-325, collection `VaultCatalog.cs:89-93`): note path, lookup id, the code (for **Copy link** again), snapshot time, the note version it was made from (for "older than this note"), who published it. The unpublishable marks live there too, or in `VaultMeta`; the builder's call.
- Notes have no stable id; everything is path-based (`UnlockedVault.cs:151`). The record's paths, and the marks', are rewritten on `mv` of a note or folder (`Notes/NoteMover.cs:155-221`, `EncryptedNoteStore.cs:253-275`).
- A new collection must be in `ContentCollections` (`VaultCatalog.cs:682`), so vault deletion removes it, and in `RekeyCollection`/`RekeyOrder` (`VaultCatalog.Rekey.cs:713-724`), so a re-key doesn't break the vault-side slot. The copy's own attachments are sealed under the publication key, not as vault blobs, so `BlobSweep` doesn't touch them; if the builder shares blobs instead, they must be pinned there (`BlobSweep.cs:57,147`).
- Writing a copy needs the vault unlocked, which every publishing caller has. Reading needs only the code.

## 5. Security and documentation

What this changes, to be written into `docs/security-architecture.md` (lines as of today) and `docs/concept.md`:

- `:18` "Everything in the database is encrypted": still true for content; a publication is a mini vault whose key is reachable from the share code. The lookup id, the vault id, creation and size of a copy are in the clear.
- §2's threat table (`:32-48`): a new row, "someone holding the link". They read that one copy, nothing else. Someone with a database dump and no code reads nothing. Someone with a dump **and** the code reads the copy, also from a backup made before it was withdrawn: withdrawing deletes it from the database, not from backups.
- §4 actors and levels (`:215-254`): a reader, with no account; the owner's prerogative; "May publish".
- §5.6 "What is in the clear" (`:333-358`): the publication documents.
- §6 deleting (`:551-565`): publications go with the note, the vault and the account.
- §7 server measures (`:606+`): the anonymous reader endpoint, its rate limit, its CSP and cookie.
- Prompt injection (`:57`): publishing doesn't create the risk of an agent being talked into copying out, it widens where to (no account needed). "May publish" off by default and unpublishable marks are the mitigations; neither is a guarantee.
- `docs/concept.md`: `:15`, §3 threat model, §7 cryptography, `:191`.
- README: the feature, `sh publish`, `KUMBU_PUBLISH_ENABLED`.

## 6. End to end

Alice owns `Work`. `Work/Routing/Questions.md`:

```markdown
---
status: draft
---
# Questions on routing #routing

1. Should the chatbot move the user between screens? See [[Routing master]].
2. Is there a list of routes? [Router docs](https://example.org/router)
%% ask about deep links later %%
![[flow.png]]
```

`Work/Private/` is marked unpublishable; `flow.png` lies in `Work/Routing/` and has GPS data in its EXIF.

1. Alice opens the note's menu, **Publish…**, gives her password. The dialog shows `https://kumbu.example.org/s#K7QM-4XDA-…` (32 characters); she copies it into the chat.
2. The colleague opens it. The server never sees the code in a request path or log; the page posts it, removes the fragment and shows:
   - "Questions on routing", "Snapshot of 5 Nov 2026, 14:32";
   - "1. Should the chatbot move the user between screens? See Routing master." (plain text, no link);
   - "2. Is there a list of routes? Router docs", a working web link;
   - the image without EXIF; no frontmatter, no `#routing`, no comment, no name of Alice or `Work`.
3. The chat service's preview of the link shows a generic page.
4. Alice adds a line. Her bar turns yellow, "Published copy is older than this note". The colleague, reloading, still sees the snapshot of 14:32, with no hint that anything changed. Alice clicks **Update copy**: the same link shows the new text and the new time.
5. Alice's agent, a key with `write` but without "May publish", runs `sh publish rm /Work/Routing/Questions.md`: refused, naming the missing right. It runs `rm /Work/Routing/Questions.md`: refused, naming the publication. `publish ls /Work` lists the note with its snapshot time and "older: no", without the link.
6. Bob, a member of `Work` with `write`, sees "Published by the vault's owner" above the note and `publish ls` gives him the path alone. He edits the note freely; his edit doesn't reach the copy.
7. Alice moves the note to `Work/Done/Questions.md`: the link still works. She moves it to `Work/Private/`: the publication is withdrawn, the link answers as for a wrong code, and her history shows the withdrawal and why.

## 7. Left to the builder

- The exact tag rule (§3.3), the reader page's technology (§3.12), where the marks live (§4), where the overview lives in the web app (§3.11).
- Whether PDFs make it into v1 (§3.4).
- Wording of dialogs, bars, refusals and help, in British English, matching the app.
- Tests: the filtering (every link form, comments, tags, frontmatter, remote images), metadata stripping, rights (owner, member, key with and without "May publish", device, pre-release credential), withdrawal on every path in §3.7, who may remove a published note (owner, device, key with and without the right, member), `mv` keeping the link and the marks, the reader endpoint (wrong code, withdrawn, rate limit, cookie scope, headers), the console's refusals.
- New hand-test steps go to `/SWE/Kumbu/Tests/Test list 0.3.2-pre.1.md` under "Added later".

# Fixes from the hand tests of 0.6.0-pre.5

Chris hand-tested `v0.6.0-pre.5` on staging on 2026-11-20, with Claude through the `kumbu-staging` connector and Playwright. The findings are in Kumbu² at `/SWE/Kumbu/Tests/Test list 0.6.0-pre.5.md`, section "Found". This spec covers the ones that are small, clear and fixable before the 0.6.0 release. Chris approved this list in the session on 2026-11-20 ("ja mach das gleich bitte … + overnighter"). Every item is a bug fix or a wording change in code that already exists. None of them is a new feature.

## 1. Goal and decisions

0.6.0 is planned for release around Sunday 2026-11-22. This round removes what the hand tests found in the code, so that the remaining hand tests on Saturday run on a clean build.

- **Fix only what is listed here.** Why: the release is two days away. A wider round would need its own hand tests.
- **The click-to-Write wait goes from 250 ms to 200 ms.** Decided by Chris, because 250 ms "feels too slow; 200 ms would be just that little bit snappier".
- **The base `compose.yaml` restarts the server (`restart: unless-stopped`).** Decided by Chris ("I think so, yes"). Why: the memory ceiling (`mem_limit`) ends the server when it reaches the limit. Without a restart policy, a plain `docker compose up -d` deployment stays down until someone notices. `compose.traefik.yaml` already sets the policy, and the production deployment's own compose file does too.

## 2. The items

### 2.1 F-A (Medium, bug): a deep link leaves the tree on the wrong vault

**Now:** a note's deep link `/n/<vault id>/HT/Alpha.md` opens the right note. The file tree and the vault picker stay on the vault that was shown before (for example Welcome), so the tree and the open note disagree. `/notes/Work/HT/Alpha.md` switches both correctly. `web/src/routes/DeepLink.tsx` resolves the link and then `<Navigate>`s to `noteUrl(path)`. Somewhere on that path, the shell's notion of the current vault isn't updated, possibly because `settleStart` or the last-vault state runs before the navigation, or isn't run at all.

**Then:** after a deep link the tree, the vault picker and the open note all show the link's vault, exactly as after opening `/notes/<vault name>/<path>`. This holds on the desktop and on the phone (D27 is the same path on the phone), signed in already or after sign-in (`routes/SignIn.tsx:55` sends `/n/` links on).

**Example:** Alice is signed in and looks at Welcome. She pastes `https://<host>/n/<Work's id>/HT/Alpha.md`. Alpha opens, the vault picker says Work, and the tree shows Work's folders with `HT/Alpha.md` marked.

**Tests:** a Vitest test of the shell or route, red first. Also an end-to-end test in `web/e2e/deeplinks/`, tagged `@deeplinks`, that starts on another vault and asserts the picker and the tree.

### 2.2 F-C (Low, bug): the reader keeps "This code doesn't open a published note"

**Now:** the reader (`web/src/reader/Reader.tsx:439`) shows "This code doesn't open a published note" after a wrong code. If a right link is then opened in the same tab, only the `#…` part of the address changes. Nothing reloads, and the message stays. A reload opens the note.

**Then:** when the hash changes, the reader drops its old state and opens the new code, as a fresh load would. A wrong code still shows the message, and a right one opens the note. The reader keeps removing the code from the history as it does today (`Reader.tsx:483`, `history.replaceState`); that stays. The hash also carries in-copy navigation (`#/<id>` through `pushState`, `#n<id>` anchors, `Reader.tsx:226-236`, `:335`): a new handler must not read those as codes.

**Example:** Bob types `…/p#WRONG`, sees the message, then pastes `…/p#<right code>` into the same tab and presses Enter. The published note appears without a reload.

**Tests:** a Vitest test that changes `location.hash` and asserts the note.

### 2.3 Click-to-Write wait: 250 ms → 200 ms

`web/src/note/NoteView.tsx:79`, `DOUBLE_CLICK_MS = 250` becomes `200`. Tests that name 250 ms for this wait move with it. Don't change `PREVIEW_PAUSE_MS`, the other 250 ms in the same file, or the windows spec's other timings.

The workspace spec states the wait as "a quarter of a second" (`docs/superpowers/specs/2026-11-18-workspace-design.md:93`). It gets a one-line amendment there: "200 ms, decided by Chris in the hand tests of 2026-11-20, because 250 ms felt slow". Line 109 uses the same phrase for the preview pause, which stays.

### 2.4 The PIN dialog names the wrong place

`web/src/discreet/PinDialog.tsx:87` says "The PIN is set in Settings → Discreet vaults". The setting sits under **Settings → Security → Discreet vaults**, and the link already goes to `/settings/security#discreet-vaults`. The text becomes "The PIN is set in Settings → Security → Discreet vaults.", the link text "Settings → Security → Discreet vaults". Update the tests that match the old text (`PinDialog.test.tsx`, `MobileShell.test.tsx:1253`, and any others `rg` finds).

### 2.5 `restart: unless-stopped` for the server in `compose.yaml`

Add `restart: unless-stopped` to the `kumbu` service in `compose.yaml`, next to its `mem_limit`. Change the comment above `mem_limit` (`compose.yaml:70`) to say that the server comes back after the kernel ends it. `README.md:566` says the restart policy comes only with `compose.traefik.yaml`: correct it. Note in the report that a server ending at its memory limit during the end-to-end suite now restarts quietly instead of failing loudly. Leave the other services as they are. The end-to-end stack is layered over `compose.yaml`, so check that `web/e2e/stack/up.sh` and `down.sh` still behave. A restart policy must not keep a test stack alive after `down.sh`.

### 2.6 Four small wordings

1. **Start's publication bar says that something published with it changed** (`web/src/note/PublicationBar.tsx`). On a bundle's start note whose copy is older because a **linked** note or file changed, the bar now says "Published copy is older than this note. Published 20 Nov, 10:15 · changed 10:15", though Start itself is unchanged. The publication answer (`PublicationState`, `src/Kumbu.Server/Publishing/Publications.cs:651`) carries only `Older`, the latest `ChangedAt` across start, members and files, and `Notes`; it doesn't say what changed, and an embedded file can be it. **Then:** when Start's own last write is not after the snapshot (compare its `lastWriteAt` with `snapshotAt`), the bar says "Published copy is older: a linked note or file changed 10:15" (with the day as today, when it isn't the same day). When Start itself changed, the text stays as it is today. No server change, and no extra fetch for naming the note.
2. **The recovery key says why Continue is greyed out** (`web/src/components/RecoveryKeyReveal.tsx`). While the key hasn't been downloaded or copied, a line near Continue says "Download or copy the key first." Once it has been downloaded or copied, the line goes. The checkbox rule stays: Continue still needs both.
3. **The Info panel of a file the server doesn't read.** For a PDF or image over the size the server reads (15 MB, the same rule as the agents' "larger than 15 MB: Kumbu² keeps it, but doesn't read it"), the web viewer's Info panel (`web/src/attachments/Lightbox.tsx:202`) adds one line: "Larger than 15 MB: Kumbu² keeps it, but doesn't search its text or show a thumbnail." Use the existing `MAX_ATTACHMENT_BYTES` (`web/src/attachments/Thumbnail.tsx:66`); don't copy the number. Under the limit, nothing changes.
4. **The archive's count in the tree** (`web/src/shell/TreeDrawer.tsx:374`). The tree shows a vault's archive as "Archive · 1 file · 8 B" when the one item is an archived **note**. The server's summary (`ArchiveSummary(int Files, long Bytes)`, `src/Kumbu.Server/Api/NoteApi.cs:103`) counts both together and doesn't tell them apart, and the API stays as it is. **Then:** the label says "item": "Archive · 1 item · 8 B", "Archive · 3 items · 2 MB". That is true for notes and files alike.

## 3. Out of scope

These are UX blocks of their own. Each needs a UX review before its spec, so they go to the backlog in Kumbu², not into this run:

- an indicator while a vault is being re-embedded, and which embeddings each vault uses
- a Settings category "External" with an "Embeddings" tab for the TensorX key
- a key to switch into Write mode
- `help`'s summary column, which starts at about character 120
- `rm`'s sentence on how many links each note holds
- G36's Contents order (noted as fine)
- A7's focus after a reload (noted as fine)
- A9's link address without `#…`

Also out of scope:

- the server's API
- the Obsidian plugin (frozen)
- anything in `Auth/`, `Crypto/`, sharing, roles, sessions or the CSPs
- the version number
- `CHANGELOG.md`

## 4. Security boundary

No item touches authentication, authorisation, encryption, sessions, sharing or the CSPs. F-A and F-C touch routing in the web app. The rule stays as `DeepLink.tsx` states it: whatever fails, nothing says whether the vault or the note exists. The fix of F-A must keep that. A deep link to a vault the reader can't open must not switch the picker to it, name it or show it. The reader fix (F-C) must not keep a previous code or note in memory after the hash changes.

## 5. Hand tests for Chris

The run writes these into its report, as numbered steps with where to look and what passes, for staging:

- F-A: from Welcome, open a `/n/` link of Work, on the laptop and on the phone
- F-C: a wrong code, then the right link in the same tab
- the 200 ms wait: click into the other pane a few times, then double-click a word there
- the PIN dialog's text
- Start's publication bar after changing Alpha
- the recovery key's line
- the Info panel of the 16 MB PDF
- the archive's count ("1 item")

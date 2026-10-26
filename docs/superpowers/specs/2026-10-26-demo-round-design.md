# The demo round: app updates and PWA, export and import, welcome vault, activity, GDPR deletion — Design

Date: 2026-10-26. Decided with Chris in a brainstorming session on the day before the COR.energy demo (Tuesday 2026-10-27). Five independent features, one pull request each, planned together with the Obsidian sync ([2026-10-26-obsidian-sync-design.md](2026-10-26-obsidian-sync-design.md)). Where this document says *Claude's call*, the decision was not discussed with Chris and is open to review.

## Global

- **Audience:** Kumbu² is built by SCAI for an open audience of users and deployers. COR.energy is invited to use and sponsor it; it is not the client. Nothing in code, copy, defaults or docs names COR or its addresses; whatever a deployer would want to set is configuration.
- **Users:** prosumers and professionals. Defaults trust them; safer options are offered, not forced.
- **Language:** all product text in English for now (British spelling, as the repo); other languages later.
- **Who may:** export, import and everything in §5 are for **sessions only** (the user, or an admin for §5), never for API keys, connections or agents.
- These features sit next to OAuth (`step-10-oauth`, not merged yet) and the Obsidian sync in the same web app; menus they share (the vault menu, Settings) are named below so the plan can order the merges.

## 1. App updates, version display and PWA

### Why

Chris has seen users of another app of his (Chatsundere) stay on an old version after a release, in the browser too, until a hard reload. The cause there is most likely its service worker: it serves `index.html` from its cache, a new worker waits until every tab is closed, and by design an update is applied only on the login screen. A second cause applies to every single-page app: a tab or an installed app that stays open never loads new code. Kumbu² must pick up a release reliably, within this version. Installing as an app is the easy part ("an icon on the device").

### What it does

- **No service worker** (*Claude's call*, agreed by Chris). Only a web app manifest. Chrome installs from its menu without a service worker since version 108 on Android and 112 on desktop; iOS's "Add to Home Screen" never needed one. That removes the most likely cause entirely. The cost: no automatic install banner on Android, so the app shows its own short hint "Install Kumbu² as an app" with the steps per platform (in Settings, and once, dismissible, on a phone's first visit; *Claude's call*). Kumbu² needs no offline mode: notes live encrypted on the server.
- `index.html` keeps `Cache-Control: no-cache` and hashed `/assets` stay `immutable` (`src/Kumbu.Server/Program.cs:155`), as today.
- **Versions:** the web app and the server carry **the same build version**, the one the image build already computes (`x.y.z` for a tag, `0.1.0-pre.N` on master, `.github/workflows/docker.yml`), plus the short commit hash; the Dockerfile passes both into the web stage as build arguments (*Claude's call*: `version.txt` alone would make app and server differ on every master build). A local build (`0.0.0-dev`, no hash) on either side switches update detection off. The server sends its own as a header, `X-Kumbu-Version: 0.2.0+a6362ec`, on every `/api` response. The app compares them on every API response and when it becomes visible again (`visibilitychange`).
- **Applying an update:**
  - Nothing unsaved: the app reloads by itself at the next navigation or when it becomes visible, **on the same page** (decided by Chris: the user must not be confused). The URL stays; scroll position and read/edit mode are restored. Afterwards a short notice: "Updated to 0.2.0".
  - Unsaved text in the editor: a bar "Kumbu² 0.2.0 is available · Update"; the reload happens after the text is saved, never before.
  - Loop guard: at most one automatic reload per version. If the version is still old afterwards (a caching proxy, say), the app stops and shows "Kumbu² couldn't load version 0.2.0. Reload the page, or ask your admin."
- **Version display** in Settings: "Kumbu² 0.2.0 (a6362ec)"; if the app is behind the server, the server's version next to it.

### Manifest and icon

- Name `Kumbu²`, short name `Kumbu²`, `display: standalone`, `start_url: /`, `theme_color` and `background_color` from the default colour scheme, plus `apple-touch-icon` and the iOS meta tags.
- **Icon: a placeholder "K²"** in the default theme's colours (decided by Chris; the real logo comes later, Chris wants to work it out himself). One SVG source; a small script generates the favicon, 192 and 512 px icons, a maskable variant (with the safe zone) and the 180 px `apple-touch-icon`. Replacing the logo later means replacing the SVG and running the script.

## 2. Export and import

### Why

With the Obsidian sync, export is for keeping a copy of one's own notes and for tools Kumbu² doesn't support (new ones, say). Together with import, it is also how a user moves to another Kumbu² server (data portability). It is a rarely used feature and must not push frequently used ones aside: it lives where people seldom go.

### Export one vault

- Vault menu → **"Export as ZIP"** (next to "Connect Obsidian…").
- `Work 2026-10-26.zip`, holding one folder `Work/` with the vault's notes as `.md` files in their folders. Unpacked, it opens directly in Obsidian as a vault ("Open folder as vault"; Obsidian creates `.obsidian/` itself).
- Only the current notes, as stored: no history, no trash, no checkpoints, no settings (decided by Chris).
- Each file's modification time is the note's last write in Kumbu², so Obsidian's "modified" sort is right at once (*Claude's call*).
- Built by the server and streamed (*Claude's call*), from the session's unlocked vault.

### Export all

- Settings → "Your data" → **"Export my vaults…"**: a dialog lists every vault of the user with a checkbox, all ticked; any can be unticked (decided by Chris).
- `Kumbu² export 2026-10-26.zip`: **one top-level folder per vault**, each as in the single export, so each is an Obsidian vault of its own. This is the format import reads.

### Import

- Settings → "Your data" → **"Import vaults…"**: pick a ZIP.
- The ZIP is **unpacked in the browser** (*Claude's call*): the server gets only what it handles today, creating a vault (`POST /api/vaults`) and uploading notes in batches (`POST /api/upload/…`, at most 200 files and 5 MB per request). No ZIP parsing on the server.
- A preview first: each top-level folder becomes a vault, listed with a checkbox and its number of notes. Files that aren't notes are skipped and listed, as the upload does today.
- A vault of that name exists already: per vault, **merge into it** or **skip**. Merging asks **once per vault, before anything is written** (*Claude's call*, as the Obsidian first sync): the browser hashes each file and compares it with the vault's version list (`GET /api/vaults/{vault}/versions` from the Obsidian design), so it knows every differing path up front; the choice is replace all (the history keeps the old versions) or skip all, then the batches run with `onConflict` set accordingly. Asking per batch would write some batches before a later one's conflicts were even shown.
- A progress bar, then a summary in the style of the Obsidian first sync: "3 vaults, 412 notes imported · ✓ no conflicts" or the counts.
- Not carried: history, trash, checkpoints, per-vault settings such as the embedding provider. A move is a fresh start with one's own data and a new history (decided by Chris: anything else would be over-engineered and expensive).
- A single folder dropped in without a top-level vault folder (an Obsidian vault zipped directly) imports as one vault named after the ZIP (*Claude's call*).

## 3. Welcome vault

### Why

A new user, for example someone who registers after the demo, should understand within minutes what Kumbu² offers, and then help themselves without asking anyone: that they can work by hand, that their AI works in the same notes, and that it connects to their AI and to Obsidian. Plus the security and privacy concept, at a prosumer's level.

### What it is

- Placeholders in the texts are filled in when the vault is created: `{{server}}` with `Kumbu:PublicUrl` (so "your MCP address is https://…/mcp" is concrete), else left as a sentence telling the user to ask their admin (*Claude's call*).
- Clients are marked *tested* only once tested end to end (the dry run decides for claude.ai, Desktop and mobile; Claude Code is tested since step 7); Chris reviews the security and privacy text in the PR.
- On registration (joining with an invitation, and `bootstrap-admin`), every new user gets a vault **"Welcome"**, theirs like any other: editable, renamable, deletable (*Claude's call*). Existing users get nothing.
- The texts are Markdown files in the repository (e.g. `src/Kumbu.Server/Welcome/`), shipped with every release, so new users always get the current version; existing welcome vaults are never touched.
- **AI-agnostic** (decided by Chris): "your AI" or "your agent", never one vendor, except in the per-client sections below.
- English.

### Notes

- **`Start here.md`**: what Kumbu² is in a few paragraphs, then the three main topics with links:
  1. **`Working by hand.md`**: the editor, links with `[[ ]]`, search, history, trash, checkpoints.
  2. **`Your AI in Kumbu².md`**: what an agent can do here, how its changes show (banner, highlights, Activity) and how to undo them.
  3. **`Obsidian.md`**: the plugin, the pairing code, the first sync, step by step.
  
  It ends with two or three sentences to try with one's AI, e.g. "Look in my Kumbu² Welcome vault and summarise what Kumbu² can do" (*Claude's call*).
- **`Connect your AI.md`**: what any client needs (MCP; through an OAuth connector or an API key), then short sections starting "This is how you connect …": Claude (claude.ai, Desktop, mobile), Claude Code, any other MCP client. Clients tested by us are marked *tested*; the rest "should work, not tested yet".
- **`Security and privacy.md`**: everything encrypted at rest, paths included; a key per vault; what an API key can and can't do; what an admin can and can't see; what deletion does and that backups are not reached. Source: `docs/security-architecture.md`, said plainly.
- **`Your data.md`**: export, import, deleting a vault or the account.
- The texts must match the features as shipped; any feature PR that changes what a welcome note says updates it.

## 4. Activity

### Why

Chris's use: "what did it say about topic X before? I had a different thought about it once." And a prosumer without git experience should see what changed, by whom, without a diff tool. A full diff view of the whole vault is out of reach; this is the compromise (Chris's proposal).

### What it does

- Vault menu → **"Activity"**: the vault's **changesets**, newest first. A changeset is what one writer did with gaps under 10 minutes, for an hour at most (step 6b); every note version already carries its changeset's id. Each row: who, with the writer's kind (you, you in Obsidian (Laptop), agent *liz*; from the Obsidian design §5a), when (from–to), and counts "3 changed · 1 added · 1 renamed · 2 deleted".
- Expanded: the files, each marked changed, added, renamed (old → new) or deleted. Moves and trash records get the writer's changeset id from now on (`MoveRecord`, `TrashedNote`, additive; *Claude's call*), so new renames and deletions count in their changeset; older ones without an id show as rows of their own, as `history` shows them today.
- A file opens a **side-by-side comparison**, switchable between **"against the version before"** and **"against now (latest)"**. On a phone, the two versions stack. Same colours as the agent highlights; the existing line diff (`web/src/note/lineDiff.ts`) does the work.
- **Honest limits:** the history keeps 10 versions per note, the trash and move records 30 days, so older changesets are only partly there. The view says so ("Older changes may be incomplete: Kumbu² keeps the last 10 versions of each note") instead of pretending.
- **Search in earlier versions** (Claude's proposal, not objected to): the search gets a switch "include earlier versions". A hit in an old version opens the comparison with today's text. Words only (no embeddings for old versions), from the vault's versions in memory.
- Sessions only, the owner's vaults.

### Later, maybe (YAGNI)

- **Reads** (which key or connection read which vault when) as a security view. Chris isn't sure it is needed; today each key has "last used".

## 5. GDPR deletion by an admin

### Why

A deletion request (Art. 17) reaches a deployer's privacy contact. An admin carries it out and answers the person by hand. That is enough technically; the product makes it one careful step and gives the admin what they need for the answer and for their records. Not legal advice: each deployer's data protection officer decides.

### What it does

- **One step for an active user** (today an admin must deactivate first: `src/Kumbu.Server/Accounts/Deletion.cs:70`). Admin → Users → a user → **"Delete for a privacy request…"**: a dialog reminds the admin to check the requester's identity and of the one-month deadline (Art. 12(3)), asks for the admin's password and the username typed out, then deletes everything of the user in one transaction (vaults, notes, history, trash, checkpoints, keys, connections, sessions), as the existing deletion does.
- **Deletion receipt**, shown afterwards and downloadable (a small text or JSON file the admin keeps as a record): time, username, user id, the admin who deleted, counts (vaults, notes, keys, connections), the external embedding providers **this instance has configured**, as "may have received text from vaults that used them; no copy was deleted there" (*Claude's call*: an admin can't read which provider a vault chose, since that is sealed with the vault, and a vault may have switched back; "none configured" when there are none), and the sentence that backups are not reached. **No content, no vault or note names.**
- **"Copy reply"**: a neutral English email template for the admin to send from the privacy contact, saying: the account and all notes were deleted on <date>; copies the person's AI tools read are outside Kumbu² and must be deleted there; backups are not reached and are kept as described; and, for future requests, that a user with access can export first and delete their own account in Settings. The deployer's privacy contact is configuration (`Kumbu:Privacy:Contact`, optional; *Claude's call*); without it the template leaves a placeholder.
- **Log:** the deletion with ids, counts and the admin, never content or names (as today).
- **Backups:** README and an admin note say that deletions don't reach backups. The server keeps a **list of deleted user ids** (random ids only, no names) **outside the database**, in its own small volume like the recovery keys (e.g. `Kumbu:Deletions:Path`, `/deletions`; *Claude's call*), because a list inside MongoDB would be rolled back by the same restore it must detect. On start, and in the admin's users page, the server checks it: if a listed id exists in the database (a backup was restored), it warns the admins and offers to **repeat the deletion** in one step.
- The user's own deletion (Settings → delete account) stays as it is; it also adds the id to the list.

## 6. Order and pull requests

One PR per section. §1 and §3 are independent of the rest. §2 and §4 add entries to the vault menu that the Obsidian sync also extends ("Connect Obsidian…"); §4 uses the writer kinds of Obsidian design §5a; §5 deletes connections, which arrive with OAuth. The plan orders the merges accordingly; everything must be on kumbu.tidesson.net for the dry run on the evening of 2026-10-26.

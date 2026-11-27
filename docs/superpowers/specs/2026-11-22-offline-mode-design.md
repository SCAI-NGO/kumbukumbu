# Offline mode for the web app (0.7.0) — Design

Date: 2026-11-22. Status: architecture approved by Chris on 2026-11-22; the UI of the mode (work package 0) reviewed and decided with Chris on 2026-11-22 (§3a). Read against `master` of `kumbukumbu` (`7967a22`), `kumbu-obsidian` (`0b32b17`) and `kumbukumbu-security` (`584685f`).

**Decisions of 2026-11-22 (Chris):**
- 0.7.0 is this one feature, offline reading and writing; besides it only fixes and small UX improvements. Graph view stays in preview until at least 0.8.0 and link prediction waits, unless either runs alongside without hurting the goal (§9). Why: offline is the feature many users wait for, and the gateway to Eidolon in Chatsundere.
- The browser's data is treated exactly as an Obsidian device's: one sync behaviour, the existing sync API, conflict copies. Why: one behaviour to test and explain, and a much smaller project.
- Offline unlocking after a reload asks for the password, never a PIN. Why: a PIN of 4 to 6 digits is guessed in seconds against a copied disk.
- The client-side data layer is built as the **Eidolon core**, a package that can be taken out of Kumbu² later (§2a). Why: 1.0.0 extracts Eidolon as an LGPL library for Chatsundere's C2Duress, and a clean boundary now saves untangling it then.
- Development happens on the branch `release/0.7.0`; pull requests go there (§11).

Earlier thinking this builds on: `/SWE/Kumbu/Ideas/Offline mode 2026-11-13.md` (read-only first, discreet vaults never mirrored, search by meaning off while offline, a mode of its own that is always visible).

## 1. The short answer

**Yes: the browser can be "just another Obsidian device", and that shrinks the project a lot.** The server already speaks the sync protocol to web sessions, and the plugin's engine is pure TypeScript behind ports. What costs real work is not sync but three things around it:

1. **The web app is a thin client.** Links, backlinks, tags, search, calendar, board and graph are all computed on the server (`/api/links`, `/api/backlinks`, `/api/search`, `/api/tags`, `/api/graph/*` …). Offline, a small part of that has to run in the browser, and the rest is plainly marked "needs a connection".
2. **Plaintext in the browser.** Today the security architecture promises that the web app keeps no note content in browser storage and has no service worker (`docs/security-architecture.md`, "Lost or stolen laptop"). Offline breaks both promises, so the mirror needs its own encryption at rest and the document needs a new section.
3. **The app shell offline.** Without a service worker the app doesn't even load offline, and the update mechanism (`web/src/update/`) counts on there being none.

Embeddings are the smallest part: offline means words search in the browser, and what was written offline is embedded by the server when it is uploaded, as every write already is.

## 2. Can the web app reuse the Obsidian sync? (Chris's question)

### What already fits, unchanged

| Piece | Where | Works for a web session today? |
|---|---|---|
| Version list, full and `since` with 5-minute overlap, `ETag`/304, `damaged`, `attachments=1`, `text=1` | `src/Kumbu.Server/Api/SyncApi.cs:57`, `:130` | Yes: no caller restriction, it reads the caller's own unlocked vaults |
| WebSocket hints | `SyncApi.cs:59` ("API keys and web sessions"), `Sync/SyncHub.cs` | Yes: the web app already uses it (`web/src/hooks/useSyncSocket.ts:130`) |
| `PUT /api/notes` with `version`, `409` with the current text | existing | Yes: the editor uses it |
| `DELETE` with `version`, attachments `PUT/DELETE /api/files` with base | existing | Yes |
| Decision table, conflict copy with callout, first-sync merge | `kumbu-obsidian/src/engine/decide.ts`, `conflict.ts`, `firstSync.ts` | Pure functions, no Obsidian import |
| Engine (one queue per vault, check-before-write, `409` as information) | `kumbu-obsidian/src/engine/engine.ts` (1,072 lines) | Pure TS behind `RemoteApi`, `LocalFiles`, `StateStore`, `Clock` (`src/engine/ports.ts`) |
| HTTP client | `kumbu-obsidian/src/api/client.ts` | Takes an injected `HttpFn`; `fetch` with the cookie fits |
| Hints with backoff | `kumbu-obsidian/src/live/hints.ts` | Same protocol |

So the behaviour can be exactly the Obsidian one: per path `local`/`remote`/`base`, a real conflict becomes `<name> (conflict <device> <date> <time>).md` with the callout, Kumbu²'s version stays at the path. One behaviour, one set of race tests, one thing to explain.

### The gaps for a browser client

1. **Many vaults, not one.** A device key holds one vault; a session holds all of the user's vaults, shared ones included. The web host runs one engine per mirrored vault and keys the mirror by **vault id** (`VaultView.Id`, `src/Kumbu.Server/Api/NoteApi.cs:42`), never by name: names are per caller (a member's alias) and can be renamed while the device is offline. The version endpoint is addressed by name, so the host looks the name up from `GET /api/vaults` on every reconnect.
2. **A session, not a device key.** The browser must **not** pair itself and keep a device key: a long-lived bearer key in storage JavaScript can read is exactly what an XSS would take. The session cookie is `HttpOnly` and `SameSite=Strict`, and sync already accepts it. Consequences: a session ends (30 minutes idle, 12 hours; a remembered one 7 days idle, 30 in all, `Accounts/Sessions.cs:36`), so a week offline usually comes back to a sign-in. The engine's `AuthError` ("pair again" in the plugin) becomes "Sign in to upload your 4 changes"; local changes stay in the mirror until then. Web writes show as the user's own (`writerKind: user`), as today.
3. **The editor is the "file".** `LocalFiles.read` must return the editor's unsaved text, and `writeIf` must refuse while the editor holds a newer draft, as Obsidian's `vault.process` does. That is one adapter over the editor store (`web/src/state/editor.ts`) and the mirror.
4. **Renames offline.** The web app moves with `keepLinks: false` (`web/src/api/notes.ts:390`): the server rewrites links. The engine moves with `keepLinks: true`, because Obsidian rewrites links itself. A browser offline can't do either honestly without a local link rewriter. **Proposal: no move or rename while offline in 0.7.0**; create, edit and trash are enough.
5. **Rights.** A read-only member's mirror never queues a write (the editor is read-only offline too). A vault whose access is lost, or that becomes discreet, is purged on the next contact (§4.4).
6. **Attachments on demand.** The engine downloads every attachment under its limit and already knows "too large in Kumbu²" (`engine.ts:754`). It needs one more port, a fetch policy (`wantBytes(path, size)`), so the host decides which bytes to fetch (§6).
7. **Case.** Browser storage is case-sensitive: `caseInsensitive: false`, the simple case.

None of these needs a new server endpoint for notes. The server work is the mirror key slot and the wipe signals (§4).

### Where the engine lives

Today it is only in `kumbu-obsidian`. Two consumers want one copy. It moves into the Eidolon core (§2a), consumed by `web/` directly; the plugin takes its sync part through a vendoring script with a CI check that its copy matches a tagged commit. Why: the server and the engine's protocol change together in this repository, and the plugin's release cadence stays its own (it only changes version when its code changes). Publishing to npm would also work but is an outward step for a package that isn't public yet.

## 2a. The Eidolon core: built to be taken out

Everything the browser keeps and does with notes lives in one package, `packages/eidolon-core` in this repository (a pnpm workspace with `web/`), so that 1.0.0 can move it into its own repository as a library without untangling it from the web app.

**What it holds:**
- **Sync:** the engine, decision table, conflict copies and first sync from the plugin; the REST client and the hints over injected ports.
- **The sealed store:** records, the mirror key and its wraps, schema version and the carrying over of unsynced changes (§4, §5).
- **The local index:** link resolution by Obsidian's rules, backlinks, tags, words search.
- **The tools:** the operations over the mirror in the shape of the VAFS commands, `ls`, `cat` (with sections), `rg`, `links`, `backlinks`, `tags`, and `write`, `edit`, `replace`, `rm`. The offline UI uses them, and they are what Chatsundere's agent calls later. Same names and semantics as the server's `sh` and MCP tools, as far as a mirror can answer (no history, no `semsearch`).

**Boundary rules** (checked by a lint rule and the package's own build):
- No React, no import from `web/`, no Kumbu² UI or routing. The DOM only through ports: storage (IndexedDB behind an interface), crypto (WebCrypto behind an interface), HTTP, clock, the WebSocket.
- Kumbu²-specific things stay in `web/`: sign-in and the session, mode detection and its bar, which vaults to mirror, the editor adapter, the service worker.
- The protocol it speaks is the server's sync API as it is; a Kumbu²-only call (the mirror slot, §4.2) goes through a port the host fills, so another host can fill it differently.
- Its own Vitest suite, run in CI with the web app's; the engine's race tests move with it.

**Licence:** AGPL-3.0-or-later like the rest, until the extraction. Relicensing to LGPL then needs the copyright holder (Second Circuit) and no third-party AGPL or GPL code inside the package; dependencies are kept to permissive licences (MiniSearch is MIT) so the move stays possible.

## 3. Shape

```
Browser (one origin = one Kumbu² server)
┌───────────────────────────────────────────────────────────────┐
│ UI (React)                                                    │
│   online: react-query against /api, as today                  │
│   offline: OfflineSource (same hook shapes, a subset)         │
│                                                               │
│ Mirror host: one sync engine per mirrored vault (shared pkg)  │
│   RemoteApi → fetch + session cookie                          │
│   LocalFiles/StateStore → IndexedDB, AES-GCM sealed           │
│   local index in memory: link resolution, backlinks, tags,    │
│   words search (a scan of the texts, ADR 0031)                │
│                                                               │
│ Service worker: precached app shell only, never /api          │
└───────────────────────────────────────────────────────────────┘
```

**Online stays as it is.** The app reads from the server as today; the mirror follows in the background through the hints. Only offline does the UI read from the mirror. Why not local-first everywhere: it would rewrite the data layer of every screen at once and put the whole online app at risk for a release that should only add a mode. The cost is two read paths for the screens that work offline, kept small by giving `OfflineSource` the same shapes as the hooks it stands in for.

**The mode** (from the idea note, a must): detected automatically (failed requests plus the socket's state, `navigator.onLine` only as a hint), always visible (a bar and a mark in the sidebar), with "N changes waiting to upload" until the engine's queues are empty. The app stays in offline mode until the queues are flushed, so it never shows the server's older text right after reconnecting.

### What works offline in 0.7.0

| Works | Needs a connection (shown, not silently failing) |
|---|---|
| Vault tree, reading notes, rendering with wiki links resolved locally, embeds | Search by meaning, `semsearch`-style ranking |
| Words search across mirrored vaults | Console (`sh`), history, blame, diffs, checkpoints, restore |
| Backlinks and tags (from the local index) | Graph, calendar views, board |
| Editing, new notes, trashing | Move and rename, sharing, publishing, keys, admin, settings |
| Images and PDFs that are in the mirror | Attachments not fetched yet |

Calendar and board read-only offline are possible later from the same local index; left out to keep 0.7.0 tight.

## 3a. The mode in the UI (work package 0)

UX review of 2026-11-22, its findings discussed with Chris one by one and each decided as below. **One rule over all of them (Chris): a unified UX.** Offline uses the screens, bars and habits people already know (the sign-in form, the update bar's line, the Write refusal's pattern, the search sheet's notice) rather than screens of its own. Why: nothing new to learn for a mode people meet rarely and under stress, and less UI to build and test.

### Turning it on

- **Off by default, per device; invited once.** On a device where the user ticked "Keep me signed in" or runs Kumbu² as an installed app, a bar in the shape of the install hint (`web/src/update/InstallHint.tsx`): "Keep your notes on this device for when you're offline?" with **Set up** (to Settings, Offline) and **Not now**, shown once per device, answered or not. Why: confidentiality first, a copy on a borrowed or shared computer must never come about unasked; then space and data; and changes made offline live only on the device until uploaded, which a person should have chosen knowingly. The two signals are the ones where the device plainly is the person's own.
- **Easy to reach and change** (Chris): its own Settings entry (below), reachable offline too, and the offline bar and the sidebar's mark lead there in one tap.
- **The first download shows size and progress, and is fault-tolerant.** Each vault shows its size before it is chosen, and its progress while it comes ("Work: 1,204 of 2,000 notes"). No separate warning about mobile data: the browser can't tell reliably, and the size is shown before choosing. **The inventory comes first:** a vault starts with the version list (`attachments=1`), every path with kind, size and version, sealed like the rest; then the note texts; then attachments under 1 MB. So the tree is complete at once, and an interrupted download is a normal state, never an error: what is there is usable, the rest says "Not on this device yet", and the download resumes where it stopped once online.

### Unlocking

- **The sign-in form opens the copy offline; there is no unlock screen.** After a reload offline, the usual username and password open the copy on this device, with one line above the form: "You're offline. Signing in opens the notes kept on this device." "Keep me signed in" is hidden offline (it does nothing there). Why: one habit, and password managers fill it as always; the username is needed anyway, since the mirror's header holds only the user id's hash, and it picks the right copy when two people share a browser. A wrong password costs the Argon2id hash (§4.2), which is the throttle; no lockout counter.
- **An offline unlock ends like a session.** The same idle limits as the session (`Accounts/Sessions.cs:36`): 30 minutes, or 7 days when "Keep me signed in" was chosen at the last sign-in on this device. Then unsaved editor text goes into the copy, the key leaves memory, and the sign-in form asks again; changes waiting stay sealed. Why: a key in memory without a limit would leave an unlocked tab open forever, the laptop left open on a train being exactly the case the sealing is for.

### The bar and the mark

One line in the page's flow under the top bar, styled like the update bar (`web/src/update/UpdateBar.tsx`), always there while offline and never dismissable; a mark beside the vault name in the sidebar. Both lead to Settings, Offline. The sentences speak of the person's notes, not of the engine:

| State | Bar |
|---|---|
| Offline, nothing waiting | "You're offline. Your notes are here; what needs a connection is marked." |
| Offline, changes waiting | "You're offline. 3 changes will upload when you're back." |
| Offline, no copy on this device | "You're offline. This device keeps no copy of your notes." |
| Back, uploading | "Back online. Uploading 3 changes…" (why the app is still in offline mode) |
| Back, session ended | "Sign in to upload your 3 changes" with **Sign in** |
| Done | "All changes uploaded", four seconds, like "Updated to …" |
| Done, with conflicts | "Uploaded 4 changes. One note was also changed elsewhere: your version is kept beside it." with **Open**; several: "3 notes were also changed elsewhere", **Open** leads to the list. Stays until opened or dismissed. |

- **No flicker: at least 15 seconds per mode.** Offline only after a failed request with the socket down (`navigator.onLine` only as a hint); online only once the socket is back and every queue is empty (§3). A mode, once entered, holds at least 15 seconds. Why (Chris): a connection that switches more often is broken anyway, and the bar should not mirror its noise. The 15 seconds hold the mode, not the text: the count updates at once, and "All changes uploaded" keeps its four seconds.
- **Conflicts stay visible until seen**, unlike the done line. Why: they ask the person to look, and the conflict copy (as in Obsidian, with its callout) leaves them the most agency; power users report conflicts as rare, so the notice is no nuisance.
- **Every tab shows the same.** Each tab unlocks for itself (the key is per tab; online the slot answers without asking), but the mode and the number of changes waiting are the same in every tab, never two different numbers; one engine per vault across tabs (WP4, WP8).

### "Needs a connection"

- **Dimmed, not hidden, with the reason.** What doesn't work offline (§3's table) stays where it is, disabled, with "Needs a connection" as its title, and in menus (Move, Rename, Share, Publish, History …) as the item's second line: the pattern of the Write refusal in the top bar (`web/src/shell/TopBar.tsx`, `refusal`). Why: people find things in the same place when they're back, and whole parts of the screen don't vanish and reappear.
- **A page reached by address or left open** (console, history, blame, graph, calendar, board, every Settings page but Offline) shows one calm sentence in its pane: "The console needs a connection. It's back when you are."
- **Search says what it searches.** Online the server already says when meaning isn't available, in the search sheet's notice line (`src/Kumbu.Server/Api/NoteApi.cs:1064`). Offline the same line, place and style: "You're offline: search finds words only. Search by meaning is back when you are." The chips Hybrid and Meaning are dimmed with "Needs a connection"; every offline search runs by words, and the results' heading says "words" where online it names the mode; the chosen mode is kept and applies again online. The heading decided by Chris on 2026-11-25, because naming the chosen mode while the notice says words only reads as a contradiction. Below the results: "Not searched: Archive (not kept on this device)".
- **What isn't on this device says so.** A vault not kept: in the vault switcher, dimmed, "Not kept on this device". An attachment not fetched: its place in the note shows name, size and "Opens when you're online"; online, opening it fetches it and keeps it within the budget.

### Settings, Offline

A Settings entry **Offline**, after Vaults, reachable offline. Top to bottom:

1. The switch **Keep my notes on this device**, with two sentences: the copy is sealed with your password; someone who finds the device can only try to guess it.
2. **All my vaults**, and below it each vault with its size and, while it downloads, its progress. New and newly shared vaults follow "All my vaults". Discreet vaults never appear (§4.3).
3. **Attachments:** space used and the budget, 250 MB, 500 MB (default), 1 GB or 2 GB.
4. **Kept offline:** the folders and vaults marked Keep offline, each with Remove. Keep offline itself is set where the folder is: an item **Keep offline** in the folder and vault menus of the tree. Why: the choice belongs beside what it is about; Settings gives the overview.
5. **Storage:** "Uses 312 MB of about 4 GB this browser allows", and whether the browser protects the copy from clean-up (`navigator.storage.persisted()`). On Safari outside the Home Screen: "Safari removes the copy after 7 days without a visit. Add Kumbu² to your Home Screen to keep it." with a link to the install steps (Appearance); the install hint gains the same sentence where offline is on.
6. **Devices with a copy:** each mirror slot (§4.6, work package 3) as "Firefox on Linux · last online 3 days ago · had 2 changes waiting", this browser marked "This device", each with **Remove offline copy** and a confirmation that names the waiting changes ("2 changes on that device haven't uploaded and will be lost") and says the copy goes when that device next comes online. The label is made from browser and system when the copy is turned on; every device, this one too, has **Rename**, a dialog with the name to change (as the board renames a column), so two "Firefox on Linux" can be told apart. Why in 0.7.0 rather than 0.8.0: the label is the only way to tell devices apart before removing one, and a rename touches nothing but the label, so it costs no risk to the copy.
7. **Remove the copy from this device**, with the waiting changes named first.

**Security's texts say what happens to copies:** "Sign out other sessions" adds "…and removes the offline copies on those devices, with any changes they haven't uploaded."; "Re-encrypt my vaults" and the recovery key add "Offline copies are downloaded again, on this device too." (both delete every slot, §4.6).

### Sign in again, sign out

- **Sign in again is a dialog over the app.** When the session ended while changes wait, the bar's **Sign in** opens a password dialog over the app, the username shown and known; the note, the place and the unlocked copy stay. After signing in the upload starts at once. The dialog uses the sign-in page's password field (`autocomplete="current-password"`, the username as a hidden field) so password managers fill it. Online with nothing waiting, a 401 goes to the sign-in page as today. Why: the sign-in page resets the editor and the cache (`web/src/routes/SignIn.tsx`), losing the place the person came back to.
- **Sign-out with changes waiting.** Online: they upload first, as the editor saves first today (`web/src/shell/TreeDrawer.tsx:442`). Offline: a dialog "3 changes haven't uploaded yet. They stay on this device, sealed, and upload the next time you sign in here." with **Sign out** and **Stay**, and a quieter **Sign out and remove the copy**, which says the 3 changes are then lost.
- **A sign-out offline is pending until online.** The server can't be told, and JavaScript can't delete the `HttpOnly` cookie: the app keeps a mark (a flag in local storage, no secret, so unsealed) and sends `DELETE /api/session` before anything else on its next start online, and says so: "Your session ends on the server when this device is back online." Why (Chris: it gives the person agency on a borrowed device): without it a remembered session cookie would live on unnoticed.

### Purges and discreet vaults

- **A purge names no vault: purge is purge.** The browser can't tell why a vault left the contact answer (access lost, deleted or marked discreet are alike by design, §4.6), so naming it would put a just-hidden discreet vault's name on screen. The notice: "A vault's copy was removed from this device: it's no longer available here.", adding ", with 2 changes that hadn't uploaded." when that is so. The notice follows the removal, not the path that found it: a contact's answer, the engines' vault list, the contacts of the unlocking or another tab's removal, whichever comes first (`SealedMirror.listenRemoved`); a vault the device no longer chooses to keep gives none, as Settings has said what that loses. Decided by Chris on 2026-11-24, because the engines' vault list often removed the vault before the contact that alone set the notice (the 0.7.0 test run's F-06). No download of lost changes first: it would keep what the server wants gone, and leave a trace in the discreet case. A whole wipe (`mirror-wipe`) leads to the sign-in page with "The offline copy on this device was removed."
- **A discreet note open when the connection drops** stays readable while open; Write is off with "Discreet vaults aren't kept offline: changes are saved when you're back", adding "; reloading now loses them" while unsaved text is held. That text stays in memory only, never in IndexedDB, and is saved once online. The discreet vault leaves the tree and the switcher while offline, as when hidden: a "Not kept on this device" label would give it away. The open note closes on leaving it.

### To verify

- Safari's 7-day eviction on current iOS and macOS: until a test iPhone is at hand, the sentence rests on WebKit's published storage policy, checked again before the release; the hand-test item waits for the device, and power users on Safari are asked.

## 4. Security

### 4.1 What is in the mirror, and sealed

Everything about notes is sealed: content, paths, vault names, attachment bytes, the engine's state (bases are keyed by path). In the clear only a header: schema version, the user id's hash, salts and wrapped keys. Why paths too: the discreet design already established that a name alone can give a person away.

The local index (links, tags, words search) is built in memory after unlocking and not stored in 0.7.0: a vault of 2,000 notes indexes in well under a second, so there is no reason to add a second sealed thing.

### 4.2 The mirror key

- A random **mirror key** (AES-GCM 256) per user and browser encrypts every record (a fresh nonce per record).
- **Wrapped twice:**
  - **Online:** by the server, in a new per-device **mirror slot** sealed under the user key (`POST/GET/DELETE /api/account/mirror-slots`, sessions only). A signed-in tab unwraps without asking.
  - **Offline:** under a key derived from the password, so the app opens offline after a reload. The web app has the password at every sign-in, so it re-wraps then, and a password change needs no special path.
- **KDF: Argon2id**, 64 MiB, 3 passes, one lane (RFC 9106's second recommendation; one lane, as WebAssembly runs on one thread). The header names the hash and keeps its parameters (`kdf`), so later copies can take more without breaking earlier ones; a header asking for more than a browser can give (over 1 GiB, 16 passes or 8 lanes) doesn't open, so a header changed on the device can't make the unlock take all memory. The core holds no implementation: the host brings it as a port (`CryptoPort.kdf`), so the core stays free of WebAssembly and of the library's choice. The web app runs hash-wasm's WebAssembly build (MIT) in a worker of its own, `/assets/kdfWorker-*.js`, the one script the server lets compile WebAssembly (`Security.KdfWorkerPolicy`, `'wasm-unsafe-eval'` on that file only); the page and every other script keep the policy that compiles nothing. Where a browser holds the worker to the page's policy, @noble/hashes' plain-script Argon2id (MIT) answers instead: the same bytes, only slower. Why: PBKDF2 is cheap to guess on GPUs against a copied disk, and Argon2id's memory cost isn't; the worker keeps the opening of the CSP to the few lines that need it. Copies made with PBKDF2 (earlier 0.7.0 builds, header schema 1) are passed over and made again; nothing was released with them.
- The key lives in memory only, per tab. A reload offline asks for the password; online the slot answers.
- **A password change leaves the other devices' copies in place.** Each still opens offline with the old password until its device next signs in, which wraps the key again under the new one. The first visit after the change lists the devices that keep a copy, each with "Remove offline copy", and Settings, Security says so before the change. Why: wiping them would lose changes on the person's own devices that haven't uploaded, the one thing in Kumbu² with no trash or history; a device that isn't trusted any more is removed from that list, and its session has ended with the change anyway.
- **No PIN for offline unlocking:** 4 to 6 digits against a copied disk are guessed in seconds.

### 4.3 Discreet vaults

**Never mirrored**, revealed or not. This is the anti-goal of the discreet design ("a hidden discreet vault's name or content reaching the browser in any … storage") taken literally. A vault marked discreet on another device is purged here at the next contact.

### 4.4 Wipe and revocation

The server can only reach a device that comes online. On contact the client purges:

| Signal | Purges |
|---|---|
| Vault access lost (membership revoked, vault deleted) | That vault's mirror |
| Vault marked discreet | That vault's mirror |
| Mirror slot deleted ("Remove offline copy" in the device list), user deactivated, account deleted, "end other sessions" | The whole mirror |
| Account or vault re-encrypted | The affected mirrors (they are re-synced under a new mirror key) |

A purge also removes changes that haven't uploaded yet, so the device list says beforehand when a device had changes waiting (its slot records that at each contact). **Removing another device's copy that had changes waiting, or hasn't reported yet, takes the password**, as deleting a vault does; the server checks it against the slot as it is at the removal, so a contact in between that reports changes makes it ask. This browser's own copy needs no password: the browser removes it itself, having said what goes with it. **"End other sessions" leaves those copies in place** and ends only their sessions; they wait for a removal with the password. Why: a stolen session must not be enough to destroy work that exists nowhere else, and a lost device is cut off at once all the same, as its session ends. A device that never comes online again can't be wiped; the security document says so plainly, as it does for the Obsidian plugin.

**Deactivating a user deletes their slots**, in the deactivation's transaction, so their browsers are told to purge at their next request, signed in or not. Why: a deactivation is how a departing user is cut off, and without it their copies, shared vaults included, would keep opening offline with the password for ever. A reactivation can't bring the copies back, nor the changes they hadn't uploaded; the admin's confirmation says so. **Restoring a backup** deletes the copies made since the backup in the same way (their slots aren't in it); the security document's "Backups" tells operators to let devices upload first.

**The kill switch** (backlog, not built) gets room here: a duress password at the offline unlock that destroys the mirror is a natural extension, decided in its own design.

### 4.5 What changes in the security architecture

- The "Lost or stolen laptop" row: the web app now keeps sealed note content in IndexedDB on devices where offline is on; what a finder can do (guess the password against Argon2id offline) and can't.
- "There is no service worker": now there is one, caching the built assets only; API answers stay `no-store` and are never cached. **It checks what it serves:** the build writes each kept file's SHA-256 beside its name into the worker, and a kept file whose bytes differ is deleted and fetched again, never served, offline neither. Why: CacheStorage is the origin's, so any page of it can write the worker's cache, and one script run in the origin (a compromised dependency in one build, a minute at someone's developer tools) would otherwise be served on every later start, across sign-outs, password changes and the next builds. Subresource Integrity on `index.html` would cover only its entry files, online only.
- A new §6 entry "Offline mirror", and the 0.7.0 audit (Mythos, blue team) covers it, the service worker and the mirror slot endpoints.

### 4.6 The mirror slot API (work package 3, built)

Decisions of 2026-11-22 while building it, each with its reason; to be confirmed in review.

- **Routes.** `GET /api/account/mirror-slots` (the device list: id, label, created, last contact, changes waiting, whether it is this browser's), `POST /api/account/mirror-slots` with a `label` (answers `201` with the id, the token, the key and the vaults), `POST /api/account/mirror-slots/contact` with the `token` and the changes `waiting` (answers the id, the key and the vaults; `waiting` left out means "not counted yet", recorded as `null`, for the contact of an unlock, before the copy is open, and the count follows once it is: a lost connection between the two must not leave the device list saying nothing is waiting), `PUT /api/account/mirror-slots/{id}/label` with a `label` (the same one line as at its making; `204`, `404` for a slot not the user's), `DELETE /api/account/mirror-slots/{id}`. Sessions only. Why a rename changes only the label: the key, the token and the vaults are the copy's, and the device needs to know nothing of its new name. Why a contact route beside the three of §4.2: the slot records the changes waiting at each contact, a write, so it is a `POST`, and it is the one call a browser makes on every reconnect to learn what to purge.
- **The server makes the mirror key**, and seals it under a key from the user key **and** a random token the browser keeps in the clear. Why: no weak key from a buggy client, and neither the database nor the user key alone (which every session of the user holds) opens a browser's key without that browser's token. The token, not the slot's id, is what a contact and the `401` take; the id is only for the device list's removal.
- **The vault list is the signal for one vault.** A contact lists the vaults the browser may mirror now; a mirrored vault not listed is purged (access lost, deleted, marked discreet), and one listed with another tag than it was mirrored under was re-keyed. Why: one answer covers all the per-vault signals of §4.4, with no new code on the notes and sync routes, and the tag needs no new field (it is an HMAC of the vault's key id under a key from the token).
- **Another user's token is refused, not wiped**: a contact with a token of another user's slot answers `403` without a code, and the browser keeps that copy and doesn't open it for this session. Why: the copy isn't gone, and wiping it would lose its owner's changes that haven't uploaded; the token is random, so the answer tells nothing to someone who doesn't hold it.
- **`mirror-wipe` purges the whole mirror**: the code on a contact's `404`, and on the `401` of a request without a caller that hands in the token in `Kumbu-Mirror` when no slot of it exists. Why the header: a browser whose session ended can't make contact, and must still learn that its copy was removed or its account deleted; a session that merely ended keeps the copy.
- **"End other sessions" deletes the user's other slots**: all but those the asking session made or last contacted. Why the session and not a token in the request: the server knows it without the client's help, and the browser that asks contacts its slot on every sign-in.
- **A re-encryption or recovery deletes every slot**, the asking browser's too. Why: its mirror is sealed under a mirror key the old user key wrapped; it is synced again under a new one (§4.4).
- **The socket frame `{"type":"mirror"}`** asks a tab to make contact now: on a removed slot, and on a session's socket that lost a vault. **Never on a discreet mark.** Why: the discreet design shows nothing on a socket that names a mark (its close says only "connect again"); the sockets that held the vault close anyway, and a tab makes contact whenever its socket connects again.
- **At most 20 slots per user.** Why: a person's browsers are few, and a stolen session mustn't fill the database.

### 4.7 The sealed store (work package 4, built)

Decisions of 2026-11-22 while building it, each with its reason; to be confirmed in review. The code is `packages/eidolon-core/src/store/`, the browser's adapters `web/src/offline/`.

- **A copy per user and browser, found by two hashes.** The one record in the clear is the copy's header: schema, SHA-256 of the user id (finds the copy when signed in), SHA-256 of the username in lower case (finds it at an offline sign-in, where only the username is known, §3a), the slot's id and token, the password wrap and the idle limit. Why the username's hash: offline there is no way from the username to the id, and two people may share a browser. A new copy for the same user replaces the old one; the host looks for one and unlocks it instead.
- **Two keys from the server's mirror key**, by HKDF, both non-extractable: AES-GCM for the records, HMAC-SHA-256 for their names. The raw key is wiped once they are made. Why: script can use the keys but not read them, so not even a bug can write the key anywhere.
- **Every record name is an HMAC**, of the vault id, and of the vault id with the path; the storage keys show neither. Each record is sealed with its storage key as additional data. Why: a finder with the disk can't tell vaults or paths apart, and can't move a record to another path's place to make the app show it there; a record that doesn't open is left out with a warning.
- **The password wrap is bound to its copy** (additional data) and takes a fresh salt at every sign-in, Argon2id since the fix of §4.2, its parameters kept in the header so they can be raised. Why: a wrap copied into another copy's header opens nothing, and a changed password needs nothing but the next sign-in.
- **The first contact reports no changes waiting, and a second one reports them** when there are some. Why: before the key has opened the copy nothing can be counted; sealing the count in the clear header would put it beside the key, and the second call costs one request only when it matters.
- **Inventory first through the fetch policy, without changing the engine.** The engine's version list is recorded in the copy before the engine acts on it (every path with kind, size and version), so the tree is complete at once; the policy says no to every attachment until a check has gone through to the end (every note's text is here), then yes to images and PDFs up to 1 MB, and the host asks for a full check. Why: the order of §3a falls out of the port the plugin shares, and the plugin's copy of the engine stays as it is.
- **One writer at a time across tabs.** Every write takes the copy's Web Lock and reads the entry afresh inside it, then tells the other unlocked tabs which paths changed (BroadcastChannel, never a lock or a closed tab). The tab that holds a second lock, the engines' lock per copy, runs the sync engines; the others read the same records, and their own writes reach the engines through the same "changed" message. When that tab locks or closes, another unlocked tab takes the lock. Why: IndexedDB can't hold a transaction open across WebCrypto's awaits, so the lock is what makes check-then-write atomic; one lock per copy rather than per vault is simpler and gives the same "one engine per vault".
- **No trash and no folders of its own.** The copy's `trashToLocal` and `trashIf` delete (Kumbu² keeps the trash); a folder is the paths below it, so none is ever empty. Why: the mirror is a copy, and an empty folder in Kumbu² already comes as its marker.
- **A wipe locks every tab of the copy**, and a purge of one vault drops it in every tab. Why: a tab must not go on showing what the server wants gone.
- **What waits for the next packages:** the switch that turns the copy on and the vault choice (WP9), the editor's drafts as the copy's `Drafts` port (WP7: unsaved discreet text never reaches it, as the copy never holds a discreet vault), the `Kumbu-Mirror` header and the contacts on sign-in, reconnect and `mirror` frame (WP8, with the mode), the tree and reading from the copy (WP5).

### 4.8 Reading the copy (work package 5, built)

Decisions of 2026-11-22 while building it, each with its reason; to be confirmed in review. The code is `packages/eidolon-core/src/read/` and `src/links/`, the web app's side `web/src/offline/source.ts`.

- **The hooks keep their shapes; their query functions ask the copy while offline.** `useTree`, `useNote`, `useLinks` and `fetchFile` (so `useAttachmentUrl`, thumbnails, PDFs and text files) answer from an `OfflineSource` while one is set, and from the API otherwise. The mode (work package 8) sets it on entering offline mode with an unlocked copy, clears it on leaving, and asks `tree`, `note` and `links` again on each switch. Why: §3's "same hook shapes, a subset" with no second set of hooks, and every screen that reads through them (embeds, the lightbox, the gallery) works offline without a change of its own.
- **The server's link resolution, ported, not reimplemented.** `src/links/` is a line-by-line port of `Kumbu.Core/Links` (wiki and Markdown links, code spans and fences, aliases, Obsidian's rules with the archive and folder preferences), with the C# tests' cases. Why: a link must lead to the same note offline as online; two different sets of rules would be a bug per difference. Where it can't be exact: the aliases are read without a YAML parser (no dependency for it), so frontmatter broken outside the `aliases` key still yields aliases where the server yields none; the text order uses the root collation, as .NET's invariant culture does, which can differ in rare scripts.
- **Vaults are named as last seen online.** A copy knows its vaults by id; paths name them by name, which is per member and changes. The vault's meta keeps the name the vault list last gave (`nameVaults`, called after each contact online by work packages 8 and 9), sealed like the rest. Why: offline there is no other way from `/Work/plan.md` to a vault, and a contact carries no names (§4.6).
- **The tree comes from the inventory**, so it is whole before the texts are here; each file says whether this device holds it. A note listed without its text answers "Not on this device yet. It comes when you're back online." (an error of its own, not a 404, so the view offers no "create it" and nothing retries); an attachment's place says "Opens when you're online." A note removed here and not uploaded yet is gone from the tree and reads as a 404. No flags in the tree offline: they come from tags, which the local index (work package 6) reads.
- **Links are resolved against the whole inventory, aliases from the texts held.** One resolver per vault, built again only when a path or a version changed. Why: a link to a note whose text hasn't come still leads to it (and to "Not on this device yet"), not to "not found"; the aliases need the texts, opened once per change rather than per note shown.
- **Earlier versions and checkpoints need a connection**, as §3 says; the copy gives today's bytes only.

### 4.9 Offline writing (work package 7, built)

Decisions of 2026-11-22 while building it, each with its reason; to be confirmed in review. The core is `packages/eidolon-core/src/store/writing.ts`, the web app's side `web/src/offline/writing.ts`.

- **The editor writes into the copy, and the engine uploads.** While the host has offline writes on (from going offline until every change has uploaded, §3), `useSaveNote`, `useCreateNote`, `useCreateUntitled` and `useDeleteNote` go to the copy through the core's `NoteWriter` instead of `/api`; the engine sends them once online, as it sends an edit made in Obsidian. Why: one way a change reaches Kumbu², with the engine's conflict copies, and the screens keep their hooks.
- **The answers keep the server's shapes.** A save answers the new version, or the text there now as a `409` does; a read-only vault refuses with the `403` and its code `read-only`; a vault not kept here, or a note listed but not here yet, says it needs a connection. Why: the editor's conflict choice, the Write refusal and the error line work offline as they do online, with nothing new to learn (§3a, unified UX).
- **A save checks against what the copy stores, never against the editor's own unsaved text.** Why: the editor's draft is the very text being saved; comparing with it would never find another tab's save. Another tab's save is found this way, and offered as "changed elsewhere".
- **The open editor is the copy's draft only while offline writes are on.** The engine then reads what the user sees, so a download while uploading never replaces unsaved typing (it makes a conflict copy). Online it doesn't: the editor saves to the server, and an engine reading unsaved text would upload it beside the editor's own save, ending in a conflict with oneself. Only this tab's editors count; another tab's are covered by the version check of their saves.
- **Read-only members are refused in the core**, from the rights of the last contact (`canWrite`), so nothing reaches even the copy. The board's structure (`maintain`) isn't known offline; the server refuses it on upload and the engine lists it as refused, as for Obsidian.
- **A new note never takes a path the copy knows**, on this device or only listed, and only a note's path Kumbu² takes (relative, no empty or dot segment, `.md`). A note made offline and trashed again leaves nothing to upload. **Untitled in the board's folder needs a connection**: there a note is a card, and the board does (§3).
- **Trashing removes the note from the copy;** the engine's upload trashes it in Kumbu², which keeps the trash (§4.7). A note listed but not here can't be trashed offline: the engine has no base to delete it on.
- **Each write tells the vault's engine (`localChanged`) and the offline mode**, which counts the changes waiting again. Why: a tab's own writes don't come back to it over the copy's channel, which reaches the other tabs only.
- **Saving a conflict's text as a new note works offline** (into the copy, as a new note). **Duplicating a note stays "needs a connection"** in 0.7.0: it reads the server's text and leaves out keys a copy doesn't take, which is the server's to say.
- **Wiring left to the second of the parallel packages to land:** the offline mode (WP8) sets and clears `setOfflineWrites` with the offline source's `vaultFor` (WP5) and the engines' `localChanged` (WP9), passes `draftsFor(vault.id)` to `vault.files()`, and subscribes `onOfflineWrite` to recount the changes waiting. Before the copy locks (idle, sign-out), `flushOfflineEditors()` saves the open editors' unsaved text into it (§3a, "An offline unlock ends like a session"); a save that meets a conflict leaves the text in its editor.

### 4.10 Attachments, Keep offline and the vault choice (work package 9, built)

Decisions of 2026-11-22 while building it, each with its reason; to be confirmed in review. The code is `packages/eidolon-core/src/store/choice.ts` and `engines.ts`, the browser's side `web/src/offline/` (`engines.ts`, `turnOn.ts`, `keepOffline.ts`, `OfflineInvite.tsx`) and `web/src/routes/settings/Offline.tsx`.

- **The choice is one sealed record of the copy**: All my vaults, the vaults chosen, the budget, the folders kept offline, and when each larger attachment was last opened. Why: folder and file paths name things (§4.1), and one record changed under the copy's lock is all the tabs need to agree.
- **Turning on checks the password first** (`POST /api/session/confirm`), then makes the slot. Why: the offline wrap is made from it, and a mistyped password would leave a copy that never opens offline.
- **"Keep me signed in" is remembered as a flag in local storage** at each sign-in, no secret. Why: the invitation needs it (§3a), and the new copy's idle limit follows it.
- **Opening an attachment online keeps it.** The viewer reads from the server as today; it marks the file as opened, and the engines fetch it in the background with a full check. Why: no second read path online, and the engine stays the only one that downloads into the mirror.
- **The budget lets the least recently opened go**, with their engine paused for the moment: the bytes go and so does the engine's base, so the path is as one never downloaded and the fetch policy decides it afresh. Why: the engine keeps its state in memory, and with the base left behind a later yes would read as a deletion here and trash the file in Kumbu². Never a file changed here and not uploaded; never a small one or one kept offline; one larger than the whole budget isn't kept at all.
- **A file this device holds stays wanted** until the budget lets it go, so a newer version in Kumbu² replaces it. Why: otherwise a removed Keep offline would leave stale bytes behind.
- **Keep offline takes in what is below it**: keeping a folder drops the folders kept inside it, keeping the vault keeps all. Known gap: a kept folder renamed or moved online stays listed under its old path, and keeps nothing until kept again (moves are online only in 0.7.0).
- **A vault's size before it is chosen is its note count** from the vault list; while it comes, "1,204 of 2,000 notes"; then its notes and the size of its attachments here. Why: no new server field, and note texts are small; the attachments that weigh are the budgeted ones.
- **Unticking All my vaults keeps today's vaults chosen** one by one; unticking a vault removes its copy, asking first when changes in it haven't uploaded.
- **Removing the copy here also removes its place in the device list**; offline, the list keeps it until it is removed there.
- **A vault leaving the copy stops its engine first** (purged on a contact, a re-key among them, in another tab, or unticked in Settings): the vault's mirror is dropped, so every port of it throws from then on, and its engine is stopped before the records go; the next refresh adopts it afresh, with no bases, so it downloads rather than deletes. Why: an engine left running read the emptied mirror as every file deleted on this device, and trashed the vault's notes and attachments in Kumbu², for every member of a shared vault.
- **A missing or unopenable record is damage, never a deletion.** A path whose entry says its bytes are here, or that the engine agreed on (it has a base), and whose records are gone or don't open, makes the engine's read fail: the path is listed as not synced and left as it is in Kumbu². A deletion made on the device leaves a mark in the entry, so it still goes through. This replaces §4.7's "left out with a warning" for the engine. Why: anyone with the disk can remove records without the key, and an engine that reads the hole as "deleted here" would trash those notes in Kumbu² at the next unlock.
- **An older record put back is refused.** Every write stamps its records with a new generation and moves the vault's head record (`v/<vault>/h`, sealed) to it in the same transaction; a body opens only under the version its entry names. An entry, a state or a body of another generation than the head's reads as damage (above), never as the device's own change or deletion. A whole vault put back as it was at an earlier moment, head included, is consistent and reads as that moment: the newer texts come down again, nothing older goes up. Each adoption of the vault into the copy has its own epoch in the sealed meta, carried by the head and every record: a record of an earlier adoption never opens in a later one, and a head missing while the vault has records is damage, so the vault starts a new epoch and is synced afresh (ADR 0044). Why: records can be copied without the key, and a replayed older text or deletion mark would otherwise be uploaded over newer work in Kumbu²; a consistent earlier moment is no worse than restoring a disk backup.
- **A vault taken into the copy starts clean**: adopting it removes whatever its name holds in storage in the same write as its meta. **A header that doesn't parse is passed over**, and a new copy looks for the old one before it asks for a slot. Why: leftovers of an earlier mirror would be read as this one's, and one bad record in the clear must not stop every user of the browser from finding or making a copy.
- **The engines run in the one tab that leads the copy** (§4.7), hosted in the core (`MirrorEngines`). Another tab's choice and writes reach them through the copy's channel.

### 4.11 The mode (work package 8, built)

Decisions of 2026-11-22 while building it, each with its reason; to be confirmed in review. The code is `packages/eidolon-core/src/mode/` (the rule and its sharing across tabs), the app's side `web/src/offline/mode.ts`, `copy.ts` and `OfflineBar.tsx`.

- **The rule lives in the Eidolon core.** `ModeMachine` takes what a tab sees (a request failed or reached Kumbu² again, a socket opened or closed, the changes waiting, a lost session, a conflict copy) and says the mode. Why: any host of the core needs the same "offline only with the socket down, online only once flushed", and as a pure machine over a clock it is tested without a browser.
- **What counts as unreachable:** `fetch` failing with a `TypeError` (no answer at all), a `502`, `503` or `504` without Kumbu²'s `X-Kumbu-Version` header (a gateway with Kumbu² down behind it), and the browser's `offline` event as a hint. Not an `AbortError` (a query the app cancelled) or any other error of the app's own. Why: only these say something about the connection; a bug must not put the app into offline mode. A `503` that carries the header is Kumbu² itself saying it is busy (a call that costs seconds of CPU, a re-encryption under way): it reached Kumbu², so it counts as reached (2026-11-27).
- **A `401` proves Kumbu² reachable.** While the app is offline with changes waiting, a `401` doesn't end the session as it does today: the bar says "Sign in to upload your 3 changes" and signs in again over the app (§3a). With nothing waiting it leaves offline mode, and the usual way to the sign-in page follows. Why: a session that ended while away can't open a socket, so without this the app would wait for one forever.
- **The first mode isn't held.** The 15 seconds count from a switch; an app opened without a connection is offline at once. Why: holding the start would show "online" for 15 seconds over a dead connection.
- **"Uploaded N changes" says the most that were waiting** while offline. Why: the count falls as the queues empty, and what the person made away is the number at its height.
- **Conflict copies are shown online too**, until seen. Why: the engines keep syncing online in the background, and a conflict there asks the person to look just the same.
- **One mode for every tab through a lead tab.** The tab that holds the Web Lock `kumbu-mode` runs the machine; the others send it what they see over a BroadcastChannel and show the state it posts. A tab that starts leading asks every tab for its socket and what it saw again; what a tab sent before any tab led reaches the first one that does. Why: one decider is the only way to never show two numbers (§3a), and the lock hands over when the tab closes, as the engines' lock does (§4.7).
- **Contacts:** the copy opens with the slot's key on every page load signed in, and after the sign-in form, which wraps it again under the password (§4.2); every socket (re)connect and each `mirror` frame make contact (§4.6). The app sends the copy's slot token in `Kumbu-Mirror` on every request it knows a copy for, and a `401` with `mirror-wipe` removes the copy and goes to the sign-in page with "The offline copy on this device was removed." `onContacted` tells others (the vault names of §4.8) after each contact.
- **The sign-in form opens the copy offline** (`web/src/offline/unlock.ts`): in offline mode, or when the sign-in request gets no answer at all. A wrong password and a username this device keeps no copy for get the same sentence. Why: the form shouldn't tell a finder which usernames have a copy here. Signed in offline, the app knows only the username until the session view comes back online. A page that needs the session goes to the sign-in form while offline, unless the copy is already open.
- **Every unlocked copy has the idle lock**, opened with the password or with the slot's key (the first version armed it for an offline unlock only, and a copy opened online and then left offline stayed open without limit). When it comes due, it starts again only while the server vouches for the session: online, signed in, nothing lost. Otherwise the copy locks and the sign-in form asks. Why: online, the server's own idle limit governs and the slot would open the copy again anyway; without it, nothing else ends the key's time in memory. A use or a wake-up (`visibilitychange`, `pageshow`, `focus`) after the limit has passed unseen locks instead of starting again, because no timer runs while a laptop sleeps. Before the copy locks, `onBeforeLock` gives the editor up to three seconds to write its unsaved text into the copy. Why the limit: a save that hangs mustn't keep the copy open.
- **The copy's life in the tab follows the session's:**
  - A `401` that ends the session locks the copy, as a wipe does. The one exception is "Sign in again" with changes waiting, which §3a wants, and the idle lock bounds it.
  - The copy the tab holds is compared with the signed-in user's on every sign-in and contact. Another user's copy is locked and set aside, and their token is never handed in under a session; the one exception is the sign-in page's question after removed copies below, made while no one is signed in in the tab (ADR 0033). The comparison for a contact is made in one place every contact passes, the slot port (`signedInSlots`), against this tab's session immediately before the request; a copy that finishes opening (or being made) after its user has gone is locked, not kept, and one found gone then leaves the new user's session alone (ADR 0029). A sign-in in another tab is the server's `403` to answer (ADR 0020). Decided by Chris on 2026-11-24, because checks at each caller, before an await, let a copy still opening at a sign-out hand in its token under the next user's session (the 0.7.0 test run's F-10).
  - A sign-in in a tab that holds the user's own copy opens it afresh, so the password wrap and the idle limit are renewed.
  - A copy that replaces another locks the first.
  - A sign-out locks the copy in every tab that has it open, through the copy's channel (`SealedMirror.lockEverywhere`). Each of those tabs writes its editors into the copy first.

  Why: a key left in a tab outlives the session that opened it, and then lets someone past the sign-in page or gives the next user someone else's notes.
- **The sign-in page asks after removed copies:** online, it sends one `GET /api/session` per copy kept here, each with that copy's token in `Kumbu-Mirror`, and wipes the copies answered `mirror-wipe`. Why: a lost or returned device usually sits at the sign-in page, and the wipe signal of §4.6 was made for it. It asks for every copy kept here, whoever's it is: decided by Chris on 2026-11-25 ([ADR 0033](../../adr/0033-the-sign-in-page-asks-after-every-copy-kept-here.md)), because it is what removes a deactivated or removed account's copy from a shared browser, and the question carries no session.
- **Sign-out:** online, changes waiting get up to 30 seconds to upload. Offline, or when they still wait after that, the dialog of §3a asks. A sign-out offline leaves the mark `kumbu.signOutPending` in local storage, and `DELETE /api/session` goes out before the app first renders, when the browser reports being online again, whenever Kumbu² answers a request again (a server that was down sends no `online` event), and before any sign-in. The sign-in page says "Your session ends on the server…" only when the mark was kept. Why before a sign-in: after it, the DELETE would end the new session instead of the old one. Online, signing out locks the copy too, so the key leaves memory.

### 4.12 The wiring and the end-to-end suite (work package 10, built)

Decisions of 2026-11-23 while combining work packages 5, 7, 8 and 9 and testing them across the stack, each with its reason; to be confirmed in review. The code is `web/src/offline/host.ts`, the suite `web/e2e/offline/offline.spec.ts`.

- **One module connects the parts** ([ADR 0022](../../adr/0022-one-module-wires-the-offline-mode.md)): while the mode is offline (or uploading) with the copy unlocked, the reads go to an `OfflineSource` and the writes to the copy, and `tree`, `note`, `links`, `backlinks`, `version` and `vaults` are asked again on each switch; the engines' conflicts, lost sessions and changes waiting reach the mode; every offline write recounts the changes waiting; the open editors flush into the copy before it locks; each contact names the vaults as the list gives them, and an engine's vault on adoption. Why: the parts keep the seams they were reviewed with.
- **One `openCopy`, the mode's**, with the idle lock and the wipe; turning offline on keeps the new copy the same way. Why: a copy opened at a page load had no idle lock with the other one. Signed in offline (the session knows the username only), it does nothing: looking up the copy by an empty user id found none and locked it.
- **The engines no longer replace the mode's `onChanged`**: both hear another tab's changes. And they hear the socket's `changed` hints and reconnects.
- **A note trashed offline is marked deleted here**, so its engine trashes it in Kumbu² rather than take the missing text for damage (§4.7).
- **Queries and mutations run whatever the browser says about the network** ([ADR 0023](../../adr/0023-queries-run-whatever-the-browser-says.md)). Why: React Query held them back while `navigator.onLine` was false, so nothing reached the copy.
- **Offline with the copy unlocked, the app shows it at once**, also when the session read again fails while offline. Why: a failed refetch keeps its data but reports the error, and the app showed the error instead of the notes.
- **The browser's `offline` closes the sync socket, and a start without a network counts as a failed request** ([ADR 0024](../../adr/0024-the-browsers-offline-closes-the-socket.md)). Why: the mode waits for the socket to be down, which a dead network may not say for minutes.
- **The bar knows a copy is here before it opens** (`useOfflineCopy.stored`, read from the headers at the start): after a reload offline it said "This device keeps no copy" over the very form that opens it.
- **The suite** runs each promise of §3a it can reach from a browser in Chromium and Firefox: turning on, reading after a reload, an edit, a new note and a trash uploading, a conflict copy, the idle lock with the editor's unsaved text, signing out with changes waiting, the wipe, and attachments by size. Not in WebKit: it sends no `Secure` cookie to `http://localhost`, so the suite's plain-HTTP stack can't sign it in; Safari waits for a hand test on a device.

## 5. Browser storage

- **IndexedDB only**, attachment bytes as Blobs in it. OPFS would be faster for big files, but one store is simpler, and on-demand fetching keeps the mirror small. OPFS stays an option if measurement says so.
- **`navigator.storage.persist()`** on turning offline on, `estimate()` shown in Settings, Offline. Chrome grants it to installed apps and engaged sites; Firefox asks the user; Safari evicts script-written storage of sites not added to the home screen after seven days without use, home-screen apps are exempt (behaviour as I know it, to verify on current Safari). So the install hint (`web/src/update/InstallHint.tsx`) gains a line for Safari users with offline on.
- **Eviction loses only unsynced changes**, everything else is re-synced. Hence: upload as soon as possible, and keep "N changes waiting" in sight.
- **Schema migrations:** the mirror is a rebuildable cache, so a new schema drops it and syncs again, **except** paths whose local version differs from their base (the unsynced changes), which a migration carries over. Only that part ever needs migration code.

## 6. Attachments on demand and "Keep offline"

- The version list always brings every attachment's path, kind, size and version (it already does with `attachments=1`).
- Bytes: images and PDFs up to 1 MB are fetched with the notes; larger ones when opened online, then kept in a budget (LRU, default 500 MB, in Settings).
- **Keep offline** per vault or folder (per device): fetches every attachment below it, whatever its size, up to the engine's 15 MB limit.
- **Which vaults are mirrored:** a per-device choice in Settings, Offline, per vault, with its size; "all my vaults" as one switch. Discreet vaults aren't listed there while hidden.

## 7. Embeddings

- **Offline: words search only**, in the browser, over the decrypted mirror, in memory. Decided already on 2026-11-13; it holds.
- **A plain scan, not MiniSearch** ([ADR 0031](../../adr/0031-offline-words-search-scans-the-copy-in-memory.md), accepted by Chris on 2026-11-25). Decided by Chris on 2026-11-25 to have words search offline in 0.7.0 ("a little search has to work offline too"); done as a scan of the copy's texts in the Eidolon core (`read/search.ts`), because for the few thousand notes of a vault it answers within the search sheet's debounce, it needs no new dependency days before the release, and it finds the same sections, addresses and snippets as the server's words search, so a result opens the same place whichever way it was found. A section matches when it holds every word of the query, or the whole query in a line, ignoring case; title hits come first, then by how often the query's words occur; the tag filter and the archive rule are the server's. Each note's text is opened at the first search after unlocking and again only once its version changed. No fuzzy or prefix matching; an index can replace the scan later behind the same call.
- **Offline writes need no embedding queue of their own:** the engine uploads them as ordinary writes, and the server's incremental indexing embeds them as it does every write.
- **TensorX from the client: not for 0.7.0.** Why:
  - Offline usually means no network at all, so TensorX is out of reach as well. It would only help while the Kumbu² server is down and the internet is up.
  - Searching by meaning in the browser also needs the vault's vectors there: about 4 KB per section at 1,024 floats, so 80 MB for 20,000 sections (20 MB as int8), all derived from the plaintext and to be sealed too.
  - Short-lived scoped tokens need TensorX to issue them; I found nothing that says it does. A server that mints them is a proxy again, which is down in exactly the case this is for.
  - BYOK in the browser would work (the key sealed under the mirror key, sent only from the client), if TensorX answers CORS preflights. **Unverified:** this sandbox's proxy doesn't reach `api.tensorx.ai`. One check from your machine settles it:
    ```sh
    curl -si -X OPTIONS https://api.tensorx.ai/v1/embeddings -H "Origin: https://example.org" \
      -H "Access-Control-Request-Method: POST" -H "Access-Control-Request-Headers: authorization,content-type" | head -20
    ```
  - Better fit later: Chatsundere's local embedding engine once Eidolon is a library (1.0.0).

## 8. Work packages

Sizes: S up to a day, M two to three days, L about a week, for one agent track including tests and review.

| # | Package | Size | Needs | Repo |
|---|---|---|---|---|
| 0 | UX review and spec: the offline mode's bar, Settings, Offline, the sign-in-again flow, "needs a connection" states | S | nothing | kumbukumbu |
| 1 | Eidolon core package: workspace, boundary lint, the engine moved in with its tests, the fetch-policy port and multi-vault host; the plugin vendors the sync part | M | nothing | both |
| 2 | Service worker for the app shell, tied into the update flow (new release: bar, reload, the worker updates with it) | M | 0 | kumbukumbu |
| 3 | Mirror slot endpoints and wipe signals on the server (codes on `401`/`403`, a socket frame), with isolation tests | M | 0 | kumbukumbu |
| 4 | Sealed mirror store: IndexedDB schema, mirror key, both wraps, PBKDF2, `LocalFiles` and `StateStore` adapters, persist and quota | L | 1, 3 | kumbukumbu |
| 5 | Offline reading: `OfflineSource` for tree and note, local link resolution (Obsidian's rules, a TS port of the server's), embeds, attachments from the mirror | L | 4 | kumbukumbu |
| 6 | Local index and tools: backlinks, tags, words search, the VAFS-shaped tools over the mirror (§2a) | M | 4 | kumbukumbu |
| 7 | Offline writing: editor as `LocalFiles`, create and trash, read-only members, "changes waiting", conflict copies shown | M | 4, 5 | kumbukumbu |
| 8 | Mode detection, the bar, switching back only after flushing | M | 0, 5 | kumbukumbu |
| 9 | Attachments on demand, Keep offline, vault choice | M | 4 | kumbukumbu |
| 10 | Playwright e2e with `context.setOffline`, the engine's race tests against the browser adapters, security architecture update, Mythos audit | M | all | kumbukumbu, security |

**Order:** 0, 1, 2 and 3 start at once. 4 is the narrow point. Then 5, 6, 7, 8 and 9 run in parallel, 10 throughout and at the end.

**Realistic time:** with parallel agent tracks the coding fits in about a week; your UX reviews (one by one, per CLAUDE.md), the hand-test list and the audit are the bottleneck, so I'd plan about two weeks from the start, not one.

## 9. Graph view and link prediction alongside

- **Graph view out of preview: realistic as a parallel track.** It sits on the finished graph backend (`/api/graph/*`) and lives in the note view's graph column and dialog (#274, #276), far from the data layer offline changes. It is "needs a connection" offline. Take the open fix "link graph memory" (L6-27, in the backlog) with it. Size: M. The cost is your review time, not code conflicts.
- **Link prediction: I'd leave it for 0.8.0.** It needs its own spec and UX review (suggestions, the feedback loop, the hub trap), a server part over embeddings that differs between internal and BYOK vaults, and its UI rides on the graph view leaving preview first. Running it as a third track would compete for exactly the bottleneck above. If you want a piece of it anyway: the server side as a `sh` command (`backlinks --predicted` or similar) is cleanly separable, S to M.

## 10. Risks

| Risk | Mitigation |
|---|---|
| Lost offline changes (Safari eviction, sign-out, purge) | Upload at once; "N changes waiting" always visible; warn before sign-out and in the device list before removing a copy |
| Races between the online UI and the mirror around reconnect | Stay in offline mode until the queues are empty; one engine per vault, as in the plugin |
| The service worker serving an old app forever | Precache by content hash, the worker updates with each release through the existing update bar; tested in e2e |
| A weak password guessed against a copied disk | Argon2id, 64 MiB (§4.2); stated in the security document |
| Two copies of the engine drifting | One package, the plugin vendors a tagged copy with a CI check |
| Scope creep into "everything offline" | The table in §3 is the scope; anything else says "needs a connection" |

## 11. Branches until 0.6.0 is out

- **`release/0.7.0`**, cut from `master` on 2026-11-22, takes every pull request of this work, squash-merged as usual. CI runs on pull requests into it; the image build runs only for `master`, so it first runs on the final merge.
- **`master` stays 0.6.0's** until its release; its fixes go there as today. `master` is merged into `release/0.7.0` after each such fix (a merge, not a rebase), so the two never drift far apart.
- **At 0.6.0's release:** the tag goes on `master`, `maintenance/0.6` is cut from it (step 4 of Releasing), then `release/0.7.0` comes into `master` with one pull request, merged with a merge commit rather than squashed, so its pull requests stay readable in the history. Why not rename the branches: `master` keeps its protection rules, required checks and the image workflow's triggers, and nothing that points at it breaks.

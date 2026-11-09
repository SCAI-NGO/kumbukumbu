# Discreet vaults: out of sight in the web app until a PIN shows them

Date: 2026-11-09. Status: approved by the maintainer on 2026-11-09, known limits (§6) included. Target: 0.4.0 (release planned for Sunday 2026-11-15).

## 1. Why

Requested by Kumbu²'s Discord community. A user wants to take Kumbu² everywhere (a family gathering, work, the phone on the train) without anything on the screen giving them away, and even a vault's name can give a person away. Today every vault is listed in the sidebar, the search, the console and the Access screen.

The maintainer compares it to the SFW mode of another product of his, but Kumbu² is the serious product line, so it should look presentable. The name is **discreet** ("neutral and a little bashful", in the maintainer's words). "Private" was ruled out: everything in Kumbu² is private.

This is about the **web app only**. Agents, sync and WebDAV are already controlled per vault through keys, connections and shares, and they stay exactly as they are.

## 2. Goals, non-goals, anti-goals

**Goals**

- A user can mark any vault they can open as **discreet**: their own vaults, and vaults others share with them. The mark belongs to the user and the vault and holds on every device.
- In the web app, a discreet vault **does not exist** while it is hidden: no name, no content, no trace, anywhere (§3.4).
- **More vaults…** at the end of the vault menu asks for a **PIN** (4 to 6 digits, separate from the password). The right PIN shows the discreet vaults until a chosen time runs out, or for good. The time choices are 5, 10, 30 and 60 minutes, and never. The last choice is remembered **per device**.
- While discreet vaults are shown, an **amber frame and pill** say so, and ✕ on the pill hides them at once.
- When they are hidden again (time out or ✕), everything closes: drafts are saved, discreet tabs close, and the view returns to the last ordinary vault.
- **Plausible deniability, soft:** a wrong PIN, no PIN set, and no discreet vaults all get the same answer, "You have no discreet vaults." Someone curious can't tell whether anything is hidden.

**Non-goals**

- **No change to sync, agents, MCP, `sh` over MCP, WebDAV, OAuth, API keys, device keys or connections** (maintainer, explicitly). They keep reading and writing discreet vaults as their grants allow. Only the web app hides them, including its console.
- The **kill switch** (a second PIN that destroys the discreet vaults) is a 0.5.0 flagship feature with its own design (maintainer, 2026-11-09). This design only keeps room for it (§3.9).
- Encryption. The PIN only gates the web app's view and never unlocks or seals anything (§3.2).
- Hiding from an admin's counts, from the server's operator, or from someone with the database (§6).

**Anti-goals** (failure even if it technically works)

- A hidden discreet vault's name or content reaching the browser in any response, frame, URL, title or storage: the server must hide it, not just the client (§3.3).
- Any observable difference between "wrong PIN", "no PIN" and "no discreet vaults" in the PIN dialog's answer, status code, response time or rate limit (§3.6).
- A change in what an agent, a synced device or a WebDAV client can do.
- Losing an unsaved edit when discreet vaults are hidden again.

## 3. Decisions taken

### 3.1 Marking (maintainer)

- The mark is **per user and vault**, set in the web app, and it holds on all of the user's devices. A member can mark a vault shared with her as discreet for herself; the owner and other members see no difference (maintainer: "das ist super sogar").
- **Marking happens only in discreet mode** (maintainer, 2026-11-09, after the builder check). A dialog that asks "set a PIN" in one case and "enter your PIN" in the other would tell a curious person that a PIN exists, and so that something is hidden. A switch without any check would let them mark a vault and reveal everything. So:
  - Settings has a section **Discreet vaults** that looks the same for everyone: **Set or change PIN**, after the password, as other sensitive settings already are.
  - The **Discreet** switch per vault exists only while discreet mode is on.
  - **The right PIN always starts discreet mode**, even without any discreet vaults. Only someone who knows the PIN can tell it was right (§3.6).
  - Setting up for the first time: set a PIN in Settings, open More vaults…, enter the PIN, mark the vaults.
- A vault marked during the mode stays shown until the mode ends. Unmarking also happens in the mode.
- The system vault **Skills** (`VaultKinds.Skills`) can't be marked: the Skills screen and the server's automatic creation of that vault depend on it (*Claude's call*).

### 3.2 A separate PIN (maintainer)

- 4 to 6 digits, separate from the password. The maintainer's reason: with the password, the kill switch later would run into the password-manager problem, since a password manager fills in the one stored password. A PIN is also quicker to type at a family table.
- A forgotten PIN is replaced in Settings after the password.
- The PIN is stored only as a verifier, a slow hash with the same KDF the server uses for passwords (*Claude's call*). It never derives a key and never decrypts anything. A 4-digit PIN would be weak protection for data. It is a gate to a view, and the server enforces it (§3.3, §3.6).

### 3.3 The server hides, not the browser (Claude's call, needed for the anti-goal)

- The **reveal** is server state on the web session, modelled on the remembered session's password window (`Sessions.cs`, `ConfirmedAt`/`WindowUsedAt`): the session slot records until when discreet vaults are shown (or "until the mode ends" for *never*).
- While a session hasn't revealed them, the session's credential leaves discreet vaults out. Every web endpoint that reads vaults through it then behaves as if they don't exist: `UserCredential.OpenAsync` (`Vaults/Credentials.cs:30-44`), the only path web sessions take to their vaults.
- **Only web sessions are filtered.** API keys, connections, device keys and WebDAV shares have their own credential classes (`Credentials.cs:85,115,135`) and stay untouched.
- Sign-out ends the reveal with the session. A new session starts hidden.

### 3.4 Where a hidden vault does not exist

Everything the web app shows. Through the filtered credential:

- the vault list and menu
- tree, notes, attachments, gallery, board, checkpoints
- search, earlier search, tags
- the agent banner of recent writes (`/api/changes`)
- trash, activity, history
- the **web console** (`POST /api/console` runs the same `Shell`; `ls /`, `rg`, `semsearch`, `history`, `df`, `du`, `publish ls`, `skills` all follow the vault list)
- publications
- skills
- the Access screen's keys, shares and connections
- the WebSocket ticket

These sources don't go through the store and need their own filter:

- **Member notices** (`/api/member-notices`) and **vault events** (`/api/vault-events`) about a hidden vault.
- **The session's Skills notice** (`SessionView.SkillsNotice`), when it names a hidden vault.
- **Keys, shares and connections** whose grants cover only hidden vaults: they are not listed at all, rather than listed with an empty vault list. One that covers both kinds is listed with the ordinary vaults only.
- **`df` in the console:** it compares the owner's vaults with the listed ones (`ShellStorageFigures.cs:25`), and filtering would make it say that more vaults count against the limit. It counts against the visible vaults instead. The storage total stays the true total: a total names no vault, and the quota is real (maintainer: "nur Gesamtsumme").
- **Name clashes:** creating or renaming a vault, or renaming a shared vault's alias, to a hidden vault's name is refused with a neutral "This name can't be used here", and the hidden vault isn't named (§6).
- **The WebSocket:** hiding again recomputes or closes the session's sockets (`SyncHub.RefreshAsync`, `MembershipChangedAsync`), so no `changed`, `renamed` or `presence` frame for a hidden vault reaches the browser.

### 3.5 Address bar, title and browser storage (maintainer: "the discreet mode is a state of exception")

The browser's history can't be cleaned, so discreet paths must never get into it.

- Inside a discreet vault, the URL stays neutral. Routes that carry a vault (`/notes/:vault/*`, `/board/:vault`, `/gallery/:vault`, `/view/:vault/*`, `/checkpoints/:vault/…`, `web/src/router.tsx:118-124`) are replaced with a URL without the vault or path (`history.replaceState`).
- A session ending in a discreet vault never puts the path into `/signin?next=` (`router.tsx:42`).
- The tab title stays "Kumbu²". Today it never changes (`web/index.html:6`), and it must stay so.
- Nothing from a discreet vault goes to browser storage. Today nothing with a vault name does: the editor store is in memory only (`web/src/state/editor.ts:20-21`). It stays that way.
- **The price, accepted by the maintainer:** a discreet note can't be bookmarked or shared by URL.

### 3.6 The PIN dialog and its single answer (maintainer)

- **More vaults…** is the last entry of the vault menu, **always**, whether or not the user has discreet vaults or a PIN. Its presence gives nothing away. It is quieter than a button in the sidebar (maintainer's choice, mockup 1C).
- The dialog: PIN digits, then **Hide again after** with 5 min, 10 min, 30 min, 60 min and Never, preselected with this device's last choice, and **Open** (mockup 3A).
- **The right PIN:** discreet mode starts for the chosen time, with the frame and the pill, and shows the discreet vaults if there are any (§3.1).
- **A wrong PIN, or no PIN set** (with or without discreet vaults): always the same answer, "You have no discreet vaults." It comes with the same status, the same shape of response and the same timing (the server runs the hash even when no PIN is set), so the cases are indistinguishable. The maintainer: "soft plausible deniability". A user who mistypes simply tries again.
- **Guessing:** 4 digits are only 10,000 possibilities. PIN checks are rate-limited per account, like password checks (`AccountLimits`, `Security.cs:392`), and the limit applies to every account in the same way, with or without a PIN. Over the limit, the answer is "Too many attempts; try again in a minute", for everyone alike.

- **Every PIN entry counts** (maintainer, 2026-11-09): right or wrong, every entry in the PIN dialog counts against the session, and the **tenth ends the session**, so the password is needed again. A right PIN doesn't reset the count. This stops a stolen session cookie from trying all 10,000 PINs within a remembered session's 30 days. It applies to every account alike, with or without a PIN.
- **Setting or changing the PIN asks for the current password every time**, in the request itself, as the export ticket does (`ExportApi.cs:116-124`). The password window of a session is not enough: a normal session's window is always open.

### 3.7 While discreet vaults are shown (maintainer, mockup 2C)

- An amber frame around the sidebar, and a pill **👁 Discreet · 9 min · ✕** (the remaining time, or no time for Never) where the menu sits. ✕ hides them at once.
- On a phone, the same frame and pill in the mobile layout (*Claude's call*).
- The time runs on the server. Tabs show it and act when it ends.

### 3.8 When they are hidden again (maintainer: "alles dicht, alles zu")

By time-out or by ✕:

- Drafts are saved first.
- Every tab of the session closes its discreet tabs, drops their data from the in-memory caches (the query cache, the editor store: `forgetVault`, `editor.ts:89`), and returns to the last ordinary vault.
- Other tabs of the same session hear it over their socket. Another device has its own session and its own state.
- **Saving before the end** (maintainer, 2026-11-09): a tab saves an open discreet note shortly before the time runs out. If the device slept through the end, the server still accepts **one** save of the note that was open, from that session, up to **5 minutes** after the end; after that, nothing.
- **The socket:** the server drops the discreet vaults from the session's sockets at the end, by ✕, time-out or the hub's regular check. It never relies on the browser to stop listening.

### 3.9 Room for the kill switch in 0.5.0

The single answer of §3.6, the menu entry that is always there, and a PIN verifier on the user document are what the kill switch will need: a second, alternative PIN entered in the same dialog. Nothing here may depend on the PIN being the only one.

### 3.10 Example, end to end

Alice has the vaults `Work`, `Garten` and `Tagebuch`, and Bob shares `Familie` with her.

1. In Settings → Discreet vaults she chooses Set or change PIN, confirms her password and sets `4711`.
2. She opens More vaults…, enters `4711` and chooses Never. Discreet mode starts with the amber frame and the pill "Discreet · ✕", though she has no discreet vaults yet. In Settings she now sees a Discreet switch for each vault, Skills excepted. She switches on `Tagebuch` and `Familie`; Bob notices nothing.
3. She clicks ✕. `Tagebuch` and `Familie` vanish from the menu, the tree, the search, the console (`ls /` lists `Work` and `Garten`) and the Access screen. A key of hers for `Tagebuch` alone is no longer listed. Her agent's key still writes to `Tagebuch` over MCP.
4. At her family's, her brother opens More vaults… and types `1234`: "You have no discreet vaults."
5. On the train she opens More vaults…, enters `4711` and keeps "30 min", this phone's last choice. The vaults are back with the frame and "Discreet · 30 min · ✕". She opens a note in `Tagebuch`: the address bar shows no vault and no path.
6. Twenty minutes later she types in that note. At thirty minutes the note is saved, its tab closes, and the app shows `Work`.

## 4. Claude's calls (the maintainer can override them)

Added after the independent review of 2026-11-09:

- **Key rotation:** the sealed list of discreet vaults is resealed with the user key wherever that key changes: re-encrypting her vaults (`UserKeyRotation`) and account recovery (`UserCatalog`). Otherwise it would become unreadable, and every hidden vault would reappear. This is a narrow change to the rotation, inside its existing transaction.
- **The list keeps ids of vaults that are gone** (deleted, left, revoked), so notices and events about them stay filtered. Marks from two tabs at once use a conditional update.
- **Neutral URL, strictly:**
  - no discreet path in the router's state or `history.state`, and no discreet path in any link's `href`, so that a middle click can't put it into the history;
  - the open discreet view lives in memory;
  - reloading `/discreet` shows the vault list.
- **Invitations:** joining a shared vault whose name clashes with a hidden vault answers neutrally, like creating or renaming (§3.4).
- **Export file name:** an export of a discreet vault gets a neutral file name, so that the browser's downloads list doesn't name it. The web app exports vault by vault; there is no "all vaults" export to filter.
- **The OAuth consent page for a connection that covers only hidden vaults** shows the connection's own name with no vaults; its grants stay as they are (§6).


- **Where the mark lives:** a sealed list of vault ids on the user document (precedent: `UserDocument.SkillsNotice`), not a clear field on the vault slots, so the database doesn't show which vaults are discreet. The PIN verifier sits on the user document too.
- **The menu entry's English text:** "More vaults…". The dialog says "You have no discreet vaults." The setting is "Discreet vaults", and a vault's switch is "Discreet".
- **Per-device time choice:** in `localStorage` (`kumbu.discreetTimeout`). It holds a number, no vault name.
- **Neutral URL:** inside a discreet vault, the URL is `/discreet` without anything after it. The app keeps the open note in memory.
- **The OAuth consent page** (`ConnectionApi.cs:173`, `Consent.tsx:135`) leaves out hidden vaults. A connection's existing grants on them stay as they are and are neither dropped nor renewed differently.
- **Editing a key, share or connection while hidden keeps its grants on hidden vaults.** The server merges the edit into the stored grants, so that an agent never silently loses access. Revoking revokes the whole item, as today.
- **The export** (`GET /api/export` without a vault, `ExportApi.cs:50`) follows the filter: while hidden, a full export leaves the discreet vaults out without naming them, and its `MISSING.txt` and logs say nothing of them. The README says that a full backup includes discreet vaults only while they are shown. (This moves the export from "not touched" to "filtered like every web endpoint".)
- **An admin's counts:** `GET /api/admin/users` counts vaults per user (`AccountApi.cs:118-120`). An admin's own row includes her discreet vaults. The counts name nothing, so they stay as they are (§6).

## 5. Security boundary

**Touched: sessions and authentication, narrowly.** A security review of the whole change applies. It covers:

- new fields on the session slot (the reveal and its expiry)
- a PIN verifier and a sealed list on the user document
- a new rate-limited PIN check
- a filter in the web session's credential and in the sources of §3.4

**Not touched:** API keys, device keys, connections, OAuth, WebDAV shares and their credential classes, MCP and `sh` over MCP, sync, encryption and key slots, sharing and roles, publishing's public pages, the Obsidian plugin. OAuth's consent page and the edit paths of keys, shares and connections change only their display and keep stored grants (§4).

## 6. Known limits (stated in the README)

- **What it doesn't hide:**
  - Discreet vaults are hidden from the web app's screen, not from the server's operator or the database. The PIN gates a view; it encrypts nothing.
  - The storage total and an admin's vault count include discreet vaults; neither names one.
- **Name guessing:** someone who guesses a hidden vault's exact name and tries to create it gets "This name can't be used here", so a hidden name can be confirmed by guessing.
- **A connection covering only hidden vaults** still shows its own name on the OAuth consent page when it renews.
- **Agents and synced devices see discreet vaults as before.** Whoever gets hold of a synced Obsidian vault or a configured agent sees what those see.

## 7. Automated tests

The maintainer asked for this feature to be tested end to end automatically.

- **Server:**
  - for every endpoint and source in §3.4, a hidden discreet vault is absent, and present once revealed;
  - keys, shares and connections that cover only hidden vaults are not listed;
  - `df` doesn't change its sentence;
  - the console's `ls /`, `rg`, `semsearch` and `history` don't show the vault;
  - a socket gets no frame for a hidden vault and is refreshed on hiding;
  - MCP, an API key, a device key and a WebDAV share still read and write the vault while it is hidden.
- **Indistinguishable:** wrong PIN, no PIN and no discreet vaults give byte-identical responses with the same status, and the same rate limit with and without a PIN.
- **Reveal and expiry:** shown for the chosen time with a fake clock, hidden after it; Never lasts until ✕ or sign-out; a new session starts hidden.
- **Web:**
  - "More vaults…" is always in the menu;
  - the dialog preselects the device's choice;
  - frame and pill with the remaining time;
  - ✕ and time-out save the draft, close discreet tabs and switch to an ordinary vault;
  - the URL is neutral inside a discreet vault;
  - nothing from it reaches `localStorage` or `sessionStorage`.

## 8. Left to the builder

- How the reveal is passed from `Sessions.ResolveAsync` through `RequestCaller.ForSession` into `UserCredential`.
- How tabs learn about a time-out: a socket frame from the server, a client timer, or both.
- Visual details of the frame and pill within the app's existing colours, in light and dark mode, at phone width.

# Overnighter report: discreet vaults

Run of 2026-11-09 on `feature/discreet-vaults`. Draft pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/179 (base `master`, depends on #176). Brief: [2026-11-09-discreet-vaults-brief.md](2026-11-09-discreet-vaults-brief.md). Plan: [2026-11-09-discreet-vaults.md](../plans/2026-11-09-discreet-vaults.md).

Every task of the plan is done. Nothing was merged, tagged or deployed, and the version is unchanged.

## 1. Environment

- Cloud container, 4 cores, 15 GB RAM. .NET SDK 10.0.403 was already there.
- **Installed:**
  - Node 26.13.0 from nodejs.org, checksum verified, into `/opt/node26`. The image had Node 22.
  - pnpm 10.32.1 (`npm install -g`).
  - `litmus` 0.13 from apt, for the WebDAV job.
- **Docker** wasn't running, so `dockerd` was started by hand.
- **MongoDB** ran as its own `mongo:7.0` container, a replica set, for every run.
  - The sandbox's hard limit on open files is **20000**, and it can't be raised: `ulimit` refuses, and the container refuses higher values.
  - The first full run therefore crashed mongod ("Too many open files", WiredTiger panic), giving 86 failures, all `MongoConnectionException`.
  - The fix was to have WiredTiger close idle handles: `--wiredTigerEngineConfigString "file_manager=(close_idle_time=15,close_scan_interval=5,close_handle_minimum=250)"`. That kept the peak at about 6300 open files, and every later full run was clean. **Worth adopting** wherever the test MongoDB meets a file limit.
- `TESTCONTAINERS_RYUK_DISABLED=true` and `KUMBU_TEST_MONGO` pointed at the container, as the brief describes.
- **Superpowers skills were available.** I followed subagent-driven development:
  - a fresh implementer per task (red, green, commit, every mutation);
  - then a spec-compliance reviewer and a code-quality reviewer, each fresh, run in parallel;
  - then a fresh fixer for their findings.
  - Implementers of tasks that touch different files ran in parallel. Each staged only its own files. I pushed after every commit.
- The phase gates ran in a separate `git worktree`, so that the other agents' work in progress couldn't affect them.

## 2. The plan and its review

- The plan is in [`docs/superpowers/plans/2026-11-09-discreet-vaults.md`](../plans/2026-11-09-discreet-vaults.md). It was written after a survey by a subagent of every web endpoint that reaches vaults without going through the session's credential. The survey found 20 bypasses: 7 high, 8 medium and 5 low.
- A fresh reviewer read the brief, the spec and the draft plan against the code, including the React Router source. It made **19 findings, all accepted** as rulings at the top of the plan. The main ones:
  - **Hiding again:** clear every cache (query cache, console scrollback, presence), not only keys naming the vault (ruling 1).
  - **`df`:** its total must be the true total, which needed a `Total` in `OwnFigures` (ruling 2).
  - **The tenth-entry test** can't run against the real rate limit (ruling 3).
  - **A mark racing a key rotation** could seal the list under the old key and hide every vault. Writes are now conditional on `KeyId` (ruling 7).
  - **The socket design was replaced:** sockets close with 4412 and reconnect with a fresh ticket instead of carrying a per-vault flag (ruling 8).
  - **Phase 1 alone wasn't safe** with the lists and the socket in phase 2, so both moved into phase 1 (ruling 9).
  - **The late save could lose an edit** on a failed write, so the marker is put back (ruling 10). The security review later limited this (§5).
  - **Neutral URL specifics for React Router 6.30:** `pushState` must rewrite the URL itself, `usr` state carries vault paths, and the auto-update reload has to wait (ruling 12).
  - **Order of checks** for delete and rekey (ruling 13), and **export names** in the existing form (ruling 16).

## 3. Task 0: baseline

| | Planned (brief) | Measured at `4c88031` |
|---|---|---|
| `dotnet restore --locked-mode`, `build -c Release` | — | restore ok, build 0 errors |
| `dotnet test -c Release` | total 5333, skipped 4, 2 failures fixed by `a4c899b` | total 5333, **failed 0**, succeeded 5327, skipped 6 (17 min 27 s) |
| web | green | 115 files, 1863 tests; build ok |
| pdf | green | 9 files, 198 tests; build ok |

Skipped is 6, not 4: the model and TensorX tests, plus two that skip when the tests run as root, as earlier runs noted.

## 4. Tasks, commits and test output

Each task got a red run first, then green, then every listed mutation applied alone (each one failing the named test and then restored), then two reviews and a fix-up. All counts below are as printed by `dotnet test` or `pnpm test`.

### Phase 1: the server (safe on its own)

**Task 1: sealed list and PIN verifier** (`866dcba`, fixes `ec3cf52`)
- Red: build error CS0246. Green: `*DiscreetVaultsTests` total 18, failed 0, then 22 after the fixes, run three times.
- Mutations, all failing:
  - (a) skip the KDF without a PIN;
  - (b) dummy salt for a set PIN;
  - (c) unconditional write;
  - (d) an unreadable list counted as empty;
  - `KeyId` dropped from the write filter;
  - the fixes' mutations: `Attempts = 1`, an unconditional write in the forced race, the missing-user throw dropped, `[BsonIgnoreExtraElements]` dropped.
- Review fixes:
  - the failure type is logged;
  - `allIds` leaves out Skills;
  - a dedicated test that gives up after 3 attempts;
  - a forced race;
  - `PinSentence` made public;
  - the verifier can carry later fields.

**Task 2: the reveal on the session slot** (`be03ff2`, `3b78088`, fixes `f6d17d4`)
- Red: CS0246. Green: Reveal, Session and RememberedSession total 67, then 74; PairingTests 55.
- Mutations, all failing:
  - (a) `<=` instead of `<`;
  - (b) a limit of 11;
  - (c) a reveal resets the count;
  - (d) hide unsets only the end;
  - the fixes' mutations: ending the session decided by `DeletedCount` (caught by the concurrent test), `RevealAsync` ignoring `MatchedCount`, the idle bound dropped, the millisecond truncation dropped.
- Review fixes:
  - `RevealAsync` answers whether it wrote;
  - every entry from the tenth on reports the session ended;
  - `ShownUntil` versus the raw `RevealUntil`;
  - a concurrency test with 15 parallel entries.

**Task 3: the credential filter** (`2d99b09`, fixes `7a15872`, `d5157c9`)
- Red: 35 of 39 failed. Green: 39 of 39. Every Api, Sharing, Accounts, Auth and Discreet class together: 1575 of 1575. Publishing, Skills, Welcome and others: 644 of 644.
- Mutations, all failing:
  - (a) no filter;
  - (b) `Revealed` ignored;
  - (c) an unreadable list counted as empty;
  - (d) no check in `OwnVaultIdAsync`;
  - (e) no hidden ids passed to `AccessAsync`;
  - (f) filtering API keys too (simulated by dropping the vault by name in `ApiKeyCredential`): the MCP and device tests fail;
  - after the fixes, dropping the filter fails 10 of 11 `HiddenVaultTests`.
- Review fixes:
  - one immutable snapshot in the credential;
  - the unreadable-list warning logged once per user;
  - hidden ids read before the `try` that drops pairings;
  - positive controls for every console command;
  - tests for pairing, rename, embeddings, upload, restore, export, the files ZIP and the WebSocket ticket.

**Task 4: keep hidden grants on a connection edit** (`117be7d`, fixes `c55359e`)
- Red: 2 of 4 failed. Green: 4 of 4; ConnectionApi and Connection 44.
- Mutations, all failing: (a) `keep` empty; (b) "at least one vault" judged on the edit alone; the no-op branch disabled; the empty edit of an unknown id; the edit's right winning over a kept vault.
- Review fixes:
  - an edit that changes nothing writes nothing and revokes no token (decision 9 in §6);
  - an empty edit of an unknown id answers 400 as before.

**Task 5: PIN, reveal, hide and mark endpoints** (`bc1facf`, fixes `bd984e0`)
- Red: CS0246 `DiscreetView`. Green:
  - DiscreetApi 14, then 16;
  - AccountApi, RememberedSession, DiscreetApi and HiddenVault together 117;
  - PasswordWindow 70;
  - web 1863.
- Mutations, all failing:
  - (a) "You have no PIN." for a missing PIN;
  - (b) the KDF skipped for a malformed PIN;
  - (c) only users with a PIN rate-limited;
  - (d) only wrong PINs counted;
  - (e) marking allowed while hidden;
  - the fixes' mutations: counting before the limit, the limit keyed by session.
- Review fixes:
  - a dedicated `DiscreetListConflictException` answered 409;
  - the rotation case left to `ApiErrors`;
  - headers compared in the identity and rate-limit tests;
  - the limit counts across sessions;
  - a refused entry doesn't count.

**Task 6: reseal on rotation and recovery** (`3000f1b`, fixes `232aab1`)
- Red: 3 of 44 failed. Green: 44 of 44, then DiscreetRotation 7, UserKeyRotation 40, Rekeying and UserCatalog 40.
- Mutations, all failing: (a) no reseal; (b) an unconditional apply; the no-list conflict branch deleted.
- Review fixes:
  - a test for a first mark during a rotation;
  - an unreadable list survives a rotation as it is;
  - the conflict test runs for both re-encryption and recovery.

**Task 8: access lists, consent, export names, Skills' `without`** (`be1e6d8`, fixes `c49a841`)
- Red: 2 of 5 failed. Green: 6, then 9; ApiKey, DavShareApi, ConnectionApi, ExportApi and SkillsApi together 123.
- Mutations, all failing:
  - (a) hidden-only keys listed;
  - (b) the vault named in the export;
  - (c) no consent filter;
  - (d) no filter on Skills' `without`;
  - the fixes' mutations: the old "all hidden" rule, `lostVaults` unfiltered, the hidden check removed from find and from instructions, the real id passed instead of `NoId`.
- Review fixes:
  - items are judged by visible vaults, so a hidden vault plus a deleted one is also left out;
  - ids of hidden-only items answer as unknown on edit and find.

**Task 10: the socket** (`2498ca4`, fixes `cccf93b`, `c5c6246`)
- Red: CS0117 `SyncHub.Hidden`. Green:
  - DiscreetSocket 7, then 10 (three runs);
  - SyncSocket 36, SyncHubReview 11, Presence 7, SyncApi 20;
  - web hook 15, then 17.
- Mutations, all failing:
  - (a) no `hidden` frame;
  - (b) the check never closes;
  - (c) a mark closes key sockets;
  - 4412 handled with back-off;
  - frames without a vault ignored;
  - the fixes' mutations: no mark check in `RunAsync`, `<=` changed to `<`, no close on a renewed reveal, no timer re-arm, no `held` shrink, no session invalidation on 4412, no `wasOpen` check, unsubscribe doing nothing.
- Review fixes:
  - a ticket issued before a mark and used after it closes;
  - a renewed reveal closes the revealed sockets and the timer re-arms;
  - **a web blocker:** after a 4412 the tab never got the discreet vaults back, because its `held` set never shrank;
  - an immediate reconnect only after an open socket, at most once in 5 s.

**Phase 1 gate** (server, worktree at `a05a76e`):
- Build: 0 warnings, 0 errors.
- Tests: total 5469, failed 6, succeeded 5457, skipped 6.
- All six failures were `CallerTests`, a **regression**: the late save had given `CallerMiddleware` a `TimeProvider` that the test's minimal host doesn't register. It was fixed in `56ffaf4` (the middleware reads the clock from `Sessions`). `CallerTests` then passed 25 of 25.

### Phase 2: the other sources

**Task 7: notices, events, the Skills notice** (`3d503dc`, fixes `d24c998`)
- Red: 4 of 5 failed. Green: HiddenNotices 5, then 9. Also:

| Class | Passed |
|---|---|
| VaultEvent | 21 |
| MemberView | 4 |
| SkillsVaultClash | 18 |
| SkillsVaultCreation | 10 |
| MembershipApi | 26 |
| SharedRekey | 28 |
| ClosingPeriod | 14 |
| AccountApi | 74 |
| UserKeyRotation | 40 |

- Mutations, all failing:
  - notices unfiltered;
  - dismiss without the hidden set;
  - events unfiltered;
  - the Skills notice compared without normalising;
  - sign-in returning the notice unfiltered;
  - the fixes' mutations: no vault id written, dismiss ignoring hiding, `Nin` dropped, the name fallback removed, sign-in skipping the filter.
- Review fixes:
  - the Skills notice carries its vault's id;
  - dismissing a hidden notice leaves it in place;
  - sign-in reads the sealed list only when there is a notice;
  - notices are filtered in MongoDB;
  - a revealed session doesn't open its vaults just to find nothing hidden.

**Task 9: `df` and names** (`6244329`, fixes `8c7c747`)
- Red: Core build error CS1729; server 3 of 5 failed. Green:
  - Core `DfTests` 104;
  - HiddenNames, DfServer, QuotaApi, NoteApi, InvitationApi and AliasApi together 94, then 103 after the fixes.
- Mutations, all failing:
  - (a) the old message for a hidden clash;
  - (b) `allOwned` without the hidden ids;
  - `Total` set for every caller;
  - `Df` ignoring `Total`;
  - the invitation mapping off;
  - the fixes' mutations: peek without the hidden set, own-vault accept unmapped, `allOwned` computed for sessions.
- Review fixes:
  - invitations to a hidden vault she owns or belongs to answer as invalid, for peek and accept;
  - the invitation's vault is read before the transaction;
  - a session's `allOwned` is always true;
  - the MCP `df` test compares before and after.

**Phase 2 gate** (worktree at `dfd9452`):
- Build: 0 warnings, 0 errors.
- Tests: total 5476, failed 1, succeeded 5469, skipped 6.
- The failure was `PresenceTests.An_off_is_never_lost_in_a_burst_of_hints`: a presence frame didn't arrive within the wait under full load. The class ran alone three times: 7 of 7 each time. It involves no discreet state and isn't on the brief's list of known flaky tests. I recorded it here and left it alone.

### Phase 3: the web app

**Task 11: "More vaults…" and the PIN dialog** (`e70281e`, fixes `c40800d`)
- Red: imports failed. Green: 19; full web 1880.
- Mutations, all failing: (a) the entry shown only with discreet vaults; (b) the stored choice ignored; (c) the PIN stored in `localStorage`.
- Review fixes:
  - the store resets when the user changes;
  - the store follows the vault list through one query-cache subscription;
  - focus returns to the PIN field after an error;
  - `gcTime: 0` on the PIN mutations;
  - a "4 to 6 digits" hint;
  - a storage spy in the end-to-end test.

**Task 12: frame, pill, Settings, hiding again** (`95312c4`, `8d31e6f`; fixes `79e7eb3`, `6263cde`, `2b60a46`, `89334eb`)
- Green: web 1919, then 1971, then 1992.
- **Deviation:** the first implementer wrote its tests after most of the code, so there was no real red run for Task 12 itself. The mutations show the tests catch the behaviour; the fix-ups did run red first.
- Mutations, all failing:
  - hiding before saving;
  - the vault not forgotten in the store;
  - the switches shown while hidden (this one survived until the test was strengthened);
  - the fix-ups' mutations: no `finally`, the sweep, the stale session read, the held draft (user change, two drafts), the vault list after a resent hide, the `light-dark()` tokens, the ring's contrast, the overlapping triggers.
- Review fixes:
  - **ruling 1's full clear**, plus search, find, the selected folder and the scroll target;
  - a failed save is held out of view and retried once, waiting for `online`;
  - the time-out doesn't call `hide`, so the late-save marker survives;
  - `try`/`finally` around hiding;
  - stale session answers are ignored;
  - tokens defined per scheme instead of `light-dark()`, which older Safari lacks;
  - a brighter amber for the light frame, at least 3:1;
  - a 44 px ✕ target;
  - **flaky tests under parallel load:** two causes (waiting only for the URL, a stale session read) fixed in `89334eb`, then 36 of 36 stress runs passed.

**Task 13: the late save**
- Server: `a05a76e`, fixes `a7386a3`, `56ffaf4`. Red: 10 of 10 failed. Green: LateSave 10, then 17 (three runs), then 18. NoteApi 26, Reveal 19, DiscreetApi 16, HiddenVault 11, PasswordWindow 70, DotSegment 14, Caller 25.
- Mutations, all failing:
  - the marker not taken;
  - no time limit;
  - the marker stored in clear;
  - no put-back;
  - the rotation keeping the marker;
  - the fixes' mutations: no `LateSaveRetried` check, the error filter off, an expired marker not forgotten, the take without the reveal checks, no `OnStarting` put-back (this one survived until a test was added), a seal without the session, no metadata on the PUT.
- Web (`d15899c`, `dfd9452`): the tab tells the server which discreet note is open, `{vault: null}` when none is, and nothing while hidden.
- **Security review of the server half:**
  - a late-save PUT's 409 returned the note's full text, and a 404 named the place a note had moved to;
  - failures could be retried without limit.
  - Both are fixed: every non-2xx answer of a late save is now "This late save wrote nothing.", and one retry is allowed.

**Task 14: the neutral URL** (`c33d386`, fixes `7ee7fd7`, `12a7e63`)
- Red: the import failed. Green: discreet 55 (three runs); web 1987, then 1992.
- Mutations, all failing:
  - (a) discreet URLs passed through;
  - (b) `createHref` unwrapped;
  - (c) the memory kept on hide;
  - the fixes' mutations: the fragment handler removed, `preventDefault` removed, the id decoded first (survived until strengthened), download buttons back to links (Lightbox and TextFile), the entry's state from `history.state`, no `memory.delete`, the delete moved before the path is resolved.
- Playwright against `vite dev` with a mocked API:
  - the address bar shows `/discreet` inside Tagebuch;
  - `history.state` is `{key, idx}` only;
  - Back and Forward restore the discreet notes from memory;
  - reloading `/discreet` lands on "Your vaults";
  - the title stays "Kumbu²".
- Review fixes:
  - footnote links (`#…`) inside a discreet note left the view, and now scroll in place;
  - download links named the vault in `href` and are now buttons.

### Phase 4

**Task 15: README and open items** (`e969cc5`, `3bda913`, `79fdb04`)
- A "Discreet vaults" section in the README, ending with "What it doesn't hide".
- Open-items rows.
- The Settings help line names WebDAV and connections too.

**Task 16: security review** (§5); fixes in `d97a5aa` and `9fa2e30`.

**Task 17: final gate** at `89334eb`, fresh MongoDB, its own worktree, as CI does it:

| Step | Result |
|---|---|
| `dotnet restore --locked-mode` | ok |
| `dotnet build -c Release` | 0 warnings, 0 errors |
| `dotnet test -c Release` | total **5478**, failed **0**, succeeded 5472, skipped 6 (15 min 56 s) |
| web install, test, build | 119 files, **1992** tests passed; build ok |
| pdf install, test, build | 9 files, **198** tests passed; build ok |
| WebDAV litmus (`ci.yml` steps, run locally) | exit 0: "every failure is an expected one (21 listed)" |

## 5. Security review

A fresh subagent reviewed the whole branch diff (`4c88031..HEAD`, `src` and `web/src`) against spec §3, §5 and §6.

1. **Medium: an empty connection edit revealed that hidden vaults exist.** It answered 200 for a connection that also covers a hidden vault, and 400 otherwise. **Fixed** (`d97a5aa`): an empty edit always answers 400, and a byte-identical test covers it.
2. **Medium: browser password managers may store the PIN.** The Set-PIN form looked like a change of password. **Fixed** (`9fa2e30`): the PIN fields are text inputs masked by CSS and `autocomplete="one-time-code"`. **Hand test needed** in Chrome, Firefox and Safari (§8, step 12).
3. **Low–medium: download URLs name the vault.** This covers exports, file ZIPs and single files, and the browser's downloads list keeps the URL.
   - **In-page links fixed:** download links are now buttons (`7ee7fd7`).
   - **The URL itself is open:** stated in the README's limits, and an open item (a neutral URL bound to a ticket would fix it).
4. **Low: a forged cross-origin PUT could spend the late save's retry.** **Fixed** (`d97a5aa`): the Origin check comes before the marker is taken.
5. **Low: names and counts of hidden-only keys, connections and shares still answer** ("already named …", the 20-share limit). **Not fixed:** in the README's limits and an open item.
6. **Low: an unchanged connection edit no longer revokes the tokens.** **Accepted** as decision 9 (§6).
7. **Info: the storage totals exceed the visible rows while vaults are hidden.** **Accepted** (spec §3.4, "only the total") and stated in the README.
8. **Info: a socket close reason named the event.** **Fixed:** the reason is now "connect again".
9. **Info: peek and accept disagree** for an invitation to a discreet vault she has left. **Open item.**
10. **Info: a rare re-keying race of a hidden vault fails a request with 503.** This was already so before the branch. **Left as is.**

Earlier reviews also caught several leaks before the security review ran:
- the late-save conflict body (§4, Task 13);
- the access lists' gone-vault case and the hidden-only ids (Task 8);
- invitations to her own hidden vault (Task 9);
- the socket's ticket issued before a mark (Task 10);
- footnote navigation and download hrefs (Task 14).

## 6. Decisions made without Chris

Each lists the options I saw and why I chose this one. The rules of the brief, in order: breaks nothing, true to the application, no new hole, true to the spec.

1. **Where the reveal lives:** fields on the session slot (`revealedAt`, `revealUntil`, `pinEntries`, `lateSave`, `lateSaveRetried`), handed from `ResolveAsync` into `UserCredential`. The alternatives were a collection of its own, or in-memory state, which wouldn't survive a restart and isn't the password window's pattern.
2. **The sealed list** is a JSON array of ids sealed like `SkillsNotice`, and it keeps ids of vaults that are gone. A list that doesn't open hides every vault but Skills.
   - Options: count it as empty (shows everything: a hole), fail every request (breaks the app), or hide all (shows less).
   - Hide all wins by rule 3, and the next mark repairs the list.
3. **Rotation and recovery** reseal the list in the rotation's transaction, conditional like `MetaReseal`. A list that doesn't open is left as it is.
4. **The PIN verifier** is Argon2id through `Argon2Kdf` with its own salt. With no PIN set, the KDF runs on the given PIN against a dummy salt and verifier. A malformed PIN runs it on `"000000"`. The check returns `PinMatch`, which leaves room for 0.5.0.
5. **Counting entries:**
   - a counter on the slot, atomic;
   - every entry counts, and the tenth deletes the slot and answers as any ended session (401);
   - the order is rate limit, then count, then KDF, so an entry refused by the limit doesn't count. That keeps one rule for everyone, and the limit bounds guessing anyway.
6. **Rate limit:** its own limiter, 5 per minute per user id. A per-session or per-address limit would be easier to get around.
7. **Routes and answers:**
   - everything under `/api/account/discreet/…`;
   - a wrong PIN, no PIN and a malformed PIN get 400 with `{"error":"You have no discreet vaults."}`. I chose an error over a 200 with `revealed:false` so the right answer is the only 200;
   - marking while hidden gets the 404 of a vault you don't have;
   - `GET /api/session` gets `discreet: {revealed, until}`, with `until` only while revealed.
8. **The socket:** close-and-reconnect (4412) instead of flagging vaults on the socket. The flags would miss a ticket issued just before a mark. A close can't miss it, and the next ticket comes from the filtered credential. There is a timer at the reveal's end, re-armed when a reveal is renewed.
9. **Connections, keys, shares:**
   - an edit merges the stored grants on hidden vaults;
   - an empty edit always answers 400;
   - items that cover no visible vault aren't listed, and their ids answer as unknown on edit and find;
   - revoking stays whole;
   - an edit that changes nothing writes nothing and revokes no token. Before, every edit revoked the tokens. Now an agent never loses access over an edit that changed nothing (rule 1), and nothing is weakened.
10. **Neutral URL:** a router `window` of its own instead of rewriting every place that builds a URL (77 of them).
    - `pushState` and `replaceState` write `/discreet` and keep the real URL and `usr` state in memory;
    - `createHref` is wrapped;
    - reloading `/discreet` goes to `/`.
11. **Late save:**
    - the tab tells the server the open discreet note, which is stored sealed per session;
    - after a time-out, `CallerMiddleware` lets through exactly one PUT of that note within 5 minutes, marked by metadata on the PUT endpoint;
    - one failed attempt may be retried once;
    - every refusal is "This late save wrote nothing."
    - ✕, a new reveal and a rotation clear it.
    - The tab doesn't call `hide` at a time-out: the server ends the reveal itself, so the marker survives for the late save.
12. **Skills:**
    - it can't be marked;
    - the notice carries its vault's id and is hidden while that vault is hidden, falling back to the name for older notices;
    - **the naming is unchanged** (an open item): a renamed vault's new name can skip a hidden one ("Skills (renamed 2)").
13. **Names:**
    - a clash with a hidden name answers 409 "This name can't be used here." when it is found, which is after the catalogue refuses, so the order of checks is unchanged;
    - the invitation peek calls such an alias free;
    - an invitation to a hidden vault answers as an invalid one.
14. **Export names:** `Kumbu² export {date}.zip`, the existing several-vaults form, rather than the spec's `kumbu-export-…`. It follows the application's own style (plan ruling 16).
15. **`df`:** a session prints the true total and keeps its sentence. Keys and MCP print exactly as before.
16. **PIN fields:** masked text inputs, not password inputs (security review, finding 2).
17. **Time-out choice:** `localStorage` `kumbu.discreetTimeout`, default 10.
18. **Clock skew** (web): a tab doesn't show the vaults again for a session whose end has already passed locally, and asks again after 2 s.

## 7. Deviations, surprises, open points

- **Deviations:**
  - Task 12's first implementation wrote its tests after the code (§4).
  - The socket design changed during planning (ruling 8).
  - The export name follows ruling 16 rather than Decision 15.
  - Task 8, 10 and 13 commits came in a different order from the plan's numbering, because independent tasks ran in parallel.
  - New files beyond the plan's lists:
    - `Api/LateSave.cs`
    - `discreet/openNote.ts`, `discreet/SetPinDialog.tsx`, `discreet/neutralWindow.ts`
    - `Discreet/HiddenWorld.cs`, a shared test setup
- **Surprises:**
  - The sandbox's file limit crashed MongoDB (§1).
  - The late save's middleware needed a `TimeProvider` that broke `CallerTests`' minimal host (fixed).
  - remark-gfm writes footnote ids percent-encoded.
  - `light-dark()` isn't in the browsers the app targets.
- **Possibly flaky:** `PresenceTests.An_off_is_never_lost_in_a_burst_of_hints` failed once under a full run and passed alone three times (§4). It isn't on the brief's list. Worth watching on CI.
- **No tasks are left.**
- **Open points for Chris:**
  - The decisions in §6, above all 2, 5, 8, 9 (unchanged edits keep tokens), 11 and 14.
  - The security review's open items: download URLs, hidden-only credential names and the share limit, and the invitation peek after leaving. Also the Skills naming. All are in [`open-items.md`](../open-items.md).
  - The README's "What it doesn't hide" lists every known limit. Please check it reads as you want.
  - Whether a stale session read that says hidden, arriving right after a reveal, needs handling. The web reviewer judged it very unlikely, since a PIN has to be typed first.

## 8. Hand-test checklist (staging)

Use Playwright with one browser context per test user. Alice and Bob each need an account; Bob shares `Familie` with Alice.

1. **Set the PIN.** As Alice, with vaults `Work`, `Garten` and `Tagebuch`: Settings → Discreet vaults → Set or change PIN, current password, PIN `4711` twice. **Pass:** 204, and the dialog closes. There's no password-manager prompt (check Chrome and Safari).
2. **Reveal with Never.** Vault menu → "More vaults…" (the last entry) → `4711` → Never → Open. **Pass:** an amber frame around the sidebar and the pill "Discreet · ✕" with no time. In Settings, a "Discreet" switch for each vault except Skills.
3. **Mark.** Switch on `Tagebuch` and `Familie`. In Bob's context, nothing changes: `Familie` is listed and works.
4. **Hide.** Click ✕. **Pass:**
   - `Tagebuch` and `Familie` are gone from the menu, the tree, the search (search for a word that is only in `Tagebuch`) and the Access screen;
   - a key for `Tagebuch` alone is not listed;
   - the console's `ls /` lists `Work`, `Garten` and Skills;
   - `df` keeps its sentence.
5. **A key still works.** Over MCP, with Alice's agent key for `Tagebuch`, write a note there while she is hidden. **Pass:** the write succeeds, and the web app still shows nothing of it.
6. **WebDAV still works.** A WebDAV client (for example `rclone lsd` or Finder) with a share for `Tagebuch`, while hidden. **Pass:** it lists and writes the vault.
7. **The brother's wrong PIN.** In a fresh context signed in as Alice: "More vaults…" → `1234`. **Pass:** "You have no discreet vaults.", the field is cleared, and nothing else changes. Repeat with a user who has no PIN. **Pass:** the same text. Make the sixth try within a minute. **Pass:** "Too many attempts; try again in a minute.".
8. **Reveal for 30 minutes** on a phone-width context (390 px). Choose "30 min" and Open. **Pass:**
   - the frame and the pill "Discreet · 30 min · ✕" show in the mobile layout;
   - reopening the dialog later preselects 30 (per device);
   - open a note in `Tagebuch`: the address bar shows `/discreet`, with no vault and no path.
9. **Time-out while typing.** Reveal for 5 minutes, open a `Tagebuch` note in Write mode, and type until the end (or move the clock). **Pass:** the text is saved (read the note over MCP or after a new reveal), the note closes, and the app shows `Work`. Variant: put the context offline before the end and back online within 5 minutes. **Pass:** the held text is saved once.
10. **Neutral URL and history.** After step 8, open two or three discreet notes, then use Back and Forward. **Pass:** the address bar stays `/discreet` and the notes follow. Then click ✕, sign out, and look at the browser history (`chrome://history`) and the downloads list. **Pass:** no `/notes/Tagebuch/…`; reloading `/discreet` shows the vault list; sign-in `next=` is `/`.
11. **✕ in one tab closes the other tab.** Two tabs of one session, both revealed, with a `Tagebuch` note open in tab B. Click ✕ in tab A. **Pass:** tab B closes the note within a second or two, shows `Work`, and its pill and frame go.
12. **Password managers.** In Chrome, Firefox and Safari with saved credentials, open Set-PIN and the PIN dialog and submit. **Pass:** no offer to save or update a password with the PIN.
13. **Light and dark, phone width.** Repeat steps 2 and 8 in light and dark mode, desktop and 390 px. **Pass:** the frame and pill are readable and nothing overflows.
14. **Rotation.** Settings → Re-encrypt my vaults, with `Tagebuch` marked. **Pass:** `Tagebuch` stays hidden, `Work` stays visible, and a reveal still shows it as discreet.

## 9. Pull request

Draft: https://github.com/SCAI-NGO/kumbukumbu/pull/179 (base `master`, depends on #176). Its row is in [`docs/superpowers/open-items.md`](../open-items.md).

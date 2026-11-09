# Overnighter brief: discreet vaults

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React web app, on MongoDB. This run builds **discreet vaults**, which Kumbu²'s Discord community asked for. A user marks vaults as discreet, and in the **web app only** they then don't exist until a separate PIN shows them for a chosen time. Agents, sync, WebDAV and keys are untouched. People should be able to take Kumbu² to a family gathering or to work without a vault's name giving them away, so the hiding has to be done by the server and must not leak anywhere. It ships in 0.4.0, planned for Sunday 2026-11-15. A sound, honest partial result beats a rushed whole one.

Nobody is available to answer questions during this run. That is intended: Chris has reviewed and approved the spec, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it (everything you need from it is repeated here)
3. Spec: `docs/superpowers/specs/2026-11-09-discreet-vaults-design.md`, reviewed and approved by Chris. Please don't edit it.
4. For context, as needed (a code survey made while writing the spec; trust the code over any line number):
   - **The one path of web sessions to their vaults:** `src/Kumbu.Server/Vaults/Credentials.cs:30-44` (`UserCredential.OpenAsync` → `VaultCatalog.OpenForUserAsync`, `VaultCatalog.cs:223`). `RequestCaller.ForSession` (`src/Kumbu.Server/Auth/RequestCaller.cs:142`, from `CallerMiddleware.cs:115-119`) makes the web's `UserCredential`. `WelcomeVault.cs:60` makes one too, at join; it needs no filter (nothing is discreet yet). The other credential classes (`Credentials.cs:85,115,135`) are keys, connections and shares: don't touch them. `WelcomeVault.cs:60` and `SkillsVaults.cs:64` also open a user's vaults, at join and sign-in.
   - **The store's vault lists** that follow the credential: `EncryptedNoteStore.cs:58,1114,1132` (`ListVaultsAsync`, `AllVaultsAsync`, `VaultsAsync`) and `caller.OpenVaultsAsync` (`RequestCaller.cs:136`).
   - **Sessions:** `src/Kumbu.Server/Accounts/Sessions.cs`.
     - `ResolveAsync` is at `:105-144`, with the password window (`ConfirmedAt`, `WindowUsedAt`) at `:121-123`.
     - `OpenWindowAsync` is at `:212-222`, `EndAsync` at `:226`, `PasswordWrongAsync` at `:309-337`.
     - The slot document is `UserSlotDocument`, `Accounts/UserDocuments.cs:149-208`.
     - The password window is the pattern for the reveal.
   - **Password checks and limits:**
     - `AccountLimits.PasswordCheck` is at `src/Kumbu.Server/Api/Security.cs:392`.
     - `ApiErrors.TooManyPasswordChecks` is at `Api/ApiErrors.cs:53-54`.
     - `WrongPasswordException` is at `Accounts/Admin.cs:262`, and its filter at `ApiErrors.cs:156-161`.
     - `AccountApi.PasswordGivenAsync` is at `AccountApi.cs:367-370`.
   - **User preferences and sealed fields on the user:** `UserDocument` (`Accounts/UserDocuments.cs:108-121`, `SkillsNotice` is sealed), `/api/account/preferences` (`AccountApi.cs:234,748-787`).
   - **Sources outside the store** (spec §3.4):
     - member notices: `Sharing/SharingApi.cs:80,302`, `Sharing/MemberNotices.cs:69-70`, view `SharingApi.cs:58`
     - vault events: `SharingApi.cs:76,226`
     - the session's `SkillsNotice` (`AccountApi.cs:30-36`; the field is at `UserDocuments.cs:139-142`)
     - name checks: `NameFreeAsync`, `VaultCatalog.Members.cs:91`
     - `df`: `Accounts/ShellStorageFigures.cs:13-37`, `allOwned` at `:25`
     - the OAuth consent view: `ConnectionApi.cs:173`, `web/src/routes/Consent.tsx:135`
     - the export: `ExportApi.cs:44-66,157` (vault by vault; the file name of a discreet vault's export is neutral)
   - **Access lists:** keys `AccountApi.cs:214,452,703`; WebDAV shares `:221,484,717`; connections `ConnectionApi.cs:67,205`.
   - **The console:** `src/Kumbu.Server/Api/ConsoleApi.cs:39-55,111`. It runs the same scoped `Shell` as MCP (`Program.cs:190`).
   - **WebSocket:**
     - `SyncApi.cs:56-86` (the ticket from `caller.OpenVaultsAsync`)
     - `src/Kumbu.Server/Sync/SyncHub.cs` (frames at `:17-34`, `RefreshAsync`/`MembershipChangedAsync` at `:215-218`, `VaultsNowAsync` at `:373-400`)
     - `web/src/hooks/useSyncSocket.ts:23-95`
   - **Web routing and state:**
     - `web/src/router.tsx:42,118-124` (routes with the vault in them, and `next=` on sign-in)
     - `web/src/api/paths.ts:8`, `web/src/shell/MobileShell.tsx:131`
     - `web/src/state/editor.ts:20-21,89` (in memory only, `forgetVault`)
     - `web/src/routes/SignIn.tsx:39-46`
     - the vault menu in the sidebar (find it from `web/src/shell/`)
   - **Admin:** `AccountApi.cs:118-120,502-507`, `Accounts/Admin.cs:31-37` (counts only, no names).

## Where you run

- **Branch:** `feature/discreet-vaults`. The session starts on the default branch, so first run `git fetch origin feature/discreet-vaults && git checkout feature/discreet-vaults`. Everything you push to that branch survives, everything else does not.
- **The branch is based on `ingest-attachments`** (pull request #176, not merged yet: ingesting attachments with the archive, plus three test fixes). Build on what is there, including the archive endpoints, which hide discreet vaults like every other web endpoint. Don't merge `master` or `ingest-attachments` into the branch. Another run is working on `feature/structural-chunking` (chunking and `semsearch`'s output) in parallel: don't touch the chunker, and keep your `semsearch` changes to the vault filter.
- **Tools:** The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new NuGet or npm package.
- **Time budget:** Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- **SDKs:**
  - .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`.
  - Node 26, which may have to be installed by hand.
  - pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing).
  - After any pull or checkout, run `pnpm -C web install --frozen-lockfile` and `pnpm -C pdf install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script. If `docker info` says "Cannot connect", start `dockerd` yourself. The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`). Earlier runs met a hard file limit below what Testcontainers' `mongo` asks for. What worked was a container of your own, as a replica set, fresh for every full run:
  ```sh
  docker rm -fv kumbu-test-mongo
  docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 127.0.0.1:27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all
  for i in $(seq 1 30); do docker exec kumbu-test-mongo mongosh --quiet --eval 'try { rs.status().ok } catch (e) { rs.initiate({_id: "rs0", members: [{_id: 0, host: "127.0.0.1:27017"}]}).ok }' && break; sleep 1; done
  export KUMBU_TEST_MONGO='mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true'
  ```
  Check the image tag against `TestImages.cs` and use the one it names. If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- **Skipped tests:** the 4 skipped tests need the real embedding model and a TensorX key. Neither is available here, and skipping them is expected.
- **No superpowers:** if superpowers skills are not available, emulate subagent-driven development with the plain Agent tool and say so in the report.

## Open points for the run

An independent review of spec and brief found gaps. Chris settled two of them, and the rest became Claude's calls in the spec (§3.6, §3.8, §4):

- the sealed list resealed on key rotation and recovery;
- the socket dropping discreet vaults at the end;
- the password every time for the PIN;
- every PIN entry counting, with the tenth ending the session;
- one late save within 5 minutes;
- the strict neutral URL;
- invitations;
- the export file name.

Read §3.6, §3.8 and §4 of the spec closely. The points below are **decide**: settle them with the rules below and log each in the report. Where a reading is given, it is the recommended one.

1. **Where the reveal lives.** Add fields to the session slot, modelled on the password window (spec §3.3): when the reveal ends, or "until the mode ends" for Never. Then pass a `Revealed` flag from `Sessions.ResolveAsync` through `RequestCaller.ForSession` into `UserCredential`, which leaves discreet vaults out of `_opened` while not revealed.
2. **The sealed list of discreet vault ids** on `UserDocument`. Seal it the way `SkillsNotice` is sealed (spec §4). The credential needs the user key to read it, and a session has it.
2a. **Key rotation** (spec §4): reseal the list inside `UserKeyRotation`'s prepare/apply transaction (`UserKeyRotation.cs:281`, called from `Rekeying.cs:36`), and on recovery (`UserCatalog.cs:350`), with the same conflict check as `MetaReseal`. `SkillsVaults.cs:155-178` shows how the `SkillsNotice` precedent silently gives up after a rotation: the list must not. Test both.
3. **The PIN verifier.**
   - Use the same KDF as passwords (find it from `UserCatalog`/`_kdf`), with its own salt.
   - **Timing equality** (spec §3.6): with no PIN set, run the KDF against a dummy verifier, so all cases take the same time.
   - Test that the response body and status are identical in all three cases.
   - Timing is checked by design, not by a timing test. A timing test would be flaky.
4. **Counting PIN entries per session** (spec §3.6): a counter on the session slot, like `WrongPasswords`. Every entry counts, right or wrong; the tenth ends the session (delete the slot, as a remembered session's tenth wrong password does). Same for every account.
4a. **The PIN rate limit:** a new limiter next to the password check's (`Security.cs:392`), per account, with the same shape for every account. Over the limit, answer "Too many attempts; try again in a minute" for everyone.
5. **Endpoints.** Choose routes in the style of `/api/account/...`:
   - set or change the PIN (needs the current password **in the request body every time**: `limits.PasswordCheck`, `CheckPasswordAsync`, `WrongPasswordException`, `PasswordGivenAsync`, as `ExportApi.cs:116-124` does; not the password window);
   - reveal with PIN and time (one answer for wrong PIN and no PIN, spec §3.6);
   - hide;
   - mark or unmark a vault (only while revealed; refuse otherwise, without saying whether the vault exists);
   - the session's reveal state, which can be part of `GET /api/session`. It says "revealed until …" or "not revealed", and nothing else, so that it doesn't leak whether a PIN exists.

   Add them to `web/src/api/schema.d.ts` by hand, as generated output would have them.
6. **Hiding again over the socket.** `SyncHub.RefreshAsync`/`VaultsNowAsync` (`SyncHub.cs:373-400`) work out vaults from slots and memberships only and have no user key, so on their own they would never drop a discreet vault. Recommended: when a ticket is issued during a reveal, record which of its vaults are discreet and when the reveal ends. The hub then drops those vaults at the end, on ✕ and in its regular check, or closes the session's sockets so the tabs reconnect with a fresh ticket. `useSyncSocket.ts`'s `onFrame` drops any frame without a `vault` string: give the new frame one, or adjust the handler. Send the other tabs of the session a frame they act on (save drafts, close discreet tabs, go to an ordinary vault). The time-out is enforced by the server on every request. A tab can use a client timer to act at the right moment, but the server is the authority.
7. **Keys, shares and connections in both kinds of vaults** (spec §4):
   - The only edit path that sets grants is `PUT /api/connections/{id}/vaults` (`ConnectionApi.SetVaultsAsync`, `:224-233`, then `Connections.SetVaultsAsync`, `Connections.cs:267`, which replaces the stored set). Keys and shares have only create, delete and `/instructions`.
   - **Merge the hidden grants in phase 1**, together with the credential filter, or an edit would drop them as soon as the filter lands. "A connection needs at least one vault" (`:250`) judges the merged set.
   - Items that cover only hidden vaults are not listed.
   - Revoking stays whole.
8. **The neutral URL** `/discreet` (spec §4). The router is the source of the open note today (`editor.ts:21`, `MobileShell.tsx` `useMatch`). Inside a discreet vault, the open view comes from memory instead, never from `history.state` or `navigate(..., { state })`, and links render without the path in `href`. routes with a vault become `/discreet` via `history.replaceState` while the vault is discreet. Reloading `/discreet` after the reveal has ended lands on the ordinary vault list. The sign-in `next=` never carries a discreet path.
8a. **Late save** (spec §3.8): the tab saves before the end. The server accepts one save of the note that was open, from that session, up to 5 minutes after the end; record which note when the reveal ends.
9. **Skills vault** (`VaultKinds.Skills`) can't be marked (spec §3.1). Make sure `SkillsVaults`' automatic creation and renaming (`MakeRoomAsync`) never names a hidden vault in the Skills notice. If that would need a change to the naming itself, leave it out and record it (spec change).
10. **Names:** creating or renaming a vault, or an alias, or joining through an invitation (`VaultInvites.cs:144`, `VaultNameTakenException` at `VaultCatalog.cs:1934`), to a hidden vault's name answers "This name can't be used here" (spec §3.4). Keep the existing message for a clash with a visible vault, so only the hidden case is neutral.
11. **Light and dark mode, phone width:**
    - The amber frame and the pill use the app's existing colours or a new pair of theme tokens, in both modes.
    - The mobile layout (`web/src/shell/MobileShell.tsx`) gets the same frame and pill.
    - The time-out choice is stored in `localStorage` as `kumbu.discreetTimeout`.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-09-discreet-vaults.md`.
2. It starts with **Task 0: baseline**: run the full suites before any change and record the exact counts and the exact failures, against the known state below.
   Known state on `ingest-attachments` at `a4c899b` (the base of this branch, which adds only docs):
   - **.NET** (`dotnet test --solution Kumbu.slnx`), measured on Chris' machine on 2026-11-09 at `9e919a7`: total 5333, skipped 4, and 2 failures that `a4c899b` then fixed (`PairingTests`, `MembershipApiTests`).
   - **CI on #176 at `a4c899b`:** web app, PDF sidecar, WebDAV litmus and both images are green. `build` failed once on `RekeyTransitionTests.A_run_cancelled_in_its_transaction_completes_the_transition(point: "inside-finish")` with a timeout, a known flaky test under load; it is being re-run.
   - **Known flaky tests:** `RekeyTransitionTests`, `AttachmentPixelsTests.A_stored_analysis_and_its_text_are_read_without_the_sidecar` and `AttachmentMetricsTests.Turned_pages_count_for_pdf` have failed now and then under load. If one fails, run its class alone three times, record the result, and carry on. Don't fix them here.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. **Order by value**, so a short run still leaves something whole:
   1. the server: the PIN, the reveal on the session, the credential filter, and the PIN dialog's single answer with its rate limit;
   2. the other sources of spec §3.4 (notices, events, Skills notice, access lists with grant merging, `df`, names, export, OAuth consent, socket);
   3. the web app: "More vaults…", the PIN dialog, frame and pill, Settings section, hiding again, neutral URL;
   4. README (the known limits of spec §6, the export note), help texts, and the security review.

   Put a phase gate after each. The server alone must already be safe: if the run ends after phase 1, a hidden vault must already be absent from every response that goes through the credential.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by a fresh subagent.
- Subagents never merge, push or switch branches. You commit and push.
- Commit and push after every task. Commit messages imperative and free-form ("Hide discreet vaults from a session until it reveals them"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- The test filter is Microsoft.Testing.Platform's: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`, repeatable. `FullyQualifiedName~` doesn't work here, and a glob in mid-name selects nothing. Core: `--project tests/Kumbu.Core.Tests`. Web: `pnpm -C web test --run <file>`.
- Full verification at each phase gate, as CI does (`.github/workflows/ci.yml`): `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`; `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`. CI's litmus job (WebDAV) is worth running at the final gate, since spec §7 asks that WebDAV still reaches hidden vaults. If you can't follow its steps in `ci.yml`, say so in the report.
- **The web's CI gate is `pnpm -C web build`:** it runs the type check, which `pnpm test` does not.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- **Tests for every hidden surface** (spec §7): one server test per endpoint or source, asserting absent while hidden and present while revealed, plus tests that MCP, an API key, a device key and a WebDAV share still reach a hidden vault. Time-dependent behaviour goes through the `TimeProvider` the code already injects. **Test clocks start at the real time** (`DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())`), never a fixed date: MongoDB's TTL monitor runs on the real clock and sweeps session slots made at a past fake time. That was a flaky test fixed on 2026-11-09. Wait for a condition rather than a fixed delay.
- **Style:** British English in code, comments, docs, commits (colour, behaviour, initialise, licence as the noun). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples, test data. Use Alice, Bob, Carol, `Work`, `Garten`, `Tagebuch`.
- **Texts in the web app** are English, short and in the voice of the existing screens: "More vaults…", "You have no discreet vaults.", "Discreet vaults", "Discreet", "Set or change PIN", "Hide again after", "Too many attempts; try again in a minute". The web app's components and screens are the pattern for the dialog and the Settings section; check light and dark mode and phone width.
- If the run gets short, stop at the last green phase gate rather than half-way through a task, push, and say in the report which tasks are left.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. A user without discreet vaults sees no difference, apart from the menu entry. **Agents, keys, connections, device keys, WebDAV and sync behave exactly as before**, also for a discreet vault, and no stored grant is ever dropped.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture: the password window for the reveal, sealed fields on the user document, `AccountLimits` for the rate limit, `ApiErrors` for answers. No new dependencies.
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling. Where in doubt, the reading that shows **less** in the web app while hidden is right, as long as it doesn't take anything away from agents or devices. Every answer around the PIN must be the same whether or not a PIN or discreet vaults exist.
4. **True to the spec.** Chris' guiding wish: **for the web app, what is discreet does not exist** while it is hidden, and a curious person can't tell that anything is hidden.

If a question can only be answered by changing the spec's scope, do not guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the pull request description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please do not merge, tag or deploy, and don't change the version: rollout is something Chris does himself. One draft pull request is the result.
- No secrets in logs, assertions or the report: never a PIN, a verifier, a password or a vault key. Never log a discreet vault's name either.
- **Security boundary: touched, narrowly** (spec §5).
  - **Touched:**
    - new fields on the session slot
    - a PIN verifier and a sealed list on the user document
    - a rate-limited PIN check
    - a filter in the web session's credential and in the sources of spec §3.4
    - display-only changes on the OAuth consent page and the access lists, with grants merged on edit
  - **Also touched, narrowly:** `UserKeyRotation` and recovery reseal the list (spec §4).
  - **Not touched:** API keys, device keys, connections and WebDAV shares as credentials, OAuth's flows and tokens, MCP and `sh` over MCP, sync, encryption and key slots, sharing and roles, publishing's public pages, the Obsidian plugin.
  - **Security review:** a late task has a fresh subagent review the whole branch against spec §3, §5 and §6, especially for leaks of a hidden vault's name and for differences between the PIN answers. Every finding goes in the report, fixed or not, and the pull request says that a security review applies.
- **At the end:** a row with the pull request's link in `docs/superpowers/open-items.md`, and in the report a hand-test checklist with numbered steps, where to look and what passes. Chris runs the browser parts with Playwright on staging, one browser context per test user. Include:
  - spec §3.10's example step by step (Alice, Bob, `Tagebuch`, `Familie`, PIN `4711`);
  - the brother's wrong PIN;
  - a key on a hidden vault still working over MCP;
  - a WebDAV client still listing the hidden vault;
  - the time-out while typing (the draft is saved);
  - the neutral URL and the browser history after a session;
  - ✕ in one tab closing discreet tabs in a second tab;
  - the phone layout.

  Don't change anything outside the plan's files.

## Out of scope

- Everything spec §2 lists as a non-goal: any change to sync, agents, MCP, `sh` over MCP, WebDAV, OAuth flows, API keys, device keys or connections as credentials; the kill switch (0.5.0); encryption by the PIN; hiding from admins' counts, the operator or the database.
- The chunker and `semsearch`'s output format (another run's work).
- The flaky tests named above.
- The Obsidian plugin (another repository).
- Merging `master` or `ingest-attachments` into the branch.

## The report

First open a **draft** pull request with base **`master`**, titled "Discreet vaults: out of sight in the web app until a PIN shows them". Its body:

- says it depends on #176 and shows #176's commits until that is merged;
- lists the decisions made without Chris;
- says that a security review applies;
- ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

Then, as the last commit, write `docs/superpowers/overnighter/2026-11-09-discreet-vaults-report.md`, with:

- the environment
- the plan review findings and what you did with them
- Task 0 planned vs. measured
- for each task the commit and the real test output
- every mutation individually
- the security review's findings and what became of each
- the decisions made without Chris
- deviations, surprises, tasks left if any, open points for Chris
- the hand-test checklist
- the pull request's link, together with a row linking it in `docs/superpowers/open-items.md`

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!

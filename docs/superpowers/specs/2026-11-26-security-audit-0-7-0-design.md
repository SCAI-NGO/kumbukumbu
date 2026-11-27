# Security audit of 0.7.0

Status: approved by Chris, 2026-11-26.

## 1. Goal and decisions

Before 0.7.0 is released, an independent **defensive** review of everything that changed since 0.6.0. It checks the new code against the security promises of `docs/security-architecture.md`, the offline mode design (`docs/superpowers/specs/2026-11-22-offline-mode-design.md` §4) and the architecture decision records of 0.7.0 (`docs/adr/0002` to `0033`, without 0023, which is 0.8.0's; 0026 and 0027 are not on `master`), and rates each weakness on the scale of the 0.4.0 audit.

**Blue team only.** This is code review and defensive testing of our own code, and Chris is its maintainer.
- No exploit development, no attack payloads beyond the ordinary untrusted inputs a unit test feeds a parser, no attack chains, nothing run against a live system.
- A weakness is shown by a test of the behaviour the code **should** have, which fails today, or by a precise trace through the code. An example of such a test: "a contact that hands in another user's mirror token gets no mirror key".
- Ordinary unit-test inputs are, as in the earlier audits:
  - the standard sanitiser test strings (`<script>`, `javascript:` links, event-handler attributes, SVG `foreignObject`, CSS `url()`);
  - bounded oversized or deeply nested inputs (YAML front matter, Markdown, a `not-related` list, a sealed record);
  - a sealed record with a flipped byte, a replayed older version of a record, a header naming another user.

  Each test asserts that the code refuses, neutralises or limits them.

Chris: blue-team work is what we do.

Decisions, each with Chris's reason:

- **This run only finds and reports; it fixes nothing.** Its findings go into **one** fix run together with the findings of Chris's hand tests of 0.7.0 (`/SWE/Kumbu/Tests/Test list 0.7.0-pre.1.md` in Kumbu², not part of this run). Chris's reason, as before: one fix run means fewer merge conflicts, one review and one pre-release to test.
- **The exception is a High or Critical finding.** Once the second review confirms it, it is reported at once (§6) and fixed right away, outside this run.
- **The policy stays:** Medium and up is fixed before 0.7.0. A High is fixed at once. A Low is fixed only if it is quick; the rest go into 0.8.0's preparations.
- **The offline mode had a scoped review already, during its development.** Its findings, decisions and their state are in the private repository only, and this spec names none of them. This audit covers the whole release: everything that review didn't see, how the features meet, and that review's findings at this commit (area I).
- **Each 0.7.0 pull request had its own security review of its diff.** This audit looks at what lies between them: features meeting each other (for example a discreet vault revealed while a copy is open, `not-related` written offline, Similar notes for a vault this device doesn't keep, a published note edited offline), the release as a whole, and patterns that recur.
- **The run is on Anthropic's servers, with the model `claude-mythos-5-2`.** Chris's reason: defensive security work is done in the open, where Anthropic can see it.
- **Nine areas, the browser first.** Why: 0.7.0 moves sealed notes into the browser for the first time, so the device, the browser profile and other people using the same browser join the threat model; about 30,000 lines changed since 0.6.0 (41 pull requests), half of them in `packages/eidolon-core` and `web/src/offline`.

## 2. Scope

Every product file of `git diff --name-only v0.6.0..2fee5ad2` outside the tests belongs to an area; the table names the main ones, and the plan's assignment of every file is authoritative. The diff `v0.6.0..2fee5ad2` (`master` on 2026-11-26; staging runs `v0.7.0-pre.2` at `be695c0b`, and the two differ only in end-to-end tests and one sentence of the `synthesize-knowledge` skill), and the existing code it calls into wherever a check happens there (for example `CallerMiddleware`, the vault access check, `Security.cs`, the session's store, `UserKeyRotation`, the crypto under `src/Kumbu.Server/Vaults/`). Code untouched since 0.6.0 is in scope only where a new feature reaches it in a new way.

| # | Area | PRs | Where |
|---|---|---|---|
| A | **The offline copy in the browser**: the sealed store, its keys and wraps, Argon2id, the version counter, unlocking on the sign-in page, the idle lock, writing into the copy, sign-out with changes waiting, several users in one browser, the words search over the copy | #280, #285, #300, #301, #302, #303, #307, #312, #314, #327, #328, #336, #339 | all of `packages/eidolon-core/src/`: `store/` (`crypto.ts`, `mirror.ts`, `vault.ts`, `writing.ts`, `idle.ts`, `choice.ts`, `engines.ts`, `ports.ts`), `mode/`, `read/`, `search/`, `links/` (parsing and resolving links in the copy), `engine/` (the sync engine moved here by #280, and the conflict copies), `api/`, `host/`, `live/`, `globals.d.ts`; all of `web/src/offline/` (`unlock.ts`, `kdf.ts`, `kdfWorker.ts`, `argon2.ts`, `idbStorage.ts`, `mode.ts`, `host.ts`, `engines.ts`, `writing.ts`, `source.ts`, `slots.ts`, `turnOn.ts`, `OfflineBar.tsx`, `OfflineInvite.tsx`, `SignOutDialog.tsx`, `PasswordChangedCopies.tsx`, and the rest); `web/src/api/client.ts` (the client's headers), `web/src/hooks/useSyncSocket.ts`, `web/src/search/` (the words search offline), `web/src/attachments/` (attachments from the copy), `web/src/note/MarkdownEditor.tsx`, `web/src/routes/SignIn.tsx`, `web/src/routes/settings/Offline.tsx`, `web/src/routes/settings/Security.tsx`; ADRs 0002 to 0009, 0011 to 0013, 0022, 0024, 0029, 0031, 0033 |
| B | **The server side of copies**: mirror slots, contacts, the wipe signals, the devices list, removing a copy, deactivation | #282, #307, #311, #314, #328 | `src/Kumbu.Server/Accounts/MirrorSlots.cs`, `Api/MirrorSlotsApi.cs`, `Auth/CallerMiddleware.cs` (the `Kumbu-Mirror` header), `Accounts/Admin.cs`, `Deletion.cs`, `UserKeyRotation.cs`, `Api/AccountApi.cs` (end other sessions, password change), `Sync/SyncHub.cs` (the mirror frame, `4412`), `Accounts/UserDocuments.cs`, `Vaults/EncryptedNoteStore.cs`, `UnlockedVault.cs`, `Api/ApiErrors.cs`; `web/src/routes/settings/DeviceCopies.tsx`, `SettingsWindow.tsx`, `web/src/routes/admin/Users.tsx` (the deactivation); ADRs 0019, 0020 |
| C | **The service worker and the shell**: the app opens offline; only the build's files, checked by hash; the KDF worker's own policy | #279, #311 | `web/sw/` (`worker.ts`, `shell.ts`, `kept.ts`), `web/src/update/` (`serviceWorker.ts`, `apply.ts`), `web/vite.config.ts` (the build's hash list), `web/src/main.tsx`, `src/Kumbu.Server/Api/Security.cs` (`KdfWorkerPolicy`, the CSP), `Program.cs` (static files, `no-cache`); ADR 0021 |
| D | **Similarity, Similar notes and synthesis**: `sh similar`, the graph's edges by meaning, Add link and Not related, the system skill `synthesize-knowledge` | #304, #305, #306, #323, #326, #329, #334, #346 | `src/Kumbu.Core/Search/SimilarSearch.cs`, `PairSimilarity.cs`, `ISearchIndex.cs`, `SectionSearch.cs`; `Links/SuggestionService.cs`, `LinkResolver.cs`; `Sh/Shell.cs`, `Sh/Commands/Similar.cs`, `Exclusions.cs`, `Semsearch.cs` (`--outside`, `--without-tag`), `GraphCommand.cs`; `Graph/`; `src/Kumbu.Server/Api/SimilarApi.cs`, `GraphApi.cs` (`/api/graph/similarity`), `Search/OnnxEmbedder.cs`; `web/src/note/SimilarNotes.tsx`, `BacklinksList.tsx`, `web/src/api/similar.ts`, `web/src/console/` (`help.ts`, `paths.ts`), `web/src/graph/` (`SuggestionCard.tsx`, `similarity.ts`, `suggestions.ts`, `api.ts`); the skill `src/Kumbu.Core/Skills/System/synthesize-knowledge/SKILL.md` (instructions an agent follows while it reads notes other members wrote); ADRs 0010, 0014, 0015, 0016, 0028, 0032 |
| E | **The Published window and Compare** | #309, #310, #330, #333 | `src/Kumbu.Server/Publishing/Publications.cs`, `PublicationApi.cs`; `web/src/published/PublishedPage.tsx`, `web/src/note/CompareCopy.tsx`, `PublicationBar.tsx`, `web/src/api/publications.ts`, `web/src/router.tsx`, `web/src/palette/rows.ts`, `web/src/shell/` (`CompareVersions.tsx`, `TreeDrawer.tsx`, `PublishDialog.tsx`, `publishingItems.ts`, `useNoteActions.tsx`), `web/src/routes/settings/Vaults.tsx` (the list removed there), the history versions a copy keeps (`Kumbu.Core/Notes/`); ADRs 0017, 0018, 0025, 0030 |
| F | **Discreet vaults across the new views** | all | never kept offline (ADR 0013, the contact's list), not in the copy's vault list, the sign-in page's list of copies, the devices list, Similar notes and `similar` hits, the graph's suggestions, the Published window, the offline bar and its notices, the words search over the copy; the shell that shows them (`web/src/App.tsx`, `web/src/shell/` `VaultSwitcher.tsx`, `TreeDrawer.tsx`, `TreeTools.tsx`, `MobileShell.tsx`, `Dock.tsx`); `Api/DiscreetApi.cs` and `web/src/discreet/` where they meet these |
| G | **Resource use** | #304, #305, #306, #311, #336 | `similar` and `similar --between` over every vault, `/api/graph/similarity` (the local graph's parameters: depth up to 3, cap up to 100), the suggestion service, the mirror's first download and its attachment budget, the contact route, the 20-slot cap, the words scan in the tab (the tab only, but a shared vault's writer can grow it) |
| H | **Cross-cutting** | all | the changes to `docs/security-architecture.md` against the code, and whether it states the browser's promises (today its "Offline mirror" entry covers the server's part and says it grows with the browser's); the new dependencies (`hash-wasm`, `@noble/hashes`; `web/package.json`, `packages/eidolon-core/package.json`, `web/index.html`, `web/sw/tsconfig.json`) and the advisories (`dotnet list package --vulnerable --include-transitive`, `pnpm audit` at the root, `pnpm -C pdf audit --ignore-workspace`); the Eidolon core's boundary (`packages/eidolon-core` sees no DOM and imports nothing of `web/`: its `tsc` build checks the first, `boundary.test.ts` the second); that the tests' fast KDF (`web/src/test/fastKdf.ts`, `web/src/offline/testCopy.ts`) never reaches the built app; the small changes the 0.7.0 merges brought into `web/src/calendar/DateDialog.tsx`, `web/src/components/Menu.tsx` and `web/src/shell/MemberPanel.tsx`; **the console's read-only promise** for `similar` (`Api/ConsoleApi.cs`); secrets, tokens, vault and note names in logs, metrics (`Metrics/KumbuMetrics.cs`) and error texts; `Dockerfile`, `.github/workflows/ci.yml`, `.github/dependabot.yml` |
| I | **Earlier findings at this commit** | | Every finding of the offline review, in the state the private file records: still fixed, the fix holding where later 0.7.0 code sits beside it, and the ones still open said to be so. The 0.6.0 findings where 0.7.0 code touches them. Which were fixed where is in the private repository |

The threat model is `docs/security-architecture.md` §2, with the offline mode design §4 for the browser. These are the trust boundaries to check, meaning who may see or do what:

- **Someone who has the device's disk or browser profile, without the password** (a lost laptop, a copied profile): they hold the sealed records and the mirror token, and can only guess the password offline against Argon2id. They read nothing of a note, not its path, not its vault's name; what the header keeps in the clear is all they learn (design §4.1, §4.2).
- **Someone who changes the browser's storage** (another script that once ran in the origin, a tampered profile): they can't make the copy show a note it never had, roll a record back to an older version without it being noticed, or make the service worker serve a changed file (ADR 0007, ADR 0021).
- **Another person who signs in in the same browser** (a shared family laptop): they never see the first person's copy, and the first person's changes waiting stay sealed and upload at their next sign-in (ADRs 0029, 0033).
- **A member whose access ended, a deactivated user, a removed device**: their copy of that vault, or the whole copy, is purged at the next contact, and nothing more syncs; a reactivation brings no copy back (§6 "Offline mirror", ADR 0019).
- **A read-only member, above all Bob, offline**: he writes nothing, not even into the copy, and nothing he did offline reaches the server as a write.
- **A writer in a shared vault**: their note can't make Similar notes, `similar` or the graph show another member anything that member may not read, nor make an agent following `synthesize-knowledge` write outside its report or change other notes (the skill says note content is data); their `not-related` property hides suggestions in their vault only.
- **A holder of an API key, a WebDAV share, a device key or a connection limited to some vaults**: `similar`, `similar --between`, `--outside`, the graph's similarity scores and the suggestions reach only those vaults, at that right, and a refusal reads the same as a note that doesn't exist.
- **A person looking over the shoulder of a user with discreet vaults**: nothing of a hidden vault shows in the new views, offline included; a discreet vault is never kept on a device.
- **Someone without an account who holds a published link**: what the Published window and Compare added changes nothing for them; the list itself holds no reader link.
- **The admin of a shared server and the other users on it**: one user's similarity queries, first downloads or contacts don't deny service to the others.

## 3. The severity scale and the method

Both are unchanged from the 0.6.0 audit (`2026-11-19-security-audit-0-6-0-design.md` §3), which takes them from the 0.4.0 spec §3 and §4 steps 1 to 4:

- the scale is impact times reachability;
- **one fresh reviewer per area**;
- evidence is a behaviour test or a precise trace;
- **an independent second review** of every Medium-or-higher finding and every Low described in public. The second reviewer gets the claim and the code, not the first reviewer's reasoning;
- a Low described in public may only be confirmed, rated up or refuted, never rated down;
- **a severity check across the resource findings**: every resource-use finding of Medium or higher is measured against a control (what another user sees while the slow calls run). Where a doubt towards High remains, the measurement that would settle it is written down.

**Rating the browser's findings.** A weakness that needs the device's disk without the password is rated by what it gives that holder beyond guessing the password (design §4.2: the device is the user's to protect, as with the Obsidian plugin). A weakness that needs script running in the origin is rated by what it adds to what such a script can do anyway while the copy is unlocked; ADR 0021 is the one place where 0.7.0 promises more.

The per-PR reviews of 0.7.0 are in each pull request's description and in its spec, and the decisions in `docs/adr/`. A point they list as accepted is not new. A point they list as fixed that isn't fixed is new.

## 4. Where the findings go

As in the 0.6.0 audit's §4, with the names changed:

- **Confidential findings** (Medium and higher) go to the private repository `SCAI-NGO/kumbukumbu-security`: branch `audit/0-7-0`, file `audits/2026-11-26-0.7.0.md`, the tests under `audits/2026-11-26-0.7.0/`, with a pull request there.
- **The public report** goes to `docs/superpowers/overnighter/2026-11-26-security-audit-report.md` on the public branch `audit/0-7-0-security`. It holds the counts per severity, the Lows in full, and each confidential finding by id and severity only.
- **Ids** go on from the offline review's last ones, so the version's ids never collide: `S7-NN` and `L7-NN`, starting after the highest of each in the private file.
- **A finding of the offline review or of 0.6.0** still open, or whose fix doesn't hold, is reported under its own id, in the private file only.
- **A weakness in code older than 0.7.0** that a new feature doesn't reach is reported too, under its own heading, "Older than 0.7.0", and counted apart from the version's findings. Why: an honest account of what was found matters more than a clean count, and the fix run decides its order like any other.
- **A Medium-or-higher finding that the second review refutes or rates down** stays in the private file, with the verdict. A Low beside a confidential finding stays in the private file too, if describing it would point to that finding.
- **Each finding** gives:
  - id, title and area;
  - severity, with one sentence on the impact and one on how reachable it is;
  - the trust boundary it crosses;
  - the evidence;
  - the second review's verdict;
  - the proposed fix, with the tests that should prove it;
  - whether it needs a decision from Chris.

The publication rule holds: no detail of a Medium-or-higher weakness reaches a public branch, a commit message, a pull request or the Factory card before its fix is released.

## 5. No fixes

The run changes no product code, public or private. Its only commits are:
- on the public branch: the plan, `docs/superpowers/open-items.md` and the report;
- in the private repository: `audits/`.

Tests that show a Medium-or-higher weakness are written and run in a separate worktree outside the public clone (`git worktree add <scratch>/audit-run 2fee5ad2`), which is never committed from. Their source of truth is the private clone.

## 6. A High or Critical finding

As soon as the second review confirms it:
1. write it in full into the private file and push it;
2. send a `PushNotification`: "Security review 0.7.0: a High (or Critical) finding, S7-NN, details in the private pull request". It names no area and no detail;
3. move the run's card on the Factory board to **Waiting for Chris**, with the id, the severity and "Fix S7-NN now? Recommendation: yes, details in the private pull request";
4. carry on with the audit.

## 7. End-to-end example

A made-up case, to show the steps; it is not a known weakness.

1. **Area D.** The promise (`docs/security-architecture.md` §4): a key limited to some vaults learns nothing of the others, and a refusal reads the same as for a note that doesn't exist.
2. **The test.** The reviewer writes a behaviour test: "`similar --between /Work/A.md /Home/B.md` with a key limited to `Work` answers as if `/Home/B.md` didn't exist". Say it fails: the answer is a different refusal than for a path that doesn't exist, which tells the key that the note exists.
3. **The rating.**
   - Impact: that a note of that path exists in a vault the key can't open, nothing of its text.
   - Reachability: any key limited to some vaults, used on a path it can guess.
   - Result: **Low**, with the doubt written down. Had a section's text, a score against a hidden note or a discreet vault's name come through, the rating would be Medium or higher.
4. **The second review.** A fresh reviewer gets the claim and the code, reproduces it, and confirms it.
5. **Where it goes.** The finding goes into the public report as `L7-NN`, with the proposed fix (the same "not found" answer whatever the reason) and the test that should prove it. The run fixes nothing.

## 8. Out of scope

- Fixing anything (§5).
- Testing against staging, production or any real host, including TensorX and huggingface.co: the embedding model is replaced by the tests' stub embedder, and similarity is reviewed in code and against stored vectors the tests write. The end-to-end tests with search by meaning (`@search`) need the model and stay out; Chris runs them on the laptop before the release.
- Real devices and real browsers' storage quotas: the browser side is reviewed in code and with the Eidolon core's and the web app's tests (fakes for IndexedDB and the KDF), and where a run needs a real browser, Playwright against a stack the run starts itself.
- The Obsidian plugin (its vendored copy of the Eidolon core's sync part is checked against a commit in its own repository), the deployment hosts, Komodo, the image signing.
- Dependency upgrades: a vulnerable package is a finding, and its upgrade goes to the fix run.
- Code untouched since 0.6.0, except where §2 says it is in scope.
- The 0.8.0 work (queries: ADR 0023 on `master`, and the branch `release/0.8.0`), not part of this release.
- Changing a design decision of an earlier spec or an ADR. If one looks unsafe, it becomes a finding with "needs a decision from Chris". Examples of such decisions:
  - the copy unlocks offline by the account password alone, with no second factor (ADR 0005);
  - a password change keeps the other devices' copies, which still open with the old password until they come online (ADR 0008);
  - unconfirmed members see similarity scores in a vault they may read (ADR 0015);
  - `not-related` is an ordinary note property, which any writer of the vault can change (ADR 0014);
  - another user's token is refused, not wiped (ADR 0020);
  - a browser that never comes online again is never wiped (`docs/security-architecture.md` §6, "Offline mirror").

## 9. Security boundary

The audit reads everything and changes no product code. What it may write:
- the plan, `docs/superpowers/open-items.md` and the public report on `audit/0-7-0-security`;
- `audits/` in the private repository;
- its own card on the Factory board, through the `Kumbu_Factory` connector, which can reach that vault only.

Nothing about a confidential finding may go to a public branch, a commit message, a pull request or the Factory card.

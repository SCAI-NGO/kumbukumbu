# Security audit of 0.6.0

Status: draft, for Chris's review (the scope itself agreed on 2026-11-19).

## 1. Goal and decisions

Before 0.6.0 is released, an independent **defensive** review of everything that changed since 0.5.0. It checks the new code against the security promises of `docs/security-architecture.md` and the features' specs, and rates each weakness on the scale of the 0.4.0 audit.

**Blue team only.** This is code review and defensive testing of our own code, and Chris is its maintainer.
- No exploit development, no attack payloads beyond the ordinary untrusted inputs a unit test feeds a parser, no attack chains, nothing run against a live system.
- A weakness is shown by a test of the behaviour the code **should** have, which fails today, or by a precise trace through the code. An example of such a test: "a second `PUT` to an upload link is refused".
- Ordinary unit-test inputs are, as in the 0.5.0 audit:
  - the standard sanitiser test strings (`<script>`, `javascript:` links, event-handler attributes, SVG `foreignObject`, CSS `url()`);
  - an XML document with a DTD;
  - bounded oversized or deeply nested inputs (YAML, CSV, HTML, XML, SVG, Markdown).

  Each test asserts that the code neutralises or limits them.

Chris: blue-team work is what we do.

Decisions, each with Chris's reason:

- **This run only finds and reports; it fixes nothing.** Its findings go into **one** fix run together with the findings of the 0.6.0 tests (`docs/superpowers/specs/2026-11-19-tests-0-6-0-design.md`, on the branch `feature/tests-0-6-0`) and Chris's hand tests. Chris's reason, as for 0.5.0: one fix run means fewer merge conflicts, one review and one pre-release to test.
- **The exception is a High or Critical finding.** Once the second review confirms it, it is reported at once (§6) and fixed right away, outside this run.
- **The policy stays:** Medium and up is fixed before 0.6.0. A High is fixed at once. A Low is fixed only if it is quick; the rest go into 0.7.0's preparations.
- **Each 0.6.0 pull request had its own security review of its diff.** This audit looks at what lies between them: features meeting each other (for example a large file through Publish 2.0, `blame` in a discreet vault, an upload link and a shared vault's rights), the release as a whole, and patterns that recur.
- **The run is on Anthropic's servers, with the model `claude-mythos-5-2`.** Chris's reason: defensive security work is done in the open, where Anthropic can see it.
- **Larger than 0.5.0's, in nine areas.** Why: 0.6.0 is the largest version so far (about 28,000 lines in `src` and `web/src`, 41 pull requests) and opens new ways in and out: an upload link that works without a session, arbitrary file types, files up to 1 GiB, three new parsers, and a walk over links that publishes.

## 2. Scope

The diff `v0.5.0..v0.6.0-pre.4` (`63d3ca46`, which staging runs), and the existing code it calls into wherever a check happens there (for example `CallerMiddleware`, the vault access check, `Security.cs`, the session's store, the crypto under `src/Kumbu.Server/Vaults/`). Code untouched since 0.5.0 is in scope only where a new feature reaches it in a new way.

| # | Area | PRs | Where |
|---|---|---|---|
| A | **Files in and out**: every file type, served only as a download; large files in sealed chunks, streamed | #254, #257, #259 | `src/Kumbu.Server/Api/FileApi.cs`, `UploadApi.cs`, `NoteApi.cs` (the tree), `ApiErrors.cs`, `SyncApi.cs` (`size`); `src/Kumbu.Server/Vaults/` (`LargeBlobs.cs`, `BlobSweep.cs`, `UnlockedVault.cs`, `VaultCatalog.cs`, `VaultCatalog.Rekey.cs`, `Documents.cs`, `Credentials.cs`, `EncryptedNoteStore.cs`); the body-size limits for 1 GiB (`Program.cs`, `KumbuOptions.cs`, `KumbuOptionsValidator.cs`); `src/Kumbu.Core/Attachments/`; `src/Kumbu.Server/Dav/`; `Skills/SkillUpdateTaker.cs`; `web/src/attachments/`, `web/src/upload/`, the ZIP import in `web/src/data/`; the response headers (Content-Disposition, `nosniff`, CSP) on every way out |
| B | **Agents' uploads**: a one-time link that works without a session | #230 | `src/Kumbu.Server/Mcp/UploadTool.cs`, `Api/AgentUploadApi.cs`, `Api/UploadTickets.cs`, `Auth/CallerMiddleware.cs` (rewritten here: the token route, the HTTPS check, credential resolution), `Auth/WrongKeyLimiter.cs`, `OAuthUrls`, the system skill `kumbu-binary-upload`, `KUMBU_PUBLIC_URL` and the proxy's trust |
| C | **Parsers and rendering**: `csv`, `html`, `xml`; SVG; templates and properties; Mermaid from a data URL; PDF crops | #228, #242, #245, #255, #260 | `src/Kumbu.Core/Sh/Commands/Csv*.cs`, `Html.cs`, `CssSelection.cs` (the selector parser), `Xml.cs`, `src/Kumbu.Core/Attachments/SvgImage.cs`, `src/Kumbu.Server/Attachments/ImageMetadata.cs`, `pdf/src/` (SVG in the sidecar's child process, and #245's `extract.ts`, `render.ts`, `worker.ts`), `web/src/note/` (templates, the properties box's YAML), `web/src/reader/` |
| D | **Publish 2.0 and deep links** | #229, #235 | `src/Kumbu.Server/Publishing/` (`Bundles.cs`, `Publications.cs`, `PublicationApi.cs`, `ReaderApi.cs`, `ShellPublisher.cs`, `AiLinkApi.cs`), `src/Kumbu.Core/Publishing/FairCopies.cs`, `Sh/Commands/PublishCommand.cs`, `src/Kumbu.Server/Mcp/DeepLinks.cs`, the "Not here" page, `web/src/reader/` |
| E | **Sharing and who wrote what**: `blame`, history messages, start-up skills and the system skills | #223, #241, #250, #253 | `src/Kumbu.Core/Notes/Authorship.cs`, `NoteHistory.cs`, `Sh/Commands/Blame.cs`, `Api/HistoryApi.cs`, `web/src/blame/`, `src/Kumbu.Server/Mcp/HandshakeInstructions.cs`, `src/Kumbu.Core/Skills/` (`SkillDirectory.cs`, `SkillInstructions.cs`), the system skills `project-memory` and `ingest-attachments` (instructions agents follow), `Sync/SyncHub.cs` (#250) |
| F | **Discreet vaults across the new views**, and the windows' new lifecycle for passwords and recovery keys | #232, #236, #237, #238, #239, #244, #248, #251, #252, #256, #258 | the palette and favourites (`Accounts/Favourites.cs`, `Accounts/UserDocuments.cs`, `Api/FavouritesApi.cs`, `web/src/palette/`), split view and `?beside=`, the side panel, the windows (`web/src/windows/`, with `PasswordPrompt`, `RecoveryKeyReveal` and `ConfirmTyped` moved into them), `web/src/routes/admin/` and its filters, the console's completion, deep links, `blame`, graph, Recent, `Api/DiscreetApi.cs`, `web/src/discreet/` |
| G | **Resource use**: the link index and graph, the new `sh` options, the embedding queue | #223, #224, #227, #233, #243, #245, #246, #247, #249 | `src/Kumbu.Core/Graph/`, `Api/GraphApi.cs`, `Sh/Commands/GraphCommand.cs`, `Sh/Glob.cs` (`rg -g`), `Rg.cs` (`--regions`), `Sections/SectionAddress.cs`, `cat`/`rm`/`mv` with several paths, `ls -l`, `calendar --from/--to`, `src/Kumbu.Server/Search/` (the queue) |
| H | **BYOK and cross-cutting** | #209, #234, all | `src/Kumbu.Server/Search/Byok.cs`, `ByokDrivers.cs`, `Api/EmbeddingsKeyApi.cs`, `Accounts/EmbeddingsKeys.cs`, `Accounts/UserKeyRotation.cs` (the BYOK key re-sealed when the account's key rotates), `Vaults/Credentials.cs`, `Vaults/VaultCatalog.Members.cs`, `Sharing/SharingApi.cs`, `Api/AccountApi.cs`, `KumbuOptions.cs`, `web/src/routes/settings/` (sealing, the key check, logs, where the request may go); **the console's read-only promise for every new command** (`Api/ConsoleApi.cs`; `blame`, `graph`, `csv`, `html`, `xml`, `publish --dry-run`, completion); dependencies after the majors (`dotnet list package --vulnerable --include-transitive`, `pnpm -C web audit`, `pnpm -C pdf audit`); secrets, vault and note names in logs, metrics and error texts; workflows and `.dockerignore` |
| I | **The 0.5.0 findings at this tag** | | Every S5 and L5 finding: still fixed, and the fix still holding where 0.6.0 code sits beside it. Which ones were still open after the 0.5.0 fix runs, and where they were finished, is in the private repository |

The threat model is `docs/security-architecture.md` §2. These are the trust boundaries to check, meaning who may see or do what:

- **Someone without an account who holds an upload link:** one file at the one path the link was made for, once, within ten minutes, over HTTPS only (plain HTTP spends the link), stored as the key or connection that asked for it and with that credential's rights, and nothing more. They learn nothing about the vault (`docs/security-architecture.md` §4 and §6).
- **Someone without an account who holds a published link or an AI link:** they may read that copy and nothing more. In particular they never read a note left out, and never learn that a note was left out.
- **A user with an account but no access to the vault:** they see nothing of it, a deep link to it included. "Not here" is the same page whether the note was moved, was removed or was never theirs.
- **A member with a lower role, above all read-only Bob:** he changes nothing, and through `blame` and history he learns nothing his role doesn't let him read.
- **A writer in a shared vault:** their skill doesn't start other members' agents, their file doesn't run in a reader's browser, and their Markdown, SVG, CSV, HTML, XML or template doesn't tie up the server or the reader's tab beyond the stated bounds.
- **A holder of an API key, a WebDAV share, a device key or a connection limited to some vaults:** only those vaults, at that right. This holds for the new tools (`upload`, `blame`, `graph`, the readers) and for large files.
- **A person looking over the shoulder of a user with discreet vaults:** nothing of a hidden vault shows, in the new views either.
- **The admin of a shared server and the other users on it:** a user's BYOK key stays theirs and sealed. One user's large uploads, graph queries or embedding backlog don't deny service to the others.

## 3. The severity scale and the method

Both are unchanged from the 0.5.0 audit (`2026-11-14-security-audit-0-5-0-design.md` §3), which takes them from the 0.4.0 spec §3 and §4 steps 1 to 4:

- the scale is impact times reachability;
- **one fresh reviewer per area**;
- evidence is a behaviour test or a precise trace;
- **an independent second review** of every Medium-or-higher finding and every Low described in public. The second reviewer gets the claim and the code, not the first reviewer's reasoning;
- a Low described in public may only be confirmed, rated up or refuted, never rated down.

New is **a severity check across the resource findings**, as decision (a) of the 0.5.0 file did. Every resource-use finding of Medium or higher is measured against a control: what another user sees while the slow calls run. Where a doubt towards High remains, the measurement that would settle it is written down.

The per-PR reviews of 0.6.0 are in each pull request's description and in its spec. A point they list as accepted is not new. A point they list as fixed that isn't fixed is new.

## 4. Where the findings go

The 0.5.0 audit's §4, with the names changed:

- **Confidential findings** (Medium and higher) go to the private repository `SCAI-NGO/kumbukumbu-security`: branch `audit/0-6-0`, file `audits/2026-11-19-0.6.0.md`, the tests under `audits/2026-11-19-0.6.0/`, with a pull request there.
- **The public report** goes to `docs/superpowers/overnighter/2026-11-19-security-audit-report.md` on the public branch `audit/0-6-0-security`. It holds the counts per severity, the Lows in full, and each confidential finding by id and severity only.
- **Ids** are `S6-NN` and `L6-NN`, numbered afresh.
- **A 0.5.0 finding** still open, or whose fix doesn't hold, is reported under its 0.5.0 id, in the private file only.
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

Tests that show a Medium-or-higher weakness are written and run in a separate worktree outside the public clone (`git worktree add <scratch>/audit-run v0.6.0-pre.4`), which is never committed from. Their source of truth is the private clone.

## 6. A High or Critical finding

As soon as the second review confirms it:
1. write it in full into the private file and push it;
2. send a `PushNotification`: "Security review 0.6.0: a High (or Critical) finding, S6-NN, details in the private pull request". It names no area and no detail;
3. move the run's card on the Factory board to **Waiting for Chris**, with the id, the severity and "Fix S6-NN now? Recommendation: yes, details in the private pull request";
4. carry on with the audit.

## 7. End-to-end example

A made-up case, to show the steps; it is not a known weakness.

1. **Area G.** The promise (`docs/security-architecture.md` §4): a key limited to some vaults learns nothing of the others, and a refusal reads the same as for a note that doesn't exist.
2. **The test.** The reviewer writes a behaviour test: "`graph --path A B` with a key limited to `Work`, where B lies in `Home`, answers as if B didn't exist". Say it fails: the refusal names `Home`.
3. **The rating.**
   - Impact: the name of a vault the key can't open, nothing of its notes.
   - Reachability: any key limited to some vaults, used on a note path it can guess.
   - Result: **Low**, with the doubt written down. Had a note's text or a discreet vault's name come through, the rating would be Medium or higher.
4. **The second review.** A fresh reviewer gets the claim and the code, reproduces it, and confirms it.
5. **Where it goes.** The finding goes into the public report as `L6-NN`, with the proposed fix (the same "not found" answer whatever the reason) and the test that should prove it. The run fixes nothing.

## 8. Out of scope

- Fixing anything (§5).
- Testing against staging, production or any real host, including TensorX: the BYOK key check is reviewed in code, and a stub stands in for the provider.
- The Obsidian plugin (its 0.3.1 change was checked in its own pull request), the deployment hosts, Komodo, the image signing.
- Dependency upgrades: a vulnerable package is a finding, and its upgrade goes to the fix run.
- Code untouched since 0.5.0, except where §2 says it is in scope.
- Changing a design decision of an earlier spec. If one looks unsafe, it becomes a finding with "needs a decision from Chris". Examples of such decisions:
  - SVG as an image only, with no sanitiser in 0.6.0;
  - agents, WebDAV and sync seeing discreet vaults;
  - `blame` showing who wrote what to whoever may read the history.

## 9. Security boundary

The audit reads everything and changes no product code. What it may write:
- the plan, `docs/superpowers/open-items.md` and the public report on `audit/0-6-0-security`;
- `audits/` in the private repository;
- its own card on the Factory board, through the `Kumbu_Factory` connector, which can reach that vault only.

Nothing about a confidential finding may go to a public branch, a commit message, a pull request or the Factory card.

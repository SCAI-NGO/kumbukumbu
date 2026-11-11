# Security audit of 0.4.0

Status: reviewed and approved by Chris, 2026-11-10.

## 1. Goal

Before 0.4.0 is released, an independent audit of everything that changed since 0.3.2: find what an attacker could do with the new features, rate each finding on one agreed scale, and fix what needs no decision. The result decides the release: Critical, High and Medium findings are fixed before 0.4.0; Low findings if they are quick, the rest is kept for 0.5.0. This run fixes Low and Medium findings within the limits of §6; everything else goes to a fix overnighter after it, and every finding, fixed or not, is reported and reviewed by Chris afterwards.

Chris, on the hybrid: the run may fix Mediums within the limits, "aber sie werden trotzdem reported und wir bearbeiten sie nach"; it saves time, and with luck 0.4.0 is ready for release after the review and the confidential findings stay empty.

Chris, on why: "medium aufwärts sollten wir in jedem fall mitnehmen, bei low eher ‚was noch schnell geht' mitnehmen, aber ansonsten als ‚für 0.5.0 aufheben'". There is time: the release target is Sunday, a release on Thursday is fine. Quality comes before a day earlier.

## 2. Scope

The diff `v0.3.2..v0.4.0-pre.5` (`9864ff7`, the code staging runs and Chris hand-tested), and the existing code it calls into wherever that is where a check happens (for example `CallerMiddleware`, the slot derivations, the vault access check). Code untouched since 0.3.2 is in scope only where a new feature reaches it in a new way.

The areas, with the pull requests that brought them:

| # | Area | PRs | Where |
|---|---|---|---|
| A | WebDAV and the storage quota | #152, #175 | `src/Kumbu.Server/Dav/`, `Accounts/DavShares.cs`, `Accounts/DavTokens.cs`, `Accounts/StorageQuota.cs`, `Auth/CallerMiddleware.cs`, the quota hooks in every write path |
| B | Skills | #153 | `src/Kumbu.Server/Skills/` (the `/k` page, `GitHubClient.cs`, marketplaces, updates, `SkillZip.cs`, `SkillCopier.cs`), `src/Kumbu.Core/Skills/`, `Mcp/HandshakeInstructions.cs`, cross-vault copy and move, `web/src/skills/`, `web/src/skillpage/` |
| C | Ingesting attachments | #176 | `src/Kumbu.Core/Sh/Commands/PdfImages.cs`, `PdfCommand.cs`, `src/Kumbu.Core/Attachments/`, `src/Kumbu.Server/Attachments/` (`PdfSidecar.cs`, `ExtractionGate.cs`, `AttachmentPixels.cs`), the PDF sidecar package `pdf/src/`, the system skill |
| D | Search, structural chunking | #177, #184 | `src/Kumbu.Core/Search/` |
| E | Discreet vaults | #179, #182, #184 | `Accounts/DiscreetVaults.cs`, `Accounts/Sessions.cs`, `Api/LateSave.cs`, the filters of spec `2026-11-09-discreet-vaults-design.md` §3.4, `web/src/discreet/` |
| F | Web app rendering | #178, #183 | the remark plugins (highlight, comments, footnotes), KaTeX, the reader page and `web/s.html`, `web/src/note/`, the CSP |
| G | Cross-cutting | all | dependencies (`dotnet list package --vulnerable --include-transitive`, `pnpm -C web audit`, `pnpm -C pdf audit`), secrets or vault and note names in logs and metrics (`Metrics/KumbuMetrics.cs`), error texts, response headers, `KumbuOptions.cs` defaults |

The threat model is `docs/security-architecture.md` §2. The attackers to think as: someone without an account; a user with an account but no access to the vault; a member with a lower role; a holder of a WebDAV share, an API key, a device key or a connection limited to some vaults; a holder of a published link or a skill link; a malicious skill marketplace or GitHub repository; a malicious file (PDF, image, ZIP, Markdown); a person looking over the shoulder of a user with discreet vaults.

## 3. The severity scale

Severity is impact times exploitability, as CVSS reckons it, not the blast radius alone. **Impact:** whose data (one user, one vault, every user) and what the attacker can do (read, change, delete, run code). **Exploitability:** does it need an account, a role, the victim's help (a click), a lucky moment?

| Severity | Pattern | Kumbu² examples |
|---|---|---|
| **Critical** | no sign-in or any account; big damage; easy | reading another user's vault through the API; bypassing authentication on MCP or WebDAV |
| **High** | big damage behind one hurdle (an account, the victim's click) | stored XSS in a shared note that takes the session; a member escaping their role |
| **Medium** | limited damage, or real hurdles | a discreet vault's name in a log; no rate limit on the PIN |
| **Low** | hardly exploitable, or hardly harmful | a missing security header; the version in an error message |

Every finding names its severity with one sentence for the impact and one for the exploitability. When in doubt between two levels, the higher one, with the doubt written down.

## 4. How the audit works

1. **Threat model per area** (§2's table): the assets, the entry points, the attackers who reach them, and what the design promises (the area's spec under `docs/superpowers/specs/`, `docs/security-architecture.md`).
2. **Review** of each area by its own fresh subagent, against that model and the usual classes: authentication and authorisation (every new endpoint, every new credential kind, every filter), path handling and traversal, injection (process arguments, XML, Markdown to HTML, MongoDB queries), SSRF and outbound requests, resource exhaustion (archives, images, PDFs, XML, large tables, PROPFIND depth), race conditions, information leaks (existence of vaults and notes, timing, error texts, logs), cryptography (only where new slots or derivations were added), CSRF and origin checks, the CSP.
3. **Proof.** A finding counts once it is shown: by a test against the code (xUnit or Vitest, run locally, never against a real host), or by a precise trace through the code where a test isn't possible. An unproven suspicion goes into the report as such, with what would prove it.
4. **Adversarial check.** Each Medium-or-higher finding is checked by a second fresh subagent that tries to disprove it, given the code and the claim, not the first agent's reasoning. Every Low that will be described in public gets the same check, which may only rate it up (a Low rated too low would put a Medium's details in public). The verdict and the final severity go into the report.
5. **Quick fixes** (§6), only after the audit is complete, so that no finding is rated down because it is about to be fixed.

## 5. Where the findings go

The repository is public. Details of a finding of Medium or higher (the way to exploit it, a proof, the exact place) must not reach a public branch, commit message or pull request before its fix is released, **whether or not this run fixed it**.

- **Confidential findings** (Medium and higher, fixed or not): the private repository `SCAI-NGO/kumbukumbu-security` (decided by Chris; it exists, with a README). The run writes `audits/2026-11-10-0.4.0.md` there, and the proof tests under `audits/2026-11-10-0.4.0/`, on the branch `audit/0-4-0` of that repository, and opens a pull request there too. *Rejected:* only in the session's final message (not versioned, easy to lose); GitHub security advisories (rights the run probably lacks).
- **The public report** (`docs/superpowers/overnighter/2026-11-10-security-audit-report.md`, on this branch): the environment, what was audited and how, the counts per severity (not per area), every Low finding in full with its fix, F11, and for each confidential finding only its id, its severity and fixed or not ("S-03, Medium, fixed: details in the confidential findings"). No area, no commit, no test name, no mutation of a confidential finding; the commits, test output and mutations of Medium fixes go into the confidential findings, and the public pull request doesn't list Medium fixes as fixes.
- Proof tests of confidential findings go with them, never onto the public branch. A Medium fixed here keeps a regression test on the public branch, written so that it reads as an ordinary behaviour test (what the code must do, not how to break it), with a neutral commit message ("Check the share's vaults when listing the WebDAV root"). Proof tests of Low findings may be committed with their fix.
- Each finding, wherever it is written: id, title, area, severity with its two sentences, the attacker, the proof (test or trace), the adversarial verdict, a proposed fix and the tests that should prove it, and whether it needs a decision from Chris.

## 6. Quick fixes in this run

After the audit, the run fixes findings that are all of these:

- Low or Medium (decided by Chris: the hybrid of §1; Critical and High are never fixed in this run);
- need no decision: there is one obvious right behaviour, and nothing a user sees changes beyond the fault itself;
- outside authentication, sessions, cryptography, sharing and roles, and the semantics of discreet vaults. **Allowed** is adding a missing check or filter that only narrows what a caller sees or may do, without changing how a credential is verified or a role is granted. **Not touched**, whatever the finding: `src/Kumbu.Server/Auth/CallerMiddleware.cs`, `Accounts/Sessions.cs`, `Accounts/DavTokens.cs`, `Accounts/DiscreetVaults.cs`, `Api/LateSave.cs`, `src/Kumbu.Server/Sharing/`, and anything that derives keys or slots. When unsure whether a fix is inside these limits, it isn't;
- small: one commit with its red test, then green.

And **F11** from the pre.5 tests, decided by Chris: once a page is reloaded, the tab no longer knows that a vault was hidden (the hidden vaults are kept in memory only), so a bookmark of a hidden note shows the heading "Heute" and "There is no note at /Tagebuch/Heute.md (if it was removed, it is in the trash)." Wanted: every 404 for a note in a vault the user's list doesn't name says "There is no such note (if it was removed, it is in the trash)." (`NO_SUCH_NOTE`, `web/src/discreet/errorText.ts:9`) and shows no heading made from the path. A 404 in a vault the list does name keeps today's text, since the user can see that vault anyway.

Every fix: a red test first, the full gate after it, listed in the report. A Low fix carries its finding's id in the commit message's body; a Medium fix carries nothing that points to the weakness (§5).

**Each Medium fix is reviewed** by a fresh subagent that gets the confidential finding, the fix's diff and the code, not the fixer's reasoning, and answers: does it close the weakness for every attacker of §2, does it open a new one, does it change anything a user sees beyond the fault? A Medium fix is committed locally and **pushed only after this review confirms it**. If the review fails, the unpushed commit is dropped (`git reset --hard HEAD~1`; it was never pushed, so this is no force-push), the finding stays unfixed, and the verdict goes into the confidential findings. `git revert` only for commits already pushed.

## 7. End-to-end example

1. Area A. The agent for WebDAV models a share holder with `read` on `Work` only, and asks whether a `PROPFIND` with `Depth: 1` on `/dav/` lists vaults outside the share.
2. It writes a test in `tests/Kumbu.Server.Tests/Dav/` that makes such a share for Alice and sends the request. Say the answer lists `Home`: the test fails, which proves it.
3. Rating: impact, the names of a user's other vaults (not their content); exploitability, needs a share the user made. **Medium.**
4. A second agent, given the claim and the code, tries to disprove it (perhaps the names come from the share's own list). It confirms.
5. It goes into the confidential findings as S-01 with the test's code, the proposed fix (filter the root listing by the share's vaults) and the tests that should prove it. The proof test goes to `audits/2026-11-10-0.4.0/` in the private repository, not to the public branch.
6. After the audit is complete: S-01 meets every point of §6. Filtering the root listing only narrows what the share holder sees; it changes nothing in how the share's password is checked (`DavTokens.cs`, `CallerMiddleware.cs` stay as they are) and lies in the listing code of `src/Kumbu.Server/Dav/`. The run writes a behaviour test ("a share's root lists only its vaults"), sees it fail, filters the listing, sees it pass, runs the gate, commits "Check the share's vaults when listing the WebDAV root" locally. A fresh subagent reviews the fix against S-01 and confirms it; only then is it pushed. The public report says "S-01, Medium, fixed"; the confidential findings carry the details, the commit, the test output, the mutations and the review's verdict.
7. Later, area G finds that `/dav/` answers with a `Server` header naming the framework's version: Low, no decision, a header. After the audit the run removes it with a red test first, commits "Leave the framework's version out of WebDAV answers" with "L-02" in the body, and the public report describes it in full.

## 8. Out of scope

- Fixing Critical or High findings, and Mediums outside §6's limits (the fix overnighter after this one does that).
- Testing against staging, production or any real host: everything runs locally, against the test server and the test MongoDB.
- The Obsidian plugin (another repository), the deployment hosts, Komodo, the images' signing.
- Dependency upgrades: a vulnerable package is a finding; the upgrade itself goes to the fix overnighter (or to Dependabot).
- Code untouched since 0.3.2, except where §2 says it is in scope.
- Changing a design decision of an earlier spec; if one looks unsafe, it is a finding with "needs a decision from Chris".

## 9. Security boundary

The audit reads everything. **What the run may change:** only the fixes of §6, and F11 in the web app; in the private repository, only its `audits/` folder. **Not touched:** authentication, sessions and the password window, the PIN check and its rate limit, cryptography and the slots, sharing and roles, the late-save rule, WebDAV's authentication, the CSP's policy (unless a Low finding is a missing header outside it), the version; and the files §6 names. A fix that turns out to need any of these goes back to the findings, unfixed.

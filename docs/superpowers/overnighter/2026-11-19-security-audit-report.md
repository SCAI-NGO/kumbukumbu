# Overnighter report: the security review of 0.6.0

Run of 2026-11-19 on the brief `docs/superpowers/overnighter/2026-11-19-security-audit-brief.md` and the spec `docs/superpowers/specs/2026-11-19-security-audit-0-6-0-design.md`; plan `docs/superpowers/plans/2026-11-19-security-audit.md`. Branch `audit/0-6-0-security`. The code reviewed is `v0.6.0-pre.4` (`63d3ca46`) against `v0.5.0`. Nothing was fixed, merged, tagged or deployed; no product code and no version changed.

This report is public. Findings of Medium or higher are named here only by id and severity; their details are in the private repository's pull request (link at the end). Nineteen Low findings are also named by id only, because describing them here would point towards a confidential finding or because their rating carries a doubt towards Medium (decision 8).

## 1. Environment

- A cloud container with 4 cores and 15 GB of memory, shared by every agent of the run.
- .NET SDK 10.0.404 (`global.json`: 10.0.100, roll forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- Node 26.13.1 was in `/opt/node26` from the environment's setup script. **Installed by hand:** pnpm 10.32.1 (`npm install -g pnpm@10.32.1`; the image had 10.28.0). Nothing else was installed, and no dependency was added to the project. The sidecar's native canvas binary installed without trouble.
- Docker 29.10.0; `dockerd` was not running and was started. Test MongoDB `mongo:9` as a single-node replica set, as the brief gives it, with the capped log. One container served every reviewer; `TESTCONTAINERS_RYUK_DISABLED=true`.
- **One heavy run at a time:** every build and test run of every agent took its turn under one file lock (`flock`), with a 20-minute time limit per command. This kept the test container alive throughout (no restart was needed), and it was the run's main cost in time: reviewers waited for each other. Several times an idle MSBuild node left behind by a build held the lock for 15 to 30 minutes, because such a node inherits the lock's file; from then on reviewers ran with `MSBUILDDISABLENODEREUSE=1`.
- The superpowers skills were available. The plan follows the shape of `superpowers:writing-plans`; the reviews were run subagent-driven with the plain Agent tool: one fresh agent per area review, per second review, per third review and for the severity check, each in its own worktree of `v0.6.0-pre.4` outside the public clone (`/home/user/audit-scratch/<name>`), none of which was committed from, and each removed when its report was in the private clone.
- The Factory board connector was available; the card "Security review 0.6.0" carried the phases and a log.
- Nothing ran against a real host. Every request went to the in-process test server, a Kestrel on the loopback interface, the test MongoDB or a stub; no AI provider was contacted. Outbound requests were the package restores and the read-only advisory lookups (`dotnet list package --vulnerable --include-transitive` against NuGet, `pnpm -C web audit` and `pnpm -C pdf audit` against the npm registry); all of them worked behind the proxy.

## 2. The plan and its review

The plan was reviewed by a fresh agent that got the spec, the brief and the plan, not the controller's conclusions. It found that every file and test class the plan names exists at the tag and that the plan gives nothing away, and it raised fifteen points. All were folded in before the plan was committed, one of them in part:

1. Phase 5 contradicted itself on the order of the report and the pull request: rewritten (the pull request is opened on the plan commit, the report follows).
2. The brief's open point 1 (the private repository unreachable) was missing: added.
3. A test filter such as `*UploadApiTests` runs more classes than it names: filters carry the namespace; and one runner start per class would not fit 20 minutes: said so.
4. The lock alone doesn't make nine parallel reviewers feasible: at most four area reviewers at a time, a time limit per heavy command, no build servers left running, measurements under the lock, worktrees removed. Adopted in part: later in the run more than four agents ran at once (decision 2).
5. Files of the diff that no area named: each assigned to an area in the plan.
6. Changed test classes missing from the baseline: added.
7. The blame endpoint's place was named wrongly: corrected.
8. Each crossing of features needs one owner: assigned.
9. No time budget per phase: added.
10. The final check against the publication rule needs to know what is confidential: it gets the private file's summary.
11. The Factory card's set-up and rules were incomplete: completed.
12. The notification's exact words for spec §6, and what to do without notifications: added.
13. Smaller rules of the brief the plan didn't carry (no force-push, the commit trailer, a container restart, a vulnerable dependency's rating, scope questions): added.
14. Two wordings: corrected.
15. Sound as written: the phases, the order of the areas, the second review, the severity check, both pull requests, "do not merge".

The plan was the only public commit before this report.

## 3. Task 0

| Step | Result on `v0.6.0-pre.4` |
|---|---|
| Restore (locked) and Release build | 0 errors, about one minute |
| Core, 35 classes of the areas, one at a time | 1,362 tests, 0 skipped, 0 failed |
| Server, 56 classes of the areas, one at a time | 1,388 tests, 1 skipped (`SearchIndexTests`, which needs the model), 0 failed |
| Web, the areas' folders (`attachments`, `upload`, `data`, `reader`, `note`, `blame`, `palette`, `windows`, `discreet`, `console`, `routes`, `shell`) | 146 files, 2,752 tests passed |
| The sidecar (`pnpm -C pdf test`) | 10 files, 211 tests passed |

No test failed, so none had to be re-run alone. The set-up and the build took about ten minutes; the baseline itself took far longer than the brief's 20 minutes, because each class starts the runner anew and the baseline shared the lock with the first reviewers: the first pass (72 classes, the web folders and the sidecar) finished after about half an hour, and the second pass (the 19 classes the plan review added) was stopped once by the background time limit while it waited for the lock, was started again for its last eight classes and finished about two and a half hours into the run. The area reviews started beside it, as the plan says. The full suites were not run: nothing was changed, and the suite was green in CI on `63d3ca46`.

## 4. What was reviewed, and how

Nine areas, as the spec's §2 has them:

- **A**, files in and out: every file type as a download, large files in sealed chunks.
- **B**, agents' uploads through a one-time link that works without a session.
- **C**, parsers and rendering: `csv`, `html`, `xml`, SVG, templates and properties, Mermaid from a data URL, PDF crops.
- **D**, Publish 2.0 and deep links.
- **E**, sharing and who wrote what: `blame`, history messages, start-up skills and the system skills.
- **F**, discreet vaults across the new views, and the windows' lifecycle for passwords and recovery keys.
- **G**, resource use: the link index and graph, the new `sh` options, the embedding queue.
- **H**, BYOK and cross-cutting: the console's read-only promise for every new command, dependencies, logs, metrics and error texts, workflows.
- **I**, the 0.5.0 findings at this tag.

For each, one fresh reviewer in its own worktree:

1. **Read** the area's feature specs, the review in each pull request's squash commit, and the parts of `docs/security-architecture.md` at the tag that make its promises; and so knew which points earlier reviews had accepted, and which they had recorded as fixed. Several of the squash commits carry no security review section; where that was so, nothing counted as accepted earlier.
2. **Modelled** the area: every entry point new or changed since 0.5.0 (endpoint, MCP tool, `sh` command, page, background job), which of the spec's trust boundaries each touches, and what the design promises.
3. **Reviewed every entry point** against the classes of the 0.4.0 spec §4 step 2: authentication and authorisation for every role and credential kind; path handling; injection; outbound requests; resource use on bounded oversized and nested inputs; races; information leaks; cryptography where keys or slots are new; request forgery and origin checks; the CSP and response headers.
4. **Showed each candidate** by a behaviour test that asserts what the code should do, beside a positive control (the same request by the caller who should succeed, or an ordinary input of the same size), or by a trace with file and line. Measurements were taken in Release, beside a control, with the load average stated.
5. **Checked the crossings of features** it owned (the brief's open point 3) and said "checked" or "not reached" for each.

How each area went about it:

- **A:** every way out (the file endpoint, WebDAV, the three ZIP exports, sync, `cat`, a checkpoint, publishing and the reader) header by header for file types a browser would run; names and paths at each way in; the chunk format and its key against the architecture's §3 and §5; the limits and the quota at each way in; re-keying a vault that holds chunked files, then a large file through each way out.
- **B:** the link's making, storing, finding, spending and ending, with eight concurrent requests on one link; what the answers to a holder without an account say, header by header; the rights the file is stored with when the key is revoked, the role lowered, the membership ended, the vault re-keyed or the connection revoked while the link lives; the HTTPS rule; every route of the rewritten middleware against 0.5.0's.
- **C:** the spec's ordinary untrusted inputs to each parser (an XML document with a DTD, the standard sanitiser strings as HTML, bounded oversized and deeply nested CSV, HTML and XML), each asserting "neutralised or limited" beside a control of the same size; how each parser's output reaches an agent and a browser; template insertion and the properties box in the web app's own code under Vitest. A second reviewer took what the first had not reached: the sidecar's SVG drawing, PDF crops, Mermaid's data URL, and how the web app shows an SVG.
- **D:** what a bundle holds against what its maker may read, for a note left out; the walk's bounds and its crossings into other vaults, the archive and the trash; how a publication is updated and ends; deep links for every kind of caller without access.
- **E:** `blame` and history over `sh`, the API and the window for every role and credential kind, in a discreet vault and after a member leaves; the start-up skills and the system skills against the spec's boundary for a writer in a shared vault; the sync socket.
- **F:** every new endpoint and every new view for a session with its discreet vaults hidden, against the discreet design's promises; the lifetime of passwords and recovery keys in the windows; the admin lists' filters.
- **G:** cost against input size, beside a control, for the features the spec lists for this area; and seventeen access tests of the graph for a key limited to one vault.
- **H:** the BYOK key's sealing and the key check, with a stub as the provider; 122 console lines for every command new or changed since 0.5.0, compared by the vault's state, with the same writes over MCP as controls; the three advisory lookups; every log line, metric label and error text added since 0.5.0; the workflows' pins and permissions; and a sweep of the 153 changed product files for any that no area covered (none).
- **I:** for every S5 and L5 finding, the fix's place in the tag, the 0.5.0 audit's own test re-run where it still applies, and whether the fix still holds where 0.6.0's code sits beside it.

Every candidate then went to a **second fresh reviewer** who got the claim (title, place, the behaviour claimed, the test) and the code, not the first reviewer's reasoning, and who reproduced or rewrote the test, tried to disprove the claim and rated it independently. A Low could only be confirmed, rated up or refuted. Where a second reviewer was the first to show a point, or where two reviewers disagreed on the level, a **third reviewer** got the claim and the code (three times). The **severity check** of spec §3 was then run as the plan describes it, by one further reviewer.

What each area checked and found sound is recorded in the private file, not here: a public list would show the confidential findings by its gaps.

**The check of this report against the publication rule.** Before the push, a fresh reviewer got this report, the pull request's text, the commit message, the rows for the open items and the Factory card's lines, together with the private file's summary, and read them as someone who wants to learn where the unfixed weaknesses are. It raised nineteen points; all were taken. Among them: four Lows that were meant for this report were moved to the private file (L6-03, L6-05, L6-11, L6-12), because they stand too close to a confidential finding or deserve a doubt towards Medium; the paragraphs above on how each area went about it were cut back to what the spec already says; and four lines on the Factory card that said more about a finding than its id and level were reworded on the card.

## 5. Counts per severity

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 4 |
| Medium | 15 |
| Low | 28 |

Five more findings of Medium lie in code from before 0.6.0, outside the review's scope, and are reported for Chris with their severity (decision 7). No candidate was refuted by its second review. Two were rated down to Low by a later review; one finding first reported as High was settled at Medium by a third review (S6-02, decision 5).

Each of the four Highs carries a written doubt towards Medium; the private file says for each what would settle it. Spec §6 was used for each of them as soon as a second or independent review had rated it High: written in full into the private file, pushed, a notification sent with the id only, and the card moved to Waiting for Chris. By the release policy (spec §1) a High is fixed at once and Medium and up before 0.6.0.

| Id | Severity | Needs a decision from Chris |
|---|---|---|
| S6-01 | High (doubt: Medium) | only on the rating |
| S6-02 | Medium (doubt: High); first reported as High | a small one |
| S6-03 | High (doubt: Medium) | a small one |
| S6-04 | Medium (doubt: Low) | no |
| S6-05 | Medium (doubt: Low) | yes |
| S6-06 | Medium, with doubt | no |
| S6-07 | Medium (doubt: High) | no |
| S6-08 | Medium | yes |
| S6-09 | Medium (doubt: High) | yes |
| S6-10 | Medium (doubt: Low) | yes |
| S6-11 | Medium (doubt: High) | no |
| S6-12 | Medium (doubt: Low) | a small one |
| S6-13 | Medium (doubt: High) | no |
| S6-14 | Medium (doubt: Low), outside the scope | on the scope |
| S6-15 | Medium (doubt: Low), outside the scope | on the scope |
| S6-16 | Medium, with doubt | no |
| S6-17 | Medium, with doubt, outside the scope | on the scope |
| S6-18 | Medium (doubt: Low), outside the scope | yes |
| S6-19 | Medium, with doubt | yes |
| S6-20 | High (doubt: Medium) | no |
| S6-21 | High (doubt: Medium) | no |
| S6-22 | Medium (doubt: Low) | no |
| S6-23 | Medium (doubt: Low) | no |
| S6-24 | Medium, outside the scope | on the scope |
| L6-03 | Low (doubt: Medium) | yes |
| L6-05 | Low | no |
| L6-11 | Low | no |
| L6-12 | Low (doubt: Medium) | no |
| L6-14 | Low | no |
| L6-15 | Low | perhaps |
| L6-16 | Low | no |
| L6-17 | Low | yes |
| L6-18 | Low | no |
| L6-19 | Low (doubt: Medium) | no |
| L6-20 | Low | no |
| L6-21 | Low (doubt: Medium) | a small one |
| L6-22 | Low | yes |
| L6-23 | Low | no |
| L6-24 | Low | no |
| L6-25 | Low | a small one |
| L6-26 | Low | no |
| L6-27 | Low | yes |
| L6-28 | Low | no |

Details of each are in the confidential findings, the private pull request below.

**The 0.5.0 findings.** Every recorded fix of the 0.5.0 audit is in `v0.6.0-pre.4` and holds for what it covered; where one of the 0.5.0 audit's own tests no longer passes as written, the fix changed what the test observes, and the private file explains each case. L5-04 is still open, as recorded. Three of the 0.5.0 findings were checked by reading, not re-run in a browser; the private file says which.

## 6. The Low findings

Nine Lows are described here in full (L6-01, L6-02, L6-04, L6-06 to L6-10, L6-13). Their tests are in the private repository with the others; the sentences below say what each test asserts.

### L6-01: An upload link follows the vault's name, not the vault: after a rename it stores the file in another vault

- **Area:** B (agents' uploads through a one-time link, #230). **Needs a decision from Chris:** no.
- **Severity: Low.** *Impact:* one file is stored in a vault the link was not made for; where that vault is shared, its members can read a file meant for a private vault, and the Markdown the agent writes into its note points at the wrong vault. *Reachability:* only the person's own renames, made in a signed-in session within the link's ten minutes (rename the link's vault away, give its name to another vault the same credential may write); no other user and no holder of the link can cause it, and no right is exceeded, since the write is checked again against the vault that now has the name. *Doubt:* the first reviewer's, towards "no finding", since the path's text is literally the same.
- **Trust boundary:** "one file at the one path the link was made for" (the audit spec §2). The link's holder gains nothing; the person's own data lands where she did not send it.
- **The promise:** `docs/security-architecture.md` §4, "Stolen upload URL": "Store one file at the one path it was made for", and that it cannot "Reach any other path or vault"; §6: "The `upload` tool checks the path, the format's extension and the caller's right as `write` does, then makes a ticket". The checks were made against one vault and the write goes to another. Broken narrowly. A pairing code, the precedent §6 names, is bound to the vault's id and dropped on a rename (`Accounts/Pairings.cs:91`, `Api/NoteApi.cs:407, 413`).
- **Place:** `src/Kumbu.Server/Api/UploadTickets.cs:14` (the ticket holds `Path` as text), set at `src/Kumbu.Server/Mcp/UploadTool.cs:93`; the `PUT` parses it again and resolves the vault by the name it bears now, `src/Kumbu.Server/Api/AgentUploadApi.cs:42-45`, then the file endpoint's own check of the vault (`RequireVaultAsync`).
- **Evidence:** a behaviour test, "a URL does not follow the name to another vault", fails: "PUT answered 201; the file is in the vault that was Home when the URL was made: OK"; its control (the URL stores in the vault it was made for) passes. The second review's test: Alice's `Work` is read by Bob, her `Home` is hers alone; a URL is made for `/Home/payslip.png`; she renames `Home` to `Private` and `Work` to `Home`. "A URL made for a private vault does not store in a shared one that took its name" fails: "PUT answered 201; Bob, a member of the vault that was Work, reads the file made for the private vault: OK; in the private vault: NotFound"; its control passes, and a key bound to reading the vault that took the name stores nothing (403, nothing stored anywhere).
- **Second review:** confirmed, Low. It added the concrete impact and the limits: renaming is session-only, so a link's holder cannot cause it; a key limited to one vault is not affected.
- **Earlier reviews:** silent. The feature spec says the ticket holds "the one path" and never weighs name against id.
- **Proposed fix:** keep the vault's id in the ticket beside the path and refuse at the `PUT`, with the usual "ask again", when the name no longer leads to that vault; the dev token has no vault ids, so there the name stays. The second review found that it closes the finding. **Tests:** both failing tests turn green (nothing stored in either vault); a member's own name for a shared vault changed between `upload` and `PUT` is refused; the controls, the read-only bound and the existing agent-upload tests stay green.

### L6-02: An upload link sent in the clear is spent only by a `PUT` that reaches the server

- **Area:** B (agents' uploads through a one-time link, #230). **Needs a decision from Chris:** yes, a small one: spend on any method or narrow the sentence, and document the proxy case or route the prefix past the redirect. Both reviewers recommend spending on any method.
- **Severity: Low.** *Impact:* whoever saw the link on the wire can store one file at its fixed path, as its credential and within the storage limit, until the agent's own https `PUT` spends it or ten minutes pass. *Reachability:* the tool hands out an https link whenever the public URL is https, so an agent or a person must first have left the given URL or method (`curl --data-binary @file '<url>'` without `-X PUT` is a `POST`; for http the scheme must have been changed too), and someone must be listening on that path at that moment. *Doubt:* none towards Medium; the damage is one write at a fixed path.
- **Trust boundary:** "over HTTPS only (plain HTTP spends the link)" (the audit spec §2).
- **The promise:** `docs/security-architecture.md` §4: "A URL that arrived over plain `http` is spent and refused" and "the first request that arrives spends it, whatever it carries"; §6: "A request that arrives over plain `http` spends its ticket"; the README, "Agents uploading files": "Over plain `http` the PUT is refused and the URL spent". For another method than `PUT` both sentences of §4 are untrue as written, over https as well (harmless there). Behind a proxy that redirects http itself the request does not arrive, so §6 holds by its letter and the reader of §4 is misled.
- **Place:** `src/Kumbu.Server/Auth/CallerMiddleware.cs:80` (only `PUT` takes the route, the block is `:80-84`), `:325-331` (the plain-http branch); `compose.traefik.yaml:49-50` (the `web` entry point redirects every http request), `:88` (the server's router listens on `websecure` only), `:77` (no host port), `:79` (the public URL is https).
- **Evidence:** a behaviour test, "a URL sent in the clear with another method is spent", fails for `POST` and `GET`: "POST over http answered 401; the same URL then stored a file over https: 201"; its control, a `PUT` in the clear, passes (403, then 401). The second review's test, "a URL in the clear is spent whatever the method", fails for `POST`, `HEAD` and `DELETE`: "answered 401; the ticket is still open (1 of 1)"; its control passes. The proxy half is a trace by both reviewers, not run (no Traefik): with the shipped override Traefik answers every request on port 80 itself, so the server spends nothing there, for any method, `PUT` included. An agent that follows the redirect spends the link itself at once.
- **Second review:** confirmed, Low. It added the `HEAD` and `DELETE` rows, the https case, and that the proxy half cannot be closed in the server.
- **Earlier reviews:** silent on both cases. The spec says "HTTPS only outside `Development`, as every credentialed route"; spending on plain http is pinned by a test for `PUT` only.
- **Proposed fix:** take the ticket for any method under `/api/agent-upload/` that did not arrive over https; the second review found that this closes the server half. For the proxy half, a sentence wherever the promise is stated ("where the proxy redirects http itself, the server never sees the request and the link lives out its ten minutes"); the first reviewer's alternative is a Traefik router on `web` that passes this prefix to the server. **Tests:** the two failing theories turn green and the controls stay green; for a router, a hand test on staging.

### L6-04: The security architecture and the system skill still describe an upload link's write as 15 MB of six formats

- **Area:** B (agents' uploads through a one-time link, #230; the change that outdated the texts is #254 and #257). **Needs a decision from Chris:** no.
- **Severity: Low** (documentation). *Impact:* the document that states what a stolen link can do understates it: one link now stores any file up to the bound per file, 1 GiB by default, against the owner's storage limit, not 15 MB of six formats; and the skill makes agents refuse or convert files the server keeps, a functional fault. *Reachability:* none of its own; no code is wrong and there is no behaviour to exploit. *Doubt:* none.
- **Trust boundary:** the link's holder; the audit spec's "honest limits". None is crossed.
- **The promise:** `docs/security-architecture.md` §6, "Agent uploads" (line 686): "format checked from the bytes, 15 MB, storage limit". The §4 row "Stolen upload URL" names no size.
- **Place:** `docs/security-architecture.md:686`; `src/Kumbu.Core/Skills/System/kumbu-binary-upload/SKILL.md:9` ("Kumbu² keeps PNG, JPEG (.jpg, .jpeg), GIF, WebP, SVG and PDF, at most 15 MB each … Convert anything else first, or tell the user it can't be kept") and its line 7 ("an image or PDF"). Against them the code: the bound is `Kumbu:Vaults:MaxFileBytes` (`KumbuOptions.cs:279-283`), and `src/Kumbu.Server/Mcp/UploadTool.cs:100` (the tool's answer names the real bound).
- **Evidence:** a trace by both reviewers. Two existing tests, "the tool answers a URL for any file" and "a chunked body over the bound is a 413 naming it", pass at the tag and show the behaviour as built.
- **Second review:** confirmed, Low. The behaviour is intended: the files spec names "MCP's upload URL" among the ways in for every file, and the tool's own description and answer were brought along (`Manual.cs:136-139`, `UploadTool.cs:36, 100`); the skill and §6 were not. Nothing in the code relies on the narrower promise: a file of no known format is served only as a download, whichever way it came in.
- **Earlier reviews:** #257's commit "Take any file through every way in" names the agent upload URL as one of them; the two texts were not updated.
- **Proposed fix:** correct §6 (the §4 row may name the bound) and the skill's steps 1 and 5. The second review agrees; nothing is left open by it. **Tests:** none; a test of the skill's wording is not worth it.

### L6-06: The 50-megapixel bound doesn't apply to an image over 15 MB, which the lightbox still draws

- **Area:** A (files in and out: files up to 1 GiB, #257). **Needs a decision from Chris:** yes: keep the behaviour, or one of the two fixes. The reviewers make no recommendation.
- **Severity: Low.** *Impact:* a member who opens such an image has their browser fetch the whole file into the tab's memory (up to the bound of 1 GiB) and decode whatever its header declares; at worst their own tab slows or is killed, within the browser's own limits. Nothing leaves the vault and nothing runs. *Reachability:* a writer in a shared vault uploads an image a little over 15 MB with a very large declared size, and a reader has to open it; thumbnails show an icon over 15 MB and fetch nothing (`web/src/attachments/Thumbnail.tsx:66`). *Doubt:* towards "no finding": it is a decision taken on purpose, reported because its reason holds for the server only.
- **Trust boundary:** "A writer in a shared vault: […] doesn't tie up […] the reader's tab beyond the stated bounds" (the audit spec §2).
- **The promise:** the reviews read it differently. The first names `docs/security-architecture.md` §7, "Attachments: untrusted bytes in": "refusing more than 50 megapixels or 16,384 px on a side". The second holds that none is broken, since that row gives the bound as the upload check and elsewhere as the guard of the server's decoder. Both agree on the design: the files spec, decision 12, large images "are shown by the browser"; the upload code's own comment, "without the pixel limit, as nothing decodes it". The reader's browser does decode it.
- **Place:** the large-file upload on both ways in, which reads an image's size from its header without the pixel limit (`src/Kumbu.Server/Attachments/ImageHeader.cs:68-70`); `web/src/api/notes.ts:497-502` (the whole file fetched into a `Blob`); `web/src/attachments/Lightbox.tsx:389-400` (the `<img>`).
- **Evidence:** a trace by both reviewers, and an existing test, "a large image above the pixel limit is kept with its size", which pins the behaviour with 10,000 × 10,000 and passes at the tag. Not shown in a browser; what a given browser does with a very large image was not measured.
- **Second review:** confirmed, Low; "no finding by the rules", kept for the decision's reason.
- **Earlier reviews:** accepted on purpose (the commit "Keep a large image above the pixel limit", decision 12).
- **Proposed fix, if wanted:** keep the pixel limit for a large image (its header is in the first megabyte), or have the lightbox show the icon and "Download" for an image over 15 MB or over the pixel bound (the stored width and height are there). Either would close it; the second review favours neither. **Tests:** a web test that the lightbox of such an image fetches nothing; for the refusal, a server test.

### L6-07: The security architecture's row on uploads still describes 0.5.0

- **Area:** A (files in and out: every file type and files up to 1 GiB, #254, #257). **Needs a decision from Chris:** no.
- **Severity: Low** (documentation). *Impact:* a reader of the security document is told that an upload over 15 MB is a 413 before it is read and that any extension outside seven is refused, neither of which is true since #254 and #257. *Reachability:* none; nothing is exploitable by it. *Doubt:* none.
- **Trust boundary:** none is crossed.
- **The promise:** the row itself, `docs/security-architecture.md` §7, "Attachments: untrusted bytes in": "A `Content-Length` over 15 MB is a 413 before reading; otherwise the whole body is read into a pooled buffer (Kestrel's limit for this endpoint is 15 MB + 64 KB)" and "the name's extension must be one of `.pdf`, `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.svg` (anything else is refused naming them)". It also gives "refusing more than 50 megapixels" without saying that an image over 15 MB is kept without that check.
- **Place:** `docs/security-architecture.md:783`. Against it: line 785 ("Files: only ever downloads") says the opposite, and §5.4 (line 397) describes the streaming; so does the upload code at the tag, and `src/Kumbu.Core/Attachments/AttachmentFormat.cs:103-117` (any other name is a file).
- **Evidence:** the two rows side by side and the code; a reading, no test.
- **Second review:** confirmed, Low; it added the sentence on the pixel limit.
- **Earlier reviews:** silent; the row was not brought along with #254 and #257.
- **Proposed fix:** rewrite the row for every file type and the bound per file. Both reviews agree. **Tests:** none.

### L6-08: A write message can carry `history`'s own marks and format characters

- **Area:** E (the write message shown in a note's history; the message's checks came with #223). **Needs a decision from Chris:** no.
- **Severity: Low.** *Impact:* an older version's line of `sh history` can end in `  (current)` or `  (current)  (restored)`, the marks `history` puts on the current version, so a person or an agent choosing a version to restore or compare can be misled; U+202E and zero-width characters pass, so the message can show turned around in the web app's history row. Text only: nothing runs, nothing is written or disclosed. *Reachability:* any writer of a shared vault, through the message of `write`, `edit` or `replace`; read by every member in the note's history. *Doubt:* none recorded by either reviewer.
- **Trust boundary:** a writer in a shared vault, and what another member or her agent reads in the history.
- **The promise:** `docs/security-architecture.md` §5.4: the message "is one line of at most 200 characters, with control characters and the Unicode line and paragraph separators refused, so it can't fake a line of `history`". It can't fake a line; it can fake its own line's marks.
- **Place:** `src/Kumbu.Core/Notes/NoteWriter.cs:75` (only `char.IsControl`, U+2028 and U+2029 are refused); shown at `src/Kumbu.Core/Sh/Commands/History.cs:100` (the message stands before the `  (current)` mark, `:108`) and `web/src/note/HistorySheet.tsx:41` (as text, no markup).
- **Evidence:** the first reviewer's theory, "a message with `history`'s own marks or format characters is refused", fails for all four inputs (`"tidied  (current)"`, `"tidied  (current)  (restored)"`, U+202E…U+202C, U+200B) with `Assert.False() Failure … Actual: True`; its control, an ordinary message is taken, passes. The second reviewer's theory, "a message is taken unless it carries `history`'s marks or invisible characters", fails for the same four, with three controls passing (an ordinary message, one with ordinary brackets, a control character refused). No timing is involved. The web app's history sheet was read, not tested.
- **Second review:** confirmed, Low; place and inputs as claimed.
- **Earlier reviews:** #223 added "Refuse control characters and line separators in a write message"; format characters and the marks were not looked at.
- **Proposed fix:** refuse (or drop) Unicode format characters (category Cf) in a message, and either collapse runs of spaces or put `history`'s marks before the message. The second review found no objection to it. **Tests:** both theories pass.

### L6-09: The check of a person's own TensorX key has no limit, and every refused guess is remembered for the life of the process

- **Area:** H (BYOK embeddings, #234). **Needs a decision from Chris:** no.
- **Severity: Low.** *Impact:* a signed-in user can have the server test keys against TensorX as fast as it answers, from the server's address, which TensorX may then throttle or block for everyone on the instance; each distinct refused key leaves an entry in memory (a 29-character route string and a time) until the restart, which grows slowly. *Reachability:* any account with a web session on an instance that offers BYOK (a remembered session needs its password window); nothing of another user is read. *Doubt:* none upwards.
- **Trust boundary:** the admin of a shared server and the other users on it: one user's actions don't deny service to the others.
- **The promise:** none is broken; the document states no limit for this check. The severity scale names a missing rate limit as its example of a Low.
- **Place:** `src/Kumbu.Server/Api/EmbeddingsKeyApi.cs:36, 49` (no limiter of `Api/Security.cs` is asked before `CheckAsync`); `src/Kumbu.Server/Search/ByokDrivers.cs:66` (`_refused[route]` is written for any refused key; `Drop` removes an entry only for a key that was saved). The route is a hash prefix of the key, never the key.
- **Evidence:** a behaviour test, "the key check is limited per account", fails: "40 guesses in a row: 40 requests reached TensorX, statuses 400"; its control (the first guess is checked and refused with 400) passes. The second review's test, "refused guesses leave nothing in memory", fails: "12 refused guesses, 12 requests to the provider, 12 entries kept for the process's life". TensorX was a stub throughout; no timing is claimed. Each call is one outbound request, two when the provider answers 429 or 5xx (trace).
- **Second review:** confirmed, Low.
- **Earlier reviews:** silent; the pull request carries no security review text.
- **Proposed fix:** a per-user sliding window on `PUT /api/account/embeddings-key`, as the other limits have (a handful a minute, 429 with `Retry-After`), and no `_refused` entry for a key that isn't saved. The second review finds that it closes it. **Tests:** the two failing tests.

### L6-10: The embeddings client follows redirects with the vault's text, and prints the provider's reason phrase

- **Area:** H (BYOK embeddings, #234; the driver itself is older, and 0.6.0 puts people's own keys on it). **Needs a decision from Chris:** no.
- **Severity: Low.** *Impact:* on a 307 or 308 from the provider, .NET's default handler sends the same body, the sections' text or the query, to the address the answer names; the `Authorization` header is dropped and HTTPS never goes on to HTTP. The answer's size is not bounded, and the provider's HTTP reason phrase, a free text of its choosing, reaches the log and the notice an agent reads under a search. *Reachability:* only the provider itself, or whoever answers in its name; no user of Kumbu² can cause any of it. The redirect gives the text to nobody the provider couldn't give it to. *Doubt:* none; a hardening gap.
- **Trust boundary:** the server and the external provider (`docs/security-architecture.md` §7).
- **The promise:** §5.5, "The server never calls an address a person named", holds. What a redirect to another host would not keep: §5.5's "They go to the configured `BaseUrl`" and §7's "the configured external embedding provider over HTTPS". For the phrase: §5.5 says the driver "logs the provider's name, the HTTP status or the kind of network error, and the attempt".
- **Place:** `src/Kumbu.Server/Program.cs:80` (`AddHttpClient()` only; no handler is configured for `embeddings:<provider>`); `src/Kumbu.Server/Search/OpenAiCompatibleDriver.cs:115-119` (the whole response is buffered before `ReadFromJsonAsync`; `HttpClient`'s default buffer limit is 2 GB) and `:123` (`answered {status} {response.ReasonPhrase}`, which becomes the search's reason at `Vaults/EncryptedNoteStore.cs:1038` and the `LogError` at `:192`). The server's other outbound clients switch redirects off (`Program.cs:84` by the second review, `:85` by the first; `OAuth/ClientMetadataFetcher.cs:38`).
- **Evidence:** a trace in the first review. Shown by the second: "the embeddings client follows no redirect with the vault's text" uses the client as `Program.cs:80` registers it, against two loopback listeners, and fails: "the redirect was followed 1 time(s); the text was sent again: True; the key was sent again: False"; its control (asked directly: text and key arrive) passes. "The provider's reason phrase reaches neither a search answer nor the log" fails: "in the search answer: True; in the log: True". Traces, not run: the answer's size (no large answer was allocated on the shared machine), 301/302/303 turning the request into a body-less `GET`, and the phrase's length (up to the header limit, without line breaks; HTTP/2 has none).
- **Second review:** confirmed, Low, now shown by a test. It showed the reason-phrase point, which the first review had as a suspicion, and folded it in here: both are trust in the provider's answer. The line that prints the phrase predates 0.6.0 for the admin's providers.
- **Earlier reviews:** not looked at before.
- **Proposed fix:** a primary handler with `AllowAutoRedirect = false` for every `embeddings:*` client (a 3xx then fails as any non-transient status) and `MaxResponseContentBufferSize` of a few megabytes; print the status code and .NET's own name for it, never the phrase. The second review finds that it closes it. **Tests:** the two failing tests, and one with an oversized stubbed answer.

### L6-13: A long glob throws out of the shell

- **Area:** G (the new `sh` options), `rg -g`; the reports do not name the pull request. **Needs a decision from Chris:** no.
- **Severity: Low.** *Impact:* `rg -g` with a glob of about 4 KB (2,000 `*a`) ends in an unhandled exception instead of a "bad glob" line: over MCP the caller gets the library's general tool error, in the console a 500. Nothing else happens: nothing is read or changed, and the exception's message names the size of the pattern's automaton, not the pattern; nothing of the command is logged by our code. *Reachability:* any caller of `rg`, with its own command. *Doubt:* none.
- **Trust boundary:** none is crossed; the caller's own call fails with the wrong kind of answer.
- **The promise:** none is broken. The shell's own convention is that a pattern it cannot use is refused with a line of `rg`'s (an unclosed `[` or `{` and nested braces are).
- **Place:** `src/Kumbu.Core/Sh/Glob.cs:51-59`: `new Regex(…, RegexOptions.NonBacktracking)` throws `NotSupportedException` for a pattern over the engine's size limit; only `ArgumentException` is caught. The exception leaves `Shell.RunAsync`; `ToolResult.RunAsync` (`ToolResult.cs:65-70`) counts the call as failed (`ToolError.Other`) and rethrows, and `POST /api/console` goes through the same guard and ends as a 500.
- **Evidence:** a behaviour test, "a very long glob is refused as a bad glob", fails at 2,000, 20,000 and 200,000 stars: "NotSupportedException: The specified pattern with RegexOptions.NonBacktracking could result in an automata as large as '20005' nodes, which is …" (load 3.33). Its control, a shorter glob, is answered as usual. What the caller sees over MCP and in the console is by trace, not run.
- **Second review:** confirmed, Low. It added what the caller sees and that the message does not carry the pattern.
- **Earlier reviews:** silent in what the reports give.
- **Proposed fix:** catch `NotSupportedException` beside `ArgumentException` and answer `rg: bad glob '…': too long`, or bound the glob's length first. The second review names the same fix. **Tests:** the failing test.

## 7. Decisions made without Chris

1. **One lock and one test database for all reviewers,** instead of a database per reviewer as in the 0.5.0 run: the brief's "only one heavy run at a time" was enforced mechanically with `flock`. The price was waiting, and the stalls named in section 1.
2. **More than four agents at a time after the first hour.** The plan's "at most four area reviewers" held for the area reviews at first; later, second reviews, third reviews, the severity check and the last area reviews overlapped, up to eight agents. Since every heavy run still took its turn under the lock, the machine was never overloaded by builds or suites; measurements state their load average.
3. **Area C was reviewed in two parts.** Its first reviewer did not reach the sidecar, PDF crops, Mermaid and the properties box and said so; a further fresh reviewer took exactly that part, with its own second review.
4. **A third reviewer where the method needed one.** Two points were first shown by a second reviewer, not by the area's reviewer; each went to a third, independent reviewer who got the claim and the code. One disagreement between a first and a second review on the level was settled by a third reviewer, as the brief says; two more by a further review.
5. **Spec §6 was applied on the higher rating, at once.** A finding was reported as High as soon as a second or independent review had rated it High, also where the first review had rated it Medium with a doubt towards High ("when in doubt, the higher one"). For S6-02 the third review then settled the level at Medium (doubt: High); the notification for it had gone out at 19:37 UTC, and the card's log says so. Four notifications were sent, for five findings (S6-01, S6-02, S6-03, then S6-20 and S6-21 together).
6. **The four Highs are rated on what was shown, not on a full-size demonstration.** For each, the private file gives what was shown, the doubt this leaves towards Medium and what would end it. Chris may rate them Medium by the letter of the 0.5.0 audit's decision (a); the recommendation to fix them first does not depend on that.
7. **Findings in code from before 0.6.0 are counted apart.** Five Mediums sit in code that second or third reviews traced back to 0.5.0 or 0.4.0. The spec's scope takes untouched code only where a new feature reaches it in a new way; whether that holds for each is a question about the scope, so they are left out of the counts, reported for Chris with their severity as the 0.5.0 audit did with S5-10, and recorded in `docs/superpowers/open-items.md` as the brief says.
8. **Nineteen Lows stay in the private file** (L6-03, L6-05, L6-11, L6-12 and L6-14 to L6-28): each either sits beside a confidential finding, so that describing it would point there, or carries a recorded doubt towards Medium. Four of them were moved there by the check of this report against the publication rule (section 4). When in doubt whether a detail is safe to publish, it isn't.
9. **The severity check ran in parts,** by one reviewer, as the second reviews came in, rather than once at the end: it saved about two hours.
10. **The confidential file's sections were drafted by writer agents** from the reviewers' reports, each given the ids and levels; the controller wrote the summary and the decisions on severity and assembled the file. Where reports contradict each other and nobody settled it, the file gives both.
11. **Both pull requests were opened early:** the private one with the first High (spec §6), this one as a draft on the plan commit, so that the report could link it.
12. **A weakness reported by more than one reviewer is counted once.**
13. **The branches.** The session was started on a default working branch of its own; the run pushed only to `audit/0-6-0-security` here and `audit/0-6-0` in the private repository, as the brief names them.

No question came up that only a change of the spec's scope could answer, other than decision 7.

## 8. Deviations and open points

- **Task 0 took longer than 20 minutes** (section 3).
- **The Factory card:** one log line (17:59 UTC) gave the number of area reviews that were back, which the brief's "no per-area progress" forbids even without naming one; later lines keep to phases. One line carried a wrong time and was corrected by the next line, as the board's rules ask. Four lines were reworded after the check of section 4, against the board's rule that the log only grows: the publication rule comes first; the card says so in a line of its own.
- **Not reached,** said here without what it would bear on: no run in a browser (web timings are Vitest's, in Node); no run of the shipped compose stack; no real reverse proxy; no AI provider; the 0.5.0 audit's Mermaid harness; some WebDAV paths by trace only. The private file lists what each area did not reach.
- **Several squash commits of 0.6.0 carry no security review section;** for those features there was no list of accepted or fixed points to compare against. The private file names them.
- **For the fix run:** the private file gives, per finding, the proposed fix and the tests that should prove it; the reviewers' tests are behaviour tests that fail today and can be dropped into the tree at the tag; the private file's decisions say what the fix run should look at first.
- **Nothing was merged, tagged or deployed, and no product code or version changed.**

## 9. The pull requests

- Public: https://github.com/SCAI-NGO/kumbukumbu/pull/262
- Private (the confidential findings): https://github.com/SCAI-NGO/kumbukumbu-security/pull/5

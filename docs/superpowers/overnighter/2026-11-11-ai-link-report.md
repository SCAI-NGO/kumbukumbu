# Overnighter report: AI links for published notes, and the publishing switch for skill link copies

Branch `feature/ai-link`. Pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/192 (draft first, then ready; not merged, not tagged, version unchanged).
Brief: [`2026-11-11-ai-link-brief.md`](2026-11-11-ai-link-brief.md). Spec: [`../specs/2026-11-11-ai-link-design.md`](../specs/2026-11-11-ai-link-design.md) (not changed). Plan: [`../plans/2026-11-11-ai-link.md`](../plans/2026-11-11-ai-link.md).

## 1. Environment

- Cloud container, 4 cores, 15 GB memory. .NET SDK 10.0.403 (roll forward from `global.json`'s 10.0.100), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Installed by hand:** Node 26.12.1 into `/opt/node26` (the image had Node 22 and 24), pnpm 10.32.1 (`npm install -g`), the tokenizer (checksum verified), the `mongo:7.0` image.
- **Docker:** `dockerd` was not running at the start; started by hand (`dockerd &`), then the brief's own `kumbu-test-mongo` (`--ulimit nofile=20000:20000`, replica set), fresh before each server group. `TESTCONTAINERS_RYUK_DISABLED=true`.
- Task 0 (setup plus the first build) took about 10 minutes; the full gate about 25.
- Superpowers skills were listed but the run used the plain Agent tool as the brief's fallback describes: one fresh subagent per task, then a review subagent. **Deviation:** each review was one subagent with two sections (A: spec compliance, B: code quality) rather than two subagents; Tasks 5 and 6 (small diffs) I reviewed myself. Up to three subagents ran at once, only one of them running server tests at a time except during two short single-class runs.

## 2. The plan review

A fresh subagent got the spec, the brief and the plan. Its 19 findings, all folded in before the plan was committed (`6ad2ad0`):

| # | Finding | Done |
|---|---|---|
| 1 | Ceiling with one image is 55, not 56 | fixed |
| 2 | Test base URL is `KumbuAppFactory.PublicUrl`; name `Publications.BaseUrl` | fixed |
| 3 | The label mutation would survive test 1 | test 2 asserts the lookup's derivation and that `ShareCode.ReaderKey(token)` opens nothing |
| 4 | Several tests green under the stubs | positive preconditions in each |
| 5 | Test 6 would hit the per-address limit | spread over one /24; expired and used-up tokens added |
| 6 | The AI's client needs `X-Forwarded-For` per test | added |
| 7 | `FakeTimeProvider` must start at the real time (TTL monitor) | added |
| 8 | The `/s/t` fallback needs `AllowAnonymous` | added (D3) |
| 9 | Bind the index as a string | added (D3) |
| 10 | The unpublishable mark ends publications too | added to Task 1 test 3 |
| 11 | Reactivation revives a token | added to Task 2 test 3 |
| 12 | Task 3's tests never red | moved into Task 2 |
| 13 | Splice by offsets; CRLF; indented code | D5 |
| 14 | Mutate the increment's expiry filter too | added |
| 15 | A link made during a withdrawal lingers | D8 |
| 16 | Line anchors | fixed (the brief's `VaultCatalog.cs:585` TTL example is at line 1796) |
| 17 | `schema.d.ts` is generated | not hand-edited |
| 18 | `FingerprintAsync` is private | made `internal static` |
| 19 | The ceiling after an Update | added |

## 3. Task 0: baseline, planned against measured

On `feature/ai-link` at `d4ef9bc` (docs only on top of `c42732c`):

| Group | Planned | Measured |
|---|---|---|
| core | 2,266, 3 skipped | 2,266, 3 skipped, 0 failed |
| server Api, Mcp, Auth, Net | 843 | 843, 0 skipped, 0 failed |
| server Accounts | 413 | 413, **1 failed**: `DavShareTests.The_expiry_sweep_retires_expired_shares` (on the flaky list); alone three times: 29/29, 29/29, 29/29 |
| server Sharing | 353 | 353, 0 failed |
| server Welcome | 20 | 20, 0 failed |
| server Vaults, Skills, Search, Metrics | 777 | 777, 6 skipped, 0 failed |
| server, the rest | 1,143 | 1,143, 1 skipped, 0 failed |
| server total | 3,549, up to 7 skipped | 3,549, 7 skipped, 1 flaky failure |
| web | 130 files, 2,355 tests, build green | 130 files, 2,355 tests, build green |
| PDF sidecar | 9 files, 198 tests, build green | 9 files, 198 tests, build green |

## 4. The tasks

### Task 1: the token and its documents — `33faf56`, tidied in `62d0fab`

`AiLinks.cs` (`AiLinkToken`, `AiLinkDocument`, `AiLinkSlot`), the collection `ai_links` with its indexes on `VaultCatalog`, `CreateAiLinkAsync`/`OpenAiLinkAsync`/`CountAiLinkAsync` on `PublicationStore`, deletion in `DeleteCopyAsync`, `DeleteCopiesOfVaultsAsync` and `DeleteVaultsAsync` (in the transaction and in the sweep after it).

- Red: compile error (`CS0103 'AiLinkToken' does not exist`, `CS1061` for the three methods); then with stubs `failed: 5, succeeded: 0`, each at the fixture's `Assert.NotNull` (no link made).
- Green: `AiLinkStoreTests` `total: 5, failed: 0, succeeded: 5, skipped: 0`. Beside it, green: `ReaderApiTests` 19, `PublicationStoreTests` 14, `PublicationsTests` 24, `PublicationApiTests` 13, `PublishedNoteRemovalTests` 23, `UnpublishableTests` 6, `DeletionTests` 30, `ClosingPeriodTests` 14, `MemberDeletionTests` 4.
- Review (A/B): spec compliance holds; every caller of `DeleteCopyAsync` traced (`WithdrawAsync`, `SetUnpublishableAsync`, `FollowAsync` for trash, moves and marks, `WithdrawSkillAsync`), `DeleteCopiesOfVaultsAsync` (closing), the vault deletion loop. Fixed in `62d0fab`: the storage test waits for the note's embeddings and asserts `ai_links` isn't in `ContentCollections`; a comment on the deliberate double delete; `Parse` is `ShareCode.Parse`; the summary says the token is normalised; the expiry kept in whole milliseconds; a plain `UpdateOne` for the count; the slot's plaintext and an unused publication key zeroed. Not done: splitting test 3 into a theory (a nit). After the fixes `AiLinkStoreTests` 5/5, `AiLinkApiTests` 14/14.

### Task 2 (with Task 3): the endpoints, logs and metrics — `3c5d4ce`, after review `ffd12c6`, `bde48c4`

`AiLinkApi.cs`, `POST /api/s/ai-links` in `ReaderApi`, `/s/t` in `ReaderPaths`, `MapAiLinks` in `Program.cs`; 15 tests in `AiLinkApiTests`, `AI_links_are_not_counted` in `HttpMetricsTests`.

- Red: compile error; then a stub answering 404 for everything under `/s/t` and no making endpoint: all endpoint tests and the log test failed at the making precondition (`POST /api/s/ai-links` fell through to the `/api` fallback: 401 for 200); the Markdown unit test on its string; `AI_links_are_not_counted` at its precondition (6 of 7 in its class passed).
- Green: `AiLinkApiTests` 14/14 (15/15 after the review's fault test), `HttpMetricsTests` 7/7. Beside it, green: `ReaderApiTests` 19, `AiLinkStoreTests` 5, `StaticFilesTests` 28, `CallerTests` 25, `SkillPageApiTests` 42, `EndToEndTests` 1, `TextFileChannelTests` 9. After the review's fixes: `AiLink*`, `HttpMetricsTests`, `ReaderApiTests` and the 33 classes that use `CapturedLog`: 881 total, 879 passed, 2 skipped (model and TensorX), 0 failed.
- Review (A/B), no blocking finding; fixed in `ffd12c6`: image fetches now shown to count towards the ceiling (50 Markdown and 5 file reads, then the 404); `![e](<f/0>)` rewritten as the page reads it; **the request's log scope names the path**, so the handlers now write nothing within the request (refusals become answers in an endpoint filter, as `ReaderApi`'s do, replacing a nullable tuple; a fault answers a plain-text 500 and is logged by its type only, outside the request's execution context), with a test whose `CapturedLog` records scopes; two images on one line and escaped brackets in the unit test; the log test's 429 position derived from `ReaderApiTests.ReadsPerAddress`; test addresses moved off `198.18.0.0/16` (used by `DavClient`) to `100.70.x.y`; `Answer` kept, since `TypedResults.Text` writes a body to a HEAD (checked: the HEAD test fails with it). Recorded, not changed: test 6 spreads its dead tokens over one /24 rather than one address (the per-address read limit would otherwise answer 429, which is not what it tests); a fenced block inside a blockquote isn't seen as code by `MarkdownLink.Parse` (as indented code, D5), so a literal `![x](f/0)` there would be rewritten.

### Task 4: the button on the reader page — `bd1f0b7`, after review `69c5b3e`

- Red: 2 files, 5 tests failed (no button; no `untilText`), 16 passed. Green: `src/reader` 3 files, 21 tests. Full web suite: 131 files, 2,360 tests; build green.
- Review (A/B): spec compliance met. Fixed in `69c5b3e`: the sentence's live region is always laid out (a region that appears with its text is often not read out); both buttons in one row with "Copied" after them (it pushed the second button aside); the button no longer disabled while it waits (the focus fell to the page), a press meanwhile ignored and a late answer dropped; a "Copied" of the previous link cleared; the copy keyed by its id; `aria-busy` on the button dropped. Not done: a dead branch in `makeAiLink` (harmless), the clipboard's absence (older than this work). After the fixes: 21/21, build green.

### Task 5: L-02 on the server — `49594f7`

- Red (constants added first so the tests compile): `Publishing_off_keeps_a_link_copys_update_and_says_so` expected `publishing-off`, got `available`; `Publishing_off_still_checks_a_GitHub_copy` got `current` for the link copy.
- Green: `SkillUpdateCheckTests` 20/20, `SkillUpdateTakeTests` 13/13, `SkillsApiTests` 14/14.
- Review: mine (a small diff): no finding.

### Task 6: L-02 in the web — `a9864e5`

- Red: `src/routes/Skills.test.tsx` 1 failed, 14 passed. Green: 15/15; `src/skills` and `Skills.test.tsx` 7 files, 57 tests; build green.
- Review: mine: no finding.

### Task 7: documentation — `97da762`, `ffd12c6`, `bde48c4`

Publish design (dated note, §2 parenthesis, §3.12 pointer), skills design §3.10, security-architecture (§2 threat row, §4 Skills, §5.6 `ai_links`, §6 deletion, §7 the AI links row and the rows on rate limits, secrets in URLs and logs, proxy logs), README ("For an AI", the skills sentence, access logs), concept.md (one sentence).

### Task 8: security review — `bde48c4`

See §6.

### Task 9: final gate

`bash gate.sh final` on `bde48c4`, each server group on a fresh `kumbu-test-mongo`:

| Group | Task 0 | Final |
|---|---|---|
| restore (locked), Release build | green | green, 0 warnings |
| core | 2,266, 3 skipped | 2,266, 3 skipped, 0 failed |
| server Api, Mcp, Auth, Net | 843 | 843, 0 failed |
| server Accounts | 413, 1 flaky failure | 413, 0 failed |
| server Sharing | 353 | 353, 0 failed |
| server Welcome | 20 | 20, 0 failed |
| server Vaults, Skills, Search, Metrics | 777, 6 skipped | 780 (+2 L-02, +1 metrics), 6 skipped, 0 failed |
| server, the rest | 1,143, 1 skipped | 1,163 (+5 store, +15 endpoints), 1 skipped, 0 failed |
| server total | 3,549, 7 skipped | 3,572, 7 skipped, 0 failed |
| web | 130 files, 2,355 tests, build green | 131 files, 2,361 tests, build green |
| PDF sidecar | 9 files, 198 tests, build green | 9 files, 198 tests, build green |

The WebDAV compliance job (`litmus`) is left to the pull request's CI, as the brief says.

## 5. Every mutation

Each applied alone, the named class run, then restored (from a byte copy, checked with `cmp`, where the file held other uncommitted changes).

| Task | Mutation | Failing test |
|---|---|---|
| 1 | `ai_links` delete dropped from `DeleteCopyAsync` | `Every_way_a_publication_ends_takes_its_tokens` ("withdrawn: its document is left") |
| 1 | dropped from `DeleteVaultsAsync`, both places | same ("vault deleted") |
| 1 | dropped from `DeleteCopiesOfVaultsAsync` | same ("account deleted, its shared vault closing") |
| 1 | `AiLinks` added to `ContentCollections` | `Tokens_count_nothing_towards_storage` (6241 against 8968 bytes) |
| 1 | `ShareCode.LookupLabel` for the token's | `A_token_opens_…` and `The_database_holds_no_token_…` |
| 1 | `ShareCode.ReaderLabel` for the token's key label | `The_database_holds_no_token_…` (the slot opened with `ShareCode.ReaderKey(token)`) |
| 1 | `expiresAt > now` dropped from the open | `The_count_stops_at_the_ceiling_and_the_hour_at_expiry` |
| 1 | `count < ceiling` dropped from the increment | same |
| 1 | `expiresAt > now` dropped from both | same |
| 1 | the owner written in the clear | `The_database_holds_no_token_…` (field list) |
| 2 | `/s/t` left out of `ReaderPaths` | `Its_answers_carry_the_readers_headers`, `AI_links_are_not_counted` (`endpoint="/s/t/{token}"` appeared) |
| 2 | the `/s/t` fallback dropped | `GET_HEAD_and_POST_on_a_valid_and_an_unknown_token` (POST answered 401), `The_token_reaches_no_log` |
| 2 | the owner check skipped | `Every_end_answers_the_one_404` (again after the refactor) |
| 2 | the publishing switch unchecked | `Publishing_off_pauses_and_on_again_resumes` (again after the refactor) |
| 2 | `ReaderFailed` for an unknown token | `Unknown_tokens_never_block_a_network` (again after the refactor) |
| 2 | an `f/<n>` not in the copy kept | `Markdown_leaves_out_an_image_not_in_the_copy_and_keeps_code` |
| 2 | HEAD not counted | `GET_HEAD_and_POST_…` (again after the refactor) |
| 2 | HEAD writes a body | `GET_HEAD_and_POST_…` |
| 2 | the copy header unchecked when making | `No_cookie_another_cookie_or_a_wrong_copy_makes_no_link` |
| 2/3 | `LogInformation("AI link {Token} read")` | `The_token_reaches_no_log` |
| 2 | file reads not counted | `Every_end_answers_the_one_404`, `An_update_serves_the_new_copy` |
| 2 | angle brackets not stripped | the Markdown unit test |
| 2 | the fault filter's catch-all off | `A_fault_answers_a_plain_500_and_logs_no_token` (the developer page; the token in the scope) |
| 2 | `ExecutionContext.SuppressFlow()` removed | same (the token in the scope) |
| 4 | no `X-Kumbu-Copy` | "makes a link for an AI and shows it with its expiry" |
| 4 | links appended, not replaced | "a second press replaces the link", "says so when publishing is off …" |
| 4 | the off branch dropped | "says so when publishing is off …" |
| 5 | `checkedPaths` counting `publishing-off` | `Publishing_off_keeps_a_link_copys_update_and_says_so` (`Updates` empty) |
| 5 | Take/Review not refusing (condition never true; deleting the line doesn't compile, CS9113) | same (200 for 404) |
| 5 | `publishing-off` for GitHub copies too | `Publishing_off_still_checks_a_GitHub_copy` |
| 6 | the paused branch unreachable | "says publishing is switched off for a link copy, and offers no review" |

No mutation survived.

## 6. The security review

A fresh subagent got the spec's §2, §3.4, §3.5, §5c and the diff of `src/`, not this report's reasoning. Its answers: a token opens nothing but its copy and its images (**no**); it outlives neither its publication, its owner nor its hour (**no**; the hour is checked when opening and in the count's update); the token, its lookup id and the path reach no log line or metric label in the shipped configuration (**no**); neither the page nor `/s/t` is a way into the web app (**no**: the `/s/t` fallback wins over the SPA's, no session is honoured below `/api`, no CORS, the making request has the Origin check, the reader cookie is `SameSite=Strict` on `/api/s`). Findings:

1. **Low, log configuration.** A provider-specific level (`Logging__Console__LogLevel__Microsoft.AspNetCore=Information`) overrides the server's filter, and a fault while the answer is written is Kestrel's to log, with the request's scope. **Documented** in security-architecture §7 (`bde48c4`).
2. **Low, timing.** The 404 reasons differ in database round trips (malformed none, unknown or expired one, withdrawn two, owner inactive three, past the ceiling four); only someone who holds a valid token could tell them apart, and the reader endpoint behaves alike. **Accepted**, for Chris.
3. **Info:** spec §3.4 says the collection is in `ContentCollections` (see §8).
4. **Info:** a garbage path below `/s/t/` takes the `ReaderRead` allowance (D7: the limits first), bounded per address and network, as on the reader endpoint. **Accepted.**
5. **Info:** a dump shows how many AI fetches each publication had within its tokens' hours (`count`), as the spec's ceiling needs. Within spec.
6. **Info:** a link made at the moment of a withdrawal lingers until its hour is up; it opens nothing. Making links is bounded by the reader limits.
7. **Info:** expiry follows the app's clock; a clock stepped back lengthens a link by the step.

## 7. Decisions made without Chris

1. **Where the token documents live** (open point 1). Options: in `publications` (with a `kind`), or a collection of their own. Chose `ai_links` of their own (the brief's "own space"), with `vault` and `publication` in the clear; deleted by `publication` in `DeleteCopyAsync`, by `vault` in `DeleteCopiesOfVaultsAsync` and in `DeleteVaultsAsync` beside `ContentCollections` (in the transaction and the sweep after it), **not** in `ContentCollections`, so tokens readers make never count towards the owner's storage. The deleted token documents are counted in no deletion receipt: the figure would tell how many AI links readers made.
2. **The owner check** (open point 2). Options: the owner through the publication document (which doesn't name the owner), or sealed beside the key. Chose the owner's id sealed in the token's slot beside the publication key, as the reader slot holds it; checked `Active` on every fetch. Reactivation within the hour revives the token, as the brief says.
3. **`/s/t/` and the reader paths** (open point 3). `ReaderPaths.Is` matches `/s/t` and below; `GET`/`HEAD` routes plus an anonymous `MapFallback("/s/t/{**path}")` with the plain-text 404 for every other method and path.
4. **L-02's state** (open point 4). A state value `publishing-off` (the same string as `ReaderApi.OffType`), left out of `checkedPaths` as null is; the Skills screen shows the spec's sentence in place of the update state and offers no Review for it; Review and Take answer 404 with that type and sentence.
5. **The image references.** `MarkdownLink.Parse` finds the embeds outside fenced and inline code and wikilinks; an embed whose destination is `f/<n>` or `<f/n>` with `n` among the copy's files becomes `<base>/s/t/<token>/files/<n>` followed by ` (<media type>)` (beside the image, so a tool that drops images still shows it); one whose `n` isn't among them is left out. Spliced by offsets, so everything else stays byte for byte. Two divergences from the page, harmless: an indented code block, or a fence inside a blockquote, holding a literal `![x](f/0)` is rewritten (the fair copy writes `f/<n>` only for embeds it found outside code).
6. **The snapshot line** says the time in UTC (`snapshot of 2026-11-11 20:40 UTC`): the server knows no reader's time zone.
7. **A blocked network at `/s/t/`** answers the plain-text 404 sentence (the brief), where the reader endpoint answers 429.
8. **The limits are taken for every request to a `/s/t` route**, a malformed token too (the limits first, as the reader endpoint does with a code); the fallback paths take none. A request is counted against the ceiling once its token opens, before the file index is checked: a bad index on a live token uses one request.
9. **A fault at `/s/t/`** answers a plain-text 500, "Something went wrong; try again later.", and is logged by its type only, from outside the request's log scope.
10. **The reader page:** the sentence "Valid until 21:40. Anyone with this link can read this note until then: it is for an AI to fetch, not to post in a chat."; a refusal replaces a link shown before; the button stays focusable while it waits.
11. **The `<noscript>` sentence:** "A person with the link can also make a link for AIs on the page ("Link for your AI"), which a fetch tool reads with a plain GET for an hour."

## 8. Deviations, surprises and open points for Chris

- **Spec §3.4 and the brief disagree:** the spec says the token collection "is in `ContentCollections`"; the brief (binding) says it must not count towards storage. The code follows the brief (decision 1). The spec is unchanged, as asked; you may want to amend its §3.4.
- **"Nothing about readers is counted":** security-architecture §4 and §7 and publish design §3.12 say so of the reader. The token's request count is a small exception, explained in the new §7 row; the older sentences are left as they were.
- **Log scopes:** the request's log scope names the path, which now holds a secret. The handlers write nothing within a request, but an operator who enables scopes or a provider-specific level for `Microsoft.AspNetCore` gets the token (documented in security-architecture §7).
- **Timing** (security review 2): accepted; decide whether constant-time refusals are wanted.
- `dockerd` had to be started by hand; Node 26 and pnpm installed by hand.
- Reviews were one subagent with two sections instead of two subagents; Tasks 5 and 6 reviewed by me.
- The skills design and the publish design got the amendments §6 names; no other spec was changed.

## 9. Hand-test checklist

Two browser contexts: **Alice** (owner, signed in) and **Bob** (reader, no account). On staging, or through Playwright with one context each.

1. Alice: publish a note that embeds two images (`/Work/Pump/Ruling.md`). Copy its link. **Passes:** the publish dialog shows a `/s#…` link and nothing about AI links.
2. Bob: open the link. **Passes:** the note shows; beside **Copy as Markdown** there is **Link for your AI**.
3. Bob: press **Link for your AI**. **Passes:** a read-only field shows `https://<host>/s/t/` and 32 characters; **Copy**; "Valid until HH:MM" an hour on in Bob's time zone, and the sentence ending "not to post in a chat."
4. Bob: press **Copy**. **Passes:** "Copied", and the clipboard holds the link.
5. Bob: press **Link for your AI** again. **Passes:** a different link replaces the first; the first still works (each press makes its own token).
6. On a phone width (375 px): **Passes:** the field takes the whole line, Copy below it, no horizontal scroll.
7. Stand-in for an AI's fetch tool: `curl -si https://<host>/s/t/<token>`. **Passes:** `200`, `Content-Type: text/markdown; charset=utf-8`, `X-Robots-Tag: noindex, nofollow`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`, no `Set-Cookie`; the body begins with `# <title>`, then "A published note, snapshot of … UTC; links to other notes are plain text."; the two images as `![…](https://<host>/s/t/<token>/files/0) (image/…)`.
8. `curl -si https://<host>/s/t/<token>/files/0`. **Passes:** the image with its image type; `curl -si …/files/9`: `404`, text/plain, "This link for AIs has expired or the note is no longer published. Ask for a new one on the note's page."
9. `curl -si -X POST https://<host>/s/t/<token>` and `curl -si https://<host>/s/t/whatever/else`: **Passes:** the same plain-text 404, never the web app's HTML. `curl -sI` on the link: `200` without a body.
10. Alice: withdraw the publication. Repeat step 7 and 8. **Passes:** the plain-text 404 for both; Bob's reader page answers as for a withdrawn note.
11. Alice: publish again; Bob opens the new link and makes an AI link. An admin deactivates Alice: **Passes:** step 7 gives the 404. Reactivate within the hour: **Passes:** `200` again.
12. With `KUMBU_PUBLISH_ENABLED=false` (restart): step 7 gives `404` "Published notes can't be read on this server."; Bob's button says the same. Switched on again within the hour: `200`.
13. Wait an hour (or check after 21:40): step 7 gives the 404.
14. The server's log (`docker compose logs kumbu`) during steps 3–13: **Passes:** no line contains `/s/t/` or a token.
15. Skills, L-02: Dana adds a skill from Alice's skill link, Alice changes and updates the shared skill, Dana's Skills screen says "Update available". Restart with `KUMBU_PUBLISH_ENABLED=false`: **Passes:** Dana's copy shows "Publishing is switched off on this server, so updates from links can't be checked right now.", no Review button; a GitHub copy, if any, is checked as before. Switched on again: "Update available" and Review are back.

## 10. Pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/192

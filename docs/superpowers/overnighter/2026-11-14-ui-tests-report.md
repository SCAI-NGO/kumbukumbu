# Report: automated UI tests, and the specific run for 0.5.0

The run built Kumbu²'s first end-to-end UI suite: Playwright with headless Chromium against a stack built from this checkout and started from nothing. With it, it ran the specific test run for 0.5.0. It found two bugs (F-03 Medium, F-01 Low) and two unclear points (F-02, F-04). The product is unchanged.

Pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/200 (branch `feature/ui-tests-0-5-0`).

## Environment

- **Machine:** 4 CPUs, 15 GB RAM, Linux 6.18, the VM's clock in UTC. No restart of the worker during the run.
- **Node:** 22 was installed; I put Node 26.13.1 into `/opt/node26` from the nodejs.org tarball, and pnpm 10.32.1 with `npm install -g`.
- **Docker:** `dockerd` wasn't running; I started it by hand (`dockerd &`). Docker 29.9.2, Compose 5.6.1 and buildx 0.38.1 were installed; nothing to add. The hard file limit was already 20000; the suite's compose file sets MongoDB's `ulimits` to it anyway.
- **Playwright:** `@playwright/test` 1.63.0 (released 2026-09-04), the version of the pre-installed Chromium (`/opt/pw-browsers`, build 1243), so no browser download.
- **The image build behind the session's proxy.** The first `up --build` failed in both images: `npm install -g pnpm` inside the build containers stopped with `SELF_SIGNED_CERT_IN_CHAIN`, as the session's egress proxy re-signs TLS and the base images don't trust its CA. The repository's `Dockerfile` stays as it is. Instead, `web/e2e/stack/compose.proxy-ca.yaml`, layered only when `E2E_PROXY_CA` names a CA file, swaps the build stages' base images (by their exact pinned references) for local copies that also trust that CA, through BuildKit's named contexts (`additional_contexts`). npm and NuGet then download as usual. The server's final stage (`aspnet`) keeps its own base; the pdf image's final stage shares its base with its build stage, so it carries the extra CA too, on its internal network without a route out. Outside this VM nothing changes: without `E2E_PROXY_CA` the layer isn't used. This took about 10 minutes, within the brief's 20-minute budget, so the `dotnet publish` fallback wasn't needed; `E2E_STACK=dotnet` is written (`web/e2e/stack/dotnet.sh`) but untried.
- **Stack times:** `up.sh` builds and starts in about 1 min 35 s with the base layers cached (the first build, with downloads, about the same plus 1.5 min for the base images); a full cycle (`pnpm e2e --grep @smoke`: down, build, up, setup, test, down) took 1 min 52 s. Nothing of `kumbu-e2e` (containers or volumes) remained afterwards.

- **Superpowers:** available. I used `writing-plans` and `subagent-driven-development`. There was a fresh implementer per tag task, then a task review (spec and quality), fix rounds that resumed the implementer, and scoped re-reviews. At the end, one whole-suite review against the checklists and one fix wave. Deviations:
  - Implementers ran two to three at a time on one shared stack, each in its own files and folders. The brief allows that.
  - Implementers never committed; I committed and pushed.
  - Two small fix rounds were accepted without a separate re-review: flags round 2 moved one assertion; the AI link fixes were checked in the whole-suite review.

## The plan and its review

The plan is `docs/superpowers/plans/2026-11-14-ui-tests.md`. A fresh subagent reviewed it, with the spec, the brief and the plan but not my reasoning. It found 15 problems. All were folded in before Task 1 or turned into tasks:
- **Fixture layout:** the setup seeded every tag's fixtures into Work, which would have made the aliases ambiguous. Tag fixtures moved to `fixtures/tags/`, and each test seeds its own.
- **Fresh contexts:** `browser.newContext()` doesn't apply the config's `use`. Fresh contexts now get the project's zone, locale, viewport and base URL. The setup's browser runs in Vienna, because the app presets the calendar's zone from it.
- **Re-runs:** the setup can't run twice on one stack. Implementers ran with `--no-deps` after one setup.
- **Rate limits:** sign-in and PIN ledgers, with the extra time tests need to wait them out.
- **Phone failures:** a phone-only failure is marked with `onPhone(testInfo)`, which covers `serial-phone` too.
- **Project dependencies:** a failure would have skipped the serial phase. `run.sh` now runs two phases.
- **Dates:** fixtures take dates relative to today (`{{today±N}}`).
- **Calendar:** several mapping gaps.
- **Mermaid:** one feature tag per test.
- **Self-contained tasks:** each task carries its own commands.
- **Folders:** each test's folder gets a random part.
- **Finding screenshots:** taken automatically.
- **Small fixes:** `mongo:7.0` in the fallback, `logs.sh`, `gh pr checks` (done through the GitHub tools).

The plan's flag list for flags #9 named "Orange", which the app doesn't have; the implementer read the real list from `web/src/flags/api.ts`.

## Task 0, planned against measured

| | Planned (brief) | Measured |
|---|---|---|
| Setup and first build | about 15 minutes | about 35 minutes. The proxy's CA in the build took about 10 of them, and writing the stack scripts the rest |
| Web baseline on `71ca4d5` | about 155 files, 2,700 tests, green, chunk warning | 162 files, 2,981 tests passed, build green with the known chunk-size warning, 3 min 03 s |
| Flaky files under load | `commentRanges`, `SkillPage` zip tests | neither failed |
| Stack build and start | not stated | 1 min 35 s (`up.sh`, cached base layers); a full smoke cycle from nothing 1 min 52 s |
| Web gate after the config change | unchanged | 2,981 tests passed, build green (with `tsc -p e2e`), 3 min 00 s |

## The tags

Each test's title names its source and step. In the tables, "left out" lists the steps not tested and why. "Break" is the deliberate break each tag was seen to fail on before it was restored. Every test was also seen to fail during writing or under a batch of breaks.

### @smoke (spec §4), `web/e2e/smoke/smoke.spec.ts`

| Step | Test |
|---|---|
| 1 | `smoke #1: Alice signs in and sees Work, Welcome and Skills, and Home after the PIN` (@serial) |
| 2 | `smoke #2: a note renders its text, a wikilink and an image` (@phone) |
| 3 | `smoke #3: an edit is saved, survives a reload, and adds one version to History` |
| 4 | `smoke #4: a renamed note's links are rewritten` |
| 5 | `smoke #5: search by words and by tag finds the note` |
| 6 | `smoke #6: a note moved to the trash is restored` |
| 7 | `smoke #7: an uploaded image opens` |
| 8 | `smoke #8: Bob sees Work, can't edit, and never sees Home` (@serial) |
| 9 | `smoke #9: an edit in one tab shows in the other without a reload` |
| 10 | `smoke #10: hidden Home leaves no trace in the tree, search or Recent changes; shown again with the PIN` (@serial) |

- **Left out:** nothing.
- **Interpreted by the app's own specs:**
  - #2: a vault image in a note is a chip that opens the lightbox (attachments foundation spec, line 44). The test opens the chip and checks that the lightbox's image has loaded.
  - #9: Read mode shows the "Changed elsewhere" bar live; refreshing in place is the collab design (§9), and "Refresh Read mode automatically" is off by default. A marker on `window` proves there was no reload.
- **Break:** #3 expecting two new versions: `Expected 3, Received 2` at `smoke.spec.ts:88`.
- **Result:** 10 tests, 11 runs, all passed. About 24 s on a running stack, well under the spec's three minutes.

### @calendar (calendar report 1–17, flags report 20–24), `web/e2e/calendar/`

- **calendar.spec.ts** (per folder):
  - #1 Settings zone
  - #2 the empty week (in `Welcome`: Work always has dated notes from other tests)
  - #4 Add to calendar
  - #6 ✓, Undo and the reason
  - #8 Later
  - #10 Also ongoing
  - #13 at 375 px, two tests: layout in light and dark; 48 px, which holds F-01
  - #17 the agent through MCP and the console
  - flags #23 `When:`
- **whats-on.spec.ts** (all @serial):
  - #3 Today
  - #5 Zahnarzt in Home
  - #7 What's on and the badge (@phone)
  - #9 month and grid
  - #11 Dates I couldn't read
  - #12 the tag filter
  - #14 Bob, in a zone of his own (Europe/London for the test, restored afterwards)
  - #15 the discreet vault
  - #16 the board card
  - flags #20 today's day note
  - flags #21 timed entries
  - flags #22 overdue by years
- **Left out:**
  - #15's "the app icon's badge is cleared and recomputed", and flags #24: both need the installed PWA (Badging API).
  - #13 runs on the phone only.
- **Brief against the app:**
  - `mkcp` needs a key with maintain, so #17 makes one for the step.
  - A vault's board lives only at `/<vault>/Board/`, so #16 is @serial on `/Work/Board` and removes it afterwards.
- **Conditional skips:**
  - flags #21 within two hours of midnight in Vienna.
  - #17 after 22:55 Vienna time.
  - The setup refuses to start within half an hour of midnight.
- **Break:** #4 expecting `upcoming: 8d`: `- upcoming: 8d / + upcoming: 7d`.
- **Result:** 22 tests, 23 runs, passed. F-01 fails as expected on the phone.

### @flags (flags report 1–9), `web/e2e/flags/flags.spec.ts`

- **Tests:** #1 (@phone) through #9, one test each: #5 and #8 with @phone, #9 through the console.
- **#6:** the test records the requests while Mark done is tapped. Exactly one write proves "one new version": the server merges a writer's writes of one changeset, so the history count alone can't tell.
- **Left out:** nothing.
- **Break:** #2 expecting `flag/blue`: `Received string: "tags: [pump, flag/red]"`.
- **Result:** 9 tests, 12 runs, passed.

### @explorer (flags report 10–13), `web/e2e/explorer/explorer.spec.ts`

- **Tests:** #10 (@phone), #11, #12 (@phone), #13 (@serial).
- **#11:** the words "pump cert" are typed together with the chips; spec §3.1 says words and flag must both match. A chip with an empty box is tested too.
- **Locator exception:** the tree exposes no `aria-level`. Depth is read from the rows' layout in one helper (`rowsOf`), commented in the file.
- **Left out:** nothing.
- **Break:** #10 expecting Valve to remain: `explorer.spec.ts:59`, "Expected - 1".
- **Result:** 4 tests, 6 runs, passed.

### @recent (flags report 14–17), `web/e2e/recent/recent.spec.ts`

- **Tests:** #14, #15, #16 and #17, all @serial @phone.
- **#16:** hides once in this tab and once from a second tab, and checks without retrying once the page's answer has come. Two PIN entries.
- **#17:** Bob's vault chips are exactly his own. His `Welcome` and Alice's can't be told apart by name, so a leak would show as a second chip.
- **Left out:** nothing.
- **Break:** #14 with the order swapped: `Received string: "ValveWork/…/Pump · You · …"`.
- **Result:** 4 tests, 8 runs, passed. The writer's wording is F-04.

### @mermaid, @embeds, @blockrefs, @mentions (mermaid report 1–21), `web/e2e/mermaid/`

- **mermaid.spec.ts:**
  - #1, #3 the picture
  - #2 Show source
  - #4 redrawn in place on a scheme switch (`emulateMedia`, no remount)
  - #5 and #6 broken and refused diagrams
  - #11 at 375 px
  - #13 the reader page
  - #14 Copy as Markdown and the AI link
  - @embeds #1 the embed frame
- **blocks.spec.ts:**
  - #7 and #12 block links, with 60 paragraphs above so the scroll is real
  - #8 a missing block
  - #9 unlinked mentions
  - #10 Write mode
  - #15–#19 through the console
  - #20 `mv` through MCP, with a second `Plan.md` so the rewrite always runs
- **Reading the diagram:** the page's CSP refuses a fetch of its own `blob:` URLs, so the tests keep a reference to each Blob handed to `URL.createObjectURL` (an init script) and read the SVG from it. This test aid changes nothing the page shows.
- **Left out:** #21, which needs Bob as a write member.
- **Break:** #17 with an extra line: `blocks.spec.ts:182`, "- BREAK".
- **Result:** 21 tests, 22 runs, passed. #11 runs on the phone only.

### @aliases (aliases report 1–13), `web/e2e/aliases/aliases.spec.ts` (all @serial)

- **Tests:**
  - #1+#2
  - #2's heading jump, which holds F-03
  - #3
  - #4, covered by the @phone runs of #2 and #3
  - #5–#7 through the console
  - #8 through MCP
  - #11 publishing through MCP with a key that may publish, read at 375 px too
  - #12
  - #13
- **Clean-up:** each test removes its notes, and its publication, even on failure. Aliases resolve vault-wide.
- **Left out:** #9 (Bob with Write) and #10 (Carol).
- **Break:** #5 expecting `(name)`: the line with ` (alias)` was received.
- **Result:** 8 tests, 12 runs. 10 passed, and F-03 fails as expected on both projects.

### @ailink (AI link report §9), `web/e2e/ailink/ailink.spec.ts`

- **Tests:** #1–#10, #6 @phone only, and #14 the server's log (@serial).
- **"Bob" in this checklist** is a reader without an account: a context without a session.
- **Secrets:**
  - Link and token checks go through booleans with fixed messages.
  - Every token request goes through a wrapper that fails with a fixed message.
  - Failure screenshots are off for the file.
- **Left out:**
  - #11: deactivating Alice, the primary admin.
  - #12: a restart with publishing off.
  - #13: an hour passing.
  - #15: Dana, and a restart.
- **Break:** #7 expecting `text/plain`; it received `text/markdown; charset=utf-8`.
- **Result:** 11 tests, 12 runs, passed.

### @photo (flags report 18–19), `web/e2e/photo/photo.spec.ts`

- **Tests:**
  - #18: Take photo beside Upload here on the phone, and none at desktop width.
  - #18: Bob, whose folders have no menu at all.
  - #19: three photos on the phone through the file chooser, named `Photo <date> <time>.jpg`, `… 2.jpg`, `… 3.jpg`, each with the upload's confirmation.
- **The clock:** #19 fixes the browser's clock (`page.clock.setFixedTime`) so the three land in one second. The name and its suffix come from the browser's clock and tree (`photoName.ts`), not the server.
- **Left out:** the real camera (needs a device).
- **Break:** #18 at desktop width expecting Take photo: "Expected: 1, Received: 0".
- **Result:** 3 tests, 5 runs, passed.

## The two final runs

`time E2E_PROXY_CA=/root/.ccr/ca-bundle.crt pnpm -C web e2e:0.5.0`, twice in a row, each from nothing (build, up, setup, tests, down):

| | Wall time | Parallel phase (desktop, phone) | Serial phase (serial, serial-phone) | Exit |
|---|---|---|---|---|
| Run 1 | 5 min 33 s | 81 runs: 76 passed (F-01 among them, as expected), 5 skipped, 0 failed, 1.2 min | 41 passed (F-03 twice among them, as expected), 0 failed, 2.5 min | 0 |
| Run 2 | 5 min 28 s | the same | the same | 0 |

- **Identical:** yes, test by test. The 5 skips are the phone-only steps on the desktop: mermaid #11, ailink #6, both calendar #13 tests, and photo #19.
- **Clean-up:** afterwards no `kumbu-e2e` container or volume was left, and `.auth/` and `.results/` were gone.
- **The trial run before the final fixes** found two order-dependent failures on a fresh stack, both fixed before the final runs:
  - Bob's calendar zone was still UTC, because the app presets it on a user's first visit to the shell. The setup now waits for both users' presets.
  - A menu check for Bob that couldn't fail for the reason it named was dropped.
- **Earlier flakes and their fixes:**
  - Photo #18: the menu was closed by the tree's scroll (F-02). Its checks now sit inside the retry.
  - Mermaid: one unidentified failure in one of eight runs, which didn't come back.

**The web gate at the end:** `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`. 2,981 tests passed, build green with the known chunk-size warning, 3 min 25 s.

## Findings

Ordered by severity. Screenshots are in `docs/superpowers/overnighter/2026-11-14-ui-tests-0.5.0/`. A bug's test is marked `test.fail` with the annotation `{ type: "finding", description: "F-NN" }`, and its own failing assertion names the finding. Once a fix lands, Playwright reports "expected to fail, but passed".

### F-03 (Medium, bug): a link to a heading opens the note at its top, not at the heading

- **Test:** `aliases #2: roadmap opens Kumbukumbu at its Roadmap section` (@aliases @serial @phone), `web/e2e/aliases/aliases.spec.ts`.
- **Steps:**
  1. In `Work`, a note `Projects/Kumbukumbu.md` with `aliases: [Kumbu, K²]`, `# Kumbukumbu`, 40 paragraphs, then `## Roadmap`.
  2. A note `Diary/2026-11-12.md` with `Talked with Bob about [[Kumbu]] and the [[K²#Roadmap|roadmap]].`
  3. Open the diary and tap `roadmap`.
- **Expected:** Kumbukumbu opens at its Roadmap section. That is checklist step 2 of the aliases report and the aliases design, line 44.
- **Actual:** Kumbukumbu opens at its top. The Roadmap heading is out of view, and the title is in view. At 1280 px and at 375 px.
- **Scope:** any `[[note#Heading]]` link, with or without an alias. The links and sections specs don't describe the web app's behaviour for heading links; the aliases spec and checklist do.
- **Screenshots:** `F-03-desktop.png`, `F-03-phone.png`.
- **Likely place:**
  - `web/src/note/MarkdownView.tsx` around lines 233–238 passes only a block as the router state (`state={block ? { block } : undefined}`) and drops a `#Heading` subpath.
  - `web/src/note/NoteView.tsx` around lines 265–276 (`NoteAt`) handles `block` and `line` only.

### F-01 (Low, bug): the calendar's Today and This week are 44 px high

- **Test:** `calendar #13: every control is at least 48 px high` (@calendar @phone, phone only), `web/e2e/calendar/calendar.spec.ts`.
- **Steps:** at 375 px with touch, in dark and in light, open Work's calendar week.
- **Expected:** every control is at least 48 px high: calendar design §7.11 ("targets at least 48 px") and calendar checklist step 13.
- **Actual:** Today and This week are 44 px high. Every other control passes; the layout test beside it asserts them.
- **Screenshot:** `F-01-phone.png`.
- **Likely place:** both buttons use `secondaryButtonClass` (`min-h-11`, 44 px) from `web/src/components/form.tsx` around lines 42–43, at `web/src/calendar/CalendarPage.tsx` lines 157 and 160.

### F-02 (Low, unclear): a tree menu opened right after a note opens closes itself

- **Tests that meet it:**
  - `smoke #6` and `smoke #7`: the vault and folder menus.
  - `flags #18` (photo): the folder menu.
  - `ailink` on the phone: the tree row's menu didn't offer Publish… reliably, so the phone publishes from the note's own menu.
  
  Each retries the opening inside a bounded `toPass`. No test is marked: it is a behaviour, not a checklist step.
- **Steps:**
  1. Open a note deep in the tree.
  2. At once, open a folder's or the vault's ⋯ menu in the tree.
- **Expected:** the menu stays open until a choice is made or it is dismissed.
- **Actual:** the menu sometimes closes on its own a moment after opening.
- **Why unclear:** a scroll closing a menu is deliberate; whether the tree's own scroll to the open note should count is not specified.
- **Screenshot:** none; the menu is gone before one can be taken.
- **Likely place:** `web/src/components/Menu.tsx` around lines 26 and 60–66 closes the menu on any scroll. `web/src/shell/TreeDrawer.tsx` around lines 175–178 scrolls the selected row into view after a navigation.

### F-04 (Low, unclear): Recent changes names the writer "You" and `"alice"`, where the spec's example says "Alice"

- **Tests:** `flags #14` and `flags #17` (@recent), `web/e2e/recent/recent.spec.ts`. They assert the current wording.
- **Steps:** as Alice, write a note in `Work`, then open Recent changes as Alice and as Bob.
- **Expected:** the checklist (flags report, step 14) shows "Certificate · Work/Pump · Alice · 2 minutes ago".
- **Actual:** Alice sees "You"; Bob sees `"alice"` in quotes.
- **Why unclear:** spec §4 says the rows show who "in the wording of Activity", which is what the app does (`WriterLabels.Quoted`). The previous run's UX check recorded the same point.
- **Screenshot:** `recent-desktop.png`.
- **Likely place:** `src/Kumbu.Server/Api/WriterLabels.cs` (`Quoted`) and `writerText` in `web/src/api/activity.ts`, used by `web/src/recent/RecentPage.tsx` (line 179).

## `data-testid` added

None. Two places use layout rather than role, label or text, each commented in its file:
- The explorer's `rowsOf` reads tree depth from the rows' indentation. The tree exposes no `aria-level`.
- The calendar's #13 measures the boxes of every control.

## Decisions made without Chris

1. **The image build behind the session's TLS-re-signing proxy.** Options: change the `Dockerfile` (not allowed), fall back to `dotnet publish` (allowed after 20 minutes), or swap the build stages' base images for copies that trust the proxy's CA through BuildKit's named contexts, in a compose layer of the suite's own used only when `E2E_PROXY_CA` is set. I chose the last: the product's files stay untouched, the images are still built from this checkout with Compose as the spec wants, and outside such a proxy nothing changes. Cost if wrong: the pdf image built here carries one extra CA in its trust store, on a network without a route out.
2. **How Alice joins.** bootstrap-admin's link is followed in the browser; the recovery key screen is passed with **Copy** (into the headless browser's own clipboard, gone with its context) and the checkbox; the key is never read or stored. The setup takes no screenshots (its screens show recovery keys).
3. **Discreet Home through the API the web app calls** (set the PIN, reveal, mark, hide), not by clicking through Settings and the PIN dialog in the setup: the same endpoints, fewer moving parts, and the PIN dialog is tested in the tests that reveal Home.
4. **Fixtures:** the vaults' own notes in `web/e2e/fixtures/Home` and `Work` (seeded once); each tag's in `web/e2e/fixtures/tags/<tag>/`, copied by each test into a folder of its own (`/Work/e2e-<source>-<step>-<project>-<random>/`), with `{{today±N}}` replaced by dates in Vienna when seeded. Options were one shared copy per tag or per test; per test keeps the tests independent, as the spec asks.
5. **Global lists and discreet state run last, one at a time:** the projects `serial` and `serial-phone`, in a second Playwright phase of `run.sh` with one worker, so a failure among the parallel tests doesn't skip them (project dependencies would) and nothing else writes meanwhile.
6. **Fresh sessions start without cookies.** A context made with the project's options carried Alice's saved session; signing in over it ended that session for every other test. Found by the calendar implementer; fixed in the shared support.
7. **Rate limits:** sign-ins and PIN entries go through ledgers in `.auth/` across workers, and the sixth within a minute waits until the oldest is a minute old (the server's own window, not a wait for the page); such tests get the extra time. Keys are made without a password confirmation while the window is open (confirmations count against a limit too).
8. **Agent steps:** reads through the web console's API, writes through MCP with a key Alice makes per worker (write on `Work`; keys that need `maintain` or publishing are made by the test and revoked). Home is hidden in the shared session, so a key can't name it; the discreet tests write into Home through their own revealed session.
9. **A finding's test names its finding in its failing assertion** (`expect(x, "F-03: …")`). `test.fail` takes any failure as the expected one; the shared fixture reports a finding's test that failed for another reason as a failure, so a broken precondition can't pass as the finding.
10. **No HTML report.** Its steps name every address and filled value, reader links with their codes among them. The list reporter stays; `run.sh` removes `.auth/` when the run ends.
11. **Type check:** `pnpm build` runs `tsc --noEmit -p e2e` as well (covered, not excluded); Vitest excludes `e2e/**`.
12. **`page.clock`** only in `@photo` #19, to make three photos in one second and so force the " 2"/" 3" names; the name and the suffix come from the browser's clock and tree (`photoName.ts`), not the server.
13. **Bob's calendar zone in calendar #14:** set to Europe/London for the test and restored afterwards. Alice and Bob share Vienna, so "in his own zone" could not be told apart otherwise. The test is @serial, as the zone belongs to the account.
14. **calendar #2's empty week in `Welcome`.** Work always has dated notes from other tests, and the step is about the empty week's text and layout.

## Deviations, surprises and open points for Chris

- **The image build needs a CA in this VM.** See Environment. On a normal machine or in CI, `E2E_PROXY_CA` stays unset.
- **Discreet state, rate limits and shared sessions shaped the suite more than anything else:**
  - Signing in over a saved session ends it.
  - Password checks, PIN entries, sign-ins and reader reads each have a per-minute limit.
  - Discreet mode belongs to a session.
  
  The support code (`web/e2e/support/`) now handles all of these. A new test only uses `freshSession`, `revealDiscreet` and `endHidden`.
- **Tests that hide nothing:** where an assertion could pass whatever the app did, the reviews caught it and the test was changed. Examples: a history count the server merges, a check run with no menu open, a scroll that wasn't needed.
- **Secrets:**
  - Playwright's call logs print a request's URL and headers when it fails, and its HTML report names every address and filled value. The suite wraps requests so a failure prints only the method and path, and it has no HTML report.
  - Failure page snapshots (`error-context.md`) can still show a reader link while a run is going on. `run.sh` deletes them at the end of the run; the stack and its secrets are gone by then.
- **Day notes:** a note named `2026-11-12.md` counts as the day note of 12 November wherever it sits (mermaid and aliases fixtures). The calendar tests filter by tag, so they don't see them.
- **For Chris to decide:** whether F-02 and F-04 are bugs; and whether the reader page should keep showing Mermaid as code (already open).

## How to run the suite

- **Full run:** `pnpm -C web e2e`. Stack up, every test, stack down; exits non-zero on any unexpected result.
- **The 0.5.0 specific run:** `pnpm -C web e2e:0.5.0`, which is `--grep "@smoke|@calendar|@flags|@explorer|@recent|@mermaid|@embeds|@aliases|@ailink|@blockrefs|@mentions|@photo"`.
- **Environment:** Node 26 and pnpm 10.32.1, Docker with Compose 2.17+ and buildx; the pre-installed Chromium or `pnpm -C web exec playwright install chromium`. Behind a TLS-re-signing proxy, add `E2E_PROXY_CA=<CA file>`. `E2E_STACK=dotnet` runs the server from `dotnet publish` instead of the images (untried).
- **A specific run for the next release:**
  1. Tag that release's new tests with their feature tags.
  2. Add a script `e2e:X.Y.Z` with `--grep "@smoke|@<new tags>"` beside `e2e:0.5.0`.
  3. Run it twice from nothing before the hand tests.
- **While writing tests:**
  1. Run `e2e/stack/up.sh` and `pnpm exec playwright test --project=setup` once.
  2. Run with `--no-deps --project=desktop --project=phone`.
  3. Run `--no-deps --workers=1 --project=serial --project=serial-phone`.

## Pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/200

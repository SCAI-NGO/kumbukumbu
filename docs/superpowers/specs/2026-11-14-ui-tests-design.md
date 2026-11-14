# Automated UI tests: Playwright against a fresh stack

Status: approved by Chris, 2026-11-14.

## 1. Goal and decisions

Kumbu² gets an end-to-end UI test suite: Playwright with headless Chromium drives the web app of a stack built from the repository and started from nothing. Its first use is the **specific test run for 0.5.0**, in an overnighter's cloud VM, before Chris's hand tests on 2026-11-15. A fix overnighter starts the same night from what this run finds.

Decisions, each with Chris's reason:

- **A stack of our own in the VM, not staging.** Chris: that gives us 100% reproducibility from a fresh install, every time. So each run starts with `docker compose down -v`, builds the images from the checkout, and sets up users and notes through the app's own paths.
- **Two kinds of run.** A **full run** tests everything. A **specific run** tests a release's new features to see that they work. Chris: we usually run the specific test before a release, and the full test only when a change reaches deep into many features we already had.
- **One suite, tagged by feature.** A specific run is the full suite filtered by tags. A small `@smoke` set always runs with it: a cheap guard against new features breaking shared code.
- **Devices stay with Chris.** The real phone camera, installing the PWA, Obsidian and outside WebDAV clients aren't part of this suite.

## 2. What the suite is

- **Where:** `web/e2e/` holds the tests, and `web/playwright.config.ts` the configuration. `@playwright/test` becomes a dev dependency of `web/`, pinned to an exact version. This is the one new dependency the spec allows. Chromium is the only browser.
- **The stack:** `web/e2e/stack/` holds a compose file that layers `compose.yaml` and `compose.build.yaml` with test settings. It leaves `KUMBU_DEV_TOKEN` empty, so nothing is seeded and no request bypasses the web app's checks (amended after review), and sets `KUMBU_PORT=8080`, `KUMBU_PUBLIC_URL=http://localhost:8080`, `KUMBU_SEARCH=false`, `KUMBU_SKILLS_MARKETPLACES=false`, and leaves everything else at the defaults. The PDF sidecar runs. Embeddings are off because the 630 MB model download makes the run slow and flaky; search by words still works. A script `web/e2e/stack/up.sh` builds and starts the stack and waits until `/healthz` answers `ok`. `down.sh` removes it together with its volumes.
- **If images can't be built in the environment**, the suite starts the server from `dotnet publish` with the built web app (`ASPNETCORE_WEBROOT`), plus a fresh MongoDB container and the PDF sidecar if it builds, and the report says so. Chris: building the images isn't central to this work, since CI on GitHub already shows that they build. Everything else stays the same: a fresh start, setup through public paths, `localhost:8080`.
- **Setup data** goes only through public paths: `kumbu bootstrap-admin` for Alice, then sign-up, vault creation, invitations and note writes through the web app's HTTP API, the way the web app itself calls it. Nothing is written into MongoDB directly. The fixture notes live as Markdown files in `web/e2e/fixtures/`. The users are Alice, the admin, with `Home` (discreet) and `Work`, and Bob, a read-only member of `Work`. Every test gets its own notes or a reset of them, so the tests don't depend on each other.
- **Viewports:** every test runs at 1280 px in a light scheme. Tests tagged `@phone` run a second time at 375 px with touch emulation in a dark scheme.
- **Commands:**
  - `pnpm -C web e2e`: full run (stack up, every test, stack down)
  - `pnpm -C web e2e --grep "@smoke|@calendar|…"`: specific run
  - The 0.5.0 specific run is written as a script `pnpm -C web e2e:0.5.0`, so it can be run again unchanged.
- **Not in CI yet.** Putting the suite into CI comes later, with the dark factory. The suite has to be able to run unattended, though: no prompts, and it fails with a non-zero exit code.

## 3. Tags and sources

Each test carries one feature tag. It follows its source checklist step by step, and the test's title names the source and step number, e.g. `calendar #7: an overdue task of 11 months`.

| Tag | Source |
|---|---|
| `@smoke` | Section 4 below |
| `@ailink` | `docs/superpowers/overnighter/2026-11-11-ai-link-report.md`, §9 hand-test checklist |
| `@mermaid`, `@embeds`, `@blockrefs`, `@mentions` | `2026-11-12-mermaid-and-parity-report.md`, hand-test checklist |
| `@aliases` | `2026-11-12-aliases-report.md`, hand-test checklist |
| `@calendar` | `2026-11-13-calendar-report.md`, hand-test checklist, and the calendar fixes in `2026-11-14-flags-explorer-report.md` |
| `@flags`, `@explorer`, `@recent`, `@photo` | `2026-11-14-flags-explorer-report.md`, hand-test checklist |

Steps that need a real device, a second machine or an outside client are left out, and the report lists each one with the reason ("needs a phone camera"). `@photo` tests the touch path with a file chooser and a fixture image instead of a camera. Agent steps (`sh tags flag`, `rg --tag flag/red`) run through the web app's console (`web/src/console/`).

## 4. The smoke set

About ten tests, together under three minutes:

1. Alice signs in. The vault list shows `Home` and `Work`, beside the `Welcome` and `Skills` vaults every account gets.
2. Open a note. Its text renders, and so do a wikilink and an image.
3. Edit the note and save it. Reloading shows the change, and History has one new version.
4. Create a note in a folder, then rename it. Links to it are rewritten.
5. Search by words finds the note. Search for a tag finds it too.
6. Move a note to the trash and restore it.
7. Upload an image. It opens.
8. Bob signs in. He sees `Work`, and can neither edit a note nor see `Home`.
9. Two tabs as Alice: an edit in one shows up in the other without a reload (sync).
10. Hide `Home` (discreet). Nothing in the tree, search or Recent changes names it. Show it again.

## 5. What the run does with what it finds

- **The run reports; it doesn't fix the product.** A test that fails because of a real bug stays in the suite, marked `test.fail()` with an annotation that gives the finding's id (`F-01`, …). The suite then stays green, and once the fix overnighter repairs the bug, Playwright reports "expected to fail, but passed", which shows the fix took.
- Each finding is triaged as one of: **bug** (the app contradicts its spec or checklist), **test** (the test was wrong, so fix the test), or **unclear** (spec or checklist leave it open). Each bug gets a severity on the security audit's scale (High, Medium, Low), a repro made of the test's name and steps, the expected and actual behaviour, and the screenshot.
- **Screenshots** of failures, and one screenshot per tag at each viewport, go to `docs/superpowers/overnighter/2026-11-14-ui-tests-0.5.0/` as PNG files, kept small. The Playwright HTML report and traces aren't committed.
- The findings are written so that the fix overnighter's brief can link to them unchanged: one section per finding, ordered by severity.

## 6. End-to-end example

`pnpm -C web e2e:0.5.0` in the VM:
1. `down.sh` removes an older stack. `up.sh` builds `kumbu` and `pdf` from the checkout, starts them with a fresh MongoDB, and waits for `/healthz`.
2. Global setup: `bootstrap-admin` gives Alice's sign-up link. Alice signs up, creates `Home` (discreet) and `Work`, and fills them from `web/e2e/fixtures/`. She invites Bob to `Work` read-only, and Bob accepts.
3. Playwright runs `@smoke` plus the 0.5.0 tags at 1280 px in light, and the `@phone` tests again at 375 px in dark.
4. Say `flags #2` finds that the tree shows no flag after "Certificate". The test is marked `test.fail()` with `F-03`, the screenshot is saved, and the report gets `F-03 (Medium): tree misses the flag icon after setting Red`.
5. `down.sh` removes the stack and its volumes.

## 7. Out of scope

- Fixing any bug the suite finds (that is the fix overnighter's job).
- CI integration, scheduling, webhooks.
- Firefox, WebKit, real devices, the Obsidian plugin, outside WebDAV clients, MCP clients other than through the console or a plain API key call.
- Visual regression (pixel comparison of screenshots).
- Testing against staging, production or any other real host.
- A full run of everything before 0.5.0. Writing tests for 0.4.0 features beyond the smoke set.
- Changes to the server or the web app, except, at most, adding `data-testid` attributes where no role or label can find an element. Each such addition is listed in the report.

## 8. Security boundary

- The stack listens on `127.0.0.1` only, runs without a dev token, and runs with test data and generated secrets only: passwords, the PIN and API keys are random per run and are never written into the repository, the report or a screenshot.
- No real host is contacted except the registries, npm and Playwright's browser download needed to build.
- Product code: **not touched**, apart from the `data-testid` attributes of §7. In particular `Auth/`, `Crypto/`, sharing, publishing, WebDAV, sync, OAuth and both CSPs stay as they are. A test never loosens a security setting to pass: if something only works with a weaker setting, that is a finding.

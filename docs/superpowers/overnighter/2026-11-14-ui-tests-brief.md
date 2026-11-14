# Overnighter brief: automated UI tests, and the specific run for 0.5.0

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP. It is written in C# (.NET 10), with a React/Vite web app, on MongoDB. 0.5.0 is feature complete: the calendar, Mermaid diagrams with embeds, block references and unlinked mentions, aliases, the AI link, flags, the explorer's search and order, Recent changes, Take photo. This run builds Kumbu²'s first end-to-end UI test suite: Playwright with headless Chromium, against a stack built from this checkout and started from nothing. Then it runs the **specific test run for 0.5.0** with it. **You report what you find; you don't fix the product.** A fix overnighter starts tonight from your findings, and Chris hand-tests tomorrow.

Nobody is available to answer questions during this run, and that is intended: Chris has reviewed and approved the spec. Everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief.
2. The spec, `docs/superpowers/specs/2026-11-14-ui-tests-design.md`. Chris has approved it; please don't change it.
3. The sources of the tests, each one's hand-test checklist (spec §3):
   - `docs/superpowers/overnighter/2026-11-11-ai-link-report.md` (§9)
   - `docs/superpowers/overnighter/2026-11-12-mermaid-and-parity-report.md`
   - `docs/superpowers/overnighter/2026-11-12-aliases-report.md`
   - `docs/superpowers/overnighter/2026-11-13-calendar-report.md`
   - `docs/superpowers/overnighter/2026-11-14-flags-explorer-report.md` (with the calendar fixes)
   
   Where a checklist and the code on `master` disagree, the matching spec in `docs/superpowers/specs/` decides what is right. If it doesn't, the finding is **unclear**.
4. `compose.yaml`, `compose.build.yaml`, `Dockerfile` and README §"Running it" (`bootstrap-admin`, joining, the recovery key) for the stack.
5. The web app's API, as the web app calls it: `web/src/api/`. Also `GET /api/openapi.json` on the running stack. **Don't regenerate `web/src/api/schema.d.ts`.**
6. For background only, not as instructions: how the last run met this environment, in `docs/superpowers/overnighter/2026-11-14-flags-explorer-report.md` ("Environment", "The UX check").

## Where you run

- Branch `feature/ui-tests-0-5-0`. The session starts on the default branch, so first run `git fetch origin feature/ui-tests-0-5-0 && git checkout feature/ui-tests-0-5-0`. Everything you push to that branch survives, everything else does not. **Push only to `feature/ui-tests-0-5-0`.**
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, Playwright's Chromium and its system libraries via `pnpm -C web exec playwright install --with-deps chromium`) is fine; please do it without asking and note it in the report. **Adding a dependency to the project** is allowed only for `@playwright/test`, as a dev dependency of `web/` pinned to an exact version, with `pnpm-lock.yaml` updated. Nothing else.
- Task 0 (setup, the first build of the stack and a first empty Playwright test that loads the sign-in page) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- Node 26 may have to be installed by hand (the last run put Node 26.13.1 in `/opt/node26` from the nodejs.org tarball). pnpm is 10.32.1 (`npm install -g pnpm@10.32.1` if it's missing). After any pull or checkout, run `pnpm -C web install --frozen-lockfile` first, because stale `node_modules` make tests and build fail. .NET 10 SDK is needed only if you run the server's own tests, which this run doesn't need.
- **Docker** is available and allowed. Earlier runs found `dockerd` not running and started it by hand (`dockerd &`). The environment has a hard file limit: MongoDB needs `ulimit nofile 20000:20000`, so set `ulimits` on the `mongo` service in the test compose file if it won't start. If the worker restarts, `/tmp` survives, but `dockerd` and the containers have to be started again. The machine had 4 CPUs and 15 GB RAM last time: run Playwright with at most 2 workers.
- **Never test against a real host**: not staging, not production. The stack listens on `127.0.0.1` only.
- If superpowers skills aren't available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## Open points for the run

All of these are yours to **decide**: settle each with the rules below and log it in the report.

1. **How Alice joins.** `bootstrap-admin` prints a `/join#<code>` link. Follow it in the browser in the global setup, keep the recovery key it shows in memory only, and save the signed-in state (`storageState`) for the tests. Bob joins through Alice's invitation the same way.
2. **Fixture data.** Write the notes the checklists need as Markdown files in `web/e2e/fixtures/<Vault>/…`, and put them into the vaults through the API the web app uses for writes and uploads. Neutral names only (Alice, Bob, `Home`, `Work`, `Pump/Certificate.md` as the checklists use them). Calendar tests need dates relative to today: compute them in the setup, never as fixed dates.
3. **Time.** The stack runs on the real clock. Write tests for "overdue", "today" and "upcoming" by placing notes relative to now. Where a step can only be tested with a fake clock, leave it out and list it.
4. **Discreet vaults.** Find out from the code how a vault becomes discreet and how it is hidden and shown again (`web/src/discreet/`, the server's discreet-vault API). Use the app's path for it.
5. **Settled before the start** (binding, not open):
   - The run reports and doesn't fix the product. A test that finds a real bug stays, marked `test.fail()` with an annotation `{ type: 'finding', description: 'F-NN' }` (spec §5).
   - `data-testid` is allowed only where no role, label or text finds the element. Each one is listed in the report. No other change to `src/` or `web/src/`.
   - `@smoke` is the ten tests of spec §4. The scripts are `e2e` (full) and `e2e:0.5.0` (smoke plus the 0.5.0 tags) in `web/package.json`. Both bring the stack up and take it down again.
   - Vitest must not pick up `web/e2e/`: check `vitest.config`/`vite.config` and exclude it if needed. `pnpm -C web test --run` and `pnpm -C web build` must stay green, and the type check must cover or explicitly exclude `web/e2e/` (pick one, record it).
6. **Pitfalls found by the review of this brief** (binding; each was checked against the code):
   - **Build the checkout, never pull.** `compose.build.yaml` keeps `image: …:latest` next to `build:`, so a plain `up` may pull 0.4.0 from ghcr. In the suite's compose file, set the images to local names (`kumbu-e2e:local`, `kumbu-e2e-pdf:local`) with `pull_policy: build`, and have `up.sh` run `up -d --build --wait`. Then assert that the server's version (`GET /api/version`, or what `uname -a` says in the console) is the checkout's dev version, not a release.
   - **Its own Compose project.** `up.sh` and `down.sh` always pass `-p kumbu-e2e` and explicit `-f` files, and `unset COMPOSE_FILE` first. Otherwise `down -v` could remove a developer's local `kumbukumbu` volumes, recovery keys included.
   - **Compose in the VM is untested.** Earlier runs only ever used `docker run`. Check `docker compose version` (2.17 or newer, for `additional_contexts`) and buildx. Install the compose plugin and buildx if they're missing. If the nested Docker refuses the pdf service's `mem_limit`, `cpus` or `pids_limit`, the suite's compose file may drop **those resource limits only**, and the report says so. `read_only`, `cap_drop`, `no-new-privileges`, `user` and the internal network stay.
   - **Fallback, settled by Chris:** if the images can't be built in the VM after a reasonable try (about 20 minutes), run the server from `dotnet publish` (Release) against a fresh `mongo:7.0` container (see the last run's report for the `ulimit` and replica-set commands). Serve the built web app through `ASPNETCORE_WEBROOT`, with the same settings as the compose file (port 8080, `KUMBU_PUBLIC_URL`, search and marketplaces off, no dev token). `run.sh` takes this path on its own when `E2E_STACK=dotnet` is set. Record it in the report. CI already shows that the images build.
   - **Origin.** `src/Kumbu.Server/Api/Security.cs` (around lines 147–181) refuses every non-GET `/api` request from a session or anonymous caller unless `Origin` is exactly the origin of `KUMBU_PUBLIC_URL`, and a missing `Origin` counts as wrong. So the setup's API context sends `extraHTTPHeaders: { Origin: baseURL }`.
   - **Host and port are fixed.** `KUMBU_PORT=8080`, `KUMBU_PUBLIC_URL=http://localhost:8080`, `baseURL` `http://localhost:8080`: always `localhost`, never `127.0.0.1`. Chromium accepts the session cookie (`Secure; SameSite=Strict`) on `http://localhost`. Task 0 checks that the API context keeps and sends it; if it doesn't, use `context.request` from a signed-in browser context.
   - **The dev token isn't used.** Leave `KUMBU_DEV_TOKEN` empty (spec §2 amended), so the server seeds nothing and every request goes the web app's way. Point `KUMBU_VAULTS` at an empty temporary folder.
   - **`bootstrap-admin`:** `docker compose -p kumbu-e2e <files> exec -T kumbu kumbu bootstrap-admin`. It needs `KUMBU_PUBLIC_URL`, and only the link goes to stdout.
   - **Bob joins in two steps.** There is no open sign-up.
     1. Alice makes an account invitation as admin (`POST /api/admin/invitations`). Bob joins through `/join#<code>`.
     2. Alice makes a vault invitation for `Work`, role `read`, with `confirm: false` (`POST /api/vaults/Work/invitations`). Bob accepts it through `/share#<secret>`.
     
     Find the exact shapes in `src/Kumbu.Server/Api/` and `web/src/`.
   - **Every account gets `Welcome` and `Skills` vaults.** Assertions about the vault list allow for them (smoke #1).
   - **Rate limits and discreet state.** Sign-in is limited to 5 a minute per username. PIN entries are limited to 5 a minute per account, and the 10th in one session ends that session. Discreet mode, revealed or hidden, belongs to the session. So:
     - tests that touch discreet vaults run serially, each in a signed-in session of its own made for it, and each ends hidden;
     - no test signs out of the shared saved session;
     - count sign-ins and PIN entries against the limits.
   - **Isolation at runtime.** Recent changes, the explorer's "Last changed", What's on and its badge look across vaults. Each test works in its own folder (for example `/Work/e2e-<tag>-<n>/`) and asserts relative to it. Tests that check a global list or count run serially in a project with one worker. Where a checklist names a shared note (`/Work/Pump/Certificate.md`), give each test its own copy.
   - **Agent steps.** The console (`/console`, `POST /api/console`) only reads; use it for read steps. Write steps (`calendar set/close/…`, `mv`, `write`, `publish add`) go through `POST /mcp`, with an API key that Alice makes in the app (`POST /api/keys`, which needs her password). The key is held in memory only.
   - **Steps beyond the spec's people.** Some steps need more than Alice and read-only Bob, a restart or an hour of waiting:
     - Bob as a write member (aliases #9, mermaid #21);
     - Carol (aliases #10) or Dana (ai-link #15);
     - deactivating Alice, who is the primary admin (ai-link #11);
     - a restart with publishing off (ai-link #12, #15);
     - an hour passing (ai-link #13).
     
     Leave these steps out and list each with its reason. Don't add users, roles or stack phases.
   - **Secrets and state on disk.** The saved signed-in state goes to `web/e2e/.auth/`. Add it and `web/playwright-report/` to `.gitignore` and `.dockerignore`; that is allowed. Passwords, the PIN and keys are random per run and passed through environment variables or a file in that folder, never logged.
   - **One wrapper script.** `e2e` and `e2e:0.5.0` both call `web/e2e/stack/run.sh "$@"`. It brings the stack up, traps `down.sh` on exit, passes `"$@"` to `playwright test`, and exits with Playwright's exit code. Otherwise pnpm hands the arguments to the last command of a chain, and the exit code is lost.
   - **Time zone.** Set `timezoneId: 'Europe/Vienna'` and `locale: 'en-GB'` in `playwright.config.ts`, and compute "today", week labels and relative dates in that zone, never with Node's local date (the VM runs in UTC). Playwright's `page.clock` moves only the browser's clock, not the server's. You may use it only where the server's time doesn't matter, and the report says where.
   - **Phone project:** `isMobile: true, hasTouch: true`, 375 px, dark scheme. Task 0 checks that `matchMedia('(pointer: coarse)')` matches, since Take photo depends on it (`web/src/shell/TreeDrawer.tsx`). If the install hint covers controls, dismiss it the way a person would.
   - **Clipboard:** `context.grantPermissions(['clipboard-read', 'clipboard-write'])` for Copy steps. Where a checklist expects `https://<host>`, assert the origin of `KUMBU_PUBLIC_URL`.
   - **A bug on one viewport only** gets `test.fail(testInfo.project.name === 'phone', …)`, or the desktop equivalent, so the other project doesn't report "expected to fail, but passed".
   - **Hosts the build may reach:** Docker Hub, ghcr.io and mcr.microsoft.com (base images), nuget.org, the npm registry, nodejs.org, and Playwright's browser download. Nothing else.
7. **If time runs short:** the stack and the smoke set first, then the tags in this order: `@calendar`, `@flags`, `@explorer`, `@recent`, `@mermaid` and `@embeds`, `@aliases`, `@ailink`, `@blockrefs` and `@mentions`, `@photo`. Whatever is left goes into the report and `docs/superpowers/open-items.md`.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-14-ui-tests.md`.
2. It starts with **Task 0: baseline**. On `feature/ui-tests-0-5-0` before any change, run `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build` and record the exact counts. Known state: about 155 files and 2,700 web tests, build green, with a known chunk-size warning. Task 0's measurement wins. **Known flaky under load:** `web/src/note/commentRanges.test.tsx` "reads comments in linear time" and the zip tests of `web/src/skillpage/SkillPage.test.tsx`. If one fails, run its file alone three times, record the result, and carry on. Then build and start the stack once, and time it.
3. Every task stands alone: exact file paths, the exact command for each step and the expected outcome. No placeholders, no "see above", no "similar to Task N". One task per tag, or per group of tags.
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Subagent-driven (`superpowers:subagent-driven-development`): one fresh subagent per tag task, then one review that checks the tests against their checklist (every step covered or listed as left out, the assertions really check what "passes" says, nothing asserted that the checklist doesn't say). Subagents never merge, push or switch branches; you commit and push. Several subagents may share one running stack only if each works in its own vault or folder. Otherwise run them one after another.
- **Test discipline:** no fixed waits (`waitForTimeout`). Use Playwright's auto-waiting and `expect` with locators by role, label or text. A test is only done when it has been seen to fail: break the assertion once (or point it at the wrong note), watch it fail, restore it. Record that per tag in the report.
- **Triage each failure** (spec §5): run it again alone. If it passes now, it is flaky: fix the test's waiting, never add a retry. If it fails again, decide **bug**, **test** or **unclear**. A bug gets `test.fail()` with its finding id, a severity (High: data loss or a security breach, such as a read-only member writing, or a discreet vault showing; Medium: a feature doesn't do what its checklist says; Low: wording, layout, a target under 48 px) and a screenshot.
- **The run at the end:** after every tag is written, run `pnpm -C web e2e:0.5.0` from a fresh stack **twice in a row**. Both runs must give the same result. Record the times and counts of both. A test that differs between the two runs is flaky: fix it or take it out and list it.
- Commit and push after every task. **Commit messages** are imperative and free-form, with no Conventional Commits prefix (e.g. "Test the calendar in the browser"), and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended. Stage files by name, never `git add -A`.
- **The web gate**, at the end, with no agent editing web files meanwhile: `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`. The server's tests aren't affected, as the run changes nothing in `src/` beyond `data-testid`. If you add any, run `pnpm -C web test --run` for the touched components too. CI runs the rest; check it on the pull request with `gh pr checks`.
- **Style:** British English in code, comments, docs, commits and user-visible text. Match the surrounding code's naming, idiom and comment density, and add comments only where the reasoning isn't obvious. New TypeScript files and shell scripts start with `// SPDX-License-Identifier: AGPL-3.0-or-later` (`#` in shell).
- **Names in the repository:** no employer, customer or workplace in code, docs, tests or examples (use Alice, Bob, Dana, `Home`, `Work`). The owner of the software, Second Circuit – Association for digital freedom of thought, may be named where needed.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** The product's behaviour is unchanged. The existing web tests and build stay green. The suite leaves no state behind (`down -v`).
2. **Reproducible from a fresh install.** This is Chris's reason for the whole setup. Every run starts from nothing, sets up through the app's own public paths, and gives the same result twice. Prefer the plain way: one compose file, one setup, locators a person would use.
3. **Opens no new security hole.** The stack stays local, secrets are random and never written down, and a test never loosens a security setting to pass.
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, don't guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the PR description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version: rollout is something Chris does himself.
- No secrets in logs, assertions, screenshots or the report: the dev token, passwords, recovery keys, API keys, invitation and join codes.
- Specs stay as they are. If one looks wrong, mention it in the report.
- **Security boundary:** spec §8. **Not touched:** everything under `src/`, and everything under `web/src/` apart from `data-testid` attributes, so in particular `Auth/`, `Crypto/`, sharing, publishing, WebDAV, sync, OAuth and both CSPs. The repository's `compose.yaml` and `compose.build.yaml` stay as they are: test settings go into the suite's own compose file.
- **At the end:** add a row with the pull request's link to `docs/superpowers/open-items.md`, plus rows for what is left: CI integration of the suite, a full run, the steps left out because they need a device or a fake clock, and every finding (bug or unclear) by id. Add each row only if it isn't there yet.

## Out of scope

- Spec §7, in full. In particular, fixing any bug the suite finds.
- The open Dependabot pull requests and all other dependency upgrades.
- Everything else in `docs/superpowers/open-items.md`.

## The report

Do these in order. Push the last task. Open the pull request from `feature/ui-tests-0-5-0` to `master` as a **draft**, titled "Test the web app end to end with Playwright". Then write the report, with the pull request's link and the `open-items.md` rows, as the last commit, push, and mark the pull request ready. The report is `docs/superpowers/overnighter/2026-11-14-ui-tests-report.md`, and the screenshots go to `docs/superpowers/overnighter/2026-11-14-ui-tests-0.5.0/`. The report contains:
- the environment, and the time to build and start the stack;
- the plan review's findings and what you did with them;
- Task 0, planned against measured;
- for each tag: its tests (source step → test name), the steps left out with reasons, the deliberate break and its failure, and the result;
- the two final runs: times, counts, identical or not;
- **the findings**, one section each, ordered by severity: id, severity, kind (bug or unclear), the test, steps, expected, actual, the screenshot's path, and for a bug the likely place in the code if you saw it. Write these so a fix overnighter's brief can link to them unchanged;
- every `data-testid` added;
- the decisions made without Chris;
- deviations, surprises and open points for Chris;
- how to run the suite (the two commands, and what a specific run for the next release would look like);
- the pull request's link.

The pull request's description ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Don't merge it. Never force-push: if something has to come out of a pushed branch, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it's available; otherwise the pull request is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!

# Overnighter brief: the security review of 0.5.0

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. 0.5.0 is feature complete and on staging as `v0.5.0-pre.1`. It brings the AI link, Mermaid diagrams, embeds, block references, unlinked mentions, aliases, the calendar, flags, the explorer's search and order, Recent changes and Take photo.

Before it is released we want an independent, **defensive** security review of everything since 0.4.0. Check our own code against its security promises, show each weakness with a behaviour test or a code trace, rate it on one scale, and report it. **You fix nothing**: a joint fix run tomorrow takes your findings. A weakness you can show is worth ten suspicions.

**Blue team only.** This is code review and defensive testing of code whose maintainer, Chris, asked for it. No exploit development, no attack payloads, no attack chains, nothing against a live system. Every test checks the behaviour the code **should** have. Standard sanitiser test strings and bounded oversized or nested inputs count as ordinary unit-test inputs, each asserting that the code neutralises or limits them (spec §1).

Nobody is available to answer questions during this run, and that is intended: Chris has reviewed and approved the spec. Everything after it is yours, from the plan to the pull requests. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief.
2. The spec, `docs/superpowers/specs/2026-11-14-security-audit-0-5-0-design.md`. Chris has approved it; please don't edit it.
3. From the 0.4.0 audit, the parts the spec takes over:
   - `docs/superpowers/specs/2026-11-10-security-audit-0-4-0-design.md` §3 (the severity scale), §4 steps 1 to 4 (the method) and §5 (what may go public, and what may not);
   - for the shape of a good result, `docs/superpowers/overnighter/2026-11-10-security-audit-report.md`.
   - Of that spec's §5, only the publication rule applies (spec §4). Where it speaks of attackers or exploitability, read the trust boundary crossed and reachability (spec §3).
   - The 0.4.0 **confidential** findings, in the private clone: `audits/2026-11-10-0.4.0.md` on the branch `audit/0-4-0`, and their fixes on `fixes/0-4-0`. A 0.4.0 finding still open, or whose fix doesn't hold, is reported under its 0.4.0 id, in the private file only (spec §3).
4. `docs/security-architecture.md`, whose promises you check against: the threat model (§2), who can do what (§4), sessions, keys and connections (§6), web hardening (§7), publishing and the AI link.
5. Per area, its design spec under `docs/superpowers/specs/` and the security review in its report under `docs/superpowers/overnighter/`. They record what earlier reviews already found and settled.

   | Area | Spec | Report |
   |---|---|---|
   | AI link | `2026-11-11-ai-link-design.md` | `2026-11-11-ai-link-report.md` |
   | Mermaid and parity | `2026-11-12-mermaid-and-parity-design.md` | `2026-11-12-mermaid-and-parity-report.md` |
   | Aliases | `2026-11-12-aliases-design.md` | `2026-11-12-aliases-report.md` |
   | Calendar | `2026-11-12-calendar-design.md` | `2026-11-13-calendar-report.md` |
   | Flags, explorer, photo | `2026-11-14-flags-explorer-photo-design.md` | `2026-11-14-flags-explorer-report.md` |

## Where you run

- **Two repositories.**
  - Kumbu² (`SCAI-NGO/kumbukumbu`, public), branch `audit/0-5-0-security`. The session starts on the default branch, so first run `git fetch origin audit/0-5-0-security && git checkout audit/0-5-0-security`.
  - The private `SCAI-NGO/kumbukumbu-security`, for the confidential findings. The session is set up with it as a second repository, so it should be cloned beside the first one; if it isn't, clone it (`git clone https://github.com/SCAI-NGO/kumbukumbu-security`). Create the branch `audit/0-5-0` from `master` there and push to it.
  - **Push only to these two branches.**
- **The code under review is `v0.5.0-pre.1` (`8f3f4b55`).** Review it against `git diff v0.4.0..v0.5.0-pre.1`. The branch also holds #200 (the end-to-end suite, test code only) and this audit's spec and brief. Review the suite and `.dockerignore` at the branch head (`git diff 8f3f4b55..HEAD`).
- **Time:** have both reports written and pushed by **2026-11-15 06:00 UTC**. That is the measure for "if time runs short".
- The cloud environment may lack tools. Installing what you need is fine: SDKs, CLIs, packages, or `poppler-utils` to look at a PDF case. Please do it without asking and note it in the report. Adding a dependency to the project is not allowed.
- Task 0 (setup, the first build, and the tests of the areas you will touch) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- **Tools.**
  - .NET 10 SDK as `global.json` says (roll forward), with `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
  - Node 26, which may have to be installed by hand: the last run put Node 26.13.1 in `/opt/node26` from the nodejs.org tarball.
  - pnpm 10.32.1 (`npm install -g pnpm@10.32.1`).
  - After any checkout, run `pnpm -C web install --frozen-lockfile` first.
- **Docker.** Earlier runs found `dockerd` not running and started it (`dockerd &`). The server tests need MongoDB. Use a container of your own, because the environment's file limit is below what Testcontainers' `mongo` asks for:
  ```sh
  docker rm -fv kumbu-test-mongo 2>/dev/null
  docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all
  sleep 5 && docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate()'
  export KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true"
  export TESTCONTAINERS_RYUK_DISABLED=true
  ```
  If the worker restarts, start `dockerd` and the container again. Run server test classes one at a time: earlier runs lost test runs for lack of memory.
- **Image builds** behind this environment's TLS proxy need the trick of `web/e2e/stack/` (`E2E_PROXY_CA`; see `docs/superpowers/overnighter/2026-11-14-ui-tests-report.md`, "Environment"). You probably don't need any image.
- **Test commands.**
  - One core class: `dotnet test --project tests/Kumbu.Core.Tests --configuration Release --no-build -- --filter-class "*ClassName"`.
  - One server class: the same with `tests/Kumbu.Server.Tests`.
  - After changing a test, build again first with `dotnet build --configuration Release --no-restore`.
  - One web file: `pnpm -C web test --run <file>`.
- **Server tests fake the clock in the year 2037**, because MongoDB's TTL expires sessions by the real clock.
- **Never test against a real host**: not staging, not production, not GitHub's API beyond a stub. **Read-only advisory lookups are allowed**: `pnpm audit` (npm registry), `dotnet list package --vulnerable --include-transitive` (NuGet), and GitHub's advisory database if those fail. If they fail behind the proxy, record it as a deviation; don't guess.
- If superpowers skills aren't available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## The Factory board

This run reports its progress on a Kanban board in Kumbu², through the connector `Kumbu_Factory`. It reaches only the vault `Factory`.

1. At the start, read the board's rules: `sh board /Factory`.
2. Add your card with the `board` tool: vault `Factory`, title "Security review 0.5.0", column `Running`. The card's text gives:
   - the repository and branch;
   - this brief's path;
   - the trigger `trig_01ToeBk9kmgaq4JQsehYnhKG`;
   - the session's link;
   - a `## Steps` checklist of the **phases only**, never per area: "Task 0", "Phase 1: area reviews (7)", "Phase 2: second reviews", "Phase 3: confidential findings", "Phase 4: public report";
   - a `## Log`.
3. Tick steps as they finish, and add a log line at least every hour ("still in phase 1").
4. **The card never names an area, a file or a detail of a finding.** It never logs per-area progress either. Ids, severities and counts go on it only at the end of a phase, so that no finding can be tied to an area by its timing. The one exception is spec §6 (a High or Critical finding).
5. At the end, move the card to Done with the public pull request's link.

If the connector isn't available, say so in the report and carry on without the board.

## Open points for the run

All of these are yours to **decide**: settle each with the rules below and log it in the report.

1. **The private repository is unreachable.** Then write the confidential findings nowhere public. Put them in full into your final message in this session, which Chris can read. The public report and the pull request say only that this happened, with the ids and severities. If `audit/0-5-0` already exists there, check it out instead of creating it.
2. **The order of the areas.** Go by how much a weakness there would matter:
   - A: the anonymous AI-link path;
   - F: discreet vaults across the new views;
   - E: the new cross-vault endpoints and writes;
   - D: the calendar's writes;
   - G: the console's read-only promise and the rest;
   - B: rendering;
   - C: linking.

   Parallel subagents are welcome. Give each one area, with its files, the spec's trust boundaries for that area, and the classes of the 0.4.0 spec §4 step 2.
3. **A test that shows a Medium-or-higher weakness** never goes to the public branch or the public working tree. Write and run it in a separate worktree outside the public clone (`git worktree add <scratch>/audit-run v0.5.0-pre.1`), and never commit from it. Its code goes to `audits/2026-11-14-0.5.0/` in the private repository, as a file you could drop into `tests/…` to run. A test that shows a Low may be described in the public report.
4. **A finding, or a design decision?** Some behaviour is a decision an earlier spec made on purpose. For example: agents, WebDAV and sync see discreet vaults; the PIN hides, it doesn't encrypt; the flag endpoints work for keys and connections; Recent changes shows writers as Activity does. Such a decision is no finding unless it breaks a promise of `docs/security-architecture.md`. If you think a decision is unsafe, report it as a finding with "needs a decision from Chris".
5. **Known points:** the earlier reviews listed points they accepted or recorded. One listed as accepted is no new finding. One listed as fixed but not fixed is a new finding.
6. **A dependency with a known vulnerability** is a finding, rated by whether Kumbu² reaches the vulnerable code. Upgrading it is not part of this run.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-14-security-audit.md`. It names the areas and how each is reviewed. It can't name findings it doesn't know yet, and **the public plan never names a finding**.
2. It starts with **Task 0**. Set up, build (`dotnet restore --locked-mode && dotnet build --configuration Release --no-restore`), and run the test classes of the areas you will touch, recording the counts. The full suite was green on #200's merge in CI. Known flaky under load, each green when its class runs alone: `DiscreetSocketTests`, `HiddenVaultTests`, `DavShareTests`, `SyncHubReviewTests`, `AccountApiTests`, `RekeyTransitionTests`, `PairingTests`, and `web/src/note/commentRanges.test.tsx`. If one fails, run it alone three times, record it, and carry on.
3. **Phases:**
   1. Review every area: findings with evidence and severity.
   2. The independent second review of every Medium-or-higher finding and of every Low.
   3. The confidential findings written and pushed to the private repository, with its pull request opened.
   4. The public report and pull request.

   In phases 1 to 3 the only public commit is the plan.
4. **If time runs short:** stop reviewing and write what you have. The reports come first: an honest account of the areas you didn't finish is worth more than another finding.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in its findings, then commit the plan.

## How to work

- One fresh subagent per area. For every Medium-or-higher finding, and every Low, a second fresh subagent gets the claim and the code, not the first agent's reasoning. It may confirm the finding, rate it up, or refute it. It may rate a Low down only to "refuted".
- A Medium-or-higher finding that is refuted or rated down stays in the private file only, with the verdict.
- **Ids** are `S5-NN` and `L5-NN` (spec §4).
- Subagents never merge, push or switch branches. You commit and push.
- **Keep the public tree clean.**
  - Confidential notes and tests that show a Medium-or-higher weakness live in the private clone, never in the public working tree.
  - Stage files by name, never `git add -A`, `git add .` or `commit -a`.
  - Before every public push, run `git diff origin/audit/0-5-0-security --stat` and check that only the files you mean are in it.
- **Commit messages** are imperative and free-form, with no Conventional Commits prefix, and end with `Co-Authored-By: Claude Mythos 5.2 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- **Style:** British English in docs, commits and the reports.
- **No names of organisations, customers or employers** in the repository. Use Alice, Bob, Dana, `Home` and `Work`.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Gives nothing away.** Nothing about an unreleased weakness on a public branch, in a commit message, in a pull request or on the Factory card. When in doubt whether a detail is safe to publish, it isn't.
2. **Defensive only.** Tests check the behaviour the code should have; nothing more is built than is needed to show a weakness.
3. **Honest.** Rate up when in doubt, and write the doubt down. A suspicion you couldn't show is reported as a suspicion, with what would show it. Chris: honesty is part of the security architecture.
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, don't guess: leave that part out and record it in `docs/superpowers/open-items.md`: by a finding's id, or for a question without one as "Security review 0.5.0: a scope question, see the private file".

Log every decision you made this way. Put it in the report and in the public pull request's description under **Decisions made without Chris**, each with the options you saw and why you chose this one, revealing nothing confidential.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change any product code or the version.
- No secrets in logs, assertions or the reports. Test passwords and keys made by fixtures are fine.
- **Security boundary:** spec §9.
- **At the end:** add a row with the public pull request's link to `docs/superpowers/open-items.md`, with no details of a weakness, in the same commit as the report.

## Out of scope

- Spec §8, in full.
- Everything else in `docs/superpowers/open-items.md`, unless one item turns out to be a security finding.

## The report

Two pull requests, neither merged:

1. **Private**, in `SCAI-NGO/kumbukumbu-security`, from `audit/0-5-0` to `master`, titled "Security review of 0.5.0".
   - It holds `audits/2026-11-14-0.5.0.md` with every finding of Medium or higher, with the fields of spec §4 and the second review's verdict.
   - The tests that show them go under `audits/2026-11-14-0.5.0/`.
   - Push it as soon as phase 3 is done.
   - If nothing is Medium or higher, the file says "none" and why you trust that.
2. **Public**, in `SCAI-NGO/kumbukumbu`, from `audit/0-5-0-security` to `master`, titled "Security review of 0.5.0".
   - Its description gives the counts per severity (not per area), the Low findings in full, and the confidential findings by id and severity only. It also gives the decisions made without Chris, and ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
   - As the last commit, `docs/superpowers/overnighter/2026-11-14-security-audit-report.md`, with:
     - the environment;
     - the plan review's findings and what you did with them;
     - Task 0;
     - what was reviewed per area and how (the method, not what was found);
     - the counts per severity;
     - every Low finding in full;
     - the decisions made without Chris;
     - deviations, surprises and open points for Chris;
     - both pull requests' links. Link the private one by its URL only; it opens for Chris alone.

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it's available; otherwise the pull requests are the signal, and the report says so. Spec §6 says what to do at once on a High or Critical finding. You never fix it yourself: Chris does that, outside this run. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!

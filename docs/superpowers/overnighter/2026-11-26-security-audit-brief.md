# Brief: the security review of 0.7.0

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP. It is written in C# (.NET 10), with a React/Vite web app, a TypeScript data layer for the browser (`packages/eidolon-core`) and a Node PDF sidecar, on MongoDB.

0.7.0 is feature complete; staging runs `v0.7.0-pre.2` (`be695c0b`), and `master` is at `2fee5ad2`. It changed about 30,000 lines in 41 pull requests. It brings:
- the offline mode: the web app keeps chosen vaults sealed in the browser, to read and write without a connection, and uploads the changes when back;
- the server side of it: mirror slots, the devices list, the wipe signals;
- an app shell that opens offline, through a service worker that serves only the build's own files;
- `sh similar` and the system skill `synthesize-knowledge`;
- the graph out of preview, its edges by meaning;
- Similar notes, with Add link and Not related;
- the Published window, with Compare.

Before it is released we want an independent, **defensive** security review of everything since 0.6.0:
- check our own code against its security promises;
- show each weakness with a behaviour test or a code trace;
- rate each weakness on one scale;
- report it.

**You fix nothing**: a joint fix run takes your findings, together with those of Chris's hand tests. A weakness you can show is worth ten suspicions.

**Blue team only.** This is code review and defensive testing of code whose maintainer, Chris, asked for it:
- no exploit development;
- no attack payloads;
- no attack chains;
- nothing against a live system.

Every test checks the behaviour the code **should** have. The standard sanitiser strings, a sealed record with a flipped byte, a replayed older record, and bounded oversized or nested inputs count as ordinary unit-test inputs, each asserting that the code refuses, neutralises or limits them (spec §1).

Nobody is available to answer questions during this run, and that is intended: Chris has reviewed and approved the spec. Everything after it is yours, from the plan to the pull requests. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief.
2. The spec, `docs/superpowers/specs/2026-11-26-security-audit-0-7-0-design.md`. Please don't edit it.
3. The 0.6.0 audit, whose method and shape carry over: its spec `docs/superpowers/specs/2026-11-19-security-audit-0-6-0-design.md`, its brief `docs/superpowers/overnighter/2026-11-19-security-audit-brief.md` and its public report `docs/superpowers/overnighter/2026-11-19-security-audit-report.md` (above all §1, the environment and the lock). From 0.4.0, as the specs say: `2026-11-10-security-audit-0-4-0-design.md` §3 (the scale) and §4 steps 1 to 4 (the method).
4. In the private clone:
   - `audits/2026-11-22-0.7.0-offline.md` and `audits/2026-11-22-0.7.0-offline/` on `master`: the scoped review of the offline mode, with its findings, decisions, suspicions not shown and the status lines of its fixes. Area I starts there; its suspicions are re-checked and reported under a new id only if shown; the other areas must not report its points as new. Its highest ids tell you where this audit's ids start (spec §4). **The public texts never name its ids, findings or decisions**: they are paired with nothing public yet;
   - `audits/2026-11-19-0.6.0.md` on the branch `audit/0-6-0` (not merged into `master` there), with its fix records on `fixes/0-6-0`: the 0.6.0 findings for area I, and the best example of a good confidential result, including the controller's decisions on severity.
5. The promises you check against:
   - `docs/security-architecture.md`, at `2fee5ad2`: §2 (the threat model and its honest limits), §4 (who can do what), §6 with "Offline mirror" (the server's part of the copies), §7 (web hardening: the CSP, `KdfWorkerPolicy`, "Version and updates" with the service worker, and "Backups" under "Deployment shape");
   - `docs/superpowers/specs/2026-11-22-offline-mode-design.md` §3a and §4: the browser's promises, which the architecture document doesn't state yet (area H checks that gap);
   - `docs/adr/0002` to `0033`, without 0023 (0.8.0's; 0026 and 0027 are not on `master`): the decisions of 0.7.0. A decision is no finding unless it breaks a promise (open point 5).
6. Per area, the feature's spec under `docs/superpowers/specs/` (dated 2026-11-21 to 2026-11-23) and the security review in its pull request's description: `gh pr view <n>` for the PR numbers in spec §2. Without a `gh` login, the squash commit carries the same description: `git log -1 --format=%b <sha>`.

## Where you run

- **Two repositories.**
  - Kumbu² (`SCAI-NGO/kumbukumbu`, public), branch `audit/0-7-0-security`. Run `git fetch origin audit/0-7-0-security && git checkout audit/0-7-0-security`.
  - The private `SCAI-NGO/kumbukumbu-security`, cloned beside it; if it isn't, clone it. Create `audit/0-7-0` from `master` there.
  - **Push only to these two branches.**
- **The code under review is `2fee5ad2`** (`master` on 2026-11-26), against `git diff v0.6.0..2fee5ad2`. The branch adds only this spec and this brief.
- **Time:** have both reports pushed within about ten hours of the start. That is the measure for "if time runs short".
- **Tools.** Install what you need without asking, and note it in the report. Adding a dependency to the project is not allowed.
  - .NET 10 SDK as `global.json` says, with `DOTNET_CLI_TELEMETRY_OPTOUT=1` and **`MSBUILDDISABLENODEREUSE=1`** (and `-nodeReuse:false` on every `dotnet build`): in the 0.6.0 run an idle MSBuild node inherited the lock's file and blocked everyone for 15 to 30 minutes.
  - Node 26: the cloud environment's setup script puts it in `/opt/node26`; `export PATH=/opt/node26/bin:$PATH` and check with `node --version`. If it isn't there, install Node 26 from the nodejs.org tarball into `/opt/node26`, as earlier runs did.
  - pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if it's missing).
  - After any checkout, run `pnpm -C web install --frozen-lockfile` (it installs the web app and the Eidolon core, one workspace with one `pnpm-lock.yaml` at the root). The PDF sidecar (`pdf/src`) is unchanged since 0.6.0: skip its install and tests, except for its advisory lookup (below).
- **One heavy run at a time, enforced by a lock.** Every build and test run of every agent goes through one `flock` (for example `flock /tmp/kumbu-heavy.lock -c '<command>'`) with a time limit per command (`timeout 20m`). Two suites at once from parallel subagents have killed the container (exit 137). Tell each subagent so, and give it the lock's command.
- **Docker and MongoDB for the server tests** (learnt in earlier cloud sessions):
  ```sh
  dockerd > /tmp/dockerd.log 2>&1 &   # if it isn't running
  docker rm -fv kumbu-test-mongo 2>/dev/null
  docker run -d --name kumbu-test-mongo --network host --ulimit nofile=20000:20000 --log-opt max-size=20m mongo:9 \
    --replSet rs0 --bind_ip 127.0.0.1 --quiet \
    --wiredTigerEngineConfigString "file_manager=(close_idle_time=20,close_scan_interval=5,close_handle_minimum=250)"
  until docker exec kumbu-test-mongo mongosh --quiet --eval 'db.runCommand({ping:1})' >/dev/null 2>&1; do sleep 1; done
  docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate()'
  export KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true"
  export TESTCONTAINERS_RYUK_DISABLED=true
  ```
  - The log cap matters: an uncapped log once filled the disk.
  - After a container restart: start `dockerd`, run `docker start kumbu-test-mongo`, wait until the ping loop above answers and the replica set has a primary, then resume the stopped agents with a message saying what was lost. Tell them to `cd` into their own worktree and to use `git -C <path>` in every command.
- **Test commands:**
  - one core class: `dotnet test --project tests/Kumbu.Core.Tests --configuration Release --no-build -- --filter-class "Kumbu.Core.Tests.<Namespace>.<ClassName>"` (with the namespace: a bare `*ClassName` runs more classes than it names);
  - one server class: the same with `tests/Kumbu.Server.Tests`;
  - one web file: `pnpm -C web test --run <file>`;
  - the Eidolon core: `pnpm -C packages/eidolon-core test` (with its boundary check), one file with `pnpm -C packages/eidolon-core exec vitest run <file>`;
  - the service worker's tests are in `web/sw/*.test.ts` and run with the web app's;
  - **for the baseline, batch**: one runner start per class cost the 0.6.0 run two and a half hours; give several `--filter-class` in one call, or `--filter-namespace "Kumbu.Server.Tests.<Namespace>"` as the CI shards do (`.github/workflows/ci.yml`). Single classes only for re-runs;
  - the Eidolon core's type boundary: `pnpm -C packages/eidolon-core build` (its `tsc` is what keeps DOM types out; `boundary.test.ts` checks the imports).
- **Expected skips.** The tests bound to the tokenizer (`KUMBU_TOKENIZER`) and to the embedding model or a TensorX key skip here, because neither may be fetched (CI fetches the tokenizer from huggingface.co, which this run may not reach). Record the skipped count; a skip is neither a failure nor a pass.
- **A real browser, where a promise can only be shown in one** (the service worker, IndexedDB across tabs, the KDF worker's policy): `pnpm -C web e2e --grep "<tag or title>" --grep-invert "@search"` starts a stack of its own from the checkout (`web/e2e/stack/up.sh`; it needs Docker with buildx, else `E2E_STACK=dotnet`; behind a TLS proxy add `E2E_PROXY_CA=<the proxy's CA file>`) and stops it again. The 0.7.0 tags are `@smoke|@offline|@devices|@sw|@graph|@similar|@synthesis|@overview`.
  - **Never** `E2E_SEARCH=1`, `pnpm e2e:0.7.0` or `pnpm e2e:0.7.0:tensorx`: they fetch the model from huggingface.co or need TensorX.
  - Install the browsers first with `pnpm -C web exec playwright install --with-deps chromium chromium-headless-shell firefox`.
  - It is a heavy run: under the lock, with `timeout 60m` (the image build comes first).
  - `run.sh` passes `--pass-with-no-tests`: a run that reports no passed test counts as not run. It deletes `web/e2e/.results` when it exits, so keep the evidence from its console output.
  - Its stack never leaves the container.
- **Server tests fake the clock in 2037**, because MongoDB's TTL expires sessions by the real clock.
- **Measurements:** take them in Release, each beside a control under the same load, and state the load average. The 0.5.0 report shows how.
- **Never test against a real host**: not staging, not production, not TensorX, not huggingface.co, not GitHub beyond reading pull requests.
  - **Allowed:** read-only advisory lookups (`pnpm audit` at the root for the web app and the Eidolon core, `pnpm -C pdf audit --ignore-workspace` for the sidecar, checking that its output names the sidecar's own packages, `dotnet list package --vulnerable --include-transitive`, GitHub's advisory database).
  - If one fails behind the proxy, record it; don't guess.
- If superpowers skills aren't available, emulate subagent-driven work with the plain Agent tool and say so in the report.

## The Factory board

1. At the start, read the rules with `sh board /Factory` (connector `Kumbu_Factory`).
2. Add your card with the `board` tool: vault `Factory`, title "Security review 0.7.0", column `Running`. The card's text gives:
   - the repository and branch;
   - this brief's path;
   - the session's link;
   - `## Steps` with the **phases only**: "Task 0", "Phase 1: area reviews (9)", "Phase 2: second reviews", "Phase 3: severity check", "Phase 4: confidential findings", "Phase 5: public report";
   - `## Log`.
3. Add a log line at least every hour.
4. **The card never names an area, a file or a detail of a finding, and never logs per-area progress.** Ids, severities and counts go on it only at the end of a phase. The one exception is spec §6. On the card, a second look at something is "a further review", never which one.
5. At the end, move the card to Done with the public pull request's link.

If the connector isn't available, say so in the report and carry on.

## Open points for the run

All of these are yours to **decide**: settle each with the rules below and log it in the report.

1. **The private repository is unreachable.** Then write the confidential findings nowhere public. Put them in full into your final message in this session, which Chris can read; the public report says only that this happened, with ids and severities.
2. **The order of the areas.** Go by how much a weakness there would matter:
   1. A, the offline copy in the browser;
   2. B, the server side of copies;
   3. F, discreet vaults across the new views;
   4. C, the service worker and the shell;
   5. D, similarity, Similar notes and synthesis;
   6. E, the Published window and Compare;
   7. I, the earlier findings at this commit;
   8. H, cross-cutting;
   9. G, resource use.

   Parallel subagents are welcome, at most four area reviewers at a time, and only one heavy run at a time (the lock). Give each one area, with its files, the spec's trust boundaries for that area, the classes of the 0.4.0 spec §4 step 2, and the list of the offline review's points (ids and titles from the private file) so it doesn't report them as new.
3. **Features meeting each other** are what the per-PR reviews couldn't see (spec §1). Give every area reviewer these crossings, with one owner each (in brackets), and have them check the ones that touch their area:
   - a discreet vault revealed, or marked discreet, while a copy is open in another tab (F);
   - a second person signing in over a tab whose copy has changes waiting, then the first again, with the service worker's cache and the KDF worker in between (A);
   - a password change, an account re-encryption or "End other sessions" while another tab holds an unlocked copy (B);
   - `not-related` and `## Related` written offline by a writer, uploaded later, while a reader's Similar notes is open (D);
   - Similar notes, `similar` and the graph's suggestions for a vault this device doesn't keep, offline and online (D);
   - a published note edited offline, then Compare and Update copy once back (E);
   - a member removed, or a user deactivated, while their device is offline with changes waiting (B);
   - an agent following `synthesize-knowledge` in a shared vault whose notes a writer controls (D);
   - a restore of a backup older than a device's copy (B; reviewed against `docs/security-architecture.md` "Backups", not run).
4. **A test that shows a Medium-or-higher weakness** never goes to the public branch or the public tree. Every reviewer works in a worktree of its own outside the public clone, `git worktree add <scratch>/audit-<agent> 2fee5ad2`, with its own build and `pnpm -C web install --frozen-lockfile`, never committed from and removed once its tests are in the private clone. A test's code goes to `audits/2026-11-26-0.7.0/` in the private repository, as a file you could drop into `tests/…`, `web/src/…` or `packages/eidolon-core/test/…` to run, starting with `// SPDX-License-Identifier: AGPL-3.0-or-later` like its siblings. Every behaviour test has a positive control beside it (the same call where the code should allow it), as in the 0.6.0 report.
5. **A finding, or a design decision?** Some behaviour was decided on purpose (spec §8 lists examples; the ADRs record the rest). Such a decision is no finding unless it breaks a promise of `docs/security-architecture.md` or of the offline design §4. If you think it is unsafe anyway, report it with "needs a decision from Chris".
6. **Known points.** A point that an earlier review accepted is no new finding. A point listed as fixed that isn't fixed is new. A point of the offline review still open is reported under its own id in area I, not as a new one.
7. **A dependency with a known vulnerability** is a finding, rated by whether Kumbu² reaches the vulnerable code. Upgrading it is not part of this run.
8. **The severity check** (spec §3): one fresh reviewer measures every Medium-or-higher resource finding against a control.
9. **The architecture document's gap** (spec §2, area H): that `docs/security-architecture.md` doesn't yet state the browser's promises is itself a point for the report. Rate it as a documentation finding (Low unless a promise the code keeps nowhere else is missing) and propose the text's outline, not the text.
10. **A weakness in code older than 0.7.0**: report it under "Older than 0.7.0", counted apart (spec §4).
11. **The public pull request's CI.** It changes docs only. If a check fails, rerun it once, record the result in the report, and change nothing.

## Planning

1. Write the plan to `docs/superpowers/plans/2026-11-26-security-audit.md`. It names the areas and how each is reviewed, assigns every file of the diff to an area, gives each crossing its owner and each phase a time budget. **The public plan never names a finding.**
2. **Task 0:**
   1. set up;
   2. build with `dotnet restore --locked-mode && dotnet build --configuration Release --no-restore -nodeReuse:false`;
   3. run `pnpm -C web install --frozen-lockfile`;
   4. run the test classes of the areas you will touch (batched, see the test commands), `pnpm -C packages/eidolon-core test` and `build`, and the web app's offline and service-worker tests (`pnpm -C web test --run src/offline sw`), recording the counts and the skips.

   The suites were green on `2fee5ad2` in CI. If a test fails, run it alone three times, record the result, and carry on. The set-up and build should take about **20 minutes**; the baseline may run on beside the first reviews, under the lock.
3. **Phases:**
   1. Review every area: findings with evidence and severity.
   2. The independent second review of every Medium-or-higher finding and every Low.
   3. The severity check of the resource findings.
   4. The confidential findings pushed to the private repository, with its pull request opened.
   5. The public report and its pull request.

   Until phase 5 the only public commit is the plan. Open the public pull request as a draft right after the plan commit, so the report can name its link; open the private one at the first High or Critical finding or at phase 4, whichever comes first.
4. **If time runs short:** stop reviewing and write what you have. The reports come first: an honest account of the areas you didn't finish is worth more than another finding.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in what it finds, then commit the plan.

## How to work

- **Reviewers:**
  - one fresh subagent per area;
  - for every candidate, a second fresh subagent that gets the claim and the code, not the first agent's reasoning, and tries to reproduce and to disprove it. It may confirm the finding, rate it up or refute it; it may rate a Low down only to "refuted";
  - a claim first made by a second reviewer gets a third, who gets only the claim;
  - where two disagree on severity, a third reviewer or a measurement settles it; spec §6 fires on the higher rating at once.
- **Ids:** `S7-NN` and `L7-NN`, starting after the highest of each in the offline review's private file (spec §4).
- **Subagents** never merge, push or switch branches; you commit and push.
- **Keep the public tree clean:**
  - confidential notes and tests live in the private clone;
  - stage files by name, never `git add -A`;
  - before every public push, run `git diff origin/audit/0-7-0-security --stat` and check that only the files you mean are in it.
- **Before anything public is pushed** (the report, the pull request's description, `open-items.md`, the card's last lines): a fresh reviewer gets the public texts **and** the private file's summary and checks that nothing points to a confidential finding: no method paragraph that only makes sense if something was found, no line numbers of a public Low inside a confidential finding's code, no id paired with a pull request, file or feature. The 0.6.0 run's check found 19 such points.
- **Commit messages**, in both repositories: imperative and free-form, ending with `Co-Authored-By: Claude Mythos 5.2 <noreply@anthropic.com>`. Both pull requests' descriptions end with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **Style:** British English. Neutral names only: Alice, Bob, Carol, Dana, `Home`, `Work`.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Gives nothing away.** Nothing about an unreleased weakness on a public branch, in a commit message, in a pull request or on the Factory card. When in doubt whether a detail is safe to publish, it isn't.
2. **Defensive only.** Tests check the behaviour the code should have; nothing is built beyond what shows a weakness.
3. **Honest.** Rate up when in doubt, and write the doubt down. A suspicion you couldn't show is reported as a suspicion, with what would show it. Chris: honesty is part of the security architecture.
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, leave that part out and record it in `docs/superpowers/open-items.md`: by the finding's id, or, for a question without an id, as "Security review 0.7.0: a scope question, see the private file". Log every decision you made this way, in the report and in the public pull request under **Decisions made without Chris**, revealing nothing confidential.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change any product code or the version.
- No secrets in logs, assertions or the reports. Test passwords and keys made by fixtures are fine.
- **Security boundary:** spec §9.
- **At the end:** add a row with the public pull request's link to `docs/superpowers/open-items.md`, with no details of any weakness, in the same commit as the report.

## Out of scope

- Spec §8, in full.
- Everything else in `docs/superpowers/open-items.md`, unless an item turns out to be a security finding.

## The report

Two pull requests, neither merged.

**1. Private**, in `SCAI-NGO/kumbukumbu-security`, from `audit/0-7-0` to `master`, titled "Security review of 0.7.0".
- It holds `audits/2026-11-26-0.7.0.md` with:
  - the summary table;
  - the controller's decisions on severity;
  - every Medium-or-higher finding with the fields of spec §4;
  - the Lows kept private;
  - the state of the offline review's findings and of the 0.6.0 findings that 0.7.0 touches;
  - what each area checked and found sound.
- The tests go under `audits/2026-11-26-0.7.0/`, the reviewers' reports under `audits/2026-11-26-0.7.0/reports/`.
- Push it as soon as phase 4 is done.
- If nothing is Medium or higher, the file says "none" and why you trust that.

**2. Public**, in `SCAI-NGO/kumbukumbu`, from `audit/0-7-0-security` to `master`, titled "Security review of 0.7.0".
- Its description gives:
  - the counts per severity (not per area);
  - the Lows in full;
  - the confidential findings by id and severity only;
  - the decisions made without Chris.

  It ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- As the last commit, `docs/superpowers/overnighter/2026-11-26-security-audit-report.md`, with:
  - the environment;
  - the plan review;
  - Task 0;
  - per area what was reviewed and how (the method, not what was found);
  - the counts;
  - every public Low in full;
  - the decisions made without Chris;
  - deviations and open points;
  - both pull requests' links. Link the private one by its URL only.

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short `PushNotification` if it's available; otherwise the pull requests are the signal. Spec §6 says what to do at once on a High or Critical finding, and you never fix it yourself. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!

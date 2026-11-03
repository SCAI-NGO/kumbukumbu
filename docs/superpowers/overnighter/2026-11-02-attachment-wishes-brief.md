# Overnighter brief: the attachment wishes

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP. Five AI clients worked through its images and PDFs on 2026-11-01, and four things got in their way: a sheet photographed sideways, a GIF that stops at its first frame, `rg` finding a word but not where on the page, and regions that leave out a chart's labels. Tonight's work is `--rotate`, `--frame N` and `rg --regions`, plus an honest attempt at the labels. The attempt may well fail its measure, and a clear "not reliable enough" is a good result too.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it; everything you need from it is also here
3. Spec: `docs/superpowers/specs/2026-11-02-attachment-wishes-design.md` (approved; its assumptions are accepted, see the plan's header)
4. Plan: `docs/superpowers/plans/2026-11-02-attachment-wishes.md`; start with Task 0
5. As the tasks need them: `docs/security-architecture.md`, `docs/test-playbook-attachments.md`, `docs/notes-attachments-model-hands-on.md` (the form of the hands-on note)

## Where you run

- **Branch `feature/attachment-wishes`.** Everything you push there survives, everything else does not.
- **Pushing to `feature/attachment-wishes` is explicitly allowed and expected after every task, even if your environment assigned you another branch.** You have this permission now; please don't wait for anyone to give it. A previous run lost hours waiting for exactly that. If you were put on another branch, switch to `feature/attachment-wishes` and push there: `git push -u origin feature/attachment-wishes`.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report.
- Task 0 (setup plus first build and tests) should take about **15 minutes**; the last run here needed about 25, with the Docker notes below. If it takes much longer, something is off: please note it and carry on if you can.
- **There was no dry run.** The machine this was planned on has no dotnet, node or docker, so no command in the plan has been executed. The baseline comes from CI (run 41511547330, on `8a7c64e`, the code your branch starts from): `dotnet test` total 3264, succeeded 3260, skipped 4, failed 0; web 84 files, 1346 tests; sidecar 6 files, 78 tests. To look for yourself: `gh run list --repo SCAI-NGO/kumbukumbu --branch master --workflow CI --limit 1`, then `gh run view <id> --repo SCAI-NGO/kumbukumbu --log`. Record what you measure, and any difference. Known failures before you start: none.
- .NET 10 SDK as `global.json` says (10.0.100, roll forward); `echo $DOTNET_CLI_TELEMETRY_OPTOUT` is expected to print `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if it is missing).
- **Docker** is set up by the environment's setup script (a `dockerd` with `socat`). Test containers are available and allowed: the server tests start MongoDB through Testcontainers (`mongo:7.0`, see `tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", please check whether the setup script's daemon is still running, and start it again if you can.
  - If Docker can't work at all, use an external replica-set MongoDB: install `mongod` 7.0, start it as a single-node replica set, and set `KUMBU_TEST_MONGO` (see `ExternalMongo` in `TestImages.cs`).
  - The last run here met a hard file limit below what Testcontainers' `mongo` asks for. What worked: `docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all --wiredTigerEngineConfigString "file_manager=(close_idle_time=5,close_scan_interval=2,close_handle_minimum=250)"`, then `docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate({_id:"rs0",members:[{_id:0,host:"localhost:27017"}]})'` and `export KUMBU_TEST_MONGO="mongodb://localhost:27017/?replicaSet=rs0&directConnection=true"`.
  - Give every full run a fresh container, with `docker rm -fv kumbu-test-mongo` first: mongod runs out of file handles over many runs, and without `-v` anonymous volumes fill the disk.
  - If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- **For Tasks 12, 13 and 14:** `uv`, if it is missing (`curl -LsSf https://astral.sh/uv/install.sh | sh`). **For Task 14 only:** LibreOffice, which you may install: `apt-get update && apt-get install -y --no-install-recommends libreoffice-calc fonts-liberation`. If LibreOffice can't run after a fair try, the verdict on the labels is "not measured" and that item is left out.
- The 4 skipped tests need the real embedding model and a TensorX key. Neither is available here, and skipping them is expected. Running as root can skip 2 more.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by its own subagent. Then you, the controller, push.
- Subagents commit locally, and never merge, push or switch branches; you push.
- Push after every task, to `feature/attachment-wishes`.
- Commit messages imperative and free-form ("Let img turn a picture"), no Conventional Commits prefix, ending with this line: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The pull request's body ends with: `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- The test filter is Microsoft.Testing.Platform's: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`, repeatable. `FullyQualifiedName~` doesn't work here, a glob in mid-name gives "Zero tests ran", and "Zero tests ran" is a failure.
- **Full verification at the end of each phase**, whatever it touched, as CI does, each line:

  ```sh
  dotnet restore --locked-mode
  dotnet build --configuration Release --no-restore
  dotnet test --configuration Release --no-build
  pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
  pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
  ```

  `pnpm -C web install --frozen-lockfile` and `pnpm -C pdf install --frozen-lockfile` come first, as stale `node_modules` make tests and build fail. The web app's type check runs in `pnpm -C web build`, and `pnpm -C web test --run` doesn't give it. The sidecar's runs in `pnpm -C pdf test`.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- **Lessons from the last run.** `PasswordWindowTests` covers every session endpoint and sits in no narrow test filter, so full suites at the phase ends really matter. Exact lists of menu items, help texts and metrics have tests that break on additions: here especially `HelpTests`, `LabelGuardTests`, and `DashboardTests.Every_metric_the_server_defines_has_a_panel`, so a new metric needs its dashboard panel in the same task.
- **Style:** British English everywhere, in code, comments, docs, sentences and commit messages (colour, behaviour, initialise, licence as the noun). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`, where their siblings do; new Python ones with the same after `#`.
- `web/src/api/schema.d.ts`: no REST shape changes, so it shouldn't change. If it must, edit it by hand exactly as generated output would have it.
- **The repository names no organisation, customer or employer**: code, docs, test data, tests, examples, generated files. Use neutral words such as `Work`, `Papers`, Alice, Bob.
- **Update `docs/security-architecture.md`** where a task changes what it claims. It cites file and line, so please keep those true where you touch them.
- **Uncertainty is welcome.** When spec and plan leave something open, decide it, say so in a commit message, and list it in the report. If the spec seems wrong, build the safer reading, write down why, and carry on. The plan already lists 16 decisions it made where the spec was silent or couldn't hold; overrule one if you see better, and say so. The plan's line numbers are from `836354d` and drift as you work; the sense counts.

## A few things that matter to us

- **Please do not merge, tag, release or deploy.** No version change (`version.txt`, `src/Kumbu.Server/Kumbu.Server.csproj`, `web/package.json`), no push to `master`, and leave `gitops-*`/`argocd-*` and devices alone. The pull request is a **draft**. A rollout is something Chris does together with a person.
- No secrets in logs, assertions, test data or the report. The hands-on's dev token is random and stays in the shell.
- Specs outside this topic stay as they are; if one looks wrong, please mention it in the report.
- **Security boundary, from spec §5: nothing moves.** The rule still reads: **untrusted PDFs are never parsed, and untrusted images never decoded, in the process that holds vault keys.**
  - **Touched, and to be held as they are:** the PDF sidecar's parsing (`pdf/src/`: new `omggif`, new `/text-boxes`) and the server's client for it (`src/Kumbu.Server/Attachments/PdfSidecar.cs`, `AttachmentPixels.cs`). Please read `docs/security-architecture.md` §7, "The PDF sidecar", before changing them.
  - **Same reading paths:** all new reads go through `IAttachmentPixels`, with the request's credential (`store.VaultOfAsync`, `catalog.ReadBlobAsync`). `rg` finds its PDFs through `StoreAttachments.UnderAsync` as before. No new REST endpoint, and no new route to the sidecar beyond its internal network.
  - **Bytes stay sealed:** opened per request, sent over the compose network, forgotten. `--rotate`, `--frame` and `--regions` store nothing at all: neither pixels nor boxes nor frame counts. Only Task 14, if it ships, adds one box per region inside the sealed part 0.
  - **GIFs stay in the sidecar:** `omggif` runs in a worker under the existing heap, time and container limits, the composing path is capped at 16 MP, which Task 13 measures under `--memory=1536m` and lowers if it doesn't hold, and a frame's rectangle is checked against the canvas **before** it is decoded, as it would otherwise allocate up to 65,535 × 65,535 from a few bytes of file. The server never parses a GIF.
  - **What comes back is checked:** the `rotate` and `frame` echoes, the existing header and size checks on pictures, the bounds on the text-box answer. **Parameters are whitelisted on both sides:** `rotate` from a set, `frame` and `pages` as whole numbers; nothing a person typed reaches a query unparsed.
  - **Text from a PDF stays data:** the region goes before the text, so a line can't forge one. **Metrics hold no names:** enums and flags only, which `LabelGuardTests` guards.
  - **Not touched, on purpose:** pins, slots and all cryptography, re-keying and its transactions, `_loading`, sessions, OAuth, the sharing and membership code, and the sidecar's hardening in `compose.yaml`. No new cryptographic code. These hard rules still hold, each with its test class: `MembershipCheckTests`, `BoundMembershipWriteTests`, `PinTests`, `NoNamesTests`, `MarkerTests`.
  - **The accepted limit stays:** a compromised sidecar sees the files sent to it. `--regions` sends files that a plain search wouldn't, on the caller's explicit request; one sentence for `docs/security-architecture.md`, in Task 12.
- **At the end,** in this order (the plan's final task): full verification; push; the draft pull request; the report and the row with the PR link in `docs/superpowers/open-items.md`; commit; push. No STATUS file here.

## Out of scope

- Everything in the spec's non-goals: marking text that isn't visible, naming what a region shows, image recognition or OCR on the server, angles other than 90°, 180°, 270°, remembering a turn, a turn or a frame in the web app's lightbox, several frames in one call, animated WebP or PNG, regions in `semsearch` or for notes, a cache of pixels.
- The Obsidian plugin (a separate repository).
- The client round of the playbook's new steps, and the maintainer's private PDFs: a person does those on the result. Your hands-on note leaves the checklist.
- Versions, tags, images, deployment.

## The report

First push, and open a **draft** pull request from `feature/attachment-wishes` to `master`, ending with the line above and stating the verdict below. **Do not merge it.** If no pull request can be opened, use the compare URL `https://github.com/SCAI-NGO/kumbukumbu/compare/master...feature/attachment-wishes` as its link.

Then the last commit, which you also push: `docs/superpowers/overnighter/2026-11-02-attachment-wishes-report.md`, and the row with the PR link in `docs/superpowers/open-items.md`. The report holds:

- the environment, and what you installed;
- Task 0 planned against measured;
- per task the commit and the real test output;
- every mutation individually;
- deviations and the calls you made;
- surprises;
- **the verdict on regions with labels, in one sentence,** with the numbers of both held-out rounds if there were two;
- the measured memory peak of the composing path (Task 13), or "memory not measured";
- open points for Chris, with **what he must check by hand** first;
- the PR link.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the PR is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. `--rotate`, `--frame` and `rg --regions`, green and honestly reported, are already a good night's work; the labels are a bonus, and only with an honest number.

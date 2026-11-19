# Overnighter brief: help texts that say what the code does

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. Agents learn it from `sh help`, the MCP tool descriptions and the error texts, and from nothing else. A read-only audit compared every one of those texts with the code and found two small bugs, about a dozen misleading texts and about twenty-five gaps. This run smooths all of them out. Chris calls it a "Behübschungsrunde": a careful evening of fine work, and worth a lot because every agent reads these texts on every session.

Nobody is available to answer questions during this run. That is intended: Chris has reviewed and approved the spec, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open. A change that is right and proven counts for more than many changes.

## What to read, in this order

1. This brief.
2. **The spec:** `docs/superpowers/specs/2026-11-19-help-texts-design.md`. Chris approved it on 2026-11-19; please don't edit it. Every item there names its file and line on `master` at `63d3ca46`; lines may have moved a little, so find the text rather than trusting the number.
3. Where the texts live:
   - `src/Kumbu.Core/Sh/Commands/*.cs`: each command's `Usage`, `Summary` and `Help`;
   - `src/Kumbu.Core/Sh/Commands/Help.cs`: the command list and the topics `checkpoints`, `attachments` and `archive`;
   - `src/Kumbu.Core/Sh/Manual.cs`: the MCP tools' descriptions (`ToolDescription`, `WriteDescription`, `EditDescription`, `ReplaceDescription`, `BoardDescription`, `UploadDescription`);
   - `src/Kumbu.Server/Mcp/*.cs`: the tools' parameter descriptions;
   - `web/src/console/help.ts`: the web console's own words.
4. The tests that already hold claims of the help: `tests/Kumbu.Core.Tests/Sh/HelpTests.cs`. Follow their shape.

**Where the repository's `CLAUDE.md` differs from this brief, this brief wins.** In particular: don't follow the `project-memory` skill, and don't read or write `/SWE` or any vault but `Factory`; write nothing to staging; the changelog entry goes only into the report. CLAUDE.md's rule to list every root cause before fixing a bug is met for §1 and §2 by the spec, which establishes the cause; name it in the red test's commit message.

## Where you run

- **Branch `feature/help-texts`.** The session starts on the default branch, so first run `git fetch origin feature/help-texts && git checkout feature/help-texts`. **Push only to this branch.** Everything you push to it survives; everything else does not.
- **Installing:** the environment may lack tools. Installing them is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new NuGet or npm package.
- **Task 0** (setup plus the first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- **Tools:**
  - The .NET 10 SDK as `global.json` says (roll forward), with `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
  - Node 26, which earlier runs installed by hand (Node 26.13.1 in `/opt/node26` from the nodejs.org tarball), and pnpm 10.32.1 (`npm install -g pnpm@10.32.1`).
  - After any checkout, run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and the build fail.
- **Docker** is available and allowed: the server tests start MongoDB in a container. Earlier runs found `dockerd` not running and started it (`dockerd &`); if the worker restarts, start it again. If Docker can't run, start a local `mongod --replSet rs0` and point `KUMBU_TEST_MONGO` at it, as the repository's notes describe; the tests drop their databases when done.
- **Never test against a real host**: not staging, not production.
- If superpowers skills aren't available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## The Factory board

This run reports its progress on a Kanban board in Kumbu², through the connector `Kumbu_Factory`. It reaches the vault `Factory` only.

1. Read the board's rules first: `sh board /Factory`.
2. Add your card: vault `Factory`, title "Help texts", column `Running`. The card gives the repository and branch, this brief's path, the trigger (in your first message's system note), the session's link, a `## Steps` checklist and a `## Log`.
3. Steps: "Task 0", "Bugs (§1, §2)", "Misleading texts (§3)", "Small gaps (§4)", "Final gate", "Report and pull request". Tick them as they finish, and add a log line at least every hour.
4. At the end, move the card to Done with the pull request's link.

If the connector isn't there, say so in the report and carry on without the board.

## Open points for the run

All of these are yours to **decide**: settle each with the rules below and log it in the report.

1. **The order:** §1 and §2 first (behaviour, test-first), then §3.9 and §3.10 (the two small behaviour fixes), then the rest of §3, then §4. Text changes in the same file may be grouped into one task; keep a task to one coherent area so that its review stays readable.
2. **§2 in detail.**
   - `NoteWriter.NotText` is `public static` (`NoteWriter.cs:603`) with four callers (`NoteWriter.cs:138,189,324`, `Replacer.cs:32`). It gets a `skillsVault` parameter; every caller passes `await store.SkillsVaultAsync(ct)` (`Replacer` asks its store too).
   - `INoteReader.SkillsVaultAsync` defaults to null (`INoteReader.cs:85`), and so does the test fake's `InMemoryNoteStore.SkillsVault` (`tests/Kumbu.Core.Tests/Fakes/InMemoryNoteStore.cs:78`). An existing test that writes into `/Skills/...` and relied on the name gets `SkillsVault = "Skills"` set on its fake, as `TextAttachmentTests.cs:18` already does; never a looser assertion.
   - Seeding §2's `edit` test: today `write` refuses the file in a vault marked under another name (that is the bug), so put it in through `writer.ForAttachments()!.WriteAsync(...)`, which already uses the mark. Each of the three tests must go red against today's code for the reason §2 names.
   - **The other callers of `ForPath`:** the spec's list is not complete. Find every caller with `rg -n 'ForPath\(' src` (also e.g. `MarketplaceIndex.cs:275`, `Publications.cs:909`, `Bundles.cs:185`). `AttachmentFormat.KindOf` (`AttachmentFormat.cs:153`) fans out to many API endpoints (`NoteApi.cs`, `RecentApi.cs`, `HistoryApi.cs`, `Activity.cs`): unless the mark is already in scope at each one, leave it as it is and add a row to `docs/superpowers/open-items.md`. The same for any caller that would need more than passing the skills vault it can already reach (a new dependency through several layers, a change of an endpoint's contract): leave it, explain why in the report, add a row.
3. **§3.9: telling an empty folder from a missing path.** Today `LinksCommand.cs:64` takes the folder form only when `ListNotesUnderAsync(path)` returns notes. A folder exists when its vault does (`ListVaultsAsync`) and the path is the vault itself, or has attachments or a folder marker below it (`StoreAttachments.PathsUnderAsync`), as `blame` decides at `Blame.cs:58-63`. Such a folder answers exactly `(no broken or ambiguous links below <dir>)` (`LinksCommand.cs:145`). A path that doesn't exist keeps "No such file or directory"; `--version` on such a folder keeps its current error.
4. **§1's result line.** Append it to the `Output` of the successful result in `NoteWriter.WriteAsync`'s SVG and text-file branch (`NoteWriter.cs:128-136`), as `\nmessage not kept: an SVG's or a skill file's history keeps no messages`, only when a message was given (after `TryMessage`) and the write did not fail. That is how `write`'s other hints reach the caller (`NoteWriter.cs:159-160`, `{LinkHintsAsync}{shared}`). Don't use `ShellResult.Notice` (only `Shell` shows it; the MCP tool's `ToolResult.From` never does) or `src/Kumbu.Server/Mcp/Notices.cs` (one-time notices to members of shared vaults).
   The spec's example `Created /Work/diagram.svg (version …)` stands for the existing answer, which is `created /Work/diagram.svg` (or `wrote …`, `AttachmentWriter.cs:153,167`) with the version as its own block (`ToolResult.cs:28`). Don't change that answer.
5. **Numbers from constants (spec, principle 2).** In the commands' `Help`, `Usage` and the topics, numbers come from their constants; where a help is a plain `"""…"""` today, making it interpolated to take a constant is fine. `Manual.cs` is the exception: its descriptions are `public const string`s that feed `[Description(...)]` attributes, and a const interpolated string can't hold an `int` or a `TimeSpan`. There, keep the number as written and add a test asserting the description contains the constant's value as text.
6. **§4 and the tool description.** §4 changes a command's `Usage` and `Help` only; the `help` list is built from `Usage`, so it follows by itself. The one-line-per-command list in `Manual.ToolDescription` (`Manual.cs:8-50`) changes only where §3 names `Manual.cs`, or where a line there says something false. Don't add §4's options to it: every agent pays for its length on every session. `HelpTests.cs:51-63` and `:193` pin lines of it; keep them true.
7. **§3.5, pdfimages' 50 seconds: the spec's wording is wrong; follow the code.** `PdfImages.cs:25-26,343-351`: after `Ceiling` storing stops even within a page and reports that page as partly stored. The help says: "storing stops at the first page boundary after 20 seconds; after 50 it stops even within a page, and names that page as partly stored". Mention this in the report as a correction of the spec.
8. **§3.12's test.** The command-level list comes from `web/src/console/console-fixture.json`'s `refused`, which `tests/Kumbu.Server.Tests/Api/ConsoleFixtureTests.cs:56` keeps equal to `ConsoleApi.Refused`. Only the subcommand refusals (publish other than `ls`, the calendar write verbs, `skills copy`; `ConsoleApi.cs:83-100`) are copied into the Vitest, with a comment pointing at `ConsoleApi.cs`.
9. **Existing tests that assert the old wording:** update them to the new wording, never loosen one. A test that pins a claim the spec corrects is updated to the corrected claim.
10. **If time runs short:** stop after the last green gate. The priority is:
   1. the report and the pull request, with every section's state;
   2. §1 and §2;
   3. §3;
   4. §4.

   Whatever is left goes into the report and into `docs/superpowers/open-items.md`.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-19-help-texts.md`.
2. It starts with **Task 0: baseline**: run the full suites before any change and record the exact counts and the exact failures. Known state on `master` at `63d3ca46`, measured on Chris' laptop on 2026-11-19: 
   - Server (`dotnet test --solution Kumbu.slnx`): 7,780 tests, 7,696 passed, 83 skipped, **1 failed** in `Kumbu.Server.Tests` (23 min 46 s; `Kumbu.Core.Tests` all passed). The failing test's name wasn't captured; Task 0 finds out which one and whether it fails in the cloud too. The skip count is high locally because the embedding model, the TensorX key and the tokenizer are missing there; expect fewer skips with the cloud's setup.
   - Web (`pnpm -C web test --run`): 205 files, 4,111 tests, all passed. `pnpm -C web build` passed (a chunk-size warning is expected).
   CI was green on `master` for #260. Known flaky under load (`docs/superpowers/open-items.md`, 2026-11-07): `AttachmentPixelsTests.A_stored_analysis_and_its_text_are_read_without_the_sidecar`, `MembershipApiTests.Last_use_is_the_use_on_this_vault_not_the_keys_global_one`, `PairingTests.A_device_name_gets_the_same_answer_for_an_open_and_an_unknown_code`; on the web side `web/src/note/commentRanges.test.tsx` and the zip tests of `web/src/skillpage/SkillPage.test.tsx`. If one fails, run it alone three times (e.g. `dotnet test --project tests/Kumbu.Server.Tests -- --filter-method "*A_stored_analysis_and_its_text_are_read_without_the_sidecar"`), record the result, and carry on. The token-bound tests skip locally without `KUMBU_TOKENIZER`, so the skip count can differ between machines; earlier runs measured 6 skipped.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in its findings, then commit the plan.

## How to work

- **Subagent-driven** (`superpowers:subagent-driven-development`):
  1. A fresh subagent per task: red test, green, commit locally, then every mutation on its own (break, watch it fail, restore with `git checkout -- <file>`). For a pure rewording without a test, the mutation step is "none: wording only", said in the report.
  2. Then a spec-compliance and code-quality review by a fresh subagent. For text tasks, the reviewer reads each changed text against the code it describes, not only against the spec: the point of this round is that the text is true.
- Subagents never merge, push or switch branches. You commit and push, after every task.
- **Commit messages** are imperative and free-form, with no Conventional Commits prefix, saying what changes for the agent or user ("Say in help that graph and calendar hide the archive too"), and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- Stage files by name, never `git add -A`, `git add .` or `commit -a`.
- **Test commands.**
  - One test class: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"` (server classes: `--project tests/Kumbu.Server.Tests`). One test: `-- --filter-method "*Test_name"`. The runner is Microsoft.Testing.Platform (`global.json`) with xunit.v3: `--filter "FullyQualifiedName~…"` doesn't work, a glob in mid-name selects nothing, and "Zero tests ran" counts as a failure, not a pass.
  - The server gate: `dotnet test --solution Kumbu.slnx` (4 tests skip without the embedding model and a TensorX key; that is expected).
  - One web file: `pnpm -C web test --run src/console/help.test.ts` (paths relative to `web/`).
  - The web gate: `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build` (the build runs the type check).
  - Only these commands count as evidence.
- **The final gate**, at the end of each phase and at the end: `dotnet restore --locked-mode`, `dotnet build --configuration Release --no-restore`, then the server gate and the web gate. The locked restore is CI's first step and catches a dependency that slipped in. CI runs on the pull request (`.github/workflows/ci.yml`, including the PDF sidecar and WebDAV's `litmus`); check it with `gh pr checks` and rerun a flaky job once, saying so.
- **Style:**
  - British English in code, comments, docs, commits and every user-visible text (colour, behaviour, initialise, licence as the noun).
  - The help texts' own style: short sentences, plain words, examples in the shape the command's help already uses, no marketing. A help gets longer only by what it was missing. Keep the indentation and line width of the surrounding help (lines up to about 120 characters).
  - Match the surrounding code: its comment density, naming and idiom. Comments only where the reasoning isn't obvious. New source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **Names:** no customer or employer anywhere in the repository; use Alice, Bob, Dana, `Home` and `Work`. The one organisation it may name, where needed, is its owner: Second Circuit – Association for digital freedom of thought.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working, apart from what the spec changes. Prefer the reversible change.
2. **True to the spirit of the application.** Honest: a text says what the code does, no more and no less; nothing is promised that isn't there, nothing is hidden. Plain: an agent understands it on first reading. Follow the existing patterns, style and architecture; no new dependencies.
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling.
4. **True to the spec.**

If you find another divergence between a text and the code that the spec doesn't list, fix it if it is clearly of the same kind (a text that says less or something else than the code), and list it in the report under "Found on the way". If fixing it would change behaviour, leave it and record it in `docs/superpowers/open-items.md`.

If a question can only be answered by changing the spec's scope, don't guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the pull request's description under **Decisions made without Chris**, with the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version.
- No secrets in logs, assertions or the report.
- Specs outside this topic stay as they are; if one looks wrong, mention it in the report (the spec already names one: the CSV/HTML/XML spec's "a call stops after 10 seconds").
- **Security boundary:** spec, "Security boundary". §2 changes how `write` and `edit` decide a skill's text file; it must not let a write create what upload or the skill rules refuse, and its tests cover both directions. `src/Kumbu.Server/Auth`, `src/Kumbu.Server/OAuth`, `src/Kumbu.Server/Sharing` and `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` are **not touched**.
- **At the end:**
  - a row with the pull request's link in `docs/superpowers/open-items.md`;
  - in the report, a proposed entry for the version's changelog, in the shape of `CHANGELOG.md` (a bold lead-in and what it means for the people who use or run Kumbu², under Better or Fixed). Chris adds it to his notes; you don't need to reach them;
  - in the report, a short hand-check list: a few `sh` lines Chris can run in the web console or through an agent on staging, with what each should print.

## Out of scope

- Spec, "Out of scope", in full; in particular a message on attachment versions, which Chris looks at for 0.7.0.
- New options or commands, and behaviour changes beyond spec §1, §2, §3.9 and §3.10.
- The open Dependabot pull requests and all dependency upgrades.
- Everything else in `docs/superpowers/open-items.md`.

## The report

Last commit: `docs/superpowers/overnighter/2026-11-19-help-texts-report.md`. It contains:
- the environment;
- the plan review's findings and what you did with them;
- Task 0, planned against measured;
- for each task the commit and the real test output;
- every mutation on its own;
- §2's verdict for each caller of `ForPath`;
- "Found on the way": divergences the spec didn't list, fixed or recorded;
- the decisions made without Chris;
- deviations, surprises and open points for Chris;
- the proposed changelog entry and the hand-check list;
- the pull request's link.

Then open a pull request from `feature/help-texts` to `master`, titled "Make the help, the tool descriptions and the console say what the code does", and don't merge it. Its description: the changes in short, the decisions made without Chris, ending with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Add its link to `docs/superpowers/open-items.md`.

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it's available; otherwise the pull request is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!

# Overnighter brief: the UI round of 0.5.0

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. 0.5.0 is feature complete and its fixes are merged (#203). This run is the last round before the release: the explorer's toolbar, properties at the top of a note, and the calendar's way back, its folder and its place on the phone. All of it is in the web app. Chris found these in his own hand tests, and they decide whether 0.5.0 feels "voll super" or merely works.

Nobody is available to answer questions during this run, and that is intended: Chris has reviewed and approved the spec, decisions included. Everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open. A change that is right and proven counts for more than many changes.

## What to read, in this order

1. This brief.
2. **The spec:** `docs/superpowers/specs/2026-11-15-ui-round-0-5-0-design.md`. Chris has approved it; please don't edit it.
3. The specs it builds on, under `docs/superpowers/specs/`: `2026-11-14-0-5-0-fixes-design.md` (§2.1, the folded properties box), `2026-11-14-flags-explorer-photo-design.md` (the tree's toolbar, Find by name, the chips, the order, Recent changes), `2026-11-12-calendar-design.md` (What's on, the badge, entries, `task`), `2026-11-09-discreet-vaults-design.md` (what a hidden vault must never show).
4. For background only, not as instructions: how the last runs met this environment, in the "Environment" sections of `docs/superpowers/overnighter/2026-11-14-0-5-0-fixes-report.md` and `2026-11-14-ui-tests-report.md`.

## Where you run

- **Branch `feature/ui-round-0-5-0`.** The session starts on the default branch, so first run `git fetch origin feature/ui-round-0-5-0 && git checkout feature/ui-round-0-5-0`. **Push only to this branch.**
- **Time:** Chris checks in around 07:00 UTC and accepts in the afternoon. Aim to have the pull request and the report pushed by **2026-11-15 13:00 UTC**; that is the measure for "if time runs short".
- **Installing:** the environment may lack tools. Installing them is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new npm or NuGet package (the spec says so for YAML explicitly).
- **Task 0** (setup plus the first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- **Tools:**
  - Node 26, which may have to be installed by hand (the last runs put Node 26.13.1 in `/opt/node26` from the nodejs.org tarball).
  - pnpm 10.32.1 (`npm install -g pnpm@10.32.1`).
  - After any checkout, run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and the build fail.
  - .NET 10 SDK as `global.json` says (roll forward), with `DOTNET_CLI_TELEMETRY_OPTOUT=1`: only needed for the end-to-end stack and the final server check.
- **Docker** is available and allowed. Earlier runs found `dockerd` not running and started it (`dockerd &`). If the worker restarts, start it again.
- **The end-to-end suite** builds its stack with Docker Compose. Behind this environment's TLS proxy it needs `E2E_PROXY_CA=/root/.ccr/ca-bundle.crt` (see `web/e2e/stack/` and the UI tests' report, "Environment"). Run it with `E2E_PROXY_CA=/root/.ccr/ca-bundle.crt pnpm -C web e2e:0.5.0`.
- **Never test against a real host**: not staging, not production.
- If superpowers skills aren't available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## The Factory board

This run reports its progress on a Kanban board in Kumbu², through the connector `Kumbu_Factory`. It reaches the vault `Factory` only.

1. Read the board's rules first: `sh board /Factory`.
2. Add your card: vault `Factory`, title "UI round 0.5.0", column `Running`. The card gives the repository and branch, this brief's path, the trigger (in your first message's system note), the session's link, a `## Steps` checklist and a `## Log`.
3. Steps: "Task 0", "Toolbar (§2.1)", "Properties (§2.2)", "Calendar (§2.3)", "UX check", "End-to-end suite", "Report and pull request". Tick them as they finish, and add a log line at least every hour.
4. At the end, move the card to Done with the pull request's link.

If the connector isn't there, say so in the report and carry on without the board.

## Open points for the run

All of these are yours to **decide**: settle each with the rules below and log it in the report.

1. **The order:** §2.3 (the calendar: four small changes) first, then §2.1 (the toolbar), then §2.2 (properties, the largest). They may be built in parallel in separate worktrees and cherry-picked onto the branch one at a time; §2.1 and §2.3.2 both touch `TreeDrawer.tsx`, so not those two at once.
2. **The frontmatter text in the client (§2.2).** Byte preservation is new on the client side. Build the move and the add as pure functions over the note's text (input text in, output text or a refusal out), with table tests over the edge cases the spec lists, CRLF line endings, a frontmatter without a final newline, an empty frontmatter, and a body that starts with a blank line. Save through `useSaveNote` as the editor does.
3. **The web types:** follow `web/src/calendar/api.ts`. **Don't regenerate `web/src/api/schema.d.ts`.**
4. **Existing tests the changes alter** (the order radiogroup, the vault menu's place, the badge on the file tree's button, the Recent changes link): update them to the new behaviour; never loosen one. The end-to-end tests that look for these too.
5. **If time runs short:** stop after the last green gate. The priority is:
   1. the report and the pull request, with every section's state;
   2. §2.3;
   3. §2.1;
   4. §2.2's hint and Move to properties;
   5. §2.2's Add property.

   Whatever is left goes into the report and into `docs/superpowers/open-items.md`.
6. **A conflict outside the editor (§2.2).** On a 409, `useSaveNote` returns `{conflict}` without throwing (`web/src/api/notes.ts` around line 319), and today's conflict handling lives in the editor. For Move to properties and Add property, suggested: on a conflict write nothing, say "This note changed elsewhere; try again", and refresh the note.
7. **Frontmatter without a box (§2.2).** `Properties` returns null for an empty frontmatter (`NoteView.tsx` around line 524) and shows a `<pre>` when `parseProperties` fails. Suggested: offer ⋯ **Add property…** whenever no box with keys is shown; refuse Add and Move when the existing frontmatter can't be read, and say why.
8. **The chevron as its own button (§2.3.2).** Today `FolderButton` (`TreeDrawer.tsx` around line 185) is one button with chevron, name and count. Suggested: for `Calendar/` and `Board/`, a 48 px chevron target reaching into the leading padding, so the rows stay aligned with the others.
9. **Chips when no note carries a flag (§2.1).** Suggested: hide the chip group (including "Any flag"), except a pressed chip, which stays shown as the spec says.
10. **Navigation state already in use (§2.3.1).** `NoteView` reads `location.state` for jumps to a block, heading or line (`NoteView.tsx` around line 280). Merge the back link's origin into that state, never replace it. Entry points that should pass it: `EntryItem.tsx` (around 64), `CalendarPage.tsx` (around 98), `CouldntRead.tsx` (around 48), and What's on's.
11. **Wording that follows (§2.1).** The empty tree's "Make the first with the menu above" should point to + New note; the vault menu's item is "New note here", which the toolbar's + New note matches in what it does.
12. **Search mode (§2.1).** Today's search row also holds Search the contents…, the order switch and Recent changes. Suggested: in search mode, Sort and Recent changes drop out; Search the contents… stays.
13. **The e2e tag for §2.2.** `e2e:0.5.0` has no `@properties` tag (`web/e2e/properties` rides on `@calendar`/`@flags`). Add `@properties` to the script, or use an existing tag; say which in the plan.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-15-ui-round-0-5-0.md`.
2. It starts with **Task 0: baseline**. Run the web gate and the end-to-end suite on the branch before any change, and record the exact counts and failures. CI was green on `master` at `6e787a21` (#203). Known flaky under load: `web/src/note/commentRanges.test.tsx` and the zip tests of `web/src/skillpage/SkillPage.test.tsx`; if one fails, run its file alone three times, record the result, and carry on.
3. Every task stands alone: exact file paths with line anchors, and the exact command for each test step with the expected pass or fail. Then a red test, green, and every mutation on its own. No placeholders, no "see above".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in its findings, then commit the plan.

## How to work

- **Subagent-driven** (`superpowers:subagent-driven-development`):
  1. A fresh subagent per task: red test, green, commit locally, then every mutation on its own (break, watch it fail, restore with `git checkout -- <file>`).
  2. Then a spec-compliance and code-quality review by a fresh subagent.
- Subagents never merge, push or switch branches. You commit and push, after every task.
- **Commit messages** are imperative and free-form, with no Conventional Commits prefix, saying what the app now does ("Put the vault's menu in the tree's toolbar"), and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- Stage files by name, never `git add -A`, `git add .` or `commit -a`.
- **Byte preservation:** every frontmatter write keeps every other byte.
- **48 px:** every new target is at least 48 × 48 px.
- **A UX check** by a fresh subagent with Playwright, before the final gate.
  - It runs against the e2e stack, never a real host.
  - It covers spec §2.1 to §2.3 and the example of §3 at 1280 px and at 375 px with touch, in a light and a dark scheme.
  - It reports every place where the screen speaks of our fields instead of the person's wish, where the spec's wording isn't used, where a target is under 48 px, or where something is cut off or scrolls sideways.
  - Fix what it confirms; record the rest.
- **Test commands.**
  - One web file: `pnpm -C web test --run <file>`.
  - The web gate: `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build` (the build runs the type check). A web gate counts only if no agent was editing web files meanwhile.
  - The end-to-end suite: `E2E_PROXY_CA=/root/.ccr/ca-bundle.crt pnpm -C web e2e:0.5.0`. New tests carry a tag that `e2e:0.5.0` already selects (e.g. `@explorer`, `@calendar`), or a new tag added to that script.
- **The final gate**, at the end of each phase and at the end:
  - the web gate;
  - `dotnet build --configuration Release` once, to show the server still builds (nothing in `src/` or `tests/` should have changed: check with `git diff origin/master --stat -- src tests`);
  - at the very end, the end-to-end suite twice from a fresh stack; both runs must be identical.
  - CI runs the server suites, the PDF sidecar and WebDAV's `litmus` on the pull request; check them with `gh pr checks` and rerun a flaky job once.
- **Style:** British English in code, comments, docs, commits and user-visible text, and the UI is English. Match the surrounding code, its comment density, naming and idiom: comments only where the reasoning isn't obvious. New TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **Names:** no customer or employer anywhere in the repository; use Alice, Bob, Dana, `Home` and `Work`. The one organisation it may name, where needed, is its owner: Second Circuit – Association for digital freedom of thought.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working, apart from what the spec changes. Prefer the reversible change.
2. **True to the spirit of the application.**
   - Campy: plain notes and frontmatter, no new stores.
   - Human-centric: the screen speaks of the person's wish.
   - Honest: a button does what it says, and nothing is moved or hidden quietly. Chris: honesty is part of the security architecture.
3. **Opens no new security hole.**
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, don't guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the pull request's description under **Decisions made without Chris**, with the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version.
- No secrets in logs, assertions or the report.
- Specs outside this topic stay as they are; if one looks wrong, mention it in the report.
- **Security boundary:** spec §6. The server (`src/`, `tests/`) is **not touched**; every write goes through the existing writes with the person's rights. Nothing may name a hidden discreet vault.
- **At the end:** a row with the pull request's link in `docs/superpowers/open-items.md`. Write a hand-test checklist into the report: numbered steps saying where to look and what passes, for §2.1 to §2.3, runnable by Chris on staging on the laptop and the phone. Don't write dates into it that depend on the day of the run; say "today".

## Out of scope

- Spec §7, in full.
- The open Dependabot pull requests and all dependency upgrades.
- Everything else in `docs/superpowers/open-items.md`.

## The report

Last commit: `docs/superpowers/overnighter/2026-11-15-ui-round-0-5-0-report.md`. It contains:
- the environment;
- the plan review's findings and what you did with them;
- Task 0, planned against measured;
- for each task the commit and the real test output;
- every mutation on its own;
- the UX check;
- both end-to-end runs;
- the decisions made without Chris;
- deviations, surprises and open points for Chris;
- the hand-test checklist;
- the pull request's link.

Then open a pull request from `feature/ui-round-0-5-0` to `master`, titled "Give the explorer a toolbar, properties their place, and the calendar its way back", and don't merge it. Its description: the changes, the decisions made without Chris, ending with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Add its link to `docs/superpowers/open-items.md`.

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it's available; otherwise the pull request is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!

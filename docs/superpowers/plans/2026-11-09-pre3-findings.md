# The findings of the pre.3 tests: plan

Spec: `docs/superpowers/specs/2026-11-09-pre3-findings-design.md`. Brief: `docs/superpowers/overnighter/2026-11-09-pre3-findings-brief.md`. Branch `fix/pre3-findings`, based on `master` at `6a7a4c7`.

Order by value, each task its own commit, a gate after each (`pnpm -C web test --run` and `pnpm -C web build`, both green):

0. Baseline
1. §4 F3: a new user starts in Welcome, not in Skills
2. §2 F1: More vaults… points to where the PIN is set
3. §5 F4: Discreet / Not discreet in the vault's own menu
4. §3 F2: the Welcome notes explain discreet vaults
5. §7 F6: the README says where hiding leads
6. §6 F5: no request for the old name after a note is named
7. Final gate, pull request, report (do not merge)

Conventions for every task:

- Before the first test of a session, and after any checkout: `pnpm -C web install --frozen-lockfile`. Node 26 is under `/opt/node26/bin` (put it first on `PATH`).
- One web test file: `pnpm -C web test --run <path relative to web/>`; all: `pnpm -C web test --run`; type check and build: `pnpm -C web build`.
- British English; menu labels and texts exactly as the spec writes them; comments only where the reasoning isn't obvious, in the voice of the surrounding code; no new npm or NuGet package; no organisation names (use Alice, Bob, Dana, `Home`, `Tagebuch`).
- Outside discreet mode nothing may differ between users with and without a PIN or discreet vaults.
- Mutations: after the green commit, break the code one way at a time, run the task's test file and see it fail, then restore with `git checkout -- <file>` and see it pass again. Record each mutation and its failing test.
- Subagents never merge, push or switch branches; the coordinator commits and pushes after each task.

## Task 0: baseline

1. `docker rm -fv kumbu-test-mongo; docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all`, then `docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate()'`.
2. `DOTNET_CLI_TELEMETRY_OPTOUT=1 TESTCONTAINERS_RYUK_DISABLED=true KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true" dotnet test --solution Kumbu.slnx`. Expected: 5524 tests, all passing, 4 skipped (2 more may skip as root).
3. `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`. Expected: about 120 files and 2170 tests, all passing; build green.
4. Any failure of `AccountApiTests`, `RekeyTransitionTests`, `PairingTests` or a sync-hub test: run that class alone three times (`dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`) and record whether it is flaky. Record exact counts and failures for the report.

## Task 1: §4 F3, a new user starts in Welcome, not in Skills

Files: `web/src/shell/MobileShell.tsx` (the effect at `:236-238`, `if (!activeVault && vaults.data?.length) setActiveVault(vaults.data[0].name);`), `web/src/shell/MobileShell.test.tsx`.

1. **Red.** In `web/src/shell/MobileShell.test.tsx`, following how the file already renders the shell with a mocked `/api/vaults` and a reset editor store, add three tests in a `describe("the first vault", …)`:
   - vaults `[{ name: "Skills", kind: "skills", … }, { name: "Welcome", … }]` and no active vault: `useEditor.getState().activeVault` becomes `"Welcome"`;
   - only `[{ name: "Skills", kind: "skills" }]`: it becomes `"Skills"`;
   - `activeVault` already `"Skills"` with both listed: it stays `"Skills"` (a vault the user chose stays chosen).
   Setup: `activeVault` isn't persisted (`editor.ts:22`) and `openNote` sets it from the URL (`editor.ts:154`), so render at `/` with no note open, pass the vault list through `signedInApi`'s `extra`, and answer `GET /api/vaults/Welcome/tree` and `/api/vaults/Skills/tree`. To set a chosen vault, `useEditor.getState().setActiveVault("Skills")` before rendering.
   Run `pnpm -C web test --run src/shell/MobileShell.test.tsx`: the first test fails (it picks Skills), the other two pass.
2. **Green.** Replace the effect's body with: the first vault whose `kind !== "skills"` (as `NewKeyDialog.tsx:165` tells them apart), else the first vault; nothing else changes. Same command: all pass.
3. Gate: `pnpm -C web test --run && pnpm -C web build`, green.
4. Commit "Start a new user in Welcome, not in Skills".
5. Mutations, each alone: (a) back to `vaults.data[0]` → the Welcome test fails; (b) drop the fallback to the first vault (Skills alone sets nothing) → the Skills-alone test fails; (c) drop `!activeVault &&` → the chosen-vault test fails.
6. Notes for the report: `web/src/discreet/hideAgain.ts:166`'s fallback can also pick Skills first; it stays ("nothing else changes"). The change applies to any session at `/` whose first listed vault is Skills, not only to new users; within the spec.

## Task 2: §2 F1, More vaults… points to where the PIN is set

Files: `web/src/discreet/PinDialog.tsx` (the hint `<p id={`${id}-hint`}>4 to 6 digits</p>` at `:78-80`, the error at `:81`), `web/src/shell/TreeDrawer.tsx` (`<PinDialog onClose=…>` at `:644`), `web/src/routes/Settings.tsx` (`DiscreetSection` at `:217-250`, `InstallSection`'s hash effect at `:752-757` as the pattern), `web/src/discreet/PinDialog.test.tsx`, `web/src/routes/Settings.test.tsx` (`:94-98` checks the `#install` id only), `web/src/shell/MobileShell.test.tsx`.

1. **Red, the dialog.** In `PinDialog.test.tsx`, first wrap `renderDialog` (`:34-42`) in a `MemoryRouter` (a `<Link>` outside a router throws) and check the existing tests stay green; then:
   - the text "The PIN is set in Settings → Discreet vaults." is shown, and a link named "Settings → Discreet vaults" has `href` `/settings#discreet-vaults`;
   - the line is directly after "4 to 6 digits" and before the error: after a wrong PIN (the server's 400 with "You have no discreet vaults.") the line is still there, unchanged, and the error comes after it in document order;
   - clicking the link calls `onSettings` when given, else `onClose`;
   - the same text with and without a PIN: the dialog asks nothing before the PIN is entered, so the test asserts the line is present on first render and after the 400, which is the same answer for both cases.
   Run `pnpm -C web test --run src/discreet/PinDialog.test.tsx`: the new tests fail.
2. **Red, the section.** In `Settings.test.tsx`: the Discreet vaults section has `id="discreet-vaults"`; and with `Element.prototype.scrollIntoView = vi.fn()` stubbed and restored (as `MarkdownView.test.tsx:268-272` does), `renderApp("/settings#discreet-vaults")` calls it on an element inside the "Discreet vaults" region. Run that file: fails.
2b. **Red, the drawer.** In `MobileShell.test.tsx`: open the drawer, the vault switcher, More vaults…, click "Settings → Discreet vaults": the location is `/settings` with hash `#discreet-vaults`, no PIN dialog and no "File tree" drawer dialog remain. Fails.
3. **Green.**
   - `PinDialog.tsx`: a new optional prop `onSettings?(): void`. Directly below the "4 to 6 digits" paragraph and above `{reveal.isError && …}`, `<p className="text-sm text-muted">The PIN is set in <Link to="/settings#discreet-vaults" onClick={onSettings ?? onClose} className="…">Settings → Discreet vaults</Link>.</p>` (link style as `InstallHint.tsx:70`).
   - `TreeDrawer.tsx:644`: `onSettings={() => { setAskingPin(false); setOverlay("none"); }}`, as the account row's `close` (`:403`) sets the overlay unconditionally.
   - `Settings.tsx`: `<Section title="Discreet vaults" id="discreet-vaults">`, wrapped like `InstallSection` with a ref and an effect scrolling it into view when `hash === "#discreet-vaults"`.
   Run the three files: green.
4. Gate, commit "Point More vaults… to where the PIN is set".
5. Mutations: (a) remove the line → text test fails; (b) `to="/settings"` without the hash → href test fails; (c) drop the `onClick` → onClose test fails; (d) move the line below the error → order test fails; (e) drop the section's `id` → Settings id test fails; (f) drop the scroll effect → scroll test fails; (g) drop `setOverlay("none")` → drawer test fails.

## Task 3: §5 F4, Discreet / Not discreet in the vault's own menu

Files: `web/src/shell/TreeDrawer.tsx` (`vaultItems` at `:518-547`, `active` at `:450`, the error blocks at `:569-583`, `copied` at `:467`), `web/src/shell/useCopied.tsx`, a new test `web/src/shell/TreeDrawerDiscreet.test.tsx` (set up like `TreeDrawerFolders.test.tsx`), `README.md` ("Marking." bullet in "Discreet vaults", around `:382`).

1. **Red.** New `TreeDrawerDiscreet.test.tsx` (SPDX header), rendering the drawer as `TreeDrawerFolders.test.tsx` does, opening the vault's ⋯ menu:
   - not revealed (`useDiscreet` `revealed: false`): no item named "Discreet" or "Not discreet"; the menu's item list equals the one before this change (same labels in the same order) for a vault with and without `discreet` (a guard: it passes before the change too);
   - revealed, active vault unmarked: "Discreet" just before "Delete vault…"; selecting it sends `PUT /api/account/discreet/vaults/<vault>` with `{ discreet: true }` and then shows "Marked as discreet" in the drawer's status line (`aria-live="polite"`);
   - revealed, active vault `discreet: true`: "Not discreet"; selecting it sends `{ discreet: false }` and shows "No longer discreet";
   - revealed, a vault shared with the user (`owner` set, `membership` set): the entry is there too, just before "Leave this vault…";
   - revealed, active vault `kind: "skills"`: no entry;
   - revealed, the PUT answers 500: a `FormError` with the error text is shown under the header;
   - while the PUT is pending the entry is disabled (`MenuItem.disabled`, `Menu.tsx:11`, `:128`): hold the PUT on a deferred promise, reopen the ⋯ menu (it closes on select), expect the item `toBeDisabled()`, then release.
   Run `pnpm -C web test --run src/shell/TreeDrawerDiscreet.test.tsx`: fails.
2. **Green.**
   - `useCopied.tsx`: besides `copy`, return `say(text: string)` that shows any text in the same live region for the same 3 s; `copy` keeps its texts. `useCopied.test.tsx` and `TreeDrawerAttachments.test.tsx` stay green unchanged.
   - `TreeDrawer.tsx`: `const revealed = useRevealed()` is already read at `:552`; move it above `vaultItems`. `const markDiscreet = useMarkDiscreet()` (from `../discreet/api`). An entry, only when `revealed && active && active.kind !== "skills"`, placed just before "Delete vault…" / "Leave this vault…" (after "Search by meaning…"): label `active.discreet ? "Not discreet" : "Discreet"`, icon `active.discreet ? Eye : EyeOff` (lucide; the menu says what selecting does: Eye to show it as ordinary again, EyeOff to hide it), `onSelect` → `const next = !active.discreet; markDiscreet.mutate({ vault: active.name, discreet: next }, { onSuccess: () => copied.say(next ? "Marked as discreet" : "No longer discreet") })`, disabled while `markDiscreet.isPending`. A `{markDiscreet.isError && <FormError>…}` block next to the others.
   - README "Marking.": the vault's ⋯ menu offers **Discreet** / **Not discreet** too, in discreet mode only (beyond the spec's letter; logged as a decision).
   Run the new file, `useCopied.test.tsx`, `TreeDrawerAttachments.test.tsx`: green.
3. Gate, commit "Offer Discreet and Not discreet in the vault menu while they are shown".
4. Mutations: (a) drop `revealed &&` → not-revealed test fails; (b) drop the Skills condition → Skills test fails; (c) always `discreet: true` → Not discreet test fails; (d) drop the `say` → confirmation tests fail; (e) drop the FormError → failure test fails; (f) drop `disabled` → pending test fails.

## Task 4: §3 F2, the Welcome notes explain discreet vaults

Files: `src/Kumbu.Server/Welcome/Notes/Security and privacy.md` (a new `## Discreet vaults` section after `## Keys and connections` and before `## The honest limits`), `tests/Kumbu.Server.Tests/Welcome/WelcomeTextTests.cs` (`The_menu_names_quoted_are_the_shared_labels`, `:149-161`).

1. Read all eight notes first; confirm the place (privacy is where a new user looks; the vault menu is explained in "Working by hand" only in passing).
2. **Red.** Add `"More vaults…"` and `"Settings → **Discreet vaults**"` (bold, as `"Settings → **Install as an app**"`) to the label list; the text uses exactly these strings. No note mentions them today. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*WelcomeTextTests"`: fails on the new labels.
3. **Green.** The section, in the voice of the others, short: what they are for (Kumbu² open at a family table, at work, on the train); the three steps (set the PIN in Settings → Discreet vaults; open More vaults… in the vault menu and enter it; while the amber frame is shown, mark vaults in their ⋯ menu with **Discreet**, or in Settings); one sentence that agents, synced devices, WebDAV and keys still see them, and that the PIN hides, it doesn't encrypt. No vendor names, no "rename … vault", no new wikilink subpath unless the heading exists. Same command: green.
4. Also run `--filter-class "*WelcomeVaultTests"`: green.
5. Gate: the two classes above green; `pnpm -C web test --run && pnpm -C web build` green (web unchanged). Commit "Explain discreet vaults in the Welcome notes".
6. Mutations: (a) remove "More vaults…" from the text → the label test fails; (b) write "Settings → Discreet vaults" without bold → the label test fails; (c) put "Claude" into it → the vendor test fails; (d) drop `[[Start here]]` from the note → the back-link test fails.

## Task 5: §7 F6, the README says where hiding leads

Files: `README.md` (the "Hiding again" bullet, around `:386`), `web/src/discreet/hideAgain.ts` (the comment at `:134` says "ordinary vault"; correct comments only if they say otherwise).

1. Replace "and the app returns to the last ordinary vault" with: the tab goes back to the last ordinary note it showed, or to the overview of the vaults if there is none; an ordinary note on screen stays.
2. Read `hideAgain.ts`'s comments; correct only one that says "the last ordinary vault" or the like.
3. No test (spec §8). Gate: `pnpm -C web test --run src/discreet/hideAgain.test.tsx` green, `pnpm -C web build` green.
4. Commit "Say in the README where hiding the discreet vaults leads". No mutations (no code).

## Task 6: §6 F5, no request for the old name after a note is named

Files: `web/src/api/notes.ts` (`moveNote`, `:304-311`), `web/src/note/MarkdownEditor.test.tsx` (the `gardenApi`/`writeIn`/`read` harness at `:620-713`, extending "names the note on Read…" at `:700`), `web/src/shell/MoveDialog.test.tsx` (its `api()` at `:30-48`).

Cause (to be confirmed first by a red test): `moveNote` removes the `note`, `links`, `backlinks` and `history` queries for `from`, then invalidates every `note` and `publication` query. The URL and the open note move on only afterwards (`moved()` plus `follow`, `nameFromHeading.ts:187-188`; `MoveDialog`'s `onSuccess`), and until React has rendered that, observers of the old path are still mounted (`NoteView key={path}`, `usePublication(path)` in `MobileShell`). A removed query that an observer still watches is built again and fetched (the 404 for the note); `["publication", from]` is only invalidated and so refetched while active (the repeated GETs come from its observers and the rebuilds; a 404 there maps to null and is not retried, `publications.ts:53-58`, `App.tsx:20`).

1. **Red.** Two tests, in harnesses that mount the real observers and record requests (add a `/api/publications/*` handler answering 404 to each; `PublicationBar.tsx:29` asks for it):
   - a note `/<vault>/Untitled.md` open with a heading `# Groceries`, Write mode ends with everything saved, the rename goes through: after it settles (and the URL shows `Groceries.md`), no request of any method whose URL path contains `Untitled.md` was sent after the `POST /api/move` (note, links, backlinks, publication, history; and no autosave PUT that would make the note again);
   - the same for Rename or move (`MoveDialog`) renaming the open note.
   Run the file(s): both fail with the old-path requests recorded.
2. **Green**, in `moveNote` only:
   - invalidate as now, but with a predicate that leaves out the moved keys (`gone(q.queryKey)`) for `note`, `links`, `backlinks`, `history` and `publication`;
   - cancel the moved keys' fetches under way (`cancelQueries` with the same predicate);
   - drop the moved keys (`note`, `links`, `backlinks`, `history`, `publication`) once nothing watches them: those without observers at once, the others when their last observer goes (a `queryClient.getQueryCache().subscribe` listener, only for the queries that had observers at the time of the move, reacting to `observerRemoved` and `removed`, unsubscribed once they are all gone). Deferring the removal by `setTimeout(0)` (as `hideAgain.ts:180`) was considered and rejected: the navigation runs under `v7_startTransition`, so its render has no fixed moment. This keeps the reason they were removed (a new `Untitled.md` later must not show the old one's text) without fetching for the old path in between.
   Run the two files, plus `src/note/nameFromHeading.test.ts`, `src/note/NoteView.test.tsx`: green.
3. Gate, commit "Ask nothing more for a note's old name once it is renamed".
4. Mutations: (a) back to removing the moved keys at once → the heading test fails; (b) invalidate `publication` without the predicate → the publication assertion fails; (c) never drop the moved keys → a test that a new note at the old path doesn't show the old text (add it if none exists: after the rename and unmount, `queryClient.getQueryData(["note", from])` is undefined) fails.
5. If the cause turns out to be elsewhere and deep, leave it and add a row to `docs/superpowers/open-items.md` instead.

## Task 7: final gate, pull request, report

1. Fresh Mongo container (Task 0 step 1), then as CI does: `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build` (with `KUMBU_TEST_MONGO` as in Task 0); `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`. Record exact counts.
2. Push `fix/pre3-findings`. Open the pull request against `master`, titled "The findings of the pre.3 tests": decisions made without Chris, that the security boundary is not touched, ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
3. Last commit: `docs/superpowers/overnighter/2026-11-09-pre3-findings-report.md` (environment; plan review; Task 0 planned vs measured; per task commit and test output; every mutation; decisions; deviations; hand-test checklist; the PR link) and a row with the PR's link in `docs/superpowers/open-items.md`. Push.
4. **Do not merge, tag or deploy; do not change the version.**

## Plan review

A fresh subagent reviewed this plan against the spec, the brief and the code: 14 findings (4 must-fix, 7 should-fix, 3 nits), all folded in above: the router for `PinDialog.test.tsx`; `onSettings` decided and a drawer test; the right harnesses for Task 6 and a check for any request to the old path; the cause's wording on retries; `next` captured in Task 3; the pending test made concrete; the scroll stub; the bold label; real mutations for Task 4; Task 1's setup; line anchors; the README "Marking." edit logged as a decision.

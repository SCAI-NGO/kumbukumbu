# The findings of the pre.3 tests

Date: 2026-11-09. Status: approved by the maintainer.

On the evening of 2026-11-09 the maintainer and Claude (through Playwright, with test users on staging) tested `v0.4.0-pre.3`. Nothing was broken; what came out is about understanding, not correctness. The maintainer himself got stuck setting up discreet vaults, which is the point of testing this way: what confuses him will confuse users. The test list lives in his Kumbu² (`/SWE/Kumbu/Tests/Test list 0.4.0-pre.3.md`, findings F1 to F6); this spec takes them for 0.4.0. Hand tests by the maintainer follow tomorrow and will bring a polish round of their own.

One branch, one pull request, each section its own commit. Web app and texts only.

## 1. Goals, non-goals, anti-goals

Goals: §2 to §7.

Non-goals:

- Any change to what discreet vaults hide or how (the server, the PIN check, the rate limit, the single answer).
- Other parts of the test list that haven't been tested yet.

Anti-goals, which make this a failure even if it works:

- **Anything that tells a curious person that discreet vaults exist.** Every new text and every new menu entry is the same for every user outside discreet mode, whether or not they have a PIN or discreet vaults. The single answer ("You have no discreet vaults.") stays as it is.
- **A user who loses their place.** Nothing moves the user away from what they were doing on its own; a link the user taps may.

### An example from end to end

Dana joins and lands in `Welcome`, not in `Skills`. She reads "Discreet vaults" in the Welcome notes, opens More vaults… out of curiosity and sees under the PIN field "The PIN is set in Settings → Discreet vaults." with a link; she follows it, sets `4711`, comes back, opens More vaults…, enters it and picks 10 minutes. The sidebar has its amber frame. In the ⋯ menu of her vault `Tagebuch` she chooses **Discreet**; she taps ✕ and `Tagebuch` is gone. Later, revealed again, the same menu says **Not discreet**.

### Security boundary

Web app and texts only. Not touched: the server and its API (the existing `PUT /api/account/discreet/vaults/{vault}` is used as it is), the CSP, encryption, accounts, sessions, the PIN check, sharing, WebDAV, sync. The hint of §2 is static text.

## 2. F1: More vaults… points to where the PIN is set

Today, before a PIN exists, More vaults… asks for one and answers "You have no discreet vaults.", which is right (the single answer) but a dead end: nothing says that the PIN is set in Settings. The maintainer entered a new PIN there, expecting to set it, and was stuck.

- In `web/src/discreet/PinDialog.tsx`, below the PIN field's "4 to 6 digits" hint, a fixed line: "The PIN is set in Settings → Discreet vaults." "Settings → Discreet vaults" is a link to the Settings page, scrolled to that section (give the section an id if it has none), and it closes the dialog.
- The line is always there, for every user, before and after a wrong PIN, so it gives nothing away.

## 3. F2: the Welcome notes explain discreet vaults

The switch to mark a vault is visible only in discreet mode, by design, so nobody finds it without being told. A short section goes into the Welcome vault's notes, in the voice of the others, where it fits best (Claude's choice: `Security and privacy.md`, or `Working by hand.md` if that reads better): what discreet vaults are for (Kumbu² open at a family table, at work, on the train), and the three steps: set the PIN in Settings → Discreet vaults; open More vaults… in the vault menu; while the frame is shown, mark vaults in their ⋯ menu (§5) or in Settings. One sentence that agents, synced devices, WebDAV and keys still see them, and that the PIN hides, it doesn't encrypt. Keep `WelcomeTextTests` green.

## 4. F3: a new user starts in Welcome, not in Skills

`web/src/shell/MobileShell.tsx:237` takes the first vault of the list as the active one when none is set, and `Skills` sorts before `Welcome`. Choose instead the first vault that isn't the Skills vault (`kind === "skills"`, as `NewKeyDialog.tsx:165` tells them apart); only if there is no other, Skills. Nothing else changes: a vault the user chose stays chosen.

## 5. F4: Discreet / Not discreet in the vault's own menu

Decided by the maintainer: in discreet mode, the vault's ⋯ menu in the sidebar (`vaultItems`, `web/src/shell/TreeDrawer.tsx:518`) offers **Discreet** for a vault that isn't marked and **Not discreet** for one that is. Why: that is where he looked for it first. Outside discreet mode the entry doesn't exist, so the menu looks as it does today and gives nothing away.

- Not for the Skills vault, as in Settings.
- It calls what the Settings switch calls, with the same effect: a vault marked while revealed stays shown until the mode ends.
- Also for a vault shared with the user (the mark is theirs, as in Settings).
- A short confirmation where other vault actions show theirs ("Marked as discreet" / "No longer discreet"); a failure says so.

## 6. F5: no request for the old name after a note is named

After `Untitled.md` is named from its heading, the app asks `GET /api/notes/<vault>/Untitled.md` once more and gets 404 (and `GET /api/publications/<vault>/Untitled.md` several times). Nobody sees it, but it is noise and an extra request. Find what still asks for the old path after `moved(from, to)` (a query not removed or a refetch of the old key) and stop it, the way a manual rename through `MoveDialog` does it if that one is quiet. If the cause is shared with the manual rename, fix both. If it turns out to be deep, leave it and record it in `docs/superpowers/open-items.md`.

## 7. F6: the README says where hiding leads

The README says hiding "returns to the last ordinary vault". What the code does (`web/src/discreet/hideAgain.ts:164-182`, pinned by `hideAgain.test.tsx:166` and `:189`): if a discreet view is on screen, the tab goes to the last ordinary note it read (`recentPaths`), or to the vault overview ("Your vaults") if there is none; if an ordinary note is showing, it stays. On staging the test users had read no ordinary note, so they saw the overview.

Decided by the maintainer: **keep the behaviour, correct the README.** Why: going back to the note one last read is not a jump to somewhere unexpected; only something "random" would be. A blank slate where there is nothing to go back to.

- `README.md:386`: "the app returns to the last ordinary vault" becomes a sentence saying the tab goes back to the last ordinary note it showed, or to the overview of the vaults if there is none, and that an ordinary note on screen stays.
- No code change; `hideAgain.ts` and its tests stay as they are. Its comments are corrected only if they say otherwise.
- The discreet-vaults spec stays as written.

## 8. Tests

Vitest, as the surrounding code has them: §2 the line and its link, the same with and without a PIN; §4 the first non-Skills vault, Skills alone, a chosen vault kept; §5 the entry only while revealed, Discreet / Not discreet, not for Skills, the call made; §6 no request for the old path after a heading names a note and after a rename through Rename or move. §3 by `WelcomeTextTests` (`dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*WelcomeTextTests"`); §7 needs none.

## 9. Hand test

The report's hand-test checklist: steps for each section, where to look, what passes, including the example above as a whole.

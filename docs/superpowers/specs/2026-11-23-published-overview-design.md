# Published: everything you published, in one place

Date: 2026-11-23. Status: F1 decided by the maintainer on 2026-11-23 (a window of its own); the other findings built on their recommendations while he decides them. Target: 0.7.0 (step 1); step 2 may move to 0.8.0.

## 1. Why

The owner wants direct access to everything they have published: go to it, withdraw it, update it, and see at a glance where the copy and the note have drifted apart. An overview existed (publish spec §3.11) but it was a section at the bottom of Settings → Vaults, read-only: to update or withdraw a copy, the owner had to open each note and use its bar.

## 2. What it is

**Step 1.** A window of its own, **Published** (`/published`), as Recent, in place of the section in Settings → Vaults. Per published note:

- the path, a link to the note (in the focused pane, as before);
- a dot and a word, as on the note's bar: green "Up to date", yellow "Copy is older than the note" (the note, a linked note of its bundle or an embedded file changed since the copy);
- "with N linked notes" for a bundle, the snapshot's time, and who published it;
- **Update copy**, only where the copy is older; for a bundle it opens the publish dialog with the bundle's list first, exactly as the bar does;
- **Unpublish**, after the same question as on the bar.

Above the list: "2 of 7 copies are older than their notes." when any is. Publishing switched off on the server: one line says so, Update copy goes, Unpublish stays (withdrawing always works, publish spec decision 20).

It is opened from the drawer's account row ("Published", while the server publishes) and from the command palette ("Open / Published").

**Step 2 (a separate pull request).** **Compare**, on the list and on the note's bar: the published copy and the note side by side, in the comparison view the activity and search already use.

## 3. UX review

As CLAUDE.md asks for a new functional block of the UI; each finding with its recommendation, to be decided one by one. Principles: unified UX (reuse the bar's actions, dialogs and words), cut before adding.

**F1. Where it lives.** Recommended was a page of Settings, beside Access (the other things the owner gave out). **Decided (Chris, 2026-11-23): a window of its own, as Recent.** *Why:* publishing is a first-class use, not a setting; in practice people write notes in order to publish them, at work and in his own use.

**F2. Which actions.** *Recommendation:* Update copy and Unpublish, through the bar's own hooks and dialogs (the password window is asked for by the same prompt). **No Copy link** here. *Why:* the overview's answer never holds a link (publish spec §3.11), so this page doesn't put every share code on one screen; Copy link stays on the note's bar, one click away through the row's link.

**F3. Order.** *Recommendation:* by path, as before, with the count of older copies above the list. *Why:* a stable order is findable; a list that resorts itself after each Update copy moves the row away under the pointer. *Alternative:* older copies first.

**F4. Skills shared by link.** *Recommendation:* not on this page. They have their own list in Settings → Skills → Shared, with their own actions. *Alternative:* one page for both; more complete, but two kinds of rows with different actions.

**F5. Compare (step 2).** *Recommendation:* the copy is compared as the note's own text at the version the copy was made from, against the note now, with the activity's side-by-side view (`CompareVersions`): the owner sees what they changed, including what the copy's filter takes out (comments, tags). Where that version is no longer kept in the note's history, the column says so. For a bundle, a picker of its notes that changed. *Alternative:* the stored copy against a fresh filtered copy of the note: shows exactly what Update copy would change, but needs the copy decrypted for the owner and the filter run on every view.

## 4. Built

- `GET /api/publications` (the overview) now also carries each publication's walk, `notes`, `depth` and `without`, as the note's own read does, so that Update copy can show a bundle's list from the overview. Still never the link (`src/Kumbu.Server/Publishing/PublicationApi.cs`, `PublicationOverviewView`).
- `web/src/published/PublishedPage.tsx` replaces `PublicationsSection.tsx`; the bar's `ConfirmWithdraw` is shared.

Step 2, Compare (its own commit, so that it can be reverted to move it to 0.8.0):

- The owner's reads of a publication (`GET /api/publications`, `GET /api/publications/{vault}/{path}`) carry `copied`, each note of the copy with the version it was made from, the start first.
- `web/src/note/CompareCopy.tsx`, a sheet over `CompareVersions` (with `fixed`: no mode switch, and a version no longer kept says so). **Compare** sits on the row and on the note's bar wherever the copy is older.

Decision records: `docs/adr/0017-published-is-a-window-of-its-own.md`, `docs/adr/0018-compare-a-copy-by-its-note-version.md`.

# Test notes: phase 9a (web app)

Date: 2026-10-25. Branch `step-9a` (from the plan `docs/superpowers/plans/2026-10-24-web-app.md`), 10 tasks plus one fix wave after the final review; web 278 tests, `dotnet test` 985 passed. Walked through by the agent in Chromium through the Claude in Chrome extension, against the local stack on `http://localhost:8083`, signed in as the test admin.

Test data was made through the API first: vaults `Arbeit` and `Privat`, `/Arbeit/projekte/{alpha,beta}/status.md` (an ambiguous `[[status]]`), `/Privat/urlaub.md` (a link across vaults), `/Arbeit/plan.md` with links, tasks, a table and a code block, and an API key `testagent` with write access to `Arbeit` to play the agent.

Window sizes: a laptop window (1248 × 674 viewport) and the narrowest window Chromium allows (500 × 658 viewport). Chromium does not make a window narrower than 500 px; 390 px needs DevTools' device mode or a phone.

## Review Focus

| # | Case | Result |
|---|---|---|
| 1 | Editor saves while the agent writes the same note | The agent's `PUT` lands, the next autosave gets 409: toast `Changed elsewhere — reload / keep mine / view diff`. The server keeps the agent's text until a choice. The diff shows `−` for the agent's lines and `+` for mine. „Keep mine“ then writes on top, `lastWriter: admin` ✅ |
| 2 | Folder upload with `.obsidian/`, images, nested folders, an existing note | Simulated as a drop (a synthetic `drop` with `webkitGetAsEntry` entries; the native folder picker cannot be driven). `4 Markdown files to upload, 3 files skipped`, each skipped one with its reason. „Upload“ writes nothing while `projekte/alpha/status.md` waits for a choice (`3 new notes wait for this choice`); after ticking it: `3 notes uploaded, 1 replaced, 3 files skipped`, the old version stays in the history ✅. Per-batch progress not seen: four files are one batch |
| 3 | A new API key on screen | Not in `localStorage`, `sessionStorage`, the URL or a script-readable cookie. „Back to the notes“ before copying asks `You haven't saved your API key` (Stay / Leave without it); after „Copy key“ it leaves without asking ✅ |
| 4 | Wiki links: ambiguous, across vaults, missing | `[[status]]` dotted with `2 notes`; `[[urlaub]]` (only in `Privat`) and `[[gibts-nicht]]` dashed, never resolved across vaults; `[[projekte/alpha/status\|Alpha]]` and the escaped pipe in the table resolve. Tapping a missing link creates the note in the folder and opens it in Write ✅ |
| 5 | Session ends mid-use (`end-others` from a second session) | Next click lands on `/signin?next=…` with `Your session has ended. Please sign in again.`; after signing in, back on the same note ✅. Also while typing in Write: the text typed after the session ended is kept, restored after signing in and saved ✅ |

## Also checked

| Check | Result |
|---|---|
| Agent banner after an agent's edit | `Agent key testagent edited just now`; „Show“ tints exactly the two changed task lines, labelled `testagent` |
| Search (hybrid) on the narrow window | Results with path, snippet, score bar and reason (`exact` / `meaning`); opening one goes to the note |
| Drawer on the narrow window | Nested tree with counts, vault chips, Keys / Settings / Admin / Sign out |
| Editor toolbar | `[[ ]]` inserts the brackets with the cursor inside; „Done“ saves and switches to Read |
| Ticking a task in Read | Saved as `- [x]` |
| Console | No errors |

## Findings

All five fixed on the branch before the merge (commits 96a88f2 to d3c540f, reviewed, checked again in the browser).

1. **Ambiguous link candidates only in a tooltip.** `web/src/note/MarkdownView.tsx:131` shows the candidates as `title`; on a phone there is no way to see them or pick one. Suggestion: tapping opens a small list of the candidates.
2. **A mixed list loses its bullets.** A list with one task item gets `contains-task-list` on the whole `<ul>`, which `MarkdownView.tsx` turns into `list-none`, so ordinary items in it show without a bullet (`- eins` next to `- [x] …`).
3. **No feedback on „Copy key“.** `web/src/routes/KeyReveal.tsx:23` only records that it was saved; the button looks the same afterwards. The same pattern is in `RecoveryKeyReveal.tsx` and `InvitationReveal.tsx`. Note for the VPS: `navigator.clipboard` needs HTTPS; under plain HTTP copying fails and only the hand selection is left.
4. **Drawer open again after Keys.** Opening Keys from the drawer and going „Back to the notes“ shows the note with the drawer still open.
5. **Note title twice.** The file name (`plan`) is shown as a title above the note's own H1 (`Plan`).

## Ideas, not findings

- Completion for `[[` in the editor (note names of the vault); not in the concept.

## Not tested here

- A real phone: toolbar above the on-screen keyboard, `keepalive` on closing the tab, the recovery key download on iOS Safari, edge swipes.
- The native file and folder pickers.
- Join with an invitation and the recovery key screen (covered by tests; not walked through in the browser this time).

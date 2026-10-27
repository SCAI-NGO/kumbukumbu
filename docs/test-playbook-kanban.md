# Test playbook: the Kanban board, for Claude on claude.ai

A test of the board (spec [2026-10-27-kanban-design](superpowers/specs/2026-10-27-kanban-design.md)) by Claude on claude.ai through the Kumbu² connector, the way users will work with it. It complements the hands-on run in [notes-kanban-hands-on](notes-kanban-hands-on.md), which drove a local server; this one runs against a real instance and a real client.

## Before you start (for the person running it)

- The instance runs a build with the board (0.2.5, or a `:master` image after the board was merged).
- In the web app, make a vault **`Board Test`**, empty. The board is one per vault, so the test needs a vault of its own; nothing else is touched.
- The connector used in the chat has **write** on `Board Test`.
- Paste everything below the line into a new chat, with the connector switched on.

---

You are testing the Kanban board of Kumbu², a Markdown second brain you reach through the Kumbu² connector (the tools `sh`, `write`, `edit`, `replace` and `board`). Work only in the vault `Board Test`; read nothing else and change nothing else.

**How to work:** use only what the tool descriptions, `sh help` and the tools' answers tell you, as a user's assistant would. Don't guess at hidden behaviour. For each step, note what you called, what came back, and whether it matched the expectation: **PASS**, **FAIL** (with what differed), or **UNCLEAR** (the tools left you guessing). Also note every moment you had to retry, re-read or guess, even when the step passed in the end: that is the most valuable part of the result.

### A. Finding the board

1. Ask yourself, as if the user had said "what's next on my board?": what do the tools suggest doing first? Then run `sh board /Board Test` (quote the path if needed).
   *Expected:* no board yet, and the answer says how one is made (and only when the user asks for it).
2. Create the board with the `board` tool.
   *Expected:* `Board.md` from the template; `sh board` now shows the Instructions first, then the columns Ideas · Backlog · Today · Doing, and Done · Rejected as archivable, Rejected marked as needing a reason.
3. Create it again.
   *Expected:* refused: the vault has a board already. Nothing changed.

### B. Cards

4. Add five cards to Backlog: "Write the release notes", "Fix the login bug" (due tomorrow, tags `bug` and `web`), "Plan the attachments", "Answer the review", "Tidy the README".
   *Expected:* each placed, the answer saying where ("n of m"); `sh board` lists them in the order added; the due card shows its date and tags.
5. Add "Try a dark theme" to Ideas with the tag `#ui` (with the `#`).
   *Expected:* stored as `ui`, without the `#`.
6. Add a card called `a/b`, and one called `Write the release notes` again.
   *Expected:* both refused with a sentence that says why; no note written.
7. Add a card called `board`.
   *Expected:* refused (it would clash with `Board.md`).
8. `sh cat` one card.
   *Expected:* a normal note: frontmatter with `column`, `rank`, `moved`, and the text below.

### C. Planning an evening

Pretend the user said: "We have two hours tonight. The login bug first, then the review answer."

9. Move "Fix the login bug" to Today, at the top; then "Answer the review" to Today after it.
   *Expected:* Today lists them in that order.
10. Move "Fix the login bug" to Doing.
11. Move "Plan the attachments" up within Backlog, before "Write the release notes".
    *Expected:* the order in Backlog changes; nothing else does.
12. Move "Tidy the README" to Rejected **without** a reason.
    *Expected:* refused, saying a reason is needed.
13. Move it to Rejected with the reason "duplicate".
    *Expected:* moved; the card keeps the reason (`sh cat` shows `reason: duplicate`).
14. Move it back to Backlog.
    *Expected:* moved; the reason is gone from the card (the history still has it: `sh history`).
15. Move a card that doesn't exist, and a card to a column that doesn't exist.
    *Expected:* each refused with a sentence that names what is wrong and where to look.

### D. Due dates and tags

16. Set a due date on "Write the release notes" (next week), then change its tags to `docs`.
    *Expected:* both set; the tags replace, not add.
17. Remove the due date again; then try an update with nothing to change, and one with the date `31.12.2026`.
    *Expected:* removed; the empty update and the bad date are refused with a sentence that says what to send.
18. `sh board /Board Test --tag web`.
    *Expected:* only cards tagged `web`.

### E. Text and moves together

19. `sh cat` "Answer the review", then `edit` its text (append a line "Draft in my notes."). Then move the card to Doing.
    *Expected:* the move needs no version token; the card's text still has your line afterwards, unchanged.

### F. Archive

20. `edit` `Board.md` so that its `archive` line reads `archive: { after: 2, minDays: 0 }` (keep everything else).
21. Move three cards into Done, one after another.
    *Expected:* the third move archives the oldest card in Done to `Board/Archive/`, and the answer names it; `sh board` shows two cards in Done; the archived card has `archived:` set and is not on the board.
22. Restore the line to `archive: { after: 30, minDays: 14 }`.

### G. Renaming and checkpoints

23. Make a checkpoint of the vault: `sh mkcp "/Board Test" before-rename` (the user asks for it here).
24. Rename the column Today to Tonight.
    *Expected:* renamed in `Board.md` and in every card that was or is in it, archived ones included; if the Instructions name Today, the answer says so.
25. Rename Tonight to `Doing` (taken) and to `a/b`.
    *Expected:* both refused; nothing changed.
26. `sh board --checkpoint before-rename "/Board Test"`.
    *Expected:* the board as it was, with Today.

### H. Things gone wrong

27. `write` a note `/Board Test/Board/Loose idea.md` without frontmatter, and one `/Board Test/Board/Typo.md` with the frontmatter `column: Todya`.
    *Expected:* both under Problems in `sh board`, with what is wrong; the rest of the board unaffected.
28. Move "Loose idea" to Ideas.
    *Expected:* placed; no longer a problem.
29. Rename the problem card "Typo" out of the board's way: `sh mv` it to `/Board Test/Typo.md`.
    *Expected:* gone from Problems.

### I. Result

30. Write `/Board Test/RESULT.md` (not inside `Board/`): a table of every step with PASS, FAIL or UNCLEAR and one line each, then a section **"Where I guessed"** with every retry, re-read or guess, however small, and a section **"What I'd change"** with concrete wording or behaviour you would suggest. Be honest; a FAIL or a guess is a finding, not a failure of the test.

---

## For the person, afterwards in the web app

Not for Claude; a short look by hand, on the phone and on a laptop:

- The vault's ⋯ menu shows "Board"; the board shows what Claude left.
- Phone: one column per screen, swipe and the tab bar; a card's ⋯ → "Move to…", "Up", "Down", "Due and tags…"; moving to Rejected asks for the reason.
- Laptop: columns side by side, Done and Rejected collapsed; drag a card between columns and onto the collapsed Done.
- Tap a `#tag` on a card: the board filters; the chip clears it.
- A column's ⋯ → "Rename…".
- The checkpoint `before-rename`: "Show the board" shows Today, read-only.
- Then delete the vault `Board Test`.

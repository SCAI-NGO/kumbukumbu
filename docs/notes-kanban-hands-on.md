# Test notes: the Kanban board, hands-on as the agent

Date: 2026-10-27. Hands-on test of the board (spec [2026-10-27-kanban-design](superpowers/specs/2026-10-27-kanban-design.md)) by Claude, as the agent that will use it. Set-up: the server from `src/Kumbu.Server` on `127.0.0.1:8099` against a local MongoDB, a dev token, a seeded vault `Work`, search off. Driven over MCP (`/mcp`, JSON-RPC over HTTP) with a throwaway client, reading nothing but the tool descriptions, `help board` and the tools' output. The REST endpoints were checked with `curl`.

## What was done

| Step | Calls | Result |
|---|---|---|
| Look | `ls /`, `help board`, `board /Work` | "no board in /Work; the board tool's create makes one when the user asks for it" |
| Create | `board` `create` with `vault` `/Work`, then `Work` again | Created from the template; the second call refused: "/Work/Board/Board.md exists already" |
| Five cards | `add` into Backlog (one with `due` and two tags), Backlog, Today, Ideas (tag `#ui`), and `backlog` at `top` | All placed, with "(n of m)"; `#ui` stored as `ui`; `backlog` matched Backlog |
| Plan an evening | `move` to Doing; to Today `top`; `before:` and `after:` a card; `after:` a card of another column; Rejected without and with a reason | All as asked; the wrong `after:` and the missing reason refused with a sentence that says why |
| Wrong names | `move` of an unknown card, to an unknown column | "no card 'Nonexistent' in /Work/Board ('board /Work' lists them)"; "there is no column 'Later' (columns: …)" |
| Update | `due` by the card's path; `tags`; a bad date; `due: null`; nothing; `clear_due`; `tags: []` | All as documented; `tags: []` removes the tags as `clear_tags` does |
| Archive | `edit` of `Board.md` to `archive: { after: 2, minDays: 0 }`, then two moves and an add into Done | The third card pushed the oldest into `/Work/Board/Archive/` with `archived:` set; `history /Work/Board` shows it as `dev (mv)` |
| Rename | `rename_column` `today` → `Tonight`; → `doing` (taken); → `a/b` | Renamed in `Board.md` and 3 cards (one of them in the archive); the other two refused |
| Checkpoint | `mkcp /Work before-rename` before the rename, then `board --checkpoint before-rename /Work` and `--column Done` | The board as it was, with Today |
| Problems | `write` of a card without frontmatter and one with `column: Todya` | Listed under Problems; `move` of the first one placed it |
| Broken `Board.md` | `kumbu-board: 2` | Reader, tool and `GET` all say what is wrong (`GET` 409); nothing guesses |
| REST | `curl` on create, move, add | 409 board exists, 404 unknown card, 400 unknown column, 409 card exists, 400 missing reason, 404 unknown vault |

## Where I guessed, retried or misread

1. **`board <vault>` in sh, `vault` in the tool.** The tool's description said "read the board first with sh `board <vault>`" and its `vault` parameter's example is `Work`, so I tried `board Work` and got "paths must be absolute". The tool takes `Work` and `/Work` alike, so only sh tripped. *Fixed:* the description says `board /<vault>`.
2. **Which column needs a reason.** `board /Work` printed `Done (0) · Rejected (0): archivable`; nothing said that Rejected takes a card only with a reason, and the description's "reason is optional" for `move` read as if it never mattered. I found out by being refused (the refusal was clear). *Fixed:* the reader marks such a column, `Rejected (1, needs a reason)`, in the archivable line, the lane heading and `--column`, `help board` says so, and the description's `move` line points to it.
3. **What the archive took.** "archived 1 card to /Work/Board/Archive/" didn't say which; to tell the user I had to `ls` the archive. *Fixed:* the result names the cards, by their name in the archive (`archived 'Answer the review' to /Work/Board/Archive/`, at most five, then "and N more").
4. **A rename leaves the Instructions stale.** After renaming Today to Tonight, `board /Work` still began with "when Today and Backlog run short". The Instructions are the user's text, so the rename rightly leaves them, but nothing told me. *Fixed:* the result adds "the Instructions in Board.md still name Today: edit them if they should follow" when they name the old column as a word.
5. **`update`'s requirements.** "Needs vault, card and due, tags, clear_due or clear_tags" reads as if all four were needed. And with the spec's "null removes" in mind I sent `due: null` to remove the date; the answer "update needs due (or clear_due) or tags (or clear_tags)" took a second read before I saw that `null` counts as not given. *Fixed:* "at least one of due, tags, clear_due, clear_tags", and the refusal says "to remove the due date or the tags, set clear_due or clear_tags".
6. **Do tags add or replace?** The `tags` parameter said "replacing the card's", the description's `update` line didn't, and that line is what I read first. *Fixed:* "tags replace the card's tags".
7. **`create` on a board that exists.** Not a retry (the refusal is clear), but the description didn't say it; an agent asked to "set up the board" might fear overwriting. *Fixed:* "refused when the vault has a board".
8. **Problems without a way out.** `Problems (1)` and `/Work/Board/Loose idea.md: no column` said what was wrong but not what to do; `move` happens to fix it. *Fixed:* a line under the problems, "the board tool's move puts a card in a column".
9. **Would I find the board at all?** The server's `initialize` instructions point to `semsearch` and `rg` for "projects, decisions, plans"; asked "what do we get done tonight?", I would have searched notes before I thought of `board`, which only the sh command list and the board tool mention. *Fixed:* one clause in `ServerInstructions.Text`: "For what to work on next, a vault may have a Kanban board (sh: board /<vault>)."
10. **The archive** also deserves a line in the tool description: an add or move into Done can move other cards, which an agent should tell the user. *Fixed:* the description says so and that the result names them.

## What worked without a second look

- Every refusal says why and what to do: an unknown card names `board /Work`, an unknown column lists the columns, `before:`/`after:` a card of another column says that the card must be in the target column, a bad date names `YYYY-MM-DD`, a taken or invalid column name says so, an unknown `kumbu-board` version names the versions the server knows.
- Columns ignore case (`backlog`, `today`, `rejected`), a card can be named by title or path, a tag given as `#ui` is stored as `ui`.
- "(2 of 2)" after a move is enough to confirm a reorder without reading the board again.
- `--checkpoint` reads the board as it was, `--column` of an archivable column lists its cards.
- The card's text stayed as written through every move and update.

## Left as it is

- `board --column Rejected` shows the titles, not the reasons; `cat` of a card shows its `reason`. Fine for a planning look.
- `board --tag` still prints the Instructions and every column, empty ones included; consistent, a little long.
- Cards that entered an archivable column on the same day are archived in rank order, so with `minDays: 0` the card just moved to the top of Done went first. Days are the rule's unit by design; with the defaults (14 days) this doesn't arise.
- Two cards with the same content (same column, rank and day) get the same version token; tokens are per note, so nothing is confused.

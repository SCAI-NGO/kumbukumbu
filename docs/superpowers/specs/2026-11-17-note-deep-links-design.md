# Note deep links — design

Status: approved by Chris on 2026-11-17, for 0.6.0. Idea note: `/SWE/Kumbu/Ideas/Note deep links via MCP 2026-11-17.md`.

## Why

An agent that just wrote or found a note knows only its vault path, which isn't clickable. With a link in its answer, the user taps once and reviews what was written, in chat, by voice or on the phone.

## Goals

- Every note and file has a clickable URL into the web app, and the MCP tools hand it out without an extra call.
- The same URL works for every member of a shared vault, whatever each calls the vault ("gartenarbeit" for Alice, "garten" for Bob).
- Opening it needs a sign-in; it never makes anything public (that is Publish).

## Non-goals

- **Links inside notes recognised as links** (`links`, `backlinks`, `graph` and `mv` treating these URLs like `[[…]]`). Decided by Chris: out, "eindeutig". It would make them cross-vault links, a large topic of its own (the link index is sealed per vault, and backlinks across vaults would tell one vault about another); it comes well after 1.1.
- Section or block anchors. The web app has no heading anchor in its note URLs today; a link leads to the note.
- Links that survive a move. A link names the path; see below.

## Approaches

One sensible way: name the vault by its id, which every member's access already carries (`Vaults/VaultAccess.cs:17`), and let the web app turn it into the reader's own name for the vault. A stable note id would survive moves but is new to the data model and buys nothing for links meant to be tapped now; a scheme of our own (`kumbu://`) isn't clickable everywhere; the plain `/notes/<name>/…` URL breaks between members.

## The design

### The URL

`<origin>/n/<vault id>/<path inside the vault>`, each segment percent-encoded, e.g. `https://kumbu.example.org/n/6a1f…/Garden/Roses.md`. The same form for notes and files (`/n/<id>/Garden/rose.png`).

Origin: `KUMBU_PUBLIC_URL`, else the request's origin, as OAuth does (`OAuth/OAuthUrls.cs`). Another instance's link simply leads to that instance.

### Where the MCP tools return it

**One rule: wherever a version block is printed, the link is printed with it.** That covers `write`, `edit`, `replace` (one per changed note), `cat` of a note or a section (the link leads to the note), and the upload's answer (`docs/superpowers/specs/2026-11-17-binary-upload-for-agents-design.md`). Not in search results (`rg`, `semsearch`): many hits, many tokens, little use; the agent `cat`s the note it wants to show.

**At most ten links per answer. Decided by Chris.** `Mcp/ToolResult.cs` prints the link block after the first ten version blocks that get a link and then one line, `…and N more links: cat a note for its link`; the version blocks stay complete. Reason: versions are for the agent, and every one is needed for a follow-up edit; links are for the person to tap, and past a handful they only cost context (a `replace` of 400 notes would add about 44 KB of links). One limit in one place covers `replace`, `cat` pipelines and `mv` of a folder alike, so the one rule holds, with a measure.

It is its own text block after the version block, on its own line, e.g. `link to /Work/plan.md: https://…/n/…/plan.md`, so it never mixes with the note's text or the version token (`Mcp/ToolResult.cs`). The server maps the path's vault name to the vault's id through the caller's opened access; Core stays unaware of hosts and ids.

The tool descriptions (`Sh/Manual.cs`) say once that the link opens the note in the web app after sign-in, and that it is the thing to show the user.

### The web app's route `/n/:vaultId/*`

1. Not signed in: to sign-in, and back here afterwards (`safeNext` accepts `/n/`; the sign-in's reset for another user, `routes/SignIn.tsx:55`, covers `/n/` as it does `/notes/`).
2. Finds the vault in the user's vault list by its id: `VaultView` (`Api/NoteApi.cs:41`) gains `id`.
3. Checks the note or file exists, then replaces the URL with `/notes/<the user's name for it>/<path>` for a note, `/view/<name>/<path>` for a file. The tree leaves out the archive, so a path under `_archive/` is checked as the archive sheet checks it: a note through the notes API, a file against the archive's file list (found in the final review: an archived note's `cat` prints a link too, and the one rule holds there).
4. **Otherwise one page for every case**: the vault isn't in the list (not a member, deleted, a discreet vault not revealed), or the path doesn't exist (moved, removed). Its text says so plainly and the same for all: "This link leads to a note that isn't here: it was moved or removed, or it's in a vault you can't see right now." It offers the search, filled with the note's name. Why one page: a link must never tell whether a vault or note exists that the reader can't see; and the honest sentence names every case, so it doesn't mislead the owner of a discreet vault either.

### A link pasted into a note

Not a user error, and nothing to clean up: it is an ordinary `https` link, clickable in Kumbu², Obsidian and everywhere, and it leads every member to the note under their own name for the vault. It names the path, so after a `mv` it leads to the page above; `mv` doesn't rewrite it, since the link index doesn't know it (non-goal). Accepted as "one rule over exceptions": a deep link is a pointer for now, `[[…]]` is the link that lasts.

### Security

The vault id is in the clear in the database already and opens nothing; a link without a session opens nothing. `docs/security-architecture.md` §5.6 ("What is in the clear") names the id as visible in deep links.

## Tests (first)

xUnit: the link next to every version block (`write`, `edit`, `replace` with two notes, `cat`, `cat` of a section); the id is the vault's, the same for owner and member; a path with spaces and umlauts encoded; `KUMBU_PUBLIC_URL` wins over the request's origin; `VaultView.id`.
Vitest: `/n/` resolves to the user's name (an alias for a member); to `/view/` for a file; the one page for an unknown id, a missing path; sign-in comes back to the link.

## Left to the builder

The exact line format and the tool descriptions' wording within the rule above, how existence is checked (the notes and files APIs as they are).

## Changelog

New: **Links that agents can show you** — after writing or reading a note, an agent gets a link that opens it in the web app with one tap; it works for every member of a shared vault, whatever each of you calls the vault.

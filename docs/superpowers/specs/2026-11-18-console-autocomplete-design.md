# Console autocomplete

Status: design, agreed with the maintainer on 2026-11-18. Target: 0.6.0, as the pull request after "ls -l, console shortcuts and peek" (`2026-11-18-console-shortcuts-and-peek-design.md`), which it builds on. A small change in `web/src/console/` only; no server change.

## Why

Typing paths in full is the console's slowest part, on the phone above all. A shell without completion feels unfinished to anyone used to bash or zsh.

## Goals

- Complete vaults, folders, notes and attachments, and the first word as a command.
- On the desktop by Tab, as in zsh; on the phone by a suggestion row above the input.

## Non-goals

- No server change, no new endpoint: completion reads what `ls` over `sh` already gives.
- No completion of options (`--archive`), tags or note contents.
- No fuzzy matching: a prefix, case-sensitive as paths are.

## Approaches

- **Phone: a suggestion row (chosen, A).** As soon as a path is being typed, a row above the input offers the matches as tappable chips, like a phone keyboard's word suggestions; a tap puts one in. Why: the maintainer found it the most comfortable; one sees the matches without asking.
- **Phone: a Tab button beside the input (B).** Less code, but the matches only show on the second press.
- **Data from a new completion endpoint.** Rejected: `ls` already lists what completion needs, and a cache per folder keeps it cheap.

## What completes

- The word under the cursor (the console's tokeniser decides where a word starts; a quoted word completes inside its quotes).
- The first word of a stage: commands, those of `sh` (from `help`'s list or a fixed list kept with the console's help) plus the console's own (`cd`, `pwd`, `clear`, `exit`, `l`, `la`, `lsa`, `p`).
- Any other word: a path. At `/` the vaults; otherwise the entries of the folder the word's directory part names, relative to the working directory as console paths are (`..`, `.` work). `cd` completes folders and vaults only.
- Names with spaces, quotes or other characters the tokeniser wouldn't read as themselves are put in with backslash escapes (`Meeting\ Notes.md`, `it\'s.md`), unless the word already opened a quote: then completion stays inside it and closes it only on a unique file match. Why: typing goes on after a folder (`My\ Folder/` then the next name) and a partial completion never leaves a quote open, which a closing quote would block or an open one would leave unreadable.
- A word that names a note or an attachment followed by `/` completes nothing.
- A vault's `_archive/` is offered only once its `_` is typed, as `ls` leaves it out, and only if the vault has one (the vault is listed with `ls -a` for that).
- A folder or vault completes with a trailing `/`, a file with a trailing space.

## Desktop

- **Tab:** one match completes fully; several complete to their common prefix; the next Tab, when the one before put in nothing new or only that common prefix, lists the matches in the output (one line, as bash and zsh do), without running anything. No match: the command line flashes once, briefly (its background lights up and fades in about 200 ms, in the accent colour, or the phosphor colour in Green and Amber), like a terminal's visual bell; no sound, no line in the output, no movement. Why: the maintainer wanted a small, transient sign that Tab was heard; a flash of colour rather than a shake keeps it calm and works under `prefers-reduced-motion` as it is.
- Tab on an empty line or an empty command word (after a `|`) does nothing. Why: listing some 30 commands unasked is noise.
- Tab never leaves the input (it's the console's key while the input has focus); Shift+Tab keeps moving the focus as usual.

## Phone (below the desktop breakpoint, `useDesktop()`)

- A row of chips directly above the input while the word under the cursor is a path or a command with at least one match, at most a scrollable single line; each chip a 48 px high target with the name (folders with `/`).
- An empty word after a command (`cat ` with the cursor after the space) offers the working folder's entries too, for `cd ` its folders only; an empty first word offers nothing. Why: the row is there to save typing on the phone, and the first name is where it saves the most; every command at once would be no help.
- A tap puts the match in as Tab would with one match, and the keyboard stays up (the input keeps focus).
- The row hides when the input is empty, a command is running, or nothing matches. It never covers the output's last line (the output's bottom padding grows with it).
- In Green and Amber the chips take the phosphor colours, outlined.

## Data

- A folder's entries come from `sh` `ls <folder>` (vaults: `ls`), cached per folder for the console's session, refreshed after any command that ran (it may have been `cd` into a changed place) and at most every 10 s; a failing `ls` offers nothing and shows nothing.
- No request per keystroke: one per folder until it's cached.

## Tests (Vitest)

- Tab: unique match, common prefix, second Tab lists, no match (the flash: a class on the command line, removed again), a folder's `/`, a file's space, quoted names, relative paths with `..`, `cd` folders only, commands as the first word and after a pipe, `_archive/` only when typed.
- Phone: the row shows matches for a path word, a tap completes and keeps focus, hides when empty, while running and with no match.
- Cache: one `ls` per folder, refreshed after a command.

## Left to the builder

- Where the completion logic lives (a pure function over the line, cursor, cwd and a lister, tested on its own, is the natural shape).
- The list of `sh` commands: from the manual or fixed.

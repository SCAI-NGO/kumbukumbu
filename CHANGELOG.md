# Changelog

What changed for the people who use and run Kumbu², per release. Older releases are described in their pull requests.

## 0.5.0 (2026-11-15)

The calendar, flags and Recent changes make Kumbu² a personal command centre: what's on, what matters and what just changed, and all of it is still just your notes.

### New

- **A calendar made of your notes.** Give a note a day, a time or a week in its front matter (`when`, `until`, `task`, `upcoming`, `tz`), or name it after a day (`2026-11-12.md`) or a week (`2026-W46.md`), and it is in its vault's calendar. The entry stays a note where it lives, so search, links, history, sync, WebDAV and agents work for it as for any other. Week and month views, a small month grid, a tag filter, **Today** and **This week**, and **Add to calendar…** in every note's menu. A to-do is ticked off, reopened, or moved on with **Later**; a board card with a due date is an entry too, its column its status.
- **What's on**: what is overdue, ongoing or coming up across all your vaults, with its count on a badge in the navigation and on the app icon. Times are local: your time zone and your week (workdays, lighter days, days off) are in Settings → Calendar.
- **`sh calendar` for agents:** a vault's week or month, `calendar notifications` for what's on, and `set`, `move`, `close`, `reopen` and `unset` for one note.
- **Flags.** Red, green, blue, purple, yellow, white, done and important, set from a note's menu and shown in its header, in the tree and in Recent changes. A flag is a tag (`flag/red`), so search and agents find it.
- **Recent changes:** every note and attachment across your vaults by its last change, with who changed it and when, filtered by words, vault and flag.
- **The explorer's toolbar:** one row above the folders with **+ New note**, Upload, Calendar, Board, Recent changes, Sort and Find in this vault. Find matches names by every word and by flag; the tree sorts by name or by last change.
- **The open note's own actions:** Rename or move and Delete note in a slim row above the note, in Read and Write mode.
- **Take photo** on phones and tablets, beside Upload here: a whiteboard straight into the vault.
- **Mermaid diagrams,** drawn in notes and in embedded notes, with Show source, and Full size for wide ones. Published pages show the diagram's code.
- **Closer to Obsidian:** links to a single block (`[[Note#^id]]`, and `cat "<path>#^id"` for agents); unlinked mentions below "Linked from" (`backlinks --unlinked`); `aliases` in the front matter, which links resolve by; and `cat` brings the notes a note embeds with `![[Note]]` along, so agents read what people see.
- **A link for your AI** on a published note's reader page: a link that an AI with an HTTP tool can fetch as Markdown, valid for an hour.

### Better

- **Properties at the top.** The properties box folds to "Properties · N" on calendar entries, flagged notes and board cards, whose header already says it in words; **+ Add property** adds one; a block further down that looks like properties offers **Move to properties**.
- **The calendar's tick is a checkbox** (To do / Done), with clear steps between weeks and months, **Back to this week**, and the arrow keys where the tree is docked.
- **Back where you came from:** a note opened from a calendar view or What's on leads back to the same week, month and filter, and the vault's `Calendar/` and `Board/` folders open their views.
- **The last note opens on start**, on each device; never one in a discreet vault.
- **Ctrl+S / ⌘S** in Write mode does what Done does.
- **A link to a heading** opens the note at that heading, or says the heading isn't there.
- **All vaults** marks the vault you are in.
- **Hardening from a security review of this release,** around the calendar, flags, Recent changes, Mermaid and AI links.

### Fixed

- **Front matter can't stall or crash the server any more:** a self-referencing YAML anchor or one that expands without end is refused, for boards, tags and skills too.
- **A menu no longer closes** when the tree scrolls by itself.
- **Signing out** no longer asks for the session again behind your back.
- **With publishing switched off,** a skill brought in by link says so, instead of failing its update check.

### Updating

- **Nothing to migrate,** no new settings, nothing is indexed again. `TZ`, if set, is the calendar's zone for people who haven't chosen one (else their browser's, else UTC).
- **A reverse proxy's access log:** request lines under `/s/t/` carry the tokens of AI links. Leave them out of the log as you do for `/api` (README, "Accounts, API keys and the REST API").
- **A `:prerelease` image tag** moves with every version tag, pre-releases included, for a staging server to follow.

## 0.4.0 (2026-11-11)

### New

- **WebDAV access.** WebDAV shares under Access → WebDAV: a label, vaults with read, write or maintain, an optional expiry, and a password shown once, with instructions for Dolphin, GNOME Files, Finder, Windows Explorer, rclone, Remotely Save and Nextcloud.
- **Storage quota.** A default limit for every user (`KUMBU_QUOTA_DEFAULT`, unlimited unless set) and a per-user limit in the admin's user list, enforced on every write, whichever way it comes in. Settings show the use, with a notice at 90 %; `df` and `du` in `sh`; the owner can empty a vault's trash.
- **The Access page in three tabs:** keys, apps, WebDAV.
- **Skills.** Instructions in the open Agent Skills format (`SKILL.md` and its files), in a Skills vault of every user and in `_skills/` folders of any vault. An AI finds and reads them through `sh skills`. Skills can be shared behind a link and brought in from marketplaces on GitHub; updates are shown for review, never taken automatically.
- **Ingesting attachments.** A system skill that turns PDFs and images into Markdown notes, their pictures stored beside them and described in words; images come out of PDFs with `pdfimages`.
- **Discreet vaults.** Vaults you mark as discreet don't exist in the web app until a PIN of their own shows them, for a time you choose (5 minutes to "never"). Agents, sync, WebDAV, keys and connections still see them: the PIN hides, it doesn't encrypt. Set up in Settings → Discreet vaults, shown through More vaults…; the Welcome note "Security and privacy" explains it.
- **Closer to Obsidian:** `==highlights==`, `%%comments%%`, maths with KaTeX and footnotes; Copy file path, Copy link and Copy Markdown link in the note's and the tree's menus; a new note takes its name from its first heading.
- **Published notes for AIs:** Copy as Markdown on the reader page, and a hint for agents on how to read a published link.

### Better

- **Search keeps sections whole.** Notes are split along their structure (sections, then paragraphs, lists, tables, code and quotes); a table stays whole or is split between rows with its header in every part; nothing is cut off at the model's limit any more; PDF pages are split by words.
- **A search hit shows the line that matched best,** not the first line of its section.
- **New users start in the vault Welcome,** not in Skills.
- **Hardening from a security review of this release:** tighter limits on what uploads, WebDAV, concurrent writes and very long lines can make the server hold or compute, and a few checks around discreet vaults, app connections, shared skills and published copies.

### Fixed

- **No more requests for a note's old path after a rename**, from the heading or through Rename or move.
- **A note's publication is asked for only when the note is published:** no 404 in the browser's console for every unpublished note.
- **Ticking a task box offline** says at once that the server can't be reached, instead of sending the tick later with an older version.

### Updating

- **Search is rebuilt once.** Every note and PDF is split anew after the update; texts that didn't change keep their vectors, so only what changed is embedded again. Until a vault is done, `semsearch` ranks its notes by words and says how many are pending.
- **New settings, all optional:** `KUMBU_DAV_ENABLED` (default true), `KUMBU_QUOTA_DEFAULT` (default no limit), `KUMBU_SKILLS_MARKETPLACES` (default true), `KUMBU_GITHUB_TOKEN`, and `TZ` for the time `archive` adds to names. See `.env.example` and the README.

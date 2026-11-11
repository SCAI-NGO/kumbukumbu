# Changelog

What changed for the people who use and run Kumbu², per release. Older releases are described in their pull requests.

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

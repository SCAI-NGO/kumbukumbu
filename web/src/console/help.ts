// SPDX-License-Identifier: AGPL-3.0-or-later

// Console design §6: the console's own words come before the agents' manual, which it never edits.

/** What `help` prints before the server's answer. */
export const helpBlock = `In this console:
  cd [path] (.. alone is cd ..), pwd, clear, exit
  l, la and lsa for ls -l, ls -a and ls -la; p <note> shows a note as it reads in the app (help p).
  Relative paths work here; the console turns them into absolute ones, and → shows the result.
  It only reads: pdfimages, restore, mv, archive, mkdir, rm, mkcp, rmcp, publish add, update and rm and skills copy are refused, and the write tools aren't here.

The manual agents get, unchanged:`;

/** `help <name>` for the commands the console answers itself, in the shape of the shell's own. */
export const localHelp: Record<string, string> = {
  cd: `cd [path]
  Change the working directory, a vault or a folder in one. Without a path it goes to /;
  . and .. work, and .. at / stays at /. The other commands' relative paths start here.
  .. alone is cd ..
  A note, an attachment or nothing there: cd: <path>: not a folder.

  cd /Work
  cd projects
  cd ..`,
  pwd: `pwd
  Print the working directory.`,
  clear: `clear
  Empty the scrollback. The history (up and down) stays.`,
  exit: `exit
  Go back to the notes. The directory, the scrollback and the history stay until a reload.`,
  l: `l [path]
  ls -l: the folder's notes and files with their sizes and times. The rest of the line goes on as typed.

  l
  l /Work | rg md`,
  la: `la [path]
  ls -a: the listing with the vault's _archive/ too.`,
  lsa: `lsa [path]
  ls -la: the listing with the vault's _archive/ too, with sizes and times.`,
  p: `p <note>
  Peek: show a note as it reads in the app, with headings, links and task boxes. A link opens
  its note and closes the console. Tasks don't tick here, and Mermaid diagrams show as their code.
  It reads the note with cat; a section works too (p "plan.md#Goals").
  A folder, an attachment or nothing there: an error.

  p plan.md
  p /Work/projects/roadmap.md`,
};

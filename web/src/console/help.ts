// SPDX-License-Identifier: AGPL-3.0-or-later

// Console design §6: the console's own words come before the agents' manual, which it never edits.

/** What `help` prints before the server's answer. */
export const helpBlock = `In this console:
  cd [path] (.. alone is cd ..), pwd, clear, exit
  Relative paths work here; the console turns them into absolute ones, and → shows the result.
  It only reads: restore, mv, mkdir, rm, mkcp and rmcp are refused, and the write tools aren't here.

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
};

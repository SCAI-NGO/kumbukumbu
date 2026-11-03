// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";
import { useSession } from "../state/session";

/** A piece of a command's answer, as the scrollback shows it. */
export type Output =
  | { kind: "text"; text: string; error?: boolean }
  | { kind: "image"; src: string; alt: string };

/** A command and what it answered. */
export interface Entry {
  id: number;
  /** The working directory it was typed in. */
  cwd: string;
  typed: string;
  /** What went to the server as `sh`; none for `cd` and the local commands. */
  sent?: string;
  output: Output[];
}

/** The command waiting for its answer: shown as typed until the answer makes it an entry. */
export type Running = Omit<Entry, "output">;

const HISTORY = 200;
const SCROLLBACK = 500;

interface ConsoleState {
  cwd: string;
  entries: Entry[];
  history: string[];
  running: Running | null;
  /** Raised by every reset: an answer to a command of an earlier generation is dropped. */
  generation: number;
  /** Records a typed line: not an empty one, and not twice in a row. */
  remember(line: string): void;
  /** Adds an answered command to the scrollback, which ends the running one. */
  add(entry: Entry): void;
  reset(): void;
}

const initial = { cwd: "/", entries: [], history: [], running: null };

/**
 * The console's directory, scrollback and history (console design §5): in memory only, outside the page so they
 * survive moving around in the app, gone on reload and when the session ends.
 */
export const useConsole = create<ConsoleState>()((set, get) => ({
  ...initial,
  generation: 0,
  remember: (line) => {
    const { history } = get();
    if (line.trim() === "" || history.at(-1) === line) return;
    set({ history: [...history, line].slice(-HISTORY) });
  },
  add: (entry) => set({ entries: [...get().entries, entry].slice(-SCROLLBACK), running: null }),
  reset: () => set({ ...initial, generation: get().generation + 1 }),
}));

// A sign-out, a session that ended, and one user replacing another directly (a sign-in over a session) all forget
// the console; only a first sign-in finds nothing to forget.
useSession.subscribe((session, previous) => {
  if (session.user?.id !== previous.user?.id && previous.user !== null) useConsole.getState().reset();
});

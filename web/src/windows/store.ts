// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";

export type WindowKind = "navigated" | "console" | "light";

/** A location of the app, as React Router has it. */
export type Behind = { pathname: string; search: string; hash: string; state: unknown };

/** The vault list's location: what a window reached without a location behind shows under it. */
export const ROOT: Behind = { pathname: "/", search: "", hash: "", state: null };

/**
 * The window over the notes, in memory only: the location the shell shows behind it (the last one that wasn't a
 * window), and whether that location is the history entry before the window's (reached in-app) or not (a deep link,
 * a reload, a vault hidden meanwhile).
 */
interface WindowsState {
  behind: Behind | null;
  inApp: boolean;
  /** Why the window mustn't be left unasked (a secret on show), or null. */
  lock: string | null;
}

export const useWindows = create<WindowsState>()(() => ({ behind: null, inApp: false, lock: null }));

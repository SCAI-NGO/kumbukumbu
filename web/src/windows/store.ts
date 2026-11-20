// SPDX-License-Identifier: AGPL-3.0-or-later
import { matchRoutes, type Location, type RouteObject } from "react-router-dom";
import { create } from "zustand";

export type WindowKind = "navigated" | "console" | "light";

/**
 * A location of the app, as React Router has it. Its key too: the note opened at a block or a line jumps there once per
 * key, and a window opening or closing must not make it jump again.
 */
export type Behind = Pick<Location, "pathname" | "search" | "hash" | "state" | "key">;

/** The vault list's location: what a window reached without a location behind shows under it. */
export const ROOT: Behind = { pathname: "/", search: "", hash: "", state: null, key: "behind" };

/** The vault list itself, not the note `/` would open on. */
export const OVERVIEW: Behind = { ...ROOT, state: { overview: true } };

/**
 * The window over the notes, in memory only: the location the shell shows behind it (the last one that wasn't a
 * window), and whether that location is the history entry before the window's (reached in-app) or not (a deep link,
 * a reload, a vault hidden meanwhile).
 */
interface WindowsState {
  /** A window is open: the router's location is a window's. */
  open: boolean;
  behind: Behind | null;
  inApp: boolean;
  /** Why the window mustn't be left unasked (a secret on show), or null. */
  lock: string | null;
  /** The phone's navigation pane is open: the content under it is out of reach. */
  navOpen: boolean;
}

export const useWindows = create<WindowsState>()(() => ({ open: false, behind: null, inApp: false, lock: null, navOpen: false }));

/** The app's routes, set by the router: whether a location is a window's, for code the router itself imports. */
let routes: RouteObject[] = [];
export const setWindowRoutes = (all: RouteObject[]) => void (routes = all);
export const isWindowLocation = (location: Pick<Location, "pathname">) =>
  !!matchRoutes(routes, location)?.some((m) => (m.route.handle as { window?: boolean } | undefined)?.window === true);

// SPDX-License-Identifier: AGPL-3.0-or-later
import { createContext, createElement, useCallback, useContext, useEffect, useLayoutEffect } from "react";
import { createPath, Navigate, parsePath, useMatches, useNavigate, type NavigateFunction } from "react-router-dom";
import { ROOT, useWindows } from "./store";

/** Set by the window host around a window's content. */
export const InWindow = createContext(false);

/** Whether this renders inside a window. */
export const useInWindow = () => useContext(InWindow);

/** Whether the location is a window's: its route carries `handle: { window: true }`. */
export function useWindowOpen(): boolean {
  return useMatches().some((m) => (m.handle as { window?: boolean } | undefined)?.window === true);
}

/**
 * Closes the window. Reached in-app, it is the history entry after the location behind, and Back to that is the
 * way out; reached otherwise (a deep link, a reload), there is nothing behind it in this app's history, and the window's
 * entry becomes the location behind: the vault list (or what that opens on), or where the shell was moved meanwhile.
 */
export function useCloseWindow(): () => void {
  const navigate = useNavigate();
  return useCallback(() => {
    const { inApp, behind } = useWindows.getState();
    if (inApp) void navigate(-1);
    else void navigate(behind ? createPath(behind) : "/", { replace: true, state: behind?.state });
  }, [navigate]);
}

/**
 * The shell's navigations that no click asked for (the sync socket following a vault renamed or taken away): behind
 * a window they move the location behind, and the window stays with what was typed into it. That location is no
 * longer the history entry before the window's, so closing replaces the window's entry with it.
 */
export function useShellNavigate(): NavigateFunction {
  const navigate = useNavigate();
  return useCallback(
    ((to, options) => {
      if (typeof to === "number" || !useWindows.getState().open) return navigate(to as never, options);
      const path = typeof to === "string" ? parsePath(to) : to;
      useWindows.setState({ behind: { ...ROOT, pathname: path.pathname ?? "/", search: path.search ?? "", hash: path.hash ?? "", state: options?.state ?? null }, inApp: false });
    }) as NavigateFunction,
    [navigate],
  );
}

/**
 * The shell's own redirects (to the kept note, to the note last shown): behind a window they move the location
 * behind instead of the window's address.
 */
export function ShellRedirect({ to }: { to: string }) {
  const open = useWindowOpen();
  useLayoutEffect(() => {
    if (open) useWindows.setState({ behind: { ...ROOT, pathname: to } });
  }, [open, to]);
  return open ? null : createElement(Navigate, { to, replace: true });
}

/** While `text` is set, the window's navigation is locked and shows it; the lock ends when the caller unmounts. Outside a window it does nothing. */
export function useWindowLock(text: string | null): void {
  const inWindow = useInWindow();
  useEffect(() => {
    if (!inWindow || text === null) return;
    useWindows.setState({ lock: text });
    return () => {
      // Only the lock this caller set: another's, set since, stays.
      if (useWindows.getState().lock === text) useWindows.setState({ lock: null });
    };
  }, [inWindow, text]);
}

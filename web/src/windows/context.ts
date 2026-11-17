// SPDX-License-Identifier: AGPL-3.0-or-later
import { createContext, createElement, useCallback, useContext, useEffect, useLayoutEffect } from "react";
import { Navigate, useMatches, useNavigate } from "react-router-dom";
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
 * entry becomes the vault list (or what that opens on).
 */
export function useCloseWindow(): () => void {
  const navigate = useNavigate();
  return useCallback(() => {
    if (useWindows.getState().inApp) void navigate(-1);
    else void navigate("/", { replace: true });
  }, [navigate]);
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
    return () => useWindows.setState({ lock: null });
  }, [inWindow, text]);
}

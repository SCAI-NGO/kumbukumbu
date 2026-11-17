// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useLayoutEffect } from "react";
import { Outlet, Route, Routes, useLocation } from "react-router-dom";
import { vaultOfRoute } from "../discreet/neutralWindow";
import { useDiscreet } from "../discreet/state";
import MobileShell from "../shell/MobileShell";
import { InWindow, useWindowOpen } from "./context";
import { ROOT, useWindows } from "./store";

/**
 * The notes and the windows over them (windows design, "The how"): the shell is rendered against the location behind,
 * so that it goes on showing the note (Write mode, a draft and all) while a window's address is the router's; the
 * window renders through the outlet.
 */
export default function WindowHost() {
  const open = useWindowOpen();
  const location = useLocation();
  const behind = useWindows((s) => s.behind);

  // Before the paint, so that the window opening finds the location it came from: a deep link or a reload has none,
  // and shows the window over `/`, the kept note or the vault list.
  useLayoutEffect(() => {
    if (!open) useWindows.setState({ behind: { pathname: location.pathname, search: location.search, hash: location.hash, state: location.state, key: location.key }, inApp: true });
    else if (!useWindows.getState().behind) useWindows.setState({ behind: ROOT, inApp: false });
  }, [open, location]);
  // Another page (the board, sign-in) leaves the shell: a window opened after it has nothing behind it in the shell.
  useEffect(() => () => useWindows.setState({ behind: null, inApp: false }), []);

  // A discreet vault hidden again under a window: the window's address names no vault, so hiding doesn't navigate.
  // The note behind gives way to the vault list, and the window stays, with what was typed into it.
  useEffect(
    () =>
      useDiscreet.subscribe((s, previous) => {
        if (s.gone === previous.gone) return;
        const vault = vaultOfRoute(useWindows.getState().behind?.pathname ?? "");
        if (vault !== null && s.gone.has(vault)) useWindows.setState({ behind: ROOT, inApp: false });
      }),
    [],
  );

  return (
    <>
      {/* `contents`: the shell lays out as if the wrapper weren't there. */}
      <div inert={open} className="contents">
        <Routes location={open ? (behind ?? ROOT) : location}>
          <Route path="*" element={<MobileShell />} />
        </Routes>
      </div>
      {open && (
        <InWindow value>
          <Outlet />
        </InWindow>
      )}
    </>
  );
}

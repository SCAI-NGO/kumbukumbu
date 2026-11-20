// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useLayoutEffect } from "react";
import { Outlet, Route, Routes, useLocation } from "react-router-dom";
import { besideOf, noteUrl } from "../api/paths";
import { besideVaultOf, vaultOfRoute } from "../discreet/neutralWindow";
import { useDiscreet } from "../discreet/state";
import MobileShell from "../shell/MobileShell";
import { InWindow, useWindowOpen } from "./context";
import { OVERVIEW, ROOT, useWindows } from "./store";

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
    if (!open) useWindows.setState({ open, behind: { pathname: location.pathname, search: location.search, hash: location.hash, state: location.state, key: location.key }, inApp: true });
    else if (!useWindows.getState().behind) useWindows.setState({ open, behind: ROOT, inApp: false });
    else useWindows.setState({ open });
  }, [open, location]);
  // Another page (the board, sign-in) leaves the shell: a window opened after it has nothing behind it in the shell.
  useEffect(() => () => useWindows.setState({ open: false, behind: null, inApp: false }), []);

  // A discreet vault hidden again under a window: the window's address names no vault, so hiding doesn't navigate.
  // The note behind gives way to the vault list (`OVERVIEW`), and the window stays, with what was typed into it. A
  // note of it in the split view's right pane goes from the address, and the window closes to the left one; with the
  // left one's vault hidden, it closes to the right one alone.
  useEffect(
    () =>
      useDiscreet.subscribe((s, previous) => {
        if (s.gone === previous.gone) return;
        const behind = useWindows.getState().behind;
        const vault = vaultOfRoute(behind?.pathname ?? "");
        const besideVault = behind && besideVaultOf(behind.pathname + behind.search);
        if (vault !== null && s.gone.has(vault)) {
          // A note beside it of a vault still shown stays, as the only pane.
          const beside = besideVault !== null && !s.gone.has(besideVault) ? besideOf(behind!.search) : null;
          useWindows.setState({ behind: beside ? { ...behind!, pathname: noteUrl(beside), search: "" } : OVERVIEW, inApp: false });
        } else if (behind && s.gone.has(besideVault ?? "")) {
          const search = new URLSearchParams(behind.search);
          search.delete("beside");
          useWindows.setState({ behind: { ...behind, search: search.size ? `?${search}` : "" }, inApp: false });
        }
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

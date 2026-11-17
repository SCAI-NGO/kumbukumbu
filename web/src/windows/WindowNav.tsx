// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronLeft, ChevronRight } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { NavLink, useLocation } from "react-router-dom";
import { useDesktop } from "../hooks/useDesktop";
import { useWindows } from "./store";

export type NavEntry = { to: string; label: string; /** The path prefix that marks the entry as the current one. */ match: string };

/**
 * A window's navigation: a 220 px column on the desktop; on a phone a slim tab at the left edge that slides a pane in
 * over the dimmed content. Moves replace the window's history entry. While the window is locked the entries stay put,
 * dimmed, under the reason.
 */
export default function WindowNav({ label, entries }: { label: string; entries: NavEntry[] }) {
  const desktop = useDesktop();
  const lock = useWindows((s) => s.lock);
  const { pathname } = useLocation();
  const [open, setOpen] = useState(false);
  const tab = useRef<HTMLButtonElement>(null);

  const fold = () => {
    setOpen(false);
    tab.current?.focus();
  };

  // Esc folds the pane only: caught before `useDialog` would close the window.
  useEffect(() => {
    if (desktop || !open) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      e.stopPropagation();
      e.preventDefault();
      fold();
    };
    document.addEventListener("keydown", onKeyDown, true);
    return () => document.removeEventListener("keydown", onKeyDown, true);
  }, [desktop, open]);

  useEffect(() => {
    if (lock !== null) setOpen(false);
  }, [lock]);

  const shown = !desktop && open;
  useEffect(() => {
    useWindows.setState({ navOpen: shown });
    return () => useWindows.setState({ navOpen: false });
  }, [shown]);

  const list = (
    <nav aria-label={label} className={desktop ? "flex h-full w-[220px] flex-none flex-col overflow-y-auto border-r border-line bg-surface-2 p-2" : "flex h-full w-3/4 flex-col overflow-y-auto border-r border-line bg-surface-2 p-2"}>
      {lock !== null && <p className="px-3 py-2 text-sm text-muted">{lock}</p>}
      {entries.map((e) => (
        <NavLink
          key={e.to}
          to={e.to}
          replace
          aria-current={pathname.startsWith(e.match) ? "page" : undefined}
          aria-disabled={lock !== null ? "true" : undefined}
          onClick={(ev) => {
            if (lock !== null) ev.preventDefault();
            else if (!desktop) fold();
          }}
          className={`flex min-h-12 items-center rounded-lg px-3 text-text focus-visible:outline-2 focus-visible:outline-accent ${pathname.startsWith(e.match) ? "bg-surface font-bold" : ""} ${lock !== null ? "opacity-50" : ""}`}
        >
          {e.label}
        </NavLink>
      ))}
    </nav>
  );

  if (desktop) return list;
  const Chevron = open ? ChevronLeft : ChevronRight;
  return (
    <>
      {open && (
        <>
          <div data-testid="nav-scrim" aria-hidden className="absolute inset-0 z-10 bg-black/40" onClick={fold} />
          <div className="absolute inset-y-0 left-0 z-20 w-3/4 motion-safe:animate-[k-slide-right_200ms_ease-out]">{list}</div>
        </>
      )}
      <button
        ref={tab}
        type="button"
        aria-label="Show navigation"
        aria-expanded={open}
        disabled={lock !== null}
        onClick={() => setOpen(!open)}
        className={`absolute top-1/2 z-30 flex h-[72px] w-12 -translate-y-1/2 items-center justify-center rounded-r-lg border border-l-0 border-line bg-surface-2 text-text focus-visible:outline-2 focus-visible:outline-accent disabled:text-muted disabled:opacity-50 ${open ? "left-[75%]" : "left-0"}`}
      >
        <Chevron size={20} aria-hidden />
      </button>
    </>
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useEffect, useState } from "react";

export const sidebarDefault = 318;
export const sidebarMin = 200;
export const sidebarStep = 16;
const absoluteMax = 560;
const storageKey = "kumbu.sidebarWidth";

/** The widest the sidebar may be: half the viewport, but never more than 560 px (and never below the minimum). */
export const sidebarMax = (viewport: number) => Math.max(sidebarMin, Math.min(absoluteMax, Math.floor(viewport / 2)));

const clamp = (width: number, viewport: number) => Math.round(Math.min(sidebarMax(viewport), Math.max(sidebarMin, width)));

// Local storage is a convenience: where it is blocked or full, the width simply isn't remembered.
function read(): number {
  try {
    const stored = Number(localStorage.getItem(storageKey));
    return Number.isFinite(stored) && stored > 0 ? stored : sidebarDefault;
  } catch {
    return sidebarDefault;
  }
}

function write(width: number | null) {
  try {
    if (width === null) localStorage.removeItem(storageKey);
    else localStorage.setItem(storageKey, String(width));
  } catch {
    // Not remembered; the width still holds for this visit.
  }
}

/**
 * The tree sidebar's width on the desktop. `set` takes a wanted width and clamps it; `persist` is false while a drag
 * is under way (the width is stored once, when it ends). The stored width is clamped again whenever the window changes.
 */
export function useSidebarWidth() {
  const [wanted, setWanted] = useState(read);
  const [viewport, setViewport] = useState(() => window.innerWidth);

  useEffect(() => {
    const onResize = () => setViewport(window.innerWidth);
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, []);

  const width = clamp(wanted, viewport);
  const set = useCallback(
    (next: number, persist = true) => {
      const clamped = clamp(next, window.innerWidth);
      setWanted(clamped);
      if (persist) write(clamped);
    },
    [],
  );
  const store = useCallback(() => write(clamp(wanted, window.innerWidth)), [wanted]);
  const reset = useCallback(() => {
    setWanted(sidebarDefault);
    write(null);
  }, []);

  return { width, min: sidebarMin, max: sidebarMax(viewport), set, store, reset };
}

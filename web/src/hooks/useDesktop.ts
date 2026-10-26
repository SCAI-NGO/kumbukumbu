// SPDX-License-Identifier: AGPL-3.0-or-later
import { useSyncExternalStore } from "react";

/** Tailwind's `lg` (64rem, 1024 px): from here on the tree sits beside the note instead of in a drawer over it. */
const query = "(min-width: 64rem)";

const media = () => {
  try {
    return window.matchMedia?.(query) ?? null;
  } catch {
    return null;
  }
};

function subscribe(onChange: () => void) {
  const list = media();
  list?.addEventListener("change", onChange);
  return () => list?.removeEventListener("change", onChange);
}

/** Whether the window is desktop-wide; without `matchMedia` it counts as a phone, the layout that works anywhere. */
export function useDesktop(): boolean {
  return useSyncExternalStore(subscribe, () => media()?.matches ?? false);
}

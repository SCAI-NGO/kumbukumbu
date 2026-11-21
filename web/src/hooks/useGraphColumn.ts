// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useState } from "react";

const collapsedKey = "kumbu.graphColumnCollapsed";

/** Whether the graph column (the widest windows) is collapsed, remembered per browser; it starts expanded, so it is found. */
export function useGraphColumnCollapsed() {
  // Local storage is a convenience, as for the side panel: where it is blocked, the column starts expanded.
  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem(collapsedKey) === "true";
    } catch {
      return false;
    }
  });
  const set = useCallback((next: boolean) => {
    setCollapsed(next);
    try {
      if (next) localStorage.setItem(collapsedKey, "true");
      else localStorage.removeItem(collapsedKey);
    } catch {
      // Not remembered; it still holds for this visit.
    }
  }, []);
  return [collapsed, set] as const;
}

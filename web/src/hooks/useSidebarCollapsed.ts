// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useState } from "react";

const storageKey = "kumbu.sidebarCollapsed";

// Local storage is a convenience, as for the width: where it is blocked, the sidebar simply starts expanded.
function read(): boolean {
  try {
    return localStorage.getItem(storageKey) === "true";
  } catch {
    return false;
  }
}

function write(collapsed: boolean) {
  try {
    if (collapsed) localStorage.setItem(storageKey, "true");
    else localStorage.removeItem(storageKey);
  } catch {
    // Not remembered; it still holds for this visit.
  }
}

/** Whether the docked sidebar (desktop widths) is collapsed, remembered per browser. */
export function useSidebarCollapsed() {
  const [collapsed, setCollapsed] = useState(read);
  const set = useCallback((next: boolean) => {
    setCollapsed(next);
    write(next);
  }, []);
  return [collapsed, set] as const;
}

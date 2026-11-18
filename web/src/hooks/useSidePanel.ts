// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useState } from "react";

const collapsedKey = "kumbu.sidePanelCollapsed";
const tabKey = "kumbu.sidePanelTab";

export type SidePanelTab = "links" | "outline";

// Local storage is a convenience, as for the tree: where it is blocked, the panel starts expanded, on Links.
function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    // Not remembered; it still holds for this visit.
  }
}

/** Whether the side panel (wide windows) is collapsed, remembered per browser; it starts expanded, so it is found. */
export function useSidePanelCollapsed() {
  const [collapsed, setCollapsed] = useState(() => read(collapsedKey) === "true");
  const set = useCallback((next: boolean) => {
    setCollapsed(next);
    write(collapsedKey, next ? "true" : null);
  }, []);
  return [collapsed, set] as const;
}

/** The side panel's tab last chosen, remembered per browser. */
export function useSidePanelTab() {
  const [tab, setTab] = useState<SidePanelTab>(() => (read(tabKey) === "outline" ? "outline" : "links"));
  const set = useCallback((next: SidePanelTab) => {
    setTab(next);
    write(tabKey, next === "links" ? null : next);
  }, []);
  return [tab, set] as const;
}

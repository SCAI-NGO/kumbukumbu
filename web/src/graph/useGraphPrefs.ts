// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import type { GraphFilters } from "./api";
import type { SizeBy } from "./model";

/** The column's toolbar choices, remembered per browser. */
export type GraphPrefs = GraphFilters & { sizeBy: SizeBy; preview: boolean; suggestions: boolean; list: boolean };

const prefsKey = "kumbu.graphPrefs";

/** As the backend's: depth 1, everything shown, size by links, preview on, suggestions off. */
const defaults: GraphPrefs = { depth: 1, days: true, attachments: true, ghosts: true, sizeBy: "links", preview: true, suggestions: false, list: false };

const flags = ["days", "attachments", "ghosts", "preview", "suggestions", "list"] as const;

// Local storage is a convenience: what can't be read, or isn't a valid value, is the default, field by field.
function stored(): GraphPrefs {
  let raw: unknown;
  try {
    raw = JSON.parse(localStorage.getItem(prefsKey) ?? "null");
  } catch {
    return defaults;
  }
  if (typeof raw !== "object" || raw === null) return defaults;
  const s = raw as Record<string, unknown>;
  const prefs = { ...defaults };
  if (s.depth === 1 || s.depth === 2 || s.depth === 3) prefs.depth = s.depth;
  if (s.sizeBy === "links" || s.sizeBy === "backlinks" || s.sizeBy === "length") prefs.sizeBy = s.sizeBy;
  for (const f of flags) if (typeof s[f] === "boolean") prefs[f] = s[f];
  return prefs;
}

export function useGraphPrefs(): [GraphPrefs, (change: Partial<GraphPrefs>) => void] {
  const [prefs, setPrefs] = useState(stored);
  // Functional, so two changes in one tick both land; the write is the same however often React calls it.
  const change = (c: Partial<GraphPrefs>) =>
    setPrefs((prefs) => {
      const next = { ...prefs, ...c };
      try {
        localStorage.setItem(prefsKey, JSON.stringify(next));
      } catch {
        // Not remembered; the choice still holds for this visit.
      }
      return next;
    });
  return [prefs, change];
}

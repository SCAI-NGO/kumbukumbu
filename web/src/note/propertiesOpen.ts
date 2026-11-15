// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useState } from "react";

const storageKey = "kumbu.propertiesOpen";

function stored(): boolean {
  try {
    return localStorage.getItem(storageKey) === "open";
  } catch {
    return false;
  }
}

/**
 * Whether the properties box of a note whose header says it in words is open: one choice per device for all such
 * notes, folded unless chosen. Storage may be blocked, so every access is guarded.
 */
export function useStoredOpen(): [boolean, (open: boolean) => void] {
  const [open, setOpen] = useState(stored);
  const set = useCallback((next: boolean) => {
    setOpen(next);
    try {
      if (next) localStorage.setItem(storageKey, "open");
      else localStorage.removeItem(storageKey);
    } catch {
      // Not kept: the choice holds until the note is left.
    }
  }, []);
  return [open, set];
}

// SPDX-License-Identifier: AGPL-3.0-or-later

const storageKey = "kumbu.autoRefresh";

/**
 * "Refresh Read mode automatically": whether Read mode takes a version written elsewhere by itself. Off unless
 * chosen, kept per device like the colour scheme (no secret, nothing of a note); storage may be blocked, so every
 * access is guarded.
 */
export function storedAutoRefresh(): boolean {
  try {
    return localStorage.getItem(storageKey) === "on";
  } catch {
    return false;
  }
}

export function setAutoRefresh(on: boolean): void {
  try {
    if (on) localStorage.setItem(storageKey, "on");
    else localStorage.removeItem(storageKey);
  } catch {
    // Not kept: Read mode goes on asking with its bar.
  }
}

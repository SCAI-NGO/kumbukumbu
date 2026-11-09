// SPDX-License-Identifier: AGPL-3.0-or-later

const storageKey = "kumbu.discreetTimeout";

/** How long the PIN shows the discreet vaults: minutes, or until the session ends or hides them. */
export type DiscreetTimeout = "5" | "10" | "30" | "60" | "never";

export const timeouts: readonly DiscreetTimeout[] = ["5", "10", "30", "60", "never"];

const fallback: DiscreetTimeout = "10";

/** What the reveal request takes: the minutes, or null for Never. */
export const minutesOf = (timeout: DiscreetTimeout): number | null => (timeout === "never" ? null : Number(timeout));

/** This device's last choice. Local storage is a convenience: where it is blocked, or holds anything else, 10 minutes. */
export function readTimeout(): DiscreetTimeout {
  try {
    const stored = localStorage.getItem(storageKey);
    return timeouts.find((t) => t === stored) ?? fallback;
  } catch {
    return fallback;
  }
}

export function writeTimeout(timeout: DiscreetTimeout) {
  try {
    localStorage.setItem(storageKey, timeout);
  } catch {
    // Not remembered; the next dialog starts at 10 minutes again.
  }
}

// SPDX-License-Identifier: AGPL-3.0-or-later
export type ConsoleScheme = "plain" | "green" | "amber";

export const consoleSchemes: readonly ConsoleScheme[] = ["plain", "green", "amber"];

const storageKey = "kumbu.consoleScheme";

/**
 * Phosphor text, ground and dimmed text (path, prompt). Contrast against the ground, by the WCAG formula:
 * green text 14.9:1, dimmed 7.3:1; amber text 11.0:1, dimmed 5.5:1. Both dimmed colours clear AA's 4.5:1.
 */
export const phosphor = {
  green: { fg: "#33ff66", bg: "#030a05", dim: "#22b348" },
  amber: { fg: "#ffb000", bg: "#0a0600", dim: "#b37b00" },
} as const;

// Local storage is a convenience: where it is blocked, the console simply starts plain.
export function storedConsoleScheme(): ConsoleScheme {
  try {
    const stored = localStorage.getItem(storageKey);
    return stored === "green" || stored === "amber" ? stored : "plain";
  } catch {
    return "plain";
  }
}

export function keepConsoleScheme(scheme: ConsoleScheme) {
  try {
    localStorage.setItem(storageKey, scheme);
  } catch {
    // Not remembered; it still holds for this visit.
  }
}

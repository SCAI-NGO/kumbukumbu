// SPDX-License-Identifier: AGPL-3.0-or-later
import { defaultSchemeFor, schemes } from "./schemes";

const storageKey = "kumbu.scheme";

/** The user's choice, if they made one; storage may be blocked, so every access is guarded. */
export function storedScheme(): string | null {
  try {
    const id = localStorage.getItem(storageKey);
    return schemes.some((s) => s.id === id) ? id : null;
  } catch {
    return null;
  }
}

/** Sets the scheme on <html>; a choice is kept (the scheme name only), `null` goes back to following the system. */
export function applyScheme(id: string | null): void {
  try {
    if (id === null) localStorage.removeItem(storageKey);
    else localStorage.setItem(storageKey, id);
  } catch {
    // The scheme still applies for this visit.
  }
  const effective = id ?? defaultSchemeFor(window.matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.dataset.scheme = effective;
}

export function initScheme(): void {
  const chosen = storedScheme();
  applyScheme(chosen);
  // Without a choice, follow the system when it changes.
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    if (storedScheme() === null) applyScheme(null);
  });
}

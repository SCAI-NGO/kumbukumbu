// SPDX-License-Identifier: AGPL-3.0-or-later
import { useSyncExternalStore } from "react";

/** A hook answering whether the window matches `query`, following it; without `matchMedia` it answers false. */
export function mediaQueryHook(query: string): () => boolean {
  const media = () => {
    try {
      return window.matchMedia?.(query) ?? null;
    } catch {
      return null;
    }
  };
  const subscribe = (onChange: () => void) => {
    const list = media();
    list?.addEventListener("change", onChange);
    return () => list?.removeEventListener("change", onChange);
  };
  const matches = () => media()?.matches ?? false;
  return () => useSyncExternalStore(subscribe, matches);
}

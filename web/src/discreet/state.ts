// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";
import type { components } from "../api/schema";

export type DiscreetView = components["schemas"]["DiscreetView"];

/**
 * The session's discreet mode as the server last told it, and the names of the discreet vaults it shows: in memory
 * only, never in browser storage. The names come from the vault list, which carries them only while the mode is on.
 */
interface DiscreetState {
  revealed: boolean;
  /** When the mode ends; null for Never (or while hidden). */
  until: string | null;
  names: ReadonlySet<string>;
  setFromSession(view: DiscreetView | undefined): void;
  setFromVaults(vaults: readonly { name: string; discreet?: boolean }[]): void;
  reset(): void;
}

const initial = { revealed: false, until: null, names: new Set<string>() };

export const useDiscreet = create<DiscreetState>()((set) => ({
  ...initial,
  // An older server's session has no `discreet`: nothing is shown.
  setFromSession: (view) => set({ revealed: !!view?.revealed, until: view?.revealed ? view.until : null }),
  setFromVaults: (vaults) => set({ names: new Set(vaults.filter((v) => v.discreet).map((v) => v.name)) }),
  reset: () => set({ ...initial, names: new Set() }),
}));

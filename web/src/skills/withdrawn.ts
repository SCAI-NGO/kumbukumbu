// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";

/**
 * The skills whose link she withdrew in this tab, by user and folder: sharing one of them again warns first that copies
 * made from the old link lose their updates (Decision 15). In memory only; the server keeps no such list.
 */
interface WithdrawnState {
  keys: string[];
  add(user: string, path: string): void;
  reset(): void;
}

export const withdrawnKey = (user: string, path: string) => `${user}\u0000${path}`;

export const useWithdrawn = create<WithdrawnState>()((set) => ({
  keys: [],
  add: (user, path) => set((s) => ({ keys: [...s.keys, withdrawnKey(user, path)] })),
  reset: () => set({ keys: [] }),
}));

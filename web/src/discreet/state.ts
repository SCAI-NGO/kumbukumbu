// SPDX-License-Identifier: AGPL-3.0-or-later
import type { QueryClient } from "@tanstack/react-query";
import { create } from "zustand";
import type { components } from "../api/schema";
import { useSession } from "../state/session";

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

export const useDiscreet = create<DiscreetState>()((set, get) => ({
  ...initial,
  // An older server's session has no `discreet`: nothing is shown.
  setFromSession: (view) => set({ revealed: !!view?.revealed, until: view?.revealed ? view.until : null }),
  setFromVaults: (vaults) => {
    const names = vaults.filter((v) => v.discreet).map((v) => v.name);
    const { names: now } = get();
    if (names.length === now.size && names.every((n) => now.has(n))) return;
    set({ names: new Set(names) });
  },
  reset: () => set({ ...initial, names: new Set() }),
}));

/**
 * The names follow the vault list wherever it comes from (a read, `setQueryData`, a removal, `clear`). Returns the
 * call that stops it.
 */
export function followVaults(queryClient: QueryClient): () => void {
  const read = () => useDiscreet.getState().setFromVaults(queryClient.getQueryData<{ name: string; discreet?: boolean }[]>(["vaults"]) ?? []);
  read();
  return queryClient.getQueryCache().subscribe((event) => {
    const key = event.query.queryKey;
    if (key.length !== 1 || key[0] !== "vaults") return;
    if (event.type === "removed" || (event.type === "updated" && event.action.type === "success")) read();
  });
}

// As the console: a sign-out, a session that ended, and one user replacing another forget the mode.
useSession.subscribe((session, previous) => {
  if (session.user?.id !== previous.user?.id && previous.user !== null) useDiscreet.getState().reset();
});

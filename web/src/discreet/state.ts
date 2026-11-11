// SPDX-License-Identifier: AGPL-3.0-or-later
import type { QueryClient } from "@tanstack/react-query";
import { create } from "zustand";
import { vaultOf } from "../api/paths";
import type { components } from "../api/schema";
import { useEditor } from "../state/editor";
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
  /** While this tab hides them again: the views of the discreet vaults are closing, none opens meanwhile. */
  hiding: boolean;
  /** This tab hid them, and the server hasn't heard of it yet (offline): until it has, a session saying shown is not followed. */
  unsent: boolean;
  /**
   * The vaults this tab hid again, in memory only and never shown. A view of one that renders once more before it
   * goes (its route still names it until the navigation is in) shows nothing of it. A vault the list names again,
   * shown or ordinary, is no longer gone.
   */
  gone: ReadonlySet<string>;
  setFromSession(view: DiscreetView | undefined): void;
  setFromVaults(vaults: readonly { name: string; discreet?: boolean }[]): void;
  reset(): void;
}

const initial = { revealed: false, until: null, names: new Set<string>() };

export const useDiscreet = create<DiscreetState>()((set, get) => ({
  ...initial,
  hiding: false,
  unsent: false,
  gone: new Set<string>(),
  // An older server's session has no `discreet`: nothing is shown.
  setFromSession: (view) => set({ revealed: !!view?.revealed, until: view?.revealed ? view.until : null }),
  setFromVaults: (vaults) => {
    const names = vaults.filter((v) => v.discreet).map((v) => v.name);
    const { names: now, gone } = get();
    // Listed as ordinary (made so, or an ordinary vault of that name later), its notes render again.
    const ordinary = vaults.filter((v) => !v.discreet && gone.has(v.name)).map((v) => v.name);
    if (ordinary.length > 0) set({ gone: new Set([...gone].filter((n) => !ordinary.includes(n))) });
    if (names.length === now.size && names.every((n) => now.has(n))) return;
    set({ names: new Set(names) });
  },
  // `hiding`, `unsent` and `gone` outlast it: hiding again resets on its way.
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

// As the console: a sign-out, a session that ended, and one user replacing another forget the mode. The next session
// starts with the discreet vaults hidden, so nothing the tab kept of them opens again in it.
useSession.subscribe((session, previous) => {
  if (session.user?.id !== previous.user?.id && previous.user !== null) {
    useEditor.getState().forgetShown(useDiscreet.getState().names);
    useDiscreet.getState().reset();
    useDiscreet.setState({ unsent: false, gone: new Set() });
  }
});

/** Whether `path` is in a vault this tab hid again and that isn't shown now: its view shows nothing of it. */
export const useGoneWithVaults = (path: string | null): boolean => useDiscreet((s) => path !== null && goneVault(s, vaultOf(path)));

/** Whether `vault` is one this tab hid again and that isn't shown now: nothing of it may be on screen. */
export const goneVault = (s: Pick<DiscreetState, "gone" | "names">, vault: string | null | undefined): boolean => !!vault && s.gone.has(vault) && !s.names.has(vault);

// SPDX-License-Identifier: AGPL-3.0-or-later
import type { QueryClient } from "@tanstack/react-query";
import { vaultOf } from "../api/paths";
import { useDiscreet } from "./state";

/** Whether a query's key names one of `names`, as a vault (`Tagebuch`) or by a path in it (`/Tagebuch/a.md`). */
export const mentions = (key: readonly unknown[], names: ReadonlySet<string>) =>
  key.some((k) => typeof k === "string" && (names.has(k) || (k.startsWith("/") && names.has(vaultOf(k)))));

/**
 * A view of a discreet vault that renders once more before it goes (its route hasn't changed yet) asks for its
 * queries again: for a while, such a query is dropped as soon as nothing observes it, and a mutation that was under way
 * (a save, a mark) is dropped when it settles. Until the vaults are shown again, if they are meanwhile.
 */
export function sweep(queryClient: QueryClient, names: ReadonlySet<string>) {
  const cache = queryClient.getQueryCache();
  const mutations = queryClient.getMutationCache();
  const forget = () => {
    for (const q of cache.findAll({ predicate: (q) => mentions(q.queryKey, names) })) if (q.getObserversCount() === 0) cache.remove(q);
  };
  const stops = [
    cache.subscribe((event) => {
      if ((event.type === "added" || event.type === "observerRemoved" || event.type === "updated") && mentions(event.query.queryKey, names)) queueMicrotask(forget);
    }),
    mutations.subscribe((event) => {
      if (event.type === "updated" && (event.action.type === "success" || event.action.type === "error")) queueMicrotask(() => mutations.remove(event.mutation));
    }),
    useDiscreet.subscribe((s) => void (s.revealed && stop())),
  ];
  const timer = setTimeout(stop, 10_000);
  function stop() {
    clearTimeout(timer);
    for (const s of stops) s();
  }
  forget();
}

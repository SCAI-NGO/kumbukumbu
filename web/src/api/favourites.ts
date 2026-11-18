// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call } from "./client";
import type { components } from "./schema";

export type Favourite = components["schemas"]["FavouriteView"];
export type FavouriteInput = components["schemas"]["FavouriteInput"];

const key = ["favourites"];

/** `command:<id>`, `<kind>:<vault>` for a vault, board, calendar or gallery, `note:<vault>/<path>`. */
export const favouriteKey = (f: FavouriteInput): string =>
  f.kind === "command" ? `command:${f.id}` : f.kind === "note" ? `note:${f.vault}/${f.path}` : `${f.kind}:${f.vault}`;

const input = ({ kind, id, vault, path }: Favourite): FavouriteInput => ({ kind, ...(id ? { id } : {}), ...(vault ? { vault } : {}), ...(path ? { path } : {}) });

/**
 * Her favourites in the order added, those of the vaults her session lists and the commands. A change sends the whole
 * list (the server replaces what the session sees), shown at once and put back if the write fails. Until the list has
 * been read (`ready`), a change does nothing: it would replace what the server holds with one entry.
 */
export function useFavourites() {
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: key, queryFn: async () => (await call(api.GET("/api/account/favourites"))).favourites });
  const list = query.data ?? [];

  const write = useMutation({
    mutationKey: key,
    // One at a time, in the order made: the last write is the one that stands.
    scope: { id: "favourites" },
    mutationFn: ({ next }: { next: Favourite[] }) => call(api.PUT("/api/account/favourites", { body: { favourites: next.map(input) } })),
    // Only the last write of a run says what the list is: an earlier answer would show a state already left.
    onSuccess: (view) => {
      if (queryClient.isMutating({ mutationKey: key }) <= 1 && queryClient.getQueryData(key) !== undefined) queryClient.setQueryData(key, view.favourites);
    },
    // After a failure the server is the truth, not what this tab last showed: read it again once no write is pending.
    onError: () => {
      if (queryClient.isMutating({ mutationKey: key }) <= 1) void queryClient.invalidateQueries({ queryKey: key });
    },
  });

  // From the cache, not the render: a second change straight after the first sees it.
  const change = (to: (now: Favourite[]) => Favourite[]) => {
    const before = queryClient.getQueryData<Favourite[]>(key);
    if (!before) return;
    const next = to(before);
    void queryClient.cancelQueries({ queryKey: key });
    queryClient.setQueryData(key, next);
    write.mutate({ next });
  };

  const keys = new Set(list.map(favouriteKey));
  return {
    list,
    ready: query.data !== undefined,
    has: (f: FavouriteInput) => keys.has(favouriteKey(f)),
    /** Adds or removes `f`; in the same write, the entries keyed `stale` go (note favourites whose note is gone). */
    toggle: (f: FavouriteInput, stale: string[] = []) =>
      change((all) => {
        const now = all.filter((x) => !stale.includes(favouriteKey(x)));
        return now.some((x) => favouriteKey(x) === favouriteKey(f)) ? now.filter((x) => favouriteKey(x) !== favouriteKey(f)) : [...now, { ...f, added: new Date().toISOString() }];
      }),
    drop: (drop: string[]) => change((now) => now.filter((x) => !drop.includes(favouriteKey(x)))),
  };
}

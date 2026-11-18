// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call, HttpError } from "./client";
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
 * list (the server replaces what the session sees), shown at once and put back if the write fails.
 */
export function useFavourites() {
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: key, queryFn: async () => (await call(api.GET("/api/account/favourites"))).favourites });
  const list = query.data ?? [];

  const write = useMutation({
    mutationFn: (next: Favourite[]) => call(api.PUT("/api/account/favourites", { body: { favourites: next.map(input) } })),
    onMutate: async (next) => {
      await queryClient.cancelQueries({ queryKey: key });
      const before = queryClient.getQueryData<Favourite[]>(key);
      queryClient.setQueryData(key, next);
      return { before };
    },
    onSuccess: (view) => queryClient.setQueryData(key, view.favourites),
    onError: (error, _next, context) => {
      queryClient.setQueryData(key, context?.before);
      // Someone else wrote meanwhile: what the list is now is for the server to say.
      if (error instanceof HttpError && error.status === 409) void queryClient.invalidateQueries({ queryKey: key });
    },
  });

  const keys = new Set(list.map(favouriteKey));
  return {
    list,
    has: (f: FavouriteInput) => keys.has(favouriteKey(f)),
    toggle: (f: FavouriteInput) =>
      write.mutate(keys.has(favouriteKey(f)) ? list.filter((x) => favouriteKey(x) !== favouriteKey(f)) : [...list, { ...f, added: new Date().toISOString() }]),
    drop: (drop: string[]) => write.mutate(list.filter((x) => !drop.includes(favouriteKey(x)))),
  };
}

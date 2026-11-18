// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { api, call, HttpError } from "./client";
import { errorText } from "./error-text";
import type { components } from "./schema";

export type Favourite = components["schemas"]["FavouriteView"];
export type FavouriteInput = components["schemas"]["FavouriteInput"];
type View = components["schemas"]["FavouritesView"];
/** A change to the list, made again to the list as it is when its write is sent: so it gives the same list made twice. */
type Change = (now: Favourite[]) => Favourite[];

const key = ["favourites"];
const read = async () => call(api.GET("/api/account/favourites"));

// How often the list was read afresh for other vaults (`forgetFavourites`): the answer to a write sent before is a list no longer shown.
let resets = 0;

/** Forgets the favourites, as the vaults the session lists changed: those answered are the listed vaults' ones. */
export function forgetFavourites(queryClient: QueryClient) {
  resets++;
  void queryClient.resetQueries({ queryKey: key });
}

/** `command:<id>`, `<kind>:<vault>` for a vault, board, calendar or gallery, `note:<vault>/<path>`. */
export const favouriteKey = (f: FavouriteInput): string =>
  f.kind === "command" ? `command:${f.id}` : f.kind === "note" ? `note:${f.vault}/${f.path}` : `${f.kind}:${f.vault}`;

const input = ({ kind, id, vault, path }: Favourite): FavouriteInput => ({ kind, ...(id ? { id } : {}), ...(vault ? { vault } : {}), ...(path ? { path } : {}) });

/**
 * Her favourites in the order added, those of the vaults her session lists and the commands. A change sends the whole
 * list (the server replaces what the session sees), shown at once, with the version of the list it was made from; when
 * another device wrote since (409), the list is read again and the change made to it once more. After a failed write the
 * list is read again, and `error` says why until the next change. Until the list has been read (`ready`), a change does
 * nothing: it would replace what the server holds with one entry.
 */
export function useFavourites() {
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: key, queryFn: read });
  const list = query.data?.favourites ?? [];

  const write = useMutation({
    mutationKey: key,
    // One at a time, in the order made: the last write is the one that stands.
    scope: { id: "favourites" },
    mutationFn: async ({ change }: { change: Change; made: number }) => {
      const generation = resets;
      const put = (from: View) => call(api.PUT("/api/account/favourites", { body: { favourites: change(from.favourites).map(input), version: from.version } }));
      // As the cache has it when sent: the version the write before answered, and every change made since. Forgotten
      // meanwhile (the vaults changed), it was made to a list no longer shown: nothing is sent.
      const now = queryClient.getQueryData<View>(key);
      if (!now) return null;
      try {
        return { generation, view: await put(now) };
      } catch (e) {
        if (!(e instanceof HttpError && e.status === 409)) throw e;
        const fresh = await queryClient.fetchQuery({ queryKey: key, queryFn: read, staleTime: 0 });
        // Forgotten while read again: the change was made to a list no longer shown.
        if (generation !== resets) return null;
        queryClient.setQueryData<View>(key, { ...fresh, favourites: change(fresh.favourites) });
        return { generation, view: await put(fresh) };
      }
    },
    // Only the last write of a run says what the list is: an earlier answer would show a state already left, but its
    // version is the one the next write is made over.
    onSuccess: (sent) => {
      if (!sent || sent.generation !== resets || queryClient.getQueryData(key) === undefined) return;
      const { view } = sent;
      if (queryClient.isMutating({ mutationKey: key }) <= 1) queryClient.setQueryData(key, view);
      else queryClient.setQueryData<View>(key, (old) => old && { ...old, version: view.version });
    },
    // After a failure the server is the truth, not what this tab last showed: read it again once no write is pending.
    onError: () => {
      if (queryClient.isMutating({ mutationKey: key }) <= 1) void queryClient.invalidateQueries({ queryKey: key });
    },
  });

  // From the cache, not the render: a second change straight after the first sees it.
  const change = (to: Change) => {
    const before = queryClient.getQueryData<View>(key);
    if (!before) return;
    void queryClient.cancelQueries({ queryKey: key });
    queryClient.setQueryData<View>(key, { ...before, favourites: to(before.favourites) });
    write.mutate({ change: to, made: resets });
  };

  const keys = new Set(list.map(favouriteKey));
  return {
    list,
    ready: query.data !== undefined,
    // Not of a change made before the vaults changed: its sentence may name a vault now hidden.
    error: write.error && write.variables?.made === resets ? errorText(write.error) : null,
    has: (f: FavouriteInput) => keys.has(favouriteKey(f)),
    /** Adds or removes `f`, as it is now; in the same write, the entries keyed `stale` go (note favourites whose note is gone). */
    toggle: (f: FavouriteInput, stale: string[] = []) => {
      const at = favouriteKey(f);
      const on = !queryClient.getQueryData<View>(key)?.favourites.some((x) => favouriteKey(x) === at);
      change((all) => {
        const now = all.filter((x) => !stale.includes(favouriteKey(x)));
        if (!on) return now.filter((x) => favouriteKey(x) !== at);
        return now.some((x) => favouriteKey(x) === at) ? now : [...now, { ...f, added: new Date().toISOString() }];
      });
    },
  };
}

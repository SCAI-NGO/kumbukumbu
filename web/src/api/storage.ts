// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { useSession } from "../state/session";
import { api, call } from "./client";
import { sessionQuery } from "./publications";
import type { components } from "./schema";

export type AccountStorage = components["schemas"]["AccountStorageView"];

/**
 * "512 B", "1.5 KB", "181.7 MB", "182 MB", "2 GB": a storage figure, binary as the server takes `KUMBU_QUOTA_DEFAULT`
 * (`2GB` is 2 GiB), with one decimal that a whole number leaves out.
 */
export function storageText(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  const [value, unit] = bytes < 1024 ** 2 ? [bytes / 1024, "KB"] : bytes < 1024 ** 3 ? [bytes / 1024 ** 2, "MB"] : [bytes / 1024 ** 3, "GB"];
  return `${value.toFixed(1).replace(/\.0$/, "")} ${unit}`;
}

/** How full the storage is, in whole percent, rounded down (so 90 % is reached, not approached); null without a limit. */
export function percentUsed(storage: AccountStorage): number | null {
  if (storage.limit == null) return null;
  const limit = Number(storage.limit);
  return limit <= 0 ? 100 : Math.floor((Number(storage.used) / limit) * 100);
}

// The signed-in user's limit as last asked, for the quota refusal's sentence (errorText can't wait for a request).
let known: { user: string; limit: number | null } | null = null;

/** Keeps `user`'s limit for `quotaSentence`; the storage query does this with every answer. */
export function rememberStorage(user: string, storage: AccountStorage) {
  known = { user, limit: storage.limit == null ? null : Number(storage.limit) };
}

/** What the owner is told of her full storage (WebDAV design §3.10), with her limit when it is known. */
export const fullSentence = (limit: number | null) =>
  `Your storage is full${limit != null ? ` (${storageText(limit)})` : ""}. Empty the trash or ask your admin for more.`;

/** The owner's sentence for a write refused for her full storage, with the limit last asked for the signed-in user. */
export const quotaSentence = () => fullSentence(known && known.user === useSession.getState().user?.id ? known.limit : null);

/** Whether a storage limit applies to the signed-in user, as the session says (no count of her storage); undefined until
 * the session is in. */
export const useStorageLimited = (): boolean | undefined => useQuery({ ...sessionQuery, select: (s) => s.storageLimited === true }).data;

/** What the session's own vaults take, and her limit (Settings, the shell's notice). The server counts it at most every
 * 30 seconds, reading every document of her vaults: the shell's notice asks with `enabled` only when a limit applies,
 * and no more than once a minute. */
export const useAccountStorage = (options: { enabled?: boolean; staleTime?: number; refetchOnWindowFocus?: boolean } = {}) =>
  useQuery({
    ...options,
    queryKey: ["account", "storage"],
    queryFn: async () => {
      const storage = await call(api.GET("/api/account/storage"));
      const user = useSession.getState().user?.id;
      if (user) rememberStorage(user, storage);
      return storage;
    },
  });

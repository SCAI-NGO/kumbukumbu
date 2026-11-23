// SPDX-License-Identifier: AGPL-3.0-or-later
// The app shell's kept files, checked before they are served. Any page of the origin can write the worker's cache
// (CacheStorage is the origin's, not the worker's), so a file kept there is served only while its bytes are the ones
// the build listed; the list lives in the worker's own script, which the browser keeps and compares with the
// server's, out of any page's reach.

/** SHA-256 of `bytes`, in hex, as the build writes it into the worker (vite.config.ts). */
export async function sha256(bytes: BufferSource): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("");
}

/** One build's cache, opened, by path. */
export interface Kept {
  match(path: string): Promise<Response | undefined>;
  put(path: string, response: Response): Promise<void>;
  delete(path: string): Promise<boolean>;
}

const isBuilds = async (response: Response, expected: string) =>
  response.status === 200 && (await sha256(await response.clone().arrayBuffer())) === expected;

/** The kept answer for `path` if it is the build's; one that isn't is deleted, so nothing serves it again. */
async function checked(cache: Kept, path: string, expected: string): Promise<Response | undefined> {
  const kept = await cache.match(path);
  if (kept === undefined) return undefined;
  if (await isBuilds(kept, expected)) return kept;
  await cache.delete(path);
  return undefined;
}

/** A file of the build: kept, if it is the build's; else the network's, which is kept again when it is. */
export async function asset(cache: Kept, path: string, expected: string, fetchIt: () => Promise<Response>): Promise<Response> {
  const kept = await checked(cache, path, expected);
  if (kept !== undefined) return kept;
  const fresh = await fetchIt();
  if (await isBuilds(fresh, expected)) await cache.put(path, fresh.clone());
  return fresh;
}

/** The kept app page, for a start without a network, if it is the build's. */
export const keptPage = (cache: Kept, expected: string): Promise<Response | undefined> => checked(cache, "/index.html", expected);

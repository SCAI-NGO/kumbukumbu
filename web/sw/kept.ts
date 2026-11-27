// SPDX-License-Identifier: AGPL-3.0-or-later
// The app shell's kept files, checked before they are served. Any page of the origin can write the worker's cache
// (CacheStorage is the origin's, not the worker's), so a file kept there is served only while its bytes are the ones
// the build listed; the list lives in the worker's own script, which the browser keeps and compares with the
// server's, out of any page's reach. The worker answers with those bytes and headers of its own, never with the
// response it found in the cache.
import { appPolicy, isKdfWorker, kdfWorkerPolicy } from "./policy";

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

/** A file as the build lists it (vite.config.ts): its SHA-256 and the content type the server gives it. */
export interface Listed {
  sha256: string;
  type: string;
}

const isBuilds = async (status: number, bytes: ArrayBuffer, file: Listed) => status === 200 && (await sha256(bytes)) === file.sha256;

/**
 * The worker's own answer for `path`: the checked bytes, with the headers the server gives a file of the app
 * (Api/Security.cs), never those of the answer they came in. A new response has no URL, so the request's is the
 * base its relative imports resolve against.
 */
function answer(path: string, bytes: ArrayBuffer, file: Listed): Response {
  return new Response(bytes, {
    status: 200,
    headers: {
      "Content-Type": file.type,
      "X-Content-Type-Options": "nosniff",
      "Referrer-Policy": "same-origin",
      "Content-Security-Policy": isKdfWorker(path) ? kdfWorkerPolicy : appPolicy,
    },
  });
}

/** The kept answer for `path` if it is the build's; one that isn't is deleted, so nothing serves it again. */
async function checked(cache: Kept, path: string, file: Listed): Promise<Response | undefined> {
  const kept = await cache.match(path);
  if (kept === undefined) return undefined;
  const bytes = await kept.arrayBuffer();
  if (await isBuilds(kept.status, bytes, file)) return answer(path, bytes, file);
  await cache.delete(path);
  return undefined;
}

/** A file of the build: kept, if it is the build's; else the network's, which is kept again when it is. */
export async function asset(cache: Kept, path: string, file: Listed, fetchIt: () => Promise<Response>): Promise<Response> {
  const kept = await checked(cache, path, file);
  if (kept !== undefined) return kept;
  const fresh = await fetchIt();
  const bytes = await fresh.clone().arrayBuffer();
  if (!(await isBuilds(fresh.status, bytes, file))) return fresh;
  await cache.put(path, answer(path, bytes, file));
  return answer(path, bytes, file);
}

/** The kept app page, for a start without a network, if it is the build's. */
export const keptPage = (cache: Kept, file: Listed): Promise<Response | undefined> => checked(cache, "/index.html", file);

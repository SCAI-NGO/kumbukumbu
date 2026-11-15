// SPDX-License-Identifier: AGPL-3.0-or-later
import { vaultOf } from "../api/paths";

/**
 * The view a note was opened from, which its header leads back to (spec §2.3.1); `tag`, the filter What's on keeps on
 * the page only while discreet vaults are shown, which the address doesn't carry.
 */
export type CameFrom = { label: "Calendar" | "What's on"; to: string; tag?: string };

// Browsers keep the history's state across a reload, but the way back is for this visit only: a state written by an
// earlier page load is refused.
const load = typeof crypto !== "undefined" && typeof crypto.randomUUID === "function" ? crypto.randomUUID() : `${Math.random()}`.slice(2);

const calendarTo = /^\/calendar\/([^/?#]+)(?:[?#]|$)/;
const whatsOnTo = /^\/whats-on(?:[?#]|$)/;

/** A vault's week or month (with its `?tag=` and anchor) or What's on, with the tag `kept` on its page; anywhere else, none. */
export function cameFromHere({ pathname, search, hash }: { pathname: string; search: string; hash: string }, kept?: string): CameFrom | null {
  const to = pathname + search + hash;
  if (calendarTo.test(to)) return { label: "Calendar", to };
  if (whatsOnTo.test(to)) return kept ? { label: "What's on", to, tag: kept } : { label: "What's on", to };
  return null;
}

/** The router state `state` with the way back joined to it, never replacing what it holds. */
export function withCameFrom(state: unknown, from: CameFrom | null): unknown {
  if (!from) return state;
  const base = typeof state === "object" && state !== null ? state : {};
  return { ...base, cameFrom: { ...from, load } };
}

const decoded = (part: string) => {
  try {
    return decodeURIComponent(part);
  } catch {
    return null;
  }
};

/** The way back `state` carries for the note at `path`: only one this page load wrote, to its own vault's calendar or What's on. */
export function cameFromOf(state: unknown, path: string): CameFrom | null {
  const from = typeof state === "object" && state !== null ? (state as { cameFrom?: unknown }).cameFrom : undefined;
  if (typeof from !== "object" || from === null) return null;
  const { label, to, tag, load: written } = from as { label?: unknown; to?: unknown; tag?: unknown; load?: unknown };
  if (written !== load || typeof to !== "string" || (tag !== undefined && typeof tag !== "string")) return null;
  const calendar = calendarTo.exec(to);
  if (label === "Calendar" && calendar && decoded(calendar[1]) === vaultOf(path)) return { label, to };
  if (label === "What's on" && whatsOnTo.test(to)) return tag ? { label, to, tag } : { label, to };
  return null;
}

/** The router state ‹ What's on goes back with: the tag the page kept, for this page load only. */
export const keptTagState = (tag: string) => ({ keptTag: { tag, load } });

/** The tag What's on is to take up as it opens, from `keptTagState`; none from an earlier page load. */
export function keptTagOf(state: unknown): string | undefined {
  const kept = typeof state === "object" && state !== null ? (state as { keptTag?: unknown }).keptTag : undefined;
  if (typeof kept !== "object" || kept === null) return undefined;
  const { tag, load: written } = kept as { tag?: unknown; load?: unknown };
  return written === load && typeof tag === "string" && tag ? tag : undefined;
}

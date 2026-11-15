// SPDX-License-Identifier: AGPL-3.0-or-later
import { vaultOf } from "../api/paths";

/** The view a note was opened from, which its header leads back to (spec §2.3.1). */
export type CameFrom = { label: "Calendar" | "What's on"; to: string };

// Browsers keep the history's state across a reload, but the way back is for this visit only: a state written by an
// earlier page load is refused.
const load = typeof crypto !== "undefined" && typeof crypto.randomUUID === "function" ? crypto.randomUUID() : `${Math.random()}`.slice(2);

const calendarTo = /^\/calendar\/([^/?#]+)(?:[?#]|$)/;
const whatsOnTo = /^\/whats-on(?:[?#]|$)/;

/** A vault's week or month (with its `?tag=` and anchor) or What's on; anywhere else, none. */
export function cameFromHere({ pathname, search, hash }: { pathname: string; search: string; hash: string }): CameFrom | null {
  const to = pathname + search + hash;
  if (calendarTo.test(to)) return { label: "Calendar", to };
  if (whatsOnTo.test(to)) return { label: "What's on", to };
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
  const { label, to, load: written } = from as { label?: unknown; to?: unknown; load?: unknown };
  if (written !== load || typeof to !== "string") return null;
  const calendar = calendarTo.exec(to);
  if (label === "Calendar" && calendar && decoded(calendar[1]) === vaultOf(path)) return { label, to };
  if (label === "What's on" && whatsOnTo.test(to)) return { label, to };
  return null;
}

// SPDX-License-Identifier: AGPL-3.0-or-later
// The decision table of spec §6 as a pure function of three versions.
import type { Version } from "./ports";

export type Action =
  | { kind: "nothing" }
  | { kind: "upload"; base: Version | null } // base null: create
  | { kind: "download" }
  | { kind: "trashLocal" } // remote gone, local unchanged
  | { kind: "recordBase"; version: Version }
  | { kind: "conflict" }
  | { kind: "deleteRemote"; base: Version }
  | { kind: "reupload" }; // remote gone, local changed: create anew

/** `local`/`remote` null means absent. `remoteDamagedPossible`: a full list with `damaged > 0`. */
export interface Sides { local: Version | null; remote: Version | null; base: Version | null; remoteDamagedPossible: boolean; }

export function decide({ local, remote, base, remoteDamagedPossible }: Sides): Action {
  if (base === null) {
    if (local === null) return remote === null ? { kind: "nothing" } : { kind: "download" };
    if (remote === null) return { kind: "upload", base: null };
    return local === remote ? { kind: "recordBase", version: local } : { kind: "conflict" };
  }

  if (local === null) {
    if (remote === null) return { kind: "nothing" };
    return remote === base ? { kind: "deleteRemote", base } : { kind: "download" };
  }

  if (remote === null) {
    if (local !== base) return { kind: "reupload" };
    // A missing note may be one of the damaged ones, so absence alone trashes nothing.
    return remoteDamagedPossible ? { kind: "nothing" } : { kind: "trashLocal" };
  }

  const localChanged = local !== base;
  const remoteChanged = remote !== base;
  if (!localChanged && !remoteChanged) return { kind: "nothing" };
  if (localChanged && !remoteChanged) return { kind: "upload", base };
  if (!localChanged) return { kind: "download" };
  return local === remote ? { kind: "recordBase", version: local } : { kind: "conflict" };
}

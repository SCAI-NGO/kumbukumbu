// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";

/** A note's key in `editors`: the vault (the caller's name for it) and the path inside it, as the socket names them. */
export const presenceKey = (vault: string, path: string) => `${vault}\n${path}`;

interface SyncState {
  /** Whether this tab's sync socket is open: while it is, its hints stand in for the open note's version poll. */
  connected: boolean;
  /** Who else is editing which note, as the server shows them to this tab (collab design §9.2); while connected only. */
  editors: Record<string, string[]>;
  /** Sends a frame on the open socket; false without one. */
  send(frame: unknown): boolean;
}

export const useSyncStatus = create<SyncState>(() => ({ connected: false, editors: {}, send: () => false }));

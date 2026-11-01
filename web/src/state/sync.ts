// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";

/** Whether this tab's sync socket is open: while it is, its hints stand in for the open note's version poll. */
export const useSyncStatus = create<{ connected: boolean }>(() => ({ connected: false }));

// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline copy this tab has open: its header, and the unlocked mirror while the key is in memory.
import type { CopyHeader, SealedMirror } from "eidolon-core/store/mirror";
import { create } from "zustand";

interface OfflineCopyState {
  header: CopyHeader | null;
  mirror: SealedMirror | null;
  set(header: CopyHeader, mirror: SealedMirror | null): void;
  clear(): void;
}

export const useOfflineCopy = create<OfflineCopyState>()((set) => ({
  header: null,
  mirror: null,
  set: (header, mirror) => set({ header, mirror }),
  clear: () => set({ header: null, mirror: null }),
}));

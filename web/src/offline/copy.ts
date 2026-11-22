// SPDX-License-Identifier: AGPL-3.0-or-later
// This tab's offline copy (offline mode design §3a, §4.7): its header, known as soon as the copy is found, and the
// unlocked mirror while the key is in this tab's memory. Set where a copy is made or unlocked, cleared on its removal,
// a wipe or the lock.
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

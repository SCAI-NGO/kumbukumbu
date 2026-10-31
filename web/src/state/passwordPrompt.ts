// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";

/** The dialog that asks a remembered session for the password again, before a guarded request goes on. */
interface PasswordPromptState {
  open: boolean;
  /** Opens the dialog; every request that asks while it is open waits for the same answer. True: confirmed. */
  ask(): Promise<boolean>;
  /** Answers every waiting request and closes the dialog. */
  settle(confirmed: boolean): void;
}

let pending: { promise: Promise<boolean>; resolve(confirmed: boolean): void } | null = null;

export const usePasswordPrompt = create<PasswordPromptState>()((set) => ({
  open: false,
  ask: () => {
    if (!pending) {
      let resolve!: (confirmed: boolean) => void;
      const promise = new Promise<boolean>((r) => (resolve = r));
      pending = { promise, resolve };
      set({ open: true });
    }
    return pending.promise;
  },
  settle: (confirmed) => {
    const waiting = pending;
    pending = null;
    set({ open: false });
    waiting?.resolve(confirmed);
  },
}));

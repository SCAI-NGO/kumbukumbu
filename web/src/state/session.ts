// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";
import type { components } from "../api/schema";

export type SessionView = components["schemas"]["SessionView"];
export type User = components["schemas"]["UserView"];

/** What a session's end does to the offline copy this tab holds: locked here, locked in every tab, or removed. */
export type CopyEnd = "lock" | "lockEverywhere" | "wipe";

// Set by the offline mode (offline/mode.ts), which imports this store: the copy's life in the tab follows the session's,
// so the next person at the browser never finds it open.
let endCopy: (how: CopyEnd) => void = () => {};
export function onSessionEnd(fn: (how: CopyEnd) => void): void {
  endCopy = fn;
}

/** Who is signed in, in memory only: the session itself lives in the HttpOnly cookie. */
interface SessionState {
  user: User | null;
  previousSignInAt: string | null;
  /** Set by a 401: the app goes to sign-in. */
  ended: boolean;
  /** One short sentence for the sign-in screen. */
  notice: string | null;
  /** Whose session ended in this tab (a 401), until the next sign-in: someone else then starts afresh. */
  previousUser: string | null;
  /** Signed out on purpose in this tab, until the next sign-in: a 401 then is that sign-out, not a lost session. */
  signedOut: boolean;
  /** What the store keeps of a session view: the join answers without the window flag. */
  signedIn(view: Pick<SessionView, "user" | "previousSignInAt">): void;
  /** The user signed out: everything is forgotten, and the requests of the page being left may answer 401. The copy locks in every tab. */
  signOut(): void;
  /**
   * `notice` replaces the usual "your session has ended" for an end the user caused, such as a new password. The copy
   * locks in this tab; `copy` "wipe" removes it (the account is gone).
   */
  end(notice?: string, copy?: CopyEnd): void;
  reset(): void;
}

const initial = { user: null, previousSignInAt: null, ended: false, notice: null, previousUser: null, signedOut: false };

export const useSession = create<SessionState>()((set, get) => ({
  ...initial,
  signedIn: (view) => set({ user: view.user, previousSignInAt: view.previousSignInAt, ended: false, notice: null, previousUser: null, signedOut: false }),
  signOut: () => {
    endCopy("lockEverywhere");
    set({ ...initial, signedOut: true });
  },
  // Only a session that was running in this tab "has ended"; a first visit just needs to sign in.
  end: (notice, copy = "lock") => {
    endCopy(copy);
    set({
      ...initial,
      ended: true,
      notice: notice ?? (get().user ? "Your session has ended. Please sign in again." : get().notice),
      previousUser: get().user?.username ?? get().previousUser,
    });
  },
  reset: () => set(initial),
}));

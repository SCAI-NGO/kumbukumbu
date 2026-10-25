// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";
import type { components } from "../api/schema";

export type SessionView = components["schemas"]["SessionView"];
export type User = components["schemas"]["UserView"];

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
  signedIn(view: SessionView): void;
  /** `notice` replaces the usual "your session has ended" for an end the user caused, such as a new password. */
  end(notice?: string): void;
  reset(): void;
}

const initial = { user: null, previousSignInAt: null, ended: false, notice: null, previousUser: null };

export const useSession = create<SessionState>()((set, get) => ({
  ...initial,
  signedIn: (view) => set({ user: view.user, previousSignInAt: view.previousSignInAt, ended: false, notice: null, previousUser: null }),
  // Only a session that was running in this tab "has ended"; a first visit just needs to sign in.
  end: (notice) =>
    set({
      ...initial,
      ended: true,
      notice: notice ?? (get().user ? "Your session has ended. Please sign in again." : get().notice),
      previousUser: get().user?.username ?? get().previousUser,
    }),
  reset: () => set(initial),
}));

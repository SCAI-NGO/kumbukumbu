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
  signedIn(view: SessionView): void;
  end(): void;
  reset(): void;
}

const initial = { user: null, previousSignInAt: null, ended: false, notice: null };

export const useSession = create<SessionState>()((set, get) => ({
  ...initial,
  signedIn: (view) => set({ user: view.user, previousSignInAt: view.previousSignInAt, ended: false, notice: null }),
  // Only a session that was running in this tab "has ended"; a first visit just needs to sign in.
  end: () =>
    set({ ...initial, ended: true, notice: get().user ? "Your session has ended. Please sign in again." : get().notice }),
  reset: () => set(initial),
}));

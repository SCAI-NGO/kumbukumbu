// SPDX-License-Identifier: AGPL-3.0-or-later
import { create } from "zustand";
import { api } from "../api/client";
import { appVersion, newerOnServer, type Version } from "./version";

interface UpdateState {
  /** The server's `X-Kumbu-Version`, as last heard. */
  server: string | null;
  /** The server's build when it differs from this app's: the app reloads into it at a safe moment. */
  available: Version | null;
  /** The reload into `available` happened, yet this is still the old app (a cache in the way): not tried again. */
  failed: Version | null;
  /** The failure the user put away (`0.2.0+a6362ec`): not shown again in this tab. */
  dismissed: string | null;
  /** The semver this tab just reloaded into, for a short notice. */
  updated: string | null;
  /** A response's header; null (an older server, a test double) leaves everything as it was. */
  report(server: string | null): void;
  fail(v: Version): void;
  dismissFailed(): void;
  markUpdated(semver: string | null): void;
}

export const useUpdate = create<UpdateState>()((set, get) => ({
  server: null,
  available: null,
  failed: null,
  dismissed: null,
  updated: null,
  // Every API response reports: the same version again changes nothing, so nothing re-renders.
  report: (server) => {
    if (!server || server === get().server) return;
    // The server runs this app's own build again (a deploy that settled, a rollback): nothing failed any more.
    set({ server, available: newerOnServer(appVersion, server), ...(server === appVersion ? { failed: null } : {}) });
  },
  fail: (v) => {
    if (get().dismissed !== `${v.semver}+${v.sha}`) set({ failed: v });
  },
  dismissFailed: () => {
    const { failed } = get();
    if (failed) set({ failed: null, dismissed: `${failed.semver}+${failed.sha}` });
  },
  markUpdated: (semver) => set({ updated: semver }),
}));

/** Asks the server for its version; the header does the reporting. A failed check says nothing about versions. */
export async function checkVersion(): Promise<void> {
  try {
    await api.GET("/api/version");
  } catch {
    // Offline, or the server is restarting: the next response or visibility change asks again.
  }
}

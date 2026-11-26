// SPDX-License-Identifier: AGPL-3.0-or-later
// The sync engines of this tab's unlocked copy (offline mode design §3, §6), over fetch and the session cookie. They run
// while the copy is open and this tab leads it; the app hears their conflicts, lost sessions and changes waiting.
import { KumbuApi, type HttpFn } from "eidolon-core/api/client";
import { AuthError, type ListedVault } from "eidolon-core/engine/ports";
import type { OfflineChoice } from "eidolon-core/store/choice";
import { MirrorEngines } from "eidolon-core/store/engines";
import type { SealedMirror } from "eidolon-core/store/mirror";
import { api, call } from "../api/client";
import { useOfflineCopy } from "./copy";
import { useSession, type SessionView } from "../state/session";
import { deviceLabel } from "./deviceLabel";
import { mirrorCopies, openCopy } from "./mode";
import { nameVaults } from "./source";
import { draftsFor } from "./writing";

export interface EngineEvents {
  conflict(vaultId: string, original: string, copy: string): void;
  sessionLost(): void;
  waitingChanged(): void;
}

const listeners = new Set<EngineEvents>();

export function listenToEngines(events: EngineEvents): () => void {
  listeners.add(events);
  return () => void listeners.delete(events);
}

/** The engines' HTTP: same origin, the session cookie with it. */
export const mirrorHttp: HttpFn = async (r) => {
  const response = await fetch(r.url, { method: r.method, headers: r.headers, body: r.body, credentials: "same-origin" });
  const headers: Record<string, string> = {};
  response.headers.forEach((value, key) => (headers[key] = value));
  const bytes = await response.arrayBuffer();
  return { status: response.status, headers, text: new TextDecoder().decode(bytes), bytes };
};

let reading: Promise<SessionView | null> | null = null;

/**
 * Reads the session and holds it against the copy this tab has open: the cookie is the browser's, and after an
 * offline sign-in (the tab knows the username only) it may still be someone else's. The copy's own user's: the tab's
 * session is that one from now on, and the view comes back. Another person's: the copy locks, its changes stay sealed,
 * and the tab goes to sign-in. Null for that, and when Kumbu² can't be reached or there is no copy open.
 */
export function confirmSession(): Promise<SessionView | null> {
  reading ??= (async () => {
    try {
      const mirror = useOfflineCopy.getState().mirror;
      if (mirror === null || mirror.locked) return null;
      const view = await call(api.GET("/api/session"));
      // As the slot port compares them: by the hash of the user id in the copy's header.
      if ((await (await mirrorCopies()).forUser(view.user.id))?.copy !== mirror.header.copy) {
        // The session's end locks the copy.
        useSession.getState().end();
        return null;
      }
      if (useSession.getState().user?.id === "") useSession.getState().signedIn(view);
      return view;
    } catch {
      // Unreachable, or a 401, which the API client has dealt with.
      return null;
    }
  })().finally(() => (reading = null));
  return reading;
}

async function listVaults(): Promise<ListedVault[]> {
  if ((await confirmSession()) === null) throw new AuthError("This offline copy isn't the signed-in user's.");
  return (await call(api.GET("/api/vaults"))).map((v) => ({
    id: v.id, name: v.name, canWrite: v.canWrite, shared: v.shared, level: v.level, discreet: v.discreet,
  }));
}

let running: { mirror: SealedMirror; engines: MirrorEngines } | null = null;

function follow(mirror: SealedMirror | null): void {
  if (running?.mirror === mirror) return;
  void running?.engines.stop();
  running = null;
  if (mirror === null || mirror.locked) return;
  const engines = new MirrorEngines({
    mirror, device: deviceLabel(), listVaults,
    // The open editors are the engine's drafts while offline writes are on (§4.9); the vault keeps the caller's name (§4.8).
    files: (vault) => vault.files(draftsFor(vault.id)),
    adopted: (ref) => nameVaults(mirror, [ref]),
    remote: (name) => new KumbuApi(mirrorHttp, window.location.origin, name, null),
    clock: { now: () => Date.now(), setTimeout: (fn, ms) => setTimeout(fn, ms), clearTimeout: (h) => clearTimeout(h as number) },
    onStatus: () => listeners.forEach((l) => l.waitingChanged()),
    onConflict: (vault, original, copy) => listeners.forEach((l) => l.conflict(vault, original, copy)),
    onAuthLost: () => listeners.forEach((l) => l.sessionLost()),
  });
  running = { mirror, engines };
  void engines.start().catch((e: unknown) => console.warn("The offline copy's sync couldn't start.", e));
}

/**
 * Once, at the app's start: opens this user's copy when signed in (with the slot's key, nothing to ask), and starts and
 * stops the engines with the copy this tab has open.
 */
export function runEngines(): () => void {
  const opening = (user: { id: string } | null) => {
    if (user !== null) void openCopy(user.id).catch(() => {});
  };
  opening(useSession.getState().user);
  const stopOpening = useSession.subscribe((s, before) => {
    if (s.user?.id !== before.user?.id) opening(s.user);
  });
  follow(useOfflineCopy.getState().mirror);
  const stopFollowing = useOfflineCopy.subscribe((s) => follow(s.mirror));
  return () => {
    stopOpening();
    stopFollowing();
  };
}

/** A socket "changed" frame, naming the vault as the caller sees it. */
export function enginesHinted(vaultName: string): void {
  running?.engines.hinted(vaultName);
}

/** A path of a vault was written in this tab, offline: its engine uploads it (another tab's write comes over the copy). */
export function enginesLocalChanged(vaultId: string, path: string): void {
  running?.engines.localChanged(vaultId, path);
}

/** The socket is (back) up: the vault list and the engines are brought in line. */
export function enginesReconnected(): void {
  void running?.engines.refresh().catch(() => {});
}

/** An attachment opened online: kept on this device within the budget (§6). Nothing without an open copy. */
export function openedOnline(vaultId: string, path: string): void {
  void running?.engines.opened(vaultId, path).catch(() => {});
}

/**
 * Changes what this device keeps offline. The tab that runs the engines applies it at once; the others hear of it
 * through the copy (another tab's engines apply it there).
 */
export async function chooseOffline(mirror: SealedMirror, change: (c: OfflineChoice) => OfflineChoice): Promise<void> {
  await mirror.choose(change);
  if (running?.mirror === mirror) await running.engines.refresh(true);
}

// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline mode in this app (offline mode design §3, §3a): what the tab sees goes into the Eidolon core's mode,
// shared by every tab of the browser, and the state comes back here for the bar and for the screens that read from
// the copy. Contacts with the mirror slot happen here too: at sign-in, on every socket (re)connect and on each
// `mirror` frame (§4.6).
import { AuthError, OfflineError } from "eidolon-core/engine/ports";
import { initialMode, type Conflict, type ModeInput, type ModeState } from "eidolon-core/mode/mode";
import { SharedMode, type ModeChannel, type ModePorts } from "eidolon-core/mode/shared";
import { IDLE_MS, MirrorCopies, REMEMBERED_IDLE_MS, type SealedMirror } from "eidolon-core/store/mirror";
import { create } from "zustand";
import { useSession } from "../state/session";
import { useOfflineCopy } from "./copy";
import { browserPorts, locks } from "./ports";

/** The sign-in page's sentence after a wipe (§3a "Purges and discreet vaults"). */
export const WIPED = "The offline copy on this device was removed.";

interface OfflineModeState extends ModeState {
  /** A contact purged vaults: how many changes went with them. The notice names no vault (§3a). */
  purged: { lost: number } | null;
  failed(): void;
  reached(): void;
  socket(up: boolean): void;
  /** The changes waiting may have changed: read again from the unlocked copy. */
  waitingChanged(): void;
  sessionLost(): void;
  signedIn(): void;
  conflict(c: Conflict): void;
  /** The done line or the conflicts were seen. */
  seen(): void;
  dismissPurged(): void;
}

const tab = crypto.randomUUID();

// Without Web Locks (the tests) this tab leads on its own.
const hasLocks = typeof navigator !== "undefined" && navigator.locks !== undefined;

function modeChannel(): ModeChannel {
  if (typeof BroadcastChannel === "undefined") return { post() {}, listen: () => () => {} };
  const bc = new BroadcastChannel("kumbu-mode");
  return {
    post: (m) => bc.postMessage(m),
    listen(fn) {
      const heard = (e: MessageEvent) => fn(e.data as Parameters<typeof fn>[0]);
      bc.addEventListener("message", heard);
      return () => bc.removeEventListener("message", heard);
    },
  };
}

const ports: ModePorts = {
  locks: hasLocks ? locks : { run: (_n, fn) => fn(), hold: () => ({ acquired: Promise.resolve(), release() {} }) },
  channel: modeChannel(),
  clock: { now: () => Date.now(), setTimeout: (fn, ms) => setTimeout(fn, ms), clearTimeout: (h) => clearTimeout(h as number) },
};

let shared: SharedMode | null = null;
/** This tab sent a failure and nothing has reached Kumbu² since: only then is "reached" worth telling. */
let failing = false;

function send(input: ModeInput) {
  shared ??= new SharedMode(ports, tab, (s) => useOfflineMode.setState(s));
  shared.send(input);
}

export const useOfflineMode = create<OfflineModeState>()((set) => ({
  ...initialMode,
  purged: null,
  failed: () => {
    failing = true;
    send({ type: "failed" });
  },
  reached: () => {
    if (!failing) return;
    failing = false;
    send({ type: "reached" });
  },
  socket: (up) => send({ type: "socket", tab, up }),
  waitingChanged: () => {
    const mirror = useOfflineCopy.getState().mirror;
    if (mirror !== null && !mirror.locked) send({ type: "waiting", count: mirror.waiting() });
  },
  sessionLost: () => send({ type: "sessionLost" }),
  signedIn: () => send({ type: "signedIn" }),
  conflict: (conflict) => send({ type: "conflict", conflict }),
  seen: () => send({ type: "seen" }),
  dismissPurged: () => set({ purged: null }),
}));

if (typeof window !== "undefined") {
  // A hint only: the browser knows it has no network at all; it never says the server is reachable.
  window.addEventListener("offline", () => useOfflineMode.getState().failed());
  window.addEventListener("pagehide", () => shared?.stop());
}

const contactedListeners = new Set<(mirror: SealedMirror) => void>();

/** Told after every contact that went through, with the copy it unlocked or kept in line. */
export function onContacted(fn: (mirror: SealedMirror) => void): () => void {
  contactedListeners.add(fn);
  return () => void contactedListeners.delete(fn);
}

let copies: Promise<MirrorCopies> | null = null;
const mirrorCopies = () => (copies ??= browserPorts().then((p) => new MirrorCopies(p)));

/** The copy went: this tab forgets it, and the sign-in page says so. */
function wiped() {
  useOfflineCopy.getState().clear();
  useSession.getState().end(WIPED);
}

function contacted(mirror: SealedMirror) {
  useOfflineMode.getState().waitingChanged();
  for (const fn of [...contactedListeners]) fn(mirror);
}

let opening: Promise<void> | null = null;

/**
 * Opens this user's copy with the slot's key, which is the contact at sign-in: on a page load signed in, and after
 * the sign-in form, which also brings the password for the offline wrap (§4.2) and the idle limit. Nothing to do
 * where this browser keeps no copy of the user's.
 */
export function openCopy(userId: string, signIn?: { username: string; password: string; remember: boolean }): Promise<void> {
  // No IndexedDB (a private window of some browsers, the tests): no copy can be here.
  if (useOfflineCopy.getState().mirror !== null || typeof indexedDB === "undefined") return Promise.resolve();
  opening ??= (async () => {
    try {
      const all = await mirrorCopies();
      const header = await all.forUser(userId);
      if (header === null) return;
      const opened = await all.unlockOnline(header, signIn && {
        username: signIn.username, password: signIn.password, idleMs: signIn.remember ? REMEMBERED_IDLE_MS : IDLE_MS,
      });
      if (opened === "wiped") return wiped();
      opened.onPurged = (vault) => {
        if (vault === null) wiped();
        else useOfflineMode.getState().waitingChanged();
      };
      opened.onChanged = () => useOfflineMode.getState().waitingChanged();
      useOfflineCopy.getState().set(opened.header, opened);
      contacted(opened);
    } catch (e) {
      // Offline or signed out meanwhile: the next sign-in or reconnect opens it.
      if (!(e instanceof OfflineError || e instanceof AuthError)) console.error("Kumbu² offline copy:", e);
    } finally {
      opening = null;
    }
  })();
  return opening;
}

/** Makes contact with the unlocked copy's slot: on every socket (re)connect and each `mirror` frame. */
export async function contact(): Promise<void> {
  const mirror = useOfflineCopy.getState().mirror;
  if (mirror === null || mirror.locked) return;
  try {
    const result = await mirror.contact();
    if (result === "wiped") return wiped();
    if (result.vaults > 0) useOfflineMode.setState({ purged: { lost: result.lost } });
    contacted(mirror);
  } catch (e) {
    if (!(e instanceof OfflineError || e instanceof AuthError)) console.error("Kumbu² offline copy:", e);
  }
}

/** A 401 that said `mirror-wipe` (the request handed in the slot's token): the copy goes from this device. */
export async function wipeCopy(): Promise<void> {
  const header = useOfflineCopy.getState().header;
  useOfflineCopy.getState().mirror?.lock();
  if (header !== null) await (await mirrorCopies()).wipe(header.copy);
  wiped();
}

/** For the tests: a fresh mode, as in a new tab. */
export function resetOfflineMode() {
  shared?.stop();
  shared = null;
  failing = false;
  opening = null;
  copies = null;
  useOfflineMode.setState({ ...initialMode, purged: null });
}

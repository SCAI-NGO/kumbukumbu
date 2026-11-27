// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline mode in this app (offline mode design §3, §3a): what the tab sees goes into the Eidolon core's mode,
// shared by every tab of the browser, and the state comes back here for the bar and for the screens that read from
// the copy. Contacts with the mirror slot happen here too: at sign-in, on every socket (re)connect and on each
// `mirror` frame (§4.6).
import { AuthError, OfflineError } from "eidolon-core/engine/ports";
import { initialMode, type Conflict, type ModeInput, type ModeState } from "eidolon-core/mode/mode";
import { SharedMode, type ModeChannel, type ModePorts } from "eidolon-core/mode/shared";
import { IdleLock } from "eidolon-core/store/idle";
import { IDLE_MS, MirrorCopies, REMEMBERED_IDLE_MS, type SealedMirror } from "eidolon-core/store/mirror";
import { create } from "zustand";
import { onSessionEnd, useSession } from "../state/session";
import { useOfflineCopy } from "./copy";
import { browserPorts, keepThroughCleanUps, locks } from "./ports";

/** The sign-in page's sentence after a wipe (§3a "Purges and discreet vaults"). */
export const WIPED = "The offline copy on this device was removed.";

interface OfflineModeState extends ModeState {
  /** Vaults' copies were removed (SealedMirror.listenRemoved): how many changes went with them. The notice names no vault (§3a). */
  purged: { lost: number } | null;
  failed(): void;
  /** The heartbeat got no answer: a failed request, and no tab's socket counts as up until one opens again. */
  lost(): void;
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
  lost: () => {
    failing = true;
    send({ type: "lost" });
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

/** Whether the app is offline: what needs a connection is dimmed then, with `NEEDS_CONNECTION` as its reason (§3a). */
export const useOffline = () => useOfflineMode((s) => s.offline);

/** The reason beside what doesn't work offline: a control's title, a menu item's second line (§3a). */
export const NEEDS_CONNECTION = "Needs a connection";

if (typeof window !== "undefined") {
  // A hint only: the browser knows it has no network at all; it never says the server is reachable.
  window.addEventListener("offline", () => useOfflineMode.getState().failed());
  // Started without a network (the installed app opened offline, at the sign-in page even, which asks nothing of the
  // server): the same hint, at once.
  if (navigator.onLine === false) queueMicrotask(() => useOfflineMode.getState().failed());
  window.addEventListener("pagehide", () => shared?.stop());
}

/** Resolves once nothing waits to upload, or after `ms`, whichever comes first. */
export function uploaded(ms: number): Promise<void> {
  if (useOfflineMode.getState().waiting === 0) return Promise.resolve();
  return new Promise((resolve) => {
    const timer = setTimeout(done, ms);
    const stop = useOfflineMode.subscribe((s) => s.waiting === 0 && done());
    function done() {
      clearTimeout(timer);
      stop();
      resolve();
    }
  });
}

const contactedListeners = new Set<(mirror: SealedMirror) => void>();

/** Told after every contact that went through, with the copy it unlocked or kept in line. */
export function onContacted(fn: (mirror: SealedMirror) => void): () => void {
  contactedListeners.add(fn);
  return () => void contactedListeners.delete(fn);
}

let copies: Promise<MirrorCopies> | null = null;
export const mirrorCopies = () => (copies ??= browserPorts().then((p) => new MirrorCopies(p)));

let sessionKept = 0;

/**
 * While held, a copy found gone leaves this tab's session as it is: this session's own re-encryption removes every
 * slot, this browser's too, and keeps the session, whose new recovery key is on screen until it is seen. A 401 that
 * wipes the copy still ends the session: the server's is gone then. Answers the release.
 */
export function keepSessionThroughWipe(): () => void {
  sessionKept++;
  let released = false;
  return () => {
    if (!released) sessionKept--;
    released = true;
  };
}

/**
 * The copy went: this tab forgets it, and the sign-in page says so. `sessionGone`: the server ended the session too,
 * which no hold keeps.
 */
function wiped(sessionGone = false) {
  idle?.stop();
  idle = null;
  useOfflineCopy.getState().mirror?.lock();
  useOfflineCopy.getState().clear();
  if (sessionGone || sessionKept === 0) useSession.getState().end(WIPED);
}

const beforeLock = new Set<() => void | Promise<void>>();
/** How long the hooks before a lock may take: a save that hangs mustn't keep the copy open. */
const BEFORE_LOCK_MS = 3_000;

/** Runs before the copy locks: the editor writes its unsaved text into the copy then. */
export function onBeforeLock(fn: () => void | Promise<void>): () => void {
  beforeLock.add(fn);
  return () => void beforeLock.delete(fn);
}

/** Drops the key from this tab: unsaved editor text goes into the copy first; changes waiting stay sealed. */
export async function lockCopy(everywhere = false): Promise<void> {
  const { header, mirror } = useOfflineCopy.getState();
  idle?.stop();
  idle = null;
  if (mirror === null || header === null) return;
  const hooks = Promise.all([...beforeLock].map(async (fn) => fn())).catch((e: unknown) => console.error("Kumbu² offline copy:", e));
  await Promise.race([hooks, new Promise((r) => setTimeout(r, BEFORE_LOCK_MS))]);
  // Another copy may have replaced this one meanwhile: only this one locks.
  if (everywhere) mirror.lockEverywhere();
  else mirror.lock();
  if (useOfflineCopy.getState().mirror === mirror) useOfflineCopy.getState().set(header, null);
}

/** Locks the copy in every tab that has it open: a sign-out in one tab ends what the others show too. */
export const lockEverywhere = () => lockCopy(true);

let idle: IdleLock | null = null;
const clock = { now: () => Date.now(), setTimeout: (fn: () => void, ms: number) => setTimeout(fn, ms), clearTimeout: (h: unknown) => clearTimeout(h as number) };

/**
 * Every unlocked copy ends like a session (§3a). Due, it starts again only while the server vouches
 * for the session (online, signed in, nothing lost: the server's own idle limit governs then, and the slot would open
 * the copy again anyway); otherwise the copy locks and the sign-in form asks.
 */
function armIdle(mirror: SealedMirror) {
  idle?.stop();
  idle = new IdleLock(clock, mirror.header.idleMs, () => {
    const mode = useOfflineMode.getState();
    if (!mode.offline && useSession.getState().user !== null) return armIdle(mirror);
    useSession.getState().end();
  });
}

if (typeof window !== "undefined") {
  for (const type of ["keydown", "pointerdown", "wheel", "touchstart"]) window.addEventListener(type, () => idle?.touch(), { passive: true, capture: true });
  // No timer runs while the laptop sleeps or the page is frozen: waking up looks at the clock.
  for (const type of ["visibilitychange", "pageshow", "focus"]) window.addEventListener(type, () => idle?.check());
}

function contacted(mirror: SealedMirror) {
  useOfflineMode.getState().waitingChanged();
  for (const fn of [...contactedListeners]) fn(mirror);
}

/** The copy being opened in this tab, and for whom. */
let opening: { userId: string; done: Promise<void> } | null = null;

// A switch to offline under way (turnOn.ts), kept across a reload: one cut short after its copy was made finishes at
// the next start, one cut short before says it didn't (Settings, Offline). Only the user's id, nothing of the copy.
const switchKey = "kumbu.turningOn";

export function switchBegun(userId: string): void {
  try {
    localStorage.setItem(switchKey, userId);
  } catch {
    // Not kept: a reload that cuts the switch short goes unnoticed, as before.
  }
}

export function switchEnded(): void {
  try {
    localStorage.removeItem(switchKey);
  } catch {
    // Nothing was kept.
  }
}

function switchUnder(userId: string): boolean {
  try {
    return localStorage.getItem(switchKey) === userId;
  } catch {
    return false;
  }
}

/** This user's switch was cut short before its copy was made: there is nothing to finish. */
export async function switchCutShort(userId: string): Promise<boolean> {
  return switchUnder(userId) && (await (await mirrorCopies()).forUser(userId)) === null;
}

/** Holds an unlocked copy in this tab, following what other tabs do to it; a copy it held before is locked. */
export function keep(opened: SealedMirror) {
  const before = useOfflineCopy.getState().mirror;
  if (before !== null && before !== opened) before.lock();
  armIdle(opened);
  opened.onPurged = (vault) => {
    if (vault === null) wiped();
    else useOfflineMode.getState().waitingChanged();
  };
  // The notice follows the removal, whichever path made it (a contact, the engines' vault list, another tab).
  opened.listenRemoved(({ lost }) => useOfflineMode.setState(({ purged }) => ({ purged: { lost: (purged?.lost ?? 0) + lost } })));
  opened.onChanged = () => useOfflineMode.getState().waitingChanged();
  opened.onLockAsked = () => void lockCopy();
  useOfflineCopy.getState().set(opened.header, opened);
}

/**
 * Opens this user's copy with the slot's key, which is the contact at sign-in: on a page load signed in, and after
 * the sign-in form, which also brings the password for the offline wrap (§4.2) and the idle limit. Nothing to do
 * where this browser keeps no copy of the user's.
 */
export function openCopy(userId: string, signIn?: { username: string; password: string; remember: boolean }): Promise<void> {
  // No IndexedDB (a private window of some browsers, the tests): no copy can be here.
  if (typeof indexedDB === "undefined") return Promise.resolve();
  // Signed in offline: the session knows the username only (id ""), and the copy is open already. Looking it up by
  // that id would find none and lock it.
  if (userId === "") return Promise.resolve();
  if (opening?.userId === userId) return opening.done;
  // Another user's opening still under way (signed out and someone else in): this one waits for it to end first.
  const before = opening?.done ?? Promise.resolve();
  const entry = { userId, done: Promise.resolve() };
  entry.done = (async () => {
    try {
      await before;
      const all = await mirrorCopies();
      const header = await all.forUser(userId);
      const held = useOfflineCopy.getState().mirror;
      // This user's copy is open already; a sign-in still wraps it again under the password and renews its limit.
      if (held !== null && held.header.copy === header?.copy && signIn === undefined) return;
      // Someone else's copy, or this one about to open afresh: it never serves another session.
      if (held !== null) await lockCopy();
      // Another person's copy open in other tabs locks there; this user's own stays open in theirs.
      await all.lockOthers(userId);
      if (header === null) return useOfflineCopy.getState().clear();
      const opened = await all.unlockOnline(header, signIn && {
        username: signIn.username, password: signIn.password, idleMs: signIn.remember ? REMEMBERED_IDLE_MS : IDLE_MS,
      });
      // Signed out, or someone else signed in, while it opened: it isn't kept for the session there is now, and a copy
      // found gone (removed from the device already) ends no one else's session.
      if (useSession.getState().user?.id !== userId) return opened === "wiped" ? undefined : opened.lock();
      if (opened === "wiped") return wiped();
      keep(opened);
      contacted(opened);
      // The switch that made it was cut short: its last step, asking the browser to keep the copy, is done now.
      if (switchUnder(userId)) {
        switchEnded();
        await keepThroughCleanUps().catch(() => false);
      }
    } catch (e) {
      // Offline or signed out meanwhile: the next sign-in or reconnect opens it.
      if (!(e instanceof OfflineError || e instanceof AuthError)) console.error("Kumbu² offline copy:", e);
    } finally {
      if (opening === entry) opening = null;
    }
  })();
  opening = entry;
  return entry.done;
}

/** Makes contact with the unlocked copy's slot: on every socket (re)connect and each `mirror` frame. */
export async function contact(): Promise<void> {
  const mirror = useOfflineCopy.getState().mirror;
  if (mirror === null || mirror.locked) return;
  // Under another user's session the slot port refuses it (slots.ts): their token is never handed in.
  try {
    const result = await mirror.contact();
    if (result === "wiped") return wiped();
    contacted(mirror);
  } catch (e) {
    if (!(e instanceof OfflineError || e instanceof AuthError)) console.error("Kumbu² offline copy:", e);
  }
}

/**
 * Signed in again over the app (the bar's dialog, §3a): the open copy is wrapped under the password just given
 * (§4.2), its idle limit starts again, and the contact is made, as after the sign-in form.
 */
export async function signedInAgain(username: string, password: string): Promise<void> {
  const mirror = useOfflineCopy.getState().mirror;
  if (mirror === null || mirror.locked) return;
  try {
    const result = await mirror.rewrap(username, password);
    if (result === "wiped") return wiped();
    armIdle(mirror);
    contacted(mirror);
  } catch (e) {
    if (!(e instanceof OfflineError || e instanceof AuthError)) console.error("Kumbu² offline copy:", e);
  }
}

/**
 * A 401 that said `mirror-wipe` to a request that handed in `token`, the slot's: the copy goes from this device. Only
 * while it is still the copy the tab holds: the answer may come after another one was opened.
 */
export async function wipeCopy(token: string | null): Promise<void> {
  const header = useOfflineCopy.getState().header;
  if (token !== (header?.slot.token ?? null)) return;
  useOfflineCopy.getState().mirror?.lock();
  wiped(true);
  if (header === null || typeof indexedDB === "undefined") return;
  await (await mirrorCopies()).wipe(header.copy).catch((e: unknown) => console.error("Kumbu² offline copy:", e));
}

/** Removes the copy this tab holds from the device, without a word on the sign-in page: the caller has its own. */
async function removeCopy(): Promise<void> {
  const header = useOfflineCopy.getState().header;
  idle?.stop();
  idle = null;
  useOfflineCopy.getState().mirror?.lock();
  useOfflineCopy.getState().clear();
  if (header === null || typeof indexedDB === "undefined") return;
  await (await mirrorCopies()).wipe(header.copy).catch((e: unknown) => console.error("Kumbu² offline copy:", e));
}

/** What the mode knows of the person (conflict copies' paths, the done line, the count waiting) goes with them. */
const forget = () => send({ type: "forget" });

onSessionEnd((how) => {
  forget();
  void (how === "wipe" ? removeCopy() : lockCopy(how === "lockEverywhere"));
});
useSession.subscribe((s, before) => {
  // Someone else signed in where another person's session was; the same person's offline sign-in confirmed is no change.
  if (s.user !== null && before.user !== null && s.user.username.toLowerCase() !== before.user.username.toLowerCase()) forget();
});
useOfflineCopy.subscribe((s, before) => {
  if (before.mirror !== null && s.mirror !== before.mirror) forget();
});

/** For the tests: a fresh mode, as in a new tab. */
export function resetOfflineMode() {
  shared?.stop();
  shared = null;
  idle?.stop();
  idle = null;
  failing = false;
  opening = null;
  copies = null;
  useOfflineMode.setState({ ...initialMode, purged: null });
}

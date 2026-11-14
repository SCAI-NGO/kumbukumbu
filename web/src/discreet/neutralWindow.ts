// SPDX-License-Identifier: AGPL-3.0-or-later
import { useSession } from "../state/session";
import { useDiscreet } from "./state";

/** What the address bar, the history and every link show of a URL in a discreet vault (spec §3.5, §4). */
export const NEUTRAL = "/discreet";

/** The routes that name a vault first: `/notes/Tagebuch/…`, `/board/Tagebuch`, `/calendar/…`, `/gallery/…`, `/view/…`, `/checkpoints/…`. */
const vaultRoute = /^\/(?:notes|board|calendar|gallery|view|checkpoints)\/([^/?#]+)/;

/** The vault a URL of the app shows, if it shows one. */
export function vaultOfRoute(pathname: string): string | null {
  const match = vaultRoute.exec(pathname);
  if (!match) return null;
  try {
    return decodeURIComponent(match[1]).normalize("NFC");
  } catch {
    return null;
  }
}

/** Whether a URL of the app (a path, or a full URL) shows a vault that is discreet now. */
export function discreetUrl(url: string): boolean {
  const vault = vaultOfRoute(pathOf(url));
  return vault !== null && useDiscreet.getState().names.has(vault);
}

function pathOf(url: string | URL): string {
  if (typeof url === "string" && url.startsWith("/") && !url.startsWith("//")) return url;
  try {
    const parsed = new URL(url, window.location.href);
    return parsed.origin === window.location.origin ? parsed.pathname + parsed.search + parsed.hash : "";
  } catch {
    return "";
  }
}

/** Whether the address bar shows the neutral URL: a discreet view is open (or about to go to `/`). */
export const showsDiscreet = () => window.location.pathname === NEUTRAL;

type HistoryState = { usr?: unknown; key?: string; idx?: number } & Record<string, unknown>;

/**
 * Entries of this tab's history kept in memory, by React Router's key: for one whose address bar shows `/discreet`,
 * the URL it stands for; and its `usr` (a lightbox's paths, say), which then never reaches `history.state`.
 */
const memory = new Map<string, { url: string | null; usr: unknown }>();

/** Forgets every discreet entry (hiding again, a session's end): going back to one shows `/discreet`, which goes to `/`. */
export function forgetDiscreet(): void {
  memory.clear();
}

const newKey = () => Math.random().toString(36).substring(2, 10);

/**
 * A `window` for React Router's browser history: its history writes `/discreet` into the browser for a URL in a
 * discreet vault, and only React Router's key and index into `history.state`, keeping the rest in `memory`; its
 * location reads the real URL back. Everything else the router asks of a window is the real one's.
 *
 * Two of React Router's features would write the real paths past it: view transitions (`viewTransition`, kept in
 * `sessionStorage` as `remix-router-transitions`) and a `ScrollRestoration` whose `getKey` is the pathname. Neither
 * is added without going through this first.
 */
export function createNeutralWindow(real: Window): Window {
  const history = real.history;
  const remembered = () => {
    const key = (history.state as HistoryState | null)?.key;
    return typeof key === "string" ? memory.get(key) : undefined;
  };
  // The real URL of the entry shown, or null while the address bar shows what it is.
  const hidden = () => {
    const url = real.location.pathname === NEUTRAL ? remembered()?.url : null;
    return url ? new URL(url, real.location.origin) : null;
  };
  const currentPath = () => {
    const url = hidden();
    return url ? url.pathname + url.search + url.hash : real.location.pathname + real.location.search + real.location.hash;
  };

  function write(method: "pushState" | "replaceState", state: unknown, url: string | URL | null | undefined) {
    const given = (state ?? null) as HistoryState | null;
    const same = url == null;
    const path = same ? currentPath() : pathOf(url);
    const discreet = discreetUrl(path) || (same && method === "replaceState" && !!remembered()?.url);
    // A replaced entry can't come back: what is remembered of it goes (and comes again below if it is still needed).
    const replaced = method === "replaceState" ? (history.state as HistoryState | null)?.key : undefined;
    if (typeof replaced === "string") memory.delete(replaced);
    const keepUsr = discreet || (given?.usr != null && useDiscreet.getState().names.size > 0);
    if (!keepUsr) return history[method](state, "", url);
    const { usr, ...rest } = given ?? {};
    const key = typeof rest.key === "string" ? rest.key : newKey();
    memory.set(key, { url: discreet ? path : null, usr: usr ?? null });
    history[method]({ ...rest, key }, "", discreet ? NEUTRAL : url);
  }

  const neutralHistory = {
    get state() {
      const entry = remembered();
      return entry ? { ...(history.state as HistoryState), usr: entry.usr } : history.state;
    },
    get length() {
      return history.length;
    },
    pushState: (state: unknown, _title: string, url?: string | URL | null) => write("pushState", state, url),
    replaceState: (state: unknown, _title: string, url?: string | URL | null) => write("replaceState", state, url),
    go: (delta?: number) => history.go(delta),
    back: () => history.back(),
    forward: () => history.forward(),
  };

  // A full page load (React Router's fallback when pushState fails, or a redirect) never loads a discreet URL either.
  const outward = (url: string | URL) => (discreetUrl(pathOf(url)) ? NEUTRAL : url);
  const location = {
    get pathname() {
      return hidden()?.pathname ?? real.location.pathname;
    },
    get search() {
      return hidden()?.search ?? real.location.search;
    },
    get hash() {
      return hidden()?.hash ?? real.location.hash;
    },
    get href() {
      return hidden()?.href ?? real.location.href;
    },
    get origin() {
      return real.location.origin;
    },
    get host() {
      return real.location.host;
    },
    get protocol() {
      return real.location.protocol;
    },
    assign: (url: string | URL) => real.location.assign(outward(url)),
    replace: (url: string | URL) => real.location.replace(outward(url)),
    reload: () => real.location.reload(),
  };

  // A vault marked discreet while its URL is the entry shown: that entry goes, before anything else is pushed.
  useDiscreet.subscribe((state, previous) => {
    if (state.names === previous.names || real.location.pathname === NEUTRAL) return;
    const path = currentPath();
    if (discreetUrl(path)) write("replaceState", neutralHistory.state, path);
  });
  // A sign-out, a session that ended, another user: the next session starts hidden.
  useSession.subscribe((session, previous) => {
    if (session.user?.id !== previous.user?.id) forgetDiscreet();
  });

  return {
    history: neutralHistory,
    location,
    document: real.document,
    get sessionStorage() {
      return real.sessionStorage;
    },
    addEventListener: real.addEventListener.bind(real),
    removeEventListener: real.removeEventListener.bind(real),
  } as unknown as Window;
}

export const neutralWindow: Window = createNeutralWindow(window);

/** Every link's `href` to a URL in a discreet vault is `/discreet`, so that a middle click can't put it into the history. */
export function wrapCreateHref<R extends { createHref(to: never): string }>(router: R): R {
  const own = router.createHref.bind(router) as (to: unknown) => string;
  router.createHref = ((to: unknown) => {
    const href = own(to);
    return discreetUrl(href) ? NEUTRAL : href;
  }) as R["createHref"];
  return router;
}

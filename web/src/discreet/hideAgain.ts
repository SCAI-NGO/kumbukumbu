// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useRef } from "react";
import { useLocation, useNavigate, type NavigateFunction } from "react-router-dom";
import { api, HttpError } from "../api/client";
import { inVault, noteUrl, vaultOf } from "../api/paths";
import { clearAppBadge } from "../calendar/useBadge";
import { useConsole } from "../console/store";
import { onDiscreetFrame } from "../hooks/useSyncSocket";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { useSyncStatus } from "../state/sync";
import { forgetDiscreet, showsDiscreet, vaultOfRoute } from "./neutralWindow";
import { useDiscreet } from "./state";
import { mentions, sweep } from "./sweep";

/** How long before the end the open discreet note is saved, so that the end finds nothing to save. */
export const SAVE_AHEAD = 15_000;
/** After the end, the server takes one late save of the note that was open (spec §3.8). */
export const LATE_SAVE = 5 * 60_000;
/**
 * A held draft is tried again when the browser is back online, or after this, whichever comes first; while the browser
 * says it is offline, only when it is online again, or once just before the late-save window closes.
 */
const RETRY_AFTER = 30_000;
const LAST_TRY_AHEAD = 1_000;

/**
 * Text of a discreet note that couldn't be saved on hiding (offline, say): out of view, not the editor's `draft`, and
 * tried once more within the late-save window, then forgotten either way.
 */
let held: { path: string; text: string; version: string; deadline: number; stop(): void } | null = null;

function hold(path: string, text: string, version: string, deadline: number, { open }: { open: boolean }) {
  // The open note's text comes first: a draft an earlier session end left never replaces what is held.
  if (held && !open) return;
  held?.stop();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const last = deadline - LAST_TRY_AHEAD;
  const wait = (ms: number) => (timer = setTimeout(() => (navigator.onLine || Date.now() >= last ? retry() : wait(last - Date.now())), Math.max(0, ms)));
  const retry = () => {
    const draft = held;
    if (!draft) return;
    draft.stop();
    held = null;
    if (Date.now() > draft.deadline) return;
    void api
      .PUT("/api/notes/{vault}/{path}", { params: { path: { vault: vaultOf(draft.path), path: inVault(draft.path) } }, body: { content: draft.text, version: draft.version } })
      .catch(() => {});
  };
  wait(Math.min(RETRY_AFTER, last - Date.now()));
  window.addEventListener("online", retry);
  held = {
    path,
    text,
    version,
    deadline,
    stop: () => {
      clearTimeout(timer);
      window.removeEventListener("online", retry);
    },
  };
}

/** Whether a draft is held for its late retry; for tests, which never see its text. */
export const holding = () => held !== null;

/** Ends a held draft's wait, as the session's end does. */
export function forgetHeld() {
  held?.stop();
  held = null;
}

/** The open discreet note saves what is pending; true when nothing is left unsaved. */
async function saveOpen(names: ReadonlySet<string>): Promise<boolean> {
  const { currentPath, beforeRead } = useEditor.getState();
  if (!currentPath || !names.has(vaultOf(currentPath)) || !beforeRead) return true;
  // No read of the note afterwards: it would ask for a vault about to be hidden.
  return beforeRead({ reread: false }).catch(() => false);
}

/** Saves the open discreet note a little before the end (spec §3.8); the mode stays. */
export const saveAhead = () => saveOpen(useDiscreet.getState().names);

/**
 * Tells the server. Offline, the hide didn't reach it: the tab stays hidden, and a session read meanwhile that still
 * says shown sends it again instead of showing them (`followSession`).
 */
async function tellServer() {
  try {
    const { response } = await api.POST("/api/account/discreet/hide");
    useDiscreet.setState({ unsent: !response.ok && response.status !== 401 });
  } catch {
    useDiscreet.setState({ unsent: true });
  }
}

let running: Promise<void> | null = null;

/**
 * Hides the discreet vaults again in this tab (spec §3.8): the open discreet note saves first, then (`tell`) the
 * server hears of it; the tab closes the discreet vaults' views, drops every cached answer but the session (asked again
 * then), the console's scrollback and who edits what, and forgets them in the editor's state; a discreet view on screen
 * gives way to the last ordinary note the tab showed, or to the vault overview. ✕ and the end of the time tell the
 * server; a `hidden` frame or a session read as hidden don't need to.
 * One at a time: a second call meanwhile joins the first.
 */
export function hideAgain(queryClient: QueryClient, navigate: NavigateFunction, { tell, pathname }: { tell: boolean; pathname: string }): Promise<void> {
  running ??= run(queryClient, navigate, tell, pathname).finally(() => (running = null));
  return running;
}

async function run(queryClient: QueryClient, navigate: NavigateFunction, tell: boolean, pathname: string) {
  const names = new Set(useDiscreet.getState().names);
  const until = useDiscreet.getState().until;
  const discreet = (path: string | null | undefined): path is string => path != null && names.has(vaultOf(path));

  // From here on no session read is followed, nor the URL of the note on its way out.
  useDiscreet.setState({ hiding: true });
  try {
    const { currentPath } = useEditor.getState();
    if (!(await saveOpen(names)) && discreet(currentPath)) {
      const text = useEditor.getState().unsaved?.();
      const end = Math.min(Date.now(), until ? Date.parse(until) : Date.now());
      if (text) hold(currentPath, text.text, text.version, end + LATE_SAVE, { open: true });
    }
    // Text a session end left for a discreet note waits the same way, never in view.
    const { draft } = useEditor.getState();
    if (draft && discreet(draft.path)) hold(draft.path, draft.text, draft.version, Date.now() + LATE_SAVE, { open: false });

    // A session read under way answers as the session was: it is not waited for.
    await queryClient.cancelQueries({ queryKey: ["session"] });
    if (tell) await tellServer();

    const editor = useEditor.getState();
    const ordinary = editor.recentPaths.find((p) => !discreet(p)) ?? null;
    const listed = queryClient.getQueryData<{ name: string; discreet?: boolean }[]>(["vaults"]);
    const fallback = listed?.find((v) => !v.discreet && !names.has(v.name))?.name ?? null;
    const vault = ordinary ? vaultOf(ordinary) : editor.activeVault && !names.has(editor.activeVault) ? editor.activeVault : fallback;
    const shown = vaultOfRoute(pathname);

    // From here on a view of these vaults that renders once more shows nothing of them, and once the mode is reset the
    // editor's guard lets the navigation away from such a note pass: what it had unsaved is saved or held above, and
    // the editor saves nothing more (text typed in the moment since, as the server is told of a ✕, isn't kept).
    useDiscreet.setState({ gone: new Set([...useDiscreet.getState().gone, ...names]) });
    for (const name of names) useEditor.getState().forgetVault(name);
    // What was looked for, and the note's tools, may name what was in them.
    useEditor.setState({ searchQuery: "", searchTag: null, find: { query: "", index: 0, count: 0 }, noteTool: null, mode: "read" });
    if (vault) useEditor.getState().setActiveVault(vault);
    useDiscreet.getState().reset();
    // The address bar shows /discreet for a discreet view whatever the router's location says.
    if ((shown !== null && names.has(shown)) || showsDiscreet()) {
      navigate(ordinary ? noteUrl(ordinary) : "/", { replace: true });
      // The shell doesn't follow the URL while hiding: the note it goes to opens here.
      if (ordinary) useEditor.getState().openNote(ordinary);
    }
    // The views of the discreet vaults go first, so that nothing still shown asks for them again.
    await new Promise((resolve) => setTimeout(resolve, 0));
    // Going back to a discreet view's entry now finds /discreet, which goes to the vault list.
    forgetDiscreet();

    // What's on and its count named the discreet vaults' entries, as Recent changes did: the app's icon is cleared, and
    // each, kept for the views still showing them (the badge, the What's on page, Recent changes), is emptied at once
    // and asked again; a removed query would stay on screen with what it had until its view rendered again.
    clearAppBadge();
    queryClient.removeQueries({ predicate: (q) => q.queryKey[0] !== "session" && q.queryKey[0] !== "whats-on" && q.queryKey[0] !== "recent" });
    void queryClient.resetQueries({ queryKey: ["whats-on"] });
    void queryClient.resetQueries({ queryKey: ["recent"] });
    sweep(queryClient, names);
    for (const m of queryClient.getMutationCache().getAll()) if (m.state.status !== "pending") queryClient.getMutationCache().remove(m);
    void queryClient.refetchQueries({ queryKey: ["session"] });
    useConsole.getState().reset();
    useSyncStatus.setState({ editors: {} });
  } finally {
    useDiscreet.setState({ hiding: false });
  }
}

/** The pathname now, for callbacks that run later (a timer, a frame, a session read) than the render that made them. */
export function useLatestPathname(): () => string {
  const { pathname } = useLocation();
  const at = useRef(pathname);
  at.current = pathname;
  return useCallback(() => at.current, []);
}

/** Hides the discreet vaults again from a control of this tab (✕), telling the server. */
export function useHideAgain() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const pathname = useLatestPathname();
  return useCallback(() => hideAgain(queryClient, navigate, { tell: true, pathname: pathname() }), [queryClient, navigate, pathname]);
}

/**
 * The discreet mode's clock and ears, once per signed-in tab: a little before the end the open discreet note is
 * saved, at the end the vaults are hidden again; so they are when another tab hid them (the socket's `hidden`), and
 * the session is asked again when a vault that was discreet answers 404, which then finds the session hidden. The end
 * of the time isn't told to the server, which ends it by its own clock and then takes one late save of the open note
 * (spec §3.8): a hide would forgo that.
 */
export function useDiscreetMode() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const pathname = useLatestPathname();
  const revealed = useDiscreet((s) => s.revealed);
  const until = useDiscreet((s) => s.until);

  useEffect(
    () =>
      onDiscreetFrame((type) => {
        if (type === "hidden" && useDiscreet.getState().revealed) void hideAgain(queryClient, navigate, { tell: false, pathname: pathname() });
      }),
    [queryClient, navigate, pathname],
  );

  useEffect(() => {
    if (!revealed || !until) return;
    const left = Date.parse(until) - Date.now();
    const end = Date.parse(until);
    // Woken after the end, both run at once: the end's save is the one.
    const ahead = setTimeout(() => void (Date.now() < end && saveAhead()), Math.max(0, left - SAVE_AHEAD));
    const ended = setTimeout(() => void hideAgain(queryClient, navigate, { tell: false, pathname: pathname() }), Math.max(0, left));
    return () => {
      clearTimeout(ahead);
      clearTimeout(ended);
    };
  }, [revealed, until, queryClient, navigate, pathname]);

  useEffect(
    () =>
      queryClient.getQueryCache().subscribe((event) => {
        if (event.type !== "updated" || event.action.type !== "error") return;
        const { names, revealed } = useDiscreet.getState();
        const error = event.action.error;
        if (!revealed || !(error instanceof HttpError) || error.status !== 404) return;
        if (mentions(event.query.queryKey, names)) void queryClient.invalidateQueries({ queryKey: ["session"] });
      }),
    [queryClient],
  );
}

// The session ends, or another user's begins: a draft held for a late save goes with it.
useSession.subscribe((session, previous) => {
  if (session.user?.id !== previous.user?.id) forgetHeld();
});

/** The session as read again: shown before, hidden now (the time ran out elsewhere, say), and the tab follows. */
export function followSession(queryClient: QueryClient, navigate: NavigateFunction, pathname: string, view: { revealed: boolean; until: string | null } | undefined) {
  // Hiding asks for the session again when it is done; an answer meanwhile may tell of the session as it was.
  if (useDiscreet.getState().hiding) return;
  if (useDiscreet.getState().unsent) {
    if (!view?.revealed) return useDiscreet.setState({ unsent: false });
    void tellServer().then(() => {
      if (useDiscreet.getState().unsent) return;
      // A vault list read meanwhile still has them.
      void queryClient.resetQueries({ queryKey: ["vaults"] });
      void queryClient.invalidateQueries({ queryKey: ["session"] });
    });
    return;
  }
  const was = useDiscreet.getState().revealed;
  if (was && !view?.revealed) {
    void hideAgain(queryClient, navigate, { tell: false, pathname });
    return;
  }
  // This tab's time ran out before the server's (its clock is a little behind): not shown again, asked again shortly.
  if (!was && view?.revealed && view.until && Date.parse(view.until) <= Date.now()) {
    askAgain ??= setTimeout(() => {
      askAgain = null;
      void queryClient.invalidateQueries({ queryKey: ["session"] });
    }, ASK_AGAIN);
    return;
  }
  // A vault list read meanwhile still has them.
  if (!view?.revealed && queryClient.getQueryData<{ discreet?: boolean }[]>(["vaults"])?.some((v) => v.discreet)) void queryClient.resetQueries({ queryKey: ["vaults"] });
  useDiscreet.getState().setFromSession(view);
}

/** How soon a session that still shows them after this tab's end is asked again. */
const ASK_AGAIN = 2_000;
let askAgain: ReturnType<typeof setTimeout> | null = null;

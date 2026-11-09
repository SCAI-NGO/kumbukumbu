// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useNavigate, type NavigateFunction } from "react-router-dom";
import { api, call, HttpError } from "../api/client";
import { vaultHinted } from "../api/history";
import { useVaults } from "../api/notes";
import { followRename } from "../api/sharing";
import { presenceKey, useSyncStatus } from "../state/sync";

/** The server closed the socket for a newer one of this session (more than four tabs): reconnecting would replace that one. */
const REPLACED = 4409;
/** The server closed the socket because the session hid its discreet vaults (or a vault became one): connect again at once, the new ticket leaves them out. */
const HIDDEN = 4412;
const MAX_DELAY = 30_000;
/** WebSocket.OPEN. */
const OPEN = 1;

const socketUrl = (ticket: string) => {
  const { protocol, host } = window.location;
  return `${protocol === "https:" ? "wss" : "ws"}://${host}/api/ws?ticket=${encodeURIComponent(ticket)}`;
};

type DiscreetFrame = (queryClient: QueryClient, navigate: NavigateFunction) => void;

/**
 * What the frames about the session's discreet vaults do, which name no vault: `hidden`, the session hid them again
 * (in another tab, or their time ran out), and `revealed`, it showed them, so the session and the vault list are asked
 * again (and the socket opens again for the vaults it gains). The discreet mode sets `hidden`.
 */
export const discreetFrames: { hidden: DiscreetFrame; revealed: DiscreetFrame } = {
  hidden: () => {},
  revealed: (queryClient) => {
    void queryClient.invalidateQueries({ queryKey: ["session"] });
    void queryClient.invalidateQueries({ queryKey: ["vaults"] });
  },
};

/**
 * A frame from the server; `vault` is the caller's own name for it (an alias for a vault shared with her). `renamed`
 * comes to the owner's sockets only, `from` naming the vault as it was.
 */
function onFrame(queryClient: QueryClient, navigate: NavigateFunction, data: unknown) {
  if (typeof data !== "string") return;
  let frame: unknown;
  try {
    frame = JSON.parse(data);
  } catch {
    return;
  }
  if (!frame || typeof frame !== "object" || !("type" in frame)) return;
  if (frame.type === "hidden" || frame.type === "revealed") {
    discreetFrames[frame.type](queryClient, navigate);
    return;
  }
  if (!("vault" in frame) || typeof frame.vault !== "string") return;
  // One naming no move (the server's catalog forgot the vault meanwhile) would only forget what the tab holds under it.
  if (frame.type === "renamed" && "from" in frame && typeof frame.from === "string" && frame.from !== frame.vault) followRename(queryClient, navigate, frame.from, frame.vault);
  if (frame.type === "changed") vaultHinted(queryClient, frame.vault);
  if (frame.type === "presence" && "path" in frame && typeof frame.path === "string" && "editors" in frame && Array.isArray(frame.editors)) {
    const key = presenceKey(frame.vault, frame.path);
    const shown = frame.editors.filter((e): e is string => typeof e === "string");
    useSyncStatus.setState(({ editors }) => {
      const next = { ...editors };
      if (shown.length) next[key] = shown;
      else delete next[key];
      return { editors: next };
    });
  }
}

/** Down: nobody is known to be editing anything, and nothing can be sent. */
const down = { connected: false, editors: {}, send: () => false };

/**
 * One socket per signed-in tab (collab design §9.1): each hint has the vault's tree, history, trash, checkpoints and
 * the open note's version asked again. Every connection takes a fresh ticket, which holds the vaults as the server
 * opens them then; a vault gained since (an invitation accepted in another tab) comes with a new ticket, so the socket
 * is opened again when the vault list names one it didn't. Closed, it tries again after 1, 2, 4 … 30 seconds (at once
 * when its discreet vaults were hidden), and meanwhile the version poll runs; after a gap, what it may have missed is
 * asked again.
 */
export function useSyncSocket() {
  const queryClient = useQueryClient();
  // Not a dependency of the socket: a new navigate mustn't open it again.
  const navigateNow = useNavigate();
  const navigate = useRef(navigateNow);
  navigate.current = navigateNow;
  const vaults = useVaults();
  const [generation, setGeneration] = useState(0);
  /** The vault names when the ticket was asked for, null before the list is in. */
  const held = useRef<Set<string> | null>(null);
  const opened = useRef(false);

  useEffect(() => {
    if (typeof WebSocket === "undefined") return;
    let stopped = false;
    let socket: WebSocket | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;

    const retry = () => {
      if (stopped) return;
      timer = setTimeout(() => void connect(), Math.min(MAX_DELAY, 1000 * 2 ** attempt++));
    };
    const connect = async () => {
      const names = queryClient.getQueryData<{ name: string }[]>(["vaults"]);
      let ticket: string;
      try {
        ticket = (await call(api.POST("/api/ws-ticket"))).ticket;
      } catch (e) {
        // A session the server gives no ticket to won't get one by asking again; the poll goes on.
        if (!(e instanceof HttpError && e.status === 403)) retry();
        return;
      }
      if (stopped) return;
      held.current = names ? new Set(names.map((v) => v.name)) : null;
      const s = new WebSocket(socketUrl(ticket));
      socket = s;
      s.onopen = () => {
        attempt = 0;
        useSyncStatus.setState({
          connected: true,
          send: (frame) => {
            if (socket !== s || s.readyState !== OPEN) return false;
            s.send(JSON.stringify(frame));
            return true;
          },
        });
        // Hints sent while there was no socket are lost: everything they would have asked again is.
        if (opened.current) for (const key of ["tree", "vaults", "history", "trash", "archive", "checkpoints", "changes", "version", "links", "backlinks"]) void queryClient.invalidateQueries({ queryKey: [key] });
        opened.current = true;
      };
      s.onmessage = (e: MessageEvent) => onFrame(queryClient, navigate.current, e.data);
      s.onclose = (e: CloseEvent) => {
        if (socket !== s) return;
        socket = null;
        useSyncStatus.setState(down);
        if (e.code === HIDDEN) {
          attempt = 0;
          if (!stopped) void connect();
        } else if (e.code !== REPLACED) retry();
      };
    };

    void connect();
    return () => {
      stopped = true;
      clearTimeout(timer);
      const s = socket;
      socket = null;
      s?.close();
      useSyncStatus.setState(down);
    };
  }, [queryClient, generation]);

  // A vault the ticket didn't hold: the socket is opened again, with a ticket that holds it.
  const names = vaults.data?.map((v) => v.name).join("\n");
  useEffect(() => {
    if (names === undefined) return;
    const list = names ? names.split("\n") : [];
    if (!held.current) {
      held.current = new Set(list);
      return;
    }
    if (list.some((n) => !held.current!.has(n))) {
      held.current = new Set(list);
      setGeneration((g) => g + 1);
    }
  }, [names]);
}

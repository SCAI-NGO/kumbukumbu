// SPDX-License-Identifier: AGPL-3.0-or-later
import { useIsFetching, useIsMutating } from "@tanstack/react-query";
import { useEffect, useRef, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { HttpError } from "../api/client";
import { useVersionPoll, type NoteVersion } from "../api/history";
import { noteUrl } from "../api/paths";
import { useSession } from "../state/session";
import { ago, writerOf } from "./AgentBanner";

/**
 * A write of the open note made elsewhere (an agent, another tab or device): the server's version differs from the
 * one shown and was written after it. A version is a hash of the text, so going back to a text shown before (a
 * restore) is a change too; the time tells it from an older answer. `gone` when the note isn't at its path any more.
 * Nothing counts while the view's own save or restore is under way, or the note is being read again: their answer
 * is the version they bring, which the view then holds.
 */
export function useChangedElsewhere(
  path: string,
  shown: { version: string; lastWriteAt: string } | undefined,
): { change: NoteVersion | null; gone: boolean; movedTo: string | null } {
  const poll = useVersionPoll(path, shown !== undefined);
  const busy =
    useIsMutating({ mutationKey: ["save", path] }) + useIsMutating({ mutationKey: ["restore"] }) + useIsFetching({ queryKey: ["note", path], exact: true }) > 0;
  const data = poll.data;
  // A failed poll keeps its last answer, which says nothing about now.
  const change =
    !busy && !poll.isError && shown && data && data.path === path && data.version !== shown.version && Date.parse(data.lastWriteAt) > Date.parse(shown.lastWriteAt) ? data : null;
  const gone = !busy && poll.error instanceof HttpError && poll.error.status === 404;
  return { change, gone, movedTo: gone ? movedTo((poll.error as HttpError).message) : null };
}

/**
 * Where a moved note is now, from the 404's sentence (`Moves.HintAsync` on the server: "; it was moved to
 * /Work/b.md on 2026-10-25 09:03 UTC (liz)"); null if it wasn't moved, or isn't there any more either.
 */
export function movedTo(message: string): string | null {
  const m = /; it was moved to (\/.+?\.md) on \d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC /.exec(message);
  return m && !message.includes(", and is not there any more") ? m[1] : null;
}

/** "This note was moved elsewhere, to <link>", or "… moved or removed elsewhere." */
export function GoneText({ movedTo }: { movedTo: string | null }) {
  if (!movedTo) return <>This note was moved or removed elsewhere.</>;
  return (
    <>
      This note was moved elsewhere, to{" "}
      <Link to={noteUrl(movedTo)} className="font-medium text-accent-text underline underline-offset-2">
        {movedTo.slice(1)}
      </Link>
      .
    </>
  );
}

const linkClass = "h-11 flex-none rounded-[8px] px-2.5 font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent";

/** The device of a key paired from Obsidian: what follows the last "Obsidian " in its name ("Work – Obsidian Laptop" is "Laptop"). */
export function deviceOf(writer: string): string {
  const at = writer.lastIndexOf("Obsidian ");
  return at < 0 ? writer : writer.slice(at + "Obsidian ".length);
}

/**
 * "liz changed this note just now"; "You changed this note in Obsidian (Laptop) …" for a write by one of the user's
 * devices, and "You changed this note elsewhere …" for their own write in another tab.
 */
export function ChangeText({ change }: { change: NoteVersion }) {
  const username = useSession((s) => s.user?.username);
  const writer = writerOf(change.lastWriter);
  if (change.lastWriterKind === "device") return <>You changed this note in Obsidian ({deviceOf(writer)}) {ago(change.lastWriteAt)}</>;
  return writer === username ? (
    <>You changed this note elsewhere {ago(change.lastWriteAt)}</>
  ) : (
    <>
      <code className="font-mono text-text">{writer}</code> changed this note {ago(change.lastWriteAt)}
    </>
  );
}

/** The quiet bar over the note: what changed elsewhere, and what the user can do about it. */
export function ElsewhereBar({ children, actions }: { children: ReactNode; actions?: ReactNode }) {
  return (
    <section
      aria-label="Changed elsewhere"
      className="flex min-h-11 flex-none flex-wrap items-center gap-x-1 border-b border-line bg-surface pr-1.5 pl-4 text-[13px] text-muted"
    >
      <span aria-hidden className="size-2 flex-none rounded-full bg-accent" />
      <span role="status" className="min-w-0 flex-1 py-2 pl-1.5">
        {children}
      </span>
      {actions}
    </section>
  );
}

export function BarButton({ onClick, children }: { onClick(): void; children: ReactNode }) {
  return (
    <button type="button" onClick={onClick} className={linkClass}>
      {children}
    </button>
  );
}

/** Calls `onTimeout` once, `ms` after `active` turned true, unless it turns false first. */
export function useTimeout(active: boolean, ms: number, onTimeout: () => void) {
  const callback = useRef(onTimeout);
  callback.current = onTimeout;
  useEffect(() => {
    if (!active) return;
    const timer = setTimeout(() => callback.current(), ms);
    return () => clearTimeout(timer);
  }, [active, ms]);
}

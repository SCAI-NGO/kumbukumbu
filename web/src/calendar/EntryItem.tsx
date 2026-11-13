// SPDX-License-Identifier: AGPL-3.0-or-later
import { AlertTriangle, Check, CircleDot, Clock, ListTodo, SquareKanban, StickyNote, type LucideIcon } from "lucide-react";
import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { noteUrl } from "../api/paths";
import type { EntryKind, EntryState, EntryView } from "./api";

/** How a state is shown: never by colour alone, always its icon and its word; overdue in the warning colour, calmly. */
const states: Partial<Record<EntryState, { icon: LucideIcon; word: string; className: string }>> = {
  overdue: { icon: AlertTriangle, word: "overdue", className: "text-warning" },
  upcoming: { icon: Clock, word: "upcoming", className: "text-muted" },
  ongoing: { icon: CircleDot, word: "ongoing", className: "text-accent-text" },
  done: { icon: Check, word: "closed", className: "text-muted" },
};

const kinds: Record<EntryKind, { icon: LucideIcon; word: string }> = {
  task: { icon: ListTodo, word: "To do" },
  card: { icon: SquareKanban, word: "Card" },
  dated: { icon: StickyNote, word: "Note" },
};

/** What a person who may write can do with an entry: close a to-do at once, or move it on with Later. */
export interface EntryActions {
  onDone(entry: EntryView): void;
  /** The control beside ✓ that moves the entry on; without it, a plain Later button that opens nothing. */
  renderLater?: (entry: EntryView) => ReactNode;
}

/** An open to-do has ✓; Later is on every open to-do and on a board card that isn't done. */
const closable = (entry: EntryView) => entry.kind === "task" && entry.task === "open";
const movable = (entry: EntryView) => closable(entry) || (entry.kind === "card" && entry.state !== "done" && entry.state !== "past");

/**
 * One entry in a calendar list: its time, its title (which opens the note), its state and a mark for its kind. A to-do
 * and a note given a day are told apart at a glance: only a to-do has ✓ and Later. Without `actions`, read only.
 */
export default function EntryItem({ entry, actions, range }: { entry: EntryView; actions?: EntryActions; range?: string }) {
  const state = states[entry.state];
  const kind = kinds[entry.kind];
  return (
    <li className="flex min-h-12 items-start gap-1 rounded-[10px] bg-surface px-1 py-1">
      {actions && closable(entry) ? (
        <button
          type="button"
          aria-label={`Done: ${entry.title}`}
          onClick={() => actions.onDone(entry)}
          className="flex size-12 flex-none items-center justify-center rounded-[10px] text-muted hover:text-text focus-visible:outline-2 focus-visible:outline-accent"
        >
          <span aria-hidden className="flex size-5 items-center justify-center rounded-[5px] border-2 border-current" />
        </button>
      ) : (
        <span aria-hidden className="w-2 flex-none" />
      )}
      <div className="flex min-w-0 flex-1 flex-col gap-0.5 py-2">
        <p className="flex min-w-0 items-baseline gap-2">
          {entry.time && <span className="flex-none font-mono text-sm text-text-body">{entry.time}</span>}
          <Link
            to={noteUrl(entry.path)}
            className="min-w-0 font-medium text-text [overflow-wrap:anywhere] hover:underline focus-visible:outline-2 focus-visible:outline-accent"
          >
            {entry.title}
          </Link>
          <span title={kind.word} className="ml-auto flex-none self-center text-dim">
            <kind.icon aria-hidden size={14} />
            <span className="sr-only">{kind.word}</span>
          </span>
        </p>
        {(range || state) && (
          <p className="flex flex-wrap items-center gap-x-2 text-sm">
            {range && <span className="text-text-body">{range}</span>}
            {state && (
              <span className={`flex items-center gap-1 ${state.className}`}>
                <state.icon aria-hidden size={14} className="flex-none" />
                <span>{entry.span && entry.state !== "done" ? `${state.word} · ${entry.span}` : state.word}</span>
              </span>
            )}
          </p>
        )}
      </div>
      {actions &&
        movable(entry) &&
        (actions.renderLater ? (
          actions.renderLater(entry)
        ) : (
          <button
            type="button"
            aria-label={`Later: ${entry.title}`}
            className="flex min-h-12 flex-none items-center rounded-[10px] px-3 text-sm font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
          >
            Later
          </button>
        ))}
    </li>
  );
}

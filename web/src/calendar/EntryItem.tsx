// SPDX-License-Identifier: AGPL-3.0-or-later
import { AlertTriangle, Check, CircleDot, Clock, ListTodo, SquareKanban, StickyNote, type LucideIcon } from "lucide-react";
import type { ReactNode } from "react";
import { Link, useLocation } from "react-router-dom";
import { noteUrl } from "../api/paths";
import type { EntryKind, EntryState, EntryView } from "./api";
import { cameFromHere, withCameFrom } from "./backLink";
import TaskCheckbox from "./TaskCheckbox";

/** How a state is shown: never by colour alone, always its icon and its word; overdue in the warning colour, calmly. */
const states: Partial<Record<EntryState, { icon: LucideIcon; word: string; className: string }>> = {
  overdue: { icon: AlertTriangle, word: "overdue", className: "text-warning" },
  upcoming: { icon: Clock, word: "upcoming", className: "text-muted" },
  ongoing: { icon: CircleDot, word: "ongoing", className: "text-accent-text" },
  done: { icon: Check, word: "closed", className: "text-muted" },
};

/** The state's word and its span: "overdue · 3 weeks", "ongoing · until 15:00"; on its day before its time "today · 14:00". */
const stateText = (entry: EntryView, word: string) => {
  if (!entry.span || entry.state === "done") return word;
  return entry.beforeTime ? `today · ${entry.span}` : `${word} · ${entry.span}`;
};

const kinds: Record<EntryKind, { icon: LucideIcon; word: string }> = {
  task: { icon: ListTodo, word: "To do" },
  card: { icon: SquareKanban, word: "Card" },
  dated: { icon: StickyNote, word: "Note" },
};

/** What a person who may write can do with an entry: close a to-do at once, open it again, or move it on with Later. */
export interface EntryActions {
  onDone(entry: EntryView): void;
  /** Unticks a closed to-do: it opens again. Without it, a closed to-do shows no box. */
  onReopen?(entry: EntryView): void;
  /** The control beside its box that moves the entry on; without it, a plain Later button that opens nothing. */
  renderLater?: (entry: EntryView) => ReactNode;
}

/** An open to-do has its box, a closed one its ticked box; Later is on every open to-do and on a board card that isn't done. */
const closable = (entry: EntryView) => entry.kind === "task" && entry.task === "open";
const reopenable = (entry: EntryView) => entry.kind === "task" && entry.task === "closed";
const movable = (entry: EntryView) => closable(entry) || (entry.kind === "card" && entry.state !== "done" && entry.state !== "past");

/**
 * One entry in a calendar list: its time, its title (which opens the note), its state and a mark for its kind. A to-do
 * and a note given a day are told apart at a glance: only a to-do has its box and Later. Without `actions`, read only;
 * `vault` names the entry's vault, where a list spans several (What's on); `kept`, the tag its page keeps out of the
 * address, which the note's way back takes along.
 *
 * The title has the whole width of its column and wraps by words: in a narrow column (a day of the desktop's week) the
 * time stands above it and its box and Later on a line below; where there is room, beside it.
 */
export default function EntryItem({ entry, actions, range, vault, kept }: { entry: EntryView; actions?: EntryActions; range?: string; vault?: string; kept?: string }) {
  const state = states[entry.state];
  const kind = kinds[entry.kind];
  const done = actions && closable(entry);
  const reopen = reopenable(entry) ? actions?.onReopen : undefined;
  const later = actions && movable(entry);
  const location = useLocation();
  return (
    <li className="@container rounded-[10px] bg-surface px-1 py-1">
      <div className="flex flex-wrap items-start gap-x-1 @xs:flex-nowrap">
        <div className="min-w-0 basis-full px-2 pb-2 @xs:order-2 @xs:flex-1 @xs:basis-auto">
          <div className="flex flex-col @xs:flex-row @xs:items-start @xs:gap-2">
            {entry.time && <span className="pt-2 font-mono text-sm text-text-body @xs:flex-none @xs:pt-3.5">{entry.time}</span>}
            <Link
              to={noteUrl(entry.path)}
              state={withCameFrom(undefined, cameFromHere(location, kept))}
              className="block min-h-12 min-w-0 py-3 font-medium break-words text-text hover:underline focus-visible:outline-2 focus-visible:outline-accent @xs:flex-1"
            >
              {entry.title}
            </Link>
          </div>
          <p className="flex flex-wrap items-center gap-x-2 text-sm">
            <span title={kind.word} className="flex-none text-dim">
              <kind.icon aria-hidden size={14} />
              <span className="sr-only">{kind.word}</span>
            </span>
            {vault && <span className="text-muted">{vault}</span>}
            {range && <span className="text-text-body">{range}</span>}
            {state && (
              <span className={`flex items-center gap-1 ${state.className}`}>
                <state.icon aria-hidden size={14} className="flex-none" />
                <span>{stateText(entry, state.word)}</span>
              </span>
            )}
          </p>
        </div>
        {(done || reopen) && (
          <div className="flex-none text-sm @xs:order-1">
            {done ? (
              <TaskCheckbox closed={false} title={entry.title} onChange={() => actions.onDone(entry)} />
            ) : (
              <TaskCheckbox closed title={entry.title} onChange={() => reopen!(entry)} />
            )}
          </div>
        )}
        {later && (
          <div className="ml-auto flex-none @xs:order-3">
            {actions.renderLater ? (
              actions.renderLater(entry)
            ) : (
              <button
                type="button"
                aria-label={`Later: ${entry.title}`}
                className="flex min-h-12 flex-none items-center rounded-[10px] px-3 text-sm font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
              >
                Later
              </button>
            )}
          </div>
        )}
      </div>
    </li>
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { CalendarDays, ChevronRight, ChevronsRight, X } from "lucide-react";
import { useEffect, useId, useRef, useState } from "react";
import FormDialog from "../board/FormDialog";
import { inputClass, labelClass } from "../components/form";
import { Menu, type MenuItem } from "../components/Menu";
import { errorText } from "../api/error-text";
import { useEntryAction, type EntryView, type LaterRequest, type WrittenView } from "./api";
import { shortDay, weekOf } from "./dates";

type Part = LaterRequest["part"];

const isWeek = (value: string) => /^\d{4}-W\d{2}$/.test(value);

export const laterButtonClass =
  "flex min-h-12 flex-none items-center rounded-[10px] px-3 text-sm font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent";

/**
 * Later beside ✓ (spec §7.13): +1 day, +1 week or a picked day, each counted from the later of today and the entry's
 * date, so nothing moves earlier. A to-do with an end offers Postpone (all of it) and More time (only its end), each
 * under its heading; without one, or on a board card, the three alone. A week has no +1 day.
 */
export default function LaterMenu({
  entry,
  onProblem,
  onMoved,
  className = laterButtonClass,
}: {
  entry: EntryView;
  onProblem(text: string | null): void;
  /** Said once the entry has moved, for the short message that follows (`useMoved`). */
  onMoved?(moved: Moved): void;
  className?: string;
}) {
  const action = useEntryAction();
  const [picking, setPicking] = useState<Part | null>(null);
  const moved = (part: Part) => (written: WrittenView) => onMoved?.({ before: entry, part, after: written.note.entry });

  const later = (part: Part, step: "day" | "week") => {
    onProblem(null);
    action.mutate({ kind: "later", path: entry.path, part, step }, { onSuccess: moved(part), onError: (err) => onProblem(errorText(err)) });
  };
  const items = (part: Part, value: string): MenuItem[] => [
    ...(isWeek(value) ? [] : [{ label: "+1 day", icon: ChevronRight, onSelect: () => later(part, "day") }]),
    { label: "+1 week", icon: ChevronsRight, onSelect: () => later(part, "week") },
    { label: "Pick a date…", icon: CalendarDays, onSelect: () => setPicking(part) },
  ];
  const withEnd = entry.kind !== "card" && entry.until !== null;

  return (
    <>
      <Menu
        label={`Later: ${entry.title}`}
        items={withEnd ? [] : items("postpone", entry.when)}
        sections={withEnd ? [{ heading: "Postpone", items: items("postpone", entry.when) }, { heading: "More time", items: items("moreTime", entry.until!) }] : undefined}
        placement="below"
        className={className}
      >
        Later
      </Menu>
      {picking && (
        <PickDate
          title={picking === "postpone" ? `Postpone ${entry.title}` : `More time for ${entry.title}`}
          submitLabel={picking === "postpone" ? "Postpone" : "Give more time"}
          onCancel={() => setPicking(null)}
          onPick={async (date) => {
            onProblem(null);
            const written = await action.mutateAsync({ kind: "later", path: entry.path, part: picking, date });
            setPicking(null);
            moved(picking)(written);
          }}
        />
      )}
    </>
  );
}

/** Pick a date…: the browser's own date input. A week is moved to the week of the day picked. */
function PickDate({ title, submitLabel, onCancel, onPick }: { title: string; submitLabel: string; onCancel(): void; onPick(date: string): Promise<unknown> }) {
  const id = useId();
  const [date, setDate] = useState("");
  return (
    <FormDialog title={title} submitLabel={submitLabel} ready={date !== ""} onCancel={onCancel} onSubmit={() => onPick(date)}>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-day`} className={labelClass}>
          Day
        </label>
        <input id={`${id}-day`} data-autofocus type="date" value={date} onChange={(e) => setDate(e.target.value)} className={inputClass} />
      </div>
    </FormDialog>
  );
}

/** What Later did: the entry before, which part moved, and the entry as it is now. */
export type Moved = { before: EntryView; part: Part; after: EntryView | null };

const SHOWN_MS = 8_000;

/** "Sun 15 Nov", or "Week 47" for a week. */
const dateWords = (value: string, day: string) => (isWeek(value) ? `Week ${weekOf(day).week}` : shortDay(day, true));

/** "Moved to Sun 15 Nov", or for More time "More time until Fri 20 Nov". */
export function movedText({ part, after }: Moved): string {
  if (!after) return "Moved";
  return part === "moreTime" && after.until ? `More time until ${dateWords(after.until, after.lastDay)}` : `Moved to ${dateWords(after.when, after.firstDay)}`;
}

/**
 * Later's short message (spec §7.13), as ✓ has "Closed · Undo": `onMoved` goes to LaterMenu, `toast` is the message.
 * It lives with the page, not the entry, which may have moved out of the list.
 */
export function useMoved() {
  const [moved, setMoved] = useState<{ moved: Moved; n: number } | null>(null);
  return {
    onMoved: (next: Moved) => setMoved((m) => ({ moved: next, n: (m?.n ?? 0) + 1 })),
    dismiss: () => setMoved(null),
    toast: moved && <MovedToast key={moved.n} moved={moved.moved} onDone={() => setMoved(null)} />,
  };
}

/**
 * "Moved to Sun 15 Nov · Undo", for 8 seconds or until dismissed. Undo puts it back: Postpone moves the entry to its
 * old date (a card's due through Later's own date), More time gives back the old end.
 */
function MovedToast({ moved, onDone }: { moved: Moved; onDone(): void }) {
  const action = useEntryAction();
  const [problem, setProblem] = useState<string | null>(null);
  const done = useRef(onDone);
  done.current = onDone;
  useEffect(() => {
    const timer = setTimeout(() => done.current(), SHOWN_MS);
    return () => clearTimeout(timer);
  }, []);

  const { before, part } = moved;
  const undo = () => {
    setProblem(null);
    const request =
      part === "moreTime" && before.until
        ? { kind: "set" as const, path: before.path, until: before.until }
        : before.kind === "card"
          ? { kind: "later" as const, path: before.path, part: "postpone" as const, date: before.when.slice(0, 10) }
          : { kind: "move" as const, path: before.path, when: before.when };
    action.mutate(request, { onSuccess: onDone, onError: (err) => setProblem(errorText(err)) });
  };

  return (
    <div
      role="status"
      aria-label="Moved"
      className="fixed inset-x-3 bottom-[max(12px,env(safe-area-inset-bottom))] z-50 mx-auto flex max-w-md flex-col gap-1 rounded-[12px] border border-line bg-surface-2 py-1 pr-1.5 pl-3.5 text-sm text-text shadow-lg"
    >
      <div className="flex flex-wrap items-center">
        <span>{movedText(moved)}</span>
        <span aria-hidden className="whitespace-pre">{" · "}</span>
        <button
          type="button"
          onClick={undo}
          disabled={action.isPending}
          className="min-h-12 px-1.5 font-medium text-accent-text underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-accent"
        >
          Undo
        </button>
        <button type="button" aria-label="Dismiss" onClick={onDone} className="ml-auto flex size-12 items-center justify-center rounded-[10px] text-muted focus-visible:outline-2 focus-visible:outline-accent">
          <X size={18} aria-hidden />
        </button>
      </div>
      {problem && <p className="pb-1 text-text">{problem}</p>}
    </div>
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowLeft, ChevronLeft, ChevronRight } from "lucide-react";
import { useEffect, useState } from "react";
import { Link, Navigate, useLocation, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { errorText } from "../api/error-text";
import { calendarUrl, noteUrl } from "../api/paths";
import { FormError, secondaryButtonClass } from "../components/form";
import { useEditor } from "../state/editor";
import { useCalendar, useCalendarAction, useCalendarViewState, useSetCalendarView, type CalendarView, type EntryView } from "./api";
import { addDays, formatWeekTitle, monthId, monthTitle, weekId, weekOf } from "./dates";
import { useClosing } from "./ClosedToast";
import CouldntRead from "./CouldntRead";
import type { EntryActions } from "./EntryItem";
import LaterMenu, { useMoved, type Moved } from "./LaterMenu";
import MonthGrid from "./MonthGrid";
import MonthList, { dayAnchor, weekAnchor } from "./MonthList";
import NewEntryDialog from "./NewEntryDialog";
import TagFilter, { useTag, withTag } from "./TagFilter";
import WeekList, { emptyWeek } from "./WeekList";

const EMPTY_WEEK = 'Nothing this week yet. Tap a day to add something, or give any note a day from its ⋯ menu. Your AI can do it too: "put this on Friday".';

const navClass =
  "flex size-12 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent";
const switchClass = (on: boolean) =>
  `min-h-12 px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${on ? "bg-surface-2 text-text" : "text-muted"}`;

/** "2026-11" ± a month. */
function shiftMonth(month: string, by: number) {
  const [year, m] = month.split("-").map(Number);
  const index = year * 12 + (m - 1) + by;
  return `${Math.floor(index / 12)}-${String((index % 12) + 1).padStart(2, "0")}`;
}

const monthUrl = (vault: string, month: string) => `${calendarUrl(vault)}?month=${month}`;

/**
 * `/calendar/<Vault>`: the vault's calendar, a view of every note in it with a time. `?week=2026-W46` or
 * `?month=2026-11`; neither: the view the person left this calendar in (spec §7.3), the week by default, at today.
 * `?tag=` narrows it to a tag.
 */
export default function CalendarPage() {
  const { vault = "" } = useParams();
  const [params] = useSearchParams();
  const { hash } = useLocation();
  const week = params.get("week") ?? undefined;
  const month = week ? undefined : (params.get("month") ?? undefined);
  const tag = useTag();
  const remembered = useCalendarViewState(vault);
  // Asked at once, the month too: this week is also what gives today, and the month's address needs it.
  const calendar = useCalendar(vault, { week, month }, tag);
  const view = calendar.data;
  const unnamed = !week && !month;

  if (unnamed && remembered.data?.view === "month" && view) return <Navigate replace to={withTag(monthUrl(vault, monthId(view.today)), tag) + hash} />;
  const waiting = calendar.isPending || (unnamed && remembered.isPending);
  const title = view && !waiting ? (month ? monthTitle(`${month}-01`) : view.weeks[0] ? formatWeekTitle(view.weeks[0]) : vault) : vault;

  return (
    <div className="relative flex h-dvh flex-col bg-bg">
      <header className="flex flex-none items-center gap-1 border-b border-line px-2 py-1.5 lg:px-3">
        <Link to="/" aria-label="Back to the notes" className="flex size-12 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent">
          <ArrowLeft size={20} aria-hidden />
        </Link>
        {/* Which calendar this is, above its week or month; the title wraps on a phone rather than losing its year. */}
        <div className="min-w-0 flex-1 px-2">
          {title !== vault && <p className="text-sm font-medium break-words text-muted">{vault}</p>}
          <h1 className="text-[20px] leading-tight font-bold break-words text-text">{title}</h1>
        </div>
      </header>
      {waiting && <p className="p-4 text-sm text-muted">Loading …</p>}
      {calendar.isError && <p className="p-4 text-text">{errorText(calendar.error)}</p>}
      {view && !waiting && <Calendar vault={vault} view={view} month={month} tag={tag} grid={remembered.data?.grid ?? false} />}
    </div>
  );
}

function Calendar({ vault, view, month, tag, grid }: { vault: string; view: CalendarView; month?: string; tag?: string; grid: boolean }) {
  const navigate = useNavigate();
  const { hash } = useLocation();
  const action = useCalendarAction(vault);
  const setView = useSetCalendarView(vault);
  const [adding, setAdding] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const shown = view.weeks[0];
  const shownMonth = month ?? (shown ? monthId(addDays(shown.monday, 3)) : monthId(view.today));

  /** Opens a note the calendar just made or found in Write mode, to write in at once. */
  const open = (path: string) => {
    useEditor.getState().openNote(path, "write");
    navigate(noteUrl(path));
  };
  const make = (kind: "today" | "this-week") => {
    setProblem(null);
    action.mutate({ kind }, { onSuccess: (written) => open(written.path), onError: (err) => setProblem(errorText(err)) });
  };
  const choose = (next: "week" | "month") => {
    setView.mutate({ view: next, grid });
    if (next === "month") return navigate(withTag(monthUrl(vault, shownMonth), tag));
    const inMonth = monthId(view.today) === shownMonth;
    navigate(withTag(calendarUrl(vault, weekId(inMonth || !shown ? weekOf(view.today) : weekOf(shown.monday))), tag));
  };
  const toggleGrid = () => setView.mutate({ view: month ? "month" : "week", grid: !grid });

  /** The month grid's day: in the month, the list jumps to it (or to its week, when nothing is on it); in the week, its week opens. */
  const jump = (day: string) => {
    if (!month) return navigate(withTag(calendarUrl(vault, weekId(weekOf(day))), tag));
    const target = document.getElementById(dayAnchor(day)) ?? document.getElementById(weekAnchor(weekOf(day)));
    target?.scrollIntoView?.({ block: "start" });
    target?.querySelector<HTMLElement>("h2, h3")?.focus({ preventScroll: true });
  };

  // What's on links to the dates it couldn't read, `#couldnt-read`: the router doesn't scroll to an anchor.
  const unread = view.couldntRead.length > 0;
  useEffect(() => {
    if (hash.length > 1) document.getElementById(decodeURIComponent(hash.slice(1)))?.scrollIntoView?.({ block: "start" });
  }, [hash, unread]);

  const closing = useClosing();
  const moving = useMoved();
  const say = (text: string | null) => {
    closing.setProblem(null);
    setProblem(text);
  };
  const onMoved = (moved: Moved) => {
    closing.dismiss();
    moving.onMoved(moved);
  };
  const actions: EntryActions | undefined = view.canWrite
    ? {
        onDone: (entry: EntryView) => {
          setProblem(null);
          moving.dismiss();
          closing.close(entry);
        },
        renderLater: (entry: EntryView) => <LaterMenu entry={entry} onProblem={say} onMoved={onMoved} />,
      }
    : undefined;
  const previous = month ? monthUrl(vault, shiftMonth(month, -1)) : shown && calendarUrl(vault, weekId(weekOf(addDays(shown.monday, -7))));
  const next = month ? monthUrl(vault, shiftMonth(month, 1)) : shown && calendarUrl(vault, weekId(weekOf(addDays(shown.monday, 7))));

  return (
    <>
      <nav aria-label="Calendar" className="flex flex-none flex-wrap items-center gap-1 border-b border-line px-2 py-1.5 lg:px-3">
        {previous && (
          <Link to={withTag(previous, tag)} aria-label={month ? "Previous month" : "Previous week"} className={navClass}>
            <ChevronLeft size={20} aria-hidden />
          </Link>
        )}
        {next && (
          <Link to={withTag(next, tag)} aria-label={month ? "Next month" : "Next week"} className={navClass}>
            <ChevronRight size={20} aria-hidden />
          </Link>
        )}
        {view.canWrite && (
          <>
            <button type="button" disabled={action.isPending} onClick={() => make("today")} className={secondaryButtonClass}>
              Today
            </button>
            <button type="button" disabled={action.isPending} onClick={() => make("this-week")} className={secondaryButtonClass}>
              This week
            </button>
          </>
        )}
        <div className="ml-auto flex flex-wrap items-center gap-1">
          <div role="group" aria-label="View" className="flex overflow-hidden rounded-[10px] border border-line">
            <button type="button" aria-pressed={!month} onClick={() => month && choose("week")} className={switchClass(!month)}>
              Week
            </button>
            <button type="button" aria-pressed={!!month} onClick={() => !month && choose("month")} className={switchClass(!!month)}>
              Month
            </button>
          </div>
          <button type="button" aria-pressed={grid} onClick={toggleGrid} className={`min-h-12 rounded-[10px] border border-line px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${grid ? "bg-surface-2 text-text" : "text-muted"}`}>
            Show grid
          </button>
          <TagFilter />
        </div>
      </nav>
      <main className="min-h-0 flex-1 overflow-y-auto px-3 py-3 lg:px-4">
        {(problem ?? closing.problem) && <FormError>{problem ?? closing.problem}</FormError>}
        {grid && (
          <div className="mb-4">
            {month ? <MonthGrid month={month} weeks={view.weeks} today={view.today} onDay={jump} /> : <WeekGrid vault={vault} month={shownMonth} tag={tag} onDay={jump} />}
          </div>
        )}
        {month ? (
          <MonthList vault={vault} weeks={view.weeks} actions={actions} tag={tag} />
        ) : (
          <div className="flex flex-col gap-6">
            {view.weeks.map((w) => (
              <WeekList key={w.monday} week={w} today={view.today} actions={actions} onAdd={view.canWrite ? setAdding : undefined} />
            ))}
          </div>
        )}
        {!month && view.weeks.every(emptyWeek) && <p className="mt-4 max-w-prose text-text-body">{view.canWrite ? EMPTY_WEEK : "Nothing this week yet."}</p>}
        <CouldntRead notes={view.couldntRead} canWrite={view.canWrite} />
      </main>
      {closing.toast}
      {moving.toast}
      {adding && (
        <NewEntryDialog
          day={adding}
          onCancel={() => setAdding(null)}
          onAdd={async (entry) => {
            const written = await action.mutateAsync({ kind: "entries", ...entry });
            setAdding(null);
            open(written.path);
          }}
        />
      )}
    </>
  );
}

/** The grid in the week view: the shown week's month, asked for on its own; a day opens its week. */
function WeekGrid({ vault, month, tag, onDay }: { vault: string; month: string; tag?: string; onDay(day: string): void }) {
  const calendar = useCalendar(vault, { month }, tag);
  if (!calendar.data) return null;
  return <MonthGrid month={month} weeks={calendar.data.weeks} today={calendar.data.today} onDay={onDay} />;
}

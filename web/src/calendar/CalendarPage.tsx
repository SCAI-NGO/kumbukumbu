// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowLeft, ChevronLeft, ChevronRight } from "lucide-react";
import { useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { errorText } from "../api/error-text";
import { calendarUrl, noteUrl } from "../api/paths";
import { FormError, secondaryButtonClass } from "../components/form";
import { useEditor } from "../state/editor";
import { useCalendar, useCalendarAction, useSetCalendarView, type CalendarView, type EntryView } from "./api";
import { addDays, formatWeekTitle, monthId, monthTitle, weekId, weekOf } from "./dates";
import { useClosing } from "./ClosedToast";
import type { EntryActions } from "./EntryItem";
import LaterMenu from "./LaterMenu";
import NewEntryDialog from "./NewEntryDialog";
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

/**
 * `/calendar/<Vault>`: the vault's calendar, a view of every note in it with a time. `?week=2026-W46` (the default:
 * this week) or `?month=2026-11`.
 */
export default function CalendarPage() {
  const { vault = "" } = useParams();
  const [params] = useSearchParams();
  const week = params.get("week") ?? undefined;
  const month = week ? undefined : (params.get("month") ?? undefined);
  const calendar = useCalendar(vault, { week, month });
  const view = calendar.data;
  const title = view ? (month ? monthTitle(`${month}-01`) : view.weeks[0] ? formatWeekTitle(view.weeks[0]) : vault) : vault;

  return (
    <div className="relative flex h-dvh flex-col bg-bg">
      <header className="flex flex-none items-center gap-1 border-b border-line px-2 py-1.5 lg:px-3">
        <Link to="/" aria-label="Back to the notes" className="flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent">
          <ArrowLeft size={20} aria-hidden />
        </Link>
        <h1 className="min-w-0 flex-1 truncate px-2 text-[20px] font-bold text-text">{title}</h1>
      </header>
      {calendar.isPending && <p className="p-4 text-sm text-muted">Loading …</p>}
      {calendar.isError && <p className="p-4 text-text">{errorText(calendar.error)}</p>}
      {view && <Calendar vault={vault} view={view} month={month} />}
    </div>
  );
}

function Calendar({ vault, view, month }: { vault: string; view: CalendarView; month?: string }) {
  const navigate = useNavigate();
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
    setView.mutate({ view: next, grid: false });
    if (next === "month") return navigate(`${calendarUrl(vault)}?month=${shownMonth}`);
    const inMonth = monthId(view.today) === shownMonth;
    navigate(calendarUrl(vault, weekId(inMonth || !shown ? weekOf(view.today) : weekOf(shown.monday))));
  };

  const closing = useClosing();
  const say = (text: string | null) => {
    closing.setProblem(null);
    setProblem(text);
  };
  const actions: EntryActions | undefined = view.canWrite
    ? {
        onDone: (entry: EntryView) => {
          setProblem(null);
          closing.close(entry);
        },
        renderLater: (entry: EntryView) => <LaterMenu entry={entry} onProblem={say} />,
      }
    : undefined;
  const previous = month ? `${calendarUrl(vault)}?month=${shiftMonth(month, -1)}` : shown && calendarUrl(vault, weekId(weekOf(addDays(shown.monday, -7))));
  const next = month ? `${calendarUrl(vault)}?month=${shiftMonth(month, 1)}` : shown && calendarUrl(vault, weekId(weekOf(addDays(shown.monday, 7))));

  return (
    <>
      <nav aria-label="Calendar" className="flex flex-none flex-wrap items-center gap-1 border-b border-line px-2 py-1.5 lg:px-3">
        {previous && (
          <Link to={previous} aria-label={month ? "Previous month" : "Previous week"} className={navClass}>
            <ChevronLeft size={20} aria-hidden />
          </Link>
        )}
        {next && (
          <Link to={next} aria-label={month ? "Next month" : "Next week"} className={navClass}>
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
        <div role="group" aria-label="View" className="ml-auto flex overflow-hidden rounded-[10px] border border-line">
          <button type="button" aria-pressed={!month} onClick={() => month && choose("week")} className={switchClass(!month)}>
            Week
          </button>
          <button type="button" aria-pressed={!!month} onClick={() => !month && choose("month")} className={switchClass(!!month)}>
            Month
          </button>
        </div>
      </nav>
      <main className="min-h-0 flex-1 overflow-y-auto px-3 py-3 lg:px-4">
        {(problem ?? closing.problem) && <FormError>{problem ?? closing.problem}</FormError>}
        <div className="flex flex-col gap-6">
          {view.weeks.map((w) => (
            <WeekList key={w.monday} week={w} today={view.today} actions={actions} onAdd={view.canWrite ? setAdding : undefined} />
          ))}
        </div>
        {!month && view.weeks.every(emptyWeek) && <p className="mt-4 max-w-prose text-text-body">{view.canWrite ? EMPTY_WEEK : "Nothing this week yet."}</p>}
      </main>
      {closing.toast}
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

// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowLeft, ChevronLeft, ChevronRight } from "lucide-react";
import { useEffect, useState } from "react";
import { Link, Navigate, useLocation, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { errorText } from "../api/error-text";
import { calendarUrl } from "../api/paths";
import { useOpenNote } from "../shell/openInPane";
import { FormError, secondaryButtonClass } from "../components/form";
import { useDesktop } from "../hooks/useDesktop";
import { topDialog } from "../hooks/useDialog";
import { useCalendar, useCalendarAction, useCalendarViewState, useEntryAction, useSetCalendarView, type CalendarView, type EntryView } from "./api";
import { cameFromHere, withCameFrom } from "./backLink";
import { addDays, formatWeekTitle, monthId, monthName, monthTitle, weekId, weekOf } from "./dates";
import { useClosing } from "./ClosedToast";
import CouldntRead from "./CouldntRead";
import type { EntryActions } from "./EntryItem";
import LaterMenu, { useMoved, type Moved } from "./LaterMenu";
import MonthGrid from "./MonthGrid";
import MonthList, { dayAnchor, weekAnchor } from "./MonthList";
import NewEntryDialog from "./NewEntryDialog";
import TagFilter, { useTagFilter, withTag } from "./TagFilter";
import WeekList, { emptyWeek } from "./WeekList";

const EMPTY_WEEK = 'Nothing this week yet. Tap a day to add something, or give any note a day from its ⋯ menu. Your AI can do it too: "put this on Friday".';

const navClass =
  "flex size-12 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent";
// The calendar's controls are 48 px; secondaryButtonClass is 44.
const makeClass = secondaryButtonClass.replace("min-h-11", "min-h-12");
const pageClass =
  "flex min-h-12 items-center rounded-[10px] border border-line px-4 font-medium text-text focus-visible:outline-2 focus-visible:outline-accent";
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
  const [tag, chooseTag] = useTagFilter();
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
      {view && !waiting && <Calendar vault={vault} view={view} month={month} tag={tag} chooseTag={chooseTag} grid={remembered.data?.grid ?? false} />}
    </div>
  );
}

function Calendar({ vault, view, month, tag, chooseTag, grid }: { vault: string; view: CalendarView; month?: string; tag?: string; chooseTag(next: string | null): void; grid: boolean }) {
  const navigate = useNavigate();
  const openNote = useOpenNote();
  const location = useLocation();
  const { hash } = location;
  const desktop = useDesktop();
  const action = useCalendarAction(vault);
  const setView = useSetCalendarView(vault);
  const [adding, setAdding] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const shown = view.weeks[0];
  const shownMonth = month ?? (shown ? monthId(addDays(shown.monday, 3)) : monthId(view.today));

  /** Opens a note the calendar just made or found in Write mode, to write in at once. */
  const open = (path: string) => {
    openNote(path, { mode: "write", state: withCameFrom(undefined, cameFromHere(location)) });
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
  const reopen = useEntryAction();
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
        onReopen: (entry: EntryView) => {
          say(null);
          moving.dismiss();
          closing.dismiss();
          reopen.mutate({ kind: "reopen", path: entry.path }, { onError: (err) => setProblem(errorText(err)) });
        },
        renderLater: (entry: EntryView) => <LaterMenu entry={entry} onProblem={say} onMoved={onMoved} />,
      }
    : undefined;
  const previous = month ? monthUrl(vault, shiftMonth(month, -1)) : shown && calendarUrl(vault, weekId(weekOf(addDays(shown.monday, -7))));
  const next = month ? monthUrl(vault, shiftMonth(month, 1)) : shown && calendarUrl(vault, weekId(weekOf(addDays(shown.monday, 7))));
  const previousLabel = month ? monthName(shiftMonth(month, -1)) : shown && `Week ${weekOf(addDays(shown.monday, -7)).week}`;
  const nextLabel = month ? monthName(shiftMonth(month, 1)) : shown && `Week ${weekOf(addDays(shown.monday, 7)).week}`;
  const away = month ? month !== monthId(view.today) : !!shown && !shown.current;
  const here = month ? monthUrl(vault, monthId(view.today)) : calendarUrl(vault, weekId(weekOf(view.today)));

  // ← and → turn the page where the tree is docked; not in a text field or a menu, which use the keys themselves,
  // nor under a dialog or with a modifier.
  useEffect(() => {
    if (!desktop || !previous || !next) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
      if (e.ctrlKey || e.metaKey || e.altKey || e.shiftKey || e.defaultPrevented) return;
      const target = e.target as HTMLElement | null;
      if (target?.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"], [role="menu"]')) return;
      if (topDialog()) return;
      e.preventDefault();
      navigate(withTag(e.key === "ArrowLeft" ? previous : next, tag));
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [desktop, previous, next, tag, navigate]);

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
        {away && (
          <Link to={withTag(here, tag)} className="flex min-h-12 items-center rounded-[10px] px-3 text-sm font-medium text-accent-text underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-accent">
            {month ? "Back to this month" : "Back to this week"}
          </Link>
        )}
        {view.canWrite && (
          <>
            <button type="button" disabled={action.isPending} onClick={() => make("today")} className={makeClass}>
              + Today
            </button>
            <button type="button" disabled={action.isPending} onClick={() => make("this-week")} className={makeClass}>
              + This week
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
          <TagFilter tag={tag} choose={chooseTag} />
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
        {previous && next && (
          <nav aria-label={month ? "Months before and after" : "Weeks before and after"} className="mt-6 flex flex-wrap items-center justify-center gap-3">
            <Link to={withTag(previous, tag)} className={pageClass}>
              <span aria-hidden>‹&nbsp;</span>
              {previousLabel}
            </Link>
            <Link to={withTag(next, tag)} className={pageClass}>
              {nextLabel}
              <span aria-hidden>&nbsp;›</span>
            </Link>
          </nav>
        )}
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

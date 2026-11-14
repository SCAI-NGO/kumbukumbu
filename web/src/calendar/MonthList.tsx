// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId } from "react";
import { Link } from "react-router-dom";
import { calendarUrl } from "../api/paths";
import type { WeekView } from "./api";
import { dayHeading, dayLabel, shortDay, shortWeekTitle, weekId } from "./dates";
import EntryItem, { type EntryActions } from "./EntryItem";
import { withTag } from "./TagFilter";
import { AlsoOngoing, emptyWeek, rangeOf } from "./WeekList";

/** The ids the month grid jumps to: a day shown in the list, else its week. */
export const dayAnchor = (day: string) => `day-${day}`;
export const weekAnchor = (week: { year: number; week: number }) => `week-${weekId(week)}`;

/**
 * A month as a stack of weeks (spec §7.4): each week a heading that opens it, the entries over several days on top,
 * then only the days with something on; an empty week says "nothing yet". The same on a phone and a desktop.
 */
export default function MonthList({ vault, weeks, actions, tag }: { vault: string; weeks: WeekView[]; actions?: EntryActions; tag?: string }) {
  const id = useId();
  return (
    <div className="mx-auto flex max-w-3xl flex-col gap-6">
      {weeks.map((w) => {
        const heading = `${id}-${w.monday}`;
        const days = w.days.filter((d) => d.entries.length > 0);
        return (
          <section key={w.monday} id={weekAnchor(w)} aria-labelledby={heading} className="flex flex-col gap-2">
            <h2 id={heading} tabIndex={-1} className="flex flex-wrap items-baseline gap-x-3 text-[15px] font-bold text-text focus-visible:outline-2 focus-visible:outline-accent">
              <Link
                to={withTag(calendarUrl(vault, weekId(w)), tag)}
                className="flex min-h-12 items-center underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-accent"
              >
                {shortWeekTitle(w)}
              </Link>
              {w.current && <span className="text-sm font-medium text-accent-text">this week</span>}
            </h2>
            {w.spanning.length > 0 && (
              <ul aria-label="More than a day" className="flex flex-col gap-1">
                {w.spanning.map((e) => (
                  <EntryItem key={e.path} entry={e} actions={actions} range={rangeOf(e)} />
                ))}
              </ul>
            )}
            {days.map((d) => {
              const dayId = `${heading}-${d.day}`;
              return (
                <section key={d.day} id={dayAnchor(d.day)} aria-labelledby={dayId} className="grid grid-cols-[4.5rem_minmax(0,1fr)] gap-x-2">
                  <h3
                    id={dayId}
                    tabIndex={-1}
                    aria-label={dayLabel(d.day, d.entries)}
                    className="pt-3.5 font-mono text-sm text-text-body focus-visible:outline-2 focus-visible:outline-accent"
                  >
                    {shortDay(d.day)}
                  </h3>
                  <div className="flex min-w-0 flex-col gap-1">
                    <ul aria-label={dayHeading(d.day)} className="flex flex-col gap-1">
                      {d.entries.map((e) => (
                        <EntryItem key={e.path} entry={e} actions={actions} />
                      ))}
                    </ul>
                    <AlsoOngoing entries={d.alsoOngoing} actions={actions} />
                  </div>
                </section>
              );
            })}
            {emptyWeek(w) && <p className="pl-1 text-sm text-muted">nothing yet</p>}
          </section>
        );
      })}
    </div>
  );
}

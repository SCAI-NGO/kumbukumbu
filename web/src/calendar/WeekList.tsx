// SPDX-License-Identifier: AGPL-3.0-or-later
import { Plus } from "lucide-react";
import { useId } from "react";
import type { EntryView, WeekView } from "./api";
import { dayHeading, dayLabel, partsOf, shortDay } from "./dates";
import EntryItem, { type EntryActions } from "./EntryItem";

/** The days an entry over more than one day covers: "Tue 3 – Sat 14", a week entry as "Week 47". */
export function rangeOf(entry: EntryView): string {
  const week = /^\d{4}-W(\d{2})$/.exec(entry.when);
  if (week && !entry.until) return `Week ${Number(week[1])}`;
  const months = partsOf(entry.firstDay).month !== partsOf(entry.lastDay).month;
  return `${shortDay(entry.firstDay, months)} – ${shortDay(entry.lastDay, months)}`;
}

/**
 * The entries over several days that touch a day, so a day shows everything on it (spec §7.2): collapsed, with their
 * own count, left out when there are none.
 */
export function AlsoOngoing({ entries, actions }: { entries: EntryView[]; actions?: EntryActions }) {
  if (entries.length === 0) return null;
  return (
    <details>
      <summary className="flex min-h-12 cursor-pointer items-center rounded-[10px] px-1 text-sm text-text-body focus-visible:outline-2 focus-visible:outline-accent">
        Also ongoing ({entries.length})
      </summary>
      <ul className="flex flex-col gap-1">
        {entries.map((e) => (
          <EntryItem key={e.path} entry={e} actions={actions} range={rangeOf(e)} />
        ))}
      </ul>
    </details>
  );
}

/** Whether a week has nothing on: no entry over the week, none on any day. */
export const emptyWeek = (week: WeekView) => week.spanning.length === 0 && week.days.every((d) => d.entries.length === 0);

/**
 * A week as a document: the entries over more than a day on top, then the seven days, each a heading with its entries
 * beneath. Seven columns on a wide screen, stacked on a phone, from the same markup. With `onAdd`, each day has its +,
 * and its empty space does the same.
 */
export default function WeekList(props: { week: WeekView; today: string; actions?: EntryActions; onAdd?: (day: string) => void }) {
  const { week, today, actions, onAdd } = props;
  const id = useId();
  return (
    <section aria-label={`Week ${week.week}`} className="flex flex-col gap-3">
      {week.spanning.length > 0 && (
        <ul aria-label="More than a day" className="flex flex-col gap-1">
          {week.spanning.map((e) => (
            <EntryItem key={e.path} entry={e} actions={actions} range={rangeOf(e)} />
          ))}
        </ul>
      )}
      <div className="grid grid-cols-1 gap-3 lg:grid-cols-7 lg:gap-2">
        {week.days.map((d, i) => {
          const heading = dayHeading(d.day);
          const isToday = d.day === today;
          return (
            <section key={d.day} aria-labelledby={`${id}-${i}`} className="flex min-w-0 flex-col gap-1 rounded-[12px] border border-line p-2">
              <div className="flex items-center gap-1">
                <h2
                  id={`${id}-${i}`}
                  aria-label={dayLabel(d.day, d.entries)}
                  aria-current={isToday ? "date" : undefined}
                  className={`min-w-0 flex-1 px-1 text-[15px] font-bold ${isToday ? "text-accent-text" : "text-text"}`}
                >
                  {heading}
                </h2>
                {onAdd && (
                  <button
                    type="button"
                    aria-label={`Add something on ${heading}`}
                    onClick={() => onAdd(d.day)}
                    className="flex size-12 flex-none items-center justify-center rounded-[10px] text-muted hover:text-text focus-visible:outline-2 focus-visible:outline-accent"
                  >
                    <Plus aria-hidden size={20} />
                  </button>
                )}
              </div>
              <ul aria-label={heading} className="flex flex-col gap-1">
                {d.entries.map((e) => (
                  <EntryItem key={e.path} entry={e} actions={actions} />
                ))}
              </ul>
              <AlsoOngoing entries={d.alsoOngoing} actions={actions} />
              {/* A tap on the day's empty space makes an entry too; keyboards and screen readers have the + for it. */}
              {onAdd && <button type="button" tabIndex={-1} aria-hidden="true" onClick={() => onAdd(d.day)} className="min-h-12 w-full flex-1 cursor-pointer" />}
            </section>
          );
        })}
      </div>
    </section>
  );
}

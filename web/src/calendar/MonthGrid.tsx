// SPDX-License-Identifier: AGPL-3.0-or-later
import type { WeekView } from "./api";
import { monthId, monthTitle, partsOf, weekend } from "./dates";

const WEEKDAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

/**
 * The small month grid behind Show grid (spec §7.4): which weekday a date is, whether something is on. A plain table,
 * a week-number column, the weekend darkened, the days with entries marked (a dot, and in words), today marked; the
 * days of other months left blank. No arrow-key grid logic: the list is its full equivalent.
 */
export default function MonthGrid({ month, weeks, today, onDay }: { month: string; weeks: WeekView[]; today: string; onDay(day: string): void }) {
  return (
    <table className="w-full max-w-md table-fixed border-collapse text-center text-sm">
      <caption className="pb-1 text-left text-sm font-bold text-text">{monthTitle(`${month}-01`)}</caption>
      <thead>
        <tr className="text-xs text-muted">
          <th scope="col" className="w-9 py-1 font-medium">
            <span aria-hidden>Wk</span>
            <span className="sr-only">Week</span>
          </th>
          {WEEKDAYS.map((d) => (
            <th key={d} scope="col" className="py-1 font-medium">
              {d}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {weeks.map((w) => (
          <tr key={w.monday}>
            <th scope="row" className="font-mono text-xs font-normal text-dim">
              {w.week}
            </th>
            {w.days.map((d) => {
              const has = d.entries.length > 0;
              return (
                <td key={d.day} className={`p-0.5 ${weekend(d.day) ? "bg-surface-2" : ""}`}>
                  {monthId(d.day) === month && (
                    <button
                      type="button"
                      aria-current={d.day === today ? "date" : undefined}
                      onClick={() => onDay(d.day)}
                      className={`relative flex min-h-12 w-full items-center justify-center rounded-[8px] font-mono focus-visible:outline-2 focus-visible:outline-accent ${
                        d.day === today ? "bg-accent font-bold text-on-accent" : "text-text hover:bg-surface"
                      }`}
                    >
                      {partsOf(d.day).day}
                      {has && (
                        <>
                          <span aria-hidden className="absolute bottom-1.5 size-1.5 rounded-full bg-current" />
                          <span className="sr-only">, has entries</span>
                        </>
                      )}
                    </button>
                  )}
                </td>
              );
            })}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

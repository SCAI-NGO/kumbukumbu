// SPDX-License-Identifier: AGPL-3.0-or-later
import { CalendarDays, ChevronRight, ChevronsRight } from "lucide-react";
import { useId, useState } from "react";
import FormDialog from "../board/FormDialog";
import { inputClass, labelClass } from "../components/form";
import { Menu, type MenuItem } from "../components/Menu";
import { errorText } from "../api/error-text";
import { useEntryAction, type EntryView, type LaterRequest } from "./api";

type Part = LaterRequest["part"];

const isWeek = (value: string) => /^\d{4}-W\d{2}$/.test(value);

export const laterButtonClass =
  "flex min-h-12 flex-none items-center rounded-[10px] px-3 text-sm font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent";

/**
 * Later beside ✓ (spec §7.13): +1 day, +1 week or a picked day, each counted from the later of today and the entry's
 * date, so nothing moves earlier. A to-do with an end offers Postpone (all of it) and More time (only its end), each
 * under its heading; without one, or on a board card, the three alone. A week has no +1 day.
 */
export default function LaterMenu({ entry, onProblem, className = laterButtonClass }: { entry: EntryView; onProblem(text: string | null): void; className?: string }) {
  const action = useEntryAction();
  const [picking, setPicking] = useState<Part | null>(null);

  const later = (part: Part, step: "day" | "week") => {
    onProblem(null);
    action.mutate({ kind: "later", path: entry.path, part, step }, { onError: (err) => onProblem(errorText(err)) });
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
            await action.mutateAsync({ kind: "later", path: entry.path, part: picking, date });
            setPicking(null);
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

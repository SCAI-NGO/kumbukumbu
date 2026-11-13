// SPDX-License-Identifier: AGPL-3.0-or-later
import { CalendarClock, CalendarPlus, CalendarX } from "lucide-react";
import { useId, useState, type ReactNode } from "react";
import { errorText } from "../api/error-text";
import { fileKindOf } from "../api/paths";
import FormDialog from "../board/FormDialog";
import { inputClass, labelClass } from "../components/form";
import type { MenuItem } from "../components/Menu";
import { useEntryAction, useNoteEntry, type CouldntReadView, type SetEntryRequest } from "./api";
import { weekId, weekOf } from "./dates";

const switchClass = (on: boolean) =>
  `min-h-11 px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${on ? "bg-surface-2 text-text" : "text-muted"}`;

/** Whether the browser has its own week input; where it hasn't, a day stands for its week. */
const weekInput = (() => {
  if (typeof document === "undefined") return false;
  const input = document.createElement("input");
  input.setAttribute("type", "week");
  return input.type === "week";
})();

const WEEK = /^\d{4}-W\d{2}$/;

/** What the note has, as the dialog's fields hold it. */
export type DateFields = { when: string; until: string; task: boolean; upcoming: string };

/** "7d", "2w", "P7D" as days; an hour span ("12h") has no whole days and stays as it is unless the field is changed. */
function daysOf(upcoming: string | null | undefined): string {
  const m = /^(?:P)?(\d+)([dDwW])$/.exec(upcoming?.trim() ?? "");
  if (!m) return "";
  return String(Number(m[1]) * (m[2].toLowerCase() === "w" ? 7 : 1));
}

const dayOf = (value: string) => (WEEK.test(value) ? "" : value.slice(0, 10));
const timeOf = (value: string) => (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(value) ? value.slice(11, 16) : "");

/**
 * The fields' values as `set` takes them: `when` always, `until` or its removal, To do only when it changed, Show
 * early only when the field changed (as days; emptied, it goes).
 */
export function setRequestOf(path: string, initial: DateFields, fields: { week: boolean; whenDay: string; whenTime: string; whenWeek: string; untilDay: string; untilTime: string; untilWeek: string; task: boolean; early: string }): SetEntryRequest {
  const when = fields.week ? fields.whenWeek : fields.whenTime ? `${fields.whenDay}T${fields.whenTime}` : fields.whenDay;
  const until = fields.week
    ? fields.untilWeek
    : fields.whenTime && (fields.untilDay || fields.untilTime)
      ? `${fields.untilDay || fields.whenDay}T${fields.untilTime || fields.whenTime}`
      : fields.untilDay;
  const request: SetEntryRequest = { path, when };
  if (until) request.until = until;
  else if (initial.until) request.clearUntil = true;
  if (fields.task !== initial.task) request.task = fields.task;
  const early = Number.parseInt(fields.early, 10);
  if (fields.task && fields.early !== daysOf(initial.upcoming)) {
    if (early > 0) request.upcoming = `${early}d`;
    else if (initial.upcoming) request.clearUpcoming = true;
  }
  return request;
}

/**
 * Add to calendar… and Edit date… (spec §7.8): When (a day, optionally with a time, or a week), Until (optional, of
 * the same kind), ☐ To do and, for a to-do, Show early. The same words as a day's +. No folder: the note stays where it
 * is. Opens with what the note has; `initial` (Fix…, what could be read) instead of it.
 */
export default function DateDialog({ path, initial: given, onClose }: { path: string; initial?: Partial<DateFields>; onClose(): void }) {
  const entry = useNoteEntry(path);
  if (!given && entry.isPending) return null;
  const has = entry.data?.entry;
  const couldntRead = entry.data?.couldntRead;
  const initial = given ?? (couldntRead && !has ? readableFields(couldntRead) : undefined);
  const fields: DateFields = {
    when: initial?.when ?? has?.when ?? "",
    until: initial?.until ?? has?.until ?? "",
    task: initial?.task ?? (has ? has.task !== "none" : false),
    upcoming: initial?.upcoming ?? has?.upcoming ?? "",
  };
  return <DateForm path={path} initial={fields} editing={!!has || !!couldntRead} onClose={onClose} />;
}

/** What Fix… and Edit date… open the dialog with for a date that couldn't be read: what could. */
export const readableFields = (couldntRead: CouldntReadView): Partial<DateFields> => ({
  when: couldntRead.readable.when ?? "",
  until: couldntRead.readable.until ?? "",
  task: couldntRead.readable.task === "open" || couldntRead.readable.task === "closed",
  upcoming: couldntRead.readable.upcoming ?? "",
});

/**
 * The note menu's calendar items (spec §7.8), for one who may change the note: Add to calendar…, or for an entry Edit
 * date… and Remove from calendar. A day or week note by its name has no Remove; a board card none of them, its date
 * and status are the board's. `known` is the calendar's answer for the note while it is asked for, the menu offers
 * Add, not yet to be chosen; when it can't be asked, Add, whose dialog asks again.
 */
export function calendarItems(
  path: string,
  known: { data?: { entry: { kind: string; byName: boolean } | null; couldntRead: unknown; canWrite: boolean }; isError: boolean },
  onDate: (path: string) => void,
  onRemove: (path: string) => void,
): MenuItem[] {
  if (fileKindOf(path) !== "note" || !path.toLowerCase().endsWith(".md") || /^\/[^/]+\/Board\/Board\.md$/.test(path)) return [];
  const add: MenuItem = { label: "Add to calendar…", icon: CalendarPlus, onSelect: () => onDate(path) };
  const view = known.data;
  if (!view) return [known.isError ? add : { ...add, disabled: true }];
  if (!view.canWrite || view.entry?.kind === "card") return [];
  if (!view.entry && !view.couldntRead) return [add];
  return [
    { label: "Edit date…", icon: CalendarClock, onSelect: () => onDate(path) },
    ...(view.entry?.byName ? [] : [{ label: "Remove from calendar", icon: CalendarX, onSelect: () => onRemove(path) }]),
  ];
}

/** Remove from calendar: at once, saying afterwards how it went, as the menu that offered it has closed. */
export function useRemoveFromCalendar(say: (text: string) => void) {
  const action = useEntryAction();
  return (path: string) =>
    action.mutate({ kind: "unset", path }, { onSuccess: () => say("Removed from the calendar."), onError: (err) => say(errorText(err)) });
}

function DateForm({ path, initial, editing, onClose }: { path: string; initial: DateFields; editing: boolean; onClose(): void }) {
  const id = useId();
  const action = useEntryAction();
  const [week, setWeek] = useState(WEEK.test(initial.when));
  const [whenDay, setWhenDay] = useState(dayOf(initial.when));
  const [whenTime, setWhenTime] = useState(timeOf(initial.when));
  const [whenWeek, setWhenWeek] = useState(WEEK.test(initial.when) ? initial.when : "");
  const [untilDay, setUntilDay] = useState(dayOf(initial.until));
  const [untilTime, setUntilTime] = useState(timeOf(initial.until));
  const [untilWeek, setUntilWeek] = useState(WEEK.test(initial.until) ? initial.until : "");
  const [task, setTask] = useState(initial.task);
  const [early, setEarly] = useState(daysOf(initial.upcoming));
  const ready = week ? whenWeek !== "" : whenDay !== "";

  return (
    <FormDialog
      title={editing ? "Edit date" : "Add to calendar"}
      submitLabel="Save"
      ready={ready}
      onCancel={onClose}
      onSubmit={async () => {
        await action.mutateAsync({ kind: "set", ...setRequestOf(path, initial, { week, whenDay, whenTime, whenWeek, untilDay, untilTime, untilWeek, task, early }) });
        onClose();
      }}
    >
      <div role="group" aria-label="A day or a week" className="flex self-start overflow-hidden rounded-[10px] border border-line">
        <button type="button" aria-pressed={!week} onClick={() => setWeek(false)} className={switchClass(!week)}>
          Day
        </button>
        <button type="button" aria-pressed={week} onClick={() => setWeek(true)} className={switchClass(week)}>
          Week
        </button>
      </div>
      <Fieldset legend="When">
        {week ? (
          <WeekField id={`${id}-when-week`} value={whenWeek} onChange={setWhenWeek} autoFocus />
        ) : (
          <>
            <Field id={`${id}-when-day`} label="Day" type="date" value={whenDay} onChange={setWhenDay} autoFocus />
            <Field id={`${id}-when-time`} label="Time" type="time" value={whenTime} onChange={setWhenTime} />
          </>
        )}
      </Fieldset>
      <Fieldset legend="Until" hint="optional">
        {week ? (
          <WeekField id={`${id}-until-week`} value={untilWeek} onChange={setUntilWeek} />
        ) : (
          <>
            <Field id={`${id}-until-day`} label="Day" type="date" value={untilDay} onChange={setUntilDay} />
            {whenTime && <Field id={`${id}-until-time`} label="Time" type="time" value={untilTime} onChange={setUntilTime} />}
          </>
        )}
      </Fieldset>
      <div className="flex flex-col">
        <label className="flex min-h-12 items-center gap-3 text-text">
          <input type="checkbox" checked={task} onChange={(e) => setTask(e.target.checked)} aria-describedby={`${id}-task`} className="size-5 accent-accent" />
          To do
        </label>
        <span id={`${id}-task`} className="pl-8 text-sm text-muted">
          something to get done, with a ✓
        </span>
      </div>
      {task && (
        <div className="flex flex-wrap items-center gap-2 text-text">
          <span id={`${id}-early`}>Show early:</span>
          <input
            type="number"
            min={1}
            inputMode="numeric"
            aria-labelledby={`${id}-early ${id}-before`}
            value={early}
            onChange={(e) => setEarly(e.target.value)}
            className={`${inputClass} w-24`}
          />
          <span id={`${id}-before`}>days before</span>
        </div>
      )}
    </FormDialog>
  );
}

function Fieldset({ legend, hint, children }: { legend: string; hint?: string; children: ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-2">
      <legend className={`${labelClass} mb-1.5`}>
        {legend}
        {hint && <span className="normal-case tracking-normal"> ({hint})</span>}
      </legend>
      <div className="flex flex-wrap gap-2">{children}</div>
    </fieldset>
  );
}

function Field(props: { id: string; label: string; type: "date" | "time" | "week"; value: string; onChange(value: string): void; autoFocus?: boolean }) {
  return (
    <div className="flex min-w-0 flex-1 flex-col gap-1">
      <label htmlFor={props.id} className="text-sm text-text-body">
        {props.label}
      </label>
      <input
        id={props.id}
        type={props.type}
        data-autofocus={props.autoFocus ? true : undefined}
        value={props.value}
        onChange={(e) => props.onChange(e.target.value)}
        className={inputClass}
      />
    </div>
  );
}

/** A week: the browser's week input, or a day whose ISO week is taken. */
function WeekField({ id, value, onChange, autoFocus }: { id: string; value: string; onChange(value: string): void; autoFocus?: boolean }) {
  // Without a week input, the day picked stays shown; its week is what is written.
  const [day, setDay] = useState("");
  if (weekInput) return <Field id={id} label="Week" type="week" value={value} onChange={onChange} autoFocus={autoFocus} />;
  return (
    <Field
      id={id}
      label="A day in the week"
      type="date"
      value={day}
      onChange={(picked) => {
        setDay(picked);
        onChange(picked ? weekId(weekOf(picked)) : "");
      }}
      autoFocus={autoFocus}
    />
  );
}

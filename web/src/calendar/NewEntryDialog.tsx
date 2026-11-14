// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import FormDialog from "../board/FormDialog";
import { narrowInputClass, inputClass, labelClass } from "../components/form";
import type { NewEntryRequest } from "./api";
import { dayHeading } from "./dates";

/** What the dialog sends: a time and Show early only when given, Show early only for a to-do. */
export function newEntryOf(day: string, title: string, time: string, task: boolean, early: string): NewEntryRequest {
  const days = Number.parseInt(early, 10);
  return {
    day,
    title: title.trim(),
    ...(time ? { time } : {}),
    task,
    ...(task && days > 0 ? { upcoming: `${days}d` } : {}),
  };
}

/** A day's +: a title and an optional time, ☐ To do, and for a to-do how early to show it. Nothing more. */
export default function NewEntryDialog({ day, onCancel, onAdd }: { day: string; onCancel(): void; onAdd(entry: NewEntryRequest): Promise<unknown> }) {
  const id = useId();
  const [title, setTitle] = useState("");
  const [time, setTime] = useState("");
  const [task, setTask] = useState(false);
  const [early, setEarly] = useState("");
  return (
    <FormDialog
      title={`Add something on ${dayHeading(day)}`}
      submitLabel="Add"
      ready={title.trim() !== ""}
      onCancel={onCancel}
      onSubmit={() => onAdd(newEntryOf(day, title, time, task, early))}
    >
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-title`} className={labelClass}>
          Title
        </label>
        <input id={`${id}-title`} data-autofocus value={title} onChange={(e) => setTitle(e.target.value)} autoComplete="off" className={inputClass} />
      </div>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-time`} className={labelClass}>
          Time
        </label>
        <input id={`${id}-time`} type="time" value={time} onChange={(e) => setTime(e.target.value)} className={inputClass} />
      </div>
      <label className="flex min-h-12 items-center gap-3 text-text">
        <input type="checkbox" checked={task} onChange={(e) => setTask(e.target.checked)} className="size-5 accent-accent" />
        To do
      </label>
      {task && (
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-text">
          <span id={`${id}-early`}>Show early:</span>
          {/* The number and its unit stay together on one line, the field only as wide as a few digits. */}
          <span className="flex flex-none items-center gap-2">
            <input
              type="number"
              min={1}
              inputMode="numeric"
              aria-labelledby={`${id}-early ${id}-before`}
              value={early}
              onChange={(e) => setEarly(e.target.value)}
              className={narrowInputClass}
            />
            <span id={`${id}-before`} className="whitespace-nowrap">
              days before
            </span>
          </span>
        </div>
      )}
    </FormDialog>
  );
}

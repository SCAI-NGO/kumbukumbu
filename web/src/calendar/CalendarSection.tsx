// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useId, useMemo, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { browserZone, defaultDays, knownZones, useCalendarPreferences, useSetDays, useSetZone, type DayLevel } from "./preferences";

const shown = 8;

const WEEKDAY_NAMES = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const LEVELS: [DayLevel, string][] = [
  ["workday", "Workday"],
  ["lighter", "Lighter day"],
  ["off", "Day off"],
];
// As the calendar's Week | Month, but the chosen level in the accent: with three to a row, a shade alone hardly showed.
const switchClass = (on: boolean) =>
  `min-h-12 px-3 text-sm focus-visible:outline-2 focus-visible:outline-accent ${on ? "bg-accent font-bold text-on-accent" : "font-medium text-muted"}`;
// The calendar's controls are 48 px; secondaryButtonClass is 44.
const resetClass = secondaryButtonClass.replace("min-h-11", "min-h-12");

/** "new york" finds America/New_York: case and underscores don't matter. */
const plain = (text: string) => text.toLowerCase().replace(/_/g, " ");

/**
 * Settings → Calendar: the zone the calendar's times are in, searched from the browser's list where it has one, and
 * the person's week.
 */
export default function CalendarSection() {
  const id = useId();
  const preferences = useCalendarPreferences();
  const set = useSetZone();
  const zones = useMemo(knownZones, []);
  const zone = preferences.data?.zone ?? null;
  const [text, setText] = useState("");
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (preferences.data) setText(preferences.data.zone ?? browserZone() ?? "");
  }, [preferences.data]);

  const query = plain(text.trim());
  const matches = zones && query && !zones.includes(text.trim()) ? zones.filter((z) => plain(z).includes(query)).slice(0, shown) : [];

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const chosen = text.trim();
    if (!chosen) return;
    setSaved(false);
    set.mutate(chosen, { onSuccess: () => setSaved(true) });
  };

  return (
    <div className="flex flex-col gap-6">
      <form onSubmit={submit} className="flex flex-col gap-3">
        <p className="text-text-body">{zone ? `Your calendar shows times in ${zone}.` : "Choose the time zone your calendar's times are in."}</p>
        <div className="flex flex-col gap-1">
          <label htmlFor={`${id}-zone`} className={labelClass}>
            Your time zone
          </label>
          <input
            id={`${id}-zone`}
            type={zones ? "search" : "text"}
            value={text}
            disabled={!preferences.data}
            autoComplete="off"
            spellCheck={false}
            onChange={(e) => {
              setText(e.target.value);
              setSaved(false);
              set.reset();
            }}
            className={inputClass}
          />
        </div>
        {matches.length > 0 && (
          <ul aria-label="Matching time zones" className="flex flex-col gap-1">
            {matches.map((z) => (
              <li key={z}>
                <button type="button" onClick={() => setText(z)} className={secondaryButtonClass}>
                  {z}
                </button>
              </li>
            ))}
          </ul>
        )}
        {preferences.isError && <FormError>{errorText(preferences.error)}</FormError>}
        {set.isError && <FormError>{errorText(set.error)}</FormError>}
        {saved && (
          <p role="status" className="text-text">
            Saved.
          </p>
        )}
        <div>
          <button type="submit" disabled={!preferences.data || set.isPending || !text.trim()} className={primaryButtonClass}>
            Save
          </button>
        </div>
      </form>
      <YourWeek />
    </div>
  );
}

/** Seven rows, each a switch saved at once; until the first tap, the browser's weekend is shown and nothing stored. */
function YourWeek() {
  const id = useId();
  const preferences = useCalendarPreferences();
  const set = useSetDays();
  const stored = preferences.data?.days;
  const week = stored ?? defaultDays();
  const choose = (i: number, level: DayLevel) => {
    if (week[i] !== level) set.mutate(week.map((l, j) => (j === i ? level : l)));
  };

  return (
    <div role="group" aria-labelledby={`${id}-week`} className="flex flex-col gap-2">
      <h3 id={`${id}-week`} className={labelClass}>
        Your week
      </h3>
      <p className="text-text-body">Days off are shaded in your calendar, lighter days lightly.</p>
      {WEEKDAY_NAMES.map((name, i) => (
        <div key={name} role="group" aria-label={name} className="flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between sm:gap-2">
          <span className="text-text">{name}</span>
          <div className="flex self-start overflow-hidden rounded-[10px] border border-line sm:self-auto">
            {LEVELS.map(([level, word]) => (
              <button
                key={level}
                type="button"
                aria-pressed={week[i] === level}
                disabled={!preferences.data || set.isPending}
                onClick={() => choose(i, level)}
                className={switchClass(week[i] === level)}
              >
                {word}
              </button>
            ))}
          </div>
        </div>
      ))}
      {stored && (
        <div>
          <button type="button" onClick={() => set.mutate(null)} disabled={set.isPending} className={resetClass}>
            Use my browser's weekend
          </button>
        </div>
      )}
      {set.isError && <FormError>{errorText(set.error)}</FormError>}
    </div>
  );
}

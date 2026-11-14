// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useId, useMemo, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { browserZone, knownZones, useCalendarPreferences, useSetZone } from "./preferences";

const shown = 8;

/** "new york" finds America/New_York: case and underscores don't matter. */
const plain = (text: string) => text.toLowerCase().replace(/_/g, " ");

/** Settings → Calendar: the zone the calendar's times are in, searched from the browser's list where it has one. */
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
  );
}

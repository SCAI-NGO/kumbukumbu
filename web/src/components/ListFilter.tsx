// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useMemo, useState, type ReactNode } from "react";
import { inputClass, labelClass, secondaryButtonClass } from "./form";

export type Status = { value: string; label: string };

const ALL = "all";

/**
 * Filters a list by a text and a single status. The state lives with the caller, so it resets when the view closes;
 * it is kept across changes of `items` (a refetch after granting, revoking, …).
 */
export function useListFilter<T>(
  items: readonly T[],
  options: {
    name(item: T): string;
    status(item: T): string;
    statuses: readonly Status[];
    searchLabel: string;
    noun: string;
  },
): { visible: T[]; bar: ReactNode } {
  const { name, status, statuses, searchLabel, noun } = options;
  const [text, setText] = useState("");
  const [chosen, setChosen] = useState(ALL);
  const groupName = useId();

  const needle = text.trim().toLocaleLowerCase();
  const matching = useMemo(
    () => (needle ? items.filter((item) => name(item).toLocaleLowerCase().includes(needle)) : [...items]),
    [items, needle, name],
  );
  const counts = useMemo(() => {
    const byStatus = new Map<string, number>();
    for (const item of matching) byStatus.set(status(item), (byStatus.get(status(item)) ?? 0) + 1);
    return byStatus;
  }, [matching, status]);
  const visible = useMemo(
    () => (chosen === ALL ? matching : matching.filter((item) => status(item) === chosen)),
    [matching, chosen, status],
  );

  if (items.length === 0) return { visible: [], bar: null };

  const choices = [{ value: ALL, label: "All", count: matching.length }, ...statuses.map((s) => ({ ...s, count: counts.get(s.value) ?? 0 }))];
  const hidden = visible.length < items.length;

  const bar = (
    <div className="flex flex-col gap-3">
      <input
        type="search"
        aria-label={searchLabel}
        placeholder={searchLabel}
        value={text}
        onChange={(e) => setText(e.target.value)}
        className={inputClass}
      />
      <fieldset className="m-0 min-w-0 border-0 p-0">
        <legend className={`${labelClass} mb-1.5`}>Status</legend>
        <div className="flex flex-wrap gap-2">
          {choices.map((c) => (
            <label
              key={c.value}
              className="min-h-11 cursor-pointer rounded-full border border-line bg-surface px-4 py-2.5 text-sm text-text has-[:checked]:border-accent has-[:checked]:bg-surface-2 has-[:checked]:text-accent-text has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-accent"
            >
              <input
                type="radio"
                name={groupName}
                value={c.value}
                checked={chosen === c.value}
                onChange={() => setChosen(c.value)}
                className="sr-only"
              />
              {c.label} {c.count}
            </label>
          ))}
        </div>
      </fieldset>
      {hidden && (
        <p className="flex flex-wrap items-center gap-3 text-sm text-text-body">
          <span>{`Showing ${visible.length} of ${items.length}`}</span>
          <button
            type="button"
            className={secondaryButtonClass}
            onClick={() => {
              setText("");
              setChosen(ALL);
            }}
          >
            Show all
          </button>
        </p>
      )}
      {visible.length === 0 && <p className="text-text-body">{`No ${noun} match.`}</p>}
    </div>
  );
  return { visible, bar };
}

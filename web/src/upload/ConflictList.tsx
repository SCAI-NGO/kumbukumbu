// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { secondaryButtonClass } from "../components/form";

const choiceButtonClass = `${secondaryButtonClass} flex-1`;

/**
 * The files whose notes exist already, before any of them is written: replace all, skip all, or replace the ones
 * ticked. `onDecide` gets the paths to replace.
 */
export default function ConflictList({ paths, waiting, onDecide }: { paths: string[]; waiting: number; onDecide(replace: Set<string>): void }) {
  const [ticked, setTicked] = useState<Set<string>>(new Set());
  const toggle = (path: string) =>
    setTicked((s) => {
      const next = new Set(s);
      if (!next.delete(path)) next.add(path);
      return next;
    });

  return (
    <fieldset className="flex min-h-0 flex-col gap-2.5">
      <legend className="text-[17px] font-bold text-text">{paths.length === 1 ? "1 note exists already" : `${paths.length} notes exist already`}</legend>
      <p className="text-sm text-text-body">Nothing is replaced until you choose. A replaced note keeps its old version in the history.</p>
      {waiting > 0 && <p className="text-sm text-text-body">{waiting === 1 ? "1 new note waits for this choice" : `${waiting} new notes wait for this choice`}</p>}
      <ul className="flex max-h-60 flex-col overflow-y-auto rounded-[10px] border border-line">
        {paths.map((path) => (
          <li key={path} className="border-b border-line last:border-b-0">
            <label className="flex min-h-11 items-center gap-3 px-3 py-1.5 text-text">
              <input type="checkbox" aria-label={`Replace ${path}`} checked={ticked.has(path)} onChange={() => toggle(path)} className="size-5 flex-none accent-accent" />
              <span className="min-w-0 flex-1 truncate font-mono text-[13px]">{path}</span>
            </label>
          </li>
        ))}
      </ul>
      <div className="flex flex-wrap gap-2">
        <button type="button" onClick={() => onDecide(new Set(paths))} className={choiceButtonClass}>
          Replace all
        </button>
        <button type="button" data-autofocus onClick={() => onDecide(new Set())} className={choiceButtonClass}>
          Skip all
        </button>
      </div>
      <button
        type="button"
        disabled={ticked.size === 0}
        onClick={() => onDecide(ticked)}
        className="min-h-11 rounded-[10px] bg-accent px-4 font-medium text-on-accent disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
      >
        {`Replace ${ticked.size} selected`}
      </button>
    </fieldset>
  );
}

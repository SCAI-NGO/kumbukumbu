// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { Link } from "react-router-dom";
import { noteUrl } from "../api/paths";
import type { CouldntReadView } from "./api";
import DateDialog, { readableFields } from "./DateDialog";

/** "`when: tomorrow` – I need a date like 2026-11-20", the value as code. */
function Plain({ text }: { text: string }) {
  return (
    <>
      {text.split("`").map((part, i) =>
        i % 2 === 1 ? (
          <code key={i} className="rounded-[4px] bg-surface-2 px-1 font-mono text-[13px] text-text">
            {part}
          </code>
        ) : (
          part
        ),
      )}
    </>
  );
}

/**
 * Dates I couldn't read (spec §7.12): each note with what is wrong in plain words and, for one who may write, Fix…,
 * which opens the date dialog with what could be read. The note's title opens it. What's on links here,
 * `#couldnt-read`. Left out when every date could be read.
 */
export default function CouldntRead({ notes, canWrite }: { notes: CouldntReadView[]; canWrite: boolean }) {
  const [fixing, setFixing] = useState<CouldntReadView | null>(null);
  if (notes.length === 0) return null;
  return (
    <section id="couldnt-read" aria-labelledby="couldnt-read-heading" className="mx-auto mt-8 flex max-w-3xl flex-col gap-2">
      <h2 id="couldnt-read-heading" className="text-[15px] font-bold text-text">
        Dates I couldn't read
      </h2>
      <ul className="flex flex-col gap-1">
        {notes.map((note) => (
          <li key={note.path} className="flex min-h-12 flex-wrap items-center gap-x-2 gap-y-1 rounded-[10px] bg-surface px-3 py-1.5 text-sm text-text-body">
            <span className="min-w-0 flex-1 [overflow-wrap:anywhere]">
              <Link to={noteUrl(note.path)} className="font-medium text-text hover:underline focus-visible:outline-2 focus-visible:outline-accent">
                {note.title}
              </Link>
              {" — "}
              <Plain text={note.plain} />
            </span>
            {canWrite && (
              <button
                type="button"
                aria-label={`Fix… ${note.title}`}
                onClick={() => setFixing(note)}
                className="flex min-h-12 flex-none items-center rounded-[10px] px-3 font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
              >
                Fix…
              </button>
            )}
          </li>
        ))}
      </ul>
      {fixing && <DateDialog path={fixing.path} initial={readableFields(fixing)} onClose={() => setFixing(null)} />}
    </section>
  );
}

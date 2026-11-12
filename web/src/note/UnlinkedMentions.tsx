// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronDown, ChevronRight, TextSearch } from "lucide-react";
import { Fragment, useId, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMentions } from "../api/notes";
import { crumbOf, noteUrl, titleOf, type NoteAt } from "../api/paths";

type Mention = NonNullable<ReturnType<typeof useMentions>["data"]>[number];

/** The line as text, with each mark in a `<mark>`; never markup, whatever the note says. */
function Marked({ text, marks }: Pick<Mention, "text" | "marks">) {
  const parts: React.ReactNode[] = [];
  let at = 0;
  // The generated schema types int32 as number | string.
  marks
    .map((m) => ({ start: Number(m.start), length: Number(m.length) }))
    .sort((a, b) => a.start - b.start)
    .forEach((m, i) => {
      if (m.start < at) return;
      parts.push(<Fragment key={`t${i}`}>{text.slice(at, m.start)}</Fragment>);
      parts.push(
        <mark key={`m${i}`} className="rounded-[3px] bg-accent/30 text-text">
          {text.slice(m.start, m.start + m.length)}
        </mark>,
      );
      at = m.start + m.length;
    });
  parts.push(<Fragment key="rest">{text.slice(at)}</Fragment>);
  return <>{parts}</>;
}

/** "Unlinked mentions": lines in other notes that name this one without linking it. Closed, and not asked, until opened. */
export default function UnlinkedMentions({ path, linked = false }: { path: string; linked?: boolean }) {
  // Open for one path only: a change of note closes it again.
  const [openFor, setOpenFor] = useState<string | null>(null);
  const open = openFor === path;
  const mentions = useMentions(path, open);
  const navigate = useNavigate();
  const id = useId();
  const Chevron = open ? ChevronDown : ChevronRight;

  return (
    <section aria-label="Unlinked mentions" className={linked ? "mt-2" : "mt-[30px] border-t border-line pt-2"}>
      <h2 className="font-mono text-[11px] font-medium tracking-[0.08em] text-dim uppercase">
        <button
          type="button"
          aria-expanded={open}
          aria-controls={id}
          onClick={() => setOpenFor(open ? null : path)}
          className="flex min-h-12 w-full items-center gap-1.5 text-left uppercase focus-visible:outline-2 focus-visible:outline-accent"
        >
          <Chevron size={14} strokeWidth={1.8} aria-hidden className="flex-none" />
          Unlinked mentions
        </button>
      </h2>
      <div id={id} hidden={!open}>
        {open && mentions.isPending && <p className="py-3 text-dim">Looking for mentions …</p>}
        {open && mentions.isError && (
          <p className="flex items-center gap-3 py-3 text-dim">
            Couldn't look for mentions.
            <button type="button" onClick={() => void mentions.refetch()} className="min-h-12 px-2 text-accent-text underline focus-visible:outline-2 focus-visible:outline-accent">
              Try again
            </button>
          </p>
        )}
        {open && mentions.data?.length === 0 && <p className="py-3 text-dim">No unlinked mentions.</p>}
        {open && !!mentions.data?.length && (
          <ul>
            {mentions.data.map((m) => (
              <li key={`${m.path}:${m.line}`}>
                <button
                  type="button"
                  onClick={() => navigate(noteUrl(m.path), { state: { line: Number(m.line) } satisfies NoteAt })}
                  className="flex min-h-12 w-full items-center gap-2.5 border-b border-line px-1 text-left text-text focus-visible:outline-2 focus-visible:outline-accent"
                >
                  <TextSearch size={18} strokeWidth={1.8} aria-hidden className="flex-none text-accent-text" />
                  <span className="flex min-w-0 flex-1 flex-col py-1.5">
                    <span className="truncate">{titleOf(m.path)}</span>
                    <span className="truncate text-[13px] text-dim">
                      <Marked text={m.text} marks={m.marks} />
                    </span>
                  </span>
                  <span className="max-w-[40%] flex-none truncate font-mono text-[11px] text-dim">{crumbOf(m.path)}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

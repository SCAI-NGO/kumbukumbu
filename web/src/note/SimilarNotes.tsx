// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronDown, ChevronRight, Link, Sparkles, X } from "lucide-react";
import { useId, useState } from "react";
import { errorText } from "../api/error-text";
import { useMayChange } from "../api/notes";
import { crumbOf, titleOf, type NoteAt } from "../api/paths";
import { useAddLink, useNotRelated, useSimilarNotes, type SimilarNote, type SimilarNotesView } from "../api/similar";
import { useOfflineMode } from "../offline/mode";
import { useOpenNote } from "../shell/openInPane";

const said: Record<Exclude<SimilarNotesView["state"], "ready">, string> = {
  off: "Similar notes need search by meaning, which is off for this vault.",
  preparing: "Search by meaning is getting ready. Try again in a moment.",
  unindexed: "This note isn't indexed by meaning yet. Try again in a moment.",
};

const icon = "flex size-11 flex-none items-center justify-center rounded-[10px] text-dim hover:text-text focus-visible:outline-2 focus-visible:outline-accent disabled:opacity-50";

/**
 * "Similar notes" (link prediction spec §3): notes of the vault close in meaning that this one neither links nor is linked
 * from, best first. As "Unlinked mentions": closed, and not asked, until opened. Each row offers Add link (a `- [[…]]` in
 * the note's Related section) and Not related (kept in its properties), where the note may be changed; a row answered
 * Not related stays in place as "Hidden · <title>" with Undo until the note changes. Offline it says it needs a connection
 * and asks nothing, as the graph does: the embeddings stay on the server.
 */
export default function SimilarNotes({ path }: { path: string }) {
  const [openFor, setOpenFor] = useState<string | null>(null);
  const open = openFor === path;
  const offline = useOfflineMode((s) => s.offline);
  const asking = open && !offline;
  const similar = useSimilarNotes(path, asking);
  const mayChange = useMayChange(path);
  const openNote = useOpenNote();
  const addLink = useAddLink();
  const notRelated = useNotRelated();
  // The rows hidden here, for this note only, each with the place it stood at, kept to offer Undo there.
  const [hidden, setHidden] = useState<{ for: string; notes: { note: SimilarNote; at: number }[] }>({ for: path, notes: [] });
  const hiddenNow = hidden.for === path ? hidden.notes : [];
  const [problem, setProblem] = useState<string | null>(null);
  const id = useId();
  const Chevron = open ? ChevronDown : ChevronRight;

  const shown = (similar.data?.notes ?? []).filter((n) => !hiddenNow.some((h) => h.note.path === n.path));
  for (const h of [...hiddenNow].sort((a, b) => a.at - b.at)) shown.splice(h.at, 0, h.note);
  const busy = addLink.isPending || notRelated.isPending;

  const answer = (note: SimilarNote, kind: "link" | "hide" | "undo") => {
    setProblem(null);
    const onError = (err: unknown) => setProblem(errorText(err));
    if (kind === "link") return addLink.mutate({ path, other: note.path }, { onError });
    notRelated.mutate(
      { path, other: note.path, undo: kind === "undo" },
      {
        onSuccess: () =>
          setHidden({
            for: path,
            notes: kind === "undo" ? hiddenNow.filter((h) => h.note.path !== note.path) : [...hiddenNow, { note, at: shown.indexOf(note) }],
          }),
        onError,
      },
    );
  };

  return (
    <section aria-label="Similar notes" className="mt-2">
      <h2 className="font-mono text-[11px] font-medium tracking-[0.08em] text-dim uppercase">
        <button
          type="button"
          aria-expanded={open}
          aria-controls={id}
          onClick={() => setOpenFor(open ? null : path)}
          className="flex min-h-12 w-full items-center gap-1.5 text-left uppercase focus-visible:outline-2 focus-visible:outline-accent"
        >
          <Chevron size={14} strokeWidth={1.8} aria-hidden className="flex-none" />
          Similar notes
        </button>
      </h2>
      <div id={id} hidden={!open}>
        {open && offline && <p className="py-3 text-dim">Similar notes need a connection. They're back when you are.</p>}
        {asking && similar.isPending && <p className="py-3 text-dim">Looking for similar notes …</p>}
        {asking && similar.isError && (
          <p className="flex items-center gap-3 py-3 text-dim">
            Couldn't look for similar notes.
            <button type="button" onClick={() => void similar.refetch()} className="min-h-12 px-2 text-accent-text underline focus-visible:outline-2 focus-visible:outline-accent">
              Try again
            </button>
          </p>
        )}
        {asking && similar.data && similar.data.state !== "ready" && (
          <p className="flex items-center gap-3 py-3 text-dim">
            {said[similar.data.state]}
            {similar.data.state !== "off" && (
              <button type="button" onClick={() => void similar.refetch()} className="min-h-12 px-2 text-accent-text underline focus-visible:outline-2 focus-visible:outline-accent">
                Try again
              </button>
            )}
          </p>
        )}
        {asking && similar.data?.state === "ready" && shown.length === 0 && <p className="py-3 text-dim">No similar notes that aren't linked.</p>}
        {open && problem && (
          <p role="alert" className="py-2 text-[13px] text-text">
            {problem}
          </p>
        )}
        {asking && shown.length > 0 && (
          <ul>
            {shown.map((n) => {
              const title = titleOf(n.path);
              const isHidden = hiddenNow.some((h) => h.note.path === n.path);
              return (
                <li key={n.path} className="flex items-center gap-1 border-b border-line">
                  {isHidden ? (
                    <p className="flex min-h-12 min-w-0 flex-1 items-center gap-2.5 px-1 text-dim">
                      <span className="truncate">Hidden · {title}</span>
                    </p>
                  ) : (
                    <button
                      type="button"
                      onClick={() => openNote(n.path, { state: { line: n.line } satisfies NoteAt })}
                      className="flex min-h-12 min-w-0 flex-1 items-center gap-2.5 px-1 text-left text-text focus-visible:outline-2 focus-visible:outline-accent"
                    >
                      <Sparkles size={18} strokeWidth={1.8} aria-hidden className="flex-none text-accent-text" />
                      {/* In a narrow row the title comes first: its column never shrinks below the title (the line doesn't
                          widen it), up to the row's width less the icon and a gap; the folder takes what is left. */}
                      <span className="flex max-w-[calc(100%-38px)] min-w-0 flex-[1_0_auto] flex-col py-1.5">
                        <span className="truncate">{title}</span>
                        <span className="w-0 min-w-full truncate text-[13px] text-dim">{n.text}</span>
                      </span>
                      <span className="max-w-[30%] min-w-0 truncate font-mono text-[11px] text-dim">{crumbOf(n.path)}</span>
                    </button>
                  )}
                  {mayChange && isHidden && (
                    <button type="button" disabled={busy} onClick={() => answer(n, "undo")} className="min-h-11 flex-none px-2 text-accent-text underline focus-visible:outline-2 focus-visible:outline-accent">
                      Undo
                    </button>
                  )}
                  {mayChange && !isHidden && (
                    <>
                      <button type="button" disabled={busy} aria-label={`Add link to ${title}`} title="Add link" onClick={() => answer(n, "link")} className={icon}>
                        <Link size={18} strokeWidth={1.8} aria-hidden />
                      </button>
                      <button type="button" disabled={busy} aria-label={`Not related: ${title}`} title="Not related" onClick={() => answer(n, "hide")} className={icon}>
                        <X size={18} strokeWidth={1.8} aria-hidden />
                      </button>
                    </>
                  )}
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </section>
  );
}

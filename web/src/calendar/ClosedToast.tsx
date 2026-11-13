// SPDX-License-Identifier: AGPL-3.0-or-later
import { X } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { errorText } from "../api/error-text";
import { inputClass } from "../components/form";
import { useEntryAction, type EntryView } from "./api";

const SHOWN_MS = 8_000;

const choiceClass = "min-h-11 px-1.5 font-medium text-accent-text underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-accent";
const Dot = () => <span aria-hidden>{" · "}</span>;

/**
 * ✓ closes at once, without a dialog (spec §7.8): closing is the most frequent action. The calendar views and the note's
 * header share this: `close` sends it, `toast` is the short message that follows, `problem` says why it didn't happen.
 */
export function useClosing() {
  const action = useEntryAction();
  const [closed, setClosed] = useState<{ entry: EntryView; n: number } | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const close = (entry: EntryView) => {
    setProblem(null);
    action.mutate(
      { kind: "close", path: entry.path },
      { onSuccess: () => setClosed((c) => ({ entry, n: (c?.n ?? 0) + 1 })), onError: (err) => setProblem(errorText(err)) },
    );
  };
  return {
    close,
    problem,
    setProblem,
    toast: closed && <ClosedToast key={closed.n} entry={closed.entry} onDone={() => setClosed(null)} />,
  };
}

/**
 * "Closed · Undo · Add a reason", for 8 seconds or until dismissed. Undo opens the to-do again; Add a reason opens a
 * one-line field, saved with Enter: the reason is the person's own memory for later, and often there is none. Once the
 * field is open the message stays until it is saved or dismissed.
 */
export default function ClosedToast({ entry, onDone }: { entry: EntryView; onDone(): void }) {
  const action = useEntryAction();
  const [reasoning, setReasoning] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const done = useRef(onDone);
  done.current = onDone;
  useEffect(() => {
    if (reasoning) return;
    const timer = setTimeout(() => done.current(), SHOWN_MS);
    return () => clearTimeout(timer);
  }, [reasoning]);

  const undo = () => {
    setProblem(null);
    action.mutate({ kind: "reopen", path: entry.path }, { onSuccess: onDone, onError: (err) => setProblem(errorText(err)) });
  };

  return (
    <div
      role="status"
      aria-label="Closed"
      className="fixed inset-x-3 bottom-[max(12px,env(safe-area-inset-bottom))] z-50 mx-auto flex max-w-md flex-col gap-1 rounded-[12px] border border-line bg-surface-2 py-1 pr-1.5 pl-3.5 text-sm text-text shadow-lg"
    >
      <div className="flex flex-wrap items-center">
        <span>Closed</span>
        <Dot />
        <button type="button" onClick={undo} disabled={action.isPending} className={choiceClass}>
          Undo
        </button>
        {!reasoning && (
          <>
            <Dot />
            <button type="button" onClick={() => setReasoning(true)} className={choiceClass}>
              Add a reason
            </button>
          </>
        )}
        <button type="button" aria-label="Dismiss" onClick={onDone} className="ml-auto flex size-11 items-center justify-center rounded-[10px] text-muted focus-visible:outline-2 focus-visible:outline-accent">
          <X size={18} aria-hidden />
        </button>
      </div>
      {reasoning && (
        <ReasonField
          initial=""
          onCancel={() => setReasoning(false)}
          onSave={(reason) => {
            if (reason === "") return onDone();
            setProblem(null);
            action.mutate({ kind: "set", path: entry.path, reason }, { onSuccess: onDone, onError: (err) => setProblem(errorText(err)) });
          }}
        />
      )}
      {problem && <p className="pb-1 text-text">{problem}</p>}
    </div>
  );
}

/** A reason in one line: Enter saves it (an empty one takes it away), Escape leaves it as it was. */
export function ReasonField({ initial, onSave, onCancel }: { initial: string; onSave(reason: string): void; onCancel(): void }) {
  const [reason, setReason] = useState(initial);
  return (
    <input
      autoFocus
      aria-label="Reason"
      value={reason}
      maxLength={2000}
      onChange={(e) => setReason(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === "Enter") {
          e.preventDefault();
          onSave(reason.trim());
        } else if (e.key === "Escape") {
          e.stopPropagation();
          onCancel();
        }
      }}
      className={`${inputClass} mb-1 mr-1.5`}
    />
  );
}

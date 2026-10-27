// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronLeft } from "lucide-react";
import { useId, useState, type DragEvent, type FormEvent, type ReactNode } from "react";
import { useBoardAction, type BoardColumn } from "../api/board";
import { errorText } from "../api/error-text";
import { FormError, inputClass, labelClass, secondaryButtonClass } from "../components/form";

const cardsText = (n: number) => (n === 1 ? "1 card" : `${n} cards`);

/**
 * One column of the board: a full screen wide below the laptop breakpoint (the lanes snap one per screen), a fixed
 * width beside the others from there on. An archivable lane starts collapsed there, as a narrow strip with its name
 * and count, since its cards are done with; on a phone it is a lane like any other.
 */
export default function Lane(props: {
  id: string;
  column: BoardColumn;
  vault: string;
  /** The cards, as CardTile elements. */
  children: ReactNode;
  readOnly: boolean;
  /** Dropping a card on the lane itself (not on one of its cards) puts it at the bottom. */
  onDrop: ((e: DragEvent) => void) | null;
  /** The sentence of an Up, Down or drop into this lane that didn't go through. */
  problem: string | null;
}) {
  const { id, column, vault, children, readOnly, onDrop, problem } = props;
  const [expanded, setExpanded] = useState(false);
  const collapsed = column.archivable && !expanded;
  const count = column.cards.length;

  return (
    <section
      id={id}
      aria-labelledby={`${id}-name`}
      className={`flex h-full w-full flex-none snap-start flex-col px-4 lg:px-0 ${collapsed ? "lg:w-14" : "lg:w-72"}`}
    >
      {collapsed && (
        <button
          type="button"
          aria-expanded={false}
          aria-label={`Show ${column.name}, ${cardsText(count)}`}
          onClick={() => setExpanded(true)}
          className="hidden h-full flex-col items-center gap-2 rounded-[12px] border border-line bg-surface py-3 text-text-body focus-visible:outline-2 focus-visible:outline-accent lg:flex"
        >
          <span className="font-mono text-[11px] text-dim">{count}</span>
          <span className="font-medium [writing-mode:vertical-rl]">{column.name}</span>
        </button>
      )}
      <div className={`flex min-h-0 flex-1 flex-col gap-2 ${collapsed ? "lg:hidden" : ""}`}>
        <div className="flex min-h-11 flex-none items-center gap-2">
          <h2 id={`${id}-name`} className="min-w-0 flex-1 truncate text-[17px] font-bold text-text">
            {column.name}
          </h2>
          <span className="font-mono text-[12px] text-dim">
            {count}
            <span className="sr-only">{count === 1 ? " card" : " cards"}</span>
          </span>
          {column.archivable && expanded && (
            <button
              type="button"
              aria-expanded
              aria-label={`Hide ${column.name}`}
              onClick={() => setExpanded(false)}
              className="hidden size-11 items-center justify-center rounded-[8px] text-muted focus-visible:outline-2 focus-visible:outline-accent lg:flex"
            >
              <ChevronLeft size={18} aria-hidden />
            </button>
          )}
        </div>
        {column.needsReason && <p className="flex-none text-sm text-muted">A card moved here takes a reason.</p>}
        {problem && <FormError>{problem}</FormError>}
        <ul
          aria-label={column.name}
          onDragOver={onDrop ? (e) => e.preventDefault() : undefined}
          onDrop={
            onDrop
              ? (e) => {
                  e.preventDefault();
                  onDrop(e);
                }
              : undefined
          }
          className="flex min-h-24 flex-col gap-2 overflow-y-auto pb-2"
        >
          {children}
        </ul>
        {/* A card can't start in a reason column: it needs one, and the server refuses it there. */}
        {!readOnly && !column.needsReason && <AddCard vault={vault} column={column.name} />}
      </div>
    </section>
  );
}

/** "+ Card": a title and an optional text, at the bottom of the lane. */
function AddCard({ vault, column }: { vault: string; column: string }) {
  const id = useId();
  const add = useBoardAction(vault);
  const [open, setOpen] = useState(false);
  const [title, setTitle] = useState("");
  const [text, setText] = useState("");

  const close = () => {
    setOpen(false);
    setTitle("");
    setText("");
    add.reset();
  };
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!title.trim()) return;
    add.mutate({ kind: "add", title: title.trim(), column, ...(text.trim() ? { text } : {}) }, { onSuccess: close });
  };

  if (!open) {
    return (
      <button
        type="button"
        aria-label={`Add a card to ${column}`}
        onClick={() => setOpen(true)}
        className="flex min-h-11 flex-none items-center self-start rounded-[8px] px-2 text-sm font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        + Card
      </button>
    );
  }
  return (
    <form aria-label={`New card in ${column}`} onSubmit={submit} className="flex flex-none flex-col gap-2 rounded-[12px] border border-line bg-surface p-3">
      <label htmlFor={`${id}-title`} className={labelClass}>
        Title
      </label>
      <input id={`${id}-title`} autoFocus value={title} onChange={(e) => setTitle(e.target.value)} autoComplete="off" className={inputClass} />
      <label htmlFor={`${id}-text`} className={labelClass}>
        Text (optional)
      </label>
      <textarea id={`${id}-text`} value={text} onChange={(e) => setText(e.target.value)} rows={3} className={`${inputClass} h-auto py-2`} />
      {add.isError && <FormError>{errorText(add.error)}</FormError>}
      <div className="flex gap-2">
        <button type="button" onClick={close} className={`${secondaryButtonClass} flex-1`}>
          Cancel
        </button>
        <button type="submit" disabled={add.isPending || !title.trim()} className="min-h-11 flex-1 rounded-[10px] bg-accent px-4 font-medium text-on-accent disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent">
          Add
        </button>
      </div>
    </form>
  );
}

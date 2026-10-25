// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMemo } from "react";
import { secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { lineDiff } from "./lineDiff";

const choiceClass = "min-h-11 px-1.5 font-medium text-accent-text underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-accent";
const Slash = () => <span aria-hidden>{" / "}</span>;

/**
 * The note changed elsewhere while the user wrote (a 409). Non-blocking: they may read and type on, nothing is
 * saved until they choose. "reload" drops their text for the newer one; "keep mine" writes theirs on top of it.
 */
export default function ConflictToast({ onReload, onKeepMine, onViewDiff }: { onReload(): void; onKeepMine(): void; onViewDiff(): void }) {
  return (
    <div
      role="alert"
      aria-label="Changed elsewhere"
      className="mx-3 mb-2 flex flex-none flex-wrap items-center rounded-[12px] border border-accent bg-surface-2 py-1 pr-1.5 pl-3.5 text-sm text-text"
    >
      <span className="mr-0.5">{"Changed elsewhere — "}</span>
      <span className="flex flex-wrap items-center">
        <button type="button" onClick={onReload} className={choiceClass}>
          reload
        </button>
        <Slash />
        <button type="button" onClick={onKeepMine} className={choiceClass}>
          keep mine
        </button>
        <Slash />
        <button type="button" onClick={onViewDiff} className={choiceClass}>
          view diff
        </button>
      </span>
    </div>
  );
}

const lineClass = {
  same: "text-muted",
  removed: "bg-surface-2 text-text before:content-['−_'] before:text-dim",
  added: "bg-surface text-text before:content-['+_'] before:text-accent-text",
};

/** The newer version against the user's text, line by line, with the toast's two choices. */
export function ConflictDiff({ theirs, mine, onReload, onKeepMine, onClose }: { theirs: string; mine: string; onReload(): void; onKeepMine(): void; onClose(): void }) {
  const dialog = useDialog<HTMLDivElement>(onClose);
  const lines = useMemo(() => lineDiff(theirs, mine), [theirs, mine]);

  return (
    <div className="absolute inset-0 z-30 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby="conflict-diff-title"
        className="k-sheet flex max-h-full w-full max-w-xl flex-col gap-3 rounded-[16px] border border-line bg-surface p-[18px]"
      >
        <h2 id="conflict-diff-title" className="text-[20px] font-bold text-text">
          Changed elsewhere
        </h2>
        <p className="text-sm text-text-body">
          <span className="font-mono text-dim">−</span> only in the newer version · <span className="font-mono text-accent-text">+</span> only in yours
        </p>
        <ol className="min-h-0 flex-1 overflow-auto rounded-[10px] border border-line bg-bg py-2 font-mono text-[13px] leading-[1.6]">
          {lines.map((line, i) => (
            <li key={i} data-kind={line.kind} className={`min-h-[1.6em] px-3 whitespace-pre-wrap ${lineClass[line.kind]}`}>
              <span>{line.text}</span>
            </li>
          ))}
        </ol>
        <div className="flex gap-2.5">
          <button type="button" onClick={onReload} className={`${secondaryButtonClass} flex-1`}>
            Reload
          </button>
          <button
            type="button"
            data-autofocus
            onClick={onKeepMine}
            className="min-h-11 flex-1 rounded-[10px] bg-accent px-4 font-medium text-on-accent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
          >
            Keep mine
          </button>
        </div>
      </div>
    </div>
  );
}

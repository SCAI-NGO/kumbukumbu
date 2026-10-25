// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronDown, ChevronUp, X } from "lucide-react";
import { useId, type KeyboardEvent } from "react";
import { secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { useVisualViewport } from "../hooks/useVisualViewport";
import { useEditor } from "../state/editor";
import type { Heading } from "./outline";

/** The note's headings as a sheet from the bottom; a tap jumps to one. */
export function OutlineSheet({ headings, onJump, onClose }: { headings: Heading[]; onJump(line: number): void; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const top = Math.min(...headings.map((h) => h.level));
  return (
    <div className="absolute inset-0 z-30 flex items-end justify-center bg-black/55 sm:items-center" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet flex max-h-[80dvh] w-full max-w-md flex-col rounded-t-[16px] border border-line bg-surface pb-[max(16px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <div className="flex flex-none items-center gap-2 py-2 pr-2 pl-[18px]">
          <h2 id={`${id}-title`} tabIndex={-1} data-autofocus className="flex-1 text-[20px] font-bold text-text focus:outline-none">
            Outline
          </h2>
          <button
            type="button"
            aria-label="Close outline"
            onClick={onClose}
            className="flex size-11 items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent"
          >
            <X size={22} aria-hidden />
          </button>
        </div>
        {headings.length === 0 ? (
          <p className="px-[18px] pb-2 text-text-body">This note has no headings.</p>
        ) : (
          <ul className="min-h-0 overflow-y-auto px-2">
            {headings.map((h) => (
              <li key={h.line} style={{ paddingLeft: (h.level - top) * 16 }}>
                <button
                  type="button"
                  onClick={() => onJump(h.line)}
                  className={`flex min-h-11 w-full items-center rounded-[8px] px-2.5 text-left focus-visible:outline-2 focus-visible:outline-accent ${
                    h.level === top ? "font-semibold text-text" : "text-text-body"
                  }`}
                >
                  <span className="min-w-0 flex-1 truncate">{h.text}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

const iconButton =
  "flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent";

/**
 * Find in the note (Read mode), in place of the dock and above the on-screen keyboard: the matches are marked in
 * the note, Enter and the arrows go to the next and previous one, Escape or × ends it.
 */
export function FindBar() {
  const { find, setFind, setNoteTool } = useEditor();
  const keyboard = useVisualViewport();
  const close = () => {
    setNoteTool(null);
    setFind({ query: "", index: 0, count: 0 });
  };
  const step = (by: number) => find.count && setFind({ index: (find.index + by + find.count) % find.count });
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === "Enter") step(e.shiftKey ? -1 : 1);
    else if (e.key === "Escape") close();
    else return;
    e.preventDefault();
  };
  const status = !find.query ? "" : find.count === 0 ? "No matches" : `${find.index + 1} of ${find.count}`;

  return (
    <div
      role="search"
      aria-label="Find in note"
      style={keyboard > 0 ? { paddingBottom: keyboard + 8 } : undefined}
      className="flex flex-none items-center gap-1 border-t border-line bg-surface px-2 pt-2 pb-[max(18px,env(safe-area-inset-bottom))]"
    >
      <input
        type="search"
        aria-label="Find in note"
        autoFocus
        value={find.query}
        onChange={(e) => setFind({ query: e.target.value, index: 0 })}
        onKeyDown={onKeyDown}
        placeholder="Find in note"
        autoComplete="off"
        autoCapitalize="none"
        autoCorrect="off"
        spellCheck={false}
        enterKeyHint="next"
        className="h-11 min-w-0 flex-1 rounded-full border border-line bg-bg px-4 text-[16px] text-text outline-none placeholder:text-muted focus:border-accent"
      />
      <span role="status" className="min-w-[4.5rem] flex-none text-center font-mono text-[12px] text-muted">
        {status}
      </span>
      <button type="button" aria-label="Previous match" disabled={!find.count} onClick={() => step(-1)} className={iconButton}>
        <ChevronUp size={20} aria-hidden />
      </button>
      <button type="button" aria-label="Next match" disabled={!find.count} onClick={() => step(1)} className={iconButton}>
        <ChevronDown size={20} aria-hidden />
      </button>
      <button type="button" aria-label="Close find" onClick={close} className={`${secondaryButtonClass} flex size-11 items-center justify-center px-0`}>
        <X size={20} aria-hidden />
      </button>
    </div>
  );
}

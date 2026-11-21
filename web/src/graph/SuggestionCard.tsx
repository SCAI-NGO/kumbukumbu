// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useRef } from "react";
import { topDialog } from "../hooks/useDialog";
import type { Suggestion } from "./suggestions";

const coming = "Coming with link prediction";

const action = "min-h-11 flex-1 rounded-[10px] border border-line bg-surface px-3 text-sm text-muted disabled:cursor-not-allowed disabled:opacity-60";

/** The card of a suggested link: the other note and what it will offer once link prediction comes. Escape or a click outside closes it, and that Escape closes nothing else. */
export default function SuggestionCard({ suggestion, onClose }: { suggestion: Suggestion; onClose(): void }) {
  const card = useRef<HTMLDivElement>(null);
  useEffect(() => {
    // Caught on the way down and stopped there, or the dialog the graph is in would close with the same Escape. A
    // dialog opened above the card (the palette) answers it instead.
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      const top = topDialog();
      if (top && !top.contains(card.current)) return;
      e.stopPropagation();
      e.preventDefault();
      onClose();
    };
    const onPointerDown = (e: PointerEvent) => !card.current?.contains(e.target as Node) && onClose();
    document.addEventListener("keydown", onKeyDown, true);
    document.addEventListener("pointerdown", onPointerDown, true);
    return () => {
      document.removeEventListener("keydown", onKeyDown, true);
      document.removeEventListener("pointerdown", onPointerDown, true);
    };
  }, [onClose]);

  return (
    <div
      ref={card}
      role="dialog"
      aria-label={suggestion.title}
      className="absolute inset-x-3 top-3 z-10 flex flex-col gap-2 rounded-[10px] border border-line bg-surface-2 px-4 py-3 text-sm shadow-lg"
    >
      <p className="truncate font-medium text-text">{suggestion.title}</p>
      <p className="text-muted">Similar in meaning, not linked</p>
      <div className="flex gap-2">
        <button type="button" disabled title={coming} className={action}>
          Add link
        </button>
        <button type="button" disabled title={coming} className={action}>
          Not related
        </button>
      </div>
      <p className="text-[12px] text-dim">{coming}</p>
    </div>
  );
}

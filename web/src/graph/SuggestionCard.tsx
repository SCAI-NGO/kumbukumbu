// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useRef, useState } from "react";
import { errorText } from "../api/error-text";
import { useMayChange } from "../api/notes";
import { useAddLink, useNotRelated } from "../api/similar";
import { topDialog } from "../hooks/useDialog";
import type { Suggestion } from "./suggestions";

const action = "min-h-11 flex-1 rounded-[10px] border border-line bg-surface px-3 text-sm text-text disabled:cursor-not-allowed disabled:opacity-60";

/**
 * The card of a suggested link from `from`: the other note and the two answers, as the Links tab's "Similar notes" gives
 * them (Add link, Not related; the latter with Undo). Offered only where `from` may be changed. Escape or a click outside
 * closes it, and in a dialog that Escape closes nothing else.
 */
export default function SuggestionCard({ from, suggestion, onClose }: { from: string; suggestion: Suggestion; onClose(): void }) {
  const card = useRef<HTMLDivElement>(null);
  const mayChange = useMayChange(from);
  const addLink = useAddLink();
  const notRelated = useNotRelated();
  const [hidden, setHidden] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const busy = addLink.isPending || notRelated.isPending;
  const onError = (err: unknown) => setProblem(errorText(err));
  const link = () => addLink.mutate({ path: from, other: suggestion.path }, { onSuccess: onClose, onError });
  const reject = (undo: boolean) =>
    notRelated.mutate({ path: from, other: suggestion.path, undo }, { onSuccess: () => (undo ? onClose() : setHidden(true)), onError });
  useEffect(() => {
    // Caught on the way down and, in the dialog the graph is in, stopped there, or the dialog would close with the same
    // Escape. A dialog opened above the card (the palette) answers it instead; in the column it goes on to the editor.
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      const top = topDialog();
      if (top && !top.contains(card.current)) return;
      if (top) {
        e.stopPropagation();
        e.preventDefault();
      }
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
      <p className="text-muted">{hidden ? "Hidden: not suggested again" : "Similar in meaning, not linked"}</p>
      {mayChange && (
        <div className="flex gap-2">
          {hidden ? (
            <button type="button" disabled={busy} onClick={() => reject(true)} className={action}>
              Undo
            </button>
          ) : (
            <>
              <button type="button" disabled={busy} onClick={link} className={action}>
                Add link
              </button>
              <button type="button" disabled={busy} onClick={() => reject(false)} className={action}>
                Not related
              </button>
            </>
          )}
        </div>
      )}
      {problem && (
        <p role="alert" className="text-[12px] text-text">
          {problem}
        </p>
      )}
    </div>
  );
}

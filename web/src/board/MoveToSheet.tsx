// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronRight } from "lucide-react";
import { useState } from "react";
import type { BoardColumn } from "../api/board";
import { errorText } from "../api/error-text";
import { FormError, secondaryButtonClass } from "../components/form";
import { Sheet } from "../components/Sheet";

const rowClass =
  "flex min-h-12 w-full items-center gap-2 rounded-[10px] border border-line bg-surface-2 px-3 text-left text-[15px] text-text focus-visible:outline-2 focus-visible:outline-accent";

/**
 * "Move to…": the columns, then the top or the bottom of the chosen one. The way to move on a phone, and on a
 * laptop for the keyboard; a refused move stays in the sheet with the server's sentence.
 */
export default function MoveToSheet(props: { card: string; from: string; columns: BoardColumn[]; onClose(): void; onMove(column: string, position: "top" | "bottom"): Promise<unknown> }) {
  const { card, from, columns, onClose, onMove } = props;
  const [column, setColumn] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  const move = async (position: "top" | "bottom") => {
    setPending(true);
    setProblem(null);
    try {
      await onMove(column!, position);
    } catch (err) {
      setProblem(errorText(err));
      setPending(false);
    }
  };

  if (column === null) {
    return (
      <Sheet title={`Move ${card}`} onClose={onClose}>
        <ul aria-label="Columns" className="flex flex-col gap-2">
          {columns.map((c) => (
            <li key={c.name}>
              <button type="button" aria-current={c.name === from ? "true" : undefined} onClick={() => setColumn(c.name)} className={rowClass}>
                <span className="min-w-0 flex-1 truncate">{c.name}</span>
                {c.name === from && (
                  <span aria-hidden className="font-mono text-[11px] text-dim uppercase">
                    here
                  </span>
                )}
                <ChevronRight size={16} aria-hidden className="flex-none text-dim" />
              </button>
            </li>
          ))}
        </ul>
      </Sheet>
    );
  }
  return (
    <Sheet title={`Move ${card}`} onClose={onClose} onBack={() => setColumn(null)} backLabel="All columns">
      <p className="text-text-body">Where in {column}?</p>
      <div className="flex gap-2.5">
        <button type="button" disabled={pending} onClick={() => void move("top")} className={`${secondaryButtonClass} flex-1`}>
          To the top
        </button>
        <button type="button" disabled={pending} onClick={() => void move("bottom")} className={`${secondaryButtonClass} flex-1`}>
          To the bottom
        </button>
      </div>
      {problem && <FormError>{problem}</FormError>}
    </Sheet>
  );
}

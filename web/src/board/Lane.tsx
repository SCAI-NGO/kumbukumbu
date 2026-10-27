// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronLeft, Ellipsis, Pencil } from "lucide-react";
import { useState, type DragEvent, type ReactNode } from "react";
import type { BoardColumn } from "../api/board";
import { FormError } from "../components/form";
import { Menu } from "../components/Menu";
import AddCard from "./AddCard";

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
  /** A checkpoint's board: no column menu, no "+ Card". */
  readOnly: boolean;
  onRename(): void;
  /** Dropping a card on the lane itself (not on one of its cards), or on its collapsed strip, puts it at the bottom. */
  onDrop: ((e: DragEvent) => void) | null;
  /** The sentence of an Up, Down or drop into this lane that didn't go through. */
  problem: string | null;
}) {
  const { id, column, vault, children, readOnly, onRename, onDrop, problem } = props;
  const [expanded, setExpanded] = useState(false);
  const collapsed = column.archivable && !expanded;
  const count = column.cards.length;
  const drop = onDrop
    ? {
        onDragOver: (e: DragEvent) => e.preventDefault(),
        onDrop: (e: DragEvent) => {
          e.preventDefault();
          onDrop(e);
        },
      }
    : {};

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
          {...drop}
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
          {!readOnly && (
            <Menu
              label={`Column actions for ${column.name}`}
              placement="below"
              className="flex size-11 items-center justify-center rounded-[8px] text-muted focus-visible:outline-2 focus-visible:outline-accent"
              items={[{ label: "Rename…", icon: Pencil, onSelect: onRename }]}
            >
              <Ellipsis size={18} aria-hidden />
            </Menu>
          )}
        </div>
        {column.needsReason && <p className="flex-none text-sm text-muted">A card moved here takes a reason.</p>}
        {problem && <FormError>{problem}</FormError>}
        <ul aria-label={column.name} {...drop} className="flex min-h-24 flex-col gap-2 overflow-y-auto pb-2">
          {children}
        </ul>
        {/* A card can't start in a reason column: it needs one, and the server refuses it there. */}
        {!readOnly && !column.needsReason && <AddCard vault={vault} column={column.name} />}
      </div>
    </section>
  );
}

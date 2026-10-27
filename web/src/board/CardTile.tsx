// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowDown, ArrowUp, CalendarDays, Ellipsis, MoveRight } from "lucide-react";
import type { DragEvent } from "react";
import { Link } from "react-router-dom";
import type { BoardCard } from "../api/board";
import { Menu } from "../components/Menu";

/** What a card can do; absent on a checkpoint's board, which is read-only. */
export interface CardActions {
  moveTo(): void;
  dueAndTags(): void;
  /** Null at the top of the lane (Up) or at its bottom (Down). */
  up: (() => void) | null;
  down: (() => void) | null;
  /** Native drag and drop, from the laptop breakpoint on. */
  draggable: boolean;
  onDragStart(): void;
  /** Dropped on this card: before it above its middle, after it below. */
  onDrop(position: string, e: DragEvent): void;
}

export const DRAG_TYPE = "text/plain";

/** A card in its lane: the title opens the note; due (red when overdue), tags and the reason of the last move under it. */
export default function CardTile({ card, href, actions }: { card: BoardCard; href: string; actions?: CardActions }) {
  const drag = actions?.draggable ? actions : null;
  return (
    <li
      draggable={drag ? true : undefined}
      onDragStart={
        drag
          ? (e) => {
              e.dataTransfer.setData(DRAG_TYPE, card.title);
              e.dataTransfer.effectAllowed = "move";
              drag.onDragStart();
            }
          : undefined
      }
      onDragOver={drag ? (e) => e.preventDefault() : undefined}
      onDrop={
        drag
          ? (e) => {
              e.preventDefault();
              e.stopPropagation();
              const box = e.currentTarget.getBoundingClientRect();
              drag.onDrop(`${e.clientY < box.top + box.height / 2 ? "before" : "after"}:${card.title}`, e);
            }
          : undefined
      }
      className={`flex items-start gap-1 rounded-[12px] border border-line bg-surface-2 py-1.5 pl-3 ${actions ? "pr-0.5" : "pr-3"} ${drag ? "cursor-grab active:cursor-grabbing" : ""}`}
    >
      <div className="min-w-0 flex-1 py-1.5">
        <Link to={href} className="block text-[15px] font-medium break-words text-text focus-visible:outline-2 focus-visible:outline-accent">
          {card.title}
        </Link>
        {(card.due || card.tags.length > 0) && (
          <p className="mt-1 flex flex-wrap items-center gap-1.5 text-[12px]">
            {card.due && (
              <span className={`rounded-[6px] px-1.5 py-0.5 font-mono ${card.overdue ? "bg-red-700 text-white" : "border border-line text-muted"}`}>
                {card.due}
                {card.overdue && " · overdue"}
              </span>
            )}
            {card.tags.map((t) => (
              <span key={t} className="font-mono text-accent-text">
                #{t}
              </span>
            ))}
          </p>
        )}
        {card.reason && <p className="mt-1 text-sm break-words text-muted">{card.reason}</p>}
      </div>
      {actions && (
        <Menu
          label={`Actions for ${card.title}`}
          placement="below"
          className="flex size-11 items-center justify-center rounded-[8px] text-muted focus-visible:outline-2 focus-visible:outline-accent"
          items={[
            { label: "Move to…", icon: MoveRight, onSelect: actions.moveTo },
            { label: "Up", icon: ArrowUp, onSelect: () => actions.up?.(), disabled: !actions.up },
            { label: "Down", icon: ArrowDown, onSelect: () => actions.down?.(), disabled: !actions.down },
            { label: "Due and tags…", icon: CalendarDays, onSelect: actions.dueAndTags },
          ]}
        >
          <Ellipsis size={18} aria-hidden />
        </Menu>
      )}
    </li>
  );
}

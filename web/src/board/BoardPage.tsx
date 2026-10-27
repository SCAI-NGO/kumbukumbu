// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowLeft } from "lucide-react";
import { useId, useState, type DragEvent } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { useBoard, useBoardAction, type BoardCard, type BoardView } from "../api/board";
import { HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { checkpointUrl, noteUrl, titleOf } from "../api/paths";
import { FormError, primaryButtonClass } from "../components/form";
import { useDesktop } from "../hooks/useDesktop";
import CardTile, { DRAG_TYPE, type CardActions } from "./CardTile";
import DueTagsDialog from "./DueTagsDialog";
import Lane from "./Lane";
import MoveToSheet from "./MoveToSheet";
import ReasonDialog from "./ReasonDialog";
import RenameColumnDialog from "./RenameColumnDialog";

/**
 * `/board/<Vault>`: the vault's board, the cards of `/<Vault>/Board/` in their columns. `?checkpoint=<name>` shows
 * a checkpoint's board as it was, read-only: no menus, no drag and drop, no "+ Card", and the cards open in the
 * checkpoint.
 */
export default function BoardPage() {
  const { vault = "" } = useParams();
  const [params] = useSearchParams();
  const checkpoint = params.get("checkpoint");
  const board = useBoard(vault, checkpoint);
  const create = useBoardAction(vault);
  const missing = board.error instanceof HttpError && board.error.status === 404;

  return (
    <div className="relative flex h-dvh flex-col bg-bg">
      {checkpoint ? (
        <section aria-label="Checkpoint" className="flex flex-none items-center gap-3 bg-accent px-4 py-2 text-on-accent">
          <p className="min-w-0 flex-1 text-sm font-medium [overflow-wrap:anywhere]">Checkpoint {checkpoint} · board · read only</p>
          <Link
            to={checkpointUrl(vault, checkpoint)}
            className="flex min-h-11 flex-none items-center rounded-[8px] border border-on-accent/40 px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-on-accent"
          >
            Back to the checkpoint
          </Link>
        </section>
      ) : null}
      <header className="flex flex-none items-center gap-1 border-b border-line px-2 py-1.5 lg:px-3">
        {!checkpoint && (
          <Link to="/" aria-label="Back to the notes" className="flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent">
            <ArrowLeft size={20} aria-hidden />
          </Link>
        )}
        <h1 className="min-w-0 flex-1 truncate px-2 text-[20px] font-bold text-text">{board.data?.title ?? vault}</h1>
      </header>

      {board.isPending && <p className="p-4 text-sm text-muted">Loading …</p>}
      {/* A later poll that failed: the board below is the last one read. */}
      {board.isError && board.data && (
        <p role="status" className="flex-none bg-red-700 px-4 py-2 text-sm text-white [overflow-wrap:anywhere]">
          The board can't be read right now: {errorText(board.error)}
        </p>
      )}
      {board.isError && !board.data && (
        <div className="flex max-w-md flex-col gap-3 p-4">
          <p className="text-text">{errorText(board.error)}</p>
          {missing && !checkpoint && (
            <>
              <p className="text-sm text-muted">A board keeps cards, one note each, in columns: the folder Board of this vault, from a template you can change.</p>
              <button type="button" disabled={create.isPending} onClick={() => create.mutate({ kind: "create" })} className={primaryButtonClass}>
                Create board
              </button>
              {create.isError && <FormError>{errorText(create.error)}</FormError>}
            </>
          )}
        </div>
      )}
      {board.data && <Board vault={vault} checkpoint={checkpoint} view={board.data} />}
    </div>
  );
}

type Asking = { card: string; column: string; position: string };

function Board({ vault, checkpoint, view }: { vault: string; checkpoint: string | null; view: BoardView }) {
  const id = useId();
  const desktop = useDesktop();
  const action = useBoardAction(vault);
  const [active, setActive] = useState(0);
  const [moving, setMoving] = useState<{ card: string; from: string } | null>(null);
  const [asking, setAsking] = useState<Asking | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [editing, setEditing] = useState<BoardCard | null>(null);
  const [problem, setProblem] = useState<{ column: string; text: string } | null>(null);
  /** Shows only the cards with this tag, in every lane; Obsidian's tags ignore case. */
  const [tag, setTag] = useState<string | null>(null);
  const readOnly = checkpoint !== null;
  const columns = view.columns.map((c) => (tag ? { ...c, cards: c.cards.filter((k) => k.tags.some((t) => t.toLowerCase() === tag.toLowerCase())) } : c));
  const laneId = (i: number) => `${id}-lane-${i}`;
  const href = (path: string) => (checkpoint ? checkpointUrl(vault, checkpoint, path) : noteUrl(path));

  const columnOf = (card: string) => view.columns.find((c) => c.cards.some((k) => k.title === card));
  const send = (card: string, column: string, position: string, reason?: string) =>
    action.mutateAsync({ kind: "move", card, column, position, ...(reason ? { reason } : {}) });
  /** True when the move goes into a reason column from another one: then the reason comes first. */
  const asksReason = (card: string, column: string) => {
    const target = view.columns.find((c) => c.name === column);
    return !!target?.needsReason && columnOf(card)?.name !== column;
  };

  /** Up, Down and drops: a refusal shows in the lane it concerns. */
  const quick = (card: string, column: string, position: string) => {
    setProblem(null);
    if (asksReason(card, column)) return setAsking({ card, column, position });
    send(card, column, position).catch((err: unknown) => setProblem({ column, text: errorText(err) }));
  };

  const dropped = (column: string, position: string, e: DragEvent) => {
    const card = e.dataTransfer.getData(DRAG_TYPE);
    // Something else dragged in (text from another window), or a card onto itself.
    if (!columnOf(card) || position.endsWith(`:${card}`)) return;
    quick(card, column, position);
  };

  const actions = (column: string, index: number): CardActions | undefined => {
    if (readOnly) return undefined;
    // The lane as shown: with a tag filter, Up and Down go past the visible neighbour.
    const cards = columns.find((c) => c.name === column)!.cards;
    const title = cards[index].title;
    return {
      moveTo: () => setMoving({ card: title, from: column }),
      dueAndTags: () => setEditing(cards[index]),
      up: index > 0 ? () => quick(title, column, `before:${cards[index - 1].title}`) : null,
      down: index < cards.length - 1 ? () => quick(title, column, `after:${cards[index + 1].title}`) : null,
      draggable: desktop,
      onDragStart: () => setProblem(null),
      onDrop: (position, e) => dropped(column, position, e),
    };
  };

  const show = (i: number) => {
    setActive(i);
    document.getElementById(laneId(i))?.scrollIntoView?.({ behavior: "smooth", block: "nearest", inline: "start" });
  };

  return (
    <>
      {view.problems.length > 0 && (
        <section aria-label="Problems" className="flex-none bg-red-700 px-4 py-2 text-sm text-white">
          <p className="font-medium">{view.problems.length === 1 ? "1 note in Board can't be placed:" : `${view.problems.length} notes in Board can't be placed:`}</p>
          <ul className="mt-1 flex flex-col gap-0.5">
            {view.problems.map((p) => (
              <li key={p.path} className="[overflow-wrap:anywhere]">
                <Link to={href(p.path)} className="font-medium underline underline-offset-[3px] focus-visible:outline-2 focus-visible:outline-white">
                  {titleOf(p.path)}
                </Link>
                : {p.what}
              </li>
            ))}
          </ul>
        </section>
      )}

      {tag && (
        <div className="flex flex-none items-center gap-2 border-b border-line px-4 py-1.5">
          <button
            type="button"
            onClick={() => setTag(null)}
            className="flex min-h-11 items-center gap-1 rounded-full border border-line px-3 font-mono text-sm text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
          >
            #{tag} <span aria-hidden>×</span>
            <span className="sr-only"> (show all cards)</span>
          </button>
        </div>
      )}

      <nav aria-label="Columns" className="flex flex-none gap-1 overflow-x-auto border-b border-line px-2 [scrollbar-width:none] lg:hidden">
        {columns.map((c, i) => (
          <button
            key={c.name}
            type="button"
            aria-current={i === active ? "true" : undefined}
            onClick={() => show(i)}
            className={`flex min-h-11 flex-none items-center gap-1.5 border-b-2 px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${
              i === active ? "border-accent text-text" : "border-transparent text-muted"
            }`}
          >
            {c.name} <span className="font-mono text-[11px] text-dim">{c.cards.length}</span>
          </button>
        ))}
      </nav>

      <div
        onScroll={(e) => !desktop && setActive(Math.round(e.currentTarget.scrollLeft / Math.max(1, e.currentTarget.clientWidth)))}
        className="flex min-h-0 flex-1 snap-x snap-mandatory overflow-x-auto py-3 lg:snap-none lg:gap-3 lg:px-4"
      >
        {columns.map((c, i) => (
            <Lane
              key={c.name}
              id={laneId(i)}
              column={c}
              vault={vault}
              readOnly={readOnly}
              onRename={() => setRenaming(c.name)}
              onDrop={!readOnly && desktop ? (e) => dropped(c.name, "bottom", e) : null}
              problem={problem?.column === c.name ? problem.text : null}
            >
              {c.cards.map((card, at) => (
                <CardTile key={card.path} card={card} href={href(card.path)} actions={actions(c.name, at)} onTag={setTag} />
              ))}
            </Lane>
        ))}
      </div>

      {moving && (
        <MoveToSheet
          card={moving.card}
          from={moving.from}
          columns={view.columns}
          onClose={() => setMoving(null)}
          onMove={async (column, position) => {
            if (asksReason(moving.card, column)) {
              setMoving(null);
              setAsking({ card: moving.card, column, position });
              return;
            }
            await send(moving.card, column, position);
            setMoving(null);
          }}
        />
      )}
      {renaming && (
        <RenameColumnDialog
          column={renaming}
          onCancel={() => setRenaming(null)}
          onRename={async (to) => {
            await action.mutateAsync({ kind: "rename-column", from: renaming, to });
            setRenaming(null);
          }}
        />
      )}
      {editing && (
        <DueTagsDialog
          card={editing}
          onCancel={() => setEditing(null)}
          onSave={async (update) => {
            await action.mutateAsync({ kind: "update", ...update });
            setEditing(null);
          }}
        />
      )}
      {asking && (
        <ReasonDialog
          card={asking.card}
          column={asking.column}
          onCancel={() => setAsking(null)}
          onMove={async (reason) => {
            await send(asking.card, asking.column, asking.position, reason);
            setAsking(null);
          }}
        />
      )}
    </>
  );
}

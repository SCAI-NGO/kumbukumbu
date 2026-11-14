// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { Fragment, useEffect, useRef, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { useBoardAction } from "../api/board";
import { errorText } from "../api/error-text";
import { boardUrl, calendarUrl, vaultOf } from "../api/paths";
import { onBoard } from "./CouldntRead";
import { afterEntryWrite, editorSettled, UNSAVED, useEntryAction, useNoteEntry, type EntryView } from "./api";
import { useClosing, ReasonField } from "./ClosedToast";
import DateDialog, { readableFields } from "./DateDialog";
import { dayAndMonth, dayHeading, shortDay, weekId, weekOf } from "./dates";
import LaterMenu, { useMoved, type Moved } from "./LaterMenu";
import TaskCheckbox from "./TaskCheckbox";
import { rangeOf } from "./WeekList";

const actionClass =
  "inline-flex min-h-12 items-center rounded-[8px] px-1 font-medium text-accent-text underline-offset-[3px] hover:underline focus-visible:outline-2 focus-visible:outline-accent";
const linkClass = "inline-flex min-h-12 items-center font-medium text-accent-text underline underline-offset-[3px] focus-visible:outline-2 focus-visible:outline-accent";

const isWeek = (value: string) => /^\d{4}-W\d{2}$/.test(value);

/** The date as the line shows it: a day note's full day, a week, the days of a span, or "Tue 15 Dec". */
function dateText(entry: EntryView): string {
  if (entry.byName && !isWeek(entry.when)) return dayHeading(entry.firstDay);
  if (isWeek(entry.when) && !entry.until) return `Week ${weekOf(entry.firstDay).week}`;
  if (entry.spans) return rangeOf(entry);
  return shortDay(entry.firstDay, true);
}

/** "This is the day note of 12 November (by its name)": honest, instead of a Remove that can't work. */
function byNameText(entry: EntryView): string {
  return isWeek(entry.when)
    ? `This is the week note of Week ${weekOf(entry.firstDay).week} (by its name)`
    : `This is the day note of ${dayAndMonth(entry.firstDay, true)} (by its name)`;
}

/**
 * The line in a note's header for every entry (spec §7.8): "📅 Tue 15 Dec", then ☐ To do and Later after a gap; a
 * closed to-do's ☑ Done says when and why, with its reason; a board card its column, with Move to Done through the
 * board or a link to it; a day or week note by its name says so. The text keeps its dots, the controls follow without.
 * The date opens the calendar at that week. Asked only for the note shown, and again with each new version of it. A
 * member who may only read sees the line without its actions.
 */
export default function EntryLine({ path, version }: { path: string; version: string }) {
  const queryClient = useQueryClient();
  const note = useNoteEntry(path);
  const seen = useRef(version);
  useEffect(() => {
    if (seen.current === version) return;
    seen.current = version;
    void queryClient.invalidateQueries({ queryKey: ["note-entry", path] });
  }, [version, path, queryClient]);

  const closing = useClosing();
  const moving = useMoved();
  const action = useEntryAction();
  const board = useBoardAction(vaultOf(path));
  const [problem, setProblem] = useState<string | null>(null);
  const [reasoning, setReasoning] = useState(false);
  const [fixing, setFixing] = useState(false);

  // The messages stay in the same place in the tree whether the line is shown or not, so a message isn't made anew
  // (losing its open reason field) when the line comes and goes.
  const messages = (
    <>
      {closing.toast}
      {moving.toast}
    </>
  );
  const view = note.data;
  if (!view || (!view.entry && !view.couldntRead)) return <>{null}{messages}{null}</>;
  const canWrite = view.canWrite;
  const shown = problem ?? closing.problem;
  const say = (text: string | null) => {
    closing.setProblem(null);
    setProblem(text);
  };
  const onMoved = (moved: Moved) => {
    closing.dismiss();
    moving.onMoved(moved);
  };
  const send = (request: Parameters<typeof action.mutate>[0], then?: () => void) => {
    say(null);
    action.mutate(request, { onSuccess: then, onError: (err) => setProblem(errorText(err)) });
  };

  const texts: ReactNode[] = [];
  const controls: ReactNode[] = [];
  const entry = view.entry;
  if (entry) {
    const open = entry.state !== "done" && entry.state !== "past";
    texts.push(
      <Link key="date" to={calendarUrl(entry.vault, weekId(weekOf(entry.firstDay)))} title="Open the calendar at this week" className={linkClass}>
        {dateText(entry)}
      </Link>,
    );
    if (entry.time && !entry.byName) texts.push(<span key="time">{entry.time}</span>);
    if (entry.kind === "card") {
      if (entry.column) texts.push(<span key="column">{entry.column}</span>);
      const moves = canWrite && entry.boardAction && entry.column !== entry.boardAction;
      if (moves) {
        controls.push(
          <button
            key="move"
            type="button"
            disabled={board.isPending}
            className={actionClass}
            onClick={async () => {
              say(null);
              if (!(await editorSettled(entry.path))) return setProblem(UNSAVED);
              board.mutate(
                { kind: "move", card: entry.title, column: entry.boardAction! },
                { onSuccess: () => void afterEntryWrite(queryClient, entry.vault, entry.path), onError: (err) => setProblem(errorText(err)) },
              );
            }}
          >
            Move to {entry.boardAction}
          </button>,
        );
      } else if (!entry.boardAction && open) {
        controls.push(
          <Link key="board" to={boardUrl(entry.vault)} className={linkClass}>
            Open the board
          </Link>,
        );
      }
      if (canWrite && open) controls.push(<LaterMenu key="later" entry={entry} onProblem={say} onMoved={onMoved} className={actionClass} />);
    } else if (entry.task === "open") {
      if (canWrite) {
        controls.push(
          <TaskCheckbox
            key="done"
            closed={false}
            disabled={action.isPending}
            onChange={() => {
              moving.dismiss();
              closing.close(entry);
            }}
          />,
          <LaterMenu key="later" entry={entry} onProblem={say} onMoved={onMoved} className={actionClass} />,
        );
      } else texts.push(<span key="todo">To do</span>);
    } else if (entry.task === "closed") {
      const closed = entry.closed ? `Closed ${dayAndMonth(entry.closed)}` : "Closed";
      const reason = entry.reason && <span className="[overflow-wrap:anywhere]">{entry.reason}</span>;
      if (canWrite) {
        controls.push(
          <TaskCheckbox key="done" closed disabled={action.isPending} onChange={() => send({ kind: "reopen", path: entry.path })} />,
          <span key="closed">
            {closed}
            {reason && <span aria-hidden>{" · "}</span>}
            {reason}
          </span>,
          // While "Closed · Undo · Add a reason" is up, its own field opens: one field, in the message.
          <button key="why" type="button" onClick={() => (closing.showing === entry.path ? closing.askReason() : setReasoning(true))} className={actionClass}>
            {entry.reason ? "Edit reason" : "Add a reason"}
          </button>,
        );
      } else {
        texts.push(<span key="closed">{closed}</span>);
        if (reason) texts.push(<Fragment key="reason">{reason}</Fragment>);
      }
    }
    if (entry.byName) texts.push(<span key="name">{byNameText(entry)}</span>);
  } else if (view.couldntRead) {
    texts.push(<span key="plain">{view.couldntRead.plain}</span>);
    // A card's date is its board's: the board is where it is put right, not the date dialog.
    if (onBoard(path))
      controls.push(
        <Link key="board" to={boardUrl(vaultOf(path))} className={linkClass}>
          Open the board
        </Link>,
      );
    else if (canWrite)
      controls.push(
        <button key="fix" type="button" onClick={() => setFixing(true)} className={actionClass}>
          Fix…
        </button>,
      );
  }

  return (
    <>
      <div role="group" aria-label="In the calendar" className="flex flex-none flex-col border-b border-line bg-surface px-4 text-sm text-text">
        <div className="flex min-h-12 flex-wrap items-center gap-x-4">
          <span>
            <span aria-hidden>📅 </span>
            {texts.map((text, i) => (
              <Fragment key={i}>
                {i > 0 && <span aria-hidden>{" · "}</span>}
                {text}
              </Fragment>
            ))}
          </span>
          {controls.length > 0 && (
            <span className="flex flex-wrap items-center gap-x-3">
              {controls.map((control, i) => (
                <Fragment key={i}>
                  {" "}
                  {control}
                </Fragment>
              ))}
            </span>
          )}
        </div>
        {reasoning && entry && (
          <ReasonField
            initial={entry.reason ?? ""}
            className="pb-2"
            onCancel={() => setReasoning(false)}
            onSave={(reason) =>
              send({ kind: "set", path: entry.path, ...(reason ? { reason } : { clearReason: true }) }, () => {
                setReasoning(false);
                closing.dismiss();
              })
            }
          />
        )}
        {shown && (
          <p role="alert" className="pb-2 text-text">
            {shown}
          </p>
        )}
      </div>
      {messages}
      {fixing && view.couldntRead && <DateDialog path={path} initial={readableFields(view.couldntRead)} onClose={() => setFixing(false)} />}
    </>
  );
}

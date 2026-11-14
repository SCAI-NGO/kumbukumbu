// SPDX-License-Identifier: AGPL-3.0-or-later
import { FlagOff, X } from "lucide-react";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { create } from "zustand";
import { errorText } from "../api/error-text";
import { vaultOf } from "../api/paths";
import { isStructural } from "../api/notes";
import { fileKindOf } from "../api/paths";
import { goneVault, useDiscreet } from "../discreet/state";
import { primaryButtonClass, secondaryButtonClass } from "../components/form";
import { Sheet } from "../components/Sheet";
import { useDialog } from "../hooks/useDialog";
import { FLAGS, isFlag, useFlag, useSetFlag, type FlagName, type SetFlagRequest } from "./api";
import FlagIcon, { flagLabel } from "./FlagIcon";

const SHOWN_MS = 8_000;

/** Whether a file takes a flag: a Markdown note, as tags are read only from those; not the board's definition, which is the board's. */
export const flaggable = (path: string) => fileKindOf(path) === "note" && path.toLowerCase().endsWith(".md") && !isStructural(path);

/** What was written, for the message after it and its Undo: the flag before, the flag now, and whether ✓ closed the to-do. */
type Written = { path: string; previous: FlagName | null; flag: FlagName | null; closed: boolean; n: number };

/**
 * The one message after a write, wherever the menu was offered: a newer write replaces it, so its Undo always takes back
 * the latest. `owner` is the place that wrote it and shows it; `asking` the place whose tick question is open, which
 * the message, fixed at the bottom as the question is on a phone, would cover.
 */
const useFlagMessage = create<{ written: (Written & { owner: string }) | null; asking: string | null }>()(() => ({ written: null, asking: null }));

let writes = 0;
const dismiss = (n: number) => useFlagMessage.setState((s) => (s.written?.n === n ? { written: null } : s));

/**
 * The flag menu with what follows it, for whoever offers it (the note's header, the note's ⋯, the tree's ⋯): `open`
 * shows the eight flags and No flag for a note; ✓ on an open to-do asks whether the calendar's to-do is done too; after
 * a write a short message offers Undo. `element` holds all three and stays where it is rendered, so the message
 * outlives the menu; there is one message for all the places, the newest. `onWritten` hears of each flag written from
 * the menu (not of Undo).
 */
export function useFlagging(onWritten?: (flag: FlagName | null) => void) {
  const [path, setPath] = useState<string | null>(null);
  const [asking, setAsking] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const owner = useId();
  const written = useFlagMessage((s) => (s.written?.owner === owner && s.asking === null ? s.written : null));
  const flag = useFlag(path);
  const set = useSetFlag();

  const close = () => {
    setPath(null);
    setAsking(false);
    setProblem(null);
  };
  // A menu and a message about a vault hidden again go with it: they name its note's flag.
  useEffect(
    () =>
      useDiscreet.subscribe((s, before) => {
        if (s.gone === before.gone && s.names === before.names) return;
        setPath((p) => (p && goneVault(s, vaultOf(p)) ? null : p));
        useFlagMessage.setState((m) => (m.written && goneVault(s, vaultOf(m.written.path)) ? { written: null } : m));
      }),
    [],
  );
  // Its place gone (the note's header, say), the message goes too.
  useEffect(() => () => useFlagMessage.setState((m) => (m.written?.owner === owner ? { written: null } : m)), [owner]);
  const questioned = path !== null && asking;
  useEffect(() => {
    if (!questioned) return;
    useFlagMessage.setState({ asking: owner });
    return () => useFlagMessage.setState((m) => (m.asking === owner ? { asking: null } : m));
  }, [questioned, owner]);
  const view = flag.data;
  const previous = view && isFlag(view.flag) ? view.flag : null;
  // What Undo puts back is the flag as read now: while it is read again (on opening), the cached one may be old.
  const reading = flag.isFetching;

  const write = (request: SetFlagRequest) => {
    setProblem(null);
    set.mutate(request, {
      onSuccess: () => {
        close();
        onWritten?.(request.flag);
        useFlagMessage.setState({
          written: { owner, path: request.path, previous, flag: request.flag, closed: request.calendar === "close", n: ++writes },
        });
      },
      onError: (err) => setProblem(errorText(err)),
    });
  };
  const choose = (chosen: FlagName | null) => {
    if (!path || !view || reading) return;
    if (chosen === previous) return close();
    // Not for a board card, whose status is its column (spec §2.4): the server says `openTask` only of a calendar to-do.
    if (chosen === "done" && view.openTask) return setAsking(true);
    write({ path, flag: chosen });
  };

  const element = (
    <>
      {path && (
        <FlagSheet onClose={close}>
          {view ? (
            view.inText ? (
              <p className="pb-2 text-text-body">{`This flag is written in the note's text (${view.inText}); change it there.`}</p>
            ) : view.canWrite ? (
              <FlagChoices current={previous} busy={set.isPending || reading} onChoose={choose} />
            ) : (
              <p className="pb-2 text-text-body">You can only read this note.</p>
            )
          ) : (
            flag.isError && <p className="pb-2 text-text-body">{errorText(flag.error)}</p>
          )}
          {problem && !asking && (
            <p role="alert" className="pb-2 text-text">
              {problem}
            </p>
          )}
        </FlagSheet>
      )}
      {path && asking && (
        <TickQuestion
          busy={set.isPending}
          problem={problem}
          onMarkDone={() => write({ path, flag: "done", calendar: "close" })}
          onJustFlag={() => write({ path, flag: "done" })}
          onCancel={() => {
            setAsking(false);
            setProblem(null);
          }}
        />
      )}
      {written && <FlagToast key={written.n} written={written} onDone={() => dismiss(written.n)} />}
    </>
  );
  return {
    open: (note: string) => {
      setProblem(null);
      setAsking(false);
      setPath(note);
    },
    element,
  };
}

function FlagSheet({ onClose, children }: { onClose(): void; children: ReactNode }) {
  return (
    <Sheet title="Flag" onClose={onClose}>
      {children}
    </Sheet>
  );
}

const choiceClass =
  "flex min-h-12 w-full items-center gap-3 rounded-[10px] px-3 text-left text-[15px] text-text hover:bg-surface-2 aria-pressed:bg-accent/15 aria-pressed:font-medium disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent";

/** The eight flags and No flag, each by its icon and its name; the note's own is marked. */
function FlagChoices({ current, busy, onChoose }: { current: FlagName | null; busy: boolean; onChoose(flag: FlagName | null): void }) {
  return (
    <div className="flex flex-col pb-2">
      {FLAGS.map((flag) => (
        <button key={flag} type="button" aria-pressed={current === flag} disabled={busy} onClick={() => onChoose(flag)} className={choiceClass}>
          <span className="flex size-6 items-center justify-center">
            <FlagIcon flag={flag} size={20} decorative />
          </span>
          {flagLabel[flag]}
        </button>
      ))}
      <button type="button" aria-pressed={current === null} disabled={busy} onClick={() => onChoose(null)} className={choiceClass}>
        <span className="flex size-6 items-center justify-center">
          <FlagOff size={20} strokeWidth={1.8} aria-hidden className="text-muted" />
        </span>
        No flag
      </button>
    </div>
  );
}

/** ✓ on an open to-do (spec §2.4): Mark done also closes it, in the same write; Just the flag leaves the calendar alone. */
function TickQuestion({ busy, problem, onMarkDone, onJustFlag, onCancel }: { busy: boolean; problem: string | null; onMarkDone(): void; onJustFlag(): void; onCancel(): void }) {
  const dialog = useDialog<HTMLDivElement>(onCancel);
  const id = useId();
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div ref={dialog} role="alertdialog" aria-modal="true" aria-labelledby={`${id}-title`} className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5">
        <p id={`${id}-title`} className="text-text-body">
          Also mark it done in the calendar?
        </p>
        <div className="flex gap-3">
          <button type="button" data-autofocus disabled={busy} onClick={onJustFlag} className={`${secondaryButtonClass.replace("min-h-11", "min-h-12")} flex-1`}>
            Just the flag
          </button>
          <button type="button" disabled={busy} onClick={onMarkDone} className={`${primaryButtonClass} flex-1`}>
            Mark done
          </button>
        </div>
        {problem && (
          <p role="alert" className="text-text">
            {problem}
          </p>
        )}
      </div>
    </div>
  );
}

const undoClass = "min-h-12 px-1.5 font-medium text-accent-text underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-accent";

/** What the message says was done, in the menu's words: a colour is flagged, ✓ ticked, ! marked important. */
function writtenText({ flag, closed }: Written): string {
  if (flag === null) return "Flag removed";
  if (flag === "done") return closed ? "Ticked and marked done" : "Ticked";
  if (flag === "important") return "Marked important";
  return `Flagged ${flagLabel[flag]}`;
}

/**
 * "Flagged Red · Undo", "Ticked · Undo", "Flag removed · Undo", for 8 seconds or until dismissed, as the calendar's Closed. Undo puts
 * the flag before back in one write, and after Mark done opens the to-do again in the same one.
 */
function FlagToast({ written, onDone }: { written: Written; onDone(): void }) {
  const set = useSetFlag();
  const [problem, setProblem] = useState<string | null>(null);
  const done = useRef(onDone);
  done.current = onDone;
  useEffect(() => {
    if (problem) return;
    const timer = setTimeout(() => done.current(), SHOWN_MS);
    return () => clearTimeout(timer);
  }, [problem]);

  const undo = () => {
    setProblem(null);
    set.mutate(
      { path: written.path, flag: written.previous, ...(written.closed ? { calendar: "reopen" as const } : {}) },
      { onSuccess: onDone, onError: (err) => setProblem(errorText(err)) },
    );
  };
  const text = writtenText(written);

  return (
    <div
      role="status"
      aria-label="Flag changed"
      className="fixed inset-x-3 bottom-[max(12px,env(safe-area-inset-bottom))] z-50 mx-auto flex max-w-md flex-col gap-1 rounded-[12px] border border-line bg-surface-2 py-1 pr-1.5 pl-3.5 text-sm text-text shadow-lg"
    >
      <div className="flex flex-wrap items-center">
        <span>{text}</span>
        {/* White space kept: a flex item drops its spaces at either end. */}
        <span aria-hidden className="whitespace-pre">
          {" · "}
        </span>
        <button type="button" onClick={undo} disabled={set.isPending} className={undoClass}>
          Undo
        </button>
        <button type="button" aria-label="Dismiss" onClick={onDone} className="ml-auto flex size-12 items-center justify-center rounded-[10px] text-muted focus-visible:outline-2 focus-visible:outline-accent">
          <X size={18} aria-hidden />
        </button>
      </div>
      {problem && <p className="pb-1 text-text">{problem}</p>}
    </div>
  );
}

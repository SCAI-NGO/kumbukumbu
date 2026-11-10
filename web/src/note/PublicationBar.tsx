// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import { errorText } from "../api/error-text";
import { useNotePublication, usePublishEnabled, useUpdatePublication, useWithdraw } from "../api/publications";
import { primaryButtonClass, secondaryButtonClass } from "../components/form";
import { CopyButton } from "../components/shownOnce";
import { useDialog } from "../hooks/useDialog";
import { barButtonClass as buttonClass, barClass } from "./ChangedElsewhere";

const dayAndTime = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
const timeOnly = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit" });
const sameDay = (a: Date, b: Date) => a.toDateString() === b.toDateString();

/** "Published 5 Nov, 14:32 · changed 16:05", in the reader's own time; the change's day only when it isn't the snapshot's. */
function olderText(snapshotAt: string, changedAt: string | null) {
  const snapshot = new Date(snapshotAt);
  const changed = changedAt ? new Date(changedAt) : null;
  const published = `Published ${dayAndTime.format(snapshot)}`;
  if (!changed) return published;
  return `${published} · changed ${(sameDay(snapshot, changed) ? timeOnly : dayAndTime).format(changed)}`;
}

/**
 * Above a published note (publish spec §3.11): for the owner its state, green when the copy is up to date, yellow when
 * the note changed since, with the link to copy again, Update copy and Unpublish. A member learns only that it is
 * published (§3.10). Switched off, only Unpublish is left: withdrawing always works.
 */
export default function PublicationBar({ path, owner }: { path: string; owner: boolean }) {
  const publication = useNotePublication(path);
  const enabled = usePublishEnabled();
  const update = useUpdatePublication(path);
  const withdraw = useWithdraw(path);
  const [asking, setAsking] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);

  const p = publication.data;
  if (!p) return null;

  if (!owner) {
    return (
      <section aria-label="Publication" className={barClass}>
        <span className="py-2">Published by the vault's owner</span>
      </section>
    );
  }
  // Not before the session says whether publishing is on: the bar would offer Update for a moment, or hide it.
  if (enabled === undefined) return null;

  const older = p.older === true && enabled;
  const tone = !enabled ? undefined : older ? "yellow" : "green";
  const error = update.error ?? withdraw.error;
  return (
    <>
      <section aria-label="Publication" data-tone={tone} className={barClass}>
        <span aria-hidden className={`size-2 flex-none rounded-full ${tone === "green" ? "bg-success" : tone === "yellow" ? "bg-warning" : "bg-dim"}`} />
        {/* A status, as the bar about a write elsewhere: a failure or a changed state is announced. */}
        <span role="status" className="min-w-0 flex-1 py-2 pl-1.5">
          {!enabled
            ? "Published. Publishing is switched off on this server, so the link reads nothing until it is on again."
            : older
              ? `Published copy is older than this note.${p.snapshotAt ? ` ${olderText(p.snapshotAt, p.changedAt)}` : ""}`
              : "Published, up to date"}
          {error && <span className="block text-text">{errorText(error)}</span>}
          {copyFailed && <span className="block text-text">Copying didn't work. Try again.</span>}
        </span>
        {/* The answer holds the link: reset once in, so the mutation keeps none. */}
        {enabled && older && (
          <button type="button" disabled={update.isPending} onClick={() => update.mutate(undefined, { onSuccess: () => update.reset() })} className={buttonClass}>
            Update copy
          </button>
        )}
        {enabled && p.link && <CopyButton value={p.link} label="Copy link" className={buttonClass} onResult={(ok) => setCopyFailed(!ok)} />}
        <button type="button" onClick={() => setAsking(true)} className={buttonClass}>
          Unpublish
        </button>
      </section>
      {asking && (
        <ConfirmWithdraw
          busy={withdraw.isPending}
          onKeep={() => setAsking(false)}
          onWithdraw={() => withdraw.mutate(undefined, { onSettled: () => setAsking(false) })}
        />
      )}
    </>
  );
}

function ConfirmWithdraw({ busy, onKeep, onWithdraw }: { busy: boolean; onKeep(): void; onWithdraw(): void }) {
  const dialog = useDialog<HTMLDivElement>(onKeep);
  const id = useId();
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <p id={`${id}-title`} className="text-text-body">
          Withdraw the link? Whoever has it can't read the note any more.
        </p>
        <div className="flex gap-3">
          <button type="button" data-autofocus onClick={onKeep} className={`${secondaryButtonClass} flex-1`}>
            Keep
          </button>
          <button type="button" disabled={busy} onClick={onWithdraw} className={`${primaryButtonClass} flex-1`}>
            Unpublish
          </button>
        </div>
      </div>
    </div>
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { lazy, Suspense, useId, useState } from "react";
import { Link } from "react-router-dom";
import { errorText } from "../api/error-text";
import { useNoteHref } from "../shell/openInPane";
import { useBundleCopies, useBundledIn, useNotePublication, usePublishEnabled, useUpdatePublication, useWithdraw } from "../api/publications";
const PublishDialog = lazy(() => import("../shell/PublishDialog"));
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
export default function PublicationBar({ path, owner, lastWriteAt }: { path: string; owner: boolean; lastWriteAt?: string }) {
  const publication = useNotePublication(path);
  const enabled = usePublishEnabled();
  const update = useUpdatePublication(path);
  const withdraw = useWithdraw(path);
  const [asking, setAsking] = useState(false);
  const [updating, setUpdating] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const starts = useBundledIn(path);

  const p = publication.data;
  // A note published with another one's bundle (Publish 2.0 decision 7): where it went out, for owner and member alike.
  if (!p && starts.length > 0) return <BundledBar starts={owner ? starts : null} enabled={enabled === true} lastWriteAt={lastWriteAt} />;
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
  const depth = Number(p.depth ?? 0);
  const bundle = depth > 0;
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
          {bundle && Number(p.notes ?? 1) > 1 && ` · with ${Number(p.notes) - 1} linked ${Number(p.notes) === 2 ? "note" : "notes"}`}
          {error && <span className="block text-text">{errorText(error)}</span>}
          {copyFailed && <span className="block text-text">Copying didn't work. Try again.</span>}
        </span>
        {/* The answer holds the link: reset once in, so the mutation keeps none. */}
        {/* A bundle's update walks again: its list is shown first, as when it was published. */}
        {enabled && older && (
          <button
            type="button"
            disabled={update.isPending}
            onClick={() => (bundle ? setUpdating(true) : update.mutate(undefined, { onSuccess: () => update.reset() }))}
            className={buttonClass}
          >
            Update copy
          </button>
        )}
        {enabled && p.link && <CopyButton value={p.link} label="Copy link" className={buttonClass} onResult={(ok) => setCopyFailed(!ok)} />}
        <button type="button" onClick={() => setAsking(true)} className={buttonClass}>
          Unpublish
        </button>
      </section>
      {updating && (
        <Suspense fallback={null}>
          <PublishDialog path={path} update={{ depth, without: p.without ?? [] }} onClose={() => setUpdating(false)} />
        </Suspense>
      )}
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

const titleOf = (path: string) => path.split("/").at(-1)!.replace(/\.md$/i, "");

/** "Published with Plan": this note went out inside the bundle of each start note named, yellow when a copy is older
 * than the note, its update at the start note; a member learns only that it is published (publish spec §3.10). */
function BundledBar({ starts, enabled, lastWriteAt }: { starts: string[] | null; enabled: boolean; lastWriteAt?: string }) {
  const noteHref = useNoteHref();
  const copies = useBundleCopies(starts ?? [], enabled);
  if (!starts) {
    return (
      <section aria-label="Publication" className={barClass}>
        <span className="py-2">Published by the vault's owner</span>
      </section>
    );
  }
  // The starts whose copy is older than the note, oldest first; a copy without a time is never older.
  const stale = starts
    .map((start, i) => ({ start, at: copies[i] }))
    .filter((c): c is { start: string; at: string } => !!c.at && !!lastWriteAt && Date.parse(lastWriteAt) > Date.parse(c.at))
    .sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
  const older = stale.length > 0;
  // Older, it says where the copy is updated: from its start note, which the bar names.
  const named = older ? stale.map((c) => c.start) : starts;
  return (
    <section aria-label="Publication" data-tone={older ? "yellow" : undefined} className={barClass}>
      {older && <span aria-hidden className="size-2 flex-none rounded-full bg-warning" />}
      {/* A status from the start, so the yellow is announced when the start's copy answers. */}
      <span role="status" className={older ? "min-w-0 flex-1 py-2 pl-1.5" : "py-2"}>
        {older ? `Published copy is older than this note. ${olderText(stale[0].at, lastWriteAt ?? null)}. Update ${stale.length > 1 ? "them" : "it"} from ` : "Published with "}
        {named.map((start, i) => (
          <span key={start}>
            {i > 0 && (i === named.length - 1 ? " and " : ", ")}
            <Link to={noteHref(start)} className="font-medium text-accent-text underline underline-offset-2">
              {titleOf(start)}
            </Link>
          </span>
        ))}
        {older && "."}
      </span>
    </section>
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

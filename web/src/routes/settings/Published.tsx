// SPDX-License-Identifier: AGPL-3.0-or-later
import { lazy, Suspense, useState } from "react";
import { Link } from "react-router-dom";
import { errorText } from "../../api/error-text";
import { useAllPublications, usePublishEnabled, useUpdatePublication, useWithdraw, type PublicationOverviewView } from "../../api/publications";
import { FormError, secondaryButtonClass } from "../../components/form";
import { ConfirmWithdraw } from "../../note/PublicationBar";
import { useNoteHref } from "../../shell/openInPane";
import { useSession } from "../../state/session";
import { useInWindow } from "../../windows/context";
const PublishDialog = lazy(() => import("../../shell/PublishDialog"));

const snapshotTime = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });

/**
 * Everything she has published from her own vaults, in one place (publish spec §3.11; Published overview design): each
 * note with its copy's state, green when up to date, yellow when the note or a note or file of its bundle changed
 * since, and the bar's own actions, Update copy and Unpublish, through the same hooks and dialogs. The overview never
 * holds a link: Copy link stays on the note's bar, so this page doesn't put every code on one screen.
 */
export default function Published() {
  const publications = useAllPublications();
  const enabled = usePublishEnabled();
  const list = publications.data;
  const older = list?.filter((p) => p.older).length ?? 0;
  return (
    <>
      <p className="text-text-body">The notes of your vaults that anyone with their link can read, as their copy was made.</p>
      {enabled === false && (
        <p className="text-text-body">Publishing is switched off on this server, so the links read nothing until it is on again. Unpublishing still works.</p>
      )}
      {publications.isError && <FormError>{errorText(publications.error)}</FormError>}
      {list?.length === 0 && <p className="text-muted">No published notes.</p>}
      {list && list.length > 0 && (
        <>
          {older > 0 && enabled && <p className="text-text-body">{`${older} of ${list.length} ${list.length === 1 ? "copy is" : older === 1 ? "copies is" : "copies are"} older than ${older === 1 ? "its note" : "their notes"}.`}</p>}
          <ul aria-label="Published notes" className="flex flex-col gap-3">
            {list.map((p) => (
              <Row key={p.path} publication={p} enabled={enabled === true} />
            ))}
          </ul>
        </>
      )}
    </>
  );
}

function Row({ publication: p, enabled }: { publication: PublicationOverviewView; enabled: boolean }) {
  const username = useSession((s) => s.user?.username);
  // From the Settings window, the note takes the window's place in the history.
  const inWindow = useInWindow();
  // In the focused pane, the split view's other pane kept.
  const noteHref = useNoteHref();
  const update = useUpdatePublication(p.path);
  const withdraw = useWithdraw(p.path);
  const [asking, setAsking] = useState(false);
  const [updating, setUpdating] = useState(false);
  const depth = Number(p.depth ?? 0);
  const notes = Number(p.notes ?? 1);
  const older = p.older && enabled;
  const tone = !enabled ? undefined : older ? "yellow" : "green";
  const error = update.error ?? withdraw.error;
  return (
    <li data-tone={tone} className="flex flex-col gap-1">
      <div className="flex items-start gap-2">
        <span aria-hidden className={`mt-2 size-2 flex-none rounded-full ${tone === "green" ? "bg-success" : tone === "yellow" ? "bg-warning" : "bg-dim"}`} />
        <div className="flex min-w-0 flex-1 flex-col">
          <Link to={noteHref(p.path)} replace={inWindow} className="break-words text-accent-text underline underline-offset-[3px] focus-visible:outline-2 focus-visible:outline-accent">
            {p.path}
          </Link>
          <span className="text-sm text-muted">
            {p.older ? "Copy is older than the note" : "Up to date"}
            {depth > 0 && notes > 1 && ` · with ${notes - 1} linked ${notes === 2 ? "note" : "notes"}`}
            {` · Snapshot of ${snapshotTime.format(new Date(p.snapshotAt))}`}
            {` · ${p.publishedBy === username ? "by you" : `by “${p.publishedBy}”`}`}
          </span>
        </div>
      </div>
      <div className="flex flex-wrap gap-2 pl-4">
        {/* A bundle's update walks again: its list is shown first, as on the note's bar. The answer holds the link: reset once in. */}
        {older && (
          <button
            type="button"
            aria-label={`Update copy of ${p.path}`}
            disabled={update.isPending}
            onClick={() => (depth > 0 ? setUpdating(true) : update.mutate(undefined, { onSuccess: () => update.reset() }))}
            className={secondaryButtonClass}
          >
            Update copy
          </button>
        )}
        <button type="button" aria-label={`Unpublish ${p.path}`} onClick={() => setAsking(true)} className={secondaryButtonClass}>
          Unpublish
        </button>
      </div>
      {error && <FormError>{errorText(error)}</FormError>}
      {updating && (
        <Suspense fallback={null}>
          <PublishDialog path={p.path} update={{ depth, without: p.without ?? [] }} onClose={() => setUpdating(false)} />
        </Suspense>
      )}
      {asking && (
        <ConfirmWithdraw busy={withdraw.isPending} onKeep={() => setAsking(false)} onWithdraw={() => withdraw.mutate(undefined, { onSettled: () => setAsking(false) })} />
      )}
    </li>
  );
}

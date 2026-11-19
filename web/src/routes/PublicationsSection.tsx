// SPDX-License-Identifier: AGPL-3.0-or-later
import { Link } from "react-router-dom";
import { errorText } from "../api/error-text";
import { useAllPublications } from "../api/publications";
import { FormError } from "../components/form";
import { useNoteHref } from "../shell/openInPane";
import { useSession } from "../state/session";
import { useInWindow } from "../windows/context";

const snapshotTime = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });

/**
 * The publications of all her own vaults (publish spec §3.11, the plan's decision 12), the section's body within
 * Settings: the note, the snapshot's time in her own time zone, whether the note changed since, and who published it.
 * The server names the writer as it stores it: her session's is her username, a key's or connection's its name.
 */
export default function PublicationsSection() {
  const publications = useAllPublications();
  const username = useSession((s) => s.user?.username);
  // From the Settings window, the note takes the window's place in the history.
  const inWindow = useInWindow();
  // In the focused pane, the split view's other pane kept.
  const noteHref = useNoteHref();
  return (
    <>
      <p className="text-text-body">The notes of your vaults that anyone with their link can read, as their copy was made.</p>
      {publications.isError && <FormError>{errorText(publications.error)}</FormError>}
      {publications.data?.length === 0 && <p className="text-muted">No published notes.</p>}
      {publications.data && publications.data.length > 0 && (
        <ul className="flex flex-col gap-2">
          {publications.data.map((p) => (
            <li key={p.path} className="flex flex-col">
              <Link to={noteHref(p.path)} replace={inWindow} className="break-words text-accent-text underline underline-offset-[3px] focus-visible:outline-2 focus-visible:outline-accent">
                {p.path}
              </Link>
              <span className="text-sm text-muted">
                {`Snapshot of ${snapshotTime.format(new Date(p.snapshotAt))}`}
                {p.older && " · older than the note"}
                {` · ${p.publishedBy === username ? "by you" : `by “${p.publishedBy}”`}`}
              </span>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

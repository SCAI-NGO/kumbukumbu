// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { useRestoreNote, useTrash, type TrashEntry } from "../api/history";
import { crumbOf, fileKindOf, folderOf, isAttachment, isMarker, noteUrl, titleOf, viewUrl, type LightboxState } from "../api/paths";
import { fileKindIcon } from "../components/fileKindIcon";
import { FormError } from "../components/form";
import { Sheet, sizeText } from "../components/Sheet";
import { ago } from "../note/AgentBanner";
import { useEditor } from "../state/editor";

const smallButton =
  "min-h-11 flex-none rounded-[10px] border border-line px-3 text-sm font-medium text-accent-text disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent";

/** A removed folder is named as the folder ("Drafts/") and placed by its parent; anything else by its file. */
const nameOf = (path: string) => (isMarker(path) ? `${titleOf(folderOf(path))}/` : titleOf(path));
const placeOf = (path: string) => crumbOf(isMarker(path) ? folderOf(path) : path);

/**
 * The vault's removed notes, images and PDFs, most recently removed first, each with its kind's icon, when it goes
 * for good and a way back. One restored here leaves the list (the trash is asked again) and is offered to open: a
 * note as the note, an attachment in the lightbox.
 */
export default function TrashSheet({ vault, onClose }: { vault: string; onClose(): void }) {
  const trash = useTrash(vault);
  const navigate = useNavigate();
  const [restored, setRestored] = useState<string[]>([]);
  const days = trash.data?.retentionDays ?? 30;
  const open = (path: string) => {
    onClose();
    useEditor.getState().setOverlay("none");
    if (isAttachment(path)) navigate(viewUrl(path), { state: { paths: [path] } satisfies LightboxState });
    else navigate(noteUrl(path));
  };
  return (
    <Sheet title="Trash" onClose={onClose}>
      <p className="text-sm text-text-body">
        Notes and files removed from {vault} stay here for {days} days, then they are gone for good. Restoring one puts it back where it was.
      </p>
      {restored.length > 0 && (
        <ul aria-label="Restored notes" className="flex flex-col gap-1.5">
          {restored.map((path) => (
            <li key={path} className="flex items-center gap-2 rounded-[12px] border border-accent bg-surface-2 px-3.5 py-2">
              <p role="status" className="min-w-0 flex-1 text-sm text-text">
                Restored <span className="font-medium">{nameOf(path)}</span> to {placeOf(path)}.
              </p>
              {!isMarker(path) && (
                <button type="button" onClick={() => open(path)} className={smallButton}>
                  Open
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {trash.isPending && <p className="text-sm text-muted">Loading the trash …</p>}
      {trash.isError && <FormError>{errorText(trash.error)}</FormError>}
      {trash.data && trash.data.notes.length === 0 && <p className="text-sm text-muted">The trash is empty.</p>}
      {trash.data && trash.data.notes.length > 0 && (
        <ul aria-label="Removed notes" className="flex flex-col gap-1.5">
          {trash.data.notes.map((entry, i) => (
            <TrashRow key={`${i}-${entry.path}-${entry.version}`} entry={entry} onRestored={() => setRestored((r) => [entry.path, ...r.filter((p) => p !== entry.path)])} />
          ))}
        </ul>
      )}
    </Sheet>
  );
}

function TrashRow({ entry, onRestored }: { entry: TrashEntry; onRestored(): void }) {
  const restore = useRestoreNote();
  const kind = fileKindOf(entry.path);
  const Icon = fileKindIcon[kind];
  return (
    <li data-file-kind={kind} className="flex flex-col gap-1 rounded-[12px] border border-line bg-surface-2 px-3.5 py-2">
      <div className="flex items-center gap-2">
        <Icon size={18} strokeWidth={1.8} aria-hidden className="flex-none text-text-body" />
        <div className="flex min-w-0 flex-1 flex-col">
          <span className="truncate font-medium text-text">{nameOf(entry.path)}</span>
          <span className="truncate font-mono text-[11px] text-dim">{placeOf(entry.path)}</span>
        </div>
        <button
          type="button"
          aria-label={`Restore ${nameOf(entry.path)}`}
          disabled={restore.isPending}
          onClick={() => restore.mutate({ path: entry.path, version: entry.version }, { onSuccess: onRestored })}
          className={smallButton}
        >
          Restore
        </button>
      </div>
      <p className="text-sm text-muted">
        Removed {ago(entry.deletedAt)} by {entry.deletedBy} · {sizeText(Number(entry.size))} · gone for good {ago(entry.expiresAt)}
      </p>
      {restore.isError && <FormError>{errorText(restore.error)}</FormError>}
    </li>
  );
}

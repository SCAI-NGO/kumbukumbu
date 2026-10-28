// SPDX-License-Identifier: AGPL-3.0-or-later
import { errorText } from "../api/error-text";
import { useBacklinks, useDeleteFile } from "../api/notes";
import { crumbOf, titleOf } from "../api/paths";
import { secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

/**
 * Asks before an image or PDF goes to the trash, as a note's deletion does: the notes that link to it or embed it,
 * whose links then lead nowhere, are named first. `version` is the one shown: if it changed meanwhile, nothing goes.
 */
export default function DeleteFileDialog({ path, version, onDeleted, onClose }: { path: string; version: string; onDeleted(): void; onClose(): void }) {
  const backlinks = useBacklinks(path);
  const remove = useDeleteFile();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const sources = [...new Set((backlinks.data ?? []).map((b) => b.path))];

  return (
    <div className="absolute inset-0 z-30 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <div ref={dialog} role="dialog" aria-modal="true" aria-labelledby="delete-file-title" className="k-sheet flex w-full max-w-md flex-col gap-3 rounded-[16px] border border-line bg-surface p-[18px]">
        <h2 id="delete-file-title" className="text-[20px] font-bold break-words text-text">
          Delete {titleOf(path)}?
        </h2>
        <p className="text-text-body">It goes to the vault's trash.</p>
        {backlinks.isPending ? (
          <p className="text-sm text-muted">Looking for notes that link here …</p>
        ) : backlinks.isError ? (
          <p className="text-sm text-muted">Couldn't check which notes link here.</p>
        ) : sources.length === 0 ? (
          <p className="text-sm text-muted">No note links here.</p>
        ) : (
          <div>
            <p className="text-sm text-text-body">{sources.length === 1 ? "This note links here; its link will lead nowhere:" : "These notes link here; their links will lead nowhere:"}</p>
            <ul className="mt-1.5 flex max-h-40 flex-col gap-1 overflow-y-auto">
              {sources.map((s) => (
                <li key={s} className="flex items-baseline gap-2 text-text">
                  <span className="truncate">{titleOf(s)}</span>
                  <span className="truncate font-mono text-[11px] text-dim">{crumbOf(s)}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
        {remove.isError && (
          <p role="alert" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
            {errorText(remove.error)}
          </p>
        )}
        <div className="mt-1 flex gap-2.5">
          <button type="button" data-autofocus onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button
            type="button"
            disabled={remove.isPending}
            onClick={() => remove.mutate({ path, version }, { onSuccess: onDeleted })}
            className="min-h-11 flex-1 rounded-[10px] bg-accent px-4 font-medium text-on-accent disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
          >
            Delete
          </button>
        </div>
      </div>
    </div>
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronRight } from "lucide-react";
import { useId, useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { useArchive, useArchivedFiles, useDeleteArchived, useRestoreArchived, type ArchiveItem } from "../api/archive";
import { errorText } from "../api/error-text";
import { storageText } from "../api/storage";
import { crumbOf, fileKindOf, folderOf, isAttachment, noteUrl, titleOf, viewUrl, type FileKind, type LightboxState } from "../api/paths";
import { fileKindIcon } from "../components/fileKindIcon";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { Sheet, timeText } from "../components/Sheet";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";

const smallButton =
  "min-h-11 flex-none rounded-[10px] border border-line px-3 text-sm font-medium text-accent-text disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent";

const nameOf = (path: string) => path.slice(path.lastIndexOf("/") + 1);
/** What the item was called before it was archived: a note by its title, a folder as the folder ("Manual/"). */
const shownName = (item: ArchiveItem) => (item.folder ? `${nameOf(item.originalPath)}/` : titleOf(item.originalPath));
const kindOf = (item: ArchiveItem): FileKind => (item.folder ? "folder" : fileKindOf(item.path));
const filesText = (n: number) => (n === 1 ? "1 file" : `${n} files`);
/** "Restored Manual/ to Work.": named at `to` as the row names an item, a folder as the folder, a note by its title. */
const restoredText = (item: ArchiveItem, to: string) => `Restored ${item.folder ? `${nameOf(to)}/` : titleOf(to)} to ${crumbOf(to)}.`;

/**
 * The vault's archive (`_archive/`, which the tree leaves out): what `archive` put away, newest first, each with
 * where it was, when it was archived and its size. A folder opens here, listing its files. Who may write in the vault
 * restores an item to where it was (or under another name, when that one is taken) or deletes it to the trash.
 */
export default function ArchiveSheet({ vault, onClose }: { vault: string; onClose(): void }) {
  const archive = useArchive(vault);
  const navigate = useNavigate();
  const [done, setDone] = useState<string | null>(null);
  const [restoringAs, setRestoringAs] = useState<{ item: ArchiveItem; message?: string } | null>(null);
  const [deleting, setDeleting] = useState<ArchiveItem | null>(null);
  const open = (path: string) => {
    onClose();
    useEditor.getState().setOverlay("none");
    if (isAttachment(path)) navigate(viewUrl(path), { state: { paths: [path] } satisfies LightboxState });
    else navigate(noteUrl(path));
  };
  const items = archive.data?.items;
  const canWrite = archive.data?.canWrite === true;
  return (
    <Sheet title="Archive" onClose={onClose}>
      <p className="text-sm text-text-body">
        What was archived in {vault} is kept here, out of the way, and still counts towards your storage. Restoring an item puts it back where it was; deleting it moves it to the trash.
      </p>
      {done && (
        <p role="status" className="rounded-[12px] border border-accent bg-surface-2 px-3.5 py-2 text-sm text-text">
          {done}
        </p>
      )}
      {archive.isPending && <p className="text-sm text-muted">Loading the archive …</p>}
      {archive.isError && <FormError>{errorText(archive.error)}</FormError>}
      {items && items.length === 0 && <p className="text-sm text-muted">The archive is empty.</p>}
      {items && items.length > 0 && (
        <ul aria-label="Archived items" className="flex flex-col gap-1.5">
          {items.map((item) => (
            <ArchiveRow
              key={item.path}
              vault={vault}
              item={item}
              canWrite={canWrite}
              onOpen={open}
              onDone={setDone}
              onTaken={(message) => setRestoringAs({ item, message })}
              onDelete={() => setDeleting(item)}
            />
          ))}
        </ul>
      )}
      {restoringAs && (
        <RestoreAsDialog
          vault={vault}
          item={restoringAs.item}
          message={restoringAs.message}
          onRestored={(to) => {
            setRestoringAs(null);
            setDone(restoredText(restoringAs.item, to));
          }}
          onClose={() => setRestoringAs(null)}
        />
      )}
      {deleting && (
        <DeleteArchivedDialog
          vault={vault}
          item={deleting}
          onDeleted={() => {
            setDeleting(null);
            setDone(`Moved ${shownName(deleting)} to the trash.`);
          }}
          onClose={() => setDeleting(null)}
        />
      )}
    </Sheet>
  );
}

function ArchiveRow({
  vault,
  item,
  canWrite,
  onOpen,
  onDone,
  onTaken,
  onDelete,
}: {
  vault: string;
  item: ArchiveItem;
  canWrite: boolean;
  onOpen(path: string): void;
  onDone(sentence: string): void;
  onTaken(message?: string): void;
  onDelete(): void;
}) {
  const restore = useRestoreArchived(vault);
  const [expanded, setExpanded] = useState(false);
  const files = useArchivedFiles(item.path, item.folder && expanded);
  const kind = kindOf(item);
  const Icon = fileKindIcon[kind];
  const name = shownName(item);
  const id = useId();
  return (
    <li data-file-kind={kind} className="flex flex-col gap-1 rounded-[12px] border border-line bg-surface-2 px-3.5 py-2">
      <div className="flex items-center gap-2">
        <Icon size={18} strokeWidth={1.8} aria-hidden className="flex-none text-text-body" />
        <div className="flex min-w-0 flex-1 flex-col">
          <span className="truncate font-medium text-text">{name}</span>
          <span className="truncate font-mono text-[11px] text-dim">{crumbOf(item.originalPath)}</span>
        </div>
      </div>
      <p className="text-sm text-muted">
        {item.archivedAt ? `Archived ${timeText(item.archivedAt)} · ` : ""}
        {item.folder ? `${filesText(Number(item.files))} · ` : ""}
        {storageText(Number(item.bytes))}
      </p>
      <div className="flex flex-wrap gap-2">
        {item.folder ? (
          <button type="button" aria-label={`Open ${name}`} aria-expanded={expanded} aria-controls={id} onClick={() => setExpanded(!expanded)} className={`${smallButton} flex items-center gap-1`}>
            <ChevronRight size={14} aria-hidden className={`transition-transform ${expanded ? "rotate-90" : ""}`} />
            Open
          </button>
        ) : (
          <button type="button" aria-label={`Open ${name}`} onClick={() => onOpen(item.path)} className={smallButton}>
            Open
          </button>
        )}
        {canWrite && (
          <>
            {/* Not disabled while it works, which would drop the focus that the Restore as… dialog gives back: a click then is ignored. */}
            <button
              type="button"
              aria-label={`Restore ${name}`}
              aria-disabled={restore.isPending}
              onClick={() => {
                if (restore.isPending) return;
                restore.mutate(
                  { item, to: item.originalPath },
                  { onSuccess: (result) => ("moved" in result ? onDone(restoredText(item, result.moved)) : onTaken(result.message)) },
                );
              }}
              className={smallButton}
            >
              Restore
            </button>
            <button type="button" aria-label={`Delete ${name}`} onClick={onDelete} className={smallButton}>
              Delete
            </button>
          </>
        )}
      </div>
      {item.folder && expanded && (
        <div id={id}>
          {files.isPending && <p className="text-sm text-muted">Loading its files …</p>}
          {files.isError && <FormError>{errorText(files.error)}</FormError>}
          {files.data && files.data.length === 0 && <p className="text-sm text-muted">It holds no files.</p>}
          {files.data && files.data.length > 0 && (
            <ul aria-label={`Files in ${name}`} className="flex flex-col gap-1">
              {files.data.map((path) => {
                const FileIcon = fileKindIcon[fileKindOf(path)];
                // Its place inside the archived folder: "Manual / images".
                const inner = folderOf(path).slice(item.path.length + 1).split("/").filter(Boolean).join(" / ");
                return (
                  <li key={path} data-file-kind={fileKindOf(path)} className="flex items-center gap-2">
                    <FileIcon size={16} strokeWidth={1.8} aria-hidden className="flex-none text-dim" />
                    <div className="flex min-w-0 flex-1 flex-col">
                      <span className="truncate text-sm text-text-body">{titleOf(path)}</span>
                      {inner && <span className="truncate font-mono text-[11px] text-dim">{inner}</span>}
                    </div>
                    <button type="button" aria-label={`Open ${titleOf(path)}`} onClick={() => onOpen(path)} className={smallButton}>
                      Open
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      )}
      {restore.isError && <FormError>{errorText(restore.error)}</FormError>}
    </li>
  );
}

/**
 * Asks before an archived item goes to the vault's trash, as a note's or a file's deletion does; a folder names how
 * many files go with it.
 */
function DeleteArchivedDialog({ vault, item, onDeleted, onClose }: { vault: string; item: ArchiveItem; onDeleted(): void; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const remove = useDeleteArchived(vault);
  const name = shownName(item);
  return (
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet flex w-full max-w-md flex-col gap-3 rounded-[16px] border border-line bg-surface p-[18px] pb-[max(18px,env(safe-area-inset-bottom))]"
      >
        <h2 id={`${id}-title`} className="text-[20px] font-bold break-words text-text">
          {item.folder ? `Move ${name} and its ${filesText(Number(item.files))} to the trash?` : `Delete ${name}?`}
        </h2>
        <p className="text-text-body">{item.folder ? "They go to the vault's trash, out of the archive." : "It goes to the vault's trash."}</p>
        {remove.isError && <FormError>{errorText(remove.error)}</FormError>}
        <div className="mt-1 flex gap-2.5">
          <button type="button" data-autofocus onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="button" disabled={remove.isPending} onClick={() => remove.mutate(item, { onSuccess: onDeleted })} className={`${primaryButtonClass} flex-1`}>
            Delete
          </button>
        </div>
      </div>
    </div>
  );
}

/**
 * Restores `item` into its original folder under another name, the one it had prefilled: that name is taken, as the
 * tree showed or the server said (`message`). An attachment keeps its extension and a note its `.md`, as in Move.
 */
function RestoreAsDialog({ vault, item, message, onRestored, onClose }: { vault: string; item: ArchiveItem; message?: string; onRestored(to: string): void; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const restore = useRestoreArchived(vault);
  const baseName = nameOf(item.originalPath);
  const isNote = !item.folder && !isAttachment(item.path);
  const extension = item.folder ? "" : isNote ? ".md" : baseName.includes(".") ? baseName.slice(baseName.lastIndexOf(".")) : "";
  const [name, setName] = useState(extension ? baseName.slice(0, baseName.length - extension.length) : baseName);
  const [problem, setProblem] = useState<string | null>(message ?? `There is already something named “${baseName}” in ${crumbOf(item.originalPath)}.`);
  const parent = folderOf(item.originalPath);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setProblem(null);
    const stem = extension && name.trim().toLowerCase().endsWith(extension.toLowerCase()) ? name.trim().slice(0, -extension.length) : name.trim();
    if (!stem || stem.includes("/")) return setProblem("A name can't be empty or contain “/”.");
    const fileName = stem + extension;
    restore.mutate(
      { item, to: `${parent}/${fileName}` },
      { onSuccess: (result) => ("moved" in result ? onRestored(result.moved) : setProblem(result.message ?? `There is already something named “${fileName}” there.`)) },
    );
  };

  return (
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={submit}
        className="k-sheet flex w-full max-w-md flex-col gap-4 rounded-[16px] border border-line bg-surface p-[18px] pb-[max(18px,env(safe-area-inset-bottom))]"
      >
        <h2 id={`${id}-title`} className="text-[20px] font-bold break-words text-text">
          Restore as…
        </h2>
        <p className="text-sm text-text-body">
          {shownName(item)} goes back to {crumbOf(item.originalPath)} under the name you give it.
        </p>
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-name`} className={labelClass}>
            Name
          </label>
          <div className="flex items-center gap-2">
            <input
              id={`${id}-name`}
              data-autofocus
              value={name}
              onChange={(e) => setName(e.target.value)}
              autoComplete="off"
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              className={`${inputClass} min-w-0 flex-1`}
            />
            {extension && !isNote && <span className="flex-none font-mono text-text-body">{extension}</span>}
          </div>
        </div>
        {problem && <FormError>{problem}</FormError>}
        {restore.isError && <FormError>{errorText(restore.error)}</FormError>}
        <div className="flex gap-2.5">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={restore.isPending || !name.trim()} className={`${primaryButtonClass} flex-1`}>
            Restore
          </button>
        </div>
      </form>
    </div>
  );
}

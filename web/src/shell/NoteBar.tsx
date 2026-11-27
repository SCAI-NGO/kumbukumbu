// SPDX-License-Identifier: AGPL-3.0-or-later
import { Columns2, FolderInput, Glasses, Trash2, X } from "lucide-react";
import { NEEDS_CONNECTION } from "../offline/mode";

const buttonClass =
  "flex size-11 flex-none items-center justify-center rounded-[8px] text-muted hover:text-text disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent";

/**
 * The open note's own few actions, in a slim row above the lines about it, in Read and Write mode alike: only what is
 * done to the note you see, not a second menu. Rename and Delete only where the note may be changed (`onRename` null
 * while the note is being named from its heading). From 1280 px on, the left pane's bar has Split view (`split`); in
 * the split view each pane's bar names its note (`title`, dimmed in the pane that isn't focused) and has ✕ (`onClose`);
 * the reading glasses mark the live preview of the note the other pane writes (`preview`). With `needsConnection`
 * Rename and Delete stay, dimmed, with the reason as their title.
 */
export default function NoteBar({
  onRename,
  onDelete,
  title = null,
  dim = false,
  preview = false,
  split = null,
  onClose = null,
  needsConnection = false,
}: {
  onRename: (() => void) | null;
  onDelete: (() => void) | null;
  title?: string | null;
  dim?: boolean;
  preview?: boolean;
  split?: { on: boolean; toggle(): void } | null;
  onClose?: (() => void) | null;
  needsConnection?: boolean;
}) {
  return (
    <div role="group" aria-label="This note" className="flex min-h-11 flex-none items-center justify-end gap-1 border-b border-line bg-bg px-1.5">
      {preview && title !== null && (
        <span role="img" aria-label={`Live preview of ${title}`} title={`Live preview of ${title}`} className="flex flex-none items-center pl-2 text-accent-text">
          <Glasses size={19} strokeWidth={1.8} aria-hidden />
        </span>
      )}
      {title !== null && <span className={`min-w-0 flex-1 truncate px-2 text-sm font-medium ${dim ? "text-dim" : "text-text"}`}>{title}</span>}
      {onRename && (
        <button type="button" aria-label="Rename or move" title={needsConnection ? NEEDS_CONNECTION : "Rename or move"} disabled={needsConnection} onClick={onRename} className={buttonClass}>
          <FolderInput size={19} strokeWidth={1.8} aria-hidden />
        </button>
      )}
      {onDelete && (
        <button type="button" aria-label="Delete note" title={needsConnection ? NEEDS_CONNECTION : "Delete note"} disabled={needsConnection} onClick={onDelete} className={buttonClass}>
          <Trash2 size={19} strokeWidth={1.8} aria-hidden />
        </button>
      )}
      {split && (
        <button
          type="button"
          aria-label="Split view"
          title="Split view"
          aria-pressed={split.on}
          onClick={split.toggle}
          className={`${buttonClass} ${split.on ? "text-accent-text" : ""}`}
        >
          <Columns2 size={19} strokeWidth={1.8} aria-hidden />
        </button>
      )}
      {onClose && (
        <button type="button" aria-label="Close this pane" title="Close this pane" onClick={onClose} className={buttonClass}>
          <X size={19} strokeWidth={1.8} aria-hidden />
        </button>
      )}
    </div>
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { FolderInput, Trash2 } from "lucide-react";

const buttonClass = "flex size-11 flex-none items-center justify-center rounded-[8px] text-muted hover:text-text focus-visible:outline-2 focus-visible:outline-accent";

/**
 * The open note's own few actions, in a slim row above the lines about it, in Read and Write mode alike: only what is
 * done to the note you see, not a second menu. Shown only where the note may be changed; `onRename` is null while the
 * note is being named from its heading.
 */
export default function NoteBar({ onRename, onDelete }: { onRename: (() => void) | null; onDelete(): void }) {
  return (
    <div role="group" aria-label="This note" className="flex min-h-11 flex-none items-center justify-end gap-1 border-b border-line bg-bg px-1.5">
      {onRename && (
        <button type="button" aria-label="Rename or move" title="Rename or move" onClick={onRename} className={buttonClass}>
          <FolderInput size={19} strokeWidth={1.8} aria-hidden />
        </button>
      )}
      <button type="button" aria-label="Delete note" title="Delete note" onClick={onDelete} className={buttonClass}>
        <Trash2 size={19} strokeWidth={1.8} aria-hidden />
      </button>
    </div>
  );
}

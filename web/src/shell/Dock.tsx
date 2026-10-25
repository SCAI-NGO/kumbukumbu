// SPDX-License-Identifier: AGPL-3.0-or-later
import { Ellipsis, FilePlus, Folder, Plus, Search, Upload } from "lucide-react";
import { Menu, type MenuItem } from "../components/Menu";
import { useEditor } from "../state/editor";

/**
 * The bottom of Read mode: files, search (showing its mode), the open note's actions and "+", all in the thumb's
 * reach. `newNote` and `upload` are null while there is no vault, `noteActions` while no note is open.
 */
export default function Dock(props: { newNote: (() => void) | null; upload: (() => void) | null; noteActions: MenuItem[] | null }) {
  const { newNote, upload, noteActions } = props;
  const searchMode = useEditor((s) => s.searchMode);
  const setOverlay = useEditor((s) => s.setOverlay);
  const items: MenuItem[] = [
    { label: "New note", icon: FilePlus, onSelect: () => newNote?.(), disabled: !newNote },
    { label: "Upload", icon: Upload, onSelect: () => upload?.(), disabled: !upload },
  ];

  return (
    <nav aria-label="Vault actions" className="flex flex-none items-center gap-2.5 border-t border-line bg-bg px-3 pt-2.5 pb-[max(20px,env(safe-area-inset-bottom))]">
      <button
        type="button"
        aria-label="Files"
        onClick={() => setOverlay("tree")}
        className="flex size-12 flex-none items-center justify-center rounded-[14px] border border-line bg-surface text-text-body focus-visible:outline-2 focus-visible:outline-accent"
      >
        <Folder size={22} strokeWidth={1.8} aria-hidden />
      </button>
      <button
        type="button"
        onClick={() => setOverlay("search")}
        className="flex h-12 min-w-0 flex-1 items-center gap-2.5 rounded-full border border-line bg-surface pr-2 pl-4 text-left text-muted focus-visible:outline-2 focus-visible:outline-accent"
      >
        <Search size={20} strokeWidth={1.9} aria-hidden />
        <span className="min-w-0 flex-1 truncate text-[15px]">Search</span>
        <span className="flex-none rounded-full bg-surface-2 px-2.5 py-1 font-mono text-[11px] text-accent-text">{searchMode}</span>
      </button>
      {noteActions && (
        <Menu
          label="Note actions"
          items={noteActions}
          placement="above"
          className="flex size-12 items-center justify-center rounded-[14px] border border-line bg-surface text-text-body focus-visible:outline-2 focus-visible:outline-accent"
        >
          <Ellipsis size={22} strokeWidth={1.8} aria-hidden />
        </Menu>
      )}
      <Menu
        label="New"
        items={items}
        placement="above"
        className="flex size-12 items-center justify-center rounded-[14px] bg-accent text-on-accent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
      >
        <Plus size={22} strokeWidth={2.2} aria-hidden />
      </Menu>
    </nav>
  );
}

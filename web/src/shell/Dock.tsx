// SPDX-License-Identifier: AGPL-3.0-or-later
import { CalendarClock, Ellipsis, FilePlus, Folder, FolderPlus, Plus, Search, Upload } from "lucide-react";
import { useRef } from "react";
import { useNavigate } from "react-router-dom";
import Badge from "../calendar/Badge";
import { useBadge } from "../calendar/useBadge";
import { Menu, type MenuItem } from "../components/Menu";
import { useEditor } from "../state/editor";

const badgeId = "dock-calendar-badge";

/**
 * The bottom of Read mode: files, search (showing its mode), the open note's actions and "+", all in the thumb's
 * reach; on a phone (`phone`) also the calendar, What's on with its count. `newNote`, `newFolder` and `upload` are null
 * while there is no vault, `noteActions` while no note is open.
 */
export default function Dock(props: { phone: boolean; newNote: (() => void) | null; newFolder: (() => void) | null; upload: (() => void) | null; noteActions: MenuItem[] | null }) {
  const { phone, newNote, newFolder, upload, noteActions } = props;
  const navigate = useNavigate();
  const badge = useBadge();
  const searchMode = useEditor((s) => s.searchMode);
  const setOverlay = useEditor((s) => s.setOverlay);
  const standIn = useRef<HTMLInputElement>(null);
  const items: MenuItem[] = [
    { label: "New note", icon: FilePlus, onSelect: () => newNote?.(), disabled: !newNote },
    { label: "New folder…", icon: FolderPlus, onSelect: () => newFolder?.(), disabled: !newFolder },
    { label: "Upload", icon: Upload, onSelect: () => upload?.(), disabled: !upload },
  ];

  return (
    <nav aria-label="Vault actions" className="relative flex flex-none items-center gap-2.5 border-t border-line bg-bg px-3 pt-2.5 pb-[max(20px,env(safe-area-inset-bottom))]">
      <button
        type="button"
        aria-label="Files"
        onClick={() => setOverlay("tree")}
        className="flex size-12 flex-none items-center justify-center rounded-[14px] border border-line bg-surface text-text-body focus-visible:outline-2 focus-visible:outline-accent"
      >
        <Folder size={22} strokeWidth={1.8} aria-hidden />
      </button>
      {phone && (
        // Its name says what it is, What's on's count is its description. The badge sits on its top corner, 4 px past
        // its edge into the dock's 10 px gap, so it covers none of its neighbours.
        <button
          type="button"
          aria-label="Calendar"
          title="What's on"
          aria-describedby={badge.count > 0 ? badgeId : undefined}
          onClick={() => {
            setOverlay("none");
            navigate("/whats-on");
          }}
          className="relative flex size-12 flex-none items-center justify-center rounded-[14px] border border-line bg-surface text-text-body focus-visible:outline-2 focus-visible:outline-accent"
        >
          <CalendarClock size={22} strokeWidth={1.8} aria-hidden />
          <Badge id={badgeId} count={badge.count} overdue={badge.overdue} className="pointer-events-none absolute top-0 right-0 translate-x-1" />
        </button>
      )}
      {/* iOS raises the keyboard only for a focus inside the tap, and the search sheet may still be loading then:
          this field takes the focus at once, keeping the keyboard up until the sheet's input takes it over. */}
      <input
        ref={standIn}
        aria-label="Search, opening"
        tabIndex={-1}
        data-stand-in
        data-focus-return="dock-search"
        className="pointer-events-none absolute size-px text-[16px] opacity-0"
      />
      <button
        id="dock-search"
        type="button"
        onClick={() => {
          standIn.current?.focus({ preventScroll: true });
          setOverlay("search");
        }}
        className="@container flex h-12 min-w-0 flex-1 items-center gap-2.5 rounded-full border border-line bg-surface pr-2 pl-4 text-left text-muted focus-visible:outline-2 focus-visible:outline-accent"
      >
        <Search size={20} strokeWidth={1.9} aria-hidden />
        <span className="min-w-0 flex-1 truncate text-[15px]">Search</span>
        {/* Where the field is too narrow for both (about 170 px), the mode gives way and the word stays. */}
        <span className="flex-none rounded-full bg-surface-2 px-2.5 py-1 font-mono text-[11px] text-accent-text @max-[11rem]:hidden">{searchMode}</span>
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

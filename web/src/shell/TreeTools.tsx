// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowDownAZ, CalendarDays, ClockArrowDown, Ellipsis, History, Plus, Search, SquareKanban, TextSearch, Upload, X } from "lucide-react";
import { Fragment, useEffect, useId, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { Link } from "react-router-dom";
import Badge from "../calendar/Badge";
import { Menu, type MenuItem } from "../components/Menu";
import type { FlagName } from "../flags/api";
import FlagChips from "../flags/FlagChips";
import { useEditor } from "../state/editor";
import { cut, type Tool } from "./toolbarCut";
import type { FlagFilter } from "./treeFilter";
import type { TreeOrder } from "./treeOrder";

/** What the tree's search holds while it is open: the words typed and the flag chip pressed, if any. */
export type TreeSearch = { words: string; flag: FlagFilter | null };

/** What the Sort button says: the order shown, and what a tap does. */
export const sortLabel = (order: TreeOrder) => (order === "name" ? "Sorted by name. Sort by last changed" : "Sorted by last changed. Sort by name");

const target = "flex min-h-12 items-center rounded-[8px] focus-visible:outline-2 focus-visible:outline-accent";
const iconTarget = `${target} relative size-12 flex-none justify-center text-muted`;

/**
 * The tree's toolbar, one row directly above the folders (UI round spec §2.1): + New note, then Upload, the vault's
 * Calendar (with its share of What's on), its Board, Recent changes across the vaults, the order, Find in this vault,
 * and the vault's menu. What the person can't do (`writes`) isn't offered. Where the row is too narrow (its width
 * followed live, as the sidebar is resized), `cut` moves the rarer tools to the top of the menu. Find opens the search
 * by name and flag over the vault the tree shows; closing it (✕ or Escape) clears it and gives the full tree back;
 * Search the contents… hands the words to the search sheet, for a search by words and meaning.
 */
export default function TreeTools({
  vault,
  writes,
  onNewNote,
  onUpload,
  onRecent,
  calendar,
  board,
  menu,
  flags,
  search,
  onSearch,
  order,
  onOrder,
  nothing,
}: {
  vault: string;
  writes: boolean;
  /** A note in the vault's root, as the menu's New note here. */
  onNewNote(): void;
  onUpload(): void;
  /** Recent changes, a page of its own: the drawer around the tree closes. */
  onRecent(): void;
  calendar: { open(): void; share?: { count: number; overdue: boolean } };
  /** Null while the vault has no board. */
  board: (() => void) | null;
  /**
   * The vault's menu. Its items with the `id` of a tool (the root's Upload here is `upload`, Recent changes `recent`)
   * give way to that tool where the row moves it into the menu, so each is there once.
   */
  menu: MenuItem[];
  /** The flags some note of the vault carries: Find by name's chips. */
  flags: readonly FlagName[];
  /** Null while the search is closed. */
  search: TreeSearch | null;
  onSearch(search: TreeSearch | null): void;
  order: TreeOrder;
  onOrder(order: TreeOrder): void;
  /** The search found nothing. */
  nothing: boolean;
}) {
  const badge = useId();
  const row = useRef<HTMLDivElement>(null);
  const newNote = useRef<HTMLButtonElement>(null);
  const [room, setRoom] = useState({ width: 0, newNote: 0 });
  const opener = useRef<HTMLButtonElement>(null);
  const closed = useRef(false);
  useEffect(() => {
    if (search || !closed.current) return;
    closed.current = false;
    // Find may sit in the menu where the row is narrow: the focus goes back to the menu's button then.
    (opener.current ?? row.current?.querySelector<HTMLElement>("[aria-haspopup=menu]"))?.focus();
  }, [search]);
  // Without a layout (no ResizeObserver) the width stays unknown, and everything shows. + New note is observed too: it
  // comes and goes with `writes` (the docked sidebar stays mounted across vaults) and changes as the web font arrives.
  const open = !search;
  useLayoutEffect(() => {
    const box = row.current;
    if (!open || !box || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver((entries) => {
      const ofRow = entries.find((e) => e.target === box);
      setRoom((room) => ({ width: ofRow ? ofRow.contentRect.width : room.width, newNote: newNote.current?.offsetWidth ?? 0 }));
    });
    observer.observe(box);
    if (newNote.current) observer.observe(newNote.current);
    return () => observer.disconnect();
  }, [open, writes]);
  const close = () => {
    closed.current = true;
    onSearch(null);
  };
  // Escape closes the search alone: not the drawer around it, which listens on the document.
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key !== "Escape") return;
    e.preventDefault();
    e.stopPropagation();
    close();
  };
  const searchContents = () => {
    useEditor.getState().setSearchQuery(search?.words.trim() ?? "");
    useEditor.getState().setOverlay("search");
  };

  if (!search) {
    const sort = sortLabel(order);
    const share = calendar.share && calendar.share.count > 0 ? calendar.share : undefined;
    const toggleOrder = () => onOrder(order === "name" ? "changed" : "name");
    const findOpen = () => onSearch({ words: "", flag: null });
    const tools: Tool[] = [...(writes ? (["newNote", "upload"] as const) : []), "calendar", ...(board ? (["board"] as const) : []), "recent", "sort", "find", "menu"];
    const { shown, inMenu } = cut(room.width, tools, room.newNote);
    // The vault's menu has Calendar and Board already; Upload here and Recent changes give way to the moved ones.
    const moved: MenuItem[] = inMenu.flatMap((t): MenuItem[] => {
      if (t === "upload") return [{ label: "Upload", icon: Upload, onSelect: onUpload }];
      if (t === "sort") return [{ label: sort, icon: order === "name" ? ClockArrowDown : ArrowDownAZ, onSelect: toggleOrder }];
      if (t === "recent") return [{ label: "Recent changes", icon: History, onSelect: onRecent }];
      if (t === "find") return [{ label: "Find in this vault", icon: Search, onSelect: findOpen }];
      return [];
    });
    const items = [...moved, ...menu.filter((i) => !(i.id && inMenu.includes(i.id as Tool)))];
    const targets: Record<Tool, ReactNode> = {
      newNote: (
        <button ref={newNote} type="button" onClick={onNewNote} className={`${target} flex-none`}>
          <span className="flex h-9 items-center gap-1 rounded-[8px] bg-accent pr-3 pl-2.5 text-[14px] font-medium whitespace-nowrap text-on-accent">
            <Plus size={16} aria-hidden />
            New note
          </span>
        </button>
      ),
      upload: (
        <button type="button" aria-label="Upload" title="Upload" onClick={onUpload} className={iconTarget}>
          <Upload size={20} aria-hidden />
        </button>
      ),
      calendar: (
        <button type="button" aria-label="Calendar" title="Calendar" aria-describedby={share && badge} onClick={calendar.open} className={iconTarget}>
          <CalendarDays size={20} aria-hidden />
          {share && <Badge id={badge} count={share.count} overdue={share.overdue} className="pointer-events-none absolute top-0.5 right-0" />}
        </button>
      ),
      board: (
        <button type="button" aria-label="Board" title="Board" onClick={() => board?.()} className={iconTarget}>
          <SquareKanban size={20} aria-hidden />
        </button>
      ),
      // A link, so it can open in a tab of its own; a plain tap goes the menu's way.
      recent: (
        <Link
          to="/recent"
          aria-label="Recent changes"
          title="Recent changes"
          onClick={(e) => {
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            e.preventDefault();
            onRecent();
          }}
          className={iconTarget}
        >
          <History size={20} aria-hidden />
        </Link>
      ),
      sort: (
        <button type="button" aria-label={sort} title={sort} onClick={toggleOrder} className={iconTarget}>
          {order === "name" ? <ArrowDownAZ size={20} aria-hidden /> : <ClockArrowDown size={20} aria-hidden />}
        </button>
      ),
      find: (
        <button ref={opener} type="button" aria-label="Find in this vault" title="Find in this vault" onClick={findOpen} className={iconTarget}>
          <Search size={20} aria-hidden />
        </button>
      ),
      menu: (
        <div className="ml-auto flex-none">
          <Menu label={`Actions for ${vault}`} title={`Actions for ${vault}`} items={items} placement="below" className={iconTarget}>
            <Ellipsis size={20} aria-hidden />
          </Menu>
        </div>
      ),
    };
    return (
      <div ref={row} role="group" aria-label="Tools for this vault" className="flex flex-none items-center px-2 pb-2">
        {shown.map((t) => (
          <Fragment key={t}>{targets[t]}</Fragment>
        ))}
      </div>
    );
  }
  return (
    <div role="search" aria-label="Find in this vault" onKeyDown={onKeyDown} className="flex flex-none flex-col gap-1 px-3 pb-2">
      <div className="flex items-center gap-1">
        <input
          type="search"
          aria-label="Find by name"
          autoFocus
          value={search.words}
          onChange={(e) => onSearch({ ...search, words: e.target.value })}
          placeholder="Find by name"
          autoComplete="off"
          autoCapitalize="none"
          autoCorrect="off"
          spellCheck={false}
          enterKeyHint="search"
          className="h-12 min-w-0 flex-1 rounded-full border border-line bg-bg px-4 text-[16px] text-text outline-none placeholder:text-muted focus:border-accent [&::-webkit-search-cancel-button]:hidden"
        />
        <button type="button" aria-label="Close the search" onClick={close} className={`${target} size-12 flex-none justify-center text-muted`}>
          <X size={20} aria-hidden />
        </button>
      </div>
      <FlagChips flag={search.flag} onFlag={(flag) => onSearch({ ...search, flag })} shown={flags} />
      {nothing && (
        <p role="status" className="px-2.5 pt-1 text-sm text-muted">
          Nothing called that here.
        </p>
      )}
      <button type="button" onClick={searchContents} className={`${target} gap-2 px-2.5 text-left text-[15px] text-accent-text`}>
        <TextSearch size={18} aria-hidden className="flex-none" />
        Search the contents…
      </button>
    </div>
  );
}

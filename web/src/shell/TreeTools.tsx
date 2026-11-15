// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowDownAZ, CalendarDays, ClockArrowDown, Ellipsis, History, Plus, Search, SquareKanban, TextSearch, Upload, X } from "lucide-react";
import { useEffect, useId, useRef, type KeyboardEvent } from "react";
import { Link } from "react-router-dom";
import Badge from "../calendar/Badge";
import { Menu, type MenuItem } from "../components/Menu";
import FlagChips from "../flags/FlagChips";
import { useEditor } from "../state/editor";
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
 * and the vault's menu. What the person can't do (`writes`) isn't offered. Find opens the search by name and flag over
 * the vault the tree shows; closing it (✕ or Escape) clears it and gives the full tree back; Search the contents…
 * hands the words to the search sheet, for a search by words and meaning.
 */
export default function TreeTools({
  vault,
  writes,
  onNewNote,
  onUpload,
  calendar,
  board,
  menu,
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
  calendar: { open(): void; share?: { count: number; overdue: boolean } };
  /** Null while the vault has no board. */
  board: (() => void) | null;
  /** The vault's menu. */
  menu: MenuItem[];
  /** Null while the search is closed. */
  search: TreeSearch | null;
  onSearch(search: TreeSearch | null): void;
  order: TreeOrder;
  onOrder(order: TreeOrder): void;
  /** The search found nothing. */
  nothing: boolean;
}) {
  const badge = useId();
  const opener = useRef<HTMLButtonElement>(null);
  const closed = useRef(false);
  useEffect(() => {
    if (search || !closed.current) return;
    closed.current = false;
    opener.current?.focus();
  }, [search]);
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
    return (
      <div role="group" aria-label="Tools for this vault" className="flex flex-none items-center px-2 pb-2">
        {writes && (
          <button type="button" onClick={onNewNote} className={`${target} flex-none pr-1`}>
            <span className="flex h-9 items-center gap-1 rounded-[8px] bg-accent px-3 text-[14px] font-medium text-on-accent">
              <Plus size={16} aria-hidden />
              New note
            </span>
          </button>
        )}
        {writes && (
          <button type="button" aria-label="Upload" title="Upload" onClick={onUpload} className={iconTarget}>
            <Upload size={20} aria-hidden />
          </button>
        )}
        <button type="button" aria-label="Calendar" title="Calendar" aria-describedby={share && badge} onClick={calendar.open} className={iconTarget}>
          <CalendarDays size={20} aria-hidden />
          {share && <Badge id={badge} count={share.count} overdue={share.overdue} className="pointer-events-none absolute top-0.5 right-0" />}
        </button>
        {board && (
          <button type="button" aria-label="Board" title="Board" onClick={board} className={iconTarget}>
            <SquareKanban size={20} aria-hidden />
          </button>
        )}
        {/* A page of its own, as the calendar: the drawer around the tree closes. */}
        <Link to="/recent" aria-label="Recent changes" title="Recent changes" onClick={() => useEditor.getState().setOverlay("none")} className={iconTarget}>
          <History size={20} aria-hidden />
        </Link>
        <button type="button" aria-label={sort} title={sort} onClick={() => onOrder(order === "name" ? "changed" : "name")} className={iconTarget}>
          {order === "name" ? <ArrowDownAZ size={20} aria-hidden /> : <ClockArrowDown size={20} aria-hidden />}
        </button>
        <button ref={opener} type="button" aria-label="Find in this vault" title="Find in this vault" onClick={() => onSearch({ words: "", flag: null })} className={iconTarget}>
          <Search size={20} aria-hidden />
        </button>
        <div className="ml-auto flex-none">
          <Menu label={`Actions for ${vault}`} items={menu} title={`Actions for ${vault}`} placement="below" className={iconTarget}>
            <Ellipsis size={20} aria-hidden />
          </Menu>
        </div>
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
      <FlagChips flag={search.flag} onFlag={(flag) => onSearch({ ...search, flag })} />
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

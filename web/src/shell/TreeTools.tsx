// SPDX-License-Identifier: AGPL-3.0-or-later
import { History, Search, TextSearch, X } from "lucide-react";
import { useEffect, useId, useRef, type KeyboardEvent } from "react";
import { Link } from "react-router-dom";
import FlagChips from "../flags/FlagChips";
import { useEditor } from "../state/editor";
import type { FlagFilter } from "./treeFilter";
import type { TreeOrder } from "./treeOrder";

/** What the tree's search holds while it is open: the words typed and the flag chip pressed, if any. */
export type TreeSearch = { words: string; flag: FlagFilter | null };

const orders: { id: TreeOrder; label: string }[] = [
  { id: "name", label: "Name" },
  { id: "changed", label: "Last changed" },
];

const target = "flex min-h-12 items-center rounded-[8px] focus-visible:outline-2 focus-visible:outline-accent";

/**
 * The tree's toolbar, under the vault's heading (spec §3.1, §3.2, §4): a search by name and flag over the vault the tree
 * shows, the order, and Recent changes across the vaults. Closing the search (✕ or Escape) clears it and gives the full tree back; Search the contents…
 * hands the words to the search sheet, for a search by words and meaning.
 */
export default function TreeTools({
  search,
  onSearch,
  order,
  onOrder,
  nothing,
}: {
  /** Null while the search is closed. */
  search: TreeSearch | null;
  onSearch(search: TreeSearch | null): void;
  order: TreeOrder;
  onOrder(order: TreeOrder): void;
  /** The search found nothing. */
  nothing: boolean;
}) {
  const name = useId();
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
  // A page of its own, as the calendar: the drawer around the tree closes.
  const recent = (
    <Link to="/recent" onClick={() => useEditor.getState().setOverlay("none")} className={`${target} gap-2 self-start px-2.5 text-[15px] text-text-body`}>
      <History size={18} aria-hidden className="flex-none text-muted" />
      Recent changes
    </Link>
  );

  const orderSwitch = (
    <div role="radiogroup" aria-label="Order" className="flex min-w-0 flex-1 rounded-[9px] border border-line bg-bg p-0.5">
      {orders.map((o) => (
        <label
          key={o.id}
          className={`flex min-h-12 flex-1 cursor-pointer items-center justify-center rounded-[7px] px-1 text-center text-[13px] has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-accent ${
            order === o.id ? "bg-accent font-semibold text-on-accent" : "text-text-body"
          }`}
        >
          <input type="radio" name={name} value={o.id} checked={order === o.id} onChange={() => onOrder(o.id)} className="sr-only" />
          {o.label}
        </label>
      ))}
    </div>
  );

  if (!search) {
    return (
      <div className="flex flex-none flex-col px-3 pb-2">
        <div className="flex items-center gap-2">
          <button ref={opener} type="button" aria-label="Find in this vault" title="Find in this vault" onClick={() => onSearch({ words: "", flag: null })} className={`${target} size-12 flex-none justify-center text-muted`}>
            <Search size={20} aria-hidden />
          </button>
          {orderSwitch}
        </div>
        {recent}
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
      {orderSwitch}
      {recent}
    </div>
  );
}

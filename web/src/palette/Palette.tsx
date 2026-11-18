// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueries, type UseQueryResult } from "@tanstack/react-query";
import { Search, Star, X } from "lucide-react";
import { useEffect, useId, useMemo, useState, type KeyboardEvent, type SyntheticEvent } from "react";
import { createPortal } from "react-dom";
import { useNavigate } from "react-router-dom";
import { favouriteKey, useFavourites, type FavouriteInput } from "../api/favourites";
import { treeQuery, useVaults, type TreeView } from "../api/notes";
import { crumbOf, inVault, noteUrl, titleOf, vaultOf } from "../api/paths";
import { usePublishEnabled } from "../api/publications";
import { useMarketplaces } from "../api/skills";
import { useDesktop } from "../hooks/useDesktop";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { applyScheme } from "../theme/apply";
import { useCloseWindow } from "../windows/context";
import { useWindows } from "../windows/store";
import { filterRows } from "./filter";
import { commandRows, noteRows, vaultRows, type Row } from "./rows";
import { withMod } from "./usePaletteShortcut";

const keepFocus = (e: SyntheticEvent) => e.preventDefault();
// Structurally shared by the query: the same trees give the same array, so the rows are built again only when one changes.
const treesOf = (results: UseQueryResult<TreeView>[]) => results.map((r) => r.data);

/** Every row the person may open: the commands she has, and the vaults and notes of the vaults the session lists. */
function useRows(): Row[] {
  const admin = useSession((s) => s.user?.role) === "admin";
  // As the Skills window decides its tabs.
  const shared = usePublishEnabled() === true;
  const marketplaces = useMarketplaces().data?.enabled === true;
  const vaults = useVaults().data;
  // Each vault's tree as the drawer has it, from the cache where it was read before.
  const trees = useQueries({ queries: (vaults ?? []).map((v) => treeQuery(v.name)), combine: treesOf });
  return useMemo(
    () => [
      ...commandRows({ admin, shared, marketplaces }),
      ...vaultRows(vaults ?? []),
      ...(vaults ?? []).flatMap((v, i) => (trees[i] ? noteRows(v.name, trees[i].root) : [])),
    ],
    [admin, shared, marketplaces, vaults, trees],
  );
}

/** A note opened lately, as its row; one not in its tree (yet) still has one. */
function recentRow(path: string, byKey: Map<string, Row>): Row {
  const favourite: FavouriteInput = { kind: "note", vault: vaultOf(path), path: inVault(path) };
  const key = favouriteKey(favourite);
  return byKey.get(key) ?? { key, group: "Notes", name: titleOf(path), place: crumbOf(path), favourite, run: { to: noteUrl(path) } };
}

type Section = { heading: string; rows: Row[] };

/**
 * The command palette: favourites, commands, vaults and notes in one list, a word or two away. On the desktop a panel
 * near the top with the field above the list; on the phone a sheet from below with the field near the thumb. Rendered
 * into the body, so that it is reachable over a window too (the shell under one is inert).
 */
export default function Palette() {
  const navigate = useNavigate();
  const closeWindow = useCloseWindow();
  const recentPaths = useEditor((s) => s.recentPaths);
  const close = () => useEditor.getState().setOverlay("none");
  const dialog = useDialog<HTMLDivElement>(close);
  const desktop = useDesktop();
  const id = useId();
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const rows = useRows();
  const favourites = useFavourites();
  const typed = query.trim();

  const sections = useMemo((): Section[] => {
    const byKey = new Map(rows.map((r) => [r.key, r]));
    if (!typed) {
      // A favourite with no row (a command this version doesn't know, a note not in its tree) isn't shown.
      const starred = favourites.list.flatMap((f) => byKey.get(favouriteKey(f)) ?? []);
      return [
        { heading: "Favourites", rows: starred },
        { heading: "Recent", rows: recentPaths.map((p) => recentRow(p, byKey)) },
      ];
    }
    const keys = new Set(favourites.list.map(favouriteKey));
    const found = filterRows(rows, typed, keys);
    return [
      { heading: "Favourites", rows: found.filter((r) => keys.has(r.key)) },
      ...(["Commands", "Vaults", "Notes"] as const).map((group) => ({ heading: group, rows: found.filter((r) => r.group === group && !keys.has(r.key)) })),
    ];
  }, [rows, typed, favourites.list, recentPaths]);

  const searchLabel = `Search notes for ‘${typed}’`;
  const entries: (Row | null)[] = [...sections.flatMap((s) => s.rows), ...(typed ? [null] : [])];
  const at = Math.min(active, entries.length - 1);
  const optionId = (i: number) => `${id}-option-${i}`;

  useEffect(() => {
    if (at >= 0) document.getElementById(`${id}-option-${at}`)?.scrollIntoView?.({ block: "nearest" });
  }, [id, at]);

  const run = (entry: Row | null) => {
    close();
    if (entry === null) {
      // Search is the shell's sheet: under a window it would be out of reach, so the window closes first.
      if (useWindows.getState().open) closeWindow();
      useEditor.getState().setSearchQuery(typed);
      useEditor.getState().setOverlay("search");
    } else if ("scheme" in entry.run) applyScheme(entry.run.scheme);
    else void navigate(entry.run.to);
  };

  const onKeyDown = (e: KeyboardEvent) => {
    // While an IME composes, Enter and the arrows are the IME's.
    if (e.nativeEvent.isComposing || e.keyCode === 229) return;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      if (entries.length) setActive((at + (e.key === "ArrowDown" ? 1 : entries.length - 1)) % entries.length);
    } else if (e.key === "Enter") {
      if (at >= 0) run(entries[at]);
    } else if (withMod(e, "d")) {
      // Caught here only, as a browser's bookmark is: the highlighted row's star.
      const row = entries[at];
      if (row) favourites.toggle(row.favourite);
    } else return;
    e.preventDefault();
  };

  let index = 0;
  const option = (row: Row | null) => {
    const i = index++;
    const on = !!row && favourites.has(row.favourite);
    const highlighted = i === at;
    return (
      <li
        key={row?.key ?? "search"}
        id={optionId(i)}
        role="option"
        aria-selected={highlighted}
        aria-label={row ? (row.place ? `${row.name}, ${row.place}` : row.name) : searchLabel}
        onPointerDown={keepFocus}
        onMouseDown={keepFocus}
        onMouseMove={() => !highlighted && setActive(i)}
        onClick={() => run(row)}
        className={`group flex min-h-11 cursor-pointer items-center gap-2 rounded-[8px] pr-1 pl-3 ${highlighted ? "bg-accent/15" : ""}`}
      >
        {row ? (
          <>
            <span className={`flex min-w-0 flex-1 ${desktop ? "items-baseline gap-3" : "flex-col py-1"}`}>
              <span className="min-w-0 truncate text-[15px] text-text">{row.name}</span>
              <span className={`min-w-0 truncate font-mono text-[12px] text-dim ${desktop ? "ml-auto flex-none" : ""}`}>{row.place}</span>
            </span>
            <button
              type="button"
              tabIndex={-1}
              aria-label={on ? `Remove ${row.name} from favourites` : `Add ${row.name} to favourites`}
              aria-pressed={on}
              onClick={(e) => {
                e.stopPropagation();
                favourites.toggle(row.favourite);
              }}
              className={`flex size-11 flex-none items-center justify-center rounded-[10px] ${on ? "text-accent-text" : "text-muted"} ${
                desktop && !on && !highlighted ? "opacity-0 group-hover:opacity-100" : ""
              }`}
            >
              <Star size={18} strokeWidth={1.8} fill={on ? "currentColor" : "none"} aria-hidden />
            </button>
          </>
        ) : (
          <span className="flex min-w-0 flex-1 items-center gap-2 py-1 text-[15px] text-text">
            <Search size={16} strokeWidth={1.9} aria-hidden className="flex-none text-muted" />
            <span className="truncate">{searchLabel}</span>
          </span>
        )}
      </li>
    );
  };

  const shown = sections.filter((s) => s.rows.length);
  const hint = !typed && favourites.ready && sections[0].rows.length === 0;
  const listId = `${id}-list`;
  const list = (
    <div className="min-h-0 flex-1 overflow-y-auto p-1.5">
      {hint && <p className="px-3 py-2.5 text-sm text-muted">Star anything here to keep it on top.</p>}
      <ul id={listId} role="listbox" aria-label="Favourites and commands" className="flex flex-col gap-1">
        {shown.map((s) => (
          <li key={s.heading} role="presentation">
            <ul role="group" aria-label={s.heading}>
              <li role="presentation" aria-hidden className="px-3 pt-2 pb-1 font-mono text-[11px] tracking-[0.08em] text-muted uppercase">
                {s.heading}
              </li>
              {s.rows.map(option)}
            </ul>
          </li>
        ))}
        {typed && <li role="presentation">{option(null)}</li>}
      </ul>
    </div>
  );
  const field = (
    <label className="flex h-12 flex-none items-center gap-2.5 rounded-full border border-line bg-surface-2 px-4 focus-within:outline-2 focus-within:outline-accent">
      <Search size={20} strokeWidth={1.9} aria-hidden className="flex-none text-muted" />
      <input
        type="text"
        role="combobox"
        aria-label="Find a command, vault or note"
        aria-expanded
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={at >= 0 ? optionId(at) : undefined}
        data-autofocus
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          setActive(0);
        }}
        placeholder="Commands, vaults, notes"
        autoComplete="off"
        className="min-w-0 flex-1 bg-transparent text-[16px] text-text outline-none placeholder:text-muted"
      />
    </label>
  );

  return createPortal(
    <div className={`fixed inset-0 z-[60] flex justify-center ${desktop ? "items-start bg-black/35 pt-[12vh]" : "items-end bg-black/55"}`} onClick={(e) => e.target === e.currentTarget && close()}>
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-label="Favourites and commands"
        onKeyDown={onKeyDown}
        className={
          desktop
            ? "flex max-h-[70vh] w-[min(640px,100vw-32px)] flex-col overflow-hidden rounded-[14px] border border-line bg-surface shadow-2xl"
            : "k-sheet flex max-h-[85dvh] w-full flex-col rounded-t-[16px] border border-b-0 border-line bg-surface pb-[max(16px,env(safe-area-inset-bottom))]"
        }
      >
        {desktop ? (
          <>
            <div className="flex-none border-b border-line p-2">{field}</div>
            {list}
          </>
        ) : (
          <>
            <div className="flex flex-none items-center justify-between py-1 pr-2 pl-[18px]">
              <h2 className="text-[17px] font-bold text-text">Favourites and commands</h2>
              <button
                type="button"
                aria-label="Close"
                onClick={close}
                className="flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent"
              >
                <X size={22} strokeWidth={1.9} aria-hidden />
              </button>
            </div>
            {list}
            <div className="flex-none border-t border-line px-3 pt-2.5">{field}</div>
          </>
        )}
      </div>
    </div>,
    document.body,
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueries, type UseQueryResult } from "@tanstack/react-query";
import { Search, Star, X } from "lucide-react";
import { useEffect, useId, useMemo, useState, type KeyboardEvent, type SyntheticEvent } from "react";
import { createPortal, flushSync } from "react-dom";
import { useNavigate } from "react-router-dom";
import { favouriteKey, useFavourites, type Favourite, type FavouriteInput } from "../api/favourites";
import { treeQuery, useVaults, type TreeView } from "../api/notes";
import { crumbOf, inVault, noteUrl, titleOf, vaultOf } from "../api/paths";
import { usePublishEnabled } from "../api/publications";
import { useMarketplaces } from "../api/skills";
import type { MenuItem } from "../components/Menu";
import { useDesktop } from "../hooks/useDesktop";
import { useDialog } from "../hooks/useDialog";
import { editedPath, useEditor, writingSide } from "../state/editor";
import { useSession } from "../state/session";
import { applyScheme } from "../theme/apply";
import { useCloseWindow } from "../windows/context";
import { useWindows } from "../windows/store";
import { filterRows } from "./filter";
import { commandRows, noteActionRows, noteRows, staleNotes, vaultRows, writeRows, type ReadTree, type Row } from "./rows";
import { withMod } from "./usePaletteShortcut";

const keepFocus = (e: SyntheticEvent) => e.preventDefault();

/** How long an action waits for its window to close; past that it is dropped, never popping up at a later close. */
const WINDOW_WAIT = 1000;

/**
 * Runs `then` once the window closing now is gone (the closing navigation goes through the router, not at once), at
 * once if it is gone already; not at all if it stays open past a second (a lock that keeps it).
 */
export function afterWindow(then: () => void) {
  if (!useWindows.getState().open) return then();
  const timer = setTimeout(() => stop(), WINDOW_WAIT);
  const stop = useWindows.subscribe((s) => {
    if (s.open) return;
    stop();
    clearTimeout(timer);
    then();
  });
}
// Structurally shared by the query: the same trees give the same array, so the rows are built again only when one changes.
// A tree whose last read failed counts as not loaded: nothing is taken as gone from it.
const treesOf = (results: UseQueryResult<TreeView>[]) => results.map((r): ReadTree | undefined => (r.isSuccess ? { tree: r.data, at: r.dataUpdatedAt } : undefined));
// Opened again within a minute, the palette takes the trees it read as they are, rather than asking for every vault's again.
const FRESH = 60_000;

/**
 * Every row the person may open: the open note's actions, the commands she has, and the vaults and notes of the vaults
 * the session lists; and the loaded trees by vault, which say which note favourites are gone. Until something is typed
 * only the trees of vaults with a note favourite are read: the empty palette shows no other note of a tree.
 */
function useRows(note: PaletteNote | null, typed: boolean, favourites: Favourite[]): { rows: Row[]; trees: ReadonlyMap<string, ReadTree> } {
  const admin = useSession((s) => s.user?.role) === "admin";
  // As the Skills window decides its tabs.
  const shared = usePublishEnabled() === true;
  const marketplaces = useMarketplaces().data?.enabled === true;
  const vaults = useVaults().data;
  // The note the editor writes, while one is in Write mode.
  const writing = useEditor((s) => (writingSide(s) ? editedPath(s) : null));
  // Each vault's tree as the drawer has it, from the cache where it was read before.
  const trees = useQueries({
    queries: (vaults ?? []).map((v) => ({
      ...treeQuery(v.name),
      staleTime: FRESH,
      enabled: typed || favourites.some((f) => f.kind === "note" && f.vault === v.name),
    })),
    combine: treesOf,
  });
  const all = useMemo(
    () => ({
      rows: [
        ...commandRows({ admin, shared, marketplaces }),
        ...vaultRows(vaults ?? []),
        ...(vaults ?? []).flatMap((v, i) => (trees[i] ? noteRows(v.name, trees[i].tree.root) : [])),
      ],
      trees: new Map((vaults ?? []).flatMap((v, i) => (trees[i] ? [[v.name, trees[i]] as const] : []))),
    }),
    [admin, shared, marketplaces, vaults, trees],
  );
  // Apart from the rest: the shell hands over new items on each of its renders, and the trees needn't be walked again for them.
  return {
    rows: [...(note ? noteActionRows(titleOf(note.path), note.items) : []), ...(writing ? writeRows(titleOf(writing)) : []), ...all.rows],
    trees: all.trees,
  };
}

/** A note opened lately, as its row; one not in its tree (yet) still has one. */
function recentRow(path: string, byKey: Map<string, Row>): Row {
  const favourite: FavouriteInput = { kind: "note", vault: vaultOf(path), path: inVault(path) };
  const key = favouriteKey(favourite);
  return byKey.get(key) ?? { key, group: "Notes", name: titleOf(path), place: crumbOf(path), favourite, run: { to: noteUrl(path) } };
}

type Section = { heading: string; rows: Row[] };

/** The open note and its menu's items (`useNoteActions`), offered as the group Note. */
export type PaletteNote = { path: string; items: MenuItem[] };

/**
 * The command palette: favourites, commands, vaults and notes in one list, a word or two away. On the desktop a panel
 * near the top with the field above the list; on the phone a sheet from below with the field near the thumb. Rendered
 * into the body, so that it is reachable over a window too (the shell under one is inert).
 */
export default function Palette({ note = null }: { note?: PaletteNote | null }) {
  const navigate = useNavigate();
  const closeWindow = useCloseWindow();
  const recentPaths = useEditor((s) => s.recentPaths);
  const close = () => useEditor.getState().setOverlay("none");
  const dialog = useDialog<HTMLDivElement>(close);
  const desktop = useDesktop();
  const id = useId();
  const [query, setQuery] = useState("");
  // By its key, not its place: a star moves a row to the favourites, or off them.
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const favourites = useFavourites();
  const { rows, trees } = useRows(note, query.trim() !== "", favourites.list);
  // Each change lets go of the note favourites whose note is gone, in the same write.
  const toggle = (f: FavouriteInput) => favourites.toggle(f, staleNotes(favourites.list, trees));
  const typed = query.trim();

  const sections = useMemo((): Section[] => {
    const byKey = new Map(rows.map((r) => [r.key, r]));
    if (!typed) {
      // A favourite with no row (a command this version doesn't know, a note not in its tree) isn't shown.
      const starred = favourites.list.flatMap((f) => byKey.get(favouriteKey(f)) ?? []);
      const keys = new Set(starred.map((r) => r.key));
      return [
        { heading: "Favourites", rows: starred },
        { heading: "Recent", rows: recentPaths.map((p) => recentRow(p, byKey)).filter((r) => !keys.has(r.key)) },
      ];
    }
    const keys = new Set(favourites.list.map(favouriteKey));
    const found = filterRows(rows, typed, keys);
    return [
      { heading: "Favourites", rows: found.filter((r) => keys.has(r.key)) },
      ...(["Note", "Commands", "Vaults", "Notes"] as const).map((group) => ({ heading: group, rows: found.filter((r) => r.group === group && !keys.has(r.key)) })),
    ];
  }, [rows, typed, favourites.list, recentPaths]);

  const searchLabel = `Search notes for ‘${typed}’`;
  const entries: (Row | null)[] = [...sections.flatMap((s) => s.rows), ...(typed ? [null] : [])];
  const keyOf = (entry: Row | null) => entry?.key ?? "search";
  // The first row where none is chosen, or the chosen one is gone.
  const at = entries.length ? Math.max(0, entries.findIndex((e) => keyOf(e) === activeKey)) : -1;
  const optionId = (i: number) => `${id}-option-${i}`;

  useEffect(() => {
    if (at >= 0) document.getElementById(`${id}-option-${at}`)?.scrollIntoView?.({ block: "nearest" });
  }, [id, at]);

  const run = (entry: Row | null) => {
    // Search and the actions (the note's, Insert template) are the shell's: under a window they would be out of reach,
    // so the window closes first; an action runs once it is gone, so that what it opens isn't inert and keeps the focus.
    const overWindow = (entry === null || "action" in entry.run) && useWindows.getState().open;
    if (overWindow) closeWindow();
    if (entry === null) {
      close();
      useEditor.getState().setSearchQuery(typed);
      useEditor.getState().setOverlay("search");
    } else if ("action" in entry.run) {
      const action = entry.run.action;
      // Gone, and its focus given back, before the action: what it opens (the find field, a question) keeps the focus.
      flushSync(close);
      if (overWindow) afterWindow(action);
      else action();
    } else {
      close();
      if ("scheme" in entry.run) applyScheme(entry.run.scheme);
      else void navigate(entry.run.to);
    }
  };

  const onKeyDown = (e: KeyboardEvent) => {
    // While an IME composes, Enter and the arrows are the IME's.
    if (e.nativeEvent.isComposing || e.keyCode === 229) return;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      if (entries.length) setActiveKey(keyOf(entries[(at + (e.key === "ArrowDown" ? 1 : entries.length - 1)) % entries.length]));
    } else if (e.key === "Enter") {
      if (at >= 0) run(entries[at]);
    } else if (withMod(e, "d")) {
      // Caught here only, as a browser's bookmark is: the highlighted row's star.
      // The highlight goes with the row, wherever the star moves it.
      const row = entries[at];
      // A note's action has no star.
      if (row?.favourite) {
        setActiveKey(row.key);
        toggle(row.favourite);
      }
    } else return;
    e.preventDefault();
  };

  let index = 0;
  const option = (row: Row | null) => {
    const i = index++;
    const on = !!row?.favourite && favourites.has(row.favourite);
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
        onMouseMove={() => !highlighted && setActiveKey(keyOf(row))}
        onClick={() => run(row)}
        className={`group flex min-h-11 cursor-pointer items-center gap-2 rounded-[8px] pr-1 pl-3 ${highlighted ? "bg-accent/15" : ""}`}
      >
        {row ? (
          <>
            <span className={`flex min-w-0 flex-1 ${desktop ? "items-baseline gap-3" : "flex-col py-1"}`}>
              <span className="min-w-0 truncate text-[15px] text-text">{row.name}</span>
              <span className={`min-w-0 truncate font-mono text-[12px] text-dim ${desktop ? "ml-auto flex-none" : ""}`}>{row.place}</span>
            </span>
            {row.favourite && (
              <button
                type="button"
                tabIndex={-1}
                aria-label={on ? `Remove ${row.name} from favourites` : `Add ${row.name} to favourites`}
                aria-pressed={on}
                onClick={(e) => {
                  e.stopPropagation();
                  toggle(row.favourite!);
                }}
                className={`flex size-11 flex-none items-center justify-center rounded-[10px] ${on ? "text-accent-text" : "text-muted"} ${
                  desktop && !on && !highlighted ? "opacity-0 group-hover:opacity-100" : ""
                }`}
              >
                <Star size={18} strokeWidth={1.8} fill={on ? "currentColor" : "none"} aria-hidden />
              </button>
            )}
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
  // On the list, not its rows: while the trees load, a starred note has no row yet.
  const hint = !typed && favourites.ready && favourites.list.length === 0;
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
    <>
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
          // On the field only: Enter on ✕ is ✕'s.
          onKeyDown={onKeyDown}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setActiveKey(null);
          }}
          placeholder="Commands, vaults, notes"
          autoComplete="off"
          className="min-w-0 flex-1 bg-transparent text-[16px] text-text outline-none placeholder:text-muted"
        />
      </label>
      {/* A star that could not be kept, until the next change. */}
      {favourites.error && (
        <p role="status" className="px-4 pt-1.5 text-sm text-muted">
          {favourites.error}
        </p>
      )}
    </>
  );

  return createPortal(
    <div className={`fixed inset-0 z-[60] flex justify-center ${desktop ? "items-start bg-black/35 pt-[12vh]" : "items-end bg-black/55"}`} onClick={(e) => e.target === e.currentTarget && close()}>
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-label="Favourites and commands"
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

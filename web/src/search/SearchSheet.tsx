// SPDX-License-Identifier: AGPL-3.0-or-later
import { Search } from "lucide-react";
import { useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { crumbOf, noteUrl, titleOf } from "../api/paths";
import { secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";
import ModeChips, { MODES } from "./ModeChips";
import SearchResultCard from "./SearchResultCard";
import { useSearch, type SearchResult } from "./useSearch";

/**
 * The search overlay (MOBILE_EDITOR §4): header and results above, the input and the mode chips at the bottom
 * where the thumb is. An empty query shows the notes opened last.
 */
export default function SearchSheet() {
  const { searchQuery, searchMode, recentPaths, setSearchQuery, setSearchMode, setOverlay } = useEditor();
  const navigate = useNavigate();
  const dialog = useDialog<HTMLDivElement>(() => setOverlay("none"));
  const search = useSearch(searchQuery, searchMode);
  const results = search.data?.results;
  const hint = MODES.find((m) => m.mode === searchMode)!.hint;

  const open = (path: string, line?: number) => {
    useEditor.getState().openNote(path);
    useEditor.getState().setScrollTarget(line === undefined ? null : { path, line });
    navigate(noteUrl(path));
  };
  const openResult = (r: SearchResult) => open(r.path, r.address ? Number(r.startLine) : undefined);

  return (
    <div ref={dialog} role="dialog" aria-modal="true" aria-label="Search" tabIndex={-1} className="k-sheet absolute inset-0 z-20 flex flex-col bg-bg">
      <header className="flex-none border-b border-line px-[22px] pt-5 pb-3">
        <h2 className="text-[17px] font-bold text-text">
          {search.idle ? (
            "Recent notes"
          ) : results ? (
            <>
              {results.length === 1 ? "1 result" : `${results.length} results`} <span className="font-mono text-[12px] font-normal text-accent-text">{searchMode}</span>
            </>
          ) : (
            <>
              Searching <span className="font-mono text-[12px] font-normal text-accent-text">{searchMode}</span>
            </>
          )}
        </h2>
        <p className="text-[13px] text-muted">{hint}</p>
      </header>

      <section aria-label="Results" aria-busy={search.waiting} className="min-h-0 flex-1 overflow-y-auto px-[22px] py-3">
        {search.idle &&
          (recentPaths.length ? (
            <ul aria-label="Recent notes" className="flex flex-col gap-2">
              {recentPaths.map((p) => (
                <li key={p}>
                  <button
                    type="button"
                    onClick={() => open(p)}
                    className="flex min-h-11 w-full flex-col rounded-[12px] border border-line bg-surface px-4 py-2.5 text-left focus-visible:outline-2 focus-visible:outline-accent"
                  >
                    <span className="truncate font-medium text-text">{titleOf(p)}</span>
                    <span className="truncate font-mono text-[12px] text-dim">{crumbOf(p)}</span>
                  </button>
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-text-body">No notes opened yet. Type to search.</p>
          ))}
        {search.error != null && <p role="alert" className="text-text-body">{errorText(search.error)}</p>}
        {results && (
          <div className="flex flex-col gap-3">
            {search.data!.notice && <p role="status" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">{search.data!.notice}</p>}
            {results.length === 0 ? (
              <div className="flex flex-col items-start gap-3">
                <p className="text-text-body">No notes found.</p>
                {searchMode === "exact" && (
                  <button type="button" onClick={() => setSearchMode("meaning")} className={secondaryButtonClass}>
                    Search by meaning instead
                  </button>
                )}
              </div>
            ) : (
              <ul className="flex flex-col gap-2">
                {results.map((r) => (
                  <li key={`${r.path}${r.address}${r.startLine}`}>
                    <SearchResultCard result={r} onOpen={() => openResult(r)} />
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </section>

      <div className="flex flex-none flex-col gap-2.5 border-t border-line px-3 pt-2.5 pb-[max(16px,env(safe-area-inset-bottom))]">
        <label className="flex h-12 items-center gap-2.5 rounded-full border border-line bg-surface pr-4 pl-4 focus-within:outline-2 focus-within:outline-accent">
          <Search size={20} strokeWidth={1.9} aria-hidden className="flex-none text-muted" />
          <input
            type="search"
            aria-label="Search"
            data-autofocus
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            placeholder="Search your notes"
            autoComplete="off"
            className="min-w-0 flex-1 bg-transparent text-[16px] text-text outline-none placeholder:text-muted"
          />
        </label>
        <ModeChips mode={searchMode} onChange={setSearchMode} />
      </div>
    </div>
  );
}

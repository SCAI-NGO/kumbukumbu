// SPDX-License-Identifier: AGPL-3.0-or-later
import { Search, X } from "lucide-react";
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { crumbOf, noteUrl, titleOf } from "../api/paths";
import { secondaryButtonClass } from "../components/form";
import { Sheet, timeText } from "../components/Sheet";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";
import CompareVersions from "../shell/CompareVersions";
import ModeChips, { MODES } from "./ModeChips";
import SearchResultCard from "./SearchResultCard";
import { useEarlierSearch, useSearch, type EarlierResult, type SearchResult } from "./useSearch";

/**
 * The search overlay (MOBILE_EDITOR §4): header and results above, the input and the mode chips at the bottom
 * where the thumb is. An empty query shows the notes opened last. A switch adds sections of earlier versions (off on
 * every visit), each opening a comparison with today's text.
 */
export default function SearchSheet() {
  const { searchQuery, searchMode, recentPaths, setSearchQuery, setSearchMode, setOverlay } = useEditor();
  const navigate = useNavigate();
  const dialog = useDialog<HTMLDivElement>(() => setOverlay("none"));
  const search = useSearch(searchQuery, searchMode);
  const results = search.data?.results;
  const hint = MODES.find((m) => m.mode === searchMode)!.hint;
  const [withEarlier, setWithEarlier] = useState(false);
  const earlier = useEarlierSearch(search.settled, withEarlier);
  const [comparing, setComparing] = useState<EarlierResult | null>(null);

  const open = (path: string, line?: number) => {
    useEditor.getState().openNote(path);
    useEditor.getState().setScrollTarget(line === undefined ? null : { path, line });
    navigate(noteUrl(path));
  };
  const openResult = (r: SearchResult) => open(r.path, r.address ? Number(r.startLine) : undefined);

  return (
    <div ref={dialog} role="dialog" aria-modal="true" aria-label="Search" tabIndex={-1} className="k-sheet absolute inset-0 z-20 flex flex-col bg-bg">
      <header className="flex flex-none items-start gap-2 border-b border-line pt-3 pr-2 pb-3 pl-[22px]">
        <div className="min-w-0 flex-1 pt-2">
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
        </div>
        <button
          type="button"
          aria-label="Close search"
          onClick={() => setOverlay("none")}
          className="flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent"
        >
          <X size={22} strokeWidth={1.9} aria-hidden />
        </button>
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
        {withEarlier && !search.idle && (
          <div className="mt-4 flex flex-col gap-2">
            <h3 className="text-[15px] font-bold text-text">Earlier versions</h3>
            {earlier.isError && <p role="alert" className="text-text-body">{errorText(earlier.error)}</p>}
            {earlier.isPending && <p className="text-sm text-muted">Searching earlier versions …</p>}
            {earlier.data && earlier.data.length === 0 && <p className="text-sm text-text-body">No earlier versions found.</p>}
            {earlier.data && earlier.data.length > 0 && (
              <ul aria-label="Earlier versions" className="flex flex-col gap-2">
                {earlier.data.map((r) => (
                  <li key={`${r.path}${r.version}${r.startLine}`}>
                    <button
                      type="button"
                      onClick={() => setComparing(r)}
                      className="flex min-h-11 w-full flex-col gap-1 rounded-[12px] border border-line bg-surface px-4 py-3 text-left focus-visible:outline-2 focus-visible:outline-accent"
                    >
                      <span className="truncate font-medium text-text">{titleOf(r.path)}</span>
                      <span className="truncate font-mono text-[12px] text-dim">{crumbOf(r.path)}</span>
                      {r.snippet && <span className="line-clamp-2 text-[14px] text-text-body">{r.snippet}</span>}
                      <span className="text-[12px] text-muted">
                        <span>Earlier version · {timeText(r.writtenAt)}</span> · by {r.writer}
                      </span>
                    </button>
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
        <button
          type="button"
          role="switch"
          aria-checked={withEarlier}
          onClick={() => setWithEarlier((on) => !on)}
          className="flex min-h-11 items-center gap-2.5 self-start rounded-[10px] px-1 text-sm text-text-body focus-visible:outline-2 focus-visible:outline-accent"
        >
          <span aria-hidden className={`relative h-6 w-10 flex-none rounded-full transition-colors ${withEarlier ? "bg-accent" : "bg-surface-2 ring-1 ring-line"}`}>
            <span className={`absolute top-1 size-4 rounded-full bg-surface shadow transition-[left] ${withEarlier ? "left-5" : "left-1"}`} />
          </span>
          Include earlier versions
        </button>
      </div>
      {comparing && (
        <Sheet title={comparing.path.slice(comparing.path.lastIndexOf("/") + 1)} onClose={() => setComparing(null)}>
          <p className="text-sm text-muted">
            Earlier version · {timeText(comparing.writtenAt)} · by {comparing.writer}
          </p>
          <CompareVersions path={comparing.path} version={comparing.version} before={comparing.before} trashed={false} beforeLost={comparing.beforeLost} initial="now" versionLabel="This version" />
        </Sheet>
      )}
    </div>
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { crumbOf, titleOf } from "../api/paths";
import type { SearchResult } from "./useSearch";

/** The snippet as plain text: the server sends the matching line with its Markdown syntax. */
const plain = (s: string) =>
  s
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/\[\[([^\]|]*\|)?([^\]]*)\]\]/g, "$2")
    .replace(/^\s*(#{1,6}\s+|>\s*|[-*+]\s+(\[[ xX]\]\s+)?|\d+\.\s+)/, "")
    .replace(/[*_`~]/g, "")
    .trim();

/** The score as a whole percentage, 0 to 100. */
export const percent = (score: SearchResult["score"]) => Math.round(Math.min(1, Math.max(0, Number(score) || 0)) * 100);

export default function SearchResultCard({ result, onOpen }: { result: SearchResult; onOpen(): void }) {
  const pct = percent(result.score);
  return (
    <button
      type="button"
      onClick={onOpen}
      className="flex min-h-11 w-full flex-col gap-1.5 rounded-[12px] border border-line bg-surface px-4 py-3 text-left focus-visible:outline-2 focus-visible:outline-accent"
    >
      <span className="truncate font-medium text-text">{titleOf(result.path)}</span>
      <span className="truncate font-mono text-[12px] text-dim">{crumbOf(result.path)}</span>
      {result.snippet && <span className="line-clamp-2 text-[14px] text-text-body">{plain(result.snippet)}</span>}
      <span className="flex items-center gap-2.5">
        <span
          role="meter"
          aria-label="Score"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={pct}
          className="h-1 min-w-0 flex-1 overflow-hidden rounded-full bg-surface-2"
        >
          <span className="block h-full rounded-full bg-accent" style={{ width: `${pct}%` }} />
        </span>
        <span className="flex-none font-mono text-[12px] text-muted">{pct}%</span>
        <span className="flex-none rounded-full bg-surface-2 px-2 py-0.5 font-mono text-[11px] text-accent-text">{result.reason}</span>
      </span>
    </button>
  );
}

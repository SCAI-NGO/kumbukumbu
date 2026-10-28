// SPDX-License-Identifier: AGPL-3.0-or-later
import { X } from "lucide-react";
import { useState } from "react";
import { errorText } from "../api/error-text";
import { crumbOf, titleOf } from "../api/paths";
import { useTagNotes, useTags, type TagLine } from "../api/tags";

const SHOWN = 30;

/** The search's tag filter as a chip above the results; × takes it away. */
export function TagFilterChip({ tag, onClear }: { tag: string; onClear(): void }) {
  return (
    <div className="mb-3 flex">
      <button
        type="button"
        aria-label={`Remove the tag filter #${tag}`}
        onClick={onClear}
        className="flex min-h-11 max-w-full items-center gap-1.5 rounded-full border border-accent bg-surface-2 pr-3 pl-3.5 font-mono text-[14px] text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        <span className="truncate">#{tag}</span>
        <X size={16} strokeWidth={2} aria-hidden className="flex-none" />
      </button>
    </div>
  );
}

/**
 * The tags of the user's vaults with their counts, most notes first; with `prefix` (a `#res` being typed) only those
 * that start with it. Tapping one sets the search's tag filter.
 */
export function TagList({ prefix = "", onPick }: { prefix?: string; onPick(tag: string): void }) {
  const tags = useTags();
  const [all, setAll] = useState(false);
  if (!tags.data) return null;
  const lower = prefix.toLowerCase();
  // The tag typed in full comes first, then those nested below it or longer.
  const matching = tags.data
    .filter((t) => t.tag.toLowerCase().startsWith(lower))
    .sort((a, b) => Number(b.tag.toLowerCase() === lower) - Number(a.tag.toLowerCase() === lower));
  if (matching.length === 0) return prefix ? <p className="text-sm text-text-body">No tag starts with #{prefix}.</p> : null;
  const shown = all ? matching : matching.slice(0, SHOWN);
  return (
    <section aria-label="Tags" className="mt-5 flex flex-col gap-2">
      <h3 className="text-[15px] font-bold text-text">Tags</h3>
      <ul className="flex flex-wrap gap-2">
        {shown.map((t) => (
          <li key={t.tag}>
            <button
              type="button"
              onClick={() => onPick(t.tag)}
              className="flex min-h-11 items-center gap-1.5 rounded-full border border-line bg-surface px-3.5 font-mono text-[14px] text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
            >
              #{t.tag}
              <span className="text-[12px] text-muted">{t.count}</span>
            </button>
          </li>
        ))}
      </ul>
      {matching.length > shown.length && (
        <button type="button" onClick={() => setAll(true)} className="min-h-11 self-start rounded-[10px] px-1 text-sm text-accent-text focus-visible:outline-2 focus-visible:outline-accent">
          All {matching.length} tags
        </button>
      )}
    </section>
  );
}

/** The notes that carry a tag, each with the first line where it stands; tapping one opens it there. */
export function TaggedNotes({ tag, onOpen }: { tag: string; onOpen(path: string, line: number): void }) {
  const lines = useTagNotes(tag);
  if (lines.isError) return <p role="alert" className="text-text-body">{errorText(lines.error)}</p>;
  if (!lines.data) return <p className="text-sm text-muted">Looking for #{tag} …</p>;
  const notes = new Map<string, TagLine>();
  for (const l of lines.data) if (!notes.has(l.path)) notes.set(l.path, l);
  return (
    <ul aria-label={`Notes tagged #${tag}`} className="flex flex-col gap-2">
      {[...notes.values()].map((l) => (
        <li key={l.path}>
          <button
            type="button"
            onClick={() => onOpen(l.path, Number(l.line))}
            className="flex min-h-11 w-full flex-col gap-1 rounded-[12px] border border-line bg-surface px-4 py-3 text-left focus-visible:outline-2 focus-visible:outline-accent"
          >
            <span className="truncate font-medium text-text">{titleOf(l.path)}</span>
            <span className="truncate font-mono text-[12px] text-dim">{crumbOf(l.path)}</span>
            <span className="line-clamp-2 text-[14px] text-text-body">{l.text.trim()}</span>
          </button>
        </li>
      ))}
    </ul>
  );
}

/** How many notes a tag listing holds, for the sheet's header; null until it is in. */
export function useTaggedCount(tag: string | null) {
  const lines = useTagNotes(tag);
  return lines.data ? new Set(lines.data.map((l) => l.path)).size : null;
}

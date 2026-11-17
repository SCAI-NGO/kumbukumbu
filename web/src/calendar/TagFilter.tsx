// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useInWindow } from "../windows/context";

/**
 * The tag filter's tag, without its #, and its setter. In the address (`?tag=kumbu`), so a filtered week can be kept
 * and shared; with `inAddress` false on the page only, as the browser's history can't be cleaned. A tag already in the
 * address shows until another is chosen; taking it away takes it from the address too. `initial` is the page's own
 * tag to start with (one a note's way back brought along).
 */
export function useTagFilter(inAddress = true, initial?: string): [string | undefined, (next: string | null) => void] {
  const [params, setParams] = useSearchParams();
  const address = params.get("tag")?.trim() || undefined;
  // Inside a window every move replaces its one history entry.
  const inWindow = useInWindow();
  const [kept, setKept] = useState<string | undefined>(inAddress ? undefined : initial);
  const toAddress = (next: string | null, replace = inWindow) =>
    setParams(
      (current) => {
        const changed = new URLSearchParams(current);
        if (next) changed.set("tag", next);
        else changed.delete("tag");
        return changed;
      },
      { replace },
    );
  if (inAddress) return [address, (next) => toAddress(next)];
  return [
    kept ?? address,
    (next) => {
      setKept(next ?? undefined);
      if (!next && address) toAddress(null, true);
    },
  ];
}

/** `url` with the tag filter kept, so moving to another week or month stays on the tag. */
export const withTag = (url: string, tag?: string) => (tag ? `${url}${url.includes("?") ? "&" : "?"}tag=${encodeURIComponent(tag)}` : url);

/**
 * The tag filter of the calendar and What's on (spec §7.10): the notes with a tag, or one nested below it, kept by
 * `useTagFilter`; set, it shows as the board's does, `#tag ×`, which takes it away again.
 */
export default function TagFilter({ tag, choose }: { tag: string | undefined; choose(next: string | null): void }) {
  const [typed, setTyped] = useState("");

  if (tag)
    return (
      <button
        type="button"
        onClick={() => choose(null)}
        className="flex min-h-12 items-center gap-1 rounded-full border border-line px-3 font-mono text-sm text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        #{tag} <span aria-hidden>×</span>
        <span className="sr-only"> (show everything)</span>
      </button>
    );

  return (
    <form
      role="search"
      aria-label="Tag filter"
      onSubmit={(e) => {
        e.preventDefault();
        const next = typed.trim().replace(/^#+/, "");
        if (!next) return;
        setTyped("");
        choose(next);
      }}
      className="flex items-center overflow-hidden rounded-[10px] border border-line focus-within:outline-2 focus-within:outline-accent"
    >
      <input
        type="text"
        aria-label="Tag"
        placeholder="#tag"
        autoCapitalize="none"
        autoCorrect="off"
        spellCheck={false}
        value={typed}
        onChange={(e) => setTyped(e.target.value)}
        className="min-h-12 w-24 min-w-0 bg-transparent px-3 font-mono text-sm text-text placeholder:text-dim focus:outline-none"
      />
      <button type="submit" className="min-h-12 px-3 text-sm font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent">
        Filter
      </button>
    </form>
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { useSearchParams } from "react-router-dom";

/** The tag in the address, `?tag=kumbu`, without its #; none when absent or empty. */
export function useTag(): string | undefined {
  const [params] = useSearchParams();
  return params.get("tag")?.trim() || undefined;
}

/** `url` with the tag filter kept, so moving to another week or month stays on the tag. */
export const withTag = (url: string, tag?: string) => (tag ? `${url}${url.includes("?") ? "&" : "?"}tag=${encodeURIComponent(tag)}` : url);

/**
 * The tag filter of the calendar and What's on (spec §7.10): the notes with a tag, or one nested below it. It lives in
 * the address (`?tag=`), so a filtered week can be kept and shared; set, it shows as the board's does, `#tag ×`, which
 * takes it away again.
 */
export default function TagFilter() {
  const [, setParams] = useSearchParams();
  const tag = useTag();
  const [typed, setTyped] = useState("");

  const choose = (next: string | null) =>
    setParams((current) => {
      const changed = new URLSearchParams(current);
      if (next) changed.set("tag", next);
      else changed.delete("tag");
      return changed;
    });

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

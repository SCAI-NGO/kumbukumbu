// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEditor } from "../state/editor";

const chip = "inline-flex max-w-full items-center rounded-full bg-surface-2 px-2 align-baseline font-mono text-[0.85em] text-accent-text";

/**
 * A tag as a chip: tapping it opens the search sheet on the notes that carry it. `still` (a checkpoint, which shows
 * the past) only names it.
 */
export default function TagChip({ tag, still = false }: { tag: string; still?: boolean }) {
  if (still) return <span className={chip}>#{tag}</span>;
  return (
    <button
      type="button"
      aria-label={`Notes tagged #${tag}`}
      onClick={() => useEditor.getState().searchByTag(tag)}
      className={`${chip} focus-visible:outline-2 focus-visible:outline-accent`}
    >
      #{tag}
    </button>
  );
}

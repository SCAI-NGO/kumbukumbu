// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useMemo, useState, type KeyboardEvent, type RefObject, type SyntheticEvent } from "react";
import { useTree } from "../api/notes";
import { crumbOf, titleOf, vaultOf } from "../api/paths";
import { completeLink, linkQuery, linkTarget, notePaths, rankNotes, type LinkQuery } from "./linkCompletion";
import { apply } from "./textEdit";

const keepFocus = (e: SyntheticEvent) => e.preventDefault();

/**
 * `[[` completion for the editor: follows the caret, and answers the list to show (between the textarea and the
 * toolbar, so above the on-screen keyboard) and the keys the textarea hands it first.
 */
export function useLinkCompletion(path: string, textarea: RefObject<HTMLTextAreaElement>, onChange: (value: string) => void) {
  const [at, setAt] = useState<LinkQuery | null>(null);
  // Asked for (or taken from the cache) only once a `[[` is typed.
  const tree = useTree(at ? vaultOf(path) : null);
  const all = useMemo(() => (tree.data ? notePaths(tree.data.root) : []), [tree.data]);
  const [active, setActive] = useState(0);
  // Escape closes the list for this `[[`; a new one opens it again.
  const [dismissed, setDismissed] = useState<number | null>(null);

  const options = useMemo(() => (at ? rankNotes(all.filter((p) => p !== path), at.query) : []), [all, at, path]);
  const open = !!at && at.start !== dismissed && options.length > 0;

  /** Called on every change of text or caret. */
  const follow = useCallback(() => {
    const area = textarea.current;
    if (!area) return;
    const next = area.selectionStart === area.selectionEnd ? linkQuery(area.value, area.selectionStart) : null;
    setAt((prev) => (prev?.start === next?.start && prev?.query === next?.query ? prev : next));
    setActive(0);
  }, [textarea]);

  const pick = (option: string) => {
    const area = textarea.current;
    if (!area || !at) return;
    apply(area, completeLink(area.value, area.selectionStart, at, linkTarget(option, all)), onChange);
    setAt(null);
  };

  /** True when the key was the list's. */
  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (!open) return false;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") setActive((i) => (i + (e.key === "ArrowDown" ? 1 : options.length - 1)) % options.length);
    else if (e.key === "Enter" || e.key === "Tab") pick(options[active]);
    else if (e.key === "Escape") setDismissed(at.start);
    else return false;
    e.preventDefault();
    return true;
  };

  const list = open ? (
    <ul
      id="link-completion"
      role="listbox"
      aria-label="Notes to link"
      className="mx-2 mb-1.5 max-h-[40vh] flex-none overflow-y-auto rounded-[12px] border border-line bg-surface-2 p-1 shadow-lg"
    >
      {options.map((option, i) => (
        <li
          key={option}
          id={`link-option-${i}`}
          role="option"
          aria-selected={i === active}
          onPointerDown={keepFocus}
          onMouseDown={keepFocus}
          onClick={() => pick(option)}
          className={`flex min-h-11 cursor-pointer flex-col justify-center rounded-[8px] px-3 py-1 ${i === active ? "bg-accent/15" : ""}`}
        >
          <span className="truncate text-[15px] text-text">{titleOf(option)}</span>
          <span className="truncate font-mono text-[11px] text-dim">{crumbOf(option)}</span>
        </li>
      ))}
    </ul>
  ) : null;

  const textareaProps = {
    "aria-autocomplete": "list" as const,
    "aria-controls": open ? "link-completion" : undefined,
    "aria-activedescendant": open ? `link-option-${active}` : undefined,
  };

  return { list, follow, onKeyDown, textareaProps };
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useMemo, useState, type KeyboardEvent, type RefObject, type SyntheticEvent } from "react";
import { useNote, useTree } from "../api/notes";
import { crumbOf, titleOf, vaultOf } from "../api/paths";
import {
  blocksFor,
  completeLink,
  completeSubpath,
  embedsAsImage,
  headingLink,
  headingsFor,
  linkedNote,
  linkQuery,
  linkTarget,
  notePaths,
  rankNotes,
  type LinkQuery,
} from "./linkCompletion";
import { apply } from "./textEdit";

const keepFocus = (e: SyntheticEvent) => e.preventDefault();

/** A row of the list: what it shows, and what picking it inserts (a note's path, a heading's link text, `^id`). */
type Option = { key: string; label: string; detail: string; value: string };

const listNames: Record<LinkQuery["kind"], string> = {
  note: "Notes to link",
  markdown: "Notes to link",
  heading: "Headings to link",
  markdownHeading: "Headings to link",
  block: "Blocks to link",
};

/**
 * Link completion for the editor (a note's name, its headings and blocks, a Markdown link's path): follows the caret,
 * and answers the list to show (between the textarea and the toolbar, so above the on-screen keyboard) and the keys
 * the textarea hands it first.
 */
export function useLinkCompletion(path: string, textarea: RefObject<HTMLTextAreaElement | null>, onChange: (value: string) => void) {
  const [at, setAt] = useState<LinkQuery | null>(null);
  // Asked for (or taken from the cache) only once a `[[` is typed.
  const tree = useTree(at ? vaultOf(path) : null);
  const all = useMemo(() => (tree.data ? notePaths(tree.data.root) : []), [tree.data]);
  // The open note's text as it is written, for its own headings and blocks (`[[#`): taken as the caret moves.
  const [own, setOwn] = useState("");
  const [active, setActive] = useState(0);
  // Escape closes the list for this `[[`; a new one opens it again.
  const [dismissed, setDismissed] = useState<number | null>(null);

  // The note whose headings or blocks are asked for: the open one for `[[#`, else the one the name leads to.
  const target = useMemo(() => {
    if (!at || at.kind === "note" || at.kind === "markdown") return null;
    if (at.kind === "markdownHeading") return null;
    return at.note === "" ? path : linkedNote(at.note, all, path);
  }, [all, at, path]);
  // Another note is read as the note view reads it, so it comes from the cache when it was open before.
  const other = useNote(target !== path ? target : null);
  const content = target === path ? own : other.data?.content;
  const loading = target !== null && target !== path && other.isPending;

  const options = useMemo((): Option[] => {
    if (at?.kind === "note") {
      return rankNotes(all.filter((p) => p !== path), at.query).map((p) => ({ key: p, label: titleOf(p), detail: crumbOf(p), value: p }));
    }
    if (!at || content === undefined) return [];
    if (at.kind === "block") return blocksFor(content, at.query).map((b) => ({ key: b.id, label: b.text, detail: `^${b.id}`, value: `^${b.id}` }));
    if (at.kind === "heading") {
      return headingsFor(content, at.query)
        .map((h) => ({ key: String(h.line), label: h.text, detail: `H${h.level}`, value: headingLink(h.text) }))
        .filter((o) => o.value !== "");
    }
    return [];
  }, [all, at, content, path]);
  const open = !!at && at.start !== dismissed && options.length > 0;

  /** Called on every change of text or caret. */
  const follow = useCallback(() => {
    const area = textarea.current;
    if (!area) return;
    const next = area.selectionStart === area.selectionEnd ? linkQuery(area.value, area.selectionStart) : null;
    setAt((prev) => (JSON.stringify(prev) === JSON.stringify(next) ? prev : next));
    if (next && "note" in next) setOwn(area.value);
    setActive(0);
  }, [textarea]);

  const pick = (option: Option) => {
    const area = textarea.current;
    if (!area || !at) return;
    const caret = area.selectionStart;
    if (at.kind === "note") apply(area, completeLink(area.value, caret, at, linkTarget(option.value, all), embedsAsImage(option.value)), onChange);
    else if (at.kind === "heading" || at.kind === "block") apply(area, completeSubpath(area.value, caret, at, option.value), onChange);
    setAt(null);
  };

  /** True when the key was the list's. */
  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    // While an IME composes (Japanese, Chinese, many phone keyboards), Enter and the arrows are the IME's.
    if (!open || e.nativeEvent.isComposing || e.keyCode === 229) return false;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") setActive((i) => (i + (e.key === "ArrowDown" ? 1 : options.length - 1)) % options.length);
    else if (e.key === "Enter" || e.key === "Tab") pick(options[active]);
    else if (e.key === "Escape") setDismissed(at.start);
    else return false;
    e.preventDefault();
    return true;
  };

  const box = "mx-2 mb-1.5 max-h-[40vh] flex-none overflow-y-auto rounded-[12px] border border-line bg-surface-2 p-1 shadow-lg";
  const list = open ? (
    <ul id="link-completion" role="listbox" aria-label={listNames[at.kind]} className={box}>
      {options.map((option, i) => (
        <li
          key={option.key}
          id={`link-option-${i}`}
          role="option"
          aria-selected={i === active}
          onPointerDown={keepFocus}
          onMouseDown={keepFocus}
          onClick={() => pick(option)}
          className={`flex min-h-11 cursor-pointer flex-col justify-center rounded-[8px] px-3 py-1 ${i === active ? "bg-accent/15" : ""}`}
        >
          <span className="truncate text-[15px] text-text">{option.label}</span>
          <span className="truncate font-mono text-[11px] text-dim">{option.detail}</span>
        </li>
      ))}
    </ul>
  ) : loading && at?.start !== dismissed ? (
    <p role="status" className={`${box} px-3 py-2 text-[15px] text-muted`}>
      Loading…
    </p>
  ) : null;

  const textareaProps = {
    "aria-autocomplete": "list" as const,
    "aria-controls": open ? "link-completion" : undefined,
    "aria-activedescendant": open ? `link-option-${active}` : undefined,
  };

  return { list, follow, onKeyDown, textareaProps };
}

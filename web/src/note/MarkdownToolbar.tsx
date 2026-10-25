// SPDX-License-Identifier: AGPL-3.0-or-later
import type { RefObject, SyntheticEvent } from "react";
import { useVisualViewport } from "../hooks/useVisualViewport";
import { apply, type Edit } from "./textEdit";

type Tool = { label: string; name: string; edit(value: string, start: number, end: number): Edit };

/** Wraps the selection in `open` … `close` (keeping it selected), or puts the pair at the caret with the caret inside. */
const wrap =
  (open: string, close = open) =>
  (value: string, start: number, end: number): Edit => ({
    start,
    end,
    text: `${open}${value.slice(start, end)}${close}`,
    selStart: start + open.length,
    selEnd: end + open.length,
  });

/** Puts `prefix` in front of every line the selection touches (the caret's line without one). */
const prefix =
  (mark: string) =>
  (value: string, start: number, end: number): Edit => {
    const from = value.lastIndexOf("\n", start - 1) + 1;
    const next = value.indexOf("\n", end);
    const to = next < 0 ? value.length : next;
    const lines = value.slice(from, to).split("\n");
    return {
      start: from,
      end: to,
      text: lines.map((l) => mark + l).join("\n"),
      selStart: start + mark.length,
      selEnd: end + mark.length * lines.length,
    };
  };

const tools: Tool[] = [
  { label: "[[ ]]", name: "Wiki link", edit: wrap("[[", "]]") },
  { label: "##", name: "Heading", edit: prefix("## ") },
  { label: "- [ ]", name: "Task", edit: prefix("- [ ] ") },
  { label: "•", name: "List item", edit: prefix("- ") },
  { label: "B", name: "Bold", edit: wrap("**") },
  { label: "`", name: "Inline code", edit: wrap("`") },
  { label: ">", name: "Quote", edit: prefix("> ") },
];

/** Keeps the focus (and the on-screen keyboard) in the textarea when a button is pressed. */
const keepFocus = (e: SyntheticEvent) => e.preventDefault();

/** Write mode's bottom bar (MOBILE_EDITOR §4): insert or wrap at the caret, and Done. It sits above the keyboard. */
export default function MarkdownToolbar({
  textarea,
  onChange,
  onDone,
}: {
  textarea: RefObject<HTMLTextAreaElement>;
  onChange(value: string): void;
  onDone(): void;
}) {
  const keyboard = useVisualViewport();
  const run = (tool: Tool) => {
    const area = textarea.current;
    if (!area) return;
    apply(area, tool.edit(area.value, area.selectionStart, area.selectionEnd), onChange);
  };

  return (
    <div
      role="toolbar"
      aria-label="Markdown"
      style={keyboard > 0 ? { paddingBottom: keyboard + 8 } : undefined}
      className="flex flex-none items-center gap-1.5 border-t border-line bg-surface px-2 pt-2 pb-[max(18px,env(safe-area-inset-bottom))]"
    >
      <div className="flex min-w-0 flex-1 gap-1 overflow-x-auto">
        {tools.map((tool) => (
          <button
            key={tool.name}
            type="button"
            aria-label={tool.name}
            onPointerDown={keepFocus}
            onMouseDown={keepFocus}
            onClick={() => run(tool)}
            className="h-11 min-w-11 flex-none rounded-[10px] bg-surface-2 px-2.5 font-mono text-sm text-text focus-visible:outline-2 focus-visible:outline-accent"
          >
            {tool.label}
          </button>
        ))}
      </div>
      <button
        type="button"
        onPointerDown={keepFocus}
        onMouseDown={keepFocus}
        onClick={onDone}
        className="h-11 flex-none rounded-[10px] bg-accent px-4 text-sm font-semibold text-on-accent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
      >
        Done
      </button>
    </div>
  );
}

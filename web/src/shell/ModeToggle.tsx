// SPDX-License-Identifier: AGPL-3.0-or-later
import { useRef } from "react";
import { useEditor, type Mode } from "../state/editor";

const modes: { mode: Mode; label: string }[] = [
  { mode: "read", label: "Read" },
  { mode: "write", label: "Write" },
];

/**
 * Segmented Read / Write; Read ends Write mode the way Done does. Write is there only for a note the caller may change;
 * `refusal` is the server's reason why not (collab design §9.3).
 */
export default function ModeToggle({ writable, refusal }: { writable: boolean; refusal?: string | null }) {
  const current = useEditor((s) => s.mode);
  const setMode = useEditor((s) => s.setMode);
  const endWrite = useEditor((s) => s.endWrite);
  const standIn = useRef<HTMLInputElement>(null);
  const write = () => {
    // iOS raises the keyboard only for a focus inside the tap; the editor may still be loading then. This field takes
    // the focus at once, and the editor's textarea takes it over as it mounts.
    standIn.current?.focus({ preventScroll: true });
    setMode("write");
  };
  return (
    <div role="group" aria-label="Editor mode" className="relative flex flex-none gap-0.5 rounded-[12px] border border-line bg-surface p-[3px]">
      <input
        ref={standIn}
        aria-label="Markdown source, opening"
        tabIndex={-1}
        data-stand-in
        className="pointer-events-none absolute size-px text-[16px] opacity-0"
      />
      {modes.map(({ mode, label }) => (
        <button
          key={mode}
          type="button"
          aria-pressed={current === mode}
          disabled={mode === "write" && !writable}
          title={mode === "write" && !writable ? (refusal ?? "You can only read this note.") : undefined}
          onClick={() => (mode === "read" ? void endWrite() : current !== "write" && write())}
          className={`min-h-11 rounded-[9px] px-3.5 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${
            current === mode ? "bg-accent text-on-accent" : "text-muted"
          } disabled:opacity-50`}
        >
          {label}
        </button>
      ))}
    </div>
  );
}

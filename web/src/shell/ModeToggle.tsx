// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEditor, type Mode } from "../state/editor";

const modes: { mode: Mode; label: string }[] = [
  { mode: "read", label: "Read" },
  { mode: "write", label: "Write" },
];

const standInId = "write-stand-in";

/**
 * Write mode, as the top bar's Write starts it; for a caller that may offer it only where that Write is on. iOS raises
 * the keyboard only for a focus inside the tap, and the editor may still be loading then: the top bar's stand-in field
 * takes the focus at once, and the editor's textarea takes it over as it mounts.
 */
export function startWrite() {
  document.getElementById(standInId)?.focus({ preventScroll: true });
  useEditor.getState().setMode("write");
}

/**
 * Segmented Read / Write; Read ends Write mode the way Done does. Write is there only for a note the caller may change;
 * `refusal` is the server's reason why not (collab design §9.3).
 */
export default function ModeToggle({ writable, refusal }: { writable: boolean; refusal?: string | null }) {
  const current = useEditor((s) => s.mode);
  const endWrite = useEditor((s) => s.endWrite);
  return (
    <div role="group" aria-label="Editor mode" className="relative flex flex-none gap-0.5 rounded-[12px] border border-line bg-surface p-[3px]">
      <input
        id={standInId}
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
          onClick={() => (mode === "read" ? void endWrite() : current !== "write" && startWrite())}
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

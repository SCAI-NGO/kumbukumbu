// SPDX-License-Identifier: AGPL-3.0-or-later
import { BookOpen, Pencil, type LucideIcon } from "lucide-react";
import { useEditor, type Mode } from "../state/editor";

// An icon below sm, so that a phone's top bar keeps room for the note's title beside Outline and Graph.
const modes: { mode: Mode; label: string; icon: LucideIcon }[] = [
  { mode: "read", label: "Read", icon: BookOpen },
  { mode: "write", label: "Write", icon: Pencil },
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
      {modes.map(({ mode, label, icon: Icon }) => (
        <button
          key={mode}
          type="button"
          aria-label={label}
          aria-pressed={current === mode}
          disabled={mode === "write" && !writable}
          title={mode === "write" && !writable ? (refusal ?? "You can only read this note.") : undefined}
          // Each on the focused pane only: its Read ends Write mode there, never in the other pane.
          onClick={() => (mode === "read" ? current === "write" && void endWrite() : current !== "write" && startWrite())}
          className={`flex min-h-11 items-center rounded-[9px] px-2.5 text-sm sm:px-3.5 font-medium focus-visible:outline-2 focus-visible:outline-accent ${
            current === mode ? "bg-accent text-on-accent" : "text-muted"
          } disabled:opacity-50`}
        >
          <Icon size={18} strokeWidth={1.8} aria-hidden className="sm:hidden" />
          <span className="hidden sm:inline">{label}</span>
        </button>
      ))}
    </div>
  );
}

// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEditor, type Mode } from "../state/editor";

const modes: { mode: Mode; label: string }[] = [
  { mode: "read", label: "Read" },
  { mode: "write", label: "Write" },
];

/** Segmented Read / Write; Read ends Write mode the way Done does. */
export default function ModeToggle() {
  const current = useEditor((s) => s.mode);
  const setMode = useEditor((s) => s.setMode);
  const endWrite = useEditor((s) => s.endWrite);
  return (
    <div role="group" aria-label="Editor mode" className="flex flex-none gap-0.5 rounded-[12px] border border-line bg-surface p-[3px]">
      {modes.map(({ mode, label }) => (
        <button
          key={mode}
          type="button"
          aria-pressed={current === mode}
          onClick={() => (mode === "read" ? void endWrite() : setMode(mode))}
          className={`min-h-11 rounded-[9px] px-3.5 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${
            current === mode ? "bg-accent text-on-accent" : "text-muted"
          }`}
        >
          {label}
        </button>
      ))}
    </div>
  );
}

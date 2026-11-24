// SPDX-License-Identifier: AGPL-3.0-or-later
import { NEEDS_CONNECTION, useOffline } from "../offline/mode";
import type { SearchMode } from "../state/editor";

export const MODES: { mode: SearchMode; label: string; hint: string }[] = [
  { mode: "hybrid", label: "Hybrid", hint: "Words and meaning together." },
  { mode: "meaning", label: "Meaning", hint: "Notes about the idea, even in other words." },
  { mode: "exact", label: "Exact", hint: "Only notes that contain exactly this." },
];

export default function ModeChips({ mode, onChange }: { mode: SearchMode; onChange(mode: SearchMode): void }) {
  // Meaning is the server's: offline its chips stay, dimmed, and the chosen mode is kept for when it's back (§3a).
  const offline = useOffline();
  return (
    <div role="group" aria-label="Search mode" className="flex gap-2">
      {MODES.map((m) => (
        <button
          key={m.mode}
          type="button"
          aria-pressed={mode === m.mode}
          disabled={offline && m.mode !== "exact"}
          title={offline && m.mode !== "exact" ? NEEDS_CONNECTION : undefined}
          onClick={() => onChange(m.mode)}
          className={`min-h-11 flex-1 rounded-full border px-4 text-[14px] focus-visible:outline-2 focus-visible:outline-accent ${
            mode === m.mode ? "border-accent bg-accent text-on-accent" : "border-line bg-surface text-text-body"
          } disabled:opacity-40`}
        >
          {m.label}
        </button>
      ))}
    </div>
  );
}

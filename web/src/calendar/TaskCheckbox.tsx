// SPDX-License-Identifier: AGPL-3.0-or-later
import { Check } from "lucide-react";

/**
 * A to-do's box: ☐ To do ticks it closed, ☑ Done unticks it, the word beside it its label. In a list, where every box
 * would be called the same, the entry's title is said too ("To do: Certificate"). A target of 48 px.
 */
export default function TaskCheckbox({ closed, title, disabled, onChange }: { closed: boolean; title?: string; disabled?: boolean; onChange(): void }) {
  return (
    <button
      type="button"
      role="checkbox"
      aria-checked={closed}
      disabled={disabled}
      onClick={onChange}
      className="inline-flex min-h-12 min-w-12 flex-none items-center gap-2 rounded-[10px] px-1 font-medium text-text disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent"
    >
      <span aria-hidden className={`flex size-5 flex-none items-center justify-center rounded-[5px] border-2 ${closed ? "border-accent bg-accent text-on-accent" : "border-current text-muted"}`}>
        {closed && <Check size={14} strokeWidth={3} />}
      </span>
      <span>{closed ? "Done" : "To do"}</span>
      {title && <span className="sr-only">: {title}</span>}
    </button>
  );
}

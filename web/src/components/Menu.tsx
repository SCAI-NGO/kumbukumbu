// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState, type ReactNode } from "react";
import type { LucideIcon } from "lucide-react";

/** One entry of a small menu; later steps add theirs to the same list (Upload, for one). */
export interface MenuItem {
  label: string;
  icon: LucideIcon;
  onSelect(): void;
  disabled?: boolean;
}

/** A button that opens a short list of actions; a tap outside or Escape closes it. */
export function Menu(props: { label: string; items: MenuItem[]; placement: "above" | "below"; className: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div
      className="relative flex-none"
      onKeyDown={(e) => {
        // Escape closes the menu only, not the drawer around it.
        if (e.key !== "Escape" || !open) return;
        e.stopPropagation();
        setOpen(false);
      }}
    >
      <button
        type="button"
        aria-label={props.label}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        className={props.className}
      >
        {props.children}
      </button>
      {open && (
        <>
          <div className="fixed inset-0 z-30" aria-hidden onClick={() => setOpen(false)} />
          <div
            role="menu"
            aria-label={props.label}
            className={`absolute right-0 z-40 min-w-48 rounded-[12px] border border-line bg-surface-2 p-1 shadow-lg ${
              props.placement === "above" ? "bottom-full mb-2" : "top-full mt-1"
            }`}
          >
            {props.items.map(({ label, icon: Icon, onSelect, disabled }) => (
              <button
                key={label}
                type="button"
                role="menuitem"
                disabled={disabled}
                onClick={() => {
                  setOpen(false);
                  onSelect();
                }}
                className="flex min-h-11 w-full items-center gap-2.5 rounded-[8px] px-3 text-left text-[15px] text-text hover:bg-surface disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent"
              >
                <Icon size={18} strokeWidth={1.8} aria-hidden className="text-muted" />
                {label}
              </button>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

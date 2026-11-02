// SPDX-License-Identifier: AGPL-3.0-or-later
import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import type { LucideIcon } from "lucide-react";

/** One entry of a small menu; later steps add theirs to the same list (Upload, for one). */
export interface MenuItem {
  label: string;
  icon: LucideIcon;
  onSelect(): void;
  disabled?: boolean;
}

/**
 * A button that opens a short list of actions; a tap outside or Escape closes it. The list is drawn on `document.body`
 * at the button's position, so no scrolling or clipping container around the button can cut it off; it turns upward
 * where there is no room below. Scrolling closes it, a resize repositions it.
 */
export function Menu(props: { label: string; items: MenuItem[]; placement: "above" | "below"; className: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const [at, setAt] = useState<{ top: number; right: number } | null>(null);
  const button = useRef<HTMLButtonElement>(null);
  const list = useRef<HTMLDivElement>(null);

  const place = () => {
    if (!button.current || !list.current) return;
    const anchor = button.current.getBoundingClientRect();
    const height = list.current.getBoundingClientRect().height;
    const below = anchor.bottom + 4;
    const above = anchor.top - 8 - height;
    const down = props.placement === "below" ? below + height <= window.innerHeight || above < 0 : above < 0;
    setAt({ top: down ? below : above, right: window.innerWidth - anchor.right });
  };

  useLayoutEffect(() => {
    if (!open) return setAt(null);
    place();
    const onScroll = (e: Event) => {
      if (!list.current?.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener("resize", place);
    window.addEventListener("scroll", onScroll, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", onScroll, true);
    };
  }, [open]);

  // The list is outside the dialog or drawer the button may sit in, so Tab is handled here: it moves between the items
  // and, past either end, hands the focus back to the button for the dialog's own trap to carry on from.
  const itemButtons = () => [...(list.current?.querySelectorAll<HTMLButtonElement>("button:not([disabled])") ?? [])];
  const onItemKey = (e: React.KeyboardEvent) => {
    const isTab = e.key === "Tab";
    if (!isTab && e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
    const buttons = itemButtons();
    const step = e.key === "ArrowUp" || (isTab && e.shiftKey) ? -1 : 1;
    const next = buttons[buttons.indexOf(document.activeElement as HTMLButtonElement) + step];
    if (next) {
      e.preventDefault();
      e.stopPropagation();
      next.focus();
    } else if (isTab) {
      if (e.shiftKey) e.preventDefault();
      setOpen(false);
      button.current?.focus();
    }
  };

  return (
    <div
      className="relative flex-none"
      onKeyDown={(e) => {
        // Escape closes the menu only, not the drawer around it.
        if (e.key !== "Escape" || !open) return;
        e.stopPropagation();
        setOpen(false);
        button.current?.focus();
      }}
    >
      <button
        ref={button}
        type="button"
        aria-label={props.label}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        onKeyDown={(e) => {
          if (e.key !== "Tab" || e.shiftKey || !open) return;
          const first = itemButtons()[0];
          if (!first) return;
          e.preventDefault();
          e.stopPropagation();
          first.focus();
        }}
        className={props.className}
      >
        {props.children}
      </button>
      {open &&
        createPortal(
          <>
            <div className="fixed inset-0 z-[60]" aria-hidden onClick={() => setOpen(false)} />
            <div
              ref={list}
              role="menu"
              aria-label={props.label}
              onKeyDown={onItemKey}
              style={at ? { top: at.top, right: at.right } : { top: 0, right: 0, visibility: "hidden" }}
              className="fixed z-[70] min-w-48 rounded-[12px] border border-line bg-surface-2 p-1 shadow-lg"
            >
              {props.items.map(({ label, icon: Icon, onSelect, disabled }) => (
                <button
                  key={label}
                  type="button"
                  role="menuitem"
                  disabled={disabled}
                  onClick={() => {
                    setOpen(false);
                    // A dialog the item opens returns its focus here, as it did when the list sat beside the button.
                    button.current?.focus();
                    onSelect();
                  }}
                  className="flex min-h-11 w-full items-center gap-2.5 rounded-[8px] px-3 text-left text-[15px] text-text hover:bg-surface disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent"
                >
                  <Icon size={18} strokeWidth={1.8} aria-hidden className="text-muted" />
                  {label}
                </button>
              ))}
            </div>
          </>,
          document.body,
        )}
    </div>
  );
}

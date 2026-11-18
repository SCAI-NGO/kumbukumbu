// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from "react";
import { createPortal, flushSync } from "react-dom";
import type { LucideIcon } from "lucide-react";

/** The least a list is given where the screen has less room on its side: three items. */
export const MIN_HEIGHT = 152;

/** One entry of a small menu; later steps add theirs to the same list (Upload, for one). */
export interface MenuItem {
  /** Names the item apart from its label, for a caller that looks for it (the toolbar's, which moves tools in). */
  id?: string;
  label: string;
  icon: LucideIcon;
  onSelect(): void;
  disabled?: boolean;
  /** Shown after the label, at the item's end: a count, say. */
  after?: ReactNode;
}

/** Items under a heading of their own, as Later's Postpone and More time. */
export interface MenuSection {
  heading: string;
  items: MenuItem[];
}

/**
 * A button that opens a short list of actions; a tap outside or Escape closes it. The list is `position: fixed` at the
 * button's position, in `document.body` or, for a button inside an `aria-modal` dialog, in that dialog (so assistive
 * technology that keeps to the dialog still reaches it), so no scrolling or clipping container around the button can
 * cut it off; it turns upward where there is no room below, and scrolls within the room on its side where it is
 * taller. A scroll the person makes (the wheel, a drag, a key, a scrollbar) closes it; a scroll the page makes by itself
 * (the tree bringing the open note into view) moves it with its button; a resize repositions it.
 */
export function Menu(props: {
  label: string;
  /** A tooltip for a button that shows only its icon. */
  title?: string;
  items: MenuItem[];
  /** After the items, each under its heading. */
  sections?: MenuSection[];
  placement: "above" | "below";
  className: string;
  /** Told when the list opens: what it offers may be asked for only then. */
  onOpen?: () => void;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const [host, setHost] = useState<HTMLElement>(document.body);
  const [at, setAt] = useState<{ top: number; right: number; maxHeight: number } | null>(null);
  const button = useRef<HTMLButtonElement>(null);
  const list = useRef<HTMLDivElement>(null);

  const place = () => {
    if (!button.current || !list.current) return;
    const anchor = button.current.getBoundingClientRect();
    // Its whole height, not the part its limit shows: the scroll height once it scrolls.
    const height = Math.max(list.current.getBoundingClientRect().height, list.current.scrollHeight);
    const below = anchor.bottom + 4;
    const fitsBelow = below + height <= window.innerHeight;
    const fitsAbove = anchor.top - 8 - height >= 0;
    const roomBelow = window.innerHeight - below - 8;
    const roomAbove = anchor.top - 8 - 8;
    // Where it fits on neither side, it takes the side with more room and scrolls within it.
    const down =
      props.placement === "below" ? fitsBelow || (!fitsAbove && roomBelow >= roomAbove) : !fitsAbove && (fitsBelow || roomBelow > roomAbove);
    const maxHeight = Math.max(MIN_HEIGHT, down ? roomBelow : roomAbove);
    const top = down ? below : Math.max(8, anchor.top - 8 - Math.min(height, maxHeight));
    setAt({ top, right: window.innerWidth - anchor.right, maxHeight });
  };

  useLayoutEffect(() => {
    if (!open) {
      setAt(null);
      return;
    }
    place();
    const outside = (e: Event) => !list.current?.contains(e.target as Node);
    // The person's own scrolling: the wheel, a finger, or a press (a scrollbar) or key held while the page scrolls. A key
    // only marks the press: Space on the button would otherwise close the list for its click to open it again.
    let pressed = false;
    const onPerson = (e: Event) => outside(e) && setOpen(false);
    const onDown = (e: Event) => (pressed = outside(e));
    const onUp = () => (pressed = false);
    const onScroll = (e: Event) => {
      if (!outside(e)) return;
      if (pressed) setOpen(false);
      else place();
    };
    const passive = { capture: true, passive: true };
    window.addEventListener("resize", place);
    window.addEventListener("scroll", onScroll, true);
    window.addEventListener("wheel", onPerson, passive);
    window.addEventListener("touchmove", onPerson, passive);
    window.addEventListener("pointerdown", onDown, true);
    window.addEventListener("pointerup", onUp, true);
    window.addEventListener("pointercancel", onUp, true);
    window.addEventListener("keydown", onDown, true);
    window.addEventListener("keyup", onUp, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", onScroll, true);
      window.removeEventListener("wheel", onPerson, true);
      window.removeEventListener("touchmove", onPerson, true);
      window.removeEventListener("pointerdown", onDown, true);
      window.removeEventListener("pointerup", onUp, true);
      window.removeEventListener("pointercancel", onUp, true);
      window.removeEventListener("keydown", onDown, true);
      window.removeEventListener("keyup", onUp, true);
    };
  }, [open]);

  // The list sits at the end of its host, not next to the button: past either end of the items, Tab hands the focus
  // back to the button for the dialog's own trap to carry on from.
  const tabOut = (e: React.KeyboardEvent) => {
    if (e.shiftKey) e.preventDefault();
    // Gone from the DOM before the dialog's trap looks for its last control.
    flushSync(() => setOpen(false));
    button.current?.focus();
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
        title={props.title}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => {
          setHost(button.current?.closest<HTMLElement>('[role="dialog"][aria-modal="true"], [role="alertdialog"][aria-modal="true"]') ?? document.body);
          if (!open) props.onOpen?.();
          setOpen(!open);
        }}
        onKeyDown={(e) => {
          if (e.key !== "Tab" || !open) return;
          const first = e.shiftKey ? undefined : itemButtons(list.current)[0];
          if (!first) {
            setOpen(false);
            return;
          }
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
          <MenuList
            label={props.label}
            items={props.items}
            sections={props.sections}
            listRef={list}
            style={at ? { top: at.top, right: at.right, maxHeight: at.maxHeight } : { top: 0, right: 0, visibility: "hidden" }}
            onChoose={(item) => {
              setOpen(false);
              // A dialog the item opens returns its focus here, as it did when the list sat beside the button.
              button.current?.focus();
              item.onSelect();
            }}
            onTabOut={tabOut}
            onDismiss={() => setOpen(false)}
          />,
          host,
        )}
    </div>
  );
}

/** The items of a list that can be chosen, in order. */
export const itemButtons = (list: HTMLElement | null) => [...(list?.querySelectorAll<HTMLButtonElement>("button:not([disabled])") ?? [])];

/**
 * The list a menu shows over a backdrop, for `Menu` at its button and `PointMenu` at a point: `position: fixed` where
 * `style` puts it. The arrows and Tab move between the items; Tab past either end goes to `onTabOut`. A chosen item
 * goes to `onChoose`, which closes the list and runs it; a tap on the backdrop to `onDismiss`.
 */
export function MenuList(props: {
  label: string;
  items: MenuItem[];
  sections?: MenuSection[];
  listRef: RefObject<HTMLDivElement | null>;
  style: CSSProperties;
  onChoose(item: MenuItem): void;
  onTabOut(e: React.KeyboardEvent): void;
  onDismiss(): void;
}) {
  const id = useId();
  const onItemKey = (e: React.KeyboardEvent) => {
    const isTab = e.key === "Tab";
    if (!isTab && e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
    const buttons = itemButtons(props.listRef.current);
    const step = e.key === "ArrowUp" || (isTab && e.shiftKey) ? -1 : 1;
    const next = buttons[buttons.indexOf(document.activeElement as HTMLButtonElement) + step];
    if (next) {
      e.preventDefault();
      e.stopPropagation();
      next.focus();
      next.scrollIntoView?.({ block: "nearest" });
    } else if (isTab) props.onTabOut(e);
  };

  const renderItem = (item: MenuItem, key: string) => {
    const { label, icon: Icon, disabled, after } = item;
    return (
      <button
        key={key}
        type="button"
        role="menuitem"
        disabled={disabled}
        onClick={() => props.onChoose(item)}
        className="flex min-h-12 w-full items-center gap-2.5 rounded-[8px] px-3 text-left text-[15px] text-text hover:bg-surface disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent"
      >
        <Icon size={18} strokeWidth={1.8} aria-hidden className="text-muted" />
        {label}
        {after && <span className="ml-auto flex">{after}</span>}
      </button>
    );
  };

  return (
    <>
      <div data-testid="menu-backdrop" className="fixed inset-0 z-[60]" aria-hidden onClick={props.onDismiss} />
      <div
        ref={props.listRef}
        role="menu"
        aria-label={props.label}
        onKeyDown={onItemKey}
        style={props.style}
        className="fixed z-[70] min-w-48 overflow-y-auto overscroll-contain rounded-[12px] border border-line bg-surface-2 p-1 shadow-lg"
      >
        {props.items.map((item) => renderItem(item, item.label))}
        {props.sections?.map((section, i) => (
          <div key={section.heading} role="group" aria-labelledby={`${id}-${i}`}>
            <div id={`${id}-${i}`} role="presentation" className="px-3 pt-2 pb-1 font-mono text-[11px] tracking-[0.08em] text-muted uppercase">
              {section.heading}
            </div>
            {section.items.map((item) => renderItem(item, `${section.heading}-${item.label}`))}
          </div>
        ))}
      </div>
    </>
  );
}

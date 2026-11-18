// SPDX-License-Identifier: AGPL-3.0-or-later
import { useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { itemButtons, MenuList, MIN_HEIGHT, type MenuItem } from "./Menu";

/** Where a menu opens: from `left`, below `bottom` (a line's, else `top`), or above `top` where there is no room below. */
export interface MenuPoint {
  left: number;
  top: number;
  bottom?: number;
}

/**
 * `Menu`'s list at a point instead of a button, a context menu: at the caret, say. It opens to the right of the point
 * and below it, flips to the left and above where there is no room, and scrolls within the side with more room where
 * it fits on neither. The focus starts on the first item; Escape, Tab past either end, a tap outside, a scroll or a
 * resize close it (`onClose`, which puts the focus back), as does a chosen item, before it runs.
 */
export function PointMenu(props: { label: string; items: MenuItem[]; at: MenuPoint; onClose(): void }) {
  const { left, top, bottom = top } = props.at;
  const list = useRef<HTMLDivElement>(null);
  const [placed, setPlaced] = useState<{ left: number; top: number; maxHeight: number } | null>(null);
  const close = useRef(props.onClose);
  close.current = props.onClose;

  useLayoutEffect(() => {
    const box = list.current!.getBoundingClientRect();
    // Its whole height, not the part its limit shows: the scroll height once it scrolls.
    const height = Math.max(box.height, list.current!.scrollHeight);
    const below = bottom + 4;
    const above = top - 4;
    const roomBelow = window.innerHeight - below - 8;
    const roomAbove = above - 8;
    const down = height <= roomBelow || (height > roomAbove && roomBelow >= roomAbove);
    const maxHeight = Math.max(MIN_HEIGHT, down ? roomBelow : roomAbove);
    const shown = Math.min(height, maxHeight);
    // On screen whatever the point: a caret scrolled out of the editor's view stands outside the window.
    const within = (at: number, size: number, room: number) => Math.max(8, Math.min(at, room - 8 - size));
    setPlaced({
      left: within(left + box.width + 8 <= window.innerWidth ? left : left - box.width, box.width, window.innerWidth),
      top: within(down ? below : above - shown, shown, window.innerHeight),
      maxHeight,
    });
    itemButtons(list.current)[0]?.focus();

    // The point stays where it was; what it stood for moves with a scroll or a resize.
    const onScroll = (e: Event) => !list.current?.contains(e.target as Node) && close.current();
    const onResize = () => close.current();
    window.addEventListener("scroll", onScroll, true);
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("scroll", onScroll, true);
      window.removeEventListener("resize", onResize);
    };
  }, [left, top, bottom]);

  return createPortal(
    <div
      onKeyDown={(e) => {
        if (e.key !== "Escape") return;
        e.preventDefault();
        e.stopPropagation();
        props.onClose();
      }}
    >
      <MenuList
        label={props.label}
        items={props.items}
        listRef={list}
        style={placed ?? { top: 0, left: 0, visibility: "hidden" }}
        onChoose={(item) => {
          props.onClose();
          item.onSelect();
        }}
        onTabOut={(e) => {
          e.preventDefault();
          props.onClose();
        }}
        onDismiss={props.onClose}
      />
    </div>,
    document.body,
  );
}

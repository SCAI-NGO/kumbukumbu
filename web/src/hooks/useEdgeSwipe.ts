// SPDX-License-Identifier: AGPL-3.0-or-later
import { useRef, type TouchEvent } from "react";

type Point = { x: number; y: number };

const EDGE = 32;
const DISTANCE = 60;

/** What a touch from `start` to `end` does to the drawer (MOBILE_EDITOR §4, Tree); null for anything else, a scroll above all. */
export function swipe(start: Point, end: Point, open: boolean): "open" | "close" | null {
  const dx = end.x - start.x;
  if (Math.abs(end.y - start.y) >= DISTANCE) return null;
  if (!open && start.x < EDGE && dx > DISTANCE) return "open";
  if (open && dx < -DISTANCE) return "close";
  return null;
}

/**
 * Touch handlers for the shell: a swipe in from the left edge opens the drawer, a swipe left closes it. A second
 * finger (a pinch) or a touch the browser takes over (`touchcancel`, a scroll or its own back gesture) is no swipe.
 */
export function useEdgeSwipe(open: boolean, onOpen: () => void, onClose: () => void) {
  const start = useRef<Point | null>(null);
  return {
    onTouchStart(e: TouchEvent) {
      const t = e.touches.length === 1 ? e.touches[0] : undefined;
      start.current = t ? { x: t.clientX, y: t.clientY } : null;
    },
    onTouchCancel() {
      start.current = null;
    },
    onTouchEnd(e: TouchEvent) {
      const t = e.changedTouches[0];
      if (!start.current || !t) return;
      const action = swipe(start.current, { x: t.clientX, y: t.clientY }, open);
      start.current = null;
      if (action === "open") onOpen();
      if (action === "close") onClose();
    },
  };
}

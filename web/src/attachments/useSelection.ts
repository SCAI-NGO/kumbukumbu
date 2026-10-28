// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useEffect, useRef, useState, type PointerEvent } from "react";

/** How long a finger rests on a tile before it selects (as a phone's photo app). */
export const longPressMs = 500;
/** A finger that moves further is scrolling, not pressing. */
const slop = 10;

/**
 * The gallery's selection (attachments design, "Gallery"): the paths in the order they were picked. Selecting is
 * on while anything is selected, or once turned on (the desktop's "Select"); `clear` ends both.
 */
export function useSelection() {
  const [selected, setSelected] = useState<string[]>([]);
  const [mode, setMode] = useState(false);

  const toggle = useCallback((path: string) => setSelected((s) => (s.includes(path) ? s.filter((p) => p !== path) : [...s, path])), []);
  const clear = useCallback(() => {
    setSelected([]);
    setMode(false);
  }, []);
  /** Keeps only what is still there (after a move or a delete, or a change elsewhere). */
  const keep = useCallback((paths: ReadonlySet<string>) => setSelected((s) => (s.every((p) => paths.has(p)) ? s : s.filter((p) => paths.has(p)))), []);
  /** Lets go of these (they moved), keeping the rest selected. */
  const drop = useCallback((paths: readonly string[]) => setSelected((s) => s.filter((p) => !paths.includes(p))), []);

  return { selected, selecting: mode || selected.length > 0, toggle, clear, keep, drop, start: () => setMode(true) };
}

/**
 * A long press on any of many elements, each told apart by its key: `handlers(key)` goes onto the element, and
 * `fired()` says (once) that the click which follows the finger's lifting belongs to the long press, not a tap.
 */
export function useLongPress(onLongPress: (key: string) => void) {
  const press = useRef<{ key: string; x: number; y: number; timer: ReturnType<typeof setTimeout> } | null>(null);
  const done = useRef(false);
  const pointer = useRef<string>("mouse");
  const callback = useRef(onLongPress);
  callback.current = onLongPress;

  const cancel = useCallback(() => {
    if (press.current) clearTimeout(press.current.timer);
    press.current = null;
  }, []);
  useEffect(() => cancel, [cancel]);

  const handlers = useCallback(
    (key: string) => ({
      onPointerDown: (e: PointerEvent) => {
        pointer.current = e.pointerType;
        // The mouse has Ctrl-click and "Select"; a long press is the finger's (and the pen's).
        if (e.pointerType === "mouse" || e.isPrimary === false) return;
        cancel();
        done.current = false;
        const timer = setTimeout(() => {
          press.current = null;
          done.current = true;
          callback.current(key);
        }, longPressMs);
        press.current = { key, x: e.clientX, y: e.clientY, timer };
      },
      onPointerMove: (e: PointerEvent) => {
        const p = press.current;
        if (p && Math.hypot(e.clientX - p.x, e.clientY - p.y) > slop) cancel();
      },
      onPointerUp: cancel,
      onPointerCancel: cancel,
      onPointerLeave: cancel,
      // The phone's own long-press menu (save image, share) would cover the selection; the mouse keeps its menu.
      onContextMenu: (e: { preventDefault(): void }) => {
        if (pointer.current !== "mouse") e.preventDefault();
      },
    }),
    [cancel],
  );

  const fired = useCallback(() => {
    const was = done.current;
    done.current = false;
    return was;
  }, []);

  return { handlers, fired };
}

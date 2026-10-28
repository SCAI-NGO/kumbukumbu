// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent, type RefObject } from "react";

// The lightbox's gestures, by hand rather than from a lightbox library: a PDF needs the browser's own scrolling with
// the app's pinch, which a library's carousel (touch-action: none over everything) and image-only zoom would fight;
// the move and delete dialogs would have to live inside a library's portal, past its focus trap and its keys; and
// these few pointer events, with nothing to load, are all an image needs.

/** How an image is shown: `scale` over its fitted size (1 = fitted), moved by `x`, `y` px from the centre. */
export interface View {
  scale: number;
  x: number;
  y: number;
}

export const fitted: View = { scale: 1, x: 0, y: 0 };
export const maxScale = 8;
const doubleTapScale = 2;

/** A swipe counts past this many pixels; a tap moves less than `tapSlop` and lasts under `tapTime`. */
const swipeDistance = 60;
const closeDistance = 100;
const tapSlop = 10;
const tapTime = 300;

type Size = { width: number; height: number };

/** The image's size fitted into the stage (`object-fit: contain`). */
export function fitSize(image: Size, stage: Size): Size {
  if (!image.width || !image.height) return stage;
  const ratio = Math.min(stage.width / image.width, stage.height / image.height);
  return { width: image.width * ratio, height: image.height * ratio };
}

/** Keeps the zoomed image over the stage: no gap at an edge while it is larger than the stage, centred while smaller. */
export function clampView(view: View, fit: Size, stage: Size): View {
  const scale = Math.min(maxScale, Math.max(1, view.scale));
  const spareX = Math.max(0, (fit.width * scale - stage.width) / 2);
  const spareY = Math.max(0, (fit.height * scale - stage.height) / 2);
  return { scale, x: Math.min(spareX, Math.max(-spareX, view.x)), y: Math.min(spareY, Math.max(-spareY, view.y)) };
}

/** `view` zoomed to `scale` about `point` (px from the stage's centre): the spot under it stays where it is. */
export function zoomAt(view: View, scale: number, point: { x: number; y: number }): View {
  const ratio = scale / view.scale;
  return { scale, x: point.x - (point.x - view.x) * ratio, y: point.y - (point.y - view.y) * ratio };
}

/** What a finger that went `dx`, `dy` without zoom asks for. */
export function swipeOf(dx: number, dy: number): "next" | "previous" | "close" | null {
  if (Math.abs(dx) > swipeDistance && Math.abs(dx) > Math.abs(dy)) return dx < 0 ? "next" : "previous";
  if (dy > closeDistance && dy > Math.abs(dx)) return "close";
  return null;
}

type Point = { x: number; y: number };

/**
 * Pinch to zoom, one finger (or the mouse) to pan while zoomed, a double tap (or click) for 2× and back, the wheel to
 * zoom at the pointer; without zoom, a swipe sideways or down asks `onSwipe`. The stage needs `touch-action: none`,
 * which keeps the browser's own zoom and scrolling out of it. `offset` is how far an unzoomed swipe has moved the
 * image so far, so it follows the finger.
 */
export function useZoomPan(stage: RefObject<HTMLElement>, image: Size | null, onSwipe: (swipe: "next" | "previous" | "close") => void) {
  const [view, setView] = useState<View>(fitted);
  const [offset, setOffset] = useState<Point>({ x: 0, y: 0 });
  const viewRef = useRef(view);
  viewRef.current = view;
  const pointers = useRef(new Map<number, Point>());
  const gesture = useRef<
    | { kind: "drag"; start: Point; from: View; at: number; moved: boolean; touch: boolean }
    | { kind: "pinch"; distance: number; middle: Point; from: View }
    | null
  >(null);
  const lastTap = useRef<{ at: number; point: Point } | null>(null);

  const measure = useCallback(() => {
    const rect = stage.current?.getBoundingClientRect();
    const size = { width: rect?.width ?? 0, height: rect?.height ?? 0 };
    return { rect, size, fit: fitSize(image ?? size, size) };
  }, [stage, image]);

  /** A client point as px from the stage's centre. */
  const local = useCallback(
    (p: Point) => {
      const { rect } = measure();
      return rect ? { x: p.x - rect.left - rect.width / 2, y: p.y - rect.top - rect.height / 2 } : { x: 0, y: 0 };
    },
    [measure],
  );

  const apply = useCallback(
    (next: View) => {
      const { size, fit } = measure();
      setView(clampView(next, fit, size));
    },
    [measure],
  );

  const zoomTo = useCallback((scale: number, point: Point = { x: 0, y: 0 }) => apply(zoomAt(viewRef.current, scale, point)), [apply]);

  const pinchOf = () => {
    const [a, b] = [...pointers.current.values()];
    return { distance: Math.hypot(a.x - b.x, a.y - b.y) || 1, middle: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } };
  };

  const onPointerDown = (e: ReactPointerEvent) => {
    if (e.pointerType === "mouse" && e.button !== 0) return;
    e.currentTarget.setPointerCapture?.(e.pointerId);
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pointers.current.size === 2) {
      const { distance, middle } = pinchOf();
      gesture.current = { kind: "pinch", distance, middle: local(middle), from: viewRef.current };
      setOffset({ x: 0, y: 0 });
    } else if (pointers.current.size === 1) {
      gesture.current = { kind: "drag", start: { x: e.clientX, y: e.clientY }, from: viewRef.current, at: e.timeStamp, moved: false, touch: e.pointerType !== "mouse" };
    }
  };

  const onPointerMove = (e: ReactPointerEvent) => {
    if (!pointers.current.has(e.pointerId)) return;
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    const g = gesture.current;
    if (g?.kind === "pinch" && pointers.current.size >= 2) {
      const { distance, middle } = pinchOf();
      const scale = Math.min(maxScale, Math.max(1, (g.from.scale * distance) / g.distance));
      // Zoomed about where the fingers started, and moved along with them.
      const moved = zoomAt(g.from, scale, g.middle);
      const now = local(middle);
      apply({ scale, x: moved.x + now.x - g.middle.x, y: moved.y + now.y - g.middle.y });
    } else if (g?.kind === "drag") {
      const dx = e.clientX - g.start.x;
      const dy = e.clientY - g.start.y;
      if (Math.hypot(dx, dy) > tapSlop) g.moved = true;
      if (g.from.scale > 1) apply({ ...g.from, x: g.from.x + dx, y: g.from.y + dy });
      else if (g.touch) setOffset({ x: dx, y: Math.max(0, dy) });
    }
  };

  const onPointerUp = (e: ReactPointerEvent) => {
    if (!pointers.current.delete(e.pointerId)) return;
    const g = gesture.current;
    if (g?.kind === "pinch") {
      // The finger left behind pans from here on, without a jump; after a pinch it never swipes.
      const [rest] = [...pointers.current.entries()];
      gesture.current = rest ? { kind: "drag", start: rest[1], from: viewRef.current, at: e.timeStamp, moved: true, touch: false } : null;
      return;
    }
    gesture.current = null;
    if (g?.kind !== "drag" || e.type === "pointercancel") {
      setOffset({ x: 0, y: 0 });
      return;
    }
    const dx = e.clientX - g.start.x;
    const dy = e.clientY - g.start.y;
    setOffset({ x: 0, y: 0 });
    if (g.moved) {
      const swipe = g.from.scale === 1 && g.touch ? swipeOf(dx, dy) : null;
      if (swipe) onSwipe(swipe);
      return;
    }
    if (e.timeStamp - g.at > tapTime) return;
    const point = { x: e.clientX, y: e.clientY };
    const last = lastTap.current;
    if (last && e.timeStamp - last.at < tapTime && Math.hypot(point.x - last.point.x, point.y - last.point.y) < tapSlop * 3) {
      lastTap.current = null;
      zoomTo(viewRef.current.scale > 1 ? 1 : doubleTapScale, local(point));
    } else {
      lastTap.current = { at: e.timeStamp, point };
    }
  };

  // The wheel zooms at the pointer; React's wheel listener is passive, so this one is the stage's own.
  useEffect(() => {
    const element = stage.current;
    if (!element) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      zoomTo(viewRef.current.scale * Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.002)), local({ x: e.clientX, y: e.clientY }));
    };
    element.addEventListener("wheel", onWheel, { passive: false });
    return () => element.removeEventListener("wheel", onWheel);
  }, [stage, zoomTo, local]);

  const reset = useCallback(() => setView(fitted), []);

  return {
    view,
    offset,
    zoomTo,
    reset,
    handlers: { onPointerDown, onPointerMove, onPointerUp, onPointerCancel: onPointerUp },
  };
}

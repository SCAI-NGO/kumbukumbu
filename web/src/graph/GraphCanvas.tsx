// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent, type PointerEvent } from "react";
import { mediaQueryHook } from "../hooks/mediaQuery";
import { useDarkScheme } from "../theme/dark";
import { nearestInDirection, type Direction } from "./keyboard";
import type { GEdge, GNode, SizeBy } from "./model";
import { ageColour, edgeOpacity, edgeWidth, radius } from "./scales";
import { similarity } from "./similarity";
import type { Suggestion } from "./suggestions";
import { useForceLayout } from "./useForceLayout";

type View = { x: number; y: number; w: number; h: number };
type Props = {
  nodes: GNode[];
  edges: GEdge[];
  centreKey: string;
  otherKey: string | null;
  sizeBy: SizeBy;
  suggestions: Suggestion[];
  showSuggestions: boolean;
  onOpen(node: GNode, beside: boolean): void;
  onHover(node: GNode | null): void;
  onSuggestion(s: Suggestion): void;
  fitSignal: number;
  running: boolean;
};

const useReducedMotion = mediaQueryHook("(prefers-reduced-motion: reduce)");

const PADDING = 24;
const ZOOM_MIN = 0.3;
const ZOOM_MAX = 3;
const HOVER_MS = 300;
const TITLE_MAX = 24;
/** Suggested notes are not in the layout: they sit on a ring this far around the centre. */
const SUGGESTION_RING = 140;
const SUGGESTION_R = 5;
/** Room below a node for its title. */
const TITLE_BELOW = 14;
/** How far the pointer moves before a press on a node is a drag rather than a click. */
const DRAG_SLOP = 3;
/** The column's size until it has been measured (and in tests, where nothing is). */
const FALLBACK_SIZE = { w: 560, h: 560 };

const arrows: Record<string, Direction> = { ArrowUp: "up", ArrowDown: "down", ArrowLeft: "left", ArrowRight: "right" };
const lineColour = "color-mix(in srgb, var(--color-line) 65%, var(--color-text))";

const truncate = (title: string) => (title.length > TITLE_MAX ? `${title.slice(0, TITLE_MAX)}…` : title);
const label = (n: GNode) => (n.kind === "ghost" ? `${n.title}, no note yet` : `${n.title}, ${n.in} ${n.in === 1 ? "link" : "links"} in, ${n.out} out`);

/** Every box given, padded, widened to the column's proportions, and never magnified past the largest zoom. */
function fit(boxes: { x: number; y: number; r: number; below: number }[], size: { w: number; h: number }): View {
  if (boxes.length === 0) return { x: -size.w / 2, y: -size.h / 2, w: size.w, h: size.h };
  const x0 = Math.min(...boxes.map((b) => b.x - b.r)) - PADDING;
  const x1 = Math.max(...boxes.map((b) => b.x + b.r)) + PADDING;
  const y0 = Math.min(...boxes.map((b) => b.y - b.r)) - PADDING;
  const y1 = Math.max(...boxes.map((b) => b.y + b.r + b.below)) + PADDING;
  const aspect = size.w / size.h;
  let [w, h] = [x1 - x0, y1 - y0];
  if (w / h < aspect) w = h * aspect;
  else h = w / aspect;
  if (size.w / w > ZOOM_MAX) [w, h] = [size.w / ZOOM_MAX, size.h / ZOOM_MAX];
  return { x: (x0 + x1 - w) / 2, y: (y0 + y1 - h) / 2, w, h };
}

/**
 * The local graph as SVG: edges, nodes shaped by kind and coloured by age, the centre ringed, suggested links dashed.
 * The view fits the layout as it settles until the user zooms, pans or drags; `fitSignal` or a new centre fits again.
 * The nodes are a roving focus group, entered at the centre; the arrow keys move between them.
 */
export default function GraphCanvas({ nodes, edges, centreKey, otherKey, sizeBy, suggestions, showSuggestions, onOpen, onHover, onSuggestion, fitSignal, running }: Props) {
  const reducedMotion = useReducedMotion();
  const dark = useDarkScheme();
  const { positions, drag } = useForceLayout(nodes, edges, { centreKey, sizeBy, reducedMotion, running });
  const svg = useRef<SVGSVGElement>(null);
  const elements = useRef(new Map<string, SVGGElement>());
  const [size, setSize] = useState(FALLBACK_SIZE);
  // null while the view follows the layout; the user's own view once they zoom, pan or drag.
  const [manual, setManual] = useState<View | null>(null);
  const [focusId, setFocusId] = useState<string | null>(null);

  const centre = nodes.find((n) => n.key === centreKey) ?? null;
  const centreAt = (centre && positions.get(centre.id)) || { x: 0, y: 0 };
  const shownSuggestions = showSuggestions ? suggestions : [];
  const suggestionAt = (i: number) => {
    const angle = -Math.PI / 2 + (i * 2 * Math.PI) / shownSuggestions.length;
    return { x: centreAt.x + SUGGESTION_RING * Math.cos(angle), y: centreAt.y + SUGGESTION_RING * Math.sin(angle) };
  };

  const fitted = fit(
    [
      ...nodes.flatMap((n) => {
        const p = positions.get(n.id);
        return p ? [{ ...p, r: radius(n, sizeBy) + 4, below: TITLE_BELOW }] : [];
      }),
      ...shownSuggestions.map((_, i) => ({ ...suggestionAt(i), r: SUGGESTION_R, below: TITLE_BELOW })),
    ],
    size,
  );
  const view = manual ?? fitted;
  const viewRef = useRef(view);
  viewRef.current = view;

  useEffect(() => setManual(null), [fitSignal, centreKey]);
  useEffect(() => setFocusId(null), [centreKey]);

  useEffect(() => {
    const el = svg.current;
    if (!el || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0 && el.clientHeight > 0) setSize({ w: el.clientWidth, h: el.clientHeight });
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  // The pointer in the graph's coordinates; the view keeps the column's proportions, so the mapping is linear.
  const toGraph = (clientX: number, clientY: number) => {
    const rect = svg.current!.getBoundingClientRect();
    const v = viewRef.current;
    const [w, h] = [rect.width || size.w, rect.height || size.h];
    return { x: v.x + ((clientX - rect.left) / w) * v.w, y: v.y + ((clientY - rect.top) / h) * v.h };
  };
  const toGraphRef = useRef(toGraph);
  toGraphRef.current = toGraph;

  // Wheel zoom around the pointer. A native listener, since React's wheel listener is passive and cannot keep the page still.
  useEffect(() => {
    const el = svg.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const v = viewRef.current;
      const zoom = (el.clientWidth || FALLBACK_SIZE.w) / v.w;
      // Clamped at the limits; a fit beyond one (a wide graph) may come back from it but goes no further.
      const target = Math.min(Math.max(ZOOM_MAX, zoom), Math.max(Math.min(ZOOM_MIN, zoom), zoom * Math.exp(-e.deltaY * 0.002)));
      const factor = target / zoom;
      if (factor === 1) return;
      const p = toGraphRef.current(e.clientX, e.clientY);
      setManual({ x: p.x - (p.x - v.x) / factor, y: p.y - (p.y - v.y) / factor, w: v.w / factor, h: v.h / factor });
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, []);

  // A press on the background pans; a press on a node drags it once it has moved past the slop, and is no click then.
  const press = useRef<{ id: string | null; clientX: number; clientY: number; view: View; moved: boolean } | null>(null);
  const justDragged = useRef(false);
  const pointerDown = (e: PointerEvent<Element>, id: string | null) => {
    if (e.button !== 0) return;
    justDragged.current = false;
    press.current = { id, clientX: e.clientX, clientY: e.clientY, view, moved: false };
  };
  const pointerMove = (e: PointerEvent<SVGSVGElement>) => {
    const p = press.current;
    if (!p) return;
    const [dx, dy] = [e.clientX - p.clientX, e.clientY - p.clientY];
    if (!p.moved && Math.hypot(dx, dy) < DRAG_SLOP) return;
    // Captured only now: a capture from the press would send a plain click to the background instead of the node.
    if (!p.moved) svg.current?.setPointerCapture?.(e.pointerId);
    p.moved = true;
    if (p.id === null) {
      const width = svg.current!.getBoundingClientRect().width || size.w;
      const scale = p.view.w / width;
      setManual({ ...p.view, x: p.view.x - dx * scale, y: p.view.y - dy * scale });
    } else {
      setManual(viewRef.current);
      const at = toGraph(e.clientX, e.clientY);
      drag(p.id, at.x, at.y);
    }
  };
  const pointerUp = () => {
    const p = press.current;
    press.current = null;
    if (p?.id && p.moved) {
      drag(p.id, null, null);
      justDragged.current = true;
    }
  };

  const hoverTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const hovering = useRef(false);
  const hoverStart = (n: GNode) => {
    clearTimeout(hoverTimer.current);
    hoverTimer.current = setTimeout(() => {
      hovering.current = true;
      onHover(n);
    }, HOVER_MS);
  };
  const hoverEnd = () => {
    clearTimeout(hoverTimer.current);
    if (hovering.current) onHover(null);
    hovering.current = false;
  };
  useEffect(() => () => clearTimeout(hoverTimer.current), []);

  const open = (n: GNode, beside: boolean) => {
    if (n.kind !== "ghost") onOpen(n, beside);
  };
  const click = (e: MouseEvent, n: GNode) => {
    if (justDragged.current) {
      justDragged.current = false;
      return;
    }
    open(n, e.ctrlKey || e.metaKey);
  };

  const rovingId = (focusId && nodes.some((n) => n.id === focusId) ? focusId : centre?.id) ?? nodes[0]?.id;
  const keyDown = (e: KeyboardEvent, n: GNode) => {
    if (e.key === "Enter") {
      e.preventDefault();
      open(n, e.ctrlKey || e.metaKey);
      return;
    }
    const dir = arrows[e.key];
    const from = positions.get(n.id);
    if (!dir || !from) return;
    e.preventDefault();
    const others = nodes.flatMap((m) => {
      const p = positions.get(m.id);
      return p && m.id !== n.id ? [{ key: m.id, ...p }] : [];
    });
    const to = nearestInDirection(from, others, dir);
    if (!to) return;
    setFocusId(to);
    elements.current.get(to)?.focus();
  };

  // Once per answer, not on every frame of the layout.
  const scores = useMemo(() => {
    const keyOf = new Map(nodes.map((n) => [n.id, n.key]));
    return edges.map((e) => (keyOf.has(e.source) && keyOf.has(e.target) ? similarity(keyOf.get(e.source)!, keyOf.get(e.target)!).score : 0));
  }, [nodes, edges]);
  const neutral = ageColour(null, 0, dark);
  const now = Date.now();

  return (
    <svg
      ref={svg}
      role="group"
      aria-label={`Graph of ${centre?.title ?? ""}`}
      viewBox={`${view.x} ${view.y} ${view.w} ${view.h}`}
      className="block h-full w-full touch-none select-none"
      onPointerDown={(e) => pointerDown(e, null)}
      onPointerMove={pointerMove}
      onPointerUp={pointerUp}
      onPointerCancel={pointerUp}
    >
      <g aria-hidden="true">
        {edges.map((e, i) => {
          const [a, b] = [positions.get(e.source), positions.get(e.target)];
          if (!a || !b) return null;
          const s = scores[i];
          return <line key={i} x1={a.x} y1={a.y} x2={b.x} y2={b.y} strokeWidth={edgeWidth(s)} strokeOpacity={edgeOpacity(s)} style={{ stroke: lineColour }} />;
        })}
        {shownSuggestions.map((s, i) => {
          const at = suggestionAt(i);
          return (
            <line key={s.path} x1={centreAt.x} y1={centreAt.y} x2={at.x} y2={at.y} strokeDasharray="4 4" strokeWidth={edgeWidth(s.similarity.score)} strokeOpacity={edgeOpacity(s.similarity.score)} style={{ stroke: "var(--color-accent)" }} />
          );
        })}
      </g>
      {nodes.map((n) => {
        const p = positions.get(n.id);
        if (!p) return null;
        const r = radius(n, sizeBy);
        const ringed = n.key === centreKey || n.key === otherKey;
        const fill = n.kind === "note" || n.kind === "day" ? ageColour(n.writtenAt, now, dark) : neutral;
        const outline = dark ? undefined : { stroke: "var(--color-dim)", strokeWidth: 0.75 };
        return (
          <g
            key={n.id}
            ref={(el) => {
              if (el) elements.current.set(n.id, el);
              else elements.current.delete(n.id);
            }}
            role="button"
            tabIndex={n.id === rovingId ? 0 : -1}
            aria-label={label(n)}
            aria-disabled={n.kind === "ghost" ? true : undefined}
            transform={`translate(${p.x} ${p.y})`}
            className={`focus-visible:outline-2 focus-visible:outline-accent ${n.kind === "ghost" ? "" : "cursor-pointer"}`}
            onPointerDown={(e) => {
              e.stopPropagation();
              pointerDown(e, n.id);
            }}
            onClick={(e) => click(e, n)}
            onKeyDown={(e) => keyDown(e, n)}
            onFocus={() => {
              setFocusId(n.id);
              hoverStart(n);
            }}
            onBlur={hoverEnd}
            onMouseEnter={() => hoverStart(n)}
            onMouseLeave={hoverEnd}
          >
            {n.kind === "ghost" ? (
              <circle r={r} fill="none" stroke={neutral} strokeWidth={1.5} />
            ) : n.kind === "attachment" ? (
              <rect x={-r / Math.SQRT2} y={-r / Math.SQRT2} width={r * Math.SQRT2} height={r * Math.SQRT2} transform="rotate(45)" fill={fill} {...outline} />
            ) : (
              <circle r={r} fill={fill} {...outline} />
            )}
            {ringed && <circle data-ring r={r + 4} fill="none" strokeWidth={2} opacity={n.key === centreKey ? undefined : 0.45} style={{ stroke: "var(--color-text)" }} />}
            {n.leftOut > 0 && (
              <text aria-hidden="true" x={r + 4} y={-r} fontSize={9} style={{ fill: "var(--color-dim)" }}>
                +{n.leftOut}
              </text>
            )}
            <text aria-hidden="true" y={r + TITLE_BELOW - 2} textAnchor="middle" fontSize={10} style={{ fill: "var(--color-dim)" }}>
              {truncate(n.title)}
            </text>
          </g>
        );
      })}
      {shownSuggestions.map((s, i) => {
        const at = suggestionAt(i);
        return (
          <g
            key={s.path}
            role="button"
            tabIndex={0}
            aria-label={`Suggested link: ${s.title}`}
            transform={`translate(${at.x} ${at.y})`}
            className="cursor-pointer focus-visible:outline-2 focus-visible:outline-accent"
            onPointerDown={(e) => e.stopPropagation()}
            onClick={() => onSuggestion(s)}
            onKeyDown={(e) => {
              if (e.key !== "Enter") return;
              e.preventDefault();
              onSuggestion(s);
            }}
          >
            <circle r={SUGGESTION_R} fill="none" strokeWidth={1.5} strokeDasharray="2 2" style={{ stroke: "var(--color-accent)" }} />
            <text aria-hidden="true" y={SUGGESTION_R + 12} textAnchor="middle" fontSize={10} style={{ fill: "var(--color-dim)" }}>
              {truncate(s.title)}
            </text>
          </g>
        );
      })}
    </svg>
  );
}

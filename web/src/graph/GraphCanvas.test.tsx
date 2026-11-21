// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GEdge, GNode } from "./model";
import type { Suggestion } from "./suggestions";

const layout = vi.hoisted(() => ({ positions: new Map<string, { x: number; y: number }>(), drag: vi.fn() }));
vi.mock("./useForceLayout", () => ({ useForceLayout: () => layout }));

const { default: GraphCanvas } = await import("./GraphCanvas");

const node = (id: string, over: Partial<GNode> = {}): GNode => ({ id, key: id, path: id, title: id, kind: "note", tags: [], in: 1, out: 1, depth: 1, size: null, writtenAt: null, leftOut: 0, ...over });

const budget = node("Work/Budget.md", { title: "Budget", in: 3, out: 5, depth: 0 });
const plan = node("Work/Plan.md", { title: "Plan" });
const left = node("Work/Left.md", { title: "Left" });
const anna = node("ghost:0", { key: "ghost:Anna", path: null, title: "Anna", kind: "ghost", in: 1, out: 0 });
const scan = node("Work/scan.png", { title: "scan.png", kind: "attachment" });
const nodes = [budget, plan, left, anna, scan];
const edges: GEdge[] = [
  { source: budget.id, target: plan.id, flags: 0 },
  { source: budget.id, target: left.id, flags: 0 },
  { source: budget.id, target: anna.id, flags: 0 },
];
const suggestions: Suggestion[] = [{ path: "Work/Idea.md", title: "Idea", similarity: { score: 0.8, measured: false } }];

type Props = Parameters<typeof GraphCanvas>[0];
const props = (over: Partial<Omit<Props, "onOpen" | "onHover" | "onSuggestion" | "onSelect">> = {}) => ({
  nodes,
  edges,
  centreKey: budget.key,
  otherKey: plan.key,
  sizeBy: "links" as const,
  suggestions: [],
  showSuggestions: false,
  onOpen: vi.fn<Props["onOpen"]>(),
  onHover: vi.fn<Props["onHover"]>(),
  onSuggestion: vi.fn<Props["onSuggestion"]>(),
  onSelect: vi.fn<NonNullable<Props["onSelect"]>>(),
  fitSignal: 0,
  running: true,
  ...over,
});

const nodeButton = (name: string) => screen.getByRole("button", { name });

beforeEach(() => {
  layout.positions = new Map([
    [budget.id, { x: 0, y: 0 }],
    [plan.id, { x: 80, y: 10 }],
    [left.id, { x: -80, y: 0 }],
    [anna.id, { x: 0, y: 90 }],
    [scan.id, { x: 10, y: -90 }],
  ]);
  layout.drag.mockReset();
});

afterEach(() => vi.useRealTimers());

describe("GraphCanvas", () => {
  it("labels the graph by its centre and gives every node a button with its links", () => {
    render(<GraphCanvas {...props()} />);
    expect(screen.getByRole("group", { name: "Graph of Budget" })).toBeInTheDocument();
    expect(screen.getAllByRole("button")).toHaveLength(nodes.length);
    expect(nodeButton("Budget, 3 links in, 5 out")).toBeInTheDocument();
    expect(nodeButton("Anna, no note yet")).toBeInTheDocument();
  });

  it("draws a ghost hollow and an attachment as a diamond", () => {
    render(<GraphCanvas {...props()} />);
    const ghost = nodeButton("Anna, no note yet");
    expect(ghost.querySelector("circle")).toHaveAttribute("fill", "none");
    const attachment = nodeButton("scan.png, 1 link in, 1 out");
    expect(attachment.querySelector("rect")?.getAttribute("transform")).toMatch(/rotate\(45\)/);
  });

  it("rings the centre, and the other pane's node more faintly", () => {
    render(<GraphCanvas {...props()} />);
    const centreRing = nodeButton("Budget, 3 links in, 5 out").querySelector("[data-ring]");
    const otherRing = nodeButton("Plan, 1 link in, 1 out").querySelector("[data-ring]");
    expect(centreRing).not.toBeNull();
    expect(centreRing).not.toHaveAttribute("opacity");
    expect(otherRing).toHaveAttribute("opacity", "0.45");
    expect(nodeButton("Left, 1 link in, 1 out").querySelector("[data-ring]")).toBeNull();
  });

  it("truncates a long title and shows what a capped node leaves out", () => {
    const long = node("Work/Long.md", { title: "A rather long title for a note", leftOut: 7 });
    layout.positions.set(long.id, { x: 50, y: 50 });
    render(<GraphCanvas {...props({ nodes: [...nodes, long] })} />);
    const button = nodeButton("A rather long title for a note, 1 link in, 1 out");
    expect(button).toHaveTextContent("A rather long title for …");
    expect(button).toHaveTextContent("+7");
  });

  it("opens a note on click, beside with Ctrl, and a ghost not at all", () => {
    const p = props();
    render(<GraphCanvas {...p} />);
    fireEvent.click(nodeButton("Plan, 1 link in, 1 out"));
    expect(p.onOpen).toHaveBeenLastCalledWith(plan, false);
    fireEvent.click(nodeButton("Plan, 1 link in, 1 out"), { ctrlKey: true });
    expect(p.onOpen).toHaveBeenLastCalledWith(plan, true);
    p.onOpen.mockClear();
    fireEvent.click(nodeButton("Anna, no note yet"));
    expect(p.onOpen).not.toHaveBeenCalled();
  });

  it("enters at the centre and moves the focus with the arrow keys; Enter opens", () => {
    const p = props();
    render(<GraphCanvas {...p} />);
    const centre = nodeButton("Budget, 3 links in, 5 out");
    expect(centre).toHaveAttribute("tabindex", "0");
    expect(nodeButton("Plan, 1 link in, 1 out")).toHaveAttribute("tabindex", "-1");
    act(() => centre.focus());
    fireEvent.keyDown(centre, { key: "ArrowRight" });
    const right = nodeButton("Plan, 1 link in, 1 out");
    expect(right).toHaveFocus();
    expect(right).toHaveAttribute("tabindex", "0");
    expect(centre).toHaveAttribute("tabindex", "-1");
    fireEvent.keyDown(right, { key: "Enter" });
    expect(p.onOpen).toHaveBeenLastCalledWith(plan, false);
    fireEvent.keyDown(right, { key: "Enter", ctrlKey: true });
    expect(p.onOpen).toHaveBeenLastCalledWith(plan, true);
  });

  it("draws suggested links dashed only when shown, and opens one on click or Enter", () => {
    const { container, rerender } = render(<GraphCanvas {...props({ suggestions })} />);
    expect(container.querySelector('[stroke-dasharray="4 4"]')).toBeNull();
    const p = props({ suggestions, showSuggestions: true });
    rerender(<GraphCanvas {...p} />);
    expect(container.querySelector('line[stroke-dasharray="4 4"]')).not.toBeNull();
    const suggestion = screen.getByRole("button", { name: "Suggested link: Idea" });
    fireEvent.click(suggestion);
    expect(p.onSuggestion).toHaveBeenLastCalledWith(suggestions[0]);
    p.onSuggestion.mockClear();
    fireEvent.keyDown(suggestion, { key: "Enter" });
    expect(p.onSuggestion).toHaveBeenLastCalledWith(suggestions[0]);
  });

  it("enters at the centre even with suggestions shown, and marks a ghost disabled", () => {
    const { container } = render(<GraphCanvas {...props({ suggestions, showSuggestions: true })} />);
    expect(container.querySelector('[tabindex="0"]')).toBe(nodeButton("Budget, 3 links in, 5 out"));
    expect(nodeButton("Anna, no note yet")).toHaveAttribute("aria-disabled", "true");
    expect(nodeButton("Plan, 1 link in, 1 out")).not.toHaveAttribute("aria-disabled");
  });

  it("draws no dashed edge without suggestions, even when they are shown", () => {
    const { container } = render(<GraphCanvas {...props({ showSuggestions: true })} />);
    expect(container.querySelector('[stroke-dasharray="4 4"]')).toBeNull();
  });

  it("draws one line per edge", () => {
    const { container } = render(<GraphCanvas {...props()} />);
    expect(container.querySelectorAll("line")).toHaveLength(edges.length);
  });

  it("reports a hover after 300 ms, and none when the pointer leaves before", () => {
    vi.useFakeTimers();
    const p = props();
    render(<GraphCanvas {...p} />);
    const plain = nodeButton("Plan, 1 link in, 1 out");
    fireEvent.mouseEnter(plain);
    act(() => vi.advanceTimersByTime(299));
    expect(p.onHover).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(1));
    expect(p.onHover).toHaveBeenLastCalledWith(plan);
    fireEvent.mouseLeave(plain);
    expect(p.onHover).toHaveBeenLastCalledWith(null);

    p.onHover.mockClear();
    const other = nodeButton("Left, 1 link in, 1 out");
    fireEvent.mouseEnter(other);
    act(() => vi.advanceTimersByTime(200));
    fireEvent.mouseLeave(other);
    act(() => vi.advanceTimersByTime(500));
    expect(p.onHover).not.toHaveBeenCalled();
  });

  it("reports a hover on keyboard focus too", () => {
    vi.useFakeTimers();
    const p = props();
    render(<GraphCanvas {...p} />);
    fireEvent.focus(nodeButton("Left, 1 link in, 1 out"));
    act(() => vi.advanceTimersByTime(300));
    expect(p.onHover).toHaveBeenLastCalledWith(left);
  });

  it("fits every node into the view with 24 px padding", () => {
    render(<GraphCanvas {...props()} />);
    const [x, y, w, h] = screen.getByRole("group").getAttribute("viewBox")!.split(" ").map(Number);
    // Left's disc reaches to −80 minus its radius, and the padding lies beyond.
    expect(x).toBeLessThan(-80 - 5 - 24 + 0.01);
    expect(x + w).toBeGreaterThan(80 + 5 + 24 - 0.01);
    expect(y).toBeLessThan(-90 - 24);
    expect(y + h).toBeGreaterThan(90 + 24);
  });
});

const viewBox = () => screen.getByRole("group").getAttribute("viewBox")!.split(" ").map(Number);
/** The graph point under a client point; jsdom measures nothing, so the svg sits at 0,0 with its fallback width of 560. */
const graphAt = (clientX: number, clientY: number) => {
  const [x, y, w, h] = viewBox();
  return { x: x + (clientX / 560) * w, y: y + (clientY / 560) * h };
};

describe("select mode", () => {
  it("a click selects instead of opening", () => {
    const p = props({ select: true });
    render(<GraphCanvas {...p} />);
    fireEvent.click(nodeButton("Plan, 1 link in, 1 out"));
    expect(p.onSelect).toHaveBeenLastCalledWith(plan);
    fireEvent.click(nodeButton("Plan, 1 link in, 1 out"), { ctrlKey: true });
    expect(p.onSelect).toHaveBeenCalledTimes(2);
    expect(p.onSelect).toHaveBeenLastCalledWith(plan);
    expect(p.onOpen).not.toHaveBeenCalled();
  });

  it("Enter selects, a ghost selects too", () => {
    const p = props({ select: true });
    render(<GraphCanvas {...p} />);
    const ghost = nodeButton("Anna, no note yet");
    expect(ghost).not.toHaveAttribute("aria-disabled");
    act(() => ghost.focus());
    fireEvent.keyDown(ghost, { key: "Enter" });
    expect(p.onSelect).toHaveBeenLastCalledWith(anna);
    expect(p.onOpen).not.toHaveBeenCalled();
  });

  it("a click on the background clears the selection, a pan does not", () => {
    const p = props({ select: true });
    render(<GraphCanvas {...p} />);
    const svg = screen.getByRole("group");
    fireEvent.pointerDown(svg, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(svg, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.click(svg, { clientX: 100, clientY: 100 });
    expect(p.onSelect).toHaveBeenLastCalledWith(null);

    p.onSelect.mockClear();
    fireEvent.pointerDown(svg, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(svg, { pointerId: 1, clientX: 140, clientY: 100 });
    fireEvent.pointerUp(svg, { pointerId: 1, clientX: 140, clientY: 100 });
    fireEvent.click(svg, { clientX: 140, clientY: 100 });
    expect(p.onSelect).not.toHaveBeenCalled();
  });

  it("the selected node is marked", () => {
    render(<GraphCanvas {...props({ select: true, selectedKey: plan.key })} />);
    const selected = nodeButton("Plan, 1 link in, 1 out");
    expect(selected).toHaveAttribute("aria-pressed", "true");
    expect(selected.querySelector("[data-selected-ring]")).not.toBeNull();
    for (const name of ["Budget, 3 links in, 5 out", "Left, 1 link in, 1 out", "Anna, no note yet"]) {
      expect(nodeButton(name)).not.toHaveAttribute("aria-pressed");
      expect(nodeButton(name).querySelector("[data-selected-ring]")).toBeNull();
    }
  });
});

describe("touch", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("a coarse pointer gives every node a hit circle of 22 px or more", () => {
    vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(pointer: coarse)", media: query, addEventListener() {}, removeEventListener() {} }));
    render(<GraphCanvas {...props()} />);
    // 22 screen pixels in the graph's units.
    const least = (22 * viewBox()[2]) / 560;
    for (const n of nodes) {
      const hit = screen.getByRole("button", { name: new RegExp(`^${n.title},`) }).firstElementChild!;
      expect(hit).toHaveAttribute("data-hit");
      expect(hit).toHaveAttribute("fill", "transparent");
      expect(Number(hit.getAttribute("r"))).toBeGreaterThanOrEqual(least - 1e-9);
    }
  });

  it("a fine pointer draws no hit circle", () => {
    const { container } = render(<GraphCanvas {...props()} />);
    expect(container.querySelector("[data-hit]")).toBeNull();
  });

  it("two pointers pinch the zoom around their midpoint", () => {
    // Spread out, so that the fitted view is zoomed out far enough for a doubling to stay within the clamp.
    for (const [id, p] of layout.positions) layout.positions.set(id, { x: p.x * 4, y: p.y * 4 });
    render(<GraphCanvas {...props()} />);
    const svg = screen.getByRole("group");
    fireEvent.pointerDown(svg, { pointerId: 1, clientX: 200, clientY: 280 });
    fireEvent.pointerDown(svg, { pointerId: 2, clientX: 360, clientY: 280 });
    const before = viewBox();
    const mid = graphAt(360, 280);
    fireEvent.pointerMove(svg, { pointerId: 2, clientX: 520, clientY: 280 });
    const after = viewBox();
    expect(after[2]).toBeCloseTo(before[2] / 2);
    expect(after[3]).toBeCloseTo(before[3] / 2);
    const still = graphAt(360, 280);
    expect(Math.abs(still.x - mid.x)).toBeLessThanOrEqual(1);
    expect(Math.abs(still.y - mid.y)).toBeLessThanOrEqual(1);
  });

  it("a pinch starting on a node drags nothing", () => {
    render(<GraphCanvas {...props()} />);
    const svg = screen.getByRole("group");
    const button = nodeButton("Plan, 1 link in, 1 out");
    const transform = button.getAttribute("transform");
    const width = viewBox()[2];
    fireEvent.pointerDown(button, { pointerId: 1, clientX: 300, clientY: 280 });
    fireEvent.pointerDown(svg, { pointerId: 2, clientX: 100, clientY: 280 });
    fireEvent.pointerMove(svg, { pointerId: 1, clientX: 340, clientY: 280 });
    fireEvent.pointerMove(svg, { pointerId: 2, clientX: 60, clientY: 280 });
    fireEvent.pointerUp(svg, { pointerId: 1, clientX: 340, clientY: 280 });
    fireEvent.pointerUp(svg, { pointerId: 2, clientX: 60, clientY: 280 });
    expect(layout.drag).not.toHaveBeenCalled();
    expect(button.getAttribute("transform")).toBe(transform);
    // A pinch, zoomed in, rather than a pan by the last finger down.
    expect(viewBox()[2]).toBeLessThan(width);
  });

  it("a touch after a pinch whose releases went astray pans", () => {
    render(<GraphCanvas {...props()} />);
    const svg = screen.getByRole("group");
    fireEvent.pointerDown(svg, { pointerId: 1, isPrimary: true, clientX: 200, clientY: 280 });
    fireEvent.pointerDown(svg, { pointerId: 2, clientX: 360, clientY: 280 });
    const [x, y, w, h] = viewBox();
    fireEvent.pointerDown(svg, { pointerId: 3, isPrimary: true, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(svg, { pointerId: 3, clientX: 140, clientY: 100 });
    expect(viewBox()).toEqual([x - (40 * w) / 560, y, w, h]);
  });

  it("lifting the third finger pinches with the other two again", () => {
    // Spread out, so that the zoom has room either way.
    for (const [id, p] of layout.positions) layout.positions.set(id, { x: p.x * 4, y: p.y * 4 });
    render(<GraphCanvas {...props()} />);
    const svg = screen.getByRole("group");
    fireEvent.pointerDown(svg, { pointerId: 1, isPrimary: true, clientX: 200, clientY: 280 });
    fireEvent.pointerDown(svg, { pointerId: 2, clientX: 360, clientY: 280 });
    fireEvent.pointerDown(svg, { pointerId: 3, clientX: 280, clientY: 400 });
    fireEvent.pointerUp(svg, { pointerId: 3, clientX: 280, clientY: 400 });
    const before = viewBox();
    fireEvent.pointerMove(svg, { pointerId: 2, clientX: 520, clientY: 280 });
    expect(viewBox()[2]).toBeCloseTo(before[2] / 2);
  });

  it("a double tap on the background fits the view again", () => {
    render(<GraphCanvas {...props()} />);
    const svg = screen.getByRole("group");
    const fitted = svg.getAttribute("viewBox");
    fireEvent.wheel(svg, { deltaY: 200, clientX: 280, clientY: 280 });
    expect(svg.getAttribute("viewBox")).not.toBe(fitted);
    fireEvent.click(svg, { detail: 1 });
    expect(svg.getAttribute("viewBox")).not.toBe(fitted);
    fireEvent.click(svg, { detail: 2 });
    expect(svg.getAttribute("viewBox")).toBe(fitted);
  });
});

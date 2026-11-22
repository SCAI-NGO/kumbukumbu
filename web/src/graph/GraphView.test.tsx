// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { useEditor } from "../state/editor";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import type { LocalGraphView } from "./api";
import type { GNode } from "./model";
import { CARD_HEIGHT } from "./NodeCard";

// The canvas has its own tests; here it is a button per node that selects it when `select` is set, else opens it, and
// is pressed while selected. A press on it goes no further, as on the canvas's nodes.
vi.mock("./GraphCanvas", () => ({
  default: (props: { nodes: GNode[]; select?: boolean; selectedKey?: string | null; onOpen(n: GNode, beside: boolean): void; onSelect?(n: GNode | null): void }) => (
    <div data-testid="canvas">
      {props.nodes.map((n) => (
        <button
          key={n.id}
          type="button"
          aria-pressed={props.select ? n.key === props.selectedKey : undefined}
          onPointerDown={(e) => e.stopPropagation()}
          onClick={() => (props.select ? props.onSelect?.(n) : props.onOpen(n, false))}
        >
          node {n.title}
        </button>
      ))}
    </div>
  ),
}));

const { default: GraphView } = await import("./GraphView");

let fromSm = false;
let wide = false;
beforeEach(() => {
  fromSm = false;
  wide = false;
  vi.stubGlobal("matchMedia", (query: string) => ({
    get matches() {
      return (query === "(min-width: 40rem)" && (fromSm || wide)) || (query === "(min-width: 80rem)" && wide);
    },
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  localStorage.clear();
  useEditor.getState().reset();
});

const BUDGET = "/Work/Budget.md";
const PLAN = "/Work/Plan.md";
const node = (path: string | null, title: string, depth: number, kind = "note") => ({ path, title, kind, tags: [], archived: false, in: 1, out: 1, depth });
const graph: LocalGraphView = {
  nodes: [node(BUDGET, "Budget", 0), node(PLAN, "Plan", 1), node(null, "Anna", 1, "ghost")],
  edges: [
    [0, 1, 0],
    [0, 2, 0],
  ],
  leftOut: {},
};
const treeNote = (path: string) => ({ name: path.split("/").pop(), path, kind: "note", size: 1, writtenAt: null });
const tree = { root: { path: "/Work", name: "Work", notes: [BUDGET, PLAN].map(treeNote), folders: [] } };
const detail = { node: node(PLAN, "Plan", 1), changed: null, created: null, createdAtLeast: false, firstParagraph: "The plan for the year." };

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.pathname + location.search}</output>;
}

function show() {
  fakeApi({
    // Ghosts left out when asked to, as the server does.
    "GET /api/graph/local": (r) =>
      json(200, new URL(r.url).searchParams.get("ghosts") === "false" ? { ...graph, nodes: graph.nodes.slice(0, 2), edges: graph.edges.slice(0, 1) } : graph),
    "GET /api/graph/node": () => json(200, detail),
    "GET /api/vaults/Work/tree": () => json(200, tree),
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onOpened = vi.fn();
  const ui = (path = BUDGET) => (
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/notes/Work/Budget.md"]}>
        <GraphView path={path} otherPath={null} running mode="dialog" onOpened={onOpened} />
        <Where />
      </MemoryRouter>
    </QueryClientProvider>
  );
  const view = render(ui());
  return { view, ui, onOpened };
}

describe("GraphView in the dialog", () => {
  it("in the dialog a tap selects and shows the card, Open opens and reports", async () => {
    const { onOpened } = show();
    await userEvent.click(await screen.findByRole("button", { name: "node Plan" }));
    expect(screen.getByRole("group", { name: "Plan" })).toBeInTheDocument();
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Budget.md");
    expect(screen.queryByRole("button", { name: "Open beside" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Open" }));
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Plan.md");
    expect(onOpened).toHaveBeenCalledTimes(1);
  });

  it("Open beside opens in the right pane", async () => {
    fromSm = true;
    useEditor.getState().openNote(BUDGET);
    const { onOpened } = show();
    await userEvent.click(await screen.findByRole("button", { name: "node Plan" }));
    await userEvent.click(screen.getByRole("button", { name: "Open beside" }));
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Budget.md?beside=Work/Plan.md");
    expect(onOpened).toHaveBeenCalledTimes(1);
  });

  it("a second tap on the selected node opens it", async () => {
    const { onOpened } = show();
    await userEvent.click(await screen.findByRole("button", { name: "node Plan" }));
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Budget.md");
    await userEvent.click(screen.getByRole("button", { name: "node Plan" }));
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Plan.md");
    expect(onOpened).toHaveBeenCalledTimes(1);
  });

  it("where the split view fits, a second tap opens it beside", async () => {
    wide = true;
    useEditor.getState().openNote(BUDGET);
    const { onOpened } = show();
    await userEvent.click(await screen.findByRole("button", { name: "node Plan" }));
    await userEvent.click(screen.getByRole("button", { name: "node Plan" }));
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Budget.md?beside=Work/Plan.md");
    expect(onOpened).toHaveBeenCalledTimes(1);
  });

  it("a second tap on a selected ghost opens nothing", async () => {
    const { onOpened } = show();
    await userEvent.click(await screen.findByRole("button", { name: "node Anna" }));
    await userEvent.click(screen.getByRole("button", { name: "node Anna" }));
    expect(screen.getByRole("group", { name: "Anna" })).toBeInTheDocument();
    expect(onOpened).not.toHaveBeenCalled();
  });

  it("a tap on another node moves the selection without opening", async () => {
    const { onOpened } = show();
    await userEvent.click(await screen.findByRole("button", { name: "node Plan" }));
    await userEvent.click(screen.getByRole("button", { name: "node Anna" }));
    expect(screen.getByRole("group", { name: "Anna" })).toBeInTheDocument();
    expect(onOpened).not.toHaveBeenCalled();
  });

  it("a new note clears the selection", async () => {
    const { view, ui } = show();
    await userEvent.click(await screen.findByRole("button", { name: "node Plan" }));
    expect(screen.getByRole("group", { name: "Plan" })).toBeInTheDocument();
    view.rerender(ui(PLAN));
    expect(screen.queryByRole("group", { name: "Plan" })).toBeNull();
  });

  it("below sm the options sit behind Options", async () => {
    show();
    await screen.findByTestId("canvas");
    expect(screen.getByRole("button", { name: "Depth 1" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "List" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Fit" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Day notes" })).toBeNull();
    expect(screen.queryByRole("combobox", { name: "Size by" })).toBeNull();
    const options = screen.getByRole("button", { name: "Options" });
    expect(options).toHaveAttribute("aria-expanded", "false");
    await userEvent.click(options);
    expect(options).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("button", { name: "Day notes" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Size by" })).toBeInTheDocument();
    expect(document.getElementById(options.getAttribute("aria-controls")!)).toContainElement(screen.getByRole("button", { name: "Day notes" }));
  });

  it("from sm up the options sit in the row", async () => {
    fromSm = true;
    show();
    await screen.findByTestId("canvas");
    expect(screen.getByRole("button", { name: "Day notes" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Options" })).toBeNull();
  });

  it("a ghost shows its card without buttons", async () => {
    show();
    await userEvent.click(await screen.findByRole("button", { name: "node Anna" }));
    const card = screen.getByRole("group", { name: "Anna" });
    expect(card).toHaveTextContent("No note yet");
    expect(screen.queryByRole("button", { name: "Open" })).toBeNull();
  });

  it("Escape clears the selection and does not reach the document", async () => {
    show();
    const plan = await screen.findByRole("button", { name: "node Plan" });
    await userEvent.click(plan);
    const reached = vi.fn();
    document.addEventListener("keydown", reached);
    try {
      plan.focus();
      await userEvent.keyboard("{Escape}");
      expect(screen.queryByRole("group", { name: "Plan" })).toBeNull();
      expect(reached).not.toHaveBeenCalled();
      // With nothing selected Escape passes through, for the dialog to close.
      await userEvent.keyboard("{Escape}");
      expect(reached).toHaveBeenCalledTimes(1);
    } finally {
      document.removeEventListener("keydown", reached);
    }
  });

  it("drops the selection when its node leaves the graph", async () => {
    show();
    await userEvent.click(await screen.findByRole("button", { name: "node Anna" }));
    expect(screen.getByRole("group", { name: "Anna" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Options" }));
    await userEvent.click(screen.getByRole("button", { name: "Ghosts" }));
    await waitFor(() => expect(screen.queryByRole("button", { name: "node Anna" })).toBeNull());
    expect(screen.queryByRole("group", { name: "Anna" })).toBeNull();
    const reached = vi.fn();
    document.addEventListener("keydown", reached);
    try {
      screen.getByRole("button", { name: "node Plan" }).focus();
      await userEvent.keyboard("{Escape}");
      expect(reached).toHaveBeenCalledTimes(1);
    } finally {
      document.removeEventListener("keydown", reached);
    }
  });

  // The body the cards are placed in, 600 by 400 at the window's corner.
  const sized = async () => {
    const canvas = await screen.findByTestId("canvas");
    vi.spyOn(canvas.parentElement!, "getBoundingClientRect").mockReturnValue({ left: 0, top: 0, width: 600, height: 400, right: 600, bottom: 400, x: 0, y: 0, toJSON: () => ({}) });
  };

  it("below sm the card sits at the body's foot, as wide as the body", async () => {
    show();
    await sized();
    const plan = screen.getByRole("button", { name: "node Plan" });
    fireEvent.pointerDown(plan, { clientX: 100, clientY: 50 });
    fireEvent.click(plan);
    const card = screen.getByRole("group", { name: "Plan" });
    expect(card).toHaveClass("inset-x-3", "bottom-3");
    expect(card.style.left).toBe("");
    expect(card.style.top).toBe("");
    expect(card.style.width).toBe("");
  });

  it("from sm the card sits at the node the tap was on", async () => {
    fromSm = true;
    show();
    await sized();
    const plan = screen.getByRole("button", { name: "node Plan" });
    fireEvent.pointerDown(plan, { clientX: 100, clientY: 50 });
    fireEvent.click(plan);
    const card = screen.getByRole("group", { name: "Plan" });
    expect(card.style.left).toBe("112px");
    expect(card.style.top).toBe("62px");
  });

  it("from sm a node near the foot gives a card that stays inside the body", async () => {
    fromSm = true;
    show();
    await sized();
    const plan = screen.getByRole("button", { name: "node Plan" });
    fireEvent.pointerDown(plan, { clientX: 100, clientY: 390 });
    fireEvent.click(plan);
    const top = parseFloat(screen.getByRole("group", { name: "Plan" }).style.top);
    expect(top).toBeGreaterThanOrEqual(12);
    expect(top + CARD_HEIGHT).toBeLessThanOrEqual(400 - 12);
  });

  it("gives the focus back to the selected node when Escape closes the card from inside", async () => {
    show();
    const plan = await screen.findByRole("button", { name: "node Plan" });
    await userEvent.click(plan);
    screen.getByRole("button", { name: "Open" }).focus();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("group", { name: "Plan" })).toBeNull();
    expect(plan).toHaveFocus();
  });
});

// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { useEditor } from "../state/editor";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import type { LocalGraphView } from "./api";
import type { GNode } from "./model";

// The canvas has its own tests; here it is a button per node that selects it when `select` is set, else opens it.
vi.mock("./GraphCanvas", () => ({
  default: (props: { nodes: GNode[]; select?: boolean; onOpen(n: GNode, beside: boolean): void; onSelect?(n: GNode | null): void }) => (
    <div data-testid="canvas">
      {props.nodes.map((n) => (
        <button key={n.id} type="button" onClick={() => (props.select ? props.onSelect?.(n) : props.onOpen(n, false))}>
          node {n.title}
        </button>
      ))}
    </div>
  ),
}));

const { default: GraphView } = await import("./GraphView");

let fromSm = false;
beforeEach(() => {
  fromSm = false;
  vi.stubGlobal("matchMedia", (query: string) => ({
    get matches() {
      return query === "(min-width: 40rem)" && fromSm;
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
    "GET /api/graph/local": () => json(200, graph),
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
});

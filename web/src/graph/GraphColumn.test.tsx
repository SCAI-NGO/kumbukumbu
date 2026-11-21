// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import type { LocalGraphView } from "./api";
import type { GNode } from "./model";

// The canvas has its own tests; here it is a button per node that opens it, and shows whether it runs.
const canvas = vi.hoisted(() => ({ running: null as boolean | null }));
vi.mock("./GraphCanvas", () => ({
  default: (props: { nodes: GNode[]; running: boolean; onOpen(n: GNode, beside: boolean): void }) => {
    canvas.running = props.running;
    return (
      <div data-testid="canvas">
        {props.nodes.map((n) => (
          <button key={n.id} type="button" onClick={(e) => props.onOpen(n, e.ctrlKey)}>
            node {n.title}
          </button>
        ))}
      </div>
    );
  },
}));

const { default: GraphColumn } = await import("./GraphColumn");

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const BUDGET = "/Work/Budget.md";
const PLAN = "/Work/Plan.md";
const node = (path: string | null, title: string, depth: number, kind = "note") => ({ path, title, kind, tags: [], archived: false, in: 1, out: 1, depth });
const graph: LocalGraphView = {
  nodes: [node(BUDGET, "Budget", 0), node(PLAN, "Plan", 1), node("/Work/Alpha.md", "Alpha", 2), node(null, "Anna", 1, "ghost"), node("/Work/scan.png", "scan.png", 1, "attachment")],
  edges: [
    [0, 1, 0],
    [0, 3, 0],
    [0, 4, 0],
    [1, 2, 0],
  ],
  leftOut: {},
};
const orphan: LocalGraphView = { nodes: [node(BUDGET, "Budget", 0)], edges: [], leftOut: {} };
const tree = { root: { path: "/Work", name: "Work", notes: [], folders: [] } };

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.pathname}</output>;
}

function show({ local = (): Response | Promise<Response> => json(200, graph), paths, otherPath = null, hidden = false }: { local?: () => Response | Promise<Response>; paths?: () => Response; otherPath?: string | null; hidden?: boolean } = {}) {
  const fetch = fakeApi({
    "GET /api/graph/local": local,
    "GET /api/vaults/Work/tree": () => json(200, tree),
    ...(paths ? { "GET /api/graph/paths": paths } : {}),
  });
  const ui = () => (
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={["/notes/Work/Budget.md"]}>
        <GraphColumn path={BUDGET} otherPath={otherPath} hidden={hidden} />
        <Where />
      </MemoryRouter>
    </QueryClientProvider>
  );
  const view = render(ui());
  return { fetch, view, ui };
}

const asked = (fetch: ReturnType<typeof fakeApi>, pathname: string) =>
  fetch.mock.calls.map(([r]) => new URL(r.url)).filter((u) => u.pathname === pathname);

describe("GraphColumn", () => {
  it("says it is drawing until the first answer is in", () => {
    show({ local: () => new Promise<Response>(() => {}) });
    expect(screen.getByRole("complementary", { name: "Graph" })).toHaveTextContent("Drawing the graph…");
  });

  it("draws the graph, says the similarity isn't measured yet, and runs while shown", async () => {
    show();
    expect(await screen.findByText("Similarity not measured yet")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "node Plan" })).toBeInTheDocument();
    expect(canvas.running).toBe(true);
  });

  it("stops the simulation while hidden", async () => {
    show({ hidden: true });
    await waitFor(() => expect(canvas.running).toBe(false));
  });

  it("says what went wrong, and asks again on Try again", async () => {
    const { fetch } = show({ local: () => json(404, { error: "There is no note at that path." }) });
    expect(await screen.findByText("There is no note at that path.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    await waitFor(() => expect(asked(fetch, "/api/graph/local")).toHaveLength(2));
  });

  it("says it couldn't draw the graph when the server gives no reason", async () => {
    show({ local: () => new Response(null, { status: 500 }) });
    expect(await screen.findByText("Couldn't draw the graph.")).toBeInTheDocument();
  });

  it("asks for depth 2 with its hub cap", async () => {
    const { fetch } = show();
    await screen.findByText("Similarity not measured yet");
    await userEvent.click(screen.getByRole("button", { name: "Depth 2" }));
    await waitFor(() => expect(asked(fetch, "/api/graph/local").at(-1)?.searchParams.get("cap")).toBe("8"));
    expect(asked(fetch, "/api/graph/local").at(-1)?.searchParams.get("depth")).toBe("2");
    expect(screen.getByRole("button", { name: "Depth 2" })).toHaveAttribute("aria-pressed", "true");
  });

  it("sizes the nodes anew without asking the server again", async () => {
    const { fetch } = show();
    await screen.findByText("Similarity not measured yet");
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Size by" }), "length");
    expect(screen.getByRole("combobox", { name: "Size by" })).toHaveValue("length");
    expect(asked(fetch, "/api/graph/local")).toHaveLength(1);
  });

  it("hides day notes on the toggle, and remembers it", async () => {
    const { fetch, view, ui } = show();
    await screen.findByText("Similarity not measured yet");
    const toggle = screen.getByRole("button", { name: "Day notes" });
    expect(toggle).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(toggle);
    await waitFor(() => expect(asked(fetch, "/api/graph/local").at(-1)?.searchParams.get("days")).toBe("false"));
    view.unmount();
    render(ui());
    expect(screen.getByRole("button", { name: "Day notes" })).toHaveAttribute("aria-pressed", "false");
  });

  it("says how far the other pane's note is", async () => {
    const paths = { nodes: graph.nodes.slice(0, 3), edges: [[0, 1, 0], [1, 2, 0]], paths: [[0, 1]], more: false, why: "none" };
    show({ otherPath: "/Work/Alpha.md", paths: () => json(200, paths) });
    expect(await screen.findByText("Other pane: 2 steps away")).toBeInTheDocument();
  });

  it("says the other pane's note is one step away", async () => {
    const paths = { nodes: graph.nodes.slice(0, 2), edges: [[0, 1, 0]], paths: [[0]], more: false, why: "none" };
    show({ otherPath: PLAN, paths: () => json(200, paths) });
    expect(await screen.findByText("Other pane: 1 step away")).toBeInTheDocument();
  });

  it("says when the other pane's note isn't connected, or too far", async () => {
    show({ otherPath: "/Work/Far.md", paths: () => json(200, { nodes: [], edges: [], paths: [], more: false, why: "not_connected" }) });
    expect(await screen.findByText("Other pane: not connected")).toBeInTheDocument();
    cleanup();
    show({ otherPath: "/Work/Far.md", paths: () => json(200, { nodes: [], edges: [], paths: [], more: false, why: "farther_than_limit" }) });
    expect(await screen.findByText("Other pane: more than 6 steps away")).toBeInTheDocument();
  });

  it("says the other pane is in another vault without asking", async () => {
    const { fetch } = show({ otherPath: "/Home/List.md" });
    expect(await screen.findByText("Other pane: in another vault")).toBeInTheDocument();
    expect(asked(fetch, "/api/graph/paths")).toHaveLength(0);
  });

  it("opens an attachment in its window", async () => {
    show();
    await userEvent.click(await screen.findByRole("button", { name: "node scan.png" }));
    expect(screen.getByTestId("where")).toHaveTextContent("/view/Work/scan.png");
  });

  it("opens a note in the pane", async () => {
    show();
    await userEvent.click(await screen.findByRole("button", { name: "node Plan" }));
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Plan.md");
  });

  it("lists the neighbours by depth, then title, a ghost as text", async () => {
    show();
    await screen.findByText("Similarity not measured yet");
    await userEvent.click(screen.getByRole("button", { name: "List" }));
    const list = screen.getByRole("list", { name: "Neighbours" });
    expect(within(list).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["Anna, no note yet", "Plan", "scan.png", "Alpha"]);
    expect(within(list).queryByRole("button", { name: /Anna/ })).toBeNull();
    await userEvent.click(within(list).getByRole("button", { name: "Plan" }));
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Plan.md");
  });

  it("says there are no links yet for an orphan, and no similarity line", async () => {
    show({ local: () => json(200, orphan) });
    await userEvent.click(await screen.findByRole("button", { name: "List" }));
    expect(await screen.findByText("No links yet")).toBeInTheDocument();
    expect(screen.queryByText("Similarity not measured yet")).toBeNull();
  });
});

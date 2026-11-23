// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { NO_SUCH_NOTE } from "../discreet/errorText";
import { useEditor } from "../state/editor";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import type { GraphSimilarityView, LocalGraphView } from "./api";
import type { GNode } from "./model";
import type { Suggestion } from "./suggestions";

// The canvas has its own tests; here it is a button per node that opens it and is hovered at once, a button per
// suggestion, and shows whether it runs.
const canvas = vi.hoisted(() => ({ running: null as boolean | null }));
vi.mock("./GraphCanvas", () => ({
  default: (props: {
    nodes: GNode[];
    suggestions: Suggestion[];
    showSuggestions: boolean;
    running: boolean;
    onOpen(n: GNode, beside: boolean): void;
    onHover(n: GNode | null): void;
    onSuggestion(s: Suggestion): void;
  }) => {
    canvas.running = props.running;
    return (
      <div data-testid="canvas">
        {props.nodes.map((n) => (
          <button key={n.id} type="button" onClick={(e) => props.onOpen(n, e.ctrlKey)} onMouseEnter={() => props.onHover(n)} onMouseLeave={() => props.onHover(null)}>
            node {n.title}
          </button>
        ))}
        {props.showSuggestions &&
          props.suggestions.map((s) => (
            <button key={s.path} type="button" onClick={() => props.onSuggestion(s)}>
              suggestion {s.title}
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
  vi.unstubAllEnvs();
  localStorage.clear();
  useEditor.getState().reset();
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
const treeNote = (path: string) => ({ name: path.split("/").pop(), path, kind: "note", size: 1, writtenAt: null });
const tree = { root: { path: "/Work", name: "Work", notes: [BUDGET, PLAN, "/Work/Alpha.md", "/Work/Elsewhere.md"].map(treeNote), folders: [] } };
const detail = { node: node(PLAN, "Plan", 1), changed: null, created: null, createdAtLeast: false, firstParagraph: "The plan for the year." };

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.pathname + location.search}</output>;
}

// Budget–Plan is measured, Plan–Alpha not yet; the ghost and the attachment have no similarity.
const measured: GraphSimilarityView = { state: "ready", reason: null, pairs: [{ from: BUDGET, to: PLAN, score: 0.4 }, { from: PLAN, to: "/Work/Alpha.md", score: null }] };

function show({
  local = (): Response | Promise<Response> => json(200, graph),
  similarity = (): Response => json(200, measured),
  paths,
  otherPath = null,
  hidden = false,
}: { local?: () => Response | Promise<Response>; similarity?: () => Response; paths?: () => Response; otherPath?: string | null; hidden?: boolean } = {}) {
  const fetch = fakeApi({
    "GET /api/graph/local": local,
    "GET /api/graph/similarity": similarity,
    "GET /api/graph/node": () => json(200, detail),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    ...(paths ? { "GET /api/graph/paths": paths } : {}),
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const ui = (path = BUDGET, collapsed = hidden) => (
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/notes/Work/Budget.md"]}>
        <GraphColumn path={path} otherPath={otherPath} hidden={collapsed} />
        <Where />
      </MemoryRouter>
    </QueryClientProvider>
  );
  const view = render(ui());
  return { fetch, view, ui, client };
}

const asked = (fetch: ReturnType<typeof fakeApi>, pathname: string) =>
  fetch.mock.calls.map(([r]) => new URL(r.url)).filter((u) => u.pathname === pathname);

describe("GraphColumn", () => {
  it("says it is drawing until the first answer is in", () => {
    show({ local: () => new Promise<Response>(() => {}) });
    expect(screen.getByRole("complementary", { name: "Graph" })).toHaveTextContent("Drawing the graph…");
  });

  it("draws the graph, says how many links have their similarity measured, and runs while shown", async () => {
    show();
    expect(await screen.findByText("Similarity measured for 1 of 2 links")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "node Plan" })).toBeInTheDocument();
    expect(canvas.running).toBe(true);
  });

  it("stops the simulation while hidden", async () => {
    const { view, ui } = show();
    await screen.findByTestId("canvas");
    view.rerender(ui(BUDGET, true));
    await waitFor(() => expect(canvas.running).toBe(false));
  });

  it("says what went wrong, and asks again on Try again", async () => {
    const { fetch } = show({ local: () => json(503, { error: "This vault is being re-encrypted. Try again in a moment." }) });
    expect(await screen.findByText("This vault is being re-encrypted. Try again in a moment.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    await waitFor(() => expect(asked(fetch, "/api/graph/local")).toHaveLength(2));
  });

  // The app's rule for a vault it doesn't name, or a discreet one: a 404 is said without its path.
  it("says a missing note without its path", async () => {
    show({ local: () => json(404, { error: "There is no note at /Work/Budget.md." }) });
    expect(await screen.findByText(NO_SUCH_NOTE)).toBeInTheDocument();
    expect(screen.getByRole("complementary", { name: "Graph" })).not.toHaveTextContent("/Work/Budget.md");
  });

  it("asks nothing of the graph while collapsed, and shows the last graph at once when expanded", async () => {
    const { fetch, view, ui, client } = show({ otherPath: "/Work/Alpha.md", paths: () => json(200, { paths: [], why: "not_connected" }) });
    await screen.findByText("Other pane: not connected");
    view.rerender(ui(BUDGET, true));
    await act(() => client.invalidateQueries({ queryKey: ["graph"] }));
    expect(asked(fetch, "/api/graph/local")).toHaveLength(1);
    expect(asked(fetch, "/api/graph/paths")).toHaveLength(1);
    view.rerender(ui(BUDGET, false));
    expect(screen.getByTestId("canvas")).toBeInTheDocument();
  });

  it("asks nothing when it starts collapsed", async () => {
    const { fetch } = show({ hidden: true, otherPath: "/Work/Alpha.md", paths: () => json(200, { paths: [], why: "not_connected" }) });
    await waitFor(() => expect(asked(fetch, "/api/vaults/Work/tree")).toHaveLength(1));
    expect(asked(fetch, "/api/graph/local")).toHaveLength(0);
    expect(asked(fetch, "/api/graph/paths")).toHaveLength(0);
  });

  it("names its toolbar apart from the column", () => {
    show({ local: () => new Promise<Response>(() => {}) });
    expect(screen.getByRole("toolbar", { name: "Graph options" })).toBeInTheDocument();
  });

  it("says it couldn't draw the graph when the server gives no reason", async () => {
    show({ local: () => new Response(null, { status: 500 }) });
    expect(await screen.findByText("Couldn't draw the graph.")).toBeInTheDocument();
  });

  it("asks for depth 2 with its hub cap", async () => {
    const { fetch } = show();
    await screen.findByText("Similarity measured for 1 of 2 links");
    await userEvent.click(screen.getByRole("button", { name: "Depth 2" }));
    await waitFor(() => expect(asked(fetch, "/api/graph/local").at(-1)?.searchParams.get("cap")).toBe("8"));
    expect(asked(fetch, "/api/graph/local").at(-1)?.searchParams.get("depth")).toBe("2");
    expect(screen.getByRole("button", { name: "Depth 2" })).toHaveAttribute("aria-pressed", "true");
  });

  it("sizes the nodes anew without asking the server again", async () => {
    const { fetch } = show();
    await screen.findByText("Similarity measured for 1 of 2 links");
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Size by" }), "length");
    expect(screen.getByRole("combobox", { name: "Size by" })).toHaveValue("length");
    expect(asked(fetch, "/api/graph/local")).toHaveLength(1);
  });

  it("hides day notes on the toggle, and remembers it", async () => {
    const { fetch, view, ui } = show();
    await screen.findByText("Similarity measured for 1 of 2 links");
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
    await screen.findByText("Similarity measured for 1 of 2 links");
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
    expect(screen.queryByText("Similarity measured for 1 of 2 links")).toBeNull();
  });

  it("opens a note beside on Ctrl+click", async () => {
    useEditor.getState().openNote(BUDGET);
    show();
    const user = userEvent.setup();
    const plan = await screen.findByRole("button", { name: "node Plan" });
    await user.keyboard("{Control>}");
    await user.click(plan);
    await user.keyboard("{/Control}");
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Budget.md?beside=Work/Plan.md");
  });

  it("opens a note from the list beside on Cmd+click", async () => {
    useEditor.getState().openNote(BUDGET);
    show();
    const user = userEvent.setup();
    await screen.findByText("Similarity measured for 1 of 2 links");
    await user.click(screen.getByRole("button", { name: "List" }));
    const plan = within(screen.getByRole("list", { name: "Neighbours" })).getByRole("button", { name: "Plan" });
    await user.keyboard("{Meta>}");
    await user.click(plan);
    await user.keyboard("{/Meta}");
    expect(screen.getByTestId("where")).toHaveTextContent("/notes/Work/Budget.md?beside=Work/Plan.md");
  });

  it("opens an attachment from the list in its window", async () => {
    show();
    await screen.findByText("Similarity measured for 1 of 2 links");
    await userEvent.click(screen.getByRole("button", { name: "List" }));
    await userEvent.click(within(screen.getByRole("list", { name: "Neighbours" })).getByRole("button", { name: "scan.png" }));
    expect(screen.getByTestId("where")).toHaveTextContent("/view/Work/scan.png");
  });

  it("says it is drawing while a note outside the last graph is asked for, not the old graph", async () => {
    let calls = 0;
    const { view, ui } = show({ local: () => (++calls === 1 ? json(200, graph) : new Promise<Response>(() => {})) });
    await screen.findByText("Similarity measured for 1 of 2 links");
    view.rerender(ui("/Work/Other.md"));
    const column = screen.getByRole("complementary", { name: "Graph" });
    await waitFor(() => expect(column).toHaveTextContent("Drawing the graph…"));
    expect(screen.queryByTestId("canvas")).toBeNull();
    expect(screen.queryByText("Similarity measured for 1 of 2 links")).toBeNull();
  });

  it("keeps the old graph while a note in it is asked for", async () => {
    let calls = 0;
    const { view, ui } = show({ local: () => (++calls === 1 ? json(200, graph) : new Promise<Response>(() => {})) });
    await screen.findByText("Similarity measured for 1 of 2 links");
    view.rerender(ui(PLAN));
    await waitFor(() => expect(calls).toBe(2));
    expect(screen.getByTestId("canvas")).toBeInTheDocument();
  });

  it("keeps a drawn graph when asking again fails", async () => {
    let calls = 0;
    const { client } = show({ local: () => (++calls === 1 ? json(200, graph) : new Response(null, { status: 500 })) });
    await screen.findByText("Similarity measured for 1 of 2 links");
    await act(() => client.invalidateQueries({ queryKey: ["graph", "local"] }));
    await waitFor(() => expect(client.getQueryCache().findAll({ queryKey: ["graph", "local"] })[0].state.status).toBe("error"));
    expect(screen.getByRole("button", { name: "node Plan" })).toBeInTheDocument();
    expect(screen.queryByText("Couldn't draw the graph.")).toBeNull();
  });

  it("previews a hovered node, asking once for the same node", async () => {
    const { fetch } = show();
    const plan = await screen.findByRole("button", { name: "node Plan" });
    await userEvent.hover(plan);
    expect(await screen.findByRole("tooltip")).toHaveTextContent("The plan for the year.");
    await userEvent.unhover(plan);
    expect(screen.queryByRole("tooltip")).toBeNull();
    await userEvent.hover(plan);
    expect(screen.getByRole("tooltip")).toHaveTextContent("The plan for the year.");
    expect(asked(fetch, "/api/graph/node")).toHaveLength(1);
  });

  it("neither previews nor asks with the preview off", async () => {
    localStorage.setItem("kumbu.graphPrefs", JSON.stringify({ preview: false }));
    const { fetch } = show();
    await userEvent.hover(await screen.findByRole("button", { name: "node Plan" }));
    await new Promise((r) => setTimeout(r, 400));
    expect(screen.queryByRole("tooltip")).toBeNull();
    expect(asked(fetch, "/api/graph/node")).toHaveLength(0);
  });

  it("offers suggested links with the layer on, and opens a suggestion's card", async () => {
    localStorage.setItem("kumbu.graph.mockSimilarity", "1");
    localStorage.setItem("kumbu.graphPrefs", JSON.stringify({ suggestions: true }));
    show();
    await userEvent.click(await screen.findByRole("button", { name: "suggestion Elsewhere" }));
    expect(screen.getByRole("dialog", { name: "Elsewhere" })).toHaveTextContent("Similar in meaning, not linked");
    // Only notes not in the graph are suggested.
    expect(screen.queryByRole("button", { name: /^suggestion (Budget|Plan|Alpha)$/ })).toBeNull();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("says nothing of the similarity once every link between notes is measured", async () => {
    const all = { ...measured, pairs: measured.pairs.map((p) => ({ ...p, score: 0.5 })) };
    const { fetch } = show({ similarity: () => json(200, all) });
    await screen.findByRole("button", { name: "node Plan" });
    await waitFor(() => expect(asked(fetch, "/api/graph/similarity")).toHaveLength(1));
    // The similarity is asked for with the graph's own parameters.
    expect(Object.fromEntries(asked(fetch, "/api/graph/similarity")[0].searchParams)).toEqual(Object.fromEntries(asked(fetch, "/api/graph/local")[0].searchParams));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByText(/^Similarity/)).toBeNull();
  });

  it("says why the similarity isn't measured when the server can't", async () => {
    show({ similarity: () => json(200, { state: "off", reason: "search by meaning is off on this server", pairs: [] }) });
    expect(await screen.findByText("Similarity not measured: search by meaning is off on this server")).toBeInTheDocument();
  });

  it("hides the Suggestions switch in a production build, until link prediction gives it data", async () => {
    vi.stubEnv("DEV", false);
    localStorage.setItem("kumbu.graphPrefs", JSON.stringify({ suggestions: true }));
    show();
    await screen.findByRole("button", { name: "node Plan" });
    expect(screen.queryByRole("button", { name: "Suggestions" })).toBeNull();
    expect(screen.getByRole("button", { name: "Ghosts" })).toBeInTheDocument();
  });

  it("suggests nothing with the layer off", async () => {
    localStorage.setItem("kumbu.graph.mockSimilarity", "1");
    show();
    await screen.findByRole("button", { name: "node Plan" });
    expect(screen.queryByRole("button", { name: /^suggestion / })).toBeNull();
  });
});

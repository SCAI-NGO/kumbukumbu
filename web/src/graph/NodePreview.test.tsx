// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import type { GNode } from "./model";
import NodePreview from "./NodePreview";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const NOW = Date.parse("2026-11-21T12:00:00Z");
const budget: GNode = { id: "/Work/Budget.md", key: "/Work/Budget.md", path: "/Work/Budget.md", title: "Budget", kind: "note", tags: ["money", "q4"], in: 1, out: 2, depth: 1, size: null, writtenAt: null, leftOut: 0 };
const anna: GNode = { ...budget, id: "ghost:0", key: "ghost:Anna", path: null, title: "Anna", kind: "ghost", tags: [] };
const detail = {
  node: { path: budget.path, title: "Budget", kind: "note", tags: budget.tags, archived: false },
  changed: "2026-10-31T12:00:00Z",
  created: null,
  createdAtLeast: false,
  firstParagraph: "What we spend in the fourth quarter.",
};

function show(node: GNode) {
  const fetch = fakeApi({ "GET /api/graph/node": () => json(200, detail) });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <NodePreview node={node} anchor={{ x: 40, y: 60 }} />
    </QueryClientProvider>,
  );
  return fetch;
}

describe("NodePreview", () => {
  it("shows the title, the tags, the first paragraph and when it changed", async () => {
    vi.spyOn(Date, "now").mockReturnValue(NOW);
    const fetch = show(budget);
    const card = screen.getByRole("tooltip");
    expect(card).toHaveTextContent("Budget");
    expect(card).toHaveTextContent("#money");
    expect(card).toHaveTextContent("#q4");
    expect(await screen.findByText("What we spend in the fourth quarter.")).toBeInTheDocument();
    expect(card).toHaveTextContent("changed 3 weeks ago");
    expect(new URL(fetch.mock.calls[0][0].url).searchParams.get("path")).toBe("/Work/Budget.md");
  });

  it("says a ghost has no note yet, without asking", () => {
    const fetch = show(anna);
    const card = screen.getByRole("tooltip");
    expect(card).toHaveTextContent("Anna");
    expect(card).toHaveTextContent("No note yet");
    expect(fetch).not.toHaveBeenCalled();
  });
});

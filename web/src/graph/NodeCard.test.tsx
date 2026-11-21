// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import type { GNode } from "./model";
import NodeCard from "./NodeCard";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const plan: GNode = { id: "/Work/Plan.md", key: "/Work/Plan.md", path: "/Work/Plan.md", title: "Plan", kind: "note", tags: [], in: 1, out: 1, depth: 1, size: null, writtenAt: null, leftOut: 0 };
const anna: GNode = { ...plan, id: "ghost:0", key: "ghost:Anna", path: null, title: "Anna", kind: "ghost" };
const detail = { node: { path: plan.path, title: "Plan", kind: "note", tags: [], archived: false }, changed: null, created: null, createdAtLeast: false, firstParagraph: "The plan for the year." };

function show(node: GNode, beside: boolean) {
  const fetch = fakeApi({ "GET /api/graph/node": () => json(200, detail) });
  const onOpen = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <NodeCard node={node} anchor={{ x: 40, y: 60 }} width={560} beside={beside} onOpen={onOpen} />
    </QueryClientProvider>,
  );
  return { fetch, onOpen };
}

describe("NodeCard", () => {
  it("shows Open and Open beside", async () => {
    const { onOpen } = show(plan, true);
    const card = screen.getByRole("group", { name: "Plan" });
    expect(await screen.findByText("The plan for the year.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Open" }));
    expect(onOpen).toHaveBeenLastCalledWith(plan, false);
    await userEvent.click(screen.getByRole("button", { name: "Open beside" }));
    expect(onOpen).toHaveBeenLastCalledWith(plan, true);
    expect(card).toBeInTheDocument();
  });

  it("beside false shows Open only", () => {
    show(plan, false);
    expect(screen.getByRole("button", { name: "Open" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Open beside" })).toBeNull();
  });

  it("a ghost has no buttons and says No note yet", () => {
    const { fetch } = show(anna, true);
    expect(screen.getByRole("group", { name: "Anna" })).toHaveTextContent("No note yet");
    expect(screen.queryByRole("button")).toBeNull();
    expect(fetch).not.toHaveBeenCalled();
  });
});

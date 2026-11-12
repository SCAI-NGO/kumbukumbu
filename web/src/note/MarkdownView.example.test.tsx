// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import type { LinkView } from "./wikiLinks";
import MarkdownView from "./MarkdownView";

vi.mock("../reader/mermaidRender", () => ({
  renderDiagram: vi.fn(async () => ({ svg: "<svg viewBox='0 0 10 10'/>", alt: "Diagram (flowchart)" })),
}));

const RULING = "/Work/Pump/Ruling.md";
// The spec's example (§1): Plan's text as Alice keeps it.
const plan = [
  "# Plan",
  "",
  "The pump needs a new controller. ^controller",
  "",
  "```mermaid",
  "flowchart LR",
  "  Sensor --> Controller --> Pump",
  "```",
  "",
  "![[Ruling#Decision]]",
].join("\n");
const ruling = "# Ruling\n\n## Decision\n\nWe buy the controller.\n\n## Costs\n\nToo high.\n";

beforeEach(() => {
  // jsdom has no blob URLs.
  for (const name of ["createObjectURL", "revokeObjectURL"] as const) {
    if (!(name in URL)) Object.defineProperty(URL, name, { value: () => undefined, configurable: true, writable: true });
  }
  vi.spyOn(URL, "createObjectURL").mockImplementation(() => "blob:http://localhost/diagram");
  vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("the spec's example", () => {
  it("Plan shows the diagram, no block marker, and Ruling's Decision in its frame", async () => {
    fakeApi({
      [`GET /api/notes${RULING}`]: () => json(200, { path: RULING, content: ruling, version: "r1", lastWriter: "alice", lastWriteAt: "2026-11-12T09:00:00Z" }),
      [`GET /api/links${RULING}`]: () => json(200, []),
    });
    const links: LinkView[] = [{ line: 10, raw: "![[Ruling#Decision]]", kind: "note", path: RULING, target: "Ruling", candidates: [] }];
    const { container } = render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <MarkdownView source={plan} links={links} tasksDisabled={false} onToggleTask={vi.fn()} onCreateMissing={vi.fn()} path="/Work/Pump/Plan.md" />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByRole("img", { name: "Diagram (flowchart)" })).toBeInTheDocument();
    expect(screen.getByText("The pump needs a new controller.", { selector: "p" })).toBeInTheDocument();
    expect(container).not.toHaveTextContent("^controller");
    const frame = await screen.findByRole("region", { name: "Embedded: Ruling › Decision" });
    expect(await within(frame).findByText("We buy the controller.")).toBeInTheDocument();
    expect(frame).not.toHaveTextContent("Too high.");
  });
});

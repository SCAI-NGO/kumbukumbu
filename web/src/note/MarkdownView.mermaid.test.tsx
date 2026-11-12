// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { schemes } from "../theme/schemes";
import MarkdownView, { type Highlight } from "./MarkdownView";

// Counts the loads of the Mermaid module; this file has a module registry of its own, so none happened before.
const { loaded, renderDiagram } = vi.hoisted(() => ({
  loaded: vi.fn(),
  renderDiagram: vi.fn(async () => ({ svg: "<svg viewBox='0 0 10 10'/>", alt: "Diagram (flowchart)" })),
}));
vi.mock("../reader/mermaidRender", () => {
  loaded();
  return { renderDiagram };
});

const light = schemes.find((s) => s.light)!.id;
const dark = schemes.find((s) => !s.light)!.id;
const plan = "# Plan\n\n```mermaid\nflowchart LR\n  A --> B\n```\n";

const show = (source: string, { nested = false, highlight = null }: { nested?: boolean; highlight?: Highlight | null } = {}) =>
  render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <MarkdownView source={source} links={[]} tasksDisabled={false} onToggleTask={vi.fn()} onCreateMissing={vi.fn()} nested={nested} highlight={highlight} />
    </MemoryRouter>,
  );

beforeEach(() => {
  // jsdom has no blob URLs.
  for (const name of ["createObjectURL", "revokeObjectURL"] as const) {
    if (!(name in URL)) Object.defineProperty(URL, name, { value: () => undefined, configurable: true, writable: true });
  }
  vi.spyOn(URL, "createObjectURL").mockImplementation(() => "blob:http://localhost/diagram");
  vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined);
  renderDiagram.mockClear();
  document.documentElement.dataset.scheme = light;
});

afterEach(() => {
  vi.restoreAllMocks();
  delete document.documentElement.dataset.scheme;
});

describe("Mermaid in the note view", () => {
  // First: once a test has drawn a diagram, the module is loaded for the rest of the file.
  it("loads nothing for a note without a mermaid block", async () => {
    show("```js\nx\n```");
    await new Promise((r) => setTimeout(r, 100));
    expect(loaded).not.toHaveBeenCalled();
    expect(document.querySelector("pre")).toHaveTextContent("x");
  });

  it("draws a mermaid block as an image", async () => {
    show(plan);
    expect(await screen.findByRole("img", { name: "Diagram (flowchart)" })).toBeInTheDocument();
  });

  it("redraws on a change of data-scheme", async () => {
    show(plan);
    await waitFor(() => expect(renderDiagram).toHaveBeenCalledWith("flowchart LR\n  A --> B", false));
    await act(async () => {
      document.documentElement.dataset.scheme = dark;
    });
    await waitFor(() => expect(renderDiagram).toHaveBeenLastCalledWith("flowchart LR\n  A --> B", true));
  });

  it("an embedded note's mermaid block is drawn too", async () => {
    show(plan, { nested: true });
    expect(await screen.findByRole("img", { name: "Diagram (flowchart)" })).toBeInTheDocument();
  });

  it("keeps the agent's highlight on a diagram", async () => {
    show(plan, { highlight: { lines: "all", writer: "Claude" } });
    const figure = (await screen.findByRole("img", { name: "Diagram (flowchart)" })).closest("figure")!;
    expect(figure.parentElement).toHaveAttribute("data-changed");
    expect(figure.parentElement).toHaveAttribute("data-writer", "Claude");
  });
});

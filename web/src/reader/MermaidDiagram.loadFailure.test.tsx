// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";

it("a failed load says diagrams can't be drawn right now", async () => {
  vi.doMock("./mermaidRender", () => {
    throw new Error("Failed to fetch dynamically imported module");
  });
  const { default: MermaidDiagram } = await import("./MermaidDiagram");
  const source = "flowchart LR\n  Sensor --> Controller --> Pump";
  render(<MermaidDiagram source={source} dark={false} />);
  expect(await screen.findByRole("note")).toHaveTextContent("Diagrams can't be drawn right now.");
  const code = document.querySelector("code.language-mermaid")!;
  expect(code).toBeVisible();
  expect(code.textContent).toBe(source);
  expect(screen.queryByRole("img")).not.toBeInTheDocument();
});

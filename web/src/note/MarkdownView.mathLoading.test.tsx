// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import MarkdownView from "./MarkdownView";

// Counts the loads of the maths module; this file has a module registry of its own, so none happened before.
const loaded = vi.hoisted(() => vi.fn());
vi.mock("./math", async (importOriginal) => {
  loaded();
  return await importOriginal();
});

const show = (source: string) =>
  render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <MarkdownView source={source} links={[]} tasksDisabled={false} onToggleTask={vi.fn()} onCreateMissing={vi.fn()} />
    </MemoryRouter>,
  );

describe("loading maths", () => {
  it("loads nothing for a note with only currency in it, and KaTeX for one with a formula", async () => {
    show("Price per kilo: $3, or $5 for two.");
    await new Promise((r) => setTimeout(r, 100));
    expect(loaded).not.toHaveBeenCalled();
    expect(screen.getByText("Price per kilo: $3, or $5 for two.")).toBeInTheDocument();

    show("Bed area: $A = \\pi r^2$");
    await waitFor(() => expect(document.querySelector(".katex")).not.toBeNull());
    expect(loaded).toHaveBeenCalledTimes(1);
  });
});

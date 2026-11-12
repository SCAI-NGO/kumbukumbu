// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import type { LinkView } from "./wikiLinks";
import MarkdownView from "./MarkdownView";

const PLAN = "/Work/Pump/Plan.md";
const plan = "# Plan\n\nIntro.\n\nThe pump needs a new controller. ^controller\n\n- order it\n";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function show(source: string, links: LinkView[] = []) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <MarkdownView source={source} links={links} tasksDisabled={false} onToggleTask={vi.fn()} onCreateMissing={vi.fn()} path="/Work/Pump/Tasks.md" />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const embed = (raw: string): LinkView => ({ line: 1, raw, kind: "note", path: PLAN, target: "Plan", candidates: [] });

function planApi() {
  fakeApi({
    [`GET /api/notes${PLAN}`]: () => json(200, { path: PLAN, content: plan, version: "r1", lastWriter: "alice", lastWriteAt: "2026-11-12T09:00:00Z" }),
    [`GET /api/links${PLAN}`]: () => json(200, []),
  });
}

describe("Block ids in the note view", () => {
  it("hides block markers in the note view", () => {
    show("The pump needs a new controller. ^controller\n\n- order it ^order\n\n^list\n\n## Decision ^decision");
    const article = screen.getByText("The pump needs a new controller.").closest("div")!;
    expect(article).not.toHaveTextContent("^");
    expect(screen.getByRole("heading", { name: "Decision", level: 2 })).toBeInTheDocument();
    expect(screen.getByRole("listitem")).toHaveTextContent(/^order it$/);
  });

  it("an embed of a block shows that block", async () => {
    planApi();
    show("![[Plan#^controller]]", [embed("![[Plan#^controller]]")]);
    const frame = await screen.findByRole("region", { name: "Embedded: Plan › ^controller" });
    expect(await within(frame).findByText("The pump needs a new controller.")).toBeInTheDocument();
    expect(frame).not.toHaveTextContent("# Plan");
    expect(within(frame).queryByRole("heading")).toBeNull();
    expect(frame).not.toHaveTextContent("Intro.");
    expect(frame).not.toHaveTextContent("order it");
  });

  it("an embed of a nested item shows it as a list", async () => {
    fakeApi({
      [`GET /api/notes${PLAN}`]: () =>
        json(200, { path: PLAN, content: "- pump\n    - controller ^controller\n        - board\n- valve\n", version: "r1", lastWriter: "alice", lastWriteAt: "2026-11-12T09:00:00Z" }),
      [`GET /api/links${PLAN}`]: () => json(200, []),
    });
    show("![[Plan#^controller]]", [embed("![[Plan#^controller]]")]);
    const frame = await screen.findByRole("region", { name: "Embedded: Plan › ^controller" });
    const items = await within(frame).findAllByRole("listitem");
    expect(items.map((li) => li.firstChild?.textContent)).toEqual(["controller", "board"]);
    expect(frame.querySelector("pre")).toBeNull();
    expect(frame).not.toHaveTextContent("pump");
  });

  it("a link to a block carries the block in its state", async () => {
    function State() {
      return <p>state {JSON.stringify(useLocation().state)}</p>;
    }
    const link: LinkView = { line: 1, raw: "[[Plan#^controller]]", kind: "note", path: PLAN, target: "Plan", candidates: [] };
    render(
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={["/notes/Work/Pump/Tasks.md"]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <Routes>
            <Route path="/notes/Work/Pump/Plan.md" element={<State />} />
            <Route
              path="*"
              element={<MarkdownView source="See [[Plan#^controller]]." links={[link]} tasksDisabled onToggleTask={vi.fn()} onCreateMissing={vi.fn()} path="/Work/Pump/Tasks.md" />}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );
    await userEvent.click(screen.getByRole("link", { name: "Plan › ^controller" }));
    expect(await screen.findByText('state {"block":"controller"}')).toBeInTheDocument();
  });

  it("a link to a block of the note itself opens it there", async () => {
    function State() {
      return <p>state {JSON.stringify(useLocation().state)}</p>;
    }
    const link: LinkView = { line: 1, raw: "[[#^controller]]", kind: "self", path: PLAN, target: "", candidates: [] };
    render(
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={["/here"]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <Routes>
            <Route path="/notes/Work/Pump/Plan.md" element={<State />} />
            <Route path="*" element={<MarkdownView source="See [[#^controller]]." links={[link]} tasksDisabled onToggleTask={vi.fn()} onCreateMissing={vi.fn()} path={PLAN} />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );
    await userEvent.click(screen.getByRole("link"));
    expect(await screen.findByText('state {"block":"controller"}')).toBeInTheDocument();
  });

  it("an embed of a missing block says so", async () => {
    planApi();
    show("![[Plan#^contoller]]", [embed("![[Plan#^contoller]]")]);
    const frame = await screen.findByRole("region", { name: "Embedded: Plan › ^contoller" });
    expect(await within(frame).findByText("This note has no block ^contoller.")).toBeInTheDocument();
  });
});

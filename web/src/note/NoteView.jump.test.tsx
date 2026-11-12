// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { NoteAt } from "../api/paths";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import MarkdownView from "./MarkdownView";

const PLAN = "/Work/Pump/Plan.md";
const planUrl = "/notes/Work/Pump/Plan.md";
// Line 10 is the second line of the paragraph from line 9: no element starts there.
const plan = ["# Plan", "", "Intro.", "", "The pump needs a new controller. ^controller", "", "- order it", "", "The valve stays.", "Bob checked it in May.", "", "Done."].join("\n");

const tree = {
  damaged: 0,
  root: { name: "Work", path: "/Work", folders: [{ name: "Pump", path: "/Work/Pump", folders: [], notes: [{ name: "Plan.md", path: PLAN, size: 1 }] }], notes: [] },
};

function planApi(content = plan) {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1 }]),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    [`GET /api/notes${PLAN}`]: () => json(200, { path: PLAN, content, version: "v1", lastWriter: "alice", lastWriteAt: "2026-11-12T09:00:00Z" }),
    [`GET /api/links${PLAN}`]: () => json(200, []),
    [`GET /api/backlinks${PLAN}`]: () => json(200, []),
    "GET /api/changes": () => json(200, []),
  });
}

const scrollIntoView = vi.fn();
beforeEach(() => {
  Element.prototype.scrollIntoView = scrollIntoView;
  scrollIntoView.mockClear();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const scrolled = () => scrollIntoView.mock.contexts as Element[];

describe("opening a note at a block or a line", () => {
  it("a link to a block scrolls to it and highlights it", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    planApi();
    renderApp({ pathname: planUrl, state: { block: "controller" } satisfies NoteAt });
    const block = await screen.findByText("The pump needs a new controller.", { selector: "p" });
    await vi.waitFor(() => expect(scrolled()).toContain(block));
    expect(scrollIntoView).toHaveBeenCalledWith({ block: "center" });
    expect(block).toHaveClass("k-flash");
    act(() => vi.advanceTimersByTime(1600));
    expect(block).not.toHaveClass("k-flash");
  });

  it("an unknown block opens at the top with a notice", async () => {
    planApi();
    renderApp({ pathname: planUrl, state: { block: "contoller" } satisfies NoteAt });
    expect(await screen.findByRole("status")).toHaveTextContent("There is no block ^contoller in this note.");
    expect(scrollIntoView).not.toHaveBeenCalled();
  });

  it("a line opens there", async () => {
    planApi();
    renderApp({ pathname: planUrl, state: { line: 10 } satisfies NoteAt });
    const paragraph = await screen.findByText(/^The valve stays\./, { selector: "p" });
    await vi.waitFor(() => expect(scrolled()).toEqual([paragraph]));
    expect(paragraph).toHaveClass("k-flash");
  });

  it("a footnote's definition above the line doesn't take the jump", async () => {
    // The definition (line 3) renders at the end of the note but keeps its source line.
    const content = ["# Plan", "", "[^1]: Alice said so.", "", "The pump is loud[^1].", "", "The valve stays.", "", "Done."].join("\n");
    planApi(content);
    renderApp({ pathname: planUrl, state: { line: 7 } satisfies NoteAt });
    const paragraph = await screen.findByText("The valve stays.", { selector: "p" });
    await vi.waitFor(() => expect(scrolled()).toEqual([paragraph]));
    expect(paragraph).toHaveClass("k-flash");
  });

  it("a line inside a footnote's definition opens at the footnote", async () => {
    planApi(["# Plan", "", "[^1]: Alice said so.", "", "The pump is loud[^1].", "", "Done."].join("\n"));
    renderApp({ pathname: planUrl, state: { line: 3 } satisfies NoteAt });
    const item = (await screen.findByText(/^Alice said so\./, { selector: "p" })).closest("li")!;
    await vi.waitFor(() => expect(scrolled()).toEqual([item]));
    expect(item).toHaveClass("k-flash");
  });

  it("a block id naming a whole quote lights up the quote", async () => {
    planApi(["# Plan", "", "> The pump is loud.", "> Bob heard it. ^noise", "", "Done."].join("\n"));
    renderApp({ pathname: planUrl, state: { block: "noise" } satisfies NoteAt });
    const quote = (await screen.findByText(/^The pump is loud\./, { selector: "p" })).closest("blockquote")!;
    await vi.waitFor(() => expect(scrolled()).toEqual([quote]));
    expect(quote).toHaveClass("k-flash");
  });

  it("a block a comment hides opens at the block above it, without a notice", async () => {
    planApi(["# Plan", "", "Intro.", "", "%%Secret plan. ^hidden", "", "%%", "", "Done."].join("\n"));
    renderApp({ pathname: planUrl, state: { block: "hidden" } satisfies NoteAt });
    const intro = await screen.findByText("Intro.", { selector: "p" });
    await vi.waitFor(() => expect(scrolled()).toEqual([intro]));
    expect(screen.queryByText(/Secret plan/)).toBeNull();
    expect(screen.queryByText(/There is no block/)).toBeNull();
  });

  it("a jump into a folded callout unfolds it", async () => {
    planApi(["# Plan", "", "> [!note]- Folded", "> The pump is loud.", ">", "> Bob heard it.", "", "Done."].join("\n"));
    renderApp({ pathname: planUrl, state: { line: 6 } satisfies NoteAt });
    const paragraph = await screen.findByText("Bob heard it.", { selector: "p" });
    await vi.waitFor(() => expect(scrolled()).toEqual([paragraph]));
    expect(paragraph.closest("details")).toHaveAttribute("open");
  });

  describe("while the block lights up", () => {
    // jsdom has no ResizeObserver: this one reports a change in size when asked.
    let resized: () => void;
    beforeEach(() => {
      vi.stubGlobal(
        "ResizeObserver",
        class {
          constructor(callback: () => void) {
            resized = callback;
          }
          observe() {}
          disconnect() {
            resized = () => {};
          }
        },
      );
    });

    it("the block is centred again when the note above it grows", async () => {
      planApi();
      renderApp({ pathname: planUrl, state: { block: "controller" } satisfies NoteAt });
      const block = await screen.findByText("The pump needs a new controller.", { selector: "p" });
      await vi.waitFor(() => expect(scrolled()).toEqual([block]));
      act(() => resized());
      expect(scrolled()).toEqual([block, block]);
    });

    it("but not once the user scrolls", async () => {
      planApi();
      renderApp({ pathname: planUrl, state: { block: "controller" } satisfies NoteAt });
      const block = await screen.findByText("The pump needs a new controller.", { selector: "p" });
      await vi.waitFor(() => expect(scrolled()).toEqual([block]));
      fireEvent.wheel(block);
      act(() => resized());
      expect(scrolled()).toEqual([block]);
    });
  });

  it("nothing inside an embed carries data-line", () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <MarkdownView source={"# Title\n\nText.\n\n- item\n\n> quote\n\n```\ncode\n```\n\n| a |\n|---|\n| b |\n"} links={[]} tasksDisabled onToggleTask={vi.fn()} onCreateMissing={vi.fn()} nested />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    expect(screen.getByText("Text.")).toBeInTheDocument();
    expect(document.querySelector("[data-line]")).toBeNull();
  });
});

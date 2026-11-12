// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, MemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { routerFuture } from "../router";
import { fakeApi, json } from "../test/app";
import BacklinksList from "./BacklinksList";
import UnlinkedMentions from "./UnlinkedMentions";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const PLAN = "/Work/projects/plan.md";
const mentionsUrl = `GET /api/mentions${PLAN}`;
const dated = { path: "/Work/Tagebuch/2026-11-12.md", line: 1, text: "Dana asked about the plan today", marks: [{ start: 21, length: 4 }] };

function show(element: React.ReactNode) {
  const router = createMemoryRouter(
    [
      { path: "/notes/*", element: <p>a note</p> },
      { path: "*", element },
    ],
    { initialEntries: ["/"], future: routerFuture },
  );
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} future={{ v7_startTransition: true }} />
    </QueryClientProvider>,
  );
  return router;
}

describe("unlinked mentions", () => {
  it("is closed and asks nothing until opened", async () => {
    const fetch = fakeApi({ [mentionsUrl]: () => json(200, [dated]) });
    show(<UnlinkedMentions path={PLAN} />);
    const button = await screen.findByRole("button", { name: "Unlinked mentions" });
    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(fetch).not.toHaveBeenCalled();
    await userEvent.click(button);
    expect(button).toHaveAttribute("aria-expanded", "true");
    await screen.findByText("2026-11-12");
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("lists the mentions with the title marked", async () => {
    fakeApi({ [mentionsUrl]: () => json(200, [dated]) });
    show(<UnlinkedMentions path={PLAN} />);
    await userEvent.click(await screen.findByRole("button", { name: "Unlinked mentions" }));
    expect(await screen.findByText("2026-11-12")).toBeInTheDocument();
    expect(screen.getByText("plan", { selector: "mark" })).toBeInTheDocument();
    expect(screen.getByText("Work / Tagebuch")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /2026-11-12/ })).toHaveTextContent("Dana asked about the plan today");
  });

  it("opens the note at the line", async () => {
    fakeApi({ [mentionsUrl]: () => json(200, [dated]) });
    const router = show(<UnlinkedMentions path={PLAN} />);
    await userEvent.click(await screen.findByRole("button", { name: "Unlinked mentions" }));
    await userEvent.click(await screen.findByRole("button", { name: /2026-11-12/ }));
    expect(router.state.location.pathname).toBe("/notes/Work/Tagebuch/2026-11-12.md");
    expect(router.state.location.state).toEqual({ line: 1 });
  });

  it("says when there are none", async () => {
    fakeApi({ [mentionsUrl]: () => json(200, []) });
    show(<UnlinkedMentions path={PLAN} />);
    await userEvent.click(await screen.findByRole("button", { name: "Unlinked mentions" }));
    expect(await screen.findByText("No unlinked mentions.")).toBeInTheDocument();
  });

  it("a failed request offers a retry", async () => {
    let calls = 0;
    const fetch = fakeApi({ [mentionsUrl]: () => (++calls === 1 ? json(500, { error: "boom" }) : json(200, [dated])) });
    show(<UnlinkedMentions path={PLAN} />);
    await userEvent.click(await screen.findByRole("button", { name: "Unlinked mentions" }));
    expect(await screen.findByText(/Couldn't look for mentions\./)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByText("2026-11-12")).toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("says it is looking while a retry is under way", async () => {
    let calls = 0;
    let answer: (r: Response) => void = () => {};
    fakeApi({ [mentionsUrl]: () => (++calls === 1 ? json(500, { error: "boom" }) : new Promise<Response>((r) => (answer = r))) });
    show(<UnlinkedMentions path={PLAN} />);
    await userEvent.click(await screen.findByRole("button", { name: "Unlinked mentions" }));
    await userEvent.click(await screen.findByRole("button", { name: "Try again" }));
    expect(await screen.findByText("Looking for mentions …")).toBeInTheDocument();
    expect(screen.queryByText(/Couldn't look for mentions\./)).not.toBeInTheDocument();
    answer(json(200, [dated]));
    expect(await screen.findByText("2026-11-12")).toBeInTheDocument();
  });

  it("closes again when the note changes", async () => {
    fakeApi({ [mentionsUrl]: () => json(200, [dated]), [`GET /api/mentions/Work/inbox.md`]: () => json(200, []) });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const at = (path: string) => (
      <QueryClientProvider client={client}>
        <MemoryRouter future={routerFuture}>
          <UnlinkedMentions path={path} />
        </MemoryRouter>
      </QueryClientProvider>
    );
    const { rerender } = render(at(PLAN));
    const button = screen.getByRole("button", { name: "Unlinked mentions" });
    await userEvent.click(button);
    expect(await screen.findByText("2026-11-12")).toBeInTheDocument();
    rerender(at("/Work/inbox.md"));
    expect(screen.getByRole("button", { name: "Unlinked mentions" })).toBe(button);
    expect(button).toHaveAttribute("aria-expanded", "false");
  });

  it("shows even when nothing links here", async () => {
    fakeApi({ [`GET /api/backlinks${PLAN}`]: () => json(200, []) });
    show(<BacklinksList path={PLAN} />);
    expect(await screen.findByRole("button", { name: "Unlinked mentions" })).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Linked from" })).not.toBeInTheDocument();
  });

  it("follows Linked from when something links here", async () => {
    fakeApi({ [`GET /api/backlinks${PLAN}`]: () => json(200, [{ path: "/Work/inbox.md", line: 3, text: "see [[plan]]", ambiguous: false }]) });
    show(<BacklinksList path={PLAN} />);
    const linked = await screen.findByRole("region", { name: "Linked from" });
    const unlinked = screen.getByRole("button", { name: "Unlinked mentions" });
    expect(linked.compareDocumentPosition(unlinked) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("a mention's text is never markup", async () => {
    fakeApi({ [mentionsUrl]: () => json(200, [{ ...dated, text: "<b>plan</b> here", marks: [{ start: 3, length: 4 }] }]) });
    show(<UnlinkedMentions path={PLAN} />);
    await userEvent.click(await screen.findByRole("button", { name: "Unlinked mentions" }));
    await screen.findByText("2026-11-12");
    expect(screen.getByRole("button", { name: /2026-11-12/ })).toHaveTextContent("<b>plan</b> here");
    expect(screen.getByRole("region", { name: "Unlinked mentions" }).querySelector("b")).toBeNull();
  });
});

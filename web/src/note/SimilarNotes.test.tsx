// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useOfflineMode } from "../offline/mode";
import { fakeApi, json } from "../test/app";
import SimilarNotes from "./SimilarNotes";

afterEach(() => {
  cleanup();
  useOfflineMode.setState({ offline: false });
  vi.unstubAllGlobals();
});

const PUMP = "/Work/pump.md";
const similarUrl = `GET /api/similar${PUMP}`;
const pipes = { path: "/Work/Water/pipes.md", line: 3, text: "Water moves through the pipes", score: 0.82 };
const ready = (notes = [pipes]) => json(200, { state: "ready", notes, pending: 0 });
const vaults = (level = "owner") => () => json(200, [{ name: "Work", notes: 2, level }]);
const written = () => json(200, { path: PUMP, version: "v2" });

function show() {
  const router = createMemoryRouter(
    [
      { path: "/notes/*", element: <p>a note</p> },
      { path: "*", element: <SimilarNotes path={PUMP} /> },
    ],
    { initialEntries: ["/"] },
  );
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

const openSection = async () => userEvent.click(await screen.findByRole("button", { name: "Similar notes" }));

describe("similar notes", () => {
  it("is closed and asks nothing until opened", async () => {
    const fetch = fakeApi({ [similarUrl]: () => ready(), "GET /api/vaults": vaults() });
    show();
    const button = await screen.findByRole("button", { name: "Similar notes" });
    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(fetch.mock.calls.some(([r]) => new URL((r as Request).url).pathname.startsWith("/api/similar"))).toBe(false);
    await userEvent.click(button);
    expect(await screen.findByText("pipes")).toBeInTheDocument();
  });

  it("lists a suggestion with its closest line and folder, and opens it at that line", async () => {
    fakeApi({ [similarUrl]: () => ready(), "GET /api/vaults": vaults() });
    const router = show();
    await openSection();
    expect(await screen.findByText("Water moves through the pipes")).toBeInTheDocument();
    expect(screen.getByText("Work / Water")).toBeInTheDocument();
    expect(screen.queryByText("0.82")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /^pipes/ }));
    expect(router.state.location.pathname).toBe("/notes/Work/Water/pipes.md");
    expect(router.state.location.state).toEqual({ line: 3 });
  });

  it("adds a link", async () => {
    const bodies: unknown[] = [];
    fakeApi({
      [similarUrl]: () => ready(),
      "GET /api/vaults": vaults(),
      "POST /api/vaults/Work/similar/link": async (r) => {
        bodies.push(await r.json());
        return written();
      },
    });
    show();
    await openSection();
    await userEvent.click(await screen.findByRole("button", { name: "Add link to pipes" }));
    await vi.waitFor(() => expect(bodies).toEqual([{ path: PUMP, target: pipes.path }]));
  });

  it("hides a suggestion as not related, in place, and takes it back with Undo", async () => {
    const bodies: unknown[] = [];
    let rejected = false;
    fakeApi({
      [similarUrl]: () => ready(rejected ? [] : [pipes]),
      "GET /api/vaults": vaults(),
      "POST /api/vaults/Work/similar/not-related": async (r) => {
        const body = (await r.json()) as { undo: boolean };
        bodies.push(body);
        rejected = !body.undo;
        return written();
      },
    });
    show();
    await openSection();
    await userEvent.click(await screen.findByRole("button", { name: "Not related: pipes" }));
    expect(await screen.findByText("Hidden · pipes")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Undo" }));
    expect(await screen.findByRole("button", { name: "Not related: pipes" })).toBeInTheDocument();
    expect(bodies).toEqual([
      { path: PUMP, other: pipes.path, undo: false },
      { path: PUMP, other: pipes.path, undo: true },
    ]);
  });

  it("keeps a row answered Not related where it stood", async () => {
    const tank = { path: "/Work/Water/tank.md", line: 1, text: "The tank holds the water", score: 0.7 };
    let rejected = false;
    fakeApi({
      [similarUrl]: () => ready(rejected ? [tank] : [pipes, tank]),
      "GET /api/vaults": vaults(),
      "POST /api/vaults/Work/similar/not-related": () => {
        rejected = true;
        return written();
      },
    });
    show();
    await openSection();
    await userEvent.click(await screen.findByRole("button", { name: "Not related: pipes" }));
    expect(await screen.findByText("Hidden · pipes")).toBeInTheDocument();
    await vi.waitFor(() => expect(screen.getAllByRole("listitem").map((li) => li.textContent)).toEqual(["Hidden · pipesUndo", expect.stringMatching(/^tank/)]));
  });

  it("offline, says it needs a connection and asks nothing", async () => {
    useOfflineMode.setState({ offline: true });
    const fetch = fakeApi({ [similarUrl]: () => ready(), "GET /api/vaults": vaults() });
    show();
    await openSection();
    expect(await screen.findByText("Similar notes need a connection. They're back when you are.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
    expect(fetch.mock.calls.some(([r]) => new URL((r as Request).url).pathname.startsWith("/api/similar"))).toBe(false);
  });

  it("offers no answers to a reader who can't change the note", async () => {
    fakeApi({ [similarUrl]: () => ready(), "GET /api/vaults": vaults("read") });
    show();
    await openSection();
    expect(await screen.findByText("pipes")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add link to pipes" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Not related: pipes" })).not.toBeInTheDocument();
  });

  it.each([
    ["off", "Similar notes need search by meaning, which is off for this vault."],
    ["preparing", "Search by meaning is getting ready. Try again in a moment."],
    ["unindexed", "This note isn't indexed by meaning yet. Try again in a moment."],
  ])("says when it is %s", async (state, text) => {
    fakeApi({ [similarUrl]: () => json(200, { state, notes: [], pending: 0 }), "GET /api/vaults": vaults() });
    show();
    await openSection();
    expect(await screen.findByText(text)).toBeInTheDocument();
  });

  it("says when there are none, and offers a retry after a failure", async () => {
    let calls = 0;
    fakeApi({ [similarUrl]: () => (++calls === 1 ? json(500, { error: "boom" }) : ready([])), "GET /api/vaults": vaults() });
    show();
    await openSection();
    expect(await screen.findByText(/Couldn't look for similar notes\./)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByText("No similar notes that aren't linked.")).toBeInTheDocument();
  });
});

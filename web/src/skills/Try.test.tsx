// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import Try from "./Try";

const skill = (name: string, location: string, description = "d") => ({ name, description, location, path: `/${location}/_skills/${name}`, source: null, lastEditor: null, byOther: false, warnings: [], canWrite: true });

const show = () =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Try />
    </QueryClientProvider>,
  );

const finds = (fetch: ReturnType<typeof fakeApi>) =>
  fetch.mock.calls.map(([r]) => new URL((r as Request).url)).filter((u) => u.pathname === "/api/skills/find").map((u) => u.pathname + u.search);

const lists = {
  "GET /api/keys": () => json(200, [{ id: "k1", name: "Laptop agent", kind: "agent" }, { id: "k2", name: "Obsidian", kind: "obsidian" }]),
  "GET /api/connections": () => json(200, [{ id: "c1", name: "Phone" }]),
};

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Try", () => {
  it("shows what skills find returns, with rank and score", async () => {
    const fetch = fakeApi({
      ...lists,
      "GET /api/skills/find": () => json(200, [{ rank: 1, score: "0.0492", skill: skill("release-post", "Work", "<b>Writes</b> posts") }, { rank: 2, score: 0.03, skill: skill("changelog", "system") }]),
    });
    show();
    await userEvent.type(screen.getByLabelText("What an agent asks for"), "release post");
    expect(await screen.findByText("1. release-post")).toBeInTheDocument();
    expect(screen.getByText("Work · score 0.0492")).toBeInTheDocument();
    expect(screen.getByText("System · score 0.0300")).toBeInTheDocument();
    expect(screen.getByText("<b>Writes</b> posts")).toBeInTheDocument();
    expect(finds(fetch)).toEqual(["/api/skills/find?q=release%20post"]);
  });

  it("asks as a chosen connection", async () => {
    const fetch = fakeApi({ ...lists, "GET /api/skills/find": () => json(200, []) });
    show();
    const select = await screen.findByLabelText("As seen by");
    expect(screen.queryByRole("option", { name: "Obsidian" })).not.toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("What an agent asks for"), "release post");
    await screen.findByText("No skill fits this request.");
    await userEvent.selectOptions(select, "Phone");
    await waitFor(() => expect(finds(fetch)).toContain("/api/skills/find?q=release%20post&as=c1"));
  });

  it("waits for the typing to pause before asking", async () => {
    const fetch = fakeApi({ ...lists, "GET /api/skills/find": () => json(200, []) });
    show();
    await userEvent.type(screen.getByLabelText("What an agent asks for"), "abc");
    await screen.findByText("No skill fits this request.");
    expect(finds(fetch)).toEqual(["/api/skills/find?q=abc"]);
  });

  it("says when nothing fits", async () => {
    fakeApi({ ...lists, "GET /api/skills/find": () => json(200, []) });
    show();
    await userEvent.type(screen.getByLabelText("What an agent asks for"), "nothing");
    expect(await screen.findByText("No skill fits this request.")).toBeInTheDocument();
  });
});

// SPDX-License-Identifier: AGPL-3.0-or-later
import { focusManager } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { setAutoRefresh, storedAutoRefresh } from "./autoRefresh";

beforeEach(() => localStorage.clear());

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  localStorage.clear();
});

const PLAN = "/Work/plan.md";
const tree = { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: "plan.md", path: PLAN, size: 10 }] } };
const texts: Record<string, string> = { v1: "# Plan\n\nFirst.\n\nSecond.\n", v2: "# Plan\n\nFirst.\n\nSecond, changed by liz.\n" };
const note = (version: string) => ({ path: PLAN, content: texts[version], version, lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z", current: true });

/** The server holds `server.version`; the note is read at it, and its version polled. */
function api(server: { version: string; writer: string; gone?: boolean }, puts: unknown[] = []) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal" }]),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/Work/plan.md": () => json(200, note(server.version)),
    "GET /api/version/Work/plan.md": () =>
      server.gone ? json(404, { error: "There is no note at /Work/plan.md" }) : json(200, { path: PLAN, version: server.version, lastWriter: server.writer, lastWriteAt: new Date().toISOString() }),
    "PUT /api/notes/Work/plan.md": async (r) => {
      const body = (await r.json()) as { content: string; version: string };
      puts.push(body);
      texts.v9 = body.content;
      server.version = "v9";
      server.writer = "ada";
      return json(200, { ...note("v9"), content: body.content });
    },
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
  });
}

/** The page went to the background and came back: the version is asked for at once. */
const backToThePage = () =>
  act(() => {
    focusManager.setFocused(false);
    focusManager.setFocused(true);
  });

const bar = () => screen.findByRole("region", { name: "Changed elsewhere" });

describe("a change made elsewhere to the open note, in Read mode", () => {
  it("is shown in a bar, and the text on screen stays until Refresh, which tints what changed", async () => {
    const server = { version: "v1", writer: "ada" };
    api(server);
    renderApp("/notes/Work/plan.md");
    expect(await screen.findByText("Second.")).toBeInTheDocument();

    server.version = "v2";
    server.writer = "liz (edit)";
    backToThePage();

    expect(await bar()).toHaveTextContent("liz changed this note just now");
    expect(screen.getByText("Second.")).toBeInTheDocument();

    await userEvent.click(within(await bar()).getByRole("button", { name: "Refresh" }));

    expect(await screen.findByText("Second, changed by liz.")).toBeInTheDocument();
    expect([...document.querySelectorAll("[data-changed]")].map((e) => e.textContent?.trim())).toEqual(["Second, changed by liz."]);
    expect(screen.queryByRole("region", { name: "Changed elsewhere" })).not.toBeInTheDocument();
  });

  it("names the user when they changed it in another tab or on another device", async () => {
    const server = { version: "v1", writer: "ada" };
    api(server);
    renderApp("/notes/Work/plan.md");
    await screen.findByText("Second.");

    server.version = "v2";
    backToThePage();

    expect(await bar()).toHaveTextContent("You changed this note elsewhere just now");
  });

  it("is taken at once with “Refresh Read mode automatically” on, with a short note who changed it", async () => {
    setAutoRefresh(true);
    const server = { version: "v1", writer: "ada" };
    api(server);
    renderApp("/notes/Work/plan.md");
    await screen.findByText("Second.");

    server.version = "v2";
    server.writer = "liz";
    backToThePage();

    expect(await screen.findByText("Second, changed by liz.")).toBeInTheDocument();
    expect(screen.getByRole("status", { name: "Updated" })).toHaveTextContent("Updated by liz");
    expect(document.querySelector("[data-changed]")).toHaveTextContent("Second, changed by liz.");
  });

  it("says when the note was moved or removed elsewhere", async () => {
    const server = { version: "v1", writer: "ada", gone: false };
    api(server);
    renderApp("/notes/Work/plan.md");
    await screen.findByText("Second.");

    server.gone = true;
    backToThePage();

    expect(await bar()).toHaveTextContent("This note was moved or removed elsewhere.");
    expect(screen.getByText("Second.")).toBeInTheDocument();
  });

  it("is not asked for while the page is hidden", async () => {
    const fetch = api({ version: "v1", writer: "ada" });
    renderApp("/notes/Work/plan.md");
    await screen.findByText("Second.");
    const polls = () => fetch.mock.calls.filter(([r]) => new URL((r as Request).url).pathname === "/api/version/Work/plan.md").length;
    await waitFor(() => expect(polls()).toBe(1));

    act(() => focusManager.setFocused(false));
    await new Promise((r) => setTimeout(r, 50));

    expect(polls()).toBe(1);
  });
});

describe("a change made elsewhere to the open note, in Write mode", () => {
  async function openInWrite(server: { version: string; writer: string }, puts: unknown[] = []) {
    api(server, puts);
    renderApp("/notes/Work/plan.md");
    await userEvent.click(await screen.findByRole("button", { name: "Write" }));
    return screen.findByRole("textbox", { name: "Markdown source" });
  }

  it("is shown, never refreshed by itself even with the setting on, and opens the conflict choice", async () => {
    setAutoRefresh(true);
    const server = { version: "v1", writer: "ada" };
    const puts: unknown[] = [];
    const editor = await openInWrite(server, puts);

    server.version = "v2";
    server.writer = "liz";
    backToThePage();

    const shown = await bar();
    expect(shown).toHaveTextContent("liz changed this note just now");
    expect(editor).toHaveValue(texts.v1);

    await userEvent.click(within(shown).getByRole("button", { name: "View the new version" }));

    const diff = await screen.findByRole("dialog", { name: "Changed elsewhere" });
    expect(within(diff).getByText("Second, changed by liz.")).toBeInTheDocument();
    expect(editor).toHaveValue(texts.v1);
    expect(puts).toHaveLength(0);
  });

  it("lets the user keep writing, and their next save meets the conflict choice instead of overwriting", async () => {
    const server = { version: "v1", writer: "ada" };
    const editor = await openInWrite(server);
    server.version = "v2";
    server.writer = "liz";
    backToThePage();

    await userEvent.click(within(await bar()).getByRole("button", { name: "Keep writing" }));

    expect(screen.queryByRole("region", { name: "Changed elsewhere" })).not.toBeInTheDocument();
    expect(editor).toHaveValue(texts.v1);
  });

  it("does not count the user's own saves", async () => {
    const server = { version: "v1", writer: "ada" };
    const puts: unknown[] = [];
    const editor = await openInWrite(server, puts);

    await userEvent.type(editor, " more");
    fireEvent.blur(editor);
    await waitFor(() => expect(puts).toHaveLength(1));
    backToThePage();
    await new Promise((r) => setTimeout(r, 50));

    expect(screen.queryByRole("region", { name: "Changed elsewhere" })).not.toBeInTheDocument();
  });
});

describe("the setting", () => {
  it("is off by default and kept on this device", () => {
    expect(storedAutoRefresh()).toBe(false);
    setAutoRefresh(true);
    expect(storedAutoRefresh()).toBe(true);
    expect(localStorage.getItem("kumbu.autoRefresh")).toBe("on");
    setAutoRefresh(false);
    expect(localStorage.getItem("kumbu.autoRefresh")).toBeNull();
  });
});

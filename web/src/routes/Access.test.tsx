// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }]),
    "GET /api/keys": () => json(200, []),
    "GET /api/connections": () => json(200, []),
    ...overrides,
  });
}

const tab = (name: string) => within(screen.getByRole("tablist", { name: "Access" })).getByRole("tab", { name });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
});

describe("Access", () => {
  it("shows the three tabs and the keys tab by default", async () => {
    api();
    const router = renderApp("/access");

    expect(await screen.findByRole("heading", { level: 1, name: "Access" })).toBeInTheDocument();
    const tabs = within(screen.getByRole("tablist", { name: "Access" })).getAllByRole("tab");
    expect(tabs.map((t) => t.textContent)).toEqual(["API keys", "Apps", "WebDAV"]);
    expect(tab("API keys")).toHaveAttribute("aria-selected", "true");
    expect(tab("Apps")).toHaveAttribute("aria-selected", "false");
    expect(screen.getByRole("tabpanel", { name: "API keys" })).toContainElement(screen.getByRole("button", { name: "New key" }));
    expect(screen.queryByRole("heading", { name: "Connections" })).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/access/keys");
  });

  it("the tab is in the URL", async () => {
    api();
    const router = renderApp("/access/apps");

    expect(await screen.findByRole("heading", { name: "Connections" })).toBeInTheDocument();
    expect(tab("Apps")).toHaveAttribute("aria-selected", "true");
    expect(screen.queryByRole("button", { name: "New key" })).not.toBeInTheDocument();
    expect(tab("API keys")).toHaveAttribute("href", "/access/keys");
    expect(tab("WebDAV")).toHaveAttribute("href", "/access/dav");

    await userEvent.click(tab("WebDAV"));
    await waitFor(() => expect(router.state.location.pathname).toBe("/access/dav"));
    expect(tab("WebDAV")).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tabpanel", { name: "WebDAV" })).toBeInTheDocument();

    await userEvent.keyboard("{ArrowLeft}");
    await waitFor(() => expect(router.state.location.pathname).toBe("/access/apps"));
    expect(tab("Apps")).toHaveFocus();

    await userEvent.click(tab("API keys"));
    expect(await screen.findByRole("button", { name: "New key" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/access/keys");
  });

  it("/keys redirects to /access/keys", async () => {
    api();
    const router = renderApp("/keys");

    expect(await screen.findByRole("button", { name: "New key" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/access/keys");
    expect(tab("API keys")).toHaveAttribute("aria-selected", "true");
  });

  it("the apps tab has the MCP subtitle", async () => {
    api();
    renderApp("/access/apps");

    const panel = await screen.findByRole("tabpanel", { name: "Apps" });
    expect(within(panel).getByText("MCP via OAuth: claude.ai connectors, ChatGPT and others")).toBeInTheDocument();
  });

  it("a new connection's address has the page to itself, without the tabs, until done", async () => {
    api({ "POST /api/connections": () => json(201, connection) });
    renderApp("/access/apps");
    await connect();

    expect(await screen.findByText(connection.url)).toBeInTheDocument();
    expect(screen.queryByRole("tablist")).not.toBeInTheDocument();
    expect(screen.queryByText("MCP via OAuth: claude.ai connectors, ChatGPT and others")).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(await screen.findByRole("tablist", { name: "Access" })).toBeInTheDocument();
    expect(tab("Apps")).toHaveAttribute("aria-selected", "true");
  });

  it("leaving in the middle of a reveal and coming back shows the tabs again", async () => {
    api({ "POST /api/connections": () => json(201, connection) });
    const router = renderApp(["/access/keys", "/access/apps"]);
    await connect();
    expect(await screen.findByText(connection.url)).toBeInTheDocument();

    await act(() => router.navigate(-1));
    expect(await screen.findByRole("button", { name: "New key" })).toBeInTheDocument();
    await act(() => router.navigate("/access/apps"));
    expect(await screen.findByRole("button", { name: "Connect an app" })).toBeInTheDocument();
    expect(screen.getByRole("tablist", { name: "Access" })).toBeInTheDocument();
    expect(tab("Apps")).toHaveAttribute("aria-selected", "true");
  });

  it("an unknown tab leads to the keys tab", async () => {
    api();
    const router = renderApp("/access/foo");

    expect(await screen.findByRole("button", { name: "New key" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/access/keys");
  });

  it("Home and End move to the first and the last tab, in place of the current entry", async () => {
    api();
    const router = renderApp(["/settings", "/access/apps"]);
    await screen.findByRole("heading", { name: "Connections" });
    tab("Apps").focus();

    await userEvent.keyboard("{End}");
    await waitFor(() => expect(router.state.location.pathname).toBe("/access/dav"));
    expect(tab("WebDAV")).toHaveFocus();
    await userEvent.keyboard("{Home}");
    await waitFor(() => expect(router.state.location.pathname).toBe("/access/keys"));
    expect(tab("API keys")).toHaveFocus();

    await act(() => router.navigate(-1));
    await waitFor(() => expect(router.state.location.pathname).toBe("/settings"));
  });
});

const connection = {
  id: "c1",
  name: "Work",
  url: "https://kumbu.example.org/mcp/c/Zk3-connection-token",
  vaults: { Work: "write" },
  actsAs: {},
  createdAt: "2026-10-01T09:00:00Z",
  lastUsedAt: null,
  expiresAt: null,
  revokedAt: null,
  state: "waiting",
  reason: null,
  client: null,
  publish: [],
};

async function connect() {
  await userEvent.click(await screen.findByRole("button", { name: "Connect an app" }));
  const dialog = screen.getByRole("dialog", { name: "Connect an app" });
  await userEvent.type(within(dialog).getByLabelText("Name"), "Work");
  await userEvent.click(within(within(dialog).getByRole("group", { name: "Work" })).getByLabelText("Read and write"));
  await userEvent.click(within(dialog).getByRole("button", { name: "Connect an app" }));
}

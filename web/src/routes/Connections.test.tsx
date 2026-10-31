// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { timeText } from "../components/Sheet";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const url = "https://kumbu.example/mcp/c/Zk3-very-long-connection-token-Qw81Xp0Lm92Ab34Cd56Ef78";
const vaults = [
  { name: "Arbeit", notes: 3 },
  { name: "Home", notes: 1 },
];
const base = {
  id: "c1",
  name: "Work",
  url,
  vaults: { Arbeit: "write" },
  createdAt: "2026-10-01T09:00:00Z",
  lastUsedAt: null as string | null,
  expiresAt: null as string | null,
  revokedAt: null as string | null,
  state: "connected",
  reason: null as string | null,
  client: { name: "Claude", host: "claude.ai", verified: true } as { name: string; host: string; verified: boolean } | null,
};

function api(
  connections: unknown[] = [],
  overrides: Record<string, (r: Request) => Response | Promise<Response>> = {},
) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, vaults),
    "GET /api/keys": () => json(200, []),
    "GET /api/connections": () => json(200, connections),
    ...overrides,
  });
}

const gets = (fetch: ReturnType<typeof api>) => fetch.mock.calls.filter(([r]) => r.method === "GET" && new URL(r.url).pathname === "/api/connections").length;

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  localStorage.clear();
  sessionStorage.clear();
  useSession.getState().reset();
});

describe("Connections", () => {
  it("creates a connection and shows its connector URL with instructions", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    const bodies: unknown[] = [];
    api([], { "POST /api/connections": async (r) => (bodies.push(await r.json()), json(201, { ...base, state: "waiting", client: null })) });
    renderApp("/keys");

    expect(await screen.findByRole("heading", { level: 1, name: "Access" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Connections" })).toBeInTheDocument();
    await userEvent.click(await screen.findByRole("button", { name: "Connect an app" }));
    const dialog = screen.getByRole("dialog", { name: "Connect an app" });
    expect(within(dialog).getByLabelText("Name")).toHaveAttribute("placeholder", "e.g. Work");
    expect(within(dialog).getByText("Claude")).toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText("Name"), "Work");
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Arbeit" })).getByLabelText("Read and write"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Connect an app" }));

    expect(await screen.findByText(url)).toBeInTheDocument();
    expect(bodies).toEqual([{ name: "Work", vaults: { Arbeit: "write" }, expiresInDays: 90 }]);
    expect(screen.getByText("Connector URL")).toBeInTheDocument();
    expect(screen.getByText("1. In claude.ai: Settings → Connectors → Add custom connector.")).toBeInTheDocument();
    expect(screen.getByText("2. Paste this URL. Leave client ID and secret empty.")).toBeInTheDocument();
    expect(screen.getByText("3. Tip: name the connector like this connection, e.g. 'Kumbu Work'.")).toBeInTheDocument();
    expect(screen.getByText("Other apps: add a remote MCP server with this URL; it signs in with OAuth.")).toBeInTheDocument();
    expect(screen.getByText("The address is not a secret: it only says which connection is meant. Access needs your consent in Kumbu².")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(writeText).toHaveBeenLastCalledWith(url);
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByText("Connector URL")).not.toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Connect an app" })).toBeInTheDocument();
  });

  it("needs a name and at least one vault with a right", async () => {
    api();
    renderApp("/keys");
    await userEvent.click(await screen.findByRole("button", { name: "Connect an app" }));
    const dialog = screen.getByRole("dialog", { name: "Connect an app" });
    const create = within(dialog).getByRole("button", { name: "Connect an app" });
    expect(create).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Name"), "x");
    expect(create).toBeDisabled();
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Home" })).getByLabelText("Read"));
    expect(create).toBeEnabled();
  });

  it("sorts the connections by least recent use on request, the revoked and expired ones within their own group", async () => {
    api([
      { ...base, id: "a", name: "Alpha", lastUsedAt: "2026-10-20T09:00:00Z" },
      { ...base, id: "b", name: "Bravo", state: "revoked", reason: "revoked", lastUsedAt: "2026-10-15T09:00:00Z" },
      { ...base, id: "c", name: "Charlie", lastUsedAt: null, state: "waiting", client: null },
      { ...base, id: "d", name: "Delta", lastUsedAt: "2026-10-03T09:00:00Z" },
      { ...base, id: "e", name: "Echo", state: "expired", lastUsedAt: null, expiresAt: "2026-10-02T09:00:00Z" },
      { ...base, id: "f", name: "Foxtrot", lastUsedAt: null },
    ]);
    renderApp("/keys");
    await screen.findByText("Alpha");
    const region = screen.getByRole("region", { name: "Connections" });
    const live = () => [...region.querySelectorAll(":scope > ul > li h3")].map((h) => h.textContent);
    const over = () => [...region.querySelectorAll("details li h3")].map((h) => h.textContent);
    const sort = within(region).getByRole("group", { name: "Sort connections" });

    expect(within(sort).getByRole("button", { name: "By creation" })).toHaveAttribute("aria-pressed", "true");
    expect(live()).toEqual(["Alpha", "Charlie", "Delta", "Foxtrot"]);
    expect(over()).toEqual(["Bravo", "Echo"]);

    await userEvent.click(within(sort).getByRole("button", { name: "Least recently used first" }));
    expect(live()).toEqual(["Charlie", "Foxtrot", "Delta", "Alpha"]);
    expect(over()).toEqual(["Echo", "Bravo"]);
  });

  it("lists states with their reasons", async () => {
    api([
      { ...base, id: "a", name: "Waiting one", state: "waiting", client: null },
      { ...base, id: "b", name: "Live one", lastUsedAt: "2026-10-05T09:00:00Z", expiresAt: "2099-01-31T09:00:00Z" },
      { ...base, id: "c", name: "Stale one", state: "needs_reconnect", reason: "rights_changed", client: { name: "Claude", host: "claude.ai", verified: false } },
      { ...base, id: "d", name: "Gone one", state: "revoked", reason: "refresh_reused", revokedAt: "2026-10-06T09:00:00Z" },
      { ...base, id: "e", name: "Old one", state: "expired", expiresAt: "2026-10-02T09:00:00Z" },
    ]);
    renderApp("/keys");
    const item = async (name: string) => (await screen.findByText(name)).closest("li")!;

    const waiting = await item("Waiting one");
    expect(waiting).toHaveTextContent("Waiting for the app");
    expect(waiting).toHaveTextContent("Never used");
    expect(waiting).toHaveTextContent("Doesn't expire");
    expect(waiting).toHaveTextContent(url);
    expect(within(waiting).getByRole("button", { name: "Copy address of Waiting one" })).toBeInTheDocument();

    const live = await item("Live one");
    expect(live).toHaveTextContent("Connected");
    expect(live).toHaveTextContent("Claude · claude.ai");
    expect(live).not.toHaveTextContent("self-registered");
    expect(live).toHaveTextContent("Arbeit: read and write");
    expect(live).toHaveTextContent(`Created 1 Oct 2026 · Last used ${timeText("2026-10-05T09:00:00Z")} · Expires 31 Jan 2099`);

    const stale = await item("Stale one");
    expect(stale).toHaveTextContent("Claude · claude.ai · self-registered");
    expect(stale).toHaveTextContent("Needs reconnect: its rights were changed");
    expect(stale).toHaveTextContent("Use Reconnect in the app.");

    const summary = screen.getByText("Revoked and expired (2)");
    const details = summary.closest("details")!;
    expect(details).not.toHaveAttribute("open");
    const gone = await item("Gone one");
    expect(details).toContainElement(gone);
    expect(gone).toHaveTextContent("Revoked: a token was used twice, which can mean it was stolen");
    expect(within(gone).queryByRole("button", { name: /Revoke|Change rights/ })).not.toBeInTheDocument();
    const old = await item("Old one");
    expect(details).toContainElement(old);
    expect(old).toHaveTextContent("Expired");
    for (const live of [waiting, stale]) expect(details).not.toContainElement(live);
  });

  it.each([
    ["revoked", "you revoked it"],
    ["vault_deleted", "its vaults were deleted"],
    ["password_changed", "your password was changed"],
    ["recovery", "your account was recovered"],
    ["deactivated", "your account was deactivated"],
    ["refresh_reused", "a token was used twice, which can mean it was stolen"],
    ["code_reused", "a sign-in code was used twice"],
    ["client_revoked", "the app disconnected"],
    ["rights_changed", "its rights were changed"],
    ["rekeyed", "a vault was re-encrypted"],
  ])("explains the reason %s", async (reason, text) => {
    api([{ ...base, state: "revoked", reason, revokedAt: "2026-10-06T09:00:00Z" }]);
    renderApp("/keys");
    expect((await screen.findByText("Work")).closest("li")).toHaveTextContent(`Revoked: ${text}`);
  });

  it("changing rights says the app has to reconnect", async () => {
    const bodies: unknown[] = [];
    const fetch = api([base], {
      "PUT /api/connections/c1/vaults": async (r) => (bodies.push(await r.json()), json(200, { ...base, vaults: { Home: "read" }, state: "needs_reconnect", reason: "rights_changed" })),
    });
    renderApp("/keys");
    await userEvent.click(await screen.findByRole("button", { name: "Change rights of Work" }));
    const dialog = screen.getByRole("dialog", { name: "Change rights" });
    expect(dialog).toHaveTextContent("The app has to reconnect after this.");
    expect(within(within(dialog).getByRole("group", { name: "Arbeit" })).getByLabelText("Read and write")).toBeChecked();
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Arbeit" })).getByLabelText("None"));
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Home" })).getByLabelText("Read"));
    const before = gets(fetch);
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(bodies).toEqual([{ vaults: { Home: "read" } }]));
    await waitFor(() => expect(gets(fetch)).toBe(before + 1));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("revoking asks first", async () => {
    let calls = 0;
    const fetch = api([base], { "DELETE /api/connections/c1": () => (calls++, noContent()) });
    renderApp("/keys");
    await userEvent.click(await screen.findByRole("button", { name: "Revoke Work" }));
    const confirm = screen.getByRole("alertdialog");
    expect(confirm).toHaveTextContent("Revoke Work?");
    expect(confirm).toHaveTextContent("The app loses access at once. Its address can't be used again; make a new connection instead.");
    await userEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(calls).toBe(0);

    const before = gets(fetch);
    await userEvent.click(screen.getByRole("button", { name: "Revoke Work" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Revoke" }));
    await waitFor(() => expect(calls).toBe(1));
    await waitFor(() => expect(gets(fetch)).toBe(before + 1));
  });

  it("keeps nothing in browser storage", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    api([], { "POST /api/connections": () => json(201, { ...base, state: "waiting", client: null }) });
    renderApp("/keys", queryClient);
    await userEvent.click(await screen.findByRole("button", { name: "Connect an app" }));
    const dialog = screen.getByRole("dialog", { name: "Connect an app" });
    await userEvent.type(within(dialog).getByLabelText("Name"), "Work");
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Arbeit" })).getByLabelText("Read"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Connect an app" }));
    await screen.findByText(url);

    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    await waitFor(() => expect(queryClient.getMutationCache().getAll().filter((m) => m.state.data !== undefined)).toEqual([]));
  });
});

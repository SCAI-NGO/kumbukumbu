// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const secret = "kumbu_Ab12Cd34Ef56Gh78Ij90Kl12Mn34Op56";
const vaults = [
  { name: "Work", notes: 3 },
  { name: "Home", notes: 1 },
];
const listed = {
  id: "k1",
  name: "Laptop",
  prefix: "Ab12Cd",
  vaults: { Work: "write", Home: "read" },
  createdAt: "2026-10-01T09:00:00Z",
  lastUsedAt: null,
  revokedAt: null,
};
const revoked = { ...listed, id: "k0", name: "Old phone", prefix: "Zz99Yy", vaults: { Home: "read" }, revokedAt: "2026-10-02T09:00:00Z" };

function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, vaults),
    "GET /api/keys": () => json(200, [revoked, listed]),
    ...overrides,
  });
}

async function openNewKey() {
  await userEvent.click(await screen.findByRole("button", { name: "New key" }));
  return screen.getByRole("dialog", { name: "New API key" });
}

async function createKey(queryClient?: QueryClient) {
  const fetch = api({
    "POST /api/keys": () => json(201, { key: secret, apiKey: { ...listed, id: "k2", name: "Claude", vaults: { Work: "write" } } }),
  });
  const router = renderApp("/keys", queryClient);
  const dialog = await openNewKey();
  await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
  await userEvent.click(within(within(dialog).getByRole("group", { name: "Work" })).getByLabelText("Read and write"));
  await userEvent.click(within(dialog).getByRole("button", { name: "Create key" }));
  await screen.findByText(secret);
  return { fetch, router };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
});

describe("Keys", () => {
  it("lists name, prefix, vaults with rights, created and last used", async () => {
    api();
    renderApp("/keys");
    const item = (await screen.findByText("Laptop")).closest("li")!;
    expect(item).toHaveTextContent("Ab12Cd");
    expect(item).toHaveTextContent("Work: read and write");
    expect(item).toHaveTextContent("Home: read");
    expect(item).toHaveTextContent("Created 1 Oct 2026");
    expect(item).toHaveTextContent("Never used");
    expect(within(item).getByRole("button", { name: "Revoke Laptop" })).toBeInTheDocument();

    const old = screen.getByText("Old phone").closest("li")!;
    expect(old).toHaveTextContent("Revoked 2 Oct 2026");
    expect(within(old).queryByRole("button", { name: /Revoke/ })).not.toBeInTheDocument();
  });

  it("revokes a key only after confirming", async () => {
    let revokedCalls = 0;
    api({ "DELETE /api/keys/k1": () => (revokedCalls++, noContent()) });
    renderApp("/keys");
    await userEvent.click(await screen.findByRole("button", { name: "Revoke Laptop" }));
    const confirm = screen.getByRole("alertdialog");
    expect(confirm).toHaveTextContent("Revoke Laptop?");
    await userEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(revokedCalls).toBe(0);

    await userEvent.click(screen.getByRole("button", { name: "Revoke Laptop" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Revoke" }));
    await waitFor(() => expect(revokedCalls).toBe(1));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
  });

  it("needs a name and at least one vault with a right", async () => {
    api();
    renderApp("/keys");
    const dialog = await openNewKey();
    const create = within(dialog).getByRole("button", { name: "Create key" });
    const work = within(dialog).getByRole("group", { name: "Work" });
    expect(within(work).getByLabelText("None")).toBeChecked();
    expect(create).toBeDisabled();

    await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
    expect(create).toBeDisabled();
    await userEvent.click(within(work).getByLabelText("Read"));
    expect(create).toBeEnabled();
    await userEvent.click(within(work).getByLabelText("None"));
    expect(create).toBeDisabled();
  });

  it("sends the name and the chosen rights, and shows the server's sentence on a refusal", async () => {
    const bodies: unknown[] = [];
    api({
      "POST /api/keys": async (r) => (bodies.push(await r.json()), json(409, { error: "You already have a key named 'Claude'." })),
    });
    renderApp("/keys");
    const dialog = await openNewKey();
    await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Work" })).getByLabelText("Read and write"));
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Home" })).getByLabelText("Read"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Create key" }));

    expect(await within(dialog).findByText("You already have a key named 'Claude'.")).toBeInTheDocument();
    expect(bodies).toEqual([{ name: "Claude", vaults: { Work: "write", Home: "read" } }]);
  });

  it("marks only the button that was used as copied", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    await createKey(new QueryClient({ defaultOptions: { queries: { retry: false } } }));
    await userEvent.click(screen.getByRole("button", { name: "Copy key" }));
    expect(screen.getByRole("button", { name: "Copy key" })).toHaveTextContent("Copied");
    expect(screen.getByRole("button", { name: "Copy command" })).toHaveTextContent(/^Copy command$/);
    expect(screen.getAllByRole("status").map((s) => s.textContent).join("")).toBe("Copied");
  });

  it("shows the new key once, with copy buttons for the key and the Claude Code line", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { router } = await createKey(queryClient);
    const line = `claude mcp add --transport http kumbu ${window.location.origin}/mcp --header "Authorization: Bearer ${secret}"`;
    expect(screen.getByText(line)).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Copy key" }));
    expect(writeText).toHaveBeenLastCalledWith(secret);
    await userEvent.click(screen.getByRole("button", { name: "Copy command" }));
    expect(writeText).toHaveBeenLastCalledWith(line);

    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByText(secret)).not.toBeInTheDocument();
    expect(await screen.findByText("Laptop")).toBeInTheDocument();
    // Neither the URL nor storage nor the client-wide caches keep it.
    expect(router.state.location.pathname + router.state.location.search + router.state.location.hash).toBe("/keys");
    expect(JSON.stringify({ ...localStorage })).not.toContain(secret);
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(secret);
    await waitFor(() => expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.data))).not.toContain(secret));
    expect(JSON.stringify(queryClient.getQueryCache().getAll().map((q) => q.state.data))).not.toContain(secret);
  });

  it("asks once before leaving the page or closing the key unsaved", async () => {
    const { router } = await createKey();

    await act(() => router.navigate("/settings"));
    expect(screen.getByRole("alertdialog")).toHaveTextContent("You haven't saved your API key");
    await userEvent.click(screen.getByRole("button", { name: "Stay" }));
    expect(router.state.location.pathname).toBe("/keys");

    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.getByRole("alertdialog")).toHaveTextContent("You haven't saved your API key");
    await userEvent.click(screen.getByRole("button", { name: "Stay" }));
    expect(screen.getByText(secret)).toBeInTheDocument();

    const event = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);

    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    await userEvent.click(screen.getByRole("button", { name: "Leave without it" }));
    expect(screen.queryByText(secret)).not.toBeInTheDocument();
  });

  it("lets the page go without asking once the key was copied", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    const { router } = await createKey();
    await userEvent.click(screen.getByRole("button", { name: "Copy key" }));
    await act(() => router.navigate("/settings"));
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/settings");
  });
});

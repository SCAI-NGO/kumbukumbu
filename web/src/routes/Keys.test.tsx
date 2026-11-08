// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { timeText } from "../components/Sheet";
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
  expiresAt: null as string | null,
  publish: [] as string[],
};
const revoked = { ...listed, id: "k0", name: "Old phone", prefix: "Zz99Yy", vaults: { Home: "read" }, revokedAt: "2026-10-02T09:00:00Z" };

function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, vaults),
    "GET /api/keys": () => json(200, [revoked, listed]),
    "GET /api/connections": () => json(200, []),
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
  const router = renderApp("/access/keys", queryClient);
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
    renderApp("/access/keys");
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

  it("lists a maintain key as 'read, write and maintain', in the warning colour", async () => {
    api({ "GET /api/keys": () => json(200, [{ ...listed, vaults: { Work: "maintain", Home: "write" } }]) });
    renderApp("/access/keys");
    const item = (await screen.findByText("Laptop")).closest("li")!;
    expect(item).toHaveTextContent("Work: read, write and maintain");
    expect(item).toHaveTextContent("Home: read and write");
    expect(within(item).getByText("read, write and maintain")).toHaveClass("text-warning");
    expect(item.querySelectorAll(".text-warning")).toHaveLength(1);
  });

  it("shows when a key was last used, to the minute", async () => {
    api({ "GET /api/keys": () => json(200, [{ ...listed, lastUsedAt: "2026-10-05T14:37:00Z" }]) });
    renderApp("/access/keys");
    const item = (await screen.findByText("Laptop")).closest("li")!;
    expect(item).toHaveTextContent(`Last used ${timeText("2026-10-05T14:37:00Z")}`);
  });

  it("sorts the keys by least recent use on request: never used first, then the oldest use", async () => {
    api({
      "GET /api/keys": () =>
        json(200, [
          { ...listed, id: "a", name: "Alpha", lastUsedAt: "2026-10-20T09:00:00Z" },
          { ...listed, id: "b", name: "Bravo", lastUsedAt: null },
          { ...listed, id: "c", name: "Charlie", lastUsedAt: "2026-10-03T09:00:00Z" },
          { ...listed, id: "d", name: "Delta", lastUsedAt: "2026-10-10T09:00:00Z" },
          { ...listed, id: "e", name: "Echo", lastUsedAt: null },
        ]),
    });
    renderApp("/access/keys");
    await screen.findByText("Alpha");
    const names = () => [...screen.getByRole("region", { name: "API keys" }).querySelectorAll("li h2")].map((h) => h.textContent);
    const sort = screen.getByRole("group", { name: "Sort API keys" });

    expect(within(sort).getByRole("button", { name: "By creation" })).toHaveAttribute("aria-pressed", "true");
    expect(names()).toEqual(["Alpha", "Bravo", "Charlie", "Delta", "Echo"]);

    await userEvent.click(within(sort).getByRole("button", { name: "Least recently used first" }));
    expect(within(sort).getByRole("button", { name: "Least recently used first" })).toHaveAttribute("aria-pressed", "true");
    expect(names()).toEqual(["Bravo", "Echo", "Charlie", "Delta", "Alpha"]);

    await userEvent.click(within(sort).getByRole("button", { name: "By creation" }));
    expect(names()).toEqual(["Alpha", "Bravo", "Charlie", "Delta", "Echo"]);
  });

  it("marks a key paired from Obsidian as an Obsidian device", async () => {
    api({ "GET /api/keys": () => json(200, [{ ...listed, kind: "agent" }, { ...listed, id: "k5", name: "Work – Obsidian Laptop", kind: "device" }]) });
    renderApp("/access/keys");
    const device = (await screen.findByText("Work – Obsidian Laptop")).closest("li")!;
    expect(device).toHaveTextContent("Obsidian device");
    expect(screen.getByText("Laptop").closest("li")!).not.toHaveTextContent("Obsidian device");
  });

  it("revokes a key only after confirming", async () => {
    let revokedCalls = 0;
    api({ "DELETE /api/keys/k1": () => (revokedCalls++, noContent()) });
    renderApp("/access/keys");
    await userEvent.click(await screen.findByRole("button", { name: "Revoke Laptop" }));
    const confirm = screen.getByRole("alertdialog");
    expect(confirm).toHaveTextContent("Revoke Laptop?");
    expect(confirm).toHaveTextContent("Agents using this key lose access at once.");
    await userEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(revokedCalls).toBe(0);

    await userEvent.click(screen.getByRole("button", { name: "Revoke Laptop" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Revoke" }));
    await waitFor(() => expect(revokedCalls).toBe(1));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
  });

  it("needs a name and at least one vault with a right", async () => {
    api();
    renderApp("/access/keys");
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
    renderApp("/access/keys");
    const dialog = await openNewKey();
    await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Work" })).getByLabelText("Read and write"));
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Home" })).getByLabelText("Read"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Create key" }));

    expect(await within(dialog).findByText("You already have a key named 'Claude'.")).toBeInTheDocument();
    expect(bodies).toEqual([{ name: "Claude", vaults: { Work: "write", Home: "read" }, expiresInDays: 90 }]);
  });

  it("offers maintain in the warning colour with its sentence, and sends it", async () => {
    const bodies: unknown[] = [];
    api({ "POST /api/keys": async (r) => (bodies.push(await r.json()), json(409, { error: "No." })) });
    renderApp("/access/keys");
    const dialog = await openNewKey();
    await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
    const work = within(dialog).getByRole("group", { name: "Work" });
    const sentence = "Can change the vault's structure: the board's columns, checkpoints.";
    expect(within(work).getByText("Maintain")).toHaveClass("text-warning");
    expect(work).not.toHaveTextContent(sentence);

    await userEvent.click(within(work).getByLabelText("Maintain"));
    expect(within(work).getByText(sentence)).toHaveClass("text-warning");
    expect(within(dialog).getByRole("group", { name: "Home" })).not.toHaveTextContent(sentence);
    await userEvent.click(within(dialog).getByRole("button", { name: "Create key" }));

    await waitFor(() => expect(bodies).toEqual([{ name: "Claude", vaults: { Work: "maintain" }, expiresInDays: 90 }]));
  });

  it("suggests write for a new key: no vault starts at maintain, and the hint says maintain is a deliberate choice", async () => {
    api();
    renderApp("/access/keys");
    const dialog = await openNewKey();
    for (const vault of ["Work", "Home"]) {
      const group = within(dialog).getByRole("group", { name: vault });
      expect(within(group).getByLabelText("None")).toBeChecked();
      expect(within(group).getByLabelText("Maintain")).not.toBeChecked();
    }
    expect(dialog).toHaveTextContent("Read and write is enough for most agents; choose Maintain only for one you trust with the vault's structure.");
  });

  it("lets a key expire after 30, 90 or 365 days, 90 unless chosen otherwise, or never", async () => {
    const bodies: unknown[] = [];
    api({ "POST /api/keys": async (r) => (bodies.push(await r.json()), json(409, { error: "taken" })) });
    renderApp("/access/keys");
    const dialog = await openNewKey();
    await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Work" })).getByLabelText("Read"));
    const expiry = within(dialog).getByRole("group", { name: "Expires after" });
    expect(within(expiry).getByLabelText("90 days")).toBeChecked();

    for (const [i, [label, days]] of ([["30 days", 30], ["1 year", 365], ["Never", undefined]] as const).entries()) {
      await userEvent.click(within(expiry).getByLabelText(label));
      await userEvent.click(within(dialog).getByRole("button", { name: "Create key" }));
      await waitFor(() => expect(bodies).toHaveLength(i + 1));
      expect(bodies[i]).toEqual({ name: "Claude", vaults: { Work: "read" }, ...(days ? { expiresInDays: days } : {}) });
      await within(dialog).findByText("taken");
    }
  });

  it("shows when a key expires, and an expired key as expired, without a revoke button", async () => {
    const soon = { ...listed, id: "k3", name: "Soon", expiresAt: "2099-01-31T09:00:00Z" };
    const gone = { ...listed, id: "k4", name: "Gone", expiresAt: "2026-01-31T09:00:00Z" };
    api({ "GET /api/keys": () => json(200, [listed, soon, gone]) });
    renderApp("/access/keys");

    expect((await screen.findByText("Laptop")).closest("li")).toHaveTextContent("Doesn't expire");
    const soonItem = screen.getByText("Soon").closest("li")!;
    expect(soonItem).toHaveTextContent("Expires 31 Jan 2099");
    expect(within(soonItem).getByRole("button", { name: "Revoke Soon" })).toBeInTheDocument();
    const goneItem = screen.getByText("Gone").closest("li")!;
    expect(goneItem).toHaveTextContent("Expired 31 Jan 2026");
    expect(within(goneItem).queryByRole("button", { name: /Revoke/ })).not.toBeInTheDocument();
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
    // While it is shown, it is only in the reveal's state: the dialog that made it is gone, and its mutation with it.
    expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.data))).not.toContain(secret);
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
    expect(router.state.location.pathname + router.state.location.search + router.state.location.hash).toBe("/access/keys");
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
    expect(router.state.location.pathname).toBe("/access/keys");

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

describe("May publish", () => {
  const own = (name: string) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false });
  const garten = { ...own("Gartenarbeit"), owner: '"Alice"', ownerUsername: "alice", level: "write", shared: true, membership: "m1" };
  const hint = "Can publish this vault's notes behind a secret link, update and withdraw them.";

  function publishing(enabled: boolean, bodies: unknown[] = []) {
    return api({
      "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true, publishEnabled: enabled }),
      "GET /api/vaults": () => json(200, [own("Work"), own("Home"), garten]),
      "POST /api/keys": async (r) => (bodies.push(await r.json()), json(409, { error: "taken" })),
    });
  }

  it("offers May publish only for owned vaults", async () => {
    publishing(true);
    renderApp("/access/keys");
    const dialog = await openNewKey();
    const work = within(dialog).getByRole("group", { name: "Work" });
    const shared = within(dialog).getByRole("group", { name: /^Gartenarbeit/ });
    // Not before a right is chosen.
    expect(within(work).queryByLabelText("May publish")).not.toBeInTheDocument();

    await userEvent.click(within(work).getByLabelText("Read"));
    await userEvent.click(within(shared).getByLabelText("Read and write"));
    expect(within(work).getByLabelText("May publish")).not.toBeChecked();
    expect(within(shared).queryByLabelText("May publish")).not.toBeInTheDocument();
    expect(dialog).not.toHaveTextContent(hint);
  });

  it("offers no May publish for Skills", async () => {
    api({
      "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true, publishEnabled: true }),
      "GET /api/vaults": () => json(200, [own("Work"), { ...own("Skills"), kind: "skills" }]),
    });
    renderApp("/access/keys");
    const dialog = await openNewKey();
    const work = within(dialog).getByRole("group", { name: "Work" });
    const skills = within(dialog).getByRole("group", { name: "Skills" });
    await userEvent.click(within(work).getByLabelText("Read"));
    await userEvent.click(within(skills).getByLabelText("Read"));
    expect(within(work).getByLabelText("May publish")).toBeInTheDocument();
    expect(within(skills).queryByLabelText("May publish")).not.toBeInTheDocument();
  });

  it("sends May publish", async () => {
    const bodies: unknown[] = [];
    publishing(true, bodies);
    renderApp("/access/keys");
    const dialog = await openNewKey();
    await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
    const work = within(dialog).getByRole("group", { name: "Work" });
    await userEvent.click(within(work).getByLabelText("Read and write"));
    await userEvent.click(within(work).getByLabelText("May publish"));
    expect(within(work).getByText(hint)).toBeInTheDocument();
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Home" })).getByLabelText("Read"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Create key" }));

    await waitFor(() => expect(bodies).toEqual([{ name: "Claude", vaults: { Work: "write", Home: "read" }, publish: ["Work"], expiresInDays: 90 }]));
  });

  it("drops May publish with the right", async () => {
    const bodies: unknown[] = [];
    publishing(true, bodies);
    renderApp("/access/keys");
    const dialog = await openNewKey();
    await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
    const work = within(dialog).getByRole("group", { name: "Work" });
    await userEvent.click(within(work).getByLabelText("Read and write"));
    await userEvent.click(within(work).getByLabelText("May publish"));
    await userEvent.click(within(work).getByLabelText("None"));
    expect(within(work).queryByLabelText("May publish")).not.toBeInTheDocument();
    await userEvent.click(within(work).getByLabelText("Read"));
    expect(within(work).getByLabelText("May publish")).not.toBeChecked();
    await userEvent.click(within(work).getByLabelText("None"));
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Home" })).getByLabelText("Read"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Create key" }));

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toEqual({ name: "Claude", vaults: { Home: "read" }, expiresInDays: 90 });
  });

  it("hides May publish when publishing is off", async () => {
    publishing(false);
    renderApp("/access/keys");
    const dialog = await openNewKey();
    const work = within(dialog).getByRole("group", { name: "Work" });
    await userEvent.click(within(work).getByLabelText("Read and write"));
    expect(within(work).queryByLabelText("May publish")).not.toBeInTheDocument();
  });

  it("lists May publish on a key's vault line", async () => {
    api({ "GET /api/keys": () => json(200, [{ ...listed, publish: ["Work"] }]) });
    renderApp("/access/keys");
    const item = (await screen.findByText("Laptop")).closest("li")!;
    expect(item).toHaveTextContent("Work: read and write, may publish");
    expect(item).toHaveTextContent("Home: read");
    expect(item).not.toHaveTextContent("Home: read, may publish");
  });
});

describe("keys and vaults shared with the user", () => {
  const own = (name: string, extra: Record<string, unknown> = {}) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false, ...extra });
  const garten = own("Gartenarbeit", { owner: '"Alice"', ownerUsername: "alice", level: "read", canWrite: false, shared: true, membership: "m1" });
  const key = (vaults: Record<string, string>) => ({ ...listed, vaults });
  const warning = /Others can write into Gartenarbeit\. An agent that reads their text and also reaches your private vaults could be talked into copying from them\. Consider a separate key for shared vaults\./;

  it("shows a capped key as acts as read", async () => {
    api({ "GET /api/vaults": () => json(200, [own("Work"), garten]), "GET /api/keys": () => json(200, [key({ Gartenarbeit: "write", Work: "write" })]) });
    renderApp("/access/keys");
    const item = (await screen.findByText("Laptop")).closest("li")!;
    await waitFor(() => expect(item).toHaveTextContent("Gartenarbeit: write, acts as read while you can only read Gartenarbeit"));
    expect(within(item).getByText(/acts as read/).querySelector("em")).toHaveTextContent("Gartenarbeit");
    // Her own vault is hers: nothing caps it.
    expect(item).toHaveTextContent("Work: read and write");
  });

  it("caps maintain at her role, and shows a right within it as it is", async () => {
    const writer = { ...garten, level: "write", canWrite: true };
    api({ "GET /api/vaults": () => json(200, [writer]), "GET /api/keys": () => json(200, [key({ Gartenarbeit: "maintain" }), { ...key({ Gartenarbeit: "read" }), id: "k9", name: "Reader" }]) });
    renderApp("/access/keys");
    const item = (await screen.findByText("Laptop")).closest("li")!;
    await waitFor(() => expect(item).toHaveTextContent("Gartenarbeit: maintain, acts as write while you can only write Gartenarbeit"));
    expect(screen.getByText("Reader").closest("li")).toHaveTextContent("Gartenarbeit: read");
    expect(screen.getByText("Reader").closest("li")).not.toHaveTextContent("acts as");
  });

  it("warns when one key covers a shared writable vault and a private one", async () => {
    const writable = { ...garten, level: "write", canWrite: true };
    api({ "GET /api/vaults": () => json(200, [own("Work"), own("Shared", { shared: true }), writable]), "GET /api/keys": () => json(200, []) });
    renderApp("/access/keys");
    const dialog = await openNewKey();
    await userEvent.click(within(within(dialog).getByRole("group", { name: /^Gartenarbeit/ })).getByLabelText("Read and write"));
    expect(within(dialog).queryByRole("note")).not.toBeInTheDocument();
    // Two vaults others write into, and none of hers alone: nothing private to copy from.
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Shared" })).getByLabelText("Read"));
    expect(within(dialog).queryByRole("note")).not.toBeInTheDocument();
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Work" })).getByLabelText("Read"));
    expect(within(dialog).getByRole("note")).toHaveTextContent(warning);
    expect(within(dialog).getByRole("note")).toHaveClass("text-warning");
    await userEvent.click(within(within(dialog).getByRole("group", { name: /^Gartenarbeit/ })).getByLabelText("None"));
    // Her own shared vault: others write into it too.
    expect(within(dialog).getByRole("note")).toHaveTextContent("Others can write into Shared.");
  });

  it("warns for a listed key that covers both, but not where the shared vault is read-only for everyone", async () => {
    const writable = { ...garten, level: "write", canWrite: true };
    const closing = { ...writable, name: "Beete", level: "read", canWrite: false, closingAt: "2026-11-08T10:00:00Z" };
    api({
      "GET /api/vaults": () => json(200, [own("Work"), writable, closing]),
      "GET /api/keys": () => json(200, [key({ Gartenarbeit: "write", Work: "read" }), { ...key({ Beete: "read", Work: "read" }), id: "k8", name: "Closing" }]),
    });
    renderApp("/access/keys");
    await waitFor(() => expect(screen.getByText("Laptop").closest("li")).toHaveTextContent(warning));
    expect(screen.getByText("Closing").closest("li")).not.toHaveTextContent("Others can write");
  });

  it("lists a shared vault in the new key's dialog with its owner, and shows a right above her role as capped", async () => {
    api({ "GET /api/vaults": () => json(200, [own("Work"), garten]), "GET /api/keys": () => json(200, []) });
    renderApp("/access/keys");
    const dialog = await openNewKey();
    const group = within(dialog).getByRole("group", { name: /^Gartenarbeit/ });
    expect(group).toHaveTextContent('shared by "Alice" (alice)');
    // Any level may be chosen, up to maintain.
    expect(within(group).getByLabelText("Maintain")).toBeInTheDocument();
    await userEvent.click(within(group).getByLabelText("Read and write"));
    expect(group).toHaveTextContent("write, acts as read while you can only read Gartenarbeit");
  });
});

describe("Skills preselected for a new key", () => {
  const skillsVaults = [{ name: "Skills", notes: 0, kind: "skills" }, { name: "Work", notes: 3 }];
  const bodies: unknown[] = [];
  const withVaults = (list: unknown[]) =>
    api({
      "GET /api/vaults": () => json(200, list),
      "POST /api/keys": async (r) => (bodies.push(await r.json()), json(201, { key: secret, apiKey: { ...listed, id: "k2", name: "Claude", vaults: { Skills: "read" } } })),
    });
  const checked = (dialog: HTMLElement, vault: string) =>
    within(dialog)
      .getAllByRole("radio")
      .filter((r) => (r as HTMLInputElement).checked && within(within(dialog).getByRole("group", { name: vault })).queryAllByRole("radio").includes(r))
      .map((r) => (r as HTMLInputElement).value);

  it("preselects Skills with Read in a new key", async () => {
    bodies.length = 0;
    withVaults(skillsVaults);
    renderApp("/access/keys");
    const dialog = await openNewKey();
    await waitFor(() => expect(within(within(dialog).getByRole("group", { name: "Skills" })).getByLabelText("Read")).toBeChecked());
    expect(checked(dialog, "Work")).toEqual(["none"]);
    await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
    await userEvent.click(within(dialog).getByRole("button", { name: "Create key" }));
    await screen.findByText(secret);
    expect(bodies).toEqual([{ name: "Claude", vaults: { Skills: "read" }, expiresInDays: 90 }]);
  });

  it("preselects by the mark, not the name", async () => {
    withVaults([{ name: "Skills", notes: 1 }, { name: "Tools", notes: 0, kind: "skills" }]);
    renderApp("/access/keys");
    const dialog = await openNewKey();
    await waitFor(() => expect(within(within(dialog).getByRole("group", { name: "Tools" })).getByLabelText("Read")).toBeChecked());
    expect(within(within(dialog).getByRole("group", { name: "Skills" })).getByLabelText("None")).toBeChecked();
  });

  it("leaves Skills out when set to None", async () => {
    bodies.length = 0;
    withVaults(skillsVaults);
    renderApp("/access/keys");
    const dialog = await openNewKey();
    await waitFor(() => expect(within(within(dialog).getByRole("group", { name: "Skills" })).getByLabelText("Read")).toBeChecked());
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Skills" })).getByLabelText("None"));
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Work" })).getByLabelText("Read"));
    await userEvent.type(within(dialog).getByLabelText("Name"), "Claude");
    await userEvent.click(within(dialog).getByRole("button", { name: "Create key" }));
    await screen.findByText(secret);
    expect(bodies).toEqual([{ name: "Claude", vaults: { Work: "read" }, expiresInDays: 90 }]);
  });

  it("keeps the user's choice when the list arrives late", async () => {
    // The list is read again after the user chose (a refresh, a pending vault): her choice is not set back.
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    withVaults(skillsVaults);
    renderApp("/access/keys", queryClient);
    const dialog = await openNewKey();
    const group = await within(dialog).findByRole("group", { name: "Skills" });
    await waitFor(() => expect(within(group).getByLabelText("Read")).toBeChecked());
    await userEvent.click(within(group).getByLabelText("None"));
    await act(() => queryClient.invalidateQueries({ queryKey: ["vaults"] }));
    expect(within(group).getByLabelText("None")).toBeChecked();
  });
});

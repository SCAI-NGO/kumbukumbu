// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { timeText } from "../components/Sheet";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const workTree = { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: "inbox.md", path: "/Work/inbox.md", size: 10 }] } };

let codes = 0;
let bodies: unknown[] = [];
let inFlight = 0;
let maxInFlight = 0;
/** While set, each code request waits for it: a slow server. */
let gate: Promise<void> | null = null;
/** The server's clock against the browser's, in ms. */
let skew = 0;

let shared = false;
let level: string | null = null;

const device = {
  id: "d1",
  name: "Work – Obsidian Laptop",
  prefix: "Ab12Cd",
  vaults: { Work: "maintain" } as Record<string, string>,
  createdAt: "2026-10-01T09:00:00Z",
  lastUsedAt: "2026-11-01T09:00:00Z" as string | null,
  revokedAt: null as string | null,
  expiresAt: null as string | null,
  kind: "device",
};
/** What GET /api/keys answers: her keys, or a status to fail with. */
let keys: (typeof device)[] | number = [];
let revoked: string[] = [];

function api() {
  codes = 0;
  bodies = [];
  inFlight = 0;
  maxInFlight = 0;
  gate = null;
  skew = 0;
  revoked = [];
  return fakeApi({
    "GET /api/keys": () => (typeof keys === "number" ? json(keys, { error: "Something went wrong." }) : json(200, keys)),
    "DELETE /api/keys/*": (r) => {
      const id = new URL(r.url).pathname.split("/").at(-1)!;
      revoked.push(id);
      if (typeof keys !== "number") keys = keys.filter((k) => k.id !== id);
      return noContent();
    },
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal", pending: false, canWrite: level !== "read", ...(level ? { level, owner: '"Alice"', membership: "m1" } : {}), ...(shared ? { shared: true, members: 1 } : {}) }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, workTree),
    "GET /api/notes/*": (r) =>
      json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z", current: true }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "POST /api/vaults/Work/pairing": async (r) => {
      bodies.push(await r.json());
      const n = ++codes;
      maxInFlight = Math.max(maxInFlight, ++inFlight);
      if (gate) await gate;
      inFlight--;
      return json(201, { code: n === 1 ? "K7QM-4XDA" : `N3WC-0D${n}0`, expiresAt: new Date(Date.now() + skew + 10 * 60_000).toISOString(), expiresIn: 600 });
    },
  });
}

beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  shared = false;
  level = null;
  keys = [];
});

async function openDialog() {
  const u = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
  renderApp("/notes/Work/inbox.md");
  await u.click(await screen.findByRole("button", { name: "Open file tree" }));
  await u.click(await within(screen.getByRole("dialog", { name: "File tree" })).findByRole("button", { name: "Actions for Work" }));
  await u.click(await screen.findByRole("menuitem", { name: "Connect Obsidian…" }));
  return { u, dialog: await screen.findByRole("dialog", { name: 'Connect "Work" with Obsidian' }) };
}

describe("Connect Obsidian", () => {
  it("gives a code for a vault she shares, and names the plugin it needs", async () => {
    api();
    shared = true;
    const { dialog } = await openDialog();
    expect(dialog).toHaveTextContent("Work is shared with others. Pairing a shared vault needs the Kumbu² plugin 0.2.0 or newer.");
    await within(dialog).findByText("Pairing code");
    expect(codes).toBe(1);
  });

  it("says a member's device acts at her role, not as maintain", async () => {
    api();
    shared = true;
    level = "read";
    const { dialog } = await openDialog();
    expect(dialog).toHaveTextContent('The device acts at your role in Work, which is read: what that doesn\'t allow stays on the device, and the plugin lists it under "Not synced".');
    expect(within(dialog).queryByText("The device may read, write and maintain this vault.", { exact: false })).not.toBeInTheDocument();
    await within(dialog).findByText("Pairing code");
  });

  it("opens from the vault menu with the plugin, the server, the expiry and a code", async () => {
    api();
    const { dialog } = await openDialog();

    expect(dialog).toHaveTextContent("Works with an empty Obsidian vault or one that already has notes.");
    expect(dialog).toHaveTextContent("SCAI-NGO/kumbu-obsidian");
    expect(within(dialog).getByRole("button", { name: "Copy the plugin's name" })).toBeInTheDocument();
    expect(dialog).toHaveTextContent(window.location.origin);
    expect(within(dialog).getByRole("button", { name: "Copy the server address" })).toBeInTheDocument();
    expect(within(dialog).getByRole("radio", { name: "Never" })).toBeChecked();
    expect(await within(dialog).findByText("K7QM-4XDA")).toBeInTheDocument();
    expect(dialog).toHaveTextContent(/valid for (10:00|9:59) · single use/);
    expect(dialog).toHaveTextContent('Each device gets its own key, named "Work – Obsidian <device>". You can revoke it under API keys.');
    expect(dialog).toHaveTextContent("Re-encrypting this vault disconnects its Obsidian devices; connect them again afterwards.");
    expect(bodies).toEqual([{ expiresInDays: null }]);
  });

  it("says the device may maintain the vault, in the warning colour with its sentence", async () => {
    api();
    const { dialog } = await openDialog();

    const level = within(dialog).getByText("The device may read, write and maintain this vault.", { exact: false });
    expect(level).toHaveTextContent("The device may read, write and maintain this vault. Can change the vault's structure: the board's columns, checkpoints.");
    expect(level).toHaveClass("text-warning");
  });

  it("counts down, and at 0:00 offers only a new code", async () => {
    api();
    const { u, dialog } = await openDialog();
    await within(dialog).findByText("K7QM-4XDA");

    act(() => vi.advanceTimersByTime(61_000));
    expect(dialog).toHaveTextContent("valid for 8:59 · single use");

    act(() => vi.advanceTimersByTime(9 * 60_000));
    expect(dialog).toHaveTextContent(/New code/);
    await waitFor(() => expect(within(dialog).queryByText("K7QM-4XDA")).not.toBeInTheDocument());
    expect(dialog).not.toHaveTextContent("single use");

    await u.click(within(dialog).getByRole("button", { name: "New code" }));
    expect(await within(dialog).findByText("N3WC-0D20")).toBeInTheDocument();
  });

  it("offers to copy the pairing code while it is valid", async () => {
    api();
    const { u, dialog } = await openDialog();
    await within(dialog).findByText("K7QM-4XDA");
    // After userEvent.setup, which puts a clipboard stub of its own on navigator.
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });

    await u.click(within(dialog).getByRole("button", { name: "Copy the pairing code" }));
    expect(writeText).toHaveBeenCalledOnce();
    expect(writeText).toHaveBeenCalledWith("K7QM-4XDA");
  });

  it("has no copy button once the code has run out", async () => {
    api();
    const { dialog } = await openDialog();
    await within(dialog).findByText("K7QM-4XDA");
    expect(within(dialog).getByRole("button", { name: "Copy the pairing code" })).toBeInTheDocument();

    act(() => vi.advanceTimersByTime(10 * 60_000 + 1000));
    await waitFor(() => expect(within(dialog).queryByText("K7QM-4XDA")).not.toBeInTheDocument());
    expect(within(dialog).queryByRole("button", { name: "Copy the pairing code" })).not.toBeInTheDocument();
  });

  it("asks for a new code with the chosen expiry", async () => {
    api();
    const { u, dialog } = await openDialog();
    await within(dialog).findByText("K7QM-4XDA");

    await u.click(within(dialog).getByRole("radio", { name: "90 days" }));
    await waitFor(() => expect(bodies).toEqual([{ expiresInDays: null }, { expiresInDays: 90 }]));
    await u.click(within(dialog).getByRole("button", { name: "New code" }));

    await waitFor(() => expect(bodies).toEqual([{ expiresInDays: null }, { expiresInDays: 90 }, { expiresInDays: 90 }]));
  });

  it("asks for one code at a time and shows the newest, never one already replaced", async () => {
    api();
    const { u, dialog } = await openDialog();
    await within(dialog).findByText("K7QM-4XDA");
    let open!: () => void;
    gate = new Promise((r) => (open = r));

    await u.click(within(dialog).getByRole("radio", { name: "90 days" }));
    await u.click(within(dialog).getByRole("radio", { name: "30 days" }));
    await waitFor(() => expect(codes).toBe(2));
    act(() => open());

    await waitFor(() => expect(codes).toBe(3));
    expect(await within(dialog).findByText("N3WC-0D30")).toBeInTheDocument();
    expect(maxInFlight).toBe(1);
    expect(bodies.at(-1)).toEqual({ expiresInDays: 30 });
  });

  it("counts down from when the code arrived, whatever the browser's clock says", async () => {
    api();
    skew = -20 * 60_000;
    const { dialog } = await openDialog();

    expect(await within(dialog).findByText("K7QM-4XDA")).toBeInTheDocument();
    expect(dialog).toHaveTextContent(/valid for (10:00|9:59) · single use/);
  });

  it("lists this vault's device keys", async () => {
    keys = [device];
    api();
    const { dialog } = await openDialog();

    expect(await within(dialog).findByText("Work – Obsidian Laptop")).toBeInTheDocument();
    expect(dialog).toHaveTextContent(`Already connected: Work – Obsidian Laptop, last used ${timeText("2026-11-01T09:00:00Z")}.`);
    expect(within(dialog).getByRole("button", { name: "Revoke Work – Obsidian Laptop" })).toHaveTextContent("Revoke");
    expect(dialog).toHaveTextContent("Revoke one only if that device is gone or is being replaced.");
  });

  it("lists no other keys", async () => {
    const past = new Date(Date.now() - 24 * 3600_000).toISOString();
    keys = [
      { ...device, id: "a1", name: "Claude", kind: "agent" },
      { ...device, id: "d2", name: "Home – Obsidian Laptop", vaults: { Home: "maintain" } },
      { ...device, id: "d3", name: "Work – Obsidian Old phone", revokedAt: "2026-10-02T09:00:00Z" },
      { ...device, id: "d4", name: "Work – Obsidian Tablet", expiresAt: past },
      { ...device, id: "d5", name: "Work – Obsidian Desktop", lastUsedAt: null, expiresAt: new Date(Date.now() + 24 * 3600_000).toISOString() },
    ];
    api();
    const { dialog } = await openDialog();

    expect(await within(dialog).findByText("Work – Obsidian Desktop")).toBeInTheDocument();
    expect(dialog).toHaveTextContent("Already connected: Work – Obsidian Desktop, never used.");
    expect(within(dialog).getAllByRole("button", { name: /^Revoke/ })).toHaveLength(1);
    for (const name of ["Claude", "Home – Obsidian Laptop", "Work – Obsidian Old phone", "Work – Obsidian Tablet"]) {
      expect(dialog).not.toHaveTextContent(name);
    }
  });

  it("shows nothing when there is no device key", async () => {
    keys = [{ ...device, id: "a1", name: "Claude", kind: "agent" }];
    api();
    const { dialog } = await openDialog();
    await within(dialog).findByText("K7QM-4XDA");

    expect(dialog).not.toHaveTextContent("Already connected");
    expect(dialog).not.toHaveTextContent("Revoke one only if");
    expect(within(dialog).queryByRole("button", { name: /^Revoke/ })).not.toBeInTheDocument();
  });

  it("revokes only after confirming", async () => {
    keys = [device, { ...device, id: "d2", name: "Work – Obsidian Phone", lastUsedAt: null }];
    api();
    const { u, dialog } = await openDialog();
    await within(dialog).findByText("K7QM-4XDA");

    await u.click(await within(dialog).findByRole("button", { name: "Revoke Work – Obsidian Laptop" }));
    const confirm = screen.getByRole("alertdialog");
    expect(confirm).toHaveTextContent("Revoke Work – Obsidian Laptop?");
    expect(revoked).toEqual([]);

    await u.click(within(confirm).getByRole("button", { name: "Revoke" }));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(revoked).toEqual(["d1"]);
    await waitFor(() => expect(dialog).not.toHaveTextContent("Work – Obsidian Laptop"));
    expect(dialog).toHaveTextContent("Already connected: Work – Obsidian Phone, never used.");
    expect(within(dialog).getByText("K7QM-4XDA")).toBeInTheDocument();
    expect(codes).toBe(1);
  });

  it("leaves the dialog working when the keys can't be read", async () => {
    keys = 500;
    api();
    const { dialog } = await openDialog();

    expect(await within(dialog).findByText("K7QM-4XDA")).toBeInTheDocument();
    expect(dialog).not.toHaveTextContent("Already connected");
  });
});

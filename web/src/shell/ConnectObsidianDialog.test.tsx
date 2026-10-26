// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const workTree = { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: "inbox.md", path: "/Work/inbox.md", size: 10 }] } };

let codes = 0;
let bodies: unknown[] = [];
let inFlight = 0;
let maxInFlight = 0;
/** While set, each code request waits for it: a slow server. */
let gate: Promise<void> | null = null;
/** The server's clock against the browser's, in ms. */
let skew = 0;

function api() {
  codes = 0;
  bodies = [];
  inFlight = 0;
  maxInFlight = 0;
  gate = null;
  skew = 0;
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal", pending: false, canWrite: true }]),
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
});

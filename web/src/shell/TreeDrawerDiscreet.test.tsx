// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const root = { name: "Work", path: "/Work", folders: [], notes: [{ name: "inbox.md", path: "/Work/inbox.md", size: 10, kind: "note", mediaType: "text/markdown" }] };

type Vault = Record<string, unknown>;
const own: Vault = { name: "Work", notes: 1, embeddings: "internal" };
const shared: Vault = { name: "Work", notes: 1, embeddings: "internal", canWrite: true, level: "write", owner: '"Alice"', shared: true, membership: "m1" };

function api(revealed: boolean, vault: Vault, extra: Parameters<typeof fakeApi>[0] = {}, others: Vault[] = []) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed, until: null } }),
    "GET /api/vaults": () => json(200, [vault, ...others]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root }),
    "GET /api/vaults/Work/publications": () => json(200, { publications: [], unpublishable: [] }),
    "GET /api/notes/*": () => json(200, { path: "/Work/inbox.md", content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/version/*": () => json(200, { path: "/Work/inbox.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "POST /api/ws-ticket": () => json(503, { error: "later" }),
    ...extra,
  });
}

const drawer = () => screen.getByRole("dialog", { name: "File tree" });
const marks = (fetch: ReturnType<typeof fakeApi>) =>
  fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.method === "PUT" && new URL(r.url).pathname === "/api/account/discreet/vaults/Work");

async function openMenu(revealed: boolean) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  renderApp("/notes/Work/inbox.md", queryClient);
  await waitFor(() => expect(useDiscreet.getState().revealed).toBe(revealed));
  await waitFor(() => expect(queryClient.getQueryState(["vaults"])).toMatchObject({ status: "success", fetchStatus: "idle" }));
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for Work" }));
  return screen.getAllByRole("menuitem").map((i) => i.textContent);
}

beforeEach(() => {
  window.innerWidth = 390;
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
});

describe("Discreet in the vault's menu", () => {
  // The menu as it was before the entry: outside the mode nothing tells whether a vault is discreet.
  const before = ["New note here", "New folder…", "Upload here", "Trash", "Activity", "Checkpoints", "Calendar", "Create board", "Gallery", "Share…", "Rename vault…", "Connect Obsidian…", "Export as ZIP…", "Delete vault…"];

  // While hidden the server leaves discreet vaults out; a list that still names one is read again.
  it.each([
    ["without discreet vaults", own, []],
    ["with discreet vaults", { ...own, discreet: false }, [{ name: "Tagebuch", notes: 1, embeddings: "internal", discreet: true }]],
  ])("isn't there while they are hidden, %s, and the menu is as before", async (_, vault, others) => {
    api(false, vault, {}, others);
    const items = await openMenu(false);
    expect(items).not.toContain("Discreet");
    expect(items).not.toContain("Not discreet");
    expect(items).toEqual(before);
  });

  it("on a touch device lists Take photo after Upload here, and is otherwise as before", async () => {
    vi.stubGlobal("matchMedia", (q: string) => ({ matches: q === "(pointer: coarse)", addEventListener() {}, removeEventListener() {} }));
    localStorage.setItem("kumbu.installHint", "dismissed");
    api(false, own);
    const items = await openMenu(false);
    expect(items).toEqual([...before.slice(0, 3), "Take photo", ...before.slice(3)]);
  });

  it("offers Discreet for an unmarked vault, just before Delete vault…, and confirms the mark", async () => {
    const fetch = api(true, own, { "PUT /api/account/discreet/vaults/Work": () => noContent() });
    const items = await openMenu(true);
    expect(items.indexOf("Discreet")).toBe(items.indexOf("Delete vault…") - 1);
    expect(items).not.toContain("Not discreet");
    await userEvent.click(screen.getByRole("menuitem", { name: "Discreet" }));
    const status = drawer().querySelector("[aria-live='polite']")!;
    await waitFor(() => expect(status).toHaveTextContent("Marked as discreet"));
    const [put] = marks(fetch);
    expect(await put.json()).toEqual({ discreet: true });
  });

  it("offers Not discreet for a marked vault, and confirms the unmarking", async () => {
    const fetch = api(true, { ...own, discreet: true }, { "PUT /api/account/discreet/vaults/Work": () => noContent() });
    const items = await openMenu(true);
    expect(items).toContain("Not discreet");
    expect(items).not.toContain("Discreet");
    await userEvent.click(screen.getByRole("menuitem", { name: "Not discreet" }));
    await waitFor(() => expect(drawer().querySelector("[aria-live='polite']")).toHaveTextContent("No longer discreet"));
    expect(await marks(fetch)[0].json()).toEqual({ discreet: false });
  });

  it("is there for a vault shared with her, just before Leave this vault…", async () => {
    api(true, shared);
    const items = await openMenu(true);
    expect(items.indexOf("Discreet")).toBe(items.indexOf("Leave this vault…") - 1);
  });

  it("isn't there for the vault Skills", async () => {
    api(true, { ...own, kind: "skills" });
    const items = await openMenu(true);
    expect(items).not.toContain("Discreet");
    expect(items).not.toContain("Not discreet");
  });

  it("says so when the mark fails", async () => {
    api(true, own, { "PUT /api/account/discreet/vaults/Work": () => json(500, { error: "later" }) });
    await openMenu(true);
    await userEvent.click(screen.getByRole("menuitem", { name: "Discreet" }));
    expect(await within(drawer()).findByText("The server can't be reached right now. Try again in a moment.")).toBeInTheDocument();
    expect(drawer().querySelector("[aria-live='polite']")).not.toHaveTextContent("Marked as discreet");
  });

  it("is disabled while the mark is on its way", async () => {
    let release!: () => void;
    const held = new Promise<Response>((resolve) => (release = () => resolve(noContent())));
    api(true, own, { "PUT /api/account/discreet/vaults/Work": () => held });
    await openMenu(true);
    await userEvent.click(screen.getByRole("menuitem", { name: "Discreet" }));
    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for Work" }));
    expect(screen.getByRole("menuitem", { name: "Discreet" })).toBeDisabled();
    release();
    await waitFor(() => expect(drawer().querySelector("[aria-live='polite']")).toHaveTextContent("Marked as discreet"));
  });
});

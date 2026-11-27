// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { HttpError } from "../api/client";
import { useDiscreet } from "../discreet/state";
import { useOfflineMode } from "../offline/mode";
import { setOfflineSource, type OfflineSource } from "../offline/source";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown" });
const treeOf = (vault: string) => ({
  damaged: 0,
  root: { name: vault, path: `/${vault}`, folders: [{ name: "Drafts", path: `/${vault}/Drafts`, folders: [], notes: [note(`/${vault}/Drafts/plan.md`)] }], notes: [note(`/${vault}/inbox.md`)] },
});

function api(vault: Record<string, unknown>) {
  const name = vault.name as string;
  vi.stubGlobal(
    "fetch",
    fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: vault.discreet === true, until: null } }),
      "GET /api/vaults": () => json(200, [vault]),
      "GET /api/embeddings": () => json(200, []),
      [`GET /api/vaults/${name}/tree`]: () => json(200, treeOf(name)),
      "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
      "GET /api/version/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/version".length)), version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
      "GET /api/links/*": () => json(200, []),
      "GET /api/backlinks/*": () => json(200, []),
      "GET /api/changes": () => json(200, []),
      "POST /api/ws-ticket": () => json(503, { error: "later" }),
    }),
  );
}

/** The copy as the tree sees it: it keeps `kept` and answers for any other vault as the server would for one it hasn't. */
const copyKeeping = (kept: string | null) =>
  ({
    tree: (vault: string) => {
      if (vault !== kept) throw new HttpError(404, `${vault} is not kept on this device.`);
      return treeOf(vault);
    },
    held: () => true,
  }) as unknown as OfflineSource;

const drawer = () => screen.getByRole("dialog", { name: "File tree" });

/** The drawer read online, then the app offline with the copy, its tree asked again as the mode does at the switch. */
async function offlineIn(vault: Record<string, unknown>, kept: string | null) {
  const name = vault.name as string;
  api(vault);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  renderApp(`/notes/${name}/inbox.md`, queryClient);
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText("Drafts");
  setOfflineSource(copyKeeping(kept));
  act(() => useOfflineMode.setState({ offline: true }));
  await act(() => queryClient.invalidateQueries({ queryKey: ["tree"] }));
}

beforeEach(() => {
  window.innerWidth = 390;
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  setOfflineSource(null);
  useOfflineMode.setState({ offline: false });
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
});

describe("the tree offline", () => {
  it("shows only the line for a vault this device doesn't keep, with no entries", async () => {
    await offlineIn({ name: "Work", notes: 2, embeddings: "internal" }, null);
    expect(await within(drawer()).findByText("Work is not kept on this device.")).toBeInTheDocument();
    expect(within(drawer()).queryByText("Drafts")).toBeNull();
    expect(within(drawer()).queryByText("inbox")).toBeNull();
    expect(within(drawer()).queryByRole("button", { name: "New note" })).toBeNull();
  });

  it("shows the same line for a discreet vault shown offline, which this device never keeps", async () => {
    await offlineIn({ name: "Home", notes: 2, embeddings: "internal", discreet: true }, null);
    expect(await within(drawer()).findByText("Home is not kept on this device.")).toBeInTheDocument();
    expect(within(drawer()).queryByText("Drafts")).toBeNull();
    expect(within(drawer()).queryByText("inbox")).toBeNull();
  });

  it("online, keeps the entries read before when the tree can't be read again", async () => {
    api({ name: "Work", notes: 2, embeddings: "internal" });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/notes/Work/inbox.md", queryClient);
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await within(drawer()).findByText("Drafts");
    vi.stubGlobal("fetch", () => Promise.resolve(json(404, { error: "There is no vault Work." })));
    await act(() => queryClient.invalidateQueries({ queryKey: ["tree"] }));
    await waitFor(() => expect(queryClient.getQueryState(["tree", "Work"])?.status).toBe("error"));
    expect(within(drawer()).getByText("Drafts")).toBeInTheDocument();
  });

  it("shows a kept vault's tree as before", async () => {
    await offlineIn({ name: "Work", notes: 2, embeddings: "internal" }, "Work");
    expect(within(drawer()).getByText("Drafts")).toBeInTheDocument();
    expect(within(drawer()).getByText("inbox")).toBeInTheDocument();
    expect(within(drawer()).queryByText(/not kept on this device/)).toBeNull();
  });
});

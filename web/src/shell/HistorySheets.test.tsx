// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { checkpointExportUrl } from "../data/exportUrl";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const INBOX = "/Work/inbox.md";
const tree = { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: "inbox.md", path: INBOX, size: 10 }] } };
const contents: Record<string, string> = { v1: "# Inbox\n\nOld line.\n", v2: "# Inbox\n\nNew line.\n" };
const noteView = (version: string, current = true) => ({ path: INBOX, content: contents[version], version, lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z", current });

/** How often the open note was read without `?version=`: each read after the first would replace the text shown. */
let noteReads = 0;

function api(extra: Parameters<typeof fakeApi>[0] = {}) {
  noteReads = 0;
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/Work/inbox.md": (r) => {
      const version = new URL(r.url).searchParams.get("version");
      if (!version) noteReads++;
      return json(200, version ? noteView(version, version === "v2") : noteView("v2"));
    },
    "GET /api/version/Work/inbox.md": () => json(200, { path: INBOX, version: "v2", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    ...extra,
  });
}

const sheet = (name: string) => screen.getByRole("dialog", { name });

describe("a note's history", () => {
  it("marks a version written from Obsidian as an Obsidian device's", async () => {
    api({
      "GET /api/history/Work/inbox.md": () =>
        json(200, [
          { version: "v2", writtenAt: "2026-10-25T09:00:00Z", writer: "Work – Obsidian Laptop", lines: 3, size: 21, current: true, changeset: null, writerKind: "device" },
          { version: "v1", writtenAt: "2026-10-24T09:00:00Z", writer: "liz", lines: 3, size: 21, current: false, changeset: null, writerKind: "agent" },
        ]),
    });
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "History" }));

    const versions = await within(await screen.findByRole("dialog", { name: "History" })).findByRole("list", { name: "Versions" });
    const rows = within(versions).getAllByRole("button");
    expect(rows[0]).toHaveTextContent("by Work – Obsidian Laptop · Obsidian device");
    expect(rows[1]).not.toHaveTextContent("Obsidian device");
  });

  it("lists the versions, shows an earlier one and its changes, and restores it", async () => {
    const fetch = api({
      "GET /api/history/Work/inbox.md": () =>
        json(200, [
          { version: "v2", writtenAt: "2026-10-25T09:00:00Z", writer: "liz", lines: 3, size: 21, current: true, changeset: null },
          { version: "v1", writtenAt: "2026-10-24T09:00:00Z", writer: "ada", lines: 3, size: 1500, current: false, changeset: null },
        ]),
      "POST /api/restore/Work/inbox.md": () => json(200, { ...noteView("v1"), version: "v3" }),
    });
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "History" }));

    const versions = await within(await screen.findByRole("dialog", { name: "History" })).findByRole("list", { name: "Versions" });
    const rows = within(versions).getAllByRole("button");
    expect(rows[0]).toHaveTextContent("Current version");
    expect(rows[0]).toHaveTextContent("by liz");
    expect(rows[1]).toHaveTextContent("by ada");
    expect(rows[1]).toHaveTextContent("1.5 KB");

    await userEvent.click(rows[1]);
    const version = await screen.findByRole("article", { name: "This version" });
    expect(await within(version).findByText("Old line.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Changes since" }));
    const diff = screen.getByRole("list", { name: "Changes since" });
    expect(within(diff).getByText("Old line.").closest("li")).toHaveAttribute("data-kind", "removed");
    expect(within(diff).getByText("New line.").closest("li")).toHaveAttribute("data-kind", "added");

    await userEvent.click(screen.getByRole("button", { name: "Restore this version" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    const restore = fetch.mock.calls.map(([r]) => r as Request).find((r) => r.method === "POST");
    expect(await restore!.json()).toEqual({ version: "v1", expected: "v2" });
  });

  it("says when a newer version arrived before the restore, and shows the list as it is now", async () => {
    let historyReads = 0;
    api({
      "GET /api/history/Work/inbox.md": () => {
        historyReads++;
        return json(200, [
          { version: "v2", writtenAt: "2026-10-25T09:00:00Z", writer: "liz", lines: 3, size: 21, current: true, changeset: null },
          { version: "v1", writtenAt: "2026-10-24T09:00:00Z", writer: "ada", lines: 3, size: 15, current: false, changeset: null },
        ]);
      },
      "POST /api/restore/Work/inbox.md": () => json(409, { error: "/Work/inbox.md has changed since version v2 (it is now v5).", current: { content: "x", version: "v5" } }),
    });
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "History" }));
    const versions = await within(await screen.findByRole("dialog", { name: "History" })).findByRole("list", { name: "Versions" });
    await userEvent.click(within(versions).getAllByRole("button")[1]);
    await userEvent.click(await screen.findByRole("button", { name: "Restore this version" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("A newer version arrived meanwhile");
    await waitFor(() => expect(historyReads).toBe(2));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("doesn't read the open note again, which would replace the text on screen", async () => {
    api({
      "GET /api/history/Work/inbox.md": () =>
        json(200, [
          { version: "v2", writtenAt: "2026-10-25T09:00:00Z", writer: "liz", lines: 3, size: 21, current: true, changeset: null },
          { version: "v1", writtenAt: "2026-10-24T09:00:00Z", writer: "ada", lines: 3, size: 15, current: false, changeset: null },
        ]),
    });
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "History" }));
    const versions = await within(await screen.findByRole("dialog", { name: "History" })).findByRole("list", { name: "Versions" });
    await userEvent.click(within(versions).getAllByRole("button")[1]);
    await userEvent.click(await screen.findByRole("button", { name: "Changes since" }));
    await screen.findByRole("list", { name: "Changes since" });
    await new Promise((r) => setTimeout(r, 50));

    expect(noteReads).toBe(1);
  });

  it("offers no restore of the current version, and goes back to the list", async () => {
    api({
      "GET /api/history/Work/inbox.md": () => json(200, [{ version: "v2", writtenAt: "2026-10-25T09:00:00Z", writer: "liz", lines: 3, size: 21, current: true, changeset: null }]),
    });
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "History" }));
    await userEvent.click(await within(await screen.findByRole("list", { name: "Versions" })).findByRole("button"));

    expect(await screen.findByRole("article", { name: "This version" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Restore this version" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "All versions" }));
    expect(screen.getByRole("list", { name: "Versions" })).toBeInTheDocument();
  });
});

async function openVaultMenu(item: string) {
  renderApp("/notes/Work/inbox.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await userEvent.click(await screen.findByRole("button", { name: "Actions for Work" }));
  await userEvent.click(screen.getByRole("menuitem", { name: item }));
  // The sheets are loaded on first use.
  await screen.findByRole("dialog", { name: item });
}

describe("the trash", () => {
  it("lists removed notes with when they go for good, and restores one", async () => {
    const trash = {
      retentionDays: 30,
      notes: [
        {
          path: "/Work/projects/old.md",
          version: "t1",
          deletedAt: new Date(Date.now() - 2 * 86_400_000).toISOString(),
          deletedBy: "liz",
          expiresAt: new Date(Date.now() + 28 * 86_400_000).toISOString(),
          lines: 1,
          size: 4,
        },
      ],
    };
    let restored = false;
    const fetch = api({
      "GET /api/vaults/Work/trash": () => json(200, restored ? { ...trash, notes: [] } : trash),
      "POST /api/restore/Work/projects/old.md": () => {
        restored = true;
        return json(200, { path: "/Work/projects/old.md", content: "old\n", version: "t1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z", current: true });
      },
    });
    await openVaultMenu("Trash");

    const dialog = sheet("Trash");
    expect(within(dialog).getByText(/stay here for 30 days, then they are gone for good/)).toBeInTheDocument();
    const list = await within(dialog).findByRole("list", { name: "Removed notes" });
    expect(list).toHaveTextContent("old");
    expect(list).toHaveTextContent("Removed 2 days ago by liz");
    expect(list).toHaveTextContent("gone for good in 28 days");

    await userEvent.click(within(list).getByRole("button", { name: "Restore old" }));

    expect(await within(dialog).findByRole("status")).toHaveTextContent("Restored old to Work / projects.");
    expect(await within(dialog).findByText("The trash is empty.")).toBeInTheDocument();
    const restore = fetch.mock.calls.map(([r]) => r as Request).find((r) => r.method === "POST");
    expect(await restore!.json()).toEqual({ version: "t1" });
    // The open note is another one: it isn't read again.
    expect(noteReads).toBe(1);
  });

  it("says why a restore didn't go through", async () => {
    api({
      "GET /api/vaults/Work/trash": () =>
        json(200, { retentionDays: 30, notes: [{ path: INBOX, version: "t1", deletedAt: "2026-10-24T09:00:00Z", deletedBy: "ada", expiresAt: "2026-11-23T09:00:00Z", lines: 1, size: 4 }] }),
      "POST /api/restore/Work/inbox.md": () => json(400, { error: "restore: /Work/inbox.md exists; to go back to an earlier version, give one from 'history /Work/inbox.md'" }),
    });
    await openVaultMenu("Trash");

    await userEvent.click(await within(sheet("Trash")).findByRole("button", { name: "Restore inbox" }));

    expect(await within(sheet("Trash")).findByText(/inbox.md exists/)).toBeInTheDocument();
  });
});

describe("checkpoints", () => {
  const checkpoint = { name: "before-cleanup", scope: "/Work/", createdBy: "ada", createdAt: "2026-10-24T09:00:00Z", notes: 3 };

  it("are listed and made with a name", async () => {
    let list = [checkpoint];
    const fetch = api({
      "GET /api/vaults/Work/checkpoints": () => json(200, { limit: 10, checkpoints: list }),
      "POST /api/vaults/Work/checkpoints": async (r) => {
        const { name } = (await r.json()) as { name: string };
        const made = { ...checkpoint, name, createdAt: new Date().toISOString() };
        list = [...list, made];
        return json(201, made);
      },
    });
    await openVaultMenu("Checkpoints");

    const dialog = sheet("Checkpoints");
    const items = await within(dialog).findByRole("list", { name: "Checkpoints" });
    expect(items).toHaveTextContent("before-cleanup");
    expect(items).toHaveTextContent("/Work/ · 3 notes");
    expect(within(dialog).getByText("1 of 10 checkpoints")).toBeInTheDocument();

    const name = within(dialog).getByLabelText("New checkpoint");
    await userEvent.type(name, "with space");
    await userEvent.click(within(dialog).getByRole("button", { name: "Make" }));
    expect(within(dialog).getByText(/A name has letters, digits/)).toBeInTheDocument();
    expect(fetch.mock.calls.some(([r]) => (r as Request).method === "POST")).toBe(false);

    await userEvent.clear(name);
    await userEvent.type(name, "v1-done");
    await userEvent.click(within(dialog).getByRole("button", { name: "Make" }));

    expect(await within(dialog).findByText("v1-done")).toBeInTheDocument();
    expect(within(dialog).getByText("2 of 10 checkpoints")).toBeInTheDocument();
  });

  it("restores one after a preview that says exactly what changes", async () => {
    const fetch = api({
      "GET /api/vaults/Work/checkpoints": () => json(200, { limit: 10, checkpoints: [checkpoint] }),
      "POST /api/vaults/Work/checkpoints/before-cleanup/preview": () =>
        json(200, {
          scope: "/Work/",
          changes: [
            { kind: "changed", path: "/Work/inbox.md", from: null, added: 1, removed: 2 },
            { kind: "trash", path: "/Work/since.md", from: null, added: null, removed: null },
            { kind: "back", path: "/Work/gone.md", from: null, added: null, removed: null },
          ],
          rewrittenLinks: 0,
          rewrittenIn: [],
          broken: ["/Other/x.md:3: [[since]] (now /Work/since.md, going to the trash)"],
          plan: "p1an",
        }),
      "POST /api/vaults/Work/checkpoints/before-cleanup/restore": () =>
        json(200, { message: "restored /Work/ to checkpoint 'before-cleanup': 1 changed, 1 back, 0 moved back, 1 to the trash" }),
    });
    await openVaultMenu("Checkpoints");
    await userEvent.click(await within(sheet("Checkpoints")).findByRole("button", { name: "Restore before-cleanup" }));

    const dialog = sheet("Restore before-cleanup?");
    const changes = await within(dialog).findByRole("list", { name: "What changes" });
    expect(within(dialog).getByText(/Restoring makes \/Work\/ match the checkpoint exactly/)).toBeInTheDocument();
    expect(changes).toHaveTextContent("/Work/inbox.md gets its text from then (+1 −2 lines)");
    expect(changes).toHaveTextContent("/Work/since.md goes to the trash (it was made since)");
    expect(changes).toHaveTextContent("/Work/gone.md comes back (it was removed since)");
    expect(within(dialog).getByText(/would lead nowhere/)).toBeInTheDocument();
    expect(fetch.mock.calls.some(([r]) => (r as Request).url.endsWith("/restore"))).toBe(false);

    await userEvent.click(within(dialog).getByRole("button", { name: "Restore 3 notes" }));

    expect(await within(sheet("Checkpoints")).findByRole("status")).toHaveTextContent("Restored /Work/ to checkpoint 'before-cleanup': 1 changed");
    const restore = fetch.mock.calls.map(([r]) => r as Request).find((r) => r.url.endsWith("/restore"));
    expect(await restore!.json()).toEqual({ plan: "p1an" });
  });

  it("says when there is nothing to restore", async () => {
    api({
      "GET /api/vaults/Work/checkpoints": () => json(200, { limit: 10, checkpoints: [checkpoint] }),
      "POST /api/vaults/Work/checkpoints/before-cleanup/preview": () => json(200, { scope: "/Work/", changes: [], rewrittenLinks: 0, rewrittenIn: [], broken: [], plan: null }),
    });
    await openVaultMenu("Checkpoints");
    await userEvent.click(await within(sheet("Checkpoints")).findByRole("button", { name: "Restore before-cleanup" }));

    expect(await within(sheet("Restore before-cleanup?")).findByText(/already matches this checkpoint/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Restore \d/ })).not.toBeInTheDocument();
  });

  it("removes one for good, after saying so", async () => {
    let list = [checkpoint];
    const fetch = api({
      "GET /api/vaults/Work/checkpoints": () => json(200, { limit: 10, checkpoints: list }),
      "DELETE /api/vaults/Work/checkpoints/before-cleanup": () => {
        list = [];
        return noContent();
      },
    });
    await openVaultMenu("Checkpoints");
    await userEvent.click(await within(sheet("Checkpoints")).findByRole("button", { name: "Remove before-cleanup" }));

    const dialog = sheet("Remove before-cleanup?");
    expect(within(dialog).getByRole("note")).toHaveTextContent("Removing a checkpoint is permanent");
    await userEvent.click(within(dialog).getByRole("button", { name: "Remove for good" }));

    expect(await within(sheet("Checkpoints")).findByRole("status")).toHaveTextContent("Removed checkpoint before-cleanup.");
    await waitFor(() => expect(within(sheet("Checkpoints")).queryByRole("list", { name: "Checkpoints" })).not.toBeInTheDocument());
    expect(fetch.mock.calls.some(([r]) => (r as Request).method === "DELETE")).toBe(true);
  });

  it("opens a checkpoint and exports it as ZIP", async () => {
    const downloads: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      if (this.hasAttribute("download")) downloads.push(this.getAttribute("href")!);
    });
    const fetch = api({
      "GET /api/vaults/Work/checkpoints": () => json(200, { limit: 10, checkpoints: [checkpoint] }),
      "GET /api/vaults/Work/checkpoints/before-cleanup/tree": () =>
        json(200, { checkpoint: { name: "before-cleanup", createdAt: checkpoint.createdAt, notes: 3 }, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    await openVaultMenu("Checkpoints");
    const dialog = sheet("Checkpoints");

    await userEvent.click(await within(dialog).findByRole("button", { name: "Export before-cleanup as ZIP" }));
    expect(downloads).toEqual([checkpointExportUrl("Work", "before-cleanup")]);
    expect(fetch.mock.calls.every(([r]) => (r as Request).method === "GET")).toBe(true);

    await userEvent.click(within(dialog).getByRole("button", { name: "Open before-cleanup" }));
    expect(await screen.findByRole("region", { name: "Checkpoint" })).toHaveTextContent("Checkpoint before-cleanup");
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const INBOX = "/Work/inbox.md";
const tree = { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: "inbox.md", path: INBOX, size: 10 }] } };
const noteView = { path: INBOX, content: "# Inbox\n", version: "i1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z", current: true };

type File = { path: string; kind: string; from?: string | null; to?: string | null; version?: string | null; before?: string | null; beforeLost?: boolean; trashed?: boolean };
const file = (f: File) => ({ from: null, to: null, version: "v2", before: "v1", beforeLost: false, trashed: false, ...f });
const row = (writer: string, writerKind: string | null, files: File[], to = "2026-10-26T09:10:00Z", from = "2026-10-26T09:00:00Z", restore = false) => ({
  id: `${writer}-${to}`,
  key: `c:${writer}-${to}`,
  restore,
  writer,
  writerKind,
  from,
  to,
  files: files.map(file),
});

function api(activity: (r: Request) => Response, extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/Work/inbox.md": () => json(200, noteView),
    "GET /api/version/Work/inbox.md": () => json(200, { path: INBOX, version: "i1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/vaults/Work/activity": activity,
    ...extra,
  });
}

async function openActivity() {
  renderApp("/notes/Work/inbox.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await userEvent.click(await screen.findByRole("button", { name: "Actions for Work" }));
  await userEvent.click(screen.getByRole("menuitem", { name: "Activity" }));
  return screen.findByRole("dialog", { name: "Activity" });
}

const rowsOf = async (dialog: HTMLElement) => within(await within(dialog).findByRole("list", { name: "Changes" })).getAllByRole("listitem");

describe("the vault's activity", () => {
  it("sits between Trash and Checkpoints in the vault menu", async () => {
    api(() => json(200, { rows: [], more: false }));
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Actions for Work" }));
    const items = screen.getAllByRole("menuitem").map((i) => i.textContent);
    expect(items.slice(items.indexOf("Trash"), items.indexOf("Trash") + 3)).toEqual(["Trash", "Activity", "Checkpoints"]);
  });

  it("lists_rows_with_writer_and_counts", async () => {
    api(() =>
      json(200, {
        rows: [row("liz", "agent", [{ path: "/Work/plan.md", kind: "changed" }, { path: "/Work/idea.md", kind: "added", before: null }])],
        more: false,
      }),
    );
    const dialog = await openActivity();

    const [first] = await rowsOf(dialog);
    expect(first).toHaveTextContent("Agent liz");
    expect(first).toHaveTextContent("1 changed · 1 added");
    expect(first).not.toHaveTextContent("renamed");
  });

  it("says_you_in_obsidian_for_a_device_row", async () => {
    api(() =>
      json(200, {
        rows: [row("Work – Obsidian Laptop", "device", [{ path: "/Work/plan.md", kind: "changed" }]), row("ada", "user", [{ path: "/Work/a.md", kind: "changed" }], "2026-10-25T09:00:00Z", "2026-10-25T09:00:00Z")],
        more: false,
      }),
    );
    const dialog = await openActivity();

    const [device, own] = await rowsOf(dialog);
    expect(device).toHaveTextContent("You in Obsidian (Laptop)");
    expect(own).toHaveTextContent(/^You/);
  });

  it("expands_a_row_into_its_files", async () => {
    api(() =>
      json(200, {
        rows: [row("liz", "agent", [{ path: "/Work/plan.md", kind: "renamed", from: "/Work/old.md" }, { path: "/Work/gone.md", kind: "deleted", version: null, trashed: true }])],
        more: false,
      }),
    );
    const dialog = await openActivity();

    const [first] = await rowsOf(dialog);
    await userEvent.click(within(first).getByRole("button", { expanded: false }));

    const files = within(first).getByRole("list", { name: "Files" });
    expect(files).toHaveTextContent("old.md → plan.md");
    expect(files).toHaveTextContent("renamed");
    expect(files).toHaveTextContent("deleted");
  });

  it("shows_no_changes_yet_for_an_empty_vault", async () => {
    api(() => json(200, { rows: [], more: false }));
    const dialog = await openActivity();

    expect(await within(dialog).findByText("No changes yet.")).toBeInTheDocument();
  });

  it("loads_more_with_before", async () => {
    const fetch = api((r) =>
      new URL(r.url).searchParams.get("before")
        ? json(200, { rows: [row("ada", "user", [{ path: "/Work/old.md", kind: "added", before: null }], "2026-10-20T09:00:00Z", "2026-10-20T09:00:00Z")], more: false })
        : json(200, { rows: [row("liz", "agent", [{ path: "/Work/plan.md", kind: "changed" }], "2026-10-26T09:10:00Z")], more: true }),
    );
    const dialog = await openActivity();
    await rowsOf(dialog);

    await userEvent.click(within(dialog).getByRole("button", { name: "Load more" }));

    expect(await within(dialog).findByText("You")).toBeInTheDocument();
    const asked = fetch.mock.calls.map(([r]) => new URL((r as Request).url)).filter((u) => u.pathname === "/api/vaults/Work/activity");
    expect(asked.at(-1)!.searchParams.get("before")).toBe("2026-10-26T09:10:00Z");
    expect(asked.at(-1)!.searchParams.get("key")).toBe("c:liz-2026-10-26T09:10:00Z");
    expect(within(dialog).queryByRole("button", { name: "Load more" })).not.toBeInTheDocument();
  });

  it("shows_the_limit_line", async () => {
    api(() => json(200, { rows: [row("liz", "agent", [{ path: "/Work/plan.md", kind: "changed" }])], more: false }));
    const dialog = await openActivity();

    expect(await within(dialog).findByText("Older changes may be incomplete: Kumbu² keeps the last 10 versions of each note.")).toBeInTheDocument();
  });

  it("opens a file's comparison and goes back to the list", async () => {
    api(() => json(200, { rows: [row("liz", "agent", [{ path: "/Work/plan.md", kind: "changed" }])], more: false }), {
      "GET /api/notes/Work/plan.md": (r) => {
        const v = new URL(r.url).searchParams.get("version");
        return json(200, { path: "/Work/plan.md", content: v === "v1" ? "one\n" : "two\n", version: v ?? "v2", lastWriter: "liz", lastWriteAt: "2026-10-26T09:10:00Z", current: v !== "v1" });
      },
    });
    const dialog = await openActivity();
    const [first] = await rowsOf(dialog);
    await userEvent.click(within(first).getByRole("button", { expanded: false }));
    await userEvent.click(within(first).getByRole("button", { name: /plan\.md/ }));

    const compare = await screen.findByRole("dialog", { name: "plan.md" });
    expect(await within(compare).findByText("one")).toBeInTheDocument();
    await userEvent.click(within(compare).getByRole("button", { name: "All changes" }));
    expect(await screen.findByRole("dialog", { name: "Activity" })).toBeInTheDocument();
  });

  it("says a deleted note was deleted and opens the trash", async () => {
    api(() => json(200, { rows: [row("liz", "agent", [{ path: "/Work/gone.md", kind: "deleted", version: null, trashed: true }])], more: false }), {
      "GET /api/vaults/Work/trash": () => json(200, { retentionDays: 30, notes: [] }),
    });
    const dialog = await openActivity();
    const [first] = await rowsOf(dialog);
    await userEvent.click(within(first).getByRole("button", { expanded: false }));
    await userEvent.click(within(first).getByRole("button", { name: /gone\.md/ }));

    const compare = await screen.findByRole("dialog", { name: "gone.md" });
    expect(within(compare).getByText("Deleted in this change")).toBeInTheDocument();
    await userEvent.click(within(compare).getByRole("button", { name: "Open the trash" }));
    expect(await screen.findByRole("dialog", { name: "Trash" })).toBeInTheDocument();
  });

  it("shows a rename as it was, also when the note moved on since", async () => {
    api(() => json(200, { rows: [row("liz", "agent", [{ path: "/Work/final.md", kind: "renamed", from: "/Work/old.md", to: "/Work/plan.md" }])], more: false }));
    const dialog = await openActivity();
    const [first] = await rowsOf(dialog);
    await userEvent.click(within(first).getByRole("button", { expanded: false }));

    expect(within(first).getByRole("list", { name: "Files" })).toHaveTextContent("old.md → plan.md");
  });

  it("marks a restore", async () => {
    api(() => json(200, { rows: [row("ada", "user", [{ path: "/Work/plan.md", kind: "changed" }], undefined, undefined, true)], more: false }));
    const dialog = await openActivity();

    const [first] = await rowsOf(dialog);
    expect(first).toHaveTextContent("You · restore");
  });
});

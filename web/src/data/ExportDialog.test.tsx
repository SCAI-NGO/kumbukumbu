// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const tree = { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: "inbox.md", path: "/Work/inbox.md", size: 10 }] } };

function api() {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () =>
      json(200, [
        { name: "Work", notes: 3, embeddings: "internal", pending: false },
        { name: "My Diary", notes: 1, embeddings: "internal", pending: false },
        { name: "Ideen", notes: 0, embeddings: "internal", pending: false },
      ]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/Work/inbox.md": () => json(200, { path: "/Work/inbox.md", content: "# Inbox\n", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z", current: true }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
  });
}

let downloads: string[] = [];

beforeEach(() => {
  downloads = [];
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    if (this.hasAttribute("download")) downloads.push(this.getAttribute("href")!);
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("Export as ZIP in the vault menu", () => {
  it("downloads the active vault at once", async () => {
    api();
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Actions for Work" }));

    await userEvent.click(screen.getByRole("menuitem", { name: "Export as ZIP" }));

    expect(downloads).toEqual(["/api/vaults/Work/export"]);
  });
});

describe("Export my vaults… in Settings", () => {
  async function openDialog() {
    api();
    renderApp("/settings");
    const section = await screen.findByRole("region", { name: "Your data" });
    expect(section).toHaveTextContent(/one folder of Markdown notes per vault/);
    await userEvent.click(within(section).getByRole("button", { name: "Export my vaults…" }));
    return screen.findByRole("dialog", { name: "Export my vaults" });
  }

  it("lists every vault ticked, with its note count, and exports them all", async () => {
    const dialog = await openDialog();
    const work = await within(dialog).findByRole("checkbox", { name: /Work/ });
    expect(work).toBeChecked();
    expect(within(dialog).getByRole("checkbox", { name: /My Diary/ })).toBeChecked();
    expect(within(dialog).getByRole("checkbox", { name: /Ideen/ })).toBeChecked();
    expect(within(dialog).getByText("3 notes")).toBeInTheDocument();
    expect(within(dialog).getByText("1 note")).toBeInTheDocument();
    expect(within(dialog).getByText("0 notes")).toBeInTheDocument();

    await userEvent.click(within(dialog).getByRole("button", { name: "Export" }));

    expect(downloads).toEqual(["/api/export?vault=Work&vault=My%20Diary&vault=Ideen"]);
    expect(screen.queryByRole("dialog", { name: "Export my vaults" })).not.toBeInTheDocument();
  });

  it("leaves out an unticked vault, and a single one uses the vault's own export", async () => {
    const dialog = await openDialog();
    await userEvent.click(await within(dialog).findByRole("checkbox", { name: /My Diary/ }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Export" }));
    expect(downloads).toEqual(["/api/export?vault=Work&vault=Ideen"]);

    const again = await openDialogAgain();
    await userEvent.click(await within(again).findByRole("checkbox", { name: /Work/ }));
    await userEvent.click(within(again).getByRole("checkbox", { name: /Ideen/ }));
    await userEvent.click(within(again).getByRole("button", { name: "Export" }));
    expect(downloads.at(-1)).toBe("/api/vaults/My%20Diary/export");
  });

  it("can't export with nothing ticked", async () => {
    const dialog = await openDialog();
    for (const name of [/Work/, /My Diary/, /Ideen/]) await userEvent.click(await within(dialog).findByRole("checkbox", { name }));

    expect(within(dialog).getByRole("button", { name: "Export" })).toBeDisabled();
  });
});

async function openDialogAgain() {
  await userEvent.click(within(screen.getByRole("region", { name: "Your data" })).getByRole("button", { name: "Export my vaults…" }));
  return screen.findByRole("dialog", { name: "Export my vaults" });
}

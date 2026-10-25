// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { summaryText } from "./UploadSheet";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });
const workTree = {
  damaged: 0,
  root: {
    name: "Work",
    path: "/Work",
    folders: [{ name: "projects", path: "/Work/projects", folders: [], notes: [note("/Work/projects/plan.md")] }],
    notes: [note("/Work/inbox.md")],
  },
};

interface Sent {
  url: string;
  onConflict: string;
  paths: string[];
}

/**
 * The API, with an upload that behaves as the server's: `ask` writes nothing of a request that meets an existing
 * note (and lists the conflicts), `skip` and `replace` write. `existing` are paths as the request names them.
 */
function uploadApi(existing: string[], options: { fail?: (n: number) => boolean; gate?: Promise<void> } = {}) {
  const sent: Sent[] = [];
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2 }]),
    "GET /api/vaults/Work/tree": () => json(200, workTree),
    "GET /api/notes/*": (r) =>
      json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "POST /api/upload/*": async (r) => {
      const body = (await r.json()) as { files: { path: string }[]; onConflict: string };
      const paths = body.files.map((f) => f.path);
      sent.push({ url: new URL(r.url).pathname, onConflict: body.onConflict, paths });
      if (sent.length === 1) await options.gate;
      if (options.fail?.(sent.length)) return json(409, { error: "Nothing was uploaded: /Work/Vault/more/n000.md changed meanwhile; try again." });
      const found = paths.filter((p) => existing.includes(p));
      if (body.onConflict === "ask" && found.length) return json(200, { written: 0, replaced: 0, skipped: [], conflicts: found.map((path) => ({ path, version: "v7" })) });
      const replaced = body.onConflict === "replace" ? found.length : 0;
      const skipped = body.onConflict === "skip" ? found.map((path) => ({ path, reason: "a note exists at this path" })) : [];
      return json(200, { written: paths.length - found.length, replaced, skipped, conflicts: [] });
    },
  });
  return { fetch, sent };
}

/** Files as a folder picker gives them: each with its path in the picked folder. */
function folderFiles(paths: string[]) {
  return paths.map((path) => {
    const file = new File([`# ${path}`], path.split("/").at(-1)!);
    Object.defineProperty(file, "webkitRelativePath", { value: path });
    return file;
  });
}

const bulk = Array.from({ length: 200 }, (_, i) => `Vault/bulk/n${String(i).padStart(3, "0")}.md`);
// An Obsidian vault: settings, an image, nested folders; two of its notes are in Kumbu² already.
const vault = [
  ...bulk,
  "Vault/.obsidian/app.json",
  "Vault/.obsidian/workspace.json",
  "Vault/img/pic.png",
  "Vault/a.md",
  "Vault/notes/b.md",
  "Vault/notes/deep/c.md",
  "Vault/notes/deep/d.markdown",
];
const existing = ["Vault/notes/b.md", "Vault/notes/deep/c.md"];

async function openFromDock() {
  await userEvent.click(await screen.findByRole("button", { name: "New" }));
  await userEvent.click(screen.getByRole("menuitem", { name: "Upload" }));
  return screen.findByRole("dialog", { name: "Upload" });
}

async function pickVault(sheet: HTMLElement, paths = vault) {
  await userEvent.upload(within(sheet).getByLabelText("Choose a folder"), folderFiles(paths));
  await userEvent.click(await within(sheet).findByRole("button", { name: "Upload" }));
}

describe("the upload", () => {
  it("writes no existing note before the user decides, lists what isn't Markdown, and shows each batch", async () => {
    let open!: () => void;
    const { sent } = uploadApi(existing, { gate: new Promise<void>((ok) => (open = ok)) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    renderApp("/notes/Work/inbox.md", queryClient);

    const sheet = await openFromDock();
    expect(within(sheet).getByText("Into Work")).toBeInTheDocument();
    await userEvent.upload(within(sheet).getByLabelText("Choose a folder"), folderFiles(vault));
    expect(await within(sheet).findByText("204 Markdown files to upload, 3 files skipped")).toBeInTheDocument();
    await userEvent.click(within(sheet).getByRole("button", { name: "Upload" }));

    expect(await within(sheet).findByText("Batch 1 of 2")).toBeInTheDocument();
    expect(within(sheet).getByRole("progressbar")).toHaveAttribute("aria-valuenow", "0");
    open();

    // Both batches asked; the second met existing notes and wrote nothing; nothing was replaced.
    const list = await within(sheet).findByRole("group", { name: "2 notes exist already" });
    expect(sent.map((s) => [s.url, s.onConflict, s.paths.length])).toEqual([
      ["/api/upload/Work", "ask", 200],
      ["/api/upload/Work", "ask", 4],
    ]);
    expect(sent[0].paths).toEqual(bulk);
    expect(within(list).getAllByRole("checkbox").map((c) => c.getAttribute("aria-label"))).toEqual(["Replace Vault/notes/b.md", "Replace Vault/notes/deep/c.md"]);

    expect(within(list).getByText("2 new notes wait for this choice")).toBeInTheDocument();
    // A choice is required: Escape doesn't close the sheet and drop the held notes, "Skip all" has the focus.
    expect(within(list).getByRole("button", { name: "Skip all" })).toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(screen.getByRole("dialog", { name: "Upload" })).toBeInTheDocument();
    expect(within(sheet).queryByRole("button", { name: /Cancel|Close/ })).not.toBeInTheDocument();

    await userEvent.click(within(list).getByRole("checkbox", { name: "Replace Vault/notes/b.md" }));
    await userEvent.click(within(sheet).getByRole("button", { name: "Replace 1 selected" }));

    expect(await within(sheet).findByText("202 notes uploaded, 1 replaced, 4 files skipped")).toBeInTheDocument();
    // The new notes of the held batch go with "skip" (an existing note is never replaced unasked), the chosen one with "replace".
    expect(sent.slice(2)).toEqual([
      { url: "/api/upload/Work", onConflict: "skip", paths: ["Vault/a.md", "Vault/notes/deep/d.markdown"] },
      { url: "/api/upload/Work", onConflict: "replace", paths: ["Vault/notes/b.md"] },
    ]);
    await userEvent.click(within(sheet).getByText("4 files skipped"));
    const skipped = within(sheet).getByRole("list", { name: "Skipped files" });
    expect(within(skipped).getAllByRole("listitem").map((li) => li.textContent)).toEqual([
      "Vault/.obsidian/app.jsonObsidian's settings or trash, not a note",
      "Vault/.obsidian/workspace.jsonObsidian's settings or trash, not a note",
      "Vault/img/pic.pngnot a Markdown file (.md or .markdown)",
      "Vault/notes/deep/c.mda note exists at this path",
    ]);
    for (const key of [["tree", "Work"], ["vaults"], ["search"], ["note"]]) expect(invalidate).toHaveBeenCalledWith({ queryKey: key });

    await userEvent.click(within(sheet).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("dialog", { name: "Upload" })).not.toBeInTheDocument();
  });

  it("replaces all, or skips all, as the user says", async () => {
    const { sent } = uploadApi(existing);
    renderApp("/notes/Work/inbox.md");
    await pickVault(await openFromDock(), vault.slice(200));
    await userEvent.click(await screen.findByRole("button", { name: "Replace all" }));
    expect(await screen.findByText("2 notes uploaded, 2 replaced, 3 files skipped")).toBeInTheDocument();
    expect(sent.slice(1).map((s) => [s.onConflict, s.paths])).toEqual([
      ["skip", ["Vault/a.md", "Vault/notes/deep/d.markdown"]],
      ["replace", ["Vault/notes/b.md", "Vault/notes/deep/c.md"]],
    ]);

    cleanup();
    useEditor.getState().reset();
    const second = uploadApi(existing);
    renderApp("/notes/Work/inbox.md");
    await pickVault(await openFromDock(), vault.slice(200));
    await userEvent.click(await screen.findByRole("button", { name: "Skip all" }));
    expect(await screen.findByText("2 notes uploaded, 0 replaced, 5 files skipped")).toBeInTheDocument();
    expect(second.sent.slice(1).map((s) => [s.onConflict, s.paths])).toEqual([["skip", ["Vault/a.md", "Vault/notes/deep/d.markdown"]]]);
  });

  it("stops at a failed batch, says what was written so far, and retries the rest", async () => {
    let failing = true;
    const { sent } = uploadApi([], { fail: (n) => n === 2 && failing });
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await pickVault(sheet, [...bulk, ...bulk.map((p) => p.replace("bulk", "more")), "Vault/last.md"]);

    expect(await within(sheet).findByRole("alert")).toHaveTextContent("Nothing was uploaded: /Work/Vault/more/n000.md changed meanwhile; try again.");
    expect(within(sheet).getByText("Batch 2 of 3 failed. So far: 200 notes uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(sent).toHaveLength(2);

    failing = false;
    await userEvent.click(within(sheet).getByRole("button", { name: "Retry" }));
    expect(await within(sheet).findByText("401 notes uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(sent.map((s) => s.paths[0])).toEqual(["Vault/bulk/n000.md", "Vault/more/n000.md", "Vault/more/n000.md", "Vault/last.md"]);
  });

  it('uploads into a folder from the drawer\'s "Upload here"', async () => {
    const { sent } = uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Actions for projects" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Upload here" }));

    const sheet = await screen.findByRole("dialog", { name: "Upload" });
    expect(within(sheet).getByText("Into Work / projects")).toBeInTheDocument();
    await userEvent.upload(within(sheet).getByLabelText("Choose files"), [new File(["x"], "one.md"), new File(["y"], "two.MD")]);
    await userEvent.click(await within(sheet).findByRole("button", { name: "Upload" }));
    expect(await within(sheet).findByText("2 notes uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(sent).toEqual([{ url: "/api/upload/Work/projects", onConflict: "ask", paths: ["one.md", "two.MD"] }]);
  });

  it("takes files dropped on the note into the note's folder, and asks before sending them", async () => {
    const { sent } = uploadApi([]);
    renderApp("/notes/Work/projects/plan.md");
    const area = (await screen.findByRole("heading", { name: "plan" })).parentElement!;
    const file = new File(["x"], "dropped.md");
    const items = [{ kind: "file", webkitGetAsEntry: () => ({ isFile: true, isDirectory: false, fullPath: "/dropped.md", file: (ok: (f: File) => void) => ok(file) }) }];
    fireEvent.dragOver(area, { dataTransfer: { types: ["Files"], items } });
    fireEvent.drop(area, { dataTransfer: { types: ["Files"], items } });

    const sheet = await screen.findByRole("dialog", { name: "Upload" });
    expect(within(sheet).getByText("Into Work / projects")).toBeInTheDocument();
    expect(await within(sheet).findByText("1 Markdown file to upload, 0 files skipped")).toBeInTheDocument();
    expect(sent).toEqual([]);
    await userEvent.click(within(sheet).getByRole("button", { name: "Upload" }));
    await waitFor(() => expect(sent).toEqual([{ url: "/api/upload/Work/projects", onConflict: "ask", paths: ["dropped.md"] }]));
  });
});

describe("the open sheet", () => {
  it("keeps a drop anywhere on the page from opening the file in place of the app", async () => {
    uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    await openFromDock();
    const drop = new Event("drop", { bubbles: true, cancelable: true });
    const over = new Event("dragover", { bubbles: true, cancelable: true });
    document.body.dispatchEvent(over);
    document.body.dispatchEvent(drop);
    expect(over.defaultPrevented).toBe(true);
    expect(drop.defaultPrevented).toBe(true);

    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    const later = new Event("drop", { bubbles: true, cancelable: true });
    document.body.dispatchEvent(later);
    expect(later.defaultPrevented).toBe(false);
  });
});

describe("summaryText", () => {
  it("counts in the singular and the plural", () => {
    expect(summaryText({ written: 34, replaced: 3, skipped: 12 })).toBe("34 notes uploaded, 3 replaced, 12 files skipped");
    expect(summaryText({ written: 1, replaced: 1, skipped: 1 })).toBe("1 note uploaded, 1 replaced, 1 file skipped");
    expect(summaryText({ written: 0, replaced: 0, skipped: 0 })).toBe("0 notes uploaded, 0 replaced, 0 files skipped");
  });
});
